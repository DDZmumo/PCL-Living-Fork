// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Threading;
using MeloongCore;
using PCLCS;

namespace PCL
{

    internal sealed class MapDownloadInstaller
    {
        private static readonly MapDownloadFiles Files = new MapDownloadFiles(PathUtils.RemoveExtendedPrefix);
        internal const string DownloadFolderName = MapDownloadFiles.DownloadFolderName;

        internal static string GetDownloadFolder(string SavesFolder)
        {
            return Files.GetDownloadFolder(SavesFolder);
        }

        internal static string Install(string ArchivePath, string SavesFolder, CancellationToken Token)
        {
            return Files.Install(ArchivePath, SavesFolder, Token);
        }
    }
}