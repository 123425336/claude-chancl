using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Java.Parser;
using PCL.Core.Minecraft.Java.Scanner;

namespace ClaudeCL.Core;

/// <summary>
/// Java 的查找、选择与自动下载
/// </summary>
public static class JavaFinder
{
    private static readonly IJavaParser Parser = new PeHeaderParser();
    private static readonly SemaphoreSlim ScanLock = new(1, 1);

    public static IReadOnlyList<JavaInstallation> Installed { get; private set; } = [];

    /// <summary>
    /// 扫描本机所有 Java（注册表、PATH、应用商店、where 命令，以及常见安装目录）
    /// </summary>
    public static async Task<IReadOnlyList<JavaInstallation>> ScanAsync()
    {
        await ScanLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var paths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            IJavaScanner[] scanners =
            [
                new RegistryJavaScanner(), new PathEnvironmentScanner(),
                new MicrosoftStoreJavaScanner(), new WhereCommandScanner(),
            ];
            await Task.Run(() =>
            {
                Parallel.ForEach(scanners, s =>
                {
                    var temp = new List<string>();
                    try { s.Scan(temp); } catch (Exception ex) { Log.Warn($"Java 扫描器出错：{ex.Message}"); }
                    foreach (var p in temp) paths.TryAdd(Path.GetFullPath(p), true);
                });
                foreach (var p in ScanFolders()) paths.TryAdd(p, true);
            }).ConfigureAwait(false);

            var list = paths.Keys
                .Where(p => p.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
                // 排除系统里那个只用来跳转的 javapath 包装器
                .Where(p => !p.Contains(@"\Common Files\Oracle\Java\", StringComparison.OrdinalIgnoreCase))
                .Select(p => { try { return Parser.Parse(p); } catch { return null; } })
                .Where(j => j is not null)
                .Select(j => j!)
                .DistinctBy(j => j.JavaFolder.ToLowerInvariant())
                .OrderByDescending(j => j.MajorVersion).ThenByDescending(j => j.Is64Bit)
                .ToList();
            Installed = list;
            Log.Info($"找到 {list.Count} 个 Java：\n  " + string.Join("\n  ", list.Select(j => j.ToDetailedString())));
            return list;
        }
        finally
        {
            ScanLock.Release();
        }
    }

    /// <summary>
    /// 在启动器运行时目录、游戏目录与常见安装位置中查找 java.exe
    /// </summary>
    private static IEnumerable<string> ScanFolders()
    {
        var roots = new List<string>
        {
            AppPaths.RuntimeDir,
            Path.Combine(Settings.Current.GameDir, "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
        };
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Zulu", "Microsoft", "BellSoft", "Amazon Corretto", "Semeru", "OpenJDK" })
                roots.Add(Path.Combine(pf, vendor));
        }
        var found = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
            Search(root, 0, found);
        return found;
    }

    private static void Search(string dir, int depth, List<string> found)
    {
        if (depth > 5) return;
        try
        {
            var exe = Path.Combine(dir, "bin", "java.exe");
            if (File.Exists(exe)) { found.Add(exe); return; }
            foreach (var sub in Directory.EnumerateDirectories(dir))
                Search(sub, depth + 1, found);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// 为指定的 Java 主版本需求挑选最合适的 Java；找不到返回 null
    /// </summary>
    public static JavaInstallation? Pick(int required)
    {
        var all = Installed.Where(j => j.IsStillAvailable && j.Is64Bit).ToList();
        if (all.Count == 0) all = Installed.Where(j => j.IsStillAvailable).ToList();
        // 优先完全匹配，其次选不低于需求的最低版本
        return all.FirstOrDefault(j => j.MajorVersion == required)
               ?? (required <= 8 ? null : all.Where(j => j.MajorVersion > required).OrderBy(j => j.MajorVersion).FirstOrDefault());
    }

    /// <summary>
    /// 根据版本 JSON 计算需要的 Java 主版本
    /// </summary>
    public static (int Major, string Component) Requirement(JsonObject json)
    {
        var major = (int?)json["javaVersion"]?["majorVersion"];
        var component = (string?)json["javaVersion"]?["component"];
        if (major is null) return (8, "jre-legacy");
        return (major.Value, component ?? major.Value switch
        {
            <= 8 => "jre-legacy",
            <= 16 => "java-runtime-alpha",
            <= 17 => "java-runtime-gamma",
            _ => "java-runtime-delta",
        });
    }

    private const string RuntimeIndexUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>
    /// 从官方运行时仓库下载一个 Java 组件到启动器数据目录
    /// </summary>
    public static async Task<JavaInstallation> DownloadRuntimeAsync(string component, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("获取 Java 运行时信息", 0.02, component));
        var index = await Net.GetJsonAsync(RuntimeIndexUrl, ct).ConfigureAwait(false);
        var platform = Environment.Is64BitOperatingSystem
            ? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "windows-arm64" : "windows-x64"
            : "windows-x86";
        var entry = index[platform]?[component]?.AsArray().FirstOrDefault()
                    ?? throw new InvalidOperationException($"官方没有提供 {component} 的 Windows 版本");
        var manifest = await Net.GetJsonAsync((string)entry["manifest"]!["url"]!, ct).ConfigureAwait(false);

        var root = Path.Combine(AppPaths.RuntimeDir, component);
        var items = new List<DlItem>();
        foreach (var (rel, file) in manifest["files"]!.AsObject())
        {
            if (file is null) continue;
            var target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if ((string?)file["type"] == "directory") { Directory.CreateDirectory(target); continue; }
            var raw = file["downloads"]?["raw"];
            if ((string?)file["type"] != "file" || raw is null) continue;
            items.Add(new DlItem(target, Net.Candidates((string)raw["url"]!), (string?)raw["sha1"], (long?)raw["size"] ?? -1));
        }
        await Downloader.RunAsync(items, new Progress<DlProgress>(p =>
        {
            if (p.Total > 0) progress?.Report(new("下载 Java 运行时", 0.05 + 0.9 * p.Done / p.Total, $"{p.Done} / {p.Total}"));
        }), ct).ConfigureAwait(false);

        var exe = Path.Combine(root, "bin", "java.exe");
        var java = Parser.Parse(exe) ?? throw new InvalidOperationException("Java 下载完成但无法识别");
        Installed = Installed.Append(java).OrderByDescending(j => j.MajorVersion).ToList();
        return java;
    }
}
