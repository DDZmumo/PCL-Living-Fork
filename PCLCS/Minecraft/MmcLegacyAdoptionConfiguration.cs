using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcLegacyAdoptionConfiguration {
    internal static void AttachOwned(MmcConfigurationQuiescence quiet, MmcAdmissionLease lease, MmcLegacyAdoptionJournal owner) {
        lease.AssertHeld(); var primary = owner.RequirePrimary(lease.VersionRoot); var state = quiet.State;
        if (state.Root != lease.VersionRoot || primary.Definition.InstanceId != lease.InstanceId) throw new IOException("转换偏好上下文与新实例身份不一致。");
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        var context = new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, lease.InstanceId);
        JObject disk = MmcConfigurationCoordinator.ReadConfig(state.Root); string ini = MmcIniConfiguration.ReadIni(state.Root);
        lock (state.Sync) {
            if (state.PauseCount == 0 || state.Inflight != 0 || state.Patches.Any(pair => pair.Value.Context != context) || state.IniPatches.Any(pair => pair.Value.Context != context))
                throw new IOException("转换目标尚未排空或包含其它身份的待写入偏好。");
            if (state.Context != context && state.Context.Kind != MmcConfigurationContextKind.Unresolved) throw new IOException("转换目标配置属于另一身份。");
            state.PublishObserved(disk, context, reattach: true); state.PublishIni(ini, notify: false);
        }
        state.NotifyPendingInvalidations(); // Registered callbacks are data-only cache invalidators.
    }
    internal static void Copy(MmcConfigurationQuiescence quiet, MmcAdmissionLease lease, MmcLegacyAdoptionJournal owner,
        MmcLegacyAdoptionPreview preview, MmcInstancePaths finalPaths) {
        if (lease.VersionRoot != owner.PrivateRoot || owner.HasDecision()) throw new IOException("只能在发布决定前写入私有管理副本偏好。");
        AttachOwned(quiet, lease, owner); var state = quiet.State;
        JObject disk = MmcConfigurationCoordinator.ReadConfig(state.Root), old = JObject.Parse(preview.ConfigJson);
        string Rewrite(string text) {
            text = MmcConfigurationCoordinator.RewritePath(text, Path.Combine(preview.Inventory.SourceRoot, "PCL"), Path.Combine(finalPaths.InstanceRoot, "PCL"));
            text = MmcConfigurationCoordinator.RewritePath(text, preview.Inventory.GameRoot, finalPaths.GameRoot);
            return preview.Inventory.SourceRoot == preview.Inventory.GameRoot ? text : MmcConfigurationCoordinator.RewritePath(text, preview.Inventory.SourceRoot, finalPaths.InstanceRoot);
        }
        foreach (var property in old.Properties().Where(property => !MmcConfigurationCoordinator.IsStructuralKey(property.Name))) {
            JToken value = property.Value.DeepClone();
            if (value is JContainer container) foreach (var text in container.Descendants().OfType<JValue>().Where(item => item.Type == JTokenType.String)) text.Value = Rewrite(text.Value<string>()!);
            else if (value is JValue { Type: JTokenType.String } text) text.Value = Rewrite(text.Value<string>()!);
            disk[property.Name] = value;
        }
        string ini = MmcIniConfiguration.ReadIni(state.Root); var previous = LegacyBaseOperations.ParseIni(preview.Ini);
        foreach (var pair in previous) if (!MmcIniConfiguration.IsStructural(pair.Key)) ini = LegacyBaseOperations.ReplaceIniValue(ini, pair.Key, Rewrite(pair.Value));
        string Mode(string key, string fallback) {
            if (!previous.TryGetValue(key, out string? value)) return fallback;
            if (value is not ("InheritGlobal" or "UsePackage" or "Custom")) throw new IOException("旧 MMC 偏好模式无效：" + key);
            return value;
        }
        string java = old["InstanceForcedJava"] is JObject || previous.Keys.Any(key => key is "VersionArgumentJavaSelect" or "VersionArgumentJavaV2" or "VersionArgumentJavaRange") ? "Custom" : "InheritGlobal";
        if (previous.GetValueOrDefault("VersionArgumentJavaSelect") == "使用全局设置") java = "InheritGlobal";
        string memory = previous.TryGetValue("VersionRamType", out string? ram) && ram != "2" ? "Custom" : "InheritGlobal";
        foreach (var pair in new Dictionary<string, string> {
            ["MmcJavaLocationMode"] = Mode("MmcJavaLocationMode", java),
            ["MmcJvmArgsMode"] = Mode("MmcJvmArgsMode", previous.ContainsKey("VersionAdvanceJvm") ? "Custom" : "InheritGlobal"),
            ["MmcMemoryMode"] = Mode("MmcMemoryMode", memory), ["MmcWindowMode"] = Mode("MmcWindowMode", "InheritGlobal")
        }) ini = LegacyBaseOperations.ReplaceIniValue(ini, pair.Key, pair.Value);
        // Layout/identity always come from the new importer. Source INI structural
        // keys cannot move the managed copy back into a flat/shared game directory.
        MmcConfigurationCoordinator.WriteAdoptionPreferences(quiet, lease, owner, disk, ini);
    }
}

public static partial class MmcConfigurationCoordinator {
    /// <summary>Private unpublished target only; preferences use coordinated atomic file writes, never a stale whole-provider autosave.</summary>
    internal static void WriteAdoptionPreferences(MmcConfigurationQuiescence quiet, MmcAdmissionLease lease,
        MmcLegacyAdoptionJournal owner, JObject disk, string ini) {
        lease.AssertHeld(); owner.RequirePrimary(lease.VersionRoot); MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        if (lease.VersionRoot != owner.PrivateRoot || owner.HasDecision()) throw new IOException("已发布实例不能使用转换偏好写入入口。");
        var state = quiet.State; var context = new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, lease.InstanceId);
        lock (state.Sync) {
            if (state.Root != lease.VersionRoot || state.PauseCount == 0 || state.Inflight != 0 || state.Patches.Count != 0 || state.IniPatches.Count != 0)
                throw new IOException("转换偏好写入需要排空的私有上下文。");
            state.AssertContext(context);
        }
        WritePreferenceConfig(state.Root, disk); MmcIniConfiguration.WriteIni(state.Root, ini);
        owner.ProducedFile("PCL/config.json", MmcDurableFiles.Hash(ConfigBytes(disk)));
        owner.ProducedFile("PCL/Setup.ini", MmcDurableFiles.Hash(new System.Text.UTF8Encoding(false).GetBytes(ini)));
        owner.RequirePrimary(lease.VersionRoot);
        state.PublishObserved(disk, context, reattach: true); state.PublishIni(ini, notify: false); state.NotifyPendingInvalidations();
    }
}
