using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>正在运行、装了游戏内桥接的一个游戏</summary>
public sealed record BridgeInfo(int Pid, int Port, string Token, string GameDir, string McVersion, string BridgeVersion, DateTime StartedAt);

/// <summary>
/// 游戏内桥接：负责把桥接模组装进支持的版本、找到正在运行的游戏、并向它发送请求
/// </summary>
public static class GameBridge
{
    /// <summary>桥接模组在 mods 文件夹里的文件名</summary>
    public const string JarName = "claudecl-bridge.jar";

    /// <summary>连接信息目录，游戏启动后桥接会在这里写下端口和口令</summary>
    public static string InfoDir => Path.Combine(AppPaths.DataDir, "bridge");

    /// <summary>为 true 时（例如由 AI 助手启动游戏），不管设置如何都装上桥接</summary>
    public static bool ForceInstall { get; set; }

    /// <summary>目前只有 Forge 1.20.1 有对应的桥接模组</summary>
    public static bool IsSupported(InstalledVersion v) =>
        v.IsValid && v.Loader == LoaderKind.Forge && v.GameVersion == "1.20.1";

    // ---------- 安装 ----------

    private static byte[]? _jar;

    private static byte[] Jar()
    {
        if (_jar is not null) return _jar;
        using var s = typeof(GameBridge).Assembly.GetManifestResourceStream("ClaudeCL.Bridge.jar")
                      ?? throw new InvalidOperationException("程序里缺少游戏内桥接模组");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _jar = ms.ToArray();
    }

    /// <summary>
    /// 启动前调用：需要时把桥接模组装进（或更新到）mods 文件夹，不需要时移除，避免残留
    /// </summary>
    public static void Prepare(InstalledVersion version, string runDir)
    {
        var mods = Path.Combine(runDir, "mods");
        var target = Path.Combine(mods, JarName);
        var disabled = target + ".disabled";
        var want = IsSupported(version) && (ForceInstall || Settings.Current.EnableAiBridge);
        try
        {
            if (!want)
            {
                if (File.Exists(target)) { File.Delete(target); Log.Info("已从 mods 移除游戏内桥接"); }
                return;
            }
            Directory.CreateDirectory(mods);
            if (File.Exists(disabled)) File.Delete(disabled);
            var jar = Jar();
            if (File.Exists(target) && new FileInfo(target).Length == jar.Length &&
                SHA1.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA1.HashData(jar))) return;
            File.WriteAllBytes(target, jar);
            Log.Info($"已把游戏内桥接装进 {mods}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理游戏内桥接模组失败");
            if (want) throw new IOException("没能把游戏内桥接模组放进 mods 文件夹：" + ex.Message, ex);
        }
    }

    // ---------- 查找正在运行的游戏 ----------

    /// <summary>列出所有还活着的桥接（顺手清理已经退出的游戏留下的文件）</summary>
    public static List<BridgeInfo> Running()
    {
        var list = new List<BridgeInfo>();
        if (!Directory.Exists(InfoDir)) return list;
        foreach (var f in Directory.GetFiles(InfoDir, "*.json"))
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(f, Encoding.UTF8))!.AsObject();
                var info = new BridgeInfo((int)o["pid"]!, (int)o["port"]!, (string)o["token"]!, (string?)o["gameDir"] ?? "",
                    (string?)o["mcVersion"] ?? "", (string?)o["bridgeVersion"] ?? "",
                    DateTimeOffset.FromUnixTimeMilliseconds((long?)o["startedAt"] ?? 0).LocalDateTime);
                if (IsAlive(info.Pid)) list.Add(info);
                else File.Delete(f);
            }
            catch (IOException) { /* 正在写入，下次再读 */ }
            catch (Exception) { try { File.Delete(f); } catch { /* 删不掉就算了 */ } }
        }
        return list.OrderByDescending(b => b.StartedAt).ToList();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------- 发送请求 ----------

    /// <summary>
    /// 向桥接发送一个操作并等待结果；桥接返回失败时抛出 <see cref="BridgeError"/>
    /// </summary>
    public static async Task<JsonNode?> CallAsync(BridgeInfo b, string op, JsonObject? args, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        // 先连 IPv4 回环地址，不行再试 IPv6 的（兼容旧版桥接）
        TcpClient? tcp = null;
        foreach (var host in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback })
        {
            var c = new TcpClient(host.AddressFamily) { NoDelay = true };
            try
            {
                await c.ConnectAsync(host, b.Port, cts.Token);
                tcp = c;
                break;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                c.Dispose();
            }
        }
        if (tcp is null) throw new BridgeError("连不上游戏内桥接（游戏可能刚退出或卡住了）");
        using var _ = tcp;
        var stream = tcp.GetStream();
        var req = new JsonObject { ["id"] = 1, ["token"] = b.Token, ["op"] = op, ["args"] = args?.DeepClone() ?? new JsonObject() }; // 复制一份，传进来的参数可能还挂在 MCP 请求上
        var bytes = Encoding.UTF8.GetBytes(req.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, cts.Token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BridgeError($"游戏在 {timeout.TotalSeconds:0} 秒内没有回应");
        }
        if (line is null) throw new BridgeError("游戏内桥接断开了连接");
        var resp = JsonNode.Parse(line)!.AsObject();
        if ((bool?)resp["ok"] != true) throw new BridgeError((string?)resp["error"] ?? "未知错误");
        return resp["result"];
    }
}

/// <summary>游戏内桥接返回的错误，消息可以直接给用户或 AI 助手看</summary>
public sealed class BridgeError(string message) : Exception(message);
