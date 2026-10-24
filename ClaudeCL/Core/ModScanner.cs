using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeCL.Core;

/// <summary>
/// mods 文件夹里的一个模组文件
/// </summary>
public sealed class ModEntry : INotifyPropertyChanged
{
    private string _path = "";

    public string Path
    {
        get => _path;
        set
        {
            _path = value;
            PropertyChanged?.Invoke(this, new(nameof(Path)));
            PropertyChanged?.Invoke(this, new(nameof(FileName)));
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
    }

    public string FileName => System.IO.Path.GetFileName(Path);
    public bool Enabled => !Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Authors { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public long Size { get; set; }

    /// <summary>在资源平台上识别出的资源 ID（未识别为空）</summary>
    public string? ProjectId { get; set; }

    private ModUpdateInfo? _update;

    /// <summary>可用的新版本（没有为空）</summary>
    public ModUpdateInfo? Update
    {
        get => _update;
        set
        {
            _update = value;
            PropertyChanged?.Invoke(this, new(nameof(Update)));
            PropertyChanged?.Invoke(this, new(nameof(HasUpdate)));
            PropertyChanged?.Invoke(this, new(nameof(UpdateText)));
        }
    }

    public bool HasUpdate => _update is not null;
    public string UpdateText => _update is null ? "" : $"新版 {_update.Latest.VersionName}";

    public void NotifyChanged(string name) => PropertyChanged?.Invoke(this, new(name));

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string Subtitle => string.Join(" · ", new[] { Version, Authors, Installer.FormatSize(Size) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 读取模组文件里的名称、版本、简介和图标（支持各家加载器的描述文件）
/// </summary>
public static class ModScanner
{
    public static List<ModEntry> Scan(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return [];
        return Directory.EnumerateFiles(modsDir)
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(Read)
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static ModEntry Read(string file)
    {
        var mod = new ModEntry { Path = file, Size = new FileInfo(file).Length };
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (!TryFabric(zip, mod) && !TryQuilt(zip, mod) && !TryToml(zip, mod) && !TryMcmod(zip, mod))
            {
                // 没有描述文件时用文件名
            }
        }
        catch (Exception ex)
        {
            mod.Description = "无法读取：" + ex.Message;
        }
        if (string.IsNullOrWhiteSpace(mod.Name))
            mod.Name = Regex.Replace(System.IO.Path.GetFileName(file), @"(\.jar)?(\.disabled)?$", "", RegexOptions.IgnoreCase);
        mod.Description = Regex.Replace(mod.Description ?? "", @"\s+", " ").Trim();
        return mod;
    }

    private static string? Text(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ImageSource? Image(ZipArchive zip, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var entry = zip.GetEntry(path.TrimStart('/'));
        if (entry is null) return null;
        try
        {
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = ms;
            bmp.EndInit();
            // 冻结后才能在后台线程创建、界面线程使用
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFabric(ZipArchive zip, ModEntry mod)
    {
        if (Text(zip, "fabric.mod.json") is not { } text) return false;
        // 部分模组的描述文件里有不合规的换行
        var json = JsonNode.Parse(text.Replace("\n", " ").Replace("\r", " "))!;
        mod.Name = (string?)json["name"] ?? (string?)json["id"] ?? "";
        mod.Version = (string?)json["version"] ?? "";
        mod.Description = (string?)json["description"] ?? "";
        mod.Authors = string.Join("、", json["authors"]?.AsArray().Select(a => a is JsonObject o ? (string?)o["name"] : (string?)a).Where(a => a is not null) ?? []);
        var icon = json["icon"] switch
        {
            JsonObject sizes => (string?)sizes.OrderByDescending(kv => int.TryParse(kv.Key, out var n) ? n : 0).FirstOrDefault().Value,
            JsonValue v => (string?)v,
            _ => null,
        };
        mod.Icon = Image(zip, icon);
        return true;
    }

    private static bool TryQuilt(ZipArchive zip, ModEntry mod)
    {
        if (Text(zip, "quilt.mod.json") is not { } text) return false;
        var loader = JsonNode.Parse(text)?["quilt_loader"];
        if (loader is null) return false;
        var meta = loader["metadata"];
        mod.Name = (string?)meta?["name"] ?? (string?)loader["id"] ?? "";
        mod.Version = (string?)loader["version"] ?? "";
        mod.Description = (string?)meta?["description"] ?? "";
        mod.Authors = string.Join("、", (meta?["contributors"] as JsonObject)?.Select(kv => kv.Key) ?? []);
        mod.Icon = Image(zip, meta?["icon"] is JsonValue iv ? (string?)iv : null);
        return true;
    }

    private static bool TryToml(ZipArchive zip, ModEntry mod)
    {
        var text = Text(zip, "META-INF/neoforge.mods.toml") ?? Text(zip, "META-INF/mods.toml");
        if (text is null) return false;
        // 只取第一个 [[mods]] 段
        var idx = text.IndexOf("[[mods]]", StringComparison.Ordinal);
        var section = idx >= 0 ? text[idx..] : text;
        var next = section.IndexOf("[[", 8, StringComparison.Ordinal);
        if (next > 0) section = section[..next];

        mod.Name = TomlValue(section, "displayName") ?? TomlValue(section, "modId") ?? "";
        mod.Version = TomlValue(section, "version") ?? "";
        mod.Description = TomlValue(section, "description") ?? "";
        mod.Authors = TomlValue(section, "authors") ?? TomlValue(text, "authors") ?? "";
        if (mod.Version.Contains("${"))
        {
            // 版本号写在清单文件里
            var manifest = Text(zip, "META-INF/MANIFEST.MF") ?? "";
            var m = Regex.Match(manifest, @"Implementation-Version:\s*(\S+)");
            mod.Version = m.Success ? m.Groups[1].Value : "";
        }
        mod.Icon = Image(zip, TomlValue(section, "logoFile") ?? TomlValue(text, "logoFile"));
        return true;
    }

    private static string? TomlValue(string text, string key)
    {
        var multi = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*'''(.*?)'''", RegexOptions.Singleline);
        if (multi.Success) return multi.Groups[1].Value.Trim();
        var single = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*""((?:[^""\\]|\\.)*)""");
        if (single.Success) return Regex.Unescape(single.Groups[1].Value);
        var quoted = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*'([^']*)'");
        return quoted.Success ? quoted.Groups[1].Value : null;
    }

    private static bool TryMcmod(ZipArchive zip, ModEntry mod)
    {
        if (Text(zip, "mcmod.info") is not { } text) return false;
        var json = JsonNode.Parse(text);
        var first = json switch
        {
            JsonArray arr => arr.FirstOrDefault(),
            JsonObject obj => obj["modList"]?.AsArray().FirstOrDefault(),
            _ => null,
        };
        if (first is null) return false;
        mod.Name = (string?)first["name"] ?? (string?)first["modid"] ?? "";
        mod.Version = (string?)first["version"] ?? "";
        mod.Description = (string?)first["description"] ?? "";
        mod.Authors = string.Join("、", first["authorList"]?.AsArray().Select(a => (string?)a).Where(a => a is not null) ?? []);
        mod.Icon = Image(zip, (string?)first["logoFile"]);
        return true;
    }

    /// <summary>
    /// 启用 / 停用：通过在文件名后加上 .disabled 实现
    /// </summary>
    public static void SetEnabled(ModEntry mod, bool enabled)
    {
        if (mod.Enabled == enabled) return;
        var target = enabled ? mod.Path[..^".disabled".Length] : mod.Path + ".disabled";
        File.Move(mod.Path, target);
        mod.Path = target;
    }
}
