using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EditBuddy
{
    // Only the disposable UIA child belongs to this job. MAGIX is never assigned
    // and must remain open when Buddy stops or its owning process exits.
    internal sealed class EditingWorkerLifetime : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct BASIC_LIMITS { public long processTime, jobTime; public uint flags; public UIntPtr minWorkingSet, maxWorkingSet; public uint activeProcesses; public UIntPtr affinity; public uint priority, scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong readOps, writeOps, otherOps, readBytes, writeBytes, otherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct EXTENDED_LIMITS { public BASIC_LIMITS basic; public IO_COUNTERS io; public UIntPtr processMemory, jobMemory, peakProcessMemory, peakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref EXTENDED_LIMITS info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private IntPtr handle;
        internal EditingWorkerLifetime()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the bounded editor worker job.");
            var limits = new EXTENDED_LIMITS(); limits.basic.flags = 0x00002000;
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(EXTENDED_LIMITS)))) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error, "Cannot establish editor worker lifetime ownership."); }
        }
        internal void Attach(IntPtr process)
        {
            if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The editor worker could not be bound to Buddy's lifetime. No request was sent.");
        }
        public void Dispose() { IntPtr current = handle; handle = IntPtr.Zero; if (current != IntPtr.Zero) CloseHandle(current); }
    }
}
