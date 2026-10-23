using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>
/// 界面间共享的状态：已安装版本、当前选择的版本、账号变化通知
/// </summary>
public static class AppState
{
    public static IReadOnlyList<InstalledVersion> Versions { get; private set; } = [];

    public static event Action? VersionsChanged;
    public static event Action? SelectionChanged;
    public static event Action? AccountsChanged;

    public static InstalledVersion? SelectedVersion =>
        Versions.FirstOrDefault(v => v.Id == Settings.Current.SelectedVersion && v.IsValid)
        ?? Versions.FirstOrDefault(v => v.IsValid);

    /// <summary>
    /// 重新扫描已安装版本（在后台线程读文件）
    /// </summary>
    public static async Task RefreshVersionsAsync()
    {
        var dir = Settings.Current.GameDir;
        Versions = await Task.Run(() => VersionStore.Scan(dir));
        VersionsChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public static void Select(InstalledVersion v)
    {
        Settings.Current.SelectedVersion = v.Id;
        Settings.Save();
        SelectionChanged?.Invoke();
    }

    public static void NotifyAccounts() => AccountsChanged?.Invoke();
}
