using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public enum MmcRuntimeWrapperMode { JavaWrapper, DirectMain }
public sealed record MmcRuntimeArtifactTarget(string ArtifactKey, string TargetRelativePath, string ActualPath);
public sealed record MmcRuntimeSupportArtifact(string ActualPath, string Sha256, long SizeBytes);
public sealed record MmcRuntimeJavaSnapshot(string ExecutablePath, string ExecutableSha256, string IdentitySha256,
    int Major, string Architecture, int Bitness);
public sealed record MmcRuntimeDirectorySnapshot(string ViewKey, string NativeDirectory, string AssetsRoot, string GameAssetsRoot);
public sealed record MmcRuntimeSettingsSnapshot(string DisplayName, string LauncherName, string LauncherVersion,
    int MinimumMemoryMiB, int MaximumMemoryMiB, int ResolutionWidth, int ResolutionHeight, Guid SessionId) {
    public MmcRuntimeWrapperMode WrapperMode { get; init; } = MmcRuntimeWrapperMode.JavaWrapper;
    public ImmutableArray<string> UserJvmTokens { get; init; } = [];
    public ImmutableArray<string> ExtraGameTokens { get; init; } = [];
    public ImmutableArray<MmcJavaConstraint> PlatformJavaConstraints { get; init; } = [];
    public string StreamEncodingName { get; init; } = "UTF-8";
    public bool AppendResolutionArguments { get; init; } = true;
    public bool Fullscreen { get; init; }
    /// <summary>Ordered active package environment mapping; empty inherits the launcher's base process environment.</summary>
    public ImmutableArray<MmcEnvironmentVariable> EnvironmentOverride { get; init; } = [];
    /// <summary>
    /// Active package <c>WrapperCommand</c> text, or null when no wrapper is declared. This is the package
    /// wrapper capability, which composes with (and never replaces) <see cref="WrapperMode"/>: the package
    /// wrapper is the outer entry and the inner Java invocation keeps its JavaWrapper/DirectMain shape.
    /// </summary>
    public string? WrapperCommand { get; init; }
    /// <summary>Whether the package wrapper entry must be registered as a second managed process identity.</summary>
    public bool HasWrapperCommand => !string.IsNullOrWhiteSpace(WrapperCommand);
}

/// <summary>Only live launch values; intentionally redacted ToString and no serializer/persistence API.</summary>
public sealed class MmcRuntimeAccountSnapshot {
    public string Name { get; }
    public string Uuid { get; }
    public string AccessToken { get; }
    public string Session { get; }
    public string UserType { get; }
    public string UserPropertiesJson { get; }
    public string? ClientToken { get; }
    public string? ClientId { get; }
    public string? Xuid { get; }
    public ImmutableArray<MmcJavaConstraint> JavaConstraints { get; }
    public MmcRuntimeAccountSnapshot(string name, string uuid, string accessToken, string session, string userType,
        string userPropertiesJson, string? clientToken = null, string? clientId = null, string? xuid = null,
        ImmutableArray<MmcJavaConstraint> javaConstraints = default) {
        Name = name; Uuid = uuid; AccessToken = accessToken; Session = session; UserType = userType;
        UserPropertiesJson = userPropertiesJson; ClientToken = clientToken; ClientId = clientId; Xuid = xuid;
        JavaConstraints = javaConstraints.IsDefault ? [] : javaConstraints;
    }
    public override string ToString() => "MMC runtime account snapshot [redacted]";
}
public sealed record MmcRuntimeLaunchRequest(MmcInstanceDefinition Definition, MmcBoundLaunchPlan BoundPlan,
    MmcInstancePaths Paths, ImmutableArray<MmcRuntimeArtifactTarget> Targets, MmcRuntimeJavaSnapshot Java,
    MmcRuntimeSettingsSnapshot Settings, MmcRuntimeAccountSnapshot Account, MmcRuntimeDirectorySnapshot Directories,
    MmcRuntimeSupportArtifact? Wrapper = null, MmcRuntimeSupportArtifact? Bridge = null) {
    [System.Text.Json.Serialization.JsonIgnore]
    public MmcMutableRuntimeAuthority? RuntimeAuthority { get; init; }
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public MmcRuntimeAuthenticationSnapshot? Authentication { get; init; }
}
public sealed record MmcRuntimeInputObservation(string Identity, string ActualPath, string Sha256, long SizeBytes);
public sealed record MmcRuntimeMutableDirectories(string SessionRoot, string TemporaryDirectory, string ExtractDirectory) {
    public MmcSessionRuntimeBinding RuntimeBinding { get; init; } = null!;
}

/// <summary>No Start method: the existing instance/session admission service must revalidate and create the process.</summary>
public sealed class MmcRuntimeLaunchPlan {
    private readonly ImmutableArray<string> arguments;
    private readonly ImmutableArray<string> secrets;
    private readonly string encodingName;
    public MmcRuntimeLaunchRequest Request { get; }
    public ImmutableArray<string> RedactedArguments { get; }
    public ImmutableArray<string> RedactedLogicalGameArguments { get; }
    public ImmutableArray<string> LogicalJvmArguments { get; }
    // Live values may contain credentials. Inspection uses them internally;
    // they are never public/log-safe fields or persistence/fingerprint inputs.
    internal ImmutableArray<string> EffectiveJvmArguments { get; }
    public ImmutableArray<string> NonSensitiveAdditionalJvmArguments { get; }
    public ImmutableArray<string> RequiredClassNames { get; }
    public ImmutableArray<string> OrderedGameClasspath { get; }
    public ImmutableArray<MmcRuntimeInputObservation> Inputs { get; }
    public MmcRuntimeMutableDirectories MutableDirectories { get; }
    public MmcSessionRuntimeBinding RuntimeBinding=>MutableDirectories.RuntimeBinding;
    public string NonSensitiveArgumentFingerprint { get; }
    public string RuntimeInputFingerprint { get; }
    internal MmcRuntimeLaunchPlan(MmcRuntimeLaunchRequest request, ImmutableArray<string> arguments,
        ImmutableArray<string> redactedArguments, ImmutableArray<string> redactedGameArguments, ImmutableArray<string> logicalJvmArguments,
        ImmutableArray<string> additionalJvm, ImmutableArray<string> requiredClasses, ImmutableArray<string> classpath, ImmutableArray<MmcRuntimeInputObservation> inputs, MmcRuntimeMutableDirectories mutable,
        string argumentFingerprint, string inputFingerprint, ImmutableArray<string> secrets, ImmutableArray<string> effectiveJvmArguments) {
        Request = request; this.arguments = arguments; RedactedArguments = redactedArguments;
        RedactedLogicalGameArguments = redactedGameArguments; LogicalJvmArguments = logicalJvmArguments;
        NonSensitiveAdditionalJvmArguments = additionalJvm; RequiredClassNames = requiredClasses;
        OrderedGameClasspath = classpath; Inputs = inputs; MutableDirectories = mutable;
        NonSensitiveArgumentFingerprint = argumentFingerprint; RuntimeInputFingerprint = inputFingerprint;
        this.secrets = secrets; encodingName = request.Settings.StreamEncodingName;
        EffectiveJvmArguments = effectiveJvmArguments;
    }
    public ProcessStartInfo CreateStartInfo() {
        string? wrapper = Request.Settings.WrapperCommand;
        // The un-wrapped line must fit even when a wrapper is declared, because the wrapper re-emits this exact
        // argv as its child arguments: the inner line is a real command line of its own.
        if (OperatingSystem.IsWindows()) MmcRuntimeWindowsCommandLine.Validate(Request.Java.ExecutablePath, arguments);
        var start = new ProcessStartInfo(Request.Java.ExecutablePath) {
            UseShellExecute = false, WorkingDirectory = Request.Paths.GameRoot, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(encodingName), StandardErrorEncoding = Encoding.GetEncoding(encodingName)
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        MmcRuntimeEnvironmentOverlay.Apply(start.Environment, Request.Settings.EnvironmentOverride, MutableDirectories.TemporaryDirectory);
        // The wrapper composes with the Java-wrapper mode already encoded in `arguments`: it becomes the entry,
        // and the inner Java invocation (JavaWrapper or DirectMain, unchanged) becomes its child.
        if (wrapper is null) return start;
        ProcessStartInfo wrapped = MmcRuntimeWrapperCommand.Apply(start, wrapper, MmcRuntimeWrapperCommand.Variables(this));
        // F-07: the wrapper line, not only the inner line, must fit the Windows command line budget.
        if (OperatingSystem.IsWindows()) MmcRuntimeWindowsCommandLine.Validate(wrapped.FileName, wrapped.ArgumentList);
        return wrapped;
    }
    public string Redact(string text) {
        foreach (string secret in secrets.Where(s => s.Length >= 4).OrderByDescending(s => s.Length)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
    public override string ToString() => "MMC runtime launch plan [arguments redacted], input " + RuntimeInputFingerprint;
}
