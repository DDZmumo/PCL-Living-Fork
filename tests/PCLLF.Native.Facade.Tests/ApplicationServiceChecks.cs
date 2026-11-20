// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Reflection;
using PCL;
using PCLCS.Applications;

namespace PCLLF.Native.Facade.Tests;

internal static class ApplicationServiceChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope scope = new(environment);
        string folder = Path.Combine(scope.Root, "application-selection");
        Directory.CreateDirectory(folder);
        scope.SelectMinecraft(folder);
        var instances = LauncherApplicationServices.Instances;
        var launches = LauncherApplicationServices.Launch;
        _ = LauncherApplicationServices.Accounts;
        object launchOwner = Native.Field(typeof(LauncherApplicationServices), "LaunchOwner").GetValue(null)!;
        object launchState = Native.Property(launchOwner.GetType(), "Runtime").GetValue(launchOwner)!;
        object accountOwner = Native.Field(typeof(LauncherApplicationServices), "AccountOwner").GetValue(null)!;
        object accountState = Native.Property(accountOwner.GetType(), "Runtime").GetValue(accountOwner)!;
        Native.Check(launchOwner.GetType().Assembly == typeof(PCLCS.Java).Assembly && accountOwner.GetType().Assembly == typeof(PCLCS.Java).Assembly,
            "Application state owners must be actual Core services.");
        Native.Check(typeof(ModLaunch).GetField("McLoginMsLoader", Native.Flags) is null &&
            ReferenceEquals(ModLaunch.McLoginMsLoader, Native.Get(accountState, "Microsoft")), "Legacy login state was copied instead of forwarded.");
        Native.Check(typeof(ModMinecraft).GetField("_McInstanceSelected", Native.Flags) is null &&
            typeof(ModLoader).GetField("LoaderTaskbar", Native.Flags) is null, "An obsolete module state field remains authoritative.");

        string firstPath = Path.Combine(folder, "versions", "owned-first");
        string secondPath = Path.Combine(folder, "versions", "owned-second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        var first = new ModMinecraft.McInstance(firstPath);
        var second = new ModMinecraft.McInstance(secondPath);
        int changes = 0;
        Action changed = () => { Native.Check(ReferenceEquals(instances.ReadSnapshot().Instance, ModMinecraft.McInstanceSelected), "Selection notification preceded its actual state."); changes++; };
        instances.Changed += changed;
        ModLoader.LoaderTask<ModLaunch.McLaunchOptions, object> originalTask = ModLaunch.McLaunchLoader;
        object? originalTarget = Native.Get(launchState, "Target");
        int animationControl = ModAnimation.AniControlEnabled;
        using ManualResetEventSlim entered = new(), release = new();
        Thread? thread = null;
        var task = new ModLoader.LoaderTask<ModLaunch.McLaunchOptions, object>("Owned application workload", _ => {
            entered.Set();
            if (!release.Wait(10000)) throw new TimeoutException("Owned application workload was not released.");
        });
        Native.Property(task.GetType(), "InterruptOnCancel").SetValue(task, false);
        try {
            // Use the existing animation-suspended selection path: server account switching is covered by its own guarded family.
            ModAnimation.AniControlEnabled = 1;
            Native.Check(instances.Select(first) && instances.Select(first) && changes == 1 &&
                ReferenceEquals(ModMinecraft.McInstanceSelected, first), "Application and compatibility selection paths diverged.");
            ModLaunch.McLaunchLoader = task;
            Native.Set(launchState, "Target", first);
            Native.Check(ReferenceEquals(Native.Get(launchState, "Task"), task), "Legacy task replacement did not reach the real launch owner.");
            ModLoader.LoaderTaskbar.Add(task);
            ApplicationTaskHandle handle = LauncherApplicationServices.Tasks.GetTasks().Single(value => value.ReadSnapshot().Id == task.Uuid);
            task.Start(new ModLaunch.McLaunchOptions { Instance = first }); thread = task.LastRunningThread;
            Native.Check(entered.Wait(5000), "Real application loader did not start.");
            Native.Check(!instances.Select(second) && ReferenceEquals(instances.ReadSnapshot().Instance, first), "New UI selection bypassed an active launch.");
            // An existing engine may refresh selection independently. Its change must not retarget the running operation.
            ModMinecraft.McInstanceSelected = second;
            object engine = Native.Field(typeof(ModLaunch), "Service").GetValue(null)!;
            object host = engine.GetType().GetFields(Native.Flags).Single(field => field.FieldType.Name.StartsWith("LegacyLaunchHost`", StringComparison.Ordinal)).GetValue(engine)!;
            var selectedForWork = (Func<ModMinecraft.McInstance>)Native.Get(host, "SelectedInstance")!;
            Native.Check(ReferenceEquals(instances.ReadSnapshot().Instance, second) && ReferenceEquals(selectedForWork(), first) &&
                launches.ReadSnapshot().InstancePath == first.PathVersion, "Changing selection retargeted the actual launch host.");
            Native.Check(handle.RequestCancellation(), "Application task handle did not cancel its actual loader.");
            ModLoader.LoaderTaskbar.Remove(task);
            ApplicationTaskSnapshot canceling = handle.ReadSnapshot();
            Native.Check(canceling.State == ApplicationTaskState.CancellationRequested && canceling.WorkloadActive == true && !canceling.IsSettled &&
                ReferenceEquals(selectedForWork(), first) && !instances.Select(first), "Cancellation discarded a real cleanup tail or its pinned launch target.");
            release.Set();
            Native.Check(thread.Join(5000), "Actual canceled loader cleanup did not finish.");
            Native.Check(handle.ReadSnapshot().IsSettled && ReferenceEquals(selectedForWork(), second) && instances.Select(second),
                "Application state did not observe genuine worker completion.");
            Native.Check(ReferenceEquals(instances.ReadSnapshot().Instance, ModMinecraft.McInstanceSelected), "Compatibility state acquired a second owner.");
        } finally {
            release.Set();
            if (thread is not null && !thread.Join(5000)) throw new TimeoutException("Owned application task escaped its fixture.");
            ModLoader.LoaderTaskbar.Remove(task);
            ModLaunch.McLaunchLoader = originalTask;
            Native.Set(launchState, "Target", originalTarget);
            ModAnimation.AniControlEnabled = animationControl;
            instances.Changed -= changed;
        }
        scope.FinishActualTasks();
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS application services: actual shared Core owners, compatibility forwarders, committed selection, pinned launch target, taskbar-independent observers and real cancellation cleanup; no account/network/game activity.");
    }
}
