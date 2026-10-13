using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryEase
{
    internal sealed class MemorySnapshot
    {
        public ulong Total, Available, Installed, Commit, CommitLimit, Cache, PagedPool, NonpagedPool;
        public bool PerformanceAvailable;
        public DateTime At = DateTime.Now;
        public ulong Used { get { return Total - Available; } }
        public double UsedPercent { get { return Total == 0 ? 0 : 100.0 * Used / Total; } }
        public static MemorySnapshot Read()
        {
            var m = new Native.MemoryStatus(); m.Length = (uint)Marshal.SizeOf(m);
            if (!Native.GlobalMemoryStatusEx(ref m)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new MemorySnapshot { Total = m.Total, Available = m.Available };
            ulong installed;
            if (Native.GetPhysicallyInstalledSystemMemory(out installed)) result.Installed = installed * 1024;
            var p = new Native.PerformanceInfo(); p.Size = (uint)Marshal.SizeOf(p);
            if (Native.GetPerformanceInfo(ref p, p.Size))
            {
                ulong page = p.PageSize.ToUInt64();
                result.PerformanceAvailable = true;
                result.Commit = p.CommitTotal.ToUInt64() * page;
                result.CommitLimit = p.CommitLimit.ToUInt64() * page;
                result.Cache = p.SystemCache.ToUInt64() * page;
                result.PagedPool = p.KernelPaged.ToUInt64() * page;
                result.NonpagedPool = p.KernelNonpaged.ToUInt64() * page;
            }
            return result;
        }
        public static string Size(ulong value) { return (value / 1073741824.0).ToString("0.00") + " GB"; }
    }
    internal sealed class ProcessRow
    {
        public int Id, ParentId;
        public uint Session;
        public string Name, Path, OwnerSid;
        public long Creation, Cpu;
        public ulong WorkingSet, Private;
        public bool Critical, CriticalKnown, Visible;
        public bool IoKnown;
        public Native.IoCounters Io;
        public long SampleTimestamp;
    }
    internal sealed class ReleaseResult
    {
        public MemorySnapshot Before, After;
        public int Candidates, Released, Skipped, Failed;
        public readonly List<string> Details = new List<string>();
        public long AvailableDelta { get { return (long)After.Available - (long)Before.Available; } }
        public string Summary()
        {
            return string.Format("已处理 {0} 个程序 · 跳过 {1} · 失败 {2} · 可用内存{3} {4:0.00} GB",
                Released, Skipped, Failed, AvailableDelta >= 0 ? "增加" : "减少", Math.Abs(AvailableDelta / 1073741824.0));
        }
    }
    internal sealed class ReleasePolicy
    {
        internal static readonly HashSet<string> ProtectedNames = new HashSet<string>(new[] {
            "system", "idle", "registry", "secure system", "memory compression", "smss", "csrss", "wininit",
            "winlogon", "lsass", "services", "svchost", "dwm", "explorer", "sihost", "fontdrvhost",
            "msmpeng", "mssense", "securityhealthservice", "audiodg", "taskmgr", "memspace", "memoryease"
        }, StringComparer.OrdinalIgnoreCase);
        public string Sid;
        public uint Session;
        public int SelfId, ForegroundId;
        public string ForegroundName;
        public HashSet<string> Exclusions;
        public HashSet<string> VisibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool IncludeVisible;
        public FamilyProtection Families;
        public string Reason(ProcessRow p)
        {
            if (p.Id <= 4 || p.Id == SelfId || ProtectedNames.Contains(p.Name)) return "系统 / 工具自身";
            if (!string.IsNullOrEmpty(p.Path) && p.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase)) return "Windows 系统组件";
            if (!p.CriticalKnown || p.Critical) return "关键进程或无法验证";
            if (p.OwnerSid == null || p.OwnerSid != Sid || p.Session != Session || p.Session == 0) return "其他用户 / 系统服务";
            string familyReason;
            if (Families != null && Families.Reasons.TryGetValue(p.Id, out familyReason)) return familyReason;
            if (p.Id == ForegroundId || (!string.IsNullOrEmpty(ForegroundName) && string.Equals(p.Name, ForegroundName, StringComparison.OrdinalIgnoreCase))) return "前台程序及同名子进程";
            if (Exclusions.Contains(p.Name)) return "排除名单";
            if (!IncludeVisible && (p.Visible || VisibleNames.Contains(p.Name))) return "有窗口的应用及同名子进程";
            if (p.WorkingSet < 32UL * 1024 * 1024) return "占用低于 32 MB";
            return null;
        }
        internal static HashSet<string> ParseExclusions(string value)
        {
            return new HashSet<string>((value ?? "").Split(new[] { ';', ',', '\r', '\n', '；', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.Length - 4) : x), StringComparer.OrdinalIgnoreCase);
        }
    }
    internal static class MemoryService
    {
        internal static List<ProcessRow> Scan()
        {
            var rows = new List<ProcessRow>();
            var visible = Native.VisibleProcesses();
            var parents = Native.Parents();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { var row = ReadProcess(process.Id, visible); if (row != null) { int parent; if (parents.TryGetValue(row.Id, out parent)) row.ParentId = parent; rows.Add(row); } }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                    catch (ArgumentException) { }
                }
            }
            return rows.OrderByDescending(x => x.WorkingSet).ToList();
        }
        internal static ProcessRow ReadProcess(int id, HashSet<int> visible)
        {
            using (var handle = Native.OpenProcess(Native.Query, false, id))
            {
                if (handle.IsInvalid) return null;
                long creation, cpu;
                if (!Native.Times(handle, out creation, out cpu)) return null;
                var mem = new Native.ProcessMemory(); mem.Size = (uint)Marshal.SizeOf(mem);
                if (!Native.GetProcessMemoryInfo(handle, ref mem, mem.Size)) return null;
                var path = new StringBuilder(1024); uint length = (uint)path.Capacity;
                bool pathKnown = Native.QueryFullProcessImageName(handle, 0, path, ref length);
                if (!pathKnown && Marshal.GetLastWin32Error() == 122) { path = new StringBuilder(32768); length = (uint)path.Capacity; pathKnown = Native.QueryFullProcessImageName(handle, 0, path, ref length); }
                string fullPath = pathKnown ? path.ToString() : "";
                string name = System.IO.Path.GetFileNameWithoutExtension(fullPath);
                if (name.Length == 0) { using (var process = Process.GetProcessById(id)) name = process.ProcessName; }
                bool critical; bool known = Native.IsProcessCritical(handle, out critical);
                uint session; if (!Native.ProcessIdToSessionId(id, out session)) session = uint.MaxValue;
                Native.IoCounters io; bool ioKnown = Native.GetProcessIoCounters(handle, out io);
                return new ProcessRow { Id = id, Name = name, Path = fullPath, Creation = creation, Cpu = cpu,
                    WorkingSet = mem.WorkingSet.ToUInt64(), Private = mem.Private.ToUInt64(), OwnerSid = Native.Owner(handle),
                    Session = session, CriticalKnown = known, Critical = critical, Visible = visible.Contains(id), Io = io, IoKnown = ioKnown, SampleTimestamp = Stopwatch.GetTimestamp() };
            }
        }
        internal static ReleasePolicy Policy(AppSettings settings, bool includeVisible, List<ProcessRow> source = null)
        {
            uint session; Native.ProcessIdToSessionId(Process.GetCurrentProcess().Id, out session);
            int front = Native.ForegroundProcess(); string frontName = "";
            try { using (var p = Process.GetProcessById(front)) frontName = p.ProcessName; } catch { }
            var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visible = Native.VisibleProcesses();
            foreach (int id in visible)
                try { using (var p = Process.GetProcessById(id)) visibleNames.Add(p.ProcessName); } catch { }
            var rows = source ?? Scan(); var families = new FamilyProtection(rows);
            families.ProtectInputs(front, visible, includeVisible, Process.GetCurrentProcess().Id);
            var excluded = ReleasePolicy.ParseExclusions(settings.Exclusions);
            foreach (var row in rows) if (row.Id != Process.GetCurrentProcess().Id) {
                if (excluded.Contains(row.Name)) families.Protect(row.Id, "排除名单中的应用及进程家族");
                else if (RecentActivity.WasRecent(row, RecentActivity.Now)) families.Protect(row.Id, "近 2 分钟使用过的应用家族");
            }
            AutomaticExclusions.Protect(settings, rows, families);
            using (var identity = WindowsIdentity.GetCurrent())
                return new ReleasePolicy { Sid = identity.User.Value, Session = session, SelfId = Process.GetCurrentProcess().Id, VisibleNames = visibleNames, Families = families,
                    ForegroundId = front, ForegroundName = frontName, Exclusions = ReleasePolicy.ParseExclusions(settings.Exclusions), IncludeVisible = includeVisible };
        }
        // CPU is measured over a real interval. A recently busy process is left alone.
        internal static async Task<ReleaseResult> ReleaseAsync(AppSettings settings, int? selectedId, CancellationToken cancellation)
        { return await ReleaseCoreAsync(settings, selectedId, null, cancellation).ConfigureAwait(false); }
        internal static async Task<ReleaseResult> ReleaseSelectionAsync(AppSettings settings, List<ProcessRow> selected, CancellationToken cancellation)
        { return await ReleaseCoreAsync(settings, null, selected, cancellation).ConfigureAwait(false); }
        private static async Task<ReleaseResult> ReleaseCoreAsync(AppSettings settings, int? selectedId, List<ProcessRow> selection, CancellationToken cancellation)
        {
            var before = MemorySnapshot.Read();
            var samples = Scan();
            bool explicitSelection = selectedId.HasValue || selection != null;
            var policy = Policy(settings, explicitSelection, samples);
            var result = new ReleaseResult { Before = before };
            var identities = selection == null ? null : new HashSet<string>(selection.Select(RecentActivity.Identity));
            var targets = samples.Where(p => identities != null ? identities.Contains(RecentActivity.Identity(p)) : !selectedId.HasValue || p.Id == selectedId.Value).ToList();
            result.Candidates = targets.Count;
            targets = targets.Where(p => {
                string reason = policy.Reason(p);
                if (reason == null) return true;
                result.Skipped++; result.Details.Add(p.Name + " (" + p.Id + ") 跳过：" + reason); return false;
            }).ToList();
            await Task.Delay(1200, cancellation).ConfigureAwait(false);
            var liveRows = Scan(); var livePolicy = Policy(settings, explicitSelection, liveRows);
            foreach (var sample in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var current = ReadProcess(sample.Id, Native.VisibleProcesses());
                    string reason = current == null || current.Creation != sample.Creation ? "进程已退出或身份改变" : policy.Reason(current);
                    if (reason == null)
                    {
                        livePolicy.Families.ProtectInputs(Native.ForegroundProcess(), Native.VisibleProcesses(), explicitSelection, livePolicy.SelfId);
                        reason = livePolicy.Reason(current);
                    }
                    if (reason == null) reason = ActivityReason(sample, current);
                    if (reason != null) { result.Skipped++; result.Details.Add(sample.Name + " (" + sample.Id + ") 跳过：" + reason); continue; }
                    using (var handle = Native.OpenProcess(Native.Query | Native.SetQuota, false, current.Id))
                    {
                        if (handle.IsInvalid) { result.Failed++; result.Details.Add(current.Name + " 无权限：" + Marshal.GetLastWin32Error()); continue; }
                        long creation, cpu; bool critical;
                        // Recheck on the exact handle passed to EmptyWorkingSet, so recycled PIDs cannot be targeted.
                        if (!Native.Times(handle, out creation, out cpu) || creation != sample.Creation || Native.Owner(handle) != policy.Sid ||
                            !Native.IsProcessCritical(handle, out critical) || critical)
                        { result.Skipped++; result.Details.Add(current.Name + " 跳过：最终身份校验未通过"); continue; }
                        if (Native.EmptyWorkingSet(handle)) { result.Released++; result.Details.Add(current.Name + " (" + current.Id + ") 工作集释放成功"); }
                        else { result.Failed++; result.Details.Add(current.Name + " 释放失败：" + Marshal.GetLastWin32Error()); }
                    }
                }
                catch (Win32Exception e) { result.Failed++; result.Details.Add(sample.Name + "：" + e.Message); }
                catch (InvalidOperationException) { result.Skipped++; }
                catch (ArgumentException) { result.Skipped++; }
            }
            await Task.Delay(1800, cancellation).ConfigureAwait(false);
            result.After = MemorySnapshot.Read();
            return result;
        }
        internal static string ActivityReason(ProcessRow before, ProcessRow after)
        {
            double seconds = (after.SampleTimestamp - before.SampleTimestamp) / (double)Stopwatch.Frequency;
            if (seconds <= 0 || before.Creation != after.Creation || after.Cpu < before.Cpu) return "无法验证活动采样";
            if ((after.Cpu - before.Cpu) / (double)TimeSpan.TicksPerSecond / seconds > 0.02) return "最近仍在使用 CPU";
            if (!before.IoKnown || !after.IoKnown) return "无法读取 I/O 活动";
            if (after.Io.ReadBytes < before.Io.ReadBytes || after.Io.WriteBytes < before.Io.WriteBytes || after.Io.WriteOperations < before.Io.WriteOperations) return "I/O 采样发生变化";
            if (after.Io.WriteOperations > before.Io.WriteOperations ||
                ((after.Io.ReadBytes - before.Io.ReadBytes) + (after.Io.WriteBytes - before.Io.WriteBytes)) / seconds > 128 * 1024)
                return "最近仍有读写活动";
            return null;
        }
    }
    internal sealed class PressureGate
    {
        private long? pressureSince;
        private long? lastAttempt;
        public bool CanAttempt(long nowMilliseconds) { return !lastAttempt.HasValue || nowMilliseconds - lastAttempt.Value >= 900000; }
        public bool Observe(bool enabled, double percent, double threshold, long nowMilliseconds)
        {
            if (!enabled || percent < threshold) { pressureSince = null; return false; }
            if (!pressureSince.HasValue) pressureSince = nowMilliseconds;
            if (nowMilliseconds - pressureSince.Value < 45000 || !CanAttempt(nowMilliseconds)) return false;
            pressureSince = null; lastAttempt = nowMilliseconds; return true;
        }
        public void MarkManual(long nowMilliseconds) { lastAttempt = nowMilliseconds; pressureSince = null; }
    }
}
