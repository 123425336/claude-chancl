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
        if (a.Length >= 2 && a[0] == "--fold-test")
        {
            // 调试：模拟几种刷屏日志，检查折叠效果
            var folder = new LogFolder();
            var outLines = new System.Collections.Generic.List<string>();
            var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            void Feed(string line) { outLines.AddRange(folder.Feed(line, t)); t = t.AddMilliseconds(1); }
            Feed("[12:00:00] [main/INFO]: 正常日志 1");
            Feed("[12:00:00] [main/INFO]: 正常日志 2");
            // 场景一：栈溢出，三行堆栈循环 1000 遍
            Feed("java.lang.StackOverflowError");
            for (var i = 0; i < 1000; i++)
            {
                Feed("\tat com.example.Foo.bar(Foo.java:10)");
                Feed("\tat com.example.Foo.baz(Foo.java:20)");
                Feed("\tat com.example.Foo.qux(Foo.java:30)");
            }
            Feed("[12:00:01] [main/INFO]: 堆栈结束后的正常日志");
            // 场景二：某个线程每毫秒写一次同样的内容（时间戳不同）
            for (var i = 0; i < 500; i++) Feed($"[12:00:{2 + i / 1000:00}.{i % 1000:000}] [Worker-3/WARN]: Tick took too long!");
            Feed("[12:00:03] [main/INFO]: 刷屏结束");
            // 场景三：刷屏混在正常日志中间（不连续）
            for (var i = 0; i < 300; i++)
            {
                Feed("[12:00:04] [Netty/ERROR]: Connection reset by peer");
                Feed($"[12:00:04] [main/INFO]: 加载区块 {i}");
            }
            t = t.AddSeconds(2);
            outLines.AddRange(folder.Tick(t));
            outLines.AddRange(folder.FlushAll());
            var report = new System.Collections.Generic.List<string> { $"输入 {1 + 2 + 1 + 3000 + 1 + 500 + 1 + 600} 行，输出 {outLines.Count} 行：", "" };
            report.AddRange(outLines.Where(l => !l.Contains("加载区块")));
            report.Add($"（另有 {outLines.Count(l => l.Contains("加载区块"))} 行“加载区块”正常日志全部保留，未列出）");
            System.IO.File.WriteAllLines(a[1], report, new System.Text.UTF8Encoding(false));
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
