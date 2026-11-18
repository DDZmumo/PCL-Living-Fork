using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcLegacyAdoptionIntent { StrictVerified, CreateCopyWithExplicitSourceRebinding }
public enum MmcLegacyAdoptionFileRole { Game, SourceEvidence, HostBackupOnly, LocalLibrary, InstanceExtra }
public enum MmcLegacyAdoptionActivity { Idle, Active, Unknown }
public enum MmcLegacyAdoptionCheckpoint { BackupDurable, PrivateImportComplete, PersonalSnapshotApplied, BeforeDecision, CommitDecided, DirectoryMoved, Completed }
public sealed record MmcLegacyAdoptionFile(string RelativePath, bool IsDirectory, long SizeBytes, string? Sha256,
    string? Sha1, long LastWriteUtcTicks, MmcLegacyAdoptionFileRole Role, string? GameRelativePath = null, uint Crc32 = 0);
public sealed record MmcLegacyAdoptionInventory(string SourceRoot, string GameRoot, string SourceName,
    ImmutableArray<MmcLegacyAdoptionFile> Files, string Fingerprint, string? VersionJsonSha256, string? ConfigSha256, string? IniSha256) {
    public int SchemaVersion { get; init; } = 1;
}
public sealed record MmcLegacyAdoptionDifference(string RelativePath, string Kind, long CurrentBytes, long AuthorBytes);
public sealed record MmcLegacyAdoptionSourceCheck(bool HistoricalSourceVerified, string Explanation,
    string OldComponents, string NewComponents, ImmutableArray<MmcLegacyAdoptionDifference> Differences);
public sealed record MmcLegacyAdoptionPreviewRequest(string SourceRoot, string GameRoot, string OriginalArchivePath, MmcRuntimeEnvironment Environment);
public sealed record MmcLegacyAdoptionCopyRequest(string TargetRoot, string InstanceName, string ConfirmedPreviewFingerprint,
    MmcLegacyAdoptionIntent Intent = MmcLegacyAdoptionIntent.StrictVerified) {
    public Action<MmcLegacyAdoptionCheckpoint, string>? Checkpoint { get; init; }
    public Action<MmcInstancePurgeCheckpoint, string?>? AutomaticCleanupCheckpoint { get; init; }
}
public sealed record MmcLegacyAdoptionProgress(string Phase, long CompletedBytes = 0, long TotalBytes = 0);
public sealed record MmcLegacyAdoptionResult(MmcInstanceBackendStatus Status, MmcInstanceDefinition? Definition = null,
    MmcInstancePaths? Paths = null, string? OperationDirectory = null, string? BackupPath = null, string? Failure = null);

/// <summary>Host source coordination; Unknown never means idle. This contract carries no accounts, global stores or mutable launcher models.</summary>
public sealed class MmcLegacyAdoptionHost {
    public required Func<string, string, MmcLegacyAdoptionActivity> ObserveActivity { get; init; }
    public required Func<string, IDisposable> AcquireLegacyPathLock { get; init; }
}

/// <summary>The caller owns one hash-bound ZIP handle from preview through copy. Never contains an adopted old identity.</summary>
public sealed class MmcLegacyAdoptionPreview : IDisposable {
    internal MmcArchiveSource Source { get; }
    internal MmcResolvedSourceBundle Resolved { get; }
    internal MmcCompiledProfile Compiled { get; }
    internal ImmutableArray<MmcLegacyAdoptionFile> AuthorFiles { get; }
    internal string ConfigJson { get; }
    internal string Ini { get; }
    private bool disposed;
    public MmcLegacyAdoptionInventory Inventory { get; }
    public MmcLegacyAdoptionSourceCheck SourceCheck { get; }
    public string PackageSha256 => Source.PackageSha256;
    public string PreviewFingerprint { get; }
    public MmcRuntimeEnvironment Environment => Compiled.Environment;
    internal MmcLegacyAdoptionPreview(MmcArchiveSource source, MmcResolvedSourceBundle resolved, MmcCompiledProfile compiled,
        MmcLegacyAdoptionInventory inventory, ImmutableArray<MmcLegacyAdoptionFile> author, MmcLegacyAdoptionSourceCheck check,
        string json, string ini, string fingerprint) {
        Source = source; Resolved = resolved; Compiled = compiled; Inventory = inventory; AuthorFiles = author; SourceCheck = check;
        ConfigJson = json; Ini = ini; PreviewFingerprint = fingerprint;
    }
    internal void AssertOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    /// <summary>The confirmation is specific to the displayed intent and destination; a strict confirmation cannot silently become rebinding.</summary>
    public string ConfirmationFingerprint(string targetRoot, string instanceName, MmcLegacyAdoptionIntent intent = MmcLegacyAdoptionIntent.StrictVerified) {
        AssertOpen();
        if (!Enum.IsDefined(intent)) throw new ArgumentException("Unknown adoption intent.", nameof(intent));
        string target = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(targetRoot));
        return MmcDurableFiles.Hash(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[] {
            "mmc-legacy-copy-confirmation-v1", PreviewFingerprint, target, instanceName, intent.ToString()
        })));
    }
    public void Dispose() { if (disposed) return; disposed = true; Source.Dispose(); }
}
