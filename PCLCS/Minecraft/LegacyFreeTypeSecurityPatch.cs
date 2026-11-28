// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeloongCore;

namespace PCLCS;

internal sealed class LegacyFreeTypeSecurityPatch(string previousVersion, string version, params LegacyFreeTypeSecurityPatch.Artifact[] artifacts)
{
    private const string Group = "org.lwjgl:lwjgl-freetype:";
    private const string ArtifactRoot = "org/lwjgl/lwjgl-freetype/";

    // The JAR bytes were read from Mojang, checked against the size and SHA-1 in the
    // 26.2 and 26.3 version manifests, then pinned by SHA-256 for download and launch.
    private static readonly LegacyFreeTypeSecurityPatch Lwjgl343 = new("3.4.3", "3.4.3-mojang-2.15.0",
        new("", 469281, "134bc57b83e78a7f954a0f078093e319739d42ea", "bc04687e8fd103456c45e4febd3f325b4d9703a01b3b6c642277a6f7287b1829"),
        new("natives-windows", 1190826, "f85d0f5acaa594f9e93d657f8feff85d6a427b6a", "efdce69d4bc8b63c04a7ea133f4b1acc9005eea00893078d696ca219b3a1bfd1"),
        new("natives-windows-arm64", 1043884, "5f475c76490bb368f235017985d8d9dc6b5b5121", "1b45d57c2e7fc9746b10b21ca4883d5ce12f7dae60899e17f92b0da142eecdc5"));
    private static readonly LegacyFreeTypeSecurityPatch Lwjgl341 = new("3.4.1", "3.4.1-mojang-2.15.0.3",
        new("", 465189, "adc701da456af7e4efd08385e55428ce986739e8", "a58281f0c3513615b0a5dbfc573a62aa2a29f9c66cdd8753e71c6d4f6281d773"),
        new("natives-windows", 1190827, "c9d78fd1df6e7ff7eff4db0126786755a6a19cd9", "fd96af6301a49d6421decf695f46d251db5e0ad452b7f3d832ae3adf1e903336"),
        new("natives-windows-arm64", 1043882, "c97ff519f79011208e5fb8be9bed7743d3b6da51", "61333b68d9384eb56fd9cc2f9f63132071eaf383e09e7f86572c5f6bf5bedebc"),
        new("natives-windows-x86", 1000778, "cae34fec6b4622218af1e3b14af90fef58df3013", "5fe4d48a604721c022ff8d99876ebaaf7ce82b590ca4dc028ce53423adfc1454"));

    internal sealed record Artifact(string Classifier, long Size, string Sha1, string Sha256);

    internal static LegacyFreeTypeSecurityPatch? Find(IEnumerable<LegacyMinecraftLibrary> libraries)
    {
        if (libraries.Any(Lwjgl343.Matches)) return Lwjgl343;
        return libraries.Any(Lwjgl341.Matches) ? Lwjgl341 : null;
    }

    internal static bool AppliesToGame(string minecraftVersion) => minecraftVersion.Length > 3 &&
        minecraftVersion.StartsWith("26.", StringComparison.Ordinal) && minecraftVersion[3] is >= '0' and <= '9';

    private bool Matches(LegacyMinecraftLibrary library) => MatchesVersion(library.OriginalName, previousVersion) ||
        MatchesVersion(library.OriginalName, version);

    private static bool MatchesVersion(string? name, string value) => name == Group + value ||
        name?.StartsWith(Group + value + ":", StringComparison.Ordinal) == true;

    internal void Apply<TLibrary>(List<TLibrary> libraries, string gameFolder, string minecraftVersion, bool x86)
        where TLibrary : LegacyMinecraftLibrary, new()
    {
        if (x86 && !artifacts.Any(artifact => artifact.Classifier == "natives-windows-x86"))
            throw new InvalidOperationException($"Minecraft {minecraftVersion} 的官方 FreeType 修复库缺少 32 位 Windows 版本，请在此实例的 Java 设置中选择 64 位 Java。");

        int insertAt = libraries.FindIndex(IsFreeType);
        libraries.RemoveAll(IsFreeType);
        foreach (Artifact artifact in artifacts) {
            if (artifact.Classifier.Length > 0 && x86 != (artifact.Classifier == "natives-windows-x86")) continue;
            string classifier = artifact.Classifier.Length == 0 ? "" : ":" + artifact.Classifier;
            string fileSuffix = artifact.Classifier.Length == 0 ? "" : "-" + artifact.Classifier;
            string path = $"{ArtifactRoot}{version}/lwjgl-freetype-{version}{fileSuffix}.jar";
            libraries.Insert(insertAt++, new TLibrary {
                OriginalName = Group + version + classifier,
                LocalPath = Path.Combine(gameFolder, "libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                Url = "https://libraries.minecraft.net/" + path,
                Size = artifact.Size,
                SHA1 = artifact.Sha1,
                RequiredSecuritySha256 = artifact.Sha256
            });
        }
    }

    private static bool IsFreeType(LegacyMinecraftLibrary library) => library.OriginalName?.StartsWith(Group, StringComparison.Ordinal) == true;

    internal static void VerifyBeforeLaunch(IReadOnlyList<LegacyMinecraftLibrary> patched, string nativesDirectory)
    {
        if (patched.Count == 0) return;
        foreach (LegacyMinecraftLibrary library in patched) {
            if (new FileChecker { ActualSize = library.Size, Hash = library.RequiredSecuritySha256 }.Check(library.LocalPath) is not null)
                throw new IOException($"FreeType 安全库校验失败（{Path.GetFileName(library.LocalPath)}），请检查网络后重试启动。");
        }

        // Modern LWJGL obtains this DLL from the classpath. An old extracted DLL
        // left in java.library.path must not be allowed to take precedence.
        if (File.Exists(Path.Combine(nativesDirectory, "freetype.dll")))
            throw new IOException("游戏原生库目录仍有旧的 FreeType DLL，请退出正在使用此实例的游戏，再重试启动。");
    }
}
