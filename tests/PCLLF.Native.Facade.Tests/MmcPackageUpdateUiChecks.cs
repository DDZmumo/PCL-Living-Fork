using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Explicit installed-Java group. The actual Main Loader and production backend
// run throughout; the tiny ZIPs and the preverified empty asset index are owned
// fixture inputs. No replacement backend/loader/settings graph is compiled.
internal static class MmcPackageUpdateUiChecks {
    internal static void Run(FixtureEnvironment environment) => Execute(environment, scope => scope.ChoicesAndPublication(),
        "actual Main Prepare/Select/fixed-preview/Commit, declared Java8-to-21 probing, explicit component/file/disabled choices and complete undo");
    internal static void RunCancellationUnstarted(FixtureEnvironment environment) => Execute(environment, scope => scope.CancellationUnstarted(),
        "actual Main unstarted cancellation creates no producer or candidate and fences late Start");
    internal static void RunCancellationPreview(FixtureEnvironment environment) => Execute(environment, scope => scope.CancellationPreview(),
        "actual Main preview cancellation discards its owned candidate after drain and preserves a foreign neighbor");
    internal static void RunCancellationBeforeDecision(FixtureEnvironment environment) => Execute(environment, scope => scope.CancellationAtDecision(false),
        "actual Main predecision cancellation preserves the primary and drains its owned candidate");
    internal static void RunCancellationAfterDecision(FixtureEnvironment environment) => Execute(environment, scope => scope.CancellationAtDecision(true),
        "actual Main cancellation after the durable decision completes publication and owned discard");
    internal static void RunRefusalUnknownDependencies(FixtureEnvironment environment) => Execute(environment, scope => scope.Refusal("unknown"),
        "actual Main refuses unknown dependencies with acknowledgement defaulting to false");
    internal static void RunRefusalDefiniteError(FixtureEnvironment environment) => Execute(environment, scope => scope.Refusal("error"),
        "actual Main independently rejects definite errors after dependency acknowledgement");
    internal static void RunRefusalStalePreview(FixtureEnvironment environment) => Execute(environment, scope => scope.Refusal("stale"),
        "actual Main refuses a fixed preview made stale before commit");
    internal static void RunRefusalGameTreeRole(FixtureEnvironment environment) => Execute(environment, scope => scope.Refusal("layout"),
        "actual Main refuses a package game-tree-role change");
    internal static void RunWorldBackupSuccess(FixtureEnvironment environment) => Execute(environment, scope => scope.WorldBackup(false),
        "actual Main Minecraft-version change captures current world bytes through the real WorldBackupEngine");
    internal static void RunWorldBackupLocked(FixtureEnvironment environment) => Execute(environment, scope => scope.WorldBackup(true),
        "actual Main refuses a Minecraft-version change when the current world is locked");
    private static void Execute(FixtureEnvironment environment, Action<Scope> run, string evidence) {
        Stage("entry");
        using FileTaskScope files = new(environment);
        Stage("FileTaskScope ready");
        using var scope = new Scope(files);
        Stage("Scope ready; running selected scenario");
        run(scope); InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-package-update-ui: " + evidence + "; producers drained; no game launch.");
    }

    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcPackageUpdateUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly List<ModLoader.LoaderCombo<string>> tasks = [];
        private readonly List<MmcInstancePackageUpdateResult> published = [], discarded = [];
        private readonly HttpClient metadataClient = new(new NoNetwork());
        private readonly Stack<Action> restore = new();
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action oldRefresh;
        private Fixture? fixture;
        private Func<MmcInstancePackageUpdatePreview, MmcInstancePackageUpdateChoices?> choose = Preserve;
        private Func<MmcInstancePackageUpdatePreview, int> confirm = _ => 1;
        private Action<ModLoader.LoaderCombo<string>>? register;
        private Action<MmcTransactionCheckpoint>? checkpoint;
        private MmcInstancePackageUpdatePreview? selected;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;

        internal Scope(FileTaskScope files) {
            Stage("Scope constructor begin");
            this.files = files;
            var oldProfile = ConfigProviders.Default;
            ConfigProviders.Default = new JsonConfigProvider(Path.Combine(files.Root, "package-update-java.json"));
            restore.Push(() => ConfigProviders.Default = oldProfile);
            Native.Check(ReferenceEquals(Configs.JavaList.Provider, ConfigProviders.Default), "ZIP update fixture cannot touch an already captured external Java provider.");
            Stage("owned Java provider ready");
            Configs.JavaList.Set(new ConcurrentList<Java>([RealJava("PCLLF_MMC_TEST_JAVA8", 8), RealJava("PCLLF_MMC_TEST_JAVA21", 21)]));
            Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            oldRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("ServicesFactory", new Func<ModMinecraft.McInstance, MmcPackageUpdateServices>(_ => new() {
                Metadata = fixture!.Metadata, CacheRoots = fixture.Caches,
                RegisterTask = task => { tasks.Add(task); ModLoader.LoaderTaskbar.Add(task); register?.Invoke(task); },
                Published = result => { Stage("Published " + result.Status); published.Add(result); },
                Discarded = result => { Stage("Discarded " + result.Status); Native.Check(Busy, "Busy was released before actual discard returned."); discarded.Add(result); },
                DownloadCreated = _ => throw new IOException("Owned ZIP inputs must not acquire external runtime files."),
                TransactionObserved = (phase, _) => {
                    if (phase is MmcTransactionCheckpoint.BeforeDecision or MmcTransactionCheckpoint.CommitDecided) Stage("transaction " + phase);
                    checkpoint?.Invoke(phase);
                }
            }));
            Set("SelectChoices", new Func<MmcInstancePackageUpdatePreview, MmcInstancePackageUpdateChoices?>(preview => {
                Stage("enter choose"); AssertDialogUnlocked(preview); Stage("choose leases checked"); return choose(preview);
            }));
            Set("ConfirmPreview", new Func<MmcInstancePackageUpdatePreview, int>(preview => {
                Stage("enter final confirm"); AssertDialogUnlocked(preview); Stage("final confirm leases checked"); selected = preview; return confirm(preview);
            }));
            Set("ErrorObserved", new Action<Exception, string>((error, text) => throw new IOException(text, error)));
        }
        private void Set(string name, object? value) { var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        private static Java RealJava(string key, int major) {
            Stage("RealJava " + major + " begin");
            string executable = Environment.GetEnvironmentVariable(key) ?? throw new IOException("Set an explicit installed test Java: " + key);
            Native.Check(Path.IsPathFullyQualified(executable) && File.Exists(executable), "Installed Java is missing: " + key);
            var java = new Java(Path.GetDirectoryName(executable)!);
            Native.Check(java.CheckAsync().GetAwaiter().GetResult() && java.Version?.Major == major, "Actual installed Java major differs: " + key); Stage("RealJava " + major + " complete"); return java;
        }
        private void AssertDialogUnlocked(MmcInstancePackageUpdatePreview preview) {
            using var lease = MmcAdmissionGate.Acquire(preview.Selection.VersionRoot, preview.Selection.ExpectedInstanceId, TimeSpan.Zero);
            using var candidate = MmcAdmissionGate.Acquire(Path.Combine(preview.WorkingRoot, "versions", "candidate"), Guid.NewGuid(), TimeSpan.Zero);
            string name = "Local\\PCLLF.MmcPackageUpdate." + Hash(Encoding.UTF8.GetBytes(preview.WorkingRoot.ToUpperInvariant()));
            using var job = new Mutex(false, name); Native.Check(job.WaitOne(TimeSpan.Zero), "A user dialog retained its candidate mutex."); job.ReleaseMutex();
            object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, preview.Selection.VersionRoot)!;
            Native.Check((int)Native.Get(state, "PauseCount")! == 0, "A user dialog retained configuration quiescence.");
        }
        private Fixture New(string name, bool extraRoot = false) {
            Stage("New fixture " + name + " begin");
            published.Clear(); discarded.Clear(); selected = null; register = null; checkpoint = null; choose = Preserve; confirm = _ => 1;
            fixture = new(Path.Combine(files.Root, "package-update-ui", name), metadataClient, extraRoot); files.SelectMinecraft(fixture.Game);
            Stage("New fixture " + name + " complete"); return fixture;
        }
        private void Start(Fixture data, string zip) { Stage("Start Main worker"); Native.Static("PCL.MmcPackageUpdateUi", "Start", data.Owner(), zip); Stage("Start returned"); }
        private void Wait(ModBase.LoadState expected) {
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (Busy || Current.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting || Native.Get(Current.Loaders.Single(), "LastRunningThread") is Thread { IsAlive: true }) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual ZIP update worker did not drain: " + Current.Error); Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            Native.Check(Current.State == expected, "Actual ZIP task state differs: " + Current.State + " expected=" + expected + "; " + Current.Error);
            Stage("Main worker drained " + Current.State);
        }
        internal void CancellationUnstarted() {
            var unstarted = New("unstarted"); string zip = unstarted.Pack("B"); string before = unstarted.Primary.PrimarySha256!;
            register = task => task.Cancel(); Start(unstarted, zip); Wait(ModBase.LoadState.Canceled);
            Native.Check(Native.Get(Current.Loaders.Single(), "LastRunningThread") is null && unstarted.Primary.PrimarySha256 == before && !Directory.Exists(unstarted.Jobs), "Unstarted ZIP task created a producer or candidate.");
            Current.Loaders.Single().Start(unstarted.Version); Native.Check(Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Late Start escaped canceled ZIP fence.");
        }
        internal void CancellationPreview() {
            var canceled = New("preview-cancel"); string zip = canceled.Pack("B"); string before = canceled.Primary.PrimarySha256!;
            string neighbor = Path.Combine(canceled.Jobs, "unowned", "keep.txt"); Directory.CreateDirectory(Path.GetDirectoryName(neighbor)!); File.WriteAllText(neighbor, "unknown residual");
            string? owned = null; choose = preview => { owned = preview.WorkingRoot; return null; }; Start(canceled, zip); Wait(ModBase.LoadState.Canceled);
            Native.Check(owned is not null && !Directory.Exists(owned) && discarded.Single().Status == MmcInstanceBackendStatus.Succeeded && File.ReadAllText(neighbor) == "unknown residual" && canceled.Primary.PrimarySha256 == before,
                "Preview cancellation failed to discard only its own candidate after drain.");
        }
        internal void CancellationAtDecision(bool decided) {
            var data = New(decided ? "after-decision" : "before-decision"); string before = data.Primary.PrimarySha256!;
            checkpoint = phase => { if (phase == (decided ? MmcTransactionCheckpoint.CommitDecided : MmcTransactionCheckpoint.BeforeDecision)) Current.Cancel(); };
            Start(data, data.Pack("B")); Wait(decided ? ModBase.LoadState.Finished : ModBase.LoadState.Canceled);
            Native.Check((data.Primary.PrimarySha256 != before) == decided && published.Count == (decided ? 1 : 0) && discarded.Single().Status == MmcInstanceBackendStatus.Succeeded,
                "ZIP transaction cancellation crossed its durable decision boundary.");
        }
        internal void ChoicesAndPublication() {
            var data = New("choices", extraRoot: true); data.UserPatch("net.minecraftforge", "-Duser=keep"); data.UserPatch("fixture.extra", "-Dremoved=true"); Stage("two user patches complete");
            data.Put("minecraft/config/personal.cfg", "personal addition"); data.Put("minecraft/config/owned.cfg", "personal edit"); File.Delete(data.PathOf("minecraft/config/deleted.cfg"));
            File.Move(data.PathOf("minecraft/mods/disabled.jar"), data.PathOf("minecraft/mods/disabled.jar.disabled"));
            long generation = data.Primary.Definition!.Generation; string initialHash = "";
            choose = preview => {
                initialHash = preview.PreviewSha256;
                Native.Check(preview.Components.Any(value => value.Uid == "fixture.extra" && value.Removed && value.Conflict), "Removed overridden component was not presented.");
                object view = Activator.CreateInstance(Native.Type("PCL.MmcPackageUpdatePreviewView"), Native.Flags, null, [preview, true], null)!;
                Native.Check(!(bool)Native.Property(view.GetType(), "CanConfirm").GetValue(view)!, "Unchosen conflicts silently received an editable-view default.");
                return new(preview.Components.Where(value => value.HasUserOverride).Select(value => new MmcPackageComponentDecision(value.Uid, !value.Removed)).ToImmutableArray(),
                    preview.Files.Where(value => value.CanChoose).Select(value => new MmcPackageFileDecision(value.ChangeId,
                        value.Ownership == PackUpdateOwnership.Disabled ? PackUpdateChoice.UseAuthor : PackUpdateChoice.PreservePersonal, true)).ToImmutableArray());
            };
            Start(data, data.Pack("B", java: 21, includeMod: true)); Wait(ModBase.LoadState.Finished);
            var result = published.Single(); var after = data.Primary;
            Native.Check(selected is not null && selected.PreviewSha256 != initialHash && selected.Errors.IsEmpty && !selected.RequiresChoices && !selected.Choices.AllowUnverifiedDependencies,
                "Actual Select did not recalculate/fix a complete conservative preview.");
            Native.Check(after.Definition!.Generation == generation + 1 && after.Definition.InstalledProjection!.RuntimeEnvironment!.JavaMajor == 21 &&
                after.Definition.ComponentOverrides.Any(value => value.Uid == "net.minecraftforge") && !after.Definition.ComponentOverrides.Any(value => value.Uid == "fixture.extra"),
                "Published result lost the chosen component overrides, monotonic generation or newly declared/probed Java21.");
            Native.Check(data.Read("minecraft/config/owned.cfg") == "personal edit" && data.Read("minecraft/config/personal.cfg") == "personal addition" &&
                !File.Exists(data.PathOf("minecraft/config/deleted.cfg")) && File.Exists(data.PathOf("minecraft/mods/disabled.jar.disabled")) && !File.Exists(data.PathOf("minecraft/mods/disabled.jar")) &&
                data.Read("minecraft/config/author.cfg") == "author B" && data.Read("libraries/runtime.jar") == "runtime B", "Chosen author/personal/deleted/disabled file state differs.");
            var receipt = JObject.Parse(File.ReadAllText(Directory.GetFiles(data.PathOf("PCL/MmcPackageUpdateReceipts"), "*.json").Single()));
            Native.Check(selected is not null && receipt.Value<string>("PreviewSha256") == selected.PreviewSha256 && result.UndoPoint?.CoverageVersion == 5 && !Directory.Exists(selected.WorkingRoot), "Fixed final preview, complete undo or owned cleanup is missing.");
        }
        internal void Refusal(string kind) {
            var data = New(kind); string before = data.Primary.PrimarySha256!;
            if (kind == "unknown") data.Put("minecraft/mods/unknown.jar", "unrecognized personal mod");
            if (kind == "error") data.Put("libraries/runtime.jar", "unowned current library bytes");
            choose = preview => Preserve(preview) with { AllowUnverifiedDependencies = kind == "error" };
            confirm = preview => { if (kind == "stale") data.Put("minecraft/config/author.cfg", "edited after final preview"); return 1; };
            Start(data, data.Pack("B", leaf: kind == "layout" ? ".minecraft" : "minecraft")); Wait(ModBase.LoadState.Failed);
            Native.Check(data.Primary.PrimarySha256 == before && published.Count == 0, "A refused ZIP result changed its primary: " + kind);
            if (kind == "unknown") Native.Check(selected?.RequiresChoices == true && !selected.Choices.AllowUnverifiedDependencies, "Unknown dependencies were unconditionally acknowledged.");
            if (kind == "error") Native.Check(selected is { Errors.IsEmpty: false } && selected.Choices.AllowUnverifiedDependencies, "Definite errors were not independently rejected after acknowledgement.");
            if (selected is not null) Native.Check(Directory.Exists(selected.WorkingRoot) && discarded.Count == 0, "Failed ZIP update deleted its recoverable source candidate.");
        }
        internal void WorldBackup(bool fail) {
            var data = New(fail ? "world-backup-fails" : "world-backup-succeeds"); data.Put("minecraft/saves/world/level.dat", "current player world");
            string before = data.Primary.PrimarySha256!;
            using var session = fail ? new FileStream(data.PathOf("minecraft/saves/world/session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null;
            Start(data, data.Pack("B", minecraft: "1.7.11")); Wait(fail ? ModBase.LoadState.Failed : ModBase.LoadState.Finished);
            Native.Check(data.Read("minecraft/saves/world/level.dat") == "current player world" && (data.Primary.PrimarySha256 != before) == !fail, "World-backup refusal crossed publication or changed player bytes.");
            if (!fail) {
                string store = (string)Native.Static("PCL.WorldBackups", "Store", data.PathOf("minecraft"))!;
                string id = published.Single().WorldBackupIds.Single(); using var backup = ZipFile.OpenRead(Path.Combine(store, "points", id, "world.zip"));
                using var reader = new StreamReader(backup.GetEntry("level.dat")!.Open()); Native.Check(reader.ReadToEnd() == "current player world", "Host's real WorldBackupEngine did not capture actual current world bytes.");
            }
        }
        public void Dispose() {
            Native.Check(!Busy && tasks.All(task => Native.Get(task.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }), "Actual ZIP producer is still active; fixture guards retained.");
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = oldRefresh; metadataClient.Dispose(); while (restore.TryPop(out var action)) action();
        }
    }
    private sealed class Fixture {
        internal readonly string Root, Game, Version;
        internal readonly MmcMetadataStore Metadata;
        internal readonly MmcArtifactCacheRoots Caches;
        private static readonly byte[] Index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        internal string Jobs => Path.Combine(Game, "PCL", "MmcPackageUpdates");
        internal MmcInstanceInspection Primary => MmcInstanceInspector.Read(Version);
        internal Fixture(string root, HttpClient client, bool extraRoot) {
            Stage("production initial fixture import begin");
            Root = root; Game = Path.Combine(root, "minecraft"); Version = Path.Combine(Game, "versions", "managed"); Directory.CreateDirectory(Game);
            Metadata = new(Path.Combine(root, "metadata"), new Uri("https://fixture.invalid/meta/"), client);
            Caches = new(Path.Combine(root, "cache", "libraries"), Path.Combine(root, "cache", "assets"), Path.Combine(root, "cache", "sources"));
            string index = Path.Combine(Caches.AssetsRoot, "indexes", "fixture.json"); Directory.CreateDirectory(Path.GetDirectoryName(index)!); File.WriteAllBytes(index, Index);
            var backend = new MmcInstanceBackend<ModNet.NetFile>(Metadata, Caches, MmcDownloads.BuildFile, (batch, token) => MmcDownloads.RunBatchAsync(batch, token));
            var imported = backend.ImportAsync(new(Pack("A", extraRoot: extraRoot, includeMod: extraRoot), Version, "managed", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            Native.Check(imported.Status == MmcInstanceBackendStatus.Succeeded, "Owned initial production import failed: " + imported.Failure);
            Stage("production initial fixture import complete");
        }
        internal ModMinecraft.McInstance Owner() { var owner = new ModMinecraft.McInstance(Version).Load(); FileTaskScope.CacheInstance(owner); return owner; }
        internal string Pack(string tag, int java = 8, string minecraft = "1.7.10", string leaf = "minecraft", bool extraRoot = false, bool includeMod = false) {
            string path = Path.Combine(Root, tag + ".zip"); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var roots = new JArray(new JObject { ["uid"] = "net.minecraft", ["version"] = minecraft }, new JObject { ["uid"] = "net.minecraftforge", ["version"] = "10.13.4.1614" });
            if (extraRoot) roots.Add(new JObject { ["uid"] = "fixture.extra", ["version"] = "1" });
            Add("mmc-pack.json", new JObject { ["formatVersion"] = 1, ["components"] = roots }.ToString()); Add("instance.cfg", "name=Fixture\n");
            Add("patches/net.minecraft.json", new JObject { ["formatVersion"] = 1, ["uid"] = "net.minecraft", ["version"] = minecraft, ["mainClass"] = "fixture.Main",
                ["mainJar"] = new JObject { ["name"] = "fixture:runtime:1", ["MMC-hint"] = "local", ["MMC-filename"] = "runtime.jar" },
                ["assetIndex"] = new JObject { ["id"] = "fixture", ["url"] = "https://fixture.invalid/index", ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Index)), ["size"] = Index.Length }, ["compatibleJavaMajors"] = new JArray(java) }.ToString());
            Add("patches/net.minecraftforge.json", new JObject { ["formatVersion"] = 1, ["uid"] = "net.minecraftforge", ["version"] = "10.13.4.1614", ["+jvmArgs"] = new JArray("-Dauthor=" + tag) }.ToString());
            if (extraRoot) Add("patches/fixture.extra.json", """{"formatVersion":1,"uid":"fixture.extra","version":"1"}""");
            Add("libraries/runtime.jar", "runtime " + tag); Add(leaf + "/config/owned.cfg", "owned " + tag); Add(leaf + "/config/author.cfg", "author " + tag); Add(leaf + "/config/deleted.cfg", "deleted " + tag);
            if (includeMod) {
                using MemoryStream mod = new(); using (var archive = new ZipArchive(mod, ZipArchiveMode.Create, true)) {
                    using var writer = new StreamWriter(archive.CreateEntry("mcmod.info").Open(), new UTF8Encoding(false)); writer.Write(new JArray(new JObject { ["modid"] = "disabled", ["name"] = "disabled", ["version"] = tag, ["mcversion"] = minecraft }).ToString());
                }
                using var output = zip.CreateEntry(leaf + "/mods/disabled.jar").Open(); output.Write(mod.ToArray());
            }
            return path;
            void Add(string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value); }
        }
        internal void UserPatch(string uid, string argument) {
            Stage("UserPatch " + uid + " begin");
            var current = Primary; var record = current.Definition!.SourceBundle.MetadataRecords.Single(value => value.Kind == MmcSourceRecordKind.Component && value.Uid == uid);
            var patch = JObject.Parse(File.ReadAllText(PathOf(record.ObjectRelativePath))); patch["+jvmArgs"] = new JArray(argument);
            var backend = new MmcInstanceDefinitionChangeBackend<ModNet.NetFile>(Metadata, Caches, MmcDownloads.BuildFile, (batch, token) => MmcDownloads.RunBatchAsync(batch, token));
            var preview = backend.PreviewAsync(new(Version, current.Definition.InstanceId, current.Definition.Generation, current.PrimarySha256!, new("windows", "x86_64", 8), [new(uid, PatchContentUtf8: patch.ToString())])).GetAwaiter().GetResult();
            Native.Check(preview.Preview is not null, "Owned actual component override failed: " + preview.Failure);
            Stage("UserPatch " + uid + " preview ready");
            var result = backend.CommitAsync(new(preview.Preview!, preview.Preview!.CandidateSha256)).GetAwaiter().GetResult(); Native.Check(result.Status == MmcInstanceBackendStatus.Succeeded, "Actual user override publication failed: " + result.Failure);
            Stage("UserPatch " + uid + " commit complete");
        }
        internal string PathOf(string relative) => Path.Combine(Version, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Put(string relative, string value) { string path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value, new UTF8Encoding(false)); }
        internal string Read(string relative) => File.ReadAllText(PathOf(relative));
    }
    private static MmcInstancePackageUpdateChoices Preserve(MmcInstancePackageUpdatePreview preview) => new(
        preview.Components.Where(value => value.HasUserOverride).Select(value => new MmcPackageComponentDecision(value.Uid, !value.Removed)).ToImmutableArray(),
        preview.Files.Where(value => value.CanChoose).Select(value => new MmcPackageFileDecision(value.ChangeId, PackUpdateChoice.PreservePersonal, true)).ToImmutableArray());
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Stage(string value) { Console.WriteLine("DIAG mmc-package-update-ui " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + value); Console.Out.Flush(); }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Owned package-update fixture cannot access external metadata."); }
}
