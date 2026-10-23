using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClaudeCL.Core;
using Microsoft.Win32;

namespace ClaudeCL;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info($"ClaudeCL {GameLauncher.LauncherVersion} 启动");

        // 全局异常兜底：记录日志并温柔地提示，而不是直接闪退
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error((Exception)args.ExceptionObject, "未处理的异常");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };

        // 以管理员身份运行的内存清理子进程：清理完立即退出
        if (e.Args.Length > 0 && e.Args[0] == "--memclean")
        {
            Shutdown(MemoryCleaner.RunElevated());
            return;
        }

        Settings.Load();
#if DEBUG
        // 调试自检：不显示窗口，安装并启动后退出
        var a = e.Args;
        // 调试截图时指定临时游戏目录，且不改动用户的设置文件
        var gi = Array.IndexOf(a, "--gamedir");
        if (gi >= 0 && gi + 1 < a.Length)
        {
            Settings.ReadOnly = true;
            Settings.Current.GameDir = a[gi + 1];
            var si = Array.IndexOf(a, "--select");
            if (si >= 0 && si + 1 < a.Length) Settings.Current.SelectedVersion = a[si + 1];
        }
        if (a.Length >= 2 && a[0] == "--redact-test")
        {
            // 调试：检查各种可能泄漏令牌的文本是否都被遮盖
            Redactor.Register("myRawMinecraftAccessToken1234567890");
            string[] samples =
            [
                "(Session ID is token:myRawMinecraftAccessToken1234567890:0123456789abcdef0123456789abcdef)",
                "--username Steve --accessToken eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U --userType msa",
                "--accessToken someOpaqueTokenValue123 --version 1.7.10",
                "GET https://example.com/cb?code=M.C123_BAY.2.U.abcdef&state=xyz",
                "Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345",
                "普通日志：Setting user: Steve，加载了 128 个模组",
            ];
            System.IO.File.WriteAllLines(a[1], samples.Select(s => "原文：" + s + "\n遮盖：" + Redactor.Clean(s) + "\n"), new System.Text.UTF8Encoding(false));
            Shutdown(0);
            return;
        }
        if (a.Length >= 4 && a[0] == "--selftest")
        {
            Settings.ReadOnly = true;
            var loader = a.Length >= 5 ? a[3] : null;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = SelfTest.RunAsync(a[1], a[2], loader, a[^1]).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.Result)));
            return;
        }
#endif
        ApplyTheme(Settings.Current.Theme);
        new MainWindow().Show();
    }

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "界面线程异常");
        e.Handled = true;
        ClaudeCL.MainWindow.Instance?.Toast("出了一点小意外：" + e.Exception.Message, ToastKind.Error);
    }

    /// <summary>
    /// 切换浅色/深色配色
    /// </summary>
    public static void ApplyTheme(AppTheme mode)
    {
        var dark = mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };
        var dicts = Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        IsDark = dark;
    }

    public static bool IsDark { get; private set; }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
