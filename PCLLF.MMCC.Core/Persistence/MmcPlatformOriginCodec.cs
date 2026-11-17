// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCLLF.MMCC.Core;

/// <summary>Bounded API relationships and content proofs. No filesystem, network, accounts or service trust roots.</summary>
public static class MmcPlatformOriginCodec {
    public const int MaximumMetadataBytes = 2 * 1024 * 1024;
    public const int MaximumEvidenceBytes = 8 * 1024 * 1024;
    public const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    private const string Prefix = "PCL/MmcSource/platform-origin/";
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64
    };

    public static MmcPlatformRelease ReadRelease(MmcPlatformProjectIdentity identity, string projectSource, string versionSource,
        string projectJson, string versionJson) {
        try {
            Identity(identity); Address(projectSource); Address(versionSource);
            Budget(projectJson); Budget(versionJson);
            using var project = MmcJson.Read(projectJson, "platform.project", 64);
            using var version = MmcJson.Read(versionJson, "platform.version", 64);
            var p = project.RootElement; var v = version.RootElement;
            var files = ImmutableArray.CreateBuilder<MmcPlatformReleaseFile>();
            if (identity.Platform == "Modrinth") {
                if (String(p, "id") != identity.ProjectId || String(p, "project_type") != "modpack" ||
                    String(v, "project_id") != identity.ProjectId || String(v, "id") != identity.VersionId)
                    Invalid("Modrinth project type, stable ID or version ownership differs from the selection.");
                if (v.TryGetProperty("status", out var status) && status.GetString() is not ("listed" or "archived" or "unlisted"))
                    Invalid("The selected Modrinth version is unavailable.");
                if (!v.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 256)
                    Invalid("The exact version needs a bounded list of release files.");
                foreach (var file in v.GetProperty("files").EnumerateArray()) {
                    if (!file.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) Invalid("Release file hashes are missing.");
                    string? sha1 = OptionalHash(hashes, "sha1", 40), sha512 = OptionalHash(hashes, "sha512", 128);
                    if (sha1 is null && sha512 is null) Invalid("A strong platform digest is required.");
                    files.Add(File(sha512 is null ? "sha1:" + sha1 : "sha512:" + sha512,
                        String(file, "filename"), String(file, "url"), Number(file, "size"), sha1, sha512));
                }
            } else {
                if (Number(p, "id").ToString(CultureInfo.InvariantCulture) != identity.ProjectId || Number(p, "gameId") != 432 || Number(p, "classId") != 4471 ||
                    Number(v, "modId").ToString(CultureInfo.InvariantCulture) != identity.ProjectId || Number(v, "id").ToString(CultureInfo.InvariantCulture) != identity.VersionId || Number(v, "gameId") != 432)
                    Invalid("CurseForge project type, game, stable ID or file ownership differs from the selection.");
                if (!v.TryGetProperty("isAvailable", out var available) || available.ValueKind != JsonValueKind.True ||
                    p.TryGetProperty("isAvailable", out var projectAvailable) && projectAvailable.ValueKind != JsonValueKind.True)
                    Invalid("The selected CurseForge project/file is unavailable.");
                if (!v.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array || hashes.GetArrayLength() > 16) Invalid("File hashes are missing or over budget.");
                var strong = v.GetProperty("hashes").EnumerateArray().Where(h => Number(h, "algo") == 1).Select(h => String(h, "value").ToLowerInvariant()).ToArray();
                if (strong.Length != 1) Invalid("CurseForge requires one exact SHA1; fingerprints and MD5 cannot authorize an origin.");
                Hash(strong[0], 40);
                files.Add(File(identity.VersionId!, String(v, "fileName"), String(v, "downloadUrl"), Number(v, "fileLength"), strong[0], null));
            }
            return new(identity, projectSource, versionSource, projectJson, versionJson, files.ToImmutable());
        } catch (Exception error) when (error is MmcInputException or JsonException or InvalidOperationException or KeyNotFoundException or EncoderFallbackException) {
            throw new ArgumentException("Invalid platform metadata relationships.", nameof(projectJson), error);
        }
    }

    public static MmcPlatformReleaseFile SelectFile(MmcPlatformRelease release, string exactFileId) {
        ArgumentNullException.ThrowIfNull(release);
        var files = release.Files.Where(file => file.FileId == exactFileId).ToArray();
        if (files.Length != 1) throw new ArgumentException("Choose exactly one release file; absent or ambiguous digest matches cannot be selected.", nameof(exactFileId));
        return files[0];
    }
    /// <summary>Explicit file selection can distinguish equal-digest files by their full official descriptor.</summary>
    public static MmcPlatformReleaseFile SelectFile(MmcPlatformRelease release, MmcPlatformReleaseFile selected) {
        ArgumentNullException.ThrowIfNull(release); ArgumentNullException.ThrowIfNull(selected);
        var files = release.Files.Where(file => file == selected).ToArray();
        if (files.Length != 1) throw new ArgumentException("The full selected release file must match exactly one official file.", nameof(selected));
        return files[0];
    }

    public static byte[] Create(MmcPlatformRelease release, string exactFileId, MmcPlatformPackageMeasurement measurement) =>
        Create(release, SelectFile(release, exactFileId), measurement);

    public static byte[] Create(MmcPlatformRelease release, MmcPlatformReleaseFile selected, MmcPlatformPackageMeasurement measurement) {
        // Reparse the raw metadata: a caller-manufactured Files array never grants evidence.
        var parsed = ReadRelease(release.Identity, release.ProjectSource, release.VersionSource, release.ProjectJson, release.VersionJson);
        var file = SelectFile(parsed, selected); Measure(measurement, file);
        var evidence = new MmcPlatformOriginEvidence(1, parsed.Identity, file, measurement, parsed.ProjectSource, parsed.VersionSource, parsed.ProjectJson, parsed.VersionJson);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Options);
        if (bytes.Length > MaximumEvidenceBytes) throw new ArgumentException("Platform evidence exceeds its byte budget.");
        return bytes;
    }

    public static MmcObjectReference Reference(ReadOnlySpan<byte> evidence) {
        if (evidence.Length is < 1 or > MaximumEvidenceBytes) throw new ArgumentException("Platform evidence exceeds its byte budget.");
        string hash = Convert.ToHexStringLower(SHA256.HashData(evidence)); return new(hash, Prefix + hash + ".json");
    }

    public static void ValidateReference(MmcObjectReference reference) {
        if (reference is null || reference.Sha256 is null || reference.Sha256.Length != 64 || reference.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
            reference.ObjectRelativePath != Prefix + reference.Sha256.ToLowerInvariant() + ".json")
            throw new ArgumentException("Platform evidence must use its exact immutable digest path.", nameof(reference));
    }

    public static MmcPlatformOriginEvidence Read(ReadOnlyMemory<byte> bytes, MmcInstanceOrigin origin) {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.PlatformProjectIdentity is null || origin.PlatformEvidenceReference is null) throw new ArgumentException("This origin has no verified platform evidence.");
        ValidateReference(origin.PlatformEvidenceReference);
        if (Reference(bytes.Span) != origin.PlatformEvidenceReference) throw new ArgumentException("Platform evidence bytes differ from the fixed reference.");
        try {
            string text = new UTF8Encoding(false, true).GetString(bytes.Span);
            using var document = MmcJson.Read(text, "platform.evidence", 64);
            var value = JsonSerializer.Deserialize<MmcPlatformOriginEvidence>(document.RootElement, Options) ?? throw new ArgumentException("Evidence is absent.");
            if (value.FormatVersion != 1 || value.Identity != origin.PlatformProjectIdentity || value.Package is null || value.SelectedFile is null ||
                value.Package.Sha256 != origin.PackageSha256) throw new ArgumentException("Evidence belongs to another package or selected identity.");
            var release = ReadRelease(value.Identity, value.ProjectSource, value.VersionSource, value.ProjectJson, value.VersionJson);
            _ = SelectFile(release, value.SelectedFile);
            Measure(value.Package, value.SelectedFile); return value;
        } catch (Exception error) when (error is MmcInputException or JsonException or EncoderFallbackException or InvalidOperationException) {
            throw new ArgumentException("Platform evidence is invalid.", nameof(bytes), error);
        }
    }

    public static void ValidateIdentity(MmcPlatformProjectIdentity identity) => Identity(identity);
    private static void Identity(MmcPlatformProjectIdentity identity) {
        if (identity is null || identity.Platform is not ("Modrinth" or "CurseForge") || string.IsNullOrEmpty(identity.VersionId)) Invalid("An exact supported platform/project/version identity is required.");
        foreach (string? value in new[] { identity.ProjectId, identity.VersionId }) {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c))) Invalid("Invalid stable platform ID.");
            if (identity.Platform == "CurseForge" && (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0 || value != id.ToString(CultureInfo.InvariantCulture))) Invalid("CurseForge IDs must use positive canonical decimal form.");
        }
    }
    private static MmcPlatformReleaseFile File(string id, string name, string url, long size, string? sha1, string? sha512) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl) || size is < 1 or > MaximumPackageBytes) Invalid("Invalid release filename or size.");
        Address(url); return new(id, name, url, size, sha1, sha512);
    }
    private static void Measure(MmcPlatformPackageMeasurement value, MmcPlatformReleaseFile file) {
        if (value is null) throw new ArgumentException("Original ZIP measurement is required.");
        Hash(value.Sha256, 64); Hash(value.Sha1, 40); Hash(value.Sha512, 128);
        if (value.SizeBytes != file.SizeBytes || file.Sha1 is { } sha1 && sha1 != value.Sha1 || file.Sha512 is { } sha512 && sha512 != value.Sha512)
            Invalid("Measured original ZIP size or platform content digest differs from the selected release file.");
    }
    private static void Budget(string value) { if (value is null || new UTF8Encoding(false, true).GetByteCount(value) > MaximumMetadataBytes) Invalid("Platform metadata exceeds its byte budget."); }
    private static string String(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString())) throw new ArgumentException("Missing API field: " + name); return value.GetString()!; }
    private static long Number(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number)) throw new ArgumentException("Missing integer API field: " + name); return number; }
    private static string? OptionalHash(JsonElement element, string name, int length) { if (!element.TryGetProperty(name, out var value)) return null; string hash = value.GetString()?.ToLowerInvariant() ?? ""; Hash(hash, length); return hash; }
    private static void Hash(string value, int length) { if (value is null || value.Length != length || value.Any(c => !char.IsAsciiHexDigit(c)) || value != value.ToLowerInvariant()) Invalid("Invalid canonical content digest."); }
    private static void Address(string value) { if (value is null || value.Length > 4096 || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) Invalid("Invalid platform metadata/file address."); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid(string message) => throw new ArgumentException(message);
}
