// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MeloongCore;
using PCLCS;

namespace PCL;

/// <summary>One project store for online rows, recognized instance resources and the favorites page.</summary>
internal static class ResourceFavorites {
    private static readonly FavoriteEvents events = new();
    private static readonly ConcurrentDictionary<ResourceFavoriteIdentity, byte> pending = new();
    private static int activeOperations;
    private static readonly Lazy<ResourceFavoriteStore> store = new(() => {
        var value = new ResourceFavoriteStore(Path.Combine(Paths.Base, "PCL", "ResourceFavorites.json"));
        value.Changed += (_, _) => events.Notify();
        return value;
    });
    internal static ResourceFavoriteStore Store => store.Value;
    internal static int PendingCount => Volatile.Read(ref activeOperations);
    internal static bool CanFavorite(ResourceProject project) => project is not null &&
        (project.Types & ResourceFavorite.SupportedTypes) != 0 && (project.Types & ~ResourceFavorite.SupportedTypes) == 0 &&
        ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out _);
    internal static bool IsFavorite(ResourceProject project) => CanFavorite(project) &&
        ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out var identity) && Store.Contains(identity);
    internal static bool IsBusy(ResourceProject project) => project is not null &&
        ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out var identity) && pending.ContainsKey(identity);
    internal static async void Toggle(ResourceProject project, ResourceVersion knownLocalVersion = null) {
        if (!CanFavorite(project) || !ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out var identity) || !pending.TryAdd(identity, 0)) return;
        Interlocked.Increment(ref activeOperations);
        try {
            events.Notify();
            var metadata = project.ToJson(); metadata["FavoriteDisplayName"] = project.TranslatedName;
            var cached = ResourceVersion.ProjectFilesCache.TryGetValue(ResourceProjectKey.Get(project.Platform, project.Id), out var values)
                ? values.ToArray() : Array.Empty<ResourceVersion>();
            var gameVersions = project.KnownGameVersions.Concat(cached.SelectMany(value => value.GameVersions ?? new List<string>()))
                .Concat(knownLocalVersion?.GameVersions ?? new List<string>()).Where(ResourceFavoriteFilter.IsGameVersionLabel).Distinct(StringComparer.Ordinal).ToArray();
            if (gameVersions.Length > 0) metadata["KnownGameVersions"] = new JArray(gameVersions);
            ModLoaders loaders = cached.Aggregate(project.ModLoaders, (current, value) => current | value.ModLoaders) | (knownLocalVersion?.ModLoaders ?? ModLoaders.None);
            if (loaders != ModLoaders.None) metadata["ModLoaders"] = (int)loaders;
            ResourceFavorite value = ResourceFavorite.FromMetadata(metadata, DateTime.UtcNow);
            var result = await Task.Run(() => {
                bool success = Store.TryToggle(value, out _, out Exception error);
                return (success, error);
            }).ConfigureAwait(false);
            if (!result.success) throw result.error;
        } catch (Exception error) {
            Logger.Warn(error, "保存资源收藏失败，已保留原收藏文件", LogBehavior.None);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                ModMain.Hint("收藏保存失败，已保留原收藏。可在收藏页查看详情。", ModMain.HintType.Red, false));
        } finally {
            pending.TryRemove(identity, out _);
            try { events.Notify(); }
            finally { Interlocked.Decrement(ref activeOperations); }
        }
    }
    // Loaded/unloaded subscriptions keep all visible copies of a project synchronized,
    // without retaining recycled rows or rebuilding the local mod list.
    internal static void Observe(FrameworkElement owner, EventHandler<EventArgs> refresh) {
        bool attached = false;
        EventHandler<EventArgs> forward = (sender, args) => {
            if (!attached || owner.Dispatcher.HasShutdownStarted) return;
            if (owner.Dispatcher.CheckAccess()) refresh(sender, args);
            else owner.Dispatcher.BeginInvoke(new Action(() => { if (attached) refresh(sender, args); }));
        };
        owner.Loaded += (_, _) => {
            if (attached) return;
            attached = true;
            WeakEventManager<FavoriteEvents, EventArgs>.AddHandler(events, nameof(FavoriteEvents.Changed), forward);
            refresh(Store, EventArgs.Empty);
        };
        owner.Unloaded += (_, _) => {
            if (!attached) return;
            attached = false;
            WeakEventManager<FavoriteEvents, EventArgs>.RemoveHandler(events, nameof(FavoriteEvents.Changed), forward);
        };
    }
    internal sealed class FavoriteEvents {
        public event EventHandler<EventArgs> Changed;
        internal void Notify() { Changed?.Invoke(this, EventArgs.Empty); }
    }
}
