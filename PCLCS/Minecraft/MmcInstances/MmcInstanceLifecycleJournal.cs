using System.Text;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal sealed record MmcLifecycleIntent(int SchemaVersion, Guid OperationId, string Kind, Guid InstanceId, string SourceRoot, string TargetRoot,
    long SourceGeneration, long TargetGeneration, string SourcePrimarySha256, string? TargetPrimarySha256, string GameRootLeaf) {
    public string? WorkspaceRelativePath { get; init; }
}
internal sealed record MmcLifecycleMarker(int SchemaVersion, Guid OperationId, Guid InstanceId, string OperationDirectory, string IntentSha256);
internal sealed record MmcLifecycleTxLink(Guid TransactionId, string PreparedSha256);
internal sealed record MmcLifecycleDecision(int SchemaVersion, Guid OperationId, string IntentSha256, string Kind);
internal sealed record MmcLifecycleCompletion(int SchemaVersion, Guid OperationId, string IntentSha256, string Disposition);
internal sealed record MmcLifecycleRestore(int SchemaVersion,Guid RestoreId,Guid DeleteOperationId,string DeleteIntentSha256);

internal sealed class MmcInstanceLifecycleJournal {
    internal const string MarkerRelative = "PCL/MmcLifecyclePending.json";
    internal string DirectoryPath { get; }
    internal MmcLifecycleIntent Intent { get; }
    internal string IntentSha256 => intentHash;
    private readonly string intentHash;
    private readonly byte[] marker;
    private MmcInstanceLifecycleJournal(string directory, MmcLifecycleIntent intent, string hash) {
        DirectoryPath = directory; Intent = intent; intentHash = hash;
        marker = JsonSerializer.SerializeToUtf8Bytes(new MmcLifecycleMarker(1,intent.OperationId,intent.InstanceId,directory,hash));
    }
    internal static MmcInstanceLifecycleJournal Create(MmcLifecycleIntent intent, MmcAdmissionLease sourceLease) {
        Validate(intent);
        sourceLease.AssertHeld();
        if(sourceLease.VersionRoot!=intent.SourceRoot || sourceLease.InstanceId!=intent.InstanceId) throw new IOException("Lifecycle intent does not own its source admission.");
        MmcFileTransactions.AssertNoPending(sourceLease); MmcSessions.AssertNoActive(sourceLease);
        var primary=MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(intent.SourceRoot));
        var management=MmcManagementProbe.Read(intent.SourceRoot);
        if(primary.Sha256!=intent.SourcePrimarySha256 || primary.Definition.InstanceId!=intent.InstanceId || primary.Definition.Generation!=intent.SourceGeneration ||
            management.State!=MmcManagementState.ManagedMmc || management.Envelope?.InstanceId!=intent.InstanceId || management.Envelope.Generation!=intent.SourceGeneration)
            throw new IOException("Lifecycle intent has no exact published source authority.");
        string parent = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(intent.SourceRoot)!)!,"PCL","MmcLifecycle");
        MmcArtifactIo.Directory(parent); string directory = Path.Combine(parent,intent.OperationId.ToString("N"));
        MmcOwnedImportReservation.CreateExclusiveDirectory(directory);
        return new(directory,intent,MmcDurableFiles.WriteRecord(Path.Combine(directory,"intent.record"),intent));
    }
    internal static MmcInstanceLifecycleJournal Read(string directory, CancellationToken cancellationToken = default) {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); MmcInstancePaths.EnsureNoLinks(directory);
        var (intent, hash) = MmcDurableFiles.ReadRecord<MmcLifecycleIntent>(Path.Combine(directory,"intent.record"), cancellationToken); Validate(intent);
        string expected = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(intent.SourceRoot)!)!,"PCL","MmcLifecycle",intent.OperationId.ToString("N"));
        if (!string.Equals(expected,directory,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)) throw new IOException("Lifecycle journal escaped its source Minecraft operation store.");
        return new(directory,intent,hash);
    }
    private static void Validate(MmcLifecycleIntent intent) {
        if(intent.SchemaVersion!=1 || intent.OperationId==Guid.Empty || intent.InstanceId==Guid.Empty || intent.Kind is not ("Rename" or "Move" or "Delete") ||
            intent.SourceGeneration<1 || !MmcDurableFiles.IsHash(intent.SourcePrimarySha256) || intent.GameRootLeaf is not ("minecraft" or ".minecraft")) throw new IOException("Unknown or damaged lifecycle intent.");
        MmcInstanceCloneReservation.ValidateRoots(intent.SourceRoot,intent.TargetRoot);
        if(intent.WorkspaceRelativePath is { } workspace){string[] parts=workspace.Split('/');if(parts.Length!=4||parts[0]!="PCL"||parts[1]!="MmcRuntime"||parts[2]!=".staging"||!Guid.TryParseExact(parts[3],"N",out Guid id)||id==Guid.Empty)throw new IOException("Lifecycle workspace pointer is invalid.");}
        if(intent.Kind=="Delete" ? intent.TargetGeneration!=intent.SourceGeneration || intent.TargetPrimarySha256 is not null :
            intent.TargetGeneration!=checked(intent.SourceGeneration+1) || !MmcDurableFiles.IsHash(intent.TargetPrimarySha256)) throw new IOException("Lifecycle generation/decision shape is invalid.");
    }
    internal void MarkPending(string location) {
        string path=MmcInstancePaths.Resolve(location).ResolveRelative(MmcPathRole.Instance,MarkerRelative);
        if(MmcInstancePaths.Attributes(path) is null) MmcDurableFiles.WriteNew(path,marker);
        else if(MmcDurableFiles.Observe(path)!=MmcDurableFiles.Hash(marker)) throw new IOException("A different outer operation already owns this instance.");
    }
    internal void ClearPending(string location) {
        string path=MmcInstancePaths.Resolve(location).ResolveRelative(MmcPathRole.Instance,MarkerRelative);
        if(MmcInstancePaths.Attributes(path) is null) return;
        if(MmcDurableFiles.Observe(path)!=MmcDurableFiles.Hash(marker)) throw new IOException("Lifecycle marker changed; preserving it for recovery.");
        File.Delete(path);
    }
    internal bool Owns(string location) {
        if(!Directory.Exists(location)) return false;
        try { return MmcDurableFiles.Observe(MmcInstancePaths.Resolve(location).ResolveRelative(MmcPathRole.Instance,MarkerRelative))==MmcDurableFiles.Hash(marker); }
        catch(IOException) { return false; }
    }
    internal string ValidatePendingPermit(MmcAdmissionLease lease) {
        lease.AssertHeld();
        if(lease.InstanceId!=Intent.InstanceId || lease.VersionRoot!=Intent.SourceRoot && lease.VersionRoot!=Intent.TargetRoot) throw new IOException("Lifecycle owner permit belongs to another lease/root.");
        var reread=Read(DirectoryPath);
        if(reread.intentHash!=intentHash || !Owns(lease.VersionRoot)) throw new IOException("Lifecycle owner facts or exact marker changed.");
        string? primary=MmcDurableFiles.Observe(MmcInstancePaths.Resolve(lease.VersionRoot).ResolveRelative(MmcPathRole.Instance,"PCL/InstanceDefinition.json"));
        if(primary!=Intent.SourcePrimarySha256 && primary!=Intent.TargetPrimarySha256 && FindTransaction(lease.VersionRoot) is null)
            throw new IOException("Lifecycle permit has neither expected primary bytes nor an exact recoverable inner journal.");
        return MmcDurableFiles.Hash(marker);
    }
    internal MmcPendingOperationPermit Enter(MmcAdmissionLease lease)=>MmcPendingOperationPermit.ForLifecycle(lease,this);
    internal void RecoverWorkspace(MmcAdmissionLease lease){
        if(Intent.WorkspaceRelativePath is not { } relative)return;
        // The original driver takes this source admission before creating any workspace, and
        // retains it through every synchronous GetResult/Dispose and directory operation. Recovery
        // cannot acquire both gates until that actual producer has exited (or its process died).
        _=ValidatePendingPermit(lease);MmcFileTransactions.AssertNoPending(lease);
        MmcArtifactWorkspace.RecoverOwned(lease,relative,Intent.SourceRoot);
    }
    internal void RecoverUnmarkedWorkspace(MmcAdmissionLease lease){
        lease.AssertHeld();if(lease.VersionRoot!=Intent.SourceRoot||lease.InstanceId!=Intent.InstanceId||Owns(lease.VersionRoot)||
            MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(lease.VersionRoot)).Sha256!=Intent.SourcePrimarySha256)throw new IOException("Unmarked lifecycle preparation has no unchanged source authority.");
        MmcFileTransactions.AssertNoPending(lease);if(Intent.WorkspaceRelativePath is { } relative)MmcArtifactWorkspace.RecoverOwned(lease,relative,Intent.SourceRoot);
    }
    internal void BindPreparedTransaction(string location) {
        var candidate=FindTransaction(location);
        if(candidate is null) throw new IOException("Lifecycle structural journal was not prepared.");
        string path=Path.Combine(DirectoryPath,"transaction.record");
        if(!File.Exists(path)) MmcDurableFiles.WriteRecord(path,candidate);
        else if(MmcDurableFiles.ReadRecord<MmcLifecycleTxLink>(path).Value!=candidate) throw new IOException("Lifecycle transaction binding changed.");
    }
    internal MmcLifecycleTxLink? FindTransaction(string location) {
        string link=Path.Combine(DirectoryPath,"transaction.record");
        if(File.Exists(link)) {
            var value=MmcDurableFiles.ReadRecord<MmcLifecycleTxLink>(link).Value; ReadPrepared(location,value); return value;
        }
        string root=MmcInstancePaths.Resolve(location).ResolveRelative(MmcPathRole.Instance,"PCL/MmcTransactions");
        if(!Directory.Exists(root)) return null;
        var matches=new List<MmcLifecycleTxLink>();
        foreach(string directory in Directory.EnumerateDirectories(root).Take(1025)) {
            string record=Path.Combine(directory,"prepared.record"); if(!File.Exists(record)) continue;
            var (prepared,hash)=MmcDurableFiles.ReadRecord<MmcPreparedJournal>(record);
            if(Matches(prepared)) matches.Add(new(prepared.TransactionId,hash));
        }
        if(matches.Count>1) throw new IOException("More than one structural journal matches the lifecycle intent.");
        return matches.SingleOrDefault();
    }
    private bool Matches(MmcPreparedJournal journal) => journal.SchemaVersion==1 && journal.TransactionId!=Guid.Empty && journal.Mutations is not null && journal.InstanceId==Intent.InstanceId && journal.CanonicalRoot==Intent.SourceRoot &&
        journal.ExpectedGeneration==Intent.SourceGeneration && journal.NewGeneration==Intent.TargetGeneration && journal.Mutations.Any(value=>value.RelativePath=="PCL/InstanceDefinition.json" && value.OldSha256==Intent.SourcePrimarySha256 && value.NewSha256==Intent.TargetPrimarySha256);
    private (MmcPreparedJournal Journal,string Directory) ReadPrepared(string location,MmcLifecycleTxLink link) {
        if(link.TransactionId==Guid.Empty || !MmcDurableFiles.IsHash(link.PreparedSha256)) throw new IOException("Lifecycle structural binding is damaged.");
        var paths=MmcInstancePaths.Resolve(location); string directory=paths.ResolveRelative(MmcPathRole.Instance,"PCL/MmcTransactions/"+link.TransactionId.ToString("N"));
        if(!Directory.Exists(directory)) directory=paths.ResolveRelative(MmcPathRole.Instance,"PCL/MmcTransactionHistory/"+link.TransactionId.ToString("N"));
        var (journal,hash)=MmcDurableFiles.ReadRecord<MmcPreparedJournal>(Path.Combine(directory,"prepared.record"));
        if(hash!=link.PreparedSha256 || journal.TransactionId!=link.TransactionId || !Matches(journal)) throw new IOException("Lifecycle binding is not the exact expected inner transaction.");
        return(journal,directory);
    }
    internal bool HasStructuralDecision(string location) {
        var link=FindTransaction(location); if(link is null) return false;
        var (_,directory)=ReadPrepared(location,link); string path=Path.Combine(directory,"CommitDecided.record");
        if(File.Exists(path)) {
            var value=MmcDurableFiles.ReadRecord<MmcTransactionMarker>(path).Value;
            if(value.SchemaVersion!=1 || value.TransactionId!=link.TransactionId || value.InstanceId!=Intent.InstanceId || value.PreparedSha256!=link.PreparedSha256 || value.Phase!="CommitDecided") throw new IOException("Lifecycle structural decision is damaged.");
            return true;
        }
        return false;
    }
    internal bool HasDeleteDecision() {
        string path=Path.Combine(DirectoryPath,"CommitDecided.record"); if(!File.Exists(path)) return false;
        if(MmcDurableFiles.ReadRecord<MmcLifecycleDecision>(path).Value!=new MmcLifecycleDecision(1,Intent.OperationId,intentHash,"Delete")) throw new IOException("Delete decision is damaged.");
        return true;
    }
    internal void DecideDelete()=>MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath,"CommitDecided.record"),new MmcLifecycleDecision(1,Intent.OperationId,intentHash,"Delete"));
    internal MmcLifecycleCompletion? Completion() {
        string path=Path.Combine(DirectoryPath,"completed.record"); if(!File.Exists(path)) return null;
        var value=MmcDurableFiles.ReadRecord<MmcLifecycleCompletion>(path).Value;
        if(value.SchemaVersion!=1 || value.OperationId!=Intent.OperationId || value.IntentSha256!=intentHash || value.Disposition is not ("Moved" or "Quarantined" or "Aborted" or "Restored")) throw new IOException("Lifecycle completion is damaged.");
        return value;
    }
    internal void Complete(string disposition) {
        string path=Path.Combine(DirectoryPath,"completed.record"); var expected=new MmcLifecycleCompletion(1,Intent.OperationId,intentHash,disposition);
        if(!File.Exists(path)) MmcDurableFiles.WriteRecord(path,expected); else if(Completion()!=expected) throw new IOException("Lifecycle completion conflicts with its original result.");
    }
    internal string PrepareRestore(){
        if(MmcInstancePurgeJournal.HasDecision(DirectoryPath))throw new IOException("Permanent cleanup was decided; this instance can no longer be restored.");
        if(Intent.Kind!="Delete"||Completion()?.Disposition!="Quarantined")throw new IOException("Only a completed quarantine supports restoration.");
        AbandonRestorePreparation();return MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath,"restore.record"),new MmcLifecycleRestore(1,Guid.NewGuid(),Intent.OperationId,intentHash));
    }
    private (MmcLifecycleRestore Intent,string Hash) ReadRestore(){
        var(value,hash)=MmcDurableFiles.ReadRecord<MmcLifecycleRestore>(Path.Combine(DirectoryPath,"restore.record"));
        if(value.SchemaVersion!=1||value.RestoreId==Guid.Empty||value.DeleteOperationId!=Intent.OperationId||value.DeleteIntentSha256!=intentHash)throw new IOException("Quarantine restoration intent is invalid.");return(value,hash);
    }
    internal bool HasRestoreDecision(){
        string path=Path.Combine(DirectoryPath,"RestoreCommitDecided.record");if(!File.Exists(path))return false;
        var(intent,hash)=ReadRestore();if(MmcDurableFiles.ReadRecord<MmcLifecycleDecision>(path).Value!=new MmcLifecycleDecision(1,intent.RestoreId,hash,"RestoreDelete"))throw new IOException("Quarantine restoration decision is damaged.");return true;
    }
    internal void DecideRestore(){if(MmcInstancePurgeJournal.HasDecision(DirectoryPath))throw new IOException("Permanent cleanup was decided; restoration is forbidden.");var(intent,hash)=ReadRestore();MmcDurableFiles.WriteRecord(Path.Combine(DirectoryPath,"RestoreCommitDecided.record"),new MmcLifecycleDecision(1,intent.RestoreId,hash,"RestoreDelete"));}
    internal void AbandonRestorePreparation(string? expectedHash=null){
        string path=Path.Combine(DirectoryPath,"restore.record");if(!File.Exists(path)||HasRestoreDecision())return;var(_,hash)=ReadRestore();if(expectedHash is not null&&hash!=expectedHash)throw new IOException("A different restore preparation owns this record.");File.Delete(path);
    }
    internal bool IsRestored(){string path=Path.Combine(DirectoryPath,"restored.record");if(!File.Exists(path))return false;
        if(!HasRestoreDecision()||MmcDurableFiles.ReadRecord<MmcLifecycleCompletion>(path).Value!=new MmcLifecycleCompletion(1,Intent.OperationId,intentHash,"Restored"))throw new IOException("Quarantine restore completion is invalid.");return true;}
    internal void CompleteRestore(){string path=Path.Combine(DirectoryPath,"restored.record");if(!File.Exists(path))MmcDurableFiles.WriteRecord(path,new MmcLifecycleCompletion(1,Intent.OperationId,intentHash,"Restored"));else _=IsRestored();}
}

internal sealed partial class MmcPendingOperationPermit {
    internal static MmcPendingOperationPermit ForLifecycle(MmcAdmissionLease lease,MmcInstanceLifecycleJournal journal)=>
        new(lease,System.Collections.Immutable.ImmutableDictionary<string,string>.Empty.Add(MmcInstanceLifecycleJournal.MarkerRelative,journal.ValidatePendingPermit(lease)));
}
