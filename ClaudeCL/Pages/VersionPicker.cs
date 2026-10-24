using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>
/// 选择要安装的资源版本：默认只列出兼容当前游戏版本与加载器的文件，可切换显示全部
/// </summary>
public static class VersionPicker
{
    /// <param name="target">装到哪个版本；整合包会装成新版本，传 null 时列出全部文件</param>
    /// <param name="installedSha1s">已安装文件的哈希，用来标出“已安装”</param>
    public static Task<ResourceFile?> ShowAsync(ResourceProject project, InstalledVersion? target, string? loader, ISet<string> installedSha1s)
    {
        var w = MainWindow.Instance!;
        var tcs = new TaskCompletionSource<ResourceFile?>();
        var body = new StackPanel { Width = 620 };
        body.Children.Add(new TextBlock { Text = $"安装 {project.Title}", Style = (Style)w.FindResource("T.H2"), FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock
        {
            Text = target is null ? "会安装成一个新的游戏版本，选一个整合包版本：" : $"装到 {target.Id}（{target.GameVersion}{(loader is null ? "" : " · " + loader)}），选一个版本：",
            Style = (Style)w.FindResource("T.Soft"), Margin = new Thickness(0, 6, 0, 10),
        });
        var showAll = new CheckBox { Content = "显示所有游戏版本的文件（可能和当前版本不兼容）", Margin = new Thickness(0, 0, 0, 10) };
        if (target is null) showAll.Visibility = Visibility.Collapsed;
        body.Children.Add(showAll);

        var host = new Grid { Height = 360 };
        var list = new ListBox { Style = (Style)w.FindResource("CardList") };
        var loading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        loading.Children.Add(new SparkLoader { Width = 36, Height = 36 });
        var hint = new TextBlock { Text = "正在查询版本列表……", Style = (Style)w.FindResource("T.Soft"), Margin = new Thickness(0, 10, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        loading.Children.Add(hint);
        host.Children.Add(list);
        host.Children.Add(loading);
        body.Children.Add(host);

        var warn = new TextBlock { Style = (Style)w.FindResource("T.Caption"), Margin = new Thickness(2, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        body.Children.Add(warn);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        var ok = new Button { Content = "安装所选版本", Style = (Style)w.FindResource("Btn.Primary"), IsEnabled = false };
        Ui.SetIcon(ok, "");
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        var handle = w.ShowDialog(body, 700);
        void Done(ResourceFile? f) { handle.Close(); tcs.TrySetResult(f); }
        cancel.Click += (_, _) => Done(null);
        handle.OnEscape = () => Done(null);
        ok.Click += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: ResourceFile f }) Done(f); };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: ResourceFile f }) Done(f); };
        list.SelectionChanged += (_, _) =>
        {
            ok.IsEnabled = list.SelectedItem is ListBoxItem;
            // 选了不兼容的文件时提醒一下
            if (list.SelectedItem is ListBoxItem { Tag: ResourceFile f } && target is not null && !f.GameVersions.Contains(target.GameVersion))
            {
                warn.Text = $"这个文件标注支持的游戏版本是 {string.Join("、", f.GameVersions.Take(4))}，不包含 {target.GameVersion}，装上后可能无法启动。";
                warn.Visibility = Visibility.Visible;
            }
            else warn.Visibility = Visibility.Collapsed;
        };

        CancellationTokenSource? cts = null;
        async Task LoadAsync()
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            list.Items.Clear();
            loading.Visibility = Visibility.Visible;
            hint.Text = "正在查询版本列表……";
            try
            {
                var all = showAll.IsChecked == true || target is null;
                var files = await ModrinthClient.GetFilesAsync(project.Id, all ? null : target!.GameVersion, project.ProjectType == "mod" ? loader : null, ct);
                if (ct.IsCancellationRequested) return;
                foreach (var f in files) list.Items.Add(new ListBoxItem { Tag = f, Content = Row(w, f, installedSha1s.Contains(f.Sha1 ?? "")) });
                if (files.Count == 0)
                {
                    hint.Text = all ? "这个资源还没有发布任何文件。" : $"没有适用于 {target!.GameVersion} 的版本，可以勾选上面的“显示所有游戏版本”看看。";
                    return;
                }
                // 默认选中最新的正式版
                var index = files.FindIndex(f => f.VersionType == "release");
                list.SelectedIndex = Math.Max(0, index);
                loading.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                hint.Text = "查询失败：" + ex.GetBaseException().Message;
            }
        }
        showAll.Click += async (_, _) => await LoadAsync();
        _ = LoadAsync();
        return tcs.Task;
    }

    private static FrameworkElement Row(MainWindow w, ResourceFile f, bool installed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = f.VersionName, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
        var typePill = new Border { Style = (Style)w.FindResource("Pill"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var typeText = new TextBlock { Text = f.TypeText, FontSize = 11 };
        typeText.SetResourceReference(TextBlock.ForegroundProperty, f.VersionType == "release" ? "AccentBrush" : "TextSoftBrush");
        typePill.Child = typeText;
        title.Children.Add(typePill);
        left.Children.Add(title);
        var games = f.GameVersions.Length <= 4 ? string.Join("、", f.GameVersions) : $"{string.Join("、", f.GameVersions.Take(4))} 等 {f.GameVersions.Length} 个版本";
        left.Children.Add(new TextBlock { Text = $"{f.DateText} · {f.SizeText} · {string.Join("/", f.Loaders)} · {games}", Style = (Style)w.FindResource("T.Caption"), Margin = new Thickness(0, 3, 0, 0) });
        grid.Children.Add(left);

        if (installed)
        {
            var pill = new Border { Style = (Style)w.FindResource("Pill"), VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, "SuccessSoftBrush");
            var t = new TextBlock { Text = "已安装", FontSize = 11 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            pill.Child = t;
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);
        }
        return grid;
    }
}
