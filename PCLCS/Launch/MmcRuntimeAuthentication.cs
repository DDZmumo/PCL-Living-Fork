using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public enum MmcRuntimeAuthenticationKind { AuthlibInjector, Nide }

/// <summary>Live host binding, never part of an instance definition or source fingerprint.</summary>
public sealed class MmcRuntimeAuthenticationSnapshot {
    public MmcRuntimeAuthenticationKind Kind { get; }
    public MmcRuntimeSupportArtifact Agent { get; }
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] public string Server { get; }
    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] public string? PrefetchedMetadataJson { get; }
    public MmcRuntimeAuthenticationSnapshot(MmcRuntimeAuthenticationKind kind, MmcRuntimeSupportArtifact agent,
        string server, string? prefetchedMetadataJson = null) {
        Kind = kind; Agent = agent; Server = server; PrefetchedMetadataJson = prefetchedMetadataJson;
    }
    public override string ToString() => "MMC runtime authentication [redacted]";
}

public static partial class MmcRuntimeLaunchBuilder {
    private sealed record AuthenticationPreparation(ImmutableArray<Value> Arguments, string? ExecutionPath = null,
        byte[]? Bytes = null, MmcRuntimeSupportArtifact? Source = null);
    private static IEnumerable<MmcJavaConstraint> AuthenticationJavaConstraints(MmcRuntimeAuthenticationSnapshot? authentication) =>
        authentication is null ? [] : [new(authentication.Kind == MmcRuntimeAuthenticationKind.Nide ? "account:nide" : "account:authlib-injector", MinimumMajor: 8)];

    private static IEnumerable<string> AuthenticationSecrets(MmcRuntimeAuthenticationSnapshot? authentication) {
        if (authentication is null) yield break;
        yield return authentication.Server;
        if (authentication.PrefetchedMetadataJson is { } json) {
            yield return json;
            yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }
    }

    private static async Task<AuthenticationPreparation> AuthenticationArgumentsAsync(MmcRuntimeLaunchRequest request, string view,
        MmcRuntimeMutableDirectories mutable, List<MmcRuntimeInputObservation> inputs, IReadOnlyList<string> effectiveJvmArguments, CancellationToken token) {
        var authentication = request.Authentication;
        if (authentication is null) return new([]);
        if (!Enum.IsDefined(authentication.Kind) || authentication.Agent is null || string.IsNullOrEmpty(authentication.Server) ||
            authentication.Server.Length > 4096 || authentication.Server.Any(char.IsControl) || authentication.Agent.SizeBytes is < 1 or > 8 * 1024 * 1024)
            Fail("MMC_RUNTIME_AUTHENTICATION", "Live authentication binding is incomplete or exceeds its budget.");
        if (authentication.Kind == MmcRuntimeAuthenticationKind.Nide) {
            if (authentication.Server.Length > 256 || authentication.Server.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
                authentication.PrefetchedMetadataJson is not null)
                Fail("MMC_RUNTIME_AUTHENTICATION", "Nide requires an ordinary live server identifier.");
        } else {
            if (!Uri.TryCreate(authentication.Server, UriKind.Absolute, out var server) || server.Scheme is not ("https" or "http") ||
                server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0 ||
                authentication.PrefetchedMetadataJson is not { Length: > 0 and <= 65536 })
                Fail("MMC_RUNTIME_AUTHENTICATION", "Authlib requires an ordinary server URI and bounded live metadata.");
            try {
                using var document = System.Text.Json.JsonDocument.Parse(authentication.PrefetchedMetadataJson);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) Fail("MMC_RUNTIME_AUTHENTICATION", "Authentication metadata must be an object.");
            } catch (System.Text.Json.JsonException) { Fail("MMC_RUNTIME_AUTHENTICATION", "Authentication metadata is invalid."); }
        }
        string name = authentication.Kind == MmcRuntimeAuthenticationKind.Nide ? "nide8auth.jar" : "authlib-injector.jar";
        MmcRuntimeFiles.Hash(authentication.Agent.Sha256);
        // Exact published support role, not merely any local file or a global LF cache.
        MmcRuntimeFiles.SamePath(authentication.Agent.ActualPath, Path.Combine(view, "support", authentication.Agent.Sha256.ToLowerInvariant(), name));
        inputs.Add(await MmcRuntimeFiles.ObserveAsync("support:authentication:" + authentication.Kind,
            authentication.Agent.ActualPath, authentication.Agent.Sha256, authentication.Agent.SizeBytes, token).ConfigureAwait(false));
        await MmcClasspathInspector.VerifyAuthenticationAgentAsync(authentication.Agent, request.Java.Major, effectiveJvmArguments, token).ConfigureAwait(false);
        byte[] bytes;
        await using (FileStream input = new(authentication.Agent.ActualPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true)) {
            if (input.Length != authentication.Agent.SizeBytes) Fail("MMC_RUNTIME_INPUT_CHANGED", "Authentication source changed before its owned execution copy.");
            bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (input.Length != bytes.Length || !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(authentication.Agent.Sha256, StringComparison.OrdinalIgnoreCase))
                Fail("MMC_RUNTIME_INPUT_CHANGED", "Authentication source changed before its owned execution copy.");
        }
        // The native javaagent ZIP opener does not share Java's long-path handling.
        // Use the already-owned short session scope and a canonical absolute token;
        // the immutable approved source above remains an independent final input.
        string execution = Path.Combine(mutable.SessionRoot, "a.jar");
        MmcRuntimeFiles.Owned(execution, mutable.SessionRoot);
        string agent = "-javaagent:" + MmcRuntimeFiles.JavaPath(execution) + "=";
        List<Value> arguments = [new(agent + authentication.Server, agent + "[authentication:server]")];
        if (authentication.Kind == MmcRuntimeAuthenticationKind.AuthlibInjector) {
            if (request.Java.Major >= 6) arguments.Add(new("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT", "-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT"));
            arguments.Add(new("-Dauthlibinjector.side=client", "-Dauthlibinjector.side=client"));
            // Route agent diagnostics through the host's pre-storage redactor;
            // authlib's default independent log file bypasses that boundary.
            arguments.Add(new("-Dauthlibinjector.noLogFile=true", "-Dauthlibinjector.noLogFile=true"));
            arguments.Add(new("-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(authentication.PrefetchedMetadataJson!)),
                "-Dauthlibinjector.yggdrasil.prefetched=[authentication:metadata]"));
        }
        return new(arguments.ToImmutableArray(), execution, bytes, authentication.Agent);
    }
    private static async Task PublishAuthenticationExecutionAsync(MmcRuntimeLaunchRequest request, MmcRuntimeMutableDirectories mutable,
        AuthenticationPreparation authentication, List<MmcRuntimeInputObservation> inputs, CancellationToken token) {
        if (authentication.ExecutionPath is null) return;
        string expected = Path.Combine(mutable.SessionRoot, "a.jar");
        MmcRuntimeFiles.SamePath(authentication.ExecutionPath, expected);
        ValidateMutableOwner(request, mutable);
        await using (FileStream output = new(expected, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) {
            await output.WriteAsync(authentication.Bytes!, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        }
        inputs.Add(await MmcRuntimeFiles.ObserveAsync("support:authentication-execution:" + request.Authentication!.Kind,
            expected, authentication.Source!.Sha256, authentication.Source.SizeBytes, token).ConfigureAwait(false));
    }
}

public static partial class MmcClasspathInspector {
    internal static async Task VerifyAuthenticationAgentAsync(MmcRuntimeSupportArtifact agent, int javaMajor,
        IReadOnlyList<string> effectiveJvmArguments, CancellationToken token) {
        string entry;
        MmcInstancePaths.EnsureNoLinks(agent.ActualPath);
        using (FileStream input = new(agent.ActualPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            MmcReadOnlyJarPreflight.Inspect(input); input.Position = 0;
            using ZipArchive archive = new(input, ZipArchiveMode.Read, true, new UTF8Encoding(false, true));
            var manifests = archive.Entries.Where(e => e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (manifests.Length != 1) throw new MmcRuntimeLaunchException("MMC_RUNTIME_AUTHENTICATION_JAR", "Authentication agent requires one bounded manifest.");
            var manifest = ReadManifest(manifests[0]);
            if (!manifest.TryGetValue("Premain-Class", out entry!) || string.IsNullOrWhiteSpace(entry) ||
                new[] { "Class-Path", "Boot-Class-Path" }.Any(key => manifest.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)))
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_AUTHENTICATION_JAR", "Authentication agent has no premain entry or introduces unbound manifest inputs.");
        }
        ValidateNames([entry]);
        var observed = await ReadJarAsync("support:authentication", agent.ActualPath, agent.Sha256, agent.SizeBytes,
            [entry], RuntimePolicy(javaMajor, effectiveJvmArguments), new(StringComparer.Ordinal) { entry }, token).ConfigureAwait(false);
        if (!observed.Found.ContainsKey(entry)) throw new MmcRuntimeLaunchException("MMC_RUNTIME_AUTHENTICATION_JAR", "Authentication agent premain class is absent.");
    }
}
