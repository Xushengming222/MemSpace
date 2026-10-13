using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MemoryEase
{
    internal sealed partial class MainForm
    {
        private void StopStrongSession()
        {
            var session = strongSession; strongSession = null; if (session != null) session.Dispose(); UpdateAuthorization();
        }
        private void UpdateAuthorization()
        {
            if (authorization == null || authorization.IsDisposed) return;
            bool ready = strongSession != null && strongSession.Ready;
            authorization.Text = ready ? "已授权" : "未授权";
            if (autoStrong != null && !autoStrong.IsDisposed) autoStrong.Enabled = !busy;
            authorize.Enabled = !busy && !ready;
        }
        private void UpdateExclusionSummary()
        {
            if (exclusionSummary != null && !exclusionSummary.IsDisposed)
                exclusionSummary.Text = "已选 " + ReleasePolicy.ParseExclusions(settings.Exclusions).Count + " 项";
        }
        private async Task ChooseExclusionsAsync()
        {
            if (busy || scanning) return; await ScanAsync(); if (IsDisposed) return;
            var choices = ExclusionChoice.Build(appGroups, settings.Exclusions);
            using (var picker = new ExclusionPicker(choices)) {
                if (picker.ShowDialog(this) != DialogResult.OK || isolated) return;
                string previous = settings.Exclusions;
                try { settings.Exclusions = picker.SelectedNames; settings.Save(); UpdateExclusionSummary(); FillProcesses(); SetStatus("排除名单已更新"); }
                catch (Exception e) { settings.Exclusions = previous; SetStatus("排除名单保存失败：" + e.Message); }
            }
        }
        private void ShowHelp()
        {
            ShowTextInfo("使用帮助 · MemSpace 1.3.2",
                "后台释放：回收闲置进程的工作集，保护前台、近期使用、读写忙碌的应用及排除名单。\r\n\r\n" +
                "强力释放：回收系统工作集、刷新修改页、清空待机列表。完成后在状态栏显示结果，详细记录写入日志，不弹出结果窗口。作用于整个系统，无法遵守应用排除规则，可能短暂卡顿或增加磁盘读写。数据重新载入后内存可能回升。\r\n\r\n" +
                "自动强力：勾选后保存，或点击授权管理员。允许 Windows 提示后，本次运行会复用授权；关闭该选项或退出软件即结束授权。重新打开软件需要再次授权。达到阈值并持续 45 秒才执行，共用至少 15 分钟的自动冷却；同时开启两种自动模式时优先使用已授权的强力模式。自动结果保存在日志中，不弹出结果窗口。\r\n\r\n" +
                "自动排除：按已知程序名与安装目录识别常见游戏、录屏和工作软件，新启动的应用会参与识别。无法识别全部软件，可在选择应用中勾选补充。名单仅用于后台释放；前台和活动保护始终生效。\r\n\r\n" +
                "最小化进入托盘，双击托盘图标恢复。关闭窗口退出。启动项的未知状态请在 Windows 启动设置核实。");
        }
    }
}
