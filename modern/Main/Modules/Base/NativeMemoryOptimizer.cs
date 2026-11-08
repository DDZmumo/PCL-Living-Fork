// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PCLCS;

namespace PCL
{

    internal sealed class NativeMemoryOptimizer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PrivilegeToken
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        private static readonly LegacyNativeMemoryOptimizer<PrivilegeToken> Backend = new LegacyNativeMemoryOptimizer<PrivilegeToken>(new LegacyNativeMemoryHost<PrivilegeToken>()
        {
            CreateToken = new Func<uint, uint, PrivilegeToken>((Count, Attributes) => new PrivilegeToken() { PrivilegeCount = Count, Attributes = Attributes }),
            Count = new Func<PrivilegeToken, uint>(State => State.PrivilegeCount),
            Lookup = Lookup,
            Capture = AdjustTokenPrivilegesCapture,
            Restore = AdjustTokenPrivilegesRestore,
            Apply = NtSetSystemInformation
        });
        private static bool Lookup(string SystemName, string Name, ref PrivilegeToken State)
        {
            return LookupPrivilegeValueW(SystemName, Name, ref State.Luid);
        }
        private NativeMemoryOptimizer()
        {
        }
        internal static void Run()
        {
            Backend.Run();
        }
        internal static void ExecuteCommands(Func<IDisposable> Acquire, Func<int, int> Apply)
        {
            LegacyMemoryOptimizationOperation.ExecuteCommands(Acquire, Apply);
        }
        internal static WindowsIdentity OpenPrivilegeIdentity()
        {
            return LegacyNativeMemoryOptimizer<PrivilegeToken>.OpenPrivilegeIdentity();
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool LookupPrivilegeValueW(string SystemName, string Name, ref long Luid);

        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", ExactSpelling = true, SetLastError = true)]
        private static extern bool AdjustTokenPrivilegesCapture(IntPtr Token, bool DisableAllPrivileges, ref PrivilegeToken NewState, uint BufferLength, ref PrivilegeToken PreviousState, ref uint ReturnLength);

        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", ExactSpelling = true, SetLastError = true)]
        private static extern bool AdjustTokenPrivilegesRestore(IntPtr Token, bool DisableAllPrivileges, ref PrivilegeToken NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtSetSystemInformation(int InformationClass, ref int Command, uint InformationLength);
    }

    internal sealed class MemoryOptimizationOperation
    {
        internal const int FailedExitCode = LegacyMemoryOptimizationOperation.FailedExitCode;
        private MemoryOptimizationOperation()
        {
        }
        internal static long Execute(bool IsElevated, Func<long> ReadAvailable, Action Optimize, Func<int> RunElevated)
        {
            return LegacyMemoryOptimizationOperation.Execute(IsElevated, ReadAvailable, Optimize, RunElevated);
        }
        internal static int ExecuteHelper(Func<long> ReadAvailable, Action Optimize)
        {
            return LegacyMemoryOptimizationOperation.ExecuteHelper(ReadAvailable, Optimize);
        }
    }
}