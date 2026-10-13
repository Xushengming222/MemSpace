using System;
using System.Drawing;
using System.Windows.Forms;

namespace MemoryEase
{
    internal sealed partial class MainForm
    {
        private Icon numberIcon;
        private int trayPercentage = -1, traySize;
        private ToolStripMenuItem trayBackground, trayStrong, trayProtection;
        private string gameReason = "";
        private bool AutoStrongPaused { get { return GameProtection.PausesStrong(settings, AutomaticRelease.Mode(settings, strongSession != null && strongSession.Ready), gameReason); } }
        private void BuildTray()
        {
            tray = new NotifyIcon { Icon = Program.AppIcon, Text = "MemSpace · 内存管家", Visible = false };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开内存管家", null, delegate { ShowWindow(); });
            trayBackground = (ToolStripMenuItem)menu.Items.Add("释放后台内存", null, async delegate { await ReleaseAsync(null, false); });
            trayStrong = (ToolStripMenuItem)menu.Items.Add("强力释放", null, async delegate { await ReleaseStrongAsync(); });
            menu.Items.Add(new ToolStripSeparator());
            trayProtection = (ToolStripMenuItem)menu.Items.Add("游戏保护：就绪"); trayProtection.Enabled = false;
            menu.Items.Add("退出", null, delegate { Close(); });
            menu.Opening += delegate { ReadGameProtection(true); UpdateTrayState(); };
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { ShowWindow(); };
        }
        private void DisposeTray()
        {
            if (tray != null) {
                tray.Visible = false;
                if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
                tray.Dispose(); tray = null;
            }
            if (numberIcon != null) { numberIcon.Dispose(); numberIcon = null; }
        }
        private bool WindowVisible { get { return Visible && WindowState != FormWindowState.Minimized; } }
        private void UpdateRefreshInterval() { timer.Interval = RefreshPolicy.Interval(WindowVisible); }
        private void ReadGameProtection(bool inspectWhenDisabled = false)
        { gameReason = settings.GameProtection && !isolated && (settings.AutoStrongRelease || inspectWhenDisabled) ? GameProtection.ReadReason() : ""; }
        private void UpdateTrayState()
        {
            if (tray == null) return;
            trayBackground.Enabled = trayStrong.Enabled = !busy;
            trayProtection.Text = !settings.GameProtection ? "游戏保护：关闭" : gameReason.Length == 0 ? "游戏保护：就绪" : AutoStrongPaused ? "游戏保护：自动强力暂停" : "游戏保护：游戏 / 全屏中";
            trayProtection.ToolTipText = gameReason;
            if (memory == null) return;
            int percentage = TrayIcon.Percentage(memory.UsedPercent);
            string text = "MemSpace · " + percentage + "% · 可用 " + MemorySnapshot.Size(memory.Available);
            if (AutoStrongPaused) text += " · 自动强力暂停";
            if (text != tray.Text) tray.Text = text.Length <= 63 ? text : text.Substring(0, 63);
            if (!tray.Visible) return;
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            uint dpi = taskbar == IntPtr.Zero ? (uint)monitorDpi : Native.GetDpiForWindow(taskbar);
            int size = Math.Max(16, Math.Min(64, Native.GetSystemMetricsForDpi(49, dpi == 0 ? 96 : dpi)));
            if (numberIcon != null && trayPercentage == percentage && traySize == size) return;
            Icon replacement = TrayIcon.Create(percentage, size), old = numberIcon;
            try { tray.Icon = replacement; }
            catch { replacement.Dispose(); throw; }
            numberIcon = replacement; trayPercentage = percentage; traySize = size;
            if (old != null) old.Dispose();
        }
        private void HideToTray()
        {
            if (tray == null) return;
            tray.Visible = true; Hide(); UpdateRefreshInterval(); UpdateTrayState();
        }
        private async void ShowWindow()
        {
            restoringWindow = true;
            try {
                Show(); WindowState = FormWindowState.Normal; Activate();
                if (tray != null) tray.Visible = false;
                UpdateRefreshInterval(); UpdateHome();
            } finally { restoringWindow = false; }
            await RefreshAsync();
            if (!IsDisposed && RefreshPolicy.Processes(WindowVisible, currentPage, busy, clock.ElapsedMilliseconds - lastScan)) await ScanAsync();
        }
    }
}
