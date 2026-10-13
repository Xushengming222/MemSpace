using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MemoryEase
{
    internal sealed class ApplicationGroup
    {
        public string Key, Name, Path;
        public readonly List<ProcessRow> Members = new List<ProcessRow>();
        public ulong WorkingSet { get { return Members.Aggregate(0UL, (n, p) => n + p.WorkingSet); } }
        public ulong Private { get { return Members.Aggregate(0UL, (n, p) => n + p.Private); } }
        internal static bool ValidParent(ProcessRow child, ProcessRow parent)
        {
            return parent.Id != child.Id && child.Creation > 0 && parent.Creation > 0 && parent.Creation <= child.Creation &&
                child.OwnerSid != null && child.OwnerSid == parent.OwnerSid && child.Session == parent.Session && parent.Session != 0;
        }
        internal static bool SystemPath(ProcessRow p)
        { return !string.IsNullOrEmpty(p.Path) && p.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase); }
        internal static ProcessRow AppParent(ProcessRow p, Dictionary<int, ProcessRow> rows)
        {
            ProcessRow parent;
            if (!rows.TryGetValue(p.ParentId, out parent) || !ValidParent(p, parent) || SystemPath(parent) || parent.Critical || ReleasePolicy.ProtectedNames.Contains(parent.Name)) return null;
            return parent;
        }
        internal static List<ApplicationGroup> Build(List<ProcessRow> source)
        {
            var rows = source.ToDictionary(p => p.Id); var groups = new Dictionary<string, ApplicationGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in source)
            {
                var root = p; var visited = new HashSet<int>();
                while (visited.Add(root.Id))
                {
                    var parent = AppParent(root, rows);
                    if (parent == null || string.IsNullOrEmpty(parent.Path) || string.IsNullOrEmpty(root.Path)) break;
                    string directory = System.IO.Path.GetDirectoryName(parent.Path);
                    // Only merge differently named helpers inside the parent's application directory.
                    if (directory == null || !root.Path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) break;
                    root = parent;
                }
                string key = string.IsNullOrEmpty(root.Path) || root.OwnerSid == null ? "pid:" + root.Id + ":" + root.Creation : root.Path + "|" + root.OwnerSid + "|" + root.Session;
                ApplicationGroup group;
                if (!groups.TryGetValue(key, out group)) { group = new ApplicationGroup { Key = key, Name = root.Name, Path = root.Path }; groups.Add(key, group); }
                group.Members.Add(p);
            }
            return groups.Values.OrderByDescending(g => g.WorkingSet).ToList();
        }
    }
    internal static class RecentActivity
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, long> ForegroundTimes = new Dictionary<string, long>();
        internal static long Now { get { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); } }
        internal static string Identity(ProcessRow p) { return p.Id + ":" + p.Creation; }
        internal static void Remember(ProcessRow p, long now)
        {
            if (p == null || p.Creation <= 0) return;
            lock (Sync) {
                ForegroundTimes[Identity(p)] = now;
                foreach (string key in ForegroundTimes.Where(x => now - x.Value > 600000).Select(x => x.Key).ToList()) ForegroundTimes.Remove(key);
            }
        }
        internal static bool WasRecent(ProcessRow p, long now)
        { lock (Sync) { long at; return ForegroundTimes.TryGetValue(Identity(p), out at) && now - at <= 120000; } }
        internal static void ObserveForeground()
        {
            try { Remember(MemoryService.ReadProcess(Native.ForegroundProcess(), new HashSet<int>()), Now); } catch { }
        }
    }
    internal sealed class FamilyProtection
    {
        private readonly Dictionary<int, ProcessRow> rows;
        private readonly Dictionary<int, List<ProcessRow>> children;
        private readonly Dictionary<string, List<ProcessRow>> images;
        internal readonly Dictionary<int, string> Reasons = new Dictionary<int, string>();
        internal FamilyProtection(List<ProcessRow> source)
        {
            rows = source.ToDictionary(p => p.Id);
            children = source.GroupBy(p => p.ParentId).ToDictionary(g => g.Key, g => g.ToList());
            images = source.Where(p => !string.IsNullOrEmpty(p.Path) && p.OwnerSid != null).GroupBy(ImageKey).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        private static string ImageKey(ProcessRow p) { return p.Path + "|" + p.OwnerSid + "|" + p.Session; }
        internal void Protect(int id, string reason)
        {
            ProcessRow current; if (!rows.TryGetValue(id, out current)) return;
            var visited = new HashSet<int>();
            while (visited.Add(current.Id)) { var parent = ApplicationGroup.AppParent(current, rows); if (parent == null) break; current = parent; }
            var queue = new Queue<ProcessRow>(); queue.Enqueue(current); visited.Clear();
            while (queue.Count > 0)
            {
                var p = queue.Dequeue(); if (!visited.Add(p.Id)) continue;
                if (!Reasons.ContainsKey(p.Id)) Reasons[p.Id] = reason;
                List<ProcessRow> list;
                if (!string.IsNullOrEmpty(p.Path) && p.OwnerSid != null && images.TryGetValue(ImageKey(p), out list)) foreach (var peer in list) if (!visited.Contains(peer.Id)) queue.Enqueue(peer);
                if (children.TryGetValue(p.Id, out list)) foreach (var child in list) if (ApplicationGroup.ValidParent(child, p)) queue.Enqueue(child);
            }
        }
        internal void ProtectInputs(int foreground, HashSet<int> visible, bool includeVisible, int self)
        {
            if (foreground != self) Protect(foreground, "前台应用及进程家族");
            if (!includeVisible) foreach (int id in visible) if (id != self) Protect(id, "有窗口的应用及进程家族");
        }
    }
}
