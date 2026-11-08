// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.VisualBasic.CompilerServices;
namespace PCLCS;
public sealed partial class ModDependencyOnlinePipeline<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile,TOptions,TCache> {

        private static List<FileData> ReadFiles(string ModsFolder, Session Work)
        {
            var Result = new List<FileData>();
            var Paths = new List<string>();
            string Root = Path.GetFullPath(ModsFolder);
            if (!Directory.Exists(Root) || (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
                return Result;
            Paths.AddRange(Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly).Where(IsModFile));
            string Legacy = Work.Report.Context?.LegacyForgeVersionFolder;
            if (!string.IsNullOrEmpty(Legacy) && Regex.IsMatch(Legacy, "^[0-9.]+$"))
            {
                string Folder = Path.Combine(Root, Legacy);
                if (Directory.Exists(Folder) && (File.GetAttributes(Folder) & FileAttributes.ReparsePoint) == 0)
                    Paths.AddRange(Directory.EnumerateFiles(Folder, "*", SearchOption.TopDirectoryOnly).Where(IsModFile));
            }
            var Targets = Paths.ToDictionary(Value => Value, Value => Work.TargetFile is not null ? ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.SameFilePath(Value, Work.TargetFile) : Work.Options.RefreshAllFiles || IsEnabledFile(Value) && (Work.Options.ComparePlatformDependencies || NeedsSupplement(Value, Work.Report)), StringComparer.OrdinalIgnoreCase);
            if (!Targets.Values.Any(Value => Value))
                return Result;
            var Total = default(long);
            var Visited = default(int);
            foreach (var FullPath in Paths.OrderByDescending(Value => Targets[Value]).ThenBy(Value => Value, StringComparer.OrdinalIgnoreCase).Take(Work.FileLimit))
            {
                Work.Check();
                try
                {
                    var Info = new FileInfo(FullPath);
                    if ((Info.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (Info.Length > Math.Max(1L, Work.Options.MaxFileBytes) || Info.Length > Math.Max(1L, Work.Options.MaxTotalBytes) - Total)
                    {
                        Work.Report.OnlineIncomplete = true;
                        Work.Note("部分文件超过联网哈希读取上限，未据此判定缺失依赖。");
                        continue;
                    }
                    Total += Info.Length;
                    Result.Add(HashFile(Info, Targets[FullPath], Work));
                }
                catch (OperationCanceledException )
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Work.Report.OnlineIncomplete = true;
                    Work.Note("部分本地文件无法读取，未据此判定缺失依赖。");
                }
                finally
                {
                    Visited += 1;
                    Work.Options.Progress?.Invoke(Visited, Paths.Count);
                }
            }
            if (Paths.Count > Work.FileLimit)
            {
                Work.Report.OnlineIncomplete = true;
                Work.Note("联网补充达到文件数量上限；本次平台映射可能不完整。");
            }
            return Result;
        }
        private static bool IsModFile(string Value)
        {
            if (Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                Value = Path.GetFileNameWithoutExtension(Value);
            return new[] { ".jar", ".zip", ".litemod" }.Contains(Path.GetExtension(Value).ToLowerInvariant());
        }
        private static bool IsEnabledFile(string Value)
        {
            return !Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
        }
        private static string ExplicitTarget(string Value, string ModsFolder, TReport Report, bool ComparePlatformDependencies)
        {
            if (string.IsNullOrWhiteSpace(Value))
                return null;
            try
            {
                string FullPath = Path.GetFullPath(Value);
                string Root = Path.GetFullPath(ModsFolder);
                string Parent = Path.GetDirectoryName(FullPath);
                if (!File.Exists(FullPath) || !Directory.Exists(Root) || !IsModFile(FullPath))
                    return null;
                bool Allowed = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.SameFilePath(Parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string Legacy = Report.Context?.LegacyForgeVersionFolder;
                if (!Allowed && !string.IsNullOrEmpty(Legacy) && Regex.IsMatch(Legacy, "^[0-9.]+$"))
                {
                    Allowed = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.SameFilePath(Parent, Path.Combine(Root, Legacy));
                }
                if (!Allowed || (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(Parent) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(FullPath) & FileAttributes.ReparsePoint) != 0)
                    return null;
                return ComparePlatformDependencies || NeedsSupplement(FullPath, Report) ? FullPath : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }
        private static bool NeedsSupplement(string FullPath, TReport Report)
        {
            var Mods = Report.Mods.Where(Item => ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.SameFilePath(Item.RootPath, FullPath)).ToList();
            if (Mods.Count == 0 || Mods.Any(Item => Item.Format == "LiteLoader" || Item.Format == "Forge (旧版)"))
                return true;
            string Name = Path.GetFileName(FullPath);
            return Report.Notices.Any(Value => Value.StartsWith(Name + "：", StringComparison.OrdinalIgnoreCase) || Value.StartsWith(Name + "!", StringComparison.OrdinalIgnoreCase));
        }
        private static FileData HashFile(FileInfo Info, bool Target, Session Work)
        {
            long Length = Info.Length;
            var Written = Info.LastWriteTimeUtc;
            var Result = new FileData()
            {
                Length = Length,
                Info = new TOnlineFile()
                {
                    FullPath = Info.FullName,
                    IsSupplementTarget = Target,
                    Enabled = !Info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !Info.Name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
                }
            };
            using (var Input = new FileStream(Info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var Sha = SHA1.Create())
            using (var Md5 = MD5.Create())
            {
                var Buffer = new byte[65536];
                int Count;
                do
                {
                    Work.Check();
                    Count = Input.Read(Buffer, 0, Buffer.Length);
                    if (Count == 0)
                        break;
                    Sha.TransformBlock(Buffer, 0, Count, Buffer, 0);
                    Md5.TransformBlock(Buffer, 0, Count, Buffer, 0);
                    for (int Index = 0, loopTo = Count - 1; Index <= loopTo; Index++)
                    {
                        if (!IsWhitespace(Buffer[Index]))
                            Result.FilteredLength += 1L;
                    }
                }
                while (true);
                Sha.TransformFinalBlock(new byte[] { }, 0, 0);
                Md5.TransformFinalBlock(new byte[] { }, 0, 0);
                Result.Info.Sha1 = Hex(Sha.Hash);
                Result.Md5 = Hex(Md5.Hash);
            }
            Info.Refresh();
            if (Info.Length != Length || Info.LastWriteTimeUtc != Written)
                throw new IOException("File changed during hashing.");
            return Result;
        }
        private static string Hex(byte[] Bytes)
        {
            return BitConverter.ToString(Bytes).Replace("-", "").ToLowerInvariant();
        }
        private static bool IsWhitespace(byte Value)
        {
            return Value == 9 || Value == 10 || Value == 13 || Value == 32;
        }
        // Same seed/filter/MurmurHash2 as LocalResourceFile.CurseForgeHash, streamed without writing its INI cache.
        private static uint Fingerprint(FileData File, Session Work)
        {
            if (File.HasFingerprint)
                return File.Fingerprint;
            if (Work.Options.Fingerprint is not null)
            {
                File.Fingerprint = Work.Options.Fingerprint(File.Info.FullPath);
            }
            else
            {
                uint Hash = 1U ^ (uint)File.FilteredLength;
                var Word = default(uint);
                var Count = default(int);
                using (var Input = System.IO.File.OpenRead(File.Info.FullPath))
                {
                    var Buffer = new byte[65536];
                    int Read;
                    do
                    {
                        Work.Check();
                        Read = Input.Read(Buffer, 0, Buffer.Length);
                        if (Read == 0)
                            break;
                        for (int Index = 0, loopTo = Read - 1; Index <= loopTo; Index++)
                        {
                            if (IsWhitespace(Buffer[Index]))
                                continue;
                            Word = Word | (uint)Buffer[Index] << 8 * Count;
                            Count += 1;
                            if (Count != 4)
                                continue;
                            Word = (uint)(Word * 0x5BD1E995L & 0xFFFFFFFFL);
                            Word = Word ^ Word >> 24;
                            Word = (uint)(Word * 0x5BD1E995L & 0xFFFFFFFFL);
                            Hash = (uint)(Hash * 0x5BD1E995L & 0xFFFFFFFFL) ^ Word;
                            Word = 0U;
                            Count = 0;
                        }
                    }
                    while (true);
                }
                if (Count > 0)
                    Hash = (uint)((Hash ^ Word) * 0x5BD1E995L & 0xFFFFFFFFL);
                Hash = Hash ^ Hash >> 13;
                Hash = (uint)(Hash * 0x5BD1E995L & 0xFFFFFFFFL);
                File.Fingerprint = Hash ^ Hash >> 15;
            }
            File.HasFingerprint = true;
            return File.Fingerprint;
        }

}
