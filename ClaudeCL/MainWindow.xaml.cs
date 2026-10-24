using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ClaudeCL.Core;
using ClaudeCL.Pages;

namespace ClaudeCL;

public enum ToastKind { Info, Success, Error }

/// <summary>
/// 打开中的对话框，调用 Close 关闭
/// </summary>
public sealed class DialogHandle
{
    internal Grid Container { get; init; } = null!;
    internal MainWindow Owner { get; init; } = null!;
    internal Action? OnEscape { get; set; }
    public bool IsClosed { get; internal set; }
    public void Close() => Owner.CloseDialog(this);
}

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly List<DialogHandle> _dialogs = new();
    private string _current = "";

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["home"] = "主页", ["download"] = "下载游戏", ["resources"] = "资源中心", ["versions"] = "版本管理",
        ["accounts"] = "账号", ["tools"] = "小工具", ["settings"] = "设置", ["about"] = "关于",
    };

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateMaxState();
        PreviewKeyDown += OnPreviewKeyDown;
        AppState.AccountsChanged += () => Dispatcher.Invoke(RefreshAccountCard);
        UpdateThemeIcon();
    }

    // ---------------- 启动画面与初始化 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        string[] lines = ["正在翻开书页……", "正在给方块世界掸掸灰……", "正在寻找你的 Java……", "马上就好～"];
        var i = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) => SplashText.Text = lines[++i % lines.Length];
        timer.Start();

        // Java 扫描与版本列表在后台进行，不阻塞界面
        _ = Task.Run(async () =>
        {
            try { await JavaFinder.ScanAsync(); } catch (Exception ex) { Log.Error(ex, "扫描 Java 失败"); }
        });
        _ = Task.Run(async () =>
        {
            try { await Installer.GetManifestAsync(); } catch (Exception ex) { Log.Warn("预加载版本列表失败：" + ex.Message); }
        });
        try { await AppState.RefreshVersionsAsync(); } catch (Exception ex) { Log.Error(ex, "读取已安装版本失败"); }

        RefreshAccountCard();
        Navigate("home");

        var wait = 1400 - (int)sw.ElapsedMilliseconds;
        if (wait > 0) await Task.Delay(wait);
        timer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase() };
        fade.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
        Splash.BeginAnimation(OpacityProperty, fade);

        if (App.UpdatedTo is { } tag) Toast($"已经更新到 {tag} 啦，看看有什么新变化吧～", ToastKind.Success);
        else if (Settings.Current.AutoCheckUpdate && !Environment.GetCommandLineArgs().Contains("--shot")) _ = CheckUpdateAsync(false);
#if DEBUG
        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--shot");
        if (idx >= 0 && idx + 1 < args.Length) await CaptureAllAsync(args[idx + 1]);
#endif
    }

#if DEBUG
    /// <summary>
    /// 调试用：依次打开每个页面并截图保存，然后退出
    /// </summary>
    private async Task CaptureAllAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        await Task.Delay(600);
        foreach (var dark in new[] { false, true })
        {
            App.ApplyTheme(dark ? AppTheme.Dark : AppTheme.Light);
            UpdateThemeIcon();
            foreach (var key in Titles.Keys)
            {
                Log.Info($"截图：{key}");
                Navigate(key);
                (_pages[key] as IPage)?.OnShown();
                await Task.Delay(key is "download" or "resources" ? 4000 : 900);
                Save(System.IO.Path.Combine(dir, $"{(dark ? "dark" : "light")}-{key}.png"));
                if (dark) break;
            }
        }
        App.ApplyTheme(Settings.Current.Theme);
        UpdateThemeIcon();
        if (AppState.SelectedVersion is { } first)
        {
            ShowVersionDetail(first);
            // 输入一点搜索词，确认清空按钮的样子
            // 等模组更新检查完成
            await Task.Delay(7000);
            Save(System.IO.Path.Combine(dir, "light-detail-mods.png"));
            if (PageHost.Content is VersionDetailPage withUpdates && withUpdates.Mods.FirstOrDefault(m => m.HasUpdate) is { } outdated)
            {
                Navigate("resources");
                GetPage<ResourcesPage>("resources")?.ShowUpdate(first, outdated, withUpdates.Mods);
                await Task.Delay(6000);
                Save(System.IO.Path.Combine(dir, "light-update-banner.png"));
                ShowVersionDetail(first);
                await Task.Delay(1500);
            }
            if (PageHost.Content is VersionDetailPage detail)
            {
                detail.LoaderTab.IsChecked = true;
                await Task.Delay(4000);
                Save(System.IO.Path.Combine(dir, "light-detail-loader.png"));
            }
        }
        if (AppState.SelectedVersion is { } logVersion)
        {
            // 模拟一次启动过程的实时日志
            var log = new GameLogView(logVersion, new System.Threading.CancellationTokenSource());
            ShowOverlay(log);
            log.Info($"准备启动 {logVersion.Id}（{logVersion.Summary}），账号：Steve（离线账号）");
            log.ReportStage(new StageProgress("下载游戏文件", 0.42, "1532 / 3648 · 182.4 MB"));
            log.Info("使用 Java：JDK 17.0.12 Oracle 64 Bit | C:\\Program Files\\Java\\jdk-17\\bin");
            for (var i = 0; i < 40; i++)
                log.Append($"[12:00:{i:00}] [Render thread/INFO]: 示例游戏输出第 {i + 1} 行 Loading resources...");
            await Task.Delay(1200);
            Save(System.IO.Path.Combine(dir, "light-log.png"));
            CloseOverlay(log);
            await Task.Delay(400);
        }
        Navigate("home");
        _ = Confirm("这是一个对话框", "用来确认截图里的对话框样式是否好看。", "好的", "取消");
        await Task.Delay(600);
        Save(System.IO.Path.Combine(dir, "light-dialog.png"));
        Close();
    }

    private void Save(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
#endif

    // ---------------- 自动更新 ----------------

    /// <summary>
    /// 检查新版本；manual 为 true 时（手动点击）没有新版也会提示
    /// </summary>
    public async Task CheckUpdateAsync(bool manual)
    {
        try
        {
            var release = await SelfUpdater.CheckAsync(default);
            if (release is null)
            {
                if (manual) Toast($"已经是最新版本啦（v{SelfUpdater.Current}）", ToastKind.Success);
                return;
            }
            Log.Info($"发现新版本 {release.Tag}");
            ShowUpdateDialog(release);
        }
        catch (Exception ex)
        {
            Log.Warn("检查更新失败：" + ex.Message);
            if (manual) Toast("检查更新失败：" + ex.GetBaseException().Message, ToastKind.Error);
        }
    }

    private void ShowUpdateDialog(ReleaseInfo r)
    {
        var body = new StackPanel { Width = 560 };
        body.Children.Add(new TextBlock { Text = $"发现新版本 {r.Tag}", Style = (Style)FindResource("T.H2"), FontSize = 20 });
        body.Children.Add(new TextBlock
        {
            Text = $"当前是 v{SelfUpdater.Current}，下载约 {Installer.FormatSize(r.AssetSize)}。更新后会自动重启，设置和账号都会保留。",
            Style = (Style)FindResource("T.Soft"), Margin = new Thickness(0, 8, 0, 12),
        });
        body.Children.Add(new TextBox
        {
            Text = string.IsNullOrWhiteSpace(r.Notes) ? "（这个版本没有写更新说明）" : r.Notes,
            Style = (Style)FindResource("TextBox.Code"), MaxHeight = 280,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var page = new Button { Content = "查看发布页", Style = (Style)FindResource("Btn.Ghost"), Margin = new Thickness(0, 0, 10, 0) };
        var later = new Button { Content = "稍后", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        var now = new Button { Content = "立即更新", Style = (Style)FindResource("Btn.Primary"), MinWidth = 100 };
        Controls.Ui.SetIcon(now, "");
        buttons.Children.Add(page);
        buttons.Children.Add(later);
        buttons.Children.Add(now);
        body.Children.Add(buttons);
        var handle = ShowDialog(body, 640);
        page.Click += (_, _) => Shell.Open(r.HtmlUrl);
        later.Click += (_, _) => handle.Close();
        handle.OnEscape = handle.Close;
        now.Click += async (_, _) =>
        {
            handle.Close();
            await UpdateNowAsync(r);
        };
    }

    private async Task UpdateNowAsync(ReleaseInfo r)
    {
        if (!SelfUpdater.CanWriteAppDir())
        {
            if (await Confirm("没法自动更新", "程序所在的文件夹没有写入权限（比如放在 Program Files 里）。可以打开发布页手动下载。", "打开发布页", "取消"))
                Shell.Open(r.HtmlUrl);
            return;
        }
        if (GameLauncher.Running is not null &&
            !await Confirm("游戏正在运行", "更新后启动器会自动重启。正在运行的游戏不受影响，但实时日志会断开。", "继续更新", "稍后"))
            return;

        using var cts = new System.Threading.CancellationTokenSource();
        var dialog = Controls.ProgressDialog.Show($"正在更新到 {r.Tag}", cts);
        try
        {
            var zip = await SelfUpdater.DownloadAsync(r, dialog.Progress, cts.Token);
            dialog.Report(new StageProgress("替换程序文件", 0.94));
            await Task.Run(() => SelfUpdater.Apply(zip));
            dialog.Report(new StageProgress("更新完成，正在重启", 1));
            await Task.Delay(600);
            SelfUpdater.Restart(r.Tag);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            Toast("已取消更新");
        }
        catch (Exception ex)
        {
            dialog.Close();
            Log.Error(ex, "自动更新失败");
            if (await Confirm("更新没有完成", ex.Message + "\n\n程序文件已经还原，可以继续使用当前版本。", "打开发布页", "知道了"))
                Shell.Open(r.HtmlUrl);
        }
    }

    // ---------------- 导航 ----------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) ShowPage(key);
    }

    /// <summary>
    /// 跳转到指定页面（同时更新侧栏选中状态）
    /// </summary>
    public void Navigate(string key)
    {
        foreach (var child in Nav.Children)
        {
            if (child is RadioButton rb && (string)rb.Tag == key)
            {
                if (rb.IsChecked == true) ShowPage(key);
                else rb.IsChecked = true;
                return;
            }
        }
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "home" => new HomePage(),
                "download" => new DownloadPage(),
                "resources" => new ResourcesPage(),
                "versions" => new VersionsPage(),
                "accounts" => new AccountsPage(),
                "tools" => new ToolsPage(),
                "settings" => new SettingsPage(),
                _ => new AboutPage(),
            };
            _pages[key] = page;
        }
        if (_current == key && PageHost.Content == page) return;
        _current = key;
        Present(page, Titles.GetValueOrDefault(key, ""));
    }

    /// <summary>
    /// 打开某个版本的详情页（不属于侧栏导航，侧栏取消选中）
    /// </summary>
    public void ShowVersionDetail(InstalledVersion version)
    {
        foreach (var child in Nav.Children)
            if (child is RadioButton rb) rb.IsChecked = false;
        _current = "detail";
        Present(new VersionDetailPage(version), $"管理模组 / {version.Id}");
    }

    private void Present(FrameworkElement page, string title)
    {
        PageTitle.Text = title;
        PageHost.Content = page;
        (page as IPage)?.OnShown();

        // 淡入 + 轻微上浮
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    public T? GetPage<T>(string key) where T : FrameworkElement =>
        _pages.TryGetValue(key, out var p) ? p as T : null;

    // ---------------- 账号小卡片 ----------------

    private void RefreshAccountCard()
    {
        var acc = AccountService.Selected;
        AccountInitial.Text = acc?.Initial ?? "+";
        AccountName.Text = acc?.Name ?? "还没有账号";
        AccountKind.Text = acc?.KindText ?? "点击添加一个吧";
    }

    private void AccountCard_Click(object sender, RoutedEventArgs e) => Navigate("accounts");

    // ---------------- 标题栏 ----------------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.Theme = App.IsDark ? AppTheme.Light : AppTheme.Dark;
        Settings.Save();
        App.ApplyTheme(Settings.Current.Theme);
        UpdateThemeIcon();
        (GetPage<SettingsPage>("settings"))?.OnShown();
    }

    public void UpdateThemeIcon() => Controls.Ui.SetIcon(ThemeButton, App.IsDark ? "" : "");

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxState()
    {
        // 无边框窗口最大化时会超出屏幕边缘，补一圈边距
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Controls.Ui.SetIcon(MaxButton, WindowState == WindowState.Maximized ? "" : "");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_dialogs.Count > 0)
        {
            _dialogs[^1].OnEscape?.Invoke();
            e.Handled = true;
        }
        else if (OverlayLayer.Children.Count > 0 && OverlayLayer.Children[0] is FrameworkElement top)
        {
            CloseOverlay(top);
            e.Handled = true;
        }
    }

    // ---------------- 全屏层 ----------------

    /// <summary>
    /// 盖住整个窗口显示一个页面（例如实时日志），从下方轻轻浮上来
    /// </summary>
    public void ShowOverlay(FrameworkElement view)
    {
        if (OverlayLayer.Children.Contains(view) && OverlayLayer.Visibility == Visibility.Visible) return;
        OverlayLayer.Children.Clear();
        OverlayLayer.Children.Add(view);
        OverlayLayer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OverlayLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        OverlayShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    public void CloseOverlay(FrameworkElement view)
    {
        if (!OverlayLayer.Children.Contains(view)) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            // 动画期间可能又打开了别的页面
            if (OverlayLayer.Opacity > 0.01) return;
            OverlayLayer.Children.Clear();
            OverlayLayer.Visibility = Visibility.Collapsed;
        };
        OverlayLayer.BeginAnimation(OpacityProperty, fade);
        OverlayShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, TimeSpan.FromMilliseconds(180)));
    }

    // ---------------- 通知 ----------------

    public void Toast(string text, ToastKind kind = ToastKind.Info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Toast(text, kind));
            return;
        }
        // 窗口尚未初始化完成时只记日志
        if (ToastLayer is null)
        {
            Log.Warn("通知未显示：" + text);
            return;
        }
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(Shape.FillProperty, kind switch
        {
            ToastKind.Success => "SuccessBrush",
            ToastKind.Error => "DangerBrush",
            _ => "AccentBrush",
        });
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, tb } };
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16, 11, 18, 11),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 10, 0, 0),
            Child = panel,
            Opacity = 0,
            RenderTransform = new TranslateTransform(30, 0),
            Effect = (Effect)FindResource("LiftShadow"),
        };
        ToastLayer.Children.Add(card);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        card.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        var life = kind == ToastKind.Error ? 6000 : 3600;
        _ = Task.Delay(life).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => ToastLayer.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
        }));
    }

    // ---------------- 对话框 ----------------

    /// <summary>
    /// 在遮罩上方显示一张卡片
    /// </summary>
    public DialogHandle ShowDialog(FrameworkElement content, double maxWidth = 480)
    {
        var dim = new Border { Opacity = 0 };
        dim.SetResourceReference(Border.BackgroundProperty, "OverlayBrush");
        var scale = new ScaleTransform(0.94, 0.94);
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            MaxWidth = maxWidth,
            Padding = new Thickness(26, 24, 26, 22),
            CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32),
            Child = content,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Effect = (Effect)FindResource("LiftShadow"),
        };
        var container = new Grid { Children = { dim, card } };
        var handle = new DialogHandle { Container = container, Owner = this };
        _dialogs.Add(handle);
        DialogLayer.Children.Add(container);
        DialogLayer.Visibility = Visibility.Visible;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        dim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        return handle;
    }

    internal void CloseDialog(DialogHandle handle)
    {
        if (handle.IsClosed) return;
        handle.IsClosed = true;
        _dialogs.Remove(handle);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            DialogLayer.Children.Remove(handle.Container);
            if (DialogLayer.Children.Count == 0) DialogLayer.Visibility = Visibility.Collapsed;
        };
        handle.Container.BeginAnimation(OpacityProperty, fade);
    }

    private StackPanel DialogBody(string title, string message, out StackPanel buttons)
    {
        var body = new StackPanel { MinWidth = 340 };
        body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("T.H2"), FontSize = 20 });
        if (!string.IsNullOrEmpty(message))
            body.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("T.Soft"), Margin = new Thickness(0, 10, 0, 0) });
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        return body;
    }

    /// <summary>
    /// 确认对话框
    /// </summary>
    public Task<bool> Confirm(string title, string message, string ok = "好的", string? cancel = "取消", bool danger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        var body = DialogBody(title, message, out var buttons);
        DialogHandle? handle = null;
        if (cancel is not null)
        {
            var c = new Button { Content = cancel, Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
            c.Click += (_, _) => { handle!.Close(); tcs.TrySetResult(false); };
            buttons.Children.Add(c);
        }
        var o = new Button { Content = ok, MinWidth = 84, Style = (Style)FindResource(danger ? "Btn.DangerFill" : "Btn.Primary"), IsDefault = true };
        o.Click += (_, _) => { handle!.Close(); tcs.TrySetResult(true); };
        buttons.Children.Add(o);
        body.Children.Add(buttons);
        handle = ShowDialog(body);
        handle.OnEscape = () => { handle.Close(); tcs.TrySetResult(false); };
        return tcs.Task;
    }

    /// <summary>
    /// 输入对话框，取消时返回 null
    /// </summary>
    public Task<string?> Prompt(string title, string message, string initial = "", string placeholder = "")
    {
        var tcs = new TaskCompletionSource<string?>();
        var body = DialogBody(title, message, out var buttons);
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 16, 0, 0) };
        Controls.Ui.SetPlaceholder(box, placeholder);
        body.Children.Add(box);
        DialogHandle? handle = null;
        var c = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        c.Click += (_, _) => { handle!.Close(); tcs.TrySetResult(null); };
        var o = new Button { Content = "确定", MinWidth = 84, Style = (Style)FindResource("Btn.Primary"), IsDefault = true };
        o.Click += (_, _) => { handle!.Close(); tcs.TrySetResult(box.Text); };
        buttons.Children.Add(c);
        buttons.Children.Add(o);
        body.Children.Add(buttons);
        handle = ShowDialog(body);
        handle.OnEscape = () => { handle.Close(); tcs.TrySetResult(null); };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return tcs.Task;
    }
}
