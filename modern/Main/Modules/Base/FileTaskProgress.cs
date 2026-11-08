// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Threading;

namespace PCL;

internal sealed class FileTaskProgress {
    internal static Action<double> Slice(Action<double> report, double start, double finish) => PCLCS.FileTaskProgress.Slice(report, start, finish);
    internal static Action<long> Bytes(long total, Action<double> report) => PCLCS.FileTaskProgress.Bytes(total, report);
    internal static long Size(IEnumerable<string> paths) => PCLCS.FileTaskProgress.Size(paths);
    internal static string Digest(string address, CancellationToken token, Action<double> report) => PCLCS.FileTaskProgress.Digest(address, token, report);
    internal static void Copy(string source, string destination, CancellationToken token, Action<double> report) => PCLCS.FileTaskProgress.Copy(source, destination, token, report);
}
