// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Behavior source: LF Modules/Base tooling helpers; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PCLCS;
// LF's private actual PrivilegeToken and P/Invoke declarations stay in the
// typed old-name adapter. This host performs no policy, state or workflow.
public delegate bool LegacyMemoryLookup<TToken>(string? systemName, string name, ref TToken state)
    where TToken : struct;
public delegate bool LegacyMemoryCapture<TToken>(IntPtr token, bool disableAll, ref TToken state, uint length, ref TToken previous, ref uint returned)
    where TToken : struct;
public delegate bool LegacyMemoryRestore<TToken>(IntPtr token, bool disableAll, ref TToken previous, uint length, IntPtr previousOutput, IntPtr returned)
    where TToken : struct;
public delegate int LegacyMemoryApply(int informationClass, ref int command, uint informationLength);
public sealed class LegacyNativeMemoryHost<TToken>
    where TToken : struct
{
    public Func<uint, uint, TToken> CreateToken = null!;
    public Func<TToken, uint> Count = null!;
    public LegacyMemoryLookup<TToken> Lookup = null!;
    public LegacyMemoryCapture<TToken> Capture = null!;
    public LegacyMemoryRestore<TToken> Restore = null!;
    public LegacyMemoryApply Apply = null!;
}

public sealed class LegacyNativeMemoryOptimizer<TToken>
    where TToken : struct
{
    private const int SystemMemoryListInformation = 80;
    private const uint SePrivilegeEnabled = 2;
    private readonly LegacyNativeMemoryHost<TToken> host;
    public LegacyNativeMemoryOptimizer(LegacyNativeMemoryHost<TToken> host) => this.host = host;
    public void Run()
    {
        try
        {
            LegacyMemoryOptimizationOperation.ExecuteCommands(AcquireProfilePrivilege, command =>
            {
                int nativeCommand = command;
                return host.Apply(SystemMemoryListInformation, ref nativeCommand, 4);
            });
        }
        catch (DllNotFoundException error)
        {
            throw new InvalidOperationException("当前系统缺少内存优化所需的系统组件。", error);
        }
        catch (EntryPointNotFoundException error)
        {
            throw new InvalidOperationException("当前系统不支持内存优化所需的系统接口。", error);
        }
    }

    // Same actual privilege token access: Query | Duplicate | AdjustPrivileges.
    public static WindowsIdentity OpenPrivilegeIdentity() => WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate | TokenAccessLevels.AdjustPrivileges);
    private IDisposable AcquireProfilePrivilege()
    {
        WindowsIdentity identity = OpenPrivilegeIdentity();
        ProfilePrivilegeLease? lease = null;
        try
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("内存优化功能需要管理员权限。");
            TToken state = host.CreateToken(1, SePrivilegeEnabled);
            if (!host.Lookup(null, "SeProfileSingleProcessPrivilege", ref state))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"获取内存优化权限标识失败（错误代码：{error}）。");
            }

            lease = new ProfilePrivilegeLease(identity, host);
            lease.Enable(ref state);
            return lease;
        }
        catch
        {
            if (lease is null)
                identity.Dispose();
            else
                lease.Dispose();
            throw;
        }
    }

    private sealed class ProfilePrivilegeLease : IDisposable
    {
        private WindowsIdentity identity;
        private TToken previousState;
        private bool restoreRequired, isDisposed;
        private readonly LegacyNativeMemoryHost<TToken> host;
        internal ProfilePrivilegeLease(WindowsIdentity identity, LegacyNativeMemoryHost<TToken> host)
        {
            this.identity = identity;
            this.host = host;
        }

        internal void Enable(ref TToken state)
        {
            uint returned = 0;
            bool adjusted = host.Capture(identity.Token, false, ref state, checked((uint)Marshal.SizeOf(typeof(TToken))), ref previousState, ref returned);
            int error = Marshal.GetLastWin32Error();
            restoreRequired = adjusted && host.Count(previousState) != 0;
            if (!adjusted || error != 0)
                throw new Win32Exception(error, $"启用内存优化权限失败（错误代码：{error}）。");
        }

        public void Dispose()
        {
            if (isDisposed)
                return;
            isDisposed = true;
            try
            {
                if (restoreRequired)
                {
                    bool restored = host.Restore(identity.Token, false, ref previousState, 0, IntPtr.Zero, IntPtr.Zero);
                    int error = Marshal.GetLastWin32Error();
                    if (!restored || error != 0)
                        throw new Win32Exception(error, $"恢复内存优化前的权限状态失败（错误代码：{error}）。");
                }
            }
            finally
            {
                identity.Dispose();
                identity = null!;
            }
        }
    }
}
