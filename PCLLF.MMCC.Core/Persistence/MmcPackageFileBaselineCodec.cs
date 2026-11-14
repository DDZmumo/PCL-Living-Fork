// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCLLF.MMCC.Core;

/// <summary>A bounded external author-file baseline. Metadata authority references its hash, not a mutable file list.</summary>
public static class MmcPackageFileBaselineCodec {
    private static readonly HashSet<string> ForeignLauncherFiles = new(StringComparer.OrdinalIgnoreCase) {
        "launcher_profiles.json", "launcher_accounts.json", "accounts.json", "accounts.cfg", "prismlauncher.cfg", "multimc.cfg"
    };

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static MmcPackageBaselineReadResult Read(MmcPackageBaselineInput input, MmcExternalBundleReference expectedReference,
        string expectedPackageSha256, string expectedRelativeRoot, MmcPackageBaselineLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(expectedReference);
        limits ??= new();
        try {
            MmcDefinitionCodec.CheckHash(expectedReference.ContentSha256); MmcDefinitionCodec.CheckHash(expectedPackageSha256);
            MmcDefinitionCodec.MetadataObjectPath(expectedReference.ObjectRelativePath);
            if (!expectedReference.ObjectRelativePath.StartsWith("PCL/MmcSource/", StringComparison.Ordinal) || expectedReference.FormatVersion != 1 || expectedReference.EntryCount < 0 ||
                expectedReference.EntryCount > limits.ArchiveLimits.MaxArchiveEntries || limits.MaxDocumentBytes < 1 || input.Content.Length > limits.MaxDocumentBytes)
                Fail("MMC_PACKAGE_BASELINE_BUDGET", "External package baseline envelope/size is invalid.");
            byte[] bytes = input.Content.ToArray();
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!MmcDefinitionCodec.SameHash(digest, expectedReference.ContentSha256)) Fail("MMC_PACKAGE_BASELINE_DIGEST", "Raw baseline bytes differ from their immutable external reference.");
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) Fail("MMC_PACKAGE_BASELINE_JSON", "Baseline must be an object.");
            int format = 0; string? package = null, root = null;
            ImmutableArray<MmcPackageArchiveDescriptor> entries = default;
            ImmutableArray<MmcPackageFileRecord> files = default;
            HashSet<string> fields = new(StringComparer.Ordinal); long pathCharacters = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                string field = Property(ref reader, fields);
                switch (field) {
                    case "formatVersion": format = checked((int)Integer(ref reader)); break;
                    case "packageSha256": package = String(ref reader, 64); break;
                    case "relativeRoot": root = Path(ref reader, ref pathCharacters, limits); break;
                    case "archiveEntries": entries = Descriptors(ref reader, ref pathCharacters, limits); break;
                    case "files": files = Files(ref reader, ref pathCharacters, limits); break;
                    default: Fail("MMC_PACKAGE_BASELINE_JSON", "Unknown baseline field."); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || fields.Count != 5 || package is null || root is null || entries.IsDefault || files.IsDefault)
                Fail("MMC_PACKAGE_BASELINE_JSON", "Baseline has missing/trailing data.");
            var document = new MmcPackageFileBaseline(format, package, root, entries, files);
            var diagnostics = ValidateInternal(document, limits, out var inspection);
            if (!diagnostics.IsEmpty) return new(null, diagnostics);
            if (!MmcDefinitionCodec.SameHash(package, expectedPackageSha256) || root != expectedRelativeRoot || files.Length != expectedReference.EntryCount)
                Fail("MMC_PACKAGE_BASELINE_IDENTITY", "Baseline does not match the package/root/count authority.");
            return new(new(digest, expectedReference, document, inspection!.GameRootLeaf!), diagnostics);
        } catch (MmcInputException error) { return new(null, [new(error.Code, MmcDiagnosticSeverity.Error, "package-baseline", error.Message)]); }
        catch (Exception error) when (error is JsonException or OverflowException or FormatException or InvalidOperationException) {
            return new(null, [new("MMC_PACKAGE_BASELINE_JSON", MmcDiagnosticSeverity.Error, "package-baseline", "Invalid package baseline JSON: " + error.Message)]);
        }
    }

    public static byte[] Serialize(MmcPackageFileBaseline baseline, MmcPackageBaselineLimits? limits = null) {
        limits ??= new();
        var diagnostics = Validate(baseline, limits);
        if (!diagnostics.IsEmpty) throw new ArgumentException(diagnostics[0].Message, nameof(baseline));
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(baseline, Options);
        if (result.Length > limits.MaxDocumentBytes) throw new ArgumentException("Package baseline exceeds its byte budget.", nameof(baseline));
        return result;
    }

    public static ImmutableArray<MmcDiagnostic> Validate(MmcPackageFileBaseline baseline, MmcPackageBaselineLimits? limits = null) =>
        ValidateInternal(baseline, limits ?? new(), out _);

    private static ImmutableArray<MmcDiagnostic> ValidateInternal(MmcPackageFileBaseline baseline, MmcPackageBaselineLimits limits,
        out MmcArchiveInspection? inspection) {
        inspection = null;
        try {
            if (baseline is null || baseline.FormatVersion != 1 || baseline.ArchiveEntries.IsDefault || baseline.Files.IsDefault || baseline.ArchiveEntries.Length > limits.ArchiveLimits.MaxArchiveEntries || baseline.Files.Length > limits.ArchiveLimits.MaxArchiveEntries)
                Fail("MMC_PACKAGE_BASELINE_BUDGET", "Baseline format/arrays exceed the supported bounds.");
            MmcDefinitionCodec.CheckHash(baseline.PackageSha256);
            if (baseline.ArchiveEntries.Any(e => e is null) || baseline.Files.Any(f => f is null)) Fail("MMC_PACKAGE_BASELINE_JSON", "Null file/entry record.");
            inspection = MmcPackageReader.InspectArchive(baseline.ArchiveEntries.Select(e => new MmcArchiveEntry(e.Path, e.SizeBytes, e.IsDirectory)).ToArray(), limits.ArchiveLimits);
            if (inspection.HasErrors) return inspection.Diagnostics;
            if (inspection.RelativeRoot != baseline.RelativeRoot) Fail("MMC_PACKAGE_BASELINE_IDENTITY", "Baseline root differs from its real unique archive root.");
            Dictionary<string, MmcPackageArchiveDescriptor> descriptors = new(baseline.ArchiveEntries.Length, StringComparer.Ordinal);
            foreach (var entry in baseline.ArchiveEntries) {
                if (entry.IsDirectory) { if (entry.SizeBytes != 0 || entry.Crc32 != 0) Fail("MMC_PACKAGE_BASELINE_IDENTITY", "Directory descriptors cannot contain file payload."); continue; }
                descriptors.Add(entry.Path.Replace('\\', '/'), entry);
            }
            if (descriptors.Count != baseline.Files.Length) Fail("MMC_PACKAGE_BASELINE_IDENTITY", "Every archive file requires exactly one measured baseline record.");
            HashSet<string> seen = new(baseline.Files.Length, StringComparer.Ordinal);
            long paths = baseline.RelativeRoot.Length;
            long estimatedBytes = 1024 + baseline.ArchiveEntries.Length * 128L + baseline.Files.Length * 256L;
            foreach (var entry in baseline.ArchiveEntries) { paths += entry.Path.Length; estimatedBytes += entry.Path.Length * 6L; }
            foreach (var file in baseline.Files) {
                if (file.RelativePath is null || file.RelativePath.Contains('\\') || !seen.Add(file.RelativePath) || !descriptors.TryGetValue(file.RelativePath, out var entry) || entry.SizeBytes != file.SizeBytes || entry.Crc32 != file.Crc32)
                    Fail("MMC_PACKAGE_BASELINE_IDENTITY", "File path/size/CRC does not match its original descriptor.");
                MmcDefinitionCodec.CheckHash(file.Sha256); MmcDefinitionCodec.CheckHash(file.Sha1, 40);
                var expected = Classify(baseline.RelativeRoot, file.RelativePath);
                if (file.Role != expected.Role || file.TargetRelativePath != expected.TargetRelativePath) Fail("MMC_PACKAGE_BASELINE_ROLE", "File role/target is not justified by its package location.");
                paths += file.RelativePath.Length + (file.TargetRelativePath?.Length ?? 0);
                estimatedBytes += 6L * (file.RelativePath.Length + (file.TargetRelativePath?.Length ?? 0));
                if (paths > limits.ArchiveLimits.MaxTotalPathCharacters || estimatedBytes > limits.MaxDocumentBytes) Fail("MMC_PACKAGE_BASELINE_BUDGET", "Baseline name/representation budget exceeded.");
            }
            return [];
        } catch (MmcInputException error) { return [new(error.Code, MmcDiagnosticSeverity.Error, "package-baseline", error.Message)]; }
    }

    public static (MmcPackageFileRole Role, string? TargetRelativePath) Classify(string relativeRoot, string sourceRelativePath) {
        MmcArtifactRules.Relative(sourceRelativePath, "package-baseline");
        if (relativeRoot.Length > 0 && !sourceRelativePath.StartsWith(relativeRoot, StringComparison.OrdinalIgnoreCase)) return (MmcPackageFileRole.InstanceExtra, null);
        string relative = sourceRelativePath[relativeRoot.Length..];
        if (relative.StartsWith("minecraft/", StringComparison.OrdinalIgnoreCase)) return (MmcPackageFileRole.Game, "minecraft" + relative["minecraft".Length..]);
        if (relative.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase)) return (MmcPackageFileRole.Game, ".minecraft" + relative[".minecraft".Length..]);
        if (relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase)) return (MmcPackageFileRole.LocalLibrary, "libraries/" + relative["libraries/".Length..]);
        if (relative.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase) || relative.Equals("instance.cfg", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("patches/", StringComparison.OrdinalIgnoreCase)) return (MmcPackageFileRole.Metadata, null);
        if (relative.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase) || ForeignLauncherFiles.Contains(relative))
            return (MmcPackageFileRole.ForeignLauncherMetadata, null);
        return (MmcPackageFileRole.InstanceExtra, relative);
    }

    private static ImmutableArray<MmcPackageArchiveDescriptor> Descriptors(ref Utf8JsonReader reader, ref long characters, MmcPackageBaselineLimits limits) {
        if (reader.TokenType != JsonTokenType.StartArray) Fail("MMC_PACKAGE_BASELINE_JSON", "archiveEntries must be an array.");
        var result = ImmutableArray.CreateBuilder<MmcPackageArchiveDescriptor>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (result.Count >= limits.ArchiveLimits.MaxArchiveEntries || reader.TokenType != JsonTokenType.StartObject) Fail("MMC_PACKAGE_BASELINE_BUDGET", "Archive descriptor count/shape invalid.");
            uint seenMask = 0; string? path = null; bool directory = false; long size = -1; uint crc = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                int field = MatchProperty(ref reader, ref seenMask, static (ref Utf8JsonReader r) =>
                    r.ValueTextEquals("path"u8) ? 0 :
                    r.ValueTextEquals("isDirectory"u8) ? 1 :
                    r.ValueTextEquals("sizeBytes"u8) ? 2 :
                    r.ValueTextEquals("crc32"u8) ? 3 : -1);
                switch (field) {
                    case 0: path = Path(ref reader, ref characters, limits); break;
                    case 1: if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) Fail("MMC_PACKAGE_BASELINE_JSON", "isDirectory must be Boolean."); directory = reader.GetBoolean(); break;
                    case 2: size = Integer(ref reader); break;
                    case 3: crc = checked((uint)Integer(ref reader)); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seenMask != 0b1111u || path is null) Fail("MMC_PACKAGE_BASELINE_JSON", "Archive descriptor field missing.");
            result.Add(new(path, directory, size, crc));
        }
        if (reader.TokenType != JsonTokenType.EndArray) Fail("MMC_PACKAGE_BASELINE_JSON", "Unterminated archiveEntries array.");
        return result.ToImmutable();
    }
    private static ImmutableArray<MmcPackageFileRecord> Files(ref Utf8JsonReader reader, ref long characters, MmcPackageBaselineLimits limits) {
        if (reader.TokenType != JsonTokenType.StartArray) Fail("MMC_PACKAGE_BASELINE_JSON", "files must be an array.");
        var result = ImmutableArray.CreateBuilder<MmcPackageFileRecord>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
            if (result.Count >= limits.ArchiveLimits.MaxArchiveEntries || reader.TokenType != JsonTokenType.StartObject) Fail("MMC_PACKAGE_BASELINE_BUDGET", "File record count/shape invalid.");
            uint seenMask = 0; string? path = null, target = null, sha256 = null, sha1 = null; MmcPackageFileRole role = default; long size = -1; uint crc = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                int field = MatchProperty(ref reader, ref seenMask, static (ref Utf8JsonReader r) =>
                    r.ValueTextEquals("relativePath"u8) ? 0 :
                    r.ValueTextEquals("role"u8) ? 1 :
                    r.ValueTextEquals("targetRelativePath"u8) ? 2 :
                    r.ValueTextEquals("sha256"u8) ? 3 :
                    r.ValueTextEquals("sha1"u8) ? 4 :
                    r.ValueTextEquals("sizeBytes"u8) ? 5 :
                    r.ValueTextEquals("crc32"u8) ? 6 : -1);
                switch (field) {
                    case 0: path = Path(ref reader, ref characters, limits); break;
                    case 1:
                        string roleName = String(ref reader, 64);
                        if (!Enum.TryParse(roleName, out role) || !Enum.IsDefined(role) || Enum.GetName(role) != roleName) Fail("MMC_PACKAGE_BASELINE_ROLE", "Unknown package file role.");
                        break;
                    case 2: target = reader.TokenType == JsonTokenType.Null ? null : Path(ref reader, ref characters, limits); break;
                    case 3: sha256 = String(ref reader, 64); break;
                    case 4: sha1 = String(ref reader, 40); break;
                    case 5: size = Integer(ref reader); break;
                    case 6: crc = checked((uint)Integer(ref reader)); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || seenMask != 0b111_1111u || path is null || sha256 is null || sha1 is null) Fail("MMC_PACKAGE_BASELINE_JSON", "File checksum/descriptor field missing.");
            result.Add(new(path, role, target, sha256, sha1, size, crc));
        }
        if (reader.TokenType != JsonTokenType.EndArray) Fail("MMC_PACKAGE_BASELINE_JSON", "Unterminated files array.");
        return result.ToImmutable();
    }
    private delegate int PropertyMatcher(ref Utf8JsonReader reader);
    private static int MatchProperty(ref Utf8JsonReader reader, ref uint seenMask, PropertyMatcher match) {
        if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueSpan.Length > 128) Fail("MMC_PACKAGE_BASELINE_JSON", "Expected a bounded property name.");
        int bit = match(ref reader);
        if (bit < 0) Fail("MMC_PACKAGE_BASELINE_JSON", "Unknown package baseline field.");
        uint flag = 1u << bit;
        if ((seenMask & flag) != 0 || !reader.Read()) Fail("MMC_PACKAGE_BASELINE_JSON", "Duplicate/missing package baseline property.");
        seenMask |= flag;
        return bit;
    }
    private static string Property(ref Utf8JsonReader reader, HashSet<string> seen) {
        if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueSpan.Length > 128) Fail("MMC_PACKAGE_BASELINE_JSON", "Expected a bounded property name.");
        string value = reader.GetString()!;
        if (!seen.Add(value) || !reader.Read()) Fail("MMC_PACKAGE_BASELINE_JSON", "Duplicate/missing package baseline property.");
        return value;
    }
    private static string String(ref Utf8JsonReader reader, int maximum) {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > maximum * 6L) Fail("MMC_PACKAGE_BASELINE_JSON", "Expected a bounded string.");
        string result = reader.GetString()!;
        if (result.Length > maximum) Fail("MMC_PACKAGE_BASELINE_BUDGET", "String exceeds baseline budget.");
        return result;
    }
    private static string Path(ref Utf8JsonReader reader, ref long characters, MmcPackageBaselineLimits limits) {
        string result = String(ref reader, 32767); characters += result.Length;
        if (characters > limits.ArchiveLimits.MaxTotalPathCharacters) Fail("MMC_PACKAGE_BASELINE_BUDGET", "Cumulative baseline path budget exceeded.");
        return result;
    }
    private static long Integer(ref Utf8JsonReader reader) { if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value) || value < 0) Fail("MMC_PACKAGE_BASELINE_JSON", "Expected a nonnegative integer."); return reader.GetInt64(); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new MmcInputException(code, "package-baseline", message);
}
