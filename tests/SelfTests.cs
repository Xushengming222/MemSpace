using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace MemoryEase
{
    internal static class SelfTests
    {
        private static readonly List<string> Results = new List<string>();
        private static void Assert(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("FAIL: " + name); Results.Add("PASS: " + name); }
        internal static string VerifyWindowDpi(Control window)
        {
            IntPtr context = Native.GetWindowDpiAwarenessContext(window.Handle);
            uint dpi = Native.GetDpiForWindow(window.Handle);
            Assert(Native.GetAwarenessFromDpiAwarenessContext(context) == 2,
                "window is per-monitor DPI aware, preventing Windows bitmap enlargement even without an EXE config");
            using (var graphics = window.CreateGraphics())
                Assert(dpi > 0 && Math.Abs(graphics.DpiX - dpi) < 0.01 && Math.Abs(graphics.DpiY - dpi) < 0.01,
                    "window and drawing surface agree on actual monitor DPI");
            return "DPI=" + dpi + Environment.NewLine + "Awareness=" +
                (Native.AreDpiAwarenessContextsEqual(context, new IntPtr(-4)) ? "PerMonitorV2" : "PerMonitor");
        }
        private static ProcessRow Row()
        { return new ProcessRow { Id = 100, Name = "idle-app", OwnerSid = "test-sid", Session = 1, CriticalKnown = true, WorkingSet = 100UL * 1048576 }; }
        private static ProcessRow AppRow(int id, string name, string path, int parent, long creation)
        { var p = Row(); p.Id = id; p.Name = name; p.Path = path; p.ParentId = parent; p.Creation = creation; return p; }
        private sealed class FakeSystemApi : ISystemReleaseApi
        {
            internal readonly List<int> Commands = new List<int>();
            internal bool Denied, Enabled, Disposed; internal int FailAt;
            public void EnablePrivilege() { if (Denied) throw new InvalidOperationException("denied"); Enabled = true; }
            public int Execute(int command) { if (!Enabled) throw new InvalidOperationException(); Commands.Add(command); return command == FailAt ? unchecked((int)0xc0000022) : 0; }
            public void Dispose() { Disposed = true; }
        }
        private static void StrongChecks()
        {
            var api = new FakeSystemApi(); var result = new StrongReleaseResult();
            StrongRelease.RunSteps(api, result, CancellationToken.None);
            Assert(api.Commands.SequenceEqual(new[] { 2, 3, 4 }) && result.Complete && api.Disposed, "strong release runs the three PCL-equivalent operations in order and restores resources");
            api = new FakeSystemApi { FailAt = 3 }; result = new StrongReleaseResult();
            StrongRelease.RunSteps(api, result, CancellationToken.None);
            Assert(api.Commands.SequenceEqual(new[] { 2, 3 }) && result.Steps.Count == 1 && !result.Complete && result.Error.Contains("C0000022") && api.Disposed, "partial system failure stops later operations and reports the NTSTATUS");
            api = new FakeSystemApi { Denied = true }; result = new StrongReleaseResult();
            StrongRelease.RunSteps(api, result, CancellationToken.None);
            Assert(api.Commands.Count == 0 && !result.Complete && api.Disposed, "permission failure prevents all system memory operations");
            api = new FakeSystemApi(); result = new StrongReleaseResult();
            StrongRelease.RunSteps(api, result, new CancellationToken(true));
            Assert(api.Commands.Count == 0 && !api.Enabled && api.Disposed, "cancelled strong release does not adjust privileges or memory");
            result = new StrongReleaseResult { Before = new MemorySnapshot { Available = 1024 }, After = new MemorySnapshot { Available = 512 }, Error = "failed" };
            Assert(result.Summary().Contains("减少") && result.Details().Contains("failed") && !result.Summary().Contains("PCL"), "strong release reports negative deltas, failures and the requested product label honestly");
            Assert(StrongRelease.Helper("invalid", "1", false) == 2, "helper rejects invalid requests before connecting or changing memory");
            Assert(StrongRelease.RequestAsync(CancellationToken.None, true).GetAwaiter().GetResult().StartsWith("PASS:"), "real helper round-trip verifies process identities over a private pipe without memory changes");
            int helper;
            using (var session = StrongSession.AuthorizeAsync(CancellationToken.None, true).GetAwaiter().GetResult()) {
                helper = session.HelperId;
                Assert(session.Ready && session.ReleaseAsync(CancellationToken.None).GetAwaiter().GetResult().Contains("request 1") && session.ReleaseAsync(CancellationToken.None).GetAwaiter().GetResult().Contains("request 2") && helper == session.HelperId,
                    "one authorized session handles multiple requests without repeated elevation (probe performs no memory changes)");
            }
            bool exited = false;
            try { using (var process = Process.GetProcessById(helper)) exited = process.WaitForExit(5000); } catch (ArgumentException) { exited = true; }
            Assert(exited, "closing the session pipe stops the helper");
            Assert(StrongSession.Helper("bad", "1", false) == 2, "persistent helper rejects malformed launch requests");
            bool cancelled = false;
            try { StrongSession.AuthorizeAsync(new CancellationToken(true), true).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "already-cancelled authorization does not start an administrator helper");
            var settings = new AppSettings { AutoStrongRelease = true };
            Assert(AutomaticRelease.Mode(settings, false) == AutomaticMode.Disabled && AutomaticRelease.Mode(settings, true) == AutomaticMode.Strong, "automatic strong release requires both an opt-in and live authorization");
            settings.AutoRelease = true;
            Assert(AutomaticRelease.Mode(settings, false) == AutomaticMode.Background && AutomaticRelease.Mode(settings, true) == AutomaticMode.Strong, "both automatic choices use authorized strong mode first, with background fallback");
            settings.AutoRelease = settings.AutoStrongRelease = false;
            Assert(AutomaticRelease.Mode(settings, true) == AutomaticMode.Disabled, "an authorized helper alone never enables automatic release");
        }
        private static void FeatureChecks()
        {
            var root = AppRow(100, "browser", @"C:\Apps\Browser\browser.exe", 0, 1000);
            var child = AppRow(101, "browser", root.Path, 100, 1100);
            var helper = AppRow(102, "helper", @"C:\Apps\Browser\helpers\helper.exe", 100, 1100);
            var separate = AppRow(103, "browser", @"C:\Other\browser.exe", 0, 1200);
            var foreign = AppRow(104, "browser", root.Path, 100, 1200); foreign.OwnerSid = "foreign";
            var stale = AppRow(105, "old-helper", @"C:\Other\old-helper.exe", 100, 500);
            var peer = AppRow(106, "browser", root.Path, 0, 1300);
            var rows = new List<ProcessRow> { root, child, helper, separate, foreign, stale, peer };
            var groups = ApplicationGroup.Build(rows); var group = groups.Single(g => g.Members.Contains(root));
            Assert(group.Members.Count == 4 && group.Members.Contains(helper) && group.Members.Contains(peer), "application aggregation includes same-image instances and installation helpers");
            Assert(group.WorkingSet == 400UL * 1048576 && group.Private == 0, "application counters aggregate consistently");
            Assert(!group.Members.Contains(separate) && !group.Members.Contains(foreign), "different installations and users stay separate");
            Assert(!ApplicationGroup.ValidParent(stale, root), "parent PID reuse cannot create a false process family");
            var families = new FamilyProtection(rows); families.Protect(helper.Id, "active");
            Assert(new[] { root.Id, child.Id, helper.Id, peer.Id }.All(id => families.Reasons.ContainsKey(id)), "foreground helper protects ancestors, siblings and same-image instances");
            Assert(!families.Reasons.ContainsKey(separate.Id) && !families.Reasons.ContainsKey(foreign.Id) && !families.Reasons.ContainsKey(stale.Id), "family protection respects identity, user and installation boundaries");
            var policy = new ReleasePolicy { Sid = "test-sid", Session = 1, SelfId = 999, ForegroundId = 0, ForegroundName = "", Exclusions = new HashSet<string>(), Families = families };
            Assert(policy.Reason(child) == "active", "release policy applies process-family protection");
            RecentActivity.Remember(root, 100000);
            Assert(RecentActivity.WasRecent(root, 100001), "recent foreground use is retained");
            Assert(!RecentActivity.WasRecent(root, 220001), "recent-use protection expires after two minutes");
            Assert(!RecentActivity.WasRecent(AppRow(root.Id, root.Name, root.Path, 0, root.Creation + 1), 100001), "recent-use history does not carry over to a recycled PID");
            var before = new ProcessRow { Creation = 1000, IoKnown = true, SampleTimestamp = 1 };
            var after = new ProcessRow { Creation = 1000, IoKnown = true, SampleTimestamp = 1 + Stopwatch.Frequency };
            Assert(MemoryService.ActivityReason(before, after) == null, "idle CPU and I/O samples remain eligible");
            after.Cpu = 300000; Assert(MemoryService.ActivityReason(before, after) != null, "CPU activity is evaluated with each process's sample interval"); after.Cpu = 0;
            after.Io.WriteOperations = 1; Assert(MemoryService.ActivityReason(before, after) != null, "even a small ongoing write protects the process"); after.Io.WriteOperations = 0;
            after.Io.ReadBytes = 262144; Assert(MemoryService.ActivityReason(before, after) != null, "sustained reads protect the process"); after.Io.ReadBytes = 0;
            after.IoKnown = false; Assert(MemoryService.ActivityReason(before, after) != null, "unknown I/O activity fails closed");
            var state = new byte[12]; BitConverter.GetBytes(2U).CopyTo(state, 0); Assert(Diagnostics.DecodeStartupState(state) == "已启用", "known Windows enabled state is decoded");
            BitConverter.GetBytes(3U).CopyTo(state, 0); Assert(Diagnostics.DecodeStartupState(state) == "已禁用", "known Windows disabled state is decoded");
            BitConverter.GetBytes(99U).CopyTo(state, 0); Assert(Diagnostics.DecodeStartupState(state).Contains("未知"), "unknown startup states are not guessed");
            Assert(Diagnostics.DecodeStartupState(null) == "未标记" && Diagnostics.DecodeStartupState(new byte[2]) == "未知格式", "missing and malformed startup records remain explicit");
            var serializer = new XmlSerializer(typeof(AppSettings));
            using (var stream = new MemoryStream()) {
                serializer.Serialize(stream, new AppSettings { FontScale = 150, GroupByApplication = false }); stream.Position = 0;
                var restored = (AppSettings)serializer.Deserialize(stream);
                Assert(restored.FontScale == 150 && !restored.GroupByApplication, "font and aggregation choices survive settings serialization");
            }
            using (var reader = new StringReader("<AppSettings><Threshold>85</Threshold></AppSettings>")) {
                var restored = (AppSettings)serializer.Deserialize(reader);
                Assert(restored.FontScale == 100 && restored.GroupByApplication, "older settings receive readable defaults");
            }
            Assert(AppSettings.NormalizeFontScale(200) == 100 && AppSettings.NormalizeFontScale(125) == 125, "font choices are bounded to supported presets");
            var game = AppRow(201, "unknown-title", @"D:\Games\steamapps\common\MyGame\game.exe", 0, 2000);
            var gameHelper = AppRow(202, "game-helper", @"D:\Games\steamapps\common\MyGame\helper.exe", 201, 2100);
            Assert(AutomaticExclusions.Recognized(game) && AutomaticExclusions.Recognized(AppRow(203, "OBS64", "", 0, 2000)), "automatic exclusions recognize known applications and exact installation-directory segments");
            Assert(!AutomaticExclusions.Recognized(AppRow(204, "normal", @"D:\Apps\steamapps-tools\normal.exe", 0, 2000)), "similar directory names are not misclassified as games");
            var systemGame = AppRow(205, "obs64", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "obs64.exe"), 0, 2000);
            Assert(!AutomaticExclusions.Recognized(systemGame), "automatic application detection does not reclassify Windows components");
            var autoRows = new List<ProcessRow> { game, gameHelper }; var protectedFamilies = new FamilyProtection(autoRows);
            AutomaticExclusions.Protect(new AppSettings { Exclusions = "" }, autoRows, protectedFamilies);
            Assert(protectedFamilies.Reasons.ContainsKey(game.Id) && protectedFamilies.Reasons.ContainsKey(gameHelper.Id), "automatic recognition protects the application's whole process family without a saved manual name");
            protectedFamilies = new FamilyProtection(autoRows); AutomaticExclusions.Protect(new AppSettings { AutoExclusions = false }, autoRows, protectedFamilies);
            Assert(protectedFamilies.Reasons.Count == 0, "automatic exclusion detection respects its checkbox");
            var choices = ExclusionChoice.Build(ApplicationGroup.Build(autoRows), "old-app; game-helper");
            Assert(choices.Any(c => c.Names.Contains("old-app") && c.Selected) && choices.Any(c => c.Names.Contains("unknown-title") && c.Selected), "application picker retains unavailable legacy names and checks running application groups");
            var current = choices.Single(c => c.Names.Contains("unknown-title")); current.Selected = false;
            Assert(ReleasePolicy.ParseExclusions(ExclusionChoice.Selection(choices)).SetEquals(new[] { "old-app" }), "unchecking an application removes its members while retaining unrelated exclusions");
            var duplicate = AppRow(206, game.Name, @"D:\OtherInstall\game.exe", 0, 2200);
            choices = ExclusionChoice.Build(ApplicationGroup.Build(new List<ProcessRow> { game, gameHelper, duplicate }), game.Name);
            Assert(choices.Count(c => c.Names.Contains(game.Name)) == 1, "global name exclusions present same-name installations as one checkbox");
            using (var stream = new MemoryStream()) {
                serializer.Serialize(stream, new AppSettings { AutoStrongRelease = true, AutoExclusions = false }); stream.Position = 0;
                var restored = (AppSettings)serializer.Deserialize(stream);
                Assert(restored.AutoStrongRelease && !restored.AutoExclusions && AutomaticRelease.Mode(restored, false) == AutomaticMode.Disabled, "new preferences persist without treating the saved strong checkbox as administrator authorization");
            }
            Assert(!new AppSettings().AutoStrongRelease && new AppSettings().AutoExclusions, "new defaults enable detection and leave automatic strong release off");
        }
        internal static int Run(string output)
        {
            Process worker = null;
            string ready = null;
            try {
                FeatureChecks();
                StrongChecks();
                var p = new ReleasePolicy { Sid = "test-sid", Session = 1, SelfId = 50, ForegroundId = 99, ForegroundName = "browser", Exclusions = ReleasePolicy.ParseExclusions("JAVA.EXE; obs64\r\ncustom") };
                Assert(p.Reason(Row()) == null, "idle user process is eligible");
                var r = Row(); r.Id = 50; Assert(p.Reason(r) != null, "self is protected");
                r = Row(); r.Name = "lsass"; Assert(p.Reason(r) != null, "system processes are protected");
                r = Row(); r.Critical = true; Assert(p.Reason(r) != null, "critical processes are protected");
                r = Row(); r.CriticalKnown = false; Assert(p.Reason(r) != null, "unknown critical status fails closed");
                r = Row(); r.OwnerSid = null; Assert(p.Reason(r) != null, "unknown owner fails closed");
                r = Row(); r.OwnerSid = "another-user"; Assert(p.Reason(r) != null, "other users are protected");
                r = Row(); r.Session = 0; Assert(p.Reason(r) != null, "services are protected");
                r = Row(); r.Id = 99; Assert(p.Reason(r) != null, "foreground process is protected");
                r = Row(); r.Name = "browser"; Assert(p.Reason(r) != null, "foreground same-name children are protected");
                r = Row(); r.Name = "java"; Assert(p.Reason(r) != null, "case-insensitive exe exclusions work");
                r = Row(); r.Visible = true; Assert(p.Reason(r) != null, "visible applications are protected by default");
                r = Row(); p.VisibleNames.Add(r.Name); Assert(p.Reason(r) != null, "same-name child processes of visible applications are protected"); p.VisibleNames.Clear();
                r = Row(); r.Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "component.exe"); Assert(p.Reason(r) != null, "Windows components are protected by path");
                r = Row(); r.Visible = true;
                p.IncludeVisible = true; Assert(p.Reason(r) == null, "explicit selection can include visible windows");
                r.Critical = true; Assert(p.Reason(r) != null, "selection cannot bypass critical process protection");
                var gate = new PressureGate();
                Assert(!gate.Observe(false, 99, 85, 0), "automatic release is opt-in");
                Assert(!gate.Observe(true, 90, 85, 0), "pressure has a dwell time");
                Assert(!gate.Observe(true, 90, 85, 44000), "short spikes do not trigger");
                Assert(gate.Observe(true, 90, 85, 45000), "sustained pressure triggers");
                Assert(!gate.Observe(true, 90, 85, 100000), "cooldown prevents thrashing");
                Assert(!gate.CanAttempt(120000), "startup release also respects the automatic cooldown");
                Assert(gate.Observe(true, 90, 85, 945000), "release becomes available after cooldown");
                gate = new PressureGate(); gate.Observe(true, 90, 85, 0); gate.Observe(true, 70, 85, 40000);
                Assert(!gate.Observe(true, 90, 85, 45000), "pressure resets after recovery");
                var m = MemorySnapshot.Read(); Assert(m.Total > 0 && m.Available <= m.Total, "live physical memory API is valid");
                Assert(m.PerformanceAvailable && m.CommitLimit > 0, "live performance API is valid");
                var rows = MemoryService.Scan(); Assert(rows.Count > 0, "live process enumeration works");
                var own = rows.First(x => x.Id == Process.GetCurrentProcess().Id);
                Assert(own.Creation > 0 && own.OwnerSid != null && own.WorkingSet > 0, "process identity, owner and counters are valid");
                Assert(own.IoKnown && Native.Parents().ContainsKey(own.Id), "live I/O and parent-process snapshots are available");
                var obsolete = new List<ProcessRow> { new ProcessRow { Id = own.Id, Creation = own.Creation - 1 } };
                Assert(MemoryService.ReleaseSelectionAsync(new AppSettings(), obsolete, CancellationToken.None).GetAwaiter().GetResult().Candidates == 0, "stale application selection cannot target a new process identity");
                Assert(new AppSettings().AutoRelease == false && !new AppSettings().StartupRelease, "default settings have no automatic side effects");
                Assert(ReleasePolicy.ParseExclusions(new AppSettings().Exclusions).Contains("VALORANT-Win64-Shipping"), "active gaming workload is excluded by default");
                var synthetic = new ReleaseResult { Before = new MemorySnapshot { Available = 100 }, After = new MemorySnapshot { Available = 50 } };
                Assert(synthetic.AvailableDelta == -50 && synthetic.Summary().Contains("减少"), "negative release deltas are reported honestly");
                string workerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MemSpace.TestWorker.exe");
                ready = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "worker-" + Guid.NewGuid().ToString("N") + ".txt");
                worker = Process.Start(new ProcessStartInfo(workerPath, "\"" + ready + "\"") { UseShellExecute = false, CreateNoWindow = true });
                for (int i = 0; i < 100 && !File.Exists(ready) && !worker.HasExited; i++) Thread.Sleep(100);
                Assert(File.Exists(ready) && !worker.HasExited, "controlled 96 MB worker is ready");
                Thread.Sleep(500);
                var initial = MemoryService.ReadProcess(worker.Id, Native.VisibleProcesses());
                var release = MemoryService.ReleaseAsync(new AppSettings(), worker.Id, CancellationToken.None).GetAwaiter().GetResult();
                if (release.Candidates == 1 && release.Released == 0 && release.Skipped == 1 && release.Failed == 0 && release.Details.Any(d => d.Contains("进程家族"))) {
                    Assert(true, "production release preserves the controlled worker when its host belongs to a protected foreground family");
                    // An interactive host may protect all its descendants. Exercise the native API
                    // directly on our exact 96 MB child without weakening production protections.
                    using (var handle = Native.OpenProcess(Native.Query | Native.SetQuota, false, worker.Id)) {
                        long creation, cpu;
                        Assert(!handle.IsInvalid && Native.Times(handle, out creation, out cpu) && creation == initial.Creation && Native.EmptyWorkingSet(handle), "real EmptyWorkingSet targets the verified controlled child handle only");
                    }
                } else Assert(release.Candidates == 1 && release.Released == 1 && release.Failed == 0, "real EmptyWorkingSet targets only the controlled child: " + string.Join("; ", release.Details));
                var trimmed = MemoryService.ReadProcess(worker.Id, Native.VisibleProcesses());
                Assert(trimmed.WorkingSet < initial.WorkingSet / 2, "controlled worker physical working set decreases");
                File.WriteAllText(ready + ".touch", "touch");
                for (int i = 0; i < 100 && !File.Exists(ready + ".done"); i++) Thread.Sleep(100);
                Assert(File.Exists(ready + ".done") && File.ReadAllText(ready + ".done") == "OK", "worker data survives release and can be paged back in");
                // Creating a control installs the WinForms synchronization context. Do this
                // after blocking async checks, which run without an application message loop.
                using (var window = new Form()) VerifyWindowDpi(window);
                string report = Diagnostics.Report(m, rows, Diagnostics.StartupItems());
                Assert(report.Contains("私有提交") && report.Contains("计划任务"), "diagnostic report includes measurement limits");
                File.WriteAllLines(output, Results, Encoding.UTF8); return 0;
            } catch (Exception e) { Results.Add(e.ToString()); File.WriteAllLines(output, Results, Encoding.UTF8); return 1; }
            finally {
                if (worker != null) { try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(3000); } } catch { } worker.Dispose(); }
                if (ready != null) foreach (string file in new[] { ready, ready + ".touch", ready + ".done" }) try { File.Delete(file); } catch { }
            }
        }
    }
}
