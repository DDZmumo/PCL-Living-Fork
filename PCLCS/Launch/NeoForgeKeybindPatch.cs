// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeloongCore;
using PCLLF.MMCC.Core;

namespace PCLCS;

/// <summary>Owns the launcher-supplied client keybind patch and its version and file boundaries.</summary>
public static class NeoForgeKeybindPatch {
    public const string FileName = "NeoForgeKeybindPatch.jar";
    public const string Sha256 = "d9c306b89a3044ea7c149c15bdb23a334687f3c4415975a4ee2ef0e781eee063";
    private const string Resource = @"Launch\NeoForgeKeybindPatch.jar";
    private const int MaxJarBytes = 256 * 1024;
    private static readonly Lazy<byte[]> Payload = new(ReadPayload);
    private static readonly object StageGate = new();

    public static bool Applies(string? minecraft, string? neoForge, int javaMajor) =>
        javaMajor >= 21 && minecraft == "1.21.1" && neoForge is { Length: > 5 and <= 40 } &&
        Regex.IsMatch(neoForge, @"\A21\.1\.\d+(?:-[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant);

    public static bool Applies(MmcCompiledProfile profile, int javaMajor) {
        if (profile.MainClass != "cpw.mods.bootstraplauncher.BootstrapLauncher") return false;
        for (int index = 0; index + 1 < profile.GameArguments.Length; index++) {
            if (profile.GameArguments[index].Value == "--fml.neoForgeVersion")
                return Applies(profile.MinecraftVersion, profile.GameArguments[index + 1].Value, javaMajor);
        }
        return false;
    }

    public static ReadOnlyMemory<byte> SupportBytes() => Payload.Value.AsMemory();

    public static bool CanUsePath(string path) => Path.IsPathFullyQualified(path) &&
        (!OperatingSystem.IsWindows() || Encoding.UTF8.GetByteCount(path) + 1 < 260) &&
        path.IndexOfAny(['=', '"', '\r', '\n', '\0']) < 0;

    public static string StageLegacy(string directory) {
        string cache = Path.Combine(Path.GetFullPath(directory), "pcllf-keybind-" + Sha256[..12]);
        string path = Path.Combine(cache, FileName);
        if (!CanUsePath(path)) throw new IOException("NeoForge 键位修补的路径不适用于 Java 启动。");
        lock (StageGate) {
            MmcInstancePaths.EnsureNoLinks(path);
            Directory.CreateDirectory(cache);
            byte[] expected = Payload.Value;
            if (!File.Exists(path) || !Matches(path, expected.Length)) FileUtils.WriteAtomic(path, expected);
            MmcInstancePaths.EnsureNoLinks(path);
            if (!Matches(path, expected.Length)) throw new IOException("NeoForge 键位修补缓存摘要不符。");
        }
        return path;
    }

    private static bool Matches(string path, int length) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return file.Length == length && Convert.ToHexStringLower(SHA256.HashData(file)) == Sha256;
    }

    private static byte[] ReadPayload() {
        using var resource = typeof(NeoForgeKeybindPatch).Assembly.GetManifestResourceStream(Resource)
            ?? throw new IOException("NeoForge 键位修补资源缺失。");
        if (resource.Length is <= 0 or > MaxJarBytes) throw new IOException("NeoForge 键位修补资源超过读取限制。");
        byte[] bytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != Sha256) throw new IOException("NeoForge 键位修补资源摘要不符。");
        return bytes;
    }
}
