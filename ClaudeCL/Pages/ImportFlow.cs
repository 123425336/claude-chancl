using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>
/// 导入文件：拖进窗口的模组、资源包、光影，以及整合包的安装
/// </summary>
public static class ImportFlow
{
    /// <summary>
    /// 拖入文件默认装到哪个版本：正在查看的版本详情，否则是当前选中的版本
    /// </summary>
    public static InstalledVersion? DefaultTarget()
    {
        if (MainWindow.Instance?.CurrentPage is VersionDetailPage detail)
            return AppState.Versions.FirstOrDefault(v => v.Id == detail.Version.Id && v.IsValid) ?? AppState.SelectedVersion;
        return AppState.SelectedVersion;
    }

    public static async Task HandleDropAsync(string[] paths)
    {
        var w = MainWindow.Instance!;
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            w.Toast("拖进来的是文件夹，请直接拖入里面的文件");
            return;
        }
        var classified = await Task.Run(() => files.Select(f => (Path: f, Kind: DropClassifier.Classify(f))).ToList());
        var packs = classified.Where(c => c.Kind == DropKind.Modpack).Select(c => c.Path).ToList();
        var others = classified.Where(c => c.Kind != DropKind.Modpack).ToList();
        if (others.Count > 0)
        {
            if (others.All(o => o.Kind == DropKind.Unknown))
                w.Toast($"认不出 {Path.GetFileName(others[0].Path)}{(others.Count > 1 ? $" 等 {others.Count} 个文件" : "")} 是什么。" +
                        "可以拖入模组（.jar）、资源包、光影或整合包（.mrpack / .zip）", ToastKind.Error);
            else await InstallFilesAsync(others);
        }
        // 整合包一个一个装
        foreach (var pack in packs) await ImportModpackAsync(pack);
    }

    // ---------------- 模组 / 资源包 / 光影 ----------------

    private static async Task InstallFilesAsync(List<(string Path, DropKind Kind)> files)
    {
        var w = MainWindow.Instance!;
        var versions = AppState.Versions.Where(v => v.IsValid).ToList();
        if (versions.Count == 0)
        {
            w.Toast("先安装一个游戏版本，才能往里面放文件哦", ToastKind.Error);
            return;
        }
        // 每个模组适用的加载器，用来提醒装错版本
        var loaders = await Task.Run(() => files.Where(f => f.Kind == DropKind.Mod)
            .ToDictionary(f => f.Path, f => DropClassifier.ModLoaders(f.Path)));
        var valid = files.Count(f => f.Kind != DropKind.Unknown);

        var tcs = new TaskCompletionSource<InstalledVersion?>();
        var body = new StackPanel { Width = 600 };
        body.Children.Add(new TextBlock { Text = $"安装 {valid} 个文件", Style = (Style)w.FindResource("T.H2"), FontSize = 20 });

        var targetRow = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        targetRow.Children.Add(new TextBlock { Text = "装到", Style = (Style)w.FindResource("T.Soft"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var box = new ComboBox();
        foreach (var v in versions) box.Items.Add(new ComboBoxItem { Content = $"{v.Id}（{v.Summary}）", Tag = v });
        var initial = DefaultTarget();
        box.SelectedIndex = Math.Max(0, versions.FindIndex(v => v.Id == initial?.Id));
        targetRow.Children.Add(box);
        body.Children.Add(targetRow);

        var rows = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        var ok = new Button { Content = "安装", Style = (Style)w.FindResource("Btn.Primary"), MinWidth = 100, IsDefault = true };
        Ui.SetIcon(ok, "");
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        InstalledVersion Target() => (InstalledVersion)((ComboBoxItem)box.SelectedItem).Tag;

        // 换了目标版本后重新检查每个文件
        void Refresh()
        {
            rows.Children.Clear();
            var target = Target();
            var runDir = target.RunDir(Settings.Current.GameDir);
            foreach (var (path, kind) in files)
            {
                string note;
                var warn = false;
                var dest = Path.Combine(runDir, DropClassifier.Folder(kind), Path.GetFileName(path));
                if (kind == DropKind.Unknown) { note = "认不出这个文件，会跳过"; warn = true; }
                else if (kind == DropKind.Mod && target.ModLoaderName is null) { note = "这个版本没有模组加载器，模组放进去不会生效"; warn = true; }
                else if (kind == DropKind.Mod && loaders[path] is { Count: > 0 } ls && !ls.Contains(target.ModLoaderName!))
                {
                    note = $"这是 {string.Join(" / ", ls.Select(DropClassifier.LoaderName).Distinct())} 的模组，和 {target.LoaderText} 不匹配，可能无法加载";
                    warn = true;
                }
                else if (File.Exists(dest)) note = "已经有同名文件，会替换掉";
                else if (File.Exists(dest + ".disabled")) note = "已经有同名的停用文件，会换成这个并启用";
                else note = $"放进 {DropClassifier.Folder(kind)} 文件夹";
                rows.Children.Add(FileRow(w, path, kind, note, warn));
            }
        }
        Refresh();
        box.SelectionChanged += (_, _) => Refresh();

        var handle = w.ShowDialog(body, 680);
        void Done(InstalledVersion? v) { handle.Close(); tcs.TrySetResult(v); }
        cancel.Click += (_, _) => Done(null);
        handle.OnEscape = () => Done(null);
        ok.Click += (_, _) => Done(Target());
        if (await tcs.Task is not { } chosen) return;

        // 复制文件（大的光影包可能要一会儿，放到后台）
        var runDir = chosen.RunDir(Settings.Current.GameDir);
        var failed = new List<string>();
        var copied = await Task.Run(() =>
        {
            var count = 0;
            foreach (var (path, kind) in files.Where(f => f.Kind != DropKind.Unknown))
            {
                var dest = Path.Combine(runDir, DropClassifier.Folder(kind), Path.GetFileName(path));
                try
                {
                    if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) { count++; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(path, dest, true);
                    if (File.Exists(dest + ".disabled")) Shell.MoveToRecycleBin(dest + ".disabled");
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"复制 {path} 失败：{ex.Message}");
                    lock (failed) failed.Add(Path.GetFileName(path));
                }
            }
            return count;
        });

        var kinds = files.Where(f => f.Kind != DropKind.Unknown).Select(f => DropClassifier.KindText(f.Kind)).Distinct().ToList();
        var what = kinds.Count == 1 ? kinds[0] : "文件";
        if (failed.Count == 0) w.Toast($"已把 {copied} 个{what}装进 {chosen.Id}", ToastKind.Success);
        else w.Toast($"装好了 {copied} 个，{failed.Count} 个失败（游戏运行时文件可能被占用）：{string.Join("、", failed.Take(3))}", ToastKind.Error);
        Log.Info($"拖入安装：{copied} 个文件 → {runDir}");

        // 正在看这个版本的模组列表时刷新一下
        if (w.CurrentPage is VersionDetailPage detail && detail.Version.Id == chosen.Id) detail.OnShown();
    }

    private static FrameworkElement FileRow(MainWindow w, string path, DropKind kind, string note, bool warn)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var pill = new Border { Style = (Style)w.FindResource("Pill"), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
        var pillText = new TextBlock { Text = DropClassifier.KindText(kind), FontSize = 11 };
        pillText.SetResourceReference(TextBlock.ForegroundProperty, kind == DropKind.Unknown ? "TextFaintBrush" : "AccentBrush");
        pill.Child = pillText;
        grid.Children.Add(pill);
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var noteText = new TextBlock { Text = note, Style = (Style)w.FindResource("T.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        if (warn) noteText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        right.Children.Add(noteText);
        grid.Children.Add(right);
        return grid;
    }

    // ---------------- 整合包 ----------------

    /// <summary>
    /// 选择本地的整合包文件并安装
    /// </summary>
    public static async Task PickModpackAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(MainWindow.Instance) == true) await ImportModpackAsync(dialog.FileName);
    }

    /// <summary>
    /// 读取并安装一个整合包
    /// </summary>
    /// <param name="iconUrl">从资源中心安装时，整合包的图标</param>
    public static async Task ImportModpackAsync(string path, string? iconUrl = null)
    {
        var w = MainWindow.Instance!;
        ModpackInfo pack;
        try
        {
            pack = await Task.Run(() => ModpackInstaller.Read(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"读取整合包 {path} 失败：{ex.Message}");
            await w.Confirm("没法安装这个整合包", ex is InvalidDataException ? ex.Message : "文件读取失败：" + ex.Message, "知道了", null);
            return;
        }

        var name = await AskNameAsync(pack, iconUrl);
        if (name is null) return;
        if (GameLauncher.Running?.Version.Id == name)
        {
            w.Toast("请先关闭正在运行的这个版本，再更新整合包", ToastKind.Error);
            return;
        }

        using var cts = new CancellationTokenSource();
        var progress = ProgressDialog.Show($"正在安装 {pack.Name}", cts);
        try
        {
            var result = await ModpackInstaller.InstallAsync(pack, name, progress.Progress, cts.Token);
            progress.Close();
            await AppState.RefreshVersionsAsync();
            var installed = AppState.Versions.FirstOrDefault(v => v.Id == name);
            if (installed is not null) AppState.Select(installed);
            var ok = result.Warnings.Count == 0;
            var message = ok
                ? "已经帮你设为当前版本，要现在就启动吗？"
                : string.Join("\n\n", result.Warnings) + "\n\n其余部分都装好了，已经设为当前版本。";
            if (await w.Confirm(ok ? $"{name} 安装好啦" : "整合包装好了，不过有一点小问题", message, "立即启动", "稍后再说") && installed is not null)
                await LaunchFlow.RunAsync(installed);
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            w.Toast("已取消安装，重新导入时会从断点继续");
        }
        catch (Exception ex)
        {
            progress.Close();
            Log.Error(ex, "安装整合包失败");
            await w.Confirm("整合包没有装完", ex.Message + "\n\n已经下载的文件会保留，重新导入时会自动跳过。", "知道了", null);
        }
    }

    /// <summary>
    /// 显示整合包信息，让玩家确认并填写版本名称；取消时返回 null
    /// </summary>
    private static Task<string?> AskNameAsync(ModpackInfo pack, string? iconUrl)
    {
        var w = MainWindow.Instance!;
        var gameDir = Settings.Current.GameDir;
        var tcs = new TaskCompletionSource<string?>();
        var body = new StackPanel { Width = 520 };

        // 标题：图标 + 名称 + 来源
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var iconBox = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(14), ClipToBounds = true, Margin = new Thickness(0, 0, 14, 0) };
        iconBox.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var initial = new TextBlock
        {
            Text = pack.Name.Length > 0 ? pack.Name[..1].ToUpperInvariant() : "?", FontSize = 24,
            FontFamily = (FontFamily)w.FindResource("SerifFont"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        initial.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var iconGrid = new Grid { Children = { initial } };
        if (!string.IsNullOrEmpty(iconUrl))
        {
            try { iconGrid.Children.Add(new Image { Source = new BitmapImage(new Uri(iconUrl)), Stretch = Stretch.UniformToFill }); }
            catch { /* 图标加载失败时显示首字 */ }
        }
        iconBox.Child = iconGrid;
        head.Children.Add(iconBox);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(titles, 1);
        titles.Children.Add(new TextBlock { Text = pack.Name, Style = (Style)w.FindResource("T.H2"), FontSize = 21, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = pack.FormatText + (pack.Version.Length > 0 ? $" · {pack.Version}" : "") + (pack.Author.Length > 0 ? $" · {pack.Author}" : "");
        titles.Children.Add(new TextBlock { Text = sub, Style = (Style)w.FindResource("T.Caption"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(titles);
        body.Children.Add(head);
        if (pack.Summary.Length > 0)
            body.Children.Add(new TextBlock { Text = pack.Summary, Style = (Style)w.FindResource("T.Soft"), TextWrapping = TextWrapping.Wrap, MaxHeight = 60, Margin = new Thickness(0, 12, 0, 0) });

        // 基本信息
        var facts = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 14, 0, 0) };
        facts.SetResourceReference(Border.BackgroundProperty, "CodeBrush");
        var factGrid = new Grid();
        factGrid.ColumnDefinitions.Add(new ColumnDefinition());
        factGrid.ColumnDefinitions.Add(new ColumnDefinition());
        factGrid.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(int col, string label, string value)
        {
            var sp = new StackPanel();
            Grid.SetColumn(sp, col);
            sp.Children.Add(new TextBlock { Text = label, Style = (Style)w.FindResource("T.Caption") });
            sp.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            factGrid.Children.Add(sp);
        }
        Fact(0, "游戏版本", pack.GameVersion);
        Fact(1, "加载器", pack.LoaderText);
        Fact(2, "需要下载", $"{pack.FileCount} 个文件");
        facts.Child = factGrid;
        body.Children.Add(facts);

        // 版本名称
        body.Children.Add(new TextBlock { Text = "版本名称", Style = (Style)w.FindResource("T.Label"), Margin = new Thickness(0, 16, 0, 0) });
        var nameBox = new TextBox { Text = ModpackInstaller.SuggestName(gameDir, pack) };
        body.Children.Add(nameBox);
        var hint = new TextBlock { Style = (Style)w.FindResource("T.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
        body.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        var ok = new Button { Content = "开始安装", Style = (Style)w.FindResource("Btn.Primary"), MinWidth = 110, IsDefault = true };
        Ui.SetIcon(ok, "");
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        // 名称是否可用：已有的普通版本不能覆盖，已有的整合包版本可以在原来的基础上更新
        void Check()
        {
            var name = nameBox.Text.Trim();
            var dir = Path.Combine(VersionStore.VersionsDir(gameDir), name);
            hint.ClearValue(TextBlock.ForegroundProperty);
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            {
                hint.Text = "版本名称不能为空，也不能包含 \\ / : * ? \" < > | 这些字符";
                hint.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                ok.IsEnabled = false;
            }
            else if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, ModpackInstaller.MarkerFile)))
            {
                var old = ModpackInstaller.ReadMarker(dir);
                hint.Text = $"会更新已有的整合包版本 {name}（{(string?)old?["name"]} {(string?)old?["version"]}）：存档和设置会保留，旧版整合包里有、新版没有的模组会移到回收站。";
                ok.IsEnabled = true;
            }
            else if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            {
                hint.Text = $"已经有一个叫 {name} 的版本了，换个名字吧";
                hint.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                ok.IsEnabled = false;
            }
            else
            {
                hint.Text = "整合包会装成一个独立的版本，模组、配置和存档都放在这个版本自己的文件夹里。";
                ok.IsEnabled = true;
            }
        }
        Check();
        nameBox.TextChanged += (_, _) => Check();

        var handle = w.ShowDialog(body, 600);
        void Done(string? n) { handle.Close(); tcs.TrySetResult(n); }
        cancel.Click += (_, _) => Done(null);
        handle.OnEscape = () => Done(null);
        ok.Click += (_, _) => { if (ok.IsEnabled) Done(nameBox.Text.Trim()); };
        nameBox.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        return tcs.Task;
    }

    /// <summary>
    /// 从资源中心安装整合包：选择版本、下载整合包文件，然后按本地整合包安装
    /// </summary>
    public static async Task InstallFromPlatformAsync(ResourceProject project)
    {
        var w = MainWindow.Instance!;
        var picked = await VersionPicker.ShowAsync(project, null, null, new HashSet<string>());
        if (picked is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "ClaudeCL", "modpacks");
        var file = Path.Combine(dir, Path.GetFileName(picked.FileName));
        using var cts = new CancellationTokenSource();
        var progress = ProgressDialog.Show($"正在下载 {project.Title}", cts);
        try
        {
            progress.Report(new StageProgress("下载整合包", 0, picked.FileName));
            await Downloader.RunAsync([new DlItem(file, [picked.Url], picked.Sha1, picked.Size)], null, cts.Token);
            progress.Close();
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            w.Toast("已取消下载");
            return;
        }
        catch (Exception ex)
        {
            progress.Close();
            Log.Error(ex, "下载整合包失败");
            w.Toast("下载整合包失败：" + ex.Message, ToastKind.Error);
            return;
        }
        try
        {
            await ImportModpackAsync(file, project.IconUrl);
        }
        finally
        {
            // 装完后整合包文件本身就没用了
            try { File.Delete(file); } catch { /* 忽略 */ }
        }
    }
}
