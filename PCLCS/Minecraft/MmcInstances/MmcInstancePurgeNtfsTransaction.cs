using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
// A transacted writer locks the whole NTFS file, including every named data stream.
// Dropping the last transaction handle before Commit rolls back staged deletion on kill.
internal sealed class MmcPurgeNtfsTransaction : IDisposable {
    private readonly SafeFileHandle transaction;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private bool disposed, committed;
    internal MmcPurgeNtfsTransaction() {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Transactional purge requires local Windows NTFS.");
        // Non-promotable, local KTM only. No DTC, service, TxRM or filesystem configuration changes.
        try { transaction = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 1, 0, 0, 0, "PCLLF verified quarantine entry purge"); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) {
            throw new PlatformNotSupportedException("Transactional NTFS is unavailable; ordinary deletion is not a safe fallback.", error);
        }
        if (transaction.IsInvalid) { transaction.Dispose(); throw Failure("Local NTFS transactions are unavailable; no ordinary deletion fallback is permitted."); }
    }
    internal MmcPurgeHandle OpenWriter(string path, bool directory) {
        AssertActive(); return MmcPurgeHandle.OpenTransactedWriter(path, directory, transaction);
    }
    internal void Commit() {
        AssertActive();
        if (!CommitTransaction(transaction)) throw Failure("Verified NTFS entry deletion did not commit; retain it for recovery.");
        committed = true;
    }
    private void AssertActive() {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (thread != Environment.CurrentManagedThreadId || committed) throw new InvalidOperationException("A purge transaction cannot cross threads or be reused after commit.");
    }
    public void Dispose() {
        if (disposed) return;
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("A purge transaction must close on its creating thread.");
        disposed = true;
        if (!committed) _ = RollbackTransaction(transaction);
        transaction.Dispose(); // KTM also rolls back if process termination closes this last handle.
    }
    internal static ImmutableArray<MmcPurgeStreamFact> ReadDataStreams(SafeFileHandle handle, bool directory) {
        byte[] buffer = new byte[64 * 1024];
        if (!GetFileInformationByHandleEx(handle, 7, buffer, (uint)buffer.Length)) {
            int error = Marshal.GetLastWin32Error();
            if (directory && error == 38) return []; // ERROR_HANDLE_EOF: no data streams on a directory.
            throw new IOException("Cannot enumerate a bounded NTFS data-stream inventory from the pinned handle.", new Win32Exception(error));
        }
        var streams = ImmutableArray.CreateBuilder<MmcPurgeStreamFact>(); HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        while (true) {
            if (offset < 0 || offset > buffer.Length - 24 || streams.Count >= 1024) throw new IOException("NTFS stream inventory exceeds its bounds.");
            var entry = buffer.AsSpan(offset); uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry), nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            long size = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            if (directory && offset == 0 && next == 0 && nameBytes == 0 && size == 0) return [];
            if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > entry.Length - 24 || size < 0 || next != 0 && (next < 24 + nameBytes || (next & 7) != 0))
                throw new IOException("NTFS data-stream inventory is incomplete or damaged.");
            string name;
            try { name = new UnicodeEncoding(false, false, true).GetString(entry.Slice(24, checked((int)nameBytes))); }
            catch (DecoderFallbackException error) { throw new IOException("NTFS data-stream name is not unambiguous Unicode.", error); }
            if (name.Contains('\0') || !names.Add(name)) throw new IOException("NTFS data-stream inventory contains an ambiguous or duplicate name.");
            streams.Add(new(name, size));
            if (next == 0) break;
            offset = checked(offset + (int)next);
        }
        return streams.ToImmutable();
    }
    private static IOException Failure(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateTransaction(
        IntPtr security, IntPtr unitOfWork, uint options, uint isolationLevel, uint isolationFlags, uint timeout, string description);
    [DllImport("ktmw32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("ktmw32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RollbackTransaction(SafeFileHandle transaction);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint size);
}
internal sealed record MmcPurgeStreamFact(string Name, long SizeBytes);
