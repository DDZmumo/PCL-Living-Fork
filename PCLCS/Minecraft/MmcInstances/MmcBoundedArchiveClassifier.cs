using System.Buffers.Binary;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
/// <summary>Fixed-memory names-only CDR routing before ZipArchive allocation; no MMC extraction policy is applied to other formats.</summary>
public static class MmcBoundedArchiveClassifier {
    private static readonly string[] OtherRootMarkers = ["manifest.json", "modrinth.index.json", "modpack.json", "modpack.zip", "modpack.mrpack"];
    public static bool IsMmcCandidate(string archivePath) {
        using FileStream file = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 22) throw new InvalidDataException("Archive end record is missing.");
        int tailSize = (int)Math.Min(file.Length, 65557); byte[] tail = new byte[tailSize];
        file.Position = file.Length - tailSize; file.ReadExactly(tail); int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (U32(tail.AsSpan(i)) == 0x06054b50 && i + 22 + U16(tail.AsSpan(i + 20)) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail.AsSpan(end + 4)) != 0 || U16(tail.AsSpan(end + 6)) != 0) throw new InvalidDataException("Unsupported or damaged archive end record.");
        ulong entries = U16(tail.AsSpan(end + 10)), length = U32(tail.AsSpan(end + 12)), offset = U32(tail.AsSpan(end + 16));
        long endPosition = file.Length - tailSize + end;
        if (entries == ushort.MaxValue || length == uint.MaxValue || offset == uint.MaxValue) {
            if (endPosition < 20) throw new InvalidDataException("ZIP64 locator is missing.");
            byte[] locator = new byte[20]; file.Position = endPosition - 20; file.ReadExactly(locator);
            ulong recordOffset = U64(locator.AsSpan(8));
            if (U32(locator) != 0x07064b50 || U32(locator.AsSpan(4)) != 0 || U32(locator.AsSpan(16)) != 1 || recordOffset > (ulong)(endPosition - 56)) throw new InvalidDataException("ZIP64 locator is invalid.");
            byte[] record = new byte[56]; file.Position = (long)recordOffset; file.ReadExactly(record);
            if (U32(record) != 0x06064b50 || U64(record.AsSpan(4)) < 44 || U32(record.AsSpan(16)) != 0 || U32(record.AsSpan(20)) != 0 || U64(record.AsSpan(24)) != U64(record.AsSpan(32))) throw new InvalidDataException("ZIP64 directory is invalid.");
            entries = U64(record.AsSpan(32)); length = U64(record.AsSpan(40)); offset = U64(record.AsSpan(48));
        } else if (U16(tail.AsSpan(end + 8)) != entries) throw new InvalidDataException("Multi-disk archives are unsupported.");
        if (offset > (ulong)file.Length || length > (ulong)file.Length - offset || offset + length > (ulong)endPosition) throw new InvalidDataException("Archive directory exceeds the source bounds.");
        file.Position = (long)offset; ulong remaining = length, count = 0;
        byte[] header = new byte[46], name = new byte[ushort.MaxValue];
        bool rootMmc = false, nestedMmc = false, rootMcbbs = false, otherRoot = false;
        while (remaining != 0) {
            if (remaining < 46) throw new InvalidDataException("Archive directory is truncated.");
            file.ReadExactly(header); if (U32(header) != 0x02014b50) throw new InvalidDataException("Archive directory entry is invalid.");
            int names = U16(header.AsSpan(28)), extra = U16(header.AsSpan(30)), comment = U16(header.AsSpan(32));
            ulong bytes = 46UL + (uint)names + (uint)extra + (uint)comment;
            if (bytes > remaining) throw new InvalidDataException("Archive directory fields exceed their record.");
            file.ReadExactly(name.AsSpan(0, names)); var value = name.AsSpan(0, names);
            if (Matches(value, "mmc-pack.json")) rootMmc = true;
            else if (EndsWithMarker(value)) nestedMmc = true;
            if (Matches(value, "mcbbs.packmeta")) rootMcbbs = true;
            foreach (string marker in OtherRootMarkers) if (Matches(value, marker)) otherRoot = true;
            file.Seek(extra + comment, SeekOrigin.Current); remaining -= bytes; count++;
        }
        if (count != entries) throw new InvalidDataException("Archive directory count is inconsistent.");
        return !rootMcbbs && (rootMmc || nestedMmc && !otherRoot);
    }
    private static bool EndsWithMarker(ReadOnlySpan<byte> value) => value.Length > 13 && value[^14] is (byte)'/' or (byte)'\\' && Matches(value[^13..], "mmc-pack.json");
    private static bool Matches(ReadOnlySpan<byte> value, string ascii) {
        if (value.Length != ascii.Length) return false;
        for (int i = 0; i < value.Length; i++) { byte item = value[i]; if (item is >= (byte)'A' and <= (byte)'Z') item += 32; if (item != ascii[i]) return false; }
        return true;
    }
    private static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    private static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);
}
