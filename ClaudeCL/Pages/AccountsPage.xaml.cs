using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>账号卡片的显示数据</summary>
public sealed class AccountItem
{
    public required Account Account { get; init; }
    public bool IsCurrent { get; init; }
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UseVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
}

public partial class AccountsPage : UserControl, IPage
{
    public AccountsPage()
    {
        InitializeComponent();
        AppState.AccountsChanged += () => Dispatcher.Invoke(Refresh);
    }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        var current = AccountService.Selected?.Id;
        var items = Settings.Current.Accounts.Select(a => new AccountItem { Account = a, IsCurrent = a.Id == current }).ToList();
        List.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Offline_Click(object sender, MouseButtonEventArgs e)
    {
        var w = MainWindow.Instance!;
        var name = await w.Prompt("添加离线账号", "玩家名需要 3~16 位，只能使用英文字母、数字和下划线。", "", "例如 Steve");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            AccountService.Add(AccountService.CreateOffline(name));
            AppState.NotifyAccounts();
            w.Toast($"欢迎你，{name.Trim()}！", ToastKind.Success);
        }
        catch (Exception ex)
        {
            w.Toast(ex.Message, ToastKind.Error);
        }
    }

    private async void Microsoft_Click(object sender, MouseButtonEventArgs e)
    {
        var w = MainWindow.Instance!;
        using var cts = new CancellationTokenSource();
        // 登录对话框：浏览器里完成授权后自动关闭
        string? loginUrl = null;
        var body = new StackPanel { Width = 420 };
        body.Children.Add(new TextBlock { Text = "微软账号登录", Style = (Style)w.FindResource("T.H2"), FontSize = 20 });
        body.Children.Add(new TextBlock
        {
            Text = "已经在浏览器里打开了微软的登录页面。登录并同意授权后，这里会自动完成，不用再回来输入什么。",
            Style = (Style)w.FindResource("T.Soft"), Margin = new Thickness(0, 8, 0, 18),
        });
        var waiting = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        waiting.Children.Add(new SparkLoader { Width = 26, Height = 26 });
        var status = new TextBlock { Text = "等你在浏览器里完成登录……", Style = (Style)w.FindResource("T.Caption"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        waiting.Children.Add(status);
        body.Children.Add(waiting);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0), MinWidth = 84 };
        var copy = new Button { Content = "复制链接", Margin = new Thickness(0, 0, 10, 0) };
        var reopen = new Button { Content = "重新打开网页", Style = (Style)w.FindResource("Btn.Primary") };
        buttons.Children.Add(cancel);
        buttons.Children.Add(copy);
        buttons.Children.Add(reopen);
        body.Children.Add(buttons);
        var handle = w.ShowDialog(body, 520);
        cancel.Click += (_, _) => cts.Cancel();
        handle.OnEscape = cts.Cancel;
        reopen.Click += (_, _) => { if (loginUrl is not null) Shell.Open(loginUrl); };
        copy.Click += (_, _) =>
        {
            if (loginUrl is null) return;
            try { Clipboard.SetText(loginUrl); w.Toast("链接已复制，可以粘贴到任意浏览器打开", ToastKind.Success); }
            catch { /* 剪贴板被占用时忽略 */ }
        };

        try
        {
            var account = await AccountService.LoginMicrosoftAsync(url => loginUrl = url, cts.Token);
            handle.Close();
            AccountService.Add(account);
            AppState.NotifyAccounts();
            w.Activate();
            w.Toast($"登录成功，欢迎回来，{account.Name}！", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            handle.Close();
        }
        catch (Exception ex)
        {
            handle.Close();
            Log.Error(ex, "微软登录失败");
            w.Activate();
            await w.Confirm("登录没有成功", ex.Message, "知道了", null);
        }
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account acc) return;
        Settings.Current.SelectedAccountId = acc.Id;
        Settings.Save();
        AppState.NotifyAccounts();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Account acc) return;
        if (!await MainWindow.Instance!.Confirm($"移除 {acc.Name}？", "只会从启动器里移除，不影响账号本身和存档。", "移除", "保留", true)) return;
        AccountService.Remove(acc);
        AppState.NotifyAccounts();
    }
}