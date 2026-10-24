using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeCL.Core;

public enum LoaderKind { Vanilla, Fabric, Quilt, Forge, NeoForge, OptiFine, Unknown }

/// <summary>
/// 一个已安装的游戏版本
/// </summary>
public sealed class InstalledVersion
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public JsonObject? Json { get; init; }
    public string? Error { get; init; }
    public LoaderKind Loader { get; init; }
    public string LoaderVersion { get; init; } = "";
    /// <summary>除了加载器之外，还装了 OptiFine</summary>
    public bool HasOptiFine { get; init; }
    public string GameVersion { get; init; } = "";
    public string Type { get; init; } = "release";
    public DateTime ModifiedAt { get; init; }

    public bool IsValid => Json is not null && Error is null;

    /// <summary>该版本实际运行时使用的游戏目录（受“版本隔离”设置影响）</summary>
    public string RunDir(string gameDir) => Settings.Current.IsolateVersions ? Dir : gameDir;

    public string LoaderText => Loader switch
    {
        LoaderKind.Vanilla => "原版",
        LoaderKind.Fabric => $"Fabric {LoaderVersion}",
        LoaderKind.Quilt => $"Quilt {LoaderVersion}",
        LoaderKind.Forge => $"Forge {LoaderVersion}",
        LoaderKind.NeoForge => $"NeoForge {LoaderVersion}",
        LoaderKind.OptiFine => "OptiFine",
        _ => "未知",
    } + (HasOptiFine && Loader != LoaderKind.OptiFine ? " · OptiFine" : "");

    public string Summary => IsValid ? $"{GameVersion} · {LoaderText}" : $"已损坏：{Error}";

    /// <summary>可安装 Mod 的加载器名（用于资源搜索过滤）</summary>
    public string? ModLoaderName => Loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };
}

/// <summary>
/// 已安装版本的扫描与版本 JSON 的继承合并
/// </summary>
public static class VersionStore
{
    public static string VersionsDir(string gameDir) => Path.Combine(gameDir, "versions");

    /// <summary>上次扫描时没有识别为游戏版本的文件夹及原因</summary>
    public static IReadOnlyList<(string Name, string Reason)> LastSkipped { get; private set; } = [];

    public static List<InstalledVersion> Scan(string gameDir)
    {
        var result = new List<InstalledVersion>();
        var skipped = new List<(string Name, string Reason)>();
        var dir = VersionsDir(gameDir);
        if (!Directory.Exists(dir))
        {
            LastSkipped = skipped;
            return result;
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            try
            {
                if (FindJson(sub, id) is null)
                {
                    var files = Directory.EnumerateFiles(sub).Select(Path.GetFileName).Take(6).ToList();
                    skipped.Add((id, files.Count == 0
                        ? "文件夹是空的"
                        : $"没有找到版本信息文件（应为 {id}.json），文件夹里有：{string.Join("、", files)}"));
                    continue;
                }
                result.Add(Load(gameDir, id));
            }
            catch (Exception ex)
            {
                // 单个文件夹出问题不影响其他版本
                skipped.Add((id, $"读取失败：{ex.Message}"));
            }
        }
        LastSkipped = skipped;
        Log.Info($"扫描版本：{dir}，识别 {result.Count} 个" +
                 (skipped.Count == 0 ? "" : $"，跳过 {skipped.Count} 个：\n  " + string.Join("\n  ", skipped.Select(s => $"{s.Name} → {s.Reason}"))));
        // 和常见启动器一样按游戏版本从新到旧排列，同一游戏版本按名称排
        return result
            .OrderByDescending(v => VersionSortKey(v.GameVersion))
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 版本号排序键：正式版按数字比较，快照等无法比较的排在最后
    /// </summary>
    private static string VersionSortKey(string gameVersion) =>
        System.Text.RegularExpressions.Regex.IsMatch(gameVersion, @"^\d+(\.\d+)+") ? ForgeInstaller.SortKey(gameVersion.Split('-', ' ')[0]) : "";

    /// <summary>
    /// 找到版本文件夹里的版本信息文件：优先“文件夹名.json”，否则在文件夹里找一个内容像版本信息的 json
    /// </summary>
    public static string? FindJson(string dir, string id)
    {
        var exact = Path.Combine(dir, id + ".json");
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (new FileInfo(file).Length > 4 * 1024 * 1024) continue;
                if (ReadJson(file) is { } obj && (obj["mainClass"] is not null || obj["inheritsFrom"] is not null) &&
                    (obj["libraries"] is not null || obj["inheritsFrom"] is not null))
                {
                    Log.Info($"版本 {id} 的信息文件名与文件夹不一致，使用 {Path.GetFileName(file)}");
                    return file;
                }
            }
            catch { /* 不是版本信息文件 */ }
        }
        return null;
    }

    public static InstalledVersion Load(string gameDir, string id)
    {
        var dir = Path.Combine(VersionsDir(gameDir), id);
        var modified = Directory.Exists(dir) ? Directory.GetLastWriteTime(dir) : DateTime.MinValue;
        try
        {
            var json = LoadMerged(gameDir, id, 0);
            var (loader, loaderVer) = DetectLoader(json);
            var hasOptiFine = json["libraries"]?.AsArray().Any(l => ((string?)l?["name"])?.StartsWith("optifine:OptiFine:") == true) == true;
            var gameVer = (string?)json["clientVersion"]
                          ?? (string?)json["_inheritsFrom"]
                          ?? GuessGameVersion(json, id);
            return new InstalledVersion
            {
                Id = id, Dir = dir, Json = json, Loader = loader, LoaderVersion = loaderVer, HasOptiFine = hasOptiFine,
                GameVersion = gameVer, Type = (string?)json["type"] ?? "release", ModifiedAt = modified,
            };
        }
        catch (Exception ex)
        {
            return new InstalledVersion { Id = id, Dir = dir, Error = ex.Message, ModifiedAt = modified, Loader = LoaderKind.Unknown };
        }
    }

    // 宽松解析：允许注释和多余的逗号，部分手工修改过的版本文件会有这些
    private static readonly System.Text.Json.JsonDocumentOptions LooseJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    };

    public static JsonObject ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), null, LooseJson) as JsonObject
        ?? throw new InvalidDataException("版本 JSON 格式错误");

    /// <summary>
    /// 读取版本 JSON，并递归合并 inheritsFrom 指向的父版本
    /// </summary>
    private static JsonObject LoadMerged(string gameDir, string id, int depth)
    {
        if (depth > 8) throw new InvalidDataException("版本继承层级过深");
        var path = FindJson(Path.Combine(VersionsDir(gameDir), id), id)
                   ?? throw new FileNotFoundException(depth == 0 ? $"缺少版本 {id} 的 JSON 文件" : $"依赖的原版 {id} 不存在（需要先安装或补全 {id}）");
        var child = ReadJson(path);
        // 记下实际的文件名：和文件夹名不一致时，游戏本体 jar 往往也沿用这个名字
        var jsonName = Path.GetFileNameWithoutExtension(path);
        if (depth == 0 && !jsonName.Equals(id, StringComparison.OrdinalIgnoreCase)) child["_jsonName"] = jsonName;
        var parentId = (string?)child["inheritsFrom"];
        if (string.IsNullOrEmpty(parentId)) return child;

        var parent = LoadMerged(gameDir, parentId, depth + 1);
        var merged = (JsonObject)parent.DeepClone();
        foreach (var (key, value) in child)
        {
            switch (key)
            {
                case "inheritsFrom":
                    break;
                case "libraries":
                    // 子版本的库排在前面，方便去重时优先保留
                    var libs = new JsonArray();
                    foreach (var l in value?.AsArray() ?? []) libs.Add(l?.DeepClone());
                    foreach (var l in parent["libraries"]?.AsArray() ?? []) libs.Add(l?.DeepClone());
                    merged["libraries"] = libs;
                    break;
                case "arguments":
                    var args = merged["arguments"] as JsonObject ?? new JsonObject();
                    foreach (var kind in new[] { "game", "jvm" })
                    {
                        var arr = args[kind] as JsonArray ?? new JsonArray();
                        foreach (var a in value?[kind]?.AsArray() ?? []) arr.Add(a?.DeepClone());
                        args[kind] = arr;
                    }
                    merged["arguments"] = args;
                    break;
                default:
                    merged[key] = value?.DeepClone();
                    break;
            }
        }
        // 子版本一般没有自己的 jar，使用父版本的
        if (child["jar"] is null) merged["jar"] = (string?)parent["jar"] ?? parentId;
        merged["_inheritsFrom"] = (string?)parent["_inheritsFrom"] ?? parentId;
        return merged;
    }

    private static (LoaderKind, string) DetectLoader(JsonObject json)
    {
        // 新版 Forge / NeoForge 会把自己的版本号写进游戏参数
        string? Arg(string key)
        {
            var game = json["arguments"]?["game"] as JsonArray;
            if (game is null) return null;
            for (var i = 0; i < game.Count - 1; i++)
                if (game[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == key)
                    return (string?)game[i + 1];
            return null;
        }
        if (Arg("--fml.neoForgeVersion") is { } neo) return (LoaderKind.NeoForge, neo);

        // 去掉版本号里的“游戏版本-”前缀，例如 1.20.1-47.2.0 → 47.2.0
        static string Strip(string ver) => ver.Contains('-') ? ver[(ver.IndexOf('-') + 1)..] : ver;

        foreach (var lib in json["libraries"]?.AsArray() ?? [])
        {
            var name = (string?)lib?["name"] ?? "";
            var parts = name.Split(':');
            if (parts.Length < 3) continue;
            var ver = parts[2];
            if (name.StartsWith("net.fabricmc:fabric-loader:")) return (LoaderKind.Fabric, ver);
            if (name.StartsWith("org.quiltmc:quilt-loader:")) return (LoaderKind.Quilt, ver);
            if (name.StartsWith("net.neoforged:neoforge:")) return (LoaderKind.NeoForge, ver);
            if (name.StartsWith("net.neoforged:forge:")) return (LoaderKind.NeoForge, Strip(ver));
        }
        if (Arg("--fml.forgeVersion") is { } forge)
        {
            // 1.20.1 的 NeoForge 仍沿用 forgeVersion 参数，但库来自 net.neoforged
            var neoLibs = json["libraries"]?.AsArray().Any(l => ((string?)l?["name"])?.StartsWith("net.neoforged") == true) == true;
            return (neoLibs ? LoaderKind.NeoForge : LoaderKind.Forge, forge);
        }
        foreach (var lib in json["libraries"]?.AsArray() ?? [])
        {
            var name = (string?)lib?["name"] ?? "";
            var parts = name.Split(':');
            if (parts.Length < 3) continue;
            var ver = parts[2];
            if (name.StartsWith("net.minecraftforge:forge:") || name.StartsWith("net.minecraftforge:fmlloader:"))
            {
                // 旧版本号形如 1.7.10-10.13.4.1614-1.7.10
                var v = Strip(ver);
                if (v.Contains('-')) v = v[..v.IndexOf('-')];
                return (LoaderKind.Forge, v);
            }
        }
        // 没有其他加载器时，才把 OptiFine 当作主要的加载器
        if (json["libraries"]?.AsArray().FirstOrDefault(l => ((string?)l?["name"])?.StartsWith("optifine:OptiFine:") == true) is { } of)
            return (LoaderKind.OptiFine, ((string)of["name"]!).Split(':')[2]);
        var main = (string?)json["mainClass"] ?? "";
        if (main.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return (LoaderKind.Fabric, "");
        if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return (LoaderKind.Quilt, "");
        if (main.Contains("forge", StringComparison.OrdinalIgnoreCase) || main.Contains("modlauncher", StringComparison.OrdinalIgnoreCase))
            return (LoaderKind.Forge, "");
        return (LoaderKind.Vanilla, "");
    }

    /// <summary>
    /// 推断游戏版本：合并成单个文件的整合版本没有继承信息，需要从参数、库名或版本名里找
    /// </summary>
    private static string GuessGameVersion(JsonObject json, string id)
    {
        // 1. 新版 Forge / NeoForge 的参数里写着游戏版本
        if (json["arguments"]?["game"] is JsonArray game)
            for (var i = 0; i < game.Count - 1; i++)
                if (game[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == "--fml.mcVersion")
                    return (string?)game[i + 1] ?? id;
        // 2. 加载器的库名里带着游戏版本
        foreach (var lib in json["libraries"]?.AsArray() ?? [])
        {
            var parts = ((string?)lib?["name"] ?? "").Split(':');
            if (parts.Length < 3) continue;
            if (parts[0] is "net.fabricmc" or "org.quiltmc" && parts[1] is "intermediary" or "hashed") return parts[2];
            if (parts[0] is "net.minecraftforge" or "net.neoforged" && parts[1] == "forge" && parts[2].Contains('-')) return parts[2][..parts[2].IndexOf('-')];
            if (parts[0] == "optifine" && parts[1] == "OptiFine" && parts[2].Contains('_')) return parts[2][..parts[2].IndexOf('_')];
        }
        // 3. 原版的 jar 名就是游戏版本
        var jar = (string?)json["jar"];
        if (!string.IsNullOrEmpty(jar)) return jar;
        // 4. 版本名开头的版本号，例如 1.20.1-Forge_47.4.16
        var m = System.Text.RegularExpressions.Regex.Match(id, @"^\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : id;
    }

    /// <summary>
    /// 把版本移到回收站
    /// </summary>
    public static void Delete(InstalledVersion v) => Shell.MoveToRecycleBin(v.Dir);
}
