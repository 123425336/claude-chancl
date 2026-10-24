using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

public partial class ResourcesPage : UserControl, IPage
{
    private readonly ObservableCollection<ResourceProject> _items = new();
    private CancellationTokenSource? _cts;
    private int _offset;
    private int _total;
    private bool _searched;

    public ResourcesPage()
    {
        InitializeComponent();
        Results.ItemsSource = _items;
        AppState.VersionsChanged += () => Dispatcher.Invoke(RefreshTargets);
    }

    private string ProjectType => PackChip.IsChecked == true ? "resourcepack" : ShaderChip.IsChecked == true ? "shader" : "mod";
    private InstalledVersion? Target => TargetBox.SelectedItem as InstalledVersion;

    public void OnShown()
    {
        RefreshTargets();
        if (!_searched) _ = SearchAsync(false);
    }

    private void RefreshTargets()
    {
        var current = Target?.Id ?? AppState.SelectedVersion?.Id;
        var list = AppState.Versions.Where(v => v.IsValid).ToList();
        TargetBox.ItemsSource = list;
        TargetBox.SelectedItem = list.FirstOrDefault(v => v.Id == current) ?? list.FirstOrDefault();
        NoTargetHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Type_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _ = SearchAsync(false);
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _searched) _ = SearchAsync(false);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync(false);
    }

    private string _lastQuery = "";

    /// <summary>
    /// 清空搜索框后自动回到默认列表
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && SearchBox.Text.Length == 0 && _lastQuery.Length > 0) _ = SearchAsync(false);
    }

    private void More_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(true);

    private async Task SearchAsync(bool more)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _searched = true;
        if (!more)
        {
            _offset = 0;
            _items.Clear();
            Scroller.ScrollToTop();
            LoadingPanel.Visibility = Visibility.Visible;
        }
        EmptyPanel.Visibility = Visibility.Collapsed;
        MoreButton.IsEnabled = false;
        try
        {
            var target = Target;
            _lastQuery = SearchBox.Text.Trim();
            var (items, total) = await ModrinthClient.SearchAsync(SearchBox.Text.Trim(), ProjectType,
                target?.GameVersion, target?.ModLoaderName, _offset, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var i in items) _items.Add(i);
            _offset += items.Count;
            _total = total;
            if (_items.Count == 0)
            {
                EmptyText.Text = target is null
                    ? "没有找到相关的资源，换个关键词试试？"
                    : $"没有找到适用于 {target.GameVersion}{(target.ModLoaderName is null ? "" : " · " + target.LoaderText)} 的结果，换个关键词试试？";
                if (ProjectType == "mod" && target?.ModLoaderName is null && target is not null)
                    EmptyText.Text += "\n（当前版本没有安装模组加载器，可以先在「下载游戏」里安装 Fabric 版本）";
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "搜索资源失败");
            EmptyText.Text = "资源库暂时连不上……稍后再试试吧。\n" + ex.Message;
            EmptyPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                MoreButton.IsEnabled = true;
                MoreButton.Visibility = _offset < _total && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ---------------- 模组更新 ----------------

    private (InstalledVersion Version, ModEntry Mod, IReadOnlyCollection<ModEntry> FolderMods, string Slug)? _pending;

    /// <summary>
    /// 从模组管理页跳过来：显示这个模组的更新信息，并在下方列出这个模组
    /// </summary>
    public async void ShowUpdate(InstalledVersion version, ModEntry mod, IReadOnlyCollection<ModEntry> folderMods)
    {
        var info = mod.Update!;
        _pending = (version, mod, folderMods, info.ProjectId);
        RefreshTargets();
        TargetBox.SelectedItem = AppState.Versions.FirstOrDefault(v => v.Id == version.Id && v.IsValid) ?? TargetBox.SelectedItem;
        ModChip.IsChecked = true;
        BannerTitle.Text = $"{mod.Name} 有新版本";
        BannerCurrent.Text = $"当前 {info.CurrentVersion}";
        BannerLatest.Text = $"最新 {info.Latest.VersionName}";
        BannerFile.Text = $"{info.Latest.FileName} · {Installer.FormatSize(info.Latest.Size)} · 发布于 {info.Latest.Published:yyyy 年 M 月 d 日}";
        BannerChangelog.Text = string.IsNullOrWhiteSpace(info.Changelog) ? "（作者没有写更新说明）" : info.Changelog;
        BannerIcon.Source = mod.Icon;
        BannerUpdateButton.IsEnabled = true;
        BannerUpdateButton.Content = "更新并替换旧版本";
        UpdateBanner.Visibility = Visibility.Visible;
        try
        {
            // 补充平台上的名称与图标，并在下方搜索出这个模组
            var project = await ModrinthClient.GetProjectAsync(info.ProjectId, CancellationToken.None);
            if (_pending?.Mod != mod) return;
            _pending = (version, mod, folderMods, string.IsNullOrEmpty(project.Slug) ? project.Id : project.Slug);
            if (!string.IsNullOrEmpty(project.IconUrl)) BannerIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(project.IconUrl));
            SearchBox.Text = project.Title;
            await SearchAsync(false);
        }
        catch (Exception ex)
        {
            Log.Warn("获取模组信息失败：" + ex.Message);
        }
    }

    private async void BannerUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } p) return;
        var w = MainWindow.Instance!;
        if (GameLauncher.Running?.Version.Id == p.Version.Id)
        {
            w.Toast("请先关闭正在运行的这个版本，模组文件被占用时无法替换", ToastKind.Error);
            return;
        }
        BannerUpdateButton.IsEnabled = false;
        BannerUpdateButton.Content = "更新中……";
        try
        {
            var name = p.Mod.Name;
            var latest = p.Mod.Update!.Latest.VersionName;
            var deps = await ModUpdater.ApplyAsync(p.Mod, p.Version, p.FolderMods, CancellationToken.None);
            w.Toast(deps.Count == 0 ? $"{name} 已更新到 {latest}" : $"{name} 已更新到 {latest}，还补装了 {deps.Count} 个前置", ToastKind.Success);
            UpdateBanner.Visibility = Visibility.Collapsed;
            _pending = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新模组失败");
            w.Toast("更新失败：" + ex.Message, ToastKind.Error);
            BannerUpdateButton.IsEnabled = true;
            BannerUpdateButton.Content = "更新并替换旧版本";
        }
    }

    private void BannerDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is { } p) Shell.Open($"https://modrinth.com/mod/{p.Slug}");
    }

    private void BannerClose_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        _pending = null;
    }

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ResourceProject p })
            Shell.Open($"https://modrinth.com/{(p.ProjectType == "resourcepack" ? "resourcepack" : p.ProjectType)}/{(string.IsNullOrEmpty(p.Slug) ? p.Id : p.Slug)}");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        if (sender is not Button { Tag: ResourceProject project } button) return;
        var target = Target;
        if (target is null)
        {
            w.Toast("先安装一个游戏版本，才能往里面放资源哦");
            return;
        }
        var type = project.ProjectType;
        // 资源包和光影不区分加载器
        var loader = type == "mod" ? target.ModLoaderName : null;
        if (type == "mod" && loader is null)
        {
            w.Toast($"{target.Id} 没有安装模组加载器，模组装进去也不会生效", ToastKind.Error);
            return;
        }

        button.IsEnabled = false;
        var runDir = target.RunDir(Settings.Current.GameDir);
        try
        {
            var files = await ModrinthClient.GetFilesAsync(project.Id, target.GameVersion, loader, CancellationToken.None);
            var file = files.FirstOrDefault();
            if (file is null)
            {
                w.Toast($"{project.Title} 还没有适用于 {target.GameVersion} 的版本", ToastKind.Error);
                return;
            }
            await ModrinthClient.DownloadAsync(file, type, runDir, CancellationToken.None);

            // 顺带安装必需的前置模组
            var deps = new List<string>();
            foreach (var dep in file.RequiredProjects)
            {
                try
                {
                    var depFiles = await ModrinthClient.GetFilesAsync(dep, target.GameVersion, loader, CancellationToken.None);
                    if (depFiles.FirstOrDefault() is { } df)
                    {
                        await ModrinthClient.DownloadAsync(df, "mod", runDir, CancellationToken.None);
                        deps.Add(Path.GetFileNameWithoutExtension(df.FileName));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"前置 {dep} 安装失败：{ex.Message}");
                }
            }
            w.Toast(deps.Count == 0
                ? $"{project.Title} 已装进 {target.Id}"
                : $"{project.Title} 已装进 {target.Id}，还顺手装好了 {deps.Count} 个前置", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "安装资源失败");
            w.Toast($"安装 {project.Title} 失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
