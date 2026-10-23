using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

public enum LoaderType { Forge, NeoForge, Fabric, Quilt }

/// <summary>某个加载器的一个可安装版本</summary>
public sealed record LoaderVersion(string Version, bool Recommended, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// 模组加载器的版本查询与安装入口
/// </summary>
public static class LoaderService
{
    public static string DisplayName(LoaderType t) => t switch
    {
        LoaderType.Forge => "Forge",
        LoaderType.NeoForge => "NeoForge",
        LoaderType.Fabric => "Fabric",
        _ => "Quilt",
    };

    public static LoaderType? FromKind(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => LoaderType.Forge,
        LoaderKind.NeoForge => LoaderType.NeoForge,
        LoaderKind.Fabric => LoaderType.Fabric,
        LoaderKind.Quilt => LoaderType.Quilt,
        _ => null,
    };

    /// <summary>
    /// 查询某个游戏版本可用的加载器版本（新的在前，推荐版本带标记）
    /// </summary>
    public static async Task<List<LoaderVersion>> GetVersionsAsync(LoaderType type, string mc, CancellationToken ct = default)
    {
        return type switch
        {
            LoaderType.Fabric => await GetMetaLoadersAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}", ct),
            LoaderType.Quilt => await GetMetaLoadersAsync($"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mc)}", ct),
            LoaderType.Forge => await ForgeInstaller.GetForgeVersionsAsync(mc, ct),
            _ => await ForgeInstaller.GetNeoForgeVersionsAsync(mc, ct),
        };
    }

    /// <summary>
    /// Fabric 与 Quilt 的元数据接口格式相同
    /// </summary>
    private static async Task<List<LoaderVersion>> GetMetaLoadersAsync(string url, CancellationToken ct)
    {
        var json = await Net.GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = json.AsArray()
            .Select(n => n!["loader"]!)
            .Select(l => (Version: (string)l["version"]!, Stable: (bool?)l["stable"] ?? !((string)l["version"]!).Contains('-')))
            // 接口返回的顺序不一定是新到旧，按版本号重新排序（同版本号时正式版在前）
            .OrderByDescending(l => ForgeInstaller.SortKey(l.Version.Split('-')[0]))
            .ThenByDescending(l => l.Stable)
            .ThenByDescending(l => ForgeInstaller.SortKey(l.Version))
            .ToList();
        var recommended = list.FirstOrDefault(l => l.Stable).Version;
        return list.Select(l => new LoaderVersion(l.Version, l.Version == recommended,
            l.Version == recommended ? $"{l.Version}（推荐）" : l.Stable ? l.Version : $"{l.Version}（测试版）")).ToList();
    }

    /// <summary>
    /// 默认的版本名称
    /// </summary>
    public static string SuggestName(string mc, LoaderType? loader, string? loaderVersion, bool optifine)
    {
        var name = mc;
        if (loader is not null) name += $"-{DisplayName(loader.Value)}";
        if (loader is LoaderType.Forge or LoaderType.NeoForge && !string.IsNullOrEmpty(loaderVersion))
            name += "_" + ForgeInstaller.ShortVersion(mc, loaderVersion);
        if (optifine) name += "-OptiFine";
        return name;
    }

    /// <summary>
    /// 安装一个游戏版本：原版 + 可选的模组加载器 + 可选的 OptiFine
    /// </summary>
    public static async Task InstallAsync(string gameDir, McVersionEntry mc, LoaderType? loader, string? loaderVersion,
        OptiFineVersion? optifine, string name, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        if (loader is null && optifine is null)
        {
            await Installer.InstallVanillaAsync(gameDir, mc, name, progress, ct);
            return;
        }

        // 1. 先准备好原版（加载器安装器和 OptiFine 都需要原版客户端）
        progress?.Report(new("准备原版文件", 0.01, mc.Id));
        await Installer.EnsureVanillaJsonAsync(gameDir, mc, ct);
        await Installer.CompleteAsync(gameDir, mc.Id, Scale(progress, 0.02, 0.5), ct);

        // 2. 安装加载器或独立的 OptiFine
        var stage = Scale(progress, 0.5, 0.85);
        switch (loader)
        {
            case LoaderType.Fabric:
                stage.Report(new("获取 Fabric 配置", 0, loaderVersion ?? ""));
                await InstallProfileAsync(gameDir, mc, name,
                    $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc.Id)}/{Uri.EscapeDataString(loaderVersion!)}/profile/json", ct);
                break;
            case LoaderType.Quilt:
                stage.Report(new("获取 Quilt 配置", 0, loaderVersion ?? ""));
                await InstallProfileAsync(gameDir, mc, name,
                    $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mc.Id)}/{Uri.EscapeDataString(loaderVersion!)}/profile/json", ct);
                break;
            case LoaderType.Forge:
            case LoaderType.NeoForge:
                await ForgeInstaller.InstallAsync(gameDir, mc, loader.Value, loaderVersion!, name, stage, ct);
                break;
            default:
                await OptiFineInstaller.InstallStandaloneAsync(gameDir, mc, optifine!, name, stage, ct);
                break;
        }

        // 3. Forge 版本的 OptiFine 以模组形式放进 mods 文件夹
        if (optifine is not null && loader is LoaderType.Forge)
        {
            progress?.Report(new("下载 OptiFine", 0.86, optifine.FileName));
            var runDir = Settings.Current.IsolateVersions ? Path.Combine(VersionStore.VersionsDir(gameDir), name) : gameDir;
            await OptiFineInstaller.DownloadAsync(optifine, Path.Combine(runDir, "mods", optifine.FileName), ct);
        }

        // 4. 补全新版本的全部文件
        await Installer.CompleteAsync(gameDir, name, Scale(progress, 0.88, 1), ct);
    }

    private static async Task InstallProfileAsync(string gameDir, McVersionEntry mc, string name, string url, CancellationToken ct)
    {
        var profile = (await Net.GetJsonAsync(url, ct)).AsObject();
        profile["id"] = name;
        profile["inheritsFrom"] = mc.Id;
        Installer.WriteVersionJson(gameDir, name, profile);
    }

    /// <summary>
    /// 把子步骤的 0~1 进度映射到总进度的一段区间
    /// </summary>
    public static IProgress<StageProgress> Scale(IProgress<StageProgress>? progress, double from, double to) =>
        new SyncProgress(p => progress?.Report(p with { Percent = from + (to - from) * Math.Clamp(p.Percent, 0, 1) }));

    /// <summary>直接转发的进度回调（外层的回调已负责切回界面线程）</summary>
    private sealed class SyncProgress(Action<StageProgress> action) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => action(value);
    }
}
