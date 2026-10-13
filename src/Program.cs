using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MemoryEase
{
    internal static class Program
    {
        internal static Icon AppIcon;
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            AppIcon = MakeIcon();
            try {
                if (args.Length == 3 && (args[0] == "--strong-session" || args[0] == "--strong-session-probe"))
                    return StrongSession.Helper(args[1], args[2], args[0] == "--strong-session-probe");
                if (args.Length == 3 && (args[0] == "--strong-release" || args[0] == "--strong-probe"))
                    return StrongRelease.Helper(args[1], args[2], args[0] == "--strong-probe");
                if (args.Length == 2 && args[0] == "--export-icon")
                { using (var stream = File.Create(args[1])) AppIcon.Save(stream); return 0; }
                if (args.Length == 2 && args[0] == "--diagnose")
                { File.WriteAllText(args[1], Diagnostics.Report(MemorySnapshot.Read(), MemoryService.Scan(), Diagnostics.StartupItems()), Encoding.UTF8); return 0; }
                if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
                if (args.Length == 2 && (args[0] == "--smoke-test" || args[0] == "--tray-smoke-test"))
                {
                    Directory.CreateDirectory(args[1]); int exit = 0;
                    using (var form = new MainForm(new AppSettings(), false, false, true))
                    {
                        form.Shown += async delegate {
                            try {
                                if (args[0] == "--tray-smoke-test") await form.CaptureTrayAsync(); else await form.CaptureViewsAsync(args[1]);
                                File.WriteAllText(Path.Combine(args[1], "smoke.txt"), args[0] == "--tray-smoke-test" ? "PASS: minimize hides the taskbar window and shows the tray icon; restore returns the normal window." : "PASS: four views, three font sizes, five window sizes, page bounds, scrolling, tray, searchable application picker and checkbox/session teardown verified. Session probes perform no memory operations; no settings changed.");
                            }
                            catch (Exception e) { exit = 1; File.WriteAllText(Path.Combine(args[1], "smoke.txt"), e.ToString()); }
                            finally { form.Close(); }
                        };
                        Application.Run(form);
                    }
                    return exit;
                }
                bool created;
                using (var identity = WindowsIdentity.GetCurrent())
                using (var mutex = new Mutex(true, @"Local\MemSpace-" + identity.User.Value, out created))
                {
                    if (!created) { MessageBox.Show("内存管家已在运行，请从任务栏或托盘打开。", "MemSpace", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "运行错误", MessageBoxButtons.OK, MessageBoxIcon.Error); };
                    Application.Run(new MainForm(AppSettings.Load(), Array.IndexOf(args, "--tray") >= 0, Array.IndexOf(args, "--startup") >= 0, false));
                    mutex.ReleaseMutex();
                }
                return 0;
            } catch (Exception e) {
                if (args.Length == 2 && args[0].StartsWith("--"))
                { try { File.WriteAllText(args[1] + ".error.txt", e.ToString(), Encoding.UTF8); } catch { } }
                else MessageBox.Show(e.Message, "MemSpace 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            } finally { AppIcon.Dispose(); }
        }
        private static Icon MakeIcon()
        {
            using (var image = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(image))
            using (var brush = new SolidBrush(Theme.Accent))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = Theme.Rounded(new RectangleF(1, 1, 30, 30), 7)) g.FillPath(brush, path);
                g.FillRectangle(Brushes.White, 7, 9, 18, 14);
                g.FillRectangle(brush, 10, 12, 4, 8); g.FillRectangle(brush, 18, 12, 4, 8);
                IntPtr handle = image.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
            }
        }
    }
}
