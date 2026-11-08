// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using MeloongCore;
using PCLCS;

namespace PCL
{
    internal sealed class WorldMetadata
    {
        internal string Name;
        internal DateTime LastPlayedUtc;
        internal string VersionName;
        internal int? DataVersion;
        internal int? GameMode;
        internal int? Difficulty;
        internal bool? Hardcore;
        internal bool? AllowCommands;
        internal bool? WasModded;
        internal bool? Snapshot;
        internal long? Seed;
        internal int? SpawnX;
        internal int? SpawnY;
        internal int? SpawnZ;
        internal long? DayTime;
        internal long? Time;
        internal static WorldMetadata Read(string Folder)
        {
            return ReadCore(Folder, false);
        }
        internal static WorldMetadata ReadDetails(string Folder)
        {
            return ReadCore(Folder, true);
        }
        private static WorldMetadata ReadCore(string Folder, bool Details)
        {
            var Data = WorldMetadataFiles.Read(Folder, Details, (Root, FileName) => InstanceResourceStore.EnsureWithin(Root, FileName));
            if (Data is null)
                return null;
            return new WorldMetadata()
            {
                Name = Data.Name,
                LastPlayedUtc = Data.LastPlayedUtc,
                VersionName = Data.VersionName,
                DataVersion = Data.DataVersion,
                GameMode = Data.GameMode,
                Difficulty = Data.Difficulty,
                Hardcore = Data.Hardcore,
                AllowCommands = Data.AllowCommands,
                WasModded = Data.WasModded,
                Snapshot = Data.Snapshot,
                Seed = Data.Seed,
                SpawnX = Data.SpawnX,
                SpawnY = Data.SpawnY,
                SpawnZ = Data.SpawnZ,
                DayTime = Data.DayTime,
                Time = Data.Time
            };
        }
        internal static string Describe(string Folder)
        {
            return WorldMetadataFiles.Describe(Folder, (Root, FileName) => InstanceResourceStore.EnsureWithin(Root, FileName), (ErrorValue, Message) => Logger.Warn(ErrorValue, Message));
        }
    }
}