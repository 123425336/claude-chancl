using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>
/// 全屏实时日志：点击启动后立即出现，先显示准备进度，游戏启动后持续显示游戏输出
/// </summary>
public partial class GameLogView : UserControl
{
    private const int MaxLines = 5000;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly InstalledVersion _version;
    private CancellationTokenSource? _cts;
    private int _lineCount;
    private bool _follow = true;
    private string _lastStage = "";

    public GameSession? Session { get; private set; }

    public GameLogView(InstalledVersion version, CancellationTokenSource? cts)
    {
        _version = version;
        _cts = cts;
        InitializeComponent();
        TitleText.Text = version.Id;
        StatusText.Text = "正在准备启动……";
        // 批量刷新到文本框，避免日志刷屏时界面卡顿
        _flush.Tick += (_, _) => Flush();
        Loaded += (_, _) => { _flush.Start(); Flush(); };
        Unloaded += (_, _) => _flush.Stop();
    }

    // ---------------- 写入日志 ----------------

    /// <summary>追加一行（任意线程可调用）</summary>
    public void Append(string line)
    {
        _pending.Enqueue(line);
        // 日志页收起时也在积累，限制一下数量
        while (_pending.Count > MaxLines) _pending.TryDequeue(out _);
    }

    /// <summary>启动器自己的提示信息</summary>
    public void Info(string msg) => Append($"[{DateTime.Now:HH:mm:ss}] [ClaudeCL] {msg}");

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var sb = new StringBuilder();
        var n = 0;
        while (_pending.TryDequeue(out var line))
        {
            sb.Append(line).Append('\n');
            n++;
        }
        LogBox.AppendText(sb.ToString());
        _lineCount += n;
        if (_lineCount > MaxLines)
        {
            // 只保留最新的一部分，防止文本过长拖慢界面
            var lines = LogBox.Text.Split('\n');
            var keep = lines.Skip(Math.Max(0, lines.Length - MaxLines * 3 / 5)).ToArray();
            LogBox.Text = string.Join('\n', keep);
            _lineCount = keep.Length;
        }
        if (_follow) LogBox.ScrollToEnd();
    }

    // ---------------- 状态 ----------------

    public IProgress<StageProgress> Progress => new Progress<StageProgress>(ReportStage);

    public void ReportStage(StageProgress p)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = p.Stage;
        DetailText.Text = p.Detail;
        var v = Math.Clamp(p.Percent, 0, 1);
        if (v >= Bar.Value || v < 0.01) Bar.Value = v;
        PercentText.Text = $"{Bar.Value * 100:0}%";
        if (p.Stage != _lastStage)
        {
            _lastStage = p.Stage;
            Info(string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage}：{p.Detail}");
        }
    }

    /// <summary>
    /// 接上正在运行的游戏，开始显示游戏输出
    /// </summary>
    public void Attach(GameSession session)
    {
        if (Session == session) return;
        Session = session;
        _cts = null;
        foreach (var line in session.Lines.ToArray()) Append(line);
        session.Output += Append;
        session.Exited += code => Dispatcher.BeginInvoke(() => OnExited(code));
        ProgressPanel.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        KillButton.Visibility = Visibility.Visible;
        StatusText.Text = "游戏运行中";
        Spark.IsActive = true;
        if (session.Process.HasExited) OnExited(session.Process.ExitCode);
    }

    private void OnExited(int code)
    {
        Spark.IsActive = false;
        KillButton.Visibility = Visibility.Collapsed;
        var minutes = Session is null ? 0 : (int)(DateTime.Now - Session.StartedAt).TotalMinutes;
        StatusText.Text = code == 0 ? $"游戏已退出 · 这次玩了 {minutes} 分钟" : $"游戏意外退出（退出码 {code}）";
        Info(StatusText.Text);
    }

    public void MarkFailed(string message)
    {
        Spark.IsActive = false;
        ProgressPanel.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "启动没有成功，原因写在下面的日志里";
        StatusText.SetResourceReference(ForegroundProperty, "DangerBrush");
        Info("启动失败：" + message);
    }

    public void MarkCanceled()
    {
        Spark.IsActive = false;
        ProgressPanel.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "已取消启动";
        Info("已取消启动");
    }

    // ---------------- 交互 ----------------

    private void LogBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只在用户自己滚动时判断是否停在底部
        if (e.ExtentHeightChange != 0) return;
        _follow = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4;
        FollowButton.Visibility = _follow ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        _follow = true;
        FollowButton.Visibility = Visibility.Collapsed;
        LogBox.ScrollToEnd();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消……";
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (Session is null || Session.Process.HasExited) return;
        if (!await MainWindow.Instance!.Confirm("要结束游戏吗？", "没有保存的进度会丢失哦。", "结束游戏", "再想想", true)) return;
        try { Session.Process.Kill(true); }
        catch (Exception ex) { Log.Error(ex, "结束游戏失败"); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        try
        {
            Clipboard.SetText(LogBox.Text);
            MainWindow.Instance!.Toast("日志已复制", ToastKind.Success);
        }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => Shell.Open(_version.RunDir(Settings.Current.GameDir));

    private void Close_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.CloseOverlay(this);

    private void Min_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        var w = MainWindow.Instance!;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => MainWindow.Instance!.Close();
}
