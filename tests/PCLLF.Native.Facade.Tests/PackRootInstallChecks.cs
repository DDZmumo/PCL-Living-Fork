using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
            ModpackUpdateCleanup.TerminateChild("HMCL root-install failed with all live context retained: " + error);
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
}
