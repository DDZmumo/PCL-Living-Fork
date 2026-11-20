// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;

namespace PCLCS.Applications;

public enum ApplicationTaskState { Waiting, Running, Succeeded, Failed, CancellationRequested }
public enum AccountProvider { Offline = 0, Nide = 2, Auth = 3, Microsoft = 5 }

/// <summary>A point-in-time observation of a real loader, never an alternative task state machine.</summary>
public sealed class ApplicationTaskSnapshot {
    public ApplicationTaskSnapshot(int id, string name, ApplicationTaskState state, double progress,
        bool? workloadActive, string? errorType = null) {
        Id = id; Name = name; State = state; Progress = progress;
        WorkloadActive = workloadActive; ErrorType = errorType;
    }
    public int Id { get; }
    public string Name { get; }
    public ApplicationTaskState State { get; }
    public double Progress { get; }
    /// <summary>Null means the adapter cannot prove workload completion.</summary>
    public bool? WorkloadActive { get; }
    public string? ErrorType { get; }
    public bool CanCancel => State == ApplicationTaskState.Running;
    public bool IsSettled => State != ApplicationTaskState.Waiting && State != ApplicationTaskState.Running && WorkloadActive == false;
}

public sealed class LaunchApplicationSnapshot {
    public LaunchApplicationSnapshot(ApplicationTaskSnapshot task, string? instancePath, int? processId, bool hasRunningGame) {
        Task = task; InstancePath = instancePath; ProcessId = processId; HasRunningGame = hasRunningGame;
    }
    public ApplicationTaskSnapshot Task { get; }
    public string? InstancePath { get; }
    public int? ProcessId { get; }
    public bool HasRunningGame { get; }
}

/// <summary>UI account metadata only: credentials, raw login inputs and token-bearing errors are excluded.</summary>
public sealed class AccountApplicationSnapshot {
    public AccountApplicationSnapshot(AccountProvider provider, string? displayName, ApplicationTaskSnapshot task, bool authenticated) {
        Provider = provider; DisplayName = displayName; Task = task; Authenticated = authenticated;
    }
    public AccountProvider Provider { get; }
    public string? DisplayName { get; }
    public ApplicationTaskSnapshot Task { get; }
    public bool Authenticated { get; }
}

public sealed class InstanceSelectionSnapshot<TInstance> where TInstance : class {
    public InstanceSelectionSnapshot(string folder, TInstance? instance) { Folder = folder; Instance = instance; }
    public string Folder { get; }
    public TInstance? Instance { get; }
}
