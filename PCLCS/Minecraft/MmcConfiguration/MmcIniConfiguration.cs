using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies. Reuses LF's existing INI codec.
internal sealed record MmcIniPatch(long Sequence, string? Value, MmcConfigurationContextStamp Context);
public static partial class MmcIniConfiguration {
    [ThreadStatic] private static MmcConfigurationContextStamp? writeContext;
    [ThreadStatic] private static string? contextNamespace;
    [ThreadStatic] private static string? contextRoot;
    /// <summary>Carry an immutable instance identity across the synchronous legacy path-only writer; do not await inside the callback.</summary>
    public static void WithInstanceContext(IInstanceConfigProvider provider, Action write) {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(write);
        provider.AssertAttached();
        var previous = writeContext; var previousNamespace = contextNamespace; var previousRoot = contextRoot;
        writeContext = provider.ContextStamp; contextNamespace = provider.PreferenceCacheNamespace; contextRoot = provider.VersionRoot;
        try { write(); } finally { writeContext = previous; contextNamespace = previousNamespace; contextRoot = previousRoot; }
    }
    public static T WithInstanceContext<T>(IInstanceConfigProvider provider, Func<T> read) {
        T result = default!;
        WithInstanceContext(provider, () => {
            var state = MmcConfigurationCoordinator.StateFor(provider.VersionRoot);
            state.EnsureIniSnapshot();
            lock (state.Sync) { AssertReadWriteContext(state); if (!state.IniSnapshotValid) throw new IOException("当前实例的 INI 快照尚未确认。"); }
            result = read();
            lock (state.Sync) { AssertReadWriteContext(state); if (!state.IniSnapshotValid) throw new IOException("当前实例的 INI 快照尚未确认。"); }
        });
        return result;
    }
    public static void ValidatePreferenceWrite(IInstanceConfigProvider provider, string key) {
        provider.AssertAttached();
        if (AlwaysReserved(key) || provider.ContextStamp.Kind == MmcConfigurationContextKind.Managed && IsStructural(key))
            throw new IOException("布局或实例身份设置只能通过有代数约束的实例维护事务修改。");
    }
    private static void AssertReadWriteContext(MmcConfigurationState state) {
        if (writeContext is null) return;
        state.AssertContext(writeContext);
        if (contextRoot != state.Root || contextNamespace != MmcConfigurationCoordinator.CacheNamespace(writeContext, state.AttachmentEpoch))
            throw new IOException("实例上下文已失效，不能访问同路径的新实例。");
    }
    private static MmcConfigurationContextStamp ContextForWrite(MmcConfigurationState state, string logicalRoot) {
        if (writeContext is not null) return writeContext;
        var observed = MmcConfigurationCoordinator.ContextFor(state.Root, MmcConfigurationPaths.ReadManagement(logicalRoot, state.Root));
        // An unqualified path is never sufficient authority to write a managed instance.
        return observed.Kind == MmcConfigurationContextKind.Unmanaged ? observed : MmcConfigurationContextStamp.Unresolved;
    }
    private static readonly HashSet<string> structural = new(StringComparer.Ordinal) {
        "VersionArgumentIndie", "VersionArgumentIndieV2", "MmcInstanceId", "MmcDefinitionGeneration", "MmcManagementKind", "MmcSchemaVersion", "MmcFormatProfile", "MmcLayoutGameRoot"
    };
    private static bool AlwaysReserved(string key) => structural.Contains(key) && key is not ("VersionArgumentIndie" or "VersionArgumentIndieV2");
    internal static bool IsStructural(string key) => structural.Contains(key);
    private static bool TryRoot(string file, out string root) {
        root = "";
        if (!string.Equals(Path.GetFileName(file), "Setup.ini", StringComparison.OrdinalIgnoreCase)) return false;
        string? pcl = Path.GetDirectoryName(file), version = Path.GetDirectoryName(pcl);
        if (!string.Equals(Path.GetFileName(pcl), "PCL", StringComparison.OrdinalIgnoreCase) || version is null) return false;
        root = version; return true;
    }
    /// <summary>True means queued; false plus lease lets the caller run its unchanged ordinary synchronous writer.</summary>
    internal static bool QueueOrAdmit(string file, string key, string? value,
        ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache, out MmcAdmissionLease? admission) {
        admission = null; if (!TryRoot(file, out string root)) return false;
        if (AlwaysReserved(key)) throw new IOException("实例身份设置只能通过实例维护事务修改。");
        var state = MmcConfigurationCoordinator.StateFor(root); state.RegisterIniCache(cache, file);
        var context = ContextForWrite(state, root);
        bool queue;
        lock (state.Sync) queue = !state.KnownLegacyWritable || state.PauseCount != 0;
        if (!queue) {
            admission = MmcConfigurationCoordinator.TryPreferenceLease(state.Root);
            if (admission is null) queue = true;
            else {
                try {
                    var management = MmcConfigurationPaths.ReadManagement(root, state.Root);
                    var currentContext = MmcConfigurationCoordinator.ContextFor(state.Root, management); state.ObserveContext(currentContext);
                    lock (state.Sync) state.KnownLegacyWritable = management is { State: MmcManagementState.Legacy, ObservationComplete: true };
                    queue = !state.KnownLegacyWritable || context != currentContext;
                } catch { admission.Dispose(); admission = null; throw; }
            }
        }
        if (!queue) {
            try { MmcConfigurationCoordinator.AssertExistingVersionRoot(state.Root); return false; }
            catch { admission?.Dispose(); admission = null; throw; }
        }
        admission?.Dispose(); admission = null;
        lock (state.Sync) { AssertReadWriteContext(state); state.EnqueueIni(key, value, context); } return true;
    }
    internal static MmcAdmissionLease? AdmitSynchronousUpdate(string file, string key) {
        if (!TryRoot(file, out string root)) return null;
        var state = MmcConfigurationCoordinator.StateFor(root);
        var lease = MmcConfigurationCoordinator.PreferenceLease(state.Root, CancellationToken.None);
        try {
            MmcConfigurationCoordinator.AssertExistingVersionRoot(state.Root);
            var management = MmcConfigurationPaths.ReadManagement(root, state.Root);
            MmcConfigurationCoordinator.AssertPreferenceWritable(management);
            lock (state.Sync) AssertReadWriteContext(state);
            if (ContextForWrite(state, root) != MmcConfigurationCoordinator.ContextFor(state.Root, management)) throw new IOException("INI 写入缺少当前实例的身份上下文。");
            if (AlwaysReserved(key) || management.State != MmcManagementState.Legacy && IsStructural(key))
                throw new IOException("布局或实例身份设置只能通过有代数约束的实例维护事务修改。");
            return lease;
        } catch { lease.Dispose(); throw; }
    }
    internal static bool TryContent(string file, ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache,
        out ConcurrentDictionary<string, string>? content) {
        content = null; if (!TryRoot(file, out string root)) return false;
        var state = MmcConfigurationCoordinator.StateFor(root); state.RegisterIniCache(cache, file); state.EnsureIniSnapshot();
        lock (state.Sync) {
            AssertReadWriteContext(state);
            if (!state.IniSnapshotValid) throw new IOException("当前实例的 INI 快照尚未确认，请等待刷新后重试。");
            if (writeContext is null && state.KnownLegacyWritable && state.PauseCount == 0 && state.IniPatches.Count == 0) return false;
            content = LegacyBaseOperations.ParseIni(state.IniSnapshot);
            foreach (var patch in state.IniPatches.Where(pair => pair.Value.Context == state.Context && (state.Context.Kind != MmcConfigurationContextKind.Managed || !IsStructural(pair.Key)))) {
                if (patch.Value.Value is null) content.TryRemove(patch.Key, out _); else content[patch.Key] = patch.Value.Value;
            }
            return true;
        }
    }
    internal static void PublishedOrdinary(string file, string text) {
        if (!TryRoot(file, out string root)) return;
        MmcConfigurationCoordinator.StateFor(root).PublishIni(text);
    }
    internal static string ReadIni(string root) {
        string file = MmcConfigurationPaths.Target(root, "PCL/Setup.ini");
        if (MmcInstancePaths.Attributes(file) is null) return "";
        if (new FileInfo(file).Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例 INI 超过读取预算。");
        return LegacyBaseOperations.ReadLegacyText(file);
    }
    internal static void WriteIni(string root, string text) {
        MmcConfigurationCoordinator.AssertExistingVersionRoot(root);
        string file = MmcConfigurationPaths.Target(root, "PCL/Setup.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + ".preferences-" + Guid.NewGuid().ToString("N"); byte[] content = new UTF8Encoding(false).GetBytes(text);
        if (content.Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例 INI 写入超过预算。");
        MmcDurableFiles.WriteNew(temporary, content);
        if (MmcInstancePaths.Attributes(file) is null) File.Move(temporary, file, false); else File.Replace(temporary, file, null, false);
        if (MmcDurableFiles.Observe(file) != MmcDurableFiles.Hash(content)) throw new IOException("实例 INI 写入后校验失败。");
    }
}

internal sealed partial class MmcConfigurationState {
    internal readonly Dictionary<string, MmcIniPatch> IniPatches = new(StringComparer.Ordinal);
    private readonly List<WeakReference<Action>> invalidators = [];
    private sealed record IniCacheRegistration(WeakReference<ConcurrentDictionary<string, ConcurrentDictionary<string, string>>> Cache, string Path);
    private readonly List<IniCacheRegistration> iniCaches = [];
    internal string IniSnapshot = "";
    internal bool IniSnapshotValid;
    internal bool KnownLegacyWritable;
    internal long? LastManagementGeneration;
    private bool consumerInvalidationPending;
    internal IDisposable RegisterInvalidator(Action callback) {
        WeakReference<Action> weak = new(callback);
        lock (Sync) { invalidators.RemoveAll(reference => !reference.TryGetTarget(out _)); invalidators.Add(weak); }
        return new CacheSubscription(this, weak);
    }
    private sealed class CacheSubscription(MmcConfigurationState owner, WeakReference<Action> weak) : IDisposable {
        public void Dispose() { lock (owner.Sync) owner.invalidators.Remove(weak); }
    }
    private void InvalidatePreferenceConsumers() {
        Action[] callbacks;
        lock (Sync) {
            callbacks = invalidators.Select(weak => weak.TryGetTarget(out var callback) ? callback : null).Where(callback => callback is not null).Cast<Action>().ToArray();
            if (callbacks.Length != 0) consumerInvalidationPending = false;
        }
        // This contract only permits ConcurrentDictionary cache removal. It never raises UI events.
        foreach (Action callback in callbacks) callback();
    }
    internal void NotifyPendingInvalidations() {
        bool pending; lock (Sync) pending = consumerInvalidationPending;
        if (pending) InvalidatePreferenceConsumers();
    }
    internal void InitializeIni() => PublishIni(MmcIniConfiguration.ReadIni(Root));
    internal void RegisterIniCache(ConcurrentDictionary<string, ConcurrentDictionary<string, string>> cache, string path) {
        lock (Sync) {
            iniCaches.RemoveAll(registration => !registration.Cache.TryGetTarget(out _));
            if (!iniCaches.Any(registration => registration.Path == path && registration.Cache.TryGetTarget(out var target) && ReferenceEquals(target, cache))) iniCaches.Add(new(new(cache), path));
        }
    }
    private void ClearRegisteredIniCaches() {
        foreach (var registration in iniCaches) if (registration.Cache.TryGetTarget(out var cache)) cache.TryRemove(registration.Path, out _);
    }
    internal void EnqueueIni(string key, string? value, MmcConfigurationContextStamp context) {
        if (key.Contains(':')) throw new ArgumentException("ini 键名中不能包含冒号", nameof(key));
        key = key.ReplaceLineEndings(""); value = value?.ReplaceLineEndings("");
        lock (Sync) {
            AssertContext(context);
            if (context.Kind == MmcConfigurationContextKind.Managed && MmcIniConfiguration.IsStructural(key))
                throw new IOException("布局或实例身份设置只能通过有代数约束的实例维护事务修改。");
            IniPatches[key] = new(++sequence, value, context);
            MmcConfigurationCoordinator.PinPending(this);
            if (PauseCount == 0) Timer.Change(500, Timeout.Infinite);
        }
    }
    internal Dictionary<string, MmcIniPatch> CaptureIniPatches() { lock (Sync) return new(IniPatches, StringComparer.Ordinal); }
    internal void RejectIni(Dictionary<string, MmcIniPatch> rejected) {
        lock (Sync) foreach (var pair in rejected) {
            if (IniPatches.TryGetValue(pair.Key, out var current) && current.Sequence == pair.Value.Sequence) IniPatches.Remove(pair.Key);
            RejectedKeys.Add("Setup.ini:" + pair.Key);
            MmcConfigurationCoordinator.UnpinIfClean(this);
        }
    }
    internal void PublishIni(string text, Dictionary<string, MmcIniPatch>? applied = null, bool notify = true) {
        lock (Sync) {
            IniSnapshot = text; IniSnapshotValid = true; Epoch++;
            if (applied is not null) foreach (var pair in applied)
                if (IniPatches.TryGetValue(pair.Key, out var current) && current.Sequence == pair.Value.Sequence) IniPatches.Remove(pair.Key);
            foreach (var registration in iniCaches) if (registration.Cache.TryGetTarget(out var cache)) cache[registration.Path] = LegacyBaseOperations.ParseIni(text);
            MmcConfigurationCoordinator.UnpinIfClean(this);
            if (!notify) consumerInvalidationPending = true;
        }
        if (notify) InvalidatePreferenceConsumers();
    }
    internal bool FlushIni(Dictionary<string, MmcIniPatch> pending, MmcManagementResult management, MmcConfigurationContextStamp context) {
        if (pending.Count == 0) {
            bool refresh; lock (Sync) refresh = !IniSnapshotValid;
            if (refresh) PublishIni(MmcIniConfiguration.ReadIni(Root), notify: false);
            return false;
        }
        var rejected = pending.Where(p => MmcIniConfiguration.IsStructural(p.Key) &&
            (management.State != MmcManagementState.Legacy || p.Key is not ("VersionArgumentIndie" or "VersionArgumentIndieV2")) || p.Value.Context != context)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (string key in rejected.Keys) pending.Remove(key); RejectIni(rejected);
        string current = MmcIniConfiguration.ReadIni(Root);
        foreach (var pair in pending) current = LegacyBaseOperations.ReplaceIniValue(current, pair.Key, pair.Value.Value);
        if (pending.Count != 0) MmcIniConfiguration.WriteIni(Root, current);
        PublishIni(current, pending); return rejected.Count != 0;
    }
}

public sealed partial class MmcConfigurationWriteScope {
    private string ini = "";
    private string? oldIniHash;
    private bool iniTouched;
    private Dictionary<string, MmcIniPatch> acceptedIni = new(StringComparer.Ordinal);
    private void InitializeIniScope(Func<MmcConfigurationContextStamp, bool> acceptContext) {
        string file = MmcInstancePaths.Resolve(state.Root).ResolveRelative(MmcPathRole.Instance, "PCL/Setup.ini");
        oldIniHash = MmcDurableFiles.Observe(file); ini = MmcIniConfiguration.ReadIni(state.Root);
        acceptedIni = state.CaptureIniPatches();
        var rejected = acceptedIni.Where(p => MmcIniConfiguration.IsStructural(p.Key) || !acceptContext(p.Value.Context)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (string key in rejected.Keys) acceptedIni.Remove(key); state.RejectIni(rejected);
        foreach (var pair in acceptedIni) ini = LegacyBaseOperations.ReplaceIniValue(ini, pair.Key, pair.Value.Value);
        iniTouched = acceptedIni.Count != 0;
    }
    public void SetIni(string key, string? value) {
        ObjectDisposedException.ThrowIf(disposed, this); lease.AssertHeld();
        if (key.Contains(':')) throw new ArgumentException("ini 键名中不能包含冒号", nameof(key));
        ini = LegacyBaseOperations.ReplaceIniValue(ini, key.ReplaceLineEndings(""), value?.ReplaceLineEndings("")); iniTouched = true;
    }
    private IEnumerable<MmcFileMutation> BuildIniMutation() {
        if (!iniTouched) yield break;
        byte[] bytes = new UTF8Encoding(false).GetBytes(ini);
        yield return new("PCL/Setup.ini", oldIniHash, bytes, MmcDurableFiles.Hash(bytes));
    }
    private void PublishIniScope(bool committed) => state.PublishIni(MmcIniConfiguration.ReadIni(state.Root), committed ? acceptedIni : null);
}
