// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

public static class MmcProfileCompiler {
    public static MmcCompilationResult Compile(MmcResolvedProfile resolved, MmcRuntimeEnvironment environment,
        IReadOnlyList<MmcJavaConstraint>? hardConstraints = null, MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(environment);
        limits ??= new();
        List<MmcDiagnostic> diagnostics = [];
        try {
            if (resolved.FormatProfile != "prism-v1") throw new MmcInputException("MMC_UNSUPPORTED_FORMAT", "profile", "Unsupported compiler profile.");
            if (resolved.Components.Length > limits.MaxComponents) throw new MmcInputException("MMC_COMPONENT_BUDGET", "profile", "Component budget exceeded.");
            environment = MmcArtifactRules.Normalize(environment);
            string? mainClass = null, minecraftVersion = null, versionType = null, gameArgumentString = null, recommendedRuntime = null;
            MmcFieldSource? gameArgumentSource = null;
            MmcFieldSource? mainClassSource = null;
            MmcArtifactReference? mainJar = null, logging = null;
            MmcAssetIndex? assets = null;
            MmcArgumentToken? loggingArgument = null;
            List<MmcArgumentToken> jvm = [];
            List<(string Value, MmcFieldSource Source)> tweakers = [];
            List<string> traits = [];
            List<int> majors = [];
            List<MmcArtifactReference> ordinary = [], native = [], maven = [];
            List<MmcArtifactDeclaration> inventory = [];
            List<MmcFieldSource> provenance = [];
            List<MmcOverrideEffect> effects = [];
            Dictionary<string, MmcResolvedComponent> localDefinitions = new(StringComparer.Ordinal);
            Dictionary<string, List<string>> contributedLibraries = new(StringComparer.Ordinal);
            long declaredArtifacts = 0;
            int fieldPosition = 0;
            foreach (MmcResolvedComponent component in resolved.Components) {
                if (component.Patch.Uid != component.Uid) throw new MmcInputException("MMC_METADATA_IDENTITY", component.Uid, "Resolved component identity is inconsistent.");
                MmcPatchReader.ValidateCapabilities(component.Patch, component.Enabled, diagnostics);
                var fields = component.Patch.Fields;
                foreach (string field in new[] { "libraries", "+libraries", "mavenFiles", "mainJar" }) {
                    if (!fields.TryGetValue(field, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                    MmcArtifactRole role = field == "mainJar" ? MmcArtifactRole.MainJar : field == "mavenFiles" ? MmcArtifactRole.DownloadOnly : MmcArtifactRole.Classpath;
                    foreach (JsonElement declaration in field == "mainJar" ? new[] { value } : value.EnumerateArray().ToArray()) {
                        if (++declaredArtifacts > limits.MaxArchiveEntries) throw new MmcInputException("MMC_ARTIFACT_BUDGET", component.Uid, "Cumulative artifact budget exceeded.");
                        inventory.Add(new(component.Uid, component.Enabled, role, declaration.Clone(), Source(component, field)));
                    }
                }
                if (!component.Enabled) continue;
                if (fields.TryGetValue("mainClass", out var main) && Nonempty(main) is { } entry) { mainClass = entry; mainClassSource = Source(component, "mainClass"); }
                if (fields.TryGetValue("minecraftArguments", out var args) && Nonempty(args) is { } argumentString) {
                    gameArgumentString = argumentString; gameArgumentSource = Source(component, "minecraftArguments");
                }
                if (fields.TryGetValue("+jvmArgs", out var jvmArgs)) foreach (var argument in jvmArgs.EnumerateArray())
                    jvm.Add(new(argument.GetString()!, Source(component, "+jvmArgs")));
                if (fields.TryGetValue("+tweakers", out var tweakArray)) {
                    var additions = tweakArray.EnumerateArray().Select(x => x.GetString()!).ToArray();
                    tweakers.RemoveAll(x => additions.Contains(x.Value, StringComparer.Ordinal));
                    tweakers.AddRange(additions.Select(x => (x, Source(component, "+tweakers"))));
                }
                foreach (string field in new[] { "+traits", "traits" }) if (fields.TryGetValue(field, out var traitArray))
                    foreach (string trait in traitArray.EnumerateArray().Select(v => v.GetString()!)) {
                        var traitSource = Source(component, field);
                        if (!traits.Contains(trait, StringComparer.Ordinal)) {
                            traits.Add(trait);
                            if (trait == "FirstThreadOnMacOS") {
                                if (environment.OperatingSystem == "osx") jvm.Add(new("-XstartOnFirstThread", traitSource));
                                else diagnostics.Add(new("MMC_TRAIT_NOT_APPLICABLE", MmcDiagnosticSeverity.Information, component.Patch.Source, "FirstThreadOnMacOS is not applicable to this runtime system."));
                            }
                        }
                    }
                if (fields.TryGetValue("compatibleJavaMajors", out var java)) { majors.AddRange(java.EnumerateArray().Select(v => v.GetInt32())); Source(component, "compatibleJavaMajors"); }
                if (fields.TryGetValue("compatibleJavaName", out var runtime) && Nonempty(runtime) is { } name) { recommendedRuntime = name; Source(component, "compatibleJavaName"); }
                if (component.Uid == "net.minecraft") {
                    minecraftVersion = component.Version;
                    if (fields.TryGetValue("type", out var type) && Nonempty(type) is { } versionKind) versionType = versionKind;
                    if (fields.TryGetValue("assetIndex", out var index) && index.ValueKind == JsonValueKind.Object) {
                        string id = MmcArtifactRules.Text(index, "id") ?? throw new MmcInputException("MMC_ASSET_INDEX", component.Patch.Source, "Asset index has no id.");
                        MmcArtifactRules.Relative(id, component.Patch.Source);
                        var source = Source(component, "assetIndex");
                        var artifact = MmcArtifactRules.Auxiliary(index, component, MmcArtifactRole.AssetIndex, "indexes/" + id + ".json", source);
                        assets = new(id, artifact, source);
                        inventory.Add(new(component.Uid, true, MmcArtifactRole.AssetIndex, index.Clone(), source));
                    } else if (fields.TryGetValue("assets", out var assetName) && Nonempty(assetName) is { } legacyId && assets is null) {
                        assets = new(legacyId, null, Source(component, "assets"));
                    }
                } else if (fields.ContainsKey("assetIndex") || fields.ContainsKey("assets") || fields.ContainsKey("type"))
                    diagnostics.Add(new("MMC_IGNORED_GAME_METADATA", MmcDiagnosticSeverity.Warning, component.Patch.Source, "Only net.minecraft may contribute game version/type/assets; other source fields are retained."));
                foreach (string field in new[] { "libraries", "+libraries", "mavenFiles" }) if (fields.TryGetValue(field, out var libraries)) {
                    foreach (var library in libraries.EnumerateArray()) {
                        MmcArtifactRole role = field == "mavenFiles" ? MmcArtifactRole.DownloadOnly : MmcArtifactRole.Classpath;
                        var artifact = MmcArtifactRules.Library(library, component, role, environment, Source(component, field), diagnostics, resolved.Package.PackageLocalScopeSha256);
                        if (artifact is null) continue;
                        if (artifact.Role == MmcArtifactRole.DownloadOnly) maven.Add(artifact);
                        else {
                            if (artifact.Role == MmcArtifactRole.Classpath) {
                                if (!contributedLibraries.TryGetValue(component.Uid, out var keys)) contributedLibraries.Add(component.Uid, keys = []);
                                keys.Add(artifact.Key);
                            }
                            Apply(artifact.Role == MmcArtifactRole.Native ? native : ordinary, artifact);
                        }
                    }
                }
                if (fields.TryGetValue("mainJar", out var jar) && jar.ValueKind != JsonValueKind.Null) {
                    mainJar = MmcArtifactRules.Library(jar, component, MmcArtifactRole.MainJar, environment, Source(component, "mainJar"), diagnostics, resolved.Package.PackageLocalScopeSha256)
                        ?? throw new MmcInputException("MMC_MAIN_JAR", component.Patch.Source, "Final main JAR does not apply to this runtime.");
                }
                if (fields.TryGetValue("logging", out var log)) {
                    var file = log.GetProperty("file");
                    string id = MmcArtifactRules.Text(file, "id")!;
                    MmcArtifactRules.Relative(id, component.Patch.Source);
                    var source = Source(component, "logging");
                    logging = MmcArtifactRules.Auxiliary(file, component, MmcArtifactRole.LoggingConfiguration, "logging/" + id, source);
                    string argument = MmcArtifactRules.Text(log, "argument")!;
                    if (!argument.Contains("${path}", StringComparison.Ordinal)) throw new MmcInputException("MMC_LOGGING", component.Patch.Source, "Logging argument does not bind its declared file.");
                    loggingArgument = new(argument.Replace("${path}", "${mmc_logging_config}", StringComparison.Ordinal), source);
                    inventory.Add(new(component.Uid, true, MmcArtifactRole.LoggingConfiguration, file.Clone(), source));
                }
                if (component.SourceKind != MmcComponentSourceKind.RemoteMetadata) {
                    string[] owned = fields.Where(field => field.Key switch {
                        "mainClass" or "minecraftArguments" => Nonempty(field.Value) is not null,
                        "mainJar" => field.Value.ValueKind == JsonValueKind.Object,
                        "+jvmArgs" or "+tweakers" or "libraries" or "+libraries" or "compatibleJavaMajors" => field.Value.GetArrayLength() > 0,
                        _ => false
                    }).Select(field => field.Key).Order(StringComparer.Ordinal).ToArray();
                    localDefinitions.Add(component.Uid, component);
                    effects.Add(new(component.Uid, owned.ToImmutableArray(), false));
                }
            }
            if (string.IsNullOrEmpty(mainClass) || mainClass.Contains('\0') || mainJar is null || minecraftVersion is null)
                throw new MmcInputException("MMC_INCOMPLETE_LAUNCH", "profile", "Minecraft version, entry class and main JAR must all be explicitly resolved.");
            HashSet<string> finalLibraries = ordinary.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < effects.Count; i++) {
                var effect = effects[i];
                var component = localDefinitions[effect.ComponentUid];
                bool finalEntryFromComponent = mainClassSource?.ComponentUid == component.Uid && mainClassSource.SourceSha256 == component.Patch.ContentSha256;
                bool finalJarFromComponent = mainJar.Provenance.ComponentUid == component.Uid && mainJar.SourceRevision == component.Patch.ContentSha256;
                bool adoptedLibraryContribution = contributedLibraries.TryGetValue(component.Uid, out var keys) && keys.Count > 0 && keys.All(finalLibraries.Contains);
                bool complete = component.Uid is "net.minecraft" or "net.minecraftforge" && finalEntryFromComponent && adoptedLibraryContribution &&
                    effect.Fields.Contains("mainClass") && (component.Uid != "net.minecraft" || effect.Fields.Contains("mainJar") && finalJarFromComponent);
                effects[i] = effect with { ReplacesBuiltinLaunchDefinition = complete };
            }
            assets ??= new("legacy", null, new("net.minecraft", minecraftVersion, "profile:legacy-assets", resolved.SourceFingerprint, "assets", fieldPosition++));
            if (loggingArgument is not null) jvm.Add(loggingArgument);
            List<MmcArgumentToken> gameArguments = [];
            if (gameArgumentString is not null) {
                // Native legacy-game semantics: literal ASCII spaces only, before macro
                // expansion. Quotes/backslashes/tabs are data, not shell syntax. Independently
                // implemented from Prism 899d2f3957215a09fe398a8a8cbb8a8a9c619968,
                // launcher/minecraft/MinecraftInstance.cpp processMinecraftArgs (L704-L712).
                gameArguments.AddRange(gameArgumentString.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => new MmcArgumentToken(v, gameArgumentSource!)));
            }
            foreach (var tweaker in tweakers) { gameArguments.Add(new("--tweakClass", tweaker.Source)); gameArguments.Add(new(tweaker.Value, tweaker.Source)); }
            if (jvm.Concat(gameArguments).Any(argument => argument.Value.Contains('\0')))
                throw new MmcInputException("MMC_ARGUMENTS", "profile", "NUL is not a valid process argument.");
            var javaPolicy = MmcJavaPolicyEvaluator.Create(majors, recommendedRuntime, hardConstraints);
            if (javaPolicy.Kind == MmcJavaPolicyKind.Unsatisfiable) throw new MmcInputException("MMC_JAVA_UNSATISFIABLE", "profile", "Declared Java candidates and hard constraints are unsatisfiable.");
            if (environment.JavaMajor != 0 && !javaPolicy.Allows(environment.JavaMajor)) throw new MmcInputException("MMC_JAVA_SELECTION", "environment", "Selected Java is outside the compiled policy.");
            List<MmcArtifactReference> auxiliaries = [];
            if (assets.Artifact is not null) auxiliaries.Add(assets.Artifact);
            if (logging is not null) auxiliaries.Add(logging);
            var classpath = ordinary.Append(mainJar).ToImmutableArray();
            ValidateTargets(classpath.Concat(native).Concat(maven).Concat(auxiliaries));
            if (diagnostics.Any(d => d.Severity == MmcDiagnosticSeverity.Error)) return new(null, diagnostics.Distinct().ToImmutableArray());
            var compiled = new MmcCompiledProfile("prism-v1", resolved.SourceFingerprint, "", "", environment, minecraftVersion, versionType,
                mainClass, mainJar, assets, jvm.ToImmutableArray(), gameArguments.ToImmutableArray(), tweakers.Select(t => t.Value).ToImmutableArray(), traits.ToImmutableArray(),
                classpath, native.ToImmutableArray(), maven.ToImmutableArray(), auxiliaries.ToImmutableArray(), inventory.ToImmutableArray(), javaPolicy,
                provenance.ToImmutableArray(), effects.ToImmutableArray());
            var fingerprints = MmcProfileFingerprints.Compute(compiled);
            return new(compiled with { ProfileFingerprint = fingerprints.ProfileFingerprint, RuntimePlanFingerprint = fingerprints.RuntimePlanFingerprint }, diagnostics.Distinct().ToImmutableArray());

            MmcFieldSource Source(MmcResolvedComponent component, string field) {
                var source = new MmcFieldSource(component.Uid, component.Version, component.Patch.Source, component.Patch.ContentSha256, field, fieldPosition++);
                provenance.Add(source);
                return source;
            }
        } catch (MmcInputException e) {
            diagnostics.Add(new(e.Code, MmcDiagnosticSeverity.Error, e.SourcePath, e.Message));
            return new(null, diagnostics.Distinct().ToImmutableArray());
        }
    }

    private static string? Nonempty(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    private static void Apply(List<MmcArtifactReference> target, MmcArtifactReference candidate) {
        int[] matching = target.Select((a, i) => (a, i)).Where(x => x.a.Coordinate!.Identity == candidate.Coordinate!.Identity).Select(x => x.i).ToArray();
        if (matching.Length > 1) throw new MmcInputException("MMC_LIBRARY_AMBIGUOUS", candidate.Provenance.Source, "Several existing library slots match this identity.");
        if (matching.Length == 0) { target.Add(candidate); return; }
        int index = matching[0];
        var existing = target[index];
        if (existing.Coordinate!.Extension != candidate.Coordinate!.Extension) throw new MmcInputException("MMC_LIBRARY_CONFLICT", candidate.Provenance.Source, "Matching library identities use different extensions.");
        int compare = MmcArtifactRules.CompareVersions(candidate.Coordinate.Version, existing.Coordinate.Version);
        if (compare == 0 && (existing.Sha1 is not null && candidate.Sha1 is not null && existing.Sha1 != candidate.Sha1 ||
            existing.Sha256 is not null && candidate.Sha256 is not null && existing.Sha256 != candidate.Sha256))
            throw new MmcInputException("MMC_LIBRARY_CONFLICT", candidate.Provenance.Source, "Equal library identities/versions declare different content.");
        if (compare > 0) target[index] = candidate;
    }
    private static void ValidateTargets(IEnumerable<MmcArtifactReference> artifacts) {
        Dictionary<string, MmcArtifactReference> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts) {
            string target = artifact.Scope + ":" + (artifact.Role == MmcArtifactRole.AssetIndex ? "assets:" : "runtime:") + artifact.RelativePath;
            if (!seen.TryAdd(target, artifact)) {
                var previous = seen[target];
                bool differentHash = previous.Sha1 is not null && artifact.Sha1 is not null && previous.Sha1 != artifact.Sha1 ||
                    previous.Sha256 is not null && artifact.Sha256 is not null && previous.Sha256 != artifact.Sha256;
                bool sameSource = previous.DownloadUrls.SequenceEqual(artifact.DownloadUrls) && previous.Scope == artifact.Scope;
                if (differentHash || !sameSource && previous.Sha1 is null && previous.Sha256 is null && artifact.Sha1 is null && artifact.Sha256 is null)
                    throw new MmcInputException("MMC_ARTIFACT_COLLISION", artifact.Provenance.Source, "Logical artifact paths collide with different or unbound content sources.");
            }
        }
    }
}
