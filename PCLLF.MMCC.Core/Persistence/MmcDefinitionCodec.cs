// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCLLF.MMCC.Core;

/// <summary>Pure structural persistence. Unknown or damaged managed data never means a legacy instance.</summary>
public static class MmcDefinitionCodec {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static MmcDefinitionReadResult Read(string json, MmcReadLimits? limits = null) {
        limits ??= new();
        try {
            if (json is null || StrictUtf8.GetByteCount(json) > limits.MaxMetadataBytes)
                throw new MmcInputException("MMC_DEFINITION_BUDGET", "definition", "Definition JSON exceeds its byte budget.");
            using var document = MmcJson.Read(json, "definition", Math.Min(64, limits.MaxDepth));
            JsonElement root = document.RootElement;
            int number = 0;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out number))
                MmcJson.Fail("definition", "Missing numeric schemaVersion.");
            if (number != 1 || MmcJson.String(root, "managementKind", "definition", true) != "Mmc" ||
                MmcJson.String(root, "formatProfile", "definition", true) != "prism-v1")
                return new(null, MmcDefinitionReadStatus.Unsupported, [new("MMC_DEFINITION_UNSUPPORTED", MmcDiagnosticSeverity.Error, "definition", "Unsupported managed definition schema/kind/profile.")]);
            ValidateGuidJson(root, "instanceId", true);
            ValidateGuidJson(root, "parentInstanceId", false);
            ValidateEnvironmentJson(root);
            var definition = JsonSerializer.Deserialize<MmcInstanceDefinition>(root, Options);
            if (definition is null) MmcJson.Fail("definition", "Missing definition object.");
            var diagnostics = Validate(definition, MmcDefinitionValidationMode.Published, limits);
            return new(diagnostics.IsEmpty ? definition : null, diagnostics.IsEmpty ? MmcDefinitionReadStatus.Valid : MmcDefinitionReadStatus.Invalid, diagnostics);
        } catch (MmcInputException error) {
            return Invalid(error.Code, error.Message);
        } catch (Exception error) when (error is JsonException or EncoderFallbackException or ArgumentException or InvalidOperationException) {
            return Invalid("MMC_DEFINITION_JSON", "Invalid managed definition JSON: " + error.Message);
        }
    }

    public static string Serialize(MmcInstanceDefinition definition, MmcReadLimits? limits = null) {
        limits ??= new();
        var diagnostics = Validate(definition, MmcDefinitionValidationMode.Published, limits);
        if (!diagnostics.IsEmpty) throw new ArgumentException(diagnostics[0].Message, nameof(definition));
        string result = JsonSerializer.Serialize(definition, Options);
        if (Encoding.UTF8.GetByteCount(result) > limits.MaxMetadataBytes) throw new ArgumentException("Definition JSON exceeds its byte budget.", nameof(definition));
        return result;
    }

    public static ImmutableArray<MmcDiagnostic> Validate(MmcInstanceDefinition definition,
        MmcDefinitionValidationMode mode = MmcDefinitionValidationMode.Published, MmcReadLimits? limits = null) {
        limits ??= new();
        try {
            if (definition is null) MmcJson.Fail("definition", "Definition is null.");
            if (!Enum.IsDefined(mode)) MmcJson.Fail("definition", "Unknown definition validation mode.");
            if (definition.SchemaVersion != 1 || definition.ManagementKind != "Mmc" || definition.FormatProfile != "prism-v1")
                throw new MmcInputException("MMC_DEFINITION_UNSUPPORTED", "definition", "Unsupported schema/kind/profile.");
            if (definition.InstanceId == Guid.Empty || definition.Generation < 1 || definition.ParentInstanceId == Guid.Empty || definition.ParentInstanceId == definition.InstanceId)
                MmcJson.Fail("definition", "Invalid identity, parent identity or generation.");
            if (definition.Layout is null || definition.Layout.LayoutVersion != 1 || definition.Layout.GameRootLeaf is not ("minecraft" or ".minecraft"))
                MmcJson.Fail("definition", "Unsupported instance layout.");
            if (definition.Origin is null || definition.SourceBundle is null) MmcJson.Fail("definition", "Origin and source bundle are required.");
            long estimated = 2048;
            void Text(string? value, int maximum = 4096) {
                if (value is null || value.Length > maximum || value.Any(char.IsControl)) MmcJson.Fail("definition", "Invalid or overlong structural string.");
                estimated += StrictUtf8.GetByteCount(value) + 64L;
                if (estimated > limits.MaxMetadataBytes) throw new MmcInputException("MMC_DEFINITION_BUDGET", "definition", "Definition field budget exceeded.");
            }
            CheckHash(definition.Origin.PackageSha256);
            Text(definition.Origin.OriginalName, 512);
            if (string.IsNullOrWhiteSpace(definition.Origin.OriginalName) || definition.Origin.OriginalName.IndexOfAny(['/', '\\', ':']) >= 0)
                MmcJson.Fail("definition", "Original name must be a name, not a path.");
            if (definition.Origin.PlatformProjectIdentity is { } platform) {
                Text(platform.Platform, 64); Text(platform.ProjectId, 256);
                if (platform.VersionId is not null) Text(platform.VersionId, 256);
            }
            if (definition.Origin.PlatformEvidenceReference is { } evidence) {
                try {
                    MmcPlatformOriginCodec.ValidateReference(evidence);
                    MmcPlatformOriginCodec.ValidateIdentity(definition.Origin.PlatformProjectIdentity!);
                } catch (ArgumentException) { MmcJson.Fail("definition", "Invalid platform evidence reference or exact identity."); }
                Text(evidence.Sha256, 64); Text(evidence.ObjectRelativePath);
            }
            var sources = definition.SourceBundle;
            if (definition.PackageOwnershipReference is { } ownership) MmcPackageOwnershipCodec.ValidateReference(ownership);
            CheckHash(sources.BundleSha256); CheckHash(sources.ResolutionFingerprint);
            if (sources.PackageLocalScopeSha256 is { } packageScope) {
                CheckHash(packageScope);
                if (!SameHash(packageScope, definition.Origin.PackageSha256)) MmcJson.Fail("definition", "Measured local-library scope must equal its original package SHA256.");
            }
            Text(sources.RelativeRoot);
            if (sources.RelativeRoot.Length != 0) {
                if (!sources.RelativeRoot.EndsWith('/') || sources.RelativeRoot.EndsWith("//", StringComparison.Ordinal)) MmcJson.Fail("definition", "Source root must be an archive-relative directory prefix.");
                MmcArtifactRules.Relative(sources.RelativeRoot[..^1], "definition.sourceBundle");
            }
            RequireArray(sources.MetadataRecords, Math.Min(limits.MaxArchiveEntries, limits.MaxComponents * 4L + 2));
            RequireArray(definition.ComponentOverrides, limits.MaxComponents);
            RequireArray(definition.ArtifactInventory, limits.MaxArchiveEntries);
            RequireArray(definition.ArtifactLocks, limits.MaxArchiveEntries);
            RequireArray(definition.DerivedOutputs, limits.MaxArchiveEntries * 2L);
            RequireArray(definition.PackageFileInventory, limits.MaxArchiveEntries); RequireArray(definition.PackageFileLocks, limits.MaxArchiveEntries);
            estimated += 512L * (sources.MetadataRecords.Length + definition.ComponentOverrides.Length + definition.ArtifactInventory.Length + definition.ArtifactLocks.Length + definition.DerivedOutputs.Length + definition.PackageFileInventory.Length + definition.PackageFileLocks.Length);
            if (estimated > limits.MaxMetadataBytes) throw new MmcInputException("MMC_DEFINITION_BUDGET", "definition", "Definition record budget exceeded; large file baselines belong in external bundles.");
            HashSet<string> metadataKeys = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> sourceObjects = new(StringComparer.OrdinalIgnoreCase);
            int manifests = 0;
            foreach (var record in sources.MetadataRecords) {
                if (record is null || !Enum.IsDefined(record.Kind)) MmcJson.Fail("definition", "Invalid metadata record.");
                Text(record.RelativePath); MmcArtifactRules.Relative(record.RelativePath, "definition.sourceBundle");
                CheckHash(record.ContentSha256); Text(record.ObjectRelativePath);
                ObjectPath(record.ObjectRelativePath, "PCL/MmcSource/" + sources.BundleSha256.ToLowerInvariant() + "/");
                if (sourceObjects.TryGetValue(record.ObjectRelativePath, out var sourceHash) && !SameHash(sourceHash, record.ContentSha256)) MmcJson.Fail("definition", "Different source metadata bytes use the same object path.");
                sourceObjects[record.ObjectRelativePath] = record.ContentSha256;
                if (!metadataKeys.Add(record.Kind + ":" + record.RelativePath)) MmcJson.Fail("definition", "Duplicate source metadata record.");
                switch (record.Kind) {
                    case MmcSourceRecordKind.Manifest:
                        if (!record.RelativePath.Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase) || record.Uid is not null || record.Version is not null) MmcJson.Fail("definition", "Invalid manifest source record.");
                        manifests++; break;
                    case MmcSourceRecordKind.Configuration:
                        if (!record.RelativePath.Equals("instance.cfg", StringComparison.OrdinalIgnoreCase) || record.Uid is not null || record.Version is not null) MmcJson.Fail("definition", "Invalid cfg source record.");
                        break;
                    case MmcSourceRecordKind.Component:
                        if (record.Uid is null || record.ComponentSourceKind is null || !Enum.IsDefined(record.ComponentSourceKind.Value) || record.ComponentSourceKind == MmcComponentSourceKind.UserPatch ||
                            record.ComponentSourceKind == MmcComponentSourceKind.RemoteMetadata && string.IsNullOrEmpty(record.Version))
                            MmcJson.Fail("definition", "Component source requires UID and source kind; remote metadata requires an exact version.");
                        MmcPatchReader.ValidateUid(record.Uid, "definition.sourceBundle"); if (record.Version is not null) Text(record.Version);
                        if (record.ComponentSourceKind == MmcComponentSourceKind.PackageLocal) {
                            string[] patchPath = record.RelativePath.Split('/');
                            if (patchPath.Length != 2 || !patchPath[0].Equals("patches", StringComparison.OrdinalIgnoreCase) || !patchPath[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) || patchPath[1][..^5] != record.Uid)
                                MmcJson.Fail("definition", "Local patch source must retain its component-relative location and exact UID.");
                        }
                        break;
                    case MmcSourceRecordKind.RecommendationIndex:
                        if (record.Uid is null || !string.IsNullOrEmpty(record.Version)) MmcJson.Fail("definition", "Invalid recommendation index identity.");
                        MmcPatchReader.ValidateUid(record.Uid, "definition.sourceBundle");
                        break;
                    case MmcSourceRecordKind.PackageMetadata:
                        if (record.Uid is not null || record.Version is not null || record.ComponentSourceKind is not null) MmcJson.Fail("definition", "Opaque package metadata cannot impersonate a component identity.");
                        break;
                }
                if ((record.Kind == MmcSourceRecordKind.RecommendationIndex || record.ComponentSourceKind == MmcComponentSourceKind.RemoteMetadata) && string.IsNullOrEmpty(record.SourceIdentity))
                    MmcJson.Fail("definition", "Adopted remote metadata requires its fixed source identity.");
                if (record.SourceIdentity is not null) { Text(record.SourceIdentity); StableSource(record.SourceIdentity); }
            }
            if (manifests != 1) MmcJson.Fail("definition", "Exactly one preserved manifest source is required.");
            HashSet<string> overrideUids = new(StringComparer.Ordinal);
            foreach (var item in definition.ComponentOverrides) {
                if (item is null) MmcJson.Fail("definition", "Null component override.");
                MmcPatchReader.ValidateUid(item.Uid, "definition.overrides");
                if (!overrideUids.Add(item.Uid)) MmcJson.Fail("definition", "Duplicate component override.");
                if (item.Version is not null) { Text(item.Version); if (item.Version.Length == 0) MmcJson.Fail("definition", "Explicit version cannot be empty."); }
                if (item.PatchReference is { } reference) {
                    CheckHash(reference.Sha256); Text(reference.ObjectRelativePath);
                    if (reference.ObjectRelativePath != "PCL/MmcOverrides/" + reference.Sha256.ToLowerInvariant() + ".json") MmcJson.Fail("definition", "User patch must reference its independent content object.");
                }
            }
            Dictionary<string, MmcArtifactInventoryRecord> inventory = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in definition.ArtifactInventory) {
                if (item is null || !Enum.IsDefined(item.Role) || !Enum.IsDefined(item.Scope) || !Enum.IsDefined(item.State)) MmcJson.Fail("definition", "Invalid artifact inventory record.");
                CheckHash(item.ArtifactKey); CheckHash(item.SourceRevision); MmcPatchReader.ValidateUid(item.ComponentUid, "definition.inventory");
                Text(item.VariantTag, 512); Text(item.RelativePath); MmcArtifactRules.Relative(item.RelativePath, "definition.inventory");
                if (item.Scope == MmcArtifactScope.InstanceLocal && item.RelativePath.Contains('/')) MmcJson.Fail("definition", "Instance-local libraries require their flat filename, not a game-root-relative path.");
                if (item.DeclaredSha1 is not null) CheckHash(item.DeclaredSha1, 40);
                if (item.DeclaredSha256 is not null) CheckHash(item.DeclaredSha256);
                if (item.SourceObject is { } original) { CheckHash(original.Sha256); Text(original.ObjectRelativePath); RecoveryPath(original.ObjectRelativePath, original.Sha256); }
                if (item.SourceMetadataObjectRelativePath is { } sourceObject) { Text(sourceObject); MetadataObjectPath(sourceObject); }
                if (!inventory.TryAdd(item.ArtifactKey, item)) MmcJson.Fail("definition", "Duplicate artifact inventory key.");
                if (item.Scope == MmcArtifactScope.InstanceLocal && item.Role is MmcArtifactRole.AssetIndex or MmcArtifactRole.LoggingConfiguration) MmcJson.Fail("definition", "Auxiliary inputs require owned shared-source views.");
            }
            Dictionary<string, MmcArtifactLockRecord> locks = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MmcArtifactLockRecord> targets = new(StringComparer.OrdinalIgnoreCase);
            long totalLockedBytes = 0;
            foreach (var item in definition.ArtifactLocks) {
                if (item is null) MmcJson.Fail("definition", "Null artifact lock.");
                CheckHash(item.ArtifactKey); CheckHash(item.SourceRevision); CheckHash(item.AdoptedSha256);
                if (!inventory.TryGetValue(item.ArtifactKey, out var declared) || declared.State != MmcArtifactAdoptionState.Adopted || !SameHash(declared.SourceRevision, item.SourceRevision))
                    MmcJson.Fail("definition", "Artifact lock has no matching adopted inventory identity.");
                if (declared.DeclaredSha256 is not null && !SameHash(declared.DeclaredSha256, item.AdoptedSha256) ||
                    declared.SourceObject is not null && !SameHash(declared.SourceObject.Sha256, item.AdoptedSha256)) MmcJson.Fail("definition", "Adopted bytes differ from their declared/original content digest.");
                if (item.ObservedSha1 is not null) CheckHash(item.ObservedSha1, 40);
                if (declared.DeclaredSha1 is not null && (item.ObservedSha1 is null || !SameHash(declared.DeclaredSha1, item.ObservedSha1))) MmcJson.Fail("definition", "Adopted bytes lack a matching observed SHA1.");
                if (item.SizeBytes < 0 || item.SizeBytes > limits.MaxEntryBytes || totalLockedBytes > limits.MaxArchiveBytes - item.SizeBytes)
                    throw new MmcInputException("MMC_DEFINITION_BUDGET", "definition", "Artifact lock byte budget exceeded.");
                totalLockedBytes += item.SizeBytes;
                Text(item.RecoveryObjectRelativePath); RecoveryPath(item.RecoveryObjectRelativePath, item.AdoptedSha256);
                Text(item.TargetRelativePath); TargetPath(declared.Role, declared.Scope, declared.RelativePath, item.TargetRelativePath);
                if (!locks.TryAdd(item.ArtifactKey, item)) MmcJson.Fail("definition", "Duplicate artifact lock key.");
                if (targets.TryGetValue(item.TargetRelativePath, out var previous) && !SameHash(previous.AdoptedSha256, item.AdoptedSha256)) MmcJson.Fail("definition", "Different locked contents target the same path.");
                targets[item.TargetRelativePath] = item;
            }
            if (inventory.Values.Any(item => item.State == MmcArtifactAdoptionState.Adopted && !locks.ContainsKey(item.ArtifactKey))) MmcJson.Fail("definition", "Adopted inventory record has no immutable lock.");
            ValidateDerived(definition, inventory, locks, limits, Text);
            ValidatePackageFiles(definition, mode, limits, Text);
            if (definition.AuthorFileBaseline is { } baseline) {
                CheckHash(baseline.ContentSha256); Text(baseline.ObjectRelativePath); ObjectPath(baseline.ObjectRelativePath, "PCL/MmcSource/" + sources.BundleSha256.ToLowerInvariant() + "/");
                if (baseline.FormatVersion != 1 || baseline.EntryCount < 0 || baseline.EntryCount > limits.MaxArchiveEntries) MmcJson.Fail("definition", "Invalid external author-file baseline envelope.");
            }
            if (definition.InstalledProjection is { } projection) {
                CheckHash(projection.ProfileFingerprint); CheckHash(projection.OutputJsonSha256); CheckHash(projection.RuntimePlanFingerprint); CheckHash(projection.BoundFingerprint);
                if (projection.RuntimeEnvironment is { } environment) {
                    if (MmcArtifactRules.Normalize(environment) != environment || environment.JavaMajor <= 0)
                        MmcJson.Fail("definition", "Published runtime environment requires canonical OS/architecture and a known positive Java major.");
                    Text(environment.OperatingSystem,16); Text(environment.Architecture,16);
                    if (environment.OsVersion is { } osVersion) {
                        Text(osVersion,256);
                        if (string.IsNullOrWhiteSpace(osVersion)) MmcJson.Fail("definition", "An explicitly stored OS version cannot be empty.");
                    }
                }
                if (projection.ManifestGeneration != definition.Generation) MmcJson.Fail("definition", "Projection generation does not match its definition.");
                if (mode == MmcDefinitionValidationMode.Published && !SameHash(projection.BoundFingerprint, ComputeBoundFingerprint(definition))) MmcJson.Fail("definition", "Bound definition fingerprint is inconsistent.");
            } else if (mode == MmcDefinitionValidationMode.Published) MmcJson.Fail("definition", "Published definition requires a bound installed projection.");
            return [];
        } catch (MmcInputException error) { return [new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)]; }
        catch (EncoderFallbackException) { return [new("MMC_DEFINITION_ENCODING", MmcDiagnosticSeverity.Error, "definition", "Invalid structural string encoding.")]; }
    }

    public static string ComputeBoundFingerprint(MmcInstanceDefinition definition) {
        var projection = definition.InstalledProjection ?? throw new ArgumentException("Projection is required.", nameof(definition));
        return MmcFingerprint.Of(new[] { "mmc-definition-binding-v1", definition.InstanceId.ToString("D"), definition.ParentInstanceId?.ToString("D"), definition.Generation.ToString(),
            definition.Origin.PackageSha256, definition.Origin.OriginalName, definition.Origin.PlatformProjectIdentity?.Platform,
            definition.Origin.PlatformProjectIdentity?.ProjectId, definition.Origin.PlatformProjectIdentity?.VersionId,
            definition.SourceBundle.BundleSha256, definition.SourceBundle.ResolutionFingerprint, definition.SourceBundle.RelativeRoot,
            definition.Layout.GameRootLeaf, definition.Layout.LayoutVersion.ToString(), projection.ProfileFingerprint, projection.OutputJsonSha256,
            projection.ManifestGeneration.ToString(), projection.RuntimePlanFingerprint,
            definition.AuthorFileBaseline?.ContentSha256, definition.AuthorFileBaseline?.ObjectRelativePath, definition.AuthorFileBaseline?.EntryCount.ToString() }
            .Concat(definition.SourceBundle.MetadataRecords.OrderBy(x => x.Kind).ThenBy(x => x.RelativePath, StringComparer.Ordinal).SelectMany(x => new[] { x.Kind.ToString(), x.RelativePath, x.ContentSha256, x.ObjectRelativePath, x.Uid, x.Version, x.ComponentSourceKind?.ToString(), x.SourceIdentity }))
            .Concat(definition.ComponentOverrides.OrderBy(x => x.Uid, StringComparer.Ordinal).SelectMany(x => new[] { x.Uid, x.Enabled?.ToString(), x.Version, x.PatchReference?.Sha256, x.PatchReference?.ObjectRelativePath }))
            .Concat(definition.ArtifactInventory.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).SelectMany(x => new[] { x.ArtifactKey, x.ComponentUid, x.Role.ToString(), x.Scope.ToString(), x.VariantTag, x.SourceRevision, x.RelativePath, x.State.ToString(), x.DeclaredSha1, x.DeclaredSha256, x.SourceObject?.Sha256, x.SourceObject?.ObjectRelativePath, x.SourceMetadataObjectRelativePath }))
            .Concat(definition.ArtifactLocks.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).SelectMany(x => new[] { x.ArtifactKey, x.SourceRevision, x.AdoptedSha256, x.SizeBytes.ToString(), x.RecoveryObjectRelativePath, x.TargetRelativePath, x.ObservedSha1 }))
            .Concat(definition.DerivedOutputs.OrderBy(x => x.TargetRelativePath, StringComparer.Ordinal).SelectMany(x => new[] { x.Kind.ToString(), x.ParentArtifactKey, x.ParentSha256, x.ProducerVersion, x.SourceSetFingerprint, x.RuntimePlanFingerprint, x.ContextFingerprint, x.ViewKey, x.RelativePath, x.TargetRelativePath, x.Sha256, x.SizeBytes.ToString(), x.ArtifactKey, x.ObservedSha1, x.RecoveryObjectRelativePath }))
            .Concat(definition.PackageFileInventory.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).SelectMany(x => new[] { x.ArtifactKey, x.SourceRevision, x.PackageSha256, x.RelativeRoot,
                x.BaselineReference.ContentSha256, x.BaselineReference.ObjectRelativePath, x.BaselineReference.EntryCount.ToString(), x.BaselineReference.FormatVersion.ToString(), x.SourceRelativePath,
                x.LibraryRelativePath, x.Sha256, x.Sha1, x.SizeBytes.ToString(), x.Crc32.ToString() }))
            .Concat(definition.PackageFileLocks.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).SelectMany(x => new[] { x.ArtifactKey, x.SourceRevision, x.AdoptedSha256, x.ObservedSha1, x.SizeBytes.ToString(), x.RecoveryObjectRelativePath, x.TargetRelativePath }))
            // No null marker is appended for historical definitions. Their original binding
            // protocol and published fingerprints must remain byte-for-byte compatible.
            .Concat(projection.RuntimeEnvironment is { } environment ? new[] {
                "mmc-published-runtime-environment-v1",environment.OperatingSystem,environment.Architecture,
                environment.JavaMajor.ToString(CultureInfo.InvariantCulture),environment.JavaBitness.ToString(CultureInfo.InvariantCulture),environment.OsVersion
            } : [])
            .Concat(definition.SourceBundle.PackageLocalScopeSha256 is { } scope ? new[] { "mmc-measured-package-local-v1", scope } : [])
            .Concat(definition.PackageOwnershipReference is { } ownership ? new[] { "mmc-package-ownership-v1", ownership.Sha256, ownership.ObjectRelativePath } : [])
            .Concat(definition.Origin.PlatformEvidenceReference is { } evidence ? new[] { "mmc-platform-origin-evidence-v1", evidence.Sha256, evidence.ObjectRelativePath } : []));
    }

    private static void ValidateEnvironmentJson(JsonElement root) {
        if (!root.TryGetProperty("installedProjection",out var projection) || projection.ValueKind != JsonValueKind.Object ||
            !projection.TryGetProperty("runtimeEnvironment",out var environment) || environment.ValueKind == JsonValueKind.Null) return;
        if (environment.ValueKind != JsonValueKind.Object) MmcJson.Fail("definition", "Published runtime environment must be an object.");
        _=MmcJson.String(environment,"operatingSystem","definition",true);
        _=MmcJson.String(environment,"architecture","definition",true);
        foreach (string field in new[]{"javaMajor","javaBitness"})
            if (!environment.TryGetProperty(field,out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out _))
                MmcJson.Fail("definition", "Published runtime environment requires explicit integer "+field+".");
    }

    private static void ValidatePackageFiles(MmcInstanceDefinition definition, MmcDefinitionValidationMode mode, MmcReadLimits limits, Action<string?, int> text) {
        Dictionary<string, MmcPackageFileInventoryRecord> inventory = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in definition.PackageFileInventory) {
            if (item is null || item.BaselineReference is null) MmcJson.Fail("definition", "Null package-original inventory/reference.");
            CheckHash(item.ArtifactKey); CheckHash(item.SourceRevision); CheckHash(item.PackageSha256); CheckHash(item.Sha256); CheckHash(item.Sha1, 40); CheckHash(item.BaselineReference.ContentSha256);
            text(item.RelativeRoot, 4096); text(item.SourceRelativePath, 4096); text(item.LibraryRelativePath, 4096); text(item.BaselineReference.ObjectRelativePath, 4096);
            MetadataObjectPath(item.BaselineReference.ObjectRelativePath);
            if (!item.BaselineReference.ObjectRelativePath.StartsWith("PCL/MmcSource/", StringComparison.Ordinal) || item.BaselineReference.FormatVersion != 1 || item.BaselineReference.EntryCount < 0 || item.BaselineReference.EntryCount > limits.MaxArchiveEntries)
                MmcJson.Fail("definition", "Invalid package-original baseline reference.");
            if (item.RelativeRoot.Length > 0) { if (!item.RelativeRoot.EndsWith('/')) MmcJson.Fail("definition", "Package-original root must be an archive prefix."); MmcArtifactRules.Relative(item.RelativeRoot[..^1], "package-original"); }
            var placement = MmcPackageFileBaselineCodec.Classify(item.RelativeRoot, item.SourceRelativePath);
            if (placement.Role != MmcPackageFileRole.LocalLibrary || placement.TargetRelativePath != "libraries/" + item.LibraryRelativePath ||
                item.ArtifactKey != MmcPackageFilePreservation.ComputeArtifactKey(item.BaselineReference.ContentSha256, item.PackageSha256, item.SourceRelativePath) ||
                item.SourceRevision != MmcPackageFilePreservation.ComputeSourceRevision(item.BaselineReference.ContentSha256, item.PackageSha256, item.SourceRelativePath))
                MmcJson.Fail("definition", "Package-original key/role/path differs from its independent source identity.");
            if (item.SizeBytes < 0 || item.SizeBytes > limits.MaxEntryBytes || !inventory.TryAdd(item.ArtifactKey, item)) MmcJson.Fail("definition", "Invalid or duplicate package-original record.");
            if (SameHash(item.PackageSha256, definition.Origin.PackageSha256) && (definition.AuthorFileBaseline is null || item.BaselineReference == definition.AuthorFileBaseline && item.RelativeRoot != definition.SourceBundle.RelativeRoot))
                MmcJson.Fail("definition", "Current package originals require a measured author baseline and a coherent current root.");
        }
        HashSet<string> locked = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> currentTargets = new(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var item in definition.PackageFileLocks) {
            if (item is null) MmcJson.Fail("definition", "Null package-original lock.");
            CheckHash(item.ArtifactKey); CheckHash(item.SourceRevision); CheckHash(item.AdoptedSha256); CheckHash(item.ObservedSha1, 40);
            if (!inventory.TryGetValue(item.ArtifactKey, out var member) || !locked.Add(item.ArtifactKey) || !SameHash(item.SourceRevision, member.SourceRevision) || !SameHash(item.AdoptedSha256, member.Sha256) || !SameHash(item.ObservedSha1, member.Sha1) || item.SizeBytes != member.SizeBytes)
                MmcJson.Fail("definition", "Package-original lock does not match its measured source membership.");
            text(item.TargetRelativePath, 4096); text(item.RecoveryObjectRelativePath, 4096); RecoveryPath(item.RecoveryObjectRelativePath, item.AdoptedSha256);
            MmcArtifactRules.Relative(item.TargetRelativePath, "package-original");
            if (item.TargetRelativePath != "libraries/" + member.LibraryRelativePath || bytes > limits.MaxArchiveBytes - item.SizeBytes) MmcJson.Fail("definition", "Package-original target/total byte budget is invalid.");
            bytes += item.SizeBytes;
            if (SameHash(member.PackageSha256, definition.Origin.PackageSha256)) {
                if (currentTargets.TryGetValue(item.TargetRelativePath, out var old) && !SameHash(old, item.AdoptedSha256)) MmcJson.Fail("definition", "Current package originals collide at one target with different measured bytes.");
                currentTargets[item.TargetRelativePath] = item.AdoptedSha256;
            }
        }
        if (mode == MmcDefinitionValidationMode.Published && inventory.Keys.Any(key => !locked.Contains(key))) MmcJson.Fail("definition", "Published package-original inventory requires immutable adopted locks.");
    }

    private static void ValidateDerived(MmcInstanceDefinition definition, Dictionary<string, MmcArtifactInventoryRecord> inventory,
        Dictionary<string, MmcArtifactLockRecord> locks, MmcReadLimits limits, Action<string?, int> text) {
        Dictionary<(string Key, string View, string Context), MmcDerivedOutputRecord> objects = [];
        Dictionary<string, MmcDerivedOutputRecord> adoptedObjects = new(StringComparer.Ordinal);
        // Validate every scalar used by cross-record comparisons first. Damaged
        // managed JSON must return Invalid, including an early null digest.
        foreach (var output in definition.DerivedOutputs) {
            if (output is null || !Enum.IsDefined(output.Kind)) MmcJson.Fail("definition", "Invalid derived output.");
            CheckHash(output.Sha256); CheckHash(output.ParentSha256); CheckHash(output.SourceSetFingerprint); CheckHash(output.RuntimePlanFingerprint); CheckHash(output.ContextFingerprint); CheckHash(output.ViewKey);
            if (output.Kind is MmcDerivedOutputKind.NativeFile or MmcDerivedOutputKind.AssetObject) CheckHash(output.ParentArtifactKey);
            else if (!MmcDerivedProofs.IsAssetObjectKey(output.ParentArtifactKey)) MmcJson.Fail("definition", "Asset layout output requires an adopted object parent key.");
        }
        foreach (var output in definition.DerivedOutputs) if (output is { Kind: MmcDerivedOutputKind.AssetObject }) {
            if (output.ArtifactKey is null || !MmcDerivedProofs.IsAssetObjectKey(output.ArtifactKey) || !objects.TryAdd((output.ArtifactKey, output.ViewKey, output.ContextFingerprint), output)) MmcJson.Fail("definition", "Asset derived-object identity is missing or duplicated.");
            if (adoptedObjects.TryGetValue(output.ArtifactKey, out var old) && (!SameHash(old.Sha256, output.Sha256) || old.SizeBytes != output.SizeBytes)) MmcJson.Fail("definition", "An adopted asset-object key changed its expected contents across views.");
            adoptedObjects[output.ArtifactKey] = output;
        }
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        long total = 0, nativeBytes = 0;
        int nativeEntries = 0;
        foreach (var output in definition.DerivedOutputs) {
            if (output is null || !Enum.IsDefined(output.Kind)) MmcJson.Fail("definition", "Invalid derived output.");
            CheckHash(output.ParentSha256); CheckHash(output.Sha256); CheckHash(output.SourceSetFingerprint); CheckHash(output.RuntimePlanFingerprint); CheckHash(output.ContextFingerprint); CheckHash(output.ViewKey);
            text(output.ProducerVersion, 128); text(output.RelativePath, 4096); text(output.TargetRelativePath, 4096);
            MmcArtifactRules.Relative(output.RelativePath, "definition.derived"); MmcArtifactRules.Relative(output.TargetRelativePath, "definition.derived");
            if (output.ProducerVersion.Length == 0 || !SameHash(output.ContextFingerprint, MmcDerivedProofs.ComputeDerivedContextFingerprint(output.RuntimePlanFingerprint, output.SourceSetFingerprint, output.ProducerVersion))) MmcJson.Fail("definition", "Derived context fingerprint is inconsistent.");
            if (output.SizeBytes < 0 || output.SizeBytes > limits.MaxEntryBytes || total > limits.MaxArchiveBytes - output.SizeBytes) MmcJson.Fail("definition", "Derived byte budget exceeded.");
            total += output.SizeBytes;
            if (!targets.Add(output.TargetRelativePath)) MmcJson.Fail("definition", "Derived outputs collide at a Windows target.");
            string root = "PCL/MmcRuntime/" + output.ViewKey + "/";
            if (output.Kind is MmcDerivedOutputKind.NativeFile or MmcDerivedOutputKind.AssetObject) {
                CheckHash(output.ParentArtifactKey);
                if (!locks.TryGetValue(output.ParentArtifactKey, out var parent) || !SameHash(parent.AdoptedSha256, output.ParentSha256)) MmcJson.Fail("definition", "Derived output lacks its immutable parent input lock.");
                var parentInventory = inventory[output.ParentArtifactKey];
                if (output.Kind == MmcDerivedOutputKind.NativeFile) {
                    if (parentInventory.Role != MmcArtifactRole.Native || output.TargetRelativePath != root + "natives/" + output.RelativePath || output.ArtifactKey is not null || output.RecoveryObjectRelativePath is not null)
                        MmcJson.Fail("definition", "Native output must derive from a native input into its owned extraction view.");
                    if (++nativeEntries > 10000 || nativeBytes > 256L * 1024 * 1024 - output.SizeBytes) MmcJson.Fail("definition", "Native derived output budget exceeded.");
                    nativeBytes += output.SizeBytes;
                } else {
                    if (parentInventory.Role != MmcArtifactRole.AssetIndex || output.ObservedSha1 is null || output.RecoveryObjectRelativePath is null) MmcJson.Fail("definition", "Asset object requires an observed hash, preserved original and locked asset-index parent.");
                    CheckHash(output.ObservedSha1, 40); RecoveryPath(output.RecoveryObjectRelativePath, output.Sha256);
                    string expected = "objects/" + output.ObservedSha1[..2].ToLowerInvariant() + "/" + output.ObservedSha1.ToLowerInvariant();
                    if (output.RelativePath != expected || output.TargetRelativePath != root + "assets/" + expected || output.ArtifactKey != MmcDerivedProofs.ComputeAssetObjectKey(parent.ArtifactKey, parent.AdoptedSha256, output.ObservedSha1, output.SizeBytes))
                        MmcJson.Fail("definition", "Asset-object identity/path does not bind its index and observed contents.");
                }
            } else {
                if (!objects.TryGetValue((output.ParentArtifactKey, output.ViewKey, output.ContextFingerprint), out var parent) || !SameHash(parent.Sha256, output.ParentSha256) || !SameHash(output.Sha256, parent.Sha256) || output.SizeBytes != parent.SizeBytes || output.ObservedSha1 != parent.ObservedSha1 ||
                    output.ViewKey != parent.ViewKey || output.ContextFingerprint != parent.ContextFingerprint) MmcJson.Fail("definition", "Asset virtual/resource output must remain identical to its adopted object parent in the same view/context.");
                string expected = output.Kind == MmcDerivedOutputKind.AssetVirtualFile ? root + "assets/" + output.RelativePath : definition.Layout.GameRootLeaf + "/resources/" + output.RelativePath;
                if (output.TargetRelativePath != expected || output.Kind == MmcDerivedOutputKind.AssetVirtualFile && !output.RelativePath.StartsWith("virtual/", StringComparison.Ordinal)) MmcJson.Fail("definition", "Asset derived layout does not match its role.");
            }
        }
    }

    internal static bool SameHash(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    internal static void CheckHash(string value, int digits = 64) {
        if (value is null || value.Length != digits || !value.All(Uri.IsHexDigit)) MmcJson.Fail("definition", "Invalid content digest or artifact key.");
    }
    internal static void RecoveryPath(string relative, string sha256) {
        if (relative != "PCL/MmcArtifacts/sha256/" + sha256.ToLowerInvariant()) MmcJson.Fail("definition", "Recovery object path must match its immutable content address.");
    }
    internal static void MetadataObjectPath(string relative) {
        MmcArtifactRules.Relative(relative, "definition.source-object");
        if (relative.StartsWith("PCL/MmcSource/", StringComparison.Ordinal)) {
            string[] pieces = relative.Split('/');
            if (pieces.Length < 4) MmcJson.Fail("definition", "Incomplete metadata object path.");
            CheckHash(pieces[2]);
        } else if (relative.StartsWith("PCL/MmcOverrides/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal)) {
            string[] pieces = relative.Split('/');
            if (pieces.Length != 3) MmcJson.Fail("definition", "Invalid user patch object path.");
            CheckHash(pieces[2][..^5]);
        } else MmcJson.Fail("definition", "Metadata source object must remain in a preserved source/override tree.");
    }
    internal static void TargetPath(MmcArtifactRole role, MmcArtifactScope scope, string artifactRelative, string target) {
        MmcArtifactRules.Relative(target, "definition.target");
        if (scope == MmcArtifactScope.InstanceLocal) {
            if (target != "libraries/" + artifactRelative) MmcJson.Fail("definition", "Local artifact target must retain its instance-library position.");
            return;
        }
        string[] parts = target.Split('/');
        if (parts.Length < 4 || parts[0] != "PCL" || parts[1] != "MmcRuntime") MmcJson.Fail("definition", "Shared source must bind an owned runtime-view target.");
        CheckHash(parts[2]);
        string suffix = role == MmcArtifactRole.AssetIndex ? "assets/" : role == MmcArtifactRole.LoggingConfiguration ? "" : "libraries/";
        if (target != "PCL/MmcRuntime/" + parts[2] + "/" + suffix + artifactRelative) MmcJson.Fail("definition", "Artifact target does not match its declared runtime role/path.");
    }
    private static void ObjectPath(string relative, string prefix) { MmcArtifactRules.Relative(relative, "definition.object"); if (!relative.StartsWith(prefix, StringComparison.Ordinal)) MmcJson.Fail("definition", "Object reference is outside its preserved source bundle."); }
    private static void StableSource(string source) {
        if (source.StartsWith("package:", StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            MmcJson.Fail("definition", "Remote source identity must be a stable HTTP(S) URL without credentials/query/fragment.");
    }
    private static void ValidateGuidJson(JsonElement root, string name, bool required) {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { if (required) MmcJson.Fail("definition", "Missing instance identity."); return; }
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(value.GetString(), "D", out var id) || id == Guid.Empty) MmcJson.Fail("definition", "Instance identities must be nonempty D-format GUIDs.");
    }
    private static void RequireArray<T>(ImmutableArray<T> items, long maximum) { if (items.IsDefault || items.Length > maximum) MmcJson.Fail("definition", "Required array is absent or over budget."); }
    private static MmcDefinitionReadResult Invalid(string code, string message) => new(null, MmcDefinitionReadStatus.Invalid, [new(code, MmcDiagnosticSeverity.Error, "definition", message)]);
}
