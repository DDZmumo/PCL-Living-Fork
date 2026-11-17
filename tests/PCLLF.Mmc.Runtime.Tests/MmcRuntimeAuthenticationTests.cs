using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using PCLCS;

// Original LF controlled-JVM regression; repository LICENCE. No real account/server IO.
internal static partial class MmcRuntimeLaunchTests {
    internal static void AuthenticationAgents() {
        AuthenticationAgentSeparatorPaths();
        foreach (var run in new[] {
            (Kind: MmcRuntimeAuthenticationKind.AuthlibInjector, Major: 17, Mode: MmcRuntimeWrapperMode.JavaWrapper),
            (Kind: MmcRuntimeAuthenticationKind.Nide, Major: 8, Mode: MmcRuntimeWrapperMode.DirectMain)
        }) {
            using Fixture fixture = new(run.Major, loader: false, wrapperMode: run.Mode);
            byte[] bytes = CompileAuthenticationAgent(fixture.Root);
            var agent = OwnedAuthenticationAgent(fixture.Request, run.Kind, bytes);
            string server = run.Kind == MmcRuntimeAuthenticationKind.Nide ? "synthetic-nide-id" : "https://fixture.invalid/auth/";
            string? metadata = run.Kind == MmcRuntimeAuthenticationKind.AuthlibInjector ? "{\"meta\":{\"serverName\":\"synthetic-auth-server\"}}" : null;
            var request = fixture.Request with { Authentication = new(run.Kind, agent, server, metadata) };
            string sourceFingerprint = request.BoundPlan.CompiledProfile.SourceFingerprint;
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult();
            Require(plan.Inputs.Single(i => i.Identity.StartsWith("support:authentication:", StringComparison.Ordinal)).ActualPath == agent.ActualPath,
                "Authentication agent is missing from the actual revalidated input set.");
            var execution = plan.Inputs.Single(i => i.Identity.StartsWith("support:authentication-execution:", StringComparison.Ordinal));
            Require(execution.ActualPath == Path.Combine(plan.MutableDirectories.SessionRoot, "a.jar") && execution.ActualPath != agent.ActualPath &&
                execution.Sha256 == agent.Sha256 && execution.SizeBytes == agent.SizeBytes &&
                Encoding.UTF8.GetByteCount(execution.ActualPath) + 1 < 260 && File.Exists(agent.ActualPath),
                "Authentication execution copy lost its short owned role or independent approved source binding.");
            string[] actual = plan.CreateStartInfo().ArgumentList.ToArray();
            string javaAgent = actual.Single(a => a.StartsWith("-javaagent:", StringComparison.Ordinal));
            Require(javaAgent == "-javaagent:" + execution.ActualPath.Replace('\\', '/') + "=" + server,
                "Authentication javaagent argument does not target the owned execution copy as one exact token.");
            string encoded = metadata is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata));
            if (metadata is not null) Require(actual.Contains("-Dauthlibinjector.side=client") && actual.Contains("-Dauthlibinjector.noLogFile=true") && actual.Contains("-Dauthlibinjector.yggdrasil.prefetched=" + encoded), "Authlib live client properties or host-only diagnostics binding were dropped.");
            string safe = string.Join('\n', plan.RedactedArguments.Concat(plan.LogicalJvmArguments));
            Require(!safe.Contains(server, StringComparison.Ordinal) && !safe.Contains(request.Account.AccessToken, StringComparison.Ordinal) &&
                (encoded.Length == 0 || !safe.Contains(encoded, StringComparison.Ordinal)), "Public runtime evidence includes authentication/account values.");
            Require(!System.Text.Json.JsonSerializer.Serialize(request.Authentication).Contains(server, StringComparison.Ordinal), "Authentication snapshot serialization disclosed its live server binding.");
            byte[] damagedExecution = bytes.ToArray(); damagedExecution[^1] ^= 1; File.WriteAllBytes(execution.ActualPath, damagedExecution);
            Require(Hash(File.ReadAllBytes(agent.ActualPath)) == agent.Sha256, "Execution mutation altered the approved source; a hard link was used.");
            Expect("MMC_RUNTIME_INPUT_CHANGED", () => MmcRuntimeLaunchBuilder.RevalidateFilesAsync(plan).GetAwaiter().GetResult());
            File.WriteAllBytes(execution.ActualPath, bytes);
            MmcRuntimeLaunchBuilder.RevalidateFilesAsync(plan).GetAwaiter().GetResult();

            using (var child = Process.Start(plan.CreateStartInfo()) ?? throw new Exception("Owned agent fixture did not start.")) {
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(20000)) { child.Kill(true); child.WaitForExit(5000); throw new TimeoutException("Owned agent fixture timed out."); }
                Require(Task.WaitAll([stdout, stderr], 5000), "Owned agent fixture output did not drain.");
                Require(child.ExitCode == 0, "Owned agent fixture failed: " + plan.Redact(stderr.Result));
                string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(server + "|" + (metadata is null ? "" : "client") + "|" + encoded));
                Require(stdout.Result.Split('\n').Any(line => line.TrimEnd('\r') == "MAUTH:" + expected), "Actual JVM premain did not receive exact bound server/client/prefetch values.");
                string log = plan.Redact(server + " " + (metadata ?? "") + " " + encoded + " " + request.Account.AccessToken);
                Require(!log.Contains(server, StringComparison.Ordinal) && !log.Contains(request.Account.AccessToken, StringComparison.Ordinal) &&
                    (encoded.Length == 0 || !log.Contains(encoded, StringComparison.Ordinal)), "Authentication log redaction missed live metadata or account values.");
            }

            fixture.RemoveMutable(plan);
            var rotated = request with { Account = new("another-player", new string('2', 32), "rotated-synthetic-secret", "rotated-synthetic-session", "legacy", "{}"),
                Authentication = new(run.Kind, agent, run.Kind == MmcRuntimeAuthenticationKind.Nide ? "other-nide-id" : "https://another.invalid/",
                    metadata is null ? null : "{\"meta\":{\"serverName\":\"rotated\"}}") };
            var next = MmcRuntimeLaunchBuilder.BuildAsync(rotated).GetAwaiter().GetResult();
            Require(plan.NonSensitiveArgumentFingerprint == next.NonSensitiveArgumentFingerprint && plan.RuntimeInputFingerprint == next.RuntimeInputFingerprint &&
                sourceFingerprint == rotated.BoundPlan.CompiledProfile.SourceFingerprint, "Account/authentication values entered a source or non-sensitive runtime fingerprint.");
            byte[] changed = bytes.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(agent.ActualPath, changed);
            Expect("MMC_RUNTIME_INPUT_CHANGED", () => MmcRuntimeLaunchBuilder.RevalidateFilesAsync(next).GetAwaiter().GetResult());
        }
        using (Fixture fixture = new(17, loader: false)) {
            byte[] bytes = CompileAuthenticationAgent(fixture.Root);
            var owned = OwnedAuthenticationAgent(fixture.Request, MmcRuntimeAuthenticationKind.AuthlibInjector, bytes);
            var authentication = new MmcRuntimeAuthenticationSnapshot(MmcRuntimeAuthenticationKind.AuthlibInjector, owned, "https://fixture.invalid/", "{}");
            string global = Path.Combine(fixture.Root, "global-authlib.jar"); File.WriteAllBytes(global, bytes);
            Expect("MMC_RUNTIME_PATH", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with {
                Authentication = new(MmcRuntimeAuthenticationKind.AuthlibInjector, owned with { ActualPath = global }, "https://fixture.invalid/", "{}") }).GetAwaiter().GetResult());
            Expect("MMC_RUNTIME_UNBOUND_INPUT", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with {
                Authentication = authentication, Settings = fixture.Request.Settings with { UserJvmTokens = ["-javaagent:" + owned.ActualPath] } }).GetAwaiter().GetResult());
            var external = OwnedAuthenticationAgent(fixture.Request, MmcRuntimeAuthenticationKind.AuthlibInjector,
                CompileAuthenticationAgent(fixture.Root, "Class-Path: adjacent.jar\r\n"));
            Expect("MMC_RUNTIME_AUTHENTICATION_JAR", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with {
                Authentication = new(MmcRuntimeAuthenticationKind.AuthlibInjector, external, "https://fixture.invalid/", "{}") }).GetAwaiter().GetResult());
        }
    }
    private static void AuthenticationAgentSeparatorPaths() {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string version = Path.Combine(root, "a=b", "versions", "x"); Directory.CreateDirectory(Path.Combine(version, "minecraft"));
            var paths = MmcInstancePaths.Resolve(version);
            Guid instanceId = Guid.NewGuid(), sessionId = Guid.NewGuid();
            string fingerprint = new('a', 64);
            var describe = typeof(MmcSessionRuntimeDirectories).GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            MmcMutableRuntimePaths Describe(MmcMutableRuntimeAuthority candidateAuthority, bool authenticationEnabled) =>
                (MmcMutableRuntimePaths)describe.Invoke(null, [paths, instanceId, sessionId, fingerprint, candidateAuthority, authenticationEnabled])!;
            var authority = FixtureAuthority(root);
            var ordinary = Describe(authority, false);
            Require(ordinary.RuntimeBinding.MutableLayout is null && ordinary.SessionRoot.Contains('='),
                "Separator regression fixture already required a short scope without authentication; it does not cover agent-only fallback.");
            var authentication = Describe(authority, true);
            Require(authentication.RuntimeBinding.MutableLayout == MmcMutableRuntimeLayout.LocalAppDataV2 && !authentication.SessionRoot.Contains('=') &&
                authentication.SessionRoot.StartsWith(Path.Combine(root, "m", instanceId.ToString("N")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "An instance ancestor containing '=' did not select its existing owned short authority for javaagent execution.");
            Require(Path.IsPathFullyQualified(Path.Combine(authentication.SessionRoot, "a.jar")) && !Directory.Exists(authentication.SessionRoot),
                "Authentication scope selection returned an unbound path or created files before validation.");

            // Simulate LocalAppData itself containing '=' through the same private
            // host-root constructor; all paths remain inside this owned GUID fixture.
            string badRoot = Path.Combine(root, "host=scope");
            var constructor = typeof(MmcMutableRuntimeAuthority).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, [typeof(string)], null)!;
            var unsupportedAuthority = (MmcMutableRuntimeAuthority)constructor.Invoke([badRoot]);
            try { Describe(unsupportedAuthority, true); throw new Exception("An authority root containing '=' produced a broken javaagent execution token."); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is IOException io && io.Message.Contains("javaagent option separator", StringComparison.Ordinal)) { }
            Require(!Directory.Exists(badRoot), "Rejected authentication authority created a mutable execution directory.");
        } finally { DeleteMutableFixture(root); }
    }
    private static MmcRuntimeSupportArtifact OwnedAuthenticationAgent(MmcRuntimeLaunchRequest request, MmcRuntimeAuthenticationKind kind, byte[] bytes) {
        string name = kind == MmcRuntimeAuthenticationKind.Nide ? "nide8auth.jar" : "authlib-injector.jar";
        string target = Path.Combine(request.Paths.RuntimeRoot, request.Directories.ViewKey, "support", Hash(bytes), name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
        return new(target, Hash(bytes), bytes.Length);
    }
    private static byte[] CompileAuthenticationAgent(string root, string extraManifest = "") {
        string source = Path.Combine(root, "agent-source", "fixture", "Agent.java"), classes = Path.Combine(root, "agent-classes");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.CreateDirectory(classes);
        File.WriteAllText(source, """
package fixture;
import java.lang.instrument.Instrumentation;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
public final class Agent {
  public static void premain(String args, Instrumentation instrumentation) {
    String value = args + "|" + System.getProperty("authlibinjector.side", "") + "|" + System.getProperty("authlibinjector.yggdrasil.prefetched", "");
    System.out.println("MAUTH:" + Base64.getEncoder().encodeToString(value.getBytes(StandardCharsets.UTF_8)));
  }
}
""", new UTF8Encoding(false));
        RunTool(Path.Combine(Path.GetDirectoryName(Java17)!, "javac.exe"), ["--release", "8", "-g:none", "-d", classes, source]);
        return Jar(new() { ["fixture/Agent.class"] = File.ReadAllBytes(Path.Combine(classes, "fixture", "Agent.class")) },
            "Manifest-Version: 1.0\r\nPremain-Class: fixture.Agent\r\n" + extraManifest + "\r\n");
    }
}
