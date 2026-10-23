using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Link.McPing;
using PCL.Core.Link.McPing.Model;

namespace ClaudeCL.Core;

/// <summary>
/// 查询 Minecraft 服务器的在线状态
/// </summary>
public static class ServerStatus
{
    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        var port = 25565;
        var idx = address.LastIndexOf(':');
        // 冒号后面是数字才当作端口（避免误伤 IPv6）
        if (idx > 0 && address.IndexOf(':') == idx && int.TryParse(address[(idx + 1)..], out var p))
        {
            port = p;
            address = address[..idx];
        }
        return (address, port);
    }

    public static async Task<McPingResult?> PingAsync(string address, CancellationToken ct)
    {
        var (host, port) = ParseAddress(address);
        if (string.IsNullOrEmpty(host)) throw new ArgumentException("请输入服务器地址");
        // 构造时会同步解析域名，放到后台线程
        using var service = await Task.Run(() => McPingServiceFactory.CreateService(host, port, 8000), ct);
        var result = await service.PingAsync(ct);
        if (result is not null) return result;
        // 新协议失败时尝试旧协议
        using var legacy = await Task.Run(() => McPingServiceFactory.CreateLegacyService(host, port, 8000), ct);
        return await legacy.PingAsync(ct);
    }

    /// <summary>
    /// 去掉 § 格式代码，得到纯文本
    /// </summary>
    public static string StripFormatting(string text) => Regex.Replace(text ?? "", "§.", "");

    /// <summary>
    /// 把服务器图标的 data URI 转成字节
    /// </summary>
    public static byte[]? DecodeFavicon(string? favicon)
    {
        if (string.IsNullOrEmpty(favicon)) return null;
        var comma = favicon.IndexOf(',');
        try { return Convert.FromBase64String(comma >= 0 ? favicon[(comma + 1)..] : favicon); }
        catch (FormatException) { return null; }
    }
}
