// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Newtonsoft.Json;
using PCLCS.Applications;
using LaunchService = PCLCS.Applications.LaunchApplicationService<object, object, object, object, object>;

internal static class ApplicationServiceTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("application launch rejects overlapping requests before changing the active session", LaunchAdmission);
        yield return ("application launch pins its target and restores it after failed admission", LaunchTarget);
        yield return ("application commands reject wrong-thread access before any mutation", ThreadAccess);
        yield return ("application selection publishes one committed instance and observes the effective folder", Selection);
        yield return ("application selection refusal and failed persistence never publish a false completion", SelectionFailures);
        yield return ("application account commands validate explicit input and expose no credential payload", Accounts);
        yield return ("application logout keeps one atomic provider transaction and stops after failed save", Logout);
        yield return ("application task observers retain the real canceled workload after taskbar removal", TaskObservation);
        yield return ("application task completion remains unknown without workload evidence", UnknownCompletion);
        yield return ("application launch cancellation retains operation log and process cleanup order", Cancellation);
    }

    private static ApplicationTaskSnapshot Task(ApplicationTaskState state = ApplicationTaskState.Waiting, bool? active = false) =>
        new(17, "synthetic job", state, 0.4, active);
    private static LaunchApplicationCommands<object, object> LaunchCommands() => new() {
        VerifyAccess = () => { }, IsActive = () => false, ReportBusy = () => { }, ResolveTarget = _ => null,
        Start = _ => true, Cancel = () => { }, Read = () => new(Task(), null, null, false)
    };
    private static void LaunchAdmission() {
        var ports = LaunchCommands();
        var service = new LaunchService(ports);
        object active = new(), target = new();
        service.Runtime.Options = active; service.Runtime.Target = target;
        int starts = 0, reports = 0;
        ports.IsActive = () => true; ports.Start = _ => { starts++; return true; }; ports.ReportBusy = () => reports++;
        Require(!service.Start(new()) && starts == 0 && reports == 1 &&
            ReferenceEquals(service.Runtime.Options, active) && ReferenceEquals(service.Runtime.Target, target), "Busy launch changed active state.");
        ports.IsActive = () => false;
        ports.Start = request => {
            starts++;
            Require(!service.Start(new()), "A reentrant launch entered its own admission callback.");
            service.Runtime.Options = request!; return true;
        };
        object accepted = new();
        Require(service.Start(accepted) && starts == 1 && reports == 2 && ReferenceEquals(service.Runtime.Options, accepted), "Launch admission lost its actual request.");
        int cancellations = 0;
        ports.Cancel = () => cancellations++;
        service.RequestCancellation();
        Require(cancellations == 0, "An idle launch cancellation touched an earlier game session.");
        ports.IsActive = () => true; service.RequestCancellation();
        Require(cancellations == 1, "An active launch cancellation was not forwarded.");
    }
    private static void LaunchTarget() {
        object selected = new(), original = selected;
        var ports = LaunchCommands(); ports.ResolveTarget = _ => selected;
        var service = new LaunchService(ports);
        object root = new(); service.Runtime.Task = root;
        service.Runtime.Operation = new(); service.Runtime.Watcher = new(); service.Runtime.Arguments = "previous session arguments";
        ports.Start = _ => { selected = new(); return true; };
        Require(service.Start() && ReferenceEquals(service.Runtime.Target, original) && ReferenceEquals(service.Runtime.Task, root) &&
            service.Runtime.Operation is null && service.Runtime.Watcher is null && service.Runtime.Arguments is null,
            "Admission retargeted the session or retained previous process/operation data.");
        object operation = new(); service.Runtime.Operation = operation;
        ports.Start = _ => false;
        Require(!service.Start() && ReferenceEquals(service.Runtime.Target, original) && ReferenceEquals(service.Runtime.Operation, operation), "Rejected admission replaced the previous session.");
        ports.Start = _ => throw new IOException("synthetic admission failure");
        Throws<IOException>(() => service.Start());
        Require(ReferenceEquals(service.Runtime.Target, original), "Failed admission replaced the previous target.");
        bool started = false; object startedTarget = selected;
        ports.IsActive = () => started;
        ports.Start = _ => { started = true; throw new IOException("synthetic failure after loader activation"); };
        Throws<IOException>(() => service.Start());
        Require(ReferenceEquals(service.Runtime.Target, startedTarget) && service.Runtime.Operation is null,
            "A fault after loader activation restored another session's target or process references.");
    }
    private static void ThreadAccess() {
        var ports = LaunchCommands(); int mutations = 0;
        ports.VerifyAccess = () => throw new InvalidOperationException("synthetic wrong thread");
        ports.ResolveTarget = _ => { mutations++; return new(); }; ports.Cancel = () => mutations++;
        var service = new LaunchService(ports);
        Throws<InvalidOperationException>(() => service.Start());
        Throws<InvalidOperationException>(service.RequestCancellation);
        Require(mutations == 0 && service.Runtime.Target is null, "Wrong-thread command mutated application state.");
    }
    private static void Selection() {
        InstanceSelectionApplicationService<object> service = null!;
        var ports = new InstanceSelectionApplicationCommands<object> {
            VerifyAccess = () => { }, CanSelect = _ => true, ReadFolder = () => "pack-effective-folder",
            SetFolder = _ => { }, ApplySelection = value => service.Runtime.Instance = value
        };
        service = new(ports); service.Runtime.FolderCache = "ordinary-cached-folder";
        object selected = new(); int changes = 0;
        service.Changed += () => { Require(ReferenceEquals(service.ReadSnapshot().Instance, selected), "Selection event preceded its commit."); changes++; };
        Require(service.Select(selected) && service.Select(selected) && changes == 1, "Selection event was duplicated.");
        Require(service.ReadSnapshot().Folder == "pack-effective-folder", "The selection query substituted a stale folder cache.");
    }
    private static void SelectionFailures() {
        int writes = 0, events = 0;
        var ports = new InstanceSelectionApplicationCommands<object> {
            VerifyAccess = () => { }, CanSelect = _ => false, ReadFolder = () => "current",
            SetFolder = _ => writes++, ApplySelection = _ => writes++
        };
        var service = new InstanceSelectionApplicationService<object>(ports); service.Changed += () => events++;
        Require(!service.Select(new()) && !service.SelectFolder("other") && writes == 0 && events == 0, "Refused selection still wrote state.");
        ports.CanSelect = _ => true; ports.CanChangeFolder = () => false;
        Require(!service.SelectFolder("locked-pack-folder") && writes == 0, "Pack mode accepted a folder change.");
        ports.ApplySelection = _ => throw new IOException("synthetic persistence failure");
        Throws<IOException>(() => service.Select(new()));
        Require(events == 0, "A failed selection published success.");
    }
    private static void Accounts() {
        int starts = 0; object? received = null; bool allowed = false;
        var ports = new AccountApplicationCommands<object> {
            VerifyAccess = () => { }, Read = () => new(AccountProvider.Microsoft, "owned user", Task(), false),
            Validate = input => ReferenceEquals(input, received) ? "" : "invalid input", CanSignIn = () => allowed,
            Start = _ => starts++, Cancel = () => { }, SignOut = new()
        };
        var service = new AccountApplicationService<object, object, object, object, object>(ports);
        service.Runtime.Login = new { AccessToken = "synthetic-token-marker", RefreshToken = "synthetic-refresh-marker" };
        received = new();
        Require(!service.SignIn(received) && starts == 0, "Account command ignored active-work admission.");
        allowed = true;
        Require(!service.SignIn(new()) && service.SignIn(received) && starts == 1, "Account command did not validate its explicit request.");
        string json = JsonConvert.SerializeObject(service.ReadSnapshot());
        string serviceJson = JsonConvert.SerializeObject(service);
        Require(!json.Contains("synthetic-token-marker") && !json.Contains("synthetic-refresh-marker") &&
            !json.Contains("AccessToken") && !json.Contains("RefreshToken") && !serviceJson.Contains("Runtime") &&
            !serviceJson.Contains("synthetic-token-marker"), "Account UI metadata or service serialization exposed credentials.");
    }
    private static void Logout() {
        List<string> order = new(); Dictionary<string, object?>? transaction = null;
        var host = new AccountSignOutHost {
            CancelMicrosoft = () => order.Add("cancel-ms"), CancelAuth = () => order.Add("cancel-auth"), CancelNide = () => order.Add("cancel-nide"),
            ResetAuthInput = () => order.Add("reset-auth"), ResetNideInput = () => order.Add("reset-nide"),
            Save = values => { order.Add("save"); transaction = values; }
        };
        AccountSessionActions.SignOut(AccountProvider.Microsoft, host);
        Require(order.SequenceEqual(new[] { "cancel-ms", "save" }) && transaction!.Count == 7 &&
            Equals(transaction["CacheMsV2Migrated"], true) && Equals(transaction["CacheMsV2Expires"], 0L) &&
            !transaction.ContainsKey("CacheMsOAuthRefresh"), "Microsoft logout changed its atomic V2-only transaction.");
        order.Clear(); host.Save = _ => { order.Add("save"); throw new IOException("synthetic save refusal"); };
        Throws<IOException>(() => AccountSessionActions.SignOut(AccountProvider.Auth, host));
        Require(order.SequenceEqual(new[] { "cancel-auth", "save" }), "Failed logout reset input after an uncommitted save.");
    }
    private sealed class ObservedTask {
        internal ApplicationTaskState State = ApplicationTaskState.Running;
        internal bool Active = true;
    }
    private static void TaskObservation() {
        var task = new ObservedTask();
        var service = new TaskApplicationService<ObservedTask>(value => Task(value.State, value.Active), value => value.State = ApplicationTaskState.CancellationRequested);
        service.RuntimeTasks.Add(task);
        ApplicationTaskHandle handle = service.GetTasks().Single();
        service.RuntimeTasks.Remove(task);
        Require(handle.RequestCancellation() && task.State == ApplicationTaskState.CancellationRequested &&
            !handle.ReadSnapshot().IsSettled && handle.ReadSnapshot().WorkloadActive == true, "Cancellation falsely completed an actual workload.");
        task.Active = false;
        Require(handle.ReadSnapshot().IsSettled && !handle.RequestCancellation(), "Observer lost the task after its row disappeared.");
    }
    private static void UnknownCompletion() {
        Require(!Task(ApplicationTaskState.Succeeded, null).IsSettled && !Task(ApplicationTaskState.CancellationRequested, null).IsSettled,
            "Unknown workload lifetime was treated as completed cleanup.");
    }
    private static void Cancellation() {
        List<string> order = new();
        var host = new LaunchCancellationHost {
            HasOperation = () => false, CancelOperation = () => order.Add("cancel"), LogCancellation = () => order.Add("log"),
            HasWatcher = () => true, KillWatcher = () => order.Add("kill"), ReadProcess = () => throw new Exception("Unexpected process fallback")
        };
        LaunchCancellationActions.Cancel(host); Require(order.Count == 0, "Empty launch cancellation performed work.");
        host.HasOperation = () => true;
        LaunchCancellationActions.Cancel(host);
        Require(order.SequenceEqual(new[] { "cancel", "log", "kill" }), "Cancellation changed operation/log/watcher order.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
