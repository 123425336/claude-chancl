using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeCL.Core;

/// <summary>
/// 版本 JSON 中 rules 字段的判断，以及 Maven 坐标与路径的换算
/// </summary>
public static class McRules
{
    /// <summary>
    /// 判断一组规则在当前系统上是否允许
    /// </summary>
    public static bool Allowed(JsonNode? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is not JsonArray arr || arr.Count == 0) return true;
        var allow = false;
        foreach (var rule in arr)
        {
            if (rule is null) continue;
            if (!OsMatches(rule["os"]) || !FeaturesMatch(rule["features"], features)) continue;
            allow = (string?)rule["action"] == "allow";
        }
        return allow;
    }

    private static bool OsMatches(JsonNode? os)
    {
        if (os is null) return true;
        var name = (string?)os["name"];
        if (name is not null && name != "windows") return false;
        var arch = (string?)os["arch"];
        if (arch is not null)
        {
            // 只有 32 位进程才匹配 x86 规则
            var is32 = RuntimeInformation.OSArchitecture == Architecture.X86;
            if (arch == "x86" && !is32) return false;
        }
        var version = (string?)os["version"];
        if (version is not null)
        {
            try
            {
                var v = Environment.OSVersion.Version;
                if (!Regex.IsMatch($"{v.Major}.{v.Minor}", version)) return false;
            }
            catch (ArgumentException) { /* 非法正则视为匹配 */ }
        }
        return true;
    }

    private static bool FeaturesMatch(JsonNode? required, IReadOnlyDictionary<string, bool>? features)
    {
        if (required is not JsonObject obj) return true;
        foreach (var (key, value) in obj)
        {
            var want = value?.GetValue<bool>() ?? false;
            var have = features is not null && features.TryGetValue(key, out var h) && h;
            if (want != have) return false;
        }
        return true;
    }

    /// <summary>
    /// Maven 坐标转相对路径：group:artifact:version[:classifier][@ext]
    /// </summary>
    public static string MavenToPath(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }
        var parts = name.Split(':');
        if (parts.Length < 3) throw new FormatException($"无法识别的库名称：{name}");
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }

    /// <summary>
    /// 去掉版本号后的库标识，用于去重（group:artifact[:classifier]）
    /// </summary>
    public static string LibraryKey(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var parts = name.Split(':');
        return parts.Length > 3 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : name;
    }
}
