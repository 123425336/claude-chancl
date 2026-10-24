using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>GitHub 上的一个发布版本</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string Name, string Notes, string HtmlUrl,
    string AssetName, string AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// 启动器自动更新：检查 GitHub 最新发布 → 下载对应的压缩包并校验 → 替换程序文件 → 重启。
/// 正在运行的程序文件不能覆盖，但可以改名，所以先把旧文件改名为 .old，再放入新文件；
/// .old 文件在下次启动时清理。
/// </summary>
public static class SelfUpdater
{
    private const string Repo = "123425336/claude-chancl";
    private const string OldSuffix = ".old";

    public static string AppDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static string UpdateDir => Path.Combine(AppPaths.DataDir, "update");

    /// <summary>记录这次更新改名的旧文件，只清理清单里的，避免误删游戏目录里其他 .old 文件</summary>
    private static string CleanupList => Path.Combine(UpdateDir, "cleanup.txt");

    /// <summary>当前版本（调试时可以用 --pretend-version 假装成旧版本）</summary>
    public static Version Current { get; set; } = Version.Parse(GameLauncher.LauncherVersion);

    /// <summary>免安装版自带运行时，下载对应的压缩包</summary>
    public static bool IsSelfContained =>
        File.Exists(Path.Combine(AppDir, "hostfxr.dll")) || File.Exists(Path.Combine(AppDir, "coreclr.dll"));

    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    /// <summary>
    /// 查询最新发布；没有比当前更新的版本时返回 null
    /// </summary>
#if DEBUG
    /// <summary>调试：让前几次检查直接失败，用来测试自动重试</summary>
    public static int DebugFailures { get; set; }
#endif

    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
#if DEBUG
        if (DebugFailures > 0)
        {
            DebugFailures--;
            throw new OperationCanceledException("调试模拟：检查更新被拦截");
        }
#endif
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Net.Client.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token))!;

        var tag = (string?)json["tag_name"] ?? "";
        var m = Regex.Match(tag, @"\d+(\.\d+){1,3}");
        if (!m.Success || !Version.TryParse(m.Value, out var latest) || Normalize(latest) <= Normalize(Current)) return null;

        // 免安装版下载完整包，轻量版下载 lite 包
        var assets = json["assets"]?.AsArray() ?? [];
        var asset = assets.FirstOrDefault(a =>
        {
            var name = (string?)a?["name"] ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)) return false;
            var lite = name.Contains("lite", StringComparison.OrdinalIgnoreCase);
            return IsSelfContained ? !lite : lite;
        }) ?? throw new InvalidOperationException($"{tag} 里没有找到适合这个版本的下载包");

        var digest = (string?)asset["digest"];
        return new ReleaseInfo(tag, latest, (string?)json["name"] ?? tag, ((string?)json["body"] ?? "").Trim(),
            (string?)json["html_url"] ?? ReleasesPage, (string)asset["name"]!, (string)asset["browser_download_url"]!,
            (long?)asset["size"] ?? -1,
            digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// 程序所在文件夹能不能写入（装在 Program Files 之类的位置时不行）
    /// </summary>
    public static bool CanWriteAppDir()
    {
        try
        {
            var probe = Path.Combine(AppDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下载并校验更新包，返回本地路径
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r, IProgress<StageProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdateDir);
        var target = Path.Combine(UpdateDir, r.AssetName);
        var temp = target + ".part";
        // 30 秒内没有收到任何数据就放弃，避免网络卡住时一直等
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TimeSpan.FromSeconds(30));
        using (var resp = await Net.Client.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
            await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            var last = Stopwatch.StartNew();
            stall.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                try
                {
                    read = await src.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("连接 GitHub 下载超时（30 秒没有收到数据），请检查网络后重试");
                }
                if (read <= 0) break;
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (last.ElapsedMilliseconds > 200 && total > 0)
                {
                    last.Restart();
                    progress.Report(new("下载新版本", 0.9 * done / total, $"{Installer.FormatSize(done)} / {Installer.FormatSize(total)}"));
                }
            }
        }

        // 校验大小与哈希，防止下载不完整或被篡改
        progress.Report(new("校验文件", 0.92));
        var size = new FileInfo(temp).Length;
        if (r.AssetSize > 0 && size != r.AssetSize)
        {
            File.Delete(temp);
            throw new IOException($"下载的文件大小不对（{size} / {r.AssetSize}），请重试");
        }
        if (r.Sha256 is not null)
        {
            await using var fs = File.OpenRead(temp);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(temp);
                throw new IOException("下载的文件校验不通过，请重试");
            }
        }
        File.Move(temp, target, true);
        return target;
    }

    /// <summary>
    /// 解压更新包并替换程序文件；任何一步失败都会把已替换的文件还原
    /// </summary>
    public static void Apply(string zipPath, IProgress<StageProgress>? progress = null)
    {
        var staging = Path.Combine(UpdateDir, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        if (!File.Exists(Path.Combine(staging, "ClaudeCL.exe"))) throw new InvalidDataException("更新包里没有 ClaudeCL.exe");

        var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
        var replaced = new List<string>();
        var added = new List<string>();
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                var rel = Path.GetRelativePath(staging, files[i]);
                var target = Path.Combine(AppDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    // 运行中的文件不能覆盖，但可以改名
                    File.Move(target, target + OldSuffix, true);
                    replaced.Add(target);
                }
                else added.Add(target);
                File.Copy(files[i], target, false);
                if (i % 20 == 0) progress?.Report(new("替换程序文件", 0.94 + 0.05 * i / files.Length, $"{i + 1} / {files.Length}"));
            }
        }
        catch
        {
            // 还原：删掉新放入的文件，把旧文件改回原名
            foreach (var f in added) TryDelete(f);
            foreach (var f in replaced)
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                    File.Move(f + OldSuffix, f);
                }
                catch (Exception ex) { Log.Warn($"还原 {f} 失败：{ex.Message}"); }
            }
            throw;
        }
        File.WriteAllLines(CleanupList, replaced.Select(f => f + OldSuffix), new System.Text.UTF8Encoding(false));
        Log.Info($"更新文件已替换：{files.Length} 个文件");
        TryDeleteDir(staging);
        TryDelete(zipPath);
    }

    /// <summary>
    /// 启动新版本并退出当前程序
    /// </summary>
    public static void Restart(string tag)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(AppDir, "ClaudeCL.exe"), $"--updated {tag}") { UseShellExecute = false, WorkingDirectory = AppDir });
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 启动时清理上次更新留下的旧文件（旧进程可能还没完全退出，删不掉的下次再删）
    /// </summary>
    public static void CleanupOldFiles()
    {
        try
        {
            if (!File.Exists(CleanupList)) return;
            var left = new List<string>();
            foreach (var f in File.ReadAllLines(CleanupList))
            {
                // 只删程序文件夹里、确实是更新时改名的文件
                if (!f.EndsWith(OldSuffix) || !f.StartsWith(AppDir, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(f);
                if (File.Exists(f)) left.Add(f);
            }
            if (left.Count == 0) File.Delete(CleanupList);
            else File.WriteAllLines(CleanupList, left, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warn("清理旧版本文件失败：" + ex.Message); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 被占用时下次再删 */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* 忽略 */ }
    }
}
