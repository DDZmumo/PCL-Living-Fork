// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace PCL;

/// <summary>
/// Starts release lookups and warms upcoming views during the startup animation. The workspace may show while
/// preloads are pending. Automatic dialogs wait for the workspace to become visible before opening.
/// </summary>
internal static class GlassStartupPreload
{
    // Limit optional UI-thread warming while startup is in progress.
    private static readonly TimeSpan PreloadBudget = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<bool>? _interfaceShown;
    private static readonly Stopwatch Clock = new();
    private static Task<ModLauncherUpdate.CurrentReleaseNotes?>? _notes;
    private static Task<JObject?>? _release;
    private static ModLauncherUpdate.UpdateSource _releaseSource;
    private static ReleaseNotesPromptView? _notesView;
    private static ModLauncherUpdate.UpdatePromptView? _updateView;
    private static string? _updateTag;
    private static bool _notesViewDone, _updateViewDone;
    private static int _warmStep;
    private static readonly FormMain.PageType[] WarmPages = { FormMain.PageType.Download, FormMain.PageType.Setup, FormMain.PageType.Other };

    /// <summary>Starts the preloads (UI thread, when the splash is requested).</summary>
    internal static void Begin()
    {
        Volatile.Write(ref _interfaceShown, new(TaskCreationOptions.RunContinuationsAsynchronously));
        Clock.Restart();
        try
        {
            // Release notes: only when they would open automatically (not dismissed for this build) and come from
            // the published release (the local summary needs no network).
            if (!FormMain.IsCurrentUpdateLogSuppressed && ModSecret.VersionBranchMain == "OpenSource" &&
                ModLauncherUpdate.UsesPublishedReleaseNotes(ModBase.VersionBaseName, ModBase.ReleaseBaselineIsPrerelease, ModBase.BuildType))
            {
                ModLauncherUpdate.UpdateSource source = ModLauncherUpdate.PreferredUpdateSource();
                _notes = Task.Run<ModLauncherUpdate.CurrentReleaseNotes?>(() => ModLauncherUpdate.ReadCurrentReleaseNotes(ModBase.VersionBaseName, null, source));
            }
            if (ModLauncherUpdate.AutomaticCheckEnabled)
            {
                _releaseSource = ModLauncherUpdate.PreferredUpdateSource();
                ModLauncherUpdate.UpdateSource source = _releaseSource;
                _release = Task.Run<JObject?>(() => ModLauncherUpdate.FindLatestReleaseForSource(ModBase.VersionBaseName, source));
            }
        }
        catch (Exception error) { Logger.Warn(error, "启动预加载未能开始"); }
        if (_notes is null) _notesViewDone = true;
        if (_release is null) _updateViewDone = true;
    }

    internal static bool CheckingForUpdate => Volatile.Read(ref _release) is { IsCompleted: false };

    /// <summary>One small step of UI-thread preloading; called from the shell's refresh while the splash shows its loader.</summary>
    internal static void Step()
    {
        if (!Clock.IsRunning || Clock.Elapsed >= PreloadBudget) return;
        try
        {
            if (!_notesViewDone && _notes is { IsCompleted: true })
            {
                _notesViewDone = true;
                if (_notes.Status == TaskStatus.RanToCompletion && _notes.Result is { FromRelease: true } notes)
                    _notesView = Warm(new ReleaseNotesPromptView(notes.Markdown, FormMain.IsCurrentUpdateLogSuppressed, FormMain.PersistUpdateLogSuppression), 720);
                return;
            }
            if (!_updateViewDone && _release is { IsCompleted: true } release)
            {
                _updateViewDone = true;
                string? tag = release.Status == TaskStatus.RanToCompletion ? release.Result?["tag_name"]?.ToString() : null;
                if (tag is not null && ModLauncherUpdate.ShouldPromptForUpdate(tag, ModBase.VersionBaseName, Settings.Get<string>("ToolLauncherUpdateIgnoredTag")))
                {
                    _updateTag = tag;
                    _updateView = Warm(new ModLauncherUpdate.UpdatePromptView(tag, _releaseSource), 570);
                }
                return;
            }
            // Pages the user most often opens next: built now (one per step) so the first visit does not stall.
            if (_warmStep < WarmPages.Length) FormMain.PrewarmPage(WarmPages[_warmStep++]);
        }
        catch (Exception error) { Logger.Warn(error, "启动预加载步骤失败"); }
    }

    // Lays a dialog body out once at its dialog width so its text and Markdown are already measured.
    private static T Warm<T>(T view, double width) where T : FrameworkElement
    {
        view.Measure(new Size(width, 640));
        view.Arrange(new Rect(view.DesiredSize));
        return view;
    }

    /// <summary>The splash has gone (or never ran): automatic dialogs may open.</summary>
    internal static void MarkInterfaceShown()
    {
        Clock.Stop();
        if (Volatile.Read(ref _interfaceShown)?.TrySetResult(true) == true)
            Logger.Info("启动界面加载完成");
    }

    internal static void Cancel()
    {
        Clock.Stop();
        Volatile.Read(ref _interfaceShown)?.TrySetResult(false);
    }

    /// <summary>Background threads: waits until the interface is shown before an automatic dialog opens.</summary>
    internal static bool WaitForInterface()
    {
        Task<bool>? ready = Volatile.Read(ref _interfaceShown)?.Task;
        if (ready is null) return true;
        if (!ready.IsCompleted && Application.Current?.Dispatcher.CheckAccess() == true) return false;
        return ready.GetAwaiter().GetResult();
    }

    /// <summary>The release notes fetched during the splash (published notes only), or null.</summary>
    internal static ModLauncherUpdate.CurrentReleaseNotes? TakeNotes()
    {
        Task<ModLauncherUpdate.CurrentReleaseNotes?>? notes = Interlocked.Exchange(ref _notes, null);
        try { return notes is not null && notes.Wait(TimeSpan.FromSeconds(15)) ? notes.Result : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The prebuilt release notes body, if it was built for these notes.</summary>
    internal static ReleaseNotesPromptView? TakeNotesView() => Interlocked.Exchange(ref _notesView, null);

    /// <summary>The latest release fetched during the splash from <paramref name="source"/>, or null.</summary>
    internal static JObject? TakeRelease(ModLauncherUpdate.UpdateSource source)
    {
        Task<JObject?>? release = Volatile.Read(ref _release);
        if (release is null || source != _releaseSource) return null;
        try { return release.Wait(TimeSpan.FromSeconds(15)) ? release.Result : null; }
        catch (Exception) { return null; }
        finally { Interlocked.CompareExchange(ref _release, null, release); }
    }

    /// <summary>The prebuilt update prompt body for <paramref name="tag"/>, or null.</summary>
    internal static ModLauncherUpdate.UpdatePromptView? TakeUpdateView(string tag, ModLauncherUpdate.UpdateSource source)
    {
        if (!string.Equals(tag, _updateTag, StringComparison.Ordinal) || source != _releaseSource) return null;
        return Interlocked.Exchange(ref _updateView, null);
    }
}
