using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCL.Core;

public enum DownloadSource { Auto, Official, Mirror }
public enum AppTheme { Light, Dark, System }

/// <summary>
/// 启动器设置，持久化为 JSON
/// </summary>
public sealed class LauncherSettings
{
    public string GameDir { get; set; } = AppPaths.DefaultGameDir;
    public string? SelectedVersion { get; set; }
    public string? SelectedAccountId { get; set; }
    public List<Account> Accounts { get; set; } = new();

    /// <summary>为空表示自动选择 Java</summary>
    public string? JavaPath { get; set; }
    public bool AutoMemory { get; set; } = true;
    public int MaxMemoryMb { get; set; } = 4096;
    public string ExtraJvmArgs { get; set; } = "";
    public string ExtraGameArgs { get; set; } = "";
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool Fullscreen { get; set; }

    /// <summary>每个版本使用独立的游戏目录（mods、存档互不干扰）</summary>
    public bool IsolateVersions { get; set; } = true;

    /// <summary>游戏启动后启动器的行为：0 保持、1 最小化、2 关闭</summary>
    public int AfterLaunch { get; set; } = 1;

    /// <summary>启动游戏前先清理一次内存（需要管理员授权）</summary>
    public bool CleanMemoryBeforeLaunch { get; set; }

    /// <summary>把短时间内大量重复的游戏日志折叠成一条</summary>
    public bool FoldRepeatedLogs { get; set; } = true;

    /// <summary>启动前检查模组前置是否齐全</summary>
    public bool CheckDependencies { get; set; } = true;

    /// <summary>
    /// 联网识别模组：把模组文件的 SHA1 哈希发给资源平台，用于检查更新、查找前置和替换旧版本。
    /// 关闭后不会发送任何模组哈希
    /// </summary>
    public bool AllowOnlineModLookup { get; set; } = true;

    /// <summary>启动时检查 GitHub 上有没有新版本</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    public DownloadSource Source { get; set; } = DownloadSource.Auto;
    public int DownloadThreads { get; set; } = 48;

    public AppTheme Theme { get; set; } = AppTheme.Light;
    public bool ShowMascot { get; set; } = true;

    /// <summary>微软登录使用的 Azure 应用 Client ID；为空时使用内置的</summary>
    public string MsClientId { get; set; } = "";

    /// <summary>工具页上次查询的服务器地址</summary>
    public string LastServer { get; set; } = "";
}

/// <summary>
/// 设置的读写入口
/// </summary>
public static class Settings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LauncherSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<LauncherSettings>(
                    File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8), Options) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取设置失败，使用默认设置");
            Current = new();
        }
        if (string.IsNullOrWhiteSpace(Current.GameDir)) Current.GameDir = AppPaths.DefaultGameDir;
    }

    /// <summary>调试时置为 true，不写入设置文件</summary>
    public static bool ReadOnly { get; set; }

    public static void Save()
    {
        if (ReadOnly) return;
        try
        {
            // 先写临时文件再替换，避免写入中途崩溃导致设置损坏；编码为 UTF-8 无 BOM
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options), new UTF8Encoding(false));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置失败");
        }
    }
}
