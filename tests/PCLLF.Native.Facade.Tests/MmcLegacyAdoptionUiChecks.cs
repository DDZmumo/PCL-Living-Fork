using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using Newtonsoft.Json.Linq;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcLegacyAdoptionUiChecks {
    internal static void Run(FixtureEnvironment environment) {
        var originalProfile = ConfigProviders.Default;
        var profile = new JsonConfigProvider(Path.Combine(environment.Data, "legacy-adoption-profile.json")); ConfigProviders.Default = profile;
        Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile), "Actual Java catalog captured a profile outside this owned fixture.");
        using FileTaskScope files = new(environment);
        string root = Path.Combine(files.Root, "legacy-ui"), game = Path.Combine(root, "minecraft"), source = Path.Combine(game, "versions", "old"), target = Path.Combine(game, "versions", "managed-copy");
        Directory.CreateDirectory(source); files.SelectMinecraft(game);
        string recoveryOnly = Path.Combine(root, "recovery-only"); Directory.CreateDirectory(recoveryOnly);
        MmcInstanceCapabilities.RequireLegacyGameFolder(recoveryOnly, "普通目录清除检查");
        string privateStore = Path.Combine(recoveryOnly, "PCL", "MmcLegacyAdoption"); Directory.CreateDirectory(privateStore);
        File.WriteAllText(Path.Combine(privateStore, "keep.private"), "owned private recovery data");
        try { MmcInstanceCapabilities.RequireLegacyGameFolder(recoveryOnly, "清除游戏目录"); throw new Exception("A folder without versions lost its private recovery-store protection."); }
        catch (InvalidOperationException) { }
        var javaScope = typeof(MmcManagedImportChecks).GetNestedType("Scope", Native.Flags)!;
        var java = (Java)Native.Call(javaScope, "FindJava", null)!;
        Configs.JavaList.Set(new ConcurrentList<Java>([java])); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
        byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"); string indexPath = Path.Combine(game, "assets", "indexes", "legacy-ui.json"); Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!); File.WriteAllBytes(indexPath, index);
        string archive = Path.Combine(root, "original.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
            Add("mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}");
            Add("patches/net.minecraft.json", new JObject { ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Main", ["compatibleJavaMajors"] = new JArray(java.Version!.Major),
                ["mainJar"] = new JObject { ["name"] = "fixture:game:1", ["MMC-hint"] = "local", ["MMC-filename"] = "fixture.jar" },
                ["assetIndex"] = new JObject { ["id"] = "legacy-ui", ["url"] = "https://fixture.invalid/index.json", ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(index)), ["size"] = index.Length } }.ToString());
            Add("libraries/fixture.jar", "inert owned library; never executed"); Add("minecraft/config/user.cfg", "author config"); Add("minecraft/mods/deleted.jar", "author mod");
            void Add(string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)); writer.Write(content); }
        }
        Put("old.json", "{\"id\":\"old\",\"mainClass\":\"legacy.Main\",\"libraries\":[]}"); Put("PCL/config.json", "{\"UnknownPreference\":\"kept\"}");
        Put("PCL/Setup.ini", "VersionArgumentIndie:1\nVersionArgumentIndieV2:True\nVersionAdvanceJvm:\n"); Put("config/user.cfg", "personal config"); Put("saves/world/level.dat", "player progress");
        string before = Tree(source); var instance = new ModMinecraft.McInstance(source);
        var type = Native.Type("PCL.MmcLegacyAdoptionUi"); var factory = Native.Property(type, "ServicesFactory"); object? oldFactory = factory.GetValue(null);
        var scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action refresh = scheduler.RefreshExtraDownload;
        using HttpClient client = new(new NoNetwork()); ModLoader.LoaderCombo<string>? actual = null; MmcLegacyAdoptionResult? published = null;
        bool confirmed = false, decidedFault = false, cancelCopy = false; string selectedName = "managed-copy"; int uiThread = Environment.CurrentManagedThreadId;
        try {
            scheduler.RefreshExtraDownload = () => { };
            factory.SetValue(null, new Func<string, MmcLegacyAdoptionUiServices>(_ => new() {
                Metadata = new MmcMetadataStore(Path.Combine(root, "metadata"), new Uri("https://fixture.invalid/meta/"), client),
                CacheRoots = new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(root, "source-cache")),
                SelectArchive = _ => archive, SelectName = (_, _) => selectedName,
                ConfirmPreview = (preview, name) => {
                    Native.Check(Environment.CurrentManagedThreadId == uiThread && !preview.SourceCheck.HistoricalSourceVerified, "Main skipped the real old-source preview or fabricated provenance.");
                    var view = (StackPanel)Activator.CreateInstance(Native.Type("PCL.MmcLegacyAdoptionPreviewView"), Native.Flags, null, [preview, name], null)!;
                    Native.Check(view.Children.OfType<DataGrid>().Single().Items.Count == preview.SourceCheck.Differences.Length && preview.SourceCheck.Differences.Any(entry => entry.Kind == "个人快照中已删除"), "Actual Main preview lost the per-file difference table.");
                    confirmed = true; return MmcLegacyAdoptionIntent.CreateCopyWithExplicitSourceRebinding;
                },
                CheckpointObserved = (phase, _) => {
                    if (cancelCopy && phase == MmcLegacyAdoptionCheckpoint.PersonalSnapshotApplied) actual!.Cancel();
                    if (!cancelCopy && !decidedFault && phase == MmcLegacyAdoptionCheckpoint.CommitDecided) { decidedFault = true; throw new IOException("owned single decided-copy interruption"); }
                },
                RegisterTask = task => { actual = task; ModLoader.LoaderTaskbar.Add(task); }, Published = result => published = result,
                DownloadCreated = _ => throw new IOException("This cache-hit Main acceptance must not start network managers.")
            }));
            var previousPage = LauncherInstanceContext.Instance;
            try {
                LauncherInstanceContext.Instance = instance; var page = new PageInstanceOverall();
                Native.Call(page.GetType(), "RefreshMmcActions", page);
                var create = (FrameworkElement)page.FindName("BtnMmcLegacyAdoption");
                Native.Check(create.Visibility == Visibility.Visible &&
                    page.FindName("BtnMmcExport") is null && page.FindName("BtnMmcHistory") is null &&
                    page.FindName("BtnMmcMove") is null && page.FindName("BtnMmcPackageUpdate") is null &&
                    page.FindName("BtnMmcComponents") is null && page.FindName("BtnMmcPlatformOrigin") is null,
                    "An ordinary instance lost its explicit copy entry or retained duplicate overview buttons.");
                Native.Call(page.GetType(), "BtnMmcLegacyAdoption_Click", page, create, null);
            } finally { LauncherInstanceContext.Instance = previousPage; }
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (actual is null || actual.State == ModBase.LoadState.Loading || (bool)Native.Property(type, "HasActiveOperation").GetValue(null)!) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual managed-copy Main task did not finish."); Thread.Sleep(5);
            }
            Native.Check(confirmed && actual!.State == ModBase.LoadState.Finished && published is { Status: MmcInstanceBackendStatus.Succeeded } && Directory.Exists(target), "Actual Main adapter failed: " + actual?.Error);
            Native.Check(Tree(source) == before && MmcManagementProbe.Read(source).State == MmcManagementState.Legacy && MmcInstanceInspector.Read(target).IsConsistent &&
                File.ReadAllText(Path.Combine(target, "minecraft", "saves", "world", "level.dat")) == "player progress" && !File.Exists(Path.Combine(target, "minecraft", "mods", "deleted.jar")),
                "Main copied an inconsistent source, managed the old root or restored a user-deleted author file.");
            Native.Check(decidedFault, "The actual Main copy did not exercise automatic decided recovery.");
            cancelCopy = true; selectedName = "cancelled-copy"; published = null;
            Native.Static("PCL.MmcLegacyAdoptionUi", "PromptCreateCopy", instance);
            deadline = DateTime.UtcNow.AddSeconds(20);
            while (actual!.State == ModBase.LoadState.Loading || (bool)Native.Property(type, "HasActiveOperation").GetValue(null)!) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline); if (DateTime.UtcNow >= deadline) throw new TimeoutException("Automatic copy rollback did not drain."); Thread.Sleep(5);
            }
            var cancelled = MmcLegacyAdoptionCatalog.Read(game).Entries.Single(entry => entry.Name == selectedName);
            Native.Check(actual.State == ModBase.LoadState.Canceled && published is null && !Directory.Exists(Path.Combine(game, "versions", selectedName)) &&
                !cancelled.CanRecover && cancelled.Description.Contains("自动回退", StringComparison.Ordinal) && Tree(source) == before &&
                File.Exists(Path.Combine(cancelled.OperationDirectory, "backup.zip")) && !Directory.Exists(Path.Combine(cancelled.OperationDirectory, "private-minecraft", "versions", selectedName)),
                "An undecided copy required manual recovery, lost its private backup or changed the original player data: " + actual.Error + " / State=" + actual.State +
                " / Backend=" + ((ModLoader.LoaderTask<string, MmcLegacyAdoptionResult>)actual.Loaders.Single()).Output?.Failure + " / Operation=" + cancelled.OperationDirectory + " / Catalog=" + cancelled.Description);
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); InstanceConfigProviders.FlushAndDisposeWithin(root);
            Native.Check(Path.GetFullPath(source).StartsWith(Path.GetFullPath(files.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Owned legacy fixture cleanup escaped its root.");
            Directory.Delete(source, true); ModMinecraft.McInstanceList = [];
            var selection = new GlassInstanceSelectionPage(FileTaskShell.Install()); var controller = Native.Get(selection, "_controller")!;
            Native.Call(controller.GetType(), "Render", controller);
            Native.Check(selection.FindName("BtnMmcLegacyRecovery") is null && selection.FindName("BtnMmcImportRecovery") is null && selection.FindName("BtnMmcRecovery") is null &&
                ((FrameworkElement)selection.FindName("PanEmpty")).Visibility == Visibility.Visible, "The actual empty-list BAML retained a manual MMC recovery button.");
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS mmc-legacy-adoption-ui: actual Main copy/preview/Java/Loader automatically finishes a decided failure and rolls back an undecided cancellation; old source/worlds/deletions and backup preserved; actual empty-list BAML has no manual recovery buttons. No game launch.");
        } finally {
            Native.Check(actual is null || actual.State != ModBase.LoadState.Loading, "Refusing to detach active managed-copy fixture hooks.");
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); factory.SetValue(null, oldFactory); scheduler.RefreshExtraDownload = refresh; ConfigProviders.Default = originalProfile;
        }
        void Put(string relative, string contents) { string path = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents, new UTF8Encoding(false)); }
    }
    private static string Tree(string root) => string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
    private sealed class NoNetwork : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new IOException("No external metadata is permitted in the adoption UI fixture.");
    }
}
