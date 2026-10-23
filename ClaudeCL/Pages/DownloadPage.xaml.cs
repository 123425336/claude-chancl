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

public partial class DownloadPage : UserControl, IPage
{
    private List<McVersionEntry> _all = [];
    private bool _loaded;
    private CancellationTokenSource? _loaderCts;
    private CancellationTokenSource? _optiCts;
    // 用户手动改过名称后，不再自动覆盖
    private bool _nameEdited;
    private bool _settingName;

    public DownloadPage()
    {
        InitializeComponent();
        NameBox.TextChanged += (_, _) => { if (!_settingName) _nameEdited = true; };
    }

    public void OnShown()
    {
        if (!_loaded) _ = LoadAsync(false);
    }

    private McVersionEntry? Selected => VersionList.SelectedItem as McVersionEntry;

    private LoaderType? SelectedLoader =>
        ForgeChip.IsChecked == true ? LoaderType.Forge :
        NeoForgeChip.IsChecked == true ? LoaderType.NeoForge :
        FabricChip.IsChecked == true ? LoaderType.Fabric :
        QuiltChip.IsChecked == true ? LoaderType.Quilt : null;

    private async Task LoadAsync(bool force)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        VersionList.Visibility = Visibility.Collapsed;
        try
        {
            _all = await Installer.GetManifestAsync(force);
            _loaded = true;
            ApplyFilter();
            VersionList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取版本列表失败");
            ErrorText.Text = "版本目录暂时打不开……检查一下网络，或者在设置里换个下载源试试。";
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(true);

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        IEnumerable<McVersionEntry> list = _all;
        if (keyword.Length > 0)
            list = list.Where(v => v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        else if (ReleaseChip.IsChecked == true)
            list = list.Where(v => v.Type == "release");
        else if (SnapshotChip.IsChecked == true)
            list = list.Where(v => v.Type == "snapshot");
        else
            list = list.Where(v => v.Type.StartsWith("old_"));
        VersionList.ItemsSource = list.ToList();
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } v) return;
        DetailTitle.Text = v.Id;
        DetailSub.Text = $"{v.TypeText} · 发布于 {v.ReleaseTime:yyyy 年 M 月 d 日}";
        InstallForm.IsEnabled = true;
        _nameEdited = false;
        UpdateName();
        if (SelectedLoader is not null) _ = LoadLoaderVersionsAsync(v);
        if (OptiFineCheck.IsChecked == true) _ = LoadOptiFineAsync(v);
    }

    private void Loader_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var loader = SelectedLoader;
        LoaderPanel.Visibility = loader is null ? Visibility.Collapsed : Visibility.Visible;
        // OptiFine 只能单独安装或作为 Forge 的模组
        var optiOk = loader is null or LoaderType.Forge;
        OptiFineCheck.IsEnabled = optiOk;
        if (!optiOk && OptiFineCheck.IsChecked == true)
        {
            OptiFineCheck.IsChecked = false;
            OptiFinePanel.Visibility = Visibility.Collapsed;
        }
        OptiFineCheck.ToolTip = optiOk ? null : "OptiFine 只能单独安装，或与 Forge 一起安装";
        UpdateName();
        UpdateInstallButton();
        if (loader is not null && Selected is { } v) _ = LoadLoaderVersionsAsync(v);
    }

    private void LoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateName(); UpdateInstallButton(); }
    private void OptiFineBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateName(); UpdateInstallButton(); }

    private void OptiFine_Click(object sender, RoutedEventArgs e)
    {
        var on = OptiFineCheck.IsChecked == true;
        OptiFinePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateName();
        UpdateInstallButton();
        if (on && Selected is { } v) _ = LoadOptiFineAsync(v);
    }

    private void UpdateName()
    {
        if (Selected is not { } v || _nameEdited) return;
        _settingName = true;
        NameBox.Text = LoaderService.SuggestName(v.Id, SelectedLoader, (LoaderBox.SelectedItem as LoaderVersion)?.Version, OptiFineCheck.IsChecked == true);
        _settingName = false;
    }

    private void UpdateInstallButton()
    {
        var loaderReady = SelectedLoader is null || LoaderBox.SelectedItem is LoaderVersion;
        var optiReady = OptiFineCheck.IsChecked != true || OptiFineBox.SelectedItem is OptiFineVersion;
        InstallButton.IsEnabled = loaderReady && optiReady;
    }

    private async Task LoadLoaderVersionsAsync(McVersionEntry v)
    {
        _loaderCts?.Cancel();
        _loaderCts = new CancellationTokenSource();
        var ct = _loaderCts.Token;
        var loader = SelectedLoader!.Value;
        var name = LoaderService.DisplayName(loader);
        LoaderBox.ItemsSource = null;
        LoaderHint.Text = $"正在查询可用的 {name} 版本……";
        UpdateInstallButton();
        try
        {
            var list = await LoaderService.GetVersionsAsync(loader, v.Id, ct);
            if (ct.IsCancellationRequested) return;
            if (list.Count == 0)
            {
                LoaderHint.Text = $"{name} 暂时还不支持 {v.Id}。";
                return;
            }
            LoaderBox.ItemsSource = list;
            LoaderBox.SelectedItem = list.FirstOrDefault(l => l.Recommended) ?? list[0];
            LoaderHint.Text = $"共 {list.Count} 个版本，默认选中推荐版本。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LoaderHint.Text = $"查询 {name} 版本失败：{ex.GetBaseException().Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                UpdateName();
                UpdateInstallButton();
            }
        }
    }

    private async Task LoadOptiFineAsync(McVersionEntry v)
    {
        _optiCts?.Cancel();
        _optiCts = new CancellationTokenSource();
        var ct = _optiCts.Token;
        OptiFineBox.ItemsSource = null;
        OptiFineHint.Text = "正在查询 OptiFine 版本……";
        UpdateInstallButton();
        try
        {
            var list = await OptiFineInstaller.GetVersionsAsync(v.Id, ct);
            if (ct.IsCancellationRequested) return;
            if (list.Count == 0)
            {
                OptiFineHint.Text = $"OptiFine 暂时还不支持 {v.Id}。";
                return;
            }
            OptiFineBox.ItemsSource = list;
            OptiFineBox.SelectedIndex = 0;
            OptiFineHint.Text = SelectedLoader == LoaderType.Forge
                ? "会作为模组放进这个版本的 mods 文件夹。"
                : "会直接打进原版里，不需要模组加载器。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OptiFineHint.Text = "查询 OptiFine 失败（它只能通过国内镜像下载）：" + ex.GetBaseException().Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) UpdateInstallButton();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        if (Selected is not { } v) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            w.Toast("版本名称不能为空，也不能包含 \\ / : * ? \" < > | 这些字符", ToastKind.Error);
            return;
        }
        var gameDir = Settings.Current.GameDir;
        if (File.Exists(Path.Combine(VersionStore.VersionsDir(gameDir), name, name + ".json")) &&
            !await w.Confirm("这个名字已经被占用了", $"已经有一个叫 {name} 的版本，要覆盖它的版本信息吗？存档和模组不会被删除。", "覆盖", "换个名字"))
            return;

        var loader = SelectedLoader;
        var loaderVersion = (LoaderBox.SelectedItem as LoaderVersion)?.Version;
        var optifine = OptiFineCheck.IsChecked == true ? OptiFineBox.SelectedItem as OptiFineVersion : null;

        using var cts = new CancellationTokenSource();
        var dialog = ProgressDialog.Show($"正在安装 {name}", cts);
        try
        {
            await LoaderService.InstallAsync(gameDir, v, loader, loaderVersion, optifine, name, dialog.Progress, cts.Token);
            dialog.Close();
            await AppState.RefreshVersionsAsync();
            var installed = AppState.Versions.FirstOrDefault(x => x.Id == name);
            if (installed is not null) AppState.Select(installed);
            _nameEdited = false;
            if (await w.Confirm($"{name} 安装好啦", "已经帮你设为当前版本，要现在就启动吗？", "立即启动", "稍后再说"))
                await LaunchFlow.RunAsync(installed);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            w.Toast("已取消安装，下次会从断点继续");
        }
        catch (Exception ex)
        {
            dialog.Close();
            Log.Error(ex, "安装失败");
            await w.Confirm("安装没有完成", ex.Message + "\n\n已经下载的文件会保留，重新安装时会自动跳过。", "知道了", null);
        }
    }
}
