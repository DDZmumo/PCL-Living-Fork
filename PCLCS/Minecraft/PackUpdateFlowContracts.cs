// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Primitive ref-field access keeps the original private VB Active field/hook as the
// one storage; the C# workflow owns when to acquire/release it.
public static class PackUpdateFlowState {
    public static int Read(ref int active) => Volatile.Read(ref active);
    public static int CompareExchange(ref int active, int value, int comparand) => Interlocked.CompareExchange(ref active, value, comparand);
    public static int Exchange(ref int active, int value) => Interlocked.Exchange(ref active, value);
}
public sealed class PackUpdateRuntimeRequest {
    public string Name = null!, BuildFolder = null!, MinecraftFolder = null!, Minecraft = null!;
    public Dictionary<ModLoaders, string> Loaders = null!;
}
public sealed class PackUpdateFlowHost<TPackage, TPlan, TNetFile, TVersion, TInstance, TTask, TCombo, TWorker, TProgress>
    where TPackage : class where TPlan : class where TVersion : ResourceVersionModelData
    where TInstance : class where TTask : class where TCombo : class where TWorker : class where TProgress : class {
    public Func<int> ReadActive = null!;
    public Func<int, int, int> CompareExchangeActive = null!;
    public Func<int, int> ExchangeActive = null!;
    public Func<bool> EnvironmentBusy = null!, HasRunningGame = null!, HasRunningSessions = null!, LaunchLoading = null!;
    public Func<TInstance, bool> IsLocked = null!;
    public Action<string> Hint = null!, HintRed = null!;
    public Func<string, string?> SelectFile = null!;
    public Func<string, Action<TTask>, TTask> CreateStep = null!;
    public Func<string, TTask, TCombo> CreateCombo = null!;
    public Func<TCombo, string, TWorker> CreateWorker = null!;
    public Func<TWorker, string, bool> TryAcquire = null!;
    public Action<TWorker, Action> OnStopped = null!;
    public Action<TWorker> StartWorker = null!;
    public Action<TCombo> AddTaskbar = null!, CancelCombo = null!;
    public Action RefreshButton = null!;
    public Func<TInstance, string> Name = null!, VersionFolder = null!, GameRoot = null!;
    public Func<TInstance, McVersion> Version = null!;
    public Action<Action> UiWait = null!;
    public Func<string?> InputProjectAddress = null!;
    public Func<string, JToken> Request = null!;
    public Func<JObject, ResourceTypes, TVersion> ParseVersion = null!;
    public Action<string, string> EnsureWithin = null!;
    public Func<string, string, string, string, bool, int> Message = null!;
    public Func<string, TPlan, Func<McVersion>, int> Preview = null!;
    public Func<List<TVersion>, string, string?, TVersion?> SelectVersion = null!;
    public Func<string, ResourcePlatforms, ResourceProjectModelData> Project = null!;
    public Action<string> RemoveVersionCache = null!;
    public Func<string, ResourcePlatforms, bool, List<TVersion>> Versions = null!;
    public Func<TVersion, string, string?, ModLoaders, TNetFile> ToUpdateFile = null!;
    public Action<List<TNetFile>, CancellationToken, TProgress?> Download = null!;
    public Func<string> TempFolder = null!;
    public Func<TTask, CancellationToken> Cancellation = null!;
    public Action<TTask, double> SetProgress = null!;
    public Action<TTask, PackUpdateStage, PackUpdateStageState, string?> SetStage = (_, _, _, _) => { };
    public Action<TTask, PackUpdateStage, double?> SetStageProgress = (_, _, _) => { };
    public Func<TTask, double, double, TProgress> Progress = null!;
    public Action<TTask, string> SetOutput = null!;
    public Func<PackUpdateRuntimeRequest, TCombo> CreateRuntime = null!;
    public Func<TCombo, string, TProgress, Action> RuntimeWorkload = null!;
    public Action<TInstance, string, string, CancellationToken> BeforeMinecraftChange = null!;
    public Action<TInstance, string, CancellationToken> CaptureHistory = null!;
    public Action<TInstance> ResetSettings = null!;
    public Func<string, TInstance> LoadInstance = null!;
    public Func<TInstance?> PageInstance = null!, SelectedInstance = null!;
    public Action<TInstance> SetPageInstance = null!, SetSelectedInstance = null!;
    public Action<string> Explorer = null!, Info = null!;
}
