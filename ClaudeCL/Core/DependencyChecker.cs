using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>设置里关闭了联网识别模组</summary>
internal sealed class LookupDisabledException : Exception;

/// <summary>需要下载的一个前置</summary>
public sealed record DownloadPlan(string Title, string ProjectId, ResourceFile File, string RequiredBy);

/// <summary>找不到的前置</summary>
public sealed record MissingDependency(string Id, string RequiredBy);

/// <summary>前置检查的结果</summary>
public sealed class DependencyReport
{
    /// <summary>已经在文件夹里但被停用的前置</summary>
    public List<(ModEntry Mod, string RequiredBy)> ToEnable { get; } = new();

    /// <summary>需要下载的前置（已包含前置的前置）</summary>
    public List<DownloadPlan> ToDownload { get; } = new();

    /// <summary>自动找不到的前置</summary>
    public List<MissingDependency> Unresolved { get; } = new();

    /// <summary>联网查询失败（只做了本地检查）</summary>
    public string? OnlineError { get; set; }

    public bool HasIssues => ToEnable.Count > 0 || ToDownload.Count > 0 || Unresolved.Count > 0;
    public bool CanFix => ToEnable.Count > 0 || ToDownload.Count > 0;
}

/// <summary>
/// 前置模组检查：找出被停用或缺失的必需前置，并递归处理“前置的前置”
/// </summary>
public static class DependencyChecker
{
    // 由游戏本体或加载器提供，不需要单独安装
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quilt-loader",
        "forge", "neoforge", "fml", "javafml", "lowcodefml", "mcp", "mod_minecraftforge",
    };

    private const int MaxDepth = 6;

#if DEBUG
    /// <summary>调试：不联网，只做本地检查</summary>
    public static bool DebugOffline { get; set; }
#endif

    /// <summary>
    /// 检查一个版本的模组前置。同时使用两种信息：
    /// 模组文件里声明的前置（按模组 ID），以及资源平台上登记的前置（按资源 ID，部分模组只在平台上登记）。
    /// 被停用的前置加入启用列表，并继续检查它们自己的前置；缺失的联网查找可下载的版本。
    /// </summary>
    public static async Task<DependencyReport> CheckAsync(InstalledVersion version, CancellationToken ct)
    {
        var report = new DependencyReport();
        var modsDir = Path.Combine(version.RunDir(Settings.Current.GameDir), "mods");
        var mods = await Task.Run(() => ModScanner.Scan(modsDir), ct);
        if (mods.Count == 0) return report;
        var loader = version.ModLoaderName;

        // ---------- 1. 联网识别每个模组在平台上对应的资源和登记的前置（失败时只做本地检查） ----------
        var projectOf = new Dictionary<ModEntry, string>();
        var platformDeps = new Dictionary<ModEntry, string[]>();
        var online = true;
        try
        {
#if DEBUG
            if (DebugOffline) throw new InvalidOperationException("调试：只做本地检查");
#endif
            if (!Settings.Current.AllowOnlineModLookup) throw new LookupDisabledException();
            var hashes = await Task.Run(() =>
            {
                var map = new Dictionary<ModEntry, string>();
                foreach (var m in mods)
                {
                    try { map[m] = ModUpdater.Sha1(m.Path); }
                    catch (IOException) { /* 文件被占用时跳过 */ }
                }
                return map;
            }, ct);
            var lookup = await ModrinthClient.LookupAsync(hashes.Values.Distinct().ToList(), ct);
            foreach (var (m, h) in hashes)
            {
                if (!lookup.TryGetValue(h, out var info)) continue;
                projectOf[m] = info.ProjectId;
                platformDeps[m] = info.RequiredProjects;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (LookupDisabledException)
        {
            // 用户关闭了联网识别：只用模组文件里声明的前置，缺失的仍按模组 ID 查找下载（不发送任何哈希）
            Log.Info("已关闭联网识别模组，前置检查只使用模组文件里的声明");
        }
        catch (Exception ex)
        {
            online = false;
            report.OnlineError = ex.GetBaseException().Message;
            Log.Warn("联网识别模组失败，只做本地前置检查：" + ex.Message);
        }

        // ---------- 2. 统一的递归检查 ----------
        var provided = new HashSet<string>(Builtins, StringComparer.OrdinalIgnoreCase);
        var enabledProjects = new HashSet<string>();
        foreach (var m in mods.Where(m => m.Enabled))
        {
            provided.UnionWith(m.ModIds);
            if (projectOf.TryGetValue(m, out var pid)) enabledProjects.Add(pid);
        }
        var disabledById = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var disabledByProject = new Dictionary<string, ModEntry>();
        foreach (var m in mods.Where(m => !m.Enabled))
        {
            // 只按模组自己的 ID 建议启用：前置只是内嵌在别的模组里时，不应该为它启用一整个无关的模组
            foreach (var id in m.OwnIds) disabledById.TryAdd(id, m);
            if (projectOf.TryGetValue(m, out var pid)) disabledByProject.TryAdd(pid, m);
        }

        var missingIds = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var missingProjects = new Dictionary<string, string>();
        var queue = new Queue<ModEntry>(mods.Where(m => m.Enabled));

        void Enable(ModEntry mod, string requiredBy)
        {
            if (report.ToEnable.Any(e => e.Mod == mod)) return;
            report.ToEnable.Add((mod, requiredBy));
            provided.UnionWith(mod.ModIds);
            if (projectOf.TryGetValue(mod, out var pid)) enabledProjects.Add(pid);
            queue.Enqueue(mod);
        }

        while (queue.Count > 0)
        {
            var mod = queue.Dequeue();
            // 模组文件里声明的前置
            foreach (var dep in mod.RequiredIds)
            {
                if (provided.Contains(dep)) continue;
                if (disabledById.TryGetValue(dep, out var d)) Enable(d, mod.Name);
                else missingIds.TryAdd(dep, mod);
            }
            // 平台上登记的前置
            if (platformDeps.TryGetValue(mod, out var deps))
            {
                foreach (var pid in deps)
                {
                    if (enabledProjects.Contains(pid)) continue;
                    if (disabledByProject.TryGetValue(pid, out var d)) Enable(d, mod.Name);
                    else missingProjects.TryAdd(pid, mod.Name);
                }
            }
        }
        // 后面启用的模组可能顺带满足了前面记下的缺失
        foreach (var id in missingIds.Keys.Where(provided.Contains).ToList()) missingIds.Remove(id);
        foreach (var pid in missingProjects.Keys.Where(enabledProjects.Contains).ToList()) missingProjects.Remove(pid);
        if (missingIds.Count == 0 && missingProjects.Count == 0) return Done(report, version);

        if (!online)
        {
            foreach (var (id, by) in missingIds) report.Unresolved.Add(new MissingDependency(id, by.Name));
            return Done(report, version);
        }

        // ---------- 3. 联网查找缺失的前置 ----------
        try
        {
            // 文件夹里已有的资源（停用的也算，避免重复下载）
            var present = new HashSet<string>(projectOf.Values);
            var scheduled = new Dictionary<string, DownloadPlan>();

            // 平台上登记的前置可以直接下载
            foreach (var (pid, by) in missingProjects)
                if (!await ScheduleAsync(pid, null, by, version, loader, present, scheduled, ct))
                    report.Unresolved.Add(new MissingDependency(pid, by));

            // 文件里声明的前置：先按模组 ID 当作短名称查找
            foreach (var (depId, requiredBy) in missingIds)
            {
                ct.ThrowIfCancellationRequested();
                var project = await ModrinthClient.FindProjectAsync(depId, ct);
                if (project is not null && project.ProjectType == "mod")
                {
                    if (present.Contains(project.Id) || scheduled.ContainsKey(project.Id)) continue;
                    if (await ScheduleAsync(project.Id, project.Title, requiredBy.Name, version, loader, present, scheduled, ct)) continue;
                }
                // 查不到时，如果需要它的模组在平台上登记的前置都已安排好，多半就是其中之一（ID 与短名称不一致）
                if (platformDeps.TryGetValue(requiredBy, out var reg) && reg.Length > 0 &&
                    reg.All(p => present.Contains(p) || scheduled.ContainsKey(p)))
                    continue;
                report.Unresolved.Add(new MissingDependency(depId, requiredBy.Name));
            }

            // 要下载的前置自己也有前置，继续递归
            var pending = new Queue<(DownloadPlan Plan, int Depth)>(scheduled.Values.Select(p => (p, 1)));
            while (pending.Count > 0)
            {
                var (plan, depth) = pending.Dequeue();
                if (depth >= MaxDepth) continue;
                foreach (var pid in plan.File.RequiredProjects.Where(p => !present.Contains(p) && !scheduled.ContainsKey(p)))
                {
                    // 前置的前置如果只是被停用了，启用即可
                    if (disabledByProject.TryGetValue(pid, out var d))
                    {
                        Enable(d, plan.Title);
                        present.Add(pid);
                        continue;
                    }
                    if (await ScheduleAsync(pid, null, plan.Title, version, loader, present, scheduled, ct))
                        pending.Enqueue((scheduled[pid], depth + 1));
                    else
                        report.Unresolved.Add(new MissingDependency(pid, plan.Title));
                }
            }
            report.ToDownload.AddRange(scheduled.Values);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("联网查找前置失败：" + ex.Message);
            report.OnlineError = ex.GetBaseException().Message;
            foreach (var (id, by) in missingIds) report.Unresolved.Add(new MissingDependency(id, by.Name));
        }
        return Done(report, version);
    }

    private static DependencyReport Done(DependencyReport report, InstalledVersion version)
    {
        Log.Info($"前置检查 {version.Id}：需启用 {report.ToEnable.Count}，需下载 {report.ToDownload.Count}，找不到 {report.Unresolved.Count}");
        return report;
    }

    /// <summary>
    /// 为一个资源找到适合当前版本的最新文件并加入下载计划
    /// </summary>
    private static async Task<bool> ScheduleAsync(string projectId, string? title, string requiredBy, InstalledVersion version, string? loader,
        HashSet<string> present, Dictionary<string, DownloadPlan> scheduled, CancellationToken ct)
    {
        if (scheduled.ContainsKey(projectId) || present.Contains(projectId)) return true;
        var files = await ModrinthClient.GetFilesAsync(projectId, version.GameVersion, loader, ct);
        // 优先正式版
        var file = files.FirstOrDefault(f => f.VersionType == "release") ?? files.FirstOrDefault();
        if (file is null) return false;
        title ??= (await ModrinthClient.FindProjectAsync(projectId, ct))?.Title ?? projectId;
        scheduled[projectId] = new DownloadPlan(title, projectId, file, requiredBy);
        return true;
    }

    /// <summary>
    /// 按检查结果启用和下载前置；返回成功处理的数量
    /// </summary>
    public static async Task<int> FixAsync(DependencyReport report, InstalledVersion version, IProgress<StageProgress> progress, CancellationToken ct)
    {
        var modsDir = Path.Combine(version.RunDir(Settings.Current.GameDir), "mods");
        Directory.CreateDirectory(modsDir);
        var done = 0;
        var total = report.ToEnable.Count + report.ToDownload.Count;
        foreach (var (mod, _) in report.ToEnable)
        {
            progress.Report(new StageProgress($"启用 {mod.Name}", (double)done / total));
            ModScanner.SetEnabled(mod, true);
            done++;
        }
        foreach (var plan in report.ToDownload)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new StageProgress($"下载 {plan.Title}", (double)done / total, plan.File.FileName));
            await Downloader.RunAsync([new DlItem(Path.Combine(modsDir, plan.File.FileName), [plan.File.Url], plan.File.Sha1, plan.File.Size)], null, ct);
            done++;
        }
        progress.Report(new StageProgress("前置已经准备好", 1));
        Log.Info($"已处理前置：启用 {report.ToEnable.Count} 个，下载 {report.ToDownload.Count} 个");
        return done;
    }

    /// <summary>
    /// 给一批资源（例如刚安装的模组的前置）递归找出需要下载的文件，供资源中心安装时使用
    /// </summary>
    public static async Task<List<DownloadPlan>> ResolveProjectsAsync(IEnumerable<string> projectIds, string requiredBy,
        InstalledVersion version, HashSet<string> present, CancellationToken ct)
    {
        var scheduled = new Dictionary<string, DownloadPlan>();
        var pending = new Queue<(string Pid, string By, int Depth)>(projectIds.Select(p => (p, requiredBy, 1)));
        while (pending.Count > 0)
        {
            var (pid, by, depth) = pending.Dequeue();
            if (depth > MaxDepth || present.Contains(pid) || scheduled.ContainsKey(pid)) continue;
            try
            {
                if (!await ScheduleAsync(pid, null, by, version, version.ModLoaderName, present, scheduled, ct)) continue;
                var plan = scheduled[pid];
                foreach (var next in plan.File.RequiredProjects) pending.Enqueue((next, plan.Title, depth + 1));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"查找前置 {pid} 失败：{ex.Message}");
            }
        }
        return scheduled.Values.ToList();
    }
}