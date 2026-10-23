using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ClaudeCL.Core;

/// <summary>
/// 敏感信息遮盖：所有日志（启动器日志、游戏输出、复制出去的内容）在保存或显示前都经过这里，
/// 避免登录令牌被不小心泄漏
/// </summary>
public static class Redactor
{
    private const string Mask = "********";

    // 本次运行中用过的令牌，原样出现时直接替换
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();

    private static readonly Regex[] Patterns =
    [
        // 形如 eyJ... 的 JWT 令牌（游戏令牌、微软令牌都是这种格式）
        new(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled),
        // 命令行参数 --accessToken xxx
        new(@"(?<=--accessToken[\s=]+)""?[^\s""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 旧版本打印的会话：token:令牌:UUID
        new(@"(?<=token:)[^:\s]{8,}(?=:)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 链接或表单里的令牌参数
        new(@"(?<=[?&](?:access_token|refresh_token|code|token)=)[^&\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 请求头 Authorization: Bearer xxx
        new(@"(?<=Bearer\s+)[A-Za-z0-9_\-\.]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    /// <summary>
    /// 登记一个需要隐藏的值（例如本次启动使用的游戏令牌）
    /// </summary>
    public static void Register(string? secret)
    {
        // 太短的值替换起来容易误伤正常文字
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 16) Secrets.TryAdd(secret, 0);
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var secret in Secrets.Keys)
            if (text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        foreach (var p in Patterns) text = p.Replace(text, Mask);
        return text;
    }
}
