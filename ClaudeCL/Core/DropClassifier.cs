using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ClaudeCL.Core;

public enum DropKind { Mod, ResourcePack, Shader, Modpack, Unknown }

/// <summary>
/// 判断拖进来的文件是什么：模组、资源包、光影还是整合包
/// </summary>
public static class DropClassifier
{
    public static DropKind Classify(string path)
    {
        if (!File.Exists(path)) return DropKind.Unknown;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".mrpack") return DropKind.Modpack;
        if (ext is not (".jar" or ".zip")) return DropKind.Unknown;
        try
        {
            using var zip = ModpackInstaller.OpenZip(path);
            if (zip.GetEntry("modrinth.index.json") is not null) return DropKind.Modpack;
            if (ext == ".jar") return DropKind.Mod;
            if (ModpackInstaller.IsModpack(path)) return DropKind.Modpack;
            if (zip.GetEntry("pack.mcmeta") is not null) return DropKind.ResourcePack;
            // 光影包的着色器放在 shaders 文件夹里
            if (zip.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))) return DropKind.Shader;
            // 很老的模组也有打包成 zip 的
            if (zip.GetEntry("mcmod.info") is not null || zip.Entries.Any(e => e.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase))) return DropKind.Mod;
        }
        catch
        {
            // 不是有效的压缩包
        }
        return DropKind.Unknown;
    }

    /// <summary>
    /// 模组适用的加载器（按资源平台的加载器名）；看不出来时返回空
    /// </summary>
    public static IReadOnlyCollection<string> ModLoaders(string path)
    {
        var result = new HashSet<string>();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("fabric.mod.json") is not null) result.UnionWith(["fabric", "quilt"]);
            if (zip.GetEntry("quilt.mod.json") is not null) result.Add("quilt");
            if (zip.GetEntry("META-INF/neoforge.mods.toml") is not null) result.Add("neoforge");
            // 早期的 NeoForge 也使用这个描述文件
            if (zip.GetEntry("META-INF/mods.toml") is not null) result.UnionWith(["forge", "neoforge"]);
            if (zip.GetEntry("mcmod.info") is not null) result.Add("forge");
        }
        catch
        {
            // 读不了时不做判断
        }
        return result;
    }

    public static string KindText(DropKind kind) => kind switch
    {
        DropKind.Mod => "模组",
        DropKind.ResourcePack => "资源包",
        DropKind.Shader => "光影",
        DropKind.Modpack => "整合包",
        _ => "无法识别",
    };

    public static string Folder(DropKind kind) => kind switch
    {
        DropKind.ResourcePack => "resourcepacks",
        DropKind.Shader => "shaderpacks",
        _ => "mods",
    };

    public static string LoaderName(string loader) => loader switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => loader,
    };
}
