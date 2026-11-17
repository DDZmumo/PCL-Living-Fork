// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

public static class MmcLaunchAuthorityEvaluator {
    public static MmcLaunchAuthorityResult Evaluate(MmcInstanceDefinition definition, MmcBoundLaunchPlan boundPlan,
        MmcResolvedProfile sources, MmcHostLaunchChain host, IReadOnlyList<MmcJarClassObservation> classObservations,
        MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(boundPlan);
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(classObservations);
        limits ??= new();
        List<MmcDiagnostic> diagnostics = [];
        try {
            diagnostics.AddRange(MmcDefinitionCodec.Validate(definition, MmcDefinitionValidationMode.Published, limits));
            if (diagnostics.Count != 0) return new(null, diagnostics.ToImmutableArray());
            var plan = boundPlan.CompiledProfile;
            if (!MmcProfileFingerprints.Matches(plan)) Reject("Compiled plan contents differ from their canonical fingerprints.");
            if (boundPlan.DefinitionGeneration != definition.Generation || boundPlan.BoundFingerprint != definition.InstalledProjection!.BoundFingerprint ||
                plan.ProfileFingerprint != definition.InstalledProjection.ProfileFingerprint || plan.RuntimePlanFingerprint != definition.InstalledProjection.RuntimePlanFingerprint ||
                sources.SourceFingerprint != plan.SourceFingerprint) Reject("Bound launch identity/generation/sources are inconsistent.");
            MmcDefinitionCodec.CheckHash(host.JavaIdentitySha256); MmcDefinitionCodec.CheckHash(host.WrapperIdentitySha256); MmcDefinitionCodec.CheckHash(host.NonSensitiveArgumentFingerprint);
            if (plan.Environment.JavaMajor < 1 || plan.JavaPolicy.Kind != MmcJavaPolicyKind.DeclaredCandidates || plan.JavaPolicy.AllowedMajors.IsEmpty || !plan.JavaPolicy.Allows(plan.Environment.JavaMajor))
                Reject("Explicit compatible Java candidates and a compatible selected Java identity are required.");
            if (host.AdditionalJvmArguments.IsDefault || host.AdditionalRequiredClasses.IsDefault || host.AdditionalRequiredClasses.Length>MmcLaunchClassRequirements.MaximumClasses || classObservations.Count > limits.MaxArchiveEntries) Reject("Launch observation budget/shape is invalid.");
            // Match the runtime's actual user -> component order, including last-wins
            // system-loader properties. A superseded user loader is not executed.
            string[] jvm = host.AdditionalJvmArguments.Concat(plan.JvmArguments.Select(a => a.Value)).ToArray();
            if (jvm.Any(a => a is null || a.Contains('\0') || IsUnboundExecutionInput(a))) Reject("Agent, bootclasspath, module or classpath replacement inputs need a separate bound executor proof.");
            var required=MmcLaunchClassRequirements.Collect(plan.MainClass,jvm,plan.GameArguments.Select(value=>value.Value),plan.Tweakers,host.AdditionalRequiredClasses);
            var locks = definition.ArtifactLocks.ToDictionary(l => l.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            var activeLocks = boundPlan.ActiveArtifactLocks.ToDictionary(l => l.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts).Concat(plan.AuxiliaryArtifacts).DistinctBy(a => a.Key)) {
                if (artifact.PackageFileOrigin is not null) Reject("Package-private preserved originals are not component execution authority.");
                if (!activeLocks.TryGetValue(artifact.Key, out var active) || !locks.TryGetValue(artifact.Key, out var expected) || active != expected || !MmcDefinitionCodec.SameHash(expected.SourceRevision, artifact.SourceRevision)) Reject("All execution inputs must remain under the bound definition's exact adopted locks.");
            }
            Dictionary<string, MmcJarClassObservation> observations = new(StringComparer.OrdinalIgnoreCase);
            long cells = 0;
            foreach (var observation in classObservations) {
                if (observation is null || observation.Classes.IsDefault || !observations.TryAdd(observation.ArtifactKey, observation)) Reject("Duplicate/null JAR observation.");
                MmcDefinitionCodec.CheckHash(observation.ArtifactKey); MmcDefinitionCodec.CheckHash(observation.ArtifactSha256);
                if (observation.Classes.Any(p => p is null)) Reject("Null class probe.");
                cells += observation.Classes.Length;
                if (cells > limits.MaxDependencyEdges) Reject("Classpath class-probe cell budget exceeded.");
                if (!activeLocks.TryGetValue(observation.ArtifactKey, out var expected) || !MmcDefinitionCodec.SameHash(observation.ArtifactSha256, expected.AdoptedSha256)) Reject("JAR class observation differs from its adopted byte digest.");
            }
            List<MmcClasspathClassBinding> bindings = [];
            foreach (string requiredClass in required) {
                MmcClasspathClassBinding? first = null;
                string entry = requiredClass.Replace('.', '/') + ".class";
                for (int position = 0; position < plan.Classpath.Length; position++) {
                    var artifact = plan.Classpath[position];
                    if (!observations.TryGetValue(artifact.Key, out var observation)) Reject("Every ordered classpath JAR requires explicit class presence/absence facts.");
                    var probes = observation.Classes.Where(p => p.ClassName == requiredClass).ToArray();
                    if (probes.Length != 1 || probes[0].Present && probes[0].EntryPath != entry || !probes[0].Present && probes[0].EntryPath is not null) Reject("Class probes are missing, ambiguous or outside the supported direct JAR class layout.");
                    if (probes[0].Present && first is null) first = new(requiredClass, artifact.Key, observation.ArtifactSha256, entry, position);
                }
                if (first is null) Reject("A required entrypoint, system-loader or tweaker class is absent from the locked classpath.");
                bindings.Add(first);
            }
            var finalEntry = plan.FieldProvenance.LastOrDefault(p => p.Field == "mainClass");
            var entrySource = sources.Components.SingleOrDefault(c => c.Enabled && c.Uid == finalEntry?.ComponentUid && c.Patch.ContentSha256 == finalEntry.SourceSha256);
            if (entrySource is null || entrySource.SourceKind == MmcComponentSourceKind.RemoteMetadata || !entrySource.Patch.Fields.TryGetValue("mainClass", out var mainClass) || mainClass.GetString() != plan.MainClass)
                Reject("Final entrypoint must be explained by an effective preserved local/user component.");
            List<MmcLaunchAuthorityTarget> targets = [];
            var minecraft = sources.Components.SingleOrDefault(c => c.Enabled && c.Uid == "net.minecraft");
            if (minecraft is null || !CompleteLocal(minecraft, true)) Reject("Minecraft has no provable complete local main-JAR/library launch definition.");
            targets.Add(new(minecraft.Uid, minecraft.Patch.ContentSha256, MmcRetiredHeuristicKind.BuiltinMinecraftJavaInference));
            var forge = sources.Components.SingleOrDefault(c => c.Enabled && c.Uid == "net.minecraftforge");
            if (forge is not null && CompleteLocal(forge, false)) targets.Add(new(forge.Uid, forge.Patch.ContentSha256, MmcRetiredHeuristicKind.BuiltinForgeJavaInference));
            string proof = MmcFingerprint.Of(new[] { "mmc-structural-launch-authority-v2", definition.InstanceId.ToString("D"), definition.Generation.ToString(), boundPlan.BoundFingerprint,
                plan.ProfileFingerprint, plan.RuntimePlanFingerprint, host.JavaIdentitySha256, host.WrapperIdentitySha256, host.NonSensitiveArgumentFingerprint }
                .Concat(targets.SelectMany(t => new[] { t.ComponentUid, t.SourceRevision, t.Heuristic.ToString() }))
                .Concat(bindings.SelectMany(c => new[] { c.ClassName, c.ArtifactKey, c.ArtifactSha256, c.EntryPath, c.ClasspathPosition.ToString() }))
                .Concat(required).Concat(host.AdditionalRequiredClasses)
                .Concat(plan.Classpath.SelectMany(artifact=>required.SelectMany(name=>{
                    var observation=observations[artifact.Key];var probe=observation.Classes.Single(value=>value.ClassName==name);
                    return new[]{artifact.Key,observation.ArtifactSha256,name,probe.Present?"present":"absent",probe.EntryPath};
                })))
                .Concat(plan.JvmArguments.Select(a => a.Value)).Concat(host.AdditionalJvmArguments).Concat(plan.GameArguments.Select(a => a.Value)).Concat(plan.Tweakers)
                .Concat(boundPlan.ActiveArtifactLocks.SelectMany(l => new[] { l.ArtifactKey, l.SourceRevision, l.AdoptedSha256, l.SizeBytes.ToString(), l.TargetRelativePath })));
            diagnostics.Add(new("MMC_AUTHORITY_STRUCTURAL_ONLY", MmcDiagnosticSeverity.Information, "authority", "Only named old MC/Forge inferences may be retired; platform/account/other hard constraints remain, and live Java/game acceptance is still required."));
            return new(new(definition.InstanceId, definition.Generation, boundPlan.BoundFingerprint, plan.ProfileFingerprint, plan.RuntimePlanFingerprint,
                host.JavaIdentitySha256, host.WrapperIdentitySha256, host.NonSensitiveArgumentFingerprint, targets.ToImmutableArray(), bindings.ToImmutableArray(), proof), diagnostics.ToImmutableArray());

            bool CompleteLocal(MmcResolvedComponent component, bool needsMainJar) {
                if (component.SourceKind == MmcComponentSourceKind.RemoteMetadata || !component.Patch.Fields.TryGetValue("mainClass", out var entry) || string.IsNullOrWhiteSpace(entry.GetString())) return false;
                if (needsMainJar && (!component.Patch.Fields.TryGetValue("mainJar", out var jar) || jar.ValueKind != System.Text.Json.JsonValueKind.Object || plan.MainJar.ComponentUid != component.Uid || plan.MainJar.SourceRevision != component.Patch.ContentSha256)) return false;
                List<MmcArtifactReference> candidates = [];
                foreach (string field in new[] { "libraries", "+libraries" }) if (component.Patch.Fields.TryGetValue(field, out var libs)) foreach (var declaration in libs.EnumerateArray()) {
                    var source = new MmcFieldSource(component.Uid, component.Version, component.Patch.Source, component.Patch.ContentSha256, field, 0);
                    var candidate = MmcArtifactRules.Library(declaration, component, MmcArtifactRole.Classpath, plan.Environment, source, diagnostics);
                    if (candidate is not null) candidates.Add(candidate);
                }
                var final = plan.Classpath.Concat(plan.NativeArtifacts).ToArray();
                bool Effective(MmcArtifactReference candidate) => final.Any(selected => selected.Key == candidate.Key ||
                    selected.Role == candidate.Role && selected.Coordinate is { } selectedCoordinate && candidate.Coordinate is { } declaredCoordinate &&
                    selectedCoordinate.Identity == declaredCoordinate.Identity && selectedCoordinate.Extension == declaredCoordinate.Extension &&
                    MmcArtifactRules.CompareVersions(selectedCoordinate.Version, declaredCoordinate.Version) >= 0);
                // The compiler's library slots select the higher Maven version. Its
                // exact final inputs were checked above; superseded declarations do
                // not require adding an obsolete JAR back to the live classpath.
                return candidates.Any(c => c.Role == MmcArtifactRole.Classpath) && candidates.All(Effective) &&
                    plan.Classpath.Any(a => a.ComponentUid == component.Uid && a.SourceRevision == component.Patch.ContentSha256 && a.Role == MmcArtifactRole.Classpath);
            }
        } catch (MmcInputException error) { diagnostics.Add(new("MMC_AUTHORITY_CONSERVATIVE", MmcDiagnosticSeverity.Warning, error.SourcePath, error.Message)); return new(null, diagnostics.ToImmutableArray()); }
        catch (FormatException error) { diagnostics.Add(new("MMC_AUTHORITY_CONSERVATIVE",MmcDiagnosticSeverity.Warning,"launch-classes",error.Message));return new(null,diagnostics.ToImmutableArray()); }
    }

    private static bool IsUnboundExecutionInput(string argument) => argument.StartsWith("-javaagent", StringComparison.Ordinal) || argument.StartsWith("-agentpath", StringComparison.Ordinal) ||
        argument.StartsWith("-agentlib", StringComparison.Ordinal) || argument.StartsWith("-Xbootclasspath", StringComparison.Ordinal) || argument.StartsWith("--patch-module", StringComparison.Ordinal) ||
        argument.StartsWith("--module-path", StringComparison.Ordinal) || argument.StartsWith("--upgrade-module-path", StringComparison.Ordinal) || argument is "-cp" or "-classpath" or "--class-path" or "-jar" or "-m" or "--module" or "-p" ||
        argument.StartsWith("--class-path=", StringComparison.Ordinal) || argument.StartsWith("--module=", StringComparison.Ordinal) || argument.StartsWith('@') || argument.StartsWith("-Xrun", StringComparison.Ordinal) ||
        new[] { "-Djava.class.path=", "-Dsun.boot.class.path=", "-Djava.ext.dirs=", "-Djava.endorsed.dirs=", "-Djdk.module.path=", "-Djdk.module.upgrade.path=" }.Any(prefix => argument.StartsWith(prefix, StringComparison.Ordinal));
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string message) => throw new MmcInputException("MMC_AUTHORITY_CONSERVATIVE", "authority", message);
}
