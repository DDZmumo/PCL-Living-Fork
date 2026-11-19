// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using PCLCS.Applications;

namespace PCL;

internal static class LauncherEntryPoint {
    [STAThread]
    public static int Main(string[] args) {
        if (args is [LocalSkinServiceProcess.Command]) {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            return LocalSkinServiceProcess.RunWorkerAsync(input, output).GetAwaiter().GetResult();
        }

        Application.Main();
        return Environment.ExitCode;
    }
}
