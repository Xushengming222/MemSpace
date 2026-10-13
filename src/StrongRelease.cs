using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace MemoryEase
{
    internal sealed class StrongReleaseResult
    {
        internal MemorySnapshot Before, After;
        internal readonly List<string> Steps = new List<string>();
        internal string Error;
        internal bool Complete { get { return Error == null && Steps.Count == 3; } }
        internal string Summary()
        {
            string state = Complete ? "强力释放完成" : "强力释放未全部完成";
            if (Before == null || After == null) return state + "：" + Error;
            long delta = (long)After.Available - (long)Before.Available;
            return state + " · 可用内存" + (delta >= 0 ? "增加 " : "减少 ") + MemorySnapshot.Size((ulong)Math.Abs(delta)) + " · 当前可用 " + MemorySnapshot.Size(After.Available);
        }
        internal string Details() { return Summary() + "\r\n" + string.Join("\r\n", Steps) + (Error == null ? "" : "\r\n" + Error) + "\r\n\r\n结果为执行后 2 秒的实测变化，程序重新加载数据后可能回升。"; }
    }

    internal interface ISystemReleaseApi : IDisposable
    {
        void EnablePrivilege();
        int Execute(int command);
    }
    // Independent implementation of the three native operations observed in PCL's IL.
    // No process exclusions apply to these system-wide requests.
    internal sealed class SystemReleaseApi : ISystemReleaseApi
    {
        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Id; public uint Attributes; }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string system, string name, out Luid id);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges value, uint length, out TokenPrivileges previous, out uint returned);
        [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);
        private IntPtr token;
        private TokenPrivileges previous;
        private bool restore;
        public void EnablePrivilege()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("需要管理员权限，请允许 Windows 的权限提示。");
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Luid id;
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out id)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new TokenPrivileges { Count = 1, Id = id, Attributes = 2 }; uint length;
            bool ok = AdjustTokenPrivileges(token, false, ref requested, (uint)Marshal.SizeOf(requested), out previous, out length);
            int error = Marshal.GetLastWin32Error(); restore = ok;
            if (!ok || error != 0) throw new Win32Exception(error == 0 ? 1300 : error, "无法启用系统内存操作权限");
        }
        public int Execute(int command)
        {
            if (!restore || command < 2 || command > 4) throw new InvalidOperationException("系统内存操作未授权");
            return NtSetSystemInformation(80, ref command, sizeof(int));
        }
        public void Dispose()
        {
            if (token == IntPtr.Zero) return;
            try { if (restore) { TokenPrivileges ignored; uint length; AdjustTokenPrivileges(token, false, ref previous, (uint)Marshal.SizeOf(previous), out ignored, out length); } }
            finally { Native.CloseHandle(token); token = IntPtr.Zero; restore = false; }
        }
    }
    internal static class StrongRelease
    {
        internal static void RunSteps(ISystemReleaseApi api, StrongReleaseResult result, CancellationToken cancellation)
        {
            using (api) {
                try {
                    cancellation.ThrowIfCancellationRequested(); api.EnablePrivilege();
                    string[] names = { "系统工作集回收", "修改页刷新", "待机列表清空" };
                    for (int command = 2; command <= 4; command++) {
                        cancellation.ThrowIfCancellationRequested(); int code = api.Execute(command);
                        if (code != 0) throw new InvalidOperationException(names[command - 2] + "失败，NTSTATUS 0x" + unchecked((uint)code).ToString("X8"));
                        result.Steps.Add(names[command - 2] + "：成功");
                    }
                } catch (Exception e) { result.Error = e.Message; }
            }
        }
        internal static async Task<StrongReleaseResult> RunAsync(CancellationToken cancellation)
        {
            var result = new StrongReleaseResult { Before = MemorySnapshot.Read() };
            RunSteps(new SystemReleaseApi(), result, cancellation);
            await Task.Delay(2000).ConfigureAwait(false);
            result.After = MemorySnapshot.Read(); return result;
        }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
        internal static string PipeName(Guid nonce) { return "MemSpace-Strong-" + nonce.ToString("N"); }
        // The privileged helper only sends results over a private pipe; it never writes to a user-supplied file path.
        internal static async Task<string> RequestAsync(CancellationToken cancellation, bool probe = false)
        {
            Guid nonce = Guid.NewGuid(); var security = new PipeSecurity();
            using (var identity = WindowsIdentity.GetCurrent()) {
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            using (var pipe = new NamedPipeServerStream(PipeName(nonce), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
            using (cancellation.Register(delegate { pipe.Dispose(); })) {
                var connection = Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null);
                using (var child = await Task.Run(() => Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    (probe ? "--strong-probe " : "--strong-release ") + nonce.ToString("N") + " " + Process.GetCurrentProcess().Id) {
                        UseShellExecute = !probe, CreateNoWindow = probe, Verb = probe ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden }))) {
                    if (child == null) throw new InvalidOperationException("无法启动系统内存操作助手。");
                    var exit = Task.Run(() => child.WaitForExit());
                    if (await Task.WhenAny(connection, exit) != connection && !pipe.IsConnected) throw new InvalidOperationException("系统内存操作助手未能连接，操作未执行（退出码 " + child.ExitCode + "）。");
                    await connection;
                    uint id;
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out id) || id != child.Id) throw new InvalidOperationException("无法验证内存操作助手身份。");
                    string result = await Task.Run(() => { using (var reader = new BinaryReader(pipe, Encoding.UTF8, true)) { int length = reader.ReadInt32(); if (length < 0 || length > 32768) throw new InvalidDataException("结果格式无效"); byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(bytes); } });
                    await exit; if (child.ExitCode != 0) throw new InvalidOperationException(result);
                    return result;
                }
            }
        }
        internal static int Helper(string nonceText, string parentText, bool probe)
        {
            Guid nonce; int parent;
            if (!Guid.TryParseExact(nonceText, "N", out nonce) || !int.TryParse(parentText, out parent) || parent <= 0) return 2;
            try {
                using (var pipe = new NamedPipeClientStream(".", PipeName(nonce), PipeDirection.Out)) {
                    pipe.Connect(10000); uint id;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out id) || id != parent) return 3;
                    using (var process = Process.GetProcessById(parent)) {
                        if (!string.Equals(process.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return 3;
                    }
                    string result = probe ? "PASS: private pipe connected; no memory operations performed" : RunAsync(CancellationToken.None).GetAwaiter().GetResult().Details();
                    byte[] bytes = Encoding.UTF8.GetBytes(result);
                    using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true)) { writer.Write(bytes.Length); writer.Write(bytes); writer.Flush(); }
                    return 0;
                }
            } catch { return 4; }
        }
    }
}
