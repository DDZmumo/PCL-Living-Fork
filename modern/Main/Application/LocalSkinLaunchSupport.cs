// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System.Security.Cryptography;
using PCLCS.Applications;
using PCLLF.MMCC.Core;

namespace PCL;

internal static class LocalSkinLaunchSupport {
    internal const string AgentFileName = "authlib-injector.jar";
    private const string SourceIdentity = "pcllf.local-skin-agent";
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    internal sealed record Agent(string Path, byte[] Bytes, string Sha256);

    internal static LocalSkinLaunch? Capture(string player, string uuid) {
        if (LauncherApplicationServices.SkinLibrary.Store.ReadOfflineTextures(player) is not { } textures) return null;
        if (!Guid.TryParse(uuid, out var profile)) throw new IOException("离线角色标识无效，无法应用本地皮肤库。");
        return new(player, profile, textures);
    }

    internal static LocalSkinServiceProcess? PrepareNative(ILegacyLaunchLoginResult login, Java java, ProcessStartInfo start, CancellationToken token) {
        if (login.Type != "Legacy" || Capture(login.Name, login.Uuid) is not { } appearance) return null;
        if (java.Version.Major < 8) throw new IOException("应用离线皮肤库需要 Java 8 或更高版本。");
        var agent = ReadAgentAsync(token).GetAwaiter().GetResult();
        var session = Start(appearance, token);
        string metadata = Convert.ToBase64String(Encoding.UTF8.GetBytes(session.Endpoint.Metadata));
        start.Arguments = $"\"-javaagent:{PathUtils.ToShortPath(agent.Path)}={session.Endpoint.Address}\" " +
            "-Dauthlibinjector.side=client -Dauthlibinjector.noLogFile=true " +
            $"-Dauthlibinjector.yggdrasil.prefetched={metadata} " + start.Arguments;
        return session;
    }

    internal static LocalSkinServiceProcess Start(LocalSkinLaunch appearance, CancellationToken token) =>
        LocalSkinServiceProcess.StartAsync(Environment.ProcessPath ?? throw new IOException("无法定位启动器程序。"), appearance, token).GetAwaiter().GetResult();

    internal static async Task<Agent> ReadAgentAsync(CancellationToken token) {
        await DownloadGate.WaitAsync(token).ConfigureAwait(false);
        try {
            string root = Path.Combine(ModBase.PathPure.Value, "LocalSkinAgent");
            string digestPath = Path.Combine(root, "agent.sha256");
            CheckCachePath(root, digestPath);
            string digest = File.Exists(digestPath) && new FileInfo(digestPath).Length == 64 ? File.ReadAllText(digestPath) : "";
            if (digest.Length == 64 && digest.All(Uri.IsHexDigit) && await ReadVerifiedAsync(root, digest, token).ConfigureAwait(false) is { } cached)
                return cached;

            var source = await MmcLaunchAuthentication.AgentSource(MmcRuntimeAuthenticationKind.AuthlibInjector, "http://127.0.0.1/", token).ConfigureAwait(false);
            digest = source.Digest.ToLowerInvariant();
            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new IOException("认证代理 SHA256 无效。");
            string path = AgentPath(root, digest);
            CheckCachePath(root, path);
            var artifact = new MmcArtifactReference(SourceIdentity, SourceIdentity, digest, MmcArtifactRole.DownloadOnly,
                MmcArtifactScope.Shared, null, AgentFileName, source.Urls, null, digest, null, null, [],
                new(SourceIdentity, null, SourceIdentity, digest, "agent", 0));
            var download = MmcDownloads.BuildFile(new(artifact, [SourceIdentity], path, source.Urls, digest, null, null, MmcRuntimeAuthenticationSupport.MaximumAgentBytes));
            await MmcDownloads.RunBatchAsync([download], token).ConfigureAwait(false);
            var agent = await ReadVerifiedAsync(root, digest, token).ConfigureAwait(false) ?? throw new IOException("认证代理下载后的 SHA256 校验失败。");
            LocalGameDataStore.AtomicWrite(digestPath, Encoding.ASCII.GetBytes(digest), null, false);
            return agent;
        } finally { DownloadGate.Release(); }
    }

    private static string AgentPath(string root, string digest) => Path.Combine(root, digest, AgentFileName);
    private static async Task<Agent?> ReadVerifiedAsync(string root, string digest, CancellationToken token) {
        string path = AgentPath(root, digest);
        CheckCachePath(root, path);
        if (!File.Exists(path)) return null;
        await using var input = File.OpenRead(path);
        if (input.Length is < 1 or > MmcRuntimeAuthenticationSupport.MaximumAgentBytes) return null;
        byte[] bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), digest, StringComparison.OrdinalIgnoreCase) ? new(path, bytes, digest) : null;
    }

    private static void CheckCachePath(string root, string path) {
        for (string current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current)!) {
            try {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("本地皮肤代理缓存包含链接。");
            } catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
