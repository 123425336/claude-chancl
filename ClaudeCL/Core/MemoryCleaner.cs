using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>系统内存快照</summary>
public readonly record struct MemoryStatus(ulong TotalPhys, ulong AvailPhys, ulong TotalCommit, ulong AvailCommit)
{
    public ulong UsedPhys => TotalPhys - AvailPhys;
    public double UsedRatio => TotalPhys == 0 ? 0 : (double)UsedPhys / TotalPhys;
    public ulong UsedCommit => TotalCommit - AvailCommit;
}

/// <summary>一个未响应的窗口程序</summary>
public sealed record HungProcess(int Id, string Name, string Title, long WorkingSet);

/// <summary>
/// 内存清理：让各进程交出闲置的工作集，并清理系统的备用列表与文件缓存（需要管理员权限）。
/// 不会结束任何程序。
/// </summary>
public static class MemoryCleaner
{
    // ---------------- 系统接口 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SET_QUOTA = 0x0100;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2, MemoryFlushModifiedList = 3, MemoryPurgeStandbyList = 4, MemoryPurgeLowPriorityStandbyList = 5;

    public static MemoryStatus Status()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return default;
        return new MemoryStatus(m.ullTotalPhys, m.ullAvailPhys, m.ullTotalPageFile, m.ullAvailPageFile);
    }

    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // ---------------- 对外入口 ----------------

    /// <summary>
    /// 清理内存：没有管理员权限时请求提升；用户拒绝则只做不需要权限的部分。
    /// 返回释放的物理内存字节数，以及是否以管理员权限完成。
    /// </summary>
    public static async Task<(long Freed, bool Full)> CleanAsync()
    {
        var before = (long)Status().AvailPhys;
        bool full;
        if (IsAdmin)
        {
            await Task.Run(() => CleanCore(true));
            full = true;
        }
        else
        {
            try
            {
                // 以管理员身份再启动一份自己，只做清理，不显示窗口
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--memclean")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                })!;
                await p.WaitForExitAsync();
                full = p.ExitCode == 0;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 用户在授权弹窗里点了“否”
                Log.Info("用户拒绝了管理员授权，只进行基础清理");
                await Task.Run(() => CleanCore(false));
                full = false;
            }
        }
        await Task.Delay(500);
        var after = (long)Status().AvailPhys;
        Log.Info($"内存清理完成：释放 {Installer.FormatSize(Math.Max(0, after - before))}（{(full ? "完整" : "基础")}）");
        return (Math.Max(0, after - before), full);
    }

    /// <summary>
    /// 管理员子进程的入口（命令行 --memclean）
    /// </summary>
    public static int RunElevated()
    {
        try
        {
            CleanCore(true);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "管理员内存清理失败");
            return 1;
        }
    }

    private static void CleanCore(bool admin)
    {
        if (admin)
        {
            EnablePrivilege("SeDebugPrivilege");
            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeProfileSingleProcessPrivilege");
        }

        // 1. 让每个进程交出暂时用不到的内存（之后需要时会自动换回来）
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                if (h == IntPtr.Zero) continue;
                EmptyWorkingSet(h);
                CloseHandle(h);
            }
            catch { /* 系统进程打不开就跳过 */ }
            finally
            {
                p.Dispose();
            }
        }
        if (!admin) return;

        // 2. 系统级清理：工作集 → 写回已修改页 → 清空低优先级备用列表 → 清空备用列表
        foreach (var cmd in new[] { MemoryEmptyWorkingSets, MemoryFlushModifiedList, MemoryPurgeLowPriorityStandbyList, MemoryPurgeStandbyList })
        {
            var c = cmd;
            var status = NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int));
            if (status != 0) Log.Warn($"内存列表命令 {cmd} 返回 0x{status:X8}");
        }

        // 3. 清空系统文件缓存
        if (!SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0))
            Log.Warn($"清理文件缓存失败：{Marshal.GetLastWin32Error()}");
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x2 };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---------------- 未响应的程序 ----------------

    /// <summary>
    /// 找出窗口已经“未响应”的程序
    /// </summary>
    public static List<HungProcess> FindHung()
    {
        var result = new List<HungProcess>();
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                if (!p.Responding)
                    result.Add(new HungProcess(p.Id, p.ProcessName, p.MainWindowTitle, p.WorkingSet64));
            }
            catch { /* 无权访问的进程跳过 */ }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    public static void Kill(HungProcess h)
    {
        using var p = Process.GetProcessById(h.Id);
        p.Kill(true);
    }
}
