// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PCLCS;

/// <summary>Routes only the reviewed LF-owned physical values. Shared PCL names
/// and unsuffixed account records keep their existing storage semantics.</summary>
public sealed class LfRegistryStorage {
    public const string Folder = "PCL Living Fork";
    private const string Prefix = "HKEY_CURRENT_USER\\Software\\";
    public static readonly string[] OwnedNames = {
        "LaunchPersonalOptionsSync", "LaunchPersonalOptionsKeys", "LaunchPersonalOptionsMouse", "LaunchPersonalOptionsView",
        "LaunchPersonalOptionsAccessibility", "LaunchPersonalOptionsAudio", "LaunchPersonalOptionsExcluded", "LaunchPersonalOptionsCapture",
        "LaunchRecentWorlds", "UiLaunchMoreSettings", "ToolLauncherUpdateIgnoredTag", "LivingPclSharedPreferencesMigrated",
        "LoginMsJson_LF", "CacheMsOAuthRefresh_LF", "CacheMsAccess_LF", "CacheMsProfileJson_LF", "CacheMsUuid_LF", "CacheMsName_LF",
        "CacheMsV2Migrated_LF", "CacheMsV2OAuthRefresh_LF", "CacheMsV2Access_LF", "CacheMsV2ProfileJson_LF", "CacheMsV2Uuid_LF", "CacheMsV2Name_LF", "CacheMsV2Expires_LF"
    };
    private static readonly HashSet<string> owned = new(OwnedNames, StringComparer.Ordinal);
    private readonly Func<string, bool, bool, ILegacySettingsStore?> open;
    private readonly string legacyFolder;
    private readonly object gate = new();
    private bool migrated;
    public LfRegistryStorage(Func<string, bool, bool, ILegacySettingsStore?> open, string legacyFolder = "PCLDebug") {
        this.open = open; this.legacyFolder = legacyFolder;
    }
    public static bool IsOwned(string name) => owned.Contains(name);
    private bool SourceFolder(string folder) => folder == "PCL" || folder == legacyFolder;
    private string Target(string folder, string name) => IsOwned(name) && (SourceFolder(folder) || folder == Folder) ? Folder : folder;
    private static string FolderFromPath(string path) {
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected settings Registry hive.");
        return path.Substring(Prefix.Length);
    }
    private static bool Equal(object? left, object? right) {
        if (left is byte[] bytes && right is byte[] otherBytes) return bytes.SequenceEqual(otherBytes);
        if (left is string[] strings && right is string[] otherStrings) return strings.SequenceEqual(otherStrings);
        return Equals(left, right);
    }
    private sealed class Move {
        public ILegacySettingsStore Source = null!;
        public string Name = "";
        public object Value = null!, Destination = null!;
#pragma warning disable CA1416 // Registry kinds are inert metadata; actual stores are supplied by the host.
        public RegistryValueKind Kind, DestinationKind;
#pragma warning restore CA1416
        public bool Added, Deleted;
    }

    public void EnsureMigrated() {
        lock (gate) {
            if (migrated) return;
            using ILegacySettingsStore? official = open("PCL", true, false);
            using ILegacySettingsStore? legacy = legacyFolder == "PCL" || legacyFolder == Folder ? null : open(legacyFolder, true, false);
            List<Move> moves = new();
            foreach (ILegacySettingsStore? source in new[] { official, legacy }) {
                if (source is null) continue;
                foreach (string name in OwnedNames) {
                    object? value = source.GetValue(name, null, true);
                    if (value is null) continue;
#pragma warning disable CA1416
                    moves.Add(new Move { Source = source, Name = name, Value = value, Kind = source.GetValueKind(name) });
#pragma warning restore CA1416
                }
            }
            if (moves.Count == 0) { migrated = true; return; }
            using ILegacySettingsStore destination = open(Folder, true, true) ?? throw new IOException("无法打开 PCLLF 专用注册表项。");
            List<Move> added = new();
            try {
                // Write/verify every destination before deleting any source value.
                foreach (Move move in moves) {
                    object? current = destination.GetValue(move.Name, null, true);
                    if (current is null) {
                        move.Added = true; added.Add(move);
                        destination.SetValue(move.Name, move.Value, move.Kind);
                        current = destination.GetValue(move.Name, null, true);
                        if (!Equal(current, move.Value) || destination.GetValueKind(move.Name) != move.Kind) throw new IOException("PCLLF 注册表迁移校验失败。");
                    }
                    move.Destination = current!; move.DestinationKind = destination.GetValueKind(move.Name);
                }
                foreach (Move move in moves) {
                    if (!Equal(move.Source.GetValue(move.Name, null, true), move.Value) || move.Source.GetValueKind(move.Name) != move.Kind ||
                        !Equal(destination.GetValue(move.Name, null, true), move.Destination) || destination.GetValueKind(move.Name) != move.DestinationKind)
                        throw new IOException("迁移期间注册表内容发生变化，已停止清理旧值。");
                    move.Deleted = true;
                    move.Source.DeleteValue(move.Name, false);
                    if (move.Source.GetValue(move.Name, null, true) is not null) throw new IOException("旧 LF 注册表值未能清理。");
                }
                migrated = true;
            } catch {
                bool restored = true;
                foreach (Move move in moves.Where(move => move.Deleted).Reverse()) {
                    try {
                        object? current = move.Source.GetValue(move.Name, null, true);
                        if (Equal(current, move.Value) && move.Source.GetValueKind(move.Name) == move.Kind) continue;
                        if (current is not null) { restored = false; continue; }
                        move.Source.SetValue(move.Name, move.Value, move.Kind);
                        if (!Equal(move.Source.GetValue(move.Name, null, true), move.Value)) restored = false;
                    } catch { restored = false; }
                }
                // If source restoration was uncertain, retain the complete new copy.
                if (restored) foreach (Move move in added.AsEnumerable().Reverse()) {
                    try {
                        if (Equal(destination.GetValue(move.Name, null, true), move.Value) && destination.GetValueKind(move.Name) == move.Kind) destination.DeleteValue(move.Name, false);
                    } catch { }
                }
                throw;
            }
        }
    }
    private string Resolve(string folder, string name) {
        string target = Target(folder, name);
        if (target == Folder && IsOwned(name)) EnsureMigrated();
        return target;
    }
    public string? ReadRegistry(string path, string name, string? fallback) {
        using ILegacySettingsStore? store = open(Resolve(FolderFromPath(path), name), false, false);
        return store?.GetValue(name, null, true)?.ToString() ?? fallback;
    }
    public void WriteRegistry(string path, string name, string? value) {
        using ILegacySettingsStore store = open(Resolve(FolderFromPath(path), name), true, true) ?? throw new IOException("无法打开设置注册表项。");
#pragma warning disable CA1416
        store.SetValue(name, value ?? "", RegistryValueKind.String);
#pragma warning restore CA1416
    }
    public bool HasRegistry(string path, string name) {
        using ILegacySettingsStore? store = open(Resolve(FolderFromPath(path), name), false, false);
        return store?.GetValue(name, null, true) is not null;
    }
    public ILegacySettingsStore? OpenRegistry(string folder, bool writable, bool create) {
        if (!SourceFolder(folder) && folder != Folder) return open(folder, writable, create);
        EnsureMigrated();
        if (!create) { using var existing = open(folder, false, false); if (existing is null) return null; }
        return new RoutedStore(this, folder, writable, create);
    }
    private sealed class RoutedStore : ILegacySettingsStore {
        private readonly LfRegistryStorage owner;
        private readonly string folder;
        private readonly bool writable, create;
        public RoutedStore(LfRegistryStorage owner, string folder, bool writable, bool create) { this.owner = owner; this.folder = folder; this.writable = writable; this.create = create; }
        private ILegacySettingsStore? Store(string name, bool write) {
            if (write && !writable) throw new UnauthorizedAccessException("Registry store is read-only.");
            string target = owner.Resolve(folder, name);
            return owner.open(target, write, write && (create || target == Folder));
        }
        public object? GetValue(string name, object? fallback = null, bool doNotExpandEnvironmentNames = false) { using var store = Store(name, false); return store?.GetValue(name, fallback, doNotExpandEnvironmentNames) ?? fallback; }
        public RegistryValueKind GetValueKind(string name) { using var store = Store(name, false); return (store ?? throw new IOException("Missing Registry value.")).GetValueKind(name); }
        public void SetValue(string name, object value, RegistryValueKind kind) { using var store = Store(name, true); (store ?? throw new IOException("Missing Registry store.")).SetValue(name, value, kind); }
        public void DeleteValue(string name, bool throwOnMissingValue) { using var store = Store(name, true); if (store is null && throwOnMissingValue) throw new IOException("Missing Registry store."); store?.DeleteValue(name, throwOnMissingValue); }
        public void Dispose() { }
    }
}
