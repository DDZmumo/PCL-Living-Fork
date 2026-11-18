// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeloongCore;
using PCLLF.MMCC.Core;

namespace PCLCS;

/// <summary>Supplies startup keybind preservation for affected Forge and NeoForge clients.</summary>
public static class ModKeybindPatch {
    public const string FileName = "ModKeybindPatch.jar";
    public const string Sha256 = "01f9a18fdbe132d2e48bf1ff71407f7a1f0309bc72d8785f2994d42cdd6e5a67";
    private const string Resource = @"Launch\ModKeybindPatch.jar";
    private const int MaxJarBytes = 256 * 1024;
    private static readonly Version FirstFixedForge55 = new(55, 0, 20);
    private static readonly Regex VersionFormat = new(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z",
        RegexOptions.CultureInvariant);
    private static readonly Lazy<byte[]> Payload = new(ReadPayload);
    private static readonly object StageGate = new();

    public static bool Applies(ModLoaders loader, string? minecraft, string? loaderVersion, int javaMajor) {
        if (javaMajor < 21 || loaderVersion is not { Length: >= 5 and <= 40 } || !VersionFormat.IsMatch(loaderVersion) ||
            !Version.TryParse(loaderVersion.Split('-', 2)[0], out var version)) return false;
        var (neoForgeMinor, forgeMajor) = minecraft switch {
            "1.21" or "1.21.0" => (0, 51),
            "1.21.1" => (1, 52),
            "1.21.2" => (2, -1),
            "1.21.3" => (3, 53),
            "1.21.4" => (4, 54),
            "1.21.5" => (-1, 55),
            _ => (-1, -1)
        };
        return loader switch {
            ModLoaders.NeoForge => version.Major == 21 && version.Minor == neoForgeMinor,
            ModLoaders.Forge => version.Major == forgeMajor && (forgeMajor != 55 || version < FirstFixedForge55),
            _ => false
        };
    }

    public static bool Applies(MmcCompiledProfile profile, int javaMajor) {
        if (profile.MainClass == "cpw.mods.bootstraplauncher.BootstrapLauncher") {
            for (int index = 0; index + 1 < profile.GameArguments.Length; index++) {
                var loader = profile.GameArguments[index].Value switch {
                    "--fml.neoForgeVersion" => ModLoaders.NeoForge,
                    "--fml.forgeVersion" => ModLoaders.Forge,
                    _ => (ModLoaders?)null
                };
                if (loader is { } match)
                    return Applies(match, profile.MinecraftVersion, profile.GameArguments[index + 1].Value, javaMajor);
            }
        }
        if (profile.MainClass != "net.minecraftforge.bootstrap.ForgeBootstrap") return false;
        string prefix = profile.MinecraftVersion + "-";
        return profile.Classpath.Any(artifact =>
            artifact.Coordinate is { Group: "net.minecraftforge", Artifact: "forge", Version: var version } &&
            version.StartsWith(prefix, StringComparison.Ordinal) &&
            Applies(ModLoaders.Forge, profile.MinecraftVersion, version[prefix.Length..], javaMajor));
    }

    public static ReadOnlyMemory<byte> SupportBytes() => Payload.Value.AsMemory();

    internal static string Argument(string jar, string gameFolder) {
        string optionsPath = MmcRuntimeFiles.JavaPath(Path.Combine(gameFolder, "options.txt"));
        // Windows' native agent options can lose characters outside the current encoding.
        return "-javaagent:" + MmcRuntimeFiles.JavaPath(jar) + "=" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(optionsPath));
    }

    public static bool CanUsePath(string path) => Path.IsPathFullyQualified(path) &&
        (!OperatingSystem.IsWindows() || Encoding.UTF8.GetByteCount(path) + 1 < 260) &&
        path.IndexOfAny(['=', '"', '\r', '\n', '\0']) < 0;

    public static string StageLegacy(string directory) {
        string cache = Path.Combine(Path.GetFullPath(directory), "pcllf-keybind-" + Sha256[..12]);
        string path = Path.Combine(cache, FileName);
        if (!CanUsePath(path)) throw new IOException("模组键位修补的路径不适用于 Java 启动。");
        lock (StageGate) {
            MmcInstancePaths.EnsureNoLinks(path);
            Directory.CreateDirectory(cache);
            byte[] expected = Payload.Value;
            if (!File.Exists(path) || !Matches(path, expected.Length)) FileUtils.WriteAtomic(path, expected);
            MmcInstancePaths.EnsureNoLinks(path);
            if (!Matches(path, expected.Length)) throw new IOException("模组键位修补缓存摘要不符。");
        }
        return path;
    }

    private static bool Matches(string path, int length) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return file.Length == length && Convert.ToHexStringLower(SHA256.HashData(file)) == Sha256;
    }

    private static byte[] ReadPayload() {
        using var resource = typeof(ModKeybindPatch).Assembly.GetManifestResourceStream(Resource)
            ?? throw new IOException("模组键位修补资源缺失。");
        if (resource.Length is <= 0 or > MaxJarBytes) throw new IOException("模组键位修补资源超过读取限制。");
        byte[] bytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != Sha256) throw new IOException("模组键位修补资源摘要不符。");
        return bytes;
    }
}
