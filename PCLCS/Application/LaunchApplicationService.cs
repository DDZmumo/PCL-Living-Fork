// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Diagnostics;
using System.Text;

namespace PCLCS.Applications;

public interface ILaunchApplicationService<TOptions> where TOptions : class {
    LaunchApplicationSnapshot ReadSnapshot();
    bool Start(TOptions? options = null);
    void RequestCancellation();
}

/// <summary>The sole storage for the existing launch slots. Only compatibility adapters should write these slots.</summary>
public sealed class LaunchRuntimeState<TOptions, TInstance, TTask, TOperation, TWatcher>
    where TOptions : class where TInstance : class where TTask : class where TOperation : class where TWatcher : class {
    public TOptions Options = null!;
    public TInstance? Target;
    public TTask Task = null!;
    public TOperation Operation = null!;
    public Process Process = null!;
    public TWatcher Watcher = null!;
    public Java Java = null!;
    public string Arguments = null!, CancellationHint = null!;
    public Encoding Encoding = null!;
}

public sealed class LaunchApplicationCommands<TOptions, TInstance> where TOptions : class where TInstance : class {
    public Action VerifyAccess = null!;
    public Func<bool> IsActive = null!;
    public Action ReportBusy = null!;
    public Func<TOptions?, TInstance?> ResolveTarget = null!;
    public Func<TOptions?, bool> Start = null!;
    public Action Cancel = null!;
    public Func<LaunchApplicationSnapshot> Read = null!;
}

public sealed class LaunchApplicationService<TOptions, TInstance, TTask, TOperation, TWatcher> : ILaunchApplicationService<TOptions>
    where TOptions : class where TInstance : class where TTask : class where TOperation : class where TWatcher : class {
    private readonly LaunchApplicationCommands<TOptions, TInstance> commands;
    private bool admitting;
    public LaunchApplicationService(LaunchApplicationCommands<TOptions, TInstance> commands) => this.commands = commands;
    [Newtonsoft.Json.JsonIgnore]
    public LaunchRuntimeState<TOptions, TInstance, TTask, TOperation, TWatcher> Runtime { get; private set; } = new();
    public LaunchApplicationSnapshot ReadSnapshot() => commands.Read();
    public bool Start(TOptions? options = null) {
        commands.VerifyAccess();
        // The host serializes UI commands. Reject reentrant requests before touching the active session.
        if (admitting || commands.IsActive()) { commands.ReportBusy(); return false; }
        var previous = Runtime;
        admitting = true;
        try {
            // A new attempt reuses the real root loader, not the previous session's process/operation references.
            Runtime = new() { Task = previous.Task, Target = commands.ResolveTarget(options) };
            bool accepted = commands.Start(options);
            if (!accepted) Runtime = previous;
            return accepted;
        } catch { Runtime = previous; throw; }
        finally { admitting = false; }
    }
    public void RequestCancellation() { commands.VerifyAccess(); if (commands.IsActive()) commands.Cancel(); }
}
