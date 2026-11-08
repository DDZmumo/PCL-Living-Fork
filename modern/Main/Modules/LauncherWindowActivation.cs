// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;

namespace PCL
{
    // 原Win32测试入口与内部类型保留；真实Unicode lookup/协议/重试业务在C#。
    internal sealed class LauncherWindowActivation
    {
        private LauncherWindowActivation()
        {
        }
        private static IntPtr FindWindowW(string ClassName, string WindowName)
        {
            return PCLCS.Startup.LauncherWindowActivation.FindWindow(ClassName, WindowName);
        }
        internal static bool TryActivateExistingWindow()
        {
            return PCLCS.Startup.LauncherWindowActivation.TryActivateExistingWindow();
        }
        internal static bool RequestActivation(Func<string, IntPtr> Lookup, Func<IntPtr, uint, bool> Queue, Action<IntPtr> Foreground, Action<int> Pause)
        {
            return PCLCS.Startup.LauncherWindowActivation.RequestActivation(Lookup, Queue, Foreground, Pause);
        }
    }
}