using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Network;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLLF.Native.Facade.Tests;

/// <summary>Explicit opt-in retained import evidence. Never add to the default facade group table.</summary>
internal static class MmcGtnhAcceptance {
    private const string ParentFlag = "--mmc-gtnh-acceptance", ChildFlag = "--mmc-gtnh-acceptance-child";
    private const string OwnerName = "acceptance-owner.json", ReceiptName = "import-result.json";
    private static readonly string[] ManagerNames = ["NetManager ThreadStarter 0", "NetManager ThreadStarter 1", "NetManager StatRefresher"];
    private sealed record Sample(string Id, string Archive, string Sha256, int JavaMajor, string JavaBin);
    private static readonly Sample[] Samples = [
        new("java8", @"D:\User\Downloads\GT_New_Horizons_2.9.0_Java_8.zip", "ae94a01a5367065f71ac9b5150966182a5b55762c841749fcb3d603ab5ed78af", 8, @"C:\Program Files\Java\jre1.8.0_491\bin"),
        new("java21", @"D:\User\Downloads\GT_New_Horizons_2.9.0_Java_17-26.zip", "6d0ebe7e6be18fb74b4fc920916cc5710e142a3f885072d324da05244d398b16", 21, @"C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot\bin")
    ];
    private static FixtureEnvironment? retainedEnvironment;
    private static object? retainedScope;

    internal static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;
        if (MmcGtnhPostPublishChecks.TryRun(args, out exitCode)) return true;
        if (!args.Contains(ParentFlag, StringComparer.Ordinal) && !args.Contains(ChildFlag, StringComparer.Ordinal)) return false;
        try { exitCode = args.Contains(ChildFlag, StringComparer.Ordinal) ? RunChild(args) : RunParent(args); }
        catch (Exception error) { Console.Error.WriteLine("FAIL explicit GTNH import acceptance: " + error); exitCode = 1; }
        return true;
    }

    private static int RunParent(string[] args) {
        string? selected = Option(args, "--gtnh-case");
        Sample[] chosen = selected is null or "both" ? Samples : Samples.Where(x => x.Id == selected).ToArray();
        if (chosen.Length == 0) throw new ArgumentException("--gtnh-case must be java8, java21 or both.");
        string repository = FindRepository();
        string? reuseRun = Option(args, "--gtnh-reuse-cache-run");
        string acceptanceBase = Path.Combine(repository, "artifacts", "mmc-gtnh-acceptance");
        var cacheCases = chosen.ToDictionary(sample => sample.Id, sample => reuseRun is null ? null : OwnedCacheCase(reuseRun, sample.Id, acceptanceBase));
        string run = Path.Combine(repository, "artifacts", "mmc-gtnh-acceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, OwnerName), JsonSerializer.Serialize(new { SchemaVersion = 1, Kind = "PCLLF.GtnhImportAcceptance", Root = run, Keep = true, Samples = chosen.Select(x => x.Id) }));
        Console.WriteLine("RETAINED GTNH acceptance root: " + run);
        int failed = 0;
        foreach (Sample sample in chosen) {
            string caseRoot = Path.Combine(run, sample.Id); Directory.CreateDirectory(caseRoot);
            try { RunOwnedSample(sample, caseRoot, cacheCases[sample.Id]); }
            catch (Exception error) { failed++; File.WriteAllText(Path.Combine(caseRoot, "parent-error.txt"), error.ToString()); Console.Error.WriteLine("GTNH " + sample.Id + " import failed; evidence retained: " + error.Message); }
        }
        File.WriteAllText(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(new { SchemaVersion = 1, TestedPhase = "actual-managed-import-only", Root = run,
            Cases = chosen.Select(sample => new { sample.Id, Result = Path.Combine(run, sample.Id, ReceiptName) }), Failed = failed, GameLaunchTested = false }, JsonOptions));
        return failed == 0 ? 0 : 1;
    }

    private static void RunOwnedSample(Sample sample, string caseRoot, string? cacheCaseRoot) {
        if (!File.Exists(sample.Archive) || !File.Exists(Path.Combine(sample.JavaBin, "java.exe"))) throw new FileNotFoundException("Required existing sample/Java is absent; this runner does not download them.");
        string fixture = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N"));
        string nonce = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(fixture); Program.ValidateRoot(fixture);
        File.WriteAllText(Path.Combine(caseRoot, "fixture-location.json"), JsonSerializer.Serialize(new { SchemaVersion = 1, Root = fixture, CaseRoot = caseRoot, Nonce = nonce, Keep = true }));
        string output = Path.Combine(fixture, "output"); Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
        var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) {
            UseShellExecute = false, WorkingDirectory = fixture, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { ChildFlag, "--gtnh-case", sample.Id, "--case-root", caseRoot, "--nonce", nonce, "--isolated", "--fixture", fixture }) start.ArgumentList.Add(arg);
        if (cacheCaseRoot is not null) { start.ArgumentList.Add("--cache-case-root"); start.ArgumentList.Add(cacheCaseRoot); }
        using var child = Process.Start(start) ?? throw new IOException("GTNH acceptance child did not start.");
        Task stdout = Pump(child.StandardOutput, Path.Combine(caseRoot, "stdout.log"), sample.Id, false);
        Task stderr = Pump(child.StandardError, Path.Combine(caseRoot, "stderr.log"), sample.Id, true);
        var watch = Stopwatch.StartNew(); bool received = false; long reportedMinute = 0;
        try {
            string receipt = Path.Combine(caseRoot, ReceiptName);
            while (!File.Exists(receipt)) {
                if (child.WaitForExit(100)) throw new IOException("Owned GTNH child exited before its receipt: " + child.ExitCode);
                if ((long)watch.Elapsed.TotalMinutes > reportedMinute) {
                    reportedMinute = (long)watch.Elapsed.TotalMinutes;
                    Console.WriteLine("GTNH child still owned/alive: " + sample.Id + " elapsed=" + watch.Elapsed + "; waiting for actual result, not a timeout verdict.");
                }
            }
            using var document = JsonDocument.Parse(File.ReadAllText(receipt)); var root = document.RootElement;
            if (root.GetProperty("Nonce").GetString() != nonce || root.GetProperty("Pid").GetInt32() != child.Id || root.GetProperty("CaseRoot").GetString() != caseRoot)
                throw new IOException("GTNH receipt ownership mismatch.");
            received = true;
            if (root.GetProperty("Status").GetString() != "Succeeded") throw new IOException("GTNH managed import rejected: " + root.GetProperty("Error").GetString());
        } finally {
            // Only the process we started is ended. Neither artifact/game data nor the fixture is deleted.
            if (!child.HasExited) child.Kill(true);
            if (!child.WaitForExit(10000) || !Task.WaitAll([stdout, stderr], 10000)) throw new IOException("Owned GTNH child/output did not stop; retained without cleanup.");
            File.WriteAllText(Path.Combine(caseRoot, "child-ended.json"), JsonSerializer.Serialize(new { Pid = child.Id, Nonce = nonce, Received = received,
                ExitCode = child.ExitCode, ElapsedSeconds = watch.Elapsed.TotalSeconds, Keep = true }));
        }
    }

    private static int RunChild(string[] args) {
        Sample sample = Samples.Single(x => x.Id == Option(args, "--gtnh-case"));
        string caseRoot = Path.GetFullPath(Option(args, "--case-root") ?? throw new ArgumentException("Missing case root."));
        string fixture = Path.GetFullPath(Option(args, "--fixture") ?? throw new ArgumentException("Missing fixture root."));
        string nonce = Option(args, "--nonce") ?? throw new ArgumentException("Missing nonce.");
        Program.ValidateRoot(fixture);
        if (!Guid.TryParseExact(nonce, "N", out _) || !args.Contains("--isolated", StringComparer.Ordinal) || Native.Full(AppContext.BaseDirectory) != Native.Full(Path.Combine(fixture, "output")))
            throw new IOException("GTNH acceptance must run from its actual isolated output.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseRoot, "fixture-location.json")));
        if (marker.RootElement.GetProperty("Nonce").GetString() != nonce || marker.RootElement.GetProperty("Root").GetString() != fixture || marker.RootElement.GetProperty("CaseRoot").GetString() != caseRoot)
            throw new IOException("GTNH case ownership marker mismatch.");
        retainedEnvironment = new(fixture); ModpackUpdateCleanup.VerifyIsolatedChild();
        Scope? scope = null;
        try {
            string? cachedCase = Option(args, "--cache-case-root");
            if (cachedCase is not null) {
                string acceptanceBase = Directory.GetParent(Directory.GetParent(caseRoot)!.FullName)!.FullName;
                if (Native.Full(OwnedCacheCase(Directory.GetParent(cachedCase)!.FullName, sample.Id, acceptanceBase)) != Native.Full(cachedCase))
                    throw new IOException("Cache case does not belong to its retained acceptance run.");
            }
            scope = new(retainedEnvironment, sample, caseRoot, cachedCase); retainedScope = scope;
            scope.Import(); scope.WriteSuccess(nonce);
        } catch (Exception error) {
            WriteReceipt(caseRoot, new { SchemaVersion = 1, Status = "Failed", Nonce = nonce, Pid = Environment.ProcessId, CaseRoot = caseRoot,
                Sample = sample.Id, Error = error.ToString(), GameLaunchTested = false });
            Console.Error.WriteLine(error);
        }
        Console.Out.Flush(); Console.Error.Flush();
        // The genuine LF foreground managers and their complete platform/profile guards stay intact
        // until this exact parent has observed the receipt and ends only this owned process.
        using ManualResetEventSlim retainedLifetime = new(); retainedLifetime.Wait();
        return 1;
    }

    private sealed class Scope {
        private readonly FixtureEnvironment environment;
        private readonly Sample sample;
        private readonly string root, game, name, cacheRoot, cacheGame;
        private readonly ConcurrentBag<ModNet.LoaderDownload> downloads = new();
        private readonly ConcurrentBag<Thread> networkWorkers = new();
        private readonly Java[] installed;
        private readonly object progressGate = new();
        private readonly Dictionary<string, (long Completed, long Total, DateTime Reported)> progressByPhase = new(StringComparer.Ordinal);
        private MmcInstanceBackendResult? result;
        private ModLoader.LoaderCombo<string>? loader;
        private string packageDigest = "";
        internal Scope(FixtureEnvironment environment, Sample sample, string root, string? cacheCaseRoot) {
            this.environment = environment; this.sample = sample; this.root = root; game = Path.Combine(root, "game");
            cacheRoot = cacheCaseRoot ?? root; cacheGame = Path.Combine(cacheRoot, "game");
            name = "GTNH-2.9.0-" + sample.Id; Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(root, OwnerName), JsonSerializer.Serialize(new { Kind = "PCLLF.GtnhImportCase", Root = root, MinecraftRoot = game, Sample = sample.Id, Keep = true }));
            ModBase.RunInUi(); MmcGtnhPostPublishChecks.InstallEmptyLegacyAccountStore(); Native.Field(typeof(ModMinecraft), "_McFolderSelected").SetValue(null, game + Path.DirectorySeparatorChar);
            Native.Cache("ToolDownloadSource", 2); Native.Cache("SystemDebugSkipCopy", true);
            var profile = new JsonConfigProvider(Path.Combine(root, "private-profile.json")); ConfigProviders.Default = profile;
            Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile), "Acceptance Java settings captured a real player profile.");
            installed = Samples.Select(value => new Java(value.JavaBin)).ToArray();
            foreach (Java java in installed) Native.Check(java.CheckAsync().GetAwaiter().GetResult() && java.Version?.Major is 8 or 21, "Existing Java failed its actual launcher probe.");
            Native.Check(installed.Select(value => value.Version!.Major).Distinct().Count() == 2, "Acceptance needs actual Java 8 and 21.");
            Configs.JavaList.Set(new ConcurrentList<Java>(installed)); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            var host = (LegacyNetworkHost<ModNet.NetFile, ModNet.LoaderDownload, ModNet.NetManagerClass, ModLoader.LoaderBase>)Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
            host.TempFolder = () => Path.Combine(root, "network-temp") + Path.DirectorySeparatorChar;
            host.RequestTempFolder = () => Path.Combine(root, "network-work-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
            host.AppData = () => Path.Combine(root, "app-data") + Path.DirectorySeparatorChar; host.MinecraftFolders = () => [game + Path.DirectorySeparatorChar];
            var start = host.RunNewThread; host.RunNewThread = (work, title, priority) => { var thread = start(work, title, priority); networkWorkers.Add(thread); return thread; };
            var integer = host.IntegerSetting; host.IntegerSetting = key => key == "ToolDownloadThread" ? 4 : integer(key);
            object packHost = Native.Get(Native.Field(typeof(ModModpack), "Service").GetValue(null)!, "host")!;
            Native.Set(packHost, "InstallGame", new Func<string, string, McComponents, List<ModLoader.LoaderBase>>((_, _, _) => throw new IOException("GTNH managed import fell back to ordinary install.")));
            Native.Property(typeof(MmcInstances), "ServicesFactory").SetValue(null, new Func<ModpackMmcInstallRequest, MmcImportServices>(_ => new() {
                Metadata = MmcDownloads.CreateMetadataStore(Path.Combine(cacheRoot, "metadata-cache")),
                CacheRoots = new(Path.Combine(cacheGame, "libraries"), Path.Combine(cacheGame, "assets"), Path.Combine(cacheRoot, "source-cache"), Path.Combine(cacheGame, "versions")),
                SelectName = _ => name,
                RegisterTask = value => { loader = value; ModLoader.LoaderTaskbar.Add(value); },
                DownloadCreated = value => downloads.Add(value),
                Published = value => result = value,
                ProgressObserved = Report
            }));
        }
        internal void Import() {
            packageDigest = HashSource(sample.Archive);
            if (packageDigest != sample.Sha256) throw new IOException("The local ZIP differs from the pinned original sample; refusing to infer authenticity.");
            File.WriteAllText(Path.Combine(root, "source-and-java.json"), JsonSerializer.Serialize(new { sample.Id, sample.Archive, SizeBytes = new FileInfo(sample.Archive).Length,
                Sha256 = packageDigest, CacheSourceCaseRoot = cacheRoot, MainSha256 = MainHash(), MainMvid = Native.Main.ManifestModule.ModuleVersionId,
                Java = installed.Select(value => new { value.JavaExePath, Version = value.Version!.ToString() }) }, JsonOptions));
            loader = ModModpack.ModpackInstall(sample.Archive) ?? throw new IOException("Actual modpack dispatcher did not start an import.");
            while (loader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting ||
                loader.Loaders.OfType<ModLoader.LoaderTask<string, MmcInstanceBackendResult>>().Any(value => value.LastRunningThread?.IsAlive == true)) {
                PumpUiSlice();
                ReportDownloads();
                Thread.Sleep(100);
            }
            foreach (var download in downloads) MmcDownloads.DrainAsync(download).GetAwaiter().GetResult();
            if (loader.State != ModBase.LoadState.Finished || result?.Status != MmcInstanceBackendStatus.Succeeded)
                throw new IOException("Actual Main managed import did not succeed: " + loader.State + " " + loader.Error + " " + result?.Failure);
            while (ModMinecraft.McInstanceListLoader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting || ModMinecraft.McInstanceListLoader.LastRunningThread?.IsAlive == true) { PumpUiSlice(); Thread.Sleep(2); }
            MmcGtnhPostPublishChecks.DrainUi();
            Native.Check(ModMinecraft.McInstanceListLoader.State == ModBase.LoadState.Finished, "Published instance refresh failed: " + ModMinecraft.McInstanceListLoader.Error);
            InstanceConfigProviders.SaveAll(); environment.AssertNoForbiddenIo();
        }
        internal void WriteSuccess(string nonce) {
            var value = result!; var definition = value.Definition!; var plan = value.BoundPlan!.CompiledProfile; var paths = value.Paths!;
            var inspection = MmcInstanceInspector.Read(paths.InstanceRoot);
            if (!inspection.IsConsistent || plan.Environment.JavaMajor != sample.JavaMajor || !plan.JavaPolicy.Allows(sample.JavaMajor)) throw new IOException("Final managed definition/Java policy is inconsistent.");
            if (plan.Classpath.IsEmpty || plan.Classpath[^1].Key != plan.MainJar.Key) throw new IOException("Final mainJar/classpath order changed.");
            var active = value.BoundPlan.ActiveArtifactLocks.ToDictionary(item => item.ArtifactKey, StringComparer.OrdinalIgnoreCase);
            var libraries = plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts).Concat(plan.AuxiliaryArtifacts).DistinctBy(item => item.Key).Select(item => {
                var record = active[item.Key]; string path = paths.ResolveRelative(MmcPathRole.Instance, record.TargetRelativePath);
                string digest = HashSource(path);
                if (digest != record.AdoptedSha256) throw new IOException("Final runtime input differs from its adopted bytes: " + item.Key);
                if (item.Scope == MmcArtifactScope.Shared && !record.TargetRelativePath.StartsWith("PCL/MmcRuntime/", StringComparison.Ordinal)) throw new IOException("Shared input is not instance-owned.");
                return new { item.Key, Role = item.Role.ToString(), Scope = item.Scope.ToString(), item.RelativePath, record.TargetRelativePath, record.AdoptedSha256, record.SizeBytes };
            }).ToArray();
            var report = new { SchemaVersion = 1, Status = "Succeeded", Nonce = nonce, Pid = Environment.ProcessId, CaseRoot = root, Sample = sample.Id,
                Archive = sample.Archive, ArchiveSha256 = packageDigest, CacheSourceCaseRoot = cacheRoot, MainSha256 = MainHash(), MainMvid = Native.Main.ManifestModule.ModuleVersionId,
                InstanceRoot = paths.InstanceRoot, paths.GameRoot, paths.LocalLibraryRoot, paths.RuntimeRoot, definition.InstanceId, definition.Generation,
                plan.MainClass, MainJarKey = plan.MainJar.Key, Environment = plan.Environment, JavaPolicy = plan.JavaPolicy,
                SelectedJava = installed.Single(java => java.Version!.Major == sample.JavaMajor).JavaExePath,
                JvmArguments = plan.JvmArguments.Select(arg => arg.Value), GameArguments = plan.GameArguments.Select(arg => arg.Value), plan.Tweakers,
                Libraries = libraries, NativeFiles = definition.DerivedOutputs.Where(item => item.Kind == MmcDerivedOutputKind.NativeFile),
                AssetObjectCount = definition.DerivedOutputs.Count(item => item.Kind == MmcDerivedOutputKind.AssetObject),
                PackageFileCount = definition.PackageFileInventory.Length, ResultDiagnostics = value.Diagnostics,
                ImportTested = true, GameLaunchTested = false, RepairTested = false, Keep = true };
            WriteReceipt(root, report);
            Console.WriteLine("GTNH IMPORT SUCCEEDED " + sample.Id + " root=" + paths.InstanceRoot + " Java=" + plan.Environment.JavaMajor + " main=" + plan.MainClass);
        }
        private string HashSource(string path) {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] bytes = new byte[131072]; long completed = 0, reported = 0; int count;
            while ((count = input.Read(bytes)) != 0) { hash.AppendData(bytes, 0, count); completed += count; if (completed - reported >= 64L * 1024 * 1024) {
                Console.WriteLine("BYTES verify " + Path.GetFileName(path) + " " + completed + "/" + input.Length); reported = completed;
            } }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        private void Report(MmcInstanceBackendProgress value) {
            lock (progressGate) {
                DateTime now = DateTime.UtcNow;
                if (progressByPhase.TryGetValue(value.Phase, out var before)) {
                    // An unchanged downloader counter is not evidence of new transfer progress.
                    if (value.CompletedBytes == before.Completed && value.TotalBytes == before.Total) return;
                    if (value.CompletedBytes - before.Completed < 16L * 1024 * 1024 && now - before.Reported < TimeSpan.FromSeconds(2)) return;
                }
                progressByPhase[value.Phase] = (value.CompletedBytes, value.TotalBytes, now);
                Console.WriteLine("BYTES " + value.Phase + " " + value.CompletedBytes + "/" + (value.TotalBytes == 0 ? "unknown" : value.TotalBytes));
            }
        }
        private void ReportDownloads() {
            long completed = downloads.Sum(download => download.Files.Sum(file => Math.Max(0, Interlocked.Read(ref file.DownloadDone))));
            long total = downloads.Sum(download => download.Files.Sum(file => Math.Max(0, Interlocked.Read(ref file.FileSize))));
            Report(new("actual-netfile-transfers", completed, total));
        }
    }

    private static async Task Pump(StreamReader input, string file, string sample, bool error) {
        await using var output = new StreamWriter(file, false, new UTF8Encoding(false)) { AutoFlush = true };
        string? line; while ((line = await input.ReadLineAsync().ConfigureAwait(false)) is not null) { await output.WriteLineAsync(line).ConfigureAwait(false); (error ? Console.Error : Console.Out).WriteLine("[" + sample + "] " + line); }
    }
    private static void WriteReceipt<T>(string root, T value) {
        string temporary = Path.Combine(root, ReceiptName + ".pending");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, value, JsonOptions); file.Flush(true); }
        File.Move(temporary, Path.Combine(root, ReceiptName), false);
    }
    private static void PumpUiSlice() {
        // ContextIdle is useful for the real UI callbacks, but lack of idle is not an import failure.
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher; DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        EventHandler tick = (_, _) => frame.Continue = false;
        timer.Tick += tick;
        DispatcherOperation idle = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= tick; if (idle.Status == DispatcherOperationStatus.Pending) idle.Abort(); }
    }
    internal static string OwnedCacheCase(string runRoot, string sample, string acceptanceBase) {
        string run = Native.Full(runRoot), expectedBase = Native.Full(acceptanceBase);
        if (!SameOwnedPath(Directory.GetParent(run)?.FullName ?? "", expectedBase)) throw new IOException("Cache run must be a direct retained acceptance child.");
        string caseRoot = Path.Combine(run, sample);
        for (DirectoryInfo? directory = new(caseRoot); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Retained cache ancestors must not be reparse points.");
        using var owner = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, OwnerName)));
        using var caseOwner = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseRoot, OwnerName)));
        var runRecord = owner.RootElement; var caseRecord = caseOwner.RootElement;
        if (runRecord.GetProperty("Kind").GetString() != "PCLLF.GtnhImportAcceptance" || !SameOwnedPath(runRecord.GetProperty("Root").GetString()!, run) || !runRecord.GetProperty("Keep").GetBoolean() ||
            caseRecord.GetProperty("Kind").GetString() != "PCLLF.GtnhImportCase" || !SameOwnedPath(caseRecord.GetProperty("Root").GetString()!, caseRoot) ||
            !SameOwnedPath(caseRecord.GetProperty("MinecraftRoot").GetString()!, Path.Combine(caseRoot, "game")) || caseRecord.GetProperty("Sample").GetString() != sample || !caseRecord.GetProperty("Keep").GetBoolean())
            throw new IOException("Retained cache ownership marker mismatch.");
        return caseRoot;
    }
    private static bool SameOwnedPath(string left, string right) => string.Equals(Native.Full(left), Native.Full(right), StringComparison.OrdinalIgnoreCase);
    private static string MainHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Native.Main.Location)));
    private static string? Option(string[] args, string key) { int position = Array.IndexOf(args, key); return position < 0 ? null : position + 1 < args.Length ? args[position + 1] : throw new ArgumentException("Missing value: " + key); }
    internal static string FindRepository() { for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName; throw new IOException("Run the explicit acceptance from its repository build output."); }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
