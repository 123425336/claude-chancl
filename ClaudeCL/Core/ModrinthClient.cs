using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>资源搜索结果中的一项</summary>
public sealed record ResourceProject(
    string Id, string Slug, string Title, string Description, string Author,
    string? IconUrl, long Downloads, string[] Categories, string ProjectType)
{
    public string DownloadsText => Downloads switch
    {
        >= 100_000_000 => $"{Downloads / 100_000_000.0:0.#} 亿",
        >= 10_000 => $"{Downloads / 10_000.0:0.#} 万",
        _ => Downloads.ToString(),
    };
}

/// <summary>某个资源的一个可下载版本</summary>
public sealed record ResourceFile(string VersionName, string FileName, string Url, string? Sha1, long Size, string[] GameVersions, string[] Loaders, DateTime Published, string[] RequiredProjects, string VersionType = "release")
{
    public string TypeText => VersionType switch
    {
        "beta" => "测试版",
        "alpha" => "开发版",
        _ => "正式版",
    };
    public string DateText => $"{Published:yyyy-MM-dd}";
    public string SizeText => Installer.FormatSize(Size);

    public string Summary => $"{string.Join(" / ", Loaders)} · {string.Join(", ", GameVersions.Take(3))}{(GameVersions.Length > 3 ? " …" : "")}";
}

/// <summary>一个模组的可用更新</summary>
public sealed record ModUpdateInfo(string ProjectId, string CurrentVersion, ResourceFile Latest, string Changelog);

/// <summary>批量检查的结果：识别出的模组（SHA1 → 资源 ID）与其中有新版的</summary>
public sealed record ModCheckResult(Dictionary<string, string> ProjectIds, Dictionary<string, ModUpdateInfo> Updates);

/// <summary>
/// 社区资源平台的搜索与下载（Mod、资源包、光影）
/// </summary>
public static class ModrinthClient
{
    private const string Api = "https://api.modrinth.com/v2";
    private const string MirrorApi = Net.ModMirror + "/modrinth/v2";

    /// <summary>
    /// 查询接口（只读）：官方接口失败时改用镜像
    /// </summary>
    private static Task<JsonNode> GetApiAsync(string pathAndQuery, CancellationToken ct) =>
        Net.GetJsonWithMirrorAsync(Api + pathAndQuery, MirrorApi + pathAndQuery, ct);

    public static async Task<(List<ResourceProject> Items, int Total)> SearchAsync(
        string query, string projectType, string? gameVersion, string? loader, int offset, CancellationToken ct)
    {
        var facets = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
        if (!string.IsNullOrEmpty(loader) && projectType == "mod") facets.Add($"[\"categories:{loader}\"]");
        var url = $"/search?limit=20&offset={offset}&index=relevance" +
                  $"&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";
        var json = await GetApiAsync(url, ct).ConfigureAwait(false);
        var items = json["hits"]!.AsArray().Select(h => new ResourceProject(
            (string)h!["project_id"]!, (string?)h["slug"] ?? "", (string?)h["title"] ?? "", (string?)h["description"] ?? "",
            (string?)h["author"] ?? "", (string?)h["icon_url"], (long?)h["downloads"] ?? 0,
            h["display_categories"]?.AsArray().Select(c => (string?)c ?? "").ToArray() ?? [],
            (string?)h["project_type"] ?? projectType)).ToList();
        return (items, (int?)json["total_hits"] ?? items.Count);
    }

    public static async Task<List<ResourceFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var url = $"/project/{projectId}/version";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(gameVersion)) query.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (!string.IsNullOrEmpty(loader)) query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));
        if (query.Count > 0) url += "?" + string.Join("&", query);
        var json = await GetApiAsync(url, ct).ConfigureAwait(false);
        var result = new List<ResourceFile>();
        foreach (var v in json.AsArray())
            if (ParseVersion(v) is { } file) result.Add(file);
        // 镜像返回的顺序和官方不同，统一按发布时间从新到旧排列
        return result.OrderByDescending(f => f.Published).ToList();
    }

    /// <summary>
    /// 把接口返回的一个版本对象转换为可下载的文件信息
    /// </summary>
    private static ResourceFile? ParseVersion(JsonNode? v)
    {
        if (v is null) return null;
        var files = v["files"]?.AsArray();
        var file = files?.FirstOrDefault(f => (bool?)f?["primary"] == true) ?? files?.FirstOrDefault();
        if (file is null) return null;
        return new ResourceFile(
            (string?)v["version_number"] ?? (string?)v["name"] ?? "", (string)file["filename"]!, (string)file["url"]!,
            (string?)file["hashes"]?["sha1"], (long?)file["size"] ?? -1,
            v["game_versions"]?.AsArray().Select(g => (string?)g ?? "").ToArray() ?? [],
            v["loaders"]?.AsArray().Select(g => (string?)g ?? "").ToArray() ?? [],
            DateTime.TryParse((string?)v["date_published"], out var t) ? t : DateTime.MinValue,
            // 必需的前置资源
            v["dependencies"]?.AsArray()
                .Where(d => (string?)d?["dependency_type"] == "required" && (string?)d?["project_id"] is { Length: > 0 })
                .Select(d => (string)d!["project_id"]!).ToArray() ?? [],
            (string?)v["version_type"] ?? "release");
    }

    /// <summary>
    /// 按文件 SHA1 批量查询：每个文件属于哪个资源、它的这个版本需要哪些前置资源
    /// </summary>
    public static async Task<Dictionary<string, (string ProjectId, string[] RequiredProjects)>> LookupAsync(
        IReadOnlyCollection<string> sha1s, CancellationToken ct)
    {
        var result = new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase);
        if (sha1s.Count == 0) return result;
        var json = await PostJsonAsync("/version_files", new JsonObject
        {
            ["hashes"] = new JsonArray(sha1s.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
            ["algorithm"] = "sha1",
        }, ct).ConfigureAwait(false);
        foreach (var (hash, v) in json.AsObject())
        {
            if ((string?)v?["project_id"] is not { } pid) continue;
            var deps = v["dependencies"]?.AsArray()
                .Where(d => (string?)d?["dependency_type"] == "required" && (string?)d?["project_id"] is { Length: > 0 })
                .Select(d => (string)d!["project_id"]!).ToArray() ?? [];
            result[hash] = (pid, deps);
        }
        return result;
    }

    /// <summary>
    /// 按资源 ID 或短名称查询资源；不存在时返回 null
    /// </summary>
    public static async Task<ResourceProject?> FindProjectAsync(string idOrSlug, CancellationToken ct)
    {
        try
        {
            return await GetProjectAsync(Uri.EscapeDataString(idOrSlug), ct).ConfigureAwait(false);
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// 按文件哈希识别模组、检查更新的接口：官方接口失败时同样改用镜像（镜像返回的结果与官方一致）
    /// </summary>
    private static Task<JsonNode> PostJsonAsync(string path, JsonObject body, CancellationToken ct) =>
        Net.PostJsonWithMirrorAsync(Api + path, MirrorApi + path, body, ct);

    /// <summary>
    /// 查询单个资源的基本信息（名称、图标、简介）
    /// </summary>
    public static async Task<ResourceProject> GetProjectAsync(string projectId, CancellationToken ct)
    {
        var p = await GetApiAsync($"/project/{projectId}", ct).ConfigureAwait(false);
        return new ResourceProject((string)p["id"]!, (string?)p["slug"] ?? "", (string?)p["title"] ?? "", (string?)p["description"] ?? "",
            "", (string?)p["icon_url"], (long?)p["downloads"] ?? 0, [], (string?)p["project_type"] ?? "mod");
    }

    /// <summary>
    /// 按文件的 SHA1 批量检查模组是否有新版本（只能识别在这个平台上发布的模组）。
    /// 返回：文件 SHA1 → 更新信息；已是最新或无法识别的不在结果里。
    /// </summary>
    public static async Task<ModCheckResult> CheckUpdatesAsync(
        IReadOnlyCollection<string> sha1s, string gameVersion, string loader, CancellationToken ct)
    {
        var result = new Dictionary<string, ModUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sha1s.Count == 0) return new(known, result);
        var hashes = new JsonArray(sha1s.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray());

        // 1. 当前文件对应的是哪个模组的哪个版本
        var current = (await PostJsonAsync("/version_files",
            new JsonObject { ["hashes"] = hashes.DeepClone(), ["algorithm"] = "sha1" }, ct).ConfigureAwait(false)).AsObject();
        foreach (var (hash, cur) in current)
            if ((string?)cur?["project_id"] is { } pid) known[hash] = pid;
        if (current.Count == 0) return new(known, result);

        // 2. 这些模组在当前游戏版本与加载器下的最新版本
        var latest = (await PostJsonAsync("/version_files/update", new JsonObject
        {
            ["hashes"] = new JsonArray(current.Select(kv => (JsonNode)JsonValue.Create(kv.Key)!).ToArray()),
            ["algorithm"] = "sha1",
            ["loaders"] = new JsonArray(loader),
            ["game_versions"] = new JsonArray(gameVersion),
        }, ct).ConfigureAwait(false)).AsObject();

        foreach (var (hash, cur) in current)
        {
            if (cur is null || latest[hash] is not { } newest) continue;
            if ((string?)newest["id"] == (string?)cur["id"]) continue;
            // 最新版的文件里就包含当前文件，说明已经是最新
            if (newest["files"]?.AsArray().Any(f => string.Equals((string?)f?["hashes"]?["sha1"], hash, StringComparison.OrdinalIgnoreCase)) == true) continue;
            var curDate = DateTime.TryParse((string?)cur["date_published"], out var cd) ? cd : DateTime.MinValue;
            var file = ParseVersion(newest);
            if (file is null || file.Published <= curDate) continue;
            var changelog = ((string?)newest["changelog"] ?? "").Trim();
            result[hash] = new ModUpdateInfo((string)cur["project_id"]!, (string?)cur["version_number"] ?? "", file,
                changelog.Length > 1500 ? changelog[..1500] + "……" : changelog);
        }
        return new(known, result);
    }

    /// <summary>
    /// 把资源下载到指定版本对应的文件夹（mods / resourcepacks / shaderpacks）
    /// </summary>
    public static async Task<string> DownloadAsync(ResourceFile file, string projectType, string runDir, CancellationToken ct)
    {
        var folder = projectType switch
        {
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            _ => "mods",
        };
        var target = Path.Combine(runDir, folder, file.FileName);
        await Downloader.RunAsync([new DlItem(target, Net.Candidates(file.Url), file.Sha1, file.Size)], null, ct).ConfigureAwait(false);
        return target;
    }
}
