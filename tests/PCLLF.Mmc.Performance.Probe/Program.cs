// Original LF host probe; governed by the repository LICENCE.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PCLCS;
using PCLLF.MMCC.Core;

return await Probe.Run(args);

internal static class Probe {
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static async Task<int> Run(string[] args) {
        if (args.Length == 0 || args.Contains("--help")) {
            Console.WriteLine("Explicit owned-fixture probe: --authorize-owned-fixture --manifest FILE --case NAME --output NEW.json [--repetitions 1..10] [--diagnostic-split] [--cancel-after-ms N]. No JVM, repair, import, or downloads. Temporary owned workspaces are created and drained by the actual backend.");
            return 0;
        }
        string? output = null;
        try {
            if (!args.Contains("--authorize-owned-fixture", StringComparer.Ordinal)) throw new ArgumentException("Explicit owned fixture authorization is required.");
            string manifestPath = Full(Required(args, "--manifest"));
            var manifest = ReadJson<Manifest>(manifestPath);
            var fixture = manifest.Cases.Single(c => c.Name == Required(args, "--case"));
            output = Full(Required(args, "--output"));
            string artifacts = Path.Combine(Repository(), "artifacts");
            if (!Within(output, artifacts) || File.Exists(output)) throw new IOException("Output must be a new JSON file below this checkout's artifacts directory.");
            NoLinks(output);
            int repetitions = Number(args, "--repetitions", 1, 1, 10);
            int cancelAfter = Number(args, "--cancel-after-ms", 0, 0, 3_600_000);
            using var context = VerifyFixture(manifest, fixture);
            using var deniedNetwork = new DeniedNetwork();
            using var http = new HttpClient(deniedNetwork);
            int downloadBatches = 0, downloadRequests = 0;
            long requestedDownloadBytes = 0;
            var backend = new MmcInstanceBackend<MmcArtifactDownloadSpec>(
                new MmcMetadataStore(Path.Combine(context.Game, "PCL", "MmcCache", "metadata"), new Uri("https://performance.invalid/"), http),
                new(Path.Combine(context.Game, "libraries"), Path.Combine(context.Game, "assets"), Path.Combine(context.Game, "PCL", "MmcCache", "sources")),
                spec => spec, (files, _) => {
                    downloadBatches++; downloadRequests += files.Length;
                    requestedDownloadBytes += files.Sum(f => f.ExpectedSize ?? 0);
                    throw new IOException("Performance probe refuses a download request; incomplete inputs are not a successful baseline.");
                });
            List<object> runs = [];
            var host = new {
                SchemaVersion = 1, Kind = "PCLLF.MmcPerformanceProbe", StartedUtc = DateTime.UtcNow,
                ProcessId = Environment.ProcessId, Framework = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Probe = AssemblyIdentity(Assembly.GetExecutingAssembly()), Host = AssemblyIdentity(typeof(MmcInstancePaths).Assembly), Core = AssemblyIdentity(typeof(MmcPackageReader).Assembly),
                ManifestSha256 = Hash(manifestPath), fixture.Name, fixture.InstanceRoot, fixture.PackageSha256, fixture.InstanceId, fixture.Generation, fixture.PrimarySha256,
                Environment = context.Environment,
                Definition = "First iteration is process-cold (fresh process, ordinary OS cache); later iterations are same-process hot. No OS page-cache eviction. Preparation timing excludes fixture verification/inventory/reporting. Diagnostic stages are separate and warmed by preceding operations, and must not be added to production prepare time.",
                IoBoundary = "GetProcessIoCounters transfer bytes are process logical I/O, including cached I/O and DLL/runtime activity, not physical disk or bytes unique to MMC. Disk sizes are logical file lengths, not allocated/compressed sizes. Sampled staging peak can miss sub-interval transients."
            };
            for (int i = 0; i < repetitions; i++) {
                AssertAuthority(context, fixture);
                var before = Authority(context);
                var diskBefore = Inventory(context);
                var stagingBefore = Staging(context.Paths);
                List<Stage> stages = [];
                List<MmcInstanceBackendProgress> progress = [];
                List<PhasePoint> phasePoints = [];
                object progressGate = new();
                using var cancelled = new CancellationTokenSource();
                var start = Stopwatch.StartNew();
                double? cancelRequestedMs = null;
                var cancelTimer = cancelAfter == 0 ? null : new Timer(_ => { cancelRequestedMs = start.Elapsed.TotalMilliseconds; cancelled.Cancel(); }, null, cancelAfter, Timeout.Infinite);
                long stagingPeak = stagingBefore.Bytes;
                int diskSamples = 0;
                using var sampleStop = new CancellationTokenSource();
                var sampleTask = Task.Run(async () => {
                    while (!sampleStop.IsCancellationRequested) {
                        var sample = Staging(context.Paths); stagingPeak = Math.Max(stagingPeak, sample.Bytes); diskSamples++;
                        try { await Task.Delay(1000, sampleStop.Token); } catch (OperationCanceledException) { break; }
                    }
                });
                MmcInstancePrepareLaunchResult prepared;
                try {
                    prepared = await Measure("Backend.PrepareLaunch", stages, () => backend.PrepareLaunchAsync(new(fixture.InstanceRoot, context.Environment, fixture.InstanceId, fixture.Generation, RepairIncomplete: false) {
                        ExpectedPrimarySha256 = fixture.PrimarySha256
                    }, cancelled.Token, new InlineProgress<MmcInstanceBackendProgress>(p => {
                        lock (progressGate) {
                            progress.Add(p);
                            if (p.Phase.StartsWith("launch-", StringComparison.Ordinal) && (p.Phase.EndsWith("-start", StringComparison.Ordinal) || p.Phase.EndsWith("-complete", StringComparison.Ordinal))) {
                                using var process = Process.GetCurrentProcess();
                                phasePoints.Add(new(p.Phase, start.Elapsed.TotalMilliseconds, Counters.Capture(process)));
                            }
                        }
                    })));
                } finally {
                    if (cancelTimer is not null) await cancelTimer.DisposeAsync(); // Drain a racing cancellation callback before inspecting timing or disposing its CTS.
                    sampleStop.Cancel(); await sampleTask; // Drain monitoring and actual backend before examining or reporting results.
                }
                foreach (var complete in phasePoints.Where(p => p.Phase.EndsWith("-complete", StringComparison.Ordinal))) {
                    string phase = complete.Phase[..^9];
                    var begin = phasePoints.LastOrDefault(p => p.Phase == phase + "-start" && p.ElapsedMs <= complete.ElapsedMs);
                    if (begin is not null) stages.Add(Difference("BackendPhase." + phase, complete.ElapsedMs - begin.ElapsedMs, begin.Counters, complete.Counters));
                }
                double? cancellationDrainMs = cancelRequestedMs is { } requested ? start.Elapsed.TotalMilliseconds - requested : null;
                if (prepared.Snapshot is { } launch) {
                    await Measure("Final.ValidatePreparedLaunch", stages, () => {
                        using var lease = MmcAdmissionGate.Acquire(context.Paths.InstanceRoot, fixture.InstanceId, TimeSpan.Zero);
                        MmcInstanceBackend<MmcArtifactDownloadSpec>.ValidatePreparedLaunch(lease, launch);
                        return Task.FromResult(true);
                    });
                }
                if (args.Contains("--diagnostic-split") && prepared.Status == MmcInstanceBackendStatus.Succeeded) {
                    var primary = await Measure("Diagnostic.ReadPrimary", stages, () => Task.FromResult(MmcSavedSourceStore.ReadPrimary(context.Paths)));
                    var saved = await Measure("Diagnostic.SavedSourceStore.Load.Parse.Resolve", stages, () => MmcSavedSourceStore.LoadAsync(context.Paths, primary.Definition));
                    var compilation = await Measure("Diagnostic.Core.Compile", stages, () => Task.FromResult(MmcProfileCompiler.Compile(saved.Resolved.Resolution.Profile!, context.Environment)));
                    if (compilation.HasErrors || compilation.Profile?.RuntimePlanFingerprint != primary.Definition.InstalledProjection?.RuntimePlanFingerprint)
                        throw new IOException("Diagnostic compile did not reproduce the published runtime plan.");
                }
                var after = Authority(context);
                var diskAfter = Inventory(context);
                var stagingAfter = Staging(context.Paths);
                AssertAuthority(context, fixture);
                bool structureUnchanged = before["primary"] == after["primary"] && before["projection"] == after["projection"];
                bool preferencesChanged = before["config"] != after["config"] || before["ini"] != after["ini"];
                string[] changedRetainedTrees = diskBefore.Keys.Where(key => diskBefore[key] != diskAfter[key]).ToArray();
                bool drained = stagingBefore.Signature == stagingAfter.Signature;
                bool success = prepared.Status == MmcInstanceBackendStatus.Succeeded && !prepared.Repaired && prepared.Snapshot is not null && structureUnchanged && changedRetainedTrees.Length == 0 && drained && downloadBatches == 0 && deniedNetwork.Attempts == 0;
                runs.Add(new {
                    Iteration = i + 1, Temperature = i == 0 ? "process-cold_OS-cache-uncontrolled" : "same-process-hot_OS-cache-uncontrolled", Success = success,
                    Status = prepared.Status.ToString(), prepared.Repaired, prepared.Failure, Stages = stages,
                    AuthorityBefore = before, AuthorityAfter = after, StructuralAuthorityUnchanged = structureUnchanged, ProviderPreferencesChanged = preferencesChanged,
                    DiskBefore = diskBefore, DiskAfter = diskAfter, ChangedRetainedTrees = changedRetainedTrees,
                    SampledStagingPeakLogicalBytes = stagingPeak, StagingSamples = diskSamples, StagingSampleIntervalMs = 1000,
                    SampledOwnedPeakLogicalBytes = diskBefore.Values.Sum(v => v.Bytes) + Math.Max(0, stagingPeak - stagingBefore.Bytes),
                    StagingDrained = drained, StagingBefore = stagingBefore, StagingAfter = stagingAfter,
                    ProgressPhases = progress.GroupBy(p => p.Phase).Select(g => new { Phase = g.Key, Observations = g.Count(), MaximumCompletedBytes = g.Max(p => p.CompletedBytes) }),
                    PhaseBoundaryOffsetsMs = phasePoints.Select(p => new { p.Phase, p.ElapsedMs }),
                    PhaseOverlap = "BackendPhase.launch-artifact-scope contains materialization, preserved-check, binding, final validation and workspace disposal; its child phases overlap it. Do not add parent and children. Backend.PrepareLaunch includes all phases and returned cleanup.",
                    LaunchInputs = prepared.Snapshot is { } snapshot ? new { Count = snapshot.Files.Length, LogicalBytesIncludingDistinctPaths = snapshot.Files.Sum(f => f.SizeBytes), snapshot.BoundPlan.BoundFingerprint, snapshot.Directories.ViewKey } : null,
                    DownloadBatches = downloadBatches, DownloadRequests = downloadRequests, RequestedDownloadBytes = requestedDownloadBytes, DownloadedBytes = 0, MetadataNetworkAttempts = deniedNetwork.Attempts,
                    CancellationRequestedMs = cancelRequestedMs, CancellationDrainMs = cancellationDrainMs
                });
                Write(output, new { Host = host, Runs = runs });
                Console.WriteLine($"{fixture.Name} {i + 1}/{repetitions}: {prepared.Status}; prepare {stages[0].ElapsedMs:F1} ms; report {output}");
                if (!success) return cancelAfter > 0 && prepared.Status == MmcInstanceBackendStatus.Cancelled && drained && structureUnchanged ? 0 : 1;
            }
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Context VerifyFixture(Manifest manifest, Case fixture) {
        if (manifest.SchemaVersion != 1 || manifest.Kind != "PCLLF.MmcOwnedPerformanceManifest") throw new IOException("Invalid explicit performance manifest.");
        string root = Full(fixture.CaseRoot), allowed = Full(manifest.AuthorizedAcceptanceRoot);
        if (!Within(root, allowed) || Path.GetFileName(root) != fixture.Name || !Same(Path.GetDirectoryName(root)!, allowed)) throw new IOException("Fixture is outside the exact authorized acceptance root.");
        NoLinks(root);
        string ownerPath = Path.Combine(root, "acceptance-owner.json"), sourcePath = Path.Combine(root, "source-and-java.json"), importPath = Path.Combine(root, "import-result.json");
        if (Hash(ownerPath) != fixture.OwnerSha256 || Hash(sourcePath) != fixture.SourceRecordSha256 || Hash(importPath) != fixture.ImportRecordSha256) throw new IOException("Owned fixture evidence changed after registration.");
        using var owner = ReadDocument(ownerPath); using var source = ReadDocument(sourcePath); using var imported = ReadDocument(importPath);
        string game = Path.Combine(root, "game");
        if (owner.RootElement.GetProperty("Kind").GetString() != "PCLLF.GtnhImportCase" || !owner.RootElement.GetProperty("Keep").GetBoolean() ||
            !Same(owner.RootElement.GetProperty("Root").GetString()!, root) || !Same(owner.RootElement.GetProperty("MinecraftRoot").GetString()!, game) || owner.RootElement.GetProperty("Sample").GetString() != fixture.Name ||
            source.RootElement.GetProperty("Sha256").GetString() != fixture.PackageSha256 || imported.RootElement.GetProperty("Status").GetString() != "Succeeded" ||
            imported.RootElement.GetProperty("InstanceId").GetGuid() != fixture.InstanceId || !Same(imported.RootElement.GetProperty("InstanceRoot").GetString()!, fixture.InstanceRoot)) throw new IOException("Fixture owner/import/source identities disagree.");
        if (!Within(fixture.InstanceRoot, Path.Combine(game, "versions")) || !Same(Path.GetDirectoryName(Full(fixture.InstanceRoot))!, Path.Combine(game, "versions"))) throw new IOException("Instance must be a direct owned versions child.");
        var environment = imported.RootElement.GetProperty("Environment").Deserialize<MmcRuntimeEnvironment>(Json) ?? throw new IOException("Published runtime environment is absent.");
        var primary = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(fixture.InstanceRoot));
        var paths = MmcInstancePaths.Resolve(fixture.InstanceRoot, primary.Definition.Layout.GameRootLeaf);
        var pins = new OriginalPins();
        var context = new Context(root, game, paths, environment, pins);
        try {
        AssertAuthority(context, fixture);
        using (var lease = MmcAdmissionGate.Acquire(fixture.InstanceRoot, fixture.InstanceId, TimeSpan.Zero)) {
            MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        }
        // Existence-only preflight avoids warming all content. The actual prepare retains every content hash check.
        foreach (var item in MmcPublishedArtifactLocks.Create(primary.Definition)) pins.Add(paths.ResolveRelative(MmcPathRole.Instance, item.RecoveryObjectRelativePath));
        return context;
        } catch { pins.Dispose(); throw; }
    }
    private static void AssertAuthority(Context context, Case fixture) {
        var primary = MmcSavedSourceStore.ReadPrimary(context.Paths);
        if (primary.Sha256 != fixture.PrimarySha256 || primary.Definition.InstanceId != fixture.InstanceId || primary.Definition.Generation != fixture.Generation || primary.Definition.Origin.PackageSha256 != fixture.PackageSha256)
            throw new IOException("The selected fixed instance authority changed; no baseline was accepted.");
    }
    private static Dictionary<string, string?> Authority(Context context) => new() {
        ["primary"] = Hash(Path.Combine(context.Paths.InstanceRoot, "PCL", "InstanceDefinition.json")),
        ["projection"] = OptionalHash(Path.Combine(context.Paths.InstanceRoot, Path.GetFileName(context.Paths.InstanceRoot) + ".json")),
        ["config"] = OptionalHash(Path.Combine(context.Paths.InstanceRoot, "PCL", "config.json")),
        ["ini"] = OptionalHash(Path.Combine(context.Paths.InstanceRoot, "PCL", "Setup.ini"))
    };
    private static Dictionary<string, Disk> Inventory(Context context) {
        Dictionary<string, Disk> result = [];
        foreach (string relative in new[] { "PCL/MmcSource", "PCL/MmcArtifacts", "PCL/MmcRuntime", "libraries" }) result["instance/" + relative] = Tree(context.Paths.ResolveRelative(MmcPathRole.Instance, relative));
        foreach (string relative in new[] { "libraries", "assets", "PCL/MmcCache" }) result["shared/" + relative] = Tree(Path.Combine(context.Game, relative));
        return result;
    }
    private static Disk Staging(MmcInstancePaths paths) => Tree(Path.Combine(paths.RuntimeRoot, ".staging"));
    private static Disk Tree(string root) {
        NoLinks(root);
        if (!Directory.Exists(root)) return new(0, 0, "missing");
        List<string> records = []; long bytes = 0; int files = 0;
        Stack<string> pending = new(); pending.Push(root);
        while (pending.TryPop(out string? directory)) {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); } catch (DirectoryNotFoundException) { continue; }
            foreach (string entry in entries) {
                try {
                    NoLinks(entry); var info = new FileInfo(entry);
                    if ((info.Attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    bytes = checked(bytes + info.Length); files++;
                    records.Add(Path.GetRelativePath(root, entry).Replace('\\', '/') + "\t" + info.Length.ToString(CultureInfo.InvariantCulture) + "\t" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            }
        }
        records.Sort(StringComparer.Ordinal);
        return new(files, bytes, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', records)))));
    }
    private static async Task<T> Measure<T>(string name, List<Stage> stages, Func<Task<T>> work) {
        using var process = Process.GetCurrentProcess(); var before = Counters.Capture(process); var watch = Stopwatch.StartNew();
        try { return await work(); }
        finally { var after = Counters.Capture(process); stages.Add(Difference(name, watch.Elapsed.TotalMilliseconds, before, after)); }
    }
    private static Stage Difference(string name, double elapsedMs, Counters before, Counters after) => new(name, elapsedMs, after.CpuMs - before.CpuMs, after.AllocatedBytes - before.AllocatedBytes,
            after.ReadBytes - before.ReadBytes, after.WriteBytes - before.WriteBytes, after.ReadOperations - before.ReadOperations, after.WriteOperations - before.WriteOperations,
            before.WorkingSet, after.WorkingSet, after.PeakWorkingSet, after.PrivateBytes);
    private static object AssemblyIdentity(Assembly assembly) => new { assembly.FullName, Sha256 = Hash(assembly.Location), Mvid = assembly.ManifestModule.ModuleVersionId };
    private static T ReadJson<T>(string path) { using var document = ReadDocument(path); return document.RootElement.Deserialize<T>(Json) ?? throw new IOException("Empty JSON record."); }
    private static JsonDocument ReadDocument(string path) { NoLinks(path); using var input = File.OpenRead(path); if (input.Length > 16 * 1024 * 1024) throw new IOException("Probe evidence exceeds its bounded metadata size."); return JsonDocument.Parse(input); }
    private static string? OptionalHash(string path) => File.Exists(path) ? Hash(path) : null;
    private static string Hash(string path) { NoLinks(path); using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private static void NoLinks(string path) { for (string? current = Full(path); current is not null; current = Path.GetDirectoryName(current)) { try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Probe boundary contains a link."); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } } }
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string root) => Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Required(string[] args, string key) { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : throw new ArgumentException("Missing " + key); }
    private static int Number(string[] args, string key, int fallback, int minimum, int maximum) { int value = args.Contains(key) ? int.Parse(Required(args, key), CultureInfo.InvariantCulture) : fallback; return value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(key); }
    private static string Repository() { for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent) if (Directory.Exists(Path.Combine(current.FullName, "PCLLF.MMCC.Core")) && Directory.Exists(Path.Combine(current.FullName, "modern"))) return current.FullName; throw new IOException("Run from the built checkout output."); }
    private static void Write(string output, object value) { NoLinks(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!); string pending = output + ".pending"; using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); } File.Move(pending, output, true); }
    private sealed class DeniedNetwork : HttpMessageHandler { internal int Attempts; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Interlocked.Increment(ref Attempts); throw new IOException("Metadata network is forbidden during a retained-fixture probe."); } }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class OriginalPins : IDisposable {
        private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
        internal void Add(string path) { NoLinks(path); if (!files.ContainsKey(path)) files.Add(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
        public void Dispose() { foreach (var stream in files.Values) stream.Dispose(); files.Clear(); }
    }
    private sealed record Context(string Root, string Game, MmcInstancePaths Paths, MmcRuntimeEnvironment Environment, OriginalPins Pins) : IDisposable { public void Dispose() => Pins.Dispose(); }
    private sealed record Manifest(int SchemaVersion, string Kind, string AuthorizedAcceptanceRoot, Case[] Cases);
    private sealed record Case(string Name, string CaseRoot, string OwnerSha256, string SourceRecordSha256, string ImportRecordSha256, string PackageSha256, string InstanceRoot, Guid InstanceId, long Generation, string PrimarySha256);
    private sealed record Disk(int Files, long Bytes, string Signature);
    private sealed record PhasePoint(string Phase, double ElapsedMs, Counters Counters);
    private sealed record Stage(string Name, double ElapsedMs, double CpuMs, long AllocatedBytes, ulong ReadTransferBytes, ulong WriteTransferBytes, ulong ReadOperations, ulong WriteOperations, long WorkingSetStart, long WorkingSetEnd, long ProcessPeakWorkingSet, long PrivateBytesEnd);
    private sealed record Counters(double CpuMs, long AllocatedBytes, ulong ReadBytes, ulong WriteBytes, ulong ReadOperations, ulong WriteOperations, long WorkingSet, long PeakWorkingSet, long PrivateBytes) {
        internal static Counters Capture(Process process) { process.Refresh(); if (!GetProcessIoCounters(process.Handle, out var io)) throw new System.ComponentModel.Win32Exception(); return new(process.TotalProcessorTime.TotalMilliseconds, GC.GetTotalAllocatedBytes(false), io.ReadTransferCount, io.WriteTransferCount, io.ReadOperationCount, io.WriteOperationCount, process.WorkingSet64, process.PeakWorkingSet64, process.PrivateMemorySize64); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
