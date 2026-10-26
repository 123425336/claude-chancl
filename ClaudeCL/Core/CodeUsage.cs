using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ClaudeCL.Core;

/// <summary>
/// 静态检查模组代码是否用到了另一个模组：用来发现“声明为可选、实际离不开”的前置
/// </summary>
public static class CodeUsage
{
    /// <summary>至少这么多个类引用了对方，才认为是离不开的依赖</summary>
    public const int Threshold = 3;

    // 专门放联动代码的包：这些类一般只在对方存在时才加载，属于正常的可选联动，不计入
    private static readonly HashSet<string> CompatSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "compat", "compatibility", "integration", "integrations", "plugin", "plugins", "addon", "addons", "mixin", "mixins",
    };

    private const long MaxClassSize = 4 * 1024 * 1024;

    /// <summary>
    /// 一个模组的主要包名前缀（以 / 结尾）：优先取三级包，覆盖不了大部分类时退到两级
    /// </summary>
    public static List<string> MainPackages(string jar)
    {
        var classes = ClassNames(jar).Where(n => !n.StartsWith("META-INF/", StringComparison.Ordinal)).ToList();
        if (classes.Count == 0) return [];
        foreach (var depth in new[] { 3, 2 })
        {
            var groups = classes
                .Select(n => n.Split('/'))
                .Where(p => p.Length > depth)
                .GroupBy(p => string.Join('/', p.Take(depth)) + "/")
                .Where(g => g.Count() * 10 >= classes.Count)
                .ToList();
            if (groups.Sum(g => g.Count()) * 10 >= classes.Count * 6) return groups.Select(g => g.Key).ToList();
        }
        return [];
    }

    /// <summary>
    /// 模组自己的类路径里有没有这一段包名（有的话按 ID 猜包名会和自己的代码混淆）
    /// </summary>
    public static bool HasOwnSegment(string jar, string segment) =>
        ClassNames(jar).Any(n => n.Split('/')[..^1].Contains(segment, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 统计模组里有多少个类引用了指定的包（needles 为包路径片段，例如 “作者/模组/”）。
    /// 联动包里的类、以及本身就属于这些包的类（内嵌了对方代码）不计入
    /// </summary>
    public static int CountReferences(string jar, IReadOnlyCollection<string> needles, string depId)
    {
        if (needles.Count == 0) return 0;
        var patterns = needles.Select(n => Encoding.ASCII.GetBytes(n)).ToList();
        var count = 0;
        using var zip = ZipFile.OpenRead(jar);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName;
            if (!name.EndsWith(".class", StringComparison.OrdinalIgnoreCase) || e.Length > MaxClassSize) continue;
            if (needles.Any(n => name.StartsWith(n.TrimStart('/'), StringComparison.Ordinal))) continue;
            var segments = name.Split('/')[..^1];
            if (segments.Any(s => CompatSegments.Contains(s) || s.Equals(depId, StringComparison.OrdinalIgnoreCase))) continue;
            byte[] data;
            using (var s = e.Open())
            using (var ms = new MemoryStream((int)Math.Max(e.Length, 0)))
            {
                s.CopyTo(ms);
                data = ms.ToArray();
            }
            if (patterns.Any(p => data.AsSpan().IndexOf(p) >= 0)) count++;
        }
        return count;
    }

    private static List<string> ClassNames(string jar)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            return zip.Entries.Select(e => e.FullName).Where(n => n.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch
        {
            return [];
        }
    }
}
