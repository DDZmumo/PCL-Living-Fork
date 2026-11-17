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

// Each entry is a fresh, bounded actual-Main task scenario. Java probing is explicit in the online groups.
internal static class MmcPlatformOriginUiTaskChecks {
    internal static void RunAttach(FixtureEnvironment environment) => Attach(environment, "success");
    internal static void RunAttachPreviewCancel(FixtureEnvironment environment) => Attach(environment, "preview-cancel");
    internal static void RunAttachMetadataChange(FixtureEnvironment environment) => Attach(environment, "metadata-change");
    internal static void RunOnline(FixtureEnvironment environment) => MmcPlatformOriginUiTaskProcess.Run(environment, "success");
    internal static void RunOnlinePreviewCancel(FixtureEnvironment environment) => MmcPlatformOriginUiTaskProcess.Run(environment, "preview-cancel");
    internal static void RunOnlineMetadataChange(FixtureEnvironment environment) => MmcPlatformOriginUiTaskProcess.Run(environment, "metadata-change");
    internal static void RunOnlineDownloadCancel(FixtureEnvironment environment) => MmcPlatformOriginUiTaskProcess.Run(environment, "download-cancel");

    private static void Attach(FixtureEnvironment environment, string mode) {
        using var scope = new Scope(environment, "attach-" + mode, null);
        var before = scope.Data.Primary;
        scope.ConfirmOrigin = preview => { scope.AssertDialogUnlocked(); if (mode == "metadata-change") scope.Data.MetadataChanged("VersionA"); return mode == "preview-cancel" ? 2 : 1; };
        Native.Static("PCL.MmcPlatformOriginUi", "StartBinding", scope.Data.Owner, scope.Data.OriginalZip,
            ResourcePlatforms.Modrinth, "ProjectA", "VersionA", scope.Data.Catalog);
        scope.Wait(mode == "success" ? ModBase.LoadState.Finished : mode == "preview-cancel" ? ModBase.LoadState.Canceled : ModBase.LoadState.Failed);
        var after = scope.Data.Primary;
        Native.Check(scope.Data.Read("minecraft/saves/world/level.dat") == "personal world" && scope.Data.Read("minecraft/config/owned.cfg") == "author data", "Origin task changed player or author files.");
        if (mode == "success") {
            Native.Check(scope.Attached.Count == 1 && after.Definition.InstanceId == before.Definition.InstanceId && after.Definition.Generation == before.Definition.Generation + 1,
                "The actual Main origin task did not publish one same-ID source revision.");
            Native.Check(MmcInstancePlatformOriginBackend.ReadVerified(scope.Data.Selection())?.Identity.VersionId == "VersionA", "Actual Main attachment lacks fixed readable evidence.");
            Native.Check(scope.Attached.Single().UndoPoint is not null, "Actual Main attachment omitted its real undo point.");
        } else Native.Check(scope.Attached.Count == 0 && after.Sha256 == before.Sha256 && after.Definition.Origin.PlatformEvidenceReference is null,
            "Canceled/changed-metadata Main attachment crossed publication.");
        scope.Finish(); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-platform-attach-" + mode + ": actual Main Loader, explicit secondary file, unlocked dialogs and exact ZIP evidence; current files preserved; no JVM/game/network transfer.");
    }

    internal static Scope Online(FixtureEnvironment environment, string mode) {
        // Reuse the existing real downloader's whitelisted transport/thread instrumentation.
        Type networkType = typeof(MmcDownloadChecks).GetNestedType("Scope", BindingFlags.NonPublic)!;
        object network = Activator.CreateInstance(networkType, Native.Flags & ~BindingFlags.Static, null, [environment], null)!;
        var server = (PackRootInstallChecksLoopback)Native.Get(network, "Server")!;
        var scope = new Scope(environment, "online-" + mode, server, probeJava: mode != "download-cancel");
        scope.Network = network;
        scope.Data.SeedOrigin(); var before = scope.Data.Primary;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        string next = scope.Data.NewPackage(); byte[] bytes = File.ReadAllBytes(next);
        server.Add("/platform-next.zip", bytes, mode == "download-cancel" ? entered : null, mode == "download-cancel" ? release : null);
        scope.Data.Publish("VersionB", next, server.Url("/platform-next.zip"));
        scope.SelectRelease = values => values.Single(value => value.VersionId == "VersionB" && value.File.FileName == "VersionB.zip");
        scope.Choose = preview => {
            scope.SelectedPreview = preview; scope.AssertDialogUnlocked(preview);
            Native.Check(preview.PlatformRelease?.File.FileName == "VersionB.zip", "The online review lost the selected official file display descriptor.");
            if (mode == "preview-cancel") return null;
            return new([], preview.Files.Where(value => value.CanChoose).Select(value => new MmcPackageFileDecision(value.ChangeId, PackUpdateChoice.UseAuthor)).ToImmutableArray());
        };
        scope.ConfirmUpdate = preview => { scope.AssertDialogUnlocked(preview); if (mode == "metadata-change") scope.Data.MetadataChanged("VersionB"); return 1; };
        Native.Static("PCL.MmcPackageUpdateUi", "StartOnline", scope.Data.Owner, scope.Data.Catalog);
        if (mode == "download-cancel") {
            scope.Until(() => entered.IsSet && scope.Download is not null && scope.Download.Files.Any(file => file.ActiveIoProducerCount > 0), "real platform archive HTTP producer enters");
            scope.Current.Cancel();
            scope.Until(() => scope.Download!.State == ModBase.LoadState.Canceled, "real archive loader cancellation propagates");
            Native.Check(scope.Busy && scope.Download!.Files.Any(file => file.ActiveIoProducerCount > 0), "Terminal UI state released the origin/update fence before real HTTP producers drained.");
            release.Set();
        }
        scope.Wait(mode == "success" ? ModBase.LoadState.Finished : mode is "preview-cancel" or "download-cancel" ? ModBase.LoadState.Canceled : ModBase.LoadState.Failed);
        Native.Check(scope.Download is not null && server.Requested("/platform-next.zip"), "Actual Main online preparation bypassed the genuine NetFile HTTP batch.");
        foreach (var download in scope.Downloads) Native.Call(networkType, "NoWriters", network, download);
        Native.Check(scope.Data.Read("minecraft/saves/world/level.dat") == "personal world", "Actual online update changed player world bytes.");
        if (mode == "success") {
            Native.Check(scope.Updated.Count == 1 && scope.Updated[0].UndoPoint?.FullSafeEnvironment == true &&
                scope.Data.Primary.Definition.InstanceId == before.Definition.InstanceId && scope.Data.Primary.Definition.Generation == before.Definition.Generation + 1,
                "Actual Main online update omitted the existing same-ID/full-undo transaction.");
            Native.Check(MmcInstancePlatformOriginBackend.ReadVerified(scope.Data.Selection())?.Identity.VersionId == "VersionB" && scope.Data.Read("minecraft/config/owned.cfg") == "new author data",
                "Actual online publication retained the old release or failed to apply the reviewed author file.");
            Native.Check(scope.Discarded.Count == 1 && scope.Discarded[0].Status == MmcInstanceBackendStatus.Succeeded && !Directory.Exists(scope.SelectedPreview!.WorkingRoot),
                "Successful Main online update did not drain/discard its owned ZIP candidate.");
        } else {
            Native.Check(scope.Updated.Count == 0 && scope.Data.Primary.Sha256 == before.Sha256 && scope.Data.Read("minecraft/config/owned.cfg") == "author data",
                "Canceled/metadata-changed actual online task crossed publication.");
            if (mode == "preview-cancel") Native.Check(scope.Discarded.Count == 1 && !Directory.Exists(scope.SelectedPreview!.WorkingRoot), "Preview cancellation retained its discarded candidate.");
            if (mode == "metadata-change") Native.Check(scope.Discarded.Count == 0 && Directory.Exists(scope.SelectedPreview!.WorkingRoot), "Failed online commit deleted its retained recovery candidate.");
        }
        scope.Finish(); Native.Call(networkType, "Finish", network); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-platform-online-" + mode + ": actual Main Prepare/Select/Commit or refusal, genuine NetFile loopback HTTP and real producer drain; " +
            (mode == "download-cancel" ? "no Java probe needed; " : "actual installed Java probe; ") + "no game.");
        return scope; // Retain original LF manager/fixture guards until the nonce-bound parent terminates this owned process.
    }

    internal sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        internal readonly Data Data;
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly List<ModLoader.LoaderCombo<string>> tasks = [];
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action oldRefresh;
        private readonly JsonConfigProvider? oldProfile;
        private bool finished;
        internal object? Network;
        internal readonly List<MmcPlatformOriginResult> Attached = [];
        internal readonly List<MmcInstancePackageUpdateResult> Updated = [], Discarded = [];
        internal Func<MmcPlatformOriginPreview, int> ConfirmOrigin = _ => 1;
        internal Func<ImmutableArray<MmcPlatformReleaseDescriptor>, MmcPlatformReleaseDescriptor>? SelectRelease;
        internal Func<MmcInstancePackageUpdatePreview, MmcInstancePackageUpdateChoices?> Choose = _ => new([], []);
        internal Func<MmcInstancePackageUpdatePreview, int> ConfirmUpdate = _ => 1;
        internal MmcInstancePackageUpdatePreview? SelectedPreview;
        internal ModNet.LoaderDownload? Download;
        internal readonly List<ModNet.LoaderDownload> Downloads = [];
        internal ModLoader.LoaderCombo<string> Current => tasks.Last();
        internal bool Busy => (bool)Native.Property(Native.Type("PCL.MmcPackageUpdateUi"), "HasActiveOperation").GetValue(null)!;
        internal Scope(FixtureEnvironment environment, string name, PackRootInstallChecksLoopback? server, bool probeJava = false) {
            files = new(environment);
            if (probeJava) {
                oldProfile = ConfigProviders.Default; ConfigProviders.Default = new JsonConfigProvider(Path.Combine(files.Root, "platform-java.json"));
                Native.Check(ReferenceEquals(Configs.JavaList.Provider, ConfigProviders.Default), "Online fixture cannot capture an external Java configuration provider.");
                string executable = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA8") ?? throw new IOException("Set explicit PCLLF_MMC_TEST_JAVA8 before the actual online Java group.");
                Native.Check(Path.IsPathFullyQualified(executable) && File.Exists(executable), "The explicit actual Java8 executable is missing.");
                var java = new Java(Path.GetDirectoryName(executable)!);
                Native.Check(java.CheckAsync().GetAwaiter().GetResult() && java.Version?.Major == 8, "The actual installed Java probe is not Java8.");
                Configs.JavaList.Set(new ConcurrentList<Java>([java])); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            }
            Data = new(files, name, server);
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            oldRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            void Register(ModLoader.LoaderCombo<string> task) { tasks.Add(task); ModLoader.LoaderTaskbar.Add(task); }
            Set("PCL.MmcPlatformOriginUi", "RegisterTask", new Action<ModLoader.LoaderCombo<string>>(Register));
            Set("PCL.MmcPlatformOriginUi", "Published", new Action<MmcPlatformOriginResult>(Attached.Add));
            Set("PCL.MmcPlatformOriginUi", "SelectRelease", new Func<ImmutableArray<MmcPlatformReleaseDescriptor>, string, string, MmcPlatformReleaseDescriptor>((values, _, _) => {
                AssertDialogUnlocked(); return SelectRelease is null ? values.Single(value => value.File.FileName == "VersionA.zip") : SelectRelease(values);
            }));
            Set("PCL.MmcPlatformOriginUi", "ConfirmOrigin", new Func<MmcPlatformOriginPreview, int>(preview => ConfirmOrigin(preview)));
            Set("PCL.MmcPackageUpdateUi", "ServicesFactory", new Func<ModMinecraft.McInstance, MmcPackageUpdateServices>(_ => new() {
                Metadata = Data.Metadata, CacheRoots = Data.Caches, RegisterTask = Register, Published = Updated.Add,
                DownloadCreated = value => { Downloads.Add(value); Download = value; }, Discarded = value => { Native.Check(Busy, "Actual Main released busy before owned discard completed."); Discarded.Add(value); }
            }));
            Set("PCL.MmcPackageUpdateUi", "SelectChoices", new Func<MmcInstancePackageUpdatePreview, MmcInstancePackageUpdateChoices?>(preview => Choose(preview)));
            Set("PCL.MmcPackageUpdateUi", "ConfirmPreview", new Func<MmcInstancePackageUpdatePreview, int>(preview => ConfirmUpdate(preview)));
        }
        private void Set(string type, string name, object value) { var property = Native.Property(Native.Type(type), name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        internal void AssertDialogUnlocked(MmcInstancePackageUpdatePreview? preview = null) {
            Native.Check(System.Windows.Application.Current.Dispatcher.CheckAccess(), "A platform selection/confirmation did not reach the real UI Dispatcher.");
            var p = Data.Primary; using var lease = MmcAdmissionGate.Acquire(Data.Version, p.Definition.InstanceId, TimeSpan.Zero);
            object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, Data.Version)!;
            Native.Check((int)Native.Get(state, "PauseCount")! == 0, "A platform dialog retained configuration quiescence.");
            if (preview is null) return;
            string candidate = Path.Combine(preview.WorkingRoot, "versions", "candidate");
            using var candidateLease = MmcAdmissionGate.Acquire(candidate, Guid.NewGuid(), TimeSpan.Zero);
            string name = "Local\\PCLLF.MmcPackageUpdate." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.WorkingRoot.ToUpperInvariant())));
            using var mutex = new Mutex(false, name); Native.Check(mutex.WaitOne(TimeSpan.Zero), "The online review retained its actual candidate use mutex."); mutex.ReleaseMutex();
            object candidateState = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, candidate)!;
            Native.Check((int)Native.Get(candidateState, "PauseCount")! == 0, "The online review retained candidate configuration quiescence.");
        }
        internal void Until(Func<bool> condition, string phase) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (!condition()) { Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Platform task phase: " + phase + "; " + (tasks.Count == 0 ? "no task" : Current.Error)); Thread.Sleep(2); }
        }
        internal void Wait(ModBase.LoadState state) {
            Until(() => !Busy && Current.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting) && Native.Get(Current.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }, "real Main worker drain");
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, DateTime.UtcNow.AddSeconds(5));
            Native.Check(Current.State == state, "Actual platform task state=" + Current.State + ", expected=" + state + "; " + Current.Error);
        }
        internal void Finish() {
            Native.Check(!Busy && tasks.All(task => Native.Get(task.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }), "A platform Main task producer still owns the fixture.");
            InstanceConfigProviders.FlushAndDisposeWithin(files.Root); Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); finished = true;
        }
        public void Dispose() {
            if (!finished) Finish(); foreach (var pair in previous) pair.Key.SetValue(null, pair.Value); foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = oldRefresh; Data.Dispose(); if (oldProfile is not null) ConfigProviders.Default = oldProfile; files.Dispose();
        }
    }
    internal sealed class Data : IDisposable {
        internal readonly string Root, Game, Version, OriginalZip;
        internal readonly ModMinecraft.McInstance Owner;
        internal readonly MmcMetadataStore Metadata;
        internal readonly MmcArtifactCacheRoots Caches;
        internal readonly MmcPlatformOriginCatalog Catalog;
        private readonly HttpClient client = new(new NoNetwork());
        private readonly ConcurrentDictionary<string, JObject> versions = new(StringComparer.Ordinal);
        internal MmcStoredPrimaryDefinition Primary => MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(Version));
        internal Data(FileTaskScope files, string name, PackRootInstallChecksLoopback? server) {
            Root = Path.Combine(files.Root, name); Game = Path.Combine(Root, "minecraft"); Directory.CreateDirectory(Root); Directory.CreateDirectory(Game); files.SelectMinecraft(Game);
            Version = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, Root, Game)!; OriginalZip = Path.Combine(Root, "owned.zip");
            Owner = new ModMinecraft.McInstance(Version).Load();
            Metadata = new(Path.Combine(Root, "metadata"), new Uri("https://fixture.invalid/metadata/"), client);
            Caches = new(Path.Combine(Root, "cache", "libraries"), Path.Combine(Root, "cache", "assets"), Path.Combine(Root, "cache", "sources"));
            string origin = server is null ? "http://127.0.0.1:1" : new Uri(server.Url("/metadata/")).GetLeftPart(UriPartial.Authority);
            Catalog = MmcPlatformOriginCatalog.ForLoopbackFixtures((uri, token) => {
                token.ThrowIfCancellationRequested(); JToken result;
                if (uri.AbsolutePath == "/v2/project/ProjectA") result = new JObject { ["id"] = "ProjectA", ["project_type"] = "modpack" };
                else if (uri.AbsolutePath == "/v2/project/ProjectA/version") result = new JArray(versions.Values.Select(value => value.DeepClone()));
                else if (uri.AbsolutePath.StartsWith("/v2/version/", StringComparison.Ordinal) && versions.TryGetValue(uri.AbsolutePath[12..], out var versionData)) result = versionData.DeepClone();
                else throw new IOException("Unexpected exact owned platform request.");
                return Task.FromResult(result.ToString(Newtonsoft.Json.Formatting.None));
            }, MmcPlatformApiPolicy.ForLoopbackFixtures(new(origin + "/v2/"), new(origin + "/v1/")));
            Publish("VersionA", OriginalZip, origin + "/original.zip"); Put("minecraft/saves/world/level.dat", "personal world");
        }
        internal void Publish(string version, string archive, string url) {
            byte[] bytes = File.ReadAllBytes(archive);
            versions[version] = new JObject { ["id"] = version, ["project_id"] = "ProjectA", ["files"] = new JArray(
                new JObject { ["filename"] = "unselected-first.zip", ["url"] = new Uri(url).GetLeftPart(UriPartial.Authority) + "/not-selected.zip", ["size"] = 1,
                    ["hashes"] = new JObject { ["sha1"] = new string('1', 40), ["sha512"] = new string('1', 128) } },
                new JObject { ["filename"] = version + ".zip", ["url"] = url, ["size"] = bytes.Length,
                    ["hashes"] = new JObject { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(bytes)) } }) };
        }
        internal void MetadataChanged(string version) => versions[version]["files"]![1]!["size"] = versions[version]["files"]![1]!["size"]!.Value<long>() + 1;
        internal MmcPlatformInstanceSelection Selection() { var p = Primary; return new(Version, p.Definition.InstanceId, p.Definition.Generation, p.Sha256); }
        internal void SeedOrigin() {
            var backend = new MmcInstancePlatformOriginBackend(Catalog); var descriptor = Catalog.GetReleaseAsync(ResourcePlatforms.Modrinth, "ProjectA", "VersionA").GetAwaiter().GetResult().Single(value => value.File.FileName == "VersionA.zip");
            var preview = backend.PreviewAsync(new(Selection(), OriginalZip, descriptor)).GetAwaiter().GetResult(); Native.Check(preview.Status == MmcInstanceBackendStatus.Succeeded, "Cannot seed exact owned origin: " + preview.Failure);
            var result = backend.CommitAsync(new(preview.Preview!, preview.Preview!.CandidateSha256)).GetAwaiter().GetResult(); Native.Check(result.Status == MmcInstanceBackendStatus.Succeeded, "Cannot commit exact owned origin seed: " + result.Failure);
        }
        internal string NewPackage() {
            string path = Path.Combine(Root, "next.zip"); File.Copy(OriginalZip, path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update); Replace("libraries/owned.jar", "New inert author library; never executed."); Replace("minecraft/config/owned.cfg", "new author data"); return path;
            void Replace(string relative, string value) { zip.GetEntry(relative)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(relative).Open(), new UTF8Encoding(false)); writer.Write(value); }
        }
        internal string Read(string relative) => File.ReadAllText(Path.Combine(Version, relative.Replace('/', Path.DirectorySeparatorChar)));
        private void Put(string relative, string value) { string path = Path.Combine(Version, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        public void Dispose() => client.Dispose();
        private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Owned component originals require no external metadata."); }
    }
}
