using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

public partial class ToolsPage : UserControl, IPage
{
    private CancellationTokenSource? _pingCts;

    private readonly System.Windows.Threading.DispatcherTimer _memTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ToolsPage()
    {
        InitializeComponent();
        AddressBox.Text = Settings.Current.LastServer;
        // 页面可见时每两秒刷新一次内存占用
        _memTimer.Tick += (_, _) => UpdateMemory();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateMemory(); _memTimer.Start(); }
            else _memTimer.Stop();
        };
    }

    public void OnShown()
    {
        ShowJava();
        UpdateMemory();
    }

    // ---------------- 内存清理 ----------------

    private void UpdateMemory()
    {
        var m = MemoryCleaner.Status();
        if (m.TotalPhys == 0) return;
        MemRing.AnimateTo(m.UsedRatio);
        MemPercent.Text = $"{m.UsedRatio * 100:0}%";
        MemDetail.Text = $"物理内存已用 {Installer.FormatSize((long)m.UsedPhys)}，共 {Installer.FormatSize((long)m.TotalPhys)}，剩余 {Installer.FormatSize((long)m.AvailPhys)}";
        CommitDetail.Text = $"提交内存（含虚拟内存）已用 {Installer.FormatSize((long)m.UsedCommit)} / {Installer.FormatSize((long)m.TotalCommit)}";
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        CleanButton.IsEnabled = false;
        CleanButton.Content = "清理中……";
        try
        {
            var (freed, full) = await MemoryCleaner.CleanAsync();
            UpdateMemory();
            w.Toast(full
                ? $"清理完成，释放了 {Installer.FormatSize(freed)} 内存"
                : $"没有拿到管理员权限，只做了基础清理，释放了 {Installer.FormatSize(freed)}", full ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "内存清理失败");
            w.Toast("清理失败：" + ex.Message, ToastKind.Error);
        }
        finally
        {
            CleanButton.IsEnabled = true;
            CleanButton.Content = "一键清理";
        }
    }

    private async void Hung_Click(object sender, RoutedEventArgs e)
    {
        HungButton.IsEnabled = false;
        var list = await Task.Run(MemoryCleaner.FindHung);
        HungButton.IsEnabled = true;
        HungPanel.Children.Clear();
        HungPanel.Visibility = Visibility.Visible;
        if (list.Count == 0)
        {
            HungPanel.Children.Add(new TextBlock { Text = "现在没有未响应的程序，大家都很精神～", Style = (Style)FindResource("T.Soft") });
            return;
        }
        HungPanel.Children.Add(new TextBlock { Text = $"找到 {list.Count} 个未响应的程序。结束它们会丢失其中没保存的内容，请确认后再操作：", Style = (Style)FindResource("T.Soft"), Margin = new Thickness(0, 0, 0, 8) });
        foreach (var h in list)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var kill = new Button { Content = "结束", Style = (Style)FindResource("Btn.Danger"), Padding = new Thickness(14, 5, 14, 5) };
            DockPanel.SetDock(kill, Dock.Right);
            kill.Click += async (_, _) =>
            {
                if (!await MainWindow.Instance!.Confirm($"结束 {h.Name}？", $"「{h.Title}」没有响应。结束后其中没保存的内容会丢失。", "结束", "再等等", true)) return;
                try
                {
                    MemoryCleaner.Kill(h);
                    HungPanel.Children.Remove(row);
                    MainWindow.Instance!.Toast($"已结束 {h.Name}", ToastKind.Success);
                }
                catch (Exception ex)
                {
                    MainWindow.Instance!.Toast("结束失败：" + ex.Message, ToastKind.Error);
                }
            };
            row.Children.Add(kill);
            row.Children.Add(new TextBlock
            {
                Text = $"{h.Name}（{h.Title}）· 占用 {Installer.FormatSize(h.WorkingSet)}",
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            HungPanel.Children.Add(row);
        }
    }

    // ---------------- 服务器状态 ----------------

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = PingAsync();
    }

    private void Ping_Click(object sender, RoutedEventArgs e) => _ = PingAsync();

    private async Task PingAsync()
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) return;
        Settings.Current.LastServer = address;
        Settings.Save();

        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var ct = _pingCts.Token;
        PingResult.Visibility = Visibility.Collapsed;
        PingError.Visibility = Visibility.Collapsed;
        PingLoading.Visibility = Visibility.Visible;
        PingButton.IsEnabled = false;
        try
        {
            var r = await ServerStatus.PingAsync(address, ct);
            if (r is null) throw new InvalidOperationException("服务器没有回应");
            Motd.Text = ServerStatus.StripFormatting(r.Description).Trim();
            Players.Text = $"在线 {r.Players.Online} / {r.Players.Max}";
            Latency.Text = $"延迟 {r.Latency} ms";
            ServerVersion.Text = ServerStatus.StripFormatting(r.Version.Name);
            Favicon.Source = null;
            if (ServerStatus.DecodeFavicon(r.Favicon) is { } bytes)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                Favicon.Source = bmp;
            }
            PingResult.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PingError.Text = "没能连上这个服务器：" + (ex.InnerException?.Message ?? ex.Message);
            PingError.Visibility = Visibility.Visible;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                PingLoading.Visibility = Visibility.Collapsed;
                PingButton.IsEnabled = true;
            }
        }
    }

    // ---------------- Java ----------------

    private void ShowJava()
    {
        JavaList.ItemsSource = JavaFinder.Installed;
        JavaEmpty.Visibility = JavaFinder.Installed.Count == 0 && JavaLoading.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ScanJava_Click(object sender, RoutedEventArgs e)
    {
        JavaLoading.Visibility = Visibility.Visible;
        JavaEmpty.Visibility = Visibility.Collapsed;
        try { await JavaFinder.ScanAsync(); }
        catch (Exception ex) { Log.Error(ex, "扫描 Java 失败"); }
        JavaLoading.Visibility = Visibility.Collapsed;
        ShowJava();
        MainWindow.Instance!.Toast($"找到了 {JavaFinder.Installed.Count} 个 Java", ToastKind.Success);
    }

    // ---------------- 文件夹 ----------------

    private static string RunDir => AppState.SelectedVersion?.RunDir(Settings.Current.GameDir) ?? Settings.Current.GameDir;

    private void OpenGame_Click(object sender, RoutedEventArgs e) => Shell.Open(Settings.Current.GameDir);
    private void OpenSaves_Click(object sender, RoutedEventArgs e) => Shell.Open(Path.Combine(RunDir, "saves"));
    private void OpenShots_Click(object sender, RoutedEventArgs e) => Shell.Open(Path.Combine(RunDir, "screenshots"));
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.LogDir);
}
