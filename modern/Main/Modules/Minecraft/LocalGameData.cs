// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL
{
    // 本机个人游戏资料，位于整合包和共享游戏配置之外。
    internal sealed class LocalGameData
    {
        internal static readonly object Gate = new object();
        internal static string TestRoot { get; set; }
        private static readonly LocalGameDataStore Store = new LocalGameDataStore(() => Root, (Root, FileName) => InstanceResourceStore.EnsureWithin(Root, FileName));
        internal static LocalGameDataStore Service
        {
            get
            {
                return Store;
            }
        }
        internal static string Root
        {
            get
            {
                return LocalGameDataStore.GetRoot(TestRoot);
            }
        }
        internal static string Address(string Name)
        {
            return Store.Address(Name);
        }
        internal static JObject ReadJson(string Name)
        {
            return Store.ReadJson(Name);
        }
        internal static void WriteJson(string Name, JObject Value)
        {
            Store.WriteJson(Name, Value);
        }
        internal static string Hash(byte[] Bytes)
        {
            return LocalGameDataStore.Hash(Bytes);
        }
        internal static void AtomicWrite(string FileName, byte[] Bytes, string ExpectedHash, bool Backup)
        {
            LocalGameDataStore.AtomicWrite(FileName, Bytes, ExpectedHash, Backup);
        }
        internal static bool ProcessMatches(int Id, long StartedUtc)
        {
            return LocalGameDataStore.ProcessMatches(Id, StartedUtc);
        }
    }
}