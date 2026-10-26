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

    private string ProjectType => PackChip.IsChecked == true ? "resourcepack" : ShaderChip.IsChecked == true ? "shader"
        : ModpackChip.IsChecked == true ? "modpack" : "mod";
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
        NoTargetHint.Visibility = list.Count == 0 && ProjectType != "modpack" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Type_Changed(object sender, RoutedEventArgs e)
    {
        // 初始化时控件还没创建完
        if (TargetBox is null || ModpackHint is null || NoTargetHint is null) return;
        // 整合包会装成新版本，不需要选目标版本
        var modpack = ProjectType == "modpack";
        TargetBox.Visibility = modpack ? Visibility.Collapsed : Visibility.Visible;
        ModpackHint.Visibility = modpack ? Visibility.Visible : Visibility.Collapsed;
        NoTargetHint.Visibility = !modpack && TargetBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 页面还没显示时，等显示后再搜索
        if (IsLoaded || _searched) _ = SearchAsync(false);
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
            // 整合包不按已安装的版本筛选
            var target = ProjectType == "modpack" ? null : Target;
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
        if (project.ProjectType == "modpack")
        {
            button.IsEnabled = false;
            try { await ImportFlow.InstallFromPlatformAsync(project); }
            finally { button.IsEnabled = true; }
            return;
        }
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
        var folder = Path.Combine(runDir, type switch { "resourcepack" => "resourcepacks", "shader" => "shaderpacks", _ => "mods" });
        try
        {
            // 先算出文件夹里已有文件的哈希：标出“已安装”的版本，以及找到同一个模组的旧版本
            var local = await Task.Run(() =>
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!Directory.Exists(folder)) return map;
                foreach (var f in Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    try { map[f] = ModUpdater.Sha1(f); } catch (IOException) { /* 被占用时跳过 */ }
                }
                return map;
            });
            button.IsEnabled = true;
            var picked = await VersionPicker.ShowAsync(project, target, loader, local.Values.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (picked is null) return;
            button.IsEnabled = false;
            if (picked.Sha1 is not null && local.ContainsValue(picked.Sha1))
            {
                w.Toast($"{project.Title} {picked.VersionName} 已经装好了");
                return;
            }

            // 同一个模组的其他版本（装新版本后移到回收站，避免两个版本同时加载）
            var oldFiles = new List<string>();
            var present = new HashSet<string>();
            if (type == "mod" && local.Count > 0 && Settings.Current.AllowOnlineModLookup)
            {
                try
                {
                    var lookup = await ModrinthClient.LookupAsync(local.Values.Distinct().ToList(), CancellationToken.None);
                    present.UnionWith(lookup.Values.Select(v => v.ProjectId));
                    oldFiles.AddRange(local.Where(kv => lookup.TryGetValue(kv.Value, out var info) && info.ProjectId == project.Id).Select(kv => kv.Key));
                }
                catch (Exception ex) { Log.Warn("识别已安装模组失败：" + ex.Message); }
            }

            await Downloader.RunAsync([new DlItem(Path.Combine(folder, picked.FileName), Net.Candidates(picked.Url), picked.Sha1, picked.Size)], null, CancellationToken.None);
            foreach (var old in oldFiles.Where(f => !string.Equals(Path.GetFileName(f), picked.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                try { Shell.MoveToRecycleBin(old); }
                catch (Exception ex) { Log.Warn($"移除旧版本 {old} 失败：{ex.Message}"); }
            }

            // 递归安装必需的前置（前置的前置也一起）
            var deps = 0;
            if (type == "mod" && picked.RequiredProjects.Length > 0)
            {
                present.Add(project.Id);
                var plans = await DependencyChecker.ResolveProjectsAsync(picked.RequiredProjects, project.Title, target, present, CancellationToken.None);
                foreach (var plan in plans)
                {
                    try
                    {
                        await Downloader.RunAsync([new DlItem(Path.Combine(folder, plan.File.FileName), Net.Candidates(plan.File.Url), plan.File.Sha1, plan.File.Size)], null, CancellationToken.None);
                        deps++;
                    }
                    catch (Exception ex) { Log.Warn($"前置 {plan.Title} 安装失败：{ex.Message}"); }
                }
            }

            var msg = $"{project.Title} {picked.VersionName} 已装进 {target.Id}";
            if (oldFiles.Count > 0) msg += "，替换了旧版本";
            if (deps > 0) msg += $"，还装好了 {deps} 个前置";
            w.Toast(msg, ToastKind.Success);
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