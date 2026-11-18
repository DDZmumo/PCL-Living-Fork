using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public static partial class MmcConfigurationCoordinator {
    /// <summary>Called only after a verified no-overwrite directory move, under both paused states and same-thread sorted admission leases.</summary>
    internal static void CompletePathRelocation(MmcConfigurationQuiescence sourceQuiet, MmcConfigurationQuiescence targetQuiet,
        MmcAdmissionLease sourceLease, MmcAdmissionLease targetLease, Guid instanceId, string targetPrimarySha256) {
        sourceLease.AssertHeld(); targetLease.AssertHeld();
        var source = sourceQuiet.State; var target = targetQuiet.State;
        if (source.Root != sourceLease.VersionRoot || target.Root != targetLease.VersionRoot || sourceLease.InstanceId != instanceId || targetLease.InstanceId != instanceId || source.Root == target.Root)
            throw new IOException("配置路径迁移与准入租约不一致。");
        if (MmcInstancePaths.Attributes(source.Root) is not null) throw new IOException("旧实例路径已被占用，不能将其缓存误当作已移动实例处理。");
        var paths = MmcInstancePaths.Resolve(target.Root);
        var primary = MmcSavedSourceStore.ReadPrimary(paths);
        if (primary.Sha256 != targetPrimarySha256 || primary.Definition.InstanceId != instanceId) throw new IOException("目标主定义不符合已决定的配置迁移。");
        MmcFileTransactions.AssertNoPending(targetLease); MmcSessions.AssertNoActive(targetLease);
        // No gate wait or file I/O is allowed while either data lock is held.
        JObject disk = ReadConfig(target.Root); string ini = MmcIniConfiguration.ReadIni(target.Root);
        var first = string.Compare(source.Root, target.Root, StringComparison.OrdinalIgnoreCase) < 0 ? source : target;
        var second = ReferenceEquals(first, source) ? target : source;
        lock (first.Sync) lock (second.Sync) {
            if (source.PauseCount == 0 || target.PauseCount == 0 || source.Inflight != 0 || target.Inflight != 0) throw new IOException("配置迁移必须先暂停并排空两条路径。");
            var context = new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, instanceId);
            if (source.Detached && source.Context == context && target.Context == context && source.Patches.Count == 0 && source.IniPatches.Count == 0) return;
            // A restarted process has no live old providers and observes the now-missing source
            // as Unresolved. The verified destination primary supplies the identity in that case.
            if (source.Context.Kind != MmcConfigurationContextKind.Unresolved || source.Patches.Count != 0 || source.IniPatches.Count != 0) source.AssertContext(context);
            if (target.Patches.Any(pair => pair.Value.Context != context) || target.IniPatches.Any(pair => pair.Value.Context != context))
                throw new IOException("目标路径含有其他身份或旧普通实例的待写入设置。");
            var json = source.Patches.Where(pair => pair.Value.Context == context && !IsStructuralKey(pair.Key)).ToArray();
            var pendingIni = source.IniPatches.Where(pair => pair.Value.Context == context && !MmcIniConfiguration.IsStructural(pair.Key)).ToArray();
            target.AttachmentEpoch++; target.PublishObserved(disk, context, reattach: true); target.PublishIni(ini, notify: false);
            foreach (var pair in json) {
                JToken? value = pair.Value.Value?.DeepClone();
                if (value is JContainer container) foreach (var text in container.Descendants().OfType<JValue>().Where(item => item.Type == JTokenType.String)) text.Value = RewritePath(text.Value<string>()!, source.Root, target.Root);
                else if (value is JValue { Type: JTokenType.String } text) text.Value = RewritePath(text.Value<string>()!, source.Root, target.Root);
                target.Enqueue(pair.Key, value, pair.Value.Remove, context);
            }
            foreach (var pair in pendingIni) target.EnqueueIni(pair.Key, pair.Value.Value is null ? null : RewritePath(pair.Value.Value, source.Root, target.Root), context);
            source.DetachAfterRelocation();
        }
        source.NotifyPendingInvalidations(); target.NotifyPendingInvalidations();
    }
    internal static string RewritePath(string text, string source, string target) {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (text.Equals(source, comparison)) return target;
        string slashSource = source.Replace('\\', '/'), slashTarget = target.Replace('\\', '/');
        return text.Replace(source + "\\", target + "\\", comparison).Replace(source + "/", slashTarget + "/", comparison)
            .Replace(slashSource + "/", slashTarget + "/", comparison).Replace(slashSource + "\\", slashTarget + "\\", comparison);
    }
    /// <summary>Quarantine stays pending: only its verified operation owner may durably consume transferred ordinary preference keys.</summary>
    internal static void FlushRelocatedPreferences(MmcConfigurationQuiescence quiet,MmcAdmissionLease lease,Guid id,string expectedPrimarySha256){
        lease.AssertHeld();var state=quiet.State;
        if(state.Root!=lease.VersionRoot||lease.InstanceId!=id||MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(state.Root)).Sha256!=expectedPrimarySha256)throw new IOException("Quarantine preference publication lost its fixed identity.");
        MmcFileTransactions.AssertNoPending(lease);MmcSessions.AssertNoActive(lease);
        Dictionary<string,MmcConfigPatch> json;Dictionary<string,MmcIniPatch> pendingIni;
        lock(state.Sync){
            state.AssertContext(new(MmcConfigurationContextKind.Managed,id));if(state.PauseCount==0||state.Inflight!=0)throw new IOException("Quarantine preference publication requires a drained state.");
            json=state.CapturePatches();pendingIni=state.CaptureIniPatches();
            if(json.Any(pair=>IsStructuralKey(pair.Key)||pair.Value.Context.InstanceId!=id)||pendingIni.Any(pair=>MmcIniConfiguration.IsStructural(pair.Key)||pair.Value.Context.InstanceId!=id))throw new IOException("Quarantine cannot consume foreign or structural preference patches.");
        }
        var disk=ReadConfig(state.Root);string ini=MmcIniConfiguration.ReadIni(state.Root);Apply(disk,json);
        foreach(var pair in pendingIni)ini=LegacyBaseOperations.ReplaceIniValue(ini,pair.Key,pair.Value.Value);
        if(json.Count!=0)WritePreferenceConfig(state.Root,disk);if(pendingIni.Count!=0)MmcIniConfiguration.WriteIni(state.Root,ini);
        state.Publish(disk,json);state.PublishIni(ini,pendingIni,notify:false);
    }
}
internal sealed partial class MmcConfigurationState {
    internal void DetachAfterRelocation() {
        Detached = true; AttachmentEpoch++; Patches.Clear(); IniPatches.Clear(); Timer.Change(Timeout.Infinite, Timeout.Infinite);
        consumerInvalidationPending = true; ClearRegisteredIniCaches(); UnpinAfterRelocation();
    }
    private void UnpinAfterRelocation() => MmcConfigurationCoordinator.UnpinIfClean(this);
}
public sealed partial class MmcConfigurationWriteScope {
    internal string IniSnapshotForRelocation { get { lease.AssertHeld();return ini; } }
}
