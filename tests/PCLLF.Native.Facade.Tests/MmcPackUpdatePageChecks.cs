using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Actual Main BAML and Loader/ZIP evidence; owned fixtures and the existing loopback metadata adapter only.
internal static class MmcPackUpdatePageChecks {
    internal static void RunUnbound(FixtureEnvironment environment) {
        using var scope = new MmcPlatformOriginUiTaskChecks.Scope(environment, "pack-page-unbound", null);
        using var hooks = new Hooks();
        var data = scope.Data; Guid originalId = data.Primary.Definition.InstanceId;
        SetDisplay(data, "VersionA", "1.0", "release", "2026-10-01T00:00:00Z");
        var page = Page(data);
        int ordinaryReads = 0, localChoices = 0, originChoices = 0;
        Native.Set(page, "ReadVersions", new Func<JObject, List<ResourceVersion>>(_ => { ordinaryReads++; throw new IOException("MMC cannot use ordinary origin metadata."); }));
        File.WriteAllText(Path.Combine(data.Version, "PCL", "ModpackOrigin.json"), "{\"version\":\"foreign ordinary origin\",\"source\":{\"platform\":\"Modrinth\",\"project\":\"OtherProject\"}}");
        hooks.Set("PCL.MmcPackageUpdateUi", "SelectArchive", new Func<string>(() => { localChoices++; return ""; }));
        try {
            Refresh(scope, page);
            Native.Check(Button(page, "BtnOrigin").Visibility == Visibility.Visible && Button(page, "BtnOrigin").IsEnabled && !Button(page, "BtnOnline").IsEnabled && Button(page, "BtnLocal").IsEnabled,
                "Unbound MMC did not share the real update page's local/source actions.");
            Native.Check(Text(page, "LabSource").Contains("平台来源未核实", StringComparison.Ordinal) && Text(page, "LabVersion") != "foreign ordinary origin" && ordinaryReads == 0,
                "Unverified or ordinary source strings granted online authority.");
            ClickButton(page, "BtnLocal"); Native.Check(localChoices == 1, "The actual local-ZIP button did not enter MmcPackageUpdateUi.");
            hooks.Input(origin => {
                originChoices++; Native.Check(origin.PackageSha256 == data.Primary.Definition.Origin.PackageSha256, "The page source form belongs to another adopted ZIP.");
                return Activator.CreateInstance(Native.Type("PCL.MmcPlatformOriginInput"), Native.Flags, null,
                    [data.OriginalZip, ResourcePlatforms.Modrinth, "ProjectA", "VersionA"], null)!;
            });
            ModpackStageLayoutChecks.Save(page, Path.Combine(data.Root, "mmc-pack-update-unbound.png"), 820, 820);
            ClickButton(page, "BtnOrigin"); scope.Wait(ModBase.LoadState.Finished);
            scope.Until(() => scope.Attached.Count == 1 && Text(page, "LabListStatus").StartsWith("已获取", StringComparison.Ordinal), "same-page verified origin and online list refresh");
            AwaitRead(scope, page);
            Native.Check(originChoices == 1 && ordinaryReads == 0 && data.Primary.Definition.InstanceId == data.Selection().ExpectedInstanceId &&
                data.Primary.Definition.InstanceId == originalId && MmcInstancePlatformOriginBackend.ReadVerified(data.Selection())?.Identity.ProjectId == "ProjectA" && Rows(page).Length == 2,
                "The real page binding skipped exact ZIP evidence or failed to populate the shared version rows.");
            Native.Check(Button(page, "BtnOrigin").Content?.ToString() == "重新核实来源" && Button(page, "BtnBackups").Content?.ToString() == "环境记录", "Bound MMC retained ordinary backup/source action labels.");
            Native.Check(data.Read("minecraft/saves/world/level.dat") == "personal world", "Page source binding changed the controlled player world.");
        } finally { Close(scope, page); }
        scope.Finish(); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-pack-update-page-unbound: genuine shared BAML, local ZIP entry and same-page original-ZIP verification/transaction/catalog; no ordinary fallback, JVM or game.");
    }

    internal static void RunBound(FixtureEnvironment environment) {
        using var scope = new MmcPlatformOriginUiTaskChecks.Scope(environment, "pack-page-bound", null);
        var data = scope.Data; string next = data.NewPackage();
        data.Publish("VersionB", next, "http://127.0.0.1:1/beta.zip");
        data.Publish("VersionC", next, "http://127.0.0.1:1/alpha.zip");
        SetDisplay(data, "VersionA", "1.0", "release", "2026-10-01T00:00:00Z");
        SetDisplay(data, "VersionB", "2.0 beta", "beta", "2026-10-02T00:00:00Z");
        SetDisplay(data, "VersionC", "3.0 alpha", "alpha", "2026-10-03T00:00:00Z");
        data.SeedOrigin(); var before = data.Primary;
        var page = Page(data); int confirmations = 0;
        Native.Set(page, "ConfirmMmcVersion", new Func<MmcPlatformReleaseDescriptor, int>(release => {
            confirmations++; Native.Check(release.ProjectId == "ProjectA" && release.VersionId == "VersionB" && release.File.FileName == "VersionB.zip", "The shared row lost its exact typed file identity."); return 1;
        }));
        try {
            Refresh(scope, page);
            var release = Radio(page, "RadioRelease"); var beta = Radio(page, "RadioBeta"); var alpha = Radio(page, "RadioAlpha"); var all = Radio(page, "RadioAll");
            var search = (TextBox)page.FindName("TextSearchVersion");
            Native.Check(release.IsChecked == true && Rows(page).Length == 2 && release.Content?.ToString() == "正式版（2）" && beta.Content?.ToString() == "测试版（2）" && alpha.Content?.ToString() == "早期测试版（2）" && all.Content?.ToString() == "全部（6）",
                "Verified official release metadata did not drive the real channel counts/default.");
            var installed = Rows(page).Where(Current).ToArray();
            Native.Check(installed.Length == 1 && AutomationProperties.GetHelpText(installed[0]).Contains("VersionA.zip", StringComparison.Ordinal) && Text(page, "LabVersion") == "1.0", "The current marker identified a different file in the same release.");
            beta.IsChecked = true; Native.Check(Rows(page).Length == 2 && Rows(page).All(row => AutomationProperties.GetName(row) == "2.0 beta"), "The real MMC page beta channel contains another release type.");
            search.Text = "VERSIONB.ZIP";
            Native.Check(Rows(page).Length == 1 && beta.Content?.ToString() == "测试版（1）", "Filename search did not intersect the actual MMC channel.");
            search.Text = "no such release";
            Native.Check(Rows(page).Length == 0 && ((TextBlock)page.FindName("LabListEmpty")).Visibility == Visibility.Visible, "A searched MMC channel lost its empty state.");
            search.Text = "1.7.10"; all.IsChecked = true;
            Native.Check(Rows(page).Length == 6 && Current(Rows(page).Single(row => AutomationProperties.GetHelpText(row).Contains("VersionA.zip", StringComparison.Ordinal))), "Minecraft search lost the exact installed file marker.");
            search.Text = "VersionB.zip"; beta.IsChecked = true;
            ModpackStageLayoutChecks.Save(page, Path.Combine(data.Root, "mmc-pack-update-bound.png"), 820, 820);
            using (var pause = new MmcRootUpdatePause(() => scope.Busy ? scope.Current : null)) {
                ClickRow(Rows(page).Single());
                Native.Check(pause.Captured && confirmations == 1, "The selected MMC row did not start the genuine existing update Loader.");
                scope.Current.Cancel(); pause.Release(); pause.AwaitReturn(); scope.Wait(ModBase.LoadState.Canceled);
                Native.Check(Native.Get(scope.Current.Loaders.Single(), "LastRunningThread") is null && scope.Downloads.Count == 0, "Canceled page selection created a download/JVM producer before scheduling.");
            }
            Native.Check(data.Primary.Sha256 == before.Sha256 && data.Read("minecraft/saves/world/level.dat") == "personal world", "Canceling the real page update altered the instance.");
        } finally { Close(scope, page); }
        scope.Finish(); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-pack-update-page-bound: real official display projection, shared channels/search/current-file rows and exact selected-file Main Loader; canceled before producers; no JVM or game.");
    }

    internal static void RunStale(FixtureEnvironment environment) {
        using var scope = new MmcPlatformOriginUiTaskChecks.Scope(environment, "pack-page-stale", null);
        using var hooks = new Hooks(); var data = scope.Data; data.SeedOrigin();
        var transport = (Func<Uri, CancellationToken, Task<string>>)Native.Get(data.Catalog, "request")!;
        using var entered = new ManualResetEventSlim();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0, choices = 0;
        var delayed = MmcPlatformOriginCatalog.ForLoopbackFixtures(async (uri, token) => {
            Interlocked.Increment(ref requests);
            if (uri.AbsolutePath == "/v2/project/ProjectA/version") { entered.Set(); await released.Task.ConfigureAwait(false); }
            return await transport(uri, token).ConfigureAwait(false);
        }, MmcPlatformApiPolicy.ForLoopbackFixtures(new("http://127.0.0.1:1/v2/"), new("http://127.0.0.1:1/v1/")));
        hooks.Set("PCL.MmcPackageUpdateUi", "SelectArchive", new Func<string>(() => { choices++; return ""; }));
        hooks.Input(_ => { choices++; return null; });
        var page = Page(data, delayed);
        try {
            page.Refresh(); scope.Until(() => entered.IsSet, "delayed genuine catalog read");
            var oldRead = Read(page); var otherOwner = new ModMinecraft.McInstance(data.Version).Load(); LauncherInstanceContext.Instance = otherOwner;
            page.Refresh(); released.SetResult(); scope.Until(() => oldRead.IsCompleted && Read(page).IsCompleted, "stale metadata producer drains");
            Native.Check(Rows(page).Length == 0 && !Button(page, "BtnLocal").IsEnabled && !Button(page, "BtnOrigin").IsEnabled &&
                !(bool)Native.Call(typeof(PageInstancePackUpdate), "IsManagedOwner", page, otherOwner, data.Selection().ExpectedInstanceId)!, "Refreshing or a stale catalog adopted a different selected owner.");
            LauncherInstanceContext.Instance = data.Owner; Refresh(scope, page);
            string projection = Path.Combine(data.Version, "managed.json"); byte[] original = File.ReadAllBytes(projection);
            File.WriteAllText(projection, "{ corrupted owned projection");
            page.Refresh(); AwaitRead(scope, page);
            Native.Check(Text(page, "LabListStatus").Contains("校验未通过", StringComparison.Ordinal) && !Button(page, "BtnLocal").IsEnabled, "Damaged MMC fell back to ordinary local updating.");
            File.WriteAllBytes(projection, original); Refresh(scope, page);

            var files = (FileTaskScope)Native.Get(scope, "files")!;
            using var replacement = new MmcPlatformOriginUiTaskChecks.Data(files, "pack-page-replacement", null);
            files.SelectMinecraft(data.Game); LauncherInstanceContext.Instance = data.Owner;
            InstanceConfigProviders.FlushAndDisposeWithin(data.Version);
            string retained = Path.Combine(data.Root, "retained-original-instance");
            Native.Check(Native.Full(data.Version).StartsWith(Native.Full(files.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Native.Full(retained).StartsWith(Native.Full(files.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Owned replacement fixture escaped its root.");
            Directory.Move(data.Version, retained); Copy(replacement.Version, data.Version);
            var replaced = data.Primary; int requestsBefore = requests;
            page.Refresh(); AwaitRead(scope, page);
            ClickButton(page, "BtnLocal"); ClickButton(page, "BtnOrigin");
            Native.Check(replaced.Definition.InstanceId != (Guid)Native.Get(page, "mmcInstanceId")!, "Replacement fixture retained the old captured ID.");
            Native.Check(!Button(page, "BtnLocal").IsEnabled && !Button(page, "BtnOnline").IsEnabled && choices == 0 && requests == requestsBefore && data.Primary.Sha256 == replaced.Sha256,
                "A same-path replacement was adopted or prompted/read online metadata from the old page.");
        } finally { released.TrySetResult(); Close(scope, page); }
        scope.Finish(); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-pack-update-page-stale: actual delayed catalog drain, owner switch, damaged projection and same-path different-ID replacement refusal; no ordinary fallback/JVM/game.");
    }

    internal static void RunOrdinary(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        string path = Path.Combine(files.Root, "ordinary-pack-page"); Directory.CreateDirectory(path);
        var owner = new ModMinecraft.McInstance(path); FileTaskScope.CacheInstance(owner); LauncherInstanceContext.Instance = owner;
        object package = Activator.CreateInstance(Native.Type("PCL.PackUpdatePackage"), true)!;
        Native.Set(package, "Name", "Ordinary pack"); Native.Set(package, "Version", "ordinary 1.0"); Native.Set(package, "Minecraft", "1.21.11");
        Native.Set(package, "Source", new JObject { ["platform"] = "Modrinth", ["project"] = "ordinary-project", ["version"] = "ordinary-current" });
        Directory.CreateDirectory(Path.Combine(path, "PCL"));
        File.WriteAllText(Path.Combine(path, "PCL", "ModpackOrigin.json"), ((JObject)Native.Static("PCL.ModpackUpdate", "Manifest", package)!).ToString());
        var page = new PageInstancePackUpdate(); int reads = 0;
        Native.Set(page, "ReadVersions", new Func<JObject, List<ResourceVersion>>(_ => {
            reads++; return [new() { Id = "ordinary-current", Version = "ordinary 1.0", Display = "ordinary 1.0", ReleaseTypeCode = 1, GameVersions = ["1.21.11"] }];
        }));
        Native.Set(page, "ReadMmcCatalog", new Func<MmcPlatformOriginCatalog>(() => throw new IOException("Ordinary pages cannot enter MMC metadata.")));
        page.Refresh(); VisualAssetChecks.Pump(() => Text(page, "LabListStatus").StartsWith("已获取", StringComparison.Ordinal), "Ordinary page catalog did not finish.", 5000);
        Native.Check(reads == 1 && Button(page, "BtnOrigin").Visibility == Visibility.Collapsed && Button(page, "BtnOnline").IsEnabled &&
            Button(page, "BtnLocal").IsEnabled && Button(page, "BtnBackups").Content?.ToString() == "更新备份" && Text(page, "LabVersion") == "ordinary 1.0" && Current(Rows(page).Single()),
            "The shared MMC page changed ordinary update metadata, controls or current-version rendering.");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Native.Call(typeof(FileTaskScope), "FinishActualTasks", files); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-pack-update-page-ordinary: genuine BAML retains ordinary origin/catalog/backups/channels and never invokes MMC metadata; no JVM or game.");
    }

    private static PageInstancePackUpdate Page(MmcPlatformOriginUiTaskChecks.Data data, MmcPlatformOriginCatalog? catalog = null) {
        LauncherInstanceContext.Instance = data.Owner;
        var page = new PageInstancePackUpdate(data.Owner, data.Selection().ExpectedInstanceId);
        Native.Check(page.GetType().Assembly == Native.Main && page.FindName("PanVersions") is StackPanel && page.FindName("PanReleaseTypes") is WrapPanel, "The fixture is not the real compiled pack update page.");
        Native.Set(page, "ReadMmcCatalog", new Func<MmcPlatformOriginCatalog>(() => catalog ?? data.Catalog)); return page;
    }
    private static void SetDisplay(MmcPlatformOriginUiTaskChecks.Data data, string id, string name, string type, string date) {
        var versions = (ConcurrentDictionary<string, JObject>)Native.Get(data, "versions")!;
        var metadata = versions[id]; metadata["name"] = name; metadata["version_number"] = name; metadata["version_type"] = type; metadata["date_published"] = date; metadata["game_versions"] = new JArray("1.7.10");
    }
    private static void Refresh(MmcPlatformOriginUiTaskChecks.Scope scope, PageInstancePackUpdate page) { page.Refresh(); AwaitRead(scope, page); }
    private static Task Read(PageInstancePackUpdate page) => (Task)Native.Property(typeof(PageInstancePackUpdate), "MmcReadCompletion").GetValue(page)!;
    private static void AwaitRead(MmcPlatformOriginUiTaskChecks.Scope scope, PageInstancePackUpdate page) {
        scope.Until(() => Read(page).IsCompleted, "actual page metadata reader drain"); Read(page).GetAwaiter().GetResult();
    }
    private static void Close(MmcPlatformOriginUiTaskChecks.Scope scope, PageInstancePackUpdate page) { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); AwaitRead(scope, page); }
    private static Button Button(PageInstancePackUpdate page, string name) => (Button)page.FindName(name);
    private static RadioButton Radio(PageInstancePackUpdate page, string name) => (RadioButton)page.FindName(name);
    private static string Text(PageInstancePackUpdate page, string name) => ((TextBlock)page.FindName(name)).Text;
    private static Button[] Rows(PageInstancePackUpdate page) => ((StackPanel)page.FindName("PanVersions")).Children.OfType<Button>().ToArray();
    private static bool Current(Button row) => Equals(row.Tag, "current-pack-version") &&
        ((StackPanel)((DockPanel)row.Content).Children[1]).Children.OfType<WrapPanel>().Single().Children
            .OfType<Border>().Any(badge => badge.Child is TextBlock { Text: "当前版本" });
    private static void ClickButton(PageInstancePackUpdate page, string name) => Button(page, name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static void ClickRow(Button row) => row.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static void Copy(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
    private sealed class Hooks : IDisposable {
        private readonly Dictionary<PropertyInfo, object?> previous = [];
        internal void Set(string type, string name, object value) {
            var property = Native.Property(Native.Type(type), name); previous.TryAdd(property, property.GetValue(null)); property.SetValue(null, value);
        }
        internal void Input(Func<MmcInstanceOrigin, object?> read) {
            var property = Native.Property(Native.Type("PCL.MmcPlatformOriginUi"), "ReadOriginInput");
            var origin = Expression.Parameter(typeof(MmcInstanceOrigin));
            var body = Expression.Convert(Expression.Invoke(Expression.Constant(read), origin), property.PropertyType.GenericTypeArguments[1]);
            Set("PCL.MmcPlatformOriginUi", "ReadOriginInput", Expression.Lambda(property.PropertyType, body, origin).Compile());
        }
        public void Dispose() { foreach (var item in previous) item.Key.SetValue(null, item.Value); }
    }
}
