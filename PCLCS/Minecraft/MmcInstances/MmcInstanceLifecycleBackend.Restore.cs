namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceLifecycleBackend {
    public Task<MmcInstanceLifecycleResult> RestoreDeletedAsync(string operationDirectory,CancellationToken cancellationToken=default,
        Action<MmcLifecycleCheckpoint,string>? checkpoint=null)=>Task.Run(()=>RestoreDeleted(operationDirectory,cancellationToken,checkpoint),CancellationToken.None);
    private MmcInstanceLifecycleResult RestoreDeleted(string directory,CancellationToken external,Action<MmcLifecycleCheckpoint,string>? checkpoint){
        MmcInstanceLifecycleJournal? journal=null;bool decided=false;string? preparationHash=null;using var fence=new MmcLifecycleCancellationFence(external);
        try{
            var recovered=Recover(directory);if(recovered.Status!=MmcInstanceBackendStatus.Succeeded||!recovered.Quarantined)return recovered;
            journal=MmcInstanceLifecycleJournal.Read(directory);var intent=journal.Intent;
            using var access=new MmcLifecycleAccess(intent.SourceRoot,intent.TargetRoot,intent.InstanceId,fence.Token);
            if(!journal.Owns(intent.TargetRoot)||MmcInstancePaths.Attributes(intent.SourceRoot)is not null)throw new IOException("Quarantine or original restore name is missing/occupied; both were preserved.");
            using(var permit=journal.Enter(access.Leases[intent.TargetRoot])){
                MmcFileTransactions.AssertNoPending(access.Leases[intent.TargetRoot]);MmcSessions.AssertNoActive(access.Leases[intent.TargetRoot]);TargetQueue(access.Quiet[intent.SourceRoot],intent.InstanceId);
                if(MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(intent.TargetRoot)).Sha256!=intent.SourcePrimarySha256)throw new IOException("Quarantine primary changed.");
                preparationHash=journal.PrepareRestore();checkpoint?.Invoke(MmcLifecycleCheckpoint.IntentPrepared,directory);fence.Token.ThrowIfCancellationRequested();
                checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeDecision,directory);fence.EnterDecision();journal.DecideRestore();decided=true;checkpoint?.Invoke(MmcLifecycleCheckpoint.CommitDecided,directory);
            }
            return RestoreForward(journal,access,checkpoint);
        }catch(OperationCanceledException error){if(journal is not null&&!decided&&preparationHash is not null){using var cleanup=new MmcLifecycleAccess(journal.Intent.SourceRoot,journal.Intent.TargetRoot,journal.Intent.InstanceId);journal.AbandonRestorePreparation(preparationHash);}return new(decided?MmcInstanceBackendStatus.NeedsRecovery:MmcInstanceBackendStatus.Cancelled,directory,journal?.Intent.TargetRoot,Quarantined:true,Failure:error.Message);}
        catch(MmcLifecycleInterruptionException error){return new(MmcInstanceBackendStatus.NeedsRecovery,directory,journal?.Intent.TargetRoot,Quarantined:true,Failure:error.Message);}
        catch(Exception error)when(Failure(error)){return new(MmcInstanceBackendStatus.NeedsRecovery,directory,journal?.Intent.TargetRoot,Quarantined:true,Failure:error.Message);}
    }
    private static MmcInstanceLifecycleResult RestoreForward(MmcInstanceLifecycleJournal journal,MmcLifecycleAccess access,Action<MmcLifecycleCheckpoint,string>? checkpoint=null){
        var intent=journal.Intent;
        if(!journal.HasDeleteDecision()||!journal.HasRestoreDecision())throw new IOException("Quarantine restoration needs both exact operation decisions.");
        if(journal.Owns(intent.TargetRoot)){
            using var permit=journal.Enter(access.Leases[intent.TargetRoot]);MmcFileTransactions.AssertNoPending(access.Leases[intent.TargetRoot]);MmcSessions.AssertNoActive(access.Leases[intent.TargetRoot]);
            EmptyStaging(intent.TargetRoot);checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeDirectoryMove,journal.DirectoryPath);
            if(MmcInstancePaths.Attributes(intent.SourceRoot)is not null)throw new IOException("Original instance name was claimed; decided restoration remains pending.");
            Directory.Move(intent.TargetRoot,intent.SourceRoot);checkpoint?.Invoke(MmcLifecycleCheckpoint.DirectoryMoved,journal.DirectoryPath);
        }
        if(!journal.Owns(intent.SourceRoot)||MmcInstancePaths.Attributes(intent.TargetRoot)is not null)throw new IOException("Restoration ownership is conflicting.");
        using(var permit=journal.Enter(access.Leases[intent.SourceRoot])){
            MmcConfigurationCoordinator.CompletePathRelocation(access.Quiet[intent.TargetRoot],access.Quiet[intent.SourceRoot],access.Leases[intent.TargetRoot],access.Leases[intent.SourceRoot],intent.InstanceId,intent.SourcePrimarySha256);
            journal.CompleteRestore();journal.ClearPending(intent.SourceRoot);
        }
        checkpoint?.Invoke(MmcLifecycleCheckpoint.Completed,journal.DirectoryPath);
        return new(MmcInstanceBackendStatus.Succeeded,journal.DirectoryPath,intent.SourceRoot,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(intent.SourceRoot)).Definition);
    }
}
