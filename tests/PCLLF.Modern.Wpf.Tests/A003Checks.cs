using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using PCLCS;
using PCLCS.Startup;
using PCLLF.Presentation.Mods;
using PCLLF.Presentation.Windowing;

namespace PCLLF.Modern.Wpf.Tests;

// A-003 feature checks against the real presentation controllers with host fakes.
// No dialog window, list reload or real settings file is required here; the native
// facade/窗口级 evidence remains separate.
internal static class A003Checks {
    internal static void Run() {
        UpdateLogChecks();
        ModFavoriteChecks();
        ResourceProjectIdentityChecks.Run();
        Console.WriteLine("PASS A-003 controllers: version-scoped automatic notes, automatic/manual routing, once-per-session upgrade/startup gate, independent update checks, the original four local mod filters and search/sort.");
    }

    // ---------- A-003.1 update-log prompt ----------
    private sealed class FakeStack : ILegacyMainPageStack {
        public int PageCode { get; set; }
        public object Additional => null;
    }
    private struct FakeAnim { }
    private sealed class UpdateLogHost {
        public string Dismissed = "";
        public string Identity = "fixture|1.4.0|stable|415|0";
        public bool ThrowOnRead;
        public readonly List<Task> UpdateChecks = new();
        public readonly List<Action> Threads = new();
        public readonly List<bool> Notices = new();
        public readonly List<string> Writes = new();
        internal LegacyMainWindowHost<FakeStack, object, FakeAnim> Build() => new() {
            // Old SystemUpdateLogPrompt=false must never be consulted as a gate.
            ReadBoolean = key => false,
            ReadString = key => {
                if (ThrowOnRead) throw new InvalidOperationException("synthetic read failure");
                return key == LegacyUpdateLogPrompt.Key ? Dismissed : "0|1|2|3|4";
            },
            UpdateLogIdentity = () => Identity,
            ShowReleaseNotes = (automatic, _) => { Notices.Add(automatic); return true; },
            NewThread = (work, name, priority) => Threads.Add(work),
            CheckUpdates = noticeClosed => UpdateChecks.Add(noticeClosed),
            LauncherName = () => "LivingPCL",
            VersionBaseName = () => "synthetic",
            ReleasesUrl = () => "https://example.invalid/releases",
            ReadInteger = key => key == "LaunchArgumentWindowType" ? 1 : 0,
            HasSaved = key => false,
            DefaultValue = key => 0,
            WriteSetting = (key, value) => Writes.Add(key + "=" + value),
            ResetSetting = key => Writes.Add("reset:" + key),
            TempFolder = () => Path.Combine(Path.GetTempPath(), "pcl-a003-temp") + Path.DirectorySeparatorChar,
            VersionCode = 1000,
            BuildType = 0,
            VersionBranchMain = () => "OpenSource"
        };
    }

    private static void UpdateLogChecks() {
        var host = new UpdateLogHost();
        var controller = new LegacyMainWindowController<FakeStack, object, FakeAnim>(new LegacyMainWindowView(), host.Build());

        Check(controller.ShouldAutoShowUpdateLog(), "Missing dismissal and legacy false must auto-show.");
        host.Dismissed = host.Identity;
        Check(!controller.ShouldAutoShowUpdateLog(), "Only the current identity must suppress automatic notes.");
        host.ThrowOnRead = true; Check(controller.ShouldAutoShowUpdateLog(), "read failure must fall back to true.");
        host.ThrowOnRead = false;

        controller.Upgrade(999);
        Check(host.Threads.Count == 0, "Suppressed prompt must not schedule the update log.");
        controller.CheckStartupUpdates();
        Check(host.UpdateChecks.Count == 1 && host.UpdateChecks[0].IsCompleted && host.Threads.Count == 0,
            "Suppressing notes must keep automatic update checking active without a pending notice.");
        host.Identity = "fixture|1.4.1|stable|416|0";
        Check(controller.ShouldAutoShowUpdateLog(), "A version change must show notes again.");
        controller.Upgrade(999);
        controller.CheckStartupUpdates();
        Task closed = controller.ShowAutomaticUpdateLog();
        Check(host.Threads.Count == 1, "Enabled prompt must schedule exactly one update log thread.");
        Check(host.UpdateChecks.Count == 2 && ReferenceEquals(host.UpdateChecks[1], closed) && !closed.IsCompleted,
            "Automatic checking must start and receive the pending notice's completion.");

        Action logThread = host.Threads[0];
        host.Threads.Clear();
        logThread();
        Check(closed.IsCompletedSuccessfully, "Closing the announcement did not release the automatic update prompt.");
        Check(host.Notices.SequenceEqual(new[] { true }), "Upgrade must reach the same automatic dialog path as startup.");

        host.Dismissed = host.Identity;
        controller.ShowUpdateLog();
        Check(host.Threads.Count == 1, "Manual viewing must remain available while suppressed.");
        host.Threads[0]();
        Check(host.Notices.SequenceEqual(new[] { true, false }), "Manual viewing must carry automatic=false.");

        var failedReadHost = new UpdateLogHost { ThrowOnRead = true };
        var failedRead = new LegacyMainWindowController<FakeStack, object, FakeAnim>(new LegacyMainWindowView(), failedReadHost.Build());
        failedRead.ShowAutomaticUpdateLog();
        Check(failedReadHost.Threads.Count == 1, "A failed preference read must still schedule a notice.");
    }

    // ---------- A-003.2 mod favorites ----------
    private sealed class FakeMod : LocalResourceFileData<ResourceProjectModelData, ResourceVersionModelData> {
        public FakeMod(string path) : base(new FileInfo(path), new LocalResourceFileServices<ResourceProjectModelData, ResourceVersionModelData> {
            ReadProject = _ => throw new InvalidOperationException("no project"),
            ReadVersion = _ => throw new InvalidOperationException("no version"),
            HideUpdates = () => false, DisableInstanceUpdates = () => false,
            ReadHashCache = _ => "", WriteHashCache = (_, _) => { },
            ReadBytes = _ => Array.Empty<byte>(), Sha1 = _ => ""
        }) { }
    }

    // The local page retains its original four filters; favorites now live in the
    // launcher-wide resource page and never filter or rebuild this file list.
    private static void ModFavoriteChecks() {
        string root = Path.Combine(Path.GetTempPath(), "pcl-a003-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var mods = new List<FakeMod>();
            foreach (string name in new[] { "Alpha.jar", "Beta.jar.disabled", "Gamma.jar" }) {
                string file = Path.Combine(root, name); File.WriteAllBytes(file, new byte[] { 1 });
                mods.Add(new FakeMod(file) { Display = name });
            }
            var items = new ConcurrentDictionary<string, Border>();
            var selected = new List<string>();
            mods[0].DeclaredModIds = new[] { "same_mod" }; mods[1].DeclaredModIds = new[] { "same_mod" };
            bool[] states = new bool[5]; string[] texts = new string[5];
            Panel list = new StackPanel(); Border empty = new(); string search = ""; int loaderCalls = 0;
            var filters = Enumerable.Range(0, 5).Select(index => new ResourcePageFilter(new Border(), index,
                () => states[index], value => states[index] = value, value => texts[index] = value)).ToArray();
            var view = new LegacyModPageView {
                Root = () => new Border(), Back = () => new Border(), Empty = () => empty, ListBack = () => new Border(),
                List = () => list, Search = () => search, SetSearch = value => search = value,
                SetSelectionText = _ => { }, ScrollHome = () => { }, FocusBack = () => { }, EnableIndex = _ => { }, IndexText = _ => { },
                Filters = () => filters, EnableDisable = _ => { }, EnableEnable = _ => { }, EnableUpdate = _ => { },
                SelectionBar = (int count, ref int previous, bool animate) => { }, SetEmptyTitle = _ => { }, SetEmptyHint = _ => { }
            };
            var host = new LegacyModPageHost<FakeMod, ResourceProjectModelData, ResourceVersionModelData, Border, object, object, Border, ModDependencyImpactItemData> {
                Output = () => mods, Items = () => items, Selected = () => selected, SetSelected = value => selected = value,
                Instance = () => null, AnimationControl = () => 0, ChangeAnimationControl = _ => { },
                LoaderRun = type => { loaderCalls++; return true; }, NewItem = (entry, isSelected) => new Border { Tag = entry },
                BindUpdate = (_, _) => { }, SetChecked = (_, _) => { }
            };
            var page = new LegacyModPage<FakeMod, ResourceProjectModelData, ResourceVersionModelData, Border, object, object, Border, ModDependencyImpactItemData>(view, host);
            page.LoadUI(); int serial = page.ListRebuildSerial;
            Check(items.Count == 3 && list.Children.Count == 3 && filters.Length == 5, "Local page must retain its original filters and add duplicate detection.");
            page.FilterChanged(1); Check(list.Children.Count == 2, "Enabled scope changed.");
            page.FilterChanged(2); Check(list.Children.Count == 1, "Disabled scope changed.");
            page.FilterChanged(4); Check(page.Filter == LegacyModFilter.Duplicate && list.Children.Count == 2, "Root-ID duplicate filter missed active/disabled copies.");
            page.FilterChanged(5); Check(page.Filter == LegacyModFilter.All && list.Children.Count == 3, "Unknown filters must fall back to All.");
            search = "gamma"; page.SearchRun(); Check(list.Children.Count == 1, "Search changed.");
            page.SortOrder = LocalResourceSortOrder.AddedOldest;
            Check(page.ListRebuildSerial == serial && loaderCalls == 0, "Local filters/search/sort rebuilt the loader.");
        } finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
