using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Network;

namespace PCLLF.Native.Facade.Tests;

// Original LF Main facade regression; self-authored Java and loopback only.
internal static partial class MmcLaunchChecks {
    internal static void RunAuthentication(FixtureEnvironment environment) {
        if (!Environment.GetCommandLineArgs().Contains(ChildSwitch, StringComparer.Ordinal)) { RunChild("mmc-authentication"); return; }
        try {
            ModpackUpdateCleanup.VerifyIsolatedChild(); retained = new(environment, false);
            retained.RunAuthentication(); environment.AssertNoForbiddenIo();
            Native.Check(System.Windows.Application.Current.Windows.Count == 0, "MMC authentication replay opened a window.");
            File.WriteAllText(Path.Combine(environment.Root, Receipt), MainHash());
            Console.WriteLine("PASS mmc-authentication: actual Main Authlib/Nide owned-agent argv and JVM premain, client prefetch, token-free persisted source, exact Nide Java constraint, source allowlist, changed settings refusal and cancellation/download drain. Self-authored JVM only; real account acceptance pending.");
            Console.Out.Flush(); Console.Error.Flush(); using ManualResetEventSlim keepGuards = new(); keepGuards.Wait();
        } catch (Exception error) {
            retained?.StopOwnedChildren(); File.WriteAllText(Path.Combine(environment.Root, Receipt + ".failed"), error.ToString());
            ModpackUpdateCleanup.TerminateChild("MMC authentication replay failed with owned guards retained: " + error);
        }
    }
    private sealed partial class Scope {
        internal void RunAuthentication() {
            Type helper = Native.Type("PCL.MmcLaunchAuthentication");
            var sourceProperty = Native.Property(helper, "AgentSource"); object originalSource = sourceProperty.GetValue(null)!;
            var observer = (Action<Guid, Process>)Native.Property(typeof(MmcLaunch), "ProcessCreated").GetValue(null)!;
            var network = (LegacyNetworkHost<ModNet.NetFile, ModNet.LoaderDownload, ModNet.NetManagerClass, ModLoader.LoaderBase>)Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
            network.SignCdn = value => server.Owns(value) ? value : throw new IOException("Authentication fixture download escaped loopback.");
            var sign = network.SignHeaders;
            network.SignHeaders = (string address, ref HttpRequestMessage request, bool browser, bool credentials) => {
                Native.Check(server.Owns(address) && !credentials, "Authentication agent request escaped loopback or enabled credentials.");
                sign(address, ref request, browser, credentials);
                Native.Check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("x-api-key"), "Agent request disclosed account credentials.");
            };
            network.TempFolder = () => Path.Combine(root, "agent-network-temp") + Path.DirectorySeparatorChar;
            network.RequestTempFolder = () => Path.Combine(root, "agent-network-work-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
            network.AppData = () => Path.Combine(root, "agent-app-data") + Path.DirectorySeparatorChar;
            network.MinecraftFolders = () => [game + Path.DirectorySeparatorChar];
            try {
                AgentMetadataSources(helper);
                byte[] auth = CompileAgent("auth"), nide = CompileAgent("nide");
                string metadata = "{\"meta\":{\"serverName\":\"owned-synthetic-auth\"}}";
                server.Add("/agent-auth", auth); server.Add("/agent-nide", nide); server.Add("/auth-server", Encoding.UTF8.GetBytes(metadata));
                sourceProperty.SetValue(null, new Func<MmcRuntimeAuthenticationKind, string, CancellationToken, Task<(ImmutableArray<string>, string)>>((kind, _, token) => {
                    token.ThrowIfCancellationRequested(); byte[] bytes = kind == MmcRuntimeAuthenticationKind.Nide ? nide : auth;
                    string digest = Convert.ToHexStringLower(kind == MmcRuntimeAuthenticationKind.Nide ? MD5.HashData(bytes) : SHA256.HashData(bytes));
                    return Task.FromResult<(ImmutableArray<string>, string)>(([server.Url(kind == MmcRuntimeAuthenticationKind.Nide ? "/agent-nide" : "/agent-auth")], digest));
                }));
                byte[] before = File.ReadAllBytes(Path.Combine(version, "PCL", "InstanceDefinition.json"));
                string expectedServer = server.Url("/auth-server"); string expectedHash = Convert.ToHexStringLower(SHA256.HashData(auth));
                bool authMode = true;
                Native.Property(typeof(MmcLaunch), "ProcessCreated").SetValue(null, new Action<Guid, Process>((id, process) => {
                    observer(id, process);
                    string token = process.StartInfo.ArgumentList.Single(value => value.StartsWith("-javaagent:", StringComparison.Ordinal));
                    int split = token.IndexOf('=', "-javaagent:".Length); Native.Check(split > 0, "Agent JVM token lost its server operand.");
                    string actual = Path.GetFullPath(token["-javaagent:".Length..split], instance.PathIndie);
                    Native.Check(token[(split + 1)..] == expectedServer && Path.GetFileName(actual) == "a.jar" && actual.Contains(id.ToString("N"), StringComparison.Ordinal) &&
                        actual.StartsWith(environment.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(actual))) == expectedHash,
                        "Actual Main JVM does not target its short owned adopted authentication execution copy.");
                    if (authMode) Native.Check(process.StartInfo.ArgumentList.Contains("-Dauthlibinjector.side=client") && process.StartInfo.ArgumentList.Contains("-Dauthlibinjector.noLogFile=true") &&
                        process.StartInfo.ArgumentList.Contains("-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata))), "Actual Main omitted Authlib live metadata/client bindings.");
                }));
                ModLaunch.McLoginLoader.Output.Type = "Auth"; Settings.Set("VersionServerAuthServer", expectedServer, instance); instance.Config.Save();
                Launch("authentication-authlib", false, false);
                Native.Check(!File.Exists(AgentExecutionPath(children[currentSession])), "Verified Authlib exit retained its mutable execution agent.");
                CheckAgentLog(metadata, expectedServer);
                authMode = false; expectedServer = "synthetic-nide-id"; expectedHash = Convert.ToHexStringLower(SHA256.HashData(nide));
                Settings.Set("VersionServerNide", expectedServer, instance); ModLaunch.McLoginLoader.Output.Type = "Nide"; instance.Config.Save();
                object captured = Native.Call(helper, "Capture", null, instance, "Nide")!;
                try { Native.Call(helper, "Revalidate", captured, instance, "Nide", new Java(Path.GetDirectoryName(java8)!, new Version(8, 0, 140))); throw new IOException("Old Nide Java was accepted."); }
                catch (MmcRuntimeLaunchException error) when (error.Code == "MMC_AUTHENTICATION_JAVA") { }
                Launch("authentication-nide", false, false); CheckAgentLog(null, expectedServer);
                Native.Check(!File.Exists(AgentExecutionPath(children[currentSession])), "Verified Nide exit retained its mutable execution agent.");
                Native.Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(version, "PCL", "InstanceDefinition.json"))), "Authentication/account data altered persisted package sources or definition.");
                ChangedAuthenticationSettings(helper, sourceProperty, nide);
                CancelAgentPreparation(sourceProperty);
                Native.Check(starts == 2 && forbidden == 0 && !MmcLaunch.HasActiveSessions, "Authentication fixture created an unapproved process or crossed ordinary launch.");
                Until(() => watcherThreads.All(thread => !thread.IsAlive), "authentication watcher threads exit");
                Native.Check(asyncErrors.IsEmpty, "Authentication watcher failed.");
                InstanceConfigProviders.FlushAndDisposeWithin(root); server.Dispose(); client.Dispose(); environment.AssertNoForbiddenIo();
            } finally { sourceProperty.SetValue(null, originalSource); Native.Property(typeof(MmcLaunch), "ProcessCreated").SetValue(null, observer); }
        }
        private static string AgentExecutionPath(Process child) {
            string token = child.StartInfo.ArgumentList.Single(value => value.StartsWith("-javaagent:", StringComparison.Ordinal));
            return token["-javaagent:".Length..token.IndexOf('=', "-javaagent:".Length)];
        }
        private void CheckAgentLog(string? metadata, string expectedServer) {
            var lines = ModLaunch.McLaunchWatcher.LatestLog.Concat(ModLaunch.McLaunchWatcher.WaitingLog).Where(line => line is not null).ToArray();
            Native.Check(lines.Any(line => line.Contains("AUTH_AGENT_STARTED", StringComparison.Ordinal)) &&
                lines.Any(line => line.StartsWith("AUTH_SERVER=[redacted]", StringComparison.Ordinal)), "Actual agent premain or pre-storage server redaction was not observed.");
            string encoded = metadata is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata));
            Native.Check(lines.All(line => !line.Contains(expectedServer, StringComparison.Ordinal) && !line.Contains(secret, StringComparison.Ordinal) &&
                (encoded.Length == 0 || !line.Contains(encoded, StringComparison.Ordinal))), "Main Watcher retained live authentication/account values.");
        }
        private void ChangedAuthenticationSettings(Type helper, System.Reflection.PropertyInfo sourceProperty, byte[] bytes) {
            string hash = Convert.ToHexStringLower(MD5.HashData(bytes));
            sourceProperty.SetValue(null, new Func<MmcRuntimeAuthenticationKind, string, CancellationToken, Task<(ImmutableArray<string>, string)>>((_, _, _) => {
                Settings.Set("VersionServerNide", "changed-synthetic-id", instance);
                return Task.FromResult<(ImmutableArray<string>, string)>(([server.Url("/agent-nide")], hash));
            }));
            RunWithoutChild("authentication-setting-change", () => { }, () => Native.Check(plan.State == ModBase.LoadState.Failed && plan.Error?.ToString().Contains("认证方式或服务器设置", StringComparison.Ordinal) == true, "Changed authentication settings were not refused before process creation."));
        }
        private void CancelAgentPreparation(System.Reflection.PropertyInfo sourceProperty) {
            using ManualResetEventSlim entered = new(), release = new(); byte[] bytes = CompileAgent("cancel");
            server.Add("/agent-cancel", bytes, entered, release);
            sourceProperty.SetValue(null, new Func<MmcRuntimeAuthenticationKind, string, CancellationToken, Task<(ImmutableArray<string>, string)>>((_, _, _) =>
                Task.FromResult<(ImmutableArray<string>, string)>(([server.Url("/agent-cancel")], Convert.ToHexStringLower(MD5.HashData(bytes))))));
            var previousDownloads = ModNet.NetManager.Tasks.ToHashSet();
            try {
                RunWithoutChild("authentication-download-cancel", () => {
                    Until(() => entered.IsSet, "authentication agent transfer enters");
                    var download = ModNet.NetManager.Tasks.Single(loader => !previousDownloads.Contains(loader));
                    var file = download.Files.Single();
                    var prepare = plan.Loaders.OfType<ModLoader.LoaderTask<int, int>>().Single(loader => loader.Name == "准备 MMC 实例环境");
                    Thread thread = prepare.LastRunningThread ?? throw new IOException("Owned preparation did not retain its actual worker.");
                    Native.Check(thread.IsAlive && file.ActiveIoProducerCount > 0, "Held authentication HTTP did not exercise a live Main preparation and file producer.");
                    plan.Cancel();
                    Until(() => download.State == ModBase.LoadState.Canceled, "authentication download cancellation becomes terminal");
                    Console.WriteLine("DIAG authentication cancellation: combo=" + plan.State + "; download=" + download.State +
                        "; prepareAlive=" + thread.IsAlive + "; fileProducers=" + file.ActiveIoProducerCount +
                        "; loaderProducers=" + ((ILegacyNetworkDownload<ModNet.NetFile>)download).CoreRecord.ActiveIoProducerCount);
                    // The local producer is waiting for these deliberately held response
                    // headers. Release only after cancellation, then let the genuine Main
                    // Prepare worker finish its own downloader drain and workspace cleanup.
                    release.Set();
                },
                    () => Native.Check(plan.State == ModBase.LoadState.Canceled, "Authentication transfer cancellation did not complete before process creation."));
                foreach (var loader in ModNet.NetManager.Tasks) {
                    Native.Check(((ILegacyNetworkDownload<ModNet.NetFile>)loader).CoreRecord.ActiveIoProducerCount == 0 && loader.Files.All(file => file.ActiveIoProducerCount == 0), "Authentication cancellation returned with live download writers.");
                }
                string staging = Path.Combine(version, "PCL", "MmcRuntime", ".staging");
                Native.Check(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(), "Authentication canceled before its owned workspace drained and cleaned.");
            } finally { release.Set(); }
        }
        private void RunWithoutChild(string name, Action afterStart, Action verify) {
            ModLaunch.McLaunchProcess = null!; ModLaunch.McLaunchWatcher = null!; currentSession = Guid.Empty;
            var options = new ModLaunch.McLaunchOptions { Instance = instance };
            ModLaunch.CurrentLaunchOptions = options;
            var steps = MmcLaunch.CreateSteps(instance, options, java => ModLaunch.McLaunchJavaSelected = java);
            var operation = (LegacyLaunchOperation)Native.Static("PCL.ModLaunch", "CreateLaunchCombo", name, steps)!;
            plan = (ModLoader.LoaderCombo<object>)operation.Source; ModLaunch.McLaunchLoaderReal = plan; ModLoader.LoaderTaskbar.Add(plan); operation.Start(); afterStart();
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (plan.State == ModBase.LoadState.Loading) { Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Authentication pre-start operation did not finish."); Thread.Sleep(3); }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            // Canceled is observable before a non-interrupted task's finally blocks
            // return. Do not replace Main's drain with a test-side DrainAsync call.
            Thread? prepareThread = plan.Loaders.OfType<ModLoader.LoaderTask<int, int>>().Single(loader => loader.Name == "准备 MMC 实例环境").LastRunningThread;
            if (prepareThread is not null) Until(() => !prepareThread.IsAlive, "actual owned MMC preparation worker exit");
            Native.Check(currentSession == Guid.Empty && ModLaunch.McLaunchProcess is null, "Canceled/changed authentication preparation created a game process."); verify();
        }
        private byte[] CompileAgent(string name) {
            string source = Path.Combine(root, "agent-source", "fixture", "AuthenticationAgent.java"), classes = Path.Combine(root, "agent-classes");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.CreateDirectory(classes);
            File.WriteAllText(source, """
package fixture;
import java.lang.instrument.Instrumentation;
public final class AuthenticationAgent {
  public static void premain(String args, Instrumentation instrumentation) {
    System.out.println("AUTH_AGENT_STARTED");
    System.out.println("AUTH_SERVER=" + args);
    System.out.println("AUTH_METADATA=" + System.getProperty("authlibinjector.yggdrasil.prefetched", ""));
  }
}
""", new UTF8Encoding(false));
            var start = new ProcessStartInfo(javac) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "--release", "8", "-g:none", "-d", classes, source }) start.ArgumentList.Add(argument);
            foreach (string key in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(key);
            using var compiler = Process.Start(start)!; var stdout = compiler.StandardOutput.ReadToEndAsync(); var stderr = compiler.StandardError.ReadToEndAsync();
            if (!compiler.WaitForExit(20000)) { compiler.Kill(true); compiler.WaitForExit(5000); throw new TimeoutException("Controlled agent javac timed out."); }
            Native.Check(Task.WaitAll([stdout, stderr], 5000) && compiler.ExitCode == 0, "Controlled agent compilation failed.");
            using MemoryStream bytes = new(); using (var jar = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
                Add(jar, "META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\nPremain-Class: fixture.AuthenticationAgent\r\nFixture-Id: " + name + "\r\n\r\n"));
                Add(jar, "fixture/AuthenticationAgent.class", File.ReadAllBytes(Path.Combine(classes, "fixture", "AuthenticationAgent.class")));
            }
            return bytes.ToArray();
        }
        private static void AgentMetadataSources(Type helper) {
            async Task<(ImmutableArray<string> Urls, string Digest)> Resolve(MmcRuntimeAuthenticationKind kind, string json) {
                using var client = new HttpClient(new MetadataResponse(json));
                string server = kind == MmcRuntimeAuthenticationKind.Nide ? "synthetic-id" : "https://fixture.invalid/";
                return await ((Task<(ImmutableArray<string>, string)>)Native.Call(helper, "ResolveAgentSourceAsync", null, kind, server, client, CancellationToken.None)!).ConfigureAwait(false);
            }
            string digest = new('a', 64);
            var auth = Resolve(MmcRuntimeAuthenticationKind.AuthlibInjector, "{\"download_url\":\"https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/fixture/agent.jar\",\"checksums\":{\"sha256\":\"" + digest + "\"}}").GetAwaiter().GetResult();
            Native.Check(auth.Digest == digest && auth.Urls.SequenceEqual(new[] { "https://authlib-injector.yushi.moe/artifact/fixture/agent.jar", "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/fixture/agent.jar" }), "Authlib mirror source did not remain bound to existing official endpoints.");
            var nide = Resolve(MmcRuntimeAuthenticationKind.Nide, "{\"jarHash\":\"" + new string('b', 32) + "\"}").GetAwaiter().GetResult();
            Native.Check(nide.Urls.SequenceEqual(new[] { LegacyAuthenticationAgentSources.NideJar }), "Nide metadata selected an unknown source.");
            try { Resolve(MmcRuntimeAuthenticationKind.AuthlibInjector, "{\"download_url\":\"https://unknown.invalid/agent.jar?token=synthetic-secret\",\"checksums\":{\"sha256\":\"" + digest + "\"}}").GetAwaiter().GetResult(); throw new IOException("Unknown agent source was accepted."); }
            catch (MmcRuntimeLaunchException error) when (error.Code == "MMC_AUTHENTICATION_METADATA") { Native.Check(!error.ToString().Contains("synthetic-secret", StringComparison.Ordinal), "Authentication metadata error exposed source credentials."); }
        }
        private sealed class MetadataResponse(string json) : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
