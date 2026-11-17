using System.Collections.Immutable;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceLifecycleBackend {
    public Task<MmcInstanceLifecycleResult> RelocateAsync(MmcInstanceRelocationRequest request,CancellationToken cancellationToken=default)=>Task.Run(()=>Relocate(request,cancellationToken),CancellationToken.None);
    public Task<MmcInstanceLifecycleResult> DeleteAsync(MmcInstanceDeleteRequest request,CancellationToken cancellationToken=default)=>Task.Run(()=>Delete(request,cancellationToken),CancellationToken.None);
    public Task<MmcInstanceLifecycleResult> RecoverAsync(string operationDirectory)=>Task.Run(()=>Recover(operationDirectory),CancellationToken.None);
    private static MmcStoredPrimaryDefinition Source(MmcAdmissionLease lease,Guid id,long generation,string hash) {
        lease.AssertHeld();MmcFileTransactions.AssertNoPending(lease);MmcSessions.AssertNoActive(lease);
        var observed=MmcManagementProbe.Read(lease.VersionRoot);var primary=MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(lease.VersionRoot));
        if(observed.State!=MmcManagementState.ManagedMmc||observed.Envelope?.InstanceId!=id||observed.Envelope.Generation!=generation||primary.Definition.InstanceId!=id||primary.Definition.Generation!=generation||primary.Sha256!=hash)
            throw new IOException("Lifecycle source identity/generation/hash is stale or pending.");
        return primary;
    }
    private static string Canonical(string root)=>MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(root));
    private static void EmptyStaging(string root) {
        string staging=MmcInstancePaths.Resolve(root).ResolveRelative(MmcPathRole.Runtime,".staging");
        if(Directory.Exists(staging)&&Directory.EnumerateFileSystemEntries(staging).Any())throw new IOException("A prepared workload still uses this instance path.");
    }
    private static void TargetQueue(MmcConfigurationQuiescence quiet,Guid id){var state=quiet.State;var context=new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed,id);
        lock(state.Sync)if(state.Patches.Any(value=>value.Value.Context!=context)||state.IniPatches.Any(value=>value.Value.Context!=context))throw new IOException("Destination has pending preferences from another identity.");}
    private MmcInstanceLifecycleResult Relocate(MmcInstanceRelocationRequest request,CancellationToken external) {
        MmcInstanceLifecycleJournal? journal=null;bool decided=false;using var fence=new MmcLifecycleCancellationFence(external);
        try {
            ArgumentNullException.ThrowIfNull(request);string source=Canonical(request.SourceRoot),target=Canonical(request.TargetRoot);
            MmcInstanceCloneReservation.ValidateRoots(source,target);MmcLifecycleAccess.SameVolume(source,target);
            if(request.Kind==MmcInstanceRelocationKind.Rename&&Path.GetDirectoryName(source)!=Path.GetDirectoryName(target))throw new IOException("Rename must stay under the same versions directory.");
            if(!Enum.IsDefined(request.Kind))throw new IOException("Unknown lifecycle operation.");
            MmcArtifactIo.Directory(Path.GetDirectoryName(target)!);
            using var access=new MmcLifecycleAccess(source,target,request.ExpectedInstanceId,fence.Token);
            var sourceLease=access.Leases[source];var targetLease=access.Leases[target];
            if(MmcInstancePaths.Attributes(target)is not null)throw new IOException("Existing destination was preserved.");TargetQueue(access.Quiet[target],request.ExpectedInstanceId);
            var primary=Source(sourceLease,request.ExpectedInstanceId,request.ExpectedGeneration,request.ExpectedPrimarySha256);var definition=primary.Definition;
            var paths=MmcInstancePaths.Resolve(source,definition.Layout.GameRootLeaf);var saved=MmcSavedSourceStore.LoadAsync(paths,definition,fence.Token).GetAwaiter().GetResult();
            var compiled=MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution,definition,request.LegacyEnvironmentCandidates);
            var active=compiled.Classpath.Concat(compiled.NativeArtifacts).Concat(compiled.DownloadOnlyArtifacts).Concat(compiled.AuxiliaryArtifacts).GroupBy(value=>value.Key,StringComparer.Ordinal).Select(group=>group.First()).ToImmutableArray();
            var declared=active.Where(value=>value.Scope==MmcArtifactScope.InstanceLocal).Select(value=>"libraries/"+value.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var artifacts=new MmcArtifactRequest(paths,compiled.RuntimePlanFingerprint,active,MmcPackageFilePreservation.CreateLibraryArtifacts(saved.Baseline).Where(value=>!declared.Contains("libraries/"+value.RelativePath)).ToImmutableArray(),MmcPublishedArtifactLocks.Create(definition),MmcArtifactIntent.RepairExisting,compiled.Assets);
            using var prepared=new MmcArtifactMaterializer().PrepareAsync(artifacts,cancellationToken:fence.Token).GetAwaiter().GetResult();
            if(!prepared.StagedMutations.IsEmpty)throw new IOException("Repair incomplete owned runtime inputs before moving this instance.");
            var observations=MmcArtifactBindingAdapter.Create(artifacts,compiled,prepared);long next=checked(definition.Generation+1);string oldName=Path.GetFileName(source),newName=Path.GetFileName(target);
            byte[] projection=MmcVersionProjection.Render(newName,definition.InstanceId,next,compiled,prepared.OrderedBindings.ToDictionary(value=>value.Artifact.Key,value=>value.TargetRelativePath),
                prepared.NativeDirectory is null?null:Path.GetRelativePath(source,prepared.NativeDirectory).Replace('\\','/'),prepared.AssetsRoot is null?"PCL/MmcRuntime/"+prepared.ViewKey+"/assets":Path.GetRelativePath(source,prepared.AssetsRoot).Replace('\\','/'));
            var bound=MmcPlanBinder.Bind(definition,compiled,observations.Facts,observations.ApplyTo(new(definition.Generation,next,MmcBindingIntent.RepairExisting,MmcDurableFiles.Hash(projection),next)),definition.ArtifactLocks,observations.DerivedOutputs,observations.AssetIndexes,publishedBaseline:definition,packageBaseline:saved.BaselineInput);
            if(bound.HasErrors||bound.Definition is null)throw new IOException("Lifecycle binding failed: "+string.Join("; ",bound.Diagnostics.Select(value=>value.Code)));
            byte[] nextDefinition=Encoding.UTF8.GetBytes(MmcDefinitionCodec.Serialize(bound.Definition));
            if(oldName!=newName&&MmcInstancePaths.Attributes(paths.ResolveRelative(MmcPathRole.Instance,newName+".json"))is not null)throw new IOException("New compatibility filename is already occupied.");
            var intent=new MmcLifecycleIntent(1,Guid.NewGuid(),request.Kind.ToString(),definition.InstanceId,source,target,definition.Generation,next,primary.Sha256,MmcDurableFiles.Hash(nextDefinition),paths.GameRootLeaf){WorkspaceRelativePath=Path.GetRelativePath(source,prepared.StageRoot).Replace('\\','/')};
            journal=MmcInstanceLifecycleJournal.Create(intent,sourceLease);request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.IntentPrepared,journal.DirectoryPath);fence.Token.ThrowIfCancellationRequested();
            journal.MarkPending(source);request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.MarkerWritten,journal.DirectoryPath);
            using(var permit=journal.Enter(sourceLease))using(var settings=access.Quiet[source].OpenProjectionRepair(sourceLease,definition.Generation,primary.Sha256)) {
                RewritePreferences(settings,source,target,compiled,next);
                var mutations=new List<MmcFileMutation>{new("PCL/InstanceDefinition.json",primary.Sha256,nextDefinition,MmcDurableFiles.Hash(nextDefinition)),
                    new(newName+".json",oldName==newName?definition.InstalledProjection!.OutputJsonSha256:null,projection,MmcDurableFiles.Hash(projection))};
                if(oldName!=newName)mutations.Add(new(oldName+".json",definition.InstalledProjection!.OutputJsonSha256,null,null));
                var result=settings.Execute(new(definition.InstanceId,definition.Generation,next,mutations),fence.Token,(phase,_)=>{
                    if(phase==MmcTransactionCheckpoint.Prepared){journal.BindPreparedTransaction(source);request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.StructurePrepared,journal.DirectoryPath);}
                    if(phase==MmcTransactionCheckpoint.BeforeDecision){request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeDecision,journal.DirectoryPath);fence.EnterDecision();}
                    if(phase==MmcTransactionCheckpoint.CommitDecided){decided=true;request.Checkpoint?.Invoke(MmcLifecycleCheckpoint.CommitDecided,journal.DirectoryPath);}
                });
                if(result.Outcome!=MmcTransactionOutcome.Committed){if(result.Outcome==MmcTransactionOutcome.Aborted){journal.Complete("Aborted");journal.ClearPending(source);}return new(fence.Token.IsCancellationRequested?MmcInstanceBackendStatus.Cancelled:MmcInstanceBackendStatus.NeedsRecovery,journal.DirectoryPath,source,Failure:result.Diagnostic);}
                decided=true;
            }
            prepared.Dispose();EmptyStaging(source);
            return Forward(journal,access,request.Checkpoint);
        }catch(MmcLifecycleInterruptionException error){return new(MmcInstanceBackendStatus.NeedsRecovery,journal?.DirectoryPath,journal?.Intent.SourceRoot,Failure:error.Message);}
        catch(OperationCanceledException error){if(journal is not null&&!decided)TryAbort(journal);return new(decided?MmcInstanceBackendStatus.NeedsRecovery:MmcInstanceBackendStatus.Cancelled,journal?.DirectoryPath,journal?.Intent.SourceRoot,Failure:error.Message);}
        catch(Exception error)when(Failure(error)){if(journal is not null&&!decided&&journal.FindTransaction(journal.Intent.SourceRoot)is null)TryAbort(journal);return new(journal is null?MmcInstanceBackendStatus.Rejected:MmcInstanceBackendStatus.NeedsRecovery,journal?.DirectoryPath,journal?.Intent.SourceRoot,Failure:error.Message);}
    }
    private static void RewritePreferences(MmcConfigurationWriteScope scope,string source,string target,MmcCompiledProfile compiled,long generation) {
        var json=scope.ConfigSnapshot;
        foreach(var value in json.Descendants().OfType<JValue>().Where(value=>value.Type==JTokenType.String))value.Value=MmcConfigurationCoordinator.RewritePath(value.Value<string>()!,source,target);
        var hint=JObject.Parse(MmcVersionProjection.ManagementHint(MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(source)).Definition.InstanceId,generation,compiled).ToJsonString());
        var manifest=json["MinecraftInstallManifest"]as JObject??new();manifest["pcllfManagement"]=hint.DeepClone();manifest["LastModifiedUtc"]=DateTime.UtcNow;json["MinecraftInstallManifest"]=manifest;
        if(json.ContainsKey("pcllfManagement"))json["pcllfManagement"]=hint.DeepClone();if(json.ContainsKey("generation"))json["generation"]=generation;
        foreach(var property in json.Properties())scope.SetConfig(property.Name,property.Value);
        var ini=LegacyBaseOperations.ParseIni(scope.IniSnapshotForRelocation);foreach(var pair in ini)scope.SetIni(pair.Key,MmcConfigurationCoordinator.RewritePath(pair.Value,source,target));
        if(ini.ContainsKey("MmcDefinitionGeneration"))scope.SetIni("MmcDefinitionGeneration",generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static MmcInstanceLifecycleResult Forward(MmcInstanceLifecycleJournal journal,MmcLifecycleAccess access,Action<MmcLifecycleCheckpoint,string>? checkpoint=null) {
        var intent=journal.Intent;string source=intent.SourceRoot,target=intent.TargetRoot;bool delete=intent.Kind=="Delete";
        if(journal.Owns(source)&&journal.Owns(target))throw new IOException("Lifecycle ownership exists at two paths; preserve both for conflict resolution.");
        if(journal.Owns(source)) {
            using var permit=journal.Enter(access.Leases[source]);MmcFileTransactions.AssertNoPending(access.Leases[source]);MmcSessions.AssertNoActive(access.Leases[source]);
            var primary=MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(source));
            if(primary.Sha256!=(delete?intent.SourcePrimarySha256:intent.TargetPrimarySha256))throw new IOException("Decided lifecycle primary changed.");
            EmptyStaging(source);TargetQueue(access.Quiet[target],intent.InstanceId);checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeDirectoryMove,journal.DirectoryPath);
            if(MmcInstancePaths.Attributes(target)is not null)throw new IOException("Destination was claimed after the decision; forward recovery is required.");
            MmcArtifactIo.Directory(Path.GetDirectoryName(target)!);Directory.Move(source,target);checkpoint?.Invoke(MmcLifecycleCheckpoint.DirectoryMoved,journal.DirectoryPath);
        }
        if(!journal.Owns(target)||MmcInstancePaths.Attributes(source)is not null)throw new IOException("Lifecycle directory location is missing or conflicting.");
        using(var permit=journal.Enter(access.Leases[target])) {
            MmcConfigurationCoordinator.CompletePathRelocation(access.Quiet[source],access.Quiet[target],access.Leases[source],access.Leases[target],intent.InstanceId,delete?intent.SourcePrimarySha256:intent.TargetPrimarySha256!);
            if(delete)MmcConfigurationCoordinator.FlushRelocatedPreferences(access.Quiet[target],access.Leases[target],intent.InstanceId,intent.SourcePrimarySha256);
            checkpoint?.Invoke(MmcLifecycleCheckpoint.BeforeFinalization,journal.DirectoryPath);journal.Complete(delete?"Quarantined":"Moved");if(!delete)journal.ClearPending(target);
        }
        checkpoint?.Invoke(MmcLifecycleCheckpoint.Completed,journal.DirectoryPath);
        return new(MmcInstanceBackendStatus.Succeeded,journal.DirectoryPath,target,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(target)).Definition,delete);
    }
    private void TryAbort(MmcInstanceLifecycleJournal journal){_ = Recover(journal.DirectoryPath);}
    private static bool Failure(Exception error)=>error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception or PlatformNotSupportedException;
}
