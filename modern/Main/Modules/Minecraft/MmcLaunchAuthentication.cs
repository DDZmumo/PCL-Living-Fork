using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

internal sealed record MmcAuthenticationJavaRequirements(ImmutableArray<MmcJavaConstraint> Majors, Version MinimumVersion);

// Original LF Main integration; governed by the repository LICENCE.
/// <summary>Reuses existing LF source identities and download queue; never logs in or stores account/session values.</summary>
internal sealed class MmcLaunchAuthentication {
    private readonly string loginType, server;
    private readonly MmcRuntimeAuthenticationKind? kind;
    internal MmcRuntimeAuthenticationSnapshot Binding { get; private set; }
    internal ImmutableArray<MmcJavaConstraint> JavaConstraints => JavaRequirements(kind).Majors;
    internal Version MinimumJavaVersion => JavaRequirements(kind).MinimumVersion;
    // Type-only preview: never reads a login result, server setting, token or account snapshot.
    internal static MmcAuthenticationJavaRequirements JavaRequirementsForType(ModLaunch.McLoginType type) => JavaRequirements(type switch {
        ModLaunch.McLoginType.Nide => MmcRuntimeAuthenticationKind.Nide,
        ModLaunch.McLoginType.Auth => MmcRuntimeAuthenticationKind.AuthlibInjector,
        _ => null
    });
    private static MmcAuthenticationJavaRequirements JavaRequirements(MmcRuntimeAuthenticationKind? type) => new(
        type is null ? [] : [new(type == MmcRuntimeAuthenticationKind.Nide ? "account:nide" : "account:authlib-injector", MinimumMajor: 8)],
        type == MmcRuntimeAuthenticationKind.Nide ? new Version(8, 0, 141) : null);

    // Only public agent metadata is substitutable in controlled facade checks.
    // The real Main downloader, owned publication, runtime binding and admission remain genuine.
    internal static Func<MmcRuntimeAuthenticationKind, string, CancellationToken, Task<(ImmutableArray<string> Urls, string Digest)>> AgentSource { get; set; } =
        (kind, server, token) => ResolveAgentSourceAsync(kind, server, ModNet.StandardMetadataClient, token);

    private MmcLaunchAuthentication(string loginType, string server, MmcRuntimeAuthenticationKind? kind) {
        this.loginType = loginType; this.server = server; this.kind = kind;
    }
    internal static MmcLaunchAuthentication Capture(ModMinecraft.McInstance instance, string loginType) {
        MmcRuntimeAuthenticationKind? kind = loginType switch { "Auth" => MmcRuntimeAuthenticationKind.AuthlibInjector, "Nide" => MmcRuntimeAuthenticationKind.Nide, _ => null };
        string server = ReadServer(instance, kind); ValidateServer(kind, server);
        return new(loginType, server, kind);
    }
    internal async Task PrepareAsync(MmcPreparedInstanceLaunch prepared, Java java, ProgressProvider progress, CancellationToken token) {
        ValidateJava(java);
        if (kind is null) { token.ThrowIfCancellationRequested(); return; }
        var source = await AgentSource(kind.Value, server, token).ConfigureAwait(false);
        var owned = await MmcRuntimeAuthenticationSupport.PrepareAsync(prepared, kind.Value, source.Urls, source.Digest,
            MmcDownloads.BuildFile, (files, cancellation) => MmcDownloads.RunBatchAsync(files, cancellation, progress), token).ConfigureAwait(false);
        string metadata = kind == MmcRuntimeAuthenticationKind.AuthlibInjector
            ? await ReadObjectAsync(server, ModNet.StandardMetadataClient, token, privateMetadata: true).ConfigureAwait(false) : null;
        Binding = new(kind.Value, owned, server, metadata);
    }
    internal void Revalidate(ModMinecraft.McInstance instance, string actualLoginType, Java java) {
        if (loginType != actualLoginType || server != ReadServer(instance, kind))
            throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_CHANGED", "认证方式或服务器设置在准备后发生变化，请重新启动。");
        ValidateJava(java);
        if (kind is not null && Binding is null) throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_MISSING", "认证代理尚未完成 owned 文件准备。");
    }
    private void ValidateJava(Java java) {
        // Keep the account's exact update constraint even when a custom main class
        // provides complete authority to retire vanilla Minecraft/Forge inference.
        if (MinimumJavaVersion is { } minimum && java.Version < minimum)
            throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_JAVA", "统一通行证需要 Java 8u141 或更高版本。");
    }
    private static string ReadServer(ModMinecraft.McInstance instance, MmcRuntimeAuthenticationKind? kind) => kind switch {
        MmcRuntimeAuthenticationKind.Nide => Settings.Get<string>("VersionServerNide", instance),
        MmcRuntimeAuthenticationKind.AuthlibInjector => Settings.Get<string>("VersionServerAuthServer", instance),
        _ => ""
    };
    private static void ValidateServer(MmcRuntimeAuthenticationKind? kind, string server) {
        if (kind is null) return;
        if (kind == MmcRuntimeAuthenticationKind.Nide) {
            if (server is { Length: > 0 and <= 256 } && server.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return;
        } else if (server is { Length: > 0 and <= 4096 } && !server.Any(char.IsControl) && Uri.TryCreate(server, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0) return;
        throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_SERVER", "第三方认证服务器设置无效。");
    }

    internal static async Task<(ImmutableArray<string> Urls, string Digest)> ResolveAgentSourceAsync(MmcRuntimeAuthenticationKind kind,
        string server, HttpClient client, CancellationToken token) {
        ValidateServer(kind, server);
        try {
            if (kind == MmcRuntimeAuthenticationKind.Nide) {
                string text = await ReadObjectAsync(LegacyAuthenticationAgentSources.NideMetadataPrefix + server, client, token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(text);
                string digest = json.RootElement.GetProperty("jarHash").GetString();
                ValidateDigest(digest, false); return ([LegacyAuthenticationAgentSources.NideJar], digest);
            }
            if (kind != MmcRuntimeAuthenticationKind.AuthlibInjector) throw new FormatException();
            string metadata;
            try { metadata = await ReadObjectAsync(LegacyAuthenticationAgentSources.AuthlibMetadata, client, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) {
                metadata = await ReadObjectAsync(LegacyAuthenticationAgentSources.AuthlibMetadataMirror, client, token).ConfigureAwait(false);
            }
            using var document = JsonDocument.Parse(metadata);
            string address = document.RootElement.GetProperty("download_url").GetString();
            string hash = document.RootElement.GetProperty("checksums").GetProperty("sha256").GetString();
            ValidateDigest(hash, true);
            if (!Uri.TryCreate(address, UriKind.Absolute, out var source) || source.Scheme != "https" || !source.IsDefaultPort ||
                source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0) throw new FormatException();
            string relative = source.Host switch {
                "authlib-injector.yushi.moe" when source.AbsolutePath.StartsWith("/artifact/", StringComparison.Ordinal) => source.AbsolutePath,
                "bmclapi2.bangbang93.com" when source.AbsolutePath.StartsWith("/mirrors/authlib-injector/artifact/", StringComparison.Ordinal) => source.AbsolutePath["/mirrors/authlib-injector".Length..],
                _ => throw new FormatException()
            };
            return (["https://authlib-injector.yushi.moe" + relative, "https://bmclapi2.bangbang93.com/mirrors/authlib-injector" + relative], hash);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_METADATA", "无法取得认证代理的有效下载信息，请稍后重试。"); }
    }
    private static void ValidateDigest(string digest, bool sha256) {
        if (digest is null || (sha256 ? digest.Length != 64 : digest.Length is not (32 or 40 or 64)) || !digest.All(Uri.IsHexDigit)) throw new FormatException();
    }
    private static async Task<string> ReadObjectAsync(string address, HttpClient client, CancellationToken token, bool privateMetadata = false) {
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (privateMetadata) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 65536) throw new IOException();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using MemoryStream output = new(); byte[] buffer = new byte[4096];
            while (true) {
                int count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > 65536) throw new IOException();
                output.Write(buffer, 0, count);
            }
            string text = new UTF8Encoding(false, true).GetString(output.ToArray());
            using var json = JsonDocument.Parse(text); if (json.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException();
            token.ThrowIfCancellationRequested(); return text;
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_CONNECTION", "无法读取第三方认证服务的有效元数据。"); }
    }
    public override string ToString() => "MMC launch authentication [redacted]";
}
