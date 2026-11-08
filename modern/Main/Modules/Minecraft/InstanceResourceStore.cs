// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore;
using MeloongCore.Extensions;
using PCLCS;

namespace PCL
{

    internal enum InstanceResourceKind
    {
        Saves,
        Screenshots,
        ResourcePacks,
        ShaderPacks,
        Schematics
    }

    internal sealed class InstanceResourceEntry : InstanceResourceEntryData
    {
        public ImageSource Icon;
    }

    // 只管理指定资源目录；读取不创建目录，写入不覆盖同名资源，删除可在本页恢复。
    internal sealed class InstanceResourceStore
    {
        private static readonly InstanceResourceTransactions Transactions = new InstanceResourceTransactions(PathUtils.RemoveExtendedPrefix);

        internal static string Title(InstanceResourceKind Kind)
        {
            return InstanceResourceKindRules.Title((int)Kind);
        }

        internal static string FolderName(InstanceResourceKind Kind)
        {
            return InstanceResourceKindRules.FolderName((int)Kind);
        }

        internal static string Root(string GameFolder, InstanceResourceKind Kind)
        {
            return Path.Combine(GameFolder, FolderName(Kind));
        }

        private static string Normalize(string Value)
        {
            return InstanceResourcePaths.Normalize(Value, PathUtils.RemoveExtendedPrefix);
        }

        internal static void EnsureWithin(string Root, string FileName, bool AllowRoot = false)
        {
            InstanceResourcePaths.EnsureWithin(Root, FileName, AllowRoot, PathUtils.RemoveExtendedPrefix);
        }

        internal static string SafeName(string Value)
        {
            return InstanceResourceNameRules.SafeName(Value);
        }

        internal static void DeleteStage(string Root, string Stage)
        {
            Transactions.DeleteStage(Root, Stage);
        }

        internal static void ValidatePackage(string FileName, InstanceResourceKind Kind)
        {
            InstanceResourcePackageRules.ValidatePackage(FileName, (int)Kind);
        }

        private static bool AcceptFile(InstanceResourceKind Kind, string FileName)
        {
            return InstanceResourceFileRules.AcceptFile((int)Kind, FileName);
        }

        internal static List<InstanceResourceEntry> Scan(string Root, string Folder, InstanceResourceKind Kind, CancellationToken Token) => ScanCached(Root, Folder, Kind, Token, null);

        internal static List<InstanceResourceEntry> ScanCached(string Root, string Folder, InstanceResourceKind Kind, CancellationToken Token, InstanceResourceScanCache<InstanceResourceEntry> Cache)
        {
            return InstanceResourceCatalog.ScanCached(Root, Folder, (int)Kind, Token, MapDownloadInstaller.DownloadFolderName, () => new InstanceResourceEntry(), (Item, FileName, Width) => Item.Icon = LoadImage(FileName, Width), (Item, Input, Width) => Item.Icon = DecodeImage(Input, Width), Value => Value.ReplaceLineEndings(" "), PathUtils.RemoveExtendedPrefix, Cache);
        }

        private static string FormatSize(long Size)
        {
            return InstanceResourceDetailsRules.FormatSize(Size);
        }

        private static void ReadPackInfo(InstanceResourceEntry Entry, ref string Detail)
        {
            InstanceResourceCatalog.ReadPackInfo(Entry, ref Detail, (Item, FileName, Width) => Item.Icon = LoadImage(FileName, Width), (Item, Input, Width) => Item.Icon = DecodeImage(Input, Width), Value => Value.ReplaceLineEndings(" "));
        }

        internal static BitmapSource LoadImage(string FileName, int Width)
        {
            return InstanceResourceCatalog.LoadImage(FileName, Width, InstanceResourceImages.Decode);
        }

        private static BitmapSource DecodeImage(Stream Stream, int Width)
        {
            return InstanceResourceImages.Decode(Stream, Width);
        }

        internal static void ValidateEntry(string Root, InstanceResourceEntry Entry)
        {
            InstanceResourceEntryValidator.ValidateEntry(Root, Entry, (Parent, FileName) => EnsureWithin(Parent, FileName));
        }

        internal static string Rename(string Root, InstanceResourceEntry Entry, string NewName)
        {
            return Transactions.Rename(Root, Entry, NewName);
        }

        internal static string ImportFile(string Root, string Folder, InstanceResourceKind Kind, string Source, CancellationToken Token)
        {
            return Transactions.ImportFile(Root, Folder, (int)Kind, Source, Token);
        }

        internal static string ImportWorld(string Root, string Source, string Name, CancellationToken Token)
        {
            return Transactions.ImportWorld(Root, Source, Name, Token);
        }

        internal static string ImportFolder(string Root, string Folder, InstanceResourceKind Kind, string Source, string Name, CancellationToken Token)
        {
            return Transactions.ImportFolder(Root, Folder, (int)Kind, Source, Name, Token);
        }

        internal static void Export(string Root, InstanceResourceEntry Entry, string Destination, CancellationToken Token)
        {
            Transactions.Export(Root, Entry, Destination, Token);
        }

        internal static void ExportMany(string Root, IEnumerable<InstanceResourceEntry> Items, string Folder, CancellationToken Token)
        {
            Transactions.ExportMany(Root, Items, Folder, Token);
        }

        internal static void RecycleMany(string Root, IEnumerable<InstanceResourceEntry> Items, string RecycleRoot, CancellationToken Token)
        {
            Transactions.RecycleMany(Root, Items, RecycleRoot, Token);
        }

        internal static string MoveToRecycle(string Root, InstanceResourceEntry Entry, string RecycleRoot)
        {
            return Transactions.MoveToRecycle(Root, Entry, RecycleRoot);
        }

        internal static string RestoreLast(string Root, string RecycleRoot)
        {
            return Transactions.RestoreLast(Root, RecycleRoot);
        }
    }
}
