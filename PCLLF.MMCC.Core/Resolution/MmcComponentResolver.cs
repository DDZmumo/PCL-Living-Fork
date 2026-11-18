// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

/// <summary>Reentrant batch resolver: retain host snapshots and call again until complete or rejected.</summary>
public static class MmcComponentResolver {
    private sealed class Node(string uid, string? version, bool enabled, bool automatic,
        MmcComponent? declaration, MmcComponentOverride? user) {
        internal string Uid = uid;
        internal string? Version = version;
        internal bool Enabled = enabled;
        internal bool Automatic = automatic;
        internal MmcComponent? Declaration = declaration;
        internal MmcComponentOverride? User = user;
        internal MmcResolvedComponent? Resolved;
    }

    public static MmcResolutionResult Resolve(MmcPackage package, IReadOnlyList<MmcMetadataSnapshot> snapshots,
        IReadOnlyList<MmcComponentOverride>? overrides = null, MmcReadLimits? limits = null,
        IReadOnlyList<MmcVersionRecommendation>? recommendations = null) {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(snapshots);
        limits ??= new();
        List<MmcDiagnostic> diagnostics = [];
        List<MmcMetadataRequest> requests = [];
        try {
            if (package.PackageLocalScopeSha256 is { } localScope) MmcDefinitionCodec.CheckHash(localScope);
            if (limits.MaxComponents < 1 || limits.MaxDependencyEdges < 1 || limits.MaxDepth is < 1 or > 256 || limits.MaxMetadataBytes < 1 || limits.MaxTotalMetadataBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(limits));
            if (package.Components.Length > limits.MaxComponents || snapshots.Count > limits.MaxComponents * 4L || (overrides?.Count ?? 0) > limits.MaxComponents || (recommendations?.Count ?? 0) > limits.MaxComponents)
                throw new MmcInputException("MMC_COMPONENT_BUDGET", "resolution", "Component/snapshot budget exceeded.");
            long metadataBytes = package.MetadataUtf8ByteCount;
            if (metadataBytes < 0 || metadataBytes > limits.MaxTotalMetadataBytes) throw new MmcInputException("MMC_METADATA_BUDGET", "package", "Source metadata budget exceeded.");
            var metadata = IndexSnapshots(snapshots, limits, ref metadataBytes);
            Dictionary<string, MmcComponentOverride> user = new(StringComparer.Ordinal);
            foreach (var item in overrides ?? []) {
                if (item is null) MmcJson.Fail("overrides", "Null component override.");
                MmcPatchReader.ValidateUid(item.Uid, "overrides");
                if (item.PatchContentUtf8 is { } text) {
                    int size = CheckTextBudget(text, item.PatchSource ?? item.Uid, limits.MaxMetadataBytes);
                    if (metadataBytes > limits.MaxTotalMetadataBytes - size) throw new MmcInputException("MMC_METADATA_BUDGET", item.Uid, "Cumulative override metadata budget exceeded.");
                    metadataBytes += size;
                }
                if (!user.TryAdd(item.Uid, item)) MmcJson.Fail("overrides", "Duplicate override UID.");
            }
            Dictionary<string, MmcVersionRecommendation> recommended = new(StringComparer.Ordinal);
            foreach (var snapshot in snapshots.Where(s => s.Kind == MmcMetadataRequestKind.Recommendation)) {
                var indexRecommendation = ReadRecommendation(snapshot, limits);
                if (indexRecommendation is not null && !recommended.TryAdd(indexRecommendation.Uid, indexRecommendation))
                    MmcJson.Fail("recommendations", "Ambiguous recommendation source.");
            }
            foreach (var item in recommendations ?? []) {
                if (item is null || string.IsNullOrEmpty(item.Version) || string.IsNullOrEmpty(item.Source) || !Hex(item.IndexSha256, 64))
                    MmcJson.Fail("recommendations", "Recommendation must bind an exact version and index source digest.");
                MmcPatchReader.ValidateUid(item.Uid, "recommendations");
                if (!recommended.TryAdd(item.Uid, item) && recommended[item.Uid] != item) MmcJson.Fail("recommendations", "Ambiguous recommendation for UID.");
            }
            List<Node> order = [];
            Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
            foreach (MmcComponent component in package.Components) {
                user.TryGetValue(component.Uid, out var change);
                bool fixedEnabled = component.Important || component.DependencyOnly;
                bool enabled = fixedEnabled || (change?.Enabled ?? !component.Disabled);
                if (change?.Enabled == false && fixedEnabled)
                    diagnostics.Add(new("MMC_CANNOT_DISABLE", MmcDiagnosticSeverity.Warning, component.Uid, "Important/dependencyOnly component remains enabled."));
                bool automatic = component.DependencyOnly && component.LocalPatch is null && change?.Version is null && change?.PatchContentUtf8 is null;
                Node node = new(component.Uid, change?.Version ?? component.EffectiveVersion, enabled, automatic, component, change);
                if (!nodes.TryAdd(node.Uid, node)) MmcJson.Fail("components", "Duplicate component UID.");
                order.Add(node);
            }
            foreach (string uid in user.Keys) if (!nodes.ContainsKey(uid)) MmcJson.Fail("overrides", "Override does not identify an existing component.");
            Dictionary<(string Uid, string Version, bool Enabled), MmcPatch> parsed = [];
            List<MmcVersionRecommendation> adoptedRecommendations = [];
            int persistentDiagnostics = diagnostics.Count;
            for (int pass = 0; pass <= limits.MaxComponents; pass++) {
                bool changed = false;
                requests.Clear();
                diagnostics.RemoveRange(persistentDiagnostics, diagnostics.Count - persistentDiagnostics);
                // Fixed declarations can constrain automatic roots before those
                // roots consume metadata for a version that is already obsolete.
                foreach (Node root in order.Where(n => n.Declaration is not null && !n.Automatic)) Adopt(root);
                var fixedConstraints = order.Where(n => n.Declaration is not null && !n.Automatic && n.Resolved is not null)
                    .SelectMany(n => n.Resolved!.Patch.Requires).Where(r => r.EqualsVersion is not null).GroupBy(r => r.Uid, StringComparer.Ordinal);
                foreach (var constraint in fixedConstraints) {
                    string[] versions = constraint.Select(r => r.EqualsVersion!).Distinct(StringComparer.Ordinal).ToArray();
                    if (versions.Length == 1 && nodes.TryGetValue(constraint.Key, out Node? automatic) && automatic.Automatic && automatic.Version != versions[0]) {
                        automatic.Version = versions[0]; automatic.Resolved = null; changed = true;
                    }
                }
                // Rebuild reachability from every author declaration. Only
                // discovered nodes can disappear; author roots remain intact.
                HashSet<string> reachable = new(StringComparer.Ordinal);
                Queue<Node> visit = new(order.Where(n => n.Declaration is not null));
                long reachableEdges = 0;
                while (visit.TryDequeue(out Node? node)) {
                    if (!reachable.Add(node.Uid)) continue;
                    Adopt(node);
                    reachableEdges += (node.Resolved?.Patch.Requires.Length ?? 0) + (long)(node.Resolved?.Patch.Conflicts.Length ?? 0);
                    if (reachableEdges > limits.MaxDependencyEdges) throw new MmcInputException("MMC_DEPENDENCY_BUDGET", node.Uid, "Dependency traversal budget exceeded.");
                    foreach (var dependency in node.Resolved?.Patch.Requires ?? [])
                        if (nodes.TryGetValue(dependency.Uid, out Node? target)) visit.Enqueue(target);
                }
                foreach (Node obsolete in order.Where(n => n.Declaration is null && !reachable.Contains(n.Uid)).ToArray()) {
                    order.Remove(obsolete); nodes.Remove(obsolete.Uid); changed = true;
                }
                var requirements = new Dictionary<string, List<(Node Consumer, MmcRequirement Requirement)>>(StringComparer.Ordinal);
                long edges = 0;
                foreach (Node node in order) if (node.Resolved is { } resolved) {
                    edges += resolved.Patch.Requires.Length + (long)resolved.Patch.Conflicts.Length;
                    if (edges > limits.MaxDependencyEdges) throw new MmcInputException("MMC_DEPENDENCY_BUDGET", node.Uid, "Dependency edge budget exceeded.");
                    foreach (var requirement in resolved.Patch.Requires) {
                        if (!requirements.TryGetValue(requirement.Uid, out var consumers)) requirements.Add(requirement.Uid, consumers = []);
                        consumers.Add((node, requirement));
                    }
                }
                // Select all unambiguous existing automatic versions first.
                // Conflicts from old patches are not final until reselection
                // has converged, and stale dependencies are never inserted.
                List<MmcInputException> pendingConflicts = [];
                bool reselected = false;
                foreach ((string uid, var consumers) in requirements) {
                    string[] exacts = consumers.Select(x => x.Requirement.EqualsVersion).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                    if (exacts.Length > 1) { pendingConflicts.Add(new("MMC_EXACT_VERSION", uid, "Consumers require conflicting exact versions.")); continue; }
                    string? exact = exacts.SingleOrDefault();
                    if (nodes.TryGetValue(uid, out Node? existing)) {
                        if (exact is not null && existing.Version != exact) {
                            if (!existing.Automatic || existing.Declaration?.LocalPatch is not null || existing.User?.PatchContentUtf8 is not null) {
                                pendingConflicts.Add(new("MMC_EXACT_VERSION", uid, "An exact dependency conflicts with a local or explicit component version."));
                                continue;
                            }
                            existing.Version = exact;
                            existing.Resolved = null;
                            reselected = true;
                        }
                    }
                }
                if (reselected) continue;
                foreach ((string uid, var consumers) in requirements) {
                    string[] exacts = consumers.Select(x => x.Requirement.EqualsVersion).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                    if (exacts.Length > 1) continue;
                    string? exact = exacts.SingleOrDefault();
                    if (nodes.TryGetValue(uid, out Node? existing)) {
                        if (existing.Declaration is null) {
                            int firstConsumer = consumers.Min(c => order.IndexOf(c.Consumer));
                            int current = order.IndexOf(existing);
                            if (current > firstConsumer) { order.RemoveAt(current); order.Insert(firstConsumer, existing); changed = true; }
                        }
                        continue;
                    }
                    if (nodes.Count >= limits.MaxComponents) throw new MmcInputException("MMC_COMPONENT_BUDGET", uid, "Discovered component budget exceeded.");
                    string? selected = exact ?? consumers.Select(c => c.Requirement.SuggestedVersion).FirstOrDefault(v => v is not null);
                    Node added = new(uid, selected, true, true, null, null);
                    nodes.Add(uid, added);
                    order.Insert(consumers.Min(c => order.IndexOf(c.Consumer)), added);
                    changed = true;
                }
                if (changed) continue;
                if (pendingConflicts.Count != 0) throw pendingConflicts[0];
                ValidateGraph(order, nodes, limits);
                if (requests.Count != 0) return Finish(null);
                if (order.Any(n => n.Resolved is null)) throw new MmcInputException("MMC_RESOLUTION", "resolution", "Unresolved component without a metadata request.");
                var components = order.Select(n => n.Resolved!).ToImmutableArray();
                var actualRecommendations = adoptedRecommendations.Where(r => nodes.TryGetValue(r.Uid, out var node) && node.Version == r.Version).Distinct().ToImmutableArray();
                string fingerprint = MmcFingerprint.Of(new[] { "mmc-prism-v1-resolver-1", package.ManifestSha256 }
                    .Concat(components.SelectMany(c => new[] { c.Uid, c.Version, c.Enabled.ToString(), c.SourceKind.ToString(), c.Patch.Source, c.Patch.ContentSha256 }))
                    .Concat(actualRecommendations.SelectMany(r => new[] { r.Uid, r.Version, r.Source, r.IndexSha256 }))
                    .Concat(package.PackageLocalScopeSha256 is { } scope ? new[] { "mmc-measured-package-local-v1", scope } : []));
                return Finish(new("prism-v1", fingerprint, package, components, actualRecommendations));
            }
            throw new MmcInputException("MMC_RESOLUTION", "resolution", "Resolution did not converge within the component budget.");

            void Adopt(Node node) {
                if (node.Resolved is not null) { MmcPatchReader.ValidateCapabilities(node.Resolved.Patch, node.Enabled, diagnostics); return; }
                if (node.User?.PatchContentUtf8 is { } userText) {
                    string source = node.User.PatchSource ?? "user:" + node.Uid;
                    CheckTextBudget(userText, source, limits.MaxMetadataBytes);
                    var patch = MmcPatchReader.Parse(userText, source, limits, node.Enabled, diagnostics);
                    CheckIdentity(patch, node.Uid, null);
                    node.Version = Nonempty(patch.Version) ?? node.Version;
                    node.Resolved = new(node.Uid, node.Version, node.Enabled, false, MmcComponentSourceKind.UserPatch, patch, node.Declaration);
                    return;
                }
                if (node.Declaration?.LocalPatch is { } local) {
                    // Recheck capability state after explicit enable/disable overrides
                    // without serializing source JSON or accepting stale warning state.
                    MmcPatchReader.ValidateCapabilities(local, node.Enabled, diagnostics);
                    node.Version = Nonempty(local.Version) ?? node.Version;
                    node.Resolved = new(node.Uid, node.Version, node.Enabled, false, MmcComponentSourceKind.PackageLocal, local, node.Declaration);
                    return;
                }
                if (node.Version is null) {
                    if (!recommended.TryGetValue(node.Uid, out var candidate)) {
                        if (metadata.ContainsKey((node.Uid, "", MmcMetadataRequestKind.Recommendation)))
                            throw new MmcInputException("MMC_VERSION_REQUIRED", node.Uid, "Index has no unambiguous recommended version; specify a version.");
                        requests.Add(new(node.Uid, "", node.Uid, MmcMetadataRequestKind.Recommendation));
                        return;
                    }
                    node.Version = candidate.Version;
                    adoptedRecommendations.Add(candidate);
                }
                if (!metadata.TryGetValue((node.Uid, node.Version, MmcMetadataRequestKind.Component), out var snapshot)) { requests.Add(new(node.Uid, node.Version, node.Uid)); return; }
                if (recommended.TryGetValue(node.Uid, out var selectedRecommendation) && selectedRecommendation.Version == node.Version &&
                    selectedRecommendation.VersionSha256 is { } requiredHash && !MmcPatchReader.Hash(snapshot.ContentUtf8).Equals(requiredHash, StringComparison.OrdinalIgnoreCase))
                    throw new MmcInputException("MMC_METADATA_DIGEST", snapshot.Source, "Selected component differs from its index digest.");
                var key = (node.Uid, node.Version, node.Enabled);
                if (!parsed.TryGetValue(key, out var remote)) {
                    remote = MmcPatchReader.Parse(snapshot.ContentUtf8, snapshot.Source, limits, node.Enabled, diagnostics);
                    CheckIdentity(remote, node.Uid, node.Version);
                    parsed.Add(key, remote);
                } else MmcPatchReader.ValidateCapabilities(remote, node.Enabled, diagnostics);
                node.Resolved = new(node.Uid, node.Version, node.Enabled, node.Automatic, MmcComponentSourceKind.RemoteMetadata, remote, node.Declaration);
            }
        } catch (MmcInputException e) {
            diagnostics.Add(new(e.Code, MmcDiagnosticSeverity.Error, e.SourcePath, e.Message));
            return Finish(null);
        }
        MmcResolutionResult Finish(MmcResolvedProfile? profile) => new(diagnostics.Any(d => d.Severity == MmcDiagnosticSeverity.Error) ? null : profile,
            requests.DistinctBy(r => (r.Uid, r.Version, r.Kind, r.SourceIdentity)).ToImmutableArray(), diagnostics.Distinct().ToImmutableArray());
    }

    public static MmcResolutionResult Resolve(MmcParseResult parsed, IReadOnlyList<MmcMetadataSnapshot> snapshots,
        IReadOnlyList<MmcComponentOverride>? overrides = null, MmcReadLimits? limits = null,
        IReadOnlyList<MmcVersionRecommendation>? recommendations = null) {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.HasErrors || parsed.Package is null) return new(null, [], parsed.Diagnostics);
        var resolved = Resolve(parsed.Package, snapshots, overrides, limits, recommendations);
        return resolved with { Diagnostics = parsed.Diagnostics.AddRange(resolved.Diagnostics).Distinct().ToImmutableArray() };
    }

    private static Dictionary<(string, string, MmcMetadataRequestKind), MmcMetadataSnapshot> IndexSnapshots(IReadOnlyList<MmcMetadataSnapshot> source, MmcReadLimits limits, ref long total) {
        Dictionary<(string, string, MmcMetadataRequestKind), MmcMetadataSnapshot> index = [];
        foreach (var item in source) {
            if (item is null || !Enum.IsDefined(item.Kind) || string.IsNullOrEmpty(item.Source) || item.Kind == MmcMetadataRequestKind.Component && string.IsNullOrEmpty(item.Version) ||
                item.Kind == MmcMetadataRequestKind.Recommendation && item.Version != "") MmcJson.Fail("metadata", "Snapshot must have an exact identity, kind and source.");
            MmcPatchReader.ValidateUid(item.Uid, item.Source);
            int size = CheckTextBudget(item.ContentUtf8, item.Source, limits.MaxMetadataBytes);
            if (total > limits.MaxTotalMetadataBytes - size) throw new MmcInputException("MMC_METADATA_BUDGET", item.Source, "Cumulative metadata snapshot budget exceeded.");
            total += size;
            string hash = MmcPatchReader.Hash(item.ContentUtf8);
            if (item.ExpectedSha256 is not null && (!Hex(item.ExpectedSha256, 64) || !hash.Equals(item.ExpectedSha256, StringComparison.OrdinalIgnoreCase)))
                throw new MmcInputException("MMC_METADATA_DIGEST", item.Source, "Metadata differs from its expected UTF-8 digest.");
            if (index.TryGetValue((item.Uid, item.Version, item.Kind), out var previous)) {
                if (previous.Source != item.Source || previous.ContentUtf8 != item.ContentUtf8) throw new MmcInputException("MMC_METADATA_AMBIGUOUS", item.Source, "Multiple source snapshots identify the same component/version.");
            } else index.Add((item.Uid, item.Version, item.Kind), item);
        }
        return index;
    }

    private static MmcVersionRecommendation? ReadRecommendation(MmcMetadataSnapshot snapshot, MmcReadLimits limits) {
        using var document = MmcJson.Read(snapshot.ContentUtf8, snapshot.Source, limits.MaxDepth);
        JsonElement root = document.RootElement;
        MmcJson.FormatVersion(root, snapshot.Source, true);
        if (MmcJson.String(root, "uid", snapshot.Source, true) != snapshot.Uid) throw new MmcInputException("MMC_METADATA_IDENTITY", snapshot.Source, "Index UID differs from request.");
        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() > limits.MaxArchiveEntries)
            MmcJson.Fail(snapshot.Source, "Index versions are missing or over budget.");
        MmcVersionRecommendation? result = null;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var version in versions.EnumerateArray()) {
            if (version.ValueKind != JsonValueKind.Object) MmcJson.Fail(snapshot.Source, "Index version must be an object.");
            string number = MmcJson.String(version, "version", snapshot.Source, true)!;
            if (!seen.Add(number)) MmcJson.Fail(snapshot.Source, "Duplicate index version.");
            if (!MmcJson.Boolean(version, "recommended", snapshot.Source)) continue;
            string hash = MmcJson.String(version, "sha256", snapshot.Source, true)!;
            if (!Hex(hash, 64)) MmcJson.Fail(snapshot.Source, "Invalid index component digest.");
            if (result is not null) throw new MmcInputException("MMC_METADATA_AMBIGUOUS", snapshot.Source, "Several versions have equal recommendation priority.");
            result = new(snapshot.Uid, number, snapshot.Source, MmcPatchReader.Hash(snapshot.ContentUtf8), hash);
        }
        return result;
    }

    private static void ValidateGraph(List<Node> order, Dictionary<string, Node> nodes, MmcReadLimits limits) {
        foreach (Node node in order) if (node.Resolved is { } resolved) {
            foreach (var requirement in resolved.Patch.Requires) if (nodes.TryGetValue(requirement.Uid, out Node? provider)) {
                if (requirement.EqualsVersion is not null && provider.Version != requirement.EqualsVersion)
                    throw new MmcInputException("MMC_EXACT_VERSION", node.Uid, "Effective provider version does not satisfy exact requirement.");
                if (node.Enabled && !provider.Enabled) throw new MmcInputException("MMC_DISABLED_DEPENDENCY", node.Uid, "Enabled consumer requires a disabled provider.");
            }
            if (node.Enabled) foreach (var conflict in resolved.Patch.Conflicts)
                if (nodes.TryGetValue(conflict.Uid, out Node? target) && target.Enabled && (conflict.EqualsVersion is null || conflict.EqualsVersion == target.Version))
                    throw new MmcInputException("MMC_COMPONENT_CONFLICT", node.Uid, "Enabled components conflict.");
        }
        Dictionary<string, int> colors = new(StringComparer.Ordinal);
        foreach (Node node in order) Visit(node, 0);
        void Visit(Node node, int depth) {
            if (depth > limits.MaxDepth) throw new MmcInputException("MMC_DEPENDENCY_DEPTH", node.Uid, "Dependency depth exceeded.");
            if (colors.TryGetValue(node.Uid, out int color)) {
                if (color == 1) throw new MmcInputException("MMC_DEPENDENCY_CYCLE", node.Uid, "Dependency cycle.");
                return;
            }
            colors.Add(node.Uid, 1);
            foreach (var requirement in node.Resolved?.Patch.Requires ?? []) if (nodes.TryGetValue(requirement.Uid, out var dependency)) Visit(dependency, depth + 1);
            colors[node.Uid] = 2;
        }
    }

    private static void CheckIdentity(MmcPatch patch, string uid, string? version) {
        if (patch.Uid != uid || version is not null && patch.Version != version)
            throw new MmcInputException("MMC_METADATA_IDENTITY", patch.Source, "Metadata UID/version differs from the requested identity.");
    }
    private static string? Nonempty(string? value) => value == "" ? null : value;
    private static bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static int CheckTextBudget(string text, string source, int limit) {
        if (text is null) MmcJson.Fail(source, "Missing metadata text.");
        int size;
        try { size = new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException) { throw new MmcInputException("MMC_METADATA_ENCODING", source, "Invalid decoded metadata encoding."); }
        if (size > limit) throw new MmcInputException("MMC_METADATA_BUDGET", source, "Metadata item budget exceeded.");
        return size;
    }
}
