// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>Pure binding of host-verified bytes. Publication and process/session admission remain host responsibilities.</summary>
public static partial class MmcPlanBinder {
    public static MmcBindingResult Bind(MmcInstanceDefinition definition, MmcCompiledProfile plan,
        IReadOnlyList<MmcMaterializedArtifactFact> facts, MmcBindOptions options,
        IReadOnlyList<MmcArtifactLockRecord>? priorLocks = null, IReadOnlyList<MmcDerivedOutputRecord>? derivedOutputs = null,
        IReadOnlyList<MmcAssetIndexInput>? assetIndexes = null, MmcReadLimits? limits = null,
        MmcInstanceDefinition? publishedBaseline = null, MmcPackageBaselineInput? packageBaseline = null) {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts); ArgumentNullException.ThrowIfNull(options);
        limits ??= new();
        List<MmcDiagnostic> diagnostics = [];
        try {
            // A published definition is prior authority, not a mutable draft.
            // Dropping its projection is insufficient to create fresh authority.
            if (definition.InstalledProjection is not null) ValidatePublished(definition);
            diagnostics.AddRange(MmcDefinitionCodec.Validate(definition, MmcDefinitionValidationMode.Draft, limits));
            if (diagnostics.Count != 0) return new(null, null, diagnostics.ToImmutableArray());
            MmcInstanceDefinition? authority = publishedBaseline;
            if (authority is not null && !(ReferenceEquals(authority, definition) && definition.InstalledProjection is not null)) ValidatePublished(authority);
            if (options.Intent != MmcBindingIntent.InitialImport) {
                authority ??= definition.InstalledProjection is not null ? definition : null;
                if (authority is null) Fail("MMC_BIND_AUTHORITY", "Noninitial binding requires a separately verified published baseline.");
                if (authority.InstanceId != definition.InstanceId || authority.Generation != options.ExpectedGeneration || definition.Generation != authority.Generation ||
                    definition.InstalledProjection is not null && definition.InstalledProjection.BoundFingerprint != authority.InstalledProjection!.BoundFingerprint)
                    Fail("MMC_BIND_AUTHORITY", "Candidate identity/generation or published projection differs from its prior baseline.");
                var oldInventoryKeys = authority.ArtifactInventory.Select(i => i.ArtifactKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var oldPackageKeys = authority.PackageFileInventory.Select(i => i.ArtifactKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!SameRecords(authority.ArtifactLocks, definition.ArtifactLocks, l => l.ArtifactKey) ||
                    !SameRecords(authority.ArtifactInventory, definition.ArtifactInventory.Where(i => oldInventoryKeys.Contains(i.ArtifactKey)).ToImmutableArray(), i => i.ArtifactKey) ||
                    !SameRecords(authority.DerivedOutputs, definition.DerivedOutputs, d => d.TargetRelativePath) ||
                    !SameRecords(authority.PackageFileInventory, definition.PackageFileInventory.Where(i => oldPackageKeys.Contains(i.ArtifactKey)).ToImmutableArray(), i => i.ArtifactKey) ||
                    !SameRecords(authority.PackageFileLocks, definition.PackageFileLocks, l => l.ArtifactKey))
                    Fail("MMC_BIND_AUTHORITY", "Candidate cannot rewrite, omit or manufacture prior adopted authority.");
                bool changedSources = SourceEnvelopeFingerprint(authority) != SourceEnvelopeFingerprint(definition);
                if (options.Intent == MmcBindingIntent.UpdateDefinition ? !changedSources : changedSources)
                    Fail("MMC_BIND_SOURCE_CHANGE", "Source changes require an explicit new-definition intent; repair/variant adoption preserve the source envelope.");
            } else if (authority is not null || !definition.DerivedOutputs.IsEmpty || !definition.PackageFileLocks.IsEmpty) Fail("MMC_BIND_AUTHORITY", "Initial import cannot inherit or manufacture published/derived/package-file prior authority.");
            if (!MmcProfileFingerprints.Matches(plan)) Fail("MMC_BIND_PLAN_FINGERPRINT", "Compiled plan contents do not match their canonical fingerprints.");
            if (definition.Generation != options.ExpectedGeneration || options.ManifestGeneration != options.NewGeneration || !Enum.IsDefined(options.Intent)) Fail("MMC_BIND_GENERATION", "Binding generation/intent is inconsistent.");
            bool increment = definition.Generation < long.MaxValue && options.NewGeneration == definition.Generation + 1;
            if (options.Intent == MmcBindingIntent.InitialImport ? definition.Generation != 1 || options.NewGeneration != 1 || definition.InstalledProjection is not null || !definition.ArtifactLocks.IsEmpty :
                options.Intent is MmcBindingIntent.AdoptDeclaredVariant or MmcBindingIntent.UpdateDefinition ? !increment : options.NewGeneration != definition.Generation && !increment)
                Fail("MMC_BIND_GENERATION", "Initial adoption, new-variant adoption and repair use distinct generation rules.");
            MmcDefinitionCodec.CheckHash(options.OutputJsonSha256);
            if (plan.FormatProfile != definition.FormatProfile || !MmcDefinitionCodec.SameHash(plan.SourceFingerprint, definition.SourceBundle.ResolutionFingerprint)) Fail("MMC_BIND_SOURCE", "Compiled sources do not match the preserved definition sources.");
            if (facts.Count > limits.MaxArchiveEntries || (derivedOutputs?.Count ?? 0) > limits.MaxArchiveEntries * 2L || (assetIndexes?.Count ?? 0) > 1) Fail("MMC_BIND_BUDGET", "Binding fact count exceeded.");
            var originalLocks = (authority?.ArtifactLocks ?? definition.ArtifactLocks).ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            if (priorLocks is not null) {
                if (priorLocks.Count != originalLocks.Count) Fail("MMC_BIND_PRIOR_LOCK", "Prior locks cannot omit adopted identities.");
                HashSet<string> supplied = new(StringComparer.OrdinalIgnoreCase);
                foreach (var item in priorLocks) if (item is null || !supplied.Add(item.ArtifactKey) || !originalLocks.TryGetValue(item.ArtifactKey, out var current) || item != current)
                    Fail("MMC_BIND_PRIOR_LOCK", "Prior locks differ from the definition's immutable adopted records.");
            }
            Dictionary<string, MmcMaterializedArtifactFact> observations = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> derivedTargets = new(StringComparer.OrdinalIgnoreCase);
            foreach (var output in derivedOutputs ?? []) if (output is null || !derivedTargets.Add(output.TargetRelativePath)) Fail("MMC_BIND_DERIVED_FACT", "Null or duplicate derived-output target fact.");
            foreach (var input in assetIndexes ?? []) if (input is null) Fail("MMC_BIND_ASSET_INDEX", "Null asset-index input snapshot.");
            foreach (var fact in facts) {
                if (fact is null || !observations.TryAdd(fact.ArtifactKey, fact)) Fail("MMC_BIND_FACT", "Null or duplicate materialization fact.");
                if (!MmcDerivedProofs.IsAssetObjectKey(fact.ArtifactKey)) MmcDefinitionCodec.CheckHash(fact.ArtifactKey);
                MmcDefinitionCodec.CheckHash(fact.SourceRevision); MmcDefinitionCodec.CheckHash(fact.ObservedSha256);
                if (fact.ObservedSha1 is not null) MmcDefinitionCodec.CheckHash(fact.ObservedSha1, 40);
                if (fact.SizeBytes < 0 || fact.SizeBytes > limits.MaxEntryBytes) Fail("MMC_BIND_BUDGET", "Observed artifact size exceeded.");
                MmcDefinitionCodec.RecoveryPath(fact.RecoveryObjectRelativePath, fact.ObservedSha256);
                MmcArtifactRules.Relative(fact.TargetRelativePath, "binding");
            }
            var active = ActiveArtifacts(plan);
            var inventory = definition.ArtifactInventory.ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            var locks = originalLocks.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var packageInventory = definition.PackageFileInventory.ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            var priorPackageLocks = (authority?.PackageFileLocks ?? definition.PackageFileLocks).ToDictionary(x => x.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            var packageLocks = priorPackageLocks.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MmcPackageFileInventoryRecord> packageMembers = new(StringComparer.OrdinalIgnoreCase);
            if (packageBaseline is not null) {
                if (definition.AuthorFileBaseline is null) Fail("MMC_BIND_PACKAGE_BASELINE", "New preserved package files require an external measured baseline reference.");
                var read = MmcPackageFileBaselineCodec.Read(packageBaseline, definition.AuthorFileBaseline, definition.Origin.PackageSha256, definition.SourceBundle.RelativeRoot,
                    new() { ArchiveLimits = limits });
                if (read.HasErrors || read.Baseline is null) Fail("MMC_BIND_PACKAGE_BASELINE", string.Join("; ", read.Diagnostics.Select(d => d.Message)));
                if (read.Baseline.GameRootLeaf != definition.Layout.GameRootLeaf) Fail("MMC_BIND_PACKAGE_BASELINE", "Measured package game-root layout differs from its definition.");
                packageMembers = MmcPackageFilePreservation.LibraryMembers(read.Baseline).ToDictionary(m => m.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            }
            if (active.Keys.Any(key => packageInventory.ContainsKey(key) || packageMembers.ContainsKey(key))) Fail("MMC_BIND_PLAN", "Package-file preservation keys cannot be promoted into component execution authority.");
            foreach (var artifact in active.Values) {
                string sourceObject = SourceObject(definition, artifact.ComponentUid, artifact.SourceRevision, artifact.Provenance.ComponentVersion);
                if (!observations.TryGetValue(artifact.Key, out var fact)) Fail("MMC_BIND_FACT_MISSING", "Active launch input has no verified materialization fact.");
                if (!MmcDefinitionCodec.SameHash(fact.SourceRevision, artifact.SourceRevision) || artifact.Sha256 is not null && !MmcDefinitionCodec.SameHash(fact.ObservedSha256, artifact.Sha256) ||
                    artifact.Sha1 is not null && (fact.ObservedSha1 is null || !MmcDefinitionCodec.SameHash(fact.ObservedSha1, artifact.Sha1)) || artifact.Size is not null && artifact.Size != fact.SizeBytes)
                    Fail("MMC_BIND_DIGEST", "Observed input does not match its source revision, declared checksums or size.");
                string variant = VariantTag(artifact, plan.Environment);
                var item = new MmcArtifactInventoryRecord(artifact.Key, artifact.ComponentUid, artifact.Role, artifact.Scope, variant, artifact.SourceRevision,
                    artifact.RelativePath, MmcArtifactAdoptionState.Adopted, artifact.Sha1, artifact.Sha256, SourceMetadataObjectRelativePath: sourceObject);
                if (inventory.TryGetValue(artifact.Key, out var previous)) {
                    if (previous.ComponentUid != item.ComponentUid || previous.Role != item.Role || previous.Scope != item.Scope || previous.VariantTag != item.VariantTag || previous.RelativePath != item.RelativePath ||
                        !MmcDefinitionCodec.SameHash(previous.SourceRevision, item.SourceRevision) || !SameOptionalHash(previous.DeclaredSha1, item.DeclaredSha1) || !SameOptionalHash(previous.DeclaredSha256, item.DeclaredSha256))
                        Fail("MMC_BIND_IDENTITY", "An existing artifact key cannot change its declaration identity.");
                    item = item with { SourceObject = previous.SourceObject };
                }
                BindStatic(item, fact);
            }
            foreach (var provenance in plan.FieldProvenance.Where(p => !p.Source.StartsWith("profile:", StringComparison.Ordinal))) SourceObject(definition, provenance.ComponentUid, provenance.SourceSha256, provenance.ComponentVersion);
            Dictionary<string, string> executingTargets = new(StringComparer.OrdinalIgnoreCase);
            foreach (string key in active.Keys) {
                var fact = observations[key];
                if (executingTargets.TryGetValue(fact.TargetRelativePath, out var previous) && !MmcDefinitionCodec.SameHash(previous, fact.ObservedSha256))
                    Fail("MMC_BIND_IDENTITY", "Active inputs conflict at one owned target.");
                executingTargets[fact.TargetRelativePath] = fact.ObservedSha256;
            }
            // Explicitly preserved inactive inputs are allowed only through an
            // existing declaration; arbitrary extra facts cannot become libraries.
            foreach (var fact in observations.Values.Where(f => !active.ContainsKey(f.ArtifactKey) && !MmcDerivedProofs.IsAssetObjectKey(f.ArtifactKey))) {
                if (packageMembers.TryGetValue(fact.ArtifactKey, out var member) || packageInventory.TryGetValue(fact.ArtifactKey, out member)) {
                    if (!priorPackageLocks.ContainsKey(fact.ArtifactKey) && !packageMembers.ContainsKey(fact.ArtifactKey)) Fail("MMC_BIND_PACKAGE_BASELINE", "A new package-original key requires raw measured baseline membership.");
                    if (packageInventory.TryGetValue(fact.ArtifactKey, out var oldMember) && oldMember != member) Fail("MMC_BIND_PACKAGE_SOURCE", "Existing package-original identity differs from measured membership.");
                    if (!MmcDefinitionCodec.SameHash(member.SourceRevision, fact.SourceRevision) || !MmcDefinitionCodec.SameHash(member.Sha256, fact.ObservedSha256) ||
                        fact.ObservedSha1 is null || !MmcDefinitionCodec.SameHash(member.Sha1, fact.ObservedSha1) || member.SizeBytes != fact.SizeBytes || fact.TargetRelativePath != "libraries/" + member.LibraryRelativePath)
                        Fail("MMC_BIND_PACKAGE_DIGEST", "Package-original observation differs from its measured local-library member.");
                    if (priorPackageLocks.TryGetValue(fact.ArtifactKey, out var oldLock)) {
                        if (!MmcDefinitionCodec.SameHash(oldLock.AdoptedSha256, fact.ObservedSha256) || !MmcDefinitionCodec.SameHash(oldLock.SourceRevision, fact.SourceRevision) || oldLock.SizeBytes != fact.SizeBytes || !MmcDefinitionCodec.SameHash(oldLock.ObservedSha1, fact.ObservedSha1))
                            Fail("MMC_BIND_REBIND", "A preserved package-file key cannot adopt different original bytes.");
                    } else if (options.Intent == MmcBindingIntent.RepairExisting) Fail("MMC_BIND_ADOPTION_REQUIRED", "New package originals require explicit adoption.");
                    if (executingTargets.TryGetValue(fact.TargetRelativePath, out var executingHash) && !MmcDefinitionCodec.SameHash(executingHash, fact.ObservedSha256))
                        Fail("MMC_BIND_PACKAGE_COLLISION", "Preservation and execution observations conflict at one live local-library target.");
                    packageInventory[member.ArtifactKey] = member;
                    packageLocks[member.ArtifactKey] = new(member.ArtifactKey, member.SourceRevision, fact.ObservedSha256, fact.ObservedSha1, fact.SizeBytes, fact.RecoveryObjectRelativePath, fact.TargetRelativePath);
                    continue;
                }
                if (!inventory.TryGetValue(fact.ArtifactKey, out var item)) Fail("MMC_BIND_UNDECLARED", "Extra input has no preserved artifact declaration.");
                SourceObject(definition, item.ComponentUid, item.SourceRevision, null);
                BindStatic(item with { State = MmcArtifactAdoptionState.Adopted }, fact);
            }
            var activeLocks = active.Keys.Select(k => locks[k]).ToImmutableArray();
            string[] views = activeLocks.Select(l => ViewKey(l.TargetRelativePath)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (views.Length > 1) Fail("MMC_BIND_VIEW", "Active owned inputs must bind one coherent runtime view.");
            string? viewKey = views.SingleOrDefault();
            List<MmcDerivedOutputRecord> newDerived = [];
            ValidateNative(plan, options.NativeDerivation, activeLocks, derivedOutputs ?? [], newDerived, ref viewKey);
            ValidateAssets(definition, plan, observations, originalLocks, assetIndexes ?? [], derivedOutputs ?? [], newDerived, options.Intent, ref viewKey, limits);
            var acceptedDerivedTargets = newDerived.Select(d => d.TargetRelativePath).ToHashSet(StringComparer.Ordinal);
            if ((derivedOutputs ?? []).Any(d => !acceptedDerivedTargets.Contains(d.TargetRelativePath))) Fail("MMC_BIND_DERIVED_UNDECLARED", "Derived output is not justified by the current native inputs or verified asset index.");
            Dictionary<string, MmcDerivedOutputRecord> mergedDerived = definition.DerivedOutputs.ToDictionary(d => d.TargetRelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var derived in newDerived) {
                if (mergedDerived.TryGetValue(derived.TargetRelativePath, out var old) && (!MmcDefinitionCodec.SameHash(old.Sha256, derived.Sha256) || old.SizeBytes != derived.SizeBytes)) Fail("MMC_BIND_REBIND", "An owned output path cannot silently adopt different bytes.");
                mergedDerived[derived.TargetRelativePath] = derived;
            }
            var candidate = definition with {
                Generation = options.NewGeneration,
                ArtifactInventory = inventory.Values.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).ToImmutableArray(),
                ArtifactLocks = locks.Values.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).ToImmutableArray(),
                PackageFileInventory = packageInventory.Values.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).ToImmutableArray(),
                PackageFileLocks = packageLocks.Values.OrderBy(x => x.ArtifactKey, StringComparer.Ordinal).ToImmutableArray(),
                DerivedOutputs = mergedDerived.Values.OrderBy(x => x.TargetRelativePath, StringComparer.Ordinal).ToImmutableArray(),
                InstalledProjection = new(plan.ProfileFingerprint, options.OutputJsonSha256, options.NewGeneration, plan.RuntimePlanFingerprint, new string('0', 64)) {
                    // A same-generation historical rebind is read-only. Recording missing
                    // environment authority requires an explicit new-generation publication.
                    RuntimeEnvironment = authority is not null && options.NewGeneration == authority.Generation && authority.InstalledProjection!.RuntimeEnvironment is null
                        ? null : plan.Environment
                }
            };
            string fingerprint = MmcDefinitionCodec.ComputeBoundFingerprint(candidate);
            if (authority is not null && options.NewGeneration == authority.Generation && !MmcDefinitionCodec.SameHash(fingerprint, authority.InstalledProjection!.BoundFingerprint))
                Fail("MMC_BIND_GENERATION", "Repair may retain a generation only when the published structural binding remains unchanged.");
            candidate = candidate with { InstalledProjection = candidate.InstalledProjection! with { BoundFingerprint = fingerprint } };
            diagnostics.AddRange(MmcDefinitionCodec.Validate(candidate, MmcDefinitionValidationMode.Published, limits));
            return diagnostics.Count != 0 ? new(null, null, diagnostics.ToImmutableArray()) : new(candidate, new(plan, options.NewGeneration, fingerprint, activeLocks), []);

            void ValidatePublished(MmcInstanceDefinition value) {
                var errors = MmcDefinitionCodec.Validate(value, MmcDefinitionValidationMode.Published, limits);
                if (!errors.IsEmpty) Fail("MMC_BIND_AUTHORITY", "Published baseline failed validation: " + errors[0].Message);
            }

            void BindStatic(MmcArtifactInventoryRecord item, MmcMaterializedArtifactFact fact) {
                if (!MmcDefinitionCodec.SameHash(item.SourceRevision, fact.SourceRevision) || item.DeclaredSha256 is not null && !MmcDefinitionCodec.SameHash(item.DeclaredSha256, fact.ObservedSha256) ||
                    item.DeclaredSha1 is not null && (fact.ObservedSha1 is null || !MmcDefinitionCodec.SameHash(item.DeclaredSha1, fact.ObservedSha1)) ||
                    item.SourceObject is not null && !MmcDefinitionCodec.SameHash(item.SourceObject.Sha256, fact.ObservedSha256)) Fail("MMC_BIND_DIGEST", "Preserved input differs from its declared source contents.");
                if (originalLocks.TryGetValue(item.ArtifactKey, out var old)) {
                    if (!MmcDefinitionCodec.SameHash(old.AdoptedSha256, fact.ObservedSha256) || !MmcDefinitionCodec.SameHash(old.SourceRevision, fact.SourceRevision) || old.SizeBytes != fact.SizeBytes)
                        Fail("MMC_BIND_REBIND", "Existing adopted artifact keys cannot change their source or byte digest.");
                } else if (options.Intent == MmcBindingIntent.RepairExisting) Fail("MMC_BIND_ADOPTION_REQUIRED", "New artifact variants require explicit adoption and a new generation.");
                MmcDefinitionCodec.TargetPath(item.Role, item.Scope, item.RelativePath, fact.TargetRelativePath);
                inventory[item.ArtifactKey] = item;
                locks[item.ArtifactKey] = new(item.ArtifactKey, fact.SourceRevision, fact.ObservedSha256, fact.SizeBytes, fact.RecoveryObjectRelativePath, fact.TargetRelativePath, fact.ObservedSha1);
            }
        } catch (MmcInputException error) { diagnostics.Add(new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)); return new(null, null, diagnostics.ToImmutableArray()); }
    }

    public static string VariantTag(MmcArtifactReference artifact, MmcRuntimeEnvironment environment) =>
        environment.OperatingSystem + "-" + environment.Architecture + (artifact.NativeClassifier is null ? "" : ":" + artifact.NativeClassifier);
    private static Dictionary<string, MmcArtifactReference> ActiveArtifacts(MmcCompiledProfile plan) {
        if (plan.Classpath.IsDefaultOrEmpty || plan.Classpath[^1].Key != plan.MainJar.Key || plan.Classpath[^1].Role != MmcArtifactRole.MainJar) Fail("MMC_BIND_PLAN", "The final classpath must end with the selected main JAR.");
        Dictionary<string, MmcArtifactReference> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts).Concat(plan.AuxiliaryArtifacts).Append(plan.MainJar)) {
            if (artifact.PackageFileOrigin is not null) Fail("MMC_BIND_PLAN", "Opaque package originals cannot enter an active compiled execution/download plan.");
            MmcDefinitionCodec.CheckHash(artifact.Key); MmcDefinitionCodec.CheckHash(artifact.SourceRevision);
            if (result.TryGetValue(artifact.Key, out var old)) {
                if (old.Role != artifact.Role || old.Scope != artifact.Scope || old.ComponentUid != artifact.ComponentUid || old.SourceRevision != artifact.SourceRevision || old.RelativePath != artifact.RelativePath || old.Coordinate != artifact.Coordinate ||
                    old.Sha1 != artifact.Sha1 || old.Sha256 != artifact.Sha256 || old.Size != artifact.Size || old.NativeClassifier != artifact.NativeClassifier || !old.ExtractExcludes.SequenceEqual(artifact.ExtractExcludes) || !old.DownloadUrls.SequenceEqual(artifact.DownloadUrls))
                    Fail("MMC_BIND_IDENTITY", "Repeated active artifact key has inconsistent descriptors.");
            } else result.Add(artifact.Key, artifact);
        }
        if (plan.Assets.Artifact is { } assets && !result.ContainsKey(assets.Key)) Fail("MMC_BIND_PLAN", "Asset-index input is absent from the compiled auxiliary artifacts.");
        return result;
    }
    private static string SourceObject(MmcInstanceDefinition definition, string uid, string sourceRevision, string? version) {
        var source = definition.SourceBundle.MetadataRecords.FirstOrDefault(r => r.Kind == MmcSourceRecordKind.Component && r.Uid == uid && MmcDefinitionCodec.SameHash(r.ContentSha256, sourceRevision) &&
            (version is null || r.Version == version || r.ComponentSourceKind == MmcComponentSourceKind.PackageLocal && string.IsNullOrEmpty(r.Version)));
        if (source is not null) return source.ObjectRelativePath;
        var user = definition.ComponentOverrides.FirstOrDefault(o => o.Uid == uid && o.PatchReference is not null && MmcDefinitionCodec.SameHash(o.PatchReference.Sha256, sourceRevision));
        if (user?.PatchReference is { } patch) return patch.ObjectRelativePath;
        throw new MmcInputException("MMC_BIND_SOURCE", uid, "Plan contribution has no preserved original/user metadata object.");
    }
    private static void ValidateNative(MmcCompiledProfile plan, MmcDerivationCompletion? completion, ImmutableArray<MmcArtifactLockRecord> activeLocks,
        IReadOnlyList<MmcDerivedOutputRecord> supplied, List<MmcDerivedOutputRecord> accepted, ref string? view) {
        var native = supplied.Where(d => d.Kind == MmcDerivedOutputKind.NativeFile).ToArray();
        if (plan.NativeArtifacts.IsEmpty) { if (native.Length != 0 || completion is not null && completion.OutputCount != 0) Fail("MMC_BIND_DERIVED_UNDECLARED", "Native output has no active native input."); return; }
        if (completion is null || !completion.Complete || completion.ProducerVersion != "bounded-native-v1" || completion.OutputCount != native.Length || completion.RuntimePlanFingerprint != plan.RuntimePlanFingerprint)
            Fail("MMC_BIND_NATIVE_INCOMPLETE", "A complete parent-bound native extraction observation is required.");
        var digestFacts = activeLocks.Select(l => new MmcArtifactDigestFact(l.ArtifactKey, l.SourceRevision, l.AdoptedSha256, l.SizeBytes)).ToArray();
        string sources = MmcDerivedProofs.ComputeNativeSourceSetFingerprint(plan.NativeArtifacts, digestFacts, completion.ProducerVersion);
        if (sources != completion.SourceSetFingerprint) Fail("MMC_BIND_DERIVED_CONTEXT", "Native source set differs from its locked ordered inputs.");
        MmcDefinitionCodec.CheckHash(completion.ViewKey);
        if (view is not null && view != completion.ViewKey) Fail("MMC_BIND_VIEW", "Native outputs and classpath bind different owned views.");
        view = completion.ViewKey;
        string context = MmcDerivedProofs.ComputeDerivedContextFingerprint(plan.RuntimePlanFingerprint, sources, completion.ProducerVersion);
        var parents = plan.NativeArtifacts.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var output in native) {
            if (!parents.Contains(output.ParentArtifactKey) || output.SourceSetFingerprint != sources || output.ContextFingerprint != context || output.ProducerVersion != completion.ProducerVersion || output.RuntimePlanFingerprint != plan.RuntimePlanFingerprint || output.ViewKey != view)
                Fail("MMC_BIND_DERIVED_CONTEXT", "Native output does not match the verified extraction batch.");
            var parent = plan.NativeArtifacts.Single(a => a.Key == output.ParentArtifactKey);
            if (parent.ExtractExcludes.Any(exclude => output.RelativePath.StartsWith(exclude, StringComparison.Ordinal))) Fail("MMC_BIND_DERIVED_CONTEXT", "Native output contradicts its source extraction exclusions.");
            accepted.Add(output);
        }
    }
    private static void ValidateAssets(MmcInstanceDefinition definition, MmcCompiledProfile plan, Dictionary<string, MmcMaterializedArtifactFact> observations,
        Dictionary<string, MmcArtifactLockRecord> staticPriorLocks, IReadOnlyList<MmcAssetIndexInput> inputs,
        IReadOnlyList<MmcDerivedOutputRecord> supplied, List<MmcDerivedOutputRecord> accepted, MmcBindingIntent intent, ref string? view, MmcReadLimits limits) {
        var dynamicFacts = observations.Values.Where(f => MmcDerivedProofs.IsAssetObjectKey(f.ArtifactKey)).ToDictionary(f => f.ArtifactKey, StringComparer.Ordinal);
        if (plan.Assets.Artifact is not { } index) {
            if (inputs.Count != 0 || dynamicFacts.Count != 0 || supplied.Any(d => d.Kind != MmcDerivedOutputKind.NativeFile)) Fail("MMC_BIND_DERIVED_UNDECLARED", "Asset data has no active asset-index parent.");
            return;
        }
        if (inputs.Count != 1 || inputs[0].ArtifactKey != index.Key) Fail("MMC_BIND_ASSET_INDEX", "Active asset index requires its preserved raw byte snapshot.");
        var parent = observations[index.Key];
        var read = MmcAssetIndexReader.Read(inputs[0], parent.ObservedSha256, limits);
        if (read.HasErrors || read.Index is null) throw new MmcInputException("MMC_BIND_ASSET_INDEX", index.Key, string.Join("; ", read.Diagnostics.Select(d => d.Message)));
        string? parentView = ViewKey(parent.TargetRelativePath);
        if (parentView is null || view is not null && view != parentView) Fail("MMC_BIND_VIEW", "Asset index must bind the coherent owned view.");
        view = parentView;
        string sources = MmcDerivedProofs.ComputeAssetSourceSetFingerprint(index.Key, parent.ObservedSha256, parent.SizeBytes);
        string context = MmcDerivedProofs.ComputeDerivedContextFingerprint(plan.RuntimePlanFingerprint, sources, "bounded-assets-v1");
        var oldObjects = definition.DerivedOutputs.Where(d => d.Kind == MmcDerivedOutputKind.AssetObject).GroupBy(d => d.ArtifactKey!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Dictionary<(MmcDerivedOutputKind Kind, string TargetRelativePath), (MmcDerivedOutputRecord First, int Count)>? aliasMap = null;
        if (read.Index.Virtual || read.Index.EffectiveMapToResources) {
            aliasMap = new(supplied.Count);
            foreach (var d in supplied) {
                if (d.Kind is MmcDerivedOutputKind.AssetVirtualFile or MmcDerivedOutputKind.AssetMappedResource) {
                    var k = (d.Kind, d.TargetRelativePath);
                    aliasMap[k] = aliasMap.TryGetValue(k, out var existing) ? (existing.First, existing.Count + 1) : (d, 1);
                }
            }
        }
        Dictionary<string, MmcDerivedOutputRecord> adopted = new(StringComparer.Ordinal);
        foreach (var asset in read.Index.Objects) {
            string key = MmcDerivedProofs.ComputeAssetObjectKey(index.Key, parent.ObservedSha256, asset.Sha1, asset.SizeBytes);
            if (!dynamicFacts.TryGetValue(key, out var fact) || !MmcDefinitionCodec.SameHash(fact.SourceRevision, parent.ObservedSha256) || fact.ObservedSha1 is null || !MmcDefinitionCodec.SameHash(fact.ObservedSha1, asset.Sha1) || fact.SizeBytes != asset.SizeBytes)
                Fail("MMC_BIND_ASSET_OBJECT", "An indexed asset object lacks its exact observed contents.");
            string relative = "objects/" + asset.Sha1[..2] + "/" + asset.Sha1;
            string target = "PCL/MmcRuntime/" + view + "/assets/" + relative;
            if (fact.TargetRelativePath != target) Fail("MMC_BIND_VIEW", "Asset-object fact escaped its index-owned assets view.");
            if (oldObjects.TryGetValue(key, out var old)) {
                if (!MmcDefinitionCodec.SameHash(old.Sha256, fact.ObservedSha256) || old.SizeBytes != fact.SizeBytes) Fail("MMC_BIND_REBIND", "Existing asset-object key cannot adopt different bytes.");
            } else if (intent == MmcBindingIntent.RepairExisting) Fail("MMC_BIND_ADOPTION_REQUIRED", "New indexed asset objects require explicit adoption.");
            var item = new MmcDerivedOutputRecord(MmcDerivedOutputKind.AssetObject, index.Key, parent.ObservedSha256, "bounded-assets-v1", sources, plan.RuntimePlanFingerprint, context, view,
                relative, target, fact.ObservedSha256, fact.SizeBytes, key, fact.ObservedSha1, fact.RecoveryObjectRelativePath);
            if (adopted.TryAdd(key, item)) accepted.Add(item);
            if (read.Index.Virtual) CheckAlias(MmcDerivedOutputKind.AssetVirtualFile);
            if (read.Index.EffectiveMapToResources) CheckAlias(MmcDerivedOutputKind.AssetMappedResource);

            void CheckAlias(MmcDerivedOutputKind kind) {
                bool virtualFile = kind == MmcDerivedOutputKind.AssetVirtualFile;
                string outputRelative = virtualFile ? "virtual/" + plan.Assets.Id + "/" + asset.Name : asset.Name;
                string outputTarget = virtualFile ? "PCL/MmcRuntime/" + parentView + "/assets/" + outputRelative : definition.Layout.GameRootLeaf + "/resources/" + asset.Name;
                if (!aliasMap!.TryGetValue((kind, outputTarget), out var match) || match.Count != 1)
                    Fail("MMC_BIND_ASSET_DERIVED", "Indexed virtual/resource file lacks a matching verified output fact.");
                var found = match.First;
                if (found.ParentArtifactKey != key || found.ParentSha256 != fact.ObservedSha256 || found.Sha256 != fact.ObservedSha256 || found.ObservedSha1 != fact.ObservedSha1 ||
                    found.SizeBytes != fact.SizeBytes || found.RelativePath != outputRelative || found.ProducerVersion != "bounded-assets-v1" || found.SourceSetFingerprint != sources || found.ContextFingerprint != context || found.RuntimePlanFingerprint != plan.RuntimePlanFingerprint || found.ViewKey != parentView)
                    Fail("MMC_BIND_ASSET_DERIVED", "Indexed virtual/resource file lacks a matching verified output fact.");
                accepted.Add(found);
            }
        }
        if (dynamicFacts.Keys.Any(key => !adopted.ContainsKey(key))) Fail("MMC_BIND_DERIVED_UNDECLARED", "Extra dynamic asset fact is not listed by the verified index.");
        foreach (var output in supplied.Where(d => d.Kind == MmcDerivedOutputKind.AssetObject)) {
            if (output.ArtifactKey is null || !adopted.TryGetValue(output.ArtifactKey, out var expected) || output != expected) Fail("MMC_BIND_ASSET_OBJECT", "Supplied asset-object output differs from its index-derived observation.");
        }
    }
    private static string? ViewKey(string relative) {
        string[] parts = relative.Split('/');
        return parts.Length >= 4 && parts[0] == "PCL" && parts[1] == "MmcRuntime" ? parts[2] : null;
    }
    private static bool SameOptionalHash(string? left, string? right) => left is null ? right is null : right is not null && MmcDefinitionCodec.SameHash(left, right);
    private static bool SameRecords<T>(ImmutableArray<T> left, ImmutableArray<T> right, Func<T, string> key) where T : class {
        if (left.IsDefault || right.IsDefault || left.Length != right.Length || left.Any(x => x is null) || right.Any(x => x is null)) return false;
        var expected = left.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        return right.All(value => seen.Add(key(value)) && expected.TryGetValue(key(value), out var old) && EqualityComparer<T>.Default.Equals(value, old));
    }
    private static string SourceEnvelopeFingerprint(MmcInstanceDefinition definition) => MmcFingerprint.Of(new[] {
        definition.Origin.PackageSha256, definition.Origin.OriginalName, definition.Origin.PlatformProjectIdentity?.Platform, definition.Origin.PlatformProjectIdentity?.ProjectId,
        definition.Origin.PlatformProjectIdentity?.VersionId, definition.SourceBundle.RelativeRoot, definition.SourceBundle.BundleSha256, definition.SourceBundle.ResolutionFingerprint,
        definition.Layout.GameRootLeaf, definition.Layout.LayoutVersion.ToString(), definition.ParentInstanceId?.ToString("D"), definition.AuthorFileBaseline?.ContentSha256,
        definition.AuthorFileBaseline?.ObjectRelativePath, definition.AuthorFileBaseline?.EntryCount.ToString() }
        .Concat(definition.SourceBundle.MetadataRecords.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ThenBy(r => r.Kind).SelectMany(r => new[] { r.RelativePath, r.ContentSha256, r.ObjectRelativePath, r.Kind.ToString(), r.Uid, r.Version, r.ComponentSourceKind?.ToString(), r.SourceIdentity }))
        .Concat(definition.ComponentOverrides.OrderBy(o => o.Uid, StringComparer.Ordinal).SelectMany(o => new[] { o.Uid, o.Enabled?.ToString(), o.Version, o.PatchReference?.Sha256, o.PatchReference?.ObjectRelativePath }))
        .Concat(definition.SourceBundle.PackageLocalScopeSha256 is { } scope ? new[] { "mmc-measured-package-local-v1", scope } : [])
        .Concat(definition.PackageOwnershipReference is { } ownership ? new[] { "mmc-package-ownership-v1", ownership.Sha256, ownership.ObjectRelativePath } : [])
        .Concat(definition.Origin.PlatformEvidenceReference is { } evidence ? new[] { "mmc-platform-origin-evidence-v1", evidence.Sha256, evidence.ObjectRelativePath } : []));
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new MmcInputException(code, "binding", message);
}
