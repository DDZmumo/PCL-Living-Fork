using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcCanonicalPaths {
    // Ordinary instance callers already own/trust their root (including existing root junctions).
    // Resolve aliases for mutual exclusion; descendants are checked separately by configuration paths.
    internal static string ForTrustedRoot(string root) {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!OperatingSystem.IsWindows()) return root;
        Stack<string> missing = new(); string existing = root;
        while (MmcInstancePaths.Attributes(existing) is null) {
            missing.Push(Path.GetFileName(existing)); existing = Path.GetDirectoryName(existing) ?? throw new IOException("无法解析配置根目录。");
        }
        if (!Directory.Exists(existing)) throw new IOException("配置根目录被普通文件占用。");
        string resolved = ResolveExisting(existing);
        while (missing.Count != 0) resolved = Path.Combine(resolved, missing.Pop());
        return Path.TrimEndingDirectorySeparator(resolved);
    }
    internal static string ForAdmission(string root) {
        if (!OperatingSystem.IsWindows()) return root;
        Stack<string> missing = new(); string existing = root;
        while (MmcInstancePaths.Attributes(existing) is null) {
            missing.Push(Path.GetFileName(existing)); existing = Path.GetDirectoryName(existing) ?? throw new IOException("无法解析准入目录。");
        }
        MmcInstancePaths.CheckDirectoryRole(existing);
        string resolved = ResolveExisting(existing);
        while (missing.Count != 0) resolved = Path.Combine(resolved, missing.Pop());
        return MmcInstancePaths.ValidateVersionRoot(resolved);
    }
    internal static string ForExistingFile(string path) {
        MmcInstancePaths.EnsureNoLinks(path);
        if (!OperatingSystem.IsWindows()) return path;
        return ResolveExisting(path);
    }
    private static string ResolveExisting(string existing) {
        using SafeFileHandle handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法取得准入目录的规范路径。", new Win32Exception(Marshal.GetLastWin32Error()));
        return ForHandle(handle);
    }
    // Destructive host operations must validate and act on this same pinned object.
    internal static string ForHandle(SafeFileHandle handle) {
        StringBuilder text = new(512); uint length = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
        if (length == 0) throw new IOException("无法读取准入目录的规范路径。", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= text.Capacity) {
            if (length > 32768) throw new IOException("规范目录路径过长。");
            text = new(checked((int)length + 1)); length = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
            if (length == 0 || length >= text.Capacity) throw new IOException("规范目录路径读取失败。");
        }
        string resolved = text.ToString();
        if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) resolved = "\\\\" + resolved[8..];
        else if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal)) resolved = resolved[4..];
        return resolved;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
