using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace ClaudeCL.Core;

/// <summary>
/// 简易日志：写入 %AppData%\ClaudeCL\logs\launcher.log，并保留最近若干行供界面查看
/// </summary>
public static class Log
{
    private static readonly object FileLock = new();
    private static readonly string LogFile = Path.Combine(AppPaths.LogDir, "launcher.log");
    private static readonly ConcurrentQueue<string> Recent = new();

    static Log()
    {
        try
        {
            // 日志超过 2MB 时滚动，避免无限增长
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2 * 1024 * 1024)
                File.Move(LogFile, LogFile + ".old", true);
        }
        catch { /* 滚动失败不影响运行 */ }
    }

    public static void Info(string msg) => Write("信息", msg);
    public static void Warn(string msg) => Write("警告", msg);
    public static void Error(Exception ex, string msg) => Write("错误", $"{msg}：{ex}");

    private static void Write(string level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{Thread.CurrentThread.ManagedThreadId}] {msg}";
        Recent.Enqueue(line);
        while (Recent.Count > 500) Recent.TryDequeue(out _);
        lock (FileLock)
        {
            try { File.AppendAllText(LogFile, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch { /* 日志写入失败时静默 */ }
        }
    }

    public static string[] Snapshot() => Recent.ToArray();
}
