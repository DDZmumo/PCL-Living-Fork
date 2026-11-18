using System.Collections.Immutable;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcInstancePreferenceView(ImmutableDictionary<string, string> Ini, JObject Config);
public static partial class MmcIniConfiguration {
    /// <summary>Queues one explicit user operation. Values/modes share one queue sequence and overlay visibility point; this is not a crash-atomic two-file journal.</summary>
    public static void QueuePreferenceBatch(IInstanceConfigProvider provider, IReadOnlyDictionary<string, string?> ini, JObject? config = null) {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(ini); provider.AssertAttached();
        if (provider.ContextStamp.Kind != MmcConfigurationContextKind.Managed) throw new IOException("批量 MMC 偏好写入需要已确认的实例身份。");
        if (ini.Count + (config?.Count ?? 0) is < 1 or > 64) throw new IOException("实例偏好批量键数量无效。");
        foreach (var pair in ini) {
            ValidatePreferenceWrite(provider, pair.Key);
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.IndexOfAny([':', '\r', '\n']) >= 0 || pair.Value?.IndexOfAny(['\r', '\n']) >= 0)
                throw new IOException("实例偏好键和值不能包含 INI 换行或冒号键名。");
        }
        if (config is not null) foreach (var property in config.Properties())
            if (MmcConfigurationCoordinator.IsStructuralKey(property.Name)) throw new IOException("实例结构不能作为用户偏好排队。");
        var state = MmcConfigurationCoordinator.StateFor(provider.VersionRoot);
        lock (state.Sync) {
            state.AssertContext(provider.ContextStamp);
            if (provider.PreferenceCacheNamespace != MmcConfigurationCoordinator.CacheNamespace(provider.ContextStamp, state.AttachmentEpoch))
                throw new IOException("实例偏好提供器已分离。");
            long sequence = ++state.sequence;
            foreach (var pair in ini) state.IniPatches[pair.Key] = new(sequence, pair.Value, provider.ContextStamp);
            if (config is not null) foreach (var property in config.Properties()) state.Patches[property.Name] = new(sequence, property.Value.DeepClone(), false, provider.ContextStamp);
            MmcConfigurationCoordinator.PinPending(state);
            if (state.PauseCount == 0) state.Timer.Change(500, Timeout.Infinite);
        }
    }
    /// <summary>Fresh disk plus current-identity JSON/INI overlays, captured in one local synchronization view under cross-process admission.</summary>
    public static MmcInstancePreferenceView CapturePreferences(IInstanceConfigProvider provider) {
        ArgumentNullException.ThrowIfNull(provider); provider.AssertAttached();
        return MmcConfigurationCoordinator.StateFor(provider.VersionRoot).CapturePreferences(provider);
    }
}
internal sealed partial class MmcConfigurationState {
    internal MmcInstancePreferenceView CapturePreferences(IInstanceConfigProvider provider) {
        bool registered = BeginOperation();
        try {
            using var admission = registered ? MmcConfigurationCoordinator.PreferenceLease(Root, CancellationToken.None) :
                MmcConfigurationCoordinator.TryPreferenceLease(Root) ?? throw new IOException("实例正在提交结构，无法读取启动偏好。");
            MmcConfigurationCoordinator.AssertExistingVersionRoot(Root);
            var management = MmcConfigurationPaths.ReadManagement(InspectionRoot, Root);
            MmcConfigurationCoordinator.AssertPreferenceWritable(management);
            var context = MmcConfigurationCoordinator.ContextFor(Root, management); ObserveContext(context);
            JObject config = MmcConfigurationCoordinator.ReadConfig(Root);
            var ini = LegacyBaseOperations.ParseIni(MmcIniConfiguration.ReadIni(Root));
            lock (Sync) {
                AssertContext(provider.ContextStamp);
                if (provider.ContextStamp != context || provider.PreferenceCacheNamespace != MmcConfigurationCoordinator.CacheNamespace(context, AttachmentEpoch))
                    throw new IOException("启动偏好的实例身份已经变化。");
                foreach (var pair in Patches.Where(pair => pair.Value.Context == context && !MmcConfigurationCoordinator.IsStructuralKey(pair.Key))) {
                    if (pair.Value.Remove) config.Remove(pair.Key); else config[pair.Key] = pair.Value.Value?.DeepClone() ?? JValue.CreateNull();
                }
                foreach (var pair in IniPatches.Where(pair => pair.Value.Context == context && !MmcIniConfiguration.IsStructural(pair.Key))) {
                    if (pair.Value.Value is null) ini.TryRemove(pair.Key, out _); else ini[pair.Key] = pair.Value.Value;
                }
                return new(ini.ToImmutableDictionary(StringComparer.Ordinal), config);
            }
        } finally { if (registered) EndOperation(); NotifyPendingInvalidations(); }
    }
}
