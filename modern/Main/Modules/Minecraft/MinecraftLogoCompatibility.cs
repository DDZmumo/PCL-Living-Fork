// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PCL
{

    /// <summary>
/// 版本图标共享配置使用官方 PCL 的资源地址，显示地址由 MyBitmap 按当前程序集解析。
/// 只迁移两边均包含的内置方块图片，不改动自定义文件、网络图片或其他设置。
/// </summary>
    internal sealed class MinecraftLogoCompatibility
    {
        private MinecraftLogoCompatibility()
        {
        }

        internal const string OfficialImageRoot = "pack://application:,,,/Plain Craft Launcher 2;component/Images/";
        private static readonly Dictionary<string, string> BuiltInFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Anvil.png", "Anvil.png" }, { "CobbleStone.png", "CobbleStone.png" }, { "CommandBlock.png", "CommandBlock.png" }, { "Egg.png", "Egg.png" }, { "Fabric.png", "Fabric.png" }, { "GoldBlock.png", "GoldBlock.png" }, { "Grass.png", "Grass.png" }, { "GrassPath.png", "GrassPath.png" }, { "NeoForge.png", "NeoForge.png" }, { "OptiFabric.png", "OptiFabric.png" }, { "RedstoneBlock.png", "RedstoneBlock.png" }, { "RedstoneLampOff.png", "RedstoneLampOff.png" }, { "RedstoneLampOn.png", "RedstoneLampOn.png" } };
        private static readonly Regex BuiltInAddress = new Regex(@"\A(?:pack://application:,,,/(?:(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL);component/)?|/(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL);component/)images/blocks/(?<File>[a-z0-9]+\.png)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string ToStorageUri(string Value)
        {
            if (string.IsNullOrEmpty(Value))
                return Value;
            var Match = BuiltInAddress.Match(Value);
            string FileName = null;
            if (!Match.Success || !BuiltInFiles.TryGetValue(Match.Groups["File"].Value, out FileName))
                return Value;
            return OfficialImageRoot + "Blocks/" + FileName;
        }

        /// <summary>
    /// 缓存命中时也要修复版本 Setup.ini；PCL.ini 的卡片缓存只保存版本名，不保存图标。
    /// </summary>
        internal static string ReadVersionLogo(string SetupFile, string DefaultValue = "")
        {
            string Original = ModBase.ReadIni(SetupFile, "Logo", null);
            if (Original is null)
                return DefaultValue;
            string SharedValue = ToStorageUri(Original);
            if (!string.Equals(Original, SharedValue, StringComparison.Ordinal))
                ModBase.WriteIni(SetupFile, "Logo", SharedValue);
            return SharedValue;
        }

        internal static void WriteVersionLogo(string SetupFile, string Value)
        {
            ModBase.WriteIni(SetupFile, "Logo", ToStorageUri(Value));
        }

        // 共享目录修复只打开已存在的版本配置，不加载版本、不创建游戏目录。
        internal static int RepairFolders(IEnumerable<string> Folders)
        {
            var Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var Repaired = default(int);
            foreach (var Folder in Folders)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(Folder) || !Path.IsPathRooted(Folder))
                        continue;
                    string Root = Path.GetFullPath(Folder).TrimEnd('\\', '/');
                    if (!Seen.Add(Root) || IsLink(Root))
                        continue;
                    string Versions = Path.Combine(Root, "versions");
                    if (!Directory.Exists(Versions) || IsLink(Versions))
                        continue;
                    foreach (var Version in Directory.EnumerateDirectories(Versions))
                    {
                        try
                        {
                            string ConfigFolder = Path.Combine(Version, "PCL");
                            if (IsLink(Version) || !Directory.Exists(ConfigFolder) || IsLink(ConfigFolder))
                                continue;
                            if (RepairFile(Path.Combine(ConfigFolder, "Setup.ini")))
                                Repaired += 1;
                        }
                        catch (Exception ex) when (IsUnavailable(ex))
                        {
                            // 某个版本不可用不影响其他版本。
                        }
                    }
                }
                catch (Exception ex) when (IsUnavailable(ex))
                {
                    // 已删除、离线或无权限的共享目录不弹出错误，也不修改目录列表。
                }
            }
            return Repaired;
        }

        private static bool IsLink(string FileName)
        {
            return (File.GetAttributes(FileName) & FileAttributes.ReparsePoint) != 0;
        }

        private static bool IsUnavailable(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
        }

        private static bool RepairFile(string SetupFile)
        {
            if (!File.Exists(SetupFile) || IsLink(SetupFile))
                return false;
            string Temporary = null;
            try
            {
                // 禁止并发写入原文件；原子替换时保留读取句柄，避免留下半份配置。
                using (var Source = new FileStream(SetupFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (Source.Length > 4L * 1024L * 1024L)
                        return false;
                    byte[] Original;
                    using (var Buffer = new MemoryStream())
                    {
                        Source.CopyTo(Buffer);
                        Original = Buffer.ToArray();
                    }
                    string Text;
                    Encoding Encoding;
                    using (var Buffer = new MemoryStream(Original))
                    using (var Reader = new StreamReader(Buffer, new UTF8Encoding(false, true), true))
                    {
                        Text = Reader.ReadToEnd();
                        Encoding = Reader.CurrentEncoding;
                    }
                    byte[] Preamble = Encoding.GetPreamble();
                    byte[] OriginalBody = Encoding.GetBytes(Text);
                    byte[] WithPreamble = Preamble.Concat(OriginalBody).ToArray();
                    byte[] Prefix = Original.SequenceEqual(WithPreamble) ? Preamble : (new byte[] { });
                    if (!Original.SequenceEqual(Prefix.Concat(OriginalBody)))
                        return false;
                    string Updated = Regex.Replace(Text, @"(?m)^(Logo:)([^\r\n]*)", Match => Match.Groups[1].Value + ToStorageUri(Match.Groups[2].Value));
                    if ((Updated ?? "") == (Text ?? ""))
                        return false;
                    Temporary = SetupFile + ".livingpcl-icons-" + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var Destination = new FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] Data = Prefix.Concat(Encoding.GetBytes(Updated)).ToArray();
                        Destination.Write(Data, 0, Data.Length);
                        Destination.Flush(true);
                    }
                    File.Replace(Temporary, SetupFile, null);
                    ModBase.IniClearCache(SetupFile);
                    return true;
                }
            }
            finally
            {
                if (Temporary is not null && File.Exists(Temporary))
                {
                    try
                    {
                        File.Delete(Temporary);
                    }
                    catch (Exception ex) when (IsUnavailable(ex))
                    {
                    }
                }
            }
        }
    }
}