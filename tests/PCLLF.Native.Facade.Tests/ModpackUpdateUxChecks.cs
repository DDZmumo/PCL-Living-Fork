using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;
using static PCLLF.Native.Facade.Tests.ModpackUpdateChecks;

namespace PCLLF.Native.Facade.Tests;

// Test-ModpackUpdateUx.ps1 (15c778c2...) live Native Main controls and downloader.
// UX-* IDs preserve its release-channel/modal/preview and slow-loopback checks.
// Uses the host's genuine Application with LF Startup already detached. No Show,
// ResourceAssembly replacement, external API, game start or mouse-cache tricks.
// Observable assertion map, original Test-ModpackUpdateUx.ps1 line -> ID:
// 66 -> UX-P1; 67 -> UX-P2; 68 -> UX-P3; 73 -> UX-P4;
// 118 (Release/count/date order) -> UX-01; 120 -> UX-02;
// 122 (selected DTO + confirm enabled) -> UX-03; 124 -> UX-04;
// 126 (clear selection/disabled confirm/one Beta) -> UX-05; 128 -> UX-06;
// 145 -> UX-07; 148, all four categories -> UX-08:<kind>;
// 151, every row/count/border/tooltip in four categories -> UX-09:<kind>;
// 155 -> UX-10; 163 -> UX-11; 164 (count + conflicts unchanged) -> UX-12.
// Original 129/156/158/162 renders are retained and encoded as GUID-owned PNGs;
// UX-13 checks their creation, not visual fidelity. Original 108 C# compilation
// is now the real host project build; original STA/startup/cleanup contracts are
// supplied by Program/FixtureEnvironment, retaining actual host EntryAssembly.
internal static class ModpackUpdateUxChecks
{
    internal static void Run(string root)
    {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        ModpackDownloadProcess.RunParent();
        VersionsAndPreview(Path.Combine(root, "preview"));
        ConflictChoicesAndOwnership(Path.Combine(root, "ownership-preview"));
        ConfigurationChoices();
        LargeNoticesScroll(Path.Combine(root, "large-preview"));
        AuthorActionsAndDisabledState(Path.Combine(root, "policy-preview"));
        ModpackLivePreviewChecks.Run(Path.Combine(root, "live-preview"));
        ModpackSidebarChecks.Run(Path.Combine(root, "pack-sidebar"));
        ModpackStageLayoutChecks.Run(Path.Combine(root, "stage-layout"));
        Console.WriteLine("PASS native modpack-update UX: UX-P1..P4 and UX-01..UX-13 (actual loopback, selection/modal and read-only preview).");
    }

    private static void ConfigurationChoices()
    {
        const string path = "config/keys.json";
        byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        var merge = (PackConfigMerge)Native.Call(typeof(PackConfigMerge), "TryCreate", null, path,
            Bytes("""{"cap":1,"speed":1,"mode":1}"""), Bytes("""{"cap":2,"speed":3,"mode":1}"""),
            Bytes("""{"cap":8,"speed":9,"mode":2}"""))!;
        object package = New("PackUpdatePackage");
        Set(package, "Name", "Configuration fixture"); Set(package, "Version", "2"); Set(package, "Minecraft", "1.21.11");
        object plan = New("PackUpdatePlan"); Set(plan, "Package", package);
        object change = New("PackUpdateChange"), incoming = New("PackUpdateFile");
        Set(change, "Relative", path); Set(change, "Before", "sha1:fixture"); Set(change, "Incoming", incoming);
        Set(change, "ConfigurationMerge", merge);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Set(change, "AuthorConfigurationKeys", keys);
        ((IList)Get(plan, "Changes")!).Add(change);
        var preview = (FrameworkElement)Construct("ModpackUpdatePreviewView", plan, "1.21.11", new Dictionary<ModLoaders, string>());
        Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(Native.Type("PCL.ModpackUpdatePreviewView+ChangeKind"), "Updated"));
        var options = Descendants(FileCards(preview).Single()).OfType<RadioButton>()
            .Where(option => ((string)option.Content).StartsWith("玩家值", StringComparison.Ordinal) || ((string)option.Content).StartsWith("作者值", StringComparison.Ordinal))
            .ToDictionary(option => ((string)option.Content));
        Native.Check(options.Count == 4 && options["玩家值 · 2"].IsChecked == true && options["玩家值 · 3"].IsChecked == true,
            "Configuration choices default to personal values and only show actual conflicts. Observed " +
            string.Join(", ", options.Select(pair => pair.Key + "=" + pair.Value.IsChecked)));
        options["作者值 · 8"].IsChecked = true;
        options["作者值 · 9"].IsChecked = true;
        Native.Check(keys.SetEquals(["/cap", "/speed"]), "Selecting one conflict changed another key.");
        options["玩家值 · 3"].IsChecked = true;
        Native.Check(keys.SetEquals(["/cap"]), "Returning one conflict to its personal value changed another key.");
        Console.WriteLine("PASS native configuration preview: default personal values and independent same-key choices.");
    }

    internal static void DownloadProgress(string folder)
    {
        Directory.CreateDirectory(folder);
        byte[] bytes = new byte[2 * 1024 * 1024]; new Random(731).NextBytes(bytes);
        string hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        using SlowLoopback server = new(bytes);
        List<ModNet.NetFile> files = new() { new(new[] { server.Url }, Path.Combine(folder, "update.jar"), new MeloongCore.FileChecker { Hash = hash }) };
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim invocationExited = new();
        Thread? invocationThread = null;
        bool invoked = false;
        ModLoader.LoaderTask<string, string> step = new("actual pack download", task =>
        {
            Volatile.Write(ref invocationThread, Thread.CurrentThread);
            Volatile.Write(ref invoked, true);
            try
            {
                task.Progress = 0.15;
                Update("DownloadFilesWithProgress", files, cancellation.Token, task.CreateSyncProgressProvider(0.15, 0.65));
            }
            finally { invocationExited.Set(); }
        });
        bool advanced = false;
        double largest = 0.15;
        try
        {
            step.Start("fixture");
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (step.State == ModBase.LoadState.Loading && DateTime.UtcNow < deadline)
            {
                double progress = step.Progress;
                if (progress > 0.15 && progress < 0.65) { advanced = true; largest = Math.Max(largest, progress); }
                Thread.Sleep(10);
            }
            Check(step.State == ModBase.LoadState.Finished, "UX-P1", "loopback download did not finish: " + step.Error);
            Check(File.Exists(files[0].LocalPath) && new FileInfo(files[0].LocalPath).Length == bytes.Length, "UX-P2", "downloaded content missing");
            Check(File.ReadAllBytes(files[0].LocalPath).SequenceEqual(bytes), "UX-P2-bytes", "download did not preserve actual bytes/hash");
            Check(advanced, "UX-P3", "reproduced 15% frozen progress despite " + server.Sent + " loopback bytes");
            using CancellationTokenSource canceled = new(); canceled.Cancel();
            bool rejected = false;
            try { Update("DownloadFilesWithProgress", files, canceled.Token, step.CreateSyncProgressProvider(0.15, 0.65)); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "UX-P4", "pre-canceled real downloader restarted");
            Console.WriteLine("PASS native modpack-update parent download progressed from 15% through " + largest.ToString("P1", CultureInfo.InvariantCulture) + " before completion (loopback only).");
        }
        finally
        {
            try
            {
                // Cancel the real synchronous download before the outer task.
                // A task's terminal state alone does not mean its IO returned.
                cancellation.Cancel();
                Thread? taskThread = step.LastRunningThread;
                if (step.State == ModBase.LoadState.Loading) step.Cancel();
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while ((Volatile.Read(ref invoked) && !invocationExited.IsSet) || Volatile.Read(ref invocationThread)?.IsAlive == true ||
                    taskThread?.IsAlive == true || Volatile.Read(ref ModNet.NetTaskThreadCount) != 0 ||
                    files.Any(file => file.Threads is not null && file.Threads.Any(part => part.Thread?.IsAlive == true)))
                {
                    if (DateTime.UtcNow >= deadline)
                        ModpackUpdateCleanup.TerminateChild("UX downloader still active before loopback/roots disposal: state=" + step.State + ", invocationExited=" + invocationExited.IsSet + ", networkThreads=" + Volatile.Read(ref ModNet.NetTaskThreadCount));
                    Thread.Sleep(2);
                }
            }
            catch (Exception error)
            {
                ModpackUpdateCleanup.TerminateChild("UX downloader cancellation/completion barrier failed before loopback/roots disposal: " + error);
            }
        }
        using FileStream exclusive = new(files[0].LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(exclusive.Length == bytes.Length, "UX-cleanup-exclusive", "completed download retained a file handle or changed length");
    }

    private static void VersionsAndPreview(string folder)
    {
        Directory.CreateDirectory(folder);
        List<ResourceVersion> versions = new();
        foreach ((string display, ResourceVersion.ReleaseTypes type, string date, string game) in new[]
        {
            ("15.0.0-beta.3 for 26.3", ResourceVersion.ReleaseTypes.Beta, "2026-11-01", "26.3"),
            ("14.1.2 for 26.2", ResourceVersion.ReleaseTypes.Release, "2026-11-01", "26.2"),
            ("14.1.1 for 26.2", ResourceVersion.ReleaseTypes.Release, "2026-10-11", "26.2"),
            ("15.0.0-alpha.5 for 26.3", ResourceVersion.ReleaseTypes.Alpha, "2026-10-05", "26.3")
        })
            versions.Add(new ResourceVersion { Id = display, Display = display, ReleaseType = type, ReleaseDate = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), GameVersions = new() { game }, ModLoaders = ModLoaders.Fabric });
        object selector = Construct("ModpackVersionSelectView", versions, "Fabulously Optimized", "13.0.0 for 1.21.11");
        IList selections = (IList)Get(selector, "Selections")!;
        static RadioButton Choice(object? row) => Descendants((FrameworkElement)row!).OfType<RadioButton>().Single();
        Check(Property(selector, "ActiveChannel")!.ToString() == "Release" && selections.Count == 2 && AutomationProperties.GetName(Choice(selections[0])) == "14.1.2 for 26.2", "UX-01", "channels mixed or releases not sorted by date");
        GlassMessageDialog modal = Dialog((FrameworkElement)selector, "选择整合包版本", "下载并预览");
        Button confirm = (Button)((Button[])Native.Get(modal, "_buttons")!)[0];
        Check(!confirm.IsEnabled, "UX-02", "download enabled before choosing a version");
        RadioButton second = Choice(selections[1]);
        second.IsChecked = true;
        Check(ReferenceEquals(Property(selector, "Selected"), ((FrameworkElement)selections[1]!).Tag) && confirm.IsEnabled, "UX-03", "grouped selection returned wrong version or left confirm disabled");
        RadioButton first = Choice(selections[0]);
        first.IsChecked = true;
        Check(selections.Cast<object>().Select(Choice).Count(item => item.IsChecked == true) == 1 && ReferenceEquals(Property(selector, "Selected"), ((FrameworkElement)selections[0]!).Tag), "UX-04", "native radio selection not exclusive");
        Native.Call(selector.GetType(), "ShowChannel", selector, ResourceVersion.ReleaseTypes.Beta);
        selections = (IList)Get(selector, "Selections")!;
        Check(Property(selector, "Selected") is null && !confirm.IsEnabled && selections.Count == 1, "UX-05", "channel switch retained invisible selection");
        Choice(selections[0]).IsChecked = true;
        Check(((ResourceVersion)Property(selector, "Selected")!).Id == "15.0.0-beta.3 for 26.3", "UX-06", "beta selection used stale ungrouped index");
        Render(modal, Path.Combine(folder, "modpack-version-selector.png"));

        object package = New("PackUpdatePackage");
        Set(package, "Name", "Fabulously Optimized"); Set(package, "Version", "15.0.0-beta.3"); Set(package, "Minecraft", "26.3");
        ((IDictionary)Get(package, "Loaders")!).Add(ModLoaders.Fabric, "0.19.3");
        object plan = New("PackUpdatePlan"); Set(plan, "Package", package);
        Dictionary<string, int> expected = new() { ["Added"] = 35, ["Updated"] = 46, ["Removed"] = 50, ["Preserved"] = 12 };
        foreach ((string kind, int count) in expected)
            for (int index = 0; index < count; index++)
            {
                object change = New("PackUpdateChange");
                string relative = "mods/" + new[] { "Iris", "Sodium", "BetterGrassify", "ConfigManager", "Continuity", "DynamicLights" }[index % 6] + "-fabric-1.21.11-" + index + ".jar";
                string reason = "作者文件发生变化";
                if (kind == "Preserved")
                {
                    Set(change, "Conflict", true); reason = "保留玩家删除或禁用";
                    if (index % 2 == 0) { relative = "config/MySettings-" + index + ".toml"; reason = "保留个人文件或修改"; }
                }
                Set(change, "Relative", relative); Set(change, "Reason", reason);
                if (kind != "Added") Set(change, "Before", "sha1:fixture-before");
                if (kind != "Removed") { object incoming = New("PackUpdateFile"); Set(incoming, "Relative", relative); Set(change, "Incoming", incoming); }
                ((IList)Get(plan, "Changes")!).Add(change);
            }
        object preview = Construct("ModpackUpdatePreviewView", plan, "1.21.11", new Dictionary<ModLoaders, string> { [ModLoaders.Fabric] = "0.19.3" });
        var options = Descendants((DependencyObject)preview).OfType<ToggleButton>()
            .Where(option => option is RadioButton or CheckBox).ToArray();
        Native.Check(options.Length > 0 && options.All(option => option.GetType() == (option is RadioButton ? typeof(RadioButton) : typeof(CheckBox)) &&
            ReferenceEquals(option.Style, GlassDialogControls.Style(option is RadioButton ? "GlassPresentationRadioBox" : "GlassPresentationCheckBox"))),
            "Preview options do not use the native controls and shared glass styles.");
        IDictionary counts = (IDictionary)Get(preview, "Counts")!;
        Type kinds = Native.Type("PCL.ModpackUpdatePreviewView+ChangeKind");
        Check(Property(preview, "ActiveKind")!.ToString() == "Preserved", "UX-07", "personal changes not visible by default");
        foreach ((string kind, int count) in expected)
        {
            object value = Enum.Parse(kinds, kind);
            Check((int)counts[value]! == count, "UX-08:" + kind, "incorrect category count");
            Native.Call(preview.GetType(), "ShowKind", preview, value);
            Border[] boxes = FileCards(preview);
            Check(boxes.Length == count && boxes.All(box => box.BorderThickness.Left == 1 && !string.IsNullOrWhiteSpace(box.ToolTip?.ToString())), "UX-09:" + kind, "file border or full path tooltip missing");
        }
        Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Preserved"));
        GlassMessageDialog previewModal = Dialog((FrameworkElement)preview, "整合包更新预览", "保留个人修改并更新");
        Render(previewModal, Path.Combine(folder, "modpack-update-preview.png"));
        Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Removed"));
        Render(previewModal, Path.Combine(folder, "modpack-removals-preview.png"));
        int before = Changes(plan).Count, conflicts = Changes(plan).Count(Conflict);
        var nativeHost = Native.Get(previewModal, "_nativeContentHost") as ContentControl
            ?? throw new InvalidOperationException("The update preview content host is missing.");
        nativeHost.Content = null;
        FrameworkElement compact = (FrameworkElement)preview; compact.MaxHeight = 240; compact.Width = 460;
        Render(compact, Path.Combine(folder, "modpack-preview-compact.png"), 470, 250);
        Check(((UniformGrid)Get(preview, "Filters")!).Columns == 4, "UX-11", "compact categories unnecessarily consumed a second row");
        compact.Width = 340;
        Render(compact, Path.Combine(folder, "modpack-preview-narrow.png"), 350, 250);
        Check(((UniformGrid)Get(preview, "Filters")!).Columns == 2, "UX-11-narrow", "narrow categories did not wrap to keep their labels readable");
        Check(Changes(plan).Count == before && Changes(plan).Count(Conflict) == conflicts, "UX-12", "preview mutated original plan");
        Check(new[] { "modpack-version-selector.png", "modpack-update-preview.png", "modpack-removals-preview.png", "modpack-preview-compact.png" }.All(name => new FileInfo(Path.Combine(folder, name)).Length > 8), "UX-13", "actual offscreen WPF renders missing");
    }

    private static void ConflictChoicesAndOwnership(string folder)
    {
        Directory.CreateDirectory(folder);
        object package = New("PackUpdatePackage");
        Set(package, "Name", "Ownership fixture"); Set(package, "Version", "2"); Set(package, "Minecraft", "26.1.2");
        ((IDictionary)Get(package, "Loaders")!).Add(ModLoaders.Fabric, "0.19.3");
        object plan = New("PackUpdatePlan"); Set(plan, "Package", package);
        ((IList)Get(plan, "Warnings")!).Add("两个同哈希本地候选需要核对。");
        ((IList)Get(plan, "RuntimeWarnings")!).Add("personal.jar 声明 minecraft <1.22，与目标 26.1.2 不兼容。");

        object Add(string relative, PackUpdateOwnership ownership, bool conflict, bool displayOnly, bool author = true, string? local = null, bool incoming = true)
        {
            object change = New("PackUpdateChange");
            Set(change, "Relative", relative); Set(change, "LocalRelative", local); Set(change, "Ownership", ownership);
            Set(change, "Conflict", conflict); Set(change, "DisplayOnly", displayOnly); Set(change, "AllowAuthor", author);
            Set(change, "Reason", ownership == PackUpdateOwnership.PersonalAdded ? "个人新增，不属于作者清单" : "fixture ownership evidence");
            if (local is not null) Set(change, "Before", "sha1:fixture-before");
            if (incoming) { object file = New("PackUpdateFile"); Set(file, "Relative", relative); Set(change, "Incoming", file); }
            ((IList)Get(plan, "Changes")!).Add(change);
            return change;
        }
        Add("mods/player-extra.jar", PackUpdateOwnership.PersonalAdded, false, true, author: false, local: "mods/player-extra.jar", incoming: false);
        const string lithiumLocal = "mods/[锂] lithium-fabric-0.21.4+mc1.21.11.jar";
        const string lithiumNext = "mods/lithium-fabric-0.24.2+mc26.1.2.jar";
        Add(lithiumNext, PackUpdateOwnership.AuthorRenamed, false, false, local: lithiumLocal);
        object modified = Add("mods/personal.jar", PackUpdateOwnership.PersonalModified, true, false, local: "mods/personal.jar");
        object disabled = Add("mods/disabled.jar", PackUpdateOwnership.Disabled, true, false, local: "mods/disabled.jar.disabled");
        object ambiguous = Add("mods/ambiguous.jar", PackUpdateOwnership.Ambiguous, true, false);
        ((IList)Get(ambiguous, "Candidates")!).Add("mods/copy-one.jar");
        ((IList)Get(ambiguous, "Candidates")!).Add("mods/copy-two.jar");
        Add("mods/unknown.jar", PackUpdateOwnership.Unknown, true, false, author: false);
        object preview = Construct("ModpackUpdatePreviewView", plan, "1.21.11", new Dictionary<ModLoaders, string> { [ModLoaders.Fabric] = "0.18.4" });
        Type kinds = Native.Type("PCL.ModpackUpdatePreviewView+ChangeKind");
        void Show(string kind) => Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, kind));
        Border Row(string relative) => FileCards(preview).Single(box => box.ToolTip?.ToString()?.Split('\r')[0] == relative);
        string[] Texts(DependencyObject element) => Descendants(element).OfType<TextBlock>().Select(block => block.Text).ToArray();
        RadioButton[] Radios(Border row) => Descendants(row).OfType<RadioButton>().ToArray();

        Show("Preserved");
        Check(Texts(Row("mods/player-extra.jar")).Any(text => text.Contains("个人新增", StringComparison.Ordinal)) && Radios(Row("mods/player-extra.jar")).Length == 0,
            "UX-C1", "personal additions missing or incorrectly offered as deletable author files");
        Check(Texts(Row("mods/ambiguous.jar")).Any(text => text.Contains("mods/copy-one.jar", StringComparison.Ordinal) && text.Contains("mods/copy-two.jar", StringComparison.Ordinal)) && Radios(Row("mods/ambiguous.jar")).Length == 0,
            "UX-C2", "ambiguous matches hidden or offered an unsafe author selection");
        Check(Radios(Row("mods/unknown.jar")).Length == 0, "UX-C3", "unconfirmed ownership offered an unsafe author selection");
        RadioButton[] modifiedChoices = Radios(Row("mods/personal.jar"));
        Check(modifiedChoices.Length == 2 && modifiedChoices.Single(button => Equals(button.Tag, PackUpdateChoice.PreservePersonal)).IsChecked == true,
            "UX-C4", "true conflicts must default to preserve and offer per-item choices");
        modifiedChoices.Single(button => Equals(button.Tag, PackUpdateChoice.UseAuthor)).IsChecked = true;
        Check((PackUpdateChoice)Get(modified, "Choice")! == PackUpdateChoice.UseAuthor && (PackUpdateChoice)Get(disabled, "Choice")! == PackUpdateChoice.PreservePersonal,
            "UX-C5", "author selection did not reach the original DTO or changed another conflict");
        Show("Updated");
        Border renamed = Row(lithiumNext);
        Check(((StackPanel)Get(preview, "Rows")!).Children.OfType<Border>().Count() == 1 && Texts(renamed).Any(text => text.Contains(lithiumLocal, StringComparison.Ordinal) && text.Contains(lithiumNext, StringComparison.Ordinal)),
            "UX-C6", "pure rename must display one logical update with its real local and target paths");
        Show("Preserved");
        modifiedChoices = Radios(Row("mods/personal.jar"));
        Check(modifiedChoices.Single(button => Equals(button.Tag, PackUpdateChoice.UseAuthor)).IsChecked == true,
            "UX-C7", "changing filters lost the explicit author choice");
        modifiedChoices.Single(button => Equals(button.Tag, PackUpdateChoice.PreservePersonal)).IsChecked = true;
        Check((PackUpdateChoice)Get(modified, "Choice")! == PackUpdateChoice.PreservePersonal, "UX-C8", "returning to preserve did not reach the original DTO");
        RadioButton disabledAuthor = Radios(Row("mods/disabled.jar")).Single(button => Equals(button.Tag, PackUpdateChoice.UseAuthor));
        Check(Descendants(Row("mods/disabled.jar")).OfType<CheckBox>().Any(state => state.IsChecked == true && state.Content?.ToString()?.Contains("保持禁用", StringComparison.Ordinal) == true),
            "UX-C9", "disabled updates did not make their disabled result explicit");
        disabledAuthor.IsChecked = true;
        Check((PackUpdateChoice)Get(disabled, "Choice")! == PackUpdateChoice.UseAuthor && Property(disabled, "DestinationRelative")!.ToString() == "mods/disabled.jar.disabled",
            "UX-C10", "choosing an author update re-enabled a disabled mod");
        string[] header = Texts((StackPanel)Get(preview, "Header")!);
        Check(header.Any(text => text.Contains("1.21.11 → 26.1.2", StringComparison.Ordinal)) && header.Any(text => text.Contains("0.18.4 → 0.19.3", StringComparison.Ordinal)),
            "UX-C11", "Minecraft/loader changes omitted before and after versions");
        Expander[] notices = Descendants((StackPanel)Get(preview, "Rows")!).OfType<Expander>()
            .Where(details => details.Header?.ToString()?.StartsWith("需要核对", StringComparison.Ordinal) == true || details.Header?.ToString()?.StartsWith("保留模组", StringComparison.Ordinal) == true).ToArray();
        Check(notices.Length == 2 && notices.Any(details => details.Header?.ToString()?.Contains("兼容性", StringComparison.Ordinal) == true && Texts(details).Any(text => text.Contains("不兼容", StringComparison.Ordinal))),
            "UX-C12", "retained-mod compatibility evidence was not accessible in preview");
        Render((FrameworkElement)preview, Path.Combine(folder, "modpack-ownership-and-choices.png"));
        Check(new FileInfo(Path.Combine(folder, "modpack-ownership-and-choices.png")).Length > 8, "UX-C13", "new choices preview did not render through actual WPF");
        Console.WriteLine("PASS native modpack-update ownership preview: UX-C1..C13 (personal additions, one pure-rename row, independent choices, disabled state, ambiguity and runtime warnings).");
    }

    private static Border[] FileCards(object preview) => ((StackPanel)Get(preview, "Rows")!).Children.OfType<FrameworkElement>()
        .SelectMany(element => element is Border card ? new[] { card } : element is Grid grid ? grid.Children.OfType<Border>() : Enumerable.Empty<Border>()).ToArray();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (DependencyObject descendant in Descendants(dependency)) yield return descendant;
    }

    private static object? Property(object target, string name) => Native.Property(target.GetType(), name).GetValue(target);
    private static void LargeNoticesScroll(string folder)
    {
        Directory.CreateDirectory(folder);
        object package = New("PackUpdatePackage");
        Set(package, "Name", "Large update preview"); Set(package, "Version", "2"); Set(package, "Minecraft", "26.3");
        ((IDictionary)Get(package, "Loaders")!).Add(ModLoaders.Fabric, "0.20.0");
        object plan = New("PackUpdatePlan"); Set(plan, "Package", package);
        var graph = new PackDependencyGraphResult();
        for (int index = 0; index < 36; index++) {
            ((List<string>)Get(plan, "Warnings")!).Add("需要核对来源：mods/author-file-" + index + ".jar");
            ((List<string>)Get(plan, "RuntimeWarnings")!).Add("保留模组需要核对新运行时：mods/personal-" + index + ".jar");
            graph.Errors.Add("默认组合需要调整的依赖：dependency-" + index);
            graph.Warnings.Add("默认组合尚待确认的声明：unknown-" + index);
            object change = New("PackUpdateChange"), incoming = New("PackUpdateFile");
            string path = "mods/author-file-" + index + ".jar";
            Set(incoming, "Relative", path); Set(change, "Relative", path); Set(change, "Incoming", incoming);
            Set(change, "Before", "sha1:fixture"); Set(change, "Conflict", true); Set(change, "Ownership", PackUpdateOwnership.PersonalModified);
            ((IList)Get(plan, "Changes")!).Add(change);
        }
        Set(plan, "DependencyCheck", graph);
        FrameworkElement preview = (FrameworkElement)Construct("ModpackUpdatePreviewView", plan, "1.21.11", new Dictionary<ModLoaders, string> { [ModLoaders.Fabric] = "0.19.3" });
        preview.Width = 460; preview.MaxHeight = 240;
        ScrollViewer scroll = (ScrollViewer)Get(preview, "Scroller")!;
        Render(preview, Path.Combine(folder, "long-notices-collapsed.png"), 470, 250, () => {
            Grid cards = ((StackPanel)Get(preview, "Rows")!).Children.OfType<Grid>().Single();
            Native.Check(cards.ColumnDefinitions.Count == 1, "Narrow conflict cards did not return to one column.");
            Native.Check(scroll.ViewportHeight >= 80 && scroll.ScrollableHeight > 0,
                "Long update notices consumed the fixed header and starved the file-list scroll viewport: viewport=" + scroll.ViewportHeight);
            double collapsed = scroll.ScrollableHeight;
            Expander[] details = Descendants((StackPanel)Get(preview, "Rows")!).OfType<Expander>().ToArray();
            Native.Check(details.Length == 5 && details.All(item => !item.IsExpanded), "Preview evidence is missing or expanded into a wall of text by default.");
            foreach (Expander detail in details) detail.IsExpanded = true;
            preview.UpdateLayout();
            Native.Check(scroll.ViewportHeight >= 80 && scroll.ScrollableHeight > collapsed, "Expanded evidence escaped the shared scroll viewport.");
            Native.Check(Descendants(preview).OfType<ScrollViewer>().Count() == 1, "Nested evidence scroll viewers can trap the mouse wheel.");
            scroll.ScrollToBottom(); preview.UpdateLayout();
            Native.Check(scroll.VerticalOffset > 0, "Large update preview could not scroll to the final file.");
        });
        foreach (Expander detail in Descendants((StackPanel)Get(preview, "Rows")!).OfType<Expander>()) detail.IsExpanded = false;
        preview.Width = double.NaN;
        GlassMessageDialog modal = Dialog(preview, "整合包更新预览", "按以上选择更新");
        modal.MaxHeight = 460; preview.MaxHeight = 240;
        Render(modal, Path.Combine(folder, "long-preview-modal.png"), 840, 520, () => {
            scroll.ScrollToTop(); modal.UpdateLayout();
            FrameworkElement confirm = (FrameworkElement)((Button[])Native.Get(modal, "_buttons")!)[0];
            Rect button = confirm.TransformToAncestor(modal).TransformBounds(new Rect(confirm.RenderSize));
            Native.Check(button.Height > 0 && button.Top >= 0 && button.Bottom <= modal.ActualHeight + 1, "Compact preview clipped its confirmation action.");
            Native.Check(scroll.ViewportHeight >= 80 && scroll.ScrollableHeight > 0, "Actual modal wrapper removed the file-list viewport.");
            var host = Native.Get(modal, "_nativeContentHost") as ContentControl
                ?? throw new InvalidOperationException("The update preview content host is missing.");
            Native.Check(ReferenceEquals(host.Content, preview) && Math.Abs(preview.ActualWidth - host.ActualWidth) <= 1,
                "Preview content left unused horizontal space inside the modal.");
            Grid cards = ((StackPanel)Get(preview, "Rows")!).Children.OfType<Grid>().Single();
            Border[] boxes = cards.Children.Cast<Border>().ToArray();
            Native.Check(cards.ColumnDefinitions.Count == 2 && boxes.Length == 36, "Wide conflict preview did not use two columns for all mods.");
            Rect left = boxes[0].TransformToAncestor(preview).TransformBounds(new Rect(boxes[0].RenderSize));
            Rect right = boxes[1].TransformToAncestor(preview).TransformBounds(new Rect(boxes[1].RenderSize));
            Native.Check(Math.Abs(left.Top - right.Top) <= 1 && left.Right <= right.Left + 1 && right.Right <= preview.ActualWidth + 1,
                "Conflict cards overlapped or escaped their two-column viewport.");
            DependencyObject? ancestor = VisualTreeHelper.GetParent(scroll);
            for (; ancestor is not null && ancestor != modal; ancestor = VisualTreeHelper.GetParent(ancestor))
                Native.Check(ancestor is not ScrollViewer, "An outer scroll viewer can intercept the preview list's mouse wheel.");
            Native.Check(ancestor == modal, "The preview list was not presented inside the production modal.");
            MouseWheelEventArgs wheel = new(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            scroll.RaiseEvent(wheel);
            Native.Check(wheel.Handled, "The genuine native preview list did not accept the mouse wheel.");
        });
        Console.WriteLine("PASS large modpack preview: bounded compact header, collapsed/expanded evidence in one scroll viewport, reachable final file, real modal footer and mouse-wheel routing.");
    }
    private static void AuthorActionsAndDisabledState(string folder)
    {
        Directory.CreateDirectory(folder);
        object package = New("PackUpdatePackage"), plan = New("PackUpdatePlan");
        Set(package, "Name", "Author priority"); Set(package, "Version", "2"); Set(package, "Minecraft", "1.21.11");
        ((IDictionary)Get(package, "Loaders")!).Add(ModLoaders.Fabric, "0.19.3"); Set(plan, "Package", package);
        object Add(string name, bool incoming, PackUpdateOwnership owner, bool conflict = false, bool compatible = false) {
            object change = New("PackUpdateChange"); Set(change, "Relative", "mods/" + name + ".jar"); Set(change, "Before", "sha1:fixture");
            Set(change, "LocalRelative", "mods/" + name + "-old.jar"); Set(change, "Ownership", owner); Set(change, "Conflict", conflict);
            Set(change, "PersonalCompatible", compatible); Set(change, "AllowPreserveAuthor", owner == PackUpdateOwnership.AuthorUnchanged || compatible);
            Set(change, "Choice", compatible || conflict ? PackUpdateChoice.PreservePersonal : PackUpdateChoice.UseAuthor);
            if (incoming) { object file = New("PackUpdateFile"); Set(file, "Relative", "mods/" + name + ".jar"); Set(change, "Incoming", file); }
            ((IList)Get(plan, "Changes")!).Add(change); return change;
        }
        object update = Add("normal", true, PackUpdateOwnership.AuthorUnchanged), removal = Add("removed", false, PackUpdateOwnership.AuthorUnchanged);
        Add("personal", true, PackUpdateOwnership.PersonalModified, compatible: true);
        Add("conflicting", true, PackUpdateOwnership.PersonalModified, conflict: true);
        object duplicate = Add("duplicate", false, PackUpdateOwnership.PersonalAdded, conflict: true);
        Set(duplicate, "Operation", PackUpdateOperation.DisablePersonal); Set(duplicate, "Choice", PackUpdateChoice.UseAuthor);
        Set(duplicate, "DisabledDestination", "mods/duplicate-old.jar.disabled"); Set(duplicate, "Reason", "禁用原因：重复模组 ID shared_id：duplicate-old.jar、author-new.jar");
        object disabled = Add("disabled", true, PackUpdateOwnership.Disabled);
        object preview = Construct("ModpackUpdatePreviewView", plan, "1.21.11", new Dictionary<ModLoaders, string> { [ModLoaders.Fabric] = "0.19.3" });
        Type kinds = Native.Type("PCL.ModpackUpdatePreviewView+ChangeKind");
        var counts = (IDictionary)Get(preview, "Counts")!;
        Check((int)counts[Enum.Parse(kinds, "Updated")]! == 2 && (int)counts[Enum.Parse(kinds, "Removed")]! == 1 &&
            (int)counts[Enum.Parse(kinds, "Preserved")]! == 3, "UX-policy-categories", "Ordinary author changes were mislabeled as conflicts.");
        Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Preserved"));
        Border duplicateRow = FileCards(preview).Single(card => card.ToolTip!.ToString()!.StartsWith("mods/duplicate.jar", StringComparison.Ordinal));
        string[] duplicateText = Descendants(duplicateRow).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Check(duplicateText.Any(text => text.Contains("禁用目标：mods/duplicate-old.jar.disabled", StringComparison.Ordinal)) &&
            duplicateText.Any(text => text.Contains("重复模组 ID shared_id", StringComparison.Ordinal)) &&
            !duplicateText.Any(text => text.Contains("作者在新版中移除", StringComparison.Ordinal) || text.Contains("个人新增，保留", StringComparison.Ordinal)),
            "UX-personal-disable-description", "A personal duplicate still claimed author removal or unconditional retention.");
        Render((FrameworkElement)preview, Path.Combine(folder, "personal-duplicate-reason.png"), 740, 480);
        Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Updated"));
        Border row = FileCards(preview).Single(card => card.ToolTip!.ToString()!.StartsWith("mods/normal.jar", StringComparison.Ordinal));
        Descendants(row).OfType<RadioButton>().Single(button => Equals(button.Tag, PackUpdateChoice.PreservePersonal)).IsChecked = true;
        Check(!(bool)Native.Property(update.GetType(), "ApplyAuthor").GetValue(update)! &&
            Descendants(row).OfType<TextBlock>().Any(text => text.Visibility == Visibility.Visible && text.Text.Contains("不执行作者更新", StringComparison.Ordinal)),
            "UX-policy-preserve", "Keeping an ordinary author version did not affect execution or explain the outcome.");
        Border disabledRow = FileCards(preview).Single(card => card.ToolTip!.ToString()!.StartsWith("mods/disabled.jar", StringComparison.Ordinal));
        CheckBox state = Descendants(disabledRow).OfType<CheckBox>().Single();
        Check(state.IsChecked == true && Native.Property(disabled.GetType(), "DestinationRelative").GetValue(disabled)!.ToString()!.EndsWith(".disabled", StringComparison.Ordinal),
            "UX-disabled-default", "Disabled author update was not paired with an explicit keep-disabled choice.");
        Render((FrameworkElement)preview, Path.Combine(folder, "author-updates-and-disabled-state.png"), 740, 480);
        System.Windows.Application app = System.Windows.Application.Current;
        Window? previousMain = app.MainWindow; ShutdownMode previousShutdown = app.ShutdownMode;
        Window window = new() { Content = preview, Width = 740, Height = 480, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Show(); window.UpdateLayout();
            Check(state.IsLoaded, "UX-native-loaded", "Native checkbox interactions require a live offscreen presentation source.");
            state.IsChecked = false;
            Check(!(bool)Get(disabled, "KeepDisabled")! && Native.Property(disabled.GetType(), "DestinationRelative").GetValue(disabled)!.ToString() == "mods/disabled.jar",
                "UX-disabled-enable", "The explicit enabled-state choice did not reach the update plan.");
            Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Preserved"));
            Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Updated"));
            Check(!(bool)Get(disabled, "KeepDisabled")! && !(bool)Native.Property(update.GetType(), "ApplyAuthor").GetValue(update)! &&
                (bool)Native.Property(removal.GetType(), "ApplyAuthor").GetValue(removal)!, "UX-policy-independent", "Filter changes reset choices or changed unrelated author actions.");
            disabledRow = FileCards(preview).Single(card => card.ToolTip!.ToString()!.StartsWith("mods/disabled.jar", StringComparison.Ordinal));
            Check(Descendants(disabledRow).OfType<CheckBox>().Single().IsChecked == false,
                "UX-native-restored", "Native checkbox did not restore the explicit choice after changing filters.");
        }
        finally
        {
            window.Content = null; window.Close();
            app.MainWindow = previousMain; app.ShutdownMode = previousShutdown;
        }
    }
    private static object Construct(string name, params object?[] values)
    {
        var constructor = Native.Type("PCL." + name).GetConstructors(Native.Flags).Single(info => info.GetParameters().Length == values.Length);
        return constructor.Invoke(values);
    }
    private static GlassMessageDialog Dialog(FrameworkElement content, string title, string button)
    {
        ModMain.MyMsgBoxConverter converter = new() { Title = title, Text = "整合包预览", Button1 = button, Button2 = "取消", IsWarn = false, ContentFactory = () => content };
        GlassMessageDialog modal = Native.Dialog(converter);
        
        
        return modal;
    }
    private static void Render(FrameworkElement element, string path, int width = 820, int height = 640, Action? verifyLayout = null)
    {
        Grid host = new() { Background = new SolidColorBrush(Color.FromRgb(231, 239, 251)) };
        host.Children.Add(element);
        try
        {
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            verifyLayout?.Invoke();
            RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            string? previews = Environment.GetEnvironmentVariable("PCLLF_MODPACK_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(previews)) {
                Directory.CreateDirectory(previews);
                File.Copy(path, Path.Combine(previews, Path.GetFileName(Path.GetDirectoryName(path)) + "-" + Path.GetFileName(path)), true);
            }
        }
        finally { host.Children.Clear(); }
    }

    private sealed class SlowLoopback : IDisposable
    {
        private readonly byte[] bytes;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<TcpClient> clients = new();
        private readonly ConcurrentBag<Task> responses = new();
        private readonly Task accept;
        private int sent;
        internal string Url { get; }
        internal int Sent => Volatile.Read(ref sent);
        internal SlowLoopback(byte[] bytes)
        {
            this.bytes = bytes; listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/update.jar";
            accept = Task.Run(Accept);
        }
        private async Task Accept()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                    clients.Add(client); responses.Add(Task.Run(() => Respond(client)));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }
        private void Respond(TcpClient client)
        {
            using (client)
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    NetworkStream stream = client.GetStream(); List<byte> request = new(); int current;
                    while ((current = stream.ReadByte()) >= 0)
                    {
                        request.Add((byte)current); int count = request.Count;
                        if (count > 16384) throw new IOException("Loopback request exceeded fixture limit.");
                        if (count > 3 && request[count - 4] == 13 && request[count - 3] == 10 && request[count - 2] == 13 && request[count - 1] == 10) break;
                    }
                    Match range = Regex.Match(Encoding.ASCII.GetString(request.ToArray()), @"(?im)^Range:\s*bytes=(\d+)-");
                    long start = range.Success ? long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    if (start < 0 || start >= bytes.Length) throw new IOException("Unexpected loopback range.");
                    string header = "HTTP/1.1 " + (range.Success ? "206 Partial Content" : "200 OK") + "\r\nContent-Length: " + (bytes.Length - start) + "\r\nAccept-Ranges: bytes\r\n" + (range.Success ? "Content-Range: bytes " + start + "-" + (bytes.Length - 1) + "/" + bytes.Length + "\r\n" : "") + "Connection: close\r\n\r\n";
                    byte[] encoded = Encoding.ASCII.GetBytes(header); stream.Write(encoded);
                    for (long offset = start; offset < bytes.Length; offset += 16384)
                    {
                        if (stop.IsCancellationRequested) break;
                        int size = (int)Math.Min(16384, bytes.Length - offset); stream.Write(bytes, (int)offset, size);
                        Interlocked.Add(ref sent, size); Thread.Sleep(25);
                    }
                }
                catch (IOException) { /* Actual downloader can close resumed/racing loopback segments. */ }
                catch (SocketException) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            stop.Cancel(); listener.Stop();
            foreach (TcpClient client in clients) client.Dispose();
            Check(accept.Wait(2000), "UX-cleanup-accept", "loopback listener did not stop");
            foreach (TcpClient client in clients) client.Dispose();
            Check(Task.WaitAll(responses.ToArray(), 2000), "UX-cleanup-responses", "loopback handlers did not stop");
            stop.Dispose();
        }
    }
}
