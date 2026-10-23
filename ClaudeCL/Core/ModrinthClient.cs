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
public sealed record ResourceFile(string VersionName, string FileName, string Url, string? Sha1, long Size, string[] GameVersions, string[] Loaders, DateTime Published, string[] RequiredProjects)
{
    public string Summary => $"{string.Join(" / ", Loaders)} · {string.Join(", ", GameVersions.Take(3))}{(GameVersions.Length > 3 ? " …" : "")}";
}

/// <summary>
/// 社区资源平台的搜索与下载（Mod、资源包、光影）
/// </summary>
public static class ModrinthClient
{
    private const string Api = "https://api.modrinth.com/v2";

    public static async Task<(List<ResourceProject> Items, int Total)> SearchAsync(
        string query, string projectType, string? gameVersion, string? loader, int offset, CancellationToken ct)
    {
        var facets = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
        if (!string.IsNullOrEmpty(loader) && projectType == "mod") facets.Add($"[\"categories:{loader}\"]");
        var url = $"{Api}/search?limit=20&offset={offset}&index=relevance" +
                  $"&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";
        var json = await Net.GetJsonDirectAsync(url, ct).ConfigureAwait(false);
        var items = json["hits"]!.AsArray().Select(h => new ResourceProject(
            (string)h!["project_id"]!, (string?)h["slug"] ?? "", (string?)h["title"] ?? "", (string?)h["description"] ?? "",
            (string?)h["author"] ?? "", (string?)h["icon_url"], (long?)h["downloads"] ?? 0,
            h["display_categories"]?.AsArray().Select(c => (string?)c ?? "").ToArray() ?? [],
            (string?)h["project_type"] ?? projectType)).ToList();
        return (items, (int?)json["total_hits"] ?? items.Count);
    }

    public static async Task<List<ResourceFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var url = $"{Api}/project/{projectId}/version";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(gameVersion)) query.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (!string.IsNullOrEmpty(loader)) query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));
        if (query.Count > 0) url += "?" + string.Join("&", query);
        var json = await Net.GetJsonDirectAsync(url, ct).ConfigureAwait(false);
        var result = new List<ResourceFile>();
        foreach (var v in json.AsArray())
        {
            if (v is null) continue;
            var files = v["files"]?.AsArray();
            var file = files?.FirstOrDefault(f => (bool?)f?["primary"] == true) ?? files?.FirstOrDefault();
            if (file is null) continue;
            result.Add(new ResourceFile(
                (string?)v["version_number"] ?? (string?)v["name"] ?? "", (string)file["filename"]!, (string)file["url"]!,
                (string?)file["hashes"]?["sha1"], (long?)file["size"] ?? -1,
                v["game_versions"]?.AsArray().Select(g => (string?)g ?? "").ToArray() ?? [],
                v["loaders"]?.AsArray().Select(g => (string?)g ?? "").ToArray() ?? [],
                DateTime.TryParse((string?)v["date_published"], out var t) ? t : DateTime.MinValue,
                // 必需的前置资源
                v["dependencies"]?.AsArray()
                    .Where(d => (string?)d?["dependency_type"] == "required" && (string?)d?["project_id"] is { Length: > 0 })
                    .Select(d => (string)d!["project_id"]!).ToArray() ?? []));
        }
        return result;
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
        await Downloader.RunAsync([new DlItem(target, [file.Url], file.Sha1, file.Size)], null, ct).ConfigureAwait(false);
        return target;
    }
}
