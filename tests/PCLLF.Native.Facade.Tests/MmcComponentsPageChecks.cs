using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Genuine sidebar BAML and the existing editor view, inputs, preview and commit.
// Separate groups retain the Native 60-second child boundary and fixture IO guard.
internal static class MmcComponentsPageChecks {
    internal static void Run(FixtureEnvironment environment) => RunPart(environment, admission: false);
    internal static void RunAdmission(FixtureEnvironment environment) => RunPart(environment, admission: true);
    private static void RunPart(FixtureEnvironment environment, bool admission) {
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) {
            if (admission) scope.Admission(); else scope.Page();
        }
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine(admission
            ? "PASS mmc-components-page-admission: fixed owner and source model; stale callback, replacement ID and damaged management cannot preview or publish."
            : "PASS mmc-components-page: real sidebar/BAML embeds the original editor; component/patch inputs, no Java picker, return/cancel, genuine preview/commit and settings Java image validation.");
    }

    private sealed class Scope : IDisposable {
        private readonly FileTaskScope files;
        private readonly MmcComponentEditorUiChecks.Fixture data;
        private readonly Type ui = Native.Type("PCL.MmcComponentEditorUi");
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        private readonly List<ModLoader.LoaderCombo<string>> tasks = [];
        private readonly List<MmcInstanceDefinitionChangeResult> publications = [];
        private readonly List<PageInstanceMmcComponents> pages = [];
        private readonly ModMinecraft.McInstance? oldOwner = LauncherInstanceContext.Instance;
        private readonly LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState> scheduler;
        private readonly Action oldRefresh;
        private readonly int oldAnimation = ModAnimation.AniControlEnabled;
        private int decision = 1, confirmations, choiceDialogs;
        private MmcComponentEditorConfirmation? confirmed;
        private ModLoader.LoaderCombo<string> Current => tasks.Last();
        private bool Busy => (bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!;

        internal Scope(FileTaskScope files) {
            this.files = files;
            data = new(Path.Combine(files.Root, "components-page"));
            files.SelectMinecraft(data.Game);
            ModAnimation.AniControlEnabled++;
            scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, LauncherLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            oldRefresh = scheduler.RefreshExtraDownload; scheduler.RefreshExtraDownload = () => { };
            Set("ServicesFactory", new Func<ModMinecraft.McInstance, MmcComponentEditorServices>(_ => new() {
                Metadata = data.Metadata, CacheRoots = data.Caches,
                RegisterTask = task => { tasks.Add(task); ModLoader.LoaderTaskbar.Add(task); },
                Published = value => publications.Add(value), DownloadCreated = _ => throw new IOException("Page fixture inputs must remain offline.")
            }));
            Set("SelectEdit", new Func<MmcComponentEditorModel, MmcComponentEditorChoice?, MmcComponentEditorChoice?>((_, _) => {
                choiceDialogs++; throw new IOException("Embedded component page reopened the obsolete choice dialog.");
            }));
            Set("ConfirmPreview", new Func<MmcComponentEditorConfirmation, int>(value => {
                confirmations++; confirmed = value;
                using var lease = MmcAdmissionGate.Acquire(data.Version, data.Primary.Definition!.InstanceId, TimeSpan.Zero);
                object state = Native.Call(typeof(MmcConfigurationCoordinator), "StateFor", null, data.Version)!;
                Native.Check((int)Native.Get(state, "PauseCount")! == 0, "Preview confirmation retained a settings pause.");
                return decision;
            }));
            Set("ErrorObserved", new Action<Exception, string>((error, text) => throw new IOException(text, error)));
        }
        private void Set(string name, object? value) {
            var property = Native.Property(ui, name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value);
        }
        private static T Part<T>(FrameworkElement page, string name) where T : class => (T)(page.FindName(name) ?? throw new IOException("Missing actual page control: " + name));
        private static Task Loading(PageInstanceMmcComponents page) => (Task)Native.Property(typeof(PageInstanceMmcComponents), "ModelTask").GetValue(page)!;
        private static void Until(Func<bool> complete, string phase) {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!complete()) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Components page did not finish " + phase);
                Thread.Sleep(3);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
        }
        private static void WaitModel(PageInstanceMmcComponents page) => Until(() => Loading(page).IsCompleted, "its read-only model");
        private void Wait(int before, ModBase.LoadState expected) {
            Until(() => tasks.Count > before && !Busy && Current.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Waiting) &&
                Native.Get(Current.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }, "its genuine backend task");
            Native.Check(Current.State == expected, "Component page task differs: " + Current.State + "; " + Current.Error);
        }
        private static void Start(PageInstanceMmcComponents page) => Native.Call(typeof(PageInstanceMmcComponents), "StartChange", page);
        private static FrameworkElement Editor(PageInstanceMmcComponents page) => Part<FrameworkElement>(page, "MmcComponentEditor");
        private PageInstanceMmcComponents Open(ModMinecraft.McInstance owner) {
            LauncherInstanceContext.Instance = owner;
            var page = (PageInstanceMmcComponents)Activator.CreateInstance(typeof(PageInstanceMmcComponents), Native.Flags, null,
                [owner, owner.Config.ContextStamp.InstanceId], null)!;
            pages.Add(page); WaitModel(page); return page;
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
            yield return root;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject node) foreach (var descendant in Descendants(node)) yield return descendant;
        }
        private void ChoosePatch(PageInstanceMmcComponents page, string main = "fixture.Embedded") {
            var editor = Editor(page); var type = editor.GetType();
            Native.Call(type, "SelectComponent", editor, "net.minecraftforge");
            var kind = type.GetNestedType("EditKind", BindingFlags.NonPublic)!;
            Native.Set(editor, "kind", Enum.Parse(kind, "Patch"));
            Native.Call(type, "Show", editor, 1);
            var input = Descendants(editor).OfType<TextBox>().Single(value => value.AcceptsReturn);
            input.Text = data.CustomPatch("1", "custom one", main, "-Dembedded=true");
            Native.Check(Part<System.Windows.Controls.Button>(page, "BtnPreviewChanges").IsEnabled &&
                ((MmcComponentEditorChoice)Native.Call(type, "ReadChoice", editor)!).Edit.PatchContentUtf8!.Contains(main, StringComparison.Ordinal),
                "Original component/patch inputs did not control the embedded page choice.");
        }

        internal void Page() {
            var owner = data.Owner(); LauncherInstanceContext.Instance = owner;
            var left = FileTaskShell.Install().GlassInstanceNavigation;
            var page = (PageInstanceMmcComponents)left.GetPage(FormMain.PageSubType.InstanceModify);
            pages.Add(page); WaitModel(page);
            var editor = Editor(page);
            Native.Check(editor.GetType() == Native.Type("PCL.MmcComponentEditorView") && double.IsNaN(editor.Height) &&
                page.FindName("BtnPrimaryAction") is null && ReferenceEquals(page.FindName("PanBack"), Native.Property(editor.GetType().BaseType!, "PageScroller").GetValue(editor)),
                "Actual sidebar route did not embed the existing view/scroller, or retained an intermediate action button.");
            Native.Check(!Part<System.Windows.Controls.Button>(page, "BtnPreviewChanges").IsEnabled, "Component page silently selected a modification.");
            Native.Call(editor.GetType(), "Show", editor, 2);
            var inputButtons = Descendants(editor).OfType<Button>().Select(value => value.Content?.ToString()).ToArray();
            Native.Check(inputButtons.Contains("选择真实本地 JAR…") && !inputButtons.Any(value => value?.Contains("选择本机 Java", StringComparison.Ordinal) == true || value == "沿用已发布环境"),
                "Local JAR input was removed or an independent Java selector remained.");
            ChoosePatch(page);
            string beforeHash = data.Primary.PrimarySha256!;
            decision = 3;
            int before = tasks.Count; Start(page); Wait(before, ModBase.LoadState.Canceled);
            Native.Check(ReferenceEquals(editor, Editor(page)) && Part<System.Windows.Controls.Button>(page, "BtnPreviewChanges").IsEnabled &&
                data.Primary.PrimarySha256 == beforeHash && choiceDialogs == 0,
                "Returning from the real preview reopened a choice dialog, lost the embedded selection or published a change.");
            decision = 2;
            before = tasks.Count; Start(page); Wait(before, ModBase.LoadState.Canceled);
            Native.Check(data.Primary.PrimarySha256 == beforeHash && publications.Count == 0, "Preview cancellation changed the instance.");
            decision = 1;
            before = tasks.Count; Start(page); Wait(before, ModBase.LoadState.Finished); WaitModel(page);
            Native.Check(publications.Count == 1 && data.Compiled().MainClass == "fixture.Embedded" && choiceDialogs == 0 &&
                confirmed?.CanCommit == true && confirmed.JavaImage is null && confirmed.Result.Preview!.Selection.Environment.JavaMajor == 8,
                "Embedded page bypassed the real preview/commit or inferred a new Java environment.");

            // Removing the Java picker must not remove its real executable probe.
            // Force a deliberately invalid executable through the one settings source.
            string fakeFolder = Path.Combine(files.Root, "settings-java"); Directory.CreateDirectory(fakeFolder);
            File.WriteAllText(Path.Combine(fakeFolder, "java.exe"), "inert invalid Java image");
            Native.Static("PCL.MmcLaunchPreferences", "SetForcedJava", owner, new Java(fakeFolder, new Version(8, 0)));
            owner.Config.Save();
            ChoosePatch(page, "fixture.MustNotPublish");
            beforeHash = data.Primary.PrimarySha256!; int previewsBefore = confirmations;
            before = tasks.Count; Start(page); Wait(before, ModBase.LoadState.Failed);
            Native.Check(data.Primary.PrimarySha256 == beforeHash && confirmations == previewsBefore && publications.Count == 1 &&
                Current.Error?.ToString().Contains("Java", StringComparison.OrdinalIgnoreCase) == true,
                "The page ignored instance settings or removed actual Java PE/version validation with the picker.");
        }

        internal void Admission() {
            var owner = data.Owner(); var page = Open(owner);
            var originalModel = (MmcComponentEditorModel)Native.Get(page, "_model")!;
            ChoosePatch(page);
            page.Refresh();
            LauncherInstanceContext.Instance = new ModMinecraft.McInstance(data.Version).Load();
            WaitModel(page);
            Start(page);
            Native.Check(!Part<System.Windows.Controls.Button>(page, "BtnPreviewChanges").IsEnabled && tasks.Count == 0, "Old page/async callback adopted another owner object.");
            LauncherInstanceContext.Instance = owner; page.Refresh(); WaitModel(page); ChoosePatch(page);

            using var replacement = new MmcComponentEditorUiChecks.Fixture(Path.Combine(files.Root, "components-replacement"));
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data.Version)), retained = target + "-retained";
            AssertWithin(target); AssertWithin(retained);
            Directory.Move(target, retained);
            try {
                CopyTree(replacement.Version, target);
                Native.Check(MmcInstanceInspector.Read(target).IsConsistent && replacement.Primary.Definition!.InstanceId != originalModel.Source.Definition!.InstanceId,
                    "Replacement fixture must have a different valid identity.");
                page.Refresh(); Start(page);
                Native.Check(!Part<System.Windows.Controls.Button>(page, "BtnPreviewChanges").IsEnabled && tasks.Count == 0, "Refresh adopted the replacement identity.");
                var reopened = Open(owner); Start(reopened);
                Native.Check(!Part<System.Windows.Controls.Button>(reopened, "BtnPreviewChanges").IsEnabled && tasks.Count == 0, "Reopening an old attached owner adopted a replacement identity.");
                try {
                    Native.Static("PCL.MmcComponentEditorUi", "StartWithChoice", owner, originalModel, new MmcComponentEditorChoice(new("net.minecraftforge", Enabled: false)));
                    throw new IOException("A stale source model reached a new identity.");
                } catch (IOException error) when (!error.Message.Contains("stale source model", StringComparison.Ordinal)) { }
                Native.Check(tasks.Count == 0, "Stale typed submission registered a backend task.");
            } finally {
                string copied = target + "-replacement"; AssertWithin(copied);
                Directory.Move(target, copied); Directory.Move(retained, target);
            }
            string projection = data.PathOf("managed.json"); byte[] kept = File.ReadAllBytes(projection);
            try {
                File.AppendAllText(projection, " "); page.Refresh(); Start(page);
                Native.Check(!Part<System.Windows.Controls.Button>(page, "BtnPreviewChanges").IsEnabled && tasks.Count == 0,
                    "Damaged MMC management fell through to another component writer.");
            } finally { File.WriteAllBytes(projection, kept); }
        }

        private void AssertWithin(string path) {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(files.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Component fixture operation escaped FileTaskScope.");
        }
        private void CopyTree(string source, string target) {
            AssertWithin(source); AssertWithin(target); Directory.CreateDirectory(target);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
        public void Dispose() {
            foreach (var page in pages) { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); WaitModel(page); }
            Native.Check(!Busy && tasks.All(task => Native.Get(task.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }), "Actual component producer remained active at page teardown.");
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            foreach (var item in previous) item.Key.SetValue(null, item.Value);
            foreach (var task in tasks) ModLoader.LoaderTaskbar.Remove(task);
            scheduler.RefreshExtraDownload = oldRefresh; ModAnimation.AniControlEnabled = oldAnimation;
        }
    }
}
