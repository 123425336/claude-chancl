using System;
using System.Collections.Generic;
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

        // 启动前检查模组前置
        if (Settings.Current.CheckDependencies && version.ModLoaderName is not null && !await CheckDependenciesAsync(version, true))
            return;

        _busy = true;
        using var cts = new CancellationTokenSource();
        // 点击启动后立即打开全屏日志，准备过程和游戏输出都显示在里面
        var view = new GameLogView(version, cts);
        _view = view;
        w.ShowOverlay(view);
        view.Info($"准备启动 {version.Id}（{version.Summary}），账号：{account.Name}（{account.KindText}）");
        void OnTrace(string msg) => view.Info(msg);
        GameLauncher.Trace += OnTrace;
        try
        {
            if (Settings.Current.CleanMemoryBeforeLaunch)
            {
                // 启动前先腾出内存
                view.ReportStage(new StageProgress("正在清理内存", 0, "可能会弹出管理员授权"));
                try
                {
                    var (freed, _) = await MemoryCleaner.CleanAsync();
                    view.Info($"启动前释放了 {Installer.FormatSize(freed)} 内存");
                }
                catch (Exception ex) { view.Info("清理内存失败：" + ex.Message); }
            }

            var session = await GameLauncher.LaunchAsync(version, account, view.Progress, cts.Token);
            session.Exited += code => w.Dispatcher.BeginInvoke(() => OnExited(session, code));
            view.Attach(session);
            view.Info("游戏进程已启动，正在加载……");
            StateChanged?.Invoke();
            await WaitForWindowAsync(session, cts.Token);
            if (!session.Process.HasExited)
            {
                view.Info("游戏窗口已出现");
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
            view.MarkCanceled();
            w.Toast("已取消启动");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动失败");
            view.MarkFailed(ex.Message);
        }
        finally
        {
            GameLauncher.Trace -= OnTrace;
            _busy = false;
        }
    }

    private static GameLogView? _view;

    // ---------------- 前置检查 ----------------

    /// <summary>
    /// 检查前置并让用户选择处理方式；返回是否继续（launching 为 true 时表示是否继续启动）
    /// </summary>
    public static async Task<bool> CheckDependenciesAsync(InstalledVersion version, bool launching)
    {
        var w = MainWindow.Instance!;
        DependencyReport report;
        using (var cts = new CancellationTokenSource())
        {
            var checking = ProgressDialog.Show("正在检查前置模组", cts);
            checking.Report(new StageProgress("检查模组前置", 0.3, "缺少的前置会联网查找"));
            try
            {
                report = await DependencyChecker.CheckAsync(version, cts.Token);
            }
            catch (OperationCanceledException)
            {
                checking.Close();
                return false;
            }
            catch (Exception ex)
            {
                checking.Close();
                Log.Warn("检查前置失败：" + ex.Message);
                w.Toast("检查前置失败：" + ex.Message, ToastKind.Error);
                // 检查本身出错时不拦着启动
                return launching;
            }
            checking.Close();
        }

        if (!report.HasIssues)
        {
            if (!launching) w.Toast("所有前置都准备好了，没有问题～", ToastKind.Success);
            return true;
        }

        var choice = await ShowDependencyDialogAsync(report, launching);
        if (choice == DependencyChoice.Cancel) return false;
        if (choice == DependencyChoice.Ignore) return true;

        using var fixCts = new CancellationTokenSource();
        var fixing = ProgressDialog.Show("正在准备前置", fixCts);
        try
        {
            await DependencyChecker.FixAsync(report, version, fixing.Progress, fixCts.Token);
            fixing.Close();
            var parts = new List<string>();
            if (report.ToEnable.Count > 0) parts.Add($"启用了 {report.ToEnable.Count} 个");
            if (report.ToDownload.Count > 0) parts.Add($"下载了 {report.ToDownload.Count} 个");
            w.Toast($"前置已处理：{string.Join("，", parts)}", ToastKind.Success);
            return true;
        }
        catch (OperationCanceledException)
        {
            fixing.Close();
            return false;
        }
        catch (Exception ex)
        {
            fixing.Close();
            Log.Error(ex, "处理前置失败");
            return await w.Confirm("前置没有全部处理好", ex.Message, launching ? "仍然启动" : "知道了", launching ? "取消" : null) && launching;
        }
    }

    private enum DependencyChoice { Cancel, Ignore, Fix }

    private static Task<DependencyChoice> ShowDependencyDialogAsync(DependencyReport report, bool launching)
    {
        var w = MainWindow.Instance!;
        var tcs = new TaskCompletionSource<DependencyChoice>();
        var body = new StackPanel { Width = 600 };
        body.Children.Add(new TextBlock { Text = "有前置模组没有准备好", Style = (Style)w.FindResource("T.H2"), FontSize = 20 });
        body.Children.Add(new TextBlock
        {
            Text = launching ? "缺少前置时游戏很可能启动失败。可以一键处理后再启动。" : "下面是检查出来的问题：",
            Style = (Style)w.FindResource("T.Soft"), Margin = new Thickness(0, 8, 0, 12),
        });

        var list = new StackPanel();
        void Section(string title, IEnumerable<string> lines)
        {
            var items = lines.ToList();
            if (items.Count == 0) return;
            list.Children.Add(new TextBlock { Text = title, Style = (Style)w.FindResource("T.Label"), Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 4) });
            foreach (var line in items)
                list.Children.Add(new TextBlock { Text = line, Style = (Style)w.FindResource("T.Body"), FontSize = 13, Margin = new Thickness(8, 1, 0, 1) });
        }
        Section($"需要启用（{report.ToEnable.Count}）", report.ToEnable.Select(e => $"· {e.Mod.Name}　—— {e.RequiredBy} 需要"));
        Section($"需要下载（{report.ToDownload.Count}）", report.ToDownload.Select(d => $"· {d.Title} {d.File.VersionName}（{d.File.SizeText}）　—— {d.RequiredBy} 需要"));
        Section($"自动找不到（{report.Unresolved.Count}）", report.Unresolved.Select(u => $"· {u.Id}　—— {u.RequiredBy} 需要"));
        if (report.Unresolved.Count > 0)
            list.Children.Add(new TextBlock
            {
                Text = report.OnlineError is null ? "这些前置在资源平台上没有找到，可能需要去模组作者的页面手动下载。" : $"联网查找失败（{report.OnlineError}），请检查网络后再试。",
                Style = (Style)w.FindResource("T.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 0, 0),
            });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        DialogHandle? handle = null;
        void Done(DependencyChoice c) { handle!.Close(); tcs.TrySetResult(c); }
        var cancel = new Button { Content = launching ? "取消" : "关闭", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        cancel.Click += (_, _) => Done(DependencyChoice.Cancel);
        buttons.Children.Add(cancel);
        if (launching)
        {
            var ignore = new Button { Content = "仍然启动", Margin = new Thickness(0, 0, 10, 0) };
            ignore.Click += (_, _) => Done(DependencyChoice.Ignore);
            buttons.Children.Add(ignore);
        }
        if (report.CanFix)
        {
            var fixText = report.ToEnable.Count > 0 && report.ToDownload.Count > 0 ? "一键启用并下载" : report.ToEnable.Count > 0 ? "一键启用" : "一键下载";
            var fix = new Button { Content = launching ? fixText + "，然后启动" : fixText, Style = (Style)w.FindResource("Btn.Primary") };
            fix.Click += (_, _) => Done(DependencyChoice.Fix);
            buttons.Children.Add(fix);
        }
        body.Children.Add(buttons);
        handle = w.ShowDialog(body, 680);
        handle.OnEscape = () => Done(DependencyChoice.Cancel);
        return tcs.Task;
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
    /// 打开全屏实时日志（主页“查看日志”按钮）
    /// </summary>
    public static void ShowLiveLog()
    {
        var w = MainWindow.Instance!;
        var session = GameLauncher.Running;
        // 正在启动或运行中的那份日志直接打开
        if (_view is not null && (_busy || _view.Session == session && session is not null))
        {
            w.ShowOverlay(_view);
            return;
        }
        if (session is null) { w.Toast("现在没有正在运行的游戏"); return; }
        var view = new GameLogView(session.Version, null);
        view.Attach(session);
        _view = view;
        w.ShowOverlay(view);
    }
}