using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>
/// 从点击“启动”到游戏窗口出现的完整流程，以及运行中/崩溃后的界面反馈
/// </summary>
public static class LaunchFlow
{
    /// <summary>游戏开始或结束运行时触发</summary>
    public static event Action? StateChanged;

    private static bool _busy;

    public static async Task RunAsync(InstalledVersion? version)
    {
        var w = MainWindow.Instance!;
        if (_busy) return;
        if (version is null || !version.IsValid)
        {
            w.Toast("还没有可以启动的版本，先去下载一个吧～");
            w.Navigate("download");
            return;
        }

        // 没有账号时，顺手创建一个离线账号
        var account = AccountService.Selected;
        if (account is null)
        {
            var name = await w.Prompt("先告诉我你的名字吧", "会作为离线账号的玩家名（3~16 位英文、数字或下划线）。以后可以在「账号」页添加微软账号。", "", "例如 Steve");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                account = AccountService.CreateOffline(name);
                AccountService.Add(account);
                AppState.NotifyAccounts();
            }
            catch (Exception ex)
            {
                w.Toast(ex.Message, ToastKind.Error);
                return;
            }
        }

        if (GameLauncher.Running is not null &&
            !await w.Confirm("已经有游戏在运行啦", "确定还要再启动一个吗？同时运行两个游戏会占用很多内存。", "再开一个", "算了"))
            return;

        _busy = true;
        if (Settings.Current.CleanMemoryBeforeLaunch)
        {
            // 启动前先腾出内存
            var clean = ProgressDialog.Show("先整理一下内存");
            clean.Report(new StageProgress("正在清理内存", 0.5, "可能会弹出管理员授权"));
            try
            {
                var (freed, _) = await MemoryCleaner.CleanAsync();
                w.Toast($"启动前释放了 {Installer.FormatSize(freed)} 内存", ToastKind.Success);
            }
            catch (Exception ex) { Log.Warn("启动前清理内存失败：" + ex.Message); }
            finally { clean.Close(); }
        }
        using var cts = new CancellationTokenSource();
        var dialog = ProgressDialog.Show($"正在启动 {version.Id}", cts);
        try
        {
            var session = await GameLauncher.LaunchAsync(version, account, dialog.Progress, cts.Token);
            dialog.Report(new StageProgress("等待游戏窗口出现", 1, "第一次启动会稍微慢一些"));
            session.Exited += code => w.Dispatcher.BeginInvoke(() => OnExited(session, code));
            StateChanged?.Invoke();
            await WaitForWindowAsync(session, cts.Token);
            dialog.Close();
            if (!session.Process.HasExited)
            {
                w.Toast($"{version.Id} 启动成功，玩得开心！", ToastKind.Success);
                switch (Settings.Current.AfterLaunch)
                {
                    case 1: w.WindowState = WindowState.Minimized; break;
                    case 2: w.Close(); break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            w.Toast("已取消启动");
        }
        catch (Exception ex)
        {
            dialog.Close();
            Log.Error(ex, "启动失败");
            if (await w.Confirm("启动没有成功", ex.Message, "打开日志", "知道了"))
                Shell.Open(AppPaths.LogDir);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 等到游戏窗口出现、进程退出或超时
    /// </summary>
    private static async Task WaitForWindowAsync(GameSession session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            ct.ThrowIfCancellationRequested();
            if (session.Process.HasExited) return;
            session.Process.Refresh();
            if (session.Process.MainWindowHandle != IntPtr.Zero) return;
            await Task.Delay(400, ct);
        }
    }

    private static async void OnExited(GameSession session, int code)
    {
        StateChanged?.Invoke();
        var w = MainWindow.Instance;
        if (w is null || !session.LooksCrashed(code)) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        await ShowCrashAsync(session, code);
    }

    private static Task ShowCrashAsync(GameSession session, int code)
    {
        var w = MainWindow.Instance!;
        var tcs = new TaskCompletionSource();
        var body = new StackPanel { Width = 620 };
        body.Children.Add(new TextBlock { Text = "游戏好像不太舒服……", Style = (Style)w.FindResource("T.H2"), FontSize = 20 });
        body.Children.Add(new TextBlock
        {
            Text = $"{session.Version.Id} 意外退出了（退出码 {code}）。下面是最后的游戏日志，常见原因有：模组不兼容、Java 版本不对、内存不够。",
            Style = (Style)w.FindResource("T.Soft"), Margin = new Thickness(0, 8, 0, 12),
        });
        var text = string.Join(Environment.NewLine, session.Lines.TakeLast(120));
        // 内存不足导致的崩溃，给出明确的建议
        var lowMemory = session.Lines.Any(l => l.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase)
                                               || l.Contains("页面文件太小") || l.Contains("OutOfMemoryError"));
        if (lowMemory)
        {
            var tip = new Border
            {
                Style = (Style)w.FindResource("Pill"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "看起来是电脑内存不够了：可以点下面的「清理内存」，或者关掉一些程序、在设置里调小游戏内存后再试。",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            body.Children.Add(tip);
        }
        body.Children.Add(new TextBox { Text = text, Style = (Style)w.FindResource("TextBox.Code"), Height = 260 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var crashDir = Path.Combine(session.Version.RunDir(Settings.Current.GameDir), "crash-reports");
        var open = new Button { Content = "崩溃报告文件夹", Margin = new Thickness(0, 0, 10, 0) };
        open.Click += (_, _) => Shell.Open(crashDir);
        var copy = new Button { Content = "复制日志", Margin = new Thickness(0, 0, 10, 0) };
        if (lowMemory)
        {
            var clean = new Button { Content = "清理内存", Margin = new Thickness(0, 0, 10, 0) };
            Controls.Ui.SetIcon(clean, "\uEA18");
            clean.Click += async (_, _) =>
            {
                clean.IsEnabled = false;
                var (freed, _) = await MemoryCleaner.CleanAsync();
                w.Toast($"释放了 {Installer.FormatSize(freed)} 内存，可以再试试启动啦", ToastKind.Success);
                clean.IsEnabled = true;
            };
            buttons.Children.Add(clean);
        }
        copy.Click += (_, _) => { Clipboard.SetText(text); w.Toast("日志已复制", ToastKind.Success); };
        var close = new Button { Content = "知道了", Style = (Style)w.FindResource("Btn.Primary"), MinWidth = 84 };
        buttons.Children.Add(open);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        var handle = w.ShowDialog(body, 700);
        close.Click += (_, _) => { handle.Close(); tcs.TrySetResult(); };
        handle.OnEscape = () => { handle.Close(); tcs.TrySetResult(); };
        return tcs.Task;
    }

    /// <summary>
    /// 实时查看正在运行的游戏日志
    /// </summary>
    public static void ShowLiveLog()
    {
        var session = GameLauncher.Running;
        var w = MainWindow.Instance!;
        if (session is null) { w.Toast("现在没有正在运行的游戏"); return; }

        var body = new StackPanel { Width = 680 };
        body.Children.Add(new TextBlock { Text = $"{session.Version.Id} 的实时日志", Style = (Style)w.FindResource("T.H2"), FontSize = 19 });
        var box = new TextBox
        {
            Style = (Style)w.FindResource("TextBox.Code"), Height = 360, Margin = new Thickness(0, 14, 0, 0),
            Text = string.Join(Environment.NewLine, session.Lines),
        };
        body.Children.Add(box);
        var close = new Button { Content = "关闭", Style = (Style)w.FindResource("Btn.Primary"), MinWidth = 84, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        body.Children.Add(close);
        var handle = w.ShowDialog(body, 760);

        // 定时刷新而不是每行刷新，避免日志刷屏时界面卡顿
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var atBottom = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 4;
            box.Text = string.Join(Environment.NewLine, session.Lines);
            if (atBottom) box.ScrollToEnd();
        };
        timer.Start();
        box.Loaded += (_, _) => box.ScrollToEnd();
        void Done() { timer.Stop(); handle.Close(); }
        close.Click += (_, _) => Done();
        handle.OnEscape = Done;
    }
}
