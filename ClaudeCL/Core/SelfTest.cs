#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

/// <summary>
/// 调试用的无界面自检：安装指定版本（可选 Fabric）并启动，等到游戏窗口出现后结束。
/// 用法：ClaudeCL.exe --selftest 游戏目录 版本号 [forge/neoforge/fabric/quilt/optifine] 结果文件
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string gameDir, string versionId, string? loaderName, string resultFile)
    {
        var sb = new StringBuilder();
        void W(string s)
        {
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {s}");
            File.WriteAllText(resultFile, sb.ToString(), new UTF8Encoding(false));
        }

        try
        {
            // 只修改内存中的设置，不写回用户的设置文件
            Settings.Current.GameDir = gameDir;
            Settings.Current.WindowWidth = 854;
            Settings.Current.WindowHeight = 480;
            Directory.CreateDirectory(gameDir);

            // 先单独测一次网络，排查连接问题
            foreach (var u in new[] { "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "https://meta.fabricmc.net/v2/versions/game" })
            {
                try { W($"网络 {u} => {(await Net.Client.GetAsync(u)).StatusCode}"); }
                catch (Exception ex) { W($"网络 {u} => {ex.GetBaseException().Message}"); }
            }
            W("扫描 Java");
            await JavaFinder.ScanAsync();
            W($"找到 Java：{JavaFinder.Installed.Count} 个");

            var manifest = await Installer.GetManifestAsync();
            var entry = manifest.First(v => v.Id == versionId);
            var name = versionId;
            var lastStage = "";
            var progress = new Progress<StageProgress>(p =>
            {
                if (p.Stage != lastStage) { lastStage = p.Stage; W($"阶段：{p.Stage} {p.Detail}"); }
            });
            if (loaderName is not null && Enum.TryParse<LoaderType>(loaderName, true, out var type))
            {
                var loaders = await LoaderService.GetVersionsAsync(type, versionId);
                var loader = (loaders.FirstOrDefault(l => l.Recommended) ?? loaders[0]).Version;
                name = LoaderService.SuggestName(versionId, type, loader, false);
                W($"安装 {type} {loader}（共 {loaders.Count} 个可选版本）");
                await LoaderService.InstallAsync(gameDir, entry, type, loader, null, name, progress, CancellationToken.None);
            }
            else if (loaderName == "optifine")
            {
                var of = (await OptiFineInstaller.GetVersionsAsync(versionId))[0];
                name = LoaderService.SuggestName(versionId, null, null, true);
                W($"安装 OptiFine {of.Display}");
                await LoaderService.InstallAsync(gameDir, entry, null, null, of, name, progress, CancellationToken.None);
            }
            else
            {
                await Installer.InstallVanillaAsync(gameDir, entry, name, progress, CancellationToken.None);
            }
            W("安装完成");

            var version = VersionStore.Load(gameDir, name);
            W($"版本识别：{version.Summary}");
            return await LaunchAndWaitAsync(version, progress, W);
        }
        catch (Exception ex)
        {
            W("失败：" + ex);
            return 1;
        }
    }

    /// <summary>
    /// 安装整合包并启动，验证整合包能完整加载。
    /// 用法：ClaudeCL.exe --pack-test 游戏目录 整合包文件 结果文件 [--no-launch]
    /// </summary>
    public static async Task<int> RunPackAsync(string gameDir, string packPath, string resultFile, bool launch)
    {
        var sb = new StringBuilder();
        void W(string s)
        {
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {s}");
            File.WriteAllText(resultFile, sb.ToString(), new UTF8Encoding(false));
        }

        try
        {
            Settings.Current.GameDir = gameDir;
            Settings.Current.WindowWidth = 854;
            Settings.Current.WindowHeight = 480;
            // 关闭全局的版本隔离，验证整合包版本仍然使用自己的文件夹
            Settings.Current.IsolateVersions = false;
            Directory.CreateDirectory(gameDir);
            await JavaFinder.ScanAsync();

            var pack = ModpackInstaller.Read(packPath);
            W($"整合包：{pack.Name} {pack.Version}（{pack.FormatText}），{pack.GameVersion} · {pack.LoaderText}，" +
              $"{pack.Files.Count} 个直接下载文件，{pack.CurseFiles.Count} 个需查询的文件，覆盖目录：{string.Join("、", pack.OverrideDirs)}");
            await AppState.RefreshVersionsAsync();
            var name = ModpackInstaller.SuggestName(gameDir, pack);
            W($"版本名：{name}");
            var lastStage = "";
            var progress = new Progress<StageProgress>(p =>
            {
                if (p.Stage != lastStage) { lastStage = p.Stage; W($"阶段：{p.Stage} {p.Detail}"); }
            });
            var result = await ModpackInstaller.InstallAsync(pack, name, progress, CancellationToken.None);
            W(result.Warnings.Count == 0 ? "安装完成，没有问题" : "安装完成，提示：\n  " + string.Join("\n  ", result.Warnings));

            var version = VersionStore.Load(gameDir, name);
            var runDir = version.RunDir(gameDir);
            W($"版本识别：{version.Summary}，来自整合包：{version.FromModpack}，游戏目录：{runDir}");
            foreach (var sub in new[] { "mods", "config", "resourcepacks", "shaderpacks" })
            {
                var d = Path.Combine(runDir, sub);
                W($"  {sub}：{(Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Count() + " 个文件" : "无")}");
            }
            var marker = ModpackInstaller.ReadMarker(version.Dir);
            W($"标记文件记录了 {marker?["files"]?.AsArray().Count ?? 0} 个整合包文件");
            if (!launch) return 0;
            return await LaunchAndWaitAsync(version, progress, W);
        }
        catch (Exception ex)
        {
            W("失败：" + ex);
            return 1;
        }
    }

    /// <summary>
    /// 启动游戏并等到完整加载到主菜单
    /// </summary>
    private static async Task<int> LaunchAndWaitAsync(InstalledVersion version, IProgress<StageProgress> progress, Action<string> W)
    {
        {
            var account = AccountService.CreateOffline("ClaudeTest");
            var session = await GameLauncher.LaunchAsync(version, account, progress, CancellationToken.None);
            W("进程已启动，等待窗口");

            var deadline = DateTime.Now.AddSeconds(600);
            while (DateTime.Now < deadline)
            {
                if (session.Process.HasExited)
                {
                    W($"游戏提前退出，退出码 {session.Process.ExitCode}");
                    W("最后日志：\n" + string.Join("\n", session.Lines.TakeLast(60)));
                    return 2;
                }
                session.Process.Refresh();
                // 等到声音引擎启动，说明游戏已经完整加载到主菜单
                if (session.Lines.Any(l => l.Contains("Sound engine started")))
                {
                    W($"成功：游戏已完整加载（窗口：{session.Process.MainWindowTitle}）");
                    W("最后日志：\n" + string.Join("\n", session.Lines.TakeLast(8)));
                    session.Process.Kill(true);
                    return 0;
                }
                await Task.Delay(1000);
            }
            W("超时：没有等到游戏窗口");
            W("最后日志：\n" + string.Join("\n", session.Lines.TakeLast(60)));
            session.Process.Kill(true);
            return 3;
        }
    }
}
#endif
