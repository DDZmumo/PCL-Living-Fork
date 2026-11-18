using System.Diagnostics;
using System.IO;
using System.Text;
using PCLCS;

internal static partial class MmcRuntimeLaunchTests {
    private static byte[] WithManifestPath(byte[] input, string value) => AppendJarEntries(input,
        ("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\nClass-Path: " + value + "\r\n\r\n")));

    private static void ManifestAbsentInputs() {
        string[] references = ["missing%20dependency.jar", "../sibling%2B.jar", "unicode-%E4%B8%AD.jar", "hash%23name.jar"];
        string probe = CompileManifestProbe();
        foreach (int major in new[] { 8, 21 }) using (var fixture = new Fixture(major, loader: false,
            firstJarTransform: bytes => WithManifestPath(bytes, string.Join(' ', references)))) {
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inspected = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            Require(inspected.ManifestInputs.Select(f => f.Reference).SequenceEqual(references), "Manifest references were omitted/reordered.");
            MmcClasspathInspector.RevalidateManifestInputs(plan, inspected);
            var absent = ManifestProbe(major, probe, plan.OrderedGameClasspath[0], references);
            Require(absent.Missing && absent.Paths.SequenceEqual(inspected.ManifestInputs.Select(f => f.TargetPath), StringComparer.OrdinalIgnoreCase),
                "Real JDK missing Class-Path/URI targets differ from observed owned targets. missing=" + absent.Missing + " Java=" + string.Join(" | ", absent.Paths) + " host=" + string.Join(" | ", inspected.ManifestInputs.Select(f => f.TargetPath)));
            var fact = inspected.ManifestInputs[0]; Directory.CreateDirectory(Path.GetDirectoryName(fact.TargetPath)!);
            File.WriteAllBytes(fact.TargetPath, Jar(new() { ["fixture/implicit.marker"] = Encoding.UTF8.GetBytes("now visible") }));
            Require(!ManifestProbe(major, probe, plan.OrderedGameClasspath[0], references).Missing, "Real JDK did not follow a newly present manifest input.");
            Expect("MMC_CLASSPATH_MANIFEST_INPUT", () => MmcClasspathInspector.RevalidateManifestInputs(plan, inspected));
            Expect("MMC_CLASSPATH_MANIFEST_INPUT", () => MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult());
        }
        string[] unsupported = ["https://example.invalid/x.jar", "//server/share/x.jar", @"\\server\share\x.jar", "file:/C:/x.jar", "C:/x.jar", "/x.jar",
            "." + string.Concat(Enumerable.Repeat("/..", 30)) + "/outside.jar", "x.jar?query", "x.jar#fragment", "bad%XX.jar", "bad%FF.jar",
            "bad%2Fname.jar", "bad%5Cname.jar", "ads%3aname", "CON.jar", "../../second/1/second-1.jar", "."];
        foreach (string reference in unsupported) using (var fixture = new Fixture(loader: false, firstJarTransform: bytes => WithManifestPath(bytes, reference))) {
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            Expect("MMC_CLASSPATH_MANIFEST_INPUT", () => MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult());
        }
        using (var fixture = new Fixture(loader: false, firstJarTransform: bytes => WithManifestPath(bytes, "\u00a0"))) {
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            Require(MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult().ManifestInputs.Length == 1,
                "Unicode whitespace was incorrectly treated as a Java StringTokenizer delimiter.");
        }
        Console.WriteLine("PASS actual Java8/21 ignore absent relative manifest inputs; newly present resources activate and final admission rejects; URI spaces/Unicode/plus/hash and authority/escape/ADS/existing-order guards.");
    }

    private static string CompileManifestProbe() {
        string folder = Path.Combine(Path.GetTempPath(), "PCLLF-MmcRuntimeTests", "manifest-probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "ManifestResourceProbe.java");
        File.WriteAllText(source, """
import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.Base64;
import java.nio.charset.StandardCharsets;
public final class ManifestResourceProbe {
 public static void main(String[] args) throws Exception {
  URL jar = new File(args[0]).toURI().toURL();
  try (URLClassLoader loader = new URLClassLoader(new URL[]{jar}, null)) {
   System.out.println("MISSING:" + (loader.getResource("fixture/implicit.marker") == null));
  }
  for (int i=1;i<args.length;i++) {
   File file = new File(new URL(jar,args[i]).toURI());
   System.out.println("TARGET:" + Base64.getEncoder().encodeToString(file.getCanonicalPath().getBytes(StandardCharsets.UTF_8)));
  }
 }
}
""", new UTF8Encoding(false));
        string javac = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVAC") ?? Path.Combine(Path.GetDirectoryName(Java17)!, "javac.exe");
        RunTool(javac, ["--release", "8", "-encoding", "UTF-8", source]); return folder;
    }

    private static (bool Missing, string[] Paths) ManifestProbe(int major, string folder, string jar, string[] references) {
        var start = new ProcessStartInfo(major == 8 ? Java8 : Java21) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-cp", folder, "ManifestResourceProbe", jar }.Concat(references)) start.ArgumentList.Add(arg);
        foreach (string name in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(name);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(stdout, stderr); Require(process.ExitCode == 0, "Controlled manifest resource probe failed: " + stderr.Result);
        string[] lines = stdout.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return (lines.Contains("MISSING:true"), lines.Where(l => l.StartsWith("TARGET:", StringComparison.Ordinal)).Select(l => Encoding.UTF8.GetString(Convert.FromBase64String(l[7..]))).ToArray());
    }
}
