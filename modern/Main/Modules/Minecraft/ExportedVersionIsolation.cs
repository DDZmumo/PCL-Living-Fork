// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Threading;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;

namespace PCL
{

    // 仅本项目导出的隔离标记生效；其他整合包保留原有的默认隔离行为。
    internal sealed class ExportedVersionIsolation
    {
        internal const string MarkerKey = "LivingExportVersionIsolation";
        internal sealed class Choice : InstanceCloneExportChoiceData
        {
            internal Choice(bool Enabled, IEnumerable<string> ExtraVersionFolders = null) : base(Enabled, ExtraVersionFolders)
            {
            }
        }
        private static readonly InstanceCloneExportIsolationEngine<Choice> Service = new InstanceCloneExportIsolationEngine<Choice>(new InstanceCloneExportIsolationHost()
        {
            WriteIni = new Action<string, string, object>((FileName, Key, Value) => ModBase.WriteIni(FileName, Key, Conversions.ToString(Value))),
            ClearIniCache = new Action<string>(FileName => ModBase.IniClearCache(FileName)),
            AddSlashSuffix = new Func<string, string>(Value => PathUtils.AddSlashSuffix(Value)),
            RemoveSettingCache = new Action<string, string>((Key, Folder) => Settings.Entries[Key].ValueCache.Remove(Folder)),
            CopyFile = new Action<string, string>((Source, Target) => FileUtils.Copy(Source, Target))
        }, (Enabled, Folders) => new Choice(Enabled, Folders));
        internal static void ConfigureExport(string OverridesFolder, bool Enabled, IEnumerable<string> VersionFolders = null)
        {
            Service.ConfigureExport(OverridesFolder, Enabled, VersionFolders);
        }
        internal static Choice ReadArchiveChoice(ZipArchive Archive, string ArchiveBaseFolder)
        {
            return Service.ReadArchiveChoice(Archive, ArchiveBaseFolder);
        }
        internal static void ConfigureTarget(string VersionFolder, Choice Policy)
        {
            Service.ConfigureTarget(VersionFolder, Policy);
        }
        internal static void SaveModrinthIsolation(string versionFolder, bool enabled, CancellationToken token)
        {
            string setup = Path.Combine(versionFolder, "PCL", "Setup.ini");
            ModBase.UpdateIniValues(setup, new Dictionary<string, string> {
                ["VersionArgumentIndie"] = enabled ? "1" : "2",
                ["VersionArgumentIndieV2"] = enabled.ToString(),
                ["IsStar"] = "False"
            }, token);
            string folder = PathUtils.AddSlashSuffix(versionFolder);
            foreach (string key in new[] { "VersionArgumentIndie", "VersionArgumentIndieV2" })
                Settings.Entries[key].ValueCache.Remove(folder);
        }
        internal static string GameFolder(string VersionFolder, string MinecraftFolder, Choice Policy)
        {
            return Service.GameFolder(VersionFolder, MinecraftFolder, Policy);
        }
        private static string NormalizeRelative(string Value)
        {
            return InstanceCloneExportIsolationRules.NormalizeRelative(Value);
        }
        internal static string OverrideTarget(string Relative, string VersionFolder, string GameFolder, Choice Policy)
        {
            return Service.OverrideTarget(Relative, VersionFolder, GameFolder, Policy);
        }
        internal static void CopyOverrides(string OverridesFolder, string VersionFolder, string GameFolder, Choice Policy)
        {
            Service.CopyOverrides(OverridesFolder, VersionFolder, GameFolder, Policy);
        }
    }
}
