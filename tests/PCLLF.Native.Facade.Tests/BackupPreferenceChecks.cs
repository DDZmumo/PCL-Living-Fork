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
        string[] globals = ["LaunchWorldBackupAutoEnabled", "LaunchWorldBackupRetention", "LaunchEnvironmentHistoryAutoCleanup", "LaunchEnvironmentHistoryRetention", "LaunchWorldBackupKeepAll"];
        object[] previous = globals.Select(key => Settings.Get(key)).ToArray();
        var first = Instance(Path.Combine(scope.Root, "policy/minecraft/versions/first"));
        var second = Instance(Path.Combine(scope.Root, "policy/minecraft/versions/second"));
        try {
            Settings.Set(globals[0], false); Settings.Set(globals[1], 8); Settings.Set(globals[2], true); Settings.Set(globals[3], 3); Settings.Set(globals[4], false);
            Native.Check((int)Preference("WorldMode", first)! == 0 && !(bool)Preference("WorldAutoEnabled", first)! && (int)Preference("WorldRetention", first)! == 8,
                "A fresh instance did not inherit both global world-backup defaults.");
            var settings = (FrameworkElement)New("PCL.WorldBackupSettings");
            Native.Call(settings.GetType(), "BindOwner", settings, first);
            var mode = Part<MyComboBox>(settings, "ComboMode");
            var independent = Part<FrameworkElement>(settings, "PanIndependent");
            Native.Check(mode.SelectedIndex == 0 && independent.Visibility == Visibility.Collapsed && Part<FrameworkElement>(settings, "LabGlobal").Visibility == Visibility.Visible,
                "Following global defaults still exposes independent backup controls.");
            Capture(settings, "world-follow-global.png");
            mode.SelectedIndex = 1;
            Native.Check((int)Preference("WorldMode", first)! == 1 && independent.Visibility == Visibility.Visible &&
                !(bool)Cache(first, "VersionWorldBackupAutoEnabled")! && (int)Cache(first, "VersionWorldBackupRetention")! == 8,
                "First independent selection did not preserve the effective global values.");
            var auto = Part<MyCheckBox>(settings, "CheckAuto"); auto.Checked = true;
            Native.Call(settings.GetType(), "AutoChanged", settings, auto, true);
            var retention = Part<MyTextBox>(settings, "TextRetention"); retention.Text = "7"; Native.Set(settings, "RetentionDirty", true);
            Native.Check((bool)Native.Call(settings.GetType(), "CommitPending", settings)! && (bool)Preference("WorldAutoEnabled", first)! &&
                (int)Preference("WorldRetention", first)! == 7 && !(bool)Preference("WorldAutoEnabled", second)!,
                "Independent backup preferences did not override global defaults or leaked to another instance.");
            var worldPolicy = Part<MyComboBox>(settings, "ComboRetentionPolicy");
            worldPolicy.SelectedIndex = 0;
            Native.Check(Part<FrameworkElement>(settings, "PanRetentionCount").Visibility == Visibility.Collapsed &&
                (int)Preference("WorldRetention", first)! == 0 && (int)Cache(first, "VersionWorldBackupRetention")! == 7,
                "Keeping all world backups did not hide the count or lost the previous limit.");
            Capture(settings, "world-keep-all.png");
            worldPolicy.SelectedIndex = 1;
            Native.Check(Part<FrameworkElement>(settings, "PanRetentionCount").Visibility == Visibility.Visible && retention.Text == "7",
                "Returning to limited world backups lost the previous count.");
            Capture(settings, "world-independent.png");
            mode.SelectedIndex = 0; Settings.Set(globals[1], 12);
            Native.Check(independent.Visibility == Visibility.Collapsed && !(bool)Preference("WorldAutoEnabled", first)! &&
                (int)Preference("WorldRetention", first)! == 12 && (int)Cache(first, "VersionWorldBackupRetention")! == 7,
                "Returning to global settings lost the stored independent values or failed to follow live global edits.");
            mode.SelectedIndex = 1;
            Native.Check(auto.Checked && retention.Text == "7", "Returning to independent mode did not restore its own values.");

            var history = new PageInstanceRestorePoints();
            var controller = (EnvironmentHistoryPageController<ModMinecraft.McInstance>)Native.Field(typeof(PageInstanceRestorePoints), "Controller").GetValue(history)!;
            controller.Owner = second; controller.BindRetention();
            var historyMode = Part<MyComboBox>(history, "ComboRetentionMode");
            var historyIndependent = Part<FrameworkElement>(history, "PanIndependentRetention");
            Native.Check(historyMode.SelectedIndex == 0 && historyIndependent.Visibility == Visibility.Collapsed &&
                (bool)Preference("HistoryAutoCleanup", second)! && (int)Preference("HistoryRetention", second)! == 3,
                "Restore-point preferences did not follow global defaults.");
            Capture(Part<MyCard>(history, "CardRetention"), "history-follow-global.png");
            historyMode.SelectedIndex = 1;
            var cleanup = Part<MyComboBox>(history, "ComboHistoryRetentionPolicy");
            var historyCount = Part<MyTextBox>(history, "TextRetention");
            Native.Check(historyIndependent.Visibility == Visibility.Visible && cleanup.SelectedIndex == 1 && historyCount.Text == "3",
                "Independent restore-point controls did not initialize from the global defaults.");
            historyCount.Text = "4"; controller.RetentionDirty = true;
            Native.Check(controller.CommitRetention() && (bool)Preference("HistoryAutoCleanup", second)! &&
                (int)Preference("HistoryRetention", second)! == 4 && Part<FrameworkElement>(history, "PanRetention").Visibility == Visibility.Visible,
                "Independent restore-point cleanup/count did not remain editable and owner-scoped.");
            cleanup.SelectedIndex = 0;
            Native.Check(!(bool)Preference("HistoryAutoCleanup", second)! && Part<FrameworkElement>(history, "PanRetention").Visibility == Visibility.Collapsed &&
                (int)Cache(second, "VersionEnvironmentHistoryRetention")! == 4, "Keeping every restore point did not hide and preserve its count.");
            Capture(Part<MyCard>(history, "CardRetention"), "history-keep-all.png");
            cleanup.SelectedIndex = 1;
            Native.Check(historyCount.Text == "4" && Part<FrameworkElement>(history, "PanRetention").Visibility == Visibility.Visible, "Limited restore points did not restore their count.");
            Capture(Part<MyCard>(history, "CardRetention"), "history-independent.png");
            historyMode.SelectedIndex = 0;
            Native.Check(historyIndependent.Visibility == Visibility.Collapsed && (bool)Preference("HistoryAutoCleanup", second)! &&
                (int)Preference("HistoryRetention", second)! == 3 && (int)Cache(second, "VersionEnvironmentHistoryRetention")! == 4,
                "Restore-point mode switching did not preserve independent preferences.");

            var globalPage = new PageSetupSystem();
            Native.Call(typeof(PageSetupSystem), "RefreshBackupRetention", globalPage);
            var globalWorldPolicy = Part<MyComboBox>(globalPage, "ComboWorldBackupRetention");
            var globalHistoryPolicy = Part<MyComboBox>(globalPage, "ComboHistoryRetention");
            globalWorldPolicy.SelectedIndex = 0; globalHistoryPolicy.SelectedIndex = 0;
            Native.Check(Part<FrameworkElement>(globalPage, "PanWorldBackupRetentionCount").Visibility == Visibility.Collapsed &&
                Part<FrameworkElement>(globalPage, "PanHistoryRetentionCount").Visibility == Visibility.Collapsed &&
                (int)Preference("WorldRetention", second)! == 0 && !(bool)Preference("HistoryAutoCleanup", second)!, "Global keep-all policies did not propagate or hide count inputs.");
            globalWorldPolicy.SelectedIndex = 1; globalHistoryPolicy.SelectedIndex = 1;
            Native.Check(Settings.Get<int>(globals[1]) == 12 && Settings.Get<int>(globals[3]) == 3, "Global policy switching reset saved counts.");
            foreach (var item in new[] { ("TextWorldBackupRetention", globals[1]), ("TextHistoryRetention", globals[3]) }) {
                var box = Part<MyTextBox>(globalPage, item.Item1);
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
    private static void Capture(FrameworkElement element, string name) {
        string? directory = Environment.GetEnvironmentVariable("PCLLF_BACKUP_PREF_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        if (element.GetType().FullName == "PCL.WorldBackupSettings") {
            var window = new Window { Content = element, Width = 680, Height = 350, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.WhiteSmoke };
            try {
                window.Show();
                var card = Part<MyCard>(element, "CardSettings");
                int oldAnimation = ModAnimation.AniControlEnabled; ModAnimation.AniControlEnabled++;
                try { card.IsSwapped = false; card.TriggerForceResize(); } finally { ModAnimation.AniControlEnabled = oldAnimation; }
                VisualAssetChecks.Pump(() => card.ActualHeight > 60, "Owned backup settings preview did not expand.", 3000);
                window.UpdateLayout();
                var image = new RenderTargetBitmap(680, Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                image.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
            } finally { window.Content = null; window.Close(); }
            return;
        }
        int animation = ModAnimation.AniControlEnabled; ModAnimation.AniControlEnabled++;
        try {
            if (element is MyCard card) { _ = card.MainTextBlock; card.IsSwapped = false; }
            foreach (MyCard child in (element as Panel)?.Children.OfType<MyCard>() ?? []) { _ = child.MainTextBlock; child.IsSwapped = false; }
        } finally { ModAnimation.AniControlEnabled = animation; }
        var parent = LogicalTreeHelper.GetParent(element) as Panel;
        int slot = parent?.Children.IndexOf(element) ?? -1;
        parent?.Children.Remove(element);
        var border = new Border { Background = Brushes.WhiteSmoke, Padding = new Thickness(15), Child = element, Width = 680 };
        border.Measure(new Size(680, 1000)); border.Arrange(new Rect(new Point(), border.DesiredSize)); border.UpdateLayout();
        var bitmap = new RenderTargetBitmap(680, Math.Max(1, (int)Math.Ceiling(border.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
        border.Child = null;
        if (parent is not null) parent.Children.Insert(slot, element);
    }
}
