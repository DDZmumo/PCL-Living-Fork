// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
namespace PCL;

internal static class BackupPreferences {
    internal static int WorldMode(ModMinecraft.McInstance instance) => Mode(instance, "VersionWorldBackupMode", "VersionWorldBackupAutoEnabled", "VersionWorldBackupRetention");
    internal static int HistoryMode(ModMinecraft.McInstance instance) => Mode(instance, "VersionEnvironmentHistoryMode", "VersionEnvironmentHistoryAutoCleanup", "VersionEnvironmentHistoryRetention");
    private static int Mode(ModMinecraft.McInstance instance, string mode, string enabled, string retention) {
        int value = Settings.Get<int>(mode, instance);
        if (value >= 0) return value == 1 ? 1 : 0;
        // Existing explicit per-instance choices remain independent; fresh
        // instances follow the global defaults. Reads never migrate user files.
        return Settings.HasSaved(enabled, instance) || Settings.HasSaved(retention, instance) ? 1 : 0;
    }
    internal static bool WorldAutoEnabled(ModMinecraft.McInstance instance) => WorldMode(instance) == 0
        ? Settings.Get<bool>("LaunchWorldBackupAutoEnabled") : Settings.Get<bool>("VersionWorldBackupAutoEnabled", instance);
    internal static int WorldRetention(ModMinecraft.McInstance instance) => WorldMode(instance) == 0
        ? Settings.Get<int>("LaunchWorldBackupRetention") : Settings.Get<int>("VersionWorldBackupRetention", instance);
    internal static bool HistoryAutoCleanup(ModMinecraft.McInstance instance) => HistoryMode(instance) == 0
        ? Settings.Get<bool>("LaunchEnvironmentHistoryAutoCleanup") : Settings.Get<bool>("VersionEnvironmentHistoryAutoCleanup", instance);
    internal static int HistoryRetention(ModMinecraft.McInstance instance) => HistoryMode(instance) == 0
        ? Settings.Get<int>("LaunchEnvironmentHistoryRetention") : Settings.Get<int>("VersionEnvironmentHistoryRetention", instance);
    internal static void SetWorldMode(ModMinecraft.McInstance instance, int mode) {
        bool enabled = WorldAutoEnabled(instance); int retention = WorldRetention(instance);
        SetMode(instance, mode, "VersionWorldBackupMode", "VersionWorldBackupAutoEnabled", "VersionWorldBackupRetention", enabled, retention);
    }
    internal static void SetHistoryMode(ModMinecraft.McInstance instance, int mode) {
        bool enabled = HistoryAutoCleanup(instance); int retention = HistoryRetention(instance);
        SetMode(instance, mode, "VersionEnvironmentHistoryMode", "VersionEnvironmentHistoryAutoCleanup", "VersionEnvironmentHistoryRetention", enabled, retention);
    }
    private static void SetMode(ModMinecraft.McInstance instance, int mode, string modeKey, string enabledKey, string retentionKey, bool enabled, int retention) {
        if (mode == 1) {
            if (!Settings.HasSaved(enabledKey, instance)) Settings.Set(enabledKey, enabled, instance);
            if (!Settings.HasSaved(retentionKey, instance)) Settings.Set(retentionKey, retention, instance);
        }
        Settings.Set(modeKey, mode == 1 ? 1 : 0, instance);
    }
}
