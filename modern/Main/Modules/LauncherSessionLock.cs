// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;

namespace PCL
{
    // 原内部类型身份保留；实际BCL单例锁/持有/反向释放全部由C#实现。
    internal sealed class LauncherSessionLock : IDisposable
    {
        private readonly PCLCS.Startup.LauncherSessionLease Native;
        private LauncherSessionLock(PCLCS.Startup.LauncherSessionLease Lease)
        {
            Native = Lease;
        }
        internal static LauncherSessionLock TryAcquire(IEnumerable<string> Names, int Timeout)
        {
            var Lease = PCLCS.Startup.LauncherSessionLease.TryAcquire(Names, Timeout);
            return Lease is null ? null : new LauncherSessionLock(Lease);
        }
        public void Dispose()
        {
            Native.Dispose();
        }
    }
}