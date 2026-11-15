// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
#if !NETFRAMEWORK
using System.Runtime.Versioning;
#endif

namespace PCLCS;

#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
public sealed class BackupStorageRegistryStore : IBackupStoragePreference {
    public const string SubKey = @"Software\PCL Living Fork";
    public const string ValueName = "BackupStorageRoot";
    private readonly string subKey, mutexName;
    public BackupStorageRegistryStore() : this(SubKey) { }
    public BackupStorageRegistryStore(string subKey) {
        if (string.IsNullOrWhiteSpace(subKey) || !subKey.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Registry path must be relative to HKCU Software.", nameof(subKey));
        this.subKey = subKey;
        using SHA256 hash = SHA256.Create();
        mutexName = @"Local\PCLLF.BackupStoragePreference." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(subKey.ToUpperInvariant()))).Replace("-", "");
    }
    private static string? ReadValue(RegistryKey? key) {
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (key!.GetValueKind(ValueName) != RegistryValueKind.String || value is not string text || string.IsNullOrWhiteSpace(text) || text.Length > 32767)
            throw new IOException("PCLLF 备份存储位置注册表值无效。");
        return text;
    }
    public string? Read() {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, false);
        return ReadValue(key);
    }
    public void Write(string root, string? expected) {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root) || root.Length > 32767) throw new IOException("备份存储目录无效。");
        using Mutex mutex = new(false, mutexName); bool acquired = false;
        try {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("其他 PCLLF 正在保存备份位置，请稍后再试。");
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey, true) ?? throw new IOException("无法打开 PCLLF 注册表项。");
            string? old = ReadValue(key);
            if (!string.Equals(old, expected, StringComparison.Ordinal)) throw new IOException("备份位置已被其他操作修改，本次迁移已停止。");
            try {
                key.SetValue(ValueName, root, RegistryValueKind.String); key.Flush();
                if (!string.Equals(ReadValue(key), root, StringComparison.Ordinal)) throw new IOException("备份位置注册表保存校验失败。");
            } catch (Exception error) {
                try {
                    if (old is null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, old, RegistryValueKind.String);
                    key.Flush();
                    if (!string.Equals(ReadValue(key), old, StringComparison.Ordinal)) throw new IOException("Previous location could not be restored.");
                } catch { throw new BackupStoragePreferenceUncertainException(error); }
                throw;
            }
        } finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
