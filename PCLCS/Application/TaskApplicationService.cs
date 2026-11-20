// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;

namespace PCLCS.Applications;

public interface ITaskApplicationService {
    IReadOnlyList<ApplicationTaskHandle> GetTasks();
}

/// <summary>An observer/command handle bound to the actual task object, also valid after its taskbar row is removed.</summary>
public sealed class ApplicationTaskHandle {
    private readonly Func<ApplicationTaskSnapshot> read;
    private readonly Action cancel;
    public ApplicationTaskHandle(Func<ApplicationTaskSnapshot> read, Action cancel) { this.read = read; this.cancel = cancel; }
    public ApplicationTaskSnapshot ReadSnapshot() => read();
    /// <summary>True means cancellation was forwarded; it does not prove worker completion or release any lease.</summary>
    public bool RequestCancellation() {
        if (!read().CanCancel) return false;
        cancel(); return true;
    }
}

public sealed class TaskApplicationService<TTask> : ITaskApplicationService where TTask : class {
    private readonly Func<TTask, ApplicationTaskSnapshot> read;
    private readonly Action<TTask> cancel;
    public TaskApplicationService(Func<TTask, ApplicationTaskSnapshot> read, Action<TTask> cancel) { this.read = read; this.cancel = cancel; }
    // The old taskbar and new observers share these actual task objects. No cached state or cancellation flag is maintained here.
    [Newtonsoft.Json.JsonIgnore]
    public ConcurrentList<TTask> RuntimeTasks { get; set; } = new();
    public IReadOnlyList<ApplicationTaskHandle> GetTasks() => Array.AsReadOnly(RuntimeTasks.Select(Observe).ToArray());
    public ApplicationTaskHandle Observe(TTask task) => new(() => read(task), () => cancel(task));
}
