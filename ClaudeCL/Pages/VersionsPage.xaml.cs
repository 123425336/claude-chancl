using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClaudeCL.Controls;
using ClaudeCL.Core;
using Microsoft.Win32;

namespace ClaudeCL.Pages;

/// <summary>版本卡片的显示数据</summary>
public sealed class VersionItem
{
    public required InstalledVersion Version { get; init; }
    public bool IsCurrent { get; init; }
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public string Icon => Version.Loader switch
    {
        LoaderKind.Vanilla => "",
        LoaderKind.Unknown => "",
        _ => "",
    };
}

public partial class VersionsPage : UserControl, IPage
{
    public VersionsPage()
    {
        InitializeComponent();
        AppState.VersionsChanged += () => Dispatcher.Invoke(Refresh);
        AppState.SelectionChanged += () => Dispatcher.Invoke(Refresh);
    }

    public void OnShown()
    {
        Refresh();
        _ = AppState.RefreshVersionsAsync();
    }

    private void Refresh()
    {
        DirText.Text = Settings.Current.GameDir;
        var current = AppState.SelectedVersion?.Id;
        var items = AppState.Versions.Select(v => new VersionItem { Version = v, IsCurrent = v.Id == current }).ToList();
        List.ItemsSource = items;
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var skipped = VersionStore.LastSkipped.Count;
        SkippedHint.Visibility = skipped > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkippedHint.Content = $"有 {skipped} 个文件夹没有识别为游戏版本，点这里查看原因";
    }

    private static InstalledVersion? From(object sender) => (sender as FrameworkElement)?.Tag is VersionItem i ? i.Version : null;

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } v) return;
        AppState.Select(v);
        await LaunchFlow.RunAsync(v);
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } v) return;
        AppState.Select(v);
        MainWindow.Instance!.Toast($"已将 {v.Id} 设为当前版本", ToastKind.Success);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is { } v) Shell.Open(v.RunDir(Settings.Current.GameDir));
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } v) return;
        var w = MainWindow.Instance!;
        using var cts = new CancellationTokenSource();
        var dialog = ProgressDialog.Show($"正在检查 {v.Id}", cts);
        try
        {
            await Installer.CompleteAsync(Settings.Current.GameDir, v.Id, dialog.Progress, cts.Token);
            dialog.Close();
            w.Toast($"{v.Id} 的文件都完好无损", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
        }
        catch (Exception ex)
        {
            dialog.Close();
            Log.Error(ex, "补全文件失败");
            await w.Confirm("补全文件没有完成", ex.Message, "知道了", null);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is not { } v) return;
        var w = MainWindow.Instance!;
        var extra = Settings.Current.IsolateVersions ? "这个版本里的存档、模组也会一起移走。" : "";
        if (!await w.Confirm($"要删除 {v.Id} 吗？", $"文件夹会被移到回收站，之后还能找回来。{extra}", "移到回收站", "保留", true)) return;
        try
        {
            VersionStore.Delete(v);
            w.Toast($"{v.Id} 已移到回收站");
            await AppState.RefreshVersionsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除版本失败");
            w.Toast("删除失败：" + ex.Message, ToastKind.Error);
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e) => Shell.Open(Settings.Current.GameDir);

    private async void ChangeDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏目录（通常名为 .minecraft）", InitialDirectory = Settings.Current.GameDir };
        if (dialog.ShowDialog() != true) return;
        Settings.Current.GameDir = dialog.FolderName;
        Settings.Save();
        await AppState.RefreshVersionsAsync();
        MainWindow.Instance!.Toast("游戏目录已更换", ToastKind.Success);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = AppState.RefreshVersionsAsync();

    private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.Navigate("download");

    /// <summary>
    /// 列出没有识别为版本的文件夹和原因，方便排查“少了一个版本”
    /// </summary>
    private void Skipped_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        var body = new StackPanel { Width = 600 };
        body.Children.Add(new TextBlock { Text = "这些文件夹没有识别为游戏版本", Style = (Style)w.FindResource("T.H2"), FontSize = 20 });
        body.Children.Add(new TextBlock
        {
            Text = "每个版本文件夹里需要有一个版本信息文件（通常是“文件夹名.json”）。如果是别的启动器装了一半或者删了一半，就会出现这种情况。",
            Style = (Style)w.FindResource("T.Soft"), Margin = new Thickness(0, 8, 0, 12),
        });
        var text = string.Join(Environment.NewLine + Environment.NewLine, VersionStore.LastSkipped.Select(s => $"{s.Name}\n  {s.Reason}"));
        body.Children.Add(new TextBox { Text = text, Style = (Style)w.FindResource("TextBox.Code"), MaxHeight = 300 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "复制", Margin = new Thickness(0, 0, 10, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(text); w.Toast("已复制", ToastKind.Success); } catch { } };
        var open = new Button { Content = "打开 versions 文件夹", Margin = new Thickness(0, 0, 10, 0) };
        open.Click += (_, _) => Shell.Open(VersionStore.VersionsDir(Settings.Current.GameDir));
        var close = new Button { Content = "知道了", Style = (Style)w.FindResource("Btn.Primary"), MinWidth = 84 };
        buttons.Children.Add(copy);
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        body.Children.Add(buttons);
        var handle = w.ShowDialog(body, 680);
        close.Click += (_, _) => handle.Close();
        handle.OnEscape = handle.Close;
    }

    // ---------------- 详情（模组与加载器） ----------------

    private void Mods_Click(object sender, RoutedEventArgs e)
    {
        if (From(sender) is { } v) MainWindow.Instance!.ShowVersionDetail(v);
    }

    private void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (From(sender) is { IsValid: true } v) MainWindow.Instance!.ShowVersionDetail(v);
    }
}