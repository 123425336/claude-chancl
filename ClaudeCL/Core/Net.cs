using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>
/// 共享的网络客户端与下载源镜像规则
/// </summary>
public static class Net
{
    public const string UserAgent = "ClaudeCL/1.0";

    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // 官方地址前缀 → 国内镜像地址前缀
    private static readonly (string Official, string Mirror)[] MirrorRules =
    [
        ("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets"),
        ("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta"),
        ("https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.neoforged.net/releases", "https://bmclapi2.bangbang93.com/maven"),
    ];

    /// <summary>
    /// 根据下载源设置，给出一个官方地址对应的候选地址列表（按尝试顺序）
    /// </summary>
    public static IReadOnlyList<string> Candidates(string officialUrl)
    {
        string? mirror = null;
        foreach (var (o, m) in MirrorRules)
        {
            if (officialUrl.StartsWith(o, StringComparison.OrdinalIgnoreCase))
            {
                mirror = m + officialUrl[o.Length..];
                break;
            }
        }
        if (mirror is null) return [officialUrl];
        return Settings.Current.Source switch
        {
            DownloadSource.Official => [officialUrl, mirror],
            DownloadSource.Mirror => [mirror, officialUrl],
            // 自动：镜像通常在国内更快；如果镜像连续失败，就改为官方优先
            _ => MirrorUnhealthy ? [officialUrl, mirror] : [mirror, officialUrl],
        };
    }

    private static int _mirrorFailures;
    private static int _mirrorSuccesses;

    /// <summary>镜像在本次运行中失败次数不少于成功次数（至少失败 5 次）</summary>
    public static bool MirrorUnhealthy => _mirrorFailures >= 5 && _mirrorFailures >= _mirrorSuccesses;

    /// <summary>
    /// 记录一次请求结果，用于自动模式判断镜像是否可用
    /// </summary>
    public static void Report(string url, bool ok)
    {
        if (!url.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)) return;
        var wasHealthy = !MirrorUnhealthy;
        if (ok) Interlocked.Increment(ref _mirrorSuccesses);
        else Interlocked.Increment(ref _mirrorFailures);
        if (wasHealthy && MirrorUnhealthy) Log.Warn("镜像源不太稳定，自动模式改为官方源优先");
    }

    /// <summary>
    /// 依次尝试候选地址获取文本
    /// </summary>
    public static async Task<string> GetStringAsync(string officialUrl, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in Candidates(officialUrl))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(25));
                    var text = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
                    Report(url, true);
                    return text;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Report(url, false);
                    last = ex;
                    Log.Warn($"请求失败（{url}）：{ex.Message}");
                }
            }
        }
        throw new HttpRequestException($"无法获取 {officialUrl}", last);
    }

    public static async Task<JsonNode> GetJsonAsync(string officialUrl, CancellationToken ct = default)
    {
        var text = await GetStringAsync(officialUrl, ct).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("返回内容为空");
    }

    /// <summary>
    /// 只请求一个地址（不走镜像），用于第三方 API
    /// </summary>
    public static async Task<JsonNode> GetJsonDirectAsync(string url, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var text = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("返回内容为空");
    }

    public static string Join(IEnumerable<string> items) => string.Join(",", items.Select(i => $"\"{i}\""));
}
