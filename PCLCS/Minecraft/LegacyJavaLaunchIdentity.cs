// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;

internal sealed record LegacyJavaLaunchIdentity(Guid LaunchId, int WindowsSession) {
    private const string JournalField = "legacyJavaLaunch";
    internal const string EnvironmentVariable = "PCLLF_LEGACY_JAVA_LAUNCH";
    private static readonly ConditionalWeakTable<Process, LegacyJavaLaunchIdentity> launches = new();

    internal string JobName => @"Local\PCLLF.LegacyJava." + LaunchId.ToString("N");
    internal bool JobAssigned { get; set; }
    internal bool WatcherCompleted { get; set; }

    internal static LegacyJavaLaunchIdentity? Prepare(ProcessStartInfo start) {
        if (!MmcCommandOperations.TryGetCurrentProcessSessionId(out int windowsSession)) return null;
        var identity = new LegacyJavaLaunchIdentity(Guid.NewGuid(), windowsSession);
        start.Environment[EnvironmentVariable] = identity.LaunchId.ToString("N");
        return identity;
    }

    internal void Bind(Process process) => launches.Add(process, this);

    internal static LegacyJavaLaunchIdentity? ForProcess(Process process) =>
        launches.TryGetValue(process, out var identity) ? identity : null;

    internal static void WriteJournal(JObject journal, Process game) {
        if (ForProcess(game) is not { } identity) return;
        journal[JournalField] = new JObject {
            ["id"] = identity.LaunchId.ToString("N"),
            ["windowsSession"] = identity.WindowsSession,
            ["jobAssigned"] = identity.JobAssigned
        };
    }

    internal static GameSessionProcessState ReadJournal(JObject journal, Process? completedEntry = null) {
        if (journal[JournalField] is not { } recorded)
            return LocalGameDataStore.CheckProcess(journal["pid"]!.ToObject<int>(), journal["started"]!.ToObject<long>());

        if (recorded is not JObject marker ||
            marker["id"]?.Type != JTokenType.String || !Guid.TryParseExact((string?)marker["id"], "N", out Guid launchId) || launchId == Guid.Empty ||
            marker["windowsSession"]?.Type != JTokenType.Integer || !int.TryParse(marker["windowsSession"]!.ToString(), out int session) || session < 0 ||
            marker["jobAssigned"]?.Type != JTokenType.Boolean ||
            journal["pid"]?.Type != JTokenType.Integer || !int.TryParse(journal["pid"]!.ToString(), out int pid) || pid <= 0 ||
            journal["started"]?.Type != JTokenType.Integer || !long.TryParse(journal["started"]!.ToString(), out long started) || started <= 0)
            return GameSessionProcessState.Unknown;

        // Only a watcher that observed the final game exit can close its own
        // sessions while the original Java relay job is still being released.
        if (completedEntry is not null && ForProcess(completedEntry) is { WatcherCompleted: true } completed &&
            completed.LaunchId == launchId && completed.WindowsSession == session && completedEntry.Id == pid &&
            completedEntry.StartTime.ToUniversalTime().Ticks == started) return GameSessionProcessState.Exited;

        GameSessionProcessState entry = LocalGameDataStore.CheckProcess(pid, started);
        if (entry != GameSessionProcessState.Exited) return entry;

        var identity = new LegacyJavaLaunchIdentity(launchId, session);
        if ((bool)marker["jobAssigned"]! &&
            MmcCommandOperations.TryGetCurrentProcessSessionId(out int currentSession) && currentSession == session) {
            switch (MmcSessionProcessTree.ObserveNamedJob(identity.JobName).State) {
                case MmcNamedJobState.Active: return GameSessionProcessState.Active;
                case MmcNamedJobState.Unknown: return GameSessionProcessState.Unknown;
            }
        }

        // An ordinary JVM can create a child before assignment, and closing the
        // launcher's last job handle removes its name while its members survive.
        return MmcCommandOperations.ProbeLiveJavaProcessesForToken(EnvironmentVariable, launchId.ToString("N"), started, session) switch {
            MmcCommandOperations.TokenScanResult.MatchAlive => GameSessionProcessState.Active,
            MmcCommandOperations.TokenScanResult.ProvenAbsent => GameSessionProcessState.Exited,
            _ => GameSessionProcessState.Unknown
        };
    }
}
