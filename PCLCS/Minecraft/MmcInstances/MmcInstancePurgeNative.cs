using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
// Windows handle identity, hashing and disposition stay together. A path check followed by
// File.Delete would allow replacement between verification and the actual deletion.
internal sealed class MmcPurgeHandle : IDisposable {
    internal SafeFileHandle Handle { get; }
    internal string Path { get; }
    internal bool Directory { get; }
    internal string Identity { get; }
    internal long Size { get; }
    internal bool TransactedWriter { get; }
    private readonly bool encrypted;
    private readonly SafeFileHandle? transaction;
    private MmcPurgeHandle(string path, bool directory, bool delete, bool outerAncestor, bool allowDeleteSharing, SafeFileHandle? transaction = null) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Permanent quarantine cleanup requires verified Windows handles.");
        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); Directory = directory;
        TransactedWriter = transaction is not null;
        this.transaction = transaction;
        uint access = transaction is null ? 0x80u | (directory ? 0u : 0x80000000u) | (delete ? 0x10000u : 0u) : 0xc0010080u;
        // Ancestors outside the owned namespace only deny renames. Owned directories and files
        // also deny writers, including a writer which was opened before this handle.
        string nativePath = NativeLocalPath(Path);
        Handle = transaction is null
            ? CreateFileW(nativePath, access, outerAncestor ? 3u : allowDeleteSharing ? 5u : 1u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)
            : CreateFileTransactedW(nativePath, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero, transaction, IntPtr.Zero, IntPtr.Zero);
        if (Handle.IsInvalid) {
            int error = Marshal.GetLastWin32Error(); Handle.Dispose();
            throw new IOException($"Cannot pin the exact purge object (logical path length {Path.Length}): {Path}", new Win32Exception(error));
        }
        try {
            if (!GetFileInformationByHandle(Handle, out var info)) throw Error("Cannot read purge object identity.");
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
                throw new IOException("Purge object is a link or has changed its directory role.");
            string resolved = MmcCanonicalPaths.ForHandle(Handle);
            if (!SamePath(resolved, Path)) throw new IOException("Pinned purge object resolves outside its exact namespace.");
            var fileSystem = new System.Text.StringBuilder(261);
            if (!GetVolumeInformationByHandleW(Handle, IntPtr.Zero, 0, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity))
                throw Error("Cannot establish the pinned object's local NTFS filesystem.");
            RequireLocalNtfsIdentity(fileSystem.ToString(), resolved, ((ulong)info.IndexHigh << 32) | info.IndexLow);
            Identity = $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}:{info.CreationHigh:x8}{info.CreationLow:x8}";
            Size = directory ? 0 : checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow));
            encrypted = (info.Attributes & (uint)FileAttributes.Encrypted) != 0;
        } catch { Handle.Dispose(); throw; }
    }
    internal static MmcPurgeHandle Open(string path, bool directory, bool delete = false, bool outerAncestor = false, bool allowDeleteSharing = false) => new(path, directory, delete, outerAncestor, allowDeleteSharing);
    internal static MmcPurgeHandle OpenTransactedWriter(string path, bool directory, SafeFileHandle transaction) {
        try { return new(path, directory, true, false, false, transaction); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) {
            throw new PlatformNotSupportedException("Transactional NTFS is unavailable; ordinary delete-pending is not a safe fallback.", error);
        }
    }
    internal string Hash(CancellationToken token) {
        if (Directory || Size > MmcInstancePurgeSnapshot.MaximumBytes) throw new IOException("Purge file exceeds its bounded role.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131072]; long offset = 0; int count;
        while ((count = RandomAccess.Read(Handle, buffer, offset)) != 0) {
            token.ThrowIfCancellationRequested(); offset = checked(offset + count);
            if (offset > MmcInstancePurgeSnapshot.MaximumBytes) throw new IOException("Purge file exceeds the byte budget.");
            hash.AppendData(buffer, 0, count);
        }
        if (offset != Size) throw new IOException("Pinned purge file size changed.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal void Verify(MmcInstancePurgeEntry expected, CancellationToken token) {
        if (Directory != expected.Directory || Identity != expected.FileIdentity || Size != expected.SizeBytes ||
            !Directory && Hash(token) != expected.Sha256) throw new IOException("A planned purge object changed; its current bytes were preserved: " + expected.RelativePath);
    }
    internal void StageTransactedDelete() {
        if (!TransactedWriter) throw new IOException("A plain delete-pending handle cannot provide cross-stream exclusion or rollback on process death.");
        // Every deletion must be enlisted before disposition. Unknown streams cannot be
        // created or changed after this check, including after this file handle is closed.
        AssertDefaultDataStreamOnly();
        if (!SamePath(MmcCanonicalPaths.ForHandle(Handle), Path)) throw new IOException("Pinned purge object moved before disposition.");
        if (Directory) AssertTransactedDirectoryEmpty();
        byte disposition = 1;
        if (!SetFileInformationByHandle(Handle, 4, ref disposition, 1)) throw Error("Exact purge object could not be deleted; remaining content was preserved.");
    }
    // The same transaction must observe children which it already staged for deletion.
    // An outside enumeration still sees those children until CommitTransaction.
    private void AssertTransactedDirectoryEmpty() {
        if (transaction is null) throw new IOException("A directory deletion requires its verified NTFS transaction.");
        IntPtr search = FindFirstFileTransactedW(NativeLocalPath(Path) + "\\*", 1, out var data, 0, IntPtr.Zero, 0, transaction);
        if (search == new IntPtr(-1)) {
            int error = Marshal.GetLastWin32Error();
            if (error == 2) return; // ERROR_FILE_NOT_FOUND: the directory has no visible children.
            throw new IOException("Cannot verify the directory's complete transactional namespace.", new Win32Exception(error));
        }
        try {
            do {
                if (data.Name is not ("." or "..")) throw new IOException("Unplanned content remains in a transactionally approved directory.");
            } while (FindNextFileW(search, out data));
            if (Marshal.GetLastWin32Error() != 18) throw Error("The transactional directory enumeration was incomplete.");
        } finally { _ = FindClose(search); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstFileTransactedW(
        string name, int informationLevel, out FindData data, int searchOperation, IntPtr filter, uint flags, SafeFileHandle transaction);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr search, out FindData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr search);
    internal void AssertDefaultDataStreamOnly() {
        if (encrypted) throw new PlatformNotSupportedException("EFS objects cannot participate in verified transactional quarantine cleanup.");
        var streams = MmcPurgeNtfsTransaction.ReadDataStreams(Handle, Directory);
        if (streams.Any(stream => stream.Name != "::$DATA"))
            throw new IOException("Named NTFS data streams are outside this permanent cleanup preview; preserve the entire object.");
        if (!Directory && (streams.Length != 1 || streams[0].SizeBytes != Size)) throw new IOException("The default NTFS data stream cannot be fully established.");
    }
    internal static bool SamePath(string a, string b) => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    internal static string NativeLocalPath(string logicalPath) {
        // This is only native I/O encoding. All authorization, stored identities and final
        // handle-path comparisons continue to use the checked logical DOS path above.
        if (!System.IO.Path.IsPathFullyQualified(logicalPath) || logicalPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Purge native opens require an absolute local DOS path, without an input device namespace.");
        string full = System.IO.Path.GetFullPath(logicalPath);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\' || full.Length > 32760)
            throw new IOException("Purge native path is outside the supported local Windows path boundary.");
        return "\\\\?\\" + full;
    }
    internal static void RequireLocalNtfsIdentity(string fileSystem, string canonicalPath, ulong fileIndex) {
        // BY_HANDLE_FILE_INFORMATION's 64-bit ID is not sufficient for ReFS. Every
        // opened object, including ancestors and journal files, must pass this gate.
        if (!fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || canonicalPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new PlatformNotSupportedException("Permanent quarantine cleanup supports only verified local NTFS objects; ReFS and network filesystems are unsupported.");
        if (fileIndex is 0 or ulong.MaxValue) throw new IOException("Pinned NTFS object has an unusable zero or all-ones file identity.");
    }
    internal static bool IsIdentity(string? value) => value is { Length: 42 } && value[8] == ':' && value[25] == ':' &&
        value.Where((_, index) => index is not (8 or 25)).All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        ulong.TryParse(value.AsSpan(9, 16), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong identity) &&
        identity is not (0 or ulong.MaxValue);
    private static IOException Error(string text) => new(text, new Win32Exception(Marshal.GetLastWin32Error()));
    public void Dispose() => Handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct Information {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileTransactedW(string name, uint access, uint share, IntPtr security, uint creation, uint flags,
        IntPtr template, SafeFileHandle transaction, IntPtr miniVersion, IntPtr extendedParameter);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, IntPtr volumeName,
        uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint flags,
        System.Text.StringBuilder fileSystemName, uint fileSystemNameSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte information, uint length);
}

/// <summary>Pins every ancestor against rename, without acquiring deletion authority over an ancestor.</summary>
internal sealed class MmcPurgeDirectoryPins : IDisposable {
    private readonly List<MmcPurgeHandle> handles = [];
    internal MmcPurgeHandle Last => handles[^1];
    internal void ReleaseLast() { handles[^1].Dispose(); handles.RemoveAt(handles.Count - 1); }
    internal MmcPurgeDirectoryPins(string directory, bool deleteLast = false, string? alreadyPinnedRoot = null) {
        try {
            var pending = new Stack<string>(); string? cursor = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
            while (cursor is not null && (alreadyPinnedRoot is null || !MmcPurgeHandle.SamePath(cursor, alreadyPinnedRoot))) {
                pending.Push(cursor); cursor = System.IO.Path.GetDirectoryName(cursor);
            }
            if (alreadyPinnedRoot is not null && cursor is null) throw new IOException("Purge parent escaped its pinned root.");
            while (pending.Count != 0) {
                string path = pending.Pop(); bool last = pending.Count == 0;
                handles.Add(MmcPurgeHandle.Open(path, true, last && deleteLast, alreadyPinnedRoot is null && !last));
            }
        } catch { Dispose(); throw; }
    }
    public void Dispose() { for (int index = handles.Count - 1; index >= 0; index--) handles[index].Dispose(); handles.Clear(); }
}
