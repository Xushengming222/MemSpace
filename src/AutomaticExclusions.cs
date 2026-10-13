using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoryEase
{
    internal static class AutomaticExclusions
    {
        private static readonly HashSet<string> Known = ReleasePolicy.ParseExclusions(
            "java;javaw;obs64;obs32;audacity;blender;Photoshop;Illustrator;AfterFX;Adobe Premiere Pro;Resolve;devenv;Code;idea64;pycharm64;webstorm64;rider64;WINWORD;EXCEL;POWERPNT;ONENOTE;UnrealEditor;Unity;VALORANT-Win64-Shipping;cs2;dota2;League of Legends;GenshinImpact;StarRail;FortniteClient-Win64-Shipping;recorder");
        private static readonly string[] Directories = { "steamapps", "Epic Games", "Riot Games", ".minecraft", "OBS Studio", "Adobe", "JetBrains", "Blender Foundation" };
        internal static bool Recognized(ProcessRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.Name) || ApplicationGroup.SystemPath(row) || ReleasePolicy.ProtectedNames.Contains(row.Name)) return false;
            if (Known.Contains(row.Name)) return true;
            string path = (row.Path ?? "").Replace('/', '\\');
            return Directories.Any(directory => path.IndexOf("\\" + directory + "\\", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        internal static void Protect(AppSettings settings, List<ProcessRow> rows, FamilyProtection families)
        {
            if (!settings.AutoExclusions) return;
            foreach (var row in rows) if (Recognized(row)) families.Protect(row.Id, "自动识别的游戏或工作软件");
        }
    }
    internal sealed class ExclusionChoice
    {
        internal string Name;
        internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool Selected, Detected;
        public override string ToString() { return Name + (Detected ? " · 自动识别" : ""); }
        internal static List<ExclusionChoice> Build(List<ApplicationGroup> groups, string saved)
        {
            var selected = ReleasePolicy.ParseExclusions(saved);
            var choices = new List<ExclusionChoice>(); var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) {
                if (group.Members.All(p => ApplicationGroup.SystemPath(p) || p.Critical || ReleasePolicy.ProtectedNames.Contains(p.Name))) continue;
                var choice = new ExclusionChoice { Name = group.Name, Detected = group.Members.Any(AutomaticExclusions.Recognized) };
                foreach (var member in group.Members) if (!string.IsNullOrEmpty(member.Name)) choice.Names.Add(member.Name);
                // Settings exclude process names globally. Present shared names once so an
                // unchecked duplicate cannot silently remain selected in another installation.
                var overlaps = choices.Where(c => c.Names.Overlaps(choice.Names)).ToArray();
                foreach (var overlap in overlaps) {
                    choice.Names.UnionWith(overlap.Names); choice.Detected |= overlap.Detected;
                    if (!string.Equals(choice.Name, overlap.Name, StringComparison.OrdinalIgnoreCase)) choice.Name = overlap.Name + " / " + choice.Name;
                    choices.Remove(overlap);
                }
                choice.Selected = choice.Names.Any(selected.Contains); choices.Add(choice); represented.UnionWith(choice.Names);
            }
            foreach (string name in selected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) if (!represented.Contains(name)) {
                var choice = new ExclusionChoice { Name = name + " · 未运行", Selected = true }; choice.Names.Add(name); choices.Add(choice);
            }
            return choices.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        internal static string Selection(IEnumerable<ExclusionChoice> choices)
        { return string.Join("; ", choices.Where(c => c.Selected).SelectMany(c => c.Names).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)); }
    }
}
