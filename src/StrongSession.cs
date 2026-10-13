using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MemoryEase
{
    internal enum AutomaticMode { Disabled, Background, Strong }
    internal static class AutomaticRelease
    {
        internal static AutomaticMode Mode(AppSettings settings, bool authorized)
        {
            if (settings.AutoStrongRelease && authorized) return AutomaticMode.Strong;
            return settings.AutoRelease ? AutomaticMode.Background : AutomaticMode.Disabled;
        }
    }
    // Authorization belongs to this process session, never to settings.xml. Closing the
    // private duplex pipe makes the idle helper exit, including when its parent crashes.
    internal sealed class StrongSession : IDisposable
    {
        private NamedPipeServerStream pipe;
        private Process child;
        private int disposed;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        internal bool Ready { get { try { return disposed == 0 && pipe != null && pipe.IsConnected && child != null && !child.HasExited; } catch { return false; } } }
        internal static async Task<StrongSession> AuthorizeAsync(CancellationToken cancellation, bool probe = false)
        {
            cancellation.ThrowIfCancellationRequested();
            var session = new StrongSession();
            try {
                var security = new PipeSecurity();
                using (var identity = WindowsIdentity.GetCurrent()) {
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                }
                Guid nonce = Guid.NewGuid();
                session.pipe = new NamedPipeServerStream(StrongRelease.PipeName(nonce), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                using (cancellation.Register(session.Dispose)) {
                    var connection = Task.Factory.FromAsync(session.pipe.BeginWaitForConnection, session.pipe.EndWaitForConnection, null);
                    session.child = await Task.Run(() => Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                        (probe ? "--strong-session-probe " : "--strong-session ") + nonce.ToString("N") + " " + Process.GetCurrentProcess().Id) {
                        UseShellExecute = !probe, CreateNoWindow = probe, Verb = probe ? "" : "runas", WindowStyle = ProcessWindowStyle.Hidden })).ConfigureAwait(false);
                    if (session.child == null) throw new InvalidOperationException("无法启动管理员助手");
                    var ended = new TaskCompletionSource<bool>();
                    session.child.Exited += delegate { ended.TrySetResult(true); }; session.child.EnableRaisingEvents = true;
                    if (session.child.HasExited) ended.TrySetResult(true);
                    var exit = ended.Task;
                    if (await Task.WhenAny(connection, exit).ConfigureAwait(false) != connection && !session.pipe.IsConnected) throw new InvalidOperationException("管理员助手连接失败");
                    await connection.ConfigureAwait(false); uint id;
                    if (!StrongRelease.GetNamedPipeClientProcessId(session.pipe.SafePipeHandle, out id) || id != session.child.Id) throw new InvalidOperationException("管理员助手身份校验失败");
                    string ready = await Task.Run(() => Read(session.pipe)).ConfigureAwait(false);
                    if (ready != "READY") throw new InvalidOperationException(ready);
                    cancellation.ThrowIfCancellationRequested(); return session;
                }
            } catch { session.Dispose(); throw; }
        }
        internal async Task<string> ReleaseAsync(CancellationToken cancellation)
        {
            await operation.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                if (!Ready) throw new InvalidOperationException("管理员授权已失效，请重新授权");
                using (cancellation.Register(Dispose))
                    return await Task.Run(() => { Write(pipe, "release"); return Read(pipe); }).ConfigureAwait(false);
            } catch { Dispose(); throw; }
            finally { operation.Release(); }
        }
        internal static void Write(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write(bytes.Length); writer.Write(bytes); writer.Flush(); }
        }
        internal static string Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
                int length = reader.ReadInt32(); if (length < 0 || length > 32768) throw new InvalidDataException("无效的助手消息");
                byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
                return Encoding.UTF8.GetString(bytes);
            }
        }
        internal static int Helper(string nonceText, string parentText, bool probe)
        {
            Guid nonce; int parent;
            if (!Guid.TryParseExact(nonceText, "N", out nonce) || !int.TryParse(parentText, out parent) || parent <= 0) return 2;
            try {
                using (var pipe = new NamedPipeClientStream(".", StrongRelease.PipeName(nonce), PipeDirection.InOut)) {
                    pipe.Connect(10000); uint id;
                    if (!StrongRelease.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out id) || id != parent) return 3;
                    using (var process = Process.GetProcessById(parent)) if (!string.Equals(process.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return 3;
                    try { if (!probe) using (var api = new SystemReleaseApi()) api.EnablePrivilege(); }
                    catch (Exception e) { Write(pipe, e.Message); return 4; }
                    Write(pipe, "READY"); int sequence = 0;
                    while (true) {
                        string command = Read(pipe);
                        if (command == "stop") return 0;
                        if (command != "release") return 5;
                        string result = probe ? "PASS: session request " + (++sequence) + "; no memory operations" : StrongRelease.RunAsync(CancellationToken.None).GetAwaiter().GetResult().Details();
                        Write(pipe, result);
                    }
                }
            } catch (EndOfStreamException) { return 0; }
            catch (IOException) { return 0; }
            catch { return 4; }
        }
        internal int HelperId { get { return child.Id; } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (pipe != null) pipe.Dispose();
            // Do not kill a helper in the middle of a native call. EOF ends its request loop.
            if (child != null) child.Dispose();
        }
    }
}
