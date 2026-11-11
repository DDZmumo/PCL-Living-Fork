// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>原 LF 环境还原点命令、确认、恢复前留档和真实工作任务生命周期。</summary>
public sealed class EnvironmentHistoryWorkflow<TInstance> where TInstance : class {
    private readonly EnvironmentHistoryEngine<TInstance> engine;
    private readonly EnvironmentHistoryWorkflowHost<TInstance> host;
    public int BusyCount;
    public bool IsBusy => Volatile.Read(ref BusyCount) != 0;
    public EnvironmentHistoryWorkflow(EnvironmentHistoryEngine<TInstance> engine, EnvironmentHistoryWorkflowHost<TInstance> host) {
        this.engine = engine; this.host = host;
    }
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);

    public void Start(TInstance? instance, bool restore, string? pointId = null, IEnumerable<string>? deleteIds = null) {
        if (instance is null || host.HasRunningGame() || host.HasRunningSessions() || host.LaunchLoading() || host.Updating() || host.AnyLocked() || host.UpdatingModFolders()) {
            host.HintError("请先关闭游戏并等待版本任务完成。");
            return;
        }
        if (Interlocked.CompareExchange(ref BusyCount, 1, 0) != 0) return;
        bool removing = deleteIds is not null;
        List<string>? ids = deleteIds?.ToList();
        EnvironmentHistoryWork work = null!;
        work = new() {
            SnapshotReservation = () => !restore && (removing || !host.HasPending(host.VersionPath(instance))),
            StepName = removing ? "删除还原点" : restore ? "恢复游戏环境" : "创建环境还原点",
            Execute = (token, setOutput) => {
                Action<double>? progress = work.Progress;
                if (removing) {
                    if (host.Confirm($"永久删除选中的 {ids!.Count} 个还原点？\r\n还原点记录及不再使用的数据将直接删除，无法还原。不会删除游戏文件或其他还原点。", "删除还原点", "删除", "取消", true) != 1) throw new OperationCanceledException();
                    token.ThrowIfCancellationRequested();
                    setOutput(engine.DeletePointsWithCancellation(host.VersionPath(instance), ids, token));
                    return;
                }
                if (host.HasPending(host.VersionPath(instance))) {
                    if (host.Confirm("上次环境修改被中断，需要先恢复到修改前。", "恢复未完成的操作", "恢复", "取消", true) != 1) throw new OperationCanceledException();
                    instance = host.RecoverInstance(instance);
                }
                if (!restore) {
                    setOutput(engine.CaptureInstanceWithProgress(instance, "手动还原点", token, progress));
                    return;
                }
                List<JObject> points = engine.Points(host.VersionPath(instance)).OrderByDescending(item => (bool)item["healthy"]!).ThenByDescending(item => (DateTime)item["created"]!).ToList();
                if (!points.Any()) throw new IOException("尚无环境还原点。可先创建还原点，后续更新前也会自动保存。");
                int? choice = null;
                if (pointId is not null) {
                    int index = points.FindIndex(item => SameText(item["id"]!.ToString(), pointId));
                    if (index < 0) throw new IOException("该还原点已不存在，请刷新列表。");
                    choice = index;
                } else {
                    choice = host.SelectPoint(points.Select(item => new EnvironmentHistoryChoice {
                        Text = ((DateTime)item["created"]!).ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + item["label"]!.ToString() + ((bool)item["healthy"]! ? " · 正常运行" : ""),
                        ToolTip = item["description"]?.ToString()
                    }));
                }
                if (!choice.HasValue) throw new OperationCanceledException();
                JObject point = points[choice.Value];
                if (host.Confirm("将整体恢复该时间点的 Minecraft、加载器、模组和配置，并移除之后新增的环境文件。\r\n" +
                    "存档、截图和地图标记保持当前内容；恢复旧游戏版本后，请确认它仍支持你的存档。\r\n\r\n" +
                    (host.SamePath(host.VersionPath(instance), host.GameFolder(instance)) ? "" : "此版本未使用独立目录，恢复模组和配置也会影响共用目录的其他版本。\r\n\r\n") +
                    "恢复前会自动保存当前环境。\r\n" + point["description"]?.ToString(), "恢复游戏环境", "恢复", "取消", true) != 1) throw new OperationCanceledException();
                string mcRoot = Path.GetDirectoryName(Path.GetDirectoryName(host.VersionPath(instance).TrimEnd('\\'))) + "\\";
                string output = engine.RestoreWithProgress(host.VersionPath(instance), host.GameFolder(instance), mcRoot, point["id"]!.ToString(), token, progress,
                    captureBefore: () => engine.CaptureInstanceWithProgress(instance, "恢复前自动保存", token, FileTaskProgress.Slice(progress, 0.32, 0.62)));
                setOutput(output);
                engine.ApplyRetention(instance, output, token);
                host.DispatchWait(() => {
                    host.ResetSettingsCache(instance);
                    TInstance refreshed = host.CreateLoadedInstance(host.VersionPath(instance));
                    host.ClearIniCache(host.VersionPath(instance) + "PCL\\Setup.ini");
                    if (host.PageInstance() is not null && host.SamePath(host.VersionPath(host.PageInstance()!), host.VersionPath(instance))) host.SetPageInstance(refreshed);
                    if (host.SelectedInstance() is not null && host.SamePath(host.VersionPath(host.SelectedInstance()!), host.VersionPath(instance))) host.SetSelectedInstance(refreshed);
                    host.RefreshPageName();
                });
            },
            Title = (removing ? "删除还原点：" : restore ? "环境恢复：" : "保存环境：") + host.Name(instance),
            Stopped = () => {
                Interlocked.Exchange(ref BusyCount, 0);
                host.Dispatch(host.NotifyChanged);
            }
        };
        if (!host.StartWork(instance, work)) Interlocked.Exchange(ref BusyCount, 0);
    }
}
