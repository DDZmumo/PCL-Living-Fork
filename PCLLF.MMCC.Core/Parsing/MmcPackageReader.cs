// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

/// <summary>Pure source parsing. A successful result is not permission to install or launch.</summary>
public static class MmcPackageReader {
    /// <summary>Validates a bounded logical file/directory namespace without requiring package metadata.</summary>
    public static ImmutableArray<MmcDiagnostic> ValidateNamespace(IReadOnlyList<MmcArchiveEntry> entries, MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(entries); return InspectPaths(entries, limits ?? new());
    }
    internal static ImmutableArray<MmcDiagnostic> InspectPaths(IReadOnlyList<MmcArchiveEntry> entries, MmcReadLimits limits) {
        List<MmcDiagnostic> diagnostics = [];
        long pathCharacters = 0;
        Index(entries, limits, diagnostics, ref pathCharacters);
        return diagnostics.ToImmutableArray();
    }
    private static readonly HashSet<string> ComponentFields = new(StringComparer.Ordinal) {
        "uid", "version", "disabled", "dependencyOnly", "important", "cachedVersion", "cachedName", "cachedRequires",
        "cachedConflicts", "cachedVolatile", "cachedOrder"
    };

    public static MmcArchiveInspection InspectArchive(IReadOnlyList<MmcArchiveEntry> archiveEntries, MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(archiveEntries);
        limits ??= new();
        ValidateLimits(limits);
        List<MmcDiagnostic> diagnostics = [];
        try {
            long rawCharacters = 0;
            var entries = Index(archiveEntries, limits, diagnostics, ref rawCharacters);
            var inspected = Describe(entries, limits);
            return new(inspected.Root, inspected.GameRoot, inspected.MetadataPaths, diagnostics.ToImmutableArray());
        } catch (MmcInputException error) {
            diagnostics.Add(new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message));
            return new(null, null, [], diagnostics.ToImmutableArray());
        }
    }

    public static MmcParseResult Parse(MmcPackageInput input, MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Entries);
        ArgumentNullException.ThrowIfNull(input.Metadata);
        limits ??= new();
        ValidateLimits(limits);
        List<MmcDiagnostic> diagnostics = [];
        try {
            long rawPathCharacters = 0;
            var entries = Index(input.Entries, limits, diagnostics, ref rawPathCharacters);
            var metadata = Metadata(input.Metadata, entries, limits, ref rawPathCharacters);
            var inspected = Describe(entries, limits);
            string root = inspected.Root;
            string manifestPath = inspected.MetadataPaths[0];
            string manifest = RequiredMetadata(manifestPath, metadata);
            using var document = MmcJson.Read(manifest, manifestPath, limits.MaxDepth);
            JsonElement manifestRoot = document.RootElement;
            MmcJson.FormatVersion(manifestRoot, manifestPath, true);
            foreach (JsonProperty field in manifestRoot.EnumerateObject())
                if (field.Name is not ("formatVersion" or "components")) MmcPatchReader.Unsupported("manifest:" + field.Name, manifestPath, true, diagnostics);
            if (!manifestRoot.TryGetProperty("components", out JsonElement componentArray) || componentArray.ValueKind != JsonValueKind.Array)
                MmcJson.Fail(manifestPath, "components must be an array.");
            if (componentArray.GetArrayLength() is 0 || componentArray.GetArrayLength() > limits.MaxComponents)
                throw new MmcInputException("MMC_COMPONENT_BUDGET", manifestPath, "Invalid component count.");

            Dictionary<string, (JsonElement Value, bool Enabled)> declared = new(StringComparer.Ordinal);
            foreach (JsonElement item in componentArray.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.Object) MmcJson.Fail(manifestPath, "Component must be an object.");
                string uid = MmcJson.String(item, "uid", manifestPath, true)!;
                MmcPatchReader.ValidateUid(uid, manifestPath);
                bool disabled = MmcJson.Boolean(item, "disabled", manifestPath);
                bool dependency = MmcJson.Boolean(item, "dependencyOnly", manifestPath);
                bool important = MmcJson.Boolean(item, "important", manifestPath);
                if (!declared.TryAdd(uid, (item, !disabled || dependency || important)))
                    throw new MmcInputException("MMC_DUPLICATE_UID", manifestPath, $"Duplicate component UID: {uid}.");
                foreach (JsonProperty field in item.EnumerateObject())
                    if (!ComponentFields.Contains(field.Name)) MmcPatchReader.Unsupported("component:" + field.Name, manifestPath, !disabled || dependency || important, diagnostics);
            }

            Dictionary<string, MmcPatch> patches = new(StringComparer.Ordinal);
            List<MmcOpaquePackageMetadata> packageMetadata = [];
            long artifactCount = 0;
            string patchesPrefix = root + "patches/";
            foreach (var entry in entries.Where(e => !e.Value.IsDirectory && e.Key.StartsWith(patchesPrefix, StringComparison.OrdinalIgnoreCase))) {
                string tail = entry.Key[patchesPrefix.Length..];
                if (tail.Contains('/') || !tail.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string filenameUid = tail[..^5];
                if (!declared.ContainsKey(filenameUid)) {
                    if (declared.Keys.Any(uid => uid.Equals(filenameUid, StringComparison.OrdinalIgnoreCase)))
                        throw new MmcInputException("MMC_PATCH_UID", entry.Key, "A component patch filename must preserve its exact UID spelling.");
                    packageMetadata.Add(new(entry.Key[root.Length..], metadata.TryGetValue(entry.Key, out string? opaque) ? MmcPatchReader.Hash(opaque) : null));
                    diagnostics.Add(new("MMC_UNREFERENCED_PATCH", MmcDiagnosticSeverity.Information, entry.Key, "Unreferenced package file is preserved as opaque metadata and contributes no component semantics."));
                    continue;
                }
                string text = RequiredMetadata(entry.Key, metadata);
                // Read identity first so disabled unsupported capabilities can be retained.
                using var identity = MmcJson.Read(text, entry.Key, limits.MaxDepth);
                string? uid = MmcJson.String(identity.RootElement, "uid", entry.Key) ?? MmcJson.String(identity.RootElement, "fileId", entry.Key);
                bool enabled = uid is not null && declared.TryGetValue(uid, out var component) && component.Enabled;
                MmcPatch patch = MmcPatchReader.Parse(text, entry.Key, limits, enabled, diagnostics);
                if (patch.Uid != tail[..^5]) throw new MmcInputException("MMC_PATCH_UID", entry.Key, "Patch identity does not match its component filename.");
                artifactCount += patch.Fields.Where(f => f.Key is "libraries" or "+libraries" or "mavenFiles").Sum(f => (long)f.Value.GetArrayLength());
                if (patch.Fields.TryGetValue("mainJar", out JsonElement mainJar) && mainJar.ValueKind != JsonValueKind.Null) artifactCount++;
                if (artifactCount > limits.MaxArchiveEntries) throw new MmcInputException("MMC_ARTIFACT_BUDGET", entry.Key, "Cumulative declared artifact budget exceeded.");
                if (!patches.TryAdd(patch.Uid, patch)) throw new MmcInputException("MMC_DUPLICATE_PATCH", entry.Key, $"Multiple local patches declare {patch.Uid}.");
                if (!declared.ContainsKey(patch.Uid)) diagnostics.Add(new("MMC_UNREFERENCED_PATCH", MmcDiagnosticSeverity.Warning, entry.Key, "Unreferenced patch retained in source; it contributes no runtime fields."));
            }

            var components = ImmutableArray.CreateBuilder<MmcComponent>();
            List<MmcMetadataRequest> requests = [];
            foreach ((string uid, var declaration) in declared) {
                JsonElement item = declaration.Value;
                string? requested = MmcJson.String(item, "version", manifestPath);
                string? cached = MmcJson.String(item, "cachedVersion", manifestPath);
                patches.TryGetValue(uid, out MmcPatch? patch);
                string? effective = !string.IsNullOrWhiteSpace(patch?.Version) ? patch.Version : requested;
                bool disabled = MmcJson.Boolean(item, "disabled", manifestPath);
                bool dependency = MmcJson.Boolean(item, "dependencyOnly", manifestPath);
                bool important = MmcJson.Boolean(item, "important", manifestPath);
                if (disabled && (dependency || important)) diagnostics.Add(new("MMC_CANNOT_DISABLE", MmcDiagnosticSeverity.Warning, manifestPath, $"{uid} is important or dependencyOnly; its effective state remains enabled."));
                if (patch is not null && requested is not null && effective != requested)
                    diagnostics.Add(new("MMC_LOCAL_VERSION", MmcDiagnosticSeverity.Warning, patch.Source, $"{uid}: local version differs from the requested version; cachedVersion is only a hint."));
                if (patch is null) Request(uid, requested, manifestPath, requests);
                components.Add(new(uid, requested, cached, disabled, dependency, important, declaration.Enabled,
                    effective, patch, MmcJson.Fields(item)));
            }
            ValidateGraph(components, requests, limits, diagnostics);
            string cfgPath = root + "instance.cfg";
            string? cfg = entries.ContainsKey(cfgPath) ? RequiredMetadata(cfgPath, metadata) : null;
            MmcConfiguration configuration = MmcConfigurationReader.Parse(cfg, cfgPath, diagnostics);
            string gameRoot = inspected.GameRoot;
            var package = new MmcPackage(root, gameRoot, MmcPatchReader.Hash(manifest), components.ToImmutable(), configuration,
                requests.DistinctBy(r => (r.Uid, r.Version, r.Kind)).ToImmutableArray(), input.Entries.ToImmutableArray(), metadata.Values.Sum(text => (long)Encoding.UTF8.GetByteCount(text))) { PackageMetadata = packageMetadata.ToImmutableArray() };
            return new(package, diagnostics.ToImmutableArray());
        } catch (MmcInputException e) {
            diagnostics.Add(new(e.Code, MmcDiagnosticSeverity.Error, e.SourcePath, e.Message));
            return new(null, diagnostics.ToImmutableArray());
        }
    }

    private static void ValidateGraph(ImmutableArray<MmcComponent>.Builder components, List<MmcMetadataRequest> requests,
        MmcReadLimits limits, List<MmcDiagnostic> diagnostics) {
        Dictionary<string, MmcComponent> byUid = components.ToDictionary(c => c.Uid, StringComparer.Ordinal);
        long edgeCount = components.Sum(c => (long)(c.LocalPatch?.Requires.Length ?? 0) + (c.LocalPatch?.Conflicts.Length ?? 0));
        if (edgeCount > limits.MaxDependencyEdges) throw new MmcInputException("MMC_DEPENDENCY_BUDGET", "components", "Dependency edge budget exceeded.");
        foreach (MmcComponent component in components) {
            if (component.LocalPatch is not { } patch) continue;
            foreach (MmcRequirement dependency in patch.Requires) {
                if (!byUid.TryGetValue(dependency.Uid, out MmcComponent? provider)) {
                    Request(dependency.Uid, dependency.EqualsVersion ?? dependency.SuggestedVersion, patch.Source, requests);
                    continue;
                }
                if (dependency.EqualsVersion is not null && provider.EffectiveVersion != dependency.EqualsVersion)
                    diagnostics.Add(new("MMC_EXACT_VERSION", MmcDiagnosticSeverity.Error, patch.Source, $"{component.Uid} requires {dependency.Uid} exactly {dependency.EqualsVersion}."));
                if (component.EffectiveEnabled && !provider.EffectiveEnabled)
                    diagnostics.Add(new("MMC_DISABLED_DEPENDENCY", MmcDiagnosticSeverity.Error, patch.Source, $"{component.Uid} requires disabled {dependency.Uid}; explicit resolution is required."));
            }
            if (component.EffectiveEnabled) foreach (MmcRequirement conflict in patch.Conflicts)
                if (byUid.TryGetValue(conflict.Uid, out MmcComponent? target) && target.EffectiveEnabled &&
                    (conflict.EqualsVersion is null || conflict.EqualsVersion == target.EffectiveVersion))
                    diagnostics.Add(new("MMC_COMPONENT_CONFLICT", MmcDiagnosticSeverity.Error, patch.Source, $"{component.Uid} conflicts with {target.Uid}."));
        }
        Dictionary<string, int> colors = new(StringComparer.Ordinal);
        foreach (string uid in byUid.Keys) Visit(uid, 0);
        void Visit(string uid, int depth) {
            if (depth > limits.MaxDepth) throw new MmcInputException("MMC_DEPENDENCY_DEPTH", uid, "Dependency depth exceeded.");
            if (colors.TryGetValue(uid, out int color)) {
                if (color == 1) throw new MmcInputException("MMC_DEPENDENCY_CYCLE", uid, "Component dependency cycle.");
                return;
            }
            colors[uid] = 1;
            if (byUid[uid].LocalPatch is { } patch) foreach (MmcRequirement requirement in patch.Requires)
                if (byUid.ContainsKey(requirement.Uid)) Visit(requirement.Uid, depth + 1);
            colors[uid] = 2;
        }
    }

    private static void Request(string uid, string? version, string source, List<MmcMetadataRequest> requests) {
        requests.Add(string.IsNullOrWhiteSpace(version)
            ? new(uid, "", source, MmcMetadataRequestKind.Recommendation)
            : new(uid, version, source));
    }

    private static Dictionary<string, MmcArchiveEntry> Index(IReadOnlyList<MmcArchiveEntry> source, MmcReadLimits limits, List<MmcDiagnostic> diagnostics, ref long rawPathCharacters) {
        if (source.Count > limits.MaxArchiveEntries) throw new MmcInputException("MMC_ARCHIVE_BUDGET", "archive", "Entry count exceeded.");
        Dictionary<string, MmcArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> aliasDiagnostics = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        long logicalPathCharacters = 0;
        foreach (MmcArchiveEntry entry in source) {
            if (entry is null) throw new MmcInputException("MMC_PATH", "archive", "Null archive entry.");
            ConsumeRawPath(entry.Path, ref rawPathCharacters, limits.MaxTotalPathCharacters);
            string path = Normalize(entry.Path, entry.IsDirectory);
            if (entry.UncompressedSize < 0 || entry.UncompressedSize > limits.MaxEntryBytes || total > limits.MaxArchiveBytes - entry.UncompressedSize)
                throw new MmcInputException("MMC_ARCHIVE_BUDGET", path, "Declared unpacked size exceeded.");
            total += entry.UncompressedSize;
            ConsumeLogical(path.Length);
            AddNode(path, entry);
            int separator = path.LastIndexOf('/');
            while (separator >= 0) {
                // Charge even repeated prefix candidates before allocating the
                // substring, bounding both retained nodes and repeated work.
                ConsumeLogical(separator);
                string parent = path[..separator];
                AddNode(parent, new(parent, 0, true));
                separator = path.LastIndexOf('/', separator - 1);
            }
        }
        return entries;
        void ConsumeLogical(int characters) {
            if (logicalPathCharacters > limits.MaxLogicalPathCharacters - characters)
                throw new MmcInputException("MMC_PATH_BUDGET", "archive", "Cumulative logical path/prefix character budget exceeded.");
            logicalPathCharacters += characters;
        }
        void AddNode(string path, MmcArchiveEntry entry) {
            if (entries.TryGetValue(path, out MmcArchiveEntry? existing)) {
                if (!entry.IsDirectory || !existing.IsDirectory)
                    throw new MmcInputException("MMC_PATH_COLLISION", path, "Leaf file collision or file/directory role conflict.");
                if (!Normalize(existing.Path, true).Equals(path, StringComparison.Ordinal) && aliasDiagnostics.Add(path))
                    diagnostics.Add(new("MMC_DIRECTORY_ALIAS", MmcDiagnosticSeverity.Warning, path,
                        "Directory spelling aliases share a Windows target node; original archive paths remain preserved."));
                return;
            }
            if (entries.Count >= (long)limits.MaxArchiveEntries * 2)
                throw new MmcInputException("MMC_ARCHIVE_BUDGET", path, "Logical directory-node budget exceeded.");
            entries.Add(path, entry);
        }
    }

    private static Dictionary<string, string> Metadata(IReadOnlyDictionary<string, string> source,
        Dictionary<string, MmcArchiveEntry> entries, MmcReadLimits limits, ref long rawPathCharacters) {
        if (source.Count > limits.MaxArchiveEntries) throw new MmcInputException("MMC_METADATA_BUDGET", "metadata", "Metadata count exceeded.");
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        UTF8Encoding strictUtf8 = new(false, true);
        foreach ((string path, string content) in source) {
            ConsumeRawPath(path, ref rawPathCharacters, limits.MaxTotalPathCharacters);
            string normalized = Normalize(path, false);
            if (content is null || !entries.TryGetValue(normalized, out MmcArchiveEntry? entry) || entry.IsDirectory)
                throw new MmcInputException("MMC_METADATA_SOURCE", normalized, "Metadata has no matching archive file.");
            int size;
            try { size = strictUtf8.GetByteCount(content); }
            catch (EncoderFallbackException) { throw new MmcInputException("MMC_METADATA_ENCODING", normalized, "Unpaired UTF-16 surrogate in decoded metadata."); }
            if (size > limits.MaxMetadataBytes || total > limits.MaxTotalMetadataBytes - size)
                throw new MmcInputException("MMC_METADATA_BUDGET", normalized, "Metadata byte budget exceeded.");
            total += size;
            if (!result.TryAdd(normalized, content)) throw new MmcInputException("MMC_PATH_COLLISION", normalized, "Duplicate metadata path.");
        }
        return result;
    }

    private static string RequiredMetadata(string path, Dictionary<string, string> metadata) =>
        metadata.TryGetValue(path, out string? text) ? text : throw new MmcInputException("MMC_METADATA_MISSING", path, "Host must supply the listed metadata content.");

    private static (string Root, string GameRoot, ImmutableArray<string> MetadataPaths) Describe(Dictionary<string, MmcArchiveEntry> entries, MmcReadLimits limits) {
        string[] manifests = entries.Where(e => !e.Value.IsDirectory && e.Key.Split('/')[^1].Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).ToArray();
        if (manifests.Length != 1) throw new MmcInputException("MMC_ROOT", "archive", manifests.Length == 0 ? "Missing mmc-pack.json." : "Multiple instance roots require explicit selection.");
        string manifestPath = manifests[0];
        string root = manifestPath[..^"mmc-pack.json".Length];
        List<string> metadata = [manifestPath];
        long metadataBytes = 0;
        foreach ((string path, MmcArchiveEntry entry) in entries.Where(p => p.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase))) {
            string relative = path[root.Length..];
            if (relative.Equals("PCL/InstanceDefinition.json", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("PCL/Mmc", StringComparison.OrdinalIgnoreCase))
                throw new MmcInputException("MMC_RESERVED_PATH", path, "Source package cannot publish LF ownership records.");
            if (relative.Equals("instance.cfg", StringComparison.OrdinalIgnoreCase)) {
                if (entry.IsDirectory) throw new MmcInputException("MMC_DIRECTORY_ROLE", path, "instance.cfg must be a file.");
                metadata.Add(path);
            } else if (relative.Equals("patches", StringComparison.OrdinalIgnoreCase) && !entry.IsDirectory)
                throw new MmcInputException("MMC_DIRECTORY_ROLE", path, "patches must be a directory.");
            else if (!entry.IsDirectory && relative.StartsWith("patches/", StringComparison.OrdinalIgnoreCase) &&
                !relative["patches/".Length..].Contains('/') && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) metadata.Add(path);
        }
        foreach (string path in metadata) {
            long size = entries[path].UncompressedSize;
            if (size > limits.MaxMetadataBytes || metadataBytes > limits.MaxTotalMetadataBytes - size)
                throw new MmcInputException("MMC_METADATA_BUDGET", path, "Declared source metadata budget exceeded.");
            metadataBytes += size;
        }
        return (root, SelectGameRoot(root, entries), metadata.ToImmutableArray());
    }

    private static string SelectGameRoot(string root, Dictionary<string, MmcArchiveEntry> entries) {
        if (entries.TryGetValue(root + "libraries", out MmcArchiveEntry? libraries) && !libraries.IsDirectory)
            throw new MmcInputException("MMC_DIRECTORY_ROLE", root + "libraries", "Local library directory role is occupied by a file.");
        bool Directory(string leaf) {
            string path = root + leaf;
            if (entries.TryGetValue(path, out MmcArchiveEntry? entry)) {
                if (!entry.IsDirectory) throw new MmcInputException("MMC_DIRECTORY_ROLE", path, "Game directory role is occupied by a file.");
                return true;
            }
            return entries.Keys.Any(key => key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
        }
        bool normal = Directory("minecraft"), hidden = Directory(".minecraft");
        return normal || !hidden ? "minecraft" : ".minecraft";
    }

    private static string Normalize(string path, bool directory) {
        if (string.IsNullOrEmpty(path)) throw new MmcInputException("MMC_PATH", "archive", "Empty path.");
        string value = path.Replace('\\', '/');
        if (directory) value = value.TrimEnd('/');
        string[] segments = value.Split('/');
        if (value.Length > 32767 || segments.Length > 128) throw new MmcInputException("MMC_PATH", path, "Path length/depth budget exceeded.");
        foreach (string segment in segments) {
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
                stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
                throw new MmcInputException("MMC_PATH", path, "Unsafe or ambiguous Windows archive path.");
        }
        return value;
    }

    private static void ConsumeRawPath(string path, ref long total, long limit) {
        if (string.IsNullOrEmpty(path)) throw new MmcInputException("MMC_PATH", "archive", "Empty path.");
        if (total > limit - path.Length) throw new MmcInputException("MMC_PATH_BUDGET", "archive", "Cumulative source path character budget exceeded.");
        total += path.Length;
    }

    private static void ValidateLimits(MmcReadLimits limits) {
        if (limits.MaxArchiveEntries < 1 || limits.MaxComponents < 1 || limits.MaxDependencyEdges < 1 || limits.MaxDepth is < 1 or > 256 ||
            limits.MaxMetadataBytes < 1 || limits.MaxTotalMetadataBytes < 1 || limits.MaxArchiveBytes < 1 || limits.MaxEntryBytes < 1 ||
            limits.MaxTotalPathCharacters < 1 || limits.MaxLogicalPathCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "Limits must be positive and depth must be within 1..256.");
    }
}
