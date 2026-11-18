namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed partial class MmcInstanceLifecycleBackend {
    /// <summary>Called at IntentPrepared, before the original producer's checkpoint can fail.</summary>
    public static MmcInstanceRelocationRecovery CaptureRelocationRecovery(MmcInstanceRelocationRequest selection, string operationDirectory) {
        ArgumentNullException.ThrowIfNull(selection);
        var journal = MmcInstanceLifecycleJournal.Read(operationDirectory);
        ValidateRelocationRecovery(selection, journal);
        return new(journal.DirectoryPath, journal.IntentSha256, selection);
    }

    /// <summary>One exact replay after the initiating producer and all of its leases have returned.</summary>
    public Task<MmcInstanceLifecycleResult> CompleteRelocationFailureAsync(MmcInstanceRelocationRecovery recovery) => Task.Run(() => {
        ArgumentNullException.ThrowIfNull(recovery);
        try {
            string intentPath = Path.Combine(recovery.OperationDirectory, "intent.record");
            MmcInstancePaths.EnsureNoLinks(intentPath);
            // Keep only the immutable external intent pinned. Instance pins would obstruct
            // the existing guarded Move/ClearPending recovery implementation.
            using var pin = new FileStream(intentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var journal = MmcInstanceLifecycleJournal.Read(recovery.OperationDirectory);
            ValidateRelocationRecovery(recovery.Selection, journal);
            if (journal.IntentSha256 != recovery.IntentSha256) throw new IOException("本次移动或重命名的操作记录已变化，已停止自动收尾。");
            foreach (string location in new[] { journal.Intent.SourceRoot, journal.Intent.TargetRoot }) {
                string marker = MmcInstancePaths.Resolve(location).ResolveRelative(MmcPathRole.Instance, MmcInstanceLifecycleJournal.MarkerRelative);
                if (MmcInstancePaths.Attributes(marker) is not null && !journal.Owns(location))
                    throw new IOException("实例已由其他操作接管，已保留现场并停止自动收尾。");
            }
            return Recover(journal.DirectoryPath);
        } catch (Exception error) when (Failure(error)) {
            return new MmcInstanceLifecycleResult(MmcInstanceBackendStatus.NeedsRecovery, recovery.OperationDirectory,
                recovery.Selection.SourceRoot, Failure: "无法核实本次操作的自动收尾权限，现场已保留。" + error.Message);
        }
    }, CancellationToken.None);

    private static void ValidateRelocationRecovery(MmcInstanceRelocationRequest selection, MmcInstanceLifecycleJournal journal) {
        var intent = journal.Intent;
        if (selection.Kind is not (MmcInstanceRelocationKind.Move or MmcInstanceRelocationKind.Rename) || intent.Kind != selection.Kind.ToString() ||
            intent.SourceRoot != Canonical(selection.SourceRoot) || intent.TargetRoot != Canonical(selection.TargetRoot) ||
            intent.InstanceId != selection.ExpectedInstanceId || intent.SourceGeneration != selection.ExpectedGeneration ||
            intent.TargetGeneration != checked(selection.ExpectedGeneration + 1) || intent.SourcePrimarySha256 != selection.ExpectedPrimarySha256)
            throw new IOException("自动收尾记录与本次固定实例、路径、代数或来源主定义不一致。");
    }
}
