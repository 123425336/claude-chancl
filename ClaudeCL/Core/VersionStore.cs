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
    };

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

    public static List<InstalledVersion> Scan(string gameDir)
    {
        var result = new List<InstalledVersion>();
        var dir = VersionsDir(gameDir);
        if (!Directory.Exists(dir)) return result;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            if (!File.Exists(Path.Combine(sub, id + ".json"))) continue;
            result.Add(Load(gameDir, id));
        }
        return result.OrderByDescending(v => v.ModifiedAt).ToList();
    }

    public static InstalledVersion Load(string gameDir, string id)
    {
        var dir = Path.Combine(VersionsDir(gameDir), id);
        var modified = Directory.Exists(dir) ? Directory.GetLastWriteTime(dir) : DateTime.MinValue;
        try
        {
            var json = LoadMerged(gameDir, id, 0);
            var (loader, loaderVer) = DetectLoader(json);
            var gameVer = (string?)json["clientVersion"]
                          ?? (string?)json["_inheritsFrom"]
                          ?? GuessGameVersion(json, id);
            return new InstalledVersion
            {
                Id = id, Dir = dir, Json = json, Loader = loader, LoaderVersion = loaderVer,
                GameVersion = gameVer, Type = (string?)json["type"] ?? "release", ModifiedAt = modified,
            };
        }
        catch (Exception ex)
        {
            return new InstalledVersion { Id = id, Dir = dir, Error = ex.Message, ModifiedAt = modified, Loader = LoaderKind.Unknown };
        }
    }

    public static JsonObject ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
        ?? throw new InvalidDataException("版本 JSON 格式错误");

    /// <summary>
    /// 读取版本 JSON，并递归合并 inheritsFrom 指向的父版本
    /// </summary>
    private static JsonObject LoadMerged(string gameDir, string id, int depth)
    {
        if (depth > 8) throw new InvalidDataException("版本继承层级过深");
        var path = Path.Combine(VersionsDir(gameDir), id, id + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException($"缺少版本 {id} 的 JSON 文件");
        var child = ReadJson(path);
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
            if (name.StartsWith("optifine:OptiFine:")) return (LoaderKind.OptiFine, ver);
        }
        var main = (string?)json["mainClass"] ?? "";
        if (main.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return (LoaderKind.Fabric, "");
        if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return (LoaderKind.Quilt, "");
        if (main.Contains("forge", StringComparison.OrdinalIgnoreCase) || main.Contains("modlauncher", StringComparison.OrdinalIgnoreCase))
            return (LoaderKind.Forge, "");
        return (LoaderKind.Vanilla, "");
    }

    private static string GuessGameVersion(JsonObject json, string id)
    {
        // 原版的 jar 名就是游戏版本
        var jar = (string?)json["jar"];
        if (!string.IsNullOrEmpty(jar)) return jar;
        return id;
    }

    /// <summary>
    /// 把版本移到回收站
    /// </summary>
    public static void Delete(InstalledVersion v) => Shell.MoveToRecycleBin(v.Dir);
}
