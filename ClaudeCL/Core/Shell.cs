using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeCL.Core;

/// <summary>
/// 与系统外壳交互：打开文件夹/网址、移入回收站
/// </summary>
public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            // 打开不存在的文件夹时先创建它（文件和网址不处理）
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
                Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"无法打开 {target}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;

    /// <summary>
    /// 移动到回收站（可在回收站里找回）
    /// </summary>
    public static void MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var code = SHFileOperation(ref op);
        if (code != 0) throw new IOException($"移入回收站失败（代码 {code}）");
    }
}
