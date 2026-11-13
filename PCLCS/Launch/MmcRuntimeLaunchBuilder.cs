using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public static partial class MmcRuntimeLaunchBuilder {
    private sealed record Value(string Actual, string Redacted);
    private static readonly HashSet<string> PairJvmOptions = new(StringComparer.Ordinal) {
        "--add-opens", "--add-exports", "--add-reads", "--enable-native-access", "--add-modules", "--limit-modules", "--enable-final-field-mutation"
    };
    public static async Task<MmcRuntimeLaunchPlan> BuildAsync(MmcRuntimeLaunchRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        var definition = request.Definition; var compiled = request.BoundPlan.CompiledProfile; var settings = request.Settings;
        ValidateSnapshot(request);
        cancellationToken.ThrowIfCancellationRequested();
        var currentPaths = MmcInstancePaths.Resolve(request.Paths.VersionRoot, definition.Layout.GameRootLeaf);
        var locks = definition.ArtifactLocks.ToDictionary(l => l.ArtifactKey, StringComparer.Ordinal);
        var targets = request.Targets.ToDictionary(t => t.ArtifactKey, StringComparer.Ordinal);
        var activeArtifacts = compiled.Classpath.Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts).GroupBy(a => a.Key).Select(group => group.First()).ToArray();
        List<MmcRuntimeInputObservation> inputs = [];
        foreach (var artifact in activeArtifacts) {
            if (!locks.TryGetValue(artifact.Key, out var adopted) || !request.BoundPlan.ActiveArtifactLocks.Any(l => l == adopted))
                Fail("MMC_RUNTIME_BINDING", "The runtime plan is missing an exact adopted active input binding.");
            if (!targets.TryGetValue(artifact.Key, out var target)) Fail("MMC_RUNTIME_BINDING", "The runtime plan is missing an actual target mapping.");
            if (target.TargetRelativePath != adopted.TargetRelativePath) Fail("MMC_RUNTIME_BINDING", "Actual target mapping differs from the bound definition.");
            string path = currentPaths.ResolveRelative(MmcPathRole.Instance, adopted.TargetRelativePath);
            MmcRuntimeFiles.SamePath(target.ActualPath, path);
            if (artifact.Scope == MmcArtifactScope.Shared) MmcRuntimeFiles.Owned(path, currentPaths.ResolveRelative(MmcPathRole.Runtime, request.Directories.ViewKey));
            inputs.Add(await MmcRuntimeFiles.ObserveAsync("artifact:" + artifact.Key, path, adopted.AdoptedSha256, adopted.SizeBytes, cancellationToken).ConfigureAwait(false));
        }
        string view = currentPaths.ResolveRelative(MmcPathRole.Runtime, request.Directories.ViewKey);
        MmcRuntimeFiles.SamePath(request.Directories.NativeDirectory, Path.Combine(view, "natives"));
        MmcRuntimeFiles.SamePath(request.Directories.AssetsRoot, Path.Combine(view, "assets"));
        ValidateGameAssets(request, view);
        foreach (var derived in definition.DerivedOutputs.Where(d => d.RuntimePlanFingerprint == compiled.RuntimePlanFingerprint && d.ViewKey == request.Directories.ViewKey)) {
            string path = currentPaths.ResolveRelative(MmcPathRole.Instance, derived.TargetRelativePath);
            inputs.Add(await MmcRuntimeFiles.ObserveAsync("derived:" + derived.TargetRelativePath, path, derived.Sha256, derived.SizeBytes, cancellationToken).ConfigureAwait(false));
        }
        inputs.Add(await MmcRuntimeFiles.ObserveAsync("java:" + request.Java.IdentitySha256, request.Java.ExecutablePath, request.Java.ExecutableSha256, null, cancellationToken, owned: false).ConfigureAwait(false));
        if (settings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper) {
            if (request.Wrapper is null || request.Bridge is null) Fail("MMC_RUNTIME_WRAPPER", "Owned verified JavaWrapper and exact argv bridge inputs are required.");
            foreach (var support in new[] { ("wrapper", request.Wrapper), ("bridge", request.Bridge) }) {
                MmcRuntimeFiles.Owned(support.Item2.ActualPath, Path.Combine(view, "support"));
                inputs.Add(await MmcRuntimeFiles.ObserveAsync("support:" + support.Item1, support.Item2.ActualPath, support.Item2.Sha256, support.Item2.SizeBytes, cancellationToken).ConfigureAwait(false));
            }
            if (!request.Bridge.Sha256.Equals(MmcRuntimeBridgeTransport.JarSha256(), StringComparison.OrdinalIgnoreCase)) Fail("MMC_RUNTIME_BRIDGE", "Bridge support bytes do not match this host's reviewed transport.");
        }
        var mutable = DescribeMutablePaths(currentPaths, request);
        ImmutableArray<string> classpath = compiled.Classpath.Select(a => targets[a.Key].ActualPath).ToImmutableArray();
        // Targets/observations stay absolute. Only the Java representation is relative
        // to the fixed process/user.dir root, avoiding repeated deep instance prefixes.
        string gameClasspath = string.Join(Path.PathSeparator, classpath.Select(path => MmcRuntimeFiles.RelativeJavaPath(currentPaths.GameRoot, path)));
        var replacements = Variables(request, targets, gameClasspath, mutable);
        List<Value> logicalJvm = [];
        if (settings.MinimumMemoryMiB > 0) logicalJvm.Add(new("-Xms" + settings.MinimumMemoryMiB.ToString(CultureInfo.InvariantCulture) + "m", "-Xms" + settings.MinimumMemoryMiB.ToString(CultureInfo.InvariantCulture) + "m"));
        logicalJvm.Add(new("-Xmx" + settings.MaximumMemoryMiB.ToString(CultureInfo.InvariantCulture) + "m", "-Xmx" + settings.MaximumMemoryMiB.ToString(CultureInfo.InvariantCulture) + "m"));
        var userJvm = settings.UserJvmTokens.Select(t => Expand(t, replacements, request.Account)).ToArray();
        logicalJvm.AddRange(userJvm);
        logicalJvm.AddRange(compiled.JvmArguments.Select(a => Expand(a.Value, replacements, request.Account)));
        ValidateJvm(logicalJvm.Select(t => t.Actual).ToArray());
        ValidateWorkingDirectory(logicalJvm.Select(t => t.Actual), currentPaths.GameRoot);
        // Only the dedicated live binding can introduce a host authentication agent.
        // Package/user JVM tokens above still cannot authorize execution inputs.
        var authentication = await AuthenticationArgumentsAsync(request, view, mutable, inputs, logicalJvm.Select(a => a.Actual).ToArray(), cancellationToken).ConfigureAwait(false);
        logicalJvm.AddRange(authentication.Arguments);
        foreach (var property in new[] {
            "-Duser.dir=" + currentPaths.GameRoot,
            "-Djava.library.path=" + MmcRuntimeFiles.JavaPath(request.Directories.NativeDirectory),
            "-Djava.io.tmpdir=" + MmcRuntimeFiles.JavaPath(mutable.TemporaryDirectory),
            "-Doolloo.jlw.tmpdir=" + MmcRuntimeFiles.JavaPath(mutable.TemporaryDirectory),
            "-Dorg.lwjgl.system.SharedLibraryExtractPath=" + MmcRuntimeFiles.JavaPath(mutable.ExtractDirectory), "-Doolloo.jlw.debug=false"
        }) logicalJvm.Add(new(property, property));
        List<Value> game = compiled.GameArguments.Select(a => Expand(a.Value, replacements, request.Account)).ToList();
        ValidateExtraGame(settings.ExtraGameTokens);
        game.AddRange(settings.ExtraGameTokens.Select(t => Expand(t, replacements, request.Account)));
        ValidateBoundGameFlags(game, replacements);
        if (settings.AppendResolutionArguments) {
            if (!game.Any(a => a.Actual == "--width")) { game.Add(new("--width", "--width")); game.Add(new(settings.ResolutionWidth.ToString(CultureInfo.InvariantCulture), settings.ResolutionWidth.ToString(CultureInfo.InvariantCulture))); }
            if (!game.Any(a => a.Actual == "--height")) { game.Add(new("--height", "--height")); game.Add(new(settings.ResolutionHeight.ToString(CultureInfo.InvariantCulture), settings.ResolutionHeight.ToString(CultureInfo.InvariantCulture))); }
        }
        if (settings.Fullscreen && !game.Any(a => a.Actual == "--fullscreen")) game.Add(new("--fullscreen", "--fullscreen"));
        var required = RequiredClasses(compiled.MainClass, logicalJvm.Select(a => a.Actual), game.Select(a => a.Actual));
        if (settings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper) await MmcClasspathInspector.VerifySupportDoesNotShadowAsync(request, required, logicalJvm.Select(a => a.Actual).ToArray(), cancellationToken).ConfigureAwait(false);
        List<string> actual = logicalJvm.Select(a => a.Actual).ToList(), redacted = logicalJvm.Select(a => a.Redacted).ToList();
        List<string> sensitive = Secrets(request.Account).Concat(AuthenticationSecrets(request.Authentication)).ToList();
        string vmClasspath = settings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper
            ? string.Join(Path.PathSeparator, new[] { request.Wrapper!.ActualPath, request.Bridge!.ActualPath }.Concat(classpath).Select(path => MmcRuntimeFiles.RelativeJavaPath(currentPaths.GameRoot, path))) : gameClasspath;
        string cpProperty = "-Djava.class.path=" + vmClasspath;
        actual.Add(cpProperty); redacted.Add(cpProperty);
        if (settings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper) {
            // VM startup sees the game/system-loader CP. Wrapper's raw parser
            // sees the same -D property, avoiding its BuiltinClassLoader injector.
            var restored = Properties(actual);
            string frame = MmcRuntimeBridgeTransport.Encode(compiled.MainClass, game.Select(a => a.Actual).ToArray(), restored);
            actual.AddRange(["oolloo.jlw.Wrapper", "-jar", MmcRuntimeFiles.JavaPath(request.Wrapper!.ActualPath), MmcRuntimeBridgeTransport.EntryPoint, frame]);
            redacted.AddRange(["oolloo.jlw.Wrapper", "-jar", MmcRuntimeFiles.JavaPath(request.Wrapper.ActualPath), MmcRuntimeBridgeTransport.EntryPoint, "[sensitive argv frame]"]);
            sensitive.Add(frame);
        } else {
            actual.Add(compiled.MainClass); redacted.Add(compiled.MainClass);
            actual.AddRange(game.Select(a => a.Actual)); redacted.AddRange(game.Select(a => a.Redacted));
        }
        // MmcSessions inserts one 32-hex nonce property immediately before Start.
        // Reserve its exact quoted length without changing arguments/fingerprints.
        if (OperatingSystem.IsWindows()) MmcRuntimeWindowsCommandLine.Validate(request.Java.ExecutablePath, actual.Prepend(MmcSessions.NonceArgumentPrefix + new string('0', 32)));
        string argumentFingerprint = MmcRuntimeFiles.Fingerprint(new[] { "mmc-runtime-arguments-v1", settings.WrapperMode.ToString(), compiled.MainClass }
            .Concat(logicalJvm.Select(a => a.Redacted)).Concat(game.Select(a => a.Redacted)).Concat([cpProperty, request.Wrapper?.Sha256, request.Bridge?.Sha256]));
        InitializeMutablePaths(mutable, request);
        try { await PublishAuthenticationExecutionAsync(request, mutable, authentication, inputs, cancellationToken).ConfigureAwait(false); }
        catch {
            // No launch plan has escaped this builder and no durable launch intent
            // exists. Retire only this freshly owned scope, preserving the source.
            try { MmcSessionRuntimeDirectories.AbandonFreshBuild(currentPaths, definition.InstanceId, settings.SessionId, mutable.RuntimeBinding, request.RuntimeAuthority); }
            catch (IOException) { /* Preserve changed ownership for explicit recovery. */ }
            throw;
        }
        string inputFingerprint = MmcRuntimeFiles.Fingerprint(new[] { "mmc-runtime-inputs-v1", definition.InstanceId.ToString("D"), definition.Generation.ToString(CultureInfo.InvariantCulture), request.BoundPlan.BoundFingerprint,
            compiled.RuntimePlanFingerprint, request.Java.IdentitySha256, argumentFingerprint, currentPaths.GameRoot, request.Directories.ViewKey, settings.SessionId.ToString("N") }
            .Concat(inputs.SelectMany(i => new[] { i.Identity, i.ActualPath, i.Sha256, i.SizeBytes.ToString(CultureInfo.InvariantCulture) })));
        return new(request, actual.ToImmutableArray(), redacted.ToImmutableArray(), game.Select(a => a.Redacted).ToImmutableArray(), logicalJvm.Select(a => a.Redacted).ToImmutableArray(),
            userJvm.Select(a => a.Redacted).ToImmutableArray(), required, classpath, inputs.ToImmutableArray(), mutable, argumentFingerprint, inputFingerprint, sensitive.Distinct().ToImmutableArray(), logicalJvm.Select(a => a.Actual).ToImmutableArray());
    }

    /// <summary>
    /// The plan-derived half of the package command variable surface (contract §3). The stage boundary itself
    /// never sees the plan, so this is the single place the plan is projected into command variables; account,
    /// session and authentication values are deliberately absent so a command can never bind them.
    /// </summary>
    public static Dictionary<string, string> CommandVariables(MmcRuntimeLaunchPlan launch) {
        ArgumentNullException.ThrowIfNull(launch);
        var request = launch.Request;
        var profile = request.BoundPlan.CompiledProfile;
        var result = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["profile_name"] = request.Settings.DisplayName,
            ["version_name"] = profile.MinecraftVersion,
            ["version_type"] = profile.MinecraftVersionType ?? "",
            ["assets_root"] = MmcRuntimeCommandExecution.CommandPath(request.Directories.AssetsRoot),
            ["assets_index_name"] = profile.Assets.Id,
            ["game_assets"] = MmcRuntimeCommandExecution.CommandPath(request.Directories.GameAssetsRoot),
            ["natives_directory"] = MmcRuntimeCommandExecution.CommandPath(request.Directories.NativeDirectory),
            ["classpath"] = string.Join(Path.PathSeparator, launch.OrderedGameClasspath.Select(MmcRuntimeCommandExecution.CommandPath))
        };
        string libraries = MmcRuntimeCommandExecution.CommandPath(Path.Combine(request.Paths.RuntimeRoot, request.Directories.ViewKey, "libraries"));
        result["library_directory"] = libraries; result["libraries_directory"] = libraries;
        var mainJar = request.Targets.FirstOrDefault(value => value.ArtifactKey == profile.MainJar.Key);
        if (mainJar is not null) result["primary_jar"] = MmcRuntimeCommandExecution.CommandPath(mainJar.ActualPath);
        var logging = profile.AuxiliaryArtifacts.FirstOrDefault(value => value.Role == MmcArtifactRole.LoggingConfiguration);
        var loggingTarget = logging is null ? null : request.Targets.FirstOrDefault(value => value.ArtifactKey == logging.Key);
        if (loggingTarget is not null) result["mmc_logging_config"] = MmcRuntimeCommandExecution.CommandPath(loggingTarget.ActualPath);
        return result;
    }

    public static async Task RevalidateFilesAsync(MmcRuntimeLaunchPlan launch, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(launch); ValidateSnapshot(launch.Request);
        MmcInstancePaths.Resolve(launch.Request.Paths.VersionRoot, launch.Request.Definition.Layout.GameRootLeaf);
        ValidateMutableOwner(launch);
        foreach (var input in launch.Inputs) await MmcRuntimeFiles.ObserveAsync(input.Identity, input.ActualPath, input.Sha256, input.SizeBytes, cancellationToken, owned: !input.Identity.StartsWith("java:", StringComparison.Ordinal)).ConfigureAwait(false);
    }

    /// <summary>
    /// The post-command revalidation receipt of contract §6. A package command may legitimately change any
    /// input byte it is allowed to write, so the pre-command fingerprint must never be reused: this recomputes
    /// the input identity from the plan's own observed inputs and the argument fingerprint.
    /// </summary>
    public static string ConfirmInputFingerprint(MmcRuntimeLaunchPlan launch) {
        ArgumentNullException.ThrowIfNull(launch);
        var request = launch.Request;
        return MmcRuntimeFiles.Fingerprint(new[] { "mmc-runtime-inputs-v1", request.Definition.InstanceId.ToString("D"), request.Definition.Generation.ToString(CultureInfo.InvariantCulture), request.BoundPlan.BoundFingerprint,
            request.BoundPlan.CompiledProfile.RuntimePlanFingerprint, request.Java.IdentitySha256, launch.NonSensitiveArgumentFingerprint, request.Paths.GameRoot, request.Directories.ViewKey, request.Settings.SessionId.ToString("N") }
            .Concat(launch.Inputs.SelectMany(i => new[] { i.Identity, i.ActualPath, i.Sha256, i.SizeBytes.ToString(CultureInfo.InvariantCulture) })));
    }
    private static void ValidateSnapshot(MmcRuntimeLaunchRequest request) {
        var errors = MmcDefinitionCodec.Validate(request.Definition);
        if (!errors.IsEmpty || !MmcProfileFingerprints.Matches(request.BoundPlan.CompiledProfile) || request.BoundPlan.DefinitionGeneration != request.Definition.Generation || request.BoundPlan.BoundFingerprint != request.Definition.InstalledProjection?.BoundFingerprint ||
            request.BoundPlan.CompiledProfile.RuntimePlanFingerprint != request.Definition.InstalledProjection?.RuntimePlanFingerprint) Fail("MMC_RUNTIME_DEFINITION", "Published definition and canonical bound runtime plan are inconsistent.");
        var environment = request.BoundPlan.CompiledProfile.Environment; var settings = request.Settings;
        string architecture = request.Java.Architecture switch { "amd64" => "x86_64", "i386" or "i686" => "x86", "aarch64" => "arm64", "arm" or "armhf" => "arm32", _ => request.Java.Architecture };
        if (!OperatingSystem.IsWindows() || environment.OperatingSystem != "windows" || request.Java.Major != environment.JavaMajor || request.Java.Bitness != environment.JavaBitness || architecture != environment.Architecture)
            Fail("MMC_RUNTIME_JAVA", "Selected Java must match the final compiled Windows runtime environment.");
        MmcRuntimeFiles.Hash(request.Java.ExecutableSha256); MmcRuntimeFiles.Hash(request.Java.IdentitySha256); MmcRuntimeFiles.Hash(request.Directories.ViewKey);
        var policy = MmcJavaPolicyEvaluator.Create(request.BoundPlan.CompiledProfile.JavaPolicy.Kind == MmcJavaPolicyKind.DeclaredCandidates ? request.BoundPlan.CompiledProfile.JavaPolicy.AllowedMajors : [],
            request.BoundPlan.CompiledProfile.JavaPolicy.RecommendedRuntimeName, request.BoundPlan.CompiledProfile.JavaPolicy.HardConstraints.Concat(settings.PlatformJavaConstraints).Concat(request.Account.JavaConstraints).Concat(AuthenticationJavaConstraints(request.Authentication)).ToArray());
        if (request.BoundPlan.CompiledProfile.JavaPolicy.Kind == MmcJavaPolicyKind.Unsatisfiable || !policy.Allows(request.Java.Major)) Fail("MMC_RUNTIME_JAVA", "Selected Java violates package, platform or account constraints.");
        if (settings.SessionId == Guid.Empty || settings.MaximumMemoryMiB is < 16 or > 1048576 || settings.MinimumMemoryMiB < 0 || settings.MinimumMemoryMiB > settings.MaximumMemoryMiB ||
            settings.ResolutionWidth is < 1 or > 65535 || settings.ResolutionHeight is < 1 or > 65535 || settings.UserJvmTokens.IsDefault || settings.ExtraGameTokens.IsDefault || settings.PlatformJavaConstraints.IsDefault || request.Targets.IsDefault || !Enum.IsDefined(settings.WrapperMode) ||
            settings.DisplayName is null || settings.LauncherName is null || settings.LauncherVersion is null || settings.UserJvmTokens.Length + request.BoundPlan.CompiledProfile.JvmArguments.Length > 16384 || settings.ExtraGameTokens.Length + request.BoundPlan.CompiledProfile.GameArguments.Length > 4096)
            Fail("MMC_RUNTIME_SETTINGS", "Runtime settings are incomplete or outside their allowed ranges.");
        // The mapping must still be an ordered list of literal, non-reserved entries at the process
        // boundary; Core already refuses these during parsing, so this guards a forged snapshot too.
        if (settings.EnvironmentOverride.IsDefault || settings.EnvironmentOverride.Length > 4096) Fail("MMC_RUNTIME_ENV_SETTINGS", "The package environment mapping is absent by construction or beyond its entry budget.");
        foreach (var entry in settings.EnvironmentOverride) {
            if (entry.Name is null || entry.Value is null || entry.Name.Length == 0 || entry.Name.Contains('=') || entry.Value.Contains('\0') || entry.Name.Any(char.IsControl))
                Fail("MMC_RUNTIME_ENV_SETTINGS", "A package environment entry is not a valid literal name/value pair.");
            if (MmcEnvironmentPolicy.IsReserved(entry.Name)) Fail("MMC_RUNTIME_ENV_RESERVED", "A package environment entry redeclares a launcher-managed variable: " + entry.Name);
        }
        if (settings.WrapperMode == MmcRuntimeWrapperMode.JavaWrapper && request.Java.Major < 8) Fail("MMC_RUNTIME_JAVA", "Exact JavaWrapper transport requires Java 8 or newer.");
        if (request.Account.Name is null || request.Account.Uuid is null || request.Account.AccessToken is null || request.Account.Session is null || request.Account.UserType is null || request.Account.UserPropertiesJson is null || request.Account.UserPropertiesJson.Length > 262144)
            Fail("MMC_RUNTIME_ACCOUNT", "Live account launch data is incomplete or exceeds its transient budget.");
        try { using var json = System.Text.Json.JsonDocument.Parse(request.Account.UserPropertiesJson); if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) Fail("MMC_RUNTIME_ACCOUNT", "Live user properties must be a JSON object."); }
        catch (System.Text.Json.JsonException) { Fail("MMC_RUNTIME_ACCOUNT", "Live user properties are not valid JSON."); }
    }
    private static Dictionary<string, Value> Variables(MmcRuntimeLaunchRequest request, Dictionary<string, MmcRuntimeArtifactTarget> targets, string classpath, MmcRuntimeMutableDirectories mutable) {
        var profile = request.BoundPlan.CompiledProfile; var account = request.Account; var settings = request.Settings;
        Dictionary<string, Value> result = new(StringComparer.Ordinal);
        void Normal(string name, string value) => result.Add(name, new(value, value));
        void Account(string name, string? value) { if (value is not null) result.Add(name, new(value, "[account:" + name + "]")); }
        Normal("profile_name", settings.DisplayName); Normal("version_name", profile.MinecraftVersion); Normal("version_type", profile.MinecraftVersionType ?? "");
        Normal("game_directory", MmcRuntimeFiles.JavaPath(request.Paths.GameRoot)); Normal("instance_directory", MmcRuntimeFiles.JavaPath(request.Paths.InstanceRoot));
        Normal("INST_DIR", MmcRuntimeFiles.JavaPath(request.Paths.InstanceRoot)); Normal("INST_MC_DIR", MmcRuntimeFiles.JavaPath(request.Paths.GameRoot));
        Normal("assets_root", MmcRuntimeFiles.JavaPath(request.Directories.AssetsRoot)); Normal("assets_index_name", profile.Assets.Id); Normal("game_assets", MmcRuntimeFiles.JavaPath(request.Directories.GameAssetsRoot));
        Normal("library_directory", MmcRuntimeFiles.JavaPath(Path.Combine(request.Paths.RuntimeRoot, request.Directories.ViewKey, "libraries"))); Normal("libraries_directory", result["library_directory"].Actual);
        Normal("natives_directory", MmcRuntimeFiles.JavaPath(request.Directories.NativeDirectory)); Normal("classpath", classpath); Normal("classpath_separator", Path.PathSeparator.ToString());
        Normal("primary_jar", MmcRuntimeFiles.JavaPath(targets[profile.MainJar.Key].ActualPath)); Normal("launcher_name", settings.LauncherName); Normal("launcher_version", settings.LauncherVersion);
        Normal("resolution_width", settings.ResolutionWidth.ToString(CultureInfo.InvariantCulture)); Normal("resolution_height", settings.ResolutionHeight.ToString(CultureInfo.InvariantCulture));
        var logging = profile.AuxiliaryArtifacts.SingleOrDefault(a => a.Role == MmcArtifactRole.LoggingConfiguration);
        if (logging is not null) Normal("mmc_logging_config", MmcRuntimeFiles.JavaPath(targets[logging.Key].ActualPath));
        Account("auth_player_name", account.Name); Account("auth_uuid", account.Uuid); Account("auth_access_token", account.AccessToken); Account("access_token", account.AccessToken);
        Account("auth_session", account.Session); Account("user_type", account.UserType); Account("user_properties", account.UserPropertiesJson);
        Account("auth_xuid", account.Xuid); Account("clientid", account.ClientId); Account("client_id", account.ClientId); Account("client_token", account.ClientToken);
        return result;
    }
    private static Value Expand(string template, Dictionary<string, Value> variables, MmcRuntimeAccountSnapshot account) {
        if (template is null || template.Contains('\0')) Fail("MMC_RUNTIME_ARGUMENT", "NUL/null runtime argument.");
        StringBuilder actual = new(), redacted = new(); int position = 0;
        while (position < template.Length) {
            int start = template.IndexOf('$', position);
            if (start < 0) { Literal(template[position..]); break; }
            Literal(template[position..start]);
            string? key = null; int end = start;
            if (start + 1 < template.Length && template[start + 1] == '{') {
                end = template.IndexOf('}', start + 2);
                if (end < 0) Fail("MMC_RUNTIME_VARIABLE", "A required launch variable is unterminated.");
                key = template[(start + 2)..end]; end++;
            } else foreach (string bare in new[] { "INST_MC_DIR", "INST_DIR" }) {
                int stop = start + bare.Length + 1;
                if (stop <= template.Length && template.AsSpan(start + 1, bare.Length).SequenceEqual(bare) && (stop == template.Length || !char.IsAsciiLetterOrDigit(template[stop]) && template[stop] != '_')) { key = bare; end = stop; break; }
            }
            if (key is null) { Literal("$"); position = start + 1; continue; }
            if (!variables.TryGetValue(key, out var value)) Fail("MMC_RUNTIME_VARIABLE", "A required launch variable is unknown or has no live value.");
            actual.Append(value.Actual); redacted.Append(value.Redacted); position = end;
        }
        string result = actual.ToString(), safe = redacted.ToString();
        if (result.Contains('\0')) Fail("MMC_RUNTIME_ARGUMENT", "NUL in expanded runtime argument.");
        return new(result, safe);
        void Literal(string value) {
            actual.Append(value);
            foreach (string secret in Secrets(account).Where(s => s.Length >= 4).OrderByDescending(s => s.Length)) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            redacted.Append(value);
        }
    }
    private static void ValidateJvm(IReadOnlyList<string> tokens) {
        bool operand = false;
        foreach (string token in tokens) {
            if (token.StartsWith('@')) Fail("MMC_RUNTIME_UNBOUND_INPUT", "Unbound JVM argument files are unsupported even as option operands.");
            if (operand) { operand = false; continue; }
            if (token.Length == 0 || !token.StartsWith('-') || token.StartsWith('@') || token.StartsWith("-javaagent", StringComparison.Ordinal) || token.StartsWith("-agent", StringComparison.Ordinal) || token.StartsWith("-Xrun", StringComparison.Ordinal) || token.StartsWith("-Xbootclasspath", StringComparison.Ordinal) ||
                token is "-jar" or "-m" or "--module" or "-p" or "-cp" or "-classpath" or "--class-path" || new[] { "--class-path=", "--module=", "--module-path", "--upgrade-module-path", "--patch-module", "-Djava.class.path=", "-Dsun.boot.class.path=", "-Djava.ext.dirs=", "-Djava.endorsed.dirs=", "-Djdk.module.path=" }.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
                Fail("MMC_RUNTIME_UNBOUND_INPUT", "JVM execution/agent/classpath inputs require a separate owned binding; raw launch-mode replacement is unsupported.");
            operand = PairJvmOptions.Contains(token);
        }
        if (operand) Fail("MMC_RUNTIME_ARGUMENT", "A JVM option is missing its required single argument token.");
    }
    private static void ValidateExtraGame(ImmutableArray<string> tokens) {
        // Additive tweakers remain tokens and join RequiredClasses below; the host must
        // inspect their presence/absence in every locked classpath JAR before launch.
        foreach (string token in tokens) if (new[] { "--username", "--uuid", "--accessToken", "--session", "--userType", "--userProperties", "--gameDir", "--assetsDir", "--assetIndex" }.Any(flag => token == flag || token.StartsWith(flag + "=", StringComparison.Ordinal)))
            Fail("MMC_RUNTIME_GAME_OVERRIDE", "Extra game arguments cannot replace account or instance-root bindings.");
    }
    private static void ValidateBoundGameFlags(List<Value> game, Dictionary<string, Value> variables) {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal) { ["--username"] = "auth_player_name", ["--uuid"] = "auth_uuid", ["--accessToken"] = "auth_access_token", ["--session"] = "auth_session", ["--userType"] = "user_type", ["--userProperties"] = "user_properties", ["--gameDir"] = "game_directory", ["--assetsDir"] = "assets_root", ["--assetIndex"] = "assets_index_name" };
        for (int i = 0; i < game.Count; i++) {
            int split = game[i].Actual.IndexOf('='); string flag = split < 0 ? game[i].Actual : game[i].Actual[..split];
            if (!flags.TryGetValue(flag, out string? variable)) continue;
            string value;
            if (split >= 0) value = game[i].Actual[(split + 1)..];
            else { if (++i == game.Count) Fail("MMC_RUNTIME_GAME_OVERRIDE", "Missing approved game/account option value."); value = game[i].Actual; }
            if (value != variables[variable].Actual) Fail("MMC_RUNTIME_GAME_OVERRIDE", "Declared account/game-root argument differs from its live approved binding.");
        }
    }
    private static ImmutableArray<string> RequiredClasses(string main, IEnumerable<string> jvm, IEnumerable<string> game) {
        try{return MmcLaunchClassRequirements.Collect(main,jvm,game);}
        catch(FormatException error){throw new MmcRuntimeLaunchException("MMC_RUNTIME_CLASS",error.Message);}
    }
    private static Dictionary<string, string> Properties(IEnumerable<string> jvm) {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        foreach (string token in jvm.Where(t => t.StartsWith("-D", StringComparison.Ordinal))) { int split = token.IndexOf('=', 2); string key = split < 0 ? token[2..] : token[2..split]; if (key.Length == 0) Fail("MMC_RUNTIME_ARGUMENT", "Empty JVM property name."); properties[key] = split < 0 ? "" : token[(split + 1)..]; }
        return properties;
    }
    private static void ValidateWorkingDirectory(IEnumerable<string> jvm, string gameRoot) {
        foreach (string token in jvm.Where(t => t == "-Duser.dir" || t.StartsWith("-Duser.dir=", StringComparison.Ordinal))) {
            string value = token == "-Duser.dir" ? "" : token["-Duser.dir=".Length..];
            try {
                if (Path.IsPathFullyQualified(value) && Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)), MmcRuntimeFiles.PathComparison)) continue;
            } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
            Fail("MMC_RUNTIME_WORKING_DIRECTORY", "JVM user.dir must be the bound GameRoot; relative classpath inputs cannot use another working directory.");
        }
    }
    private static MmcRuntimeMutableDirectories DescribeMutablePaths(MmcInstancePaths paths, MmcRuntimeLaunchRequest request) {
        var selected=MmcSessionRuntimeDirectories.Describe(paths,request.Definition.InstanceId,request.Settings.SessionId,request.BoundPlan.BoundFingerprint,request.RuntimeAuthority,containsAuthenticationAgent:request.Authentication is not null);
        if(Directory.Exists(selected.SessionRoot)||File.Exists(selected.SessionRoot))Fail("MMC_RUNTIME_SESSION_PATH","A launch session requires a fresh owned mutable directory.");
        return new(selected.SessionRoot,selected.TemporaryDirectory,selected.ExtractDirectory){RuntimeBinding=selected.RuntimeBinding};
    }
    private static void InitializeMutablePaths(MmcRuntimeMutableDirectories mutable, MmcRuntimeLaunchRequest request) {
        MmcSessionRuntimeDirectories.CreateOwned(request.Paths,request.Definition.InstanceId,request.Settings.SessionId,mutable.RuntimeBinding,request.RuntimeAuthority);
    }
    private static void ValidateMutableOwner(MmcRuntimeLaunchPlan launch) {
        ValidateMutableOwner(launch.Request, launch.MutableDirectories);
    }
    private static void ValidateMutableOwner(MmcRuntimeLaunchRequest request, MmcRuntimeMutableDirectories mutable) {
        var expected=MmcSessionRuntimeDirectories.Paths(request.Paths,request.Definition.InstanceId,request.Settings.SessionId,mutable.RuntimeBinding,request.RuntimeAuthority);
        MmcRuntimeFiles.SamePath(mutable.SessionRoot,expected.SessionRoot);MmcRuntimeFiles.SamePath(mutable.TemporaryDirectory,expected.TemporaryDirectory);MmcRuntimeFiles.SamePath(mutable.ExtractDirectory,expected.ExtractDirectory);
        MmcSessionRuntimeDirectories.ValidateLaunch(request.Paths,request.Definition.InstanceId,request.Settings.SessionId,mutable.RuntimeBinding,request.RuntimeAuthority);
    }
    private static void ValidateGameAssets(MmcRuntimeLaunchRequest request, string view) {
        string actual = Path.GetFullPath(request.Directories.GameAssetsRoot);
        string root = Path.Combine(view, "assets"), virtualRoot = Path.Combine(root, "virtual", request.BoundPlan.CompiledProfile.Assets.Id), resources = Path.Combine(request.Paths.GameRoot, "resources");
        if (!new[] { root, virtualRoot, resources }.Any(expected => actual.Equals(Path.GetFullPath(expected), MmcRuntimeFiles.PathComparison))) Fail("MMC_RUNTIME_PATH", "Game assets are not an approved owned asset/virtual/resource root.");
        MmcInstancePaths.EnsureNoLinks(actual);
    }
    private static IEnumerable<string> Secrets(MmcRuntimeAccountSnapshot account) => new[] { account.AccessToken, account.Session, account.UserPropertiesJson, account.ClientToken, account.Xuid }.OfType<string>().Where(v => v.Length != 0);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new MmcRuntimeLaunchException(code, message);
}
