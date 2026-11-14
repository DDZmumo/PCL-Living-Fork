using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcArtifactMaterializer {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private async Task<ImmutableArray<MmcNativeFileBinding>> ExtractNativesAsync(IEnumerable<PreparedInput> sources,
        string viewKey, MmcInstancePaths paths, MmcArtifactWorkspace workspace, MmcArtifactIo io,
        Dictionary<string, MmcArtifactStagedMutation> mutations, MmcRuntimeTargetClaims targetClaims,
        ConcurrentDictionary<string, byte>? verifiedDirectories, CancellationToken token) {
        verifiedDirectories ??= MmcInstancePaths.CreatePassDirectoryCache();
        var result = ImmutableArray.CreateBuilder<MmcNativeFileBinding>();
        long extracted = 0; int count = 0;
        foreach (PreparedInput source in sources) {
            token.ThrowIfCancellationRequested();
            MmcInstancePaths.EnsureNoLinks(source.OriginalInput, verifiedDirectories);
            await using var file = new FileStream(source.OriginalInput, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            string before = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            if (before != source.Lock.AdoptedSha256) throw new IOException("Native input changed before extraction.");
            file.Position = 0;
            int entries = NativeZipPreflight(file, limits.MaxNativeEntries - count, limits.MaxNativeBytes - extracted);
            count = checked(count + entries);
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, true, StrictUtf8);
            string[] excludes = source.Artifact.ExtractExcludes.IsDefault ? [] : source.Artifact.ExtractExcludes.Select(x => {
                string prefix = x.Replace('\\', '/');
                MmcArtifactIo.Relative(prefix.TrimEnd('/'));
                paths.ResolveRelative(MmcPathRole.Runtime, viewKey + "/natives/" + prefix.TrimEnd('/'), verifiedDirectories);
                return prefix;
            }).ToArray();
            foreach (ZipArchiveEntry entry in archive.Entries) {
                token.ThrowIfCancellationRequested();
                string relativeEntry = entry.FullName.Replace('\\', '/').TrimEnd('/');
                bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                string relative = "PCL/MmcRuntime/" + viewKey + "/natives/" + relativeEntry;
                string target = paths.ResolveRelative(MmcPathRole.Instance, relative, verifiedDirectories);
                if (directory || excludes.Any(prefix => entry.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))) continue;
                string staged = workspace.NewFile();
                await using (Stream content = entry.Open()) {
                    var observed = await io.ExtractAsync(content, staged, source.Artifact.Key,
                        entry.Length, entry.Crc32, limits.MaxNativeBytes - extracted, token).ConfigureAwait(false);
                    extracted = checked(extracted + observed.Size);
                    if (targetClaims.TryGetValue(relative, out string? old) && old != observed.Sha256)
                        throw new IOException("Different native inputs collide at one runtime target.");
                    targetClaims[relative] = observed.Sha256;
                    string? previous = null;
                    if (MmcInstancePaths.Attributes(target) is not null) previous = (await MmcArtifactIo.HashAsync(target, limits.MaxNativeBytes, token, verifiedDirectories: verifiedDirectories).ConfigureAwait(false)).Sha256;
                    if (previous != observed.Sha256 && !mutations.ContainsKey(relative))
                        AddMutation(paths, mutations, staged, relative, observed.Sha256, observed.Size, previous is null, previous);
                    result.Add(new(source.Artifact.Key, relativeEntry, target, observed.Sha256, observed.Size));
                }
            }
            file.Position = 0;
            string after = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            if (after != source.Lock.AdoptedSha256) throw new IOException("Native input changed during extraction.");
        }
        return result.ToImmutable().DistinctBy(x => x.TargetPath, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static int NativeZipPreflight(Stream stream, int maximumEntries, long maximumOutput) =>
        PreflightArchive(stream, maximumEntries, maximumOutput);

    // Shared index bounds; authorization and actual extraction budgets belong to the caller.
    internal static int PreflightArchive(Stream stream, int maximumEntries, long maximumOutput,
        Encoding? legacyNameEncoding = null, bool allowZip64EntrySizes = false, long? maximumEntryBytes = null) {
        if (!stream.CanSeek || maximumEntries < 0 || maximumOutput < 0 || maximumEntryBytes < 0)
            throw new ArgumentException("Invalid bounded ZIP source.");
        const int MaxCentralBytes = 16 * 1024 * 1024;
        long size = stream.Length;
        int tailLength = (int)Math.Min(size, 65557);
        byte[] tail = new byte[tailLength]; stream.Position = size - tailLength; stream.ReadExactly(tail);
        int end = -1;
        for (int offset = tail.Length - 4; offset >= 0; offset--) if (U32(tail, offset) == 0x06054b50) { end = offset; break; }
        if (end < 0 || tail.Length - end < 22 || end + 22 + U16(tail, end + 20) != tail.Length)
            throw new IOException("Unsupported native ZIP end record or trailing data.");
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new IOException("Multi-disk native ZIP is unsupported.");
        long entries = U16(tail, end + 10), centralBytes = U32(tail, end + 12), centralOffset = U32(tail, end + 16);
        long centralEnd = size - tailLength + end;
        bool sentinel = entries == 0xffff || centralBytes == uint.MaxValue || centralOffset == uint.MaxValue;
        if (centralEnd >= 20) {
            byte[] locator = new byte[20]; stream.Position = centralEnd - 20; stream.ReadExactly(locator);
            if (U32(locator, 0) == 0x07064b50) {
                ulong location = U64(locator, 8);
                if (U32(locator, 4) != 0 || U32(locator, 16) != 1 || location > long.MaxValue || (long)location + 56 != centralEnd - 20)
                    throw new IOException("Unsupported native ZIP64 locator.");
                byte[] record = new byte[56]; stream.Position = (long)location; stream.ReadExactly(record);
                if (U32(record, 0) != 0x06064b50 || U64(record, 4) != 44 || U32(record, 16) != 0 || U32(record, 20) != 0 ||
                    U64(record, 24) != U64(record, 32) || U64(record, 32) > long.MaxValue || U64(record, 40) > long.MaxValue || U64(record, 48) > long.MaxValue)
                    throw new IOException("Unsupported native ZIP64 end record.");
                long n = (long)U64(record, 32), bytes = (long)U64(record, 40), position = (long)U64(record, 48);
                if ((entries != 0xffff && entries != n) || (centralBytes != uint.MaxValue && centralBytes != bytes) ||
                    (centralOffset != uint.MaxValue && centralOffset != position)) throw new IOException("Conflicting native ZIP64 declarations.");
                entries = n; centralBytes = bytes; centralOffset = position; centralEnd = (long)location; sentinel = false;
            }
        }
        if (sentinel) throw new IOException("Native ZIP64 sentinel has no supported locator.");
        if (entries > maximumEntries || centralBytes > MaxCentralBytes || centralOffset > centralEnd || centralBytes != centralEnd - centralOffset)
            throw new IOException("Native ZIP index budget or bounds exceeded.");
        stream.Position = centralOffset;
        long remaining = centralBytes, declaredOutput = 0, rawCharacters = 0, prefixCharacters = 0;
        int count = 0;
        var roles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] header = new byte[46];
        while (remaining != 0) {
            if (remaining < 46) throw new IOException("Truncated native ZIP central header.");
            stream.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50 || ++count > maximumEntries || U16(header, 34) != 0 || (U16(header, 8) & 1) != 0)
                throw new IOException("Native ZIP central header is unsupported or exceeds its entry budget.");
            int nameBytes = U16(header, 28), extraBytes = U16(header, 30), commentBytes = U16(header, 32);
            if (46L + nameBytes + extraBytes + commentBytes > remaining) throw new IOException("Truncated native ZIP central fields.");
            uint external = U32(header, 38);
            if (((external >> 16) & 0xf000) == 0xa000 || (external & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("Native ZIP links are unsupported.");
            byte[] name = new byte[nameBytes]; stream.ReadExactly(name);
            string decoded;
            try { decoded = ((U16(header, 8) & 0x800) != 0 || legacyNameEncoding is null ? StrictUtf8 : legacyNameEncoding).GetString(name).Replace('\\', '/'); }
            catch (DecoderFallbackException error) { throw new IOException("Native ZIP filename encoding is unsupported.", error); }
            bool directory = decoded.EndsWith('/');
            string relative = decoded.TrimEnd('/'); MmcArtifactIo.Relative(relative);
            if (decoded.IndexOf('\0') >= 0) throw new IOException("Native ZIP path contains NUL.");
            rawCharacters += decoded.Length;
            if (rawCharacters > 16 * 1024 * 1024) throw new IOException("Native ZIP path budget exceeded.");
            string[] segments = relative.Split('/');
            int prefixLength = 0;
            for (int index = 0; index < segments.Length; index++) {
                prefixLength += segments[index].Length + (index == 0 ? 0 : 1); prefixCharacters += prefixLength;
                if (prefixCharacters > 32 * 1024 * 1024) throw new IOException("Native ZIP target-prefix budget exceeded.");
                string prefix = string.Join("/", segments, 0, index + 1);
                bool isDirectory = index < segments.Length - 1 || directory;
                if (roles.TryGetValue(prefix, out bool previous) && previous != isDirectory) throw new IOException("Native ZIP file/directory role collision.");
                if (roles.Count >= maximumEntries * 2L && !roles.ContainsKey(prefix)) throw new IOException("Native ZIP implicit node budget exceeded.");
                roles[prefix] = isDirectory;
            }
            if (!directory && !files.Add(relative)) throw new IOException("Native ZIP duplicate file target.");
            ulong length = U32(header, 24);
            bool consumedExtra = false;
            if (length == uint.MaxValue) {
                if (!allowZip64EntrySizes) throw new IOException("ZIP64 native entry output sizes are outside this extraction profile.");
                byte[] extra = new byte[extraBytes]; stream.ReadExactly(extra); consumedExtra = true;
                bool foundSize = false;
                for (int cursor = 0; cursor < extra.Length;) {
                    if (extra.Length - cursor < 4) throw new IOException("Truncated ZIP64 entry extra header.");
                    int id = U16(extra, cursor), bytes = U16(extra, cursor + 2); cursor += 4;
                    if (bytes > extra.Length - cursor) throw new IOException("Truncated ZIP64 entry extra data.");
                    if (id == 1) {
                        if (foundSize || bytes < 8) throw new IOException("Invalid ZIP64 entry size.");
                        length = U64(extra, cursor); foundSize = true;
                    }
                    cursor += bytes;
                }
                if (!foundSize) throw new IOException("ZIP64 entry size is missing.");
            }
            if (length > long.MaxValue || length > (ulong)(maximumEntryBytes ?? maximumOutput)) throw new IOException("ZIP entry size exceeds its bounded profile.");
            declaredOutput = checked(declaredOutput + (long)length);
            if (declaredOutput > maximumOutput) throw new IOException("Native ZIP declared output budget exceeded.");
            stream.Seek((consumedExtra ? 0 : extraBytes) + commentBytes, SeekOrigin.Current);
            remaining -= 46L + nameBytes + extraBytes + commentBytes;
        }
        if (count != entries) throw new IOException("Native ZIP actual entry count differs from its end record.");
        return count;
    }

    private static ushort U16(byte[] value, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset, 2));
    private static uint U32(byte[] value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset, 4));
    private static ulong U64(byte[] value, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(offset, 8));
}
