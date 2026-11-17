using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public static partial class MmcConfigurationCoordinator {
    // This is only a path-gate admission identity before reading disk, never an instance identity.
    private static readonly Guid PreferenceAdmissionIdentity = new("649a06b5-2b88-4f56-9abd-139c4d39cdf9");
    private static readonly ConcurrentDictionary<string, WeakReference<MmcConfigurationState>> states = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, MmcConfigurationState> pendingStates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal static Action<string, string>? OperationObserver { get; set; }
    internal static void PinPending(MmcConfigurationState state) => pendingStates[state.Root] = state;
    internal static void UnpinIfClean(MmcConfigurationState state) {
        if (state.Patches.Count == 0 && state.IniPatches.Count == 0)
            ((ICollection<KeyValuePair<string, MmcConfigurationState>>)pendingStates).Remove(new(state.Root, state));
    }
    internal static void FlushPending(string? ownedRoot = null) {
        string? prefix = ownedRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        List<Exception> errors = [];
        foreach (var state in pendingStates.Values) {
            if (prefix is not null && !Within(prefix, state.Root)) continue;
            try { state.Flush(CancellationToken.None); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("实例待写入设置尚未全部落盘。", errors);
    }
    internal static bool Within(string root, string path) => string.Equals(root, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static readonly HashSet<string> structural = new(StringComparer.Ordinal) {
        "MinecraftInstallManifest", "pcllfManagement", "instanceId", "generation", "managementKind", "schemaVersion", "formatProfile", "layout",
        "sourceBundle", "artifactInventory", "artifactLocks", "installedProjection", "componentOverrides"
    };
    internal static MmcConfigurationState StateFor(string versionRoot) {
        string logicalRoot = MmcConfigurationPaths.LogicalRoot(versionRoot), root = MmcConfigurationPaths.PhysicalRoot(logicalRoot);
        while (true) {
            if (states.TryGetValue(root, out var weak) && weak.TryGetTarget(out var found)) return found;
            var created = new MmcConfigurationState(root, logicalRoot); var candidate = new WeakReference<MmcConfigurationState>(created);
            if (weak is null ? states.TryAdd(root, candidate) : states.TryUpdate(root, candidate, weak)) return created;
            created.Timer.Dispose();
        }
    }
    /// <summary>Pause/drain before acquiring AdmissionGate; do not call while holding UI/Worker/data locks.</summary>
    public static MmcConfigurationQuiescence Quiesce(string versionRoot, CancellationToken cancellationToken = default) {
        MmcConfigurationState state = StateFor(versionRoot);
        lock (state.Sync) { state.PauseCount++; state.Resumed.Reset(); state.Timer.Change(Timeout.Infinite, Timeout.Infinite); }
        try { OperationObserver?.Invoke(state.Root, "QuiescePaused"); state.Drained.Wait(cancellationToken); return new(state, MmcConfigurationPaths.LogicalRoot(versionRoot)); }
        catch {
            lock (state.Sync) { if (--state.PauseCount == 0) state.Resumed.Set(); }
            throw;
        }
    }
    public static bool IsStructuralKey(string key) => structural.Contains(key);
    internal static bool IsAlwaysReserved(string key) => key != "MinecraftInstallManifest" && structural.Contains(key);
    internal static MmcAdmissionLease PreferenceLease(string root, CancellationToken token) => MmcAdmissionGate.AcquireConfigurationRoot(root, PreferenceAdmissionIdentity, Timeout.InfiniteTimeSpan, token);
    internal static MmcAdmissionLease? TryPreferenceLease(string root) {
        try { return MmcAdmissionGate.AcquireConfigurationRoot(root, PreferenceAdmissionIdentity, TimeSpan.Zero); }
        catch (IOException) { return null; }
    }
    internal static void AssertPreferenceWritable(MmcManagementResult management) {
        if (management.State == MmcManagementState.Legacy && management.ObservationComplete || management.State == MmcManagementState.ManagedMmc) return;
        throw new IOException("实例管理状态尚未确认，配置修改已排队并等待恢复。");
    }
    internal static JObject ReadConfig(string root) => ReadJsonObject(root, "PCL/config.json");
    internal static JObject ReadJsonObject(string root, string relative) {
        string path = MmcConfigurationPaths.Target(root, relative);
        if (MmcInstancePaths.Attributes(path) is null) return [];
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例配置超过读取预算。");
        using MemoryStream bytes = new(); byte[] buffer = new byte[8192]; int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) != 0) {
            if (bytes.Length + count > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例配置读取期间超过预算。");
            bytes.Write(buffer, 0, count);
        }
        string text = LegacyBaseOperations.DecodeLegacyText(bytes.ToArray());
        if (Encoding.UTF8.GetByteCount(text) > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例配置读取期间超过预算。");
        try { return JObject.Parse(text, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
        catch (Newtonsoft.Json.JsonException) { throw new IOException("实例配置 JSON 无效，已保留原文件。"); }
    }
    internal static void Apply(JObject target, Dictionary<string, MmcConfigPatch> patches) {
        foreach (var pair in patches) {
            if (pair.Value.Remove) target.Remove(pair.Key);
            else target[pair.Key] = pair.Value.Value?.DeepClone() ?? JValue.CreateNull();
        }
    }
    internal static byte[] ConfigBytes(JObject value) {
        byte[] bytes = new UTF8Encoding(false).GetBytes(value.ToString(Formatting.Indented));
        if (bytes.Length > MmcManagementReader.MaximumDocumentBytes) throw new IOException("实例配置写入超过预算。");
        return bytes;
    }
    internal static void WritePreferenceConfig(string root, JObject value) {
        AssertExistingVersionRoot(root);
        string path = MmcConfigurationPaths.Target(root, "PCL/config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".preferences-" + Guid.NewGuid().ToString("N");
        byte[] bytes = ConfigBytes(value); MmcDurableFiles.WriteNew(temporary, bytes);
        if (MmcInstancePaths.Attributes(path) is null) File.Move(temporary, path, false);
        else File.Replace(temporary, path, null, false);
        if (MmcDurableFiles.Observe(path) != MmcDurableFiles.Hash(bytes)) throw new IOException("实例配置写入后校验失败。");
    }
}

public sealed partial class MmcConfigurationQuiescence : IDisposable {
    internal readonly MmcConfigurationState State;
    private readonly string logicalRoot;
    private bool disposed;
    internal MmcConfigurationQuiescence(MmcConfigurationState state, string logicalRoot) { State = state; this.logicalRoot = logicalRoot; }
    public MmcConfigurationWriteScope OpenStructuralWrite(MmcAdmissionLease lease, long expectedGeneration, bool adoptUnmanagedPreferences = false) {
        ObjectDisposedException.ThrowIf(disposed, this); lease.AssertHeld();
        MmcInstancePaths.Resolve(logicalRoot); // Strict MMC publication never inherits ordinary root trust.
        if (!string.Equals(lease.VersionRoot, State.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("设置协调目录与准入租约不一致。");
        return new(State, lease, expectedGeneration, adoptUnmanagedPreferences);
    }
    public void Dispose() {
        if (disposed) return; disposed = true;
        lock (State.Sync) {
            if (--State.PauseCount == 0) { State.Resumed.Set(); if (State.Patches.Count != 0 || State.IniPatches.Count != 0) State.Timer.Change(1, Timeout.Infinite); }
        }
    }
}

/// <summary>Scoped key merge feeding the file transaction; never recursively calls public Save.</summary>
public sealed partial class MmcConfigurationWriteScope : IDisposable {
    private readonly MmcConfigurationState state;
    private readonly MmcAdmissionLease lease;
    private readonly long generation;
    private readonly string? oldConfigHash;
    private readonly JObject config;
    private readonly Dictionary<string, MmcConfigPatch> accepted;
    private readonly string? repairPrimaryHash;
    private bool disposed;
    public JObject ConfigSnapshot => (JObject)config.DeepClone();
    internal MmcConfigurationWriteScope(MmcConfigurationState state, MmcAdmissionLease lease, long generation, bool adoptUnmanagedPreferences, string? repairPrimaryHash = null) {
        this.state = state; this.lease = lease; this.generation = generation; this.repairPrimaryHash = repairPrimaryHash;
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        var paths = MmcInstancePaths.Resolve(state.Root);
        string definitionPath = paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json");
        if (generation == 0) {
            if (MmcInstancePaths.Attributes(definitionPath) is not null) throw new IOException("初始发布目标已有定义。");
        } else {
            if (repairPrimaryHash is not null) {
                if (MmcManagementProbe.Read(state.Root).State == MmcManagementState.ManagedUnsupported) throw new IOException("未知管理记录格式，禁止按旧格式重建投影。");
                ValidateRepairPrimary(definitionPath, repairPrimaryHash, lease.InstanceId, generation);
            }
            else {
                var definition = MmcManagementProbe.Read(state.Root).Envelope;
                if (definition is null || definition.InstanceId != lease.InstanceId || definition.Generation != generation) throw new IOException("结构设置的 ExpectedGeneration 已过期或定义尚不可验证。");
            }
        }
        oldConfigHash = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/config.json"));
        config = MmcConfigurationCoordinator.ReadConfig(state.Root);
        accepted = state.CapturePatches();
        var expectedContext = new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, lease.InstanceId);
        bool AcceptContext(MmcConfigurationContextStamp context) => context == expectedContext || generation == 0 && adoptUnmanagedPreferences && context == MmcConfigurationContextStamp.Unmanaged;
        var rejected = accepted.Where(p => MmcConfigurationCoordinator.IsStructuralKey(p.Key) || !AcceptContext(p.Value.Context)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (string key in rejected.Keys) accepted.Remove(key);
        state.Reject(rejected); MmcConfigurationCoordinator.Apply(config, accepted);
        InitializeIniScope(AcceptContext);
    }
    public void SetConfig<T>(string key, T? value) {
        ObjectDisposedException.ThrowIf(disposed, this); lease.AssertHeld(); config[key] = value is null ? JValue.CreateNull() : JToken.FromObject(value);
    }
    public void RemoveConfig(string key) { ObjectDisposedException.ThrowIf(disposed, this); lease.AssertHeld(); config.Remove(key); }
    public MmcTransactionResult Execute(MmcFileTransactionRequest request, CancellationToken cancellationToken = default,
        Action<MmcTransactionCheckpoint, int>? checkpoint = null) {
        ObjectDisposedException.ThrowIf(disposed, this); lease.AssertHeld();
        if (request.ExpectedGeneration != generation || request.InstanceId != lease.InstanceId) throw new IOException("结构设置事务与 ExpectedGeneration 不一致。");
        ValidateRepairPublication(request);
        if (request.Mutations.Any(m => string.Equals(m.RelativePath, "PCL/config.json", StringComparison.OrdinalIgnoreCase))) throw new IOException("配置内容只能由当前设置协调作用域合成。");
        if (request.Mutations.Any(m => string.Equals(m.RelativePath, "PCL/Setup.ini", StringComparison.OrdinalIgnoreCase))) throw new IOException("INI 设置只能由当前设置协调作用域按键合成。");
        byte[] bytes = MmcConfigurationCoordinator.ConfigBytes(config);
        var mutations = request.Mutations.Concat([new MmcFileMutation("PCL/config.json", oldConfigHash, bytes, MmcDurableFiles.Hash(bytes))]).Concat(BuildIniMutation()).ToArray();
        MmcTransactionResult result = MmcFileTransactions.Execute(lease, request with { Mutations = mutations }, cancellationToken, checkpoint);
        if (result.Outcome == MmcTransactionOutcome.Committed) { state.ObserveContext(MmcConfigurationCoordinator.ContextFor(state.Root, MmcManagementProbe.Read(state.Root))); state.Publish(MmcConfigurationCoordinator.ReadConfig(state.Root), accepted); PublishIniScope(true); }
        else if (result.Outcome == MmcTransactionOutcome.Aborted) { state.Publish(MmcConfigurationCoordinator.ReadConfig(state.Root)); PublishIniScope(false); }
        else state.InvalidateReads();
        return result;
    }
    public void Dispose() => disposed = true;
}
