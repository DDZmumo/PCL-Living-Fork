namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceLifecycleBackend {
    private MmcInstanceLifecycleResult Delete(MmcInstanceDeleteRequest request,CancellationToken external) {
        MmcInstanceLifecycleJournal? journal=null;bool decided=false;using var fence=new MmcLifecycleCancellationFence(external);
        try {
            ArgumentNullException.ThrowIfNull(request);string source=Canonical(request.SourceRoot);Guid operation=Guid.NewGuid();
            string target=Path.Combine(Path.GetDirectoryName(source)!,".pcllf-mmc-quarantine-"+operation.ToString("N"));
            MmcInstanceCloneReservation.ValidateRoots(source,target);MmcLifecycleAccess.SameVolume(source,target);
            using var access=new MmcLifecycleAccess(source,target,request.ExpectedInstanceId,fence.Token);
            var primary=Source(access.Leases[source],request.ExpectedInstanceId,request.ExpectedGeneration,request.ExpectedPrimarySha256);EmptyStaging(source);TargetQueue(access.Quiet[target],request.ExpectedInstanceId);
            journal=MmcInstanceLifecycleJournal.Create(new(1,operation,"Delete",request.ExpectedInstanceId,source,target,request.ExpectedGeneration,request.ExpectedGeneration,primary.Sha256,null,primary.Definition.Layout.GameRootLeaf),access.Leases[source]);
            request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.IntentPrepared,journal.DirectoryPath);fence.Token.ThrowIfCancellationRequested();
            journal.MarkPending(source);request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.MarkerWritten,journal.DirectoryPath);
            using(var permit=journal.Enter(access.Leases[source])){MmcFileTransactions.AssertNoPending(access.Leases[source]);MmcSessions.AssertNoActive(access.Leases[source]);request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeDecision,journal.DirectoryPath);fence.EnterDecision();journal.DecideDelete();decided=true;}
            request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.CommitDecided,journal.DirectoryPath);
            return Forward(journal,access,request.Checkpoint);
        }catch(MmcLifecycleInterruptionException error){return new(MmcInstanceBackendStatus.NeedsRecovery,journal?.DirectoryPath,journal?.Intent.SourceRoot,Failure:error.Message);}
        catch(OperationCanceledException error){if(journal is not null&&!decided)TryAbort(journal);return new(decided?MmcInstanceBackendStatus.NeedsRecovery:MmcInstanceBackendStatus.Cancelled,journal?.DirectoryPath,journal?.Intent.SourceRoot,Failure:error.Message);}
        catch(Exception error)when(Failure(error)){return new(journal is null?MmcInstanceBackendStatus.Rejected:MmcInstanceBackendStatus.NeedsRecovery,journal?.DirectoryPath,journal?.Intent.SourceRoot,Failure:error.Message);}
    }
    private MmcInstanceLifecycleResult Recover(string directory) {
        MmcInstanceLifecycleJournal? journal=null;
        try {
            journal=MmcInstanceLifecycleJournal.Read(directory);var intent=journal.Intent;
            MmcLifecycleAccess.SameVolume(intent.SourceRoot,intent.TargetRoot);
            using var access=new MmcLifecycleAccess(intent.SourceRoot,intent.TargetRoot,intent.InstanceId);
            if(MmcInstancePurgeJournal.HasArtifacts(directory)) {
                var purge=MmcInstancePurgeJournal.Read(journal);var result=RecoverPurge(purge,access);
                if(result.DecisionRecorded)return LifecyclePurgeResult(result);
            }
            if(intent.Kind=="Delete"&&journal.IsRestored()){
                var restored=TerminalPrimary(journal,access,intent.SourceRoot,intent.SourcePrimarySha256,intent.SourceGeneration,false);
                if(journal.Owns(intent.SourceRoot)){using var permit=journal.Enter(access.Leases[intent.SourceRoot]);journal.ClearPending(intent.SourceRoot);}
                return new(MmcInstanceBackendStatus.Succeeded,directory,intent.SourceRoot,restored.Definition);
            }
            if(intent.Kind=="Delete"&&journal.HasRestoreDecision())return RestoreForward(journal,access);
            var completed=journal.Completion();
            if(completed is not null){
                if(completed.Disposition=="Quarantined")journal.AbandonRestorePreparation();
                string location=completed.Disposition=="Aborted"?intent.SourceRoot:intent.TargetRoot;
                var primary=TerminalPrimary(journal,access,location,completed.Disposition=="Moved"?intent.TargetPrimarySha256!:intent.SourcePrimarySha256,
                    completed.Disposition=="Moved"?intent.TargetGeneration:intent.SourceGeneration,completed.Disposition=="Quarantined");
                if(completed.Disposition=="Moved"&&journal.Owns(location)){using var permit=journal.Enter(access.Leases[location]);journal.ClearPending(location);}
                if(completed.Disposition=="Aborted"&&journal.Owns(location)){using var permit=journal.Enter(access.Leases[location]);journal.ClearPending(location);}
                return new(completed.Disposition=="Aborted"?MmcInstanceBackendStatus.Cancelled:MmcInstanceBackendStatus.Succeeded,directory,location,
                    primary.Definition,completed.Disposition=="Quarantined");
            }
            if(journal.Owns(intent.SourceRoot)) {
                using var permit=journal.Enter(access.Leases[intent.SourceRoot]);MmcSessions.AssertNoActive(access.Leases[intent.SourceRoot]);
                if(intent.Kind=="Delete") {
                    if(journal.HasDeleteDecision())return Forward(journal,access);
                } else {
                    var link=journal.FindTransaction(intent.SourceRoot);
                    if(link is not null){var result=MmcFileTransactions.Recover(access.Leases[intent.SourceRoot],link.TransactionId);
                        if(result.Outcome==MmcTransactionOutcome.RecoveryRequired)return new(MmcInstanceBackendStatus.NeedsRecovery,directory,intent.SourceRoot,Failure:result.Diagnostic);
                        journal.RecoverWorkspace(access.Leases[intent.SourceRoot]);
                        if(result.Outcome==MmcTransactionOutcome.Committed){if(!journal.HasStructuralDecision(intent.SourceRoot))throw new IOException("Recovered structural decision has no durable proof.");return Forward(journal,access);}
                    }
                }
                if(MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(intent.SourceRoot)).Sha256!=intent.SourcePrimarySha256)throw new IOException("Undecided lifecycle source cannot be inferred from changed bytes.");
                journal.RecoverWorkspace(access.Leases[intent.SourceRoot]);
                journal.Complete("Aborted");journal.ClearPending(intent.SourceRoot);return new(MmcInstanceBackendStatus.Cancelled,directory,intent.SourceRoot);
            }
            if(journal.Owns(intent.TargetRoot)) {
                if(intent.Kind=="Delete"?!journal.HasDeleteDecision():!journal.HasStructuralDecision(intent.TargetRoot))throw new IOException("Moved lifecycle directory has no exact durable decision.");
                using(var permit=journal.Enter(access.Leases[intent.TargetRoot]))journal.RecoverWorkspace(access.Leases[intent.TargetRoot]);
                return Forward(journal,access);
            }
            // A crash before marker creation never authorizes edits to another operation's source.
            if(Directory.Exists(intent.SourceRoot)&&MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(intent.SourceRoot)).Sha256==intent.SourcePrimarySha256) {
                MmcFileTransactions.AssertNoPending(access.Leases[intent.SourceRoot]);journal.RecoverUnmarkedWorkspace(access.Leases[intent.SourceRoot]);journal.Complete("Aborted");return new(MmcInstanceBackendStatus.Cancelled,directory,intent.SourceRoot);
            }
            throw new IOException("Lifecycle source and target ownership are missing or conflicting; both paths were preserved.");
        }catch(Exception error)when(Failure(error)){return new(MmcInstanceBackendStatus.NeedsRecovery,journal?.DirectoryPath??directory,journal?.Intent.SourceRoot,Failure:error.Message){PurgeDecided=MmcInstancePurgeJournal.HasDecision(directory)};}
    }
    private static MmcStoredPrimaryDefinition TerminalPrimary(MmcInstanceLifecycleJournal journal,MmcLifecycleAccess access,string location,string hash,long generation,bool quarantine){
        var primary=MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(location));
        if(primary.Sha256!=hash||primary.Definition.InstanceId!=journal.Intent.InstanceId||primary.Definition.Generation!=generation)
            throw new IOException("Completed lifecycle location was replaced or its expected primary changed; preserving every path.");
        if(journal.Owns(location)){using var permit=journal.Enter(access.Leases[location]);MmcFileTransactions.AssertNoPending(access.Leases[location]);MmcSessions.AssertNoActive(access.Leases[location]);}
        else{if(quarantine)throw new IOException("Completed quarantine lost its exact ownership marker.");MmcFileTransactions.AssertNoPending(access.Leases[location]);}
        return primary;
    }
}
