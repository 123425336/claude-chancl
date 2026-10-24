using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

public enum PackFormat { Modrinth, CurseForge }

/// <summary>整合包里需要下载的一个文件（相对游戏目录的路径）</summary>
public sealed record PackFile(string Path, IReadOnlyList<string> Urls, string? Sha1, long Size);

/// <summary>
/// 读取出的整合包信息
/// </summary>
public sealed class ModpackInfo
{
    public required string Source { get; init; }
    public required PackFormat Format { get; init; }
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Summary { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public LoaderType? Loader { get; init; }
    /// <summary>整合包里写的加载器版本（可能是简写，安装前会换成完整版本号）</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>平台整合包：直接给出了下载地址的文件</summary>
    public List<PackFile> Files { get; } = [];

    /// <summary>另一种格式：只给出资源编号与文件编号，安装时再查询文件名</summary>
    public List<(int Project, int File)> CurseFiles { get; } = [];

    /// <summary>需要原样解压到游戏目录的文件夹（按顺序，后面的覆盖前面的）</summary>
    public List<string> OverrideDirs { get; } = [];

    public int FileCount => Files.Count + CurseFiles.Count;
    public string FormatText => Format == PackFormat.Modrinth ? "Modrinth 整合包" : "CurseForge 整合包";
    public string LoaderText => Loader is null ? "原版" : $"{LoaderService.DisplayName(Loader.Value)} {LoaderVersion}";
}

/// <summary>整合包安装结果：没能完成的部分</summary>
public sealed record ModpackResult(string VersionId, List<string> Warnings);

/// <summary>
/// 整合包的识别与安装：装好游戏版本和加载器，下载包里的模组，再解压配置文件
/// </summary>
public static class ModpackInstaller
{
    /// <summary>安装到版本文件夹里的标记文件，有它的版本总是使用独立的游戏目录</summary>
    public const string MarkerFile = "claudecl-modpack.json";

    private const string CurseSite = "https://www.curseforge.com/api/v1/mods";

    /// <summary>
    /// 打开压缩包：文件名没有标记编码时先按 UTF-8 读，出现乱码再按 GBK 读（很多国内整合包是这样打包的）
    /// </summary>
    public static ZipArchive OpenZip(string path)
    {
        var zip = ZipFile.OpenRead(path);
        if (!zip.Entries.Any(e => e.FullName.Contains('�'))) return zip;
        zip.Dispose();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return ZipFile.Open(path, ZipArchiveMode.Read, Encoding.GetEncoding(936));
    }

    /// <summary>
    /// 判断文件是不是整合包（不抛异常）
    /// </summary>
    public static bool IsModpack(string path)
    {
        try
        {
            using var zip = OpenZip(path);
            return zip.GetEntry("modrinth.index.json") is not null || IsCurseManifest(zip);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurseManifest(ZipArchive zip)
    {
        if (zip.GetEntry("manifest.json") is not { } entry) return false;
        try
        {
            var json = ReadJson(entry);
            return json["minecraft"]?["version"] is not null;
        }
        catch
        {
            return false;
        }
    }

    private static JsonNode ReadJson(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd(), null, new System.Text.Json.JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        }) ?? throw new InvalidDataException("整合包的描述文件是空的");
    }

    /// <summary>
    /// 读取整合包的描述信息
    /// </summary>
    public static ModpackInfo Read(string path)
    {
        using var zip = OpenZip(path);
        if (zip.GetEntry("modrinth.index.json") is { } index) return ReadModrinth(path, ReadJson(index));
        if (zip.GetEntry("manifest.json") is { } manifest && IsCurseManifest(zip)) return ReadCurse(path, ReadJson(manifest));
        if (zip.GetEntry("modpack.json") is not null || zip.GetEntry("mcbbs.packmeta") is not null)
            throw new InvalidDataException("暂时只支持 Modrinth（.mrpack）和 CurseForge 格式的整合包，这个整合包是其他启动器导出的格式。");
        throw new InvalidDataException("这个压缩包里没有找到整合包的描述文件，可能不是整合包。");
    }

    private static ModpackInfo ReadModrinth(string path, JsonNode json)
    {
        var deps = json["dependencies"] as JsonObject ?? [];
        string? Dep(string key) => (string?)deps[key];
        var (loader, loaderVer) =
            Dep("neoforge") is { } neo ? (LoaderType.NeoForge, neo) :
            Dep("forge") is { } forge ? (LoaderType.Forge, forge) :
            Dep("quilt-loader") is { } quilt ? (LoaderType.Quilt, quilt) :
            Dep("fabric-loader") is { } fabric ? ((LoaderType?)LoaderType.Fabric, fabric) : (null, null);
        var info = new ModpackInfo
        {
            Source = path, Format = PackFormat.Modrinth,
            Name = (string?)json["name"] ?? Path.GetFileNameWithoutExtension(path),
            Version = (string?)json["versionId"] ?? "",
            Summary = (string?)json["summary"] ?? "",
            GameVersion = Dep("minecraft") ?? throw new InvalidDataException("整合包没有写明游戏版本"),
            Loader = loader, LoaderVersion = loaderVer,
        };
        foreach (var f in json["files"]?.AsArray() ?? [])
        {
            if (f is null) continue;
            // 只在服务端使用的文件不用下载
            if ((string?)f["env"]?["client"] == "unsupported") continue;
            var urls = f["downloads"]?.AsArray().Select(u => (string?)u).Where(u => !string.IsNullOrEmpty(u)).Cast<string>().ToList() ?? [];
            if (urls.Count == 0 || (string?)f["path"] is not { Length: > 0 } rel) continue;
            info.Files.Add(new PackFile(rel, urls, (string?)f["hashes"]?["sha1"], (long?)f["fileSize"] ?? -1));
        }
        info.OverrideDirs.Add("overrides");
        info.OverrideDirs.Add("client-overrides");
        return info;
    }

    private static ModpackInfo ReadCurse(string path, JsonNode json)
    {
        var mc = json["minecraft"]!;
        var loaders = mc["modLoaders"]?.AsArray().Where(l => l is not null).ToList() ?? [];
        var primary = loaders.FirstOrDefault(l => (bool?)l!["primary"] == true) ?? loaders.FirstOrDefault();
        LoaderType? loader = null;
        string? loaderVer = null;
        if ((string?)primary?["id"] is { } id)
        {
            // 形如 forge-47.2.0、neoforge-21.1.77、fabric-0.15.7
            var dash = id.IndexOf('-');
            var kind = dash > 0 ? id[..dash].ToLowerInvariant() : id.ToLowerInvariant();
            loaderVer = dash > 0 ? id[(dash + 1)..] : null;
            loader = kind switch
            {
                "forge" => LoaderType.Forge,
                "neoforge" => LoaderType.NeoForge,
                "fabric" => LoaderType.Fabric,
                "quilt" => LoaderType.Quilt,
                _ => throw new InvalidDataException($"不认识整合包使用的加载器：{id}"),
            };
        }
        var info = new ModpackInfo
        {
            Source = path, Format = PackFormat.CurseForge,
            Name = (string?)json["name"] ?? Path.GetFileNameWithoutExtension(path),
            Version = (string?)json["version"] ?? "",
            Author = (string?)json["author"] ?? "",
            GameVersion = (string?)mc["version"] ?? throw new InvalidDataException("整合包没有写明游戏版本"),
            Loader = loader, LoaderVersion = loaderVer,
        };
        foreach (var f in json["files"]?.AsArray() ?? [])
        {
            if (f is null || (bool?)f["required"] == false) continue;
            if ((int?)f["projectID"] is { } p && (int?)f["fileID"] is { } fid) info.CurseFiles.Add((p, fid));
        }
        info.OverrideDirs.Add(((string?)json["overrides"] ?? "overrides").Trim('/', '\\'));
        return info;
    }

    /// <summary>
    /// 默认的版本名：整合包名称去掉文件名里不能用的字符，重名时加上序号
    /// </summary>
    public static string SuggestName(string gameDir, ModpackInfo pack)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(pack.Name.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "整合包";
        // 已经装过同一个整合包时，默认在原来的版本上更新
        var existing = AppState.Versions.FirstOrDefault(v => v.FromModpack && (string?)ReadMarker(v.Dir)?["name"] == pack.Name);
        if (existing is not null) return existing.Id;
        var result = name;
        for (var i = 2; Directory.Exists(Path.Combine(VersionStore.VersionsDir(gameDir), result)); i++) result = $"{name} ({i})";
        return result;
    }

    /// <summary>
    /// 把相对路径放到根目录下；路径试图跳出根目录时返回 null（防止恶意压缩包写到别处）
    /// </summary>
    public static string? SafeCombine(string root, string relative)
    {
        var baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return null; }
        return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && full.Length > baseDir.Length ? full : null;
    }

    /// <summary>
    /// 安装整合包为一个新版本
    /// </summary>
    public static async Task<ModpackResult> InstallAsync(ModpackInfo pack, string name, IProgress<StageProgress> progress, CancellationToken ct)
    {
        var gameDir = Settings.Current.GameDir;
        var warnings = new List<string>();

        // 1. 游戏本体与加载器
        progress.Report(new("查询游戏版本", 0.01, pack.GameVersion));
        var entry = await Installer.FindAsync(pack.GameVersion, ct);
        string? loaderVersion = null;
        if (pack.Loader is { } loader)
        {
            progress.Report(new($"查询 {LoaderService.DisplayName(loader)} 版本", 0.02, pack.LoaderVersion ?? ""));
            loaderVersion = await ResolveLoaderVersionAsync(loader, pack.GameVersion, pack.LoaderVersion, ct);
        }
        // 覆盖安装（更新整合包）时，记下旧版整合包带来的文件
        var runDir = Path.Combine(VersionStore.VersionsDir(gameDir), name);
        var oldFiles = ReadMarker(runDir)?["files"]?.AsArray().Select(f => (string?)f).Where(f => f is not null).Cast<string>().ToList() ?? [];
        await LoaderService.InstallAsync(gameDir, entry, pack.Loader, loaderVersion, null, name, LoaderService.Scale(progress, 0.03, 0.5), ct);

        // 2. 先写上标记：之后这个版本总是使用自己的文件夹，模组和存档不会混到别的版本里
        await WriteMarkerAsync(runDir, pack, oldFiles, ct);

        // 3. 下载整合包里的模组等文件
        var items = new List<DlItem>();
        foreach (var f in pack.Files)
        {
            if (SafeCombine(runDir, f.Path) is { } target) items.Add(new DlItem(target, f.Urls, f.Sha1, f.Size));
            else Log.Warn($"整合包里的文件路径不安全，已跳过：{f.Path}");
        }
        var staging = Path.Combine(runDir, ".claudecl-pack");
        if (pack.CurseFiles.Count > 0)
        {
            progress.Report(new("查询模组文件", 0.5, $"共 {pack.CurseFiles.Count} 个"));
            var (resolved, failed) = await ResolveCurseFilesAsync(pack.CurseFiles, runDir, staging,
                (done, total) => progress.Report(new("查询模组文件", 0.5 + 0.05 * done / total, $"{done} / {total}")), ct);
            items.AddRange(resolved);
            if (failed.Count > 0) warnings.Add($"有 {failed.Count} 个文件查询不到下载地址（可能已被作者删除）：{string.Join("、", failed.Take(5))}{(failed.Count > 5 ? " 等" : "")}");
        }
        // 更新整合包时，玩家自己停用的模组保持停用
        var toDownload = items.Where(i => !File.Exists(i.Path + ".disabled")).ToList();
        if (toDownload.Count > 0)
        {
            try
            {
                await Downloader.RunAsync(toDownload, new Progress<DlProgress>(p =>
                {
                    if (p.Total > 0) progress.Report(new("下载整合包文件", 0.55 + 0.35 * p.Done / p.Total, $"{p.Done} / {p.Total} · {Installer.FormatSize(p.Bytes)}"));
                }), ct);
            }
            catch (IOException ex)
            {
                // 个别文件失败时不影响其余部分，重新导入同一个整合包会补上缺的文件
                warnings.Add(ex.Message + "。重新导入这个整合包可以补上缺少的文件。");
            }
        }
        var installed = items.Where(i => !i.Path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Path).ToList();
        if (Directory.Exists(staging)) installed.AddRange(SortStaged(staging, runDir));
        var newFiles = installed.Select(p => Path.GetRelativePath(runDir, p).Replace('\\', '/')).ToList();

        // 4. 旧版整合包有、新版没有的文件移到回收站，避免新旧模组同时加载
        var removed = 0;
        foreach (var old in oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase))
        {
            if (SafeCombine(runDir, old) is not { } path) continue;
            foreach (var candidate in new[] { path, path + ".disabled" }.Where(File.Exists))
            {
                try { Shell.MoveToRecycleBin(candidate); removed++; }
                catch (Exception ex) { Log.Warn($"移除旧版整合包文件 {old} 失败：{ex.Message}"); }
            }
        }

        // 5. 解压配置文件、资源包等
        progress.Report(new("解压配置文件", 0.92));
        var extracted = await Task.Run(() => ExtractOverrides(pack, runDir), ct);
        await WriteMarkerAsync(runDir, pack, newFiles, ct);
        Log.Info($"整合包 {pack.Name}：下载 {items.Count} 个文件，解压 {extracted} 个文件，移除旧文件 {removed} 个，安装到 {runDir}");
        progress.Report(new("整合包安装完成", 1));
        return new ModpackResult(name, warnings);
    }

    /// <summary>
    /// 读取版本文件夹里的整合包标记（没有或损坏时返回 null）
    /// </summary>
    public static JsonObject? ReadMarker(string versionDir)
    {
        try
        {
            var path = Path.Combine(versionDir, MarkerFile);
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 写入整合包标记：名称、版本，以及整合包下载的文件（下次更新整合包时用来清理旧文件）
    /// </summary>
    private static Task WriteMarkerAsync(string runDir, ModpackInfo pack, IEnumerable<string> files, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(runDir, MarkerFile), new JsonObject
        {
            ["name"] = pack.Name,
            ["version"] = pack.Version,
            ["format"] = pack.Format.ToString(),
            ["installedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false), ct);

    /// <summary>
    /// 把整合包里写的加载器版本换成安装器需要的完整版本号
    /// </summary>
    private static async Task<string?> ResolveLoaderVersionAsync(LoaderType loader, string mc, string? version, CancellationToken ct)
    {
        try
        {
            var list = await LoaderService.GetVersionsAsync(loader, mc, ct);
            if (string.IsNullOrEmpty(version)) return (list.FirstOrDefault(l => l.Recommended) ?? list.FirstOrDefault())?.Version;
            var hit = list.FirstOrDefault(l => l.Version == version || ForgeInstaller.ShortVersion(mc, l.Version) == version);
            if (hit is not null) return hit.Version;
            Log.Warn($"{LoaderService.DisplayName(loader)} 版本列表里没有 {version}，按原样安装");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"查询 {LoaderService.DisplayName(loader)} 版本失败，按整合包里写的版本安装：{ex.Message}");
        }
        if (version is null) throw new InvalidOperationException($"整合包没有写明 {LoaderService.DisplayName(loader)} 的版本，也没能查询到可用版本");
        // 查不到列表时按常见的完整版本号格式推断
        return loader switch
        {
            LoaderType.Forge when !version.StartsWith(mc + "-") => $"{mc}-{version}",
            LoaderType.NeoForge when mc == "1.20.1" && !version.StartsWith("1.20.1-") => $"1.20.1-{version}",
            _ => version,
        };
    }

    /// <summary>
    /// 查询每个文件的文件名与大小，生成下载项：模组直接放进 mods，压缩包先放到暂存文件夹，下载后再按内容分类
    /// </summary>
    private static async Task<(List<DlItem> Items, List<string> Failed)> ResolveCurseFilesAsync(
        List<(int Project, int File)> files, string runDir, string staging, Action<int, int> onProgress, CancellationToken ct)
    {
        var items = new ConcurrentBag<DlItem>();
        var failed = new ConcurrentBag<string>();
        var done = 0;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(files.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try
            {
                JsonNode? data = null;
                for (var attempt = 0; attempt < 3 && data is null; attempt++)
                {
                    try
                    {
                        data = (await Net.GetJsonDirectAsync($"{CurseSite}/{f.Project}/files/{f.File}", ct))["data"];
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Log.Warn($"查询文件 {f.Project}/{f.File} 失败（第 {attempt + 1} 次）：{ex.Message}");
                        if (ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }) break;
                        await Task.Delay(600 * (attempt + 1), ct);
                    }
                }
                if ((string?)data?["fileName"] is not { Length: > 0 } fileName)
                {
                    failed.Add($"{f.Project}/{f.File}");
                    return;
                }
                var folder = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? Path.Combine(runDir, "mods") : staging;
                if (SafeCombine(folder, fileName) is not { } target)
                {
                    failed.Add(fileName);
                    return;
                }
                // 文件服务器地址可以由文件编号直接拼出；网页下载地址作为备用
                var cdn = $"https://edge.forgecdn.net/files/{f.File / 1000}/{f.File % 1000}/{Uri.EscapeDataString(fileName)}";
                items.Add(new DlItem(target, [cdn, $"{CurseSite}/{f.Project}/files/{f.File}/download"], null, (long?)data["fileLength"] ?? -1));
            }
            finally
            {
                gate.Release();
                onProgress(Interlocked.Increment(ref done), files.Count);
            }
        }));
        return (items.ToList(), failed.ToList());
    }

    /// <summary>
    /// 暂存文件夹里的压缩包按内容放到资源包、光影或模组文件夹
    /// </summary>
    private static List<string> SortStaged(string staging, string runDir)
    {
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(staging).ToList())
        {
            var folder = DropClassifier.Classify(file) switch
            {
                DropKind.ResourcePack => "resourcepacks",
                DropKind.Shader => "shaderpacks",
                _ => "mods",
            };
            var target = Path.Combine(runDir, folder, Path.GetFileName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, true);
            result.Add(target);
        }
        try { Directory.Delete(staging, true); } catch { /* 忽略 */ }
        return result;
    }

    /// <summary>
    /// 解压覆盖文件夹，返回解压的文件数
    /// </summary>
    private static int ExtractOverrides(ModpackInfo pack, string runDir)
    {
        using var zip = OpenZip(pack.Source);
        var count = 0;
        foreach (var dir in pack.OverrideDirs)
        {
            var prefix = dir + "/";
            foreach (var e in zip.Entries)
            {
                var full = e.FullName.Replace('\\', '/');
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || full.Length == prefix.Length) continue;
                var rel = full[prefix.Length..];
                if (SafeCombine(runDir, rel) is not { } target)
                {
                    Log.Warn($"整合包里的文件路径不安全，已跳过：{full}");
                    continue;
                }
                if (rel.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
                count++;
            }
        }
        return count;
    }
}
