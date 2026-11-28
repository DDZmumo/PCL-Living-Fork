using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
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
            Console.WriteLine("PASS local Java management: discovery, cached probes, launch validation and native priority menu ordering.");
        } finally { try { ConfigProviders.Default.Save(); } finally { ConfigProviders.Default = previous; } }
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
            object vendor = Native.Call(typeof(GlassJavaGraphics), "VendorOf", null, identity)!;
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

    private static void ManagementView()
    {
        var page = new GlassJavaManagementPage(() => { });
        page.Open(loadData: true);
        Native.Check(Descendants(page).OfType<Button>().Any(button => Equals(button.Content, "下载 Java")),
            "The native Java page lost its catalogue entry.");
        var combo = Descendants(page).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "启动优先级");
        Native.Call(page.GetType(), "RenderPriority", page);
        var first = (ComboBoxItem)combo.Items[0];
        var runtime = (Java)first.Tag;
        var up = Descendants(first).OfType<Button>().Single(action => AutomationProperties.GetName(action) == "提高优先级");
        var down = Descendants(first).OfType<Button>().Single(action => AutomationProperties.GetName(action) == "降低优先级");
        Native.Check(!up.IsEnabled && down.IsEnabled && combo.SelectedIndex == -1, "Invalid boundary actions or retained Java selection.");
        down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Native.Check(Configs.JavaList.Get()!.ElementAt(1).Folder == runtime.Folder, "The menu command did not persist the new runtime order.");
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
