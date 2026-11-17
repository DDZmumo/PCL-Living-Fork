using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies. No Shared implementation is copied.
public interface IInstanceConfigProvider : IConfigProvider, IDisposable {
    string VersionRoot { get; }
    MmcConfigurationContextStamp ContextStamp { get; }
    string PreferenceCacheNamespace { get; }
    void AssertAttached();
    void EnsurePreferenceCacheInvalidator(Action invalidatePreferenceCache);
    void Save();
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Instance-only key-patch provider. Global/account providers retain their existing implementation.</summary>
public sealed class InstanceConfigProvider : IInstanceConfigProvider {
    private sealed record TypedValue(MmcConfigValueVersion Version, object? Value);
    private readonly ConcurrentDictionary<string, TypedValue> typedValues = new(StringComparer.Ordinal);
    private readonly MmcConfigurationState state;
    private readonly long attachmentEpoch;
    private Action? cacheInvalidator;
    private IDisposable? cacheRegistration;
    private bool disposed;
    public string VersionRoot => state.Root;
    public MmcConfigurationContextStamp ContextStamp { get; }
    public string PreferenceCacheNamespace { get; }
    public long Epoch => state.Epoch;
    public Exception? LastSaveFailure => state.LastSaveFailure;
    /// <param name="invalidatePreferenceCache">Optional data-only callback: only clear thread-safe value caches; never UI, Settings.Get/Set, disk I/O or synchronous dispatch. Not called during construction.</param>
    public InstanceConfigProvider(string versionRoot, Action? invalidatePreferenceCache = null) {
        state = MmcConfigurationCoordinator.StateFor(versionRoot);
        ContextStamp = MmcConfigurationCoordinator.AttachProvider(state, MmcConfigurationPaths.LogicalRoot(versionRoot));
        lock (state.Sync) { state.ProviderCount++; attachmentEpoch = state.AttachmentEpoch; }
        PreferenceCacheNamespace = MmcConfigurationCoordinator.CacheNamespace(ContextStamp, attachmentEpoch);
        cacheInvalidator = invalidatePreferenceCache;
        if (cacheInvalidator is not null) cacheRegistration = state.RegisterInvalidator(cacheInvalidator);
        InstanceConfigProviders.Register(this);
    }
    public void Set<T>(string key, T? value, bool encrypted) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (encrypted && value is not null) throw new NotImplementedException("加密需要改为使用识别码，现在尚未实现。");
        AssertAttached();
        JToken token = value is null ? JValue.CreateNull() : JToken.FromObject(value);
        lock (state.Sync) { ObjectDisposedException.ThrowIf(disposed, this); state.AssertContext(ContextStamp, attachmentEpoch); typedValues[key] = new(state.Enqueue(key, token, remove: false, ContextStamp), value); }
    }
    public void Remove(string key) { AssertAttached(); lock (state.Sync) { ObjectDisposedException.ThrowIf(disposed, this); state.AssertContext(ContextStamp, attachmentEpoch); state.Enqueue(key, null, remove: true, ContextStamp); typedValues.TryRemove(key, out _); } }
    public void AssertAttached() {
        lock (state.Sync) { ObjectDisposedException.ThrowIf(disposed, this); state.AssertContext(ContextStamp, attachmentEpoch); }
        state.NotifyPendingInvalidations();
    }
    /// <summary>Register once after model construction; the callback must only remove thread-safe cached data, with no UI, host factory, settings access or I/O.</summary>
    public void EnsurePreferenceCacheInvalidator(Action invalidatePreferenceCache) {
        ArgumentNullException.ThrowIfNull(invalidatePreferenceCache);
        lock (state.Sync) {
            ObjectDisposedException.ThrowIf(disposed, this); state.AssertContext(ContextStamp, attachmentEpoch);
            if (cacheRegistration is not null) return;
            cacheInvalidator = invalidatePreferenceCache; cacheRegistration = state.RegisterInvalidator(cacheInvalidator);
        }
    }
    public T? Read<T>(string key, T? defaultValue, bool encrypted) {
        ObjectDisposedException.ThrowIf(disposed, this);
        AssertAttached();
        JToken? token = state.Read(key, ContextStamp, attachmentEpoch, out bool present, out var version);
        if (!present) return defaultValue;
        if (typedValues.TryGetValue(key, out var cached) && cached.Version == version) return (T?)cached.Value;
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined) return default;
        if (!encrypted) { T? value = token.ToObject<T>(); typedValues[key] = new(version, value); return value; }
        try {
            string plaintext = CryptographyUtils.AesDecrypt(token.Value<string>())!;
            T? value = typeof(T) == typeof(string) ? (T)(object)plaintext : Newtonsoft.Json.JsonConvert.DeserializeObject<T>(plaintext);
            typedValues[key] = new(version, value); return value;
        } catch (CryptographicException) { Remove(key); return defaultValue; }
    }
    public bool HasValue(string key) { AssertAttached(); state.Read(key, ContextStamp, attachmentEpoch, out bool present, out _); return present; }
    public void DiscardCache() { ObjectDisposedException.ThrowIf(disposed, this); typedValues.Clear(); state.InvalidateReads(); }
    public void Save() { AssertAttached(); state.Flush(CancellationToken.None); }
    public Task FlushAsync(CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.Run(() => { AssertAttached(); state.Flush(cancellationToken); }, cancellationToken);
    }
    public void Dispose() {
        lock (state.Sync) { if (disposed) return; disposed = true; }
        try { state.Flush(CancellationToken.None); }
        catch { lock (state.Sync) disposed = false; throw; }
        cacheRegistration?.Dispose(); cacheInvalidator = null;
        lock (state.Sync) {
            state.ProviderCount--;
            if (state.ProviderCount == 0) state.Timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }
}

public static class InstanceConfigProviders {
    private static readonly ConcurrentBag<WeakReference<InstanceConfigProvider>> providers = [];
    internal static void Register(InstanceConfigProvider provider) => providers.Add(new(provider));
    public static void SaveAll() {
        // Pending states strongly retain all queued writes, including providerless INI writes. Clean,
        // stale weak providers must not turn a completed identity handoff into a shutdown failure.
        MmcConfigurationCoordinator.FlushPending();
    }
    /// <summary>Fixture/owned-root shutdown hook; no provider under an unrelated root is flushed or disposed.</summary>
    public static void FlushAndDisposeWithin(string ownedRoot) {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        List<Exception> errors = [];
        foreach (var weak in providers) {
            if (!weak.TryGetTarget(out var provider) || !MmcConfigurationCoordinator.Within(prefix, provider.VersionRoot)) continue;
            try { provider.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        try { MmcConfigurationCoordinator.FlushPending(prefix); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("独立目录的实例配置收尾失败。", errors);
    }
}
