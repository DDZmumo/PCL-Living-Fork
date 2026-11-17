using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcConfigPatch(long Sequence, JToken? Value, bool Remove, MmcConfigurationContextStamp Context);
internal readonly record struct MmcConfigValueVersion(long Epoch, long PatchSequence);
internal sealed partial class MmcConfigurationState {
    internal readonly string Root;
    internal string InspectionRoot;
    internal readonly object Sync = new();
    internal readonly SemaphoreSlim Writer = new(1, 1);
    internal readonly ManualResetEventSlim Resumed = new(true);
    internal readonly ManualResetEventSlim Drained = new(true);
    internal readonly Timer Timer;
    internal readonly Dictionary<string, MmcConfigPatch> Patches = new(StringComparer.Ordinal);
    internal readonly List<string> RejectedKeys = [];
    internal int PauseCount, ProviderCount, Inflight;
    internal long Epoch;
    internal Exception? LastSaveFailure;
    internal long sequence;
    private JObject snapshot;
    private bool snapshotValid;
    private bool readsInvalid;

    internal MmcConfigurationState(string root, string logicalRoot) {
        Root = root; InspectionRoot = logicalRoot;
        snapshot = [];
        using (var lease = MmcConfigurationCoordinator.TryPreferenceLease(root)) {
            if (lease is null) readsInvalid = true;
            else {
                try {
                    snapshot = MmcConfigurationCoordinator.ReadConfig(root); InitializeIni();
                    var management = MmcConfigurationPaths.ReadManagement(logicalRoot, root);
                    KnownLegacyWritable = management is { State: MmcManagementState.Legacy, ObservationComplete: true };
                    ObserveContext(MmcConfigurationCoordinator.ContextFor(root, management));
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { readsInvalid = true; LastSaveFailure = error; }
            }
        }
        Timer = new(static value => {
            if (value is WeakReference<MmcConfigurationState> weak && weak.TryGetTarget(out var state)) state.AutoFlush();
        }, new WeakReference<MmcConfigurationState>(this), Timeout.Infinite, Timeout.Infinite);
    }
    internal MmcConfigValueVersion Enqueue(string key, JToken? value, bool remove, MmcConfigurationContextStamp context) {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("配置键不能为空。", nameof(key));
        lock (Sync) {
            AssertContext(context);
            if (MmcConfigurationCoordinator.IsAlwaysReserved(key) || context.Kind == MmcConfigurationContextKind.Managed && MmcConfigurationCoordinator.IsStructuralKey(key))
                throw new IOException("实例结构配置只能通过有 ExpectedGeneration 的维护事务修改。");
            Patches[key] = new(++sequence, value?.DeepClone(), remove, context);
            MmcConfigurationCoordinator.PinPending(this);
            if (PauseCount == 0) Timer.Change(500, Timeout.Infinite);
            return new(Epoch, sequence);
        }
    }
    internal JToken? Read(string key, MmcConfigurationContextStamp context, long attachmentEpoch, out bool present, out MmcConfigValueVersion version) {
        bool refresh;
        lock (Sync) refresh = readsInvalid;
        if (refresh) Reload();
        lock (Sync) {
            AssertContext(context, attachmentEpoch);
            if (Patches.TryGetValue(key, out var patch) && patch.Context == context && !MmcConfigurationCoordinator.IsAlwaysReserved(key) &&
                (context.Kind != MmcConfigurationContextKind.Managed || !MmcConfigurationCoordinator.IsStructuralKey(key))) {
                version = new(Epoch, patch.Sequence); present = !patch.Remove; return patch.Value?.DeepClone();
            }
            if (!snapshotValid) throw new IOException("当前实例的设置快照尚未确认，请等待刷新后重试。");
            version = new(Epoch, 0);
            present = snapshot.TryGetValue(key, out var token); return token?.DeepClone();
        }
    }
    internal void InvalidateReads() { lock (Sync) { readsInvalid = true; Epoch++; } }
    internal Dictionary<string, MmcConfigPatch> CapturePatches() { lock (Sync) return new(Patches, StringComparer.Ordinal); }
    internal void Publish(JObject disk, Dictionary<string, MmcConfigPatch>? applied = null) {
        lock (Sync) {
            snapshot = (JObject)disk.DeepClone(); snapshotValid = true; readsInvalid = false; Epoch++;
            if (applied is not null) foreach (var pair in applied)
                if (Patches.TryGetValue(pair.Key, out var current) && current.Sequence == pair.Value.Sequence) Patches.Remove(pair.Key);
            MmcConfigurationCoordinator.UnpinIfClean(this);
        }
    }
    internal void Reject(Dictionary<string, MmcConfigPatch> rejected) {
        lock (Sync) foreach (var pair in rejected) {
            if (Patches.TryGetValue(pair.Key, out var current) && current.Sequence == pair.Value.Sequence) Patches.Remove(pair.Key);
            RejectedKeys.Add(pair.Key);
            MmcConfigurationCoordinator.UnpinIfClean(this);
        }
    }
    internal void Flush(CancellationToken token) {
        while (true) {
            lock (Sync) { if (Patches.Count == 0 && IniPatches.Count == 0) return; }
            Resumed.Wait(token);
            if (!BeginOperation()) continue;
            try {
                MmcConfigurationCoordinator.OperationObserver?.Invoke(Root, "Registered");
                using var lease = MmcConfigurationCoordinator.PreferenceLease(Root, token);
                Writer.Wait(token);
                try { FlushUnderWriter(token); return; }
                finally { Writer.Release(); }
            } finally { EndOperation(); }
        }
    }
    private void FlushUnderWriter(CancellationToken token) {
        var pending = CapturePatches(); var iniPending = CaptureIniPatches(); if (pending.Count == 0 && iniPending.Count == 0) return;
        try {
            MmcConfigurationCoordinator.AssertExistingVersionRoot(Root);
            var management = MmcConfigurationPaths.ReadManagement(InspectionRoot, Root);
            MmcConfigurationCoordinator.AssertPreferenceWritable(management);
            var context = MmcConfigurationCoordinator.ContextFor(Root, management); ObserveContext(context);
            lock (Sync) KnownLegacyWritable = management.State == MmcManagementState.Legacy;
            JObject current = MmcConfigurationCoordinator.ReadConfig(Root);
            var rejected = pending.Where(p => MmcConfigurationCoordinator.IsAlwaysReserved(p.Key) ||
                management.State != MmcManagementState.Legacy && MmcConfigurationCoordinator.IsStructuralKey(p.Key) || p.Value.Context != context).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (string key in rejected.Keys) pending.Remove(key);
            Reject(rejected);
            MmcConfigurationCoordinator.Apply(current, pending);
            if (pending.Count != 0) MmcConfigurationCoordinator.WritePreferenceConfig(Root, current);
            Publish(current, pending); LastSaveFailure = null;
            bool iniRejected = FlushIni(iniPending, management, context);
            if (rejected.Count != 0 || iniRejected) throw new IOException("拒绝过期或未授权的实例结构配置修改。请通过实例维护流程修改。");
        } catch (Exception error) { LastSaveFailure = error; throw; }
    }
    private void Reload() {
        bool registered = BeginOperation();
        // During quiescence a reader never waits for a foreign admission owner. A reader already
        // inside admission can reenter the named mutex and refresh under Admission -> Writer order.
        MmcAdmissionLease? lease = registered ? null : MmcConfigurationCoordinator.TryPreferenceLease(Root);
        if (!registered && lease is null) return;
        try {
            lease ??= MmcConfigurationCoordinator.PreferenceLease(Root, CancellationToken.None);
            Writer.Wait();
            try { PublishObserved(MmcConfigurationCoordinator.ReadConfig(Root), MmcConfigurationCoordinator.ContextFor(Root, MmcConfigurationPaths.ReadManagement(InspectionRoot, Root))); InitializeIni(); }
            finally { Writer.Release(); }
        } finally { lease?.Dispose(); if (registered) EndOperation(); }
    }
    internal void EnsureIniSnapshot() {
        bool refresh; lock (Sync) refresh = !IniSnapshotValid;
        if (refresh) Reload();
    }
    private bool BeginOperation() {
        lock (Sync) { if (PauseCount != 0) return false; Inflight++; Drained.Reset(); return true; }
    }
    private void EndOperation() {
        lock (Sync) { if (--Inflight == 0) Drained.Set(); }
    }
    private void AutoFlush() {
        lock (Sync) { if (PauseCount != 0) return; }
        try { Flush(CancellationToken.None); }
        catch (Exception error) { LastSaveFailure = error; } // Exposed to explicit flush/shutdown; never invoke UI under a lease.
    }
}
