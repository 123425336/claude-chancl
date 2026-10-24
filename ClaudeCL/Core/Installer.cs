using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>官方版本列表中的一项</summary>
public sealed record McVersionEntry(string Id, string Type, string Url, DateTime ReleaseTime, string? Sha1)
{
    public string TypeText => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };
}

/// <summary>安装/启动过程的进度</summary>
public readonly record struct StageProgress(string Stage, double Percent, string Detail = "");

/// <summary>
/// 版本列表、原版与 Fabric 安装，以及游戏文件补全
/// </summary>
public static class Installer
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string AssetsBase = "https://resources.download.minecraft.net";
    private const string LibrariesBase = "https://libraries.minecraft.net/";

    private static List<McVersionEntry>? _manifest;
    public static string? LatestRelease { get; private set; }
    public static string? LatestSnapshot { get; private set; }

    /// <summary>
    /// 获取官方版本列表（带缓存）
    /// </summary>
    public static async Task<List<McVersionEntry>> GetManifestAsync(bool force = false, CancellationToken ct = default)
    {
        if (_manifest is not null && !force) return _manifest;
        var json = await Net.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
        LatestRelease = (string?)json["latest"]?["release"];
        LatestSnapshot = (string?)json["latest"]?["snapshot"];
        _manifest = json["versions"]!.AsArray()
            .Where(v => v is not null)
            .Select(v => new McVersionEntry(
                (string)v!["id"]!, (string)v["type"]!, (string)v["url"]!,
                DateTime.TryParse((string?)v["releaseTime"], out var t) ? t : DateTime.MinValue,
                (string?)v["sha1"]))
            .ToList();
        return _manifest;
    }

    /// <summary>
    /// 按版本号查找官方版本列表中的项
    /// </summary>
    public static async Task<McVersionEntry> FindAsync(string id, CancellationToken ct = default)
    {
        var list = await GetManifestAsync(false, ct).ConfigureAwait(false);
        return list.FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException($"官方版本列表里找不到 {id}");
    }

    /// <summary>
    /// 安装原版：写入版本 JSON 并补全全部文件
    /// </summary>
    public static async Task InstallVanillaAsync(string gameDir, McVersionEntry entry, string name, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("获取版本信息", 0.02, entry.Id));
        var text = await Net.GetStringAsync(entry.Url, ct).ConfigureAwait(false);
        var json = JsonNode.Parse(text)!.AsObject();
        json["id"] = name;
        if (name != entry.Id) json["jar"] = name;
        WriteVersionJson(gameDir, name, json);
        await CompleteAsync(gameDir, name, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保原版的版本 JSON 存在（作为加载器版本的父版本），返回其路径
    /// </summary>
    public static async Task EnsureVanillaJsonAsync(string gameDir, McVersionEntry entry, CancellationToken ct)
    {
        var vanillaJson = Path.Combine(VersionStore.VersionsDir(gameDir), entry.Id, entry.Id + ".json");
        if (File.Exists(vanillaJson)) return;
        var text = await Net.GetStringAsync(entry.Url, ct).ConfigureAwait(false);
        WriteVersionJson(gameDir, entry.Id, JsonNode.Parse(text)!.AsObject());
    }

    public static void WriteVersionJson(string gameDir, string id, JsonObject json)
    {
        var dir = Path.Combine(VersionStore.VersionsDir(gameDir), id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), json.ToJsonString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 补全版本运行所需的全部文件：客户端、库、natives、资源
    /// </summary>
    public static async Task CompleteAsync(string gameDir, string id, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var version = VersionStore.Load(gameDir, id);
        if (!version.IsValid) throw new InvalidDataException(version.Error);
        var json = version.Json!;
        var items = new List<DlItem>();

        // 1. 客户端 jar
        var client = json["downloads"]?["client"];
        if (client is not null)
            items.Add(new DlItem(JarPath(gameDir, json, id), Net.Candidates((string)client["url"]!),
                (string?)client["sha1"], (long?)client["size"] ?? -1));

        // 2. 依赖库与 natives
        items.AddRange(LibraryItems(gameDir, json));

        // 3. 资源索引
        progress?.Report(new("检查资源索引", 0.08));
        var assetIndex = json["assetIndex"];
        JsonObject? indexJson = null;
        if (assetIndex is not null)
        {
            var indexItem = new DlItem(
                Path.Combine(gameDir, "assets", "indexes", (string)assetIndex["id"]! + ".json"),
                Net.Candidates((string)assetIndex["url"]!), (string?)assetIndex["sha1"], (long?)assetIndex["size"] ?? -1);
            await Downloader.RunAsync([indexItem], null, ct).ConfigureAwait(false);
            indexJson = JsonNode.Parse(await File.ReadAllTextAsync(indexItem.Path, ct).ConfigureAwait(false))!.AsObject();
            foreach (var (_, obj) in indexJson["objects"]!.AsObject())
            {
                var hash = (string)obj!["hash"]!;
                var sub = hash[..2];
                items.Add(new DlItem(Path.Combine(gameDir, "assets", "objects", sub, hash),
                    Net.Candidates($"{AssetsBase}/{sub}/{hash}"), hash, (long?)obj["size"] ?? -1));
            }
        }

        // 4. 统一下载
        await Downloader.RunAsync(items, new Progress<DlProgress>(p =>
        {
            if (p.Total == 0) return;
            progress?.Report(new("下载游戏文件", 0.1 + 0.85 * p.Done / p.Total,
                $"{p.Done} / {p.Total} · {FormatSize(p.Bytes)}"));
        }), ct).ConfigureAwait(false);

        progress?.Report(new("文件已就绪", 1.0));
    }

    /// <summary>
    /// 客户端 jar 的路径
    /// </summary>
    public static string JarPath(string gameDir, JsonObject json, string id)
    {
        var jar = (string?)json["jar"] ?? id;
        var path = Path.Combine(VersionStore.VersionsDir(gameDir), jar, jar + ".jar");
        // 文件夹被改过名时（例如切换加载器版本后），沿用版本信息文件名对应的 jar，避免重复下载
        if (!File.Exists(path) && json["jar"] is null && (string?)json["_jsonName"] is { } jsonName)
        {
            var old = Path.Combine(VersionStore.VersionsDir(gameDir), id, jsonName + ".jar");
            if (File.Exists(old)) return old;
        }
        return path;
    }

    /// <summary>
    /// 根据版本 JSON 列出所有需要的库文件（含 natives）
    /// </summary>
    public static IEnumerable<DlItem> LibraryItems(string gameDir, JsonObject json)
    {
        var libDir = Path.Combine(gameDir, "libraries");
        foreach (var lib in json["libraries"]?.AsArray() ?? [])
        {
            if (lib is null || !McRules.Allowed(lib["rules"])) continue;
            // 旧版加载器里标记为仅服务端需要的库
            if (lib["clientreq"] is JsonValue req && req.TryGetValue<bool>(out var clientReq) && !clientReq) continue;
            var name = (string?)lib["name"];
            var artifact = lib["downloads"]?["artifact"];
            if (artifact is not null && (string?)artifact["url"] is { Length: > 0 } url)
            {
                var rel = (string?)artifact["path"] ?? McRules.MavenToPath(name!);
                yield return new DlItem(Path.Combine(libDir, rel), Net.Candidates(url),
                    (string?)artifact["sha1"], (long?)artifact["size"] ?? -1);
            }
            else if (artifact is null && name is not null && lib["natives"] is null)
            {
                // 只有 name 和仓库地址的库
                var rel = McRules.MavenToPath(name);
                var baseUrl = (string?)lib["url"] ?? LibrariesBase;
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = LibrariesBase;
                if (!baseUrl.EndsWith('/')) baseUrl += "/";
                // 旧仓库地址已经停用，换成现在的地址
                baseUrl = baseUrl.Replace("http://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/")
                                 .Replace("https://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/");
                yield return new DlItem(Path.Combine(libDir, rel), Net.Candidates(baseUrl + rel),
                    (string?)lib["sha1"], (long?)lib["size"] ?? -1);
            }

            // 旧版本的 natives 分类文件
            var nativeKey = NativeClassifier(lib);
            if (nativeKey is not null && lib["downloads"]?["classifiers"]?[nativeKey] is { } native)
            {
                yield return new DlItem(Path.Combine(libDir, (string)native["path"]!), Net.Candidates((string)native["url"]!),
                    (string?)native["sha1"], (long?)native["size"] ?? -1);
            }
        }
    }

    /// <summary>
    /// 旧版本 natives 在 Windows 上对应的分类名
    /// </summary>
    public static string? NativeClassifier(JsonNode lib)
    {
        var key = (string?)lib["natives"]?["windows"];
        return key?.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };
}
