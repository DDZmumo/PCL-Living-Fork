using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcProcessImages {
    internal static string Read(Process process) {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) return process.MainModule?.FileName ?? throw new IOException("Process image is unavailable.");
        // The kernel knows the executable before the child's user-mode module list is ready.
        // MainModule can legitimately be null immediately after CreateProcess returns.
        for (int capacity = 512; capacity <= 32768; capacity *= 2) {
            StringBuilder name = new(capacity); uint length = (uint)capacity;
            if (QueryFullProcessImageNameW(process.SafeHandle, 0, name, ref length)) return name.ToString();
            int error = Marshal.GetLastWin32Error();
            if (error != 122) throw new Win32Exception(error, "Cannot observe the process executable image.");
        }
        throw new IOException("Process image path exceeds its budget.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
}
