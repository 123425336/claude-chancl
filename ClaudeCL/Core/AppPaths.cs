using System;
using System.IO;

namespace ClaudeCL.Core;

/// <summary>
/// 启动器用到的各类路径
/// </summary>
public static class AppPaths
{
    /// <summary>程序所在目录</summary>
    public static string ExeDir { get; } = AppContext.BaseDirectory;

    /// <summary>启动器数据目录（设置、日志、Java 运行时）</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeCL");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>自动下载的 Java 运行时存放目录</summary>
    public static string RuntimeDir => Path.Combine(DataDir, "runtime");

    /// <summary>默认游戏目录：程序目录下的 .minecraft</summary>
    public static string DefaultGameDir => Path.Combine(ExeDir, ".minecraft");

    static AppPaths()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}
