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

namespace PCLCS;

/// <summary>LF backup commands and confirmations; the host supplies its existing loader and worker lifetime.</summary>
public sealed class WorldBackupWorkflow<TInstance> where TInstance : class {
    private readonly WorldBackupEngine<TInstance> _engine;
    private readonly WorldBackupWorkflowHost<TInstance> _host;
    public int BusyCount;
    public bool IsBusy => Volatile.Read(ref BusyCount) != 0;

    public WorldBackupWorkflow(WorldBackupEngine<TInstance> engine, WorldBackupWorkflowHost<TInstance> host) {
        _engine = engine;
        _host = host;
    }

    public void Start(TInstance? instance, string? restoreId = null, IEnumerable<string>? deleteIds = null,
        string? worldIdentifier = null) {
        if (instance is null || _engine.IsBusy || _engine.HasActiveGame(_host.GameFolder(instance)) || _host.LauncherBusy()) {
            _host.Hint("请先关闭游戏并等待版本任务完成。", true);
            return;
        }
        if (Interlocked.CompareExchange(ref BusyCount, 1, 0) != 0) return;
        bool removing = deleteIds is not null;
        string[]? ids = deleteIds?.ToArray();
        bool restoring = restoreId is not null;
        WorldBackupWork work = null!;
        work = new() {
            SnapshotReservation = () => !restoring && (removing || !_engine.HasPendingRecovery(_host.GameFolder(instance))),
            StepName = removing ? "删除存档备份" : restoring ? "恢复世界" : "备份世界",
            Execute = (token, setOutput) => {
                Action<double>? progress = work.Progress;
                if (removing) {
                    if (_host.Confirm($"永久删除选中的 {ids!.Length} 份存档备份？\r\n备份文件将直接删除，无法还原。不会删除当前存档。",
                        "删除存档备份", "删除") != 1) throw new OperationCanceledException();
                    token.ThrowIfCancellationRequested();
                    setOutput(_engine.DeletePointsWithCancellation(_host.GameFolder(instance), ids!, token));
                    return;
                }
                if (restoring) {
                    var point = _engine.Points(_host.GameFolder(instance))
                        .FirstOrDefault(item => item["id"]!.ToString() == restoreId);
                    if (point is null) throw new IOException("该备份已不存在，请刷新列表。");
                    string text = $"将 {point["label"]} 恢复到 {point["created"]!.ToObject<DateTime>().ToLocalTime():yyyy-MM-dd HH:mm:ss}。\r\n{point["description"]}\r\n\r\n恢复前会自动备份当前世界。";
                    if (!_host.SamePath(_host.VersionPath(instance), _host.GameFolder(instance)))
                        text += "\r\n此版本使用共用目录，恢复结果也会影响使用该存档目录的其他版本。";
                    if (_host.Confirm(text, "恢复存档备份", "恢复") != 1) throw new OperationCanceledException();
                    setOutput(_engine.RestoreWithProgress(_host.GameFolder(instance), restoreId!, _host.Retention(instance), token, progress));
                    return;
                }
                List<string> saved;
                if (worldIdentifier is null) {
                    saved = _engine.CaptureAllWithProgress(instance, "手动备份", token, progress);
                } else {
                    saved = new List<string> {
                        _engine.CaptureWorldWithProgress(_host.GameFolder(instance), worldIdentifier, "手动备份", _host.Retention(instance), token, progress)
                    };
                }
                setOutput(saved.Count.ToString());
                if (saved.Count == 0) _host.Dispatch(() => _host.Hint("此游戏目录没有可备份的存档。", false));
            },
            Title = (removing ? "删除备份：" : restoring ? "恢复世界：" : "备份世界：") + _host.InstanceName(instance),
            Stopped = () => {
                Interlocked.Exchange(ref BusyCount, 0);
                _engine.NotifyChanged();
            }
        };
        if (!_host.StartWork(instance, work)) Interlocked.Exchange(ref BusyCount, 0);
    }
}
