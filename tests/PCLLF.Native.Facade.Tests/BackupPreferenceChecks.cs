// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

internal static class BackupPreferenceChecks {
    private static object? Preference(string name, params object?[] args) => Native.Static("PCL.BackupPreferences", name, args);
    internal static void Run(FixtureEnvironment environment) {
        using Scope scope = new(environment);
        string[] globals = ["LaunchWorldBackupAutoEnabled", "LaunchWorldBackupRetention", "LaunchEnvironmentHistoryAutoCleanup", "LaunchEnvironmentHistoryRetention", "LaunchWorldBackupKeepAll", "LaunchEnvironmentHistoryAutoEnabled"];
        object[] previous = globals.Select(key => Settings.Get(key)).ToArray();
        var first = Instance(Path.Combine(scope.Root, "policy/minecraft/versions/first"));
        var second = Instance(Path.Combine(scope.Root, "policy/minecraft/versions/second"));
        try {
            Settings.Set(globals[0], false); Settings.Set(globals[1], 8); Settings.Set(globals[2], true); Settings.Set(globals[3], 3); Settings.Set(globals[4], false); Settings.Set(globals[5], false);
            Native.Check((int)Preference("WorldMode", first)! == 0 && !(bool)Preference("WorldAutoEnabled", first)! && (int)Preference("WorldRetention", first)! == 8,
                "A fresh instance did not inherit both global world-backup defaults.");
            Settings.Set("VersionWorldBackupMode", 1, first);
            Settings.Set("VersionWorldBackupAutoEnabled", true, first);
            Settings.Set("VersionWorldBackupRetention", 7, first);
            Native.Check((bool)Preference("WorldAutoEnabled", first)! && (int)Preference("WorldRetention", first)! == 7,
                "Independent world backup settings were not used.");

            Settings.Set(globals[1], 12);
            Settings.Set("VersionWorldBackupMode", 0, first);
            Native.Check(!(bool)Preference("WorldAutoEnabled", first)! &&
                (int)Preference("WorldRetention", first)! == 12 && (int)Cache(first, "VersionWorldBackupRetention")! == 7,
                "Returning to global settings lost the stored independent values or failed to follow live global edits.");

            var history = new PageInstanceRestorePoints();
            var controller = (EnvironmentHistoryPageController<ModMinecraft.McInstance>)Native.Field(typeof(PageInstanceRestorePoints), "Controller").GetValue(history)!;
            controller.Owner = second; controller.BindRetention();
            var historyMode = Part<ComboBox>(history, "ComboRetentionMode");
            var historyIndependent = Part<FrameworkElement>(history, "PanIndependentRetention");
            Native.Check(Part<TextBlock>(history, "LabHistorySource").Text == "配置来源" &&
                Part<TextBlock>(history, "LabHistoryPolicy").Text == "保留策略" && Grid.GetColumn(historyMode) == 1,
                "Restore-point settings selectors are unlabeled or misaligned.");
            Native.Check(historyMode.SelectedIndex == 0 && historyIndependent.Visibility == Visibility.Collapsed &&
                (bool)Preference("HistoryAutoCleanup", second)! && (int)Preference("HistoryRetention", second)! == 3,
                "Restore-point preferences did not follow global defaults.");
            Capture(Part<FrameworkElement>(history, "CardRetention"), "history-follow-global.png");
            historyMode.SelectedIndex = 1;
            var create = Part<CheckBox>(history, "CheckHistoryAutoCreate");
            Native.Check(create.IsChecked == false && !(bool)Preference("HistoryAutoEnabled", second)!, "Independent creation did not initialize from the global switch.");
            create.IsChecked = true;
            Native.Check((bool)Preference("HistoryAutoEnabled", second)!, "Independent restore-point creation did not override the global default.");
            var cleanup = Part<ComboBox>(history, "ComboHistoryRetentionPolicy");
            var historyCount = Part<TextBox>(history, "TextRetention");
            Native.Check(historyIndependent.Visibility == Visibility.Visible && cleanup.SelectedIndex == 1 && historyCount.Text == "3",
                "Independent restore-point controls did not initialize from the global defaults.");
            historyCount.Text = "4"; controller.RetentionDirty = true;
            Native.Check(controller.CommitRetention() && (bool)Preference("HistoryAutoCleanup", second)! &&
                (int)Preference("HistoryRetention", second)! == 4 && Part<FrameworkElement>(history, "PanRetention").Visibility == Visibility.Visible,
                "Independent restore-point cleanup/count did not remain editable and owner-scoped.");
            cleanup.SelectedIndex = 0;
            Native.Check(!(bool)Preference("HistoryAutoCleanup", second)! && Part<FrameworkElement>(history, "PanRetention").Visibility == Visibility.Collapsed &&
                (int)Cache(second, "VersionEnvironmentHistoryRetention")! == 4, "Keeping every restore point did not hide and preserve its count.");
            Capture(Part<FrameworkElement>(history, "CardRetention"), "history-keep-all.png");
            cleanup.SelectedIndex = 1;
            Native.Check(historyCount.Text == "4" && Part<FrameworkElement>(history, "PanRetention").Visibility == Visibility.Visible, "Limited restore points did not restore their count.");
            Capture(Part<FrameworkElement>(history, "CardRetention"), "history-independent.png");
            historyMode.SelectedIndex = 0;
            Native.Check(historyIndependent.Visibility == Visibility.Collapsed && (bool)Preference("HistoryAutoCleanup", second)! &&
                (int)Preference("HistoryRetention", second)! == 3 && (int)Cache(second, "VersionEnvironmentHistoryRetention")! == 4,
                "Restore-point mode switching did not preserve independent preferences.");

            var globalPage = new PageSetupSystem();
            Native.Call(typeof(PageSetupSystem), "RefreshBackupRetention", globalPage);
            var globalWorldPolicy = Part<ComboBox>(globalPage, "ComboWorldBackupRetention");
            var globalCreate = Part<CheckBox>(globalPage, "CheckHistoryAutoCreate");
            Native.Check(SettingService.GetKey(globalCreate) == "LaunchEnvironmentHistoryAutoEnabled" &&
                Part<TextBlock>(globalPage, "LabHistoryAutoCreate").Text == "自动创建" &&
                Part<TextBlock>(globalPage, "LabHistoryPolicy").Text == "保留策略" && Grid.GetColumn(globalWorldPolicy) == 1,
                "Global restore-point creation or labeled policy rows are missing.");
            foreach (string controlName in new[] { "CheckWorldBackupAuto", "CheckHistoryAutoCreate", "TextWorldBackupRetention", "TextHistoryRetention" })
                SettingService.RefreshSettings(Part<FrameworkElement>(globalPage, controlName));
            Capture(Part<FrameworkElement>(globalPage, "CardBackupSettings"), "global-backup-settings.png");
            var captureOwner = EnvironmentHistoryChecks.Instance(Path.Combine(scope.Root, "automatic/minecraft/versions/capture"));
            Cache(captureOwner, "VersionEnvironmentHistoryMode", 0);
            object? priorHistoryRoot = Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot").GetValue(null);
            Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot").SetValue(null, Path.Combine(scope.Root, "history-data"));
            try {
                Native.Check(Native.Static("PCL.EnvironmentHistory", "CaptureAutomatic", captureOwner, "游戏启动前", CancellationToken.None) is null,
                    "Global automatic restore-point disable was ignored.");
                string manual = (string)Native.Static("PCL.EnvironmentHistory", "CaptureInstance", captureOwner, "手动还原点", CancellationToken.None)!;
                Native.Check(!string.IsNullOrEmpty(manual), "Disabling automatic restore points blocked manual creation.");
                Settings.Set("VersionEnvironmentHistoryMode", 1, captureOwner); Settings.Set("VersionEnvironmentHistoryAutoEnabled", true, captureOwner);
                string automatic = (string)Native.Static("PCL.EnvironmentHistory", "CaptureAutomatic", captureOwner, "游戏启动前", CancellationToken.None)!;
                Native.Check(!string.IsNullOrEmpty(automatic), "Independent automatic creation could not override the global default.");
            } finally { Native.Property(Native.Type("PCL.EnvironmentHistory"), "TestRoot").SetValue(null, priorHistoryRoot); }
            var globalHistoryPolicy = Part<ComboBox>(globalPage, "ComboHistoryRetention");
            globalWorldPolicy.SelectedIndex = 0; globalHistoryPolicy.SelectedIndex = 0;
            Native.Check(Part<FrameworkElement>(globalPage, "PanWorldBackupRetentionCount").Visibility == Visibility.Collapsed &&
                Part<FrameworkElement>(globalPage, "PanHistoryRetentionCount").Visibility == Visibility.Collapsed &&
                (int)Preference("WorldRetention", second)! == 0 && !(bool)Preference("HistoryAutoCleanup", second)!, "Global keep-all policies did not propagate or hide count inputs.");
            globalWorldPolicy.SelectedIndex = 1; globalHistoryPolicy.SelectedIndex = 1;
            Native.Check(Settings.Get<int>(globals[1]) == 12 && Settings.Get<int>(globals[3]) == 3, "Global policy switching reset saved counts.");
            foreach (var item in new[] { ("TextWorldBackupRetention", globals[1]), ("TextHistoryRetention", globals[3]) }) {
                var box = Part<TextBox>(globalPage, item.Item1);
                Native.Check(SettingService.GetKey(box) == item.Item2, "Global retention control is not bound to its own setting.");
                SettingService.RefreshSettings(box);
                Native.Check(box.Text == Settings.Get<int>(item.Item2).ToString(), "Global retention value did not load.");
            }
            var legacy = Instance(Path.Combine(scope.Root, "policy/minecraft/versions/legacy"));
            Cache(legacy, "VersionWorldBackupAutoEnabled", false); Cache(legacy, "VersionEnvironmentHistoryRetention", 6);
            Native.Check((int)Preference("WorldMode", legacy)! == 1 && (int)Preference("HistoryMode", legacy)! == 1 &&
                !(bool)Preference("WorldAutoEnabled", legacy)! && (int)Preference("HistoryRetention", legacy)! == 6,
                "Existing explicit per-instance preferences were silently replaced with global defaults.");
        } finally { for (int index = 0; index < globals.Length; index++) Settings.Set(globals[index], previous[index]); }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS backup preference sources: real native world/history selectors, hidden inherited controls, live globals, first independent seeding, retained independent values, legacy choices and owner isolation.");
    }
    private static void Capture(FrameworkElement element, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("PCLLF_BACKUP_PREF_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        VisualAssetChecks.Render(element, Path.Combine(directory, name), 720, 560);
    }
}
