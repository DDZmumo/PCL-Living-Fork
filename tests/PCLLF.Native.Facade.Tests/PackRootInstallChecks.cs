using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// LF f4/main50 ModModpack.vb HMCL + Pages/PageDownload/ModDownloadLib.vb.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 was read for same-source
// structure only. No CE policy/rule/installer is copied; LF remains authoritative.
internal static partial class PackRootInstallChecks
{
    internal const string Group = "pack-root-install";
    private static readonly string[] Required = ["real-root", "real-game-plan", "pack-bytes", "game-json", "client-jar", "actual-transport", "library-jar", "asset-index", "asset-object", "manifest", "ignore-removed", "existing-refusal", "duplicate-refusal", "cancel-partial", "actual-cancel-network", "actual-cancel", "cancel-cleanup", "failure-partial", "actual-failure", "failure-cleanup", "outside-preserved", "no-lease-leak", "producer-barrier", "exclusive-reopen", "loopback-stopped", "no-forbidden-io"];
    private static readonly HashSet<string> checkedIds = new(StringComparer.Ordinal);
    private static void Check(string id, bool condition) { Native.Check(condition, "HMCL original root-install contract " + id); checkedIds.Add(id); }
    private static PackRootInstallChecksScope? retainedScope;
    internal static void Run(FixtureEnvironment environment)
    {
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        int child = Array.IndexOf(arguments, ChildSwitch);
        if (child < 0) { RunOwnedChild(); return; }
        try {
            VerifyChild(arguments, environment);
            checkedIds.Clear();
            retainedScope = new(environment);
            PackRootInstallChecksCounter.Run(environment.Data);
            OrdinaryInstallCleanupChecks.Run(environment.Data);
            Scenarios(retainedScope);
            ModrinthIsolation(retainedScope);
            retainedScope.Finish();
            Check("producer-barrier", true);
            Check("actual-transport", retainedScope.TransportUsed);
            Check("exclusive-reopen", retainedScope.ExclusiveFiles());
            retainedScope.StopLoopback(); Check("loopback-stopped", true);
            environment.AssertNoForbiddenIo(); Check("no-forbidden-io", retainedScope.Forbidden == 0);
            Native.Check(checkedIds.SetEquals(Required), "HMCL root-install omitted: " + string.Join(',', Required.Except(checkedIds)));
            CompleteOwnedChild(environment, arguments[child + 1], retainedScope);
        } catch (Exception error) {
            // Network manager loops are genuine LF foreground process-lifetime
            // producers. Never unwind their live platform/settings guards.
            ModpackUpdateCleanup.TerminateChild("Pack root-install failed with all live context retained: " + error);
        }
    }
    private static void Scenarios(PackRootInstallChecksScope scope)
    {
        byte[] outside = Encoding.UTF8.GetBytes("unrelated-guid-sibling-must-survive");
        string sibling = Path.Combine(scope.Root, "unrelated.txt"); File.WriteAllBytes(sibling, outside);
        var success = scope.Case("RootSuccess-1.20.1");
        var root = scope.Start(success);
        scope.Until(() => root.State == ModBase.LoadState.Finished, "successful root");
        scope.Settle(root);
        Check("real-root", root.GetType().Assembly == Native.Main && root.Name == "HMCL 整合包安装：" + success.Name + " " && root.Loaders.Count == 2 && root.Loaders.All(loader => loader.State == ModBase.LoadState.Finished));
        var game = (ModLoader.LoaderCombo<string>)root.Loaders[1];
        Check("real-game-plan", game.Name == "游戏安装" && game.Loaders.Count >= 6 && game.Loaders.Any(loader => loader.Name == "安装游戏") && game.Loaders.Any(loader => loader.Name == "记录安装清单") && scope.InstallGameCalls == 1 && scope.LibraryAnalyses >= 2 && scope.AssetAnalyses == 1);
        string folder = success.Folder;
        Check("pack-bytes", File.ReadAllText(Path.Combine(folder,"options.txt")) == "gamma:1.0" && File.ReadAllText(Path.Combine(folder,"config","root.txt")) == "actual-hmcl-overrides" && ModBase.ReadIni(Path.Combine(folder,"PCL","Setup.ini"),"VersionArgumentIndieV2") == "True");
        JObject json = JObject.Parse(File.ReadAllText(Path.Combine(folder,success.Name+".json")));
        Check("game-json", json["id"]?.ToString() == success.Name && json["clientVersion"]?.ToString() == "1.20.1" && json["mainClass"]?.ToString() == "fixture.NotExecutableMain" && json["inheritsFrom"] is null && json["libraries"] is JArray { Count:1 });
        Check("client-jar", File.ReadAllBytes(Path.Combine(folder,success.Name+".jar")).SequenceEqual(scope.JarBytes));
        Check("library-jar", File.ReadAllBytes(scope.LibraryFile).SequenceEqual(scope.LibraryBytes));
        Check("asset-index", File.ReadAllBytes(scope.AssetIndexFile).SequenceEqual(scope.AssetIndexBytes));
        Check("asset-object", File.ReadAllBytes(scope.AssetObjectFile).SequenceEqual(scope.AssetBytes));
        var instance = new ModMinecraft.McInstance(folder).Load();
        McInstallManifest? manifest = Configs.McInstallManifest.Get(instance.Config);
        Check("manifest", manifest is not null && manifest.Components.MinecraftVersion == "1.20.1" && manifest.Components.ModLoaderVersions.Count == 0 && manifest.ManagedFiles.Count == 0 && instance.Version.VanillaName == "1.20.1");
        Check("ignore-removed", !File.Exists(Path.Combine(folder,".pclignore")));
        byte[] original = File.ReadAllBytes(Path.Combine(folder,success.Name+".json")); int starts = scope.Starts;
        Check("existing-refusal", Rejected(() => scope.Start(success)) is OperationCanceledException && scope.Starts == starts && original.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,success.Name+".json"))));
        // Existing-folder validation is intentionally earlier than taskbar
        // admission. Exercise the taskbar refusal while the REAL first root is
        // Loading/registered and its original scheduler has not written files.
        var duplicate = scope.Case("RootDuplicate-1.20.1", pauseAdmission:true); var duplicateRoot = scope.Start(duplicate);
        try {
            scope.AwaitAdmission(duplicate,duplicateRoot);
            starts=scope.Starts; int hints=scope.Hints.Count, prompts=scope.NamePrompts, installs=scope.InstallGameCalls;
            Check("duplicate-refusal", Rejected(() => scope.Start(duplicate)) is OperationCanceledException && scope.Starts == starts && scope.Hints.Any(hint => hint.Text == "该整合包正在安装中！" && hint.Red));
            Native.Check(scope.NamePrompts==prompts&&scope.InstallGameCalls==installs+1&&scope.Hints.Count==hints+1&&scope.Hints.Last()==("该整合包正在安装中！",true)&&duplicateRoot.State==ModBase.LoadState.Loading&&!Directory.Exists(duplicate.Folder),"Original duplicate must reach its actual taskbar hint after real game-plan construction, without a name prompt, second Start or target write.");
        } finally { scope.ReleaseAdmission(duplicate); }
        scope.Until(() => duplicateRoot.State == ModBase.LoadState.Finished,"released genuine duplicate-admission root installation"); scope.Settle(duplicateRoot);
        Native.Check(duplicateRoot.Loaders.All(loader=>loader.State==ModBase.LoadState.Finished)&&File.ReadAllText(Path.Combine(duplicate.Folder,"config","root.txt"))=="actual-hmcl-overrides"&&File.ReadAllBytes(Path.Combine(duplicate.Folder,duplicate.Name+".jar")).SequenceEqual(scope.JarBytes)&&JObject.Parse(File.ReadAllText(Path.Combine(duplicate.Folder,duplicate.Name+".json")))["clientVersion"]?.ToString()=="1.20.1"&&!File.Exists(Path.Combine(duplicate.Folder,".pclignore")),"Released original root/game workloads did not really finish after duplicate refusal.");
        var cancel = scope.Case("RootCancel-1.20.1", hold:true); var canceled = scope.Start(cancel);
        scope.Until(() => cancel.DownloadEntered.IsSet && canceled.Loaders[0].State == ModBase.LoadState.Finished, "pack written and real metadata HTTP in flight before cancellation");
        Check("cancel-partial", File.ReadAllText(Path.Combine(cancel.Folder,"config","root.txt")) == "actual-hmcl-overrides" && File.Exists(Path.Combine(cancel.Folder,".pclignore")));
        Check("actual-cancel-network", scope.CaptureHeldTransfer(cancel,canceled) && Volatile.Read(ref ModNet.NetTaskThreadCount)>0 && scope.AllNodes(canceled).OfType<ModNet.LoaderDownload>().Any(loader => loader.State == ModBase.LoadState.Loading));
        try { canceled.Cancel(); } finally { cancel.Release.Set(); }
        scope.Until(() => canceled.State == ModBase.LoadState.Canceled && !Directory.Exists(cancel.Folder), "actual cancellation cleanup"); scope.Settle(canceled);
        Check("actual-cancel", canceled.State == ModBase.LoadState.Canceled && scope.AllNodes(canceled).Any(loader => loader.State == ModBase.LoadState.Canceled));
        Check("cancel-cleanup", !Directory.Exists(cancel.Folder) && !File.Exists(Path.Combine(cancel.Folder,".pclignore")));
        var failure = scope.Case("RootFailure-1.20.1", hold:true, fail:true); var failed = scope.Start(failure);
        scope.Until(() => failure.MetadataEntered.IsSet && failed.Loaders[0].State == ModBase.LoadState.Finished, "pack written before actual failure");
        Check("failure-partial", File.ReadAllText(Path.Combine(failure.Folder,"config","root.txt")) == "actual-hmcl-overrides" && File.Exists(Path.Combine(failure.Folder,".pclignore"))); failure.Release.Set();
        scope.Until(() => failed.State == ModBase.LoadState.Failed && !Directory.Exists(failure.Folder), "actual failure cleanup"); scope.Settle(failed);
        Check("actual-failure", failed.State == ModBase.LoadState.Failed && scope.AllNodes(failed).Any(loader => ReferenceEquals(loader.Error, failure.Failure)) && scope.Messages.Any(message => message.Title.Contains("失败",StringComparison.Ordinal)));
        Check("failure-cleanup", !Directory.Exists(failure.Folder) && !File.Exists(Path.Combine(failure.Folder,".pclignore")));
        Check("outside-preserved", File.ReadAllBytes(sibling).SequenceEqual(outside) && File.ReadAllBytes(Path.Combine(folder,success.Name+".jar")).SequenceEqual(scope.JarBytes));
        // The original HMCL root starts LoaderCombo directly, without acquiring
        // a worker lease. Do not invent lease coverage that LF never provided.
        Check("no-lease-leak", !(bool)Native.Property(typeof(ModMinecraft.McInstanceLock),"IsAnyLocked").GetValue(null)!);
        Console.WriteLine("PASS HMCL root installation data chain: real pack+game combos, loopback metadata/client/library/assets, merge/config manifest, original refusal, real cancel/failure cleanup. No game/Java launch or UI-navigation completion claimed.");
    }
    private static Exception Rejected(Func<object?> action) { try { action(); } catch(Exception error) { return error; } throw new InvalidOperationException("Original install refusal was not observed."); }

    private static void ModrinthIsolation(PackRootInstallChecksScope scope)
    {
        PackRootInstallChecksScope.InstallCase Pack(string name, bool? isolated, JArray files, bool pause = false, bool badSetup = false, string? fabric = null) =>
            scope.Case(name, pauseAdmission: pause, contents: archive => {
                void Add(string path, string content) {
                    using var entry = archive.CreateEntry(path).Open();
                    entry.Write(Encoding.UTF8.GetBytes(content));
                }
                var dependencies = new JObject { ["minecraft"] = "1.20.1" };
                if (fabric is not null) dependencies["fabric-loader"] = fabric;
                Add("modrinth.index.json", new JObject {
                    ["formatVersion"] = 1, ["name"] = name,
                    ["dependencies"] = dependencies, ["files"] = files
                }.ToString());
                if (isolated is bool enabled) {
                    Add("overrides/PCL/Setup.ini", $"LivingExportVersionIsolation:{enabled}\r\nFixture:common\r\n");
                    Add("client-overrides/PCL/Setup.ini", $"VersionArgumentIndieV2:{!enabled}\r\nFixture:client\r\n");
                    Add("client-overrides/mods/priority.jar", "fixture client-layer priority");
                }
                if (badSetup) Add("overrides/PCL/Setup.ini/blocker.txt", "fixture directory prevents INI publication");
            });

        using var modEntered = new ManualResetEventSlim();
        using var modRelease = new ManualResetEventSlim();
        const string fabricVersion = "0.16.14";
        scope.AddFabricProfile(fabricVersion);
        var isolated = Pack("Modrinth-Isolated", true, new JArray(
            scope.PackFile("mods/held.jar", "/modrinth-held-mod", "fixture delayed isolated mod", modEntered, modRelease),
            scope.PackFile("mods/PRIORITY.jar", "/modrinth-priority", "fixture index priority")), fabric: fabricVersion);
        var isolatedRoot = scope.Start(isolated);
        try {
            scope.Until(() => modEntered.IsSet && isolated.MetadataEntered.IsSet && scope.FabricProfileRequested,
                "original game and Fabric metadata start while a Modrinth mod is downloading");
            string file = Path.Combine(isolated.Folder, "PCL", "Setup.ini");
            Native.Check(ModBase.ReadIni(file, "VersionArgumentIndie") == "1" && ModBase.ReadIni(file, "VersionArgumentIndieV2") == "True" &&
                ModBase.ReadIni(file, "IsStar") == "False" && ModBase.ReadIni(file, "Fixture") == "client" &&
                File.ReadAllText(Path.Combine(isolated.Folder, "mods", "priority.jar")) == "fixture client-layer priority" &&
                File.Exists(Path.Combine(isolated.Folder, ".pclignore")), "Game started with unverified isolation, inconsistent override order or no live install marker.");
        } finally { modRelease.Set(); }
        scope.Until(() => isolatedRoot.State == ModBase.LoadState.Finished, "isolated Modrinth import");
        scope.Settle(isolatedRoot);
        var fabricProfile = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Folder, isolated.Name + ".json")));
        Native.Check(PathUtils.IsSame(new ModMinecraft.McInstance(isolated.Folder).Load().PathIndie, isolated.Folder) &&
            fabricProfile["mainClass"]?.ToString() == "fixture.NotExecutableFabricMain" &&
            !File.Exists(Path.Combine(isolated.Folder, ".pclignore")), "Isolated Modrinth publication lost the Fabric profile or its own instance directory.");

        using var iniEntered = new ManualResetEventSlim();
        using var iniRelease = new ManualResetEventSlim();
        var shared = Pack("Modrinth-Shared", false, new JArray(
            scope.PackFile("pcl/setup.INI", "/modrinth-held-ini", "Fixture:index\r\nVersionArgumentIndieV2:True\r\n", iniEntered, iniRelease)));
        var sharedRoot = scope.Start(shared);
        try {
            scope.Until(() => iniEntered.IsSet, "Modrinth setup download holds the game chain");
            Native.Check(!shared.MetadataEntered.IsSet &&
                scope.AllNodes(sharedRoot).First(loader => loader.Name == "游戏安装").State == ModBase.LoadState.Waiting,
                "Game installation started before the final Setup.ini was available.");
        } finally { iniRelease.Set(); }
        scope.Until(() => sharedRoot.State == ModBase.LoadState.Finished, "shared Modrinth import");
        scope.Settle(sharedRoot);
        Native.Check(PathUtils.IsSame(new ModMinecraft.McInstance(shared.Folder).Load().PathIndie, scope.Game) &&
            ModBase.ReadIni(Path.Combine(shared.Folder, "PCL", "Setup.ini"), "Fixture") == "client" &&
            File.Exists(Path.Combine(scope.Game, "mods", "priority.jar")), "Shared Modrinth publication lost its client overrides or game root.");

        var defaults = Pack("Modrinth-Default", null, new JArray());
        var defaultRoot = scope.Start(defaults);
        scope.Until(() => defaultRoot.State == ModBase.LoadState.Finished, "Modrinth import without overrides");
        scope.Settle(defaultRoot);
        Native.Check(ModBase.ReadIni(Path.Combine(defaults.Folder, "PCL", "Setup.ini"), "VersionArgumentIndieV2") == "True",
            "A Modrinth pack without overrides lost default isolation.");

        var failed = Pack("Modrinth-SetupFailure", null, new JArray(), badSetup: true);
        var failedRoot = scope.Start(failed);
        scope.Until(() => failedRoot.State == ModBase.LoadState.Failed && !Directory.Exists(failed.Folder), "Modrinth setup write failure and owned cleanup");
        scope.Settle(failedRoot);
        Native.Check(!failed.MetadataEntered.IsSet, "A failed Modrinth configuration released the game chain.");

        using var cancelEntered = new ManualResetEventSlim();
        using var cancelRelease = new ManualResetEventSlim();
        var canceled = Pack("Modrinth-Cancel", true, new JArray(
            scope.PackFile("PCL/Setup.ini", "/modrinth-cancel-ini", "Fixture:cancel\r\n", cancelEntered, cancelRelease)));
        var canceledRoot = scope.Start(canceled);
        try {
            scope.Until(() => cancelEntered.IsSet, "Modrinth setup download in progress before cancellation");
            canceledRoot.Cancel();
        } finally { cancelRelease.Set(); }
        scope.Until(() => canceledRoot.State == ModBase.LoadState.Canceled && !Directory.Exists(canceled.Folder), "Modrinth cancellation and owned cleanup");
        scope.Settle(canceledRoot);
        Native.Check(!canceled.MetadataEntered.IsSet, "Canceling Modrinth setup started the game chain.");

        int completedCleanups = scope.CleanupCompletions;
        var occupied = Pack("Modrinth-Occupied", null, new JArray(), pause: true);
        var occupiedRoot = scope.Start(occupied);
        string sentinel = Path.Combine(occupied.Folder, "unrelated.txt");
        try {
            scope.AwaitAdmission(occupied, occupiedRoot, children: 5);
            Directory.CreateDirectory(occupied.Folder);
            File.WriteAllText(sentinel, "fixture competing owner");
        } finally { scope.ReleaseAdmission(occupied); }
        scope.Until(() => occupiedRoot.State == ModBase.LoadState.Failed && scope.CleanupCompletions > completedCleanups,
            "Modrinth same-directory refusal reaches safe cleanup");
        scope.Settle(occupiedRoot);
        Native.Check(!occupied.MetadataEntered.IsSet && File.ReadAllText(sentinel) == "fixture competing owner" &&
            Directory.GetFileSystemEntries(occupied.Folder).Length == 1, "Losing the directory claim wrote or removed another owner's files.");
        Console.WriteLine("PASS Modrinth root installation: real parallel downloads, isolation/overlay order, default mode, write failure, cancellation and directory ownership.");
    }
}
