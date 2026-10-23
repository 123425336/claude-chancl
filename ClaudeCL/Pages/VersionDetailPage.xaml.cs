using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>
/// 单个版本的详情：管理模组、切换加载器
/// </summary>
public partial class VersionDetailPage : UserControl, IPage
{
    private InstalledVersion _version;
    private List<ModEntry> _mods = [];
    private CancellationTokenSource? _swCts;
    private bool _loaderTabLoaded;

    public VersionDetailPage(InstalledVersion version)
    {
        _version = version;
        InitializeComponent();
    }

    private string RunDir => _version.RunDir(Settings.Current.GameDir);
    private string ModsDir => Path.Combine(RunDir, "mods");

    public void OnShown()
    {
        // 重新读取，加载器可能刚被切换过
        _version = VersionStore.Load(Settings.Current.GameDir, _version.Id);
        TitleText.Text = _version.Id;
        SubText.Text = _version.Summary;
        CurrentLoaderText.Text = _version.Loader == LoaderKind.Vanilla ? "原版（没有模组加载器）" : _version.LoaderText;
        GameVersionText.Text = $"游戏版本 {_version.GameVersion}";
        _ = LoadModsAsync();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // 初始化时控件还没创建完
        if (ModsPanel is null || LoaderPanel is null) return;
        var mods = ModsTab.IsChecked == true;
        ModsPanel.Visibility = mods ? Visibility.Visible : Visibility.Collapsed;
        LoaderPanel.Visibility = mods ? Visibility.Collapsed : Visibility.Visible;
        if (!mods && !_loaderTabLoaded)
        {
            _loaderTabLoaded = true;
            // 默认选中当前加载器
            (_version.Loader switch
            {
                LoaderKind.Forge => SwForge,
                LoaderKind.NeoForge => SwNeoForge,
                LoaderKind.Fabric => SwFabric,
                LoaderKind.Quilt => SwQuilt,
                _ => SwVanilla,
            }).IsChecked = true;
            SwLoader_Checked(this, e);
        }
    }

    // ---------------- 模组 ----------------

    private async Task LoadModsAsync()
    {
        ModsLoading.Visibility = Visibility.Visible;
        ModsEmpty.Visibility = Visibility.Collapsed;
        ModList.ItemsSource = null;
        var dir = ModsDir;
        try
        {
            _mods = await Task.Run(() => ModScanner.Scan(dir));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取模组列表失败");
            _mods = [];
        }
        ModsLoading.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        IEnumerable<ModEntry> list = _mods;
        if (OnChip.IsChecked == true) list = list.Where(m => m.Enabled);
        if (OffChip.IsChecked == true) list = list.Where(m => !m.Enabled);
        if (keyword.Length > 0)
            list = list.Where(m => m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                   || m.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                   || m.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var result = list.ToList();
        ModList.ItemsSource = result;

        var on = _mods.Count(m => m.Enabled);
        SubText.Text = _mods.Count == 0 ? _version.Summary : $"{_version.Summary} · {_mods.Count} 个模组，启用 {on} 个";
        if (result.Count == 0)
        {
            ModsEmptyText.Text = _mods.Count > 0
                ? "没有符合条件的模组。"
                : _version.ModLoaderName is null
                    ? "这个版本没有模组加载器，模组放进来也不会生效。\n可以在「加载器」里给它装一个。"
                    : "还没有模组呢，点右上角「添加模组」去资源中心挑几个吧。";
            ModsEmpty.Visibility = Visibility.Visible;
        }
        else ModsEmpty.Visibility = Visibility.Collapsed;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ModEntry mod } box) return;
        try
        {
            ModScanner.SetEnabled(mod, box.IsChecked == true);
        }
        catch (Exception ex)
        {
            MainWindow.Instance!.Toast("切换失败（游戏运行时文件会被占用）：" + ex.Message, ToastKind.Error);
        }
        box.IsChecked = mod.Enabled;
        // 筛选状态下切换后需要重新筛选
        if (OnChip.IsChecked == true || OffChip.IsChecked == true) ApplyFilter();
        else SubText.Text = $"{_version.Summary} · {_mods.Count} 个模组，启用 {_mods.Count(m => m.Enabled)} 个";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModEntry mod) return;
        var w = MainWindow.Instance!;
        if (!await w.Confirm($"删除 {mod.Name}？", "文件会被移到回收站。", "移到回收站", "保留", true)) return;
        try
        {
            Shell.MoveToRecycleBin(mod.Path);
            _mods.Remove(mod);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            w.Toast("删除失败：" + ex.Message, ToastKind.Error);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadModsAsync();

    private void AddMods_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        AppState.Select(_version);
        w.Navigate("resources");
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => Shell.Open(RunDir);

    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.Navigate("home");

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        AppState.Select(_version);
        await LaunchFlow.RunAsync(_version);
    }

    // ---------------- 加载器 ----------------

    private LoaderType? SwLoader =>
        SwForge.IsChecked == true ? LoaderType.Forge :
        SwNeoForge.IsChecked == true ? LoaderType.NeoForge :
        SwFabric.IsChecked == true ? LoaderType.Fabric :
        SwQuilt.IsChecked == true ? LoaderType.Quilt : null;

    private async void SwLoader_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaderTabLoaded) return;
        _swCts?.Cancel();
        _swCts = new CancellationTokenSource();
        var ct = _swCts.Token;
        var loader = SwLoader;
        SwVersionBox.ItemsSource = null;
        SwVersionBox.Visibility = loader is null ? Visibility.Collapsed : Visibility.Visible;
        SwitchButton.IsEnabled = false;
        if (loader is null)
        {
            SwHint.Text = _version.Loader == LoaderKind.Vanilla ? "现在就是原版。" : "会去掉模组加载器，变回原版。";
            SwitchButton.IsEnabled = _version.Loader != LoaderKind.Vanilla;
            return;
        }
        var name = LoaderService.DisplayName(loader.Value);
        SwHint.Text = $"正在查询 {name} 版本……";
        try
        {
            var list = await LoaderService.GetVersionsAsync(loader.Value, _version.GameVersion, ct);
            if (ct.IsCancellationRequested) return;
            if (list.Count == 0)
            {
                SwHint.Text = $"{name} 暂时还不支持 {_version.GameVersion}。";
                return;
            }
            // 标出当前正在使用的版本
            var isSame = LoaderService.FromKind(_version.Loader) == loader;
            var items = list.Select(l =>
            {
                var current = isSame && ForgeShort(l.Version) == _version.LoaderVersion;
                return current ? l with { Display = l.Display + "（当前）" } : l;
            }).ToList();
            SwVersionBox.ItemsSource = items;
            SwVersionBox.SelectedItem = items.FirstOrDefault(l => l.Display.EndsWith("（当前）"))
                                        ?? items.FirstOrDefault(l => l.Recommended) ?? items[0];
            SwHint.Text = isSame ? $"选一个 {name} 版本，切换后原来的文件会被替换。" : $"会把这个版本换成 {name}。";
            SwitchButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SwHint.Text = $"查询 {name} 版本失败：{ex.GetBaseException().Message}";
        }
    }

    private string ForgeShort(string full) => ForgeInstaller.ShortVersion(_version.GameVersion, full);

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        var loader = SwLoader;
        var target = SwVersionBox.SelectedItem as LoaderVersion;
        if (loader is not null && target is null) return;
        if (target?.Display.EndsWith("（当前）") == true)
        {
            w.Toast("已经是这个版本啦");
            return;
        }
        var targetText = loader is null ? "原版" : $"{LoaderService.DisplayName(loader.Value)} {ForgeShort(target!.Version)}";
        if (GameLauncher.Running?.Version.Id == _version.Id)
        {
            w.Toast("请先关闭正在运行的这个版本", ToastKind.Error);
            return;
        }
        if (!await w.Confirm($"把 {_version.Id} 切换为 {targetText}？", "存档、设置和 mods 文件夹都会保留。", "切换", "取消")) return;

        using var cts = new CancellationTokenSource();
        var dialog = ProgressDialog.Show($"正在切换到 {targetText}", cts);
        try
        {
            var gameDir = Settings.Current.GameDir;
            var entry = await Installer.FindAsync(_version.GameVersion, cts.Token);
            await LoaderService.InstallAsync(gameDir, entry, loader, target?.Version, null, _version.Id, dialog.Progress, cts.Token);
            dialog.Close();
            await AppState.RefreshVersionsAsync();
            w.Toast($"{_version.Id} 已切换为 {targetText}", ToastKind.Success);
            _loaderTabLoaded = false;
            OnShown();
            _loaderTabLoaded = true;
            SwLoader_Checked(this, e);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            w.Toast("已取消切换");
        }
        catch (Exception ex)
        {
            dialog.Close();
            Log.Error(ex, "切换加载器失败");
            await w.Confirm("切换没有完成", ex.Message, "知道了", null);
        }
    }
}
