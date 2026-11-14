using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public sealed record MmcStoredPrimaryDefinition(MmcInstanceDefinition Definition, string Sha256);
public sealed record MmcSavedInstanceSources(MmcPackage Package, MmcResolvedSourceBundle Resolved,
    MmcValidatedPackageBaseline Baseline, MmcPackageBaselineInput BaselineInput) {
    public MmcConfiguration PackageConfiguration => Package.Configuration;
}

/// <summary>Reconstructs only from digest-bound immutable source records; never reads the original ZIP or player files.</summary>
public static class MmcSavedSourceStore {
    public static MmcStoredPrimaryDefinition ReadPrimary(MmcInstancePaths paths) => ReadPrimary(paths, CancellationToken.None);
    public static MmcStoredPrimaryDefinition ReadPrimary(MmcInstancePaths paths, CancellationToken cancellationToken) {
        byte[] bytes = ReadBounded(paths, "PCL/InstanceDefinition.json", 16 * 1024 * 1024, cancellationToken);
        MmcDefinitionReadResult result;
        try { result = MmcDefinitionCodec.Read(new UTF8Encoding(false, true).GetString(bytes)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("MMC primary definition is not UTF-8.", error); }
        if (result.Status != MmcDefinitionReadStatus.Valid || result.Definition is null)
            throw new InvalidDataException("MMC primary definition is damaged or unsupported; ordinary repair is forbidden.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(result.Definition, Hash(bytes));
    }

    public static async Task<MmcSavedInstanceSources> LoadAsync(MmcInstancePaths paths, MmcInstanceDefinition definition,
        CancellationToken cancellationToken = default) {
        if (!MmcDefinitionCodec.Validate(definition).IsEmpty) throw new InvalidDataException("A published definition is required to read its sources.");
        var verifiedDirectories = MmcInstancePaths.CreatePassDirectoryCache();
        _ = MmcInstancePackageOwnership.Read(paths, definition);
        _ = MmcPlatformOriginFiles.ReadSaved(paths, definition);
        MmcExternalBundleReference baselineReference = definition.AuthorFileBaseline ?? throw new InvalidDataException("Measured author baseline is missing; source reconstruction needs recovery.");
        byte[] baselineBytes = ReadBounded(paths, baselineReference.ObjectRelativePath, 64 * 1024 * 1024, cancellationToken, verifiedDirectories);
        var baselineInput = new MmcPackageBaselineInput(baselineBytes);
        var read = MmcPackageFileBaselineCodec.Read(baselineInput, baselineReference, definition.Origin.PackageSha256, definition.SourceBundle.RelativeRoot);
        if (read.HasErrors || read.Baseline is null) throw new InvalidDataException("Measured package baseline is damaged: " + string.Join("; ", read.Diagnostics.Select(d => d.Code)));
        var baseline = read.Baseline;
        if (baseline.GameRootLeaf != paths.GameRootLeaf || baseline.GameRootLeaf != definition.Layout.GameRootLeaf)
            throw new InvalidDataException("Saved package layout differs from its published instance.");
        var measured = baseline.Baseline.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        Dictionary<string, string> packageMetadata = new(StringComparer.OrdinalIgnoreCase);
        var adopted = ImmutableArray.CreateBuilder<MmcMetadataSnapshot>(); long total = 0;
        foreach (var record in definition.SourceBundle.MetadataRecords) {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = ReadBounded(paths, record.ObjectRelativePath, 16 * 1024 * 1024, cancellationToken, verifiedDirectories);
            total = checked(total + bytes.LongLength);
            if (total > 128L * 1024 * 1024 || Hash(bytes) != record.ContentSha256)
                throw new InvalidDataException("Saved source metadata digest or cumulative budget is invalid.");
            bool original = record.Kind is MmcSourceRecordKind.Manifest or MmcSourceRecordKind.Configuration or MmcSourceRecordKind.PackageMetadata ||
                record.Kind == MmcSourceRecordKind.Component && record.ComponentSourceKind == MmcComponentSourceKind.PackageLocal;
            if (original) {
                string path = definition.SourceBundle.RelativeRoot + record.RelativePath;
                if (!measured.TryGetValue(path, out var item) || item.Sha256 != record.ContentSha256 || item.SizeBytes != bytes.LongLength)
                    throw new InvalidDataException("Saved author metadata is not a measured member of its original package.");
                try { packageMetadata.Add(path, new UTF8Encoding(false, true).GetString(bytes)); }
                catch (DecoderFallbackException) when (record.Kind == MmcSourceRecordKind.PackageMetadata) { }
            } else {
                string text = new UTF8Encoding(false, true).GetString(bytes);
                adopted.Add(new(record.Uid!, record.Kind == MmcSourceRecordKind.RecommendationIndex ? "" : record.Version!,
                    record.SourceIdentity!, text, record.ContentSha256,
                    record.Kind == MmcSourceRecordKind.RecommendationIndex ? MmcMetadataRequestKind.Recommendation : MmcMetadataRequestKind.Component));
            }
        }
        if (ComputeBundleHash(definition) != definition.SourceBundle.BundleSha256) throw new InvalidDataException("Saved source-bundle identity is inconsistent.");
        var descriptors = baseline.Baseline.ArchiveEntries.Select(entry => new MmcArchiveEntry(entry.Path, entry.SizeBytes, entry.IsDirectory)).ToArray();
        var parsed = MmcPackageReader.Parse(new(descriptors, packageMetadata));
        if (parsed.HasErrors || parsed.Package is null) throw new InvalidDataException("Saved MMC metadata cannot be parsed: " + string.Join("; ", parsed.Diagnostics.Select(d => d.Code)));
        var overrides = ImmutableArray.CreateBuilder<MmcComponentOverride>();
        foreach (var value in definition.ComponentOverrides) {
            cancellationToken.ThrowIfCancellationRequested(); string? patch = null;
            if (value.PatchReference is { } source) {
                byte[] bytes = ReadBounded(paths, source.ObjectRelativePath, 16 * 1024 * 1024, cancellationToken, verifiedDirectories);
                total = checked(total + bytes.LongLength);
                if (Hash(bytes) != source.Sha256 || total > 128L * 1024 * 1024) throw new InvalidDataException("Saved user override is damaged.");
                patch = new UTF8Encoding(false, true).GetString(bytes);
            }
            overrides.Add(new(value.Uid, value.Enabled, value.Version, patch));
        }
        var package = parsed.Package with { PackageLocalScopeSha256 = definition.SourceBundle.PackageLocalScopeSha256 };
        var resolution = MmcComponentResolver.Resolve(package, adopted, overrides);
        if (!resolution.IsComplete || resolution.Profile is null || resolution.Profile.SourceFingerprint != definition.SourceBundle.ResolutionFingerprint)
            throw new InvalidDataException("Saved component resolution differs from the published source fingerprint; no latest metadata fallback is allowed.");
        // Opaque originals are not settings and cannot become execution inputs. They remain exportable
        // source evidence under the measured baseline, independently of a player's current files.
        var metadataPaths = definition.SourceBundle.MetadataRecords.Select(record => definition.SourceBundle.RelativeRoot + record.RelativePath).ToHashSet(StringComparer.Ordinal);
        var opaqueFiles = baseline.Baseline.Files.Where(file => file.TargetRelativePath is null &&
            file.Role != PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary && !metadataPaths.Contains(file.RelativePath)).ToArray();
        if (opaqueFiles.Length > 0) {
            string prefix = "PCL/MmcSource/" + definition.SourceBundle.BundleSha256 + "/package-files/";
            await Task.Run(() => MmcInstancePaths.ForEachBounded(opaqueFiles, (_, file, token) => {
                string fullPath = paths.ResolveRelative(MmcPathRole.Instance, prefix + file.Sha256, verifiedDirectories);
                var observed = MmcArtifactIo.HashFile(fullPath, 4L * 1024 * 1024 * 1024, token, file.Sha1, verifiedDirectories);
                if (observed.Sha256 != file.Sha256 || observed.Size != file.SizeBytes) throw new InvalidDataException("Opaque original source is damaged.");
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        return new(package, new(resolution, adopted.ToImmutable()), baseline, baselineInput);
    }

    internal static byte[] ReadBounded(MmcInstancePaths paths, string relative, int maximumBytes,
        CancellationToken cancellationToken = default, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        cancellationToken.ThrowIfCancellationRequested();
        string path = paths.ResolveRelative(MmcPathRole.Instance, relative, verifiedDirectories);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (input.Length > maximumBytes) throw new InvalidDataException("Preserved source exceeds its byte budget.");
        int initialCapacity = input.CanSeek ? (int)Math.Min(input.Length, maximumBytes) : 0;
        using MemoryStream output = new(initialCapacity);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try {
            int count;
            do {
                cancellationToken.ThrowIfCancellationRequested();
                count = input.Read(buffer, 0, buffer.Length);
                if (output.Length + count > maximumBytes) throw new InvalidDataException("Preserved source grew beyond its byte budget.");
                if (count > 0) output.Write(buffer, 0, count);
            } while (count != 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (initialCapacity > 0 && output.Length == initialCapacity && output.TryGetBuffer(out ArraySegment<byte> seg) &&
                seg.Offset == 0 && seg.Array!.Length == initialCapacity)
                return seg.Array;
            return output.ToArray();
        } finally {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string ComputeBundleHash(MmcInstanceDefinition definition) => Hash(JsonSerializer.SerializeToUtf8Bytes(new {
        Protocol = "PCLLF.MmcSourceBundle/v1", definition.Origin.PackageSha256, definition.SourceBundle.RelativeRoot,
        Inputs = definition.SourceBundle.MetadataRecords.OrderBy(record => record.Kind).ThenBy(record => record.RelativePath, StringComparer.Ordinal)
            .Select(record => new { record.RelativePath, record.Kind, record.Uid, record.Version, SourceKind = record.ComponentSourceKind, record.SourceIdentity, Sha256 = record.ContentSha256 })
    }));
}
