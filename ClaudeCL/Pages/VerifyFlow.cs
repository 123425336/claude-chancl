using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCL.Controls;
using ClaudeCL.Core;

namespace ClaudeCL.Pages;

/// <summary>
/// 校验游戏完整性的界面流程：逐个核对文件 SHA1，修复缺失和损坏的文件，再告诉用户结果
/// </summary>
public static class VerifyFlow
{
    private static bool _busy;

    public static async Task RunAsync(InstalledVersion v)
    {
        var w = MainWindow.Instance!;
        if (_busy) { w.Toast("已经在校验了，请等它完成", ToastKind.Info); return; }
        // 游戏运行时文件被占用，重新下载会覆盖失败
        if (LaunchFlow.HasLiveLog)
        {
            await w.Confirm("游戏正在运行", "校验时可能需要替换游戏正在使用的文件，请先关闭游戏再校验。", "知道了", null);
            return;
        }
        _busy = true;
        using var cts = new CancellationTokenSource();
        var dialog = ProgressDialog.Show($"正在校验 {v.Id}", cts);
        try
        {
            var r = await Task.Run(() => Installer.VerifyAndRepairAsync(Settings.Current.GameDir, v.Id, dialog.Progress, cts.Token));
            dialog.Close();
            if (r.AllGood)
            {
                w.Toast($"{v.Id} 的 {r.Checked} 个文件都完好无损", ToastKind.Success);
                return;
            }
            // 列出几个有问题的文件名，让用户心里有数
            var names = r.Missing.Concat(r.Damaged).Select(i => System.IO.Path.GetFileName(i.Path)).Take(6).ToList();
            var more = r.Missing.Count + r.Damaged.Count > names.Count ? " 等" : "";
            await w.Confirm("校验完成，已修复",
                $"共检查 {r.Checked} 个文件，发现 {r.Missing.Count} 个缺失、{r.Damaged.Count} 个损坏，都已经重新下载好了。\n\n" +
                $"有问题的文件：{string.Join("、", names)}{more}", "好的", null);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
        }
        catch (Exception ex)
        {
            dialog.Close();
            Log.Error(ex, "校验游戏文件失败");
            await w.Confirm("校验没有完成", ex.Message, "知道了", null);
        }
        finally
        {
            _busy = false;
        }
    }
}
