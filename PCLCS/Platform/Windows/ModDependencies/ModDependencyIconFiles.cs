// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCLCS;

// LF Controls/ModDependencyIcon.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through the C016 starting tree; MPL-2.0 for PCLLF-original code. PCL 5ef81e4 has no equivalent.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyLocalModItem.xaml.cs was reviewed
// for C#/WPF image construction only (CE Plain Craft Launcher 2/LICENCE; MPL-2.0 for PCLLF-original code); no CE cache policy is used.
public static class ModDependencyIconFiles {
    // Read-only archive access; eager decoding owns the pixels before both streams close.
    // The original has no shared icon cache: graph-level caching remains in its own controller.
    public static ImageSource? Read(string? fileName, string? entryName) {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(entryName) || entryName!.Length > 2048) return null;
        string name = entryName.Replace('\\', '/');
        if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":") || name.Split('/').Any(part => part == ".." || part == "" || part == ".")) return null;
        try {
            if ((File.GetAttributes(fileName!) & FileAttributes.ReparsePoint) != 0) return null;
            using FileStream input = new(fileName!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using ZipArchive archive = new(input, ZipArchiveMode.Read, false, InstanceResourcePackageRules.EntryNameEncoding());
            ZipArchiveEntry? entry = archive.GetEntry(name);
            if (entry is null || entry.Length == 0 || entry.Length > 4 * 1024 * 1024) return null;
            using MemoryStream data = new();
            using Stream icon = entry.Open();
            byte[] buffer = new byte[8192];
            int count;
            do {
                count = icon.Read(buffer, 0, buffer.Length);
                if (data.Length + count > 4 * 1024 * 1024) return null;
                data.Write(buffer, 0, count);
            } while (count != 0);
            data.Position = 0;
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 32;
            image.StreamSource = data;
            image.EndInit();
            image.Freeze();
            return image;
        } catch { return null; }
    }
}
