using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class JavaRuntimeDownloadChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "java-runtime-download");
        var previous = ConfigProviders.Default;
        ConfigProviders.Default = new JsonConfigProvider(Path.Combine(scope.Root, "java-profile.json"));
        try
        {
            JavaRuntimePackage sample = RoundTrip(scope.Root);
            InstalledRuntimeIdentity(scope.Root, sample);
            ExpandedVendorChecksums(scope.Root, sample);
            SearchView(sample);
            TaskStepsAndLayout();
            if (Environment.GetEnvironmentVariable("PCLLF_JAVA_RUNTIME_LIVE") is { Length: > 0 } vendor)
                OfficialPackage(scope.Root, vendor).GetAwaiter().GetResult();
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS Java runtime download: catalogue constraints, archive digest, bounded ZIP installation, reuse, cancellation, pinned nested JRE, removal and search lifecycle.");
        }
        finally { try { ConfigProviders.Default.Save(); } finally { ConfigProviders.Default = previous; } }
    }

    private static JavaRuntimePackage RoundTrip(string root)
    {
        string target = Path.Combine(root, "installed");
        var pins = new MmcJavaPinStore(Path.Combine(root, "pins"));
        var manual = new Java(Path.Combine(root, "manual", "bin"), new Version(17, 0));
        Directory.CreateDirectory(manual.Folder);
        File.WriteAllText(manual.JavaExePath, "owned external Java control");
        Configs.JavaList.Set([manual]);

        byte[] zip = MakeZip();
        string sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var requests = new RuntimeResponses(zip, sha256);
        using var client = new HttpClient(requests);
        var catalog = new JavaRuntimeCatalog(client);
        JavaRuntimePackage package = catalog.SearchAsync(21, "x64", "temurin", "jdk", CancellationToken.None)
            .GetAwaiter().GetResult().Single();
        Native.Check(package.Distribution == "temurin" && package.Architecture == "x64" && package.Size == zip.Length,
            "The Java catalogue admitted an invalid listing or lost package identity.");
        var x86Package = catalog.SearchAsync(21, "i686", "temurin", "jdk", CancellationToken.None)
            .GetAwaiter().GetResult().Single();
        Native.Check(x86Package.Architecture == "x86", "The download list lost the x86 architecture.");
        Reject<ArgumentOutOfRangeException>(() => catalog.SearchAsync(7, "x64", null, null, CancellationToken.None).GetAwaiter().GetResult());
        JavaRuntimeArchive source = catalog.ResolveAsync(package, CancellationToken.None).GetAwaiter().GetResult();
        Native.Check(source.Sha256 == sha256 && source.DownloadUri.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal),
            "Resolution did not bind the selected archive and its SHA-256.");

        var store = new ManagedJavaRuntimeStore(target, client, pins);
        Java Install(JavaRuntimePackage candidate, JavaRuntimeArchive archive, CancellationToken token = default, Action<double>? progress = null) =>
            ((Task<Java>)Native.Call(typeof(ManagedJavaRuntimeStore), "InstallCoreAsync", store, candidate, archive,
                token, progress, null, (Func<Java, CancellationToken, Task<bool>>)SyntheticProbe)!).GetAwaiter().GetResult();
        long downloadedBefore = PCLCS.FileTaskProgress.DownloadedBytes, writtenBefore = PCLCS.FileTaskProgress.WrittenBytes;
        var downloadProgress = new List<double>();
        var progress = new List<double>();
        Java installed;
        using (var prepared = store.PrepareInstallAsync(package, source, CancellationToken.None, downloadProgress.Add).GetAwaiter().GetResult())
        {
            Native.Check(Configs.JavaList.Get()!.SequenceEqual(new[] { manual }) &&
                PCLCS.FileTaskProgress.DownloadedBytes - downloadedBefore == zip.Length &&
                PCLCS.FileTaskProgress.WrittenBytes == writtenBefore && PCLCS.FileTaskProgress.ActiveDownloads == 0 &&
                downloadProgress.Count > 0 && downloadProgress[^1] == 1,
                "Downloading Java changed the installed catalogue or reported ZIP bytes as installation writes.");
            installed = ((Task<Java>)Native.Call(typeof(ManagedJavaRuntimeStore), "InstallPreparedCoreAsync", store, prepared,
                CancellationToken.None, (Action<double>)progress.Add, null, (Func<Java, CancellationToken, Task<bool>>)SyntheticProbe)!)
                .GetAwaiter().GetResult();
        }
        Native.Check(ManagedJavaRuntimeStore.IsManaged(target, installed) && installed.Version?.Major == 21 &&
            installed.Distribution == "Eclipse Temurin" && installed.VirtualMachine == "HotSpot", "Verified Java was not registered with its probe metadata.");
        JavaRuntimePackage receipt = ManagedJavaRuntimeStore.GetInstalledPackage(target, installed)!;
        Native.Check(receipt.IsSameArchive(package) && !receipt.IsSameArchive(package with { PackageType = "jre" }) &&
            !receipt.IsSameArchive(package with { Architecture = "arm64" }),
            "The managed receipt incorrectly matched a different architecture or package type.");
        Native.Check(Configs.JavaList.Get()!.SequenceEqual(new[] { manual, installed }) && progress.Count > 1 && progress[^1] == 1 &&
            progress.SequenceEqual(progress.Order()), "Installation changed the existing priority order or failed to report progress.");
        Native.Check(Install(package, source).Equals(installed) && requests.Downloads == 1 && Configs.JavaList.Get()!.Count == 2,
            "Reusing an installed package downloaded or registered another copy.");

        var folders = (IEnumerable<string>)Native.Call(typeof(ManagedJavaRuntimeStore), "InstalledFolders", null,
            target, CancellationToken.None)!;
        Native.Check(folders.Single() == Path.GetDirectoryName(installed.JavaExePath),
            "Refreshing Java after a list reset could not rediscover the owned runtime.");

        Reject<InvalidDataException>(() => Install(package with { Id = "bb" }, source with { Sha256 = new string('0', 64) }));
        using var canceled = new CancellationTokenSource();
        Reject<OperationCanceledException>(() => Install(package with { Id = "cc" }, source, canceled.Token, _ => canceled.Cancel()));
        byte[] malicious = MakeZip("../escaped.txt");
        requests.Archive = malicious;
        Reject<IOException>(() => Install(package with { Id = "dd", Size = malicious.Length },
            source with { Sha256 = Convert.ToHexString(SHA256.HashData(malicious)) }));
        Native.Check(!Directory.Exists(Path.Combine(target, "bb")) && !Directory.Exists(Path.Combine(target, "cc")) &&
            !Directory.Exists(Path.Combine(target, "dd")) && !Directory.GetDirectories(target, ".install-*").Any(),
            "Failed or canceled installation published files or left active staging.");

        Reject<IOException>(() => store.UninstallAsync(manual, () => true, CancellationToken.None).GetAwaiter().GetResult());
        Reject<IOException>(() => store.UninstallAsync(installed, () => false, CancellationToken.None).GetAwaiter().GetResult());
        string nestedJava = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(installed.JavaExePath))!, "jre", "bin", "java.exe");
        string instance = Path.Combine(root, "game", "versions", "pinned-fixture");
        Directory.CreateDirectory(instance);
        using (var pin = pins.Acquire(Guid.NewGuid(), MmcSessions.ObserveExecutable(nestedJava), Guid.NewGuid(), instance))
        {
            Reject<IOException>(() => store.UninstallAsync(installed, () => true, CancellationToken.None).GetAwaiter().GetResult());
            pin.CompleteVerifiedExit();
        }
        void RejectPostWrite(ConcurrentList<Java>? entries)
        {
            if (entries?.Count == 1) throw new IOException("Owned configuration fixture rejected removal.");
        }
        Configs.JavaList.Changed += RejectPostWrite;
        try { Reject<IOException>(() => store.UninstallAsync(installed, () => true, CancellationToken.None).GetAwaiter().GetResult()); }
        finally { Configs.JavaList.Changed -= RejectPostWrite; }
        Native.Check(File.Exists(installed.JavaExePath) && Configs.JavaList.Get()!.SequenceEqual(new[] { manual, installed }),
            "A failed catalogue save did not restore the runtime and its original priority.");
        store.UninstallAsync(installed, () => true, CancellationToken.None).GetAwaiter().GetResult();
        Native.Check(Configs.JavaList.Get()!.SequenceEqual(new[] { manual }) && !File.Exists(installed.JavaExePath) &&
            File.Exists(manual.JavaExePath) && !Directory.GetDirectories(target, ".uninstall-*").Any(),
            "Uninstall removed an unrelated runtime, leaked staging, or retained a dead entry.");
        return package;
    }

    private static void InstalledRuntimeIdentity(string root, JavaRuntimePackage sample)
    {
        string bin = Path.Combine(root, "imported-jdk", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "java.exe"), "probe fixture");
        File.WriteAllText(Path.Combine(bin, "javac.exe"), "compiler fixture");
        var java = new Java(bin);
        const string output = """
            openjdk version "21.0.13.1" 64-Bit
            java.runtime.version = 21.0.13.1+2-LTS
            java.vendor = Azul Systems, Inc.
            os.arch = amd64
            sun.arch.data.model = 64
            """;
        Native.Check(java.CheckAsync(CancellationToken.None, (_, _) => Task.FromResult(output)).GetAwaiter().GetResult(),
            "The actual probe could not read a four-part release with a build number.");
        JavaRuntimePackage exact = sample with { Distribution = "zulu", Version = "21.0.13.1+2", PackageType = "jdk" };
        Native.Check(JavaRuntimeCatalog.MatchesLocalRuntime(exact, java) &&
            !JavaRuntimeCatalog.MatchesLocalRuntime(exact with { PackageType = "jre" }, java) &&
            !JavaRuntimeCatalog.MatchesLocalRuntime(exact with { Architecture = "arm64" }, java) &&
            !JavaRuntimeCatalog.MatchesLocalRuntime(exact with { Version = "21.0.13.1+3" }, java) &&
            !JavaRuntimeCatalog.MatchesLocalRuntime(exact with { Version = "21.0.13+2" }, java),
            "An imported Java was confused with a different package type, architecture or build.");
        string x86 = output.Replace("amd64", "x86", StringComparison.Ordinal)
            .Replace("data.model = 64", "data.model = 32", StringComparison.Ordinal);
        Native.Check(java.CheckAsync(CancellationToken.None, (_, _) => Task.FromResult(x86)).GetAwaiter().GetResult() &&
            java.Is32Bit && JavaRuntimeCatalog.MatchesLocalRuntime(exact with { Architecture = "x86" }, java) &&
            !JavaRuntimeCatalog.MatchesLocalRuntime(exact, java),
            "An x86 Java could not be imported or was confused with x64.");
    }

    private static void ExpandedVendorChecksums(string root, JavaRuntimePackage sample)
    {
        byte[] zip = MakeZip();
        string sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        string sha512 = Convert.ToHexString(SHA512.HashData(zip)).ToLowerInvariant();
        using var client = new HttpClient(new RuntimeResponses(zip, sha256));
        var catalog = new JavaRuntimeCatalog(client);
        var sap = sample with { Id = "51", Distribution = "sap_machine" };
        var jbr = sample with { Id = "52", Distribution = "jetbrains" };
        Native.Check(catalog.ResolveAsync(sap, CancellationToken.None).GetAwaiter().GetResult().Sha256 == sha256,
            "SapMachine did not resolve its exact official release asset checksum.");
        JavaRuntimeArchive archive = catalog.ResolveAsync(jbr, CancellationToken.None).GetAwaiter().GetResult();
        Native.Check(archive.Sha512 == sha512 && archive.Sha256.Length == 0, "The JetBrains SHA-512 was not recognized.");
        string target = Path.Combine(root, "sha512");
        var store = new ManagedJavaRuntimeStore(target, client, new MmcJavaPinStore(Path.Combine(root, "sha512-pins")));
        Java Install(JavaRuntimePackage package, JavaRuntimeArchive source) =>
            ((Task<Java>)Native.Call(typeof(ManagedJavaRuntimeStore), "InstallCoreAsync", store, package, source,
                CancellationToken.None, null, null, (Func<Java, CancellationToken, Task<bool>>)SyntheticProbe)!).GetAwaiter().GetResult();
        Reject<InvalidDataException>(() => Install(jbr, archive with { Sha512 = new string('e', 128) }));
        var installed = Install(jbr, archive);
        Native.Check(ManagedJavaRuntimeStore.GetInstalledPackage(target, installed)?.IsSameArchive(jbr) == true &&
            Install(jbr, archive).Equals(installed), "The SHA-512 receipt could not be rediscovered or reused.");
        store.UninstallAsync(installed, () => true, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static Task<bool> SyntheticProbe(Java java, CancellationToken token) => java.CheckAsync(token, (_, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult("openjdk version \"21.0.7+6\"\njava.vendor = Eclipse Adoptium\n" +
            "java.vm.name = OpenJDK 64-Bit Server VM\nos.arch = amd64\nsun.arch.data.model = 64\n");
    });

    private static void SearchView(JavaRuntimePackage package) {
        using var page = new GlassJavaDownloadPage();
        page.Open(loadData: false);
        var controls = Children(page).ToArray();
        Button Action(string name) => controls.OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        var picker = controls.OfType<Expander>().Single();
        Native.Check(!picker.IsExpanded, "Distribution choices should initially be collapsed.");
        Action("选择发行版").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Native.Check(picker.IsExpanded, "Could not open the distribution choices.");
        Action("Eclipse Temurin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Native.Check(picker.IsExpanded, "Choosing a distribution unexpectedly closed the list.");
        Action("全部可下载").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Native.Check(!picker.IsExpanded, "Showing all downloads did not close the chooser.");
    }

    private static void TaskStepsAndLayout()
    {
        int stage = 0, releases = 0;
        using var finish = new ManualResetEventSlim();
        var fence = Activator.CreateInstance(Native.Type("PCL.MmcOperationCancellationFence"), Native.Flags, null,
            new object[] { (Action)(() => Interlocked.Increment(ref releases)), 2 }, null)!;
        ModLoader.LoaderBase Worker(string label, Action action) =>
            (ModLoader.LoaderBase)Native.Static("PCL.JavaRuntimeDownloads", "Worker", fence, label,
                (Action<ModLoader.LoaderTask<int, int>, CancellationToken, Action<Action>>)((_, _, _) => action()))!;
        var download = Worker("下载 Java 安装包", () => Volatile.Write(ref stage, 1));
        var install = Worker("校验并安装 Java", () =>
        {
            Native.Check(Volatile.Read(ref stage) == 1 && releases == 0, "The install task ran before the download completed.");
            Volatile.Write(ref stage, 2);
            Native.Check(finish.Wait(TimeSpan.FromSeconds(8)), "Installation layout fixture timed out.");
        });
        string title = new('长', 160);
        var job = new ModLoader.LoaderCombo<int>(title, [download, install]);
        job.Start(1);
        ModLoader.LoaderTaskbar.Add(job);
        try
        {
            VisualAssetChecks.Pump(() => Volatile.Read(ref stage) == 2, "start the second Java task");
            Type service = typeof(LauncherApplicationServices);
            object manager = Native.Property(service, "TaskManager").GetValue(null)!;
            Type viewType = Native.Type("PCL.GlassTaskManagerPage");
            var view = (FrameworkElement)Activator.CreateInstance(viewType, Native.Flags, null, [manager], null)!;
            Native.Call(viewType, "Open", view);
            view.Measure(new Size(265, 240));
            view.Arrange(new Rect(0, 0, 265, 240));
            var button = Children(view).OfType<Button>().Single(button => Equals(button.Content, "取消"));
            Rect bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
            Native.Check(button.IsEnabled && button.ActualWidth >= 44 && bounds.Right <= view.ActualWidth,
                "A long task title hid or clipped the cancel action.");
            Native.Check(Children(view).OfType<TextBlock>().Any(text => text.Text == title &&
                text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.CharacterEllipsis),
                "The long task title did not fit its allocated space.");
        }
        finally
        {
            finish.Set();
            ModLoader.LoaderTaskbar.Remove(job);
            job.WaitForExit(StartIfNeeded: false);
        }
        Native.Check(releases == 1 && job.GetLoaderList().Count == 2, "The two-stage task did not release its reservation once.");

    }

    private static IEnumerable<DependencyObject> Children(DependencyObject element)
    {
        var visited = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(element);
        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item)) continue;
            yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) pending.Push(child);
            if (item is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) pending.Push(VisualTreeHelper.GetChild(item, index));
        }
    }

    private static async Task OfficialPackage(string root, string vendor)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Living-Fork/1.0");
        var catalog = new JavaRuntimeCatalog(client);
        string arch = Environment.GetEnvironmentVariable("PCLLF_JAVA_RUNTIME_LIVE_ARCH") ??
            (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        int major = arch == "x86" ? 8 : 21;
        var packages = await catalog.SearchAsync(major, arch, vendor, "jre", CancellationToken.None).ConfigureAwait(false);
        JavaRuntimePackage package = packages.OrderBy(item => item.Size == 0 ? long.MaxValue : item.Size).First();
        JavaRuntimeArchive archive = await catalog.ResolveAsync(package, CancellationToken.None).ConfigureAwait(false);
        string target = Path.Combine(root, "official");
        var store = new ManagedJavaRuntimeStore(target, client, new MmcJavaPinStore(Path.Combine(root, "official-pins")));
        Java java = await store.InstallAsync(package, archive, CancellationToken.None).ConfigureAwait(false);
        Native.Check(Configs.JavaList.Get()!.Contains(java) && await java.CheckAsync().ConfigureAwait(false) &&
            java.Version?.Major == major && java.Is32Bit == (arch == "x86"), "The official runtime failed to probe from its installed location.");
        await store.UninstallAsync(java, () => true, CancellationToken.None).ConfigureAwait(false);
        Native.Check(!File.Exists(java.JavaExePath) && !Configs.JavaList.Get()!.Contains(java), "Official Java was not removed from disk and catalogue.");
        Console.WriteLine($"PASS official Java ZIP: {package.Distribution} {package.Version} / {arch}, verified, installed, probed and uninstalled.");
    }

    private static byte[] MakeZip(string? extra = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in new[] { "jdk/bin/java.exe", "jdk/bin/server/jvm.dll", "jdk/jre/bin/java.exe", "jdk/release" })
                Write(path);
            if (extra is not null) Write(extra);
            void Write(string path)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write("owned fixture entry");
            }
        }
        return stream.ToArray();
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class RuntimeResponses(byte[] archive, string sha256) : HttpMessageHandler
    {
        internal byte[] Archive = archive;
        internal int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpContent content;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/disco/v3.0/packages":
                    Native.Check(request.RequestUri.Query.Contains("version=21", StringComparison.Ordinal) &&
                        request.RequestUri.Query.Contains("distro=temurin", StringComparison.Ordinal), "The user filters were not applied to the request.");
                    string arch = request.RequestUri.Query.Contains("architecture=x86", StringComparison.Ordinal) ? "x86" : "amd64";
                    content = new StringContent(JsonSerializer.Serialize(new { result = new[] { Listing("aa", "ga", arch), Listing("ef", "ea", arch) } }));
                    break;
                case "/disco/v3.0/ids/aa":
                    content = new StringContent(JsonSerializer.Serialize(new { result = new[] {
                        new { filename = "runtime.zip", direct_download_uri = "https://runtime.invalid/runtime.zip",
                            checksum_type = "sha256", checksum = sha256 }
                    } }));
                    break;
                case "/disco/v3.0/ids/51":
                    content = new StringContent(JsonSerializer.Serialize(new { result = new[] {
                        new { filename = "runtime.zip", direct_download_uri = "https://github.com/SAP/SapMachine/releases/download/sapmachine-21.0.7/runtime.zip" }
                    } }));
                    break;
                case "/repos/SAP/SapMachine/releases/tags/sapmachine-21.0.7":
                    content = new StringContent(JsonSerializer.Serialize(new { assets = new[] {
                        new { name = "runtime.zip", size = Archive.Length, digest = "sha256:" + sha256 }
                    } }));
                    break;
                case "/disco/v3.0/ids/52":
                    content = new StringContent(JsonSerializer.Serialize(new { result = new[] {
                        new { filename = "runtime.zip", direct_download_uri = "https://runtime.invalid/runtime.zip",
                            checksum_type = "sha512", checksum = Convert.ToHexString(SHA512.HashData(Archive)) }
                    } }));
                    break;
                case "/runtime.zip":
                    Downloads++;
                    content = new ByteArrayContent(Archive);
                    break;
                default: throw new IOException("Unexpected fixture request.");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }

        private object Listing(string id, string status, string architecture) => new { id, distribution = "temurin", java_version = "21.0.7+6",
            major_version = 21, operating_system = "windows", architecture, archive_type = "zip",
            package_type = "jdk", release_status = status, directly_downloadable = true, free_use_in_production = true,
            filename = "runtime.zip", size = Archive.Length };
    }
}
