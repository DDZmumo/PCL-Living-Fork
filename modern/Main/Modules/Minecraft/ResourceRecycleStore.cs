// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections.Generic;
using System.Threading;
using MeloongCore;
using PCLCS;

namespace PCL
{

    internal sealed class ResourceRecycleStore
    {
        private static readonly ResourceRecycleFiles<InstanceResourceEntry> Files = new ResourceRecycleFiles<InstanceResourceEntry>(PathUtils.RemoveExtendedPrefix, (Left, Right) => PathUtils.IsSame(Left, Right), () => new InstanceResourceEntry(), (Item, FileName, Width) => Item.Icon = InstanceResourceStore.LoadImage(FileName, Width));
        internal class Record
        {
            public string Id;
            public string Box;
            public string Destination;
            public InstanceResourceEntry Entry;
        }

        private static Record ReadRecord(string Root, string Recycle, string Id)
        {
            var Value = Files.ReadRecord(Root, Recycle, Id);
            return new Record() { Id = Value.Id, Box = Value.Box, Destination = Value.Destination, Entry = Value.Entry };
        }

        internal static List<InstanceResourceEntry> List(string Root, string Recycle, InstanceResourceKind Kind)
        {
            return Files.List(Root, Recycle, (int)Kind);
        }

        internal static void Restore(string Root, string Recycle, IEnumerable<string> Ids, CancellationToken Token)
        {
            Files.Restore(Root, Recycle, Ids, Token);
        }


        internal static void Purge(string Root, string Recycle, IEnumerable<string> Ids, CancellationToken Token)
        {
            Files.Purge(Root, Recycle, Ids, Token);
        }
    }
}