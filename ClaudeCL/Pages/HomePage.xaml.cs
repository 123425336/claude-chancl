using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

public partial class HomePage : UserControl, IPage
{
    // 看板娘的台词
    private static readonly string[] Lines =
    [
        "欢迎回来～今天也要一起冒险吗？",
        "书上说，下矿之前一定要带够火把哦。",
        "下雨天最适合待在小木屋里看书了。",
        "遇到苦力怕的话……先后退三步！",
        "游戏文件我都帮你整理好啦。",
        "累了就休息一下，方块世界不会跑掉的。",
        "资源中心里有好多有趣的模组，要去看看吗？",
        "听说下界的岩浆很烫，我们要小心一点。",
        "种一片花田吧，我想要橙色的那种。",
        "今天的书签夹在第 64 页，正好是一组。",
    ];

    private static readonly string[] Quotes =
    [
        "每一个世界，都从第一块泥土开始。",
        "慢一点也没关系，好的故事值得细细读。",
        "带上地图和好奇心，剩下的路会自己出现。",
        "今天，也一起写下新的冒险篇章吧。",
    ];

    private readonly Random _random = new();
    private readonly DispatcherTimer _runningTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _updatingBox;

    public HomePage()
    {
        InitializeComponent();
        Quote.Text = Quotes[_random.Next(Quotes.Length)];
        AppState.VersionsChanged += () => Dispatcher.Invoke(RefreshVersions);
        AppState.SelectionChanged += () => Dispatcher.Invoke(RefreshVersions);
        AppState.AccountsChanged += () => Dispatcher.Invoke(RefreshAccount);
        LaunchFlow.StateChanged += () => Dispatcher.Invoke(RefreshRunning);
        _runningTimer.Tick += (_, _) => RefreshRunning();
        Loaded += (_, _) => StartFloating();
        SizeChanged += (_, e) => MascotArea.Visibility = e.NewSize.Width < 820 || !Settings.Current.ShowMascot ? Visibility.Collapsed : Visibility.Visible;
    }

    public void OnShown()
    {
        Greeting.Text = GreetingText();
        BgImage.Opacity = App.IsDark ? 0.22 : 0.85;
        MascotArea.Visibility = ActualWidth is > 0 and < 820 || !Settings.Current.ShowMascot ? Visibility.Collapsed : Visibility.Visible;
        RefreshVersions();
        RefreshAccount();
        RefreshRunning();
        Say(Lines[0]);
    }

    private string GreetingText()
    {
        var name = AccountService.Selected?.Name;
        var h = DateTime.Now.Hour;
        var hello = h switch
        {
            < 5 => "夜深了",
            < 11 => "早上好",
            < 13 => "中午好",
            < 18 => "下午好",
            < 23 => "晚上好",
            _ => "夜深了",
        };
        return name is null ? hello : $"{hello}，{name}";
    }

    private void RefreshVersions()
    {
        _updatingBox = true;
        var valid = AppState.Versions.Where(v => v.IsValid).ToList();
        VersionBox.ItemsSource = valid;
        VersionBox.SelectedItem = AppState.SelectedVersion;
        VersionBox.Visibility = valid.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoVersionHint.Visibility = valid.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        LaunchText.Text = valid.Count > 0 ? "启动游戏" : "去下载游戏";
        _updatingBox = false;
    }

    private void RefreshAccount()
    {
        var acc = AccountService.Selected;
        AccountLine.Text = acc is null ? "还没有账号，启动时会请你起一个名字" : $"以 {acc.Name} 的身份进入（{acc.KindText}）";
        Greeting.Text = GreetingText();
    }

    private void RefreshRunning()
    {
        var s = GameLauncher.Running;
        if (s is null || s.Process.HasExited)
        {
            RunningBar.Visibility = Visibility.Collapsed;
            _runningTimer.Stop();
            return;
        }
        RunningBar.Visibility = Visibility.Visible;
        var minutes = (int)(DateTime.Now - s.StartedAt).TotalMinutes;
        RunningText.Text = $"{s.Version.Id} 运行中 · 已玩 {minutes} 分钟";
        if (!_runningTimer.IsEnabled) _runningTimer.Start();
    }

    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBox || VersionBox.SelectedItem is not InstalledVersion v) return;
        AppState.Select(v);
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.SelectedVersion is null)
        {
            MainWindow.Instance!.Navigate("download");
            return;
        }
        Say("要出发了吗？我帮你把行李收拾好～");
        await LaunchFlow.RunAsync(AppState.SelectedVersion);
    }

    private void Log_Click(object sender, RoutedEventArgs e) => LaunchFlow.ShowLiveLog();

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        var s = GameLauncher.Running;
        if (s is null) return;
        if (!await MainWindow.Instance!.Confirm("要结束游戏吗？", "没有保存的进度会丢失哦。", "结束游戏", "再想想", true)) return;
        try { s.Process.Kill(true); } catch (Exception ex) { Log.Error(ex, "结束游戏失败"); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var v = AppState.SelectedVersion;
        Shell.Open(v is null ? Settings.Current.GameDir : v.RunDir(Settings.Current.GameDir));
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        var v = AppState.SelectedVersion;
        if (v is null) { MainWindow.Instance!.Toast("还没有选择版本"); return; }
        MainWindow.Instance!.ShowVersionDetail(v);
    }

    private async void CleanMem_Click(object sender, RoutedEventArgs e)
    {
        CleanMemButton.IsEnabled = false;
        Say("我来帮你把桌面收拾干净～");
        try
        {
            var (freed, full) = await MemoryCleaner.CleanAsync();
            MainWindow.Instance!.Toast(full ? $"清理完成，释放了 {Installer.FormatSize(freed)} 内存" : $"只做了基础清理（没有管理员权限），释放了 {Installer.FormatSize(freed)}",
                full ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex)
        {
            MainWindow.Instance!.Toast("清理失败：" + ex.Message, ToastKind.Error);
        }
        finally
        {
            CleanMemButton.IsEnabled = true;
        }
    }

    private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.Navigate("download");
    private void GoResources_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.Navigate("resources");

    // ---------------- 看板娘 ----------------

    private void StartFloating()
    {
        // 轻轻上下浮动，像在呼吸
        var anim = new DoubleAnimation(0, -6, TimeSpan.FromSeconds(2.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        MascotFloat.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void Mascot_Click(object sender, MouseButtonEventArgs e)
    {
        // 点一下会轻轻跳起来，并说一句话
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.97, KeyTime.FromPercent(0.25)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.02, KeyTime.FromPercent(0.6)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1)));
        MascotScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        string line;
        do line = Lines[_random.Next(Lines.Length)]; while (line == BubbleText.Text && Lines.Length > 1);
        Say(line);
    }

    private DispatcherTimer? _bubbleTimer;

    private void Say(string text)
    {
        BubbleText.Text = text;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        _bubbleTimer?.Stop();
        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _bubbleTimer.Tick += (_, _) =>
        {
            _bubbleTimer.Stop();
            Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        };
        _bubbleTimer.Start();
    }
}
