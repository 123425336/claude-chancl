using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ClaudeCL.Core;
using Microsoft.Win32;

namespace ClaudeCL.Pages;

public partial class SettingsPage : UserControl, IPage
{
    // 正在把设置填进界面时，不触发保存
    private bool _loading;
    private List<string?> _javaPaths = [];

    public SettingsPage()
    {
        InitializeComponent();
        var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        MemSlider.Maximum = Math.Max(2048, Math.Min(32768, totalMb - 1024) / 256 * 256);
    }

    public void OnShown()
    {
        _loading = true;
        var s = Settings.Current;
        ThemeLight.IsChecked = s.Theme == AppTheme.Light;
        ThemeDark.IsChecked = s.Theme == AppTheme.Dark;
        ThemeSystem.IsChecked = s.Theme == AppTheme.System;
        MascotCheck.IsChecked = s.ShowMascot;
        AutoUpdateCheck.IsChecked = s.AutoCheckUpdate;
        AutoMemCheck.IsChecked = s.AutoMemory;
        MemSlider.Value = Math.Clamp(s.MaxMemoryMb, MemSlider.Minimum, MemSlider.Maximum);
        WidthBox.Text = s.WindowWidth.ToString();
        HeightBox.Text = s.WindowHeight.ToString();
        FullscreenCheck.IsChecked = s.Fullscreen;
        IsolateCheck.IsChecked = s.IsolateVersions;
        CleanMemCheck.IsChecked = s.CleanMemoryBeforeLaunch;
        FoldLogCheck.IsChecked = s.FoldRepeatedLogs;
        DepCheck.IsChecked = s.CheckDependencies;
        LookupCheck.IsChecked = s.AllowOnlineModLookup;
        AfterBox.SelectedIndex = Math.Clamp(s.AfterLaunch, 0, 2);
        JvmBox.Text = s.ExtraJvmArgs;
        GameArgsBox.Text = s.ExtraGameArgs;
        SrcAuto.IsChecked = s.Source == DownloadSource.Auto;
        SrcMirror.IsChecked = s.Source == DownloadSource.Mirror;
        SrcOfficial.IsChecked = s.Source == DownloadSource.Official;
        ThreadSlider.Value = Math.Clamp(s.DownloadThreads, 4, 128);
        ClientIdBox.Text = s.MsClientId;
        FillJava();
        UpdateMemory();
        ThreadText.Text = ((int)ThreadSlider.Value).ToString();
        _loading = false;
    }

    private void FillJava()
    {
        var s = Settings.Current;
        _javaPaths = [null];
        var labels = new List<string> { "自动选择（推荐）" };
        foreach (var j in JavaFinder.Installed)
        {
            _javaPaths.Add(j.JavaExePath);
            labels.Add($"Java {j.MajorVersion} · {j.Brand} · {j.JavaFolder}");
        }
        if (!string.IsNullOrEmpty(s.JavaPath) && !_javaPaths.Contains(s.JavaPath, StringComparer.OrdinalIgnoreCase))
        {
            _javaPaths.Add(s.JavaPath);
            labels.Add(s.JavaPath);
        }
        JavaBox.ItemsSource = labels;
        var idx = string.IsNullOrEmpty(s.JavaPath) ? 0 : _javaPaths.FindIndex(p => string.Equals(p, s.JavaPath, StringComparison.OrdinalIgnoreCase));
        JavaBox.SelectedIndex = Math.Max(0, idx);
    }

    private void UpdateMemory()
    {
        var auto = AutoMemCheck.IsChecked == true;
        MemPanel.IsEnabled = !auto;
        MemPanel.Opacity = auto ? 0.45 : 1;
        var mb = (int)MemSlider.Value;
        MemText.Text = mb >= 1024 ? $"{mb / 1024.0:0.##} GB" : $"{mb} MB";
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;
        MemHint.Text = auto
            ? $"会根据电脑内存（共 {total:0.#} GB）和是否装了模组，自动决定给游戏多少内存。"
            : $"电脑共有 {total:0.#} GB 内存。原版 2~4 GB 足够，大型整合包可以给 6~8 GB。";
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Current.Theme = ThemeDark.IsChecked == true ? AppTheme.Dark : ThemeSystem.IsChecked == true ? AppTheme.System : AppTheme.Light;
        Settings.Save();
        App.ApplyTheme(Settings.Current.Theme);
        MainWindow.Instance?.UpdateThemeIcon();
    }

    private void Mem_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 初始化过程中控件还没全部创建
        if (MemText is null || MemHint is null || MemPanel is null) return;
        UpdateMemory();
        Any_Changed(sender, e);
    }

    private void Thread_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThreadText is null) return;
        ThreadText.Text = ((int)ThreadSlider.Value).ToString();
        Any_Changed(sender, e);
    }

    private void Java_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || JavaBox.SelectedIndex < 0) return;
        Settings.Current.JavaPath = _javaPaths[JavaBox.SelectedIndex];
        Settings.Save();
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 java.exe", Filter = "Java 程序|java.exe;javaw.exe" };
        if (dialog.ShowDialog() != true) return;
        Settings.Current.JavaPath = dialog.FileName;
        Settings.Save();
        _loading = true;
        FillJava();
        _loading = false;
    }

    /// <summary>
    /// 任意设置项变化时，统一从界面读回并保存
    /// </summary>
    private void Any_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var s = Settings.Current;
        s.ShowMascot = MascotCheck.IsChecked == true;
        s.AutoCheckUpdate = AutoUpdateCheck.IsChecked == true;
        s.AutoMemory = AutoMemCheck.IsChecked == true;
        s.MaxMemoryMb = (int)MemSlider.Value;
        if (int.TryParse(WidthBox.Text, out var w) && w is >= 320 and <= 10000) s.WindowWidth = w;
        else WidthBox.Text = s.WindowWidth.ToString();
        if (int.TryParse(HeightBox.Text, out var h) && h is >= 240 and <= 10000) s.WindowHeight = h;
        else HeightBox.Text = s.WindowHeight.ToString();
        s.Fullscreen = FullscreenCheck.IsChecked == true;
        s.IsolateVersions = IsolateCheck.IsChecked == true;
        s.CleanMemoryBeforeLaunch = CleanMemCheck.IsChecked == true;
        s.FoldRepeatedLogs = FoldLogCheck.IsChecked == true;
        s.CheckDependencies = DepCheck.IsChecked == true;
        s.AllowOnlineModLookup = LookupCheck.IsChecked == true;
        s.AfterLaunch = Math.Max(0, AfterBox.SelectedIndex);
        s.ExtraJvmArgs = JvmBox.Text.Trim();
        s.ExtraGameArgs = GameArgsBox.Text.Trim();
        s.Source = SrcMirror.IsChecked == true ? DownloadSource.Mirror : SrcOfficial.IsChecked == true ? DownloadSource.Official : DownloadSource.Auto;
        s.DownloadThreads = (int)ThreadSlider.Value;
        s.MsClientId = ClientIdBox.Text.Trim();
        Settings.Save();
        UpdateMemory();
    }
}
