// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;
using PCLCS;
using PCLCS.Applications;

namespace PCL;

internal enum TaskStageState { Waiting, Running, AwaitingConfirmation, Succeeded, Failed, Cancelling, Canceled, Skipped, NotRun }

internal sealed record TaskStage(string Name, TaskStageState State, double? Progress, string? Detail = null);

internal sealed record TaskManagerItem(ApplicationTaskSnapshot Task, bool CanClose, bool Cancelling, string ActionHint,
    string? Error, IReadOnlyList<TaskStage> Stages);

internal readonly record struct TaskTransferMetrics(double Progress, bool HasActiveTasks, long DownloadRate, long ReadRate,
    long WriteRate, int RemainingFiles, bool DownloadCountEstimated);

/// <summary>Observes the actual loader tasks and retains failed entries until dismissed.</summary>
internal sealed class TaskManagerApplicationService {
    private readonly object retiredGate = new();
    private readonly HashSet<ModLoader.LoaderBase> retired = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<ModLoader.LoaderBase, byte> cancelling = new(ReferenceEqualityComparer.Instance);
    private readonly PCLCS.FileTaskProgress.IoRateSampler rates = new();

    internal void OnTaskAdded(ModLoader.LoaderBase task) {
        lock (retiredGate) retired.Remove(task);
    }

    internal void OnTaskRetiring(ModLoader.LoaderBase task) {
        lock (retiredGate) {
            if (task.Show && KeepRetired(task) && ModLoader.LoaderTaskbar.Contains(task)) retired.Add(task);
        }
    }

    internal IReadOnlyList<TaskManagerItem> ReadItems() => VisibleTasks().Select(ReadItem).ToArray();

    internal void ResetRates() => rates.Reset();

    internal TaskTransferMetrics SampleTransfers(IReadOnlyList<TaskManagerItem> items) {
        var active = items.Where(item => item.Task.State is ApplicationTaskState.Waiting or ApplicationTaskState.Running
            || item.Task.WorkloadActive == true).ToArray();
        if (active.Length == 0) {
            rates.Reset();
            return new(1, false, 0, 0, 0, 0, false);
        }

        rates.Sample();
        int remaining = ModNet.NetManager.FileRemain;
        return new(Math.Clamp(active.Average(item => item.Task.Progress), 0, 1), true,
            ModNet.NetManager.Speed + (long)rates.DownloadPerSecond, (long)rates.ReadPerSecond, (long)rates.WritePerSecond,
            Math.Max(0, remaining) + PCLCS.FileTaskProgress.ActiveDownloads, remaining < 0);
    }

    internal void CloseOrCancel(int taskId) {
        ModLoader.LoaderBase? task = VisibleTasks().FirstOrDefault(task => task.Uuid == taskId);
        if (task is null || !CanClose(task)) return;

        if (task.State == ModBase.LoadState.Failed) {
            lock (retiredGate) {
                ModLoader.LoaderTaskbar.Remove(task);
                retired.Remove(task);
            }
            return;
        }

        if (!cancelling.TryAdd(task, 0)) return;
        ModBase.RunInThread(() => {
            try { task.Cancel(); }
            finally { cancelling.TryRemove(task, out _); }
        });
    }

    private ModLoader.LoaderBase[] VisibleTasks() {
        lock (retiredGate) {
            retired.RemoveWhere(task => !KeepRetired(task));
            return ((List<ModLoader.LoaderBase>)ModLoader.LoaderTaskbar).Concat(retired).Distinct<ModLoader.LoaderBase>(ReferenceEqualityComparer.Instance)
                .Where(task => task.Show && IsVisible(task)).OrderByDescending(task => task.Uuid).ToArray();
        }
    }

    private static bool IsVisible(ModLoader.LoaderBase task) =>
        task.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading || KeepRetired(task);

    private static bool KeepRetired(ModLoader.LoaderBase task) =>
        task.State == ModBase.LoadState.Failed ||
        task.HasActiveWorkload == true || task is IMmcImportProgressOperation { ProducerCompleted: false };

    private bool CanClose(ModLoader.LoaderBase task) {
        if (task.State == ModBase.LoadState.Failed)
            return task.HasActiveWorkload != true && task is not IMmcImportProgressOperation { ProducerCompleted: false };
        if (task.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting) || cancelling.ContainsKey(task)) return false;
        return task is not IMmcImportProgressOperation mmc ||
            !mmc.ImportProgress.CancellationRequested && !mmc.ImportProgress.DecisionStarted;
    }

    private TaskManagerItem ReadItem(ModLoader.LoaderBase task) {
        ApplicationTaskSnapshot snapshot = LauncherApplicationServices.ReadTask(task);
        string? error = snapshot.State == ApplicationTaskState.Failed ? task.Error?.GetDisplay(true) ?? "任务失败" : null;
        bool cancellationRequested = cancelling.ContainsKey(task) || snapshot.State == ApplicationTaskState.CancellationRequested;
        string hint = error is not null ? "关闭任务" : "取消任务";
        IReadOnlyList<TaskStage> stages;
        if (task is IMmcImportProgressOperation mmc) {
            cancellationRequested |= mmc.ImportProgress.CancellationRequested;
            if (mmc.ImportProgress.DecisionStarted) hint = "正在保存导入结果，请等待文件操作结束";
            else if (error is null) hint = "取消导入";
            stages = mmc.ImportProgress.Snapshot(task.State).Select(ImportStage).ToArray();
        } else {
            var children = new List<ModLoader.LoaderBase>();
            ModLoader.LoaderCombo<object>.GetLoaderList(task, ref children);
            stages = ModpackUpdateFlow.GetStageSnapshot(children) is { } packStages
                ? packStages.Select(PackStage).ToArray()
                : error is not null ? Array.Empty<TaskStage>() : children.Select(LoaderStage).ToArray();
        }

        if (cancellationRequested) hint = "正在取消，等待文件操作结束";
        return new(snapshot, CanClose(task), cancellationRequested, hint, error, stages);
    }

    private static TaskStage LoaderStage(ModLoader.LoaderBase task) => new(task.Name, task.State switch {
        ModBase.LoadState.Finished => TaskStageState.Succeeded,
        ModBase.LoadState.Loading => TaskStageState.Running,
        ModBase.LoadState.Waiting => TaskStageState.Waiting,
        ModBase.LoadState.Canceled => TaskStageState.Canceled,
        _ => TaskStageState.Failed
    }, task.State == ModBase.LoadState.Loading ? task.Progress : null);

    private static TaskStage PackStage(PackUpdateStageSnapshot stage) => new(stage.Name, stage.State switch {
        PackUpdateStageState.Running => TaskStageState.Running,
        PackUpdateStageState.AwaitingConfirmation => TaskStageState.AwaitingConfirmation,
        PackUpdateStageState.Finished => TaskStageState.Succeeded,
        PackUpdateStageState.Skipped => TaskStageState.Skipped,
        PackUpdateStageState.Failed => TaskStageState.Failed,
        PackUpdateStageState.Canceled => TaskStageState.Canceled,
        _ => TaskStageState.Waiting
    }, stage.Progress, stage.Detail);

    private static TaskStage ImportStage(MmcImportStageSnapshot stage) {
        string detail = stage.Detail;
        if (stage.CompletedFiles is { } completedFiles)
            detail += " " + completedFiles + (stage.TotalFiles is { } total ? "/" + total : "");
        else if (stage.TotalFiles is { } totalFiles)
            detail += "（共 " + totalFiles + " 个文件）";
        if (stage.TotalBytes > 0)
            detail += " · " + StringUtils.FormatByteSize(stage.CompletedBytes) + " / " + StringUtils.FormatByteSize(stage.TotalBytes);
        else if (stage.CompletedBytes > 0)
            detail += " · 累计 " + StringUtils.FormatByteSize(stage.CompletedBytes);

        return new(stage.Name, stage.State switch {
            MmcImportStageState.Running => TaskStageState.Running,
            MmcImportStageState.Finished => TaskStageState.Succeeded,
            MmcImportStageState.Skipped => TaskStageState.Skipped,
            MmcImportStageState.Cancelling => TaskStageState.Cancelling,
            MmcImportStageState.Canceled => TaskStageState.Canceled,
            MmcImportStageState.Failed => TaskStageState.Failed,
            MmcImportStageState.NotRun => TaskStageState.NotRun,
            _ => TaskStageState.Waiting
        }, stage.Progress, detail);
    }
}
