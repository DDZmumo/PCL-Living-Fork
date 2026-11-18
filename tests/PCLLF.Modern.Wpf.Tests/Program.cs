using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore;
using PCLCS;

namespace PCLLF.Modern.Wpf.Tests;

// Runs the existing LF Core/WPF components with a standard Application and synthetic files.
// Full LF native UI, theme, main-window, account and game acceptance remain separate gates.
internal static class Program {
    [STAThread]
    private static int Main(string[] arguments) {
        bool animationChild = arguments.Length == 2 && arguments[1] == "--animation-driver-child";
        try {
            if (animationChild) {
                string root = Path.GetFullPath(arguments[0]);
                if (!File.Exists(Path.Combine(root, "PCLCS", "PCLCS.csproj")))
                    throw new DirectoryNotFoundException("The animation child requires an explicit LF repository root.");
                Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Environment.Version.Major == 10,
                    "The animation child must execute on the actual .NET 10 STA entry thread.");
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AnimationTests.RunThreadDriverChild();
                Console.Out.Flush();
                // The original LF driver has no stop token and owns a foreground
                // worker. Process isolation ends it without changing production.
                Environment.Exit(0);
            }
            Run(arguments);
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error.ToString());
            if (animationChild) { Console.Error.Flush(); Environment.Exit(1); }
            return 1;
        }
    }

    private static void Require(bool value, string message) {
        if (!value) throw new InvalidOperationException(message);
    }

    private static bool IsLegacyWpf(AssemblyName name) =>
        string.Equals(name.Name, "MeloongCore.Wpf", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name.Name, "Meloong.Wpf", StringComparison.OrdinalIgnoreCase);

    private static void Run(string[] arguments) {
        if (arguments.Length != 1)
            throw new ArgumentException("Usage: PCLLF.Modern.Wpf.Tests <explicit-repository-root>");
        string repositoryRoot = Path.GetFullPath(arguments[0]);
        if (!File.Exists(Path.Combine(repositoryRoot, "PCLCS", "PCLCS.csproj")))
            throw new DirectoryNotFoundException("The explicit repository root does not contain LF's PCLCS source project.");
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "WPF acceptance must run on the STA entry thread.");
        Require(Environment.Version.Major == 10, "Modern WPF acceptance must execute on the .NET 10 runtime.");

        string fixture = Path.Combine(Path.GetTempPath(), "PclModernWpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        Application application = null;
        try {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            CheckAssemblyGraph();
            CheckEmbeddedResources(repositoryRoot);
            PclScreenshotControllerChecks.Run();
            Console.WriteLine("PASS shared thumbnail controller acceptance: actual WPF dispatch, concurrency, revision, viewport, unload and decoder fault behavior.");
            PclKeybindingControllerChecks.Run(Path.Combine(fixture, "keybindings"));
            Console.WriteLine("PASS shared keybinding controller acceptance: capture, confirmation, owner switch, actual options transactions and failure callbacks.");
            CheckImageDecoding(Path.Combine(fixture, "images"));
            AnimationTests.Run();
            AnimationTests.RunThreadDriverProcess(repositoryRoot);
            var failures = new System.Collections.Generic.List<Exception>();
            try { BitmapFoundationAcceptance.Run(repositoryRoot, Path.Combine(fixture, "bitmap-foundation")); }
            catch (Exception error) { Console.Error.WriteLine("FAIL bitmap acceptance: " + error); failures.Add(error); }
            try { CommonControlsAcceptance.Run(); }
            catch (Exception error) { Console.Error.WriteLine("FAIL common controls acceptance: " + error); failures.Add(error); }
            try { A003Checks.Run(); }
            catch (Exception error) { Console.Error.WriteLine("FAIL A-003 update-log/favorites checks: " + error); failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Modern foundation/control acceptance failed.", failures);
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => IsLegacyWpf(assembly.GetName())),
                "The modern runtime loaded the historical Meloong WPF assembly.");
        } finally {
            try {
                if (application != null) application.Shutdown();
            } finally {
                // The directory is created above from a fixed prefix plus a GUID, never from user data.
                if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
            }
        }
        Console.WriteLine("PASS .NET 10 LF Core/WPF component acceptance. Full LF native UI/theme/main-window and account/game acceptance remain pending.");
    }

    private static void CheckAssemblyGraph() {
        Assembly core = typeof(InstanceResourceImages).Assembly;
        TargetFrameworkAttribute framework = core.GetCustomAttribute<TargetFrameworkAttribute>();
        Require(core.GetName().Name == "PCLCS", "The modern test did not load LF's PCLCS assembly identity.");
        Require(framework != null && framework.FrameworkName == ".NETCoreApp,Version=v10.0",
            "The loaded PCLCS target is not .NET 10; a legacy assembly may have leaked into the modern output.");
        Require(!core.GetReferencedAssemblies().Any(IsLegacyWpf), "Modern PCLCS still references the historical Meloong WPF assembly.");
        Assembly shared = typeof(FileUtils).Assembly;
        Require(shared.GetName().Name == "MeloongCore", "The LF Shared dependency could not be loaded with its original identity.");
        Require(core.GetReferencedAssemblies().Any(name => name.Name == shared.GetName().Name), "Modern PCLCS does not reference its actual LF Shared dependency.");
        Require(!shared.GetReferencedAssemblies().Any(IsLegacyWpf), "LF Shared unexpectedly references the historical WPF assembly.");
        Console.WriteLine("PASS assembly graph: PCLCS targets " + framework.FrameworkName + "; LF Shared loaded; no Meloong WPF reference.");
    }

    private static void CheckEmbeddedResources(string repositoryRoot) {
        Assembly core = typeof(InstanceResourceImages).Assembly;
        Dictionary<string, string> resources = new Dictionary<string, string>(StringComparer.Ordinal) {
            { @"Launch\JavaWrapper.jar", Path.Combine(repositoryRoot, "PCLCS", "Launch", "JavaWrapper.jar") },
            { @"Launch\LwjglUnsafeAgent.jar", Path.Combine(repositoryRoot, "PCLCS", "Launch", "LwjglUnsafeAgent.jar") },
            { @"Launch\ModKeybindPatch.jar", Path.Combine(repositoryRoot, "PCLCS", "Launch", "ModKeybindPatch.jar") },
            { @"Resource\WikiEntries.txt", Path.Combine(repositoryRoot, "PCLCS", "Resource", "WikiEntries.txt") },
            { @"Minecraft\OptiFineHashes.json", Path.Combine(repositoryRoot, "PCLCS", "Minecraft", "OptifineHashes.json") }
        };
        Require(core.GetManifestResourceNames().OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(resources.Keys.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal),
            "Modern PCLCS embedded names differ from the expected case-sensitive backslash resource names.");
        foreach (KeyValuePair<string, string> resource in resources) {
            byte[] expected = File.ReadAllBytes(resource.Value);
            using (Stream embedded = core.GetManifestResourceStream(resource.Key)) {
                Require(embedded != null, "Missing exact embedded resource: " + resource.Key);
                using (MemoryStream bytes = new MemoryStream()) {
                    embedded.CopyTo(bytes);
                    Require(bytes.ToArray().SequenceEqual(expected), "Modern embedded bytes changed: " + resource.Key);
                }
            }
            // Execute the real LF Shared resource API against the actual modern Core assembly.
            Require(FileUtils.ReadAsBytes(resource.Key, typeof(InstanceResourceImages)).SequenceEqual(expected),
                "LF Shared could not read the modern Core resource bytes: " + resource.Key);
            if (resource.Key == @"Launch\ModKeybindPatch.jar")
                Require(ModKeybindPatch.SupportBytes().Span.SequenceEqual(expected), "The runtime keybind agent did not match the verified source JAR.");
            Console.WriteLine("PASS embedded resource and LF Shared read: " + resource.Key + " (" + expected.Length + " bytes).");
        }
    }

    private static void CheckImageDecoding(string fixture) {
        Directory.CreateDirectory(fixture);
        string file = Path.Combine(fixture, "synthetic.png");
        const int width = 80, height = 40;
        byte[] original = new byte[width * height * 4];
        for (int index = 0; index < original.Length; index += 4) {
            original[index] = 12; original[index + 1] = 34; original[index + 2] = 56; original[index + 3] = 255;
        }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, original, width * 4);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (FileStream output = File.Create(file)) encoder.Save(output);

        BitmapSource decoded = InstanceResourceCatalog.LoadImage<BitmapSource>(file, 32, InstanceResourceImages.Decode);
        Require(decoded != null && decoded.IsFrozen && decoded.PixelWidth == 32 && decoded.PixelHeight == 16,
            "The actual modern LF decoder did not preserve freeze, requested width or aspect ratio.");
        // Exclusive reopen fails on Windows if the catalog has retained its source stream.
        using (FileStream exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(exclusive.Length > 0, "The synthetic PNG was empty.");
        File.Delete(file);
        int stride = (decoded.PixelWidth * decoded.Format.BitsPerPixel + 7) / 8;
        byte[] pixels = new byte[stride * decoded.PixelHeight];
        decoded.CopyPixels(pixels, stride, 0);
        Require(pixels.Any(value => value != 0), "The OnLoad bitmap lost pixels after its source stream/file was removed.");
        Console.WriteLine("PASS actual LF image decoder/catalog: frozen resized bitmap, exclusive file reopen and readable pixels after source removal.");
    }
}
