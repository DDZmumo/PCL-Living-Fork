// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using PCLCS;

namespace PCL
{
    internal sealed class GameGarbageCleaner
    {
        internal sealed class CleanupResult : LegacyGameGarbageCleanupResult
        {
        }
        private GameGarbageCleaner()
        {
        }
        internal static CleanupResult Clean(IEnumerable<string> GameFolders, Func<bool> ShouldStop = null)
        {
            return LegacyGameGarbageCleaner.Clean(GameFolders, () => new CleanupResult(), ShouldStop);
        }
    }
}