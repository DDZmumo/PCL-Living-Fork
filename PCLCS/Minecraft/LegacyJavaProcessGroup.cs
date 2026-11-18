// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

/// <summary>Observes JVMs belonging to the ordinary launch without taking ownership of unrelated windows.</summary>
internal sealed class LegacyJavaProcessGroup : IDisposable {
    private readonly SafeWaitHandle job;
    private LegacyJavaProcessGroup(SafeWaitHandle job) => this.job = job;

    internal static LegacyJavaProcessGroup? TryAttach(Process entry) {
        if (!OperatingSystem.IsWindows()) return null;
        SafeWaitHandle? handle = null;
        try {
            if (entry.HasExited || !IsJava(entry.ProcessName)) return null;
            // This job is for observation. Keep the ordinary JVM's process limit and
            // lifetime policy so leaving the launcher never terminates the game.
            handle = MmcSessionProcessTree.CreateConfiguredJobHandle(limitActiveProcesses: false);
            if (!MmcSessionProcessTree.TryAssignProcessToJob(handle, entry, out _)) return null;
            var group = new LegacyJavaProcessGroup(handle);
            handle = null;
            return group;
        } catch (Exception error) when (error is Win32Exception or InvalidOperationException) {
            return null;
        } finally {
            handle?.Dispose();
        }
    }

    internal bool HasActiveJava() {
        if (job.IsClosed) return false;
        if (!TryObserve(out int[] pids)) return !job.IsClosed;
        foreach (int pid in pids) {
            using Process? process = OpenJava(pid);
            if (process is not null) return true;
        }
        if (pids.Length == 0) return false;
        // A relay can exit between the snapshot and opening its handle,
        // immediately after it started the real game.
        if (!TryObserve(out int[] current)) return !job.IsClosed;
        return Array.Exists(current, pid => Array.IndexOf(pids, pid) < 0);
    }

    internal Process? OpenJava(int pid) {
        if (job.IsClosed || pid <= 0) return null;
        Process? process = null;
        try {
            process = Process.GetProcessById(pid);
            if (!IsJava(process.ProcessName) || process.HasExited ||
                !IsProcessInJob(process.SafeHandle, job, out bool belongs) || !belongs) return null;
            Process member = process;
            process = null;
            return member;
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) {
            return null;
        } finally {
            process?.Dispose();
        }
    }

    internal bool TerminateJava() {
        if (!TryObserve(out int[] pids)) return false;
        // A non-Java helper (for example a separate media process) does not
        // become a game process merely by sharing the launcher job.
        foreach (int pid in pids) {
            using Process? process = OpenJava(pid);
            if (process is null) continue;
            try { process.Kill(); }
            catch (InvalidOperationException) { /* It already exited. */ }
        }
        return true;
    }

    public void Dispose() => job.Dispose();

    private bool TryObserve(out int[] pids) {
        try { return MmcSessionProcessTree.TryObserveJobProcessIds(job, out pids, out _); }
        catch (ObjectDisposedException) { pids = []; return false; }
    }

    private static bool IsJava(string name) => string.Equals(name, "java", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "javaw", StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeWaitHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
