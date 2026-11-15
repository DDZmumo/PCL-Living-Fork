// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;

namespace PCLCS.Applications;

public interface IInstanceSelectionApplicationService<TInstance> where TInstance : class {
    InstanceSelectionSnapshot<TInstance> ReadSnapshot();
    bool Select(TInstance? instance);
    bool SelectFolder(string folder);
    event Action? Changed;
}

public sealed class InstanceSelectionRuntimeState<TInstance> where TInstance : class {
    public string? FolderCache;
    public TInstance? Instance;
}

public sealed class InstanceSelectionApplicationCommands<TInstance> where TInstance : class {
    public Action VerifyAccess = null!;
    public Func<TInstance?, bool> CanSelect = null!;
    public Func<string> ReadFolder = null!;
    public Action<string> SetFolder = null!;
    public Action<TInstance?> ApplySelection = null!;
}

public sealed class InstanceSelectionApplicationService<TInstance> : IInstanceSelectionApplicationService<TInstance> where TInstance : class {
    private readonly InstanceSelectionApplicationCommands<TInstance> commands;
    public InstanceSelectionApplicationService(InstanceSelectionApplicationCommands<TInstance> commands) => this.commands = commands;
    [Newtonsoft.Json.JsonIgnore]
    public InstanceSelectionRuntimeState<TInstance> Runtime { get; } = new();
    public event Action? Changed;
    public InstanceSelectionSnapshot<TInstance> ReadSnapshot() => new(commands.ReadFolder(), Runtime.Instance);
    public bool Select(TInstance? instance) {
        commands.VerifyAccess();
        if (!commands.CanSelect(instance)) return false;
        ApplyLegacySelection(instance); return true;
    }
    public bool SelectFolder(string folder) {
        commands.VerifyAccess();
        if (!commands.CanSelect(Runtime.Instance)) return false;
        string previous = commands.ReadFolder();
        commands.SetFolder(folder);
        if (!string.Equals(previous, commands.ReadFolder(), StringComparison.Ordinal)) Changed?.Invoke();
        return true;
    }
    /// <summary>Preserves existing engine/UI sequencing; the compatibility setter must not duplicate the stored instance.</summary>
    public void ApplyLegacySelection(TInstance? instance) {
        TInstance? previous = Runtime.Instance;
        commands.ApplySelection(instance);
        if (!ReferenceEquals(previous, Runtime.Instance)) Changed?.Invoke();
    }
}
