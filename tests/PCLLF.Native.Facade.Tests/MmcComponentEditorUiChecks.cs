using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Actual Main, LF controls, Loader/fence, PCLCS backend and settings graph.
// All package/metadata/cache inputs are small owned fixtures; no game is launched.
internal static class MmcComponentEditorUiChecks {
    internal static void RunEdits(FixtureEnvironment environment) => Execute(environment, scope => scope.Edits(), "effective/author/override controls and real disable, enable, reset publication");
    internal static void RunExactVersion(FixtureEnvironment environment) => Execute(environment, scope => scope.ExactVersion(), "exact metadata version with fixed selected local JAR, full undo and current preferences/worlds");
    internal static void RunPatch(FixtureEnvironment environment) => Execute(environment, scope => scope.Patch(), "selected JSON bytes, mainClass/JVM preview and publication without ordinary Forge rewrite");
    internal static void RunCancellationUnstarted(FixtureEnvironment environment) => Execute(environment, scope => scope.CancelUnstarted(), "unstarted cancel has no producer and late Start is fenced");
    internal static void RunCancellationPreview(FixtureEnvironment environment) => Execute(environment, scope => scope.CancelPreview(), "preview cancel leaves the publication unchanged and drains the real worker");
    internal static void RunCancellationBeforeDecision(FixtureEnvironment environment) => Execute(environment, scope => scope.CancelDecision(false), "predecision cancel rolls back and releases busy after the worker returns");
    internal static void RunCancellationFinalValidation(FixtureEnvironment environment) => Execute(environment, scope => scope.CancelFinalValidation(), "cancel after final world/source validation still rolls back before the durable decision");
    internal static void RunCancellationAfterDecision(FixtureEnvironment environment) => Execute(environment, scope => scope.CancelDecision(true), "postdecision cancel finishes the decided publication");
    internal static void RunRefuseChangedInput(FixtureEnvironment environment) => Execute(environment, scope => scope.ChangedInput(), "selected patch/JAR mutations after preview refuse publication");
    internal static void RunRefuseUnsupported(FixtureEnvironment environment) => Execute(environment, scope => scope.Unsupported(), "unknown required capabilities cannot be acknowledged into Commit");
    internal static void RunRefuseStaleSelection(FixtureEnvironment environment) => Execute(environment, scope => scope.Stale(), "a new publication cannot be rebound to an old confirmation");
    internal static void RunRefuseSession(FixtureEnvironment environment) => Execute(environment, scope => scope.Session(), "unknown session refuses before the editor dialog");
    internal static void RunRefuseUnknownTarget(FixtureEnvironment environment) => Execute(environment, scope => scope.UnknownTarget(), "unowned live local library remains unchanged");
    internal static void RunRefuseNewRoot(FixtureEnvironment environment) => Execute(environment, scope => scope.NewRoot(), "a presentation hook cannot add a new direct component UID");
    internal static void RunWorldSuccess(FixtureEnvironment environment) => Execute(environment, scope => scope.World("success"), "Minecraft change uses the real WorldBackupEngine under final admission and publishes backup IDs");
    internal static void RunWorldLocked(FixtureEnvironment environment) => Execute(environment, scope => scope.World("locked"), "a locked current world refuses the Minecraft component publication");
    internal static void RunWorldChanged(FixtureEnvironment environment) => Execute(environment, scope => scope.World("changed"), "world namespace mutation before decision refuses publication with the backup and scene retained");
    internal static void RunJava(FixtureEnvironment environment) => Execute(environment, scope => scope.Java(), "explicit installed Java21 is actually probed before a Java8-to-21 candidate is published");
    private static void Execute(FixtureEnvironment environment, Action<Scope> action, string evidence) {
        Stage("entry"); using FileTaskScope files = new(environment); using var scope = new Scope(files);
        action(scope); InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-component-editor: " + evidence + "; actual producers drained; no game.");
    }
    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly Type ui = Native.Type("PCL.MmcComponentEditorUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly List<ModLoader.LoaderCombo<string>> tasks = [];
        private readonly List<MmcInstanceDefinitionChangeResult> published = [];
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState> scheduler;
        private readonly Action oldRefresh;
        private Fixture? fixture;
        private Func<MmcComponentEditorModel, MmcComponentEditorChoice?, MmcComponentEditorChoice?> choose = (_, _) => null;
        private Func<MmcComponentEditorConfirmation, int> confirm = _ => 1;
        private Action<ModLoader.LoaderCombo<string>>? register;
        private Action<MmcTransactionCheckpoint>? checkpoint;
        private Action? decisionReady;
        private MmcComponentEditorConfirmation? selected;
        private int choices;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;
        internal Scope(FileTaskScope files) {
            this.files = files;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            oldRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("ServicesFactory", new Func<ModMinecraft.McInstance, MmcComponentEditorServices>(_ => new() {
                Metadata = fixture!.Metadata, CacheRoots = fixture.Caches,
                RegisterTask = task => { tasks.Add(task); ModLoader.LoaderTaskbar.Add(task); register?.Invoke(task); },
                Published = result => { Native.Check(Busy, "Editor released busy before its publication callback returned."); published.Add(result); },
                DownloadCreated = _ => throw new IOException("Owned editor originals must remain offline."),
                TransactionObserved = (phase, _) => checkpoint?.Invoke(phase),
                DecisionReady = () => decisionReady?.Invoke()
            }));
            Set("SelectEdit", new Func<MmcComponentEditorModel, MmcComponentEditorChoice?, MmcComponentEditorChoice?>((model, old) => {
                choices++; AssertDialogUnlocked();
                Native.Check(model.PublishedEnvironment.JavaMajor == fixture!.Primary.Definition!.InstalledProjection!.RuntimeEnvironment!.JavaMajor,
                    "Editor inferred Java from the Minecraft version instead of the published environment.");
                object empty = Activator.CreateInstance(Native.Type("PCL.MmcComponentEditorView"), Native.Flags, null, [model, null], null)!;
                Native.Check(!(bool)Native.Property(empty.GetType(), "CanConfirm").GetValue(empty)!, "Editor silently selected an edit before a component was chosen.");
                return choose(model, old);
            }));
            Set("ConfirmPreview", new Func<MmcComponentEditorConfirmation, int>(value => {
                AssertDialogUnlocked(); selected = value;
                object view = Activator.CreateInstance(Native.Type("PCL.MmcComponentEditorPreviewView"), Native.Flags, null, [value], null)!;
                Native.Check((bool)Native.Property(view.GetType(), "CanConfirm").GetValue(view)! == value.CanCommit, "Real preview control has an incorrect commit state.");
                return confirm(value);
            }));
            Set("ErrorObserved", new Action<Exception, string>((error, text) => throw new IOException(text, error)));
        }
        private void Set(string name, object? value) { var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value); }
        private void AssertDialogUnlocked() {
            using var lease = MmcAdmissionGate.Acquire(fixture!.Version, fixture.Primary.Definition!.InstanceId, TimeSpan.Zero);
            object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, fixture.Version)!;
            Native.Check((int)Native.Get(state, "PauseCount")! == 0, "An editor dialog retained settings quiescence.");
        }
        private Fixture New(string name) {
            fixture?.Dispose(); fixture = new(Path.Combine(files.Root, "component-editor", name)); files.SelectMinecraft(fixture.Game);
            published.Clear(); register = null; checkpoint = null; selected = null; choices = 0; confirm = _ => 1;
            choose = (_, _) => new(new("net.minecraftforge", PatchContentUtf8: fixture.CustomPatch("1", "custom one", "fixture.Edited", "-Duser=edited")));
            return fixture;
        }
        private void Start(Fixture data) => Native.Static("PCL.MmcComponentEditorUi", "Start", data.Owner());
        private void Wait(ModBase.LoadState expected) {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (Busy || Current.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting || Native.Get(Current.Loaders.Single(), "LastRunningThread") is Thread { IsAlive: true }) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual component editor did not drain: " + Current.Error);
                Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
            Native.Check(Current.State == expected, "Actual component editor state differs: " + Current.State + " expected=" + expected + "; " + Current.Error);
        }
        internal void Edits() {
            var data = New("edits"); long generation = data.Primary.Definition!.Generation;
            foreach (var edit in new[] { new MmcInstanceComponentEdit("net.minecraftforge", Enabled: false), new MmcInstanceComponentEdit("net.minecraftforge", Enabled: true), new MmcInstanceComponentEdit("net.minecraftforge", Reset: true) }) {
                choose = (model, _) => {
                    var choice = new MmcComponentEditorChoice(edit);
                    object view = Activator.CreateInstance(Native.Type("PCL.MmcComponentEditorView"), Native.Flags, null, [model, choice], null)!;
                    Native.Check((bool)Native.Property(view.GetType(), "CanConfirm").GetValue(view)! && ((MmcComponentEditorChoice)Native.Call(view.GetType(), "ReadChoice", view)!).Edit == edit,
                        "Actual editor lost an explicit enable/disable/reset selection.");
                    return choice;
                };
                Start(data); Wait(ModBase.LoadState.Finished);
                Native.Check(data.Primary.Definition!.Generation == ++generation && published.Last().UndoPoint is not null, "Component edit lost monotonic generation or complete undo.");
            }
            Native.Check(data.Primary.Definition!.ComponentOverrides.IsEmpty && data.Compiled().MainClass == "fixture.Custom", "Reset did not restore author component semantics.");
        }
        internal void ExactVersion() {
            var data = New("version"); data.Put("minecraft/saves/world/level.dat", "current world"); data.Put(".minecraft/saves/world/level.dat", "alternate world");
            string replacement = Path.Combine(data.Root, "selected-custom.jar"); File.WriteAllText(replacement, "custom two", new UTF8Encoding(false));
            choose = (_, _) => new(new("net.minecraftforge", ExactVersion: "2")) { LocalFiles = [new("custom.jar", replacement)] };
            confirm = value => {
                Native.Check(value.CanCommit && value.Result.Preview!.Differences.Any(item => item.Field == "mainClass") && value.Inputs.Single().Sha256 == Hash("custom two") && value.Inputs.Single().SizeBytes == 10,
                    "Version preview did not bind actual selected bytes and runtime differences.");
                data.Owner().Config.Set("UnknownEditorPreference", "late current", false); data.Owner().Config.Save();
                Settings.Set("VersionAdvanceJvm", "", data.Owner());
                return 1;
            };
            Start(data); Wait(ModBase.LoadState.Finished);
            var after = data.Primary; var result = published.Single();
            Native.Check(after.Definition!.ComponentOverrides.Single(value => value.Uid == "net.minecraftforge").Version == "2" && data.Read("libraries/custom.jar") == "custom two" && data.Compiled().MainClass == "fixture.Remote2",
                "Exact version did not publish its real metadata/private input.");
            Native.Check(JObject.Parse(data.Read("PCL/config.json")).Value<string>("UnknownEditorPreference") == "late current" && data.Read("minecraft/saves/world/level.dat") == "current world" && data.Read(".minecraft/saves/world/level.dat") == "alternate world",
                "Component publication lost current preferences or either world's bytes.");
            var restored = new MmcInstanceHistoryBackend().RestoreAsync(new(data.Version, after.Definition.InstanceId, after.Definition.Generation,
                after.PrimarySha256!, result.UndoPoint!.PointId, result.UndoPoint.ManifestSha256)).GetAwaiter().GetResult();
            Native.Check(restored.Status == MmcInstanceBackendStatus.Succeeded && data.Read("libraries/custom.jar") == "custom one" && data.Compiled().MainClass == "fixture.Custom" &&
                data.Read("minecraft/saves/world/level.dat") == "current world" && JObject.Parse(data.Read("PCL/config.json")).Value<string>("UnknownEditorPreference") == "late current",
                "Real component undo failed to restore its old sources while preserving current player data/preferences: " + restored.Failure);
        }
        internal void Patch() {
            var data = New("patch"); string path = Path.Combine(data.Root, "chosen.json"); File.WriteAllText(path, data.CustomPatch("1", "custom one", "fixture.UserPatch", "-Dchosen=true"), new UTF8Encoding(false));
            choose = (model, _) => {
                var tuple = Native.Static("PCL.MmcComponentEditorUi", "ReadPatchFile", path)!;
                string text = (string)Native.Get(tuple, "Item1")!; var fact = (MmcComponentEditorFileFact)Native.Get(tuple, "Item2")!;
                var choice = new MmcComponentEditorChoice(new("net.minecraftforge", PatchContentUtf8: text)) { PatchFile = fact };
                object view = Activator.CreateInstance(Native.Type("PCL.MmcComponentEditorView"), Native.Flags, null, [model, choice], null)!;
                Native.Check(((MmcComponentEditorChoice)Native.Call(view.GetType(), "ReadChoice", view)!).PatchFile == fact, "Actual JSON editor lost the fixed selected file.");
                return choice;
            };
            Start(data); Wait(ModBase.LoadState.Finished);
            var confirmed = selected ?? throw new IOException("The real patch confirmation did not run.");
            Native.Check(confirmed.CanCommit && confirmed.Result.Preview!.Differences.Any(value => value.Field == "mainClass") && confirmed.Result.Preview.Differences.Any(value => value.Field == "jvmArguments") &&
                data.Compiled().MainClass == "fixture.UserPatch" && data.Compiled().JvmArguments.Any(value => value.Value == "-Dchosen=true") && data.Primary.Definition!.ComponentOverrides.Single().PatchReference?.Sha256 == Hash(File.ReadAllBytes(path)),
                "Real patch preview/publication did not preserve the explicit MMC entry and arguments.");
            var receipt = JObject.Parse(data.Read(Path.GetRelativePath(data.Version, Directory.GetFiles(data.PathOf("PCL/MmcDefinitionChanges"), "*.json").Single())));
            Native.Check(receipt.Value<string>("CandidateSha256") == confirmed.Result.Preview!.CandidateSha256, "Receipt did not bind the confirmed candidate.");
        }
        internal void CancelUnstarted() {
            var data = New("unstarted"); string before = data.Primary.PrimarySha256!;
            register = task => task.Cancel(); Start(data); Wait(ModBase.LoadState.Canceled);
            Native.Check(choices == 0 && published.Count == 0 && Native.Get(Current.Loaders.Single(), "LastRunningThread") is null && data.Primary.PrimarySha256 == before, "Unstarted editor ran its producer or changed current bytes.");
            Current.Loaders.Single().Start(data.Version); Native.Check(Native.Get(Current.Loaders.Single(), "LastRunningThread") is null, "Canceled editor accepted a late worker Start.");
        }
        internal void CancelPreview() {
            var data = New("preview-cancel"); string before = data.Primary.PrimarySha256!;
            confirm = _ => 2; Start(data); Wait(ModBase.LoadState.Canceled);
            Native.Check(selected?.CanCommit == true && published.Count == 0 && data.Primary.PrimarySha256 == before, "Preview cancellation published or skipped actual Preview.");
        }
        internal void CancelDecision(bool decided) {
            var data = New(decided ? "after-decision" : "before-decision"); string before = data.Primary.PrimarySha256!;
            checkpoint = phase => { if (phase == (decided ? MmcTransactionCheckpoint.CommitDecided : MmcTransactionCheckpoint.BeforeDecision)) Current.Cancel(); };
            Start(data); Wait(decided ? ModBase.LoadState.Finished : ModBase.LoadState.Canceled);
            Native.Check((data.Primary.PrimarySha256 != before) == decided && published.Count == (decided ? 1 : 0), "Editor cancellation crossed the durable decision boundary.");
        }
        internal void CancelFinalValidation() {
            var data = New("cancel-final-validation"); var before = data.Primary;
            string history = data.PathOf("PCL/MmcTransactionHistory");
            string[] decisionsBefore = Directory.EnumerateFiles(history, "CommitDecided.record", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            data.Put("minecraft/saves/world/level.dat", "current world");
            data.Put(".minecraft/saves/world/level.dat", "alternate world");
            choose = (model, _) => { var patch = JObject.Parse(model.PatchTexts["net.minecraft"]); patch["version"] = "1.7.11"; return new(new("net.minecraft", PatchContentUtf8: patch.ToString())); };
            int ready = 0, committed = 0;
            checkpoint = phase => { if (phase == MmcTransactionCheckpoint.CommitDecided) committed++; };
            decisionReady = () => { ready++; Current.Cancel(); };
            Start(data); Wait(ModBase.LoadState.Canceled);
            Native.Check(ready == 1 && committed == 0 && published.Count == 0 && data.Primary.PrimarySha256 == before.PrimarySha256 &&
                data.Primary.Definition!.Generation == before.Definition!.Generation &&
                decisionsBefore.SequenceEqual(Directory.EnumerateFiles(history, "CommitDecided.record", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) &&
                data.Read("minecraft/saves/world/level.dat") == "current world" && data.Read(".minecraft/saves/world/level.dat") == "alternate world",
                "Final validation sealed cancellation before a durable decision, changed the publication or lost player bytes.");
        }
        internal void ChangedInput() {
            foreach (bool patch in new[] { true, false }) {
                var data = New(patch ? "changed-patch" : "changed-jar"); string before = data.Primary.PrimarySha256!;
                string path = Path.Combine(data.Root, patch ? "chosen.json" : "chosen.jar");
                string text = patch ? data.CustomPatch("1", "custom one", "fixture.ChangedInput", "-Dfixed=true") : "custom two"; File.WriteAllText(path, text, new UTF8Encoding(false));
                choose = (_, _) => patch ? new(new("net.minecraftforge", PatchContentUtf8: text)) { PatchFile = new(Path.GetFileName(path), path, Hash(text), Encoding.UTF8.GetByteCount(text)) } :
                    new(new("net.minecraftforge", ExactVersion: "2")) { LocalFiles = [new("custom.jar", path)] };
                confirm = _ => { File.WriteAllText(path, "changed after actual preview", new UTF8Encoding(false)); return 1; };
                Start(data); Wait(ModBase.LoadState.Failed);
                Native.Check(selected?.CanCommit == true && data.Primary.PrimarySha256 == before && published.Count == 0 && File.ReadAllText(path) == "changed after actual preview", "Changed selected source was adopted or deleted.");
            }
        }
        internal void Unsupported() {
            var data = New("unsupported"); string before = data.Primary.PrimarySha256!;
            var patch = JObject.Parse(data.CustomPatch("1", "custom one", "fixture.Bad", "-Dbad=true")); patch["unknownCriticalCapability"] = true;
            choose = (_, _) => new(new("net.minecraftforge", PatchContentUtf8: patch.ToString()));
            Start(data); Wait(ModBase.LoadState.Failed); // Deliberately force the hook's commit result.
            Native.Check(selected?.CanCommit == false && selected.Result.Preview is null && data.Primary.PrimarySha256 == before && published.Count == 0, "A hook bypassed unknown-capability refusal.");
        }
        internal void Stale() {
            var data = New("stale"); string? other = null;
            confirm = _ => {
                var current = data.Primary;
                var preview = data.Changes().PreviewAsync(new(data.Version, current.Definition!.InstanceId, current.Definition.Generation, current.PrimarySha256!, current.Definition.InstalledProjection!.RuntimeEnvironment!,
                    [new("net.minecraftforge", PatchContentUtf8: data.CustomPatch("1", "custom one", "fixture.OtherPublished", "-Dother=true"))])).GetAwaiter().GetResult().Preview!;
                var result = data.Changes().CommitAsync(new(preview, preview.CandidateSha256)).GetAwaiter().GetResult();
                Native.Check(result.Status == MmcInstanceBackendStatus.Succeeded, "Competing real publication did not complete: " + result.Failure); other = data.Primary.PrimarySha256; return 1;
            };
            Start(data); Wait(ModBase.LoadState.Failed);
            Native.Check(other is not null && data.Primary.PrimarySha256 == other && data.Compiled().MainClass == "fixture.OtherPublished" && published.Count == 0, "Editor rebound its old selection onto a newer publication.");
        }
        internal void Session() {
            var data = New("unknown-session"); string before = data.Primary.PrimarySha256!;
            data.Put("PCL/MmcSessions/unknown/Launching.record", "unknown receipt");
            Start(data); Wait(ModBase.LoadState.Failed);
            Native.Check(choices == 0 && published.Count == 0 && data.Primary.PrimarySha256 == before && data.Read("PCL/MmcSessions/unknown/Launching.record") == "unknown receipt", "Unknown session reached an editable/commit UI.");
        }
        internal void UnknownTarget() {
            var data = New("unknown-target"); string before = data.Primary.PrimarySha256!;
            data.Put("libraries/custom.jar", "unknown current library");
            string path = Path.Combine(data.Root, "chosen.jar"); File.WriteAllText(path, "custom two", new UTF8Encoding(false));
            choose = (_, _) => new(new("net.minecraftforge", ExactVersion: "2")) { LocalFiles = [new("custom.jar", path)] };
            Start(data); Wait(ModBase.LoadState.Failed);
            Native.Check(selected?.CanCommit == false && published.Count == 0 && data.Primary.PrimarySha256 == before && data.Read("libraries/custom.jar") == "unknown current library", "Unknown live library was adopted or overwritten.");
        }
        internal void NewRoot() {
            var data = New("new-root"); string before = data.Primary.PrimarySha256!;
            choose = (_, _) => new(new("fixture.not-author-declared", Enabled: true));
            Start(data); Wait(ModBase.LoadState.Failed);
            Native.Check(selected is null && data.Primary.PrimarySha256 == before && published.Count == 0, "A hook manufactured a new root UID without a ZIP source update.");
        }
        internal void World(string mode) {
            var data = New("world-" + mode); data.Put("minecraft/saves/world/level.dat", "current world"); data.Put("minecraft/saves/world/session.lock", "stopped");
            data.Put(".minecraft/saves/world/level.dat", "alternate world"); string before = data.Primary.PrimarySha256!;
            using var held = mode == "locked" ? new FileStream(data.PathOf("minecraft/saves/world/session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            choose = (model, _) => { var patch = JObject.Parse(model.PatchTexts["net.minecraft"]); patch["version"] = "1.7.11"; return new(new("net.minecraft", PatchContentUtf8: patch.ToString())); };
            if (mode == "changed") checkpoint = phase => { if (phase == MmcTransactionCheckpoint.BeforeDecision) data.Put("minecraft/saves/world/late-file.dat", "late world change"); };
            Start(data); Wait(mode == "success" ? ModBase.LoadState.Finished : ModBase.LoadState.Failed);
            Native.Check(data.Read("minecraft/saves/world/level.dat") == "current world" && data.Read(".minecraft/saves/world/level.dat") == "alternate world" && (data.Primary.PrimarySha256 != before) == (mode == "success"), "Minecraft component change crossed world-backup refusal or changed player data.");
            if (mode == "success") {
                string store = (string)Native.Static("PCL.WorldBackups", "Store", data.PathOf("minecraft"))!;
                string id = published.Single().WorldBackupIds.Single(); using var zip = ZipFile.OpenRead(Path.Combine(store, "points", id, "world.zip"));
                using var reader = new StreamReader(zip.GetEntry("level.dat")!.Open()); Native.Check(reader.ReadToEnd() == "current world", "Real component backup lacks current world bytes.");
            } else if (mode == "changed") {
                Native.Check(data.Read("minecraft/saves/world/late-file.dat") == "late world change" && Directory.EnumerateFiles(data.PathOf("PCL/MmcHistory/index")).Any(), "World race erased the live change or complete undo scene.");
            }
        }
        internal void Java() {
            var data = New("java"); string path = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA21") ?? throw new IOException("Set an explicit real Java21 executable: PCLLF_MMC_TEST_JAVA21");
            Native.Check(Path.IsPathFullyQualified(path) && File.Exists(path), "Selected installed test Java is missing.");
            // This fixture's Minecraft component already declares Java 8 and 21.
            // Extend the candidate policy, then explicitly choose installed Java 21;
            // an additional component's declaration does not remove that Java 8 candidate.
            choose = (_, _) => { var patch = JObject.Parse(data.CustomPatch("1", "custom one", "fixture.Java21", "-Djava21=true")); patch["compatibleJavaMajors"] = new JArray(21, 25); return new(new("net.minecraftforge", PatchContentUtf8: patch.ToString())) { JavaPath = choices == 1 ? null : path }; };
            confirm = value => {
                if (choices == 1) { Native.Check(value.CanCommit && value.Result.Preview!.Selection.Environment.JavaMajor == 8 && value.JavaImage is null,
                    "The candidate silently changed published Java 8 before an explicit Java selection."); return 3; }
                return 1;
            };
            Start(data); Wait(ModBase.LoadState.Finished);
            Native.Check(choices == 2 && selected?.JavaImage is not null && selected.Result.Preview!.Selection.Environment.JavaMajor == 21 && data.Primary.Definition!.InstalledProjection!.RuntimeEnvironment!.JavaMajor == 21 &&
                selected.Result.Preview.Differences.Any(value => value.Field == "javaPolicy") && selected.Result.Preview.Differences.Any(value => value.Field == "environment"), "Explicit Java was not actually probed/fixed before the candidate publication.");
        }
        public void Dispose() {
            Native.Check(!Busy && tasks.All(task => Native.Get(task.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }), "Actual component producer is active; retaining fixture guards.");
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            foreach (var value in previous) value.Key.SetValue(null, value.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = oldRefresh; fixture?.Dispose();
        }
    }
    internal sealed class Fixture : IDisposable {
        internal readonly string Root, Game, Version;
        internal readonly MmcMetadataStore Metadata;
        internal readonly MmcArtifactCacheRoots Caches;
        private readonly HttpClient client;
        private ModMinecraft.McInstance? owner;
        private static readonly byte[] Index = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        internal MmcInstanceInspection Primary => MmcInstanceInspector.Read(Version);
        internal Fixture(string root) {
            Root = root; Game = Path.Combine(root, "minecraft"); Version = Path.Combine(Game, "versions", "managed"); Directory.CreateDirectory(Game);
            client = new(new MetadataHandler(this)); Metadata = new(Path.Combine(root, "metadata"), new Uri("https://fixture.invalid/meta/"), client);
            Caches = new(Path.Combine(root, "cache", "libraries"), Path.Combine(root, "cache", "assets"), Path.Combine(root, "cache", "sources"));
            string index = Path.Combine(Caches.AssetsRoot, "indexes", "fixture.json"); Directory.CreateDirectory(Path.GetDirectoryName(index)!); File.WriteAllBytes(index, Index);
            string packPath = Path.Combine(root, "fixture.zip");
            using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Create)) {
                Add("mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"},{"uid":"net.minecraftforge","version":"1"}]}""");
                Add("instance.cfg", "name=Owned component editor\n");
                Add("patches/net.minecraft.json", new JObject { ["formatVersion"] = 1, ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Base",
                    ["mainJar"] = new JObject { ["name"] = "fixture:base:1", ["MMC-hint"] = "local", ["MMC-filename"] = "base.jar" },
                    ["assetIndex"] = new JObject { ["id"] = "fixture", ["url"] = "https://fixture.invalid/index", ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Index)), ["size"] = Index.Length }, ["compatibleJavaMajors"] = new JArray(8, 21) }.ToString());
                Add("patches/net.minecraftforge.json", CustomPatch("1", "custom one", "fixture.Custom", "-Dauthor=true"));
                Add("libraries/base.jar", "base original"); Add("libraries/custom.jar", "custom one"); Add("minecraft/config/owned.cfg", "author config");
                void Add(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
            }
            var backend = new MmcInstanceBackend<ModNet.NetFile>(Metadata, Caches, MmcDownloads.BuildFile, (batch, token) => MmcDownloads.RunBatchAsync(batch, token));
            var result = backend.ImportAsync(new(packPath, Version, "managed", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            Native.Check(result.Status == MmcInstanceBackendStatus.Succeeded, "Owned production editor import failed: " + result.Failure);
        }
        internal ModMinecraft.McInstance Owner() {
            if (owner is not null) return owner;
            owner = new ModMinecraft.McInstance(Version).Load(); FileTaskScope.CacheInstance(owner); return owner;
        }
        internal string CustomPatch(string version, string bytes, string main, string argument) => new JObject {
            ["formatVersion"] = 1, ["uid"] = "net.minecraftforge", ["version"] = version, ["mainClass"] = main, ["+jvmArgs"] = new JArray(argument),
            ["compatibleJavaMajors"] = new JArray(8), ["+libraries"] = new JArray(new JObject { ["name"] = "fixture:custom:" + version, ["MMC-hint"] = "local", ["MMC-filename"] = "custom.jar",
                ["downloads"] = new JObject { ["artifact"] = new JObject { ["sha256"] = Hash(bytes), ["size"] = Encoding.UTF8.GetByteCount(bytes) } } })
        }.ToString();
        internal MmcInstanceDefinitionChangeBackend<ModNet.NetFile> Changes() => new(Metadata, Caches, MmcDownloads.BuildFile, (batch, token) => MmcDownloads.RunBatchAsync(batch, token));
        internal MmcCompiledProfile Compiled() { var current = Primary; var saved = MmcSavedSourceStore.LoadAsync(current.Paths!, current.Definition!).GetAwaiter().GetResult(); return MmcProfileCompiler.Compile(saved.Resolved.Resolution.Profile!, current.Definition!.InstalledProjection!.RuntimeEnvironment!).Profile!; }
        internal string PathOf(string relative) => Path.Combine(Version, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Put(string relative, string text) { string path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        internal string Read(string relative) => File.ReadAllText(PathOf(relative));
        public void Dispose() => client.Dispose();
        private sealed class MetadataHandler(Fixture fixture) : HttpMessageHandler {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
                token.ThrowIfCancellationRequested();
                if (request.RequestUri?.AbsolutePath != "/meta/net.minecraftforge/2.json") throw new IOException("Unexpected owned component metadata request: " + request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture.CustomPatch("2", "custom two", "fixture.Remote2", "-Dremote=2"), Encoding.UTF8, "application/json") });
            }
        }
    }
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Stage(string value) { Console.WriteLine("DIAG mmc-component-editor " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + value); Console.Out.Flush(); }
}
