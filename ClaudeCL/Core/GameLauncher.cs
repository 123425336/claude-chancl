using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Java.Parser;

namespace ClaudeCL.Core;

/// <summary>
/// 一次正在运行的游戏
/// </summary>
public sealed class GameSession
{
    public required Process Process { get; init; }
    public required InstalledVersion Version { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public ConcurrentQueue<string> Lines { get; } = new();

    /// <summary>收到一行游戏输出</summary>
    public event Action<string>? Output;

    /// <summary>游戏退出（参数为退出码）</summary>
    public event Action<int>? Exited;

    // 日志折叠：标准输出与错误输出来自两个线程，共用一把锁
    private readonly LogFolder? _folder = Settings.Current.FoldRepeatedLogs ? new LogFolder() : null;
    private readonly object _foldLock = new();
    private System.Threading.Timer? _foldTimer;

    internal void Push(string line)
    {
        // 游戏输出里可能带有令牌（例如旧版本会打印会话信息），先遮盖再保存和显示
        line = Redactor.Clean(line);
        if (_folder is null)
        {
            Emit(line);
            return;
        }
        List<string> output;
        lock (_foldLock)
        {
            output = _folder.Feed(line, DateTime.UtcNow);
            // 第一次收到输出时启动定时器，用来补发折叠提示
            _foldTimer ??= new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }
        foreach (var l in output) Emit(l);
    }

    private void Tick()
    {
        List<string> output;
        lock (_foldLock) output = _folder!.Tick(DateTime.UtcNow);
        foreach (var l in output) Emit(l);
    }

    private void Emit(string line)
    {
        Lines.Enqueue(line);
        while (Lines.Count > 600) Lines.TryDequeue(out _);
        Output?.Invoke(line);
    }

    internal void RaiseExited(int code)
    {
        // 退出前把还没汇报的折叠提示补上
        if (_folder is not null)
        {
            List<string> output;
            lock (_foldLock)
            {
                _foldTimer?.Dispose();
                _foldTimer = null;
                output = _folder.FlushAll();
            }
            foreach (var l in output) Emit(l);
        }
        Exited?.Invoke(code);
    }

    /// <summary>运行时间很短就非正常退出，大概率是崩溃了</summary>
    public bool LooksCrashed(int code) => code != 0 && Process.ExitTime - StartedAt < TimeSpan.FromMinutes(30);
}

/// <summary>
/// 启动游戏：补全文件 → 准备 Java → 刷新账号 → 解压 natives → 拼装参数 → 启动进程
/// </summary>
public static class GameLauncher
{
    public const string LauncherName = "ClaudeCL";
    /// <summary>启动器版本号：从程序集读取（来自项目文件里的 Version），发版时只需改项目文件一处</summary>
    public static readonly string LauncherVersion =
        typeof(GameLauncher).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "0.0.0";

    public static GameSession? Running { get; private set; }

    /// <summary>启动过程中的详细信息（使用的 Java、内存、参数），供实时日志显示</summary>
    public static event Action<string>? Trace;

    private static void TraceInfo(string msg)
    {
        Log.Info(msg);
        Trace?.Invoke(msg);
    }

    public static async Task<GameSession> LaunchAsync(InstalledVersion version, Account account, IProgress<StageProgress> progress, CancellationToken ct)
    {
        var s = Settings.Current;
        var gameDir = s.GameDir;

        // 1. 补全文件（进度占 0 ~ 60%）
        await Installer.CompleteAsync(gameDir, version.Id,
            new Progress<StageProgress>(p => progress.Report(p with { Percent = p.Percent * 0.6 })), ct);
        version = VersionStore.Load(gameDir, version.Id);
        var json = version.Json ?? throw new InvalidDataException(version.Error);

        // 2. 准备 Java（60% ~ 80%）
        progress.Report(new("准备 Java", 0.6));
        var java = await PrepareJavaAsync(json, progress, ct);
        TraceInfo($"使用 Java：{java.ToDetailedString()}");

        // 3. 账号
        progress.Report(new("验证账号", 0.82, account.Name));
        await AccountService.EnsureValidAsync(account, ct);
        Redactor.Register(account.AccessToken);

        // 4. natives 与旧版资源
        progress.Report(new("解压本地库", 0.86));
        var nativesDir = Path.Combine(version.Dir, $"{version.Id}-natives");
        await Task.Run(() => ExtractNatives(gameDir, json, nativesDir), ct);
        var runDir = version.RunDir(gameDir);
        Directory.CreateDirectory(runDir);
        progress.Report(new("整理资源文件", 0.9));
        var gameAssets = await Task.Run(() => PrepareLegacyAssets(gameDir, json, runDir), ct);

        // 5. 参数
        progress.Report(new("生成启动参数", 0.94));
        var args = BuildArguments(gameDir, version, json, account, java, nativesDir, runDir, gameAssets);
        TraceInfo("启动参数：" + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
            .Replace(account.AccessToken, "********"));

        // 6. 启动
        progress.Report(new("启动游戏", 0.97));
        var psi = new ProcessStartInfo(java.JavaExePath)
        {
            WorkingDirectory = runDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // 让游戏能找到 Java 自带的其他程序
        psi.Environment["PATH"] = Path.Combine(java.JavaFolder) + ";" + Environment.GetEnvironmentVariable("PATH");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var session = new GameSession { Process = process, Version = version };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) session.Push(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) session.Push(e.Data); };
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            Log.Info($"游戏已退出，退出码 {code}");
            if (Running == session) Running = null;
            session.RaiseExited(code);
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Java 进程");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Running = session;
        progress.Report(new("游戏已启动", 1.0));
        return session;
    }

    private static async Task<JavaInstallation> PrepareJavaAsync(JsonObject json, IProgress<StageProgress> progress, CancellationToken ct)
    {
        var custom = Settings.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var exe = custom.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? custom : Path.Combine(custom, "bin", "java.exe");
            if (Path.GetFileName(exe).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
                exe = Path.Combine(Path.GetDirectoryName(exe)!, "java.exe");
            return new PeHeaderParser().Parse(exe) ?? throw new FileNotFoundException($"设置里指定的 Java 不可用：{custom}");
        }

        var (major, component) = JavaFinder.Requirement(json);
        if (JavaFinder.Installed.Count == 0) await JavaFinder.ScanAsync();
        var picked = JavaFinder.Pick(major);
        if (picked is not null) return picked;

        // 本机没有合适的 Java，自动下载官方运行时
        Log.Info($"没有找到 Java {major}，开始自动下载 {component}");
        return await JavaFinder.DownloadRuntimeAsync(component,
            new Progress<StageProgress>(p => progress.Report(p with { Percent = 0.6 + p.Percent * 0.2 })), ct);
    }

    /// <summary>
    /// 解压 natives：旧版本来自分类 jar，新版本来自名称带 natives-windows 的库
    /// </summary>
    private static void ExtractNatives(string gameDir, JsonObject json, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);
        var libDir = Path.Combine(gameDir, "libraries");
        var isArm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        foreach (var lib in json["libraries"]?.AsArray() ?? [])
        {
            if (lib is null || !McRules.Allowed(lib["rules"])) continue;
            var name = (string?)lib["name"] ?? "";
            string? jar = null;
            var onlyDll = false;

            var classifier = Installer.NativeClassifier(lib);
            if (classifier is not null && lib["downloads"]?["classifiers"]?[classifier]?["path"] is { } p)
            {
                jar = Path.Combine(libDir, (string)p!);
            }
            else if (name.Contains(":natives-windows"))
            {
                var wantArm = name.Contains("arm64");
                if (wantArm != isArm || name.Contains("x86") && Environment.Is64BitOperatingSystem) continue;
                var rel = (string?)lib["downloads"]?["artifact"]?["path"] ?? McRules.MavenToPath(name);
                jar = Path.Combine(libDir, rel);
                onlyDll = true;
            }
            if (jar is null || !File.Exists(jar)) continue;

            var excludes = lib["extract"]?["exclude"]?.AsArray().Select(e => (string?)e ?? "").ToList() ?? [];
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || entry.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                    if (excludes.Any(ex => ex.Length > 0 && entry.FullName.StartsWith(ex, StringComparison.OrdinalIgnoreCase))) continue;
                    if (onlyDll && !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(nativesDir, onlyDll ? entry.Name : entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (File.Exists(target) && new FileInfo(target).Length == entry.Length) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, true);
                    }
                    catch (IOException) { /* 文件被占用时沿用旧文件 */ }
                }
            }
            catch (InvalidDataException ex)
            {
                Log.Warn($"natives 文件损坏，已跳过：{jar}（{ex.Message}）");
            }
        }
    }

    /// <summary>
    /// 很旧的版本需要把资源复制成原始文件名；返回 game_assets 目录
    /// </summary>
    private static string PrepareLegacyAssets(string gameDir, JsonObject json, string runDir)
    {
        var assetsRoot = Path.Combine(gameDir, "assets");
        var id = (string?)json["assetIndex"]?["id"] ?? (string?)json["assets"];
        if (id is null) return assetsRoot;
        var indexPath = Path.Combine(assetsRoot, "indexes", id + ".json");
        if (!File.Exists(indexPath)) return assetsRoot;
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        var isVirtual = (bool?)index["virtual"] ?? false;
        var mapToResources = (bool?)index["map_to_resources"] ?? false;
        if (!isVirtual && !mapToResources) return assetsRoot;

        var target = mapToResources ? Path.Combine(runDir, "resources") : Path.Combine(assetsRoot, "virtual", "legacy");
        foreach (var (rel, obj) in index["objects"]!.AsObject())
        {
            var hash = (string)obj!["hash"]!;
            var src = Path.Combine(assetsRoot, "objects", hash[..2], hash);
            var dst = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src) || File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
        }
        return target;
    }

    /// <summary>
    /// 自动内存：按物理内存的四成估算，原版至多 3G，带 Mod 至多 6G；
    /// 同时不超过系统当前还能提交的内存，避免内存紧张时 Java 直接启动失败
    /// </summary>
    public static int AutoMemoryMb(InstalledVersion version)
    {
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        var cap = version.Loader == LoaderKind.Vanilla ? 3072 : 6144;
        var mb = Math.Clamp(total * 0.4, 1024, cap);
        var availCommit = (long)(MemoryCleaner.Status().AvailCommit / 1024 / 1024);
        if (availCommit > 0) mb = Math.Min(mb, Math.Max(1024, availCommit - 768));
        return (int)mb;
    }

    private static List<string> BuildArguments(string gameDir, InstalledVersion version, JsonObject json, Account account,
        JavaInstallation java, string nativesDir, string runDir, string gameAssets)
    {
        var s = Settings.Current;
        var libDir = Path.Combine(gameDir, "libraries");

        // classpath：库（去重，子版本优先）+ 客户端 jar
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cp = new List<string>();
        foreach (var lib in json["libraries"]?.AsArray() ?? [])
        {
            if (lib is null || !McRules.Allowed(lib["rules"])) continue;
            var name = (string?)lib["name"];
            if (name is null) continue;
            var artifact = lib["downloads"]?["artifact"];
            if (artifact is null && lib["natives"] is not null) continue; // 只有 natives 的库不进 classpath
            if (!seen.Add(McRules.LibraryKey(name))) continue;
            var rel = (string?)artifact?["path"] ?? McRules.MavenToPath(name);
            cp.Add(Path.Combine(libDir, rel));
        }
        cp.Add(Installer.JarPath(gameDir, json, version.Id));
        var classpath = string.Join(";", cp.Distinct(StringComparer.OrdinalIgnoreCase));

        var memory = s.AutoMemory ? AutoMemoryMb(version) : Math.Max(512, s.MaxMemoryMb);
        TraceInfo($"分配内存 {memory} MB，系统剩余可提交内存 {MemoryCleaner.Status().AvailCommit / 1024 / 1024} MB");
        var isMs = account.Kind == AccountKind.Microsoft;
        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = version.Id,
            ["game_directory"] = runDir,
            ["assets_root"] = Path.Combine(gameDir, "assets"),
            ["game_assets"] = gameAssets,
            ["assets_index_name"] = (string?)json["assetIndex"]?["id"] ?? (string?)json["assets"] ?? "legacy",
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = $"token:{account.AccessToken}:{account.Uuid}",
            ["clientid"] = "",
            ["auth_xuid"] = "0",
            ["user_type"] = isMs ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_type"] = LauncherName,
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
            ["classpath"] = classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = libDir,
            ["resolution_width"] = s.WindowWidth.ToString(),
            ["resolution_height"] = s.WindowHeight.ToString(),
        };
        var features = new Dictionary<string, bool> { ["has_custom_resolution"] = !s.Fullscreen };

        var args = new List<string>
        {
            $"-Xmx{memory}m",
            // 初始堆从小开始，按需增长，减少启动瞬间的内存占用
            "-Xms256m",
            "-XX:+UseG1GC",
            "-XX:-UseAdaptiveSizePolicy",
            "-XX:-OmitStackTraceInFastThrow",
            "-Dfml.ignoreInvalidMinecraftCertificates=true",
            "-Dfml.ignorePatchDiscrepancies=true",
            "-Dlog4j2.formatMsgNoLookups=true",
            // 让游戏输出用 UTF-8，启动器读取日志时不乱码
            java.MajorVersion >= 18 ? "-Dstdout.encoding=UTF-8" : "-Dsun.stdout.encoding=UTF-8",
            java.MajorVersion >= 18 ? "-Dstderr.encoding=UTF-8" : "-Dsun.stderr.encoding=UTF-8",
        };

        var jvm = json["arguments"]?["jvm"] as JsonArray;
        if (jvm is not null) args.AddRange(Expand(jvm, vars, features));
        else
        {
            args.Add($"-Djava.library.path={nativesDir}");
            args.Add("-cp");
            args.Add(classpath);
        }
        args.AddRange(SplitArgs(s.ExtraJvmArgs));
        args.Add((string?)json["mainClass"] ?? throw new InvalidDataException("版本 JSON 缺少 mainClass"));

        var game = json["arguments"]?["game"] as JsonArray;
        var hasSize = false;
        if (game is not null)
        {
            var expanded = Expand(game, vars, features).ToList();
            hasSize = expanded.Contains("--width");
            args.AddRange(expanded);
        }
        // 旧格式（会被 Fabric 等继承版本覆盖成新格式时也兼容）
        if ((string?)json["minecraftArguments"] is { } legacy && game is null)
            args.AddRange(SplitArgs(legacy).Select(a => Replace(a, vars)));
        if (s.Fullscreen) args.Add("--fullscreen");
        else if (!hasSize)
        {
            args.AddRange(["--width", vars["resolution_width"], "--height", vars["resolution_height"]]);
        }
        args.AddRange(SplitArgs(s.ExtraGameArgs));
        return args;
    }

    /// <summary>
    /// 展开新格式参数数组：字符串直接替换变量，对象需要满足规则
    /// </summary>
    private static IEnumerable<string> Expand(JsonArray arr, Dictionary<string, string> vars, Dictionary<string, bool> features)
    {
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var str))
            {
                yield return Replace(str, vars);
            }
            else if (item is JsonObject obj && McRules.Allowed(obj["rules"], features))
            {
                var value = obj["value"];
                if (value is JsonArray many)
                {
                    foreach (var m in many) yield return Replace((string?)m ?? "", vars);
                }
                else if (value is not null)
                {
                    yield return Replace((string?)value ?? "", vars);
                }
            }
        }
    }

    private static string Replace(string text, Dictionary<string, string> vars)
    {
        if (!text.Contains("${")) return text;
        var sb = new StringBuilder(text);
        foreach (var (k, v) in vars) sb.Replace("${" + k + "}", v);
        return sb.ToString();
    }

    /// <summary>
    /// 按空格拆分参数，支持双引号
    /// </summary>
    public static List<string> SplitArgs(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
