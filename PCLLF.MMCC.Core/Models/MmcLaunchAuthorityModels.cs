// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

public enum MmcRetiredHeuristicKind { BuiltinMinecraftJavaInference, BuiltinForgeJavaInference }
public sealed record MmcLaunchAuthorityTarget(string ComponentUid, string SourceRevision, MmcRetiredHeuristicKind Heuristic);
public sealed record MmcJarClassPresence(string ClassName, bool Present, string? EntryPath = null);
/// <summary>The host probes one already-locked JAR; absence is explicit, not an omitted observation.</summary>
public sealed record MmcJarClassObservation(string ArtifactKey, string ArtifactSha256, ImmutableArray<MmcJarClassPresence> Classes);
/// <summary>Non-sensitive host launch-chain identities; no account, path or complete command is persisted in the proof.</summary>
public sealed record MmcHostLaunchChain(string JavaIdentitySha256, string WrapperIdentitySha256,
    string NonSensitiveArgumentFingerprint) {
    /// <summary>Host/user JVM tokens in their actual position before the component JVM tokens.</summary>
    public ImmutableArray<string> AdditionalJvmArguments { get; init; } = [];
    public ImmutableArray<string> AdditionalRequiredClasses { get; init; } = [];
}
public sealed record MmcClasspathClassBinding(string ClassName, string ArtifactKey, string ArtifactSha256, string EntryPath, int ClasspathPosition);
/// <summary>Structural authority to retire only named old heuristics; never a process-admission or Java-compatibility certificate.</summary>
public sealed record MmcLaunchAuthorityProof(Guid InstanceId, long DefinitionGeneration, string BoundFingerprint,
    string ProfileFingerprint, string RuntimePlanFingerprint, string JavaIdentitySha256, string WrapperIdentitySha256,
    string HostArgumentFingerprint, ImmutableArray<MmcLaunchAuthorityTarget> Targets,
    ImmutableArray<MmcClasspathClassBinding> ClassBindings, string ProofFingerprint);
public sealed record MmcLaunchAuthorityResult(MmcLaunchAuthorityProof? Proof, ImmutableArray<MmcDiagnostic> Diagnostics) {
    public bool HasAuthority => Proof is not null && !Proof.Targets.IsEmpty;
    public bool Retires(string componentUid, MmcRetiredHeuristicKind heuristic) => Proof?.Targets.Any(t => t.ComponentUid == componentUid && t.Heuristic == heuristic) == true;
}
