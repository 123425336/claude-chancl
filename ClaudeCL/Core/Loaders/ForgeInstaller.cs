using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Minecraft.Java;

namespace ClaudeCL.Core;

/// <summary>
/// Forge 与 NeoForge 的安装：两者使用同一种安装器格式
/// </summary>
internal static class ForgeInstaller
{
    // ---------------- 版本列表 ----------------

    public static async Task<List<LoaderVersion>> GetForgeVersionsAsync(string mc, CancellationToken ct)
    {
        List<string> full;
        string? recommended = null, latest = null;
        try
        {
            var meta = await Net.GetJsonDirectAsync("https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json", ct);
            full = meta[mc]?.AsArray().Select(v => (string)v!).ToList() ?? [];
            try
            {
                var promos = (await Net.GetJsonDirectAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct))["promos"];
                recommended = (string?)promos?[$"{mc}-recommended"];
                latest = (string?)promos?[$"{mc}-latest"];
            }
            catch (Exception ex) { Log.Warn("获取 Forge 推荐版本失败：" + ex.Message); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 官方源不可用时改用镜像的列表接口
            Log.Warn("官方 Forge 列表获取失败，改用镜像：" + ex.Message);
            var list = await Net.GetJsonDirectAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(mc)}", ct);
            full = list.AsArray().Select(n =>
            {
                var branch = (string?)n!["branch"];
                return $"{mc}-{(string)n["version"]!}{(string.IsNullOrEmpty(branch) ? "" : "-" + branch)}";
            }).ToList();
        }

        return full
            .Select(f => (Full: f, Short: ShortVersion(mc, f)))
            .OrderByDescending(v => SortKey(v.Short))
            .Select(v =>
            {
                var isRec = v.Short == recommended;
                var tag = isRec ? "（推荐）" : v.Short == latest ? "（最新）" : "";
                return new LoaderVersion(v.Full, isRec, v.Short + tag);
            })
            .ToList();
    }

    public static async Task<List<LoaderVersion>> GetNeoForgeVersionsAsync(string mc, CancellationToken ct)
    {
        // 1.20.1 的 NeoForge 沿用旧的坐标，之后的版本改为独立坐标
        var legacy = mc == "1.20.1";
        var url = legacy
            ? "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/forge"
            : "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
        var json = await Net.GetJsonDirectAsync(url, ct);
        var versions = json["versions"]!.AsArray().Select(v => (string)v!)
            .Where(v => legacy ? v.StartsWith("1.20.1-") : NeoForgeGameVersion(v) == mc)
            .Reverse()
            .ToList();
        var recommended = versions.FirstOrDefault(v => !v.Contains("beta", StringComparison.OrdinalIgnoreCase));
        return versions.Select(v =>
        {
            var tag = v == recommended ? "（推荐）" : v.Contains("beta", StringComparison.OrdinalIgnoreCase) ? "（测试版）" : "";
            return new LoaderVersion(v, v == recommended, ShortVersion(mc, v) + tag);
        }).ToList();
    }

    /// <summary>
    /// NeoForge 版本号推算对应的游戏版本：20.4.x → 1.20.4，21.0.x → 1.21，26.1.0.x → 26.1
    /// </summary>
    private static string? NeoForgeGameVersion(string v)
    {
        var parts = v.Split('-')[0].Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) return null;
        if (a >= 25)
        {
            // 年份制版本号
            if (parts.Length >= 4 && int.TryParse(parts[2], out var c) && c != 0) return $"{a}.{b}.{c}";
            return $"{a}.{b}";
        }
        return b == 0 ? $"1.{a}" : $"1.{a}.{b}";
    }

    /// <summary>
    /// 去掉完整版本号里的游戏版本前后缀，只保留加载器自己的版本号
    /// </summary>
    public static string ShortVersion(string mc, string full)
    {
        var s = full;
        if (s.StartsWith(mc + "-")) s = s[(mc.Length + 1)..];
        if (s.EndsWith("-" + mc)) s = s[..^(mc.Length + 1)];
        return s;
    }

    internal static string SortKey(string version)
    {
        // 每段数字补零，便于按字符串比较大小
        return string.Join(".", Regex.Matches(version, @"\d+").Select(m => m.Value.PadLeft(8, '0')));
    }

    private static string InstallerUrl(LoaderType type, string mc, string version) => type == LoaderType.Forge
        ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}/forge-{version}-installer.jar"
        : mc == "1.20.1"
            ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{version}/forge-{version}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";

    // ---------------- 安装 ----------------

    public static async Task InstallAsync(string gameDir, McVersionEntry mc, LoaderType type, string version, string name,
        IProgress<StageProgress> progress, CancellationToken ct)
    {
        var loaderName = LoaderService.DisplayName(type);
        var temp = Path.Combine(Path.GetTempPath(), "ClaudeCL", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var libDir = Path.Combine(gameDir, "libraries");
            var vanillaJar = Path.Combine(VersionStore.VersionsDir(gameDir), mc.Id, mc.Id + ".jar");
            var installer = Path.Combine(temp, "installer.jar");
            progress.Report(new($"下载 {loaderName} 安装器", 0, version));
            await Downloader.RunAsync([new DlItem(installer, Net.Candidates(InstallerUrl(type, mc.Id, version)))], null, ct);

            JsonObject versionJson;
            using (var zip = ZipFile.OpenRead(installer))
            {
                var profile = ReadJson(zip, "install_profile.json")
                              ?? throw new InvalidDataException("安装器里没有安装配置，这个版本可能太旧了");

                if (profile["versionInfo"] is JsonObject info)
                {
                    // 旧格式（1.12.2 及更早）：直接解压通用包
                    versionJson = (JsonObject)info.DeepClone();
                    var install = profile["install"]!;
                    var target = Path.Combine(libDir, McRules.MavenToPath((string)install["path"]!));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var entry = zip.GetEntry((string)install["filePath"]!) ?? throw new InvalidDataException("安装器里缺少主文件");
                    entry.ExtractToFile(target, true);
                }
                else
                {
                    // 新格式：下载依赖后运行安装处理器生成客户端文件
                    var jsonPath = ((string?)profile["json"] ?? "/version.json").TrimStart('/');
                    versionJson = ReadJson(zip, jsonPath) ?? throw new InvalidDataException("安装器里缺少版本信息");

                    var items = new List<DlItem>();
                    var libs = (profile["libraries"]?.AsArray() ?? []).Concat(versionJson["libraries"]?.AsArray() ?? []);
                    foreach (var lib in libs)
                    {
                        if (lib is null) continue;
                        var art = lib["downloads"]?["artifact"];
                        var rel = (string?)art?["path"] ?? McRules.MavenToPath((string)lib["name"]!);
                        var target = Path.Combine(libDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        var url = (string?)art?["url"];
                        if (string.IsNullOrEmpty(url))
                        {
                            // 没有下载地址的库随安装器一起提供，或由处理器生成
                            if (zip.GetEntry("maven/" + rel) is { } packed && !File.Exists(target))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                packed.ExtractToFile(target, true);
                            }
                            continue;
                        }
                        items.Add(new DlItem(target, Net.Candidates(url), (string?)art?["sha1"], (long?)art?["size"] ?? -1));
                    }
                    await Downloader.RunAsync(items, new Progress<DlProgress>(p =>
                    {
                        if (p.Total > 0) progress.Report(new($"下载 {loaderName} 依赖", 0.05 + 0.25 * p.Done / p.Total, $"{p.Done} / {p.Total}"));
                    }), ct);

                    await RunProcessorsAsync(zip, profile, gameDir, mc.Id, vanillaJar, installer, temp, progress, ct);
                }
            }

            // 写入版本信息；客户端复制一份到新版本目录（部分加载器要求客户端文件名与版本名一致）
            versionJson["id"] = name;
            versionJson["inheritsFrom"] = mc.Id;
            versionJson["jar"] = name;
            var dir = Path.Combine(VersionStore.VersionsDir(gameDir), name);
            Directory.CreateDirectory(dir);
            if (File.Exists(vanillaJar)) File.Copy(vanillaJar, Path.Combine(dir, name + ".jar"), true);
            Installer.WriteVersionJson(gameDir, name, versionJson);
            progress.Report(new($"{loaderName} 安装完成", 1));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 临时文件删不掉也没关系 */ }
        }
    }

    private static JsonObject? ReadJson(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
    }

    /// <summary>
    /// 依次运行安装配置里的处理器（反混淆、打补丁等），生成最终的客户端文件
    /// </summary>
    private static async Task RunProcessorsAsync(ZipArchive zip, JsonObject profile, string gameDir, string mcId, string vanillaJar,
        string installer, string temp, IProgress<StageProgress> progress, CancellationToken ct)
    {
        var libDir = Path.Combine(gameDir, "libraries");
        string Lib(string coord) => Path.Combine(libDir, McRules.MavenToPath(coord).Replace('/', Path.DirectorySeparatorChar));

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SIDE"] = "client",
            ["MINECRAFT_JAR"] = vanillaJar,
            ["MINECRAFT_VERSION"] = mcId,
            ["ROOT"] = gameDir,
            ["INSTALLER"] = installer,
            ["LIBRARY_DIR"] = libDir,
        };
        foreach (var (key, val) in profile["data"] as JsonObject ?? [])
        {
            var v = (string?)val?["client"];
            if (v is null) continue;
            if (v.StartsWith('[') && v.EndsWith(']')) data[key] = Lib(v[1..^1]);
            else if (v.StartsWith('\'')) data[key] = v.Trim('\'');
            else if (v.StartsWith('/'))
            {
                // 安装器内的文件，解压到临时目录后使用
                var inner = v.TrimStart('/');
                var dst = Path.Combine(temp, inner.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                zip.GetEntry(inner)?.ExtractToFile(dst, true);
                data[key] = dst;
            }
            else data[key] = v;
        }

        string Arg(string a)
        {
            if (a.StartsWith('[') && a.EndsWith(']')) return Lib(a[1..^1]);
            return Regex.Replace(a, @"\{(\w+)\}", m => data.TryGetValue(m.Groups[1].Value, out var x) ? x : m.Value);
        }

        var processors = (profile["processors"]?.AsArray() ?? [])
            .Where(p => p is not null && (p["sides"] is not JsonArray sides || sides.Any(s => (string?)s == "client")))
            .ToList();
        if (processors.Count == 0) return;

        var vanilla = VersionStore.Load(gameDir, mcId);
        var java = await PickJavaAsync(vanilla.Json, progress, ct);

        for (var i = 0; i < processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var proc = processors[i]!;
            var jar = Lib((string)proc["jar"]!);
            var main = ReadMainClass(jar) ?? throw new InvalidDataException($"处理器缺少入口：{proc["jar"]}");
            var cp = new List<string> { jar };
            cp.AddRange(proc["classpath"]?.AsArray().Select(c => Lib((string)c!)) ?? []);
            var args = proc["args"]?.AsArray().Select(a => Arg((string)a!)).ToList() ?? [];

            // 输出文件都已存在且校验一致时跳过
            if (proc["outputs"] is JsonObject outs && outs.Count > 0 &&
                outs.All(o => Sha1Matches(Arg(o.Key), Arg((string?)o.Value ?? "").Trim('\''))))
                continue;

            progress.Report(new("运行安装处理器", 0.35 + 0.65 * i / processors.Count, $"{i + 1} / {processors.Count} · {main.Split('.').Last()}"));
            await RunJavaAsync(java, cp, main, args, ct);
        }
    }

    private static async Task<JavaInstallation> PickJavaAsync(JsonObject? vanillaJson, IProgress<StageProgress> progress, CancellationToken ct)
    {
        if (JavaFinder.Installed.Count == 0) await JavaFinder.ScanAsync();
        var (major, component) = vanillaJson is null ? (8, "jre-legacy") : JavaFinder.Requirement(vanillaJson);
        var java = JavaFinder.Pick(major)
                   ?? JavaFinder.Installed.Where(j => j.MajorVersion >= major && j.IsStillAvailable).OrderBy(j => j.MajorVersion).FirstOrDefault();
        return java ?? await JavaFinder.DownloadRuntimeAsync(component, progress, ct);
    }

    private static string? ReadMainClass(string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        var entry = zip.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("Main-Class:")) return line["Main-Class:".Length..].Trim();
        return null;
    }

    private static bool Sha1Matches(string path, string sha1)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(fs)).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 用 Java 运行一个处理器，失败时带上最后的输出
    /// </summary>
    internal static async Task RunJavaAsync(JavaInstallation java, IEnumerable<string> classpath, string main, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(java.JavaExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 崩溃日志等杂物写到临时目录
            WorkingDirectory = Path.GetTempPath(),
        };
        // 显式限制内存：默认会按物理内存预留很大的堆，内存紧张时直接启动失败
        psi.ArgumentList.Add("-Xms32m");
        psi.ArgumentList.Add("-Xmx2g");
        psi.ArgumentList.Add("-XX:+UseSerialGC");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(";", classpath));
        psi.ArgumentList.Add(main);
        foreach (var a in args) psi.ArgumentList.Add(a);

        var lines = new Queue<string>();
        void Collect(string? s)
        {
            if (s is null) return;
            lock (lines)
            {
                lines.Enqueue(s);
                while (lines.Count > 30) lines.Dequeue();
            }
        }
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* 进程已退出 */ }
            throw;
        }
        if (process.ExitCode != 0)
        {
            string tail;
            lock (lines) tail = string.Join("\n", lines);
            Log.Warn($"处理器 {main} 失败：\n{tail}");
            throw new InvalidOperationException($"安装处理器运行失败（{main.Split('.').Last()}，退出码 {process.ExitCode}）。\n{tail}");
        }
    }
}
