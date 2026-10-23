using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Controls;

/// <summary>
/// 带星芒动画的进度对话框，用于安装、启动、下载等耗时操作
/// </summary>
public sealed class ProgressDialog
{
    private readonly DialogHandle _handle;
    private readonly TextBlock _stage;
    private readonly TextBlock _detail;
    private readonly TextBlock _percent;
    private readonly ProgressBar _bar;

    private ProgressDialog(string title, CancellationTokenSource? cts)
    {
        var w = MainWindow.Instance!;
        var grid = new Grid { Width = 400 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var spark = new SparkLoader { Width = 46, Height = 46, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 18, 0) };
        grid.Children.Add(spark);

        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = title, Style = (Style)w.FindResource("T.H2"), FontSize = 19 });
        _stage = new TextBlock { Text = "准备中……", Style = (Style)w.FindResource("T.Body"), Margin = new Thickness(0, 8, 0, 0) };
        right.Children.Add(_stage);
        _detail = new TextBlock { Text = " ", Style = (Style)w.FindResource("T.Caption"), Margin = new Thickness(0, 2, 0, 12) };
        right.Children.Add(_detail);
        _bar = new ProgressBar { Value = 0 };
        right.Children.Add(_bar);

        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        _percent = new TextBlock { Text = "0%", Style = (Style)w.FindResource("T.Caption"), VerticalAlignment = VerticalAlignment.Center };
        bottom.Children.Add(_percent);
        if (cts is not null)
        {
            var cancel = new Button { Content = "取消", Padding = new Thickness(14, 6, 14, 6) };
            DockPanel.SetDock(cancel, Dock.Right);
            cancel.Click += (_, _) =>
            {
                cts.Cancel();
                _stage.Text = "正在取消……";
                cancel.IsEnabled = false;
            };
            bottom.Children.Add(cancel);
        }
        right.Children.Add(bottom);
        grid.Children.Add(right);

        _handle = w.ShowDialog(grid, 520);
        if (cts is not null) _handle.OnEscape = cts.Cancel;
    }

    public static ProgressDialog Show(string title, CancellationTokenSource? cts = null) => new(title, cts);

    /// <summary>可直接传给后台任务的进度回调（自动切回界面线程）</summary>
    public IProgress<StageProgress> Progress => new Progress<StageProgress>(Report);

    public void Report(StageProgress p)
    {
        _stage.Text = p.Stage;
        _detail.Text = string.IsNullOrEmpty(p.Detail) ? " " : p.Detail;
        var v = Math.Clamp(p.Percent, 0, 1);
        // 进度只前进不后退，避免跳动
        if (v >= _bar.Value || v < 0.01) _bar.Value = v;
        _percent.Text = $"{_bar.Value * 100:0}%";
    }

    public void Close() => _handle.Close();
}
