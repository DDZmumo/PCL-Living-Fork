using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MeloongCore;
using Newtonsoft.Json;
using PCL;
using PCLCS;
using PCLLF.Presentation.Windowing;

namespace PCLLF.Native.Facade.Tests;

internal static class JavaLocalManagementChecks {
    internal static void Run(FixtureEnvironment environment) {
        using var scope = new VisualAssetChecks.Scope(environment, "java-local-management");
        var previous = ConfigProviders.Default;
        ConfigProviders.Default = new JsonConfigProvider(Path.Combine(scope.Root, "java-profile.json"));
        try {
            Discovery(scope.Root);
            ProbeExitCode();
            OpenJ9LaunchOptions();
            X86Libraries();
            VendorMarks();
            ManagementView();
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS local Java management: discovered home/PATH entries and real-path aliases, reused and invalidated the probe cache, preserved isolated validation, and filtered the projected list.");
        } finally { ConfigProviders.Default = previous; }
    }

    private static void Discovery(string root) {
        string temurin = CreateJava(Path.Combine(root, "temurin"));
        string semeru = CreateJava(Path.Combine(root, "semeru"));
        string aliasHome = Path.Combine(root, "jdk-alias");
        string alias = Path.Combine(aliasHome, "bin");
        Junction(aliasHome, Path.GetDirectoryName(temurin)!);
        try {
            var direct = (IEnumerable<string>)Native.Call(DiscoveryType(), "DirectFolders", null,
                string.Join(Path.PathSeparator, alias, temurin, @".\relative"), new[] { Path.GetDirectoryName(semeru) })!;
            string[] candidates = direct.ToArray();
            Native.Check(candidates.Length == 3 && candidates.Contains(semeru),
                "Explicit PATH entries and registered/java-home roots did not become Java candidates.");
            Native.Check(Identity(alias) == Identity(temurin), "The file identity did not resolve the parent junction.");

            // An existing alias fails to start. The same physical runtime is still discoverable via its valid bin path.
            Configs.JavaList.Set([new Java(alias)]);
            int probes = 0;
            Task<string> Probe(string executable, CancellationToken token) {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref probes);
                string folder = Path.GetDirectoryName(executable)!;
                if (folder.Equals(alias, StringComparison.OrdinalIgnoreCase)) return Task.FromResult("error: fixture alias");
                bool j9 = folder.Equals(semeru, StringComparison.OrdinalIgnoreCase);
                string vm = j9 ? "Eclipse OpenJ9 VM" : "OpenJDK 64-Bit Server VM";
                string vendor = j9 ? "IBM Semeru" : "Eclipse Adoptium";
                return Task.FromResult($"openjdk version \"21.0.7+6\"\n{vm}\n  java.vendor = {vendor}\n" +
                    $"  java.vm.name = {vm}\n  os.arch = amd64\n  sun.arch.data.model = 64\n  file.encoding = UTF-8\n");
            }
            var runner = (Func<string, CancellationToken, Task<string>>)Probe;
            void Refresh() => ((Task)Native.Call(typeof(JavaUtils), "RefreshCandidatesAsync", null, candidates,
                CancellationToken.None, null, runner)!).GetAwaiter().GetResult();

            Refresh();
            var entries = Configs.JavaList.Get()!.ToArray();
            Native.Check(entries.Length == 2 && probes == 3, "Aliased installations were duplicated or the valid fallback path was lost.");
            var t = entries.Single(java => java.Distribution == "Eclipse Temurin");
            var j = entries.Single(java => java.Distribution == "IBM Semeru");
            Native.Check(t.Architecture == "x64" && t.VirtualMachine == "HotSpot" && j.VirtualMachine == "OpenJ9",
                "The probe did not distinguish the distribution, architecture and VM.");
            string json = JsonConvert.SerializeObject(entries);
            Configs.JavaList.Set(new ConcurrentList<Java>(JsonConvert.DeserializeObject<Java[]>(json)!));
            Refresh();
            Native.Check(probes == 3, "An unchanged runtime was probed again after a catalogue round trip.");
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(temurin)!, "release"), "updated");
            Refresh();
            Native.Check(probes == 4, "Changing one installation did not invalidate just its cached probe.");
            t = Configs.JavaList.Get()!.Single(java => java.Distribution == "Eclipse Temurin");
            Native.Check(t.CheckAsync(CancellationToken.None, runner).GetAwaiter().GetResult() && probes == 5,
                "The persisted catalogue cache bypassed the independent launch probe.");
        } finally { Directory.Delete(aliasHome); }
    }

    private static void ProbeExitCode() {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "/d", "/c", "exit", "7" }) start.ArgumentList.Add(argument);
        var probe = (Task<string>)Native.Call(DiscoveryType(), "ReadProbeAsync", null, start,
            TimeSpan.FromSeconds(5), CancellationToken.None)!;
        try { _ = probe.GetAwaiter().GetResult(); }
        catch (IOException error) when (error.Message.EndsWith("7", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A failed probe process was reported as usable.");
    }

    private static void OpenJ9LaunchOptions() {
        Type engine = Native.Field(typeof(ModLaunch), "Service").FieldType;
        string[] original = ["-Xmx2048m", "-XX:+UseG1GC", "-XX:G1HeapRegionSize=32M",
            "-XX:+UseCompactObjectHeaders", "-XX:+UnlockExperimentalVMOptions", "-Xgcpolicy:gencon", "-Dtest.keep=1"];
        var managed = original.ToList();
        bool Apply(List<string> arguments, string vm, int mode, bool x86 = false) =>
            (bool)Native.Call(engine, "PrepareJavaGcArguments", null, arguments, vm, mode, x86)!;
        Native.Check(!Apply(managed, "OpenJ9", 4) && managed.SequenceEqual(["-Xmx2048m", "-Xgcpolicy:gencon", "-Dtest.keep=1"]),
            "OpenJ9 retained HotSpot flags or lost its own GC policy and other JVM arguments.");
        var manual = original.ToList();
        Native.Check(!Apply(manual, "OpenJ9", 3) && manual.SequenceEqual(original),
            "Choosing unmanaged GC changed the JVM arguments.");
        var hotspot = new List<string> { "-Xmx2048m", "-XX:+UseZGC" };
        Native.Check(Apply(hotspot, "HotSpot", 4) && hotspot.SequenceEqual(["-Xmx2048m"]),
            "Normal HotSpot GC replacement stopped working.");
        var x86 = new List<string> { "-Xmx1024m", "-XX:+UseZGC" };
        Native.Check(!Apply(x86, "HotSpot", 0, x86: true) && x86.SequenceEqual(["-Xmx1024m"]),
            "The 32-bit JVM retained a 64-bit GC option.");
    }

    private static void X86Libraries() {
        var source = Newtonsoft.Json.Linq.JObject.Parse("""
            {"libraries":[
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows"},
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-arm64"},
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-x86","rules":[{"action":"allow","os":{"name":"windows","arch":"x86"}}]},
              {"name":"example:legacy:1.0","url":"https://example.invalid/","downloads":null,"natives":{"windows":"natives-windows-${arch}"}}
            ]}
            """);
        object service = Native.Field(typeof(ModMinecraft), "Service").GetValue(null)!;
        var libraries = (List<ModMinecraft.McLibEntry>)Native.Call(service.GetType(), "McLibListGetWithJson", service, source,
            false, Path.GetTempPath(), true)!;
        Native.Check(libraries.Count == 2 && libraries.Any(item => item.OriginalName?.EndsWith("natives-windows-x86", StringComparison.Ordinal) == true) &&
            libraries.Any(item => item.IsNatives && item.LocalPath.EndsWith("natives-windows-32.jar", StringComparison.Ordinal) &&
                item.Url?.EndsWith("natives-windows-32.jar", StringComparison.Ordinal) == true) &&
            source["libraries"]![3]!["downloads"]?.Type == Newtonsoft.Json.Linq.JTokenType.Null,
            "The x86 launch selected x64/ARM DLLs or expanded the legacy architecture incorrectly.");
    }

    private static void VendorMarks() {
        DrawingImage Mark(string identity) {
            object vendor = Native.Call(typeof(GlassPresentationSession), "VendorOf", null, identity)!;
            return (DrawingImage)Native.Property(vendor.GetType(), "Logo").GetValue(vendor)!;
        }
        DrawingImage unknown = Mark("Unrecognized fixture distributor");
        foreach (string identity in new[] { "OpenLogic", "ojdk_build", "BiSheng JDK", "eliya", "mandrel",
            "gluon_graalvm", "trava", "redhat", "sap_machine", "aoj_openj9", "Oracle OpenJDK", "semeru_certified" }) {
            DrawingImage mark = Mark(identity);
            Native.Check(!ReferenceEquals(unknown, mark) && mark is { IsFrozen: true, Drawing: DrawingGroup },
                $"The Java distribution {identity} did not resolve a vector mark.");
        }
    }

    private static void ManagementView() {
        var page = new PageSetupLaunch();
        var javaPage = (PageSetupJava)Native.Property(typeof(PageSetupLaunch), "JavaPage").GetValue(page)!;
        var pane = (StackPanel)page.FindName("PanJavaManagement");
        var rows = (StackPanel)page.FindName("PanJavaRows");
        var sourceFilter = (MyTextBox)page.FindName("TextJavaFilter");
        Native.Check(!Descendants(page).Contains(pane) && Descendants(javaPage).Contains(pane),
            "Java management still belongs to the general launch settings view.");
        CheckNavigation(page, javaPage);
        Native.Call(typeof(PageSetupLaunch), "RefreshJavaManagement", page);
        using GlassPresentationSession session = GlassPresentation.Create(javaPage);
        Window window = new() { Content = session.View, Width = 900, Height = 680, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show();
            window.UpdateLayout();
            VisualAssetChecks.Drain();
            Native.Check(pane.Visibility == Visibility.Visible && rows.Children.Count == 2,
                "The standalone Java page did not display the installed catalogue.");
            var filter = Descendants(session.View).OfType<TextBox>().Single(input =>
                ReferenceEquals(BindingOperations.GetBinding(input, TextBox.TextProperty)?.Source, sourceFilter));
            filter.Text = "Temurin x64";
            VisualAssetChecks.Pump(() => rows.Children.OfType<MyListItem>().Count(row => row.Visibility == Visibility.Visible) == 1,
                "filter the projected Java list");
            var matching = rows.Children.OfType<MyListItem>().Single(row => row.Visibility == Visibility.Visible);
            Native.Check(matching.Title.Contains("Temurin", StringComparison.Ordinal) && matching.Title.Contains("HotSpot", StringComparison.Ordinal),
                "The Java management filter omitted the matching runtime metadata.");
            Native.Check(!page.IsLoaded && !window.IsActive, "The Java fixture activated another window or the source page workflow.");
        } finally {
            window.Content = null;
            window.Close();
            VisualAssetChecks.Drain();
        }
    }

    private static void CheckNavigation(PageSetupLaunch settings, PageSetupJava expected) {
        var previousSettings = ModMain.FrmSetupLaunch;
        var previousInstanceSettings = ModMain.FrmInstanceSetup;
        var previousSidebar = ModMain.FrmSetupLeft;
        try {
            ModMain.FrmSetupLaunch = settings;
            var launchSidebar = new PageSetupLeft();
            ModMain.FrmSetupLeft = launchSidebar;
            var settingsPort = (LegacyMainLeftPage)Native.Call(typeof(FormMain), "LeftPagePort", null, (int)FormMain.PageType.Setup)!;
            Native.Check(launchSidebar.FindName("ItemJava") is MyListItem { Title: "Java" } &&
                ReferenceEquals(settingsPort.Item((int)FormMain.PageSubType.SetupJava), launchSidebar.FindName("ItemJava")) &&
                ReferenceEquals(settingsPort.Item((int)FormMain.PageSubType.SetupUI), launchSidebar.FindName("ItemUI")) &&
                ReferenceEquals(launchSidebar.PageGet(FormMain.PageSubType.SetupJava), expected),
                "The settings sidebar did not route to the standalone Java page.");

            var instanceSettings = new PageInstanceSetup();
            ModMain.FrmInstanceSetup = instanceSettings;
            var instanceJava = (PageSetupJava)Native.Property(typeof(PageInstanceSetup), "JavaPage").GetValue(instanceSettings)!;
            var selector = (MyComboBox)instanceSettings.FindName("ComboArgumentJava");
            var instanceSidebar = new PageInstanceLeft();
            Native.Check(instanceSidebar.FindName("ItemJava") is MyListItem { Title: "Java" } &&
                ReferenceEquals(instanceSidebar.PageGet(FormMain.PageSubType.InstanceJava), instanceJava) &&
                Descendants(instanceJava).Contains(selector) && !Descendants(instanceSettings).Contains(selector),
                "The instance sidebar did not route to the standalone Java selector.");
        } finally {
            ModMain.FrmSetupLaunch = previousSettings;
            ModMain.FrmInstanceSetup = previousInstanceSettings;
            ModMain.FrmSetupLeft = previousSidebar;
        }
    }

    private static Type DiscoveryType() => typeof(Java).Assembly.GetType("PCLCS.WindowsJavaDiscovery", throwOnError: true)!;

    private static string Identity(string bin) =>
        (string)Native.Call(DiscoveryType(), "ActualExecutable", null, Path.Combine(bin, "java.exe"))!;

    private static string CreateJava(string home) {
        string bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(Path.Combine(bin, "server"));
        File.WriteAllText(Path.Combine(bin, "java.exe"), "Owned non-executable probe fixture");
        File.WriteAllText(Path.Combine(bin, "server", "jvm.dll"), "Owned runtime marker");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"21.0.7\"");
        return bin;
    }

    private static void Junction(string alias, string target) {
        foreach (string path in new[] { alias, target }) Native.Check(path.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) < 0,
            "Unsafe junction fixture path.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) {
            UseShellExecute = false, CreateNoWindow = true, Arguments = $"/d /s /v:off /c \"mklink /J \"{alias}\" \"{target}\"\""
        };
        using Process process = Process.Start(start) ?? throw new IOException("Could not create the owned Java junction.");
        if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("Java junction creation timed out."); }
        Native.Check(process.ExitCode == 0, "Java junction creation failed.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
        var visited = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0) {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current)) continue;
            yield return current;
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Push(child);
            if (current is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }
}
