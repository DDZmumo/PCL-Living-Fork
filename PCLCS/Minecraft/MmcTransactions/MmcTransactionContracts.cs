using System;
using System.Collections.Generic;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <param name="ExpectedOldSha256">Null means the target must be absent.</param>
/// <param name="NewContent">In-memory payload. Mutually exclusive with NewSourceRelativePath.</param>
/// <param name="NewSourceRelativePath">Owned instance-relative PCL/MmcRuntime/.staging/GUID-N/... source, copied as a stream. Both new-source fields null means recoverable deletion.</param>
public sealed record MmcFileMutation(string RelativePath, string? ExpectedOldSha256, byte[]? NewContent, string? ExpectedNewSha256,
    string? NewSourceRelativePath = null);
public sealed record MmcFileTransactionRequest(Guid InstanceId, long ExpectedGeneration, long NewGeneration, IReadOnlyList<MmcFileMutation> Mutations) {
    internal Action<MmcTransactionProducedFile>? ProducedRecoveryFile { get; init; }
}
internal sealed record MmcTransactionProducedFile(Guid TransactionId, Guid InstanceId, string CanonicalRoot,
    long ExpectedGeneration, long NewGeneration, int MutationIndex, string RelativePath, string Sha256);
public enum MmcTransactionOutcome { Committed, Aborted, RecoveryRequired, Interrupted }
public enum MmcTransactionCheckpoint { Prepared, BeforeMutation, AfterMutation, BeforeDecision, CommitDecided, BeforeFinalize, Finalized }
public sealed record MmcTransactionResult(MmcTransactionOutcome Outcome, Guid TransactionId, string? Diagnostic = null);
/// <summary>Only for explicit controlled interruption tests; leaves the durable transaction pending.</summary>
public sealed class MmcTransactionInterruptionException(string message) : Exception(message);

internal sealed record MmcJournalMutation(string RelativePath, string? OldSha256, string? NewSha256);
internal sealed record MmcPreparedJournal(int SchemaVersion, Guid TransactionId, Guid InstanceId, string CanonicalRoot,
    long ExpectedGeneration, long NewGeneration, MmcJournalMutation[] Mutations);
internal sealed record MmcTransactionMarker(int SchemaVersion, Guid TransactionId, Guid InstanceId, string PreparedSha256, string Phase);
