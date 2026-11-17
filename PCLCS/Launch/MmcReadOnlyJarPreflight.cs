using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace PCLCS;

// Original LF host implementation under the repository LICENCE.
internal sealed record MmcReadOnlyJarIndex(ImmutableArray<string> Names, long CentralDirectoryBytes,
    long DeclaredOutputBytes, long NameCharacters);

/// <summary>Bounded, read-only Java ZIP namespace. This does not authorize any extraction.</summary>
/// <remarks>Single disk, strict UTF-8 names, stored/deflated entries, bounded ZIP64 end records;
/// per-entry ZIP64 sizes/offsets are unsupported. Repeated ordinary resources are preserved.
/// Duplicate class entries (including required/MR classes) and ambiguous manifests are unsupported.</remarks>
internal static class MmcReadOnlyJarPreflight {
    internal static MmcReadOnlyJarIndex Inspect(Stream stream, int maximumEntries = 100000, long maximumOutput = 4L * 1024 * 1024 * 1024,
        long maximumEntryBytes = 256L * 1024 * 1024) {
        if (!stream.CanSeek || maximumEntries < 0 || maximumOutput < 0 || maximumEntryBytes < 0)
            throw new ArgumentException("Invalid bounded ZIP source.");
        const int MaxCentralBytes = 16 * 1024 * 1024;
        long size = stream.Length;
        int tailLength = (int)Math.Min(size, 65557);
        byte[] tail = new byte[tailLength]; stream.Position = size - tailLength; stream.ReadExactly(tail);
        int end = -1;
        for (int offset = tail.Length - 4; offset >= 0; offset--) if (U32(tail, offset) == 0x06054b50) { end = offset; break; }
        if (end < 0 || tail.Length - end < 22 || end + 22 + U16(tail, end + 20) != tail.Length)
            throw new IOException("Unsupported read-only JAR ZIP end record or trailing data.");
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new IOException("Multi-disk read-only JAR ZIP is unsupported.");
        long entries = U16(tail, end + 10), centralBytes = U32(tail, end + 12), centralOffset = U32(tail, end + 16);
        long centralEnd = size - tailLength + end;
        bool sentinel = entries == 0xffff || centralBytes == uint.MaxValue || centralOffset == uint.MaxValue;
        if (centralEnd >= 20) {
            byte[] locator = new byte[20]; stream.Position = centralEnd - 20; stream.ReadExactly(locator);
            if (U32(locator, 0) == 0x07064b50) {
                ulong location = U64(locator, 8);
                if (U32(locator, 4) != 0 || U32(locator, 16) != 1 || location > long.MaxValue || (long)location + 56 != centralEnd - 20)
                    throw new IOException("Unsupported read-only JAR ZIP64 locator.");
                byte[] record = new byte[56]; stream.Position = (long)location; stream.ReadExactly(record);
                if (U32(record, 0) != 0x06064b50 || U64(record, 4) != 44 || U32(record, 16) != 0 || U32(record, 20) != 0 ||
                    U64(record, 24) != U64(record, 32) || U64(record, 32) > long.MaxValue || U64(record, 40) > long.MaxValue || U64(record, 48) > long.MaxValue)
                    throw new IOException("Unsupported read-only JAR ZIP64 end record.");
                long n = (long)U64(record, 32), bytes = (long)U64(record, 40), position = (long)U64(record, 48);
                if ((entries != 0xffff && entries != n) || (centralBytes != uint.MaxValue && centralBytes != bytes) ||
                    (centralOffset != uint.MaxValue && centralOffset != position)) throw new IOException("Conflicting read-only JAR ZIP64 declarations.");
                entries = n; centralBytes = bytes; centralOffset = position; centralEnd = (long)location; sentinel = false;
            }
        }
        if (sentinel) throw new IOException("Read-only JAR ZIP64 sentinel has no supported locator.");
        if (entries > maximumEntries || centralBytes > MaxCentralBytes || centralOffset > centralEnd || centralBytes != centralEnd - centralOffset)
            throw new IOException("Read-only JAR ZIP index budget or bounds exceeded.");
        stream.Position = centralOffset;
        long remaining = centralBytes, declaredOutput = 0, rawCharacters = 0;
        int count = 0;
        var names = ImmutableArray.CreateBuilder<string>();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        int manifests = 0;
        byte[] header = new byte[46];
        while (remaining != 0) {
            if (remaining < 46) throw new IOException("Truncated read-only JAR ZIP central header.");
            stream.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50 || ++count > maximumEntries || U16(header, 34) != 0 || (U16(header, 8) & 1) != 0)
                throw new IOException("Read-only JAR ZIP central header is unsupported or exceeds its entry budget.");
            int nameBytes = U16(header, 28), extraBytes = U16(header, 30), commentBytes = U16(header, 32);
            if (46L + nameBytes + extraBytes + commentBytes > remaining) throw new IOException("Truncated read-only JAR ZIP central fields.");
            int flags = U16(header, 8), compression = U16(header, 10);
            if ((flags & ~0x080e) != 0 || compression is not (0 or 8) || compression == 0 && (flags & 6) != 0)
                throw new IOException("Read-only JAR encryption, flags or compression is unsupported.");
            if (U32(header, 20) == uint.MaxValue || U32(header, 24) == uint.MaxValue || U32(header, 42) == uint.MaxValue)
                throw new IOException("Read-only JAR entry ZIP64 sizes/offsets are outside this profile.");
            if (U32(header, 42) >= centralOffset || compression == 0 && U32(header, 20) != U32(header, 24))
                throw new IOException("Read-only JAR local offset or stored size is invalid.");
            byte[] name = new byte[nameBytes]; stream.ReadExactly(name);
            string decoded;
            try { decoded = new UTF8Encoding(false, true).GetString(name); }
            catch (DecoderFallbackException error) { throw new IOException("Read-only JAR filename must be UTF-8.", error); }
            // Java ZIP names are literal, case-sensitive resource names. This reader never
            // normalizes Unicode/slashes, creates implicit directories or maps Windows targets.
            // Dot segments, backslashes and Windows-special resource names remain literal.
            if (decoded.Length is 0 or > 32767 || decoded.IndexOf('\0') >= 0 || decoded.Count(c => c == '/') + (decoded.EndsWith('/') ? 0 : 1) > 128)
                throw new IOException("Read-only JAR entry name/depth exceeds its bounded profile.");
            rawCharacters = checked(rawCharacters + decoded.Length);
            if (rawCharacters > 16 * 1024 * 1024) throw new IOException("Read-only JAR name-index budget exceeded.");
            if (decoded.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase) && ++manifests > 1)
                throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "Duplicate/ambiguous JAR manifests are unsupported.");
            if (decoded.EndsWith(".class", StringComparison.Ordinal) && !classes.Add(decoded))
                throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CLASS_AMBIGUOUS", "Duplicate JAR class entries are unsupported; no first/last entry is selected.");
            names.Add(decoded); // Preserve every resource entry, including exact duplicates.
            ulong length = U32(header, 24);
            if (length > long.MaxValue || length > (ulong)maximumEntryBytes) throw new IOException("ZIP entry size exceeds its bounded profile.");
            declaredOutput = checked(declaredOutput + (long)length);
            if (declaredOutput > maximumOutput) throw new IOException("Read-only JAR ZIP declared output budget exceeded.");
            stream.Seek(extraBytes + commentBytes, SeekOrigin.Current);
            remaining -= 46L + nameBytes + extraBytes + commentBytes;
        }
        if (count != entries) throw new IOException("Read-only JAR ZIP actual entry count differs from its end record.");
        return new(names.ToImmutable(), centralBytes, declaredOutput, rawCharacters);
    }

    private static ushort U16(byte[] value, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset, 2));
    private static uint U32(byte[] value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset, 4));
    private static ulong U64(byte[] value, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(offset, 8));
}
