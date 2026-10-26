using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClaudeCL.Core;
using Microsoft.Win32;

namespace ClaudeCL;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info($"ClaudeCL {GameLauncher.LauncherVersion} 启动");

        // 全局异常兜底：记录日志并温柔地提示，而不是直接闪退
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error((Exception)args.ExceptionObject, "未处理的异常");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察到的任务异常");
            args.SetObserved();
        };

        // 以管理员身份运行的内存清理子进程：清理完立即退出
        if (e.Args.Length > 0 && e.Args[0] == "--memclean")
        {
            Shutdown(MemoryCleaner.RunElevated());
            return;
        }

        Settings.Load();

        // 自动更新后的第一次启动：记下新版本号，稍后提示；顺便清理旧版本留下的文件
        var updated = Array.IndexOf(e.Args, "--updated");
        if (updated >= 0 && updated + 1 < e.Args.Length) UpdatedTo = e.Args[updated + 1];
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            SelfUpdater.CleanupOldFiles();
        });
#if DEBUG
        // 调试：假装成旧版本，用来测试自动更新
        var pretend = Array.IndexOf(e.Args, "--pretend-version");
        if (pretend >= 0 && pretend + 1 < e.Args.Length) SelfUpdater.Current = Version.Parse(e.Args[pretend + 1]);
        // 调试：让前几次检查更新失败
        var failIdx = Array.IndexOf(e.Args, "--fail-update-checks");
        if (failIdx >= 0 && failIdx + 1 < e.Args.Length) SelfUpdater.DebugFailures = int.Parse(e.Args[failIdx + 1]);
        // 调试自检：不显示窗口，安装并启动后退出
        var a = e.Args;
        // 调试截图时指定临时游戏目录，且不改动用户的设置文件
        var gi = Array.IndexOf(a, "--gamedir");
        if (gi >= 0 && gi + 1 < a.Length)
        {
            Settings.ReadOnly = true;
            Settings.Current.GameDir = a[gi + 1];
            var si = Array.IndexOf(a, "--select");
            if (si >= 0 && si + 1 < a.Length) Settings.Current.SelectedVersion = a[si + 1];
        }
        if (a.Length >= 2 && a[0] == "--redact-test")
        {
            // 调试：检查各种可能泄漏令牌的文本是否都被遮盖
            Redactor.Register("myRawMinecraftAccessToken1234567890");
            string[] samples =
            [
                "(Session ID is token:myRawMinecraftAccessToken1234567890:0123456789abcdef0123456789abcdef)",
                "--username Steve --accessToken eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U --userType msa",
                "--accessToken someOpaqueTokenValue123 --version 1.7.10",
                "GET https://example.com/cb?code=M.C123_BAY.2.U.abcdef&state=xyz",
                "Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345",
                "普通日志：Setting user: Steve，加载了 128 个模组",
            ];
            System.IO.File.WriteAllLines(a[1], samples.Select(s => "原文：" + s + "\n遮盖：" + Redactor.Clean(s) + "\n"), new System.Text.UTF8Encoding(false));
            Shutdown(0);
            return;
        }
        if (a.Length >= 2 && a[0] == "--fold-test")
        {
            // 调试：模拟几种刷屏日志，检查折叠效果
            var folder = new LogFolder();
            var outLines = new System.Collections.Generic.List<string>();
            var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            void Feed(string line) { outLines.AddRange(folder.Feed(line, t)); t = t.AddMilliseconds(1); }
            Feed("[12:00:00] [main/INFO]: 正常日志 1");
            Feed("[12:00:00] [main/INFO]: 正常日志 2");
            // 场景一：栈溢出，三行堆栈循环 1000 遍
            Feed("java.lang.StackOverflowError");
            for (var i = 0; i < 1000; i++)
            {
                Feed("\tat com.example.Foo.bar(Foo.java:10)");
                Feed("\tat com.example.Foo.baz(Foo.java:20)");
                Feed("\tat com.example.Foo.qux(Foo.java:30)");
            }
            Feed("[12:00:01] [main/INFO]: 堆栈结束后的正常日志");
            // 场景二：某个线程每毫秒写一次同样的内容（时间戳不同）
            for (var i = 0; i < 500; i++) Feed($"[12:00:{2 + i / 1000:00}.{i % 1000:000}] [Worker-3/WARN]: Tick took too long!");
            Feed("[12:00:03] [main/INFO]: 刷屏结束");
            // 场景三：刷屏混在正常日志中间（不连续）
            for (var i = 0; i < 300; i++)
            {
                Feed("[12:00:04] [Netty/ERROR]: Connection reset by peer");
                Feed($"[12:00:04] [main/INFO]: 加载区块 {i}");
            }
            t = t.AddSeconds(2);
            outLines.AddRange(folder.Tick(t));
            outLines.AddRange(folder.FlushAll());
            var report = new System.Collections.Generic.List<string> { $"输入 {1 + 2 + 1 + 3000 + 1 + 500 + 1 + 600} 行，输出 {outLines.Count} 行：", "" };
            report.AddRange(outLines.Where(l => !l.Contains("加载区块")));
            report.Add($"（另有 {outLines.Count(l => l.Contains("加载区块"))} 行“加载区块”正常日志全部保留，未列出）");
            System.IO.File.WriteAllLines(a[1], report, new System.Text.UTF8Encoding(false));
            Shutdown(0);
            return;
        }
        if (a.Length >= 3 && a[0] == "--scan-test")
        {
            // 调试：扫描指定游戏目录，输出识别结果和跳过原因
            var found = VersionStore.Scan(a[1]);
            var report = new System.Collections.Generic.List<string> { $"识别 {found.Count} 个版本（按显示顺序）：" };
            foreach (var v in found)
                report.Add($"  {v.Id} → {v.Summary}｜游戏版本 {v.GameVersion}｜本体 {(v.Json is null ? "-" : System.IO.Path.GetFileName(Installer.JarPath(a[1], v.Json, v.Id)))}");
            report.Add($"跳过 {VersionStore.LastSkipped.Count} 个：");
            foreach (var (name, reason) in VersionStore.LastSkipped) report.Add($"  {name} → {reason}");
            System.IO.File.WriteAllLines(a[2], report, new System.Text.UTF8Encoding(false));
            Shutdown(0);
            return;
        }
        if (a.Length >= 2 && a[0] == "--check-update-test")
        {
            // 调试：只检查更新，记录耗时与异常详情
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                var report = new System.Collections.Generic.List<string>();
                for (var i = 0; i < 3; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var r = await SelfUpdater.CheckAsync(default);
                        report.Add($"第 {i + 1} 次：{sw.ElapsedMilliseconds} ms，结果 {(r is null ? "没有新版本" : r.Tag)}");
                    }
                    catch (Exception ex) { report.Add($"第 {i + 1} 次：{sw.ElapsedMilliseconds} ms 后失败：{ex}"); }
                }
                System.IO.File.WriteAllLines(a[1], report, new System.Text.UTF8Encoding(false));
                Dispatcher.Invoke(() => Shutdown(0));
            });
            return;
        }
        if (a.Length >= 2 && a[0] == "--self-update-test")
        {
            // 调试：不显示窗口，走一遍“检查 → 下载 → 替换 → 重启”
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                var report = new System.Collections.Generic.List<string> { $"当前版本 {SelfUpdater.Current}，{(SelfUpdater.IsSelfContained ? "免安装版" : "轻量版")}，程序目录可写：{SelfUpdater.CanWriteAppDir()}" };
                void Save() => System.IO.File.WriteAllLines(a[1], report, new System.Text.UTF8Encoding(false));
                try
                {
                    var r = await SelfUpdater.CheckAsync(default);
                    if (r is null)
                    {
                        report.Add("没有发现新版本");
                        Save();
                        Dispatcher.Invoke(() => Shutdown(0));
                        return;
                    }
                    report.Add($"发现 {r.Tag}：下载 {r.AssetName}（{r.AssetSize} 字节，SHA256 {(r.Sha256 ?? "无")}）");
                    var stages = new System.Collections.Generic.List<string>();
                    var zip = await SelfUpdater.DownloadAsync(r, new SyncProgress(p => { if (!stages.Contains(p.Stage)) stages.Add(p.Stage); }), default);
                    report.Add($"下载完成并校验通过：{zip}（经过阶段：{string.Join(" → ", stages)}）");
                    SelfUpdater.Apply(zip);
                    report.Add("文件替换完成，正在重启到新版本");
                    Save();
                    Dispatcher.Invoke(() => SelfUpdater.Restart(r.Tag));
                }
                catch (Exception ex)
                {
                    report.Add("失败：" + ex);
                    Save();
                    Dispatcher.Invoke(() => Shutdown(1));
                }
            });
            return;
        }
        if (a.Length >= 4 && a[0] == "--deps-report")
        {
            // 调试：只读地检查前置（不联网、不处理），用于验证本地判断
            Settings.ReadOnly = true;
            Settings.Current.GameDir = a[1];
            Settings.Current.IsolateVersions = true;
            // --no-lookup：联网，但关闭“联网识别模组”，验证不会发送哈希
            var noLookup = a.Contains("--no-lookup");
            DependencyChecker.DebugOffline = !noLookup;
            if (noLookup) Settings.Current.AllowOnlineModLookup = false;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                var lines = new System.Collections.Generic.List<string>();
                try
                {
                    var r = await DependencyChecker.CheckAsync(VersionStore.Load(a[1], a[2]), default);
                    lines.Add($"需要启用 {r.ToEnable.Count}，缺失 {r.Unresolved.Count}（离线检查，缺失的不会去联网查找）");
                    foreach (var (m, by) in r.ToEnable) lines.Add($"  启用 {m.Name}（{m.FileName}）← {by}");
                    foreach (var u in r.Unresolved) lines.Add($"  缺失 {u.Id} ← {u.RequiredBy}");
                }
                catch (Exception ex) { lines.Add("失败：" + ex); }
                System.IO.File.WriteAllLines(a[3], lines, new System.Text.UTF8Encoding(false));
                Dispatcher.Invoke(() => Shutdown(0));
            });
            return;
        }
        if (a.Length >= 4 && a[0] == "--deps-test")
        {
            // 调试：检查前置 → 一键处理 → 再检查一次
            Settings.ReadOnly = true;
            Settings.Current.GameDir = a[1];
            Settings.Current.IsolateVersions = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                var report = new System.Collections.Generic.List<string>();
                void Dump(string title, DependencyReport r)
                {
                    report.Add($"{title}：需要启用 {r.ToEnable.Count}，需要下载 {r.ToDownload.Count}，找不到 {r.Unresolved.Count}{(r.OnlineError is null ? "" : "，联网失败：" + r.OnlineError)}");
                    foreach (var (m, by) in r.ToEnable) report.Add($"  启用 {m.Name}（{m.FileName}）← {by}");
                    foreach (var d in r.ToDownload) report.Add($"  下载 {d.Title} {d.File.VersionName} ← {d.RequiredBy}");
                    foreach (var u in r.Unresolved) report.Add($"  找不到 {u.Id} ← {u.RequiredBy}");
                }
                try
                {
                    var v = VersionStore.Load(a[1], a[2]);
                    var first = await DependencyChecker.CheckAsync(v, default);
                    Dump("第一次检查", first);
                    if (first.CanFix)
                    {
                        await DependencyChecker.FixAsync(first, v, new SyncProgress(_ => { }), default);
                        report.Add("已一键处理");
                        Dump("再次检查", await DependencyChecker.CheckAsync(v, default));
                    }
                    report.Add("mods 文件夹：");
                    report.AddRange(System.IO.Directory.GetFiles(System.IO.Path.Combine(v.Dir, "mods")).Select(f => "  " + System.IO.Path.GetFileName(f)));
                    // 递归查找：只给出一个资源，看能不能找出它前置的前置
                    if (await ModrinthClient.FindProjectAsync("architectury-api", default) is { } arch)
                    {
                        var plans = await DependencyChecker.ResolveProjectsAsync([arch.Id], "测试", v, [], default);
                        report.Add($"递归查找 {arch.Title} 的前置链：" + string.Join(" → ", plans.Select(p => $"{p.Title}（{p.RequiredBy} 需要）")));
                    }
                }
                catch (Exception ex) { report.Add("失败：" + ex); }
                System.IO.File.WriteAllLines(a[3], report, new System.Text.UTF8Encoding(false));
                Dispatcher.Invoke(() => Shutdown(0));
            });
            return;
        }
        if (a.Length >= 4 && a[0] == "--update-test")
        {
            // 调试：检查指定版本的模组更新并全部执行，输出前后对比
            Settings.ReadOnly = true;
            Settings.Current.GameDir = a[1];
            Settings.Current.IsolateVersions = true;
            _ = Task.Run(async () =>
            {
                var report = new System.Collections.Generic.List<string>();
                try
                {
                    var v = VersionStore.Load(a[1], a[2]);
                    var mods = ModScanner.Scan(System.IO.Path.Combine(v.Dir, "mods"));
                    var count = await ModUpdater.CheckAsync(mods, v, default);
                    report.Add($"版本 {v.Summary}，{mods.Count} 个模组，有新版 {count} 个：");
                    foreach (var m in mods)
                        report.Add($"  {m.Name}（{m.FileName}）{(m.Enabled ? "" : "[已停用]")} → " +
                                   (m.ProjectId is null ? "无法识别" : m.Update is null ? "已是最新" : $"{m.Update.CurrentVersion} → {m.Update.Latest.VersionName}"));
                    foreach (var m in mods.Where(m => m.HasUpdate).ToList())
                    {
                        var deps = await ModUpdater.ApplyAsync(m, v, mods, default);
                        report.Add($"已更新：{m.Name} → {m.FileName}{(deps.Count > 0 ? "，补装前置 " + string.Join("、", deps) : "")}");
                    }
                    report.Add("更新后 mods 文件夹：");
                    report.AddRange(System.IO.Directory.GetFiles(System.IO.Path.Combine(v.Dir, "mods")).Select(f => "  " + System.IO.Path.GetFileName(f)));
                    var again = ModScanner.Scan(System.IO.Path.Combine(v.Dir, "mods"));
                    report.Add($"再次检查，还有新版 {await ModUpdater.CheckAsync(again, v, default)} 个");
                }
                catch (Exception ex) { report.Add("失败：" + ex); }
                System.IO.File.WriteAllLines(a[3], report, new System.Text.UTF8Encoding(false));
                Dispatcher.Invoke(() => Shutdown(0));
            });
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            return;
        }
        if (a.Length >= 3 && a[0] == "--mirror-test")
        {
            // 调试：镜像优先模式下走一遍资源平台的查询与下载
            Settings.ReadOnly = true;
            Settings.Current.Source = DownloadSource.Mirror;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Task.Run(async () =>
            {
                var r = new System.Collections.Generic.List<string>();
                try
                {
                    var (items, total) = await ModrinthClient.SearchAsync("geckolib", "mod", "1.20.1", "forge", 0, default);
                    r.Add($"搜索：{total} 个结果，第一个 {items.FirstOrDefault()?.Title}");
                    var p = await ModrinthClient.FindProjectAsync("geckolib", default);
                    r.Add($"按短名称查找：{p?.Title}（{p?.Id}）");
                    r.Add($"查找不存在的资源：{(await ModrinthClient.FindProjectAsync("claudecl-not-exist-xyz", default) is null ? "正确返回空" : "错误")}");
                    var files = await ModrinthClient.GetFilesAsync(p!.Id, "1.20.1", "forge", default);
                    r.Add($"版本列表：{files.Count} 个，最新 {files[0].VersionName}");
                    r.Add("下载候选地址：" + string.Join(" → ", Net.Candidates(files[0].Url)));
                    var target = System.IO.Path.Combine(a[1], files[0].FileName);
                    // 只给镜像地址，确认镜像本身能下载并通过校验
                    await Downloader.RunAsync([new DlItem(target, [Net.Candidates(files[0].Url)[0]], files[0].Sha1, files[0].Size)], null, default);
                    r.Add($"经镜像下载并校验通过：{new System.IO.FileInfo(target).Length} 字节");
                }
                catch (Exception ex) { r.Add("失败：" + ex); }
                System.IO.File.WriteAllLines(a[2], r, new System.Text.UTF8Encoding(false));
                Dispatcher.Invoke(() => Shutdown(0));
            });
            return;
        }
        if (a.Length >= 4 && a[0] == "--pack-test")
        {
            Settings.ReadOnly = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = SelfTest.RunPackAsync(a[1], a[2], a[3], !a.Contains("--no-launch")).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.Result)));
            return;
        }
        if (a.Length >= 4 && a[0] == "--selftest")
        {
            Settings.ReadOnly = true;
            var loader = a.Length >= 5 ? a[3] : null;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = SelfTest.RunAsync(a[1], a[2], loader, a[^1]).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.Result)));
            return;
        }
#endif
        ApplyTheme(Settings.Current.Theme);
        new MainWindow().Show();
    }

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "界面线程异常");
        e.Handled = true;
        ClaudeCL.MainWindow.Instance?.Toast("出了一点小意外：" + e.Exception.Message, ToastKind.Error);
    }

    /// <summary>
    /// 切换浅色/深色配色
    /// </summary>
    public static void ApplyTheme(AppTheme mode)
    {
        var dark = mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };
        var dicts = Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        IsDark = dark;
    }

    public static bool IsDark { get; private set; }

#if DEBUG
    /// <summary>直接在调用线程上转发的进度回调（调试用）</summary>
    private sealed class SyncProgress(Action<StageProgress> action) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => action(value);
    }
#endif

    /// <summary>刚刚自动更新到的版本号（没有更新时为空）</summary>
    public static string? UpdatedTo { get; private set; }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
