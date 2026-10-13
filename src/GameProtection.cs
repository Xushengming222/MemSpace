using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MemoryEase
{
    internal static class GameProtection
    {
        private static readonly HashSet<string> Games = ReleasePolicy.ParseExclusions(
            "VALORANT-Win64-Shipping;cs2;dota2;League of Legends;GenshinImpact;YuanShen;StarRail;FortniteClient-Win64-Shipping;RobloxPlayerBeta;GTA5;RDR2;Minecraft.Windows");
        private static readonly string[] Directories = { "steamapps", "Epic Games", "Riot Games", ".minecraft" };
        internal static bool KnownGame(string name, string path, string title = "")
        {
            if (Games.Contains(name ?? "")) return true;
            if ((string.Equals(name, "java", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "javaw", StringComparison.OrdinalIgnoreCase)) &&
                (title ?? "").IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string normalized = (path ?? "").Replace('/', '\\');
            return Directories.Any(directory => normalized.IndexOf("\\" + directory + "\\", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        internal static bool CoversMonitor(Native.Rectangle client, Native.Rectangle monitor)
        {
            // Client area excludes a maximized window's title bar and resize borders.
            return monitor.Right > monitor.Left && monitor.Bottom > monitor.Top &&
                client.Right > client.Left && client.Bottom > client.Top &&
                client.Left <= monitor.Left + 1 && client.Top <= monitor.Top + 1 &&
                client.Right >= monitor.Right - 1 && client.Bottom >= monitor.Bottom - 1;
        }
        internal static bool WindowIsFullScreen(IntPtr window)
        {
            if (window == IntPtr.Zero || !Native.VisibleWindow(window)) return false;
            var name = new StringBuilder(256); Native.GetClassName(window, name, name.Capacity);
            if (name.ToString() == "Progman" || name.ToString() == "WorkerW" || name.ToString() == "Shell_TrayWnd" || name.ToString() == "Shell_SecondaryTrayWnd") return false;
            Native.Rectangle client;
            if (!Native.GetClientRect(window, out client)) return false;
            var origin = new Native.Point();
            if (!Native.ClientToScreen(window, ref origin)) return false;
            client.Left += origin.X; client.Right += origin.X; client.Top += origin.Y; client.Bottom += origin.Y;
            var monitor = new Native.MonitorInfo(); monitor.Size = (uint)Marshal.SizeOf(monitor);
            return Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor) && CoversMonitor(client, monitor.Bounds);
        }
        internal static bool ShellBusy(int state) { return state == 2 || state == 3 || state == 4; }
        internal static string ReadReason()
        {
            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0 && ShellBusy(state)) return "全屏或演示模式";
            IntPtr window = Native.GetForegroundWindow();
            if (WindowIsFullScreen(window)) return "前台全屏应用";
            int id; Native.GetWindowThreadProcessId(window, out id);
            if (id <= 0 || !Native.VisibleWindow(window)) return "";
            using (var process = Native.OpenProcess(Native.Query, false, id)) {
                if (process.IsInvalid) return "";
                var path = new StringBuilder(512); uint length = (uint)path.Capacity;
                if (!Native.QueryFullProcessImageName(process, 0, path, ref length)) {
                    if (Marshal.GetLastWin32Error() != 122) return "";
                    path = new StringBuilder(32768); length = (uint)path.Capacity;
                    if (!Native.QueryFullProcessImageName(process, 0, path, ref length)) return "";
                }
                var title = new StringBuilder(256); Native.GetWindowText(window, title, title.Capacity);
                return KnownGame(Path.GetFileNameWithoutExtension(path.ToString()), path.ToString(), title.ToString()) ? "前台游戏" : "";
            }
        }
        internal static bool PausesStrong(AppSettings settings, AutomaticMode mode, string reason)
        { return settings.GameProtection && mode == AutomaticMode.Strong && !string.IsNullOrEmpty(reason); }
    }
}
