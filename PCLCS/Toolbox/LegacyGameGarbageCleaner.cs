// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Behavior source: LF Modules/Base tooling helpers; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace PCLCS
{
    // 只处理登记游戏目录中的明确垃圾项；不使用会跟随目录链接的递归删除辅助函数。
    public static class LegacyGameGarbageCleaner
    {
        public static TResult Clean<TResult>(IEnumerable<string> GameFolders, Func<TResult> CreateResult, Func<bool>? ShouldStop = null)
            where TResult : LegacyGameGarbageCleanupResult
        {
            if (GameFolders is null)
                throw new ArgumentNullException(nameof(GameFolders));
            TResult Result = CreateResult();
            var Context = new CleanupContext(Result, ShouldStop);
            var Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var Attributes = default(FileAttributes);
            foreach (string InputFolder in GameFolders)
            {
                Context.CheckCancellation();
                string? Root = null;
                try
                {
                    if (string.IsNullOrWhiteSpace(InputFolder) || !IsAbsolutePath(InputFolder))
                        throw new ArgumentException("游戏目录必须是绝对路径。");
                    Root = FullPath(InputFolder);
                    if (string.Equals(Root, FullPath(Path.GetPathRoot(Root)!), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("不能清理磁盘或共享根目录。");
                }
                catch (Exception ex)when (IsPathFailure(ex))
                {
                    Context.Skip(InputFolder ?? "");
                    continue;
                }

                if (!Seen.Add(Root))
                    continue;
                if (Context.TryValidate(Root, Root, ref Attributes) && (Attributes & FileAttributes.Directory) != 0)
                {
                    Context.Roots.Add(Root);
                }
                else
                {
                    Context.Skip(Root);
                }
            }

            foreach (var Root in Context.Roots)
                CleanRoot(Root, Context);
            return Result;
        }

        private static void CleanRoot(string Root, CleanupContext Context)
        {
            string[]? Entries = Context.ReadEntries(Root, Root);
            if (Entries is null)
                return;
            CleanGameDirectory(Root, Root, Entries, Context);
            var Attributes = default(FileAttributes);
            foreach (var Entry in Entries)
            {
                if (!string.Equals(Path.GetFileName(Entry), "versions", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Context.TryValidate(Root, Entry, ref Attributes))
                    continue;
                if ((Attributes & FileAttributes.Directory) == 0)
                    continue;
                string[]? Versions = Context.ReadEntries(Root, Entry);
                if (Versions is null)
                    continue;
                foreach (var Version in Versions)
                {
                    Context.CheckCancellation();
                    if (!Context.TryValidate(Root, Version, ref Attributes))
                        continue;
                    if ((Attributes & FileAttributes.Directory) == 0)
                        continue;
                    string[]? VersionEntries = Context.ReadEntries(Root, Version);
                    if (VersionEntries is not null)
                        CleanGameDirectory(Root, Version, VersionEntries, Context);
                }
            }
        }

        private static void CleanGameDirectory(string Root, string Folder, string[] Entries, CleanupContext Context)
        {
            if (!Context.GameDirectories.Add(Folder))
                return;
            string NativesName = Path.GetFileName(Folder) + "-natives";
            var Attributes = default(FileAttributes);
            foreach (var Entry in Entries)
            {
                Context.CheckCancellation();
                string Name = Path.GetFileName(Entry);
                bool IsGarbageDirectory = string.Equals(Name, ".mixin.out", StringComparison.OrdinalIgnoreCase) || string.Equals(Name, "crash-reports", StringComparison.OrdinalIgnoreCase) || string.Equals(Name, "debug", StringComparison.OrdinalIgnoreCase) || string.Equals(Name, "logs", StringComparison.OrdinalIgnoreCase) || string.Equals(Name, NativesName, StringComparison.OrdinalIgnoreCase) || string.Equals(Name, "natives-windows-x86_64", StringComparison.OrdinalIgnoreCase);
                bool IsGarbageFile = string.Equals(Path.GetExtension(Name), ".log", StringComparison.OrdinalIgnoreCase) || string.Equals(Name, "WailaErrorOutput.txt", StringComparison.OrdinalIgnoreCase);
                // hs_err_pid 只允许日志；不因文件名前缀相同而删除 hprof、dmp 或其他用户文件。
                if (!IsGarbageDirectory && !IsGarbageFile)
                    continue;
                if (!Context.TryValidate(Root, Entry, ref Attributes))
                    continue;
                if ((Attributes & FileAttributes.Directory) != 0)
                {
                    if (IsGarbageDirectory)
                        DeleteTree(Root, Entry, Context);
                }
                else if (IsGarbageFile)
                {
                    DeleteFile(Root, Entry, Context);
                }
            }
        }

        private static void DeleteTree(string Root, string Folder, CleanupContext Context)
        {
            if (!IsDescendant(Root, Folder))
            {
                Context.Skip(Folder);
                return;
            }

            // 一个登记游戏目录即使位于另一个游戏的 logs 等目录中，也不能被整树删除。
            foreach (var Registered in Context.Roots)
            {
                if (string.Equals(Registered, Folder, StringComparison.OrdinalIgnoreCase) || IsDescendant(Folder, Registered))
                {
                    Context.Skip(Folder);
                    return;
                }
            }

            var Pending = new Stack<Tuple<string, bool>>();
            Pending.Push(Tuple.Create(Folder, false));
            var Attributes = default(FileAttributes);
            while (Pending.Count > 0)
            {
                Context.CheckCancellation();
                var Frame = Pending.Pop();
                if (!Context.TryValidate(Root, Frame.Item1, ref Attributes))
                    continue;
                if ((Attributes & FileAttributes.Directory) == 0)
                {
                    Context.Skip(Frame.Item1);
                    continue;
                }

                if (Frame.Item2)
                {
                    try
                    {
                        Context.CheckCancellation();
                        // 非递归删除；有链接、拒绝访问或后来新建的内容时保留目录。
                        if (!Context.TryValidate(Root, Frame.Item1, ref Attributes))
                            continue;
                        Directory.Delete(Frame.Item1, false);
                    }
                    catch (Exception ex)when (IsPathFailure(ex))
                    {
                        Context.Skip(Frame.Item1);
                    }
                }
                else
                {
                    if (!Context.GarbageDirectories.Add(Frame.Item1))
                        continue;
                    string[]? Entries = Context.ReadEntries(Root, Frame.Item1);
                    if (Entries is null)
                        continue;
                    Pending.Push(Tuple.Create(Frame.Item1, true));
                    foreach (var Entry in Entries)
                    {
                        Context.CheckCancellation();
                        if (!Context.TryValidate(Root, Entry, ref Attributes))
                            continue;
                        if ((Attributes & FileAttributes.Directory) != 0)
                        {
                            Pending.Push(Tuple.Create(Entry, false));
                        }
                        else
                        {
                            DeleteFile(Root, Entry, Context);
                        }
                    }
                }
            }
        }

        private static void DeleteFile(string Root, string FileName, CleanupContext Context)
        {
            if (!IsDescendant(Root, FileName))
            {
                Context.Skip(FileName);
                return;
            }

            if (!Context.Files.Add(FileName))
                return;
            try
            {
                var Attributes = default(FileAttributes);
                if (!Context.TryValidate(Root, FileName, ref Attributes))
                    return;
                if ((Attributes & FileAttributes.Directory) != 0)
                {
                    Context.Skip(FileName);
                    return;
                }

                long Length = new FileInfo(FileName).Length;
                Context.CheckCancellation();
                if (!Context.TryValidate(Root, FileName, ref Attributes))
                    return;
                File.Delete(FileName);
                Context.Result.DeletedFiles = checked(Context.Result.DeletedFiles + 1);
                Context.Result.DeletedBytes = checked(Context.Result.DeletedBytes + Length);
            }
            catch (Exception ex)when (IsPathFailure(ex))
            {
                Context.Skip(FileName);
            }
        }

        private static bool IsAbsolutePath(string Value)
        {
            Value = Value.Replace("/", @"\");
            if (Value.StartsWith(@"\\?\", StringComparison.Ordinal) || Value.StartsWith(@"\\.\", StringComparison.Ordinal))
                return false;
            return Value.Length >= 3 && char.IsLetter(Value[0]) && Value[1] == ':' && Value[2] == '\\' || Value.StartsWith(@"\\", StringComparison.Ordinal);
        }

        private static string FullPath(string Value)
        {
            string Full = Path.GetFullPath(Value.Replace("/", @"\"));
            string? VolumeRoot = Path.GetPathRoot(Full);
            if (string.Equals(Full.TrimEnd('\\'), VolumeRoot!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return VolumeRoot!;
            return Full.TrimEnd('\\');
        }

        private static bool IsDescendant(string Root, string Target)
        {
            return Target.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is SecurityException;
        }

        private sealed partial class CleanupContext
        {
            internal readonly LegacyGameGarbageCleanupResult Result;
            internal readonly List<string> Roots = new List<string>();
            internal readonly HashSet<string> GameDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> GarbageDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Func<bool>? ShouldStop;
            internal CleanupContext(LegacyGameGarbageCleanupResult Result, Func<bool>? StopRequested)
            {
                this.Result = Result;
                ShouldStop = StopRequested;
            }

            internal void CheckCancellation()
            {
                if (ShouldStop is not null && ShouldStop())
                    throw new OperationCanceledException("游戏垃圾清理已取消。");
            }

            internal void Skip(string Item)
            {
                if (Skipped.Add(Item))
                    Result.SkippedItems = checked(Result.SkippedItems + 1);
            }

            internal string[]? ReadEntries(string Root, string Folder)
            {
                CheckCancellation();
                try
                {
                    var Attributes = default(FileAttributes);
                    if (!TryValidate(Root, Folder, ref Attributes))
                        return null;
                    if ((Attributes & FileAttributes.Directory) == 0)
                    {
                        Skip(Folder);
                        return null;
                    }

                    // GetFileSystemEntries 在删除前取得快照，不在修改目录时延迟迭代。
                    return Directory.GetFileSystemEntries(Folder);
                }
                catch (Exception ex)when (IsPathFailure(ex))
                {
                    Skip(Folder);
                    return null;
                }
            }

            internal bool TryValidate(string Root, string Target, ref FileAttributes Attributes)
            {
                CheckCancellation();
                try
                {
                    Target = FullPath(Target);
                    if (!string.Equals(Root, Target, StringComparison.OrdinalIgnoreCase) && !IsDescendant(Root, Target))
                    {
                        Skip(Target);
                        return false;
                    }

                    string Current = Root;
                    Attributes = File.GetAttributes(Current);
                    if ((Attributes & FileAttributes.ReparsePoint) != 0 || (Attributes & FileAttributes.Directory) == 0)
                    {
                        Skip(Target);
                        return false;
                    }

                    if (string.Equals(Root, Target, StringComparison.OrdinalIgnoreCase))
                        return true;
                    string[] Segments = Target.Substring(Root.Length + 1).Split('\\');
                    for (int Index = 0, loopTo = Segments.Length - 1; Index <= loopTo; Index++)
                    {
                        Current = Path.Combine(Current, Segments[Index]);
                        Attributes = File.GetAttributes(Current);
                        if ((Attributes & FileAttributes.ReparsePoint) != 0 || Index < Segments.Length - 1 && (Attributes & FileAttributes.Directory) == 0)
                        {
                            Skip(Target);
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception ex)when (IsPathFailure(ex))
                {
                    Skip(Target);
                    return false;
                }
            }
        }
    }
}
