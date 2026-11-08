// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Behavior source: LF Modules/Base tooling helpers; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace PCLCS
{
    // 自定义下载沿用 PCL 下载引擎；先写独立暂存文件，成功后才替换用户目标。
    public class LegacyToolboxDownloadSession
    {
        private static readonly ConcurrentDictionary<string, byte> Targets = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly object Gate = new object ();
        private bool Released;
        private bool Canceled;
        private bool Applied;
        private bool ReplacedExisting;
        private bool FinishedSuccessfully;
        public readonly string TargetPath;
        public readonly string StageFolder;
        public readonly string StagePath;
        private readonly string BackupPath;
        protected LegacyToolboxDownloadSession(string Target)
        {
            TargetPath = Target;
            StageFolder = Path.Combine(Path.GetDirectoryName(Target)!, ".livingpcl-download-" + Guid.NewGuid().ToString("N"));
            StagePath = Path.Combine(StageFolder, "payload.tmp");
            BackupPath = Path.Combine(StageFolder, "previous.tmp");
        }

        public static Uri ParseUrl(string? Value)
        {
            Uri? Result = null;
            if (string.IsNullOrWhiteSpace(Value) || !Uri.TryCreate(Value!.Trim(), UriKind.Absolute, out Result) || (Result!.Scheme ?? "") != (Uri.UriSchemeHttp ?? "") && (Result!.Scheme ?? "") != (Uri.UriSchemeHttps ?? "") || string.IsNullOrEmpty(Result!.Host) || !string.IsNullOrEmpty(Result!.UserInfo))
            {
                throw new ArgumentException("请输入完整的 HTTP 或 HTTPS 下载地址。");
            }

            return Result!;
        }

        public static string ValidateName(string? Value)
        {
            if (string.IsNullOrWhiteSpace(Value))
                return "请输入文件名。";
            if ((Value ?? "") != (Value!.Trim() ?? "") || Value!.EndsWith(".", StringComparison.Ordinal) || Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Value.Contains(@"\") || Value.Contains("/") || Value.Contains(":") || Value == "." || Value == ".." || Value.Length > 255)
                return "文件名不能包含路径或 Windows 不允许的字符。";
            string Stem = Value.Split('.')[0];
            if (Regex.IsMatch(Stem, @"\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return "不能使用 Windows 保留的设备名称。";
            return "";
        }

        public static string SuggestName(string? Value)
        {
            try
            {
                var Address = ParseUrl(Value);
                string Name = Uri.UnescapeDataString(Address.AbsolutePath.Substring(checked(Address.AbsolutePath.LastIndexOf('/') + 1)));
                return string.IsNullOrEmpty(ValidateName(Name)) ? Name : "";
            }
            catch
            {
                return "";
            }
        }

        public static string ResolveTarget(string Folder, string Name)
        {
            string ErrorText = ValidateName(Name);
            if (!string.IsNullOrEmpty(ErrorText))
                throw new ArgumentException(ErrorText);
            if (string.IsNullOrWhiteSpace(Folder) || !Regex.IsMatch(Folder.Replace("/", @"\"), @"\A[A-Za-z]:\\"))
                throw new ArgumentException("保存位置必须是以盘符开头的完整文件夹路径。");
            string Parent = Path.GetFullPath(Folder.Replace("/", @"\")).TrimEnd('\\') + @"\";
            string Result = Path.GetFullPath(Path.Combine(Parent, Name));
            if (!string.Equals(Path.GetDirectoryName(Result)!.TrimEnd('\\') + @"\", Parent, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("文件名不能离开选择的保存文件夹。");
            if (Directory.Exists(Result))
                throw new IOException("保存位置已存在同名文件夹。");
            if (File.Exists(Result) && (File.GetAttributes(Result) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能覆盖链接文件。");
            return Result!;
        }

        public static TSession Create<TSession>(string Folder, string Name, Func<string, TSession> Factory)
            where TSession : LegacyToolboxDownloadSession
        {
            string Target = ResolveTarget(Folder, Name);
            if (!Targets.TryAdd(Target, 0))
                throw new IOException("这个文件已有下载任务，请等待完成或取消原任务。");
            TSession Session = Factory(Target);
            try
            {
                Directory.CreateDirectory(Session.StageFolder);
                return Session;
            }
            catch
            {
                byte Ignored;
                Targets.TryRemove(Target, out Ignored);
                throw;
            }
        }

        private void ValidateStage()
        {
            string? ExpectedParent = Path.GetDirectoryName(TargetPath);
            if (!string.Equals(Path.GetDirectoryName(StageFolder), ExpectedParent, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(StageFolder), @"\A\.livingpcl-download-[0-9a-f]{32}\z") || Directory.Exists(StageFolder) && (File.GetAttributes(StageFolder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("暂存目录校验失败。");
            foreach (var Item in new string[]
            {
                StagePath,
                BackupPath
            }

            )
            {
                if (File.Exists(Item) && (File.GetAttributes(Item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("暂存文件不能是链接。");
            }
        }

        public void Commit(bool AllowOverwrite, Func<bool> ShouldCancel)
        {
            lock (Gate)
            {
                ValidateStage();
                if (Released || Canceled || ShouldCancel())
                    throw new OperationCanceledException();
                if (Applied)
                    throw new InvalidOperationException("文件已保存，不能重复提交。");
                if (!File.Exists(StagePath))
                    throw new IOException("未找到已下载的文件。");
                bool Existed = File.Exists(TargetPath);
                if (Existed && !AllowOverwrite)
                    throw new IOException("目标文件已经存在，请重新选择文件名。");
                if (Existed && (File.GetAttributes(TargetPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("不能覆盖链接文件。");
                try
                {
                    if (Existed)
                    {
                        File.Replace(StagePath, TargetPath, BackupPath);
                    }
                    else
                    {
                        File.Move(StagePath, TargetPath);
                    }

                    ReplacedExisting = Existed;
                    Applied = true;
                    if (ShouldCancel())
                        throw new OperationCanceledException();
                }
                catch
                {
                    Rollback();
                    throw;
                }
            }
        }

        private void Rollback()
        {
            if (!Applied)
                return;
            ValidateStage();
            if (File.Exists(TargetPath) && (File.GetAttributes(TargetPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不能通过链接恢复原文件。");
            if (ReplacedExisting)
            {
                if (!File.Exists(BackupPath))
                    throw new IOException("未找到原文件备份。");
                if (File.Exists(TargetPath))
                {
                    File.Replace(BackupPath, TargetPath, null);
                }
                else
                {
                    File.Move(BackupPath, TargetPath);
                }
            }
            else if (File.Exists(TargetPath))
            {
                File.Move(TargetPath, StagePath);
            }

            Applied = false;
        }

        public string? RecoveryPath
        {
            get
            {
                lock (Gate)
                    return !FinishedSuccessfully && File.Exists(BackupPath) ? BackupPath : null;
            }
        }

        public string? UnrevertedPath
        {
            get
            {
                lock (Gate)
                    return !FinishedSuccessfully && Applied && File.Exists(TargetPath) ? TargetPath : null;
            }
        }

        private void Cleanup()
        {
            ValidateStage();
            if (File.Exists(BackupPath) && !FinishedSuccessfully)
                throw new IOException("原文件备份已保留，不能删除。");
            foreach (var FilePath in new string[]
            {
                StagePath,
                BackupPath
            }

            )
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }

            if (Directory.Exists(StageFolder))
                Directory.Delete(StageFolder, false);
        }

        // 下载引擎的取消/合并完成回调可重复调用，仅清理本任务的精确暂存文件。
        public void OnCanceledMergeFinished()
        {
            lock (Gate)
            {
                if (!Released)
                    return;
                try
                {
                    Cleanup();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public void Complete(bool Success)
        {
            lock (Gate)
            {
                if (Released)
                    return;
                Canceled = !Success;
                try
                {
                    if (Success)
                    {
                        FinishedSuccessfully = true;
                    }
                    else
                    {
                        Rollback();
                    }

                    Cleanup();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                finally
                {
                    Released = true;
                    byte Ignored;
                    Targets.TryRemove(TargetPath, out Ignored);
                }
            }
        }
    }
}
