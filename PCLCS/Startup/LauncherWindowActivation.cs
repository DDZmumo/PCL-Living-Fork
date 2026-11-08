// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCLCS.Startup;

// LF LauncherWindowActivation.vb at22d549d24e125c356c07a88122b23ccc4e81839c.
// PCL's existing caption/6402 protocol
// stays compatible; CE is not a replacement implementation for this LF helper.
// This startup path deliberately depends only on BCL/Win32, before settings/logs.
public static class LauncherWindowActivation {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string? className, string windowName);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr window);
    public static IntPtr FindWindow(string? className, string windowName) => FindWindowW(className, windowName);
    public static bool TryActivateExistingWindow() => RequestActivation(
        caption => FindWindowW(null, caption),
        (window, message) => PostMessageW(window, message, IntPtr.Zero, IntPtr.Zero),
        window => { SetForegroundWindow(window); }, Thread.Sleep);
    public static bool RequestActivation(Func<string, IntPtr> lookup, Func<IntPtr, uint, bool> queue,
            Action<IntPtr> foreground, Action<int> pause) {
        for (int attempt = 0; attempt <= 9; attempt++) {
            foreach (string caption in new[] { "LivingPCL　", "Plain Craft Launcher　", "Plain Craft Launcher 2　" }) {
                IntPtr window = lookup(caption);
                if (window == IntPtr.Zero) continue;
                bool queued = queue(window, 6402U);
                foreground(window);
                return queued;
            }
            if (attempt < 9) pause(200);
        }
        return false;
    }
}
