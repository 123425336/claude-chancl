using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>
/// MCP 服务（ClaudeCL.exe --mcp）：通过标准输入输出与 AI 助手通信（每行一条 JSON-RPC 消息），
/// 启动器负责列出版本、启动游戏，游戏里的操作转发给游戏内桥接。
/// 注意：标准输出只能写协议消息，任何日志都写进日志文件
/// </summary>
public static class McpServer
{
    private const string DefaultProtocol = "2025-06-18";

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object WriteLock = new();
    private static Stream _out = Stream.Null;

    /// <summary>由这个 MCP 进程启动的游戏</summary>
    private static GameSession? _session;
    private static string? _lastRunDir;

    /// <summary>命令行指定的游戏目录（--gamedir），为空时用启动器设置里的</summary>
    private static string? _gameDirOverride;

    /// <summary>命令行指定的账号名（--account），为空时用启动器当前选中的账号</summary>
    private static string? _accountOverride;

    /// <summary>重新读取设置（界面那边可能改过），并套用命令行参数；不会写回设置文件</summary>
    private static void Reload()
    {
        Settings.Load();
        Settings.ReadOnly = true;
        if (_gameDirOverride is not null) Settings.Current.GameDir = _gameDirOverride;
    }

    private static Account? PickAccount()
    {
        if (_accountOverride is null) return AccountService.Selected;
        return Settings.Current.Accounts.Find(a => a.Name.Equals(_accountOverride, StringComparison.OrdinalIgnoreCase))
               ?? throw new BridgeError($"启动器里没有名为「{_accountOverride}」的账号");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        _gameDirOverride = Arg("--gamedir");
        _accountOverride = Arg("--account");
        Reload(); // 不改动用户的设置文件（界面那边可能同时开着）
        GameBridge.ForceInstall = true;
        _out = Console.OpenStandardOutput();
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        Log.Info("MCP 服务已启动");
        var running = new List<Task>();
        while (true)
        {
            var line = await input.ReadLineAsync();
            if (line is null) break; // 对方关闭了输入，结束服务
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject msg;
            try { msg = JsonNode.Parse(line)!.AsObject(); }
            catch
            {
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = Err(-32700, "无法解析的 JSON") });
                continue;
            }
            // 每个请求单独处理，耗时的操作（启动游戏、挖方块）不会挡住其他请求
            running.Add(Task.Run(() => HandleAsync(msg)));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
        Log.Info("MCP 服务结束");
        return 0;
    }

    private static void Send(JsonObject msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString(Compact) + "\n");
        lock (WriteLock)
        {
            _out.Write(bytes);
            _out.Flush();
        }
    }

    private static JsonObject Err(int code, string message) => new() { ["code"] = code, ["message"] = message };

    private static async Task HandleAsync(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        var method = (string?)msg["method"] ?? "";
        var p = msg["params"] as JsonObject ?? new JsonObject();
        if (id is null) return; // 通知消息（例如 notifications/initialized）不需要回复
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(p),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = ToolList() },
                "tools/call" => await CallToolAsync(p),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new MethodMissing(method),
            };
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (MethodMissing)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Err(-32601, "不支持的方法：" + method) });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MCP 请求处理失败");
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Err(-32603, ex.Message) });
        }
    }

    private sealed class MethodMissing(string m) : Exception(m);

    private static JsonObject Initialize(JsonObject p) => new()
    {
        ["protocolVersion"] = (string?)p["protocolVersion"] ?? DefaultProtocol,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
        ["serverInfo"] = new JsonObject { ["name"] = "claudecl", ["title"] = "ClaudeCL 游戏操控", ["version"] = GameLauncher.LauncherVersion },
        ["instructions"] =
            "通过 ClaudeCL 启动器操控 Minecraft（目前支持 Forge 1.20.1 版本）。一般流程：list_versions 选版本 → launch_game 启动并等到主菜单" +
            "（可以直接带 world 参数进入存档）→ list_worlds / join_world / create_world 进入世界 → 用 get_player、get_inventory、get_world、" +
            "screenshot 观察，用 look_at、attack（左键）、use_item（右键）、move、move_to、run_command 等操作。" +
            "坐标写成整数时表示方块（自动瞄准方块中心）。打开了界面（箱子、死亡画面等）时先用 get_screen 查看，再用 click_widget / close_screen 处理。",
    };

    // ---------- 工具定义 ----------

    private sealed record Tool(string Name, string Description, JsonObject Props, string[] Required, bool Launcher = false, bool ReadOnly = false);

    private static JsonObject S(string type, string desc, params (string, JsonNode)[] extra)
    {
        var o = new JsonObject { ["type"] = type, ["description"] = desc };
        foreach (var (k, v) in extra) o[k] = v;
        return o;
    }

    private static JsonObject Choice(string desc, params string[] values) =>
        new() { ["type"] = "string", ["description"] = desc, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    private static JsonObject P(params (string, JsonObject)[] props)
    {
        var o = new JsonObject();
        // 公用的参数定义会出现在多个工具里，每次复制一份
        foreach (var (k, v) in props) o[k] = v.DeepClone();
        return o;
    }

    private static readonly (string, JsonObject)[] Target =
    [
        ("x", S("number", "目标 X 坐标（可选，给了就先转向这个点；整数表示方块）")),
        ("y", S("number", "目标 Y 坐标")),
        ("z", S("number", "目标 Z 坐标")),
        ("entity_id", S("integer", "目标实体编号（可选，来自 get_world 的 entities）")),
    ];

    private static readonly Tool[] Tools =
    [
        // 启动器
        new("list_versions", "列出启动器里已安装的游戏版本，标出哪些支持 AI 操控（目前是 Forge 1.20.1）", P(), [], true, true),
        new("launch_game", "启动游戏（自动装上游戏内桥接），等到主菜单后返回；可以同时指定要进入的存档。启动一般要 1~3 分钟",
            P(("version", S("string", "版本名（来自 list_versions），不填用启动器当前选中的或第一个支持的版本")),
              ("world", S("string", "启动后直接进入的存档文件夹名（可选）")),
              ("timeout_seconds", S("integer", "最多等多少秒，默认 600"))), [], true),
        new("game_status", "查看游戏状态：有没有在运行、是否在世界里、当前打开的界面等", P(), [], true, true),
        new("close_game", "保存并退出游戏", P(("force", S("boolean", "为 true 时直接结束进程，不保存"))), [], true),
        new("get_game_log", "读取游戏日志（latest.log）的最后几行，排查崩溃或报错",
            P(("lines", S("integer", "行数，默认 80")), ("contains", S("string", "只看包含这段文字的行（可选）"))), [], true, true),
        new("list_worlds", "列出存档（folder 是进入存档要用的文件夹名）。游戏没运行时需要 version 参数",
            P(("version", S("string", "版本名，游戏没运行时用来找存档目录"))), [], true, true),

        // 存档
        new("join_world", "进入单人存档，等到玩家出现在世界里才返回（如果出现需要确认的界面，会返回界面信息）",
            P(("world", S("string", "存档文件夹名（来自 list_worlds 的 folder）")), ("timeout_seconds", S("integer", "最多等多少秒，默认 180"))), ["world"]),
        new("create_world", "新建一个单人世界并进入（默认创造模式、允许作弊）",
            P(("name", S("string", "世界名称")),
              ("game_mode", Choice("游戏模式，默认 creative", "survival", "creative", "adventure", "spectator")),
              ("cheats", S("boolean", "允许作弊（指令），默认 true")),
              ("flat", S("boolean", "超平坦世界，默认 false")),
              ("seed", S("string", "种子（可选）")),
              ("difficulty", Choice("难度，默认 normal", "peaceful", "easy", "normal", "hard")),
              ("hardcore", S("boolean", "极限模式")),
              ("structures", S("boolean", "生成结构，默认 true"))), []),
        new("leave_world", "保存并退出当前世界，回到标题画面", P(), []),
        new("join_server", "连接多人服务器", P(("address", S("string", "服务器地址，例如 127.0.0.1:25565"))), ["address"]),

        // 指令与聊天
        new("run_command", "执行一条指令（例如 /time set day、/give @s diamond 3），返回指令的输出。需要世界允许作弊",
            P(("command", S("string", "指令，开头的 / 可以省略")), ("wait_ms", S("integer", "最多等待输出的毫秒数，默认 1500"))), ["command"]),
        new("send_chat", "发送聊天消息（以 / 开头时当作指令）", P(("message", S("string", "消息内容"))), ["message"]),
        new("get_messages", "读取聊天栏里的消息和系统提示（按编号递增，可以用 since 只取新消息）",
            P(("since", S("integer", "只返回编号大于它的消息，默认 0")), ("limit", S("integer", "最多返回条数，默认 50")),
              ("include_actionbar", S("boolean", "是否包含物品栏上方的动作栏提示"))), [], ReadOnly: true),

        // 信息
        new("get_player", "玩家信息：坐标、朝向、生命、饥饿、经验、游戏模式、效果、手里的物品、准星对着的东西", P(), [], ReadOnly: true),
        new("get_inventory", "背包里的所有物品。格子编号：0~8 快捷栏，9~35 背包，36~39 盔甲（脚、腿、胸、头），40 副手",
            P(("include_empty", S("boolean", "是否列出空格子"))), [], ReadOnly: true),
        new("get_item", "某个格子物品的详细信息：提示文字（伤害、属性、说明）、附魔、耐久、标签、NBT",
            P(("slot", S("string", "格子编号 0~40，或 mainhand / offhand，默认 mainhand"))), [], ReadOnly: true),
        new("get_world", "世界信息：维度、时间、天气、难度、群系、亮度、出生点、附近的实体",
            P(("radius", S("integer", "列出多少格以内的实体，默认 16，最多 64"))), [], ReadOnly: true),
        new("get_block", "查看方块：编号、状态属性、硬度、亮度。不给坐标时查看准星对着的方块",
            P(("x", S("integer", "X")), ("y", S("integer", "Y")), ("z", S("integer", "Z"))), [], ReadOnly: true),
        new("find_blocks", "在周围查找某种方块，按距离从近到远返回",
            P(("block", S("string", "方块编号，例如 minecraft:oak_log；不带冒号时按包含匹配，例如 diamond_ore")),
              ("radius", S("integer", "搜索半径，默认 16，最多 32")), ("limit", S("integer", "最多返回个数，默认 20"))), ["block"], ReadOnly: true),
        new("screenshot", "截取游戏画面（JPEG 图片）", P(("max_width", S("integer", "图片最大宽度，默认 960"))), [], ReadOnly: true),

        // 操作
        new("look_at", "转动视角：看向坐标（整数表示方块中心）、看向实体，或直接设置 yaw/pitch",
            P([.. Target, ("yaw", S("number", "水平角度：0 南、90 西、180 北、-90 东")), ("pitch", S("number", "俯仰角：-90 向上、90 向下"))]), []),
        new("attack", "左键：攻击准星对着的实体，或挖准星对着的方块（默认一直挖到碎）。可以带目标坐标或实体先转过去",
            P([.. Target, ("hold_ticks", S("integer", "按住左键的刻数（20 刻 = 1 秒），0 表示挖到碎为止"))]), []),
        new("use_item", "右键：对准星对着的方块或实体使用手里的物品（放方块、开箱子、交互），或直接使用物品。吃东西、拉弓、举盾要用 hold_ticks 按住",
            P([.. Target, ("hold_ticks", S("integer", "按住右键的刻数；0 是单击；-1 表示一直按到用完（吃完、喝完）"))]), []),
        new("select_slot", "切换快捷栏格子", P(("slot", S("integer", "0~8"))), ["slot"]),
        new("hold_item", "把某种物品拿到手上：在快捷栏就切过去，在背包里就换到当前格子",
            P(("item", S("string", "物品编号或名称，例如 minecraft:diamond_sword、bread、面包"))), ["item"]),
        new("swap_slots", "把背包某格和快捷栏（或副手）交换", P(("slot", S("integer", "背包格子 0~40")), ("hotbar", S("integer", "快捷栏 0~8，或 40 表示副手"))), ["slot", "hotbar"]),
        new("drop_item", "丢出物品（默认丢出手里的一个）", P(("all", S("boolean", "丢出整组")), ("slot", S("integer", "要丢的背包格子（可选）"))), []),
        new("give_item", "用 /give 给自己物品（需要允许作弊）",
            P(("item", S("string", "物品编号，例如 diamond_sword 或 minecraft:torch")), ("count", S("integer", "数量，默认 1")),
              ("nbt", S("string", "NBT（可选），例如 {Unbreakable:1b}"))), ["item"]),
        new("move", "按住移动键若干刻（相对当前朝向）",
            P(("direction", Choice("方向，默认 forward", "forward", "back", "left", "right", "none")),
              ("ticks", S("integer", "按住的刻数，默认 20（1 秒）")),
              ("jump", S("boolean", "同时按住跳跃")), ("sneak", S("boolean", "同时潜行")), ("sprint", S("boolean", "同时疾跑"))), []),
        new("move_to", "沿直线走到某个位置（被挡住时会自动跳，但不会绕路）",
            P(("x", S("number", "目标 X")), ("z", S("number", "目标 Z")), ("sprint", S("boolean", "疾跑")),
              ("distance", S("number", "离目标多近算到达，默认 0.6")), ("timeout_ticks", S("integer", "最多走多少刻，默认 600"))), ["x", "z"]),
        new("respawn", "死亡后重生", P(), []),

        // 界面
        new("get_container", "查看当前打开的容器（箱子、熔炉、背包等）里的物品和格子编号", P(), [], ReadOnly: true),
        new("container_click", "点击容器格子来移动物品（格子编号来自 get_container）",
            P(("slot", S("integer", "格子编号，-999 表示点击界面外（丢出拿着的物品）")),
              ("button", S("integer", "0 左键、1 右键；mode 为 swap 时是快捷栏 0~8")),
              ("mode", Choice("点击方式，默认 pickup；quick_move 相当于按住 Shift 点击", "pickup", "quick_move", "swap", "throw", "pickup_all", "clone"))), ["slot"]),
        new("get_screen", "查看当前打开的界面和上面的按钮、输入框", P(), [], ReadOnly: true),
        new("click_widget", "点击界面上的按钮（按编号或按文字）", P(("index", S("integer", "控件编号（来自 get_screen）")), ("label", S("string", "按钮上的文字"))), []),
        new("click_screen", "点击界面上的某个位置（界面坐标，来自 get_screen 的 width/height）",
            P(("x", S("number", "X")), ("y", S("number", "Y")), ("button", S("integer", "0 左键、1 右键"))), ["x", "y"]),
        new("type_text", "在界面的输入框里输入文字", P(("text", S("string", "文字")), ("index", S("integer", "输入框编号（可选）")),
            ("replace", S("boolean", "替换原有内容，默认 true"))), ["text"]),
        new("close_screen", "关闭当前界面（相当于按 Esc）", P(), []),
        new("press_key", "按一个键：有界面时发给界面，在游戏里时触发绑定在这个键上的功能（也可以按功能名触发，见 list_keys）",
            P(("key", S("string", "按键名，例如 e、enter、escape、f5、space、left.shift")),
              ("mapping", S("string", "功能名，例如 key.inventory（与 key 二选一）")),
              ("hold_ticks", S("integer", "按住的刻数，默认 1"))), []),
        new("list_keys", "列出所有按键功能及绑定的键（包括模组添加的）", P(), [], ReadOnly: true),
    ];

    private static JsonArray ToolList()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = t.Props.DeepClone() };
            if (t.Required.Length > 0) schema["required"] = new JsonArray(t.Required.Select(r => (JsonNode)r).ToArray());
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject { ["readOnlyHint"] = t.ReadOnly },
            });
        }
        return arr;
    }

    // ---------- 调用工具 ----------

    private static async Task<JsonObject> CallToolAsync(JsonObject p)
    {
        var name = (string?)p["name"] ?? "";
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        var token = p["_meta"]?["progressToken"]?.DeepClone();
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return TextResult("没有这个工具：" + name, true);
        try
        {
            JsonNode? result = name switch
            {
                "list_versions" => ListVersions(),
                "launch_game" => await LaunchGameAsync(args, token),
                "game_status" => await StatusAsync(),
                "close_game" => await CloseGameAsync(args),
                "get_game_log" => GameLog(args),
                "list_worlds" => await ListWorldsAsync(args),
                _ => await ForwardAsync(name, args),
            };
            // 截图以图片形式返回，其余以 JSON 文本返回
            if (name == "screenshot" && result?["image_base64"] is { } img)
            {
                var info = $"截图 {(int?)result["width"]}×{(int?)result["height"]}（游戏窗口 {(int?)result["original_width"]}×{(int?)result["original_height"]}）";
                return new JsonObject
                {
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image", ["data"] = (string?)img, ["mimeType"] = (string?)result["mime"] ?? "image/jpeg" },
                        new JsonObject { ["type"] = "text", ["text"] = info }),
                    ["isError"] = false,
                };
            }
            return TextResult(result?.ToJsonString(Compact) ?? "{}", false);
        }
        catch (BridgeError ex)
        {
            return TextResult(ex.Message, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"MCP 工具 {name} 执行失败");
            return TextResult(ex.Message, true);
        }
    }

    private static JsonObject TextResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static void Progress(JsonNode? token, double progress, string message)
    {
        if (token is null) return;
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = new JsonObject { ["progressToken"] = token.DeepClone(), ["progress"] = Math.Round(progress * 100, 1), ["total"] = 100, ["message"] = message },
        });
    }

    /// <summary>找到要操作的游戏：优先这个进程启动的，其次最近启动的</summary>
    private static BridgeInfo NeedBridge()
    {
        var list = GameBridge.Running();
        if (list.Count == 0)
        {
            if (_session is { } s && !s.Process.HasExited)
                throw new BridgeError("游戏正在启动，桥接还没准备好，请稍后再试（或用 game_status 查看）");
            throw new BridgeError("没有找到装了桥接的游戏：请先用 launch_game 启动游戏");
        }
        var pid = _session is { } ss && !ss.Process.HasExited ? ss.Process.Id : -1;
        return list.FirstOrDefault(b => b.Pid == pid) ?? list[0];
    }

    private static TimeSpan TimeoutFor(JsonObject a)
    {
        double I(string k) => a[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        // 基础 60 秒（足够默认的“挖到碎为止”），再加上按住、走路、等待输出需要的时间
        var seconds = 60 + I("timeout_seconds") + Math.Max(Math.Max(I("hold_ticks"), I("ticks")), I("timeout_ticks")) / 20 * 2 + I("wait_ms") / 1000;
        return TimeSpan.FromSeconds(Math.Min(seconds, 1200));
    }

    private static Task<JsonNode?> ForwardAsync(string op, JsonObject args) =>
        GameBridge.CallAsync(NeedBridge(), op, args, TimeoutFor(args), CancellationToken.None);

    // ---------- 启动器这边的工具 ----------

    private static JsonObject ListVersions()
    {
        Reload();
        var gameDir = Settings.Current.GameDir;
        var arr = new JsonArray();
        foreach (var v in VersionStore.Scan(gameDir))
        {
            arr.Add(new JsonObject
            {
                ["version"] = v.Id,
                ["game_version"] = v.GameVersion,
                ["loader"] = v.Loader.ToString().ToLowerInvariant(),
                ["loader_version"] = v.LoaderVersion,
                ["valid"] = v.IsValid,
                ["ai_control"] = GameBridge.IsSupported(v),
                ["selected"] = v.Id == Settings.Current.SelectedVersion,
            });
        }
        return new JsonObject
        {
            ["game_dir"] = gameDir,
            ["versions"] = arr,
            ["account"] = PickAccount()?.Name,
            ["hint"] = "ai_control 为 true 的版本才能被操控；如果一个都没有，需要先在启动器里安装 Forge 1.20.1",
        };
    }

    private static InstalledVersion PickVersion(string? want)
    {
        var gameDir = Settings.Current.GameDir;
        var all = VersionStore.Scan(gameDir);
        if (!string.IsNullOrWhiteSpace(want))
        {
            var v = all.FirstOrDefault(x => x.Id == want) ?? all.FirstOrDefault(x => x.Id.Equals(want, StringComparison.OrdinalIgnoreCase))
                    ?? throw new BridgeError($"找不到版本「{want}」，可以用 list_versions 查看");
            if (!GameBridge.IsSupported(v)) throw new BridgeError($"版本「{v.Id}」是 {v.Summary}，目前只支持操控 Forge 1.20.1");
            return v;
        }
        return all.FirstOrDefault(x => x.Id == Settings.Current.SelectedVersion && GameBridge.IsSupported(x))
               ?? all.FirstOrDefault(GameBridge.IsSupported)
               ?? throw new BridgeError("启动器里没有 Forge 1.20.1 版本，请先在启动器的「下载」页安装");
    }

    private static async Task<JsonNode?> LaunchGameAsync(JsonObject args, JsonNode? token)
    {
        Reload();
        var world = (string?)args["world"];
        var timeout = TimeSpan.FromSeconds(Math.Clamp((int?)args["timeout_seconds"] ?? 600, 60, 1800));

        // 已经有游戏在运行：直接用它
        if (GameBridge.Running().Count > 0)
        {
            var st = await ForwardAsync("status", new JsonObject());
            if (world is null)
            {
                st!["note"] = "游戏已经在运行了，没有再启动一个";
                return st;
            }
            return await ForwardAsync("join_world", new JsonObject { ["world"] = world });
        }
        if (_session is { } old && !old.Process.HasExited) throw new BridgeError("游戏正在启动中，请用 game_status 查看");

        var version = PickVersion((string?)args["version"]);
        var account = PickAccount() ?? throw new BridgeError("启动器里还没有账号：请先打开 ClaudeCL 添加一个离线或微软账号");
        Progress(token, 0, $"准备启动 {version.Id}");
        var lastStage = "";
        var progress = new Progress<StageProgress>(s =>
        {
            if (s.Stage == lastStage) return;
            lastStage = s.Stage;
            Progress(token, s.Percent * 0.4, s.Stage);
        });
        var session = await GameLauncher.LaunchAsync(version, account, progress, CancellationToken.None);
        _session = session;
        _lastRunDir = version.RunDir(Settings.Current.GameDir);
        Log.Info($"MCP 启动了游戏 {version.Id}，进程 {session.Process.Id}");

        // 等桥接写出连接信息，再等游戏加载完资源回到主菜单
        var deadline = DateTime.Now + timeout;
        var tick = 0;
        while (DateTime.Now < deadline)
        {
            if (session.Process.HasExited)
                throw new BridgeError($"游戏在启动过程中退出了（退出码 {session.Process.ExitCode}）。最后的日志：\n" +
                                      string.Join("\n", session.Lines.TakeLast(40)));
            var b = GameBridge.Running().FirstOrDefault(x => x.Pid == session.Process.Id);
            if (b is not null)
            {
                try
                {
                    var st = await GameBridge.CallAsync(b, "status", null, TimeSpan.FromSeconds(10), CancellationToken.None);
                    // 主菜单要真的显示出来（有界面、帧数大于 0）才算加载完成
                    if ((string?)st?["state"] is "in_world" ||
                        ((string?)st?["state"] is "menu" && st?["screen"] is not null && ((int?)st["fps"] ?? 0) > 0))
                    {
                        Progress(token, 0.9, "游戏已加载到主菜单");
                        if (world is not null)
                        {
                            Progress(token, 0.92, $"进入存档 {world}");
                            return await GameBridge.CallAsync(b, "join_world", new JsonObject { ["world"] = world }, TimeSpan.FromSeconds(240), CancellationToken.None);
                        }
                        st!["version"] = version.Id;
                        return st;
                    }
                }
                catch (BridgeError) { /* 还在加载，继续等 */ }
            }
            if (++tick % 5 == 0) Progress(token, 0.4 + Math.Min(0.5, tick / 240.0), b is null ? "游戏正在加载模组" : "游戏正在加载资源");
            await Task.Delay(1000);
        }
        throw new BridgeError($"等了 {timeout.TotalSeconds:0} 秒，游戏还没有加载到主菜单（游戏仍在运行，可以稍后用 game_status 查看）");
    }

    private static async Task<JsonNode?> StatusAsync()
    {
        var list = GameBridge.Running();
        var o = new JsonObject();
        if (list.Count == 0)
        {
            if (_session is { } s && !s.Process.HasExited)
            {
                o["state"] = "starting";
                o["hint"] = "游戏进程已经启动，正在加载模组";
            }
            else
            {
                o["state"] = "not_running";
                o["hint"] = "用 launch_game 启动游戏";
            }
            return o;
        }
        var b = NeedBridge();
        var st = await GameBridge.CallAsync(b, "status", null, TimeSpan.FromSeconds(10), CancellationToken.None) as JsonObject ?? new JsonObject();
        st["pid"] = b.Pid;
        st["game_dir"] = b.GameDir;
        if (list.Count > 1) st["other_games"] = list.Count - 1;
        return st;
    }

    private static async Task<JsonNode?> CloseGameAsync(JsonObject args)
    {
        var b = NeedBridge();
        var force = (bool?)args["force"] ?? false;
        if (!force)
        {
            try { await GameBridge.CallAsync(b, "leave_world", null, TimeSpan.FromSeconds(120), CancellationToken.None); }
            catch (BridgeError) { /* 不在世界里或者已经卡住，下面会直接结束 */ }
            try { await GameBridge.CallAsync(b, "quit_game", null, TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch (BridgeError) { /* 退出时连接会断开，属于正常情况 */ }
        }
        using var p = System.Diagnostics.Process.GetProcessById(b.Pid);
        if (!p.WaitForExit(force ? 0 : 30000))
        {
            p.Kill(true);
            p.WaitForExit(10000);
        }
        return new JsonObject { ["result"] = force ? "killed" : "closed" };
    }

    private static JsonNode GameLog(JsonObject args)
    {
        var lines = Math.Clamp((int?)args["lines"] ?? 80, 1, 1000);
        var filter = (string?)args["contains"];
        var dir = GameBridge.Running().FirstOrDefault()?.GameDir ?? _lastRunDir
                  ?? throw new BridgeError("还不知道游戏目录：请先用 launch_game 启动游戏");
        var file = Path.Combine(dir, "logs", "latest.log");
        if (!File.Exists(file)) throw new BridgeError("还没有日志文件：" + file);
        string text;
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(fs, Encoding.UTF8))
            text = r.ReadToEnd();
        var all = text.Split('\n').Select(l => l.TrimEnd('\r'));
        if (!string.IsNullOrEmpty(filter)) all = all.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var tail = all.TakeLast(lines).Select(Redactor.Clean);
        return new JsonObject { ["file"] = file, ["lines"] = string.Join("\n", tail) };
    }

    private static async Task<JsonNode?> ListWorldsAsync(JsonObject args)
    {
        if (GameBridge.Running().Count > 0) return await ForwardAsync("list_worlds", new JsonObject());
        // 游戏没运行：直接读版本目录下的 saves
        Reload();
        var v = PickVersion((string?)args["version"]);
        var saves = Path.Combine(v.RunDir(Settings.Current.GameDir), "saves");
        var arr = new JsonArray();
        if (Directory.Exists(saves))
        {
            foreach (var d in new DirectoryInfo(saves).GetDirectories()
                         .Where(d => File.Exists(Path.Combine(d.FullName, "level.dat")))
                         .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d.FullName, "level.dat"))))
            {
                arr.Add(new JsonObject
                {
                    ["folder"] = d.Name,
                    ["last_played"] = File.GetLastWriteTime(Path.Combine(d.FullName, "level.dat")).ToString("yyyy-MM-dd HH:mm"),
                });
            }
        }
        return new JsonObject { ["version"] = v.Id, ["saves_dir"] = saves, ["worlds"] = arr, ["note"] = "游戏没有运行，只列出了文件夹名" };
    }
}
