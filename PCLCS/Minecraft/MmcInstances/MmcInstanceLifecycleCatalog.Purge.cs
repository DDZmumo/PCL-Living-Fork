namespace PCLCS;

// Original LF host integration; repository LICENCE applies.
public static partial class MmcInstanceLifecycleCatalog {
    /// <summary>Revalidates a catalog selection before deriving the guarded purge request from its pinned owner record.</summary>
    public static Task<MmcInstancePurgePreview> PreviewPurgeAsync(MmcLifecycleCatalogEntry preview, CancellationToken cancellationToken = default) => Task.Run(() => {
        ArgumentNullException.ThrowIfNull(preview);
        string root = Root(preview.MinecraftRoot), directory = ValidateDirectory(root, preview.OperationDirectory);
        using var pin = Pin(Path.Combine(directory, "intent.record"), MaximumRecordBytes, out _);
        long remaining = MaximumScanBytes;
        var fresh = Inspect(root, directory, ref remaining, cancellationToken);
        if (!fresh.CanPurge || preview.SnapshotFingerprint is null || fresh.SnapshotFingerprint != preview.SnapshotFingerprint ||
            preview.InstanceId != fresh.InstanceId || preview.Generation != fresh.Generation)
            throw new IOException("隔离记录或实例已变化，请重新打开 MMC 隔离区与恢复列表。");
        cancellationToken.ThrowIfCancellationRequested();
        var intent = MmcInstanceLifecycleJournal.Read(directory).Intent;
        return new MmcInstanceLifecycleBackend().PreviewPurgeAsync(new(directory, intent.InstanceId, intent.SourceGeneration,
            intent.SourcePrimarySha256), cancellationToken).GetAwaiter().GetResult();
    }, CancellationToken.None);
}
