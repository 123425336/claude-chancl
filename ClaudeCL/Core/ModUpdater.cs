using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>
/// 模组更新：计算文件哈希、批量检查新版、下载新版并替换旧文件
/// </summary>
public static class ModUpdater
{
    // 哈希缓存：文件没变（大小与修改时间相同）就不重复计算
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Time, string Sha1)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    public static string Sha1(string path)
    {
        var fi = new FileInfo(path);
        if (HashCache.TryGetValue(path, out var c) && c.Size == fi.Length && c.Time == fi.LastWriteTimeUtc) return c.Sha1;
        using var fs = File.OpenRead(path);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(fs));
        HashCache[path] = (fi.Length, fi.LastWriteTimeUtc, sha1);
        return sha1;
    }

    /// <summary>
    /// 检查一批模组的新版本，结果写回每个模组的 ProjectId / Update；返回有新版的数量
    /// </summary>
    public static async Task<int> CheckAsync(IReadOnlyList<ModEntry> mods, InstalledVersion version, CancellationToken ct)
    {
        var loader = version.ModLoaderName ?? throw new InvalidOperationException("这个版本没有模组加载器");
        // 计算哈希比较耗时，放到后台线程
        var byHash = await Task.Run(() =>
        {
            var map = new Dictionary<string, List<ModEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var h = Sha1(m.Path);
                    if (!map.TryGetValue(h, out var list)) map[h] = list = new List<ModEntry>();
                    list.Add(m);
                }
                catch (IOException) { /* 文件被占用时跳过 */ }
            }
            return map;
        }, ct);

        var result = await ModrinthClient.CheckUpdatesAsync(byHash.Keys, version.GameVersion, loader, ct);
        var count = 0;
        foreach (var (hash, list) in byHash)
        {
            result.ProjectIds.TryGetValue(hash, out var pid);
            result.Updates.TryGetValue(hash, out var update);
            foreach (var m in list)
            {
                m.ProjectId = pid;
                m.Update = update;
                if (update is not null) count++;
            }
        }
        Log.Info($"检查模组更新：{mods.Count} 个模组，识别 {result.ProjectIds.Count} 个，有新版 {count} 个");
        return count;
    }

    /// <summary>
    /// 把一个模组更新到新版：下载新文件、保持启用/停用状态、旧文件移到回收站，并补装新增的必需前置。
    /// 返回补装的前置文件名。
    /// </summary>
    public static async Task<List<string>> ApplyAsync(ModEntry mod, InstalledVersion version, IReadOnlyCollection<ModEntry> folderMods, CancellationToken ct)
    {
        var info = mod.Update ?? throw new InvalidOperationException("这个模组没有可用的更新");
        var dir = Path.GetDirectoryName(mod.Path)!;
        var wasDisabled = !mod.Enabled;
        var finalPath = Path.Combine(dir, info.Latest.FileName + (wasDisabled ? ".disabled" : ""));

        // 先下载到临时文件，成功后再替换，失败时旧文件不受影响
        var temp = Path.Combine(dir, info.Latest.FileName + ".cldl-new");
        await Downloader.RunAsync([new DlItem(temp, [info.Latest.Url], info.Latest.Sha1, info.Latest.Size)], null, ct);
        if (!string.Equals(finalPath, mod.Path, StringComparison.OrdinalIgnoreCase)) Shell.MoveToRecycleBin(mod.Path);
        File.Move(temp, finalPath, true);
        Log.Info($"模组已更新：{Path.GetFileName(mod.Path)} → {Path.GetFileName(finalPath)}");

        mod.Path = finalPath;
        mod.Size = new FileInfo(finalPath).Length;
        mod.Version = info.Latest.VersionName;
        mod.Update = null;
        mod.NotifyChanged(nameof(ModEntry.Subtitle));

        // 新版需要、但文件夹里还没有的前置
        var deps = new List<string>();
        var present = folderMods.Select(m => m.ProjectId).Where(p => p is not null).ToHashSet();
        var loader = version.ModLoaderName;
        foreach (var dep in info.Latest.RequiredProjects.Where(d => !present.Contains(d)))
        {
            try
            {
                var files = await ModrinthClient.GetFilesAsync(dep, version.GameVersion, loader, ct);
                if (files.FirstOrDefault() is not { } f) continue;
                await Downloader.RunAsync([new DlItem(Path.Combine(dir, f.FileName), [f.Url], f.Sha1, f.Size)], null, ct);
                deps.Add(f.FileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"补装前置 {dep} 失败：{ex.Message}");
            }
        }
        return deps;
    }
}
