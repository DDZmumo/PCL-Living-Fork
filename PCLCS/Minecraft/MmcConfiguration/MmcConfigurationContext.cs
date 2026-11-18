using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public enum MmcConfigurationContextKind { Unmanaged, Managed, Unresolved }
public sealed record MmcConfigurationContextStamp(MmcConfigurationContextKind Kind, Guid InstanceId) {
    public static MmcConfigurationContextStamp Unmanaged { get; } = new(MmcConfigurationContextKind.Unmanaged, Guid.Empty);
    public static MmcConfigurationContextStamp Unresolved { get; } = new(MmcConfigurationContextKind.Unresolved, Guid.Empty);
}

public static partial class MmcConfigurationCoordinator {
    internal static string CacheNamespace(MmcConfigurationContextStamp context, long attachmentEpoch) =>
        context.Kind == MmcConfigurationContextKind.Unmanaged && attachmentEpoch == 0 ? "" :
            "\0LF-instance:" + context.Kind + ":" + context.InstanceId.ToString("N") + ":" + attachmentEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static MmcConfigurationContextStamp ContextFor(string root, MmcManagementResult management) {
        if (!Directory.Exists(root)) return MmcConfigurationContextStamp.Unresolved;
        if (management is { State: MmcManagementState.Legacy, ObservationComplete: true }) return MmcConfigurationContextStamp.Unmanaged;
        return management is { State: MmcManagementState.ManagedMmc, Envelope: not null } ?
            new(MmcConfigurationContextKind.Managed, management.Envelope.InstanceId) : MmcConfigurationContextStamp.Unresolved;
    }
    internal static MmcConfigurationContextStamp AttachProvider(MmcConfigurationState state, string logicalRoot) {
        using var admission = TryPreferenceLease(state.Root);
        try {
            var management = MmcConfigurationPaths.ReadManagement(logicalRoot, state.Root);
            var context = ContextFor(state.Root, management); bool refreshPreferences;
            lock (state.Sync) refreshPreferences = context != state.Context || management.Envelope?.Generation != state.LastManagementGeneration;
            if (context.Kind != MmcConfigurationContextKind.Unresolved) { lock (state.Sync) state.InspectionRoot = logicalRoot; }
            if (admission is not null && Directory.Exists(state.Root)) {
                state.PublishObserved(ReadConfig(state.Root), context, reattach: true);
                if (refreshPreferences && context.Kind != MmcConfigurationContextKind.Unresolved) state.PublishIni(MmcIniConfiguration.ReadIni(state.Root), notify: false);
                lock (state.Sync) state.LastManagementGeneration = management.Envelope?.Generation;
            }
            else state.ObserveContext(context);
            return context;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) {
            state.LastSaveFailure = error; state.InvalidateReads(); return MmcConfigurationContextStamp.Unresolved;
        }
    }
    internal static void AssertExistingVersionRoot(string root) {
        if (MmcInstancePaths.Attributes(root) is not FileAttributes attributes || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("实例目录已不存在，待写入设置已保留，禁止自动重建目录。");
    }
    /// <summary>Explicit deletion/fixture cleanup only. A live/recreated directory can never be detached here.</summary>
    public static void DetachDeletedRoot(string versionRoot) {
        using var quiet = Quiesce(versionRoot);
        using var lease = PreferenceLease(quiet.State.Root, CancellationToken.None);
        if (MmcInstancePaths.Attributes(quiet.State.Root) is not null) throw new IOException("目录仍存在，不能丢弃其待写入设置。");
        lock (quiet.State.Sync) {
            quiet.State.Detached = true; quiet.State.AttachmentEpoch++; quiet.State.Patches.Clear(); quiet.State.IniPatches.Clear();
            quiet.State.Timer.Change(Timeout.Infinite, Timeout.Infinite);
            quiet.State.LastSaveFailure = new IOException("已由明确删除流程丢弃旧实例的待写入设置。");
            UnpinIfClean(quiet.State);
        }
    }
}

internal sealed partial class MmcConfigurationState {
    internal MmcConfigurationContextStamp Context = MmcConfigurationContextStamp.Unresolved;
    internal bool Detached;
    internal long AttachmentEpoch;
    internal void PublishObserved(JObject config, MmcConfigurationContextStamp context, bool reattach = false) {
        lock (Sync) { if (reattach && context.Kind != MmcConfigurationContextKind.Unresolved) Detached = false; ObserveContext(context); Publish(config); }
    }
    internal void ObserveContext(MmcConfigurationContextStamp context) {
        if (context.Kind == MmcConfigurationContextKind.Unresolved) return;
        lock (Sync) {
            if (Context != context) {
                Reject(Patches.Where(pair => pair.Value.Context != context).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
                RejectIni(IniPatches.Where(pair => pair.Value.Context != context).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
                Context = context; Epoch++; snapshot = []; snapshotValid = false; readsInvalid = true;
                IniSnapshot = ""; IniSnapshotValid = false; LastManagementGeneration = null;
                KnownLegacyWritable = context.Kind == MmcConfigurationContextKind.Unmanaged;
                consumerInvalidationPending = true; ClearRegisteredIniCaches();
            }
        }
    }
    internal void AssertContext(MmcConfigurationContextStamp context, long? attachmentEpoch = null) {
        if (Detached) throw new IOException("实例已经分离，旧配置提供器不能再次写入。");
        if (attachmentEpoch is not null && attachmentEpoch != AttachmentEpoch) throw new IOException("旧配置提供器已经分离，不能接管同路径的新实例。");
        if (context.Kind == MmcConfigurationContextKind.Unresolved || Context.Kind != MmcConfigurationContextKind.Unresolved && context != Context)
            throw new IOException("配置提供器的实例身份已经变化，请重新读取当前实例。");
    }
}
