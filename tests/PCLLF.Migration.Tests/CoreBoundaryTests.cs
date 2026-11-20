using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using PCLCS;

internal static class CoreBoundaryTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("Core is the product binary without a desktop framework or UI dependency", AssemblyBoundary);
        yield return ("Core stream ZIP reader preserves non-ASCII names and explicit stream ownership", ArchiveStreams);
    }

    private static void AssemblyBoundary() {
        Assembly core = typeof(Java).Assembly;
        Require(core != typeof(CoreBoundaryTests).Assembly && core.GetName().Name == "PCLCS",
            "The regression host must reference the real Core DLL, not compile its own production sources.");
        Require(core.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName == ".NETCoreApp,Version=v10.0",
            "Core must target plain .NET 10.");
        Require(core.GetCustomAttribute<TargetPlatformAttribute>() is null,
            "Core must not acquire a Windows-specific target framework.");
        HashSet<string> desktop = new(StringComparer.Ordinal) {
            "PCLLF.WPF.Core", "PCLLF.Presentation", "LivingPCL", "MeloongCore.Wpf",
            "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml",
            "System.Windows.Forms", "System.Drawing.Common"
        };
        Require(!core.GetReferencedAssemblies().Any(name => desktop.Contains(name.Name!)),
            "Core must not depend on a UI assembly.");
        Require(core.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
                .Select(attribute => attribute.AssemblyName).SequenceEqual(new[] { "PCLLF.Migration.Tests" }),
            "Only the existing white-box regression host may receive Core friend access.");
        Require(core.GetTypes().All(type => type.Namespace is null || !type.Namespace.StartsWith("PCLLF.Presentation", StringComparison.Ordinal)),
            "Presentation types leaked into Core.");
        string[] resources = [
            @"Launch\JavaWrapper.jar", @"Launch\LwjglUnsafeAgent.jar", @"Launch\ModKeybindPatch.jar",
            @"Resource\WikiEntries.txt", @"Minecraft\OptiFineHashes.json"
        ];
        Require(core.GetManifestResourceNames().Order().SequenceEqual(resources.Order()),
            "Core must expose the expected runtime resources. Actual: " + string.Join(", ", core.GetManifestResourceNames()));
        foreach (string name in resources) {
            using Stream resource = core.GetManifestResourceStream(name)!;
            Require(resource is not null && resource.Length > 0, "Missing Core resource: " + name);
            Require(MeloongCore.FileUtils.ReadAsBytes(name, typeof(Java)).LongLength == resource.Length,
                "Shared must read the unchanged logical resource name: " + name);
        }
    }

    private static void ArchiveStreams() {
        // ZIP names without EFS use the same OS code page as the legacy reader.
        Encoding encoding = InstanceResourcePackageRules.EntryNameEncoding();
        string name = encoding.GetString(encoding.GetBytes("图标-é.png"));
        byte[] payload = [1, 2, 3, 4];
        using MemoryStream input = new();
        using (ZipArchive output = new(input, ZipArchiveMode.Create, true, encoding)) {
            using Stream entry = output.CreateEntry(name).Open();
            entry.Write(payload);
        }
        input.Position = 0;
        using (ZipArchive archive = InstanceResourcePackageRules.OpenRead(input, leaveOpen: true)) {
            using Stream entry = archive.GetEntry(name)!.Open();
            using MemoryStream bytes = new();
            entry.CopyTo(bytes);
            Require(bytes.ToArray().SequenceEqual(payload), "Stream reader changed ZIP name decoding or entry contents.");
        }
        Require(input.CanRead, "leaveOpen must retain ownership with the caller.");
        input.Position = 0;
        using (ZipArchive archive = InstanceResourcePackageRules.OpenRead(input))
            Require(archive.GetEntry(name) is not null, "Default reader did not reopen the entry.");
        Require(!input.CanRead, "Default archive disposal must close the input stream.");
    }

    private static void Require(bool value, string message) {
        if (!value) throw new InvalidOperationException(message);
    }
}
