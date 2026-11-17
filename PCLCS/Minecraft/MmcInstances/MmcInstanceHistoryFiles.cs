using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstanceHistoryFiles {
    internal const int MaximumFiles = 100000;
    internal const long MaximumBytes = 64L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AccountFiles = new(["accounts.json", "accounts.cfg", "launcher_accounts.json", "launcher_profiles.json",
        "multimc.cfg", "prismlauncher.cfg"], StringComparer.OrdinalIgnoreCase);

    internal static ImmutableArray<MmcHistoryFile> Sources(MmcInstancePaths paths, MmcStoredPrimaryDefinition primary,
        MmcSavedInstanceSources saved, CancellationToken token) {
        Dictionary<string, (string Hash, long? Size)> expected = new(StringComparer.OrdinalIgnoreCase);
        void Add(string relative, string hash, long? size = null) {
            paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (relative != "PCL/InstanceDefinition.json" && !relative.StartsWith("PCL/MmcSource/", StringComparison.Ordinal) &&
                !relative.StartsWith("PCL/MmcOverrides/", StringComparison.Ordinal) && !relative.StartsWith("PCL/MmcArtifacts/sha256/", StringComparison.Ordinal))
                throw new IOException("History source is outside the immutable instance stores.");
            if (!MmcDurableFiles.IsHash(hash)) throw new IOException("History source digest is invalid.");
            if (expected.TryGetValue(relative, out var old) && (old.Hash != hash || old.Size is not null && size is not null && old.Size != size))
                throw new IOException("History sources disagree at one immutable path.");
            expected[relative] = (hash, size ?? (expected.TryGetValue(relative, out old) ? old.Size : null));
        }
        var definition = primary.Definition;
        foreach (var library in PrivateLibraries(definition)) {
            string? actual = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, library.Key));
            if (actual is not null && actual != library.Value.Sha256) throw new IOException("History cannot capture an unknown modified private library: " + library.Key);
        }
        foreach (var mapped in definition.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetMappedResource &&
            value.RuntimePlanFingerprint == definition.InstalledProjection!.RuntimePlanFingerprint)) {
            token.ThrowIfCancellationRequested();
            string? actual = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, mapped.TargetRelativePath));
            // Environment snapshots omit reproducible derived resources. Edited bytes
            // are no longer reproducible and must not vanish from a purported full undo.
            if (actual is not null && actual != mapped.Sha256)
                throw new IOException("History cannot omit a player-edited mapped resource: " + mapped.TargetRelativePath);
        }
        Add("PCL/InstanceDefinition.json", primary.Sha256);
        if (definition.Origin.PlatformEvidenceReference is { } evidence) Add(evidence.ObjectRelativePath, evidence.Sha256);
        if (definition.PackageOwnershipReference is { } ownership) Add(ownership.ObjectRelativePath, ownership.Sha256);
        foreach (var item in definition.SourceBundle.MetadataRecords) Add(item.ObjectRelativePath, item.ContentSha256);
        foreach (var item in definition.ComponentOverrides) if (item.PatchReference is { } patch) Add(patch.ObjectRelativePath, patch.Sha256);
        foreach (var item in definition.ArtifactInventory) {
            if (item.SourceObject is { } source) Add(source.ObjectRelativePath, source.Sha256);
            if (item.SourceMetadataObjectRelativePath is { } metadata) Add(metadata, item.SourceRevision);
        }
        foreach (var item in MmcPublishedArtifactLocks.Create(definition)) Add(item.RecoveryObjectRelativePath, item.AdoptedSha256, item.SizeBytes);
        foreach (var baseline in definition.PackageFileInventory.Select(item => item.BaselineReference).Append(definition.AuthorFileBaseline!).Distinct())
            Add(baseline.ObjectRelativePath, baseline.ContentSha256);
        // Opaque package originals remain provenance, never live settings or executable inputs.
        foreach (var file in saved.Baseline.Baseline.Files.Where(file => file.TargetRelativePath is null &&
            file.Role != PCLLF.MMCC.Core.MmcPackageFileRole.LocalLibrary &&
            !definition.SourceBundle.MetadataRecords.Any(record => definition.SourceBundle.RelativeRoot + record.RelativePath == file.RelativePath)))
            Add("PCL/MmcSource/" + definition.SourceBundle.BundleSha256 + "/package-files/" + file.Sha256, file.Sha256, file.SizeBytes);
        var result = ImmutableArray.CreateBuilder<MmcHistoryFile>(); long total = 0;
        foreach (var item in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            var measured = Measure(paths, item.Key, token);
            if (measured.Sha256 != item.Value.Hash || item.Value.Size is { } size && measured.SizeBytes != size)
                throw new IOException("History lacks the exact adopted original: " + item.Key);
            total = checked(total + measured.SizeBytes); if (total > MaximumBytes || result.Count >= MaximumFiles) throw new IOException("History source budget exceeded.");
            result.Add(measured);
        }
        return result.ToImmutable();
    }

    internal static bool IsEnvironment(MmcInstancePaths paths, string relative) {
        paths.ResolveRelative(MmcPathRole.Instance, relative);
        string prefix = paths.GameRootLeaf + "/";
        if (!relative.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string gameRelative = relative[prefix.Length..];
        return !AccountFiles.Contains(Path.GetFileName(gameRelative)) && EnvironmentHistoryRules.IsGameFileCovered(gameRelative, 2);
    }
    internal static ImmutableArray<string> NormalizeAdditionalPaths(MmcInstancePaths paths, ImmutableArray<string> additional, bool inspectFilesystem = true) {
        if (additional.IsDefaultOrEmpty) return [];
        if (additional.Length > MaximumFiles) throw new IOException("Additional history coverage exceeds its path-count budget.");
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase); long characters = 0;
        foreach (string relative in additional) {
            if (relative is null || relative.Contains('\\') || !IsAdditionalEnvironment(paths, relative) || !result.Add(relative) || (characters += relative.Length) > 16L * 1024 * 1024)
                throw new IOException("Additional history coverage contains duplicate, protected or invalid paths.");
            string target = paths.ResolveRelative(MmcPathRole.Instance, relative); var attributes = inspectFilesystem ? MmcInstancePaths.Attributes(target) : null;
            if (attributes is not null && (attributes.Value & FileAttributes.Directory) != 0) throw new IOException("Exact history file coverage cannot adopt or remove a directory tree.");
        }
        return result.Order(StringComparer.Ordinal).ToImmutableArray();
    }
    internal static (ImmutableArray<MmcHistoryFile> Files, ImmutableArray<string> Directories) FullEnvironment(MmcInstancePaths paths, CancellationToken token) {
        var files = ImmutableArray.CreateBuilder<MmcHistoryFile>(); var directories = ImmutableArray.CreateBuilder<string>();
        long total = 0; int nodes = 0;
        if (Directory.Exists(paths.GameRoot)) Walk(paths.GameRootLeaf, true, 0);
        return (files.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToImmutableArray(), directories.Order(StringComparer.Ordinal).ToImmutableArray());
        void Walk(string relative, bool root, int depth) {
            token.ThrowIfCancellationRequested(); if (++nodes > 200000 || depth > 64) throw new IOException("Safe environment namespace exceeds its path budget.");
            if (!root && !SafeAdditionalPolicy(paths, relative) && !SafeAdditionalPolicy(paths, relative + "/.history-inventory-probe")) return;
            string target = paths.ResolveRelative(MmcPathRole.Instance, relative); var attributes = MmcInstancePaths.Attributes(target); if (attributes is null) return;
            if ((attributes.Value & FileAttributes.Directory) != 0) {
                if (!root && !IsAdditionalEnvironment(paths, relative + "/.history-inventory-probe")) return;
                if (!root) directories.Add(relative);
                foreach (string child in Directory.EnumerateFileSystemEntries(target)) Walk(relative + "/" + Path.GetFileName(child), false, depth + 1);
            } else if (IsAdditionalEnvironment(paths, relative)) {
                var file = Measure(paths, relative, token); total = checked(total + file.SizeBytes);
                if (total > MaximumBytes || files.Count >= MaximumFiles) throw new IOException("Safe environment inventory exceeds its byte/count budget.");
                files.Add(file);
            }
        }
    }
    internal static ImmutableArray<string> AuthorPaths(MmcInstancePaths paths, MmcInstanceDefinition definition, MmcSavedInstanceSources saved) {
        var derived = definition.DerivedOutputs.Where(value => value.Kind == MmcDerivedOutputKind.AssetMappedResource)
            .Select(value => value.TargetRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = saved.Baseline.Baseline.Files.Where(value => value.Role == PCLLF.MMCC.Core.MmcPackageFileRole.Game && value.TargetRelativePath is not null)
            .Select(value => value.TargetRelativePath!).Where(value => !derived.Contains(value) && IsAdditionalEnvironment(paths, value)).ToList();
        if (MmcInstancePackageOwnership.Read(paths, definition) is { } bytes) {
            var ownership = Newtonsoft.Json.Linq.JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            if (ownership["retained"] is Newtonsoft.Json.Linq.JObject retained)
                foreach (var property in retained.Properties()) {
                    string relative = paths.GameRootLeaf + "/" + property.Name;
                    if (!derived.Contains(relative) && IsAdditionalEnvironment(paths, relative)) result.Add(relative);
                }
        }
        return NormalizeAdditionalPaths(paths, result.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), false);
    }
    internal static bool IsAdditionalEnvironment(MmcInstancePaths paths, string relative) {
        if (!SafeAdditionalPolicy(paths, relative)) return false;
        paths.ResolveRelative(MmcPathRole.Instance, relative); return true;
    }
    private static bool SafeAdditionalPolicy(MmcInstancePaths paths, string relative) {
        string prefix = paths.GameRootLeaf + "/";
        return relative.StartsWith(prefix, StringComparison.Ordinal) && MmcInstancePackageUpdatePlanning<object>.SafeAuthorFile(relative[prefix.Length..]);
    }
    internal static ImmutableArray<MmcHistoryFile> Environment(MmcInstancePaths paths, MmcInstanceDefinition definition, CancellationToken token,
        ImmutableArray<string> additional = default) {
        var result = ImmutableArray.CreateBuilder<MmcHistoryFile>(); long total = 0; int directories = 0;
        HashSet<string> captured = new(StringComparer.OrdinalIgnoreCase);
        var derived = definition.DerivedOutputs.Select(item => item.TargetRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in EnvironmentHistoryRules.GameEnvironmentFolders) Walk(paths.GameRootLeaf + "/" + folder, 0);
        if (Directory.Exists(paths.GameRoot)) foreach (string file in Directory.EnumerateFiles(paths.GameRoot)) Add(paths.GameRootLeaf + "/" + Path.GetFileName(file));
        foreach (string relative in NormalizeAdditionalPaths(paths, additional)) {
            string file = paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (File.Exists(file)) Add(relative, true); // Missing members deliberately retain their declared absence in AdditionalCoverage.Paths.
        }
        return result.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToImmutableArray();
        void Walk(string relative, int depth) {
            token.ThrowIfCancellationRequested(); if (depth > 64) throw new IOException("History environment exceeds its directory-depth budget.");
            string path = paths.ResolveRelative(MmcPathRole.Instance, relative); var attributes = MmcInstancePaths.Attributes(path); if (attributes is null) return;
            if ((attributes.Value & FileAttributes.Directory) == 0) { Add(relative); return; }
            if (++directories > MaximumFiles) throw new IOException("History environment exceeds its directory-count budget.");
            // Do not even enumerate excluded map/account subtrees.
            if (!IsEnvironment(paths, relative + "/.history-probe")) return;
            foreach (string item in Directory.EnumerateFileSystemEntries(path)) Walk(relative + "/" + Path.GetFileName(item), depth + 1);
        }
        void Add(string relative, bool explicitPath = false) {
            if ((!explicitPath && (!IsEnvironment(paths, relative) || derived.Contains(relative))) || !captured.Add(relative)) return;
            var fact = Measure(paths, relative, token); total = checked(total + fact.SizeBytes);
            if (total > MaximumBytes || result.Count >= MaximumFiles) throw new IOException("History environment exceeds its count/byte budget.");
            result.Add(fact);
        }
    }
    internal static Dictionary<string, MmcHistoryFile> PrivateLibraries(MmcInstanceDefinition definition) {
        var local = definition.ArtifactInventory.Where(item => item.Scope == MmcArtifactScope.InstanceLocal).Select(item => item.ArtifactKey).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, MmcHistoryFile> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in definition.ArtifactLocks.Where(item => local.Contains(item.ArtifactKey))) Add(item.TargetRelativePath, item.AdoptedSha256, item.SizeBytes);
        foreach (var item in definition.PackageFileLocks) Add(item.TargetRelativePath, item.AdoptedSha256, item.SizeBytes);
        return result;
        void Add(string relative, string hash, long size) {
            if (result.TryGetValue(relative, out var old) && (old.Sha256 != hash || old.SizeBytes != size)) throw new IOException("History private library declarations conflict.");
            result[relative] = new(relative, hash, size);
        }
    }
    internal static MmcHistoryFile Measure(MmcInstancePaths paths, string relative, CancellationToken token) {
        var value = MmcInstanceCloneSnapshot.Measure(paths.ResolveRelative(MmcPathRole.Instance, relative), relative, token);
        if (value.SizeBytes > MmcDurableFiles.MaximumFileBytes) throw new IOException("History input exceeds the transaction file budget.");
        return new(value.RelativePath, value.Sha256, value.SizeBytes);
    }
    internal static void Verify(MmcInstancePaths paths, IEnumerable<MmcHistoryFile> files, CancellationToken token) {
        long total = 0; int count = 0; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            if (file is null || !MmcDurableFiles.IsHash(file.Sha256) || file.SizeBytes < 0 || !seen.Add(file.RelativePath) || ++count > MaximumFiles ||
                (total = checked(total + file.SizeBytes)) > MaximumBytes || Measure(paths, file.RelativePath, token) != file)
                throw new IOException("History file inventory is damaged or exceeds its budget.");
        }
    }
    internal static void Copy(MmcInstancePaths source, MmcInstancePaths target, IEnumerable<MmcHistoryFile> files, CancellationToken token) {
        var io = new MmcArtifactIo(new(MaxCopiedBytes: MaximumBytes), null);
        foreach (var file in files) {
            string output = target.ResolveRelative(MmcPathRole.Instance, file.RelativePath); MmcArtifactIo.Directory(Path.GetDirectoryName(output)!);
            io.CopyAsync(source.ResolveRelative(MmcPathRole.Instance, file.RelativePath), output, file.RelativePath, file.Sha256, null, file.SizeBytes, token).GetAwaiter().GetResult();
        }
    }
}
