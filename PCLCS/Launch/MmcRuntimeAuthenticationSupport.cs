using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public static class MmcRuntimeAuthenticationSupport {
    public const long MaximumAgentBytes = 8 * 1024 * 1024;

    /// <summary>Uses the existing batch's validated download and cancellation drain, then publishes an owned SHA256 execution copy.</summary>
    public static async Task<MmcRuntimeSupportArtifact> PrepareAsync<TNetFile>(MmcPreparedInstanceLaunch prepared,
        MmcRuntimeAuthenticationKind kind, ImmutableArray<string> urls, string expectedDigest,
        Func<MmcArtifactDownloadSpec, TNetFile> build, Func<ImmutableArray<TNetFile>, CancellationToken, Task> runBatch,
        CancellationToken cancellationToken = default) where TNetFile : notnull {
        ArgumentNullException.ThrowIfNull(prepared); ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(runBatch);
        if (!Enum.IsDefined(kind) || urls.IsDefaultOrEmpty || urls.Length > 2 || expectedDigest is null ||
            expectedDigest.Length is not (32 or 40 or 64) || !expectedDigest.All(Uri.IsHexDigit) ||
            urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_SOURCE", "Authentication agent source identity is incomplete or invalid.");
        if (kind == MmcRuntimeAuthenticationKind.AuthlibInjector && expectedDigest.Length != 64)
            throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_SOURCE", "Authlib agent requires its published SHA256 digest.");
        string name = kind == MmcRuntimeAuthenticationKind.Nide ? "nide8auth.jar" : "authlib-injector.jar";
        cancellationToken.ThrowIfCancellationRequested();
        using var workspace = new MmcArtifactWorkspace(prepared.Paths);
        string target = workspace.NewFile();
        // This descriptor exists only for the existing downloader; it never enters
        // package sources, compiled profiles, definitions or source fingerprints.
        string identity = "host-authentication:" + kind;
        var artifact = new MmcArtifactReference(identity, "pcllf.authentication", expectedDigest, MmcArtifactRole.DownloadOnly,
            MmcArtifactScope.Shared, null, name, urls, expectedDigest.Length == 40 ? expectedDigest : null,
            expectedDigest.Length == 64 ? expectedDigest : null, null, null, [], new("pcllf.authentication", null, identity, new string('0', 64), "agent", 0));
        var spec = new MmcArtifactDownloadSpec(artifact, [identity], target, urls, artifact.Sha256, artifact.Sha1, null, MaximumAgentBytes) {
            ExpectedMd5 = expectedDigest.Length == 32 ? expectedDigest : null
        };
        // The runner must return only after all writers drain, including cancellation.
        await runBatch([build(spec)], cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(target);
        byte[] bytes;
        await using (FileStream input = new(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true)) {
            if (input.Length is < 1 or > MaximumAgentBytes) throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_SIZE", "Authentication agent exceeds its source byte budget.");
            bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (input.Length != bytes.Length) throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_CHANGED", "Authentication agent source changed during adoption.");
        }
        string observed = expectedDigest.Length switch {
            32 => Convert.ToHexStringLower(MD5.HashData(bytes)),
            40 => Convert.ToHexStringLower(SHA1.HashData(bytes)),
            _ => Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
        if (!observed.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new MmcRuntimeLaunchException("MMC_AUTHENTICATION_CHANGED", "Authentication agent differs from its published source digest.");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var support = await MmcInstanceBackend<TNetFile>.PrepareLaunchSupportAsync(prepared,
            [new(name, bytes, sha256)], cancellationToken).ConfigureAwait(false);
        var owned = support.Single(); return new(owned.ActualPath, owned.Sha256, owned.SizeBytes);
    }
}
