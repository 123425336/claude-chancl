using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

public partial class AboutPage : UserControl, IPage
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {SelfUpdater.Current} · {(SelfUpdater.IsSelfContained ? "免安装版" : "轻量版")} · .NET {System.Environment.Version}";
    }

    public void OnShown() { }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "检查中……";
        await MainWindow.Instance!.CheckUpdateAsync(true);
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = "检查更新";
    }

    private void Data_Click(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.DataDir);
    private void Logs_Click(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.LogDir);
    private void Bilibili_Click(object sender, RoutedEventArgs e) => Shell.Open("https://space.bilibili.com/381864126");

    private void License_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppPaths.ExeDir, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(path)) Shell.Open(path);
        else MainWindow.Instance!.Toast("没有找到许可证文件", ToastKind.Error);
    }
}
