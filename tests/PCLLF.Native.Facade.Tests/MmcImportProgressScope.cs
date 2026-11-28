using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

internal static partial class MmcImportRecoveryUiChecks {
    // Shared by the real Main progress/detail tests. No recovery catalog or manual retry service is installed.
    private sealed partial class Scope : IDisposable {
        private readonly FixtureEnvironment environment;
        private readonly FileTaskScope files;
        private readonly string root, game, archive;
        private readonly HttpClient client = new(new NoNetwork());
        private readonly MmcMetadataStore metadata;
        private readonly MmcArtifactCacheRoots cache;
        private readonly MmcRuntimeEnvironment runtime;
        private readonly JsonConfigProvider originalProfile;
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState> scheduler;
        private readonly Action oldRefresh;
        private readonly List<ModLoader.LoaderCombo<string>> tasks = [];
        private ModLoader.LoaderCombo<string>? current;

        internal Scope(FixtureEnvironment environment, string name) {
            this.environment = environment;
            originalProfile = ConfigProviders.Default;
            var profile = new JsonConfigProvider(Path.Combine(environment.Data, name + ".json")); ConfigProviders.Default = profile;
            Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile), "Progress Java catalog captured a profile outside its fresh fixture.");
            files = new(environment); root = Path.Combine(files.Root, name); game = Path.Combine(root, "minecraft"); files.SelectMinecraft(game);
            var javaScope = typeof(MmcManagedImportChecks).GetNestedType("Scope", Native.Flags)!;
            var java = (Java)Native.Call(javaScope, "FindJava", null)!;
            Configs.JavaList.Set(new ConcurrentList<Java>([java])); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            runtime = new("windows", "x86_64", java.Version!.Major);
            byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
            string indexPath = Path.Combine(game, "assets", "indexes", "initial-recovery.json"); Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!); File.WriteAllBytes(indexPath, index);
            archive = Path.Combine(root, "original.zip"); WriteZip(archive, "original author world", index);
            metadata = new(Path.Combine(root, "metadata"), new Uri("https://fixture.invalid/meta/"), client);
            cache = new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(root, "source-cache"), Path.Combine(game, "versions"));
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            oldRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
        }
        private void Until(Func<bool> ready, string message) {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!ready()) { Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException(message); Thread.Sleep(5); }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        }
        internal void Finish() {
            Native.Check(tasks.All(task => task.State != ModBase.LoadState.Loading && Native.Get(task.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }), "A progress fixture still has an actual producer.");
            InstanceConfigProviders.FlushAndDisposeWithin(files.Root); Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); environment.AssertNoForbiddenIo();
        }
        public void Dispose() {
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            Native.Check(tasks.All(task => task.State != ModBase.LoadState.Loading), "Refusing to detach live progress IO.");
            scheduler.RefreshExtraDownload = oldRefresh; client.Dispose(); ConfigProviders.Default = originalProfile; files.Dispose();
        }
        private void WriteZip(string path, string world, byte[] index) {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            Add("mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
            Add("patches/net.minecraft.json", new JObject { ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Main", ["compatibleJavaMajors"] = new JArray(runtime.JavaMajor),
                ["mainJar"] = new JObject { ["name"] = "fixture:game:1", ["MMC-hint"] = "local", ["MMC-filename"] = "main.jar" },
                ["assetIndex"] = new JObject { ["id"] = "initial-recovery", ["url"] = "https://fixture.invalid/index.json", ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(index)), ["size"] = index.Length } }.ToString());
            Add("libraries/main.jar", "inert owned fixture; never executed"); Add("minecraft/saves/author-world/level.dat", world);
            void Add(string name, string contents) { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)); writer.Write(contents); }
        }
    }
    private sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("Progress fixture cannot access external metadata."); }
}
