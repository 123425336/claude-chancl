using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>OptiFine 的一个可下载版本</summary>
public sealed record OptiFineVersion(string McVersion, string Type, string Patch, string FileName, string? Forge)
{
    public bool IsPreview => Patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase);
    public string Id => $"{McVersion}_{Type}_{Patch}";
    public string Display => $"{Type.Replace("HD_U", "HD U").Replace('_', ' ')} {Patch}{(IsPreview ? "（预览版）" : "")}";
    public override string ToString() => Display;
}

/// <summary>
/// OptiFine 的下载与独立安装（官方站点不提供直链，这里使用镜像源）
/// </summary>
public static class OptiFineInstaller
{
    private const string Base = "https://bmclapi2.bangbang93.com/optifine";

    public static async Task<List<OptiFineVersion>> GetVersionsAsync(string mc, CancellationToken ct = default)
    {
        var json = await Net.GetJsonDirectAsync($"{Base}/{Uri.EscapeDataString(mc)}", ct);
        return json.AsArray()
            .Where(n => n is not null)
            .Select(n => new OptiFineVersion((string)n!["mcversion"]!, (string)n["type"]!, (string)n["patch"]!,
                (string?)n["filename"] ?? $"OptiFine_{mc}_{n["type"]}_{n["patch"]}.jar", (string?)n["forge"]))
            // 正式版在前，同类按补丁号从新到旧
            .OrderBy(v => v.IsPreview)
            .ThenByDescending(v => v.Patch, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Task DownloadAsync(OptiFineVersion v, string target, CancellationToken ct) =>
        Downloader.RunAsync([new DlItem(target, [$"{Base}/{Uri.EscapeDataString(v.McVersion)}/{v.Type}/{v.Patch}"])], null, ct);

    /// <summary>
    /// 不带模组加载器的 OptiFine：给原版打补丁后作为库加载
    /// </summary>
    public static async Task InstallStandaloneAsync(string gameDir, McVersionEntry mc, OptiFineVersion of, string name,
        IProgress<StageProgress> progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ClaudeCL", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var libDir = Path.Combine(gameDir, "libraries");
            var vanillaJar = Path.Combine(VersionStore.VersionsDir(gameDir), mc.Id, mc.Id + ".jar");
            var ofJar = Path.Combine(temp, of.FileName);
            progress.Report(new("下载 OptiFine", 0.05, of.FileName));
            await DownloadAsync(of, ofJar, ct);

            var libName = $"optifine:OptiFine:{of.Id}";
            var libPath = Path.Combine(libDir, McRules.MavenToPath(libName));
            Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);

            // 启动包装器：新版本 OptiFine 自带专用版本
            string wrapperName;
            bool hasPatcher;
            using (var zip = ZipFile.OpenRead(ofJar))
            {
                hasPatcher = zip.GetEntry("optifine/Patcher.class") is not null;
                if (zip.GetEntry("launchwrapper-2.0.jar") is { } lw2)
                {
                    wrapperName = "optifine:launchwrapper:2.0";
                    Extract(lw2, Path.Combine(libDir, McRules.MavenToPath(wrapperName)));
                }
                else if (zip.GetEntry("launchwrapper-of.txt") is { } txt)
                {
                    using var reader = new StreamReader(txt.Open());
                    var ver = reader.ReadToEnd().Trim();
                    wrapperName = $"optifine:launchwrapper-of:{ver}";
                    var inner = zip.GetEntry($"launchwrapper-of-{ver}.jar") ?? throw new InvalidDataException("OptiFine 缺少启动包装器");
                    Extract(inner, Path.Combine(libDir, McRules.MavenToPath(wrapperName)));
                }
                else
                {
                    wrapperName = "net.minecraft:launchwrapper:1.12";
                }
            }

            if (hasPatcher)
            {
                progress.Report(new("给原版打 OptiFine 补丁", 0.4, of.Id));
                var vanilla = VersionStore.Load(gameDir, mc.Id);
                if (JavaFinder.Installed.Count == 0) await JavaFinder.ScanAsync();
                var (major, component) = JavaFinder.Requirement(vanilla.Json!);
                var java = JavaFinder.Pick(major)
                           ?? JavaFinder.Installed.Where(j => j.IsStillAvailable).OrderByDescending(j => j.MajorVersion).FirstOrDefault()
                           ?? await JavaFinder.DownloadRuntimeAsync(component, progress, ct);
                await ForgeInstaller.RunJavaAsync(java, [ofJar], "optifine.Patcher", [vanillaJar, ofJar, libPath], ct);
            }
            else
            {
                File.Copy(ofJar, libPath, true);
            }

            var parent = VersionStore.Load(gameDir, mc.Id).Json!;
            var json = new JsonObject
            {
                ["id"] = name,
                ["inheritsFrom"] = mc.Id,
                ["type"] = "release",
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = libName }, new JsonObject { ["name"] = wrapperName }),
            };
            // 旧版本参数是一整行字符串，需要在父版本的基础上追加
            if ((string?)parent["minecraftArguments"] is { } legacyArgs)
                json["minecraftArguments"] = legacyArgs + " --tweakClass optifine.OptiFineTweaker";
            else
                json["arguments"] = new JsonObject { ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker") };
            Installer.WriteVersionJson(gameDir, name, json);
            progress.Report(new("OptiFine 安装完成", 1));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 忽略 */ }
        }
    }

    private static void Extract(ZipArchiveEntry entry, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, true);
    }
}
