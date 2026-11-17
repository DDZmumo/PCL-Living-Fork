using System.Collections.Immutable;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstanceHistoryStore {
    internal const int CoverageVersion = 3;
    internal const int ExtendedCoverageVersion = 4;
    internal const int FullSafeCoverageVersion = 5;
    private const string Store = "PCL/MmcHistory/";
    private static string Id(Guid id) => id != Guid.Empty ? id.ToString("N") : throw new IOException("History point identity is empty.");
    internal static string PointRoot(MmcInstancePaths paths, Guid id) => paths.ResolveRelative(MmcPathRole.Instance, Store + "points/" + Id(id));
    private static string IndexPath(MmcInstancePaths paths, Guid id) => paths.ResolveRelative(MmcPathRole.Instance, Store + "index/" + Id(id) + ".record");
    private static MmcInstancePaths SourcePaths(string pointRoot, string leaf) => MmcInstancePaths.Resolve(Path.Combine(pointRoot, "versions", "snapshot"), leaf);

    internal static MmcInstanceHistoryPoint Capture(MmcAdmissionLease lease, MmcStoredPrimaryDefinition primary,
        MmcSavedInstanceSources saved, string label, CancellationToken token, Action<MmcInstanceHistoryCheckpoint>? checkpoint,
        ImmutableArray<string> additionalEnvironmentPaths = default, bool fullSafeEnvironment = false,
        ImmutableArray<string> managedScopeOverride = default) {
        lease.AssertHeld(); ValidateLabel(label); var paths = MmcInstancePaths.Resolve(lease.VersionRoot, primary.Definition.Layout.GameRootLeaf);
        var additional = MmcInstanceHistoryFiles.NormalizeAdditionalPaths(paths, additionalEnvironmentPaths);
        var source = MmcInstanceHistoryFiles.Sources(paths, primary, saved, token);
        if (fullSafeEnvironment) MmcInstanceHistoryEnvironmentObjects.AssertMappedHealthy(paths, primary.Definition);
        var fullInventory = fullSafeEnvironment ? MmcInstanceHistoryFiles.FullEnvironment(paths, token) : default;
        var environment = fullSafeEnvironment ? fullInventory.Files : MmcInstanceHistoryFiles.Environment(paths, primary.Definition, token, additional);
        var managed = fullSafeEnvironment ? MmcInstanceHistoryFiles.NormalizeAdditionalPaths(paths,
            managedScopeOverride.IsDefault
                ? MmcInstanceHistoryFiles.AuthorPaths(paths, primary.Definition, saved).Concat(MmcInstanceHistoryFiles.Environment(paths, primary.Definition, token)
                    .Where(value => MmcInstanceHistoryFiles.IsAdditionalEnvironment(paths, value.RelativePath)).Select(value => value.RelativePath))
                    .Concat(additional).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
                : managedScopeOverride, false) : additional;
        if (fullSafeEnvironment && managed.Any(path => primary.Definition.DerivedOutputs.Any(value => value.Kind == MmcDerivedOutputKind.AssetMappedResource && value.TargetRelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))))
            throw new IOException("A derived mapped asset cannot be recategorized as ordinary managed environment data.");
        if (source.Length + environment.Length > MmcInstanceHistoryFiles.MaximumFiles || source.Concat(environment).Sum(file => file.SizeBytes) > MmcInstanceHistoryFiles.MaximumBytes)
            throw new IOException("Combined history snapshot exceeds its budget.");
        // The exclusive GUID directory is private until its index reference is published.
        // No directory rename is needed while Windows/indexers may still observe copied files.
        Guid id = Guid.NewGuid(); string root = PointRoot(paths, id);
        MmcArtifactIo.Directory(Path.GetDirectoryName(root)!); MmcOwnedImportReservation.CreateExclusiveDirectory(root);
        MmcDurableFiles.WriteNew(Path.Combine(root, "owner.txt"), Encoding.UTF8.GetBytes("PCLLF.MmcHistory/v1\n" + id.ToString("N") + "\n" + primary.Definition.InstanceId.ToString("D")));
        var target = SourcePaths(root, paths.GameRootLeaf); MmcArtifactIo.Directory(target.InstanceRoot);
        MmcInstanceHistoryFiles.Copy(paths, target, fullSafeEnvironment ? source : source.Concat(environment), token);
        MmcInstanceHistoryFiles.Verify(target, fullSafeEnvironment ? source : source.Concat(environment), token);
        if (fullSafeEnvironment) MmcInstanceHistoryEnvironmentObjects.Capture(paths, environment, token);
        checkpoint?.Invoke(MmcInstanceHistoryCheckpoint.SourcesCaptured);
        // Final source namespace and byte checks happen before any visible index is published.
        var observedFull = fullSafeEnvironment ? MmcInstanceHistoryFiles.FullEnvironment(paths, token) : default;
        if (!source.SequenceEqual(MmcInstanceHistoryFiles.Sources(paths, primary, saved, token)) ||
            !environment.SequenceEqual(fullSafeEnvironment ? observedFull.Files : MmcInstanceHistoryFiles.Environment(paths, primary.Definition, token, additional)) ||
            fullSafeEnvironment && !fullInventory.Directories.SequenceEqual(observedFull.Directories))
            throw new IOException("The history source changed during capture; the private point was preserved.");
        AssertCurrent(lease, primary.Definition.InstanceId, primary.Definition.Generation, primary.Sha256);
        var manifest = new MmcHistoryManifest(1, fullSafeEnvironment ? FullSafeCoverageVersion : additional.IsEmpty ? CoverageVersion : ExtendedCoverageVersion, id, primary.Definition.InstanceId, primary.Definition.Generation,
            primary.Sha256, label, DateTime.UtcNow, paths.GameRootLeaf, source, environment) {
                AdditionalCoverage = fullSafeEnvironment || additional.IsEmpty ? null : new(1, additional),
                FullSafeCoverage = fullSafeEnvironment ? new(1, 1, managed, fullInventory.Directories) : null
            };
        string digest = MmcDurableFiles.WriteRecord(Path.Combine(root, "manifest.record"), manifest);
        token.ThrowIfCancellationRequested();
        string index = IndexPath(paths, id); MmcArtifactIo.Directory(Path.GetDirectoryName(index)!);
        string temporary = index + ".pending-" + Guid.NewGuid().ToString("N");
        MmcDurableFiles.WriteRecord(temporary, new MmcHistoryIndex(1, id, primary.Definition.InstanceId, digest));
        File.Move(temporary, index, false);
        var loaded = Read(paths, id, digest, CancellationToken.None);
        checkpoint?.Invoke(MmcInstanceHistoryCheckpoint.PointPublished);
        return loaded.Point;
    }

    internal static MmcHistoryLoadedPoint Read(MmcInstancePaths paths, Guid id, string? expectedManifestHash, CancellationToken token) {
        var (manifest, hash, source) = Header(paths, id, expectedManifestHash);
        MmcInstanceHistoryFiles.Verify(source, manifest.FullSafeCoverage is null ? manifest.SourceFiles.Concat(manifest.EnvironmentFiles) : manifest.SourceFiles, token);
        if (manifest.FullSafeCoverage is not null)
            foreach (var file in manifest.EnvironmentFiles) MmcInstanceHistoryEnvironmentObjects.Verify(paths, file, token);
        var primary = MmcSavedSourceStore.ReadPrimary(source);
        if (primary.Sha256 != manifest.PrimarySha256 || primary.Definition.InstanceId != manifest.InstanceId || primary.Definition.Generation != manifest.Generation ||
            primary.Definition.Layout.GameRootLeaf != manifest.GameRootLeaf)
            throw new IOException("History definition differs from its fixed snapshot identity.");
        var saved = MmcSavedSourceStore.LoadAsync(source, primary.Definition, token).GetAwaiter().GetResult();
        if (!manifest.SourceFiles.SequenceEqual(MmcInstanceHistoryFiles.Sources(source, primary, saved, token)))
            throw new IOException("History omits or adds immutable source authority.");
        return new(Describe(manifest, hash), manifest, source, primary.Definition, saved, paths);
    }
    private static (MmcHistoryManifest Manifest, string Hash, MmcInstancePaths Source) Header(MmcInstancePaths paths, Guid id, string? expectedManifestHash) {
        var index = MmcDurableFiles.ReadRecord<MmcHistoryIndex>(IndexPath(paths, id)).Value;
        if (index.SchemaVersion != 1 || index.PointId != id || index.InstanceId == Guid.Empty || !MmcDurableFiles.IsHash(index.ManifestSha256) ||
            expectedManifestHash is not null && index.ManifestSha256 != expectedManifestHash)
            throw new IOException("The selected history index/reference is stale, damaged or unsupported.");
        string root = PointRoot(paths, id);
        var (manifest, hash) = MmcDurableFiles.ReadRecord<MmcHistoryManifest>(Path.Combine(root, "manifest.record"));
        if (hash != index.ManifestSha256 || manifest.SchemaVersion != 1 || manifest.CoverageVersion is not (CoverageVersion or ExtendedCoverageVersion or FullSafeCoverageVersion) || manifest.PointId != id ||
            manifest.InstanceId != index.InstanceId || manifest.Generation < 1 || !MmcDurableFiles.IsHash(manifest.PrimarySha256) ||
            manifest.GameRootLeaf is not ("minecraft" or ".minecraft") || manifest.SourceFiles.IsDefault || manifest.EnvironmentFiles.IsDefault ||
            manifest.CreatedUtc.Kind != DateTimeKind.Utc || manifest.SourceFiles.Length + manifest.EnvironmentFiles.Length > MmcInstanceHistoryFiles.MaximumFiles)
            throw new IOException("History coverage or its hash-bound manifest is unsupported or damaged.");
        ValidateLabel(manifest.Label);
        var source = SourcePaths(root, manifest.GameRootLeaf);
        if (manifest.CoverageVersion == CoverageVersion ? manifest.AdditionalCoverage is not null || manifest.FullSafeCoverage is not null :
            manifest.CoverageVersion == ExtendedCoverageVersion ? manifest.FullSafeCoverage is not null || manifest.AdditionalCoverage is not { FormatVersion: 1 } || manifest.AdditionalCoverage.Paths.IsDefaultOrEmpty :
            manifest.AdditionalCoverage is not null || manifest.FullSafeCoverage is not { FormatVersion: 1, PolicyVersion: 1 } || manifest.FullSafeCoverage.ManagedPaths.IsDefault || manifest.FullSafeCoverage.Directories.IsDefault)
            throw new IOException("History coverage semantics are unsupported; fixed-scope points cannot claim dynamic coverage.");
        var additional = MmcInstanceHistoryFiles.NormalizeAdditionalPaths(source, manifest.AdditionalCoverage?.Paths ?? manifest.FullSafeCoverage?.ManagedPaths ?? [], manifest.FullSafeCoverage is null);
        if (manifest.AdditionalCoverage is not null && !additional.SequenceEqual(manifest.AdditionalCoverage.Paths)) throw new IOException("History exact-path coverage is not canonical.");
        var covered = additional.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (manifest.FullSafeCoverage is not null && !additional.SequenceEqual(manifest.FullSafeCoverage.ManagedPaths)) throw new IOException("Full history managed-path scope is not canonical.");
        if (manifest.EnvironmentFiles.Any(file => file is null || (manifest.FullSafeCoverage is null
            ? !MmcInstanceHistoryFiles.IsEnvironment(source, file.RelativePath) && !covered.Contains(file.RelativePath)
            : !MmcInstanceHistoryFiles.IsAdditionalEnvironment(source, file.RelativePath))))
            throw new IOException("History contains player data or settings outside its declared environment coverage.");
        var present = manifest.EnvironmentFiles.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string absent in additional.Where(path => manifest.FullSafeCoverage is null && !present.Contains(path)))
            if (MmcInstancePaths.Attributes(source.ResolveRelative(MmcPathRole.Instance, absent)) is not null)
                throw new IOException("A declared absent history target has an unexpected payload or directory.");
        long bytes = 0; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.SourceFiles.Concat(manifest.EnvironmentFiles)) {
            if (file is null || !MmcDurableFiles.IsHash(file.Sha256) || file.SizeBytes < 0 || file.SizeBytes > MmcDurableFiles.MaximumFileBytes || !seen.Add(file.RelativePath) ||
                (bytes = checked(bytes + file.SizeBytes)) > MmcInstanceHistoryFiles.MaximumBytes)
                throw new IOException("History file inventory shape or byte budget is invalid.");
            source.ResolveRelative(MmcPathRole.Instance, file.RelativePath);
        }
        if (manifest.FullSafeCoverage is { } full) {
            if (!manifest.EnvironmentFiles.SequenceEqual(manifest.EnvironmentFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal)))
                throw new IOException("Full history file inventory is not canonical.");
            if (full.Directories.Length > 200000 || !full.Directories.SequenceEqual(full.Directories.Order(StringComparer.Ordinal)) || full.Directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != full.Directories.Length)
                throw new IOException("Full history directory-role inventory is not canonical.");
            var inventory = manifest.EnvironmentFiles.Select(file => new MmcArchiveEntry(file.RelativePath, file.SizeBytes)).Concat(full.Directories.Select(path => new MmcArchiveEntry(path, 0, true))).ToArray();
            if (MmcPackageReader.ValidateNamespace(inventory, new() { MaxArchiveEntries = 200000 }).Any(value => value.Severity == MmcDiagnosticSeverity.Error)) throw new IOException("Full history namespace has conflicting file/directory roles.");
            foreach (string directory in full.Directories) if (!MmcInstanceHistoryFiles.IsAdditionalEnvironment(source, directory + "/.history-inventory-probe")) throw new IOException("Full history namespace includes a protected directory role.");
            if (additional.Any(path => full.Directories.Contains(path, StringComparer.OrdinalIgnoreCase))) throw new IOException("A managed file path was a directory in the full snapshot.");
        }
        return (manifest, hash, source);
    }
    private static MmcInstanceHistoryPoint Describe(MmcHistoryManifest manifest, string hash) => new(manifest.PointId, hash, manifest.InstanceId,
        manifest.Generation, manifest.PrimarySha256, manifest.CoverageVersion, manifest.Label, manifest.CreatedUtc, manifest.GameRootLeaf,
        manifest.SourceFiles.Length + manifest.EnvironmentFiles.Length, manifest.SourceFiles.Concat(manifest.EnvironmentFiles).Sum(file => file.SizeBytes)) {
            AdditionalEnvironmentPaths = manifest.AdditionalCoverage?.Paths ?? manifest.FullSafeCoverage?.ManagedPaths ?? [],
            FullSafeEnvironment = manifest.FullSafeCoverage is not null
        };

    internal static ImmutableArray<MmcInstanceHistoryPoint> List(MmcInstancePaths paths, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        string directory = paths.ResolveRelative(MmcPathRole.Instance, Store + "index");
        if (!Directory.Exists(directory)) return [];
        var points = ImmutableArray.CreateBuilder<MmcInstanceHistoryPoint>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.record")) {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || points.Count >= 10000)
                throw new IOException("History index identity/count is invalid.");
            // Browsing validates the trusted index and bounded manifest, without rehashing every
            // multi-gigabyte point. Capture/restore perform the full object and source proof.
            var (manifest, hash, _) = Header(paths, id, null); points.Add(Describe(manifest, hash));
        }
        return points.OrderByDescending(point => point.CreatedUtc).ToImmutableArray();
    }
    internal static MmcStoredPrimaryDefinition AssertCurrent(MmcAdmissionLease lease, Guid id, long generation, string hash) {
        lease.AssertHeld(); MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        var paths = MmcInstancePaths.Resolve(lease.VersionRoot); var current = MmcSavedSourceStore.ReadPrimary(paths);
        var management = MmcManagementProbe.Read(lease.VersionRoot);
        if (id == Guid.Empty || generation < 1 || !MmcDurableFiles.IsHash(hash) || current.Definition.InstanceId != id || current.Definition.Generation != generation ||
            current.Sha256 != hash || management.State != MmcManagementState.ManagedMmc || management.Envelope?.InstanceId != id || management.Envelope.Generation != generation ||
            MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, Path.GetFileName(paths.InstanceRoot) + ".json")) != current.Definition.InstalledProjection!.OutputJsonSha256)
            throw new IOException("History needs the exact current healthy publication and no active session or pending operation.");
        return current;
    }
    private static void ValidateLabel(string label) {
        if (label is null || label.Length > 256 || label.Any(char.IsControl)) throw new IOException("History label is invalid or exceeds its budget.");
    }
}
