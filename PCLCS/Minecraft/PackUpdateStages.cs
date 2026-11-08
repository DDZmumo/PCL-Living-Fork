// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Linq;

namespace PCLCS;

public enum PackUpdateStage {
    ReadRecord, PreparePackage, CompareFiles, PrepareFiles, ConfirmPreview, ValidateChoices,
    PrepareRuntime, MigrateSettings, CaptureHistory, BackupFiles, ApplyFiles, RefreshInstance
}
public enum PackUpdateStageState { Waiting, Running, AwaitingConfirmation, Finished, Skipped, Failed, Canceled }

public sealed class PackUpdateStageSnapshot {
    public PackUpdateStage Stage { get; }
    public string Name { get; }
    public PackUpdateStageState State { get; }
    public string? Detail { get; }
    public double? Progress { get; }
    internal PackUpdateStageSnapshot(PackUpdateStage stage, string name, PackUpdateStageState state, string? detail, double? progress) {
        Stage = stage; Name = name; State = state; Detail = detail; Progress = progress;
    }
}

// Presentation only: no loaders, weights, IO or work scheduling. Each state is
// reported at its actual work boundary; advancing never completes other stages.
public sealed class PackUpdateStageProgress {
    private static readonly string[] Names = {
        "读取整合包记录", "获取新版整合包", "比较作者文件与本地修改", "准备文件与检查模组",
        "确认更新预览", "复查所选更新方案", "准备 Minecraft 与加载器", "迁移个人游戏设置",
        "备份更新前的环境", "校验并备份待替换文件", "应用文件更新", "刷新版本信息"
    };
    private readonly object gate = new();
    private readonly PackUpdateStageState[] states = new PackUpdateStageState[Names.Length];
    private readonly string?[] details = new string?[Names.Length];
    private readonly double?[] progress = new double?[Names.Length];
    private readonly long[] generations = new long[Names.Length];
    private bool stopped;
    public void Reset() {
        lock (gate) { Array.Clear(states, 0, states.Length); Array.Clear(details, 0, details.Length); Array.Clear(progress, 0, progress.Length); for (int i = 0; i < generations.Length; i++) generations[i]++; stopped = false; }
    }
    public void Set(PackUpdateStage stage, PackUpdateStageState state, string? detail = null) {
        lock (gate) {
            if (stopped) return;
            if (states[(int)stage] != state || details[(int)stage] != detail) { progress[(int)stage] = null; generations[(int)stage]++; }
            states[(int)stage] = state; details[(int)stage] = detail;
        }
    }
    public void SetProgress(PackUpdateStage stage, double? value) {
        lock (gate) {
            if (stopped || states[(int)stage] != PackUpdateStageState.Running) return;
            progress[(int)stage] = value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? Math.Max(0, Math.Min(1, value.Value)) : null;
        }
    }
    public Action<double> CreateProgressReporter() {
        lock (gate) {
            int index = Array.FindLastIndex(states, state => state == PackUpdateStageState.Running);
            if (index < 0) return _ => { };
            long generation = generations[index];
            return value => { lock (gate) {
                if (generations[index] == generation) SetProgress((PackUpdateStage)index, value);
            } };
        }
    }
    public void Stop(PackUpdateStageState state) {
        if (state is not (PackUpdateStageState.Failed or PackUpdateStageState.Canceled)) throw new ArgumentOutOfRangeException(nameof(state));
        lock (gate) {
            if (stopped) return;
            stopped = true;
            for (int i = 0; i < states.Length; i++)
                if (states[i] is PackUpdateStageState.Running or PackUpdateStageState.AwaitingConfirmation) states[i] = state;
        }
    }
    public PackUpdateStageSnapshot[] Snapshot() {
        lock (gate) return Names.Select((name, i) => new PackUpdateStageSnapshot((PackUpdateStage)i, name, states[i], details[i], progress[i])).ToArray();
    }
}
