using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MemoryEase
{
    internal sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ProcessHandle() : base(true) { }
        protected override bool ReleaseHandle() { return Native.CloseHandle(handle); }
    }

    internal static class Native
    {
        internal const uint Query = 0x1000, SetQuota = 0x100;
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
        {
            public uint Length, Load;
            public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PerformanceInfo
        {
            public uint Size;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint Handles, Processes, Threads;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessMemory
        {
            public uint Size, Faults;
            public UIntPtr PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonpaged,
                Nonpaged, Pagefile, PeakPagefile, Private;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime
        {
            public uint Low, High;
            public long Value { get { return (long)(((ulong)High << 32) | Low); } }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
        {
            public uint Size, Usage, Id; public UIntPtr Heap;
            public uint Module, Threads, Parent; public int Priority; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
        }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessIoCounters(ProcessHandle handle, out IoCounters counters);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern ProcessHandle CreateToolhelp32Snapshot(uint flags, uint id);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(ProcessHandle handle, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(ProcessHandle handle, ref ProcessEntry entry);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        internal static Dictionary<int, int> Parents()
        {
            var result = new Dictionary<int, int>();
            using (var handle = CreateToolhelp32Snapshot(2, 0))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var entry = new ProcessEntry(); entry.Size = (uint)Marshal.SizeOf(entry);
                if (!Process32First(handle, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                do { result[(int)entry.Id] = (int)entry.Parent; } while (Process32Next(handle, ref entry));
                if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return result;
        }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus value);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);
        [DllImport("psapi.dll", SetLastError = true)] internal static extern bool GetPerformanceInfo(ref PerformanceInfo value, uint size);
        [DllImport("psapi.dll", SetLastError = true)] internal static extern bool GetProcessMemoryInfo(ProcessHandle process, ref ProcessMemory value, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern ProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(ProcessHandle handle, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(ProcessHandle handle, uint flags, StringBuilder text, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessCritical(ProcessHandle handle, out bool critical);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ProcessIdToSessionId(int id, out uint session);
        [DllImport("psapi.dll", SetLastError = true)] internal static extern bool EmptyWorkingSet(ProcessHandle handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(ProcessHandle process, uint access, out IntPtr token);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out int id);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

        internal static string Owner(ProcessHandle process)
        {
            IntPtr token;
            if (!OpenProcessToken(process, 8, out token)) return null;
            try { using (var identity = new WindowsIdentity(token)) return identity.User == null ? null : identity.User.Value; }
            catch { return null; }
            finally { CloseHandle(token); }
        }
        internal static bool Times(ProcessHandle handle, out long creation, out long cpu)
        {
            FileTime c, e, k, u;
            bool ok = GetProcessTimes(handle, out c, out e, out k, out u);
            creation = c.Value; cpu = k.Value + u.Value;
            return ok;
        }
        internal static HashSet<int> VisibleProcesses()
        {
            var result = new HashSet<int>();
            if (!EnumWindows(delegate(IntPtr w, IntPtr p) {
                if (IsWindowVisible(w) && !IsIconic(w)) { int id; GetWindowThreadProcessId(w, out id); result.Add(id); }
                return true;
            }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        internal static int ForegroundProcess()
        {
            int id; GetWindowThreadProcessId(GetForegroundWindow(), out id); return id;
        }
    }
}
