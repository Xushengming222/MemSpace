using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace MemoryEase
{
    public sealed class AppSettings
    {
        public bool AutoRelease;
        public bool AutoStrongRelease;
        public bool AutoExclusions = true;
        public bool StartupRelease;
        public int Threshold = 85;
        public int FontScale = 100;
        public bool GroupByApplication = true;
        public string Exclusions = "java; javaw; obs64; obs32; steam; steamwebhelper; devenv; Code; Photoshop; audacity; blender; UnrealEditor; VALORANT-Win64-Shipping; recorder; EpicWebHelper; codex; ChatGPT";
        internal static string DataDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemSpace"); } }
        internal static AppSettings Load()
        {
            try {
                using (var stream = File.OpenRead(Path.Combine(DataDirectory, "settings.xml")))
                {
                    var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
                    settings.Threshold = Math.Max(70, Math.Min(95, settings.Threshold));
                    settings.FontScale = NormalizeFontScale(settings.FontScale);
                    return settings;
                }
            } catch { return new AppSettings(); }
        }
        internal static int NormalizeFontScale(int value) { return value == 125 || value == 150 ? value : 100; }
        internal void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            string target = Path.Combine(DataDirectory, "settings.xml"), temp = target + ".tmp";
            using (var file = File.Create(temp)) new XmlSerializer(typeof(AppSettings)).Serialize(file, this);
            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
        }
    }
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "MemSpace";
        internal static bool Enabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(Name) != null;
        }
        internal static void Set(bool enabled, string exe)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                if (enabled) key.SetValue(Name, "\"" + exe + "\" --tray --startup", RegistryValueKind.String);
                else key.DeleteValue(Name, false);
        }
    }
    internal sealed class StartupItem
    {
        public string Name, Source, Command, State;
    }
    internal static class Diagnostics
    {
        internal static List<StartupItem> StartupItems()
        {
            var list = new List<StartupItem>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                        if (key != null) foreach (string name in key.GetValueNames())
                            list.Add(new StartupItem { Name = name, Source = (hive == RegistryHive.CurrentUser ? "当前用户" : "所有用户") + " / " + view, State = StartupState(hive, name, view == RegistryView.Registry32 ? "Run32" : "Run"),
                                Command = Convert.ToString(key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)) });
                } catch (System.Security.SecurityException) { list.Add(new StartupItem { Name = "无法读取启动注册表", Source = hive + " / " + view, Command = "访问被拒绝" }); }
                  catch (UnauthorizedAccessException) { list.Add(new StartupItem { Name = "无法读取启动注册表", Source = hive + " / " + view, Command = "访问被拒绝" }); }
            }
            foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                string path = Environment.GetFolderPath(folder);
                try { if (Directory.Exists(path)) foreach (string file in Directory.GetFiles(path))
                    if (!string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        list.Add(new StartupItem { Name = Path.GetFileName(file), Source = folder == Environment.SpecialFolder.Startup ? "用户启动文件夹" : "公共启动文件夹", Command = file,
                            State = StartupState(folder == Environment.SpecialFolder.Startup ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, Path.GetFileName(file), "StartupFolder") });
                } catch (UnauthorizedAccessException) { list.Add(new StartupItem { Name = "无法读取启动文件夹", Source = path, Command = "访问被拒绝" }); }
                  catch (IOException) { }
            }
            return list.GroupBy(x => x.Name + "\n" + x.Command).Select(g => g.First()).OrderBy(x => x.Name).ToList();
        }
        internal static string DecodeStartupState(byte[] value)
        {
            if (value == null) return "未标记";
            if (value.Length < 12) return "未知格式";
            uint state = BitConverter.ToUInt32(value, 0);
            return state == 2 ? "已启用" : state == 3 ? "已禁用" : "未知状态 (" + state + ")";
        }
        private static string StartupState(RegistryHive hive, string name, string section)
        {
            try {
                using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + section))
                    return DecodeStartupState(key == null ? null : key.GetValue(name) as byte[]);
            } catch { return "无法读取"; }
        }
        internal static string Advice(MemorySnapshot m)
        {
            var lines = new List<string>();
            if (m.UsedPercent < 75) lines.Add("目前可用内存较充足。Windows 会按需回收缓存，低占用数字本身不代表更快。");
            else lines.Add("内存压力偏高。先看占用排行，退出不需要的软件，再检查它的开机启动设置。");
            if (m.NonpagedPool > 1073741824UL) lines.Add("非分页池超过 1 GB，可进一步排查网卡、显卡等驱动。工作集释放无法回收驱动占用。");
            if (m.PerformanceAvailable && m.CommitLimit > 0 && m.Commit / (double)m.CommitLimit > 0.85)
                lines.Add("已提交内存接近上限。建议检查内存持续增长的程序，并在系统设置中保持分页文件自动管理。");
            if (m.Installed > m.Total && m.Installed - m.Total > 1073741824UL)
                lines.Add("硬件保留内存超过 1 GB，可查看 BIOS 的核显共享内存设置；本工具无法释放硬件保留内存。");
            lines.Add("想降低开机常驻占用：禁用不需要的第三方自启动项，重启后在相同条件下比较。保留安全软件和驱动组件。");
            return string.Join("\r\n\r\n", lines);
        }
        internal static string ShortAdvice(MemorySnapshot m)
        {
            if (m.PerformanceAvailable && m.CommitLimit > 0 && m.Commit / (double)m.CommitLimit > 0.85) return "已提交内存接近上限";
            if (m.NonpagedPool > 1073741824UL) return "非分页池偏高";
            return m.UsedPercent >= 85 ? "内存占用较高" : "内存状态正常";
        }
        internal static string Report(MemorySnapshot m, List<ProcessRow> processes, List<StartupItem> startup)
        {
            var b = new StringBuilder();
            b.AppendLine("MemSpace · 内存诊断报告").AppendLine("时间：" + m.At.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            b.AppendLine("系统可用总内存：" + MemorySnapshot.Size(m.Total));
            b.AppendLine("已安装：" + (m.Installed == 0 ? "未知" : MemorySnapshot.Size(m.Installed)));
            b.AppendLine("使用中：" + MemorySnapshot.Size(m.Used) + " / 可用：" + MemorySnapshot.Size(m.Available));
            b.AppendLine("已提交：" + MemorySnapshot.Size(m.Commit) + " / 上限：" + MemorySnapshot.Size(m.CommitLimit));
            b.AppendLine("系统缓存：" + MemorySnapshot.Size(m.Cache) + " / 非分页池：" + MemorySnapshot.Size(m.NonpagedPool));
            b.AppendLine("\r\n" + Advice(m));
            b.AppendLine("\r\n进程排行（工作集包含共享页，不能简单相加；私有提交量不是物理内存）：");
            foreach (var p in processes.Take(60)) b.AppendLine(string.Format("{0,-28} PID {1,-7} 工作集 {2,9:0.0} MB / 私有提交 {3,9:0.0} MB\r\n  {4}", p.Name, p.Id, p.WorkingSet / 1048576.0, p.Private / 1048576.0, p.Path));
            b.AppendLine("\r\n常见启动注册项与启动文件夹（可能包含已被 Windows 禁用的项；不包含服务、计划任务和所有打包应用，请在系统启动设置核实）：");
            foreach (var s in startup) b.AppendLine(s.Name + " [" + s.Source + "] [" + (s.State ?? "无法读取") + "]\r\n  " + s.Command);
            b.AppendLine("StartupApproved 只识别已知的 Windows 状态格式；未标记、未知或无法读取的项请在 Windows 启动设置核实。");
            b.AppendLine("\r\n采样时无法读取的受保护进程可能不在列表中。报告包含本机程序路径，请自行决定分享范围。");
            return b.ToString();
        }
    }
}
