using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>一个待下载文件</summary>
public sealed record DlItem(string Path, IReadOnlyList<string> Urls, string? Sha1 = null, long Size = -1);

/// <summary>下载进度快照</summary>
public readonly record struct DlProgress(int Done, int Total, long Bytes);

/// <summary>
/// 多线程下载器：跳过已完整的文件、多地址轮换重试、下载完成后校验 SHA1
/// </summary>
public static class Downloader
{
    private const int MaxAttempts = 4;

    /// <summary>
    /// 判断本地文件是否已经完整（有大小信息时按大小快速判断）
    /// </summary>
    public static bool IsComplete(DlItem item)
    {
        var fi = new FileInfo(item.Path);
        if (!fi.Exists || fi.Length == 0) return false;
        if (item.Size > 0) return fi.Length == item.Size;
        return true;
    }

    public static async Task RunAsync(IEnumerable<DlItem> items, IProgress<DlProgress>? progress, CancellationToken ct)
    {
        // 同一路径只下载一次
        var list = items
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(i => !IsComplete(i))
            .ToList();
        var total = list.Count;
        if (total == 0)
        {
            progress?.Report(new DlProgress(0, 0, 0));
            return;
        }

        var done = 0;
        long bytes = 0;
        var failures = new ConcurrentBag<string>();
        var threads = Math.Clamp(Settings.Current.DownloadThreads, 1, 128);
        using var gate = new SemaphoreSlim(threads);

        var tasks = list.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ok = await DownloadOneAsync(item, n => Interlocked.Add(ref bytes, n), ct).ConfigureAwait(false);
                if (!ok) failures.Add(System.IO.Path.GetFileName(item.Path));
            }
            finally
            {
                gate.Release();
                var d = Interlocked.Increment(ref done);
                progress?.Report(new DlProgress(d, total, Interlocked.Read(ref bytes)));
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (!failures.IsEmpty)
        {
            var names = string.Join("、", failures.Take(5));
            throw new IOException($"有 {failures.Count} 个文件下载失败：{names}{(failures.Count > 5 ? " 等" : "")}");
        }
    }

    private static async Task<bool> DownloadOneAsync(DlItem item, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Path)!);
        var temp = item.Path + ".cldl";
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // 镜像不可用时把官方地址排到前面；每次重试换一个候选地址
            var urls = Net.MirrorUnhealthy && Settings.Current.Source == DownloadSource.Auto
                ? item.Urls.OrderBy(u => u.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)).ToList()
                : item.Urls;
            var url = urls[attempt % urls.Count];
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // 20 秒内拿不到响应头就换下一个地址
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using (var resp = await Net.Client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                    var buffer = new byte[81920];
                    int read;
                    // 传输过程中只要 30 秒内有数据到达就继续，卡住则放弃
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    while ((read = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        onBytes(read);
                        cts.CancelAfter(TimeSpan.FromSeconds(30));
                    }
                }

                // 校验文件完整性
                if (item.Sha1 is { Length: > 0 })
                {
                    await using var fs = File.OpenRead(temp);
                    var hash = Convert.ToHexStringLower(await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false));
                    if (!hash.Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"SHA1 校验不通过：期望 {item.Sha1}，实际 {hash}");
                }
                File.Move(temp, item.Path, true);
                Net.Report(url, true);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"下载失败（第 {attempt + 1} 次）{url}：{ex.Message}");
                Net.Report(url, false);
                TryDelete(temp);
                await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
