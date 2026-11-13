using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLCS;
using PCLLF.MMCC.Core;

internal static partial class MmcRuntimeLaunchTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC exact bridge has deterministic owned support bytes and reviewed source", BridgeIdentity);
        yield return ("MMC production runtime preserves actual Java8 Java17 and Java21 argv through original Wrapper and exact bridge", ActualArgv);
        yield return ("MMC runtime macros distinguish Minecraft version instance name session and owned paths", MacrosAndPrivacy);
        yield return ("MMC runtime refuses unbound execution variables account overrides Java conflicts and changed inputs", Refusals);
        yield return ("MMC classpath inspector preserves shadow order and resolves Java multi-release entries", ClasspathAndMultiRelease);
        yield return ("MMC deep 92-JAR launch preserves early system-loader classpath and bounded compressed transport", LongArguments);
        yield return ("MMC runtime reserves the final managed-session nonce at the Windows command boundary", SessionNonceBudget);
        yield return ("MMC readonly JAR namespace permits resources without weakening extraction", ReadOnlyJarNamespace);
        yield return ("MMC manifest absent inputs remain gated through final admission and actual JDK file URLs", ManifestAbsentInputs);
        yield return ("MMC short owned native paths load the same locked SDL3 through actual Java21 and LWJGL", ShortNativePaths);
        yield return ("MMC owned authentication agents retain runtime authority privacy and Java constraints", AuthenticationAgents);
        yield return ("MMC launch plan applies the package environment mapping at the real process boundary", EnvironmentBoundary);
        yield return ("MMC package wrapper composes with each Java wrapper mode at the real process boundary", WrapperCommandBoundary);
    }
    private static void BridgeIdentity() {
        byte[] bytes = MmcRuntimeBridgeTransport.CreateJarBytes();
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (digest != MmcRuntimeBridgeTransport.JarSha256() || !MmcRuntimeBridgeTransport.JavaSource.Contains("getSystemClassLoader", StringComparison.Ordinal)) throw new Exception("Bridge identity/source missing.");
    }
    private static string Java17 => RequiredJava("PCLLF_MMC_TEST_JAVA17");
    private static string Java8 => RequiredJava("PCLLF_MMC_TEST_JAVA8");
    private static string Java21 => RequiredJava("PCLLF_MMC_TEST_JAVA21");
    private static string RequiredJava(string variable) {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException("Required explicit Java fixture executable is missing: " + variable + ". Runtime acceptance is not passed or skipped.");
        return path;
    }
    private static readonly Lazy<Jars> Compiled = new(BuildJars);
    private sealed record Jars(byte[] First, byte[] Second, byte[] Main, byte[] MultiRelease, byte[] UnmarkedVersions, byte[] Wrapper);
    private const string ProbeSource = """
package fixture;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
public final class Probe {
  public static void main(String[] args) {
    for (String arg : args) System.out.println("MARG:" + Base64.getEncoder().encodeToString(arg.getBytes(StandardCharsets.UTF_8)));
    System.out.println("MMODE:__MODE__");
    for (String key : new String[]{"fixture.value","fixture.loaderStartupCp","java.class.path","user.dir","java.io.tmpdir","oolloo.jlw.tmpdir","org.lwjgl.system.SharedLibraryExtractPath","java.specification.version"})
      System.out.println("MPROP:" + key + ":" + Base64.getEncoder().encodeToString(System.getProperty(key, "").getBytes(StandardCharsets.UTF_8)));
    System.out.println("MLOADER:" + ClassLoader.getSystemClassLoader().getClass().getName());
  }
}
""";
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Jars BuildJars() {
        string root = Path.Combine(Path.GetTempPath(), "PCLLF-MmcRuntimeTests", "compile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string javac = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVAC") ?? Path.Combine(Path.GetDirectoryName(Java17)!, OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        Require(File.Exists(javac), "Explicit Java17 fixture compiler is missing.");
        string bridgeSource = Path.Combine(root, "ArgvBridge.java"); File.WriteAllText(bridgeSource, MmcRuntimeBridgeTransport.JavaSource, new UTF8Encoding(false));
        RunTool(javac, ["--release", "8", "-encoding", "UTF-8", "-g:none", "-d", root, bridgeSource]);
        Require(Hash(File.ReadAllBytes(Path.Combine(root, "pcllf", "mmc", "runtime", "ArgvBridge.class"))) == MmcRuntimeBridgeTransport.ClassSha256, "Reviewed bridge source differs from embedded Java8 bytecode.");
        byte[] CompileProbe(string mode, int release) {
            string folder = Path.Combine(root, mode); Directory.CreateDirectory(Path.Combine(folder, "fixture"));
            string source = Path.Combine(folder, "fixture", "Probe.java"); File.WriteAllText(source, ProbeSource.Replace("__MODE__", mode, StringComparison.Ordinal), new UTF8Encoding(false));
            RunTool(javac, ["--release", release.ToString(), "-encoding", "UTF-8", "-g:none", source]);
            return File.ReadAllBytes(Path.Combine(folder, "fixture", "Probe.class"));
        }
        byte[] first = CompileProbe("first", 8), second = CompileProbe("second", 8), main = CompileProbe("main", 8), mr = CompileProbe("mr17", 17);
        string loaderSource = Path.Combine(root, "TestLoader.java"); File.WriteAllText(loaderSource, """
package fixture;
public final class TestLoader extends ClassLoader {
  public TestLoader(ClassLoader parent) {
    super(parent);
    System.setProperty("fixture.loaderStartupCp", System.getProperty("java.class.path"));
    int count = Integer.parseInt(System.getProperty("fixture.requireJarCount", "0"));
    for (int i = 0; i < count; i++)
      if (parent.getResource("fixture/probe-library-" + (i < 10 ? "00" : i < 100 ? "0" : "") + i + ".marker") == null)
        throw new IllegalStateException("Missing startup classpath JAR " + i);
  }
}
""", new UTF8Encoding(false));
        string tweakSource = Path.Combine(root, "Tweak.java"); File.WriteAllText(tweakSource, "package fixture; public final class Tweak {}", new UTF8Encoding(false));
        RunTool(javac, ["--release", "8", "-g:none", "-d", root, loaderSource, tweakSource]);
        byte[] loader = File.ReadAllBytes(Path.Combine(root, "fixture", "TestLoader.class")), tweak = File.ReadAllBytes(Path.Combine(root, "fixture", "Tweak.class"));
        string repo = Directory.GetCurrentDirectory(); while (!File.Exists(Path.Combine(repo, "PCLCS", "Launch", "JavaWrapper.jar"))) repo = Path.GetDirectoryName(repo) ?? throw new Exception("Cannot locate known project Wrapper input.");
        byte[] wrapper = File.ReadAllBytes(Path.Combine(repo, "PCLCS", "Launch", "JavaWrapper.jar"));
        return new(Jar(new() { ["fixture/Probe.class"] = first, ["fixture/TestLoader.class"] = loader, ["fixture/Tweak.class"] = tweak }),
            Jar(new() { ["fixture/Probe.class"] = second }), Jar(new() { ["fixture/Probe.class"] = main }),
            Jar(new() { ["fixture/Probe.class"] = first, ["META-INF/versions/17/fixture/Probe.class"] = mr }, "Manifest-Version: 1.0\r\nMulti-Release: true\r\n\r\n"),
            Jar(new() { ["fixture/Probe.class"] = first, ["META-INF/versions/17/fixture/Probe.class"] = mr }), wrapper);
    }
    private static byte[] Jar(Dictionary<string, byte[]> files, string? manifest = null) {
        using MemoryStream output = new();
        using (ZipArchive jar = new(output, ZipArchiveMode.Create, true)) {
            if (manifest is not null) files = new(files) { ["META-INF/MANIFEST.MF"] = Encoding.UTF8.GetBytes(manifest) };
            foreach (var item in files) { var entry = jar.CreateEntry(item.Key); entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var target = entry.Open(); target.Write(item.Value); }
        }
        return output.ToArray();
    }
    private static void RunTool(string executable, string[] arguments) {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Self-authored Java fixture compiler did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Fixture compiler timeout."); }
        Task.WaitAll(output, errors); Require(process.ExitCode == 0, "Self-authored Java fixture compilation failed.");
    }
    private sealed class Fixture : IDisposable {
        private readonly string mutableScope=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        internal string Root { get; }
        internal MmcRuntimeLaunchRequest Request { get; }
        internal MmcResolvedProfile Sources { get; }
        internal Fixture(int javaMajor = 17, bool multiRelease = false, bool loader = true, string? unknownArgument = null, MmcRuntimeWrapperMode wrapperMode = MmcRuntimeWrapperMode.JavaWrapper,
            bool unmarkedVersions = false, bool laterTooNew = false, bool firstTooNew = false, bool longClasspath = false, Func<byte[], byte[]>? firstJarTransform = null) {
            Root = Path.Combine(Path.GetTempPath(), "PCLLF-MmcRuntimeTests", "run-" + Guid.NewGuid().ToString("N"));
            string instance = Path.Combine(Root, "versions", "实例 space");
            if (longClasspath) instance = Path.Combine(Root, "versions", "deep-" + new string('d', Math.Max(1, 139 - Root.Length - "\\versions\\deep-\\minecraft".Length)));
            Directory.CreateDirectory(Path.Combine(instance, "minecraft"));
            var paths = MmcInstancePaths.Resolve(instance, "minecraft"); string view = new('f', 64);
            byte[] first = unmarkedVersions ? Compiled.Value.UnmarkedVersions : multiRelease ? Compiled.Value.MultiRelease : Compiled.Value.First;
            if (firstJarTransform is not null) first = firstJarTransform(first);
            if (firstTooNew) first = WithClassVersion(first, 99);
            byte[] second = laterTooNew ? WithClassVersion(Compiled.Value.Second, 99) : Compiled.Value.Second;
            byte[] main = laterTooNew ? WithClassVersion(Compiled.Value.Main, 99) : Compiled.Value.Main;
            var jarBytes = new Dictionary<string, byte[]> { ["first"] = first, ["second"] = second, ["main"] = main };
            List<string> libraries = ["first", "second"];
            if (longClasspath) {
                libraries = Enumerable.Range(0, 91).Select(i => i == 0 ? "first" : "dependency-" + i.ToString("D3") + "-" + new string('x', 24)).ToList();
                for (int i = 0; i < libraries.Count; i++) jarBytes[libraries[i]] = WithFile(i == 0 ? first : second, "fixture/probe-library-" + i.ToString("D3") + ".marker", Encoding.UTF8.GetBytes(i.ToString()));
                jarBytes["main"] = main = WithFile(main, "fixture/probe-library-091.marker", Encoding.UTF8.GetBytes("91"));
            }
            object Library(string name, byte[] bytes) => new { name, downloads = new { artifact = new { url = "https://example.invalid/" + name.Replace(':', '-'), sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), sha256 = Hash(bytes), size = bytes.Length } } };
            string game = "--username ${auth_player_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --session ${auth_session} --userType ${user_type} --userProperties ${user_properties} --profile ${profile_name} --mc ${version_name} --type ${version_type} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --libraries ${library_directory} --primary ${primary_jar} --width ${resolution_width} --height ${resolution_height} --value '' --trail 'tail\\' --中文 '中文 path'";
            if (longClasspath) game += " --cpEquivalent ${classpath}";
            if (unknownArgument is not null) game += " " + unknownArgument;
            string patch = JsonSerializer.Serialize(new Dictionary<string, object?> {
                ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["type"] = "release", ["mainClass"] = "fixture.Probe", ["mainJar"] = Library("fixture:main:1:client", main),
                ["libraries"] = libraries.Select(name => Library("fixture:" + name + ":1", jarBytes[name])).ToArray(),
                ["minecraftArguments"] = game, ["compatibleJavaMajors"] = new[] { 8, 17, 21, 24, 26 },
                ["+jvmArgs"] = loader ? new[] { "-Djava.system.class.loader=fixture.TestLoader" } : [], ["+tweakers"] = loader ? new[] { "fixture.Tweak" } : []
            });
            string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}";
            Dictionary<string, string> metadata = new() { ["mmc-pack.json"] = manifest, ["patches/net.minecraft.json"] = patch };
            var parsed = MmcPackageReader.Parse(new(metadata.Select(m => new MmcArchiveEntry(m.Key)).ToArray(), metadata));
            var resolved = MmcComponentResolver.Resolve(parsed, []); Require(resolved.IsComplete, "Runtime fixture source resolution failed.");
            Sources = resolved.Profile!;
            var compiled = MmcProfileCompiler.Compile(resolved.Profile!, new("windows", "amd64", javaMajor)); Require(!compiled.HasErrors, "Runtime fixture compile failed.");
            var plan = compiled.Profile!;
            List<MmcMaterializedArtifactFact> facts = []; List<MmcRuntimeArtifactTarget> targets = [];
            foreach (var artifact in plan.Classpath) {
                byte[] bytes = jarBytes[artifact.Coordinate!.Artifact]; string relative = "PCL/MmcRuntime/" + view + "/libraries/" + artifact.RelativePath;
                string path = paths.ResolveRelative(MmcPathRole.Instance, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
                string recovery = "PCL/MmcArtifacts/sha256/" + Hash(bytes); string original = paths.ResolveRelative(MmcPathRole.Instance, recovery); Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.WriteAllBytes(original, bytes);
                facts.Add(new(artifact.Key, artifact.SourceRevision, Hash(bytes), bytes.Length, recovery, relative, Convert.ToHexStringLower(SHA1.HashData(bytes))));
                targets.Add(new(artifact.Key, relative, path));
            }
            string bundle = new('b', 64), prefix = "PCL/MmcSource/" + bundle + "/";
            var definition = new MmcInstanceDefinition(1, "Mmc", "prism-v1", Guid.NewGuid(), 1, new(new string('a', 64), "fixture.zip"),
                new("", bundle, plan.SourceFingerprint, [new("mmc-pack.json", Hash(Encoding.UTF8.GetBytes(manifest)), prefix + "mmc-pack.json", MmcSourceRecordKind.Manifest),
                    new("patches/net.minecraft.json", Hash(Encoding.UTF8.GetBytes(patch)), prefix + "patches/net.minecraft.json", MmcSourceRecordKind.Component, "net.minecraft", "1.7.10", MmcComponentSourceKind.PackageLocal)]), new("minecraft"), [], [], []);
            var bound = MmcPlanBinder.Bind(definition, plan, facts, new(1, 1, MmcBindingIntent.InitialImport, Hash(Encoding.UTF8.GetBytes("{}")), 1)); Require(!bound.HasErrors, "Runtime fixture byte binding failed.");
            foreach (var item in metadata) { string path = paths.ResolveRelative(MmcPathRole.Instance, prefix + item.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, item.Value, new UTF8Encoding(false)); }
            File.WriteAllText(paths.ResolveRelative(MmcPathRole.Instance, "PCL/InstanceDefinition.json"), MmcDefinitionCodec.Serialize(bound.Definition!), new UTF8Encoding(false));
            string supportRoot = paths.ResolveRelative(MmcPathRole.Runtime, view + "/support"); Directory.CreateDirectory(supportRoot);
            string wrapper = Path.Combine(supportRoot, "JavaWrapper.jar"), bridge = Path.Combine(supportRoot, "MmcRuntimeBridge.jar");
            File.WriteAllBytes(wrapper, Compiled.Value.Wrapper); byte[] bridgeBytes = MmcRuntimeBridgeTransport.CreateJarBytes(); File.WriteAllBytes(bridge, bridgeBytes);
            string java = javaMajor switch { 8 => Java8, 17 => Java17, 21 => Java21, _ => throw new ArgumentOutOfRangeException(nameof(javaMajor)) }; string javaHash = Hash(File.ReadAllBytes(java));
            var settings = new MmcRuntimeSettingsSnapshot("Instance Display", "PCLLF", "test", 16, 64, 640, 360, Guid.NewGuid()) {
                WrapperMode = wrapperMode, UserJvmTokens = longClasspath ? ["-Dfixture.value=regex\\\\d+ trailing\\", "-Dfixture.requireJarCount=92"] : ["-Dfixture.value=regex\\\\d+ trailing\\"], ExtraGameTokens = ["", "中文 supplementary 😀", "double\\\\slashes", "trailing\\", "quoted\"value"], AppendResolutionArguments = false
            };
            Request = new(bound.Definition!, bound.Plan!, paths, targets.ToImmutableArray(), new(java, javaHash, javaHash, javaMajor, "amd64", 64), settings,
                new("PlayerOne", new string('1', 32), "synthetic-secret-token-one", "token:synthetic-secret-token-one:uuid", "legacy", "{\"synthetic\":\"property\"}"),
                new(view, paths.ResolveRelative(MmcPathRole.Runtime, view + "/natives"), paths.ResolveRelative(MmcPathRole.Runtime, view + "/assets"), paths.ResolveRelative(MmcPathRole.Runtime, view + "/assets/virtual/legacy")),
                new(wrapper, Hash(Compiled.Value.Wrapper), Compiled.Value.Wrapper.Length), new(bridge, Hash(bridgeBytes), bridgeBytes.Length)){RuntimeAuthority=FixtureAuthority(mutableScope)};
        }
        internal void RemoveMutable(MmcRuntimeLaunchPlan plan) {
            string target = Path.GetFullPath(plan.MutableDirectories.SessionRoot);
            string expected=plan.RuntimeBinding.MutableLayout is null?Path.Combine(Request.Paths.RuntimeRoot,"sessions",Request.Settings.SessionId.ToString("N")):
                Path.Combine(mutableScope,"m",Request.Definition.InstanceId.ToString("N"),Request.Settings.SessionId.ToString("N"));
            Require(target.Equals(Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase), "Test cleanup escaped its computed owned fixture.");
            Directory.Delete(target, true);
        }
        public void Dispose() {
            string root = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCLLF-MmcRuntimeTests"));
            Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(root)["run-".Length..], "N", out _), "Fixture cleanup target is not owned.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            DeleteMutableFixture(mutableScope);
        }
    }
    private sealed record Output(ImmutableArray<string> Arguments, Dictionary<string, string> Properties, string Mode, string Loader);
    private static Output Run(MmcRuntimeLaunchPlan plan) {
        using var process = Process.Start(plan.CreateStartInfo()) ?? throw new Exception("Controlled Java runtime did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Controlled Java runtime timeout."); }
        Task.WaitAll(stdout, stderr);
        Require(process.ExitCode == 0, "Controlled synthetic Java runtime failed (exit " + process.ExitCode + "): " + plan.Redact(stderr.Result + "\n" + stdout.Result));
        var arguments = ImmutableArray.CreateBuilder<string>(); Dictionary<string, string> properties = new(); string mode = "", loader = "";
        foreach (string line in stdout.Result.Replace("\r\n", "\n").Split('\n')) {
            if (line.StartsWith("MARG:", StringComparison.Ordinal)) arguments.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line[5..])));
            if (line.StartsWith("MPROP:", StringComparison.Ordinal)) { int split = line.IndexOf(':', 6); properties[line[6..split]] = Encoding.UTF8.GetString(Convert.FromBase64String(line[(split + 1)..])); }
            if (line.StartsWith("MMODE:", StringComparison.Ordinal)) mode = line[6..];
            if (line.StartsWith("MLOADER:", StringComparison.Ordinal)) loader = line[8..];
        }
        return new(arguments.ToImmutable(), properties, mode, loader);
    }
    private static void ActualArgv() {
        foreach (int major in new[] { 8, 17, 21 }) {
            using Fixture fixture = new(major, unknownArgument: "--emptyMacro ${client_token} --literalTab left\tright --unclosed \"unterminated");
            var account = fixture.Request.Account;
            var request = fixture.Request with { Account = new(account.Name, account.Uuid, account.AccessToken, account.Session, account.UserType, account.UserPropertiesJson, clientToken: "") };
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult();
            var observed = Run(plan);
            Require(observed.Properties["java.specification.version"] == (major == 8 ? "1.8" : major.ToString()), "Explicit fixture executable does not actually run the requested Java major.");
            Require(observed.Mode == "first" && observed.Loader == "fixture.TestLoader", "Actual VM CP order or startup system loader differs from the bound plan.");
            Require(observed.Arguments.TakeLast(5).SequenceEqual(fixture.Request.Settings.ExtraGameTokens), "Actual Wrapper/bridge argv lost empty, Unicode, slash or quote semantics.");
            Require(Flag(observed, "--value") == "''" && Flag(observed, "--trail") == "'tail\\'" && Flag(observed, "--中文") == "'中文" && observed.Arguments.Contains("path'") &&
                Flag(observed, "--emptyMacro") == "" && Flag(observed, "--literalTab") == "left\tright" && Flag(observed, "--unclosed") == "\"unterminated",
                "Actual native legacy argv changed literal quotes/backslashes/tabs or dropped an empty post-split macro expansion.");
            Require(observed.Properties["fixture.value"] == "regex\\\\d+ trailing\\", "Wrapper reparsing corrupted JVM property values.");
            Require(observed.Properties["user.dir"].Equals(fixture.Request.Paths.GameRoot, StringComparison.OrdinalIgnoreCase), "Actual process working directory is not GameRoot.");
            foreach (string property in new[] { "java.io.tmpdir", "oolloo.jlw.tmpdir", "org.lwjgl.system.SharedLibraryExtractPath" }) Require(observed.Properties[property].StartsWith(plan.MutableDirectories.SessionRoot.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "Native/Wrapper extraction escaped the per-session mutable root.");
            var separator = observed.Properties["java.class.path"].Split(';');
            Require(separator.TakeLast(plan.OrderedGameClasspath.Length).SequenceEqual(plan.OrderedGameClasspath.Select(p => Path.GetRelativePath(fixture.Request.Paths.GameRoot, p).Replace('\\', '/'))), "Game classpath order/mainJar tail changed behind Wrapper prefix.");
            MmcRuntimeLaunchBuilder.RevalidateFilesAsync(plan).GetAwaiter().GetResult();
            Console.WriteLine("LEGACY_ARGV java=" + major + " literal-quotes/slash/tab=true empty-macro-token=true exact-host-array=true");
        }
    }
    private static string Flag(Output output, string name) { int i = output.Arguments.IndexOf(name); Require(i >= 0 && i + 1 < output.Arguments.Length, "Expected controlled game flag missing."); return output.Arguments[i + 1]; }
    private static void MacrosAndPrivacy() {
        using Fixture fixture = new(); var first = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult(); var observed = Run(first);
        Require(Flag(observed, "--profile") == "Instance Display" && Flag(observed, "--mc") == "1.7.10" && Flag(observed, "--type") == "release" && Flag(observed, "--session") == fixture.Request.Account.Session, "MMC version/profile/auth_session semantics were flattened.");
        Require(Flag(observed, "--libraries").Contains("/PCL/MmcRuntime/", StringComparison.Ordinal) && Flag(observed, "--gameDir") == fixture.Request.Paths.GameRoot.Replace('\\', '/'), "Runtime macro used a global library/game root.");
        Require(!string.Join("|", first.RedactedArguments).Contains(fixture.Request.Account.AccessToken, StringComparison.Ordinal) && first.RedactedArguments[^1] == "[sensitive argv frame]", "Sensitive frame/token leaked to log-safe arguments.");
        string frame = first.CreateStartInfo().ArgumentList[^1]; Require(first.Redact("frame=" + frame) == "frame=[redacted]", "Sensitive opaque transport frame was not redacted.");
        fixture.RemoveMutable(first);
        var second = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Account = new("Player${not_reparsed}", new string('2', 32), "synthetic-secret-token-two", "different-session-token", "msa", "{\"literal\":\"${not_reparsed}\"}") }).GetAwaiter().GetResult();
        Require(first.NonSensitiveArgumentFingerprint == second.NonSensitiveArgumentFingerprint && first.RuntimeInputFingerprint == second.RuntimeInputFingerprint, "Account values influenced a persisted non-sensitive runtime fingerprint.");
        Require(!first.CreateStartInfo().Environment.ContainsKey("JDK_JAVA_OPTIONS") && !first.CreateStartInfo().Environment.ContainsKey("JAVA_TOOL_OPTIONS"), "Unobserved environment JVM options were inherited.");
    }
    private static void Refusals() {
        using (Fixture unknown = new(unknownArgument: "${unknown_critical}")) Expect("MMC_RUNTIME_VARIABLE", () => MmcRuntimeLaunchBuilder.BuildAsync(unknown.Request).GetAwaiter().GetResult());
        using (Fixture fixture = new()) {
            Expect("MMC_RUNTIME_UNBOUND_INPUT", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Settings = fixture.Request.Settings with { UserJvmTokens = ["@unbound.args"] } }).GetAwaiter().GetResult());
            Expect("MMC_RUNTIME_UNBOUND_INPUT", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Settings = fixture.Request.Settings with { UserJvmTokens = ["-javaagent:unbound.jar"] } }).GetAwaiter().GetResult());
            Expect("MMC_RUNTIME_JAVA", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Account = new("Player", "uuid", "token", "session", "legacy", "{}", javaConstraints: [new("account", MaximumMajor: 8)]) }).GetAwaiter().GetResult());
            Expect("MMC_RUNTIME_WORKING_DIRECTORY", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Settings = fixture.Request.Settings with { UserJvmTokens = ["-Duser.dir=C:/different-root"] } }).GetAwaiter().GetResult());
            string shadow = Path.Combine(Path.GetDirectoryName(fixture.Request.Wrapper!.ActualPath)!, "shadow.jar"); File.WriteAllBytes(shadow, Compiled.Value.First);
            Expect("MMC_CLASSPATH_SUPPORT_SHADOW", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Wrapper = new(shadow, Hash(Compiled.Value.First), Compiled.Value.First.Length) }).GetAwaiter().GetResult());
            string jar = fixture.Request.Targets[0].ActualPath; File.AppendAllText(jar, "tamper");
            Expect("MMC_RUNTIME_INPUT_CHANGED", () => MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult());
        }
    }
    private static void ClasspathAndMultiRelease() {
        foreach (bool hostExtra in new[] { false, true }) {
            using Fixture fixture = new(21, loader: false, unknownArgument: hostExtra ? null : "--tweakClass fixture.Tweak");
            var request = hostExtra ? fixture.Request with { Settings = fixture.Request.Settings with { ExtraGameTokens = ["--tweakClass=fixture.Tweak"] } } : fixture.Request;
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult();
            Require(plan.RequiredClassNames.SequenceEqual(new[] { "fixture.Probe", "fixture.Tweak" }), "Raw/host tweaker was lost before the actual JAR inspection.");
            var inspection = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            Require(inspection.CoreObservations.All(jar => jar.Classes.Count(value => value.ClassName == "fixture.Tweak") == 1) &&
                inspection.EffectiveClasses.Single(value => value.ClassName == "fixture.Tweak").ClasspathPosition == 0, "Tweaker facts omitted later-JAR absence or changed the first winner.");
            var authority = MmcLaunchAuthorityEvaluator.Evaluate(request.Definition, request.BoundPlan, fixture.Sources,
                new(request.Java.ExecutableSha256, request.Wrapper!.Sha256, plan.NonSensitiveArgumentFingerprint) {
                    AdditionalJvmArguments = plan.NonSensitiveAdditionalJvmArguments,
                    AdditionalRequiredClasses = plan.RequiredClassNames
                }, inspection.CoreObservations);
            Require(authority.HasAuthority && authority.Proof!.ClassBindings.Any(value => value.ClassName == "fixture.Tweak"), "Actual inspected raw/host tweaker facts did not authorize the bound chain.");
            Require(Run(plan).Arguments.Contains(hostExtra ? "--tweakClass=fixture.Tweak" : "fixture.Tweak"), "Actual Java21 argv lost the raw/host tweaker token.");
        }
        using (Fixture fixture = new(8, loader: false)) {
            var request = fixture.Request with { Settings = fixture.Request.Settings with { ExtraGameTokens = ["--tweakClass=fixture.Missing"] } };
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult();
            bool started = false;
            Expect("MMC_CLASSPATH_CLASS_MISSING", () => {
                _ = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
                started = true;
                _ = Run(plan);
            });
            Require(!started, "A missing host tweaker reached Process.Start just because Java8 satisfied an old heuristic.");
        }
        using (Fixture fixture = new()) {
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inspection = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            var selected = inspection.EffectiveClasses.Single(c => c.ClassName == "fixture.Probe");
            Require(selected.ClasspathPosition == 0 && inspection.CoreObservations.Count(j => j.Classes.Any(c => c.ClassName == "fixture.Probe" && c.Present)) == 3, "Classpath shadow order/presence was flattened.");
        }
        foreach (int major in new[] { 8, 17 }) {
            using Fixture fixture = new(major, multiRelease: true, loader: false);
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inspection = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            var selected = inspection.EffectiveClasses.Single(c => c.ClassName == "fixture.Probe");
            var output = Run(plan);
            Require(selected.Release == (major == 8 ? 0 : 17) && output.Mode == (major == 8 ? "first" : "mr17"), "Manifest MR flag/actual Java release did not match observed and executed class selection.");
        }
        (string[] Flags, int Release, string Mode, bool Marked)[] properties = [
            (["-Djdk.util.jar.enableMultiRelease=false"], 0, "first", true),
            (["-Djdk.util.jar.version=8"], 0, "first", true),
            (["-Djdk.util.jar.version=-1"], 0, "first", true),
            (["-Djdk.util.jar.version=99"], 17, "mr17", true),
            (["-Djdk.util.jar.enableMultiRelease=false", "-Djdk.util.jar.enableMultiRelease=force"], 17, "mr17", true),
            (["-Djdk.util.jar.enableMultiRelease=FALSE"], 17, "mr17", true),
            (["-Djdk.util.jar.enableMultiRelease=force"], 0, "first", false)
        ];
        foreach (var property in properties) {
            using Fixture fixture = new(17, multiRelease: property.Marked, unmarkedVersions: !property.Marked, loader: false);
            var request = fixture.Request with { Settings = fixture.Request.Settings with { UserJvmTokens = fixture.Request.Settings.UserJvmTokens.AddRange(property.Flags) } };
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult();
            var inspection = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            var output = Run(plan);
            Require(inspection.EffectiveClasses.Single(c => c.ClassName == "fixture.Probe").Release == property.Release && output.Mode == property.Mode,
                "Effective JVM MR properties diverged from actual JarFile class selection.");
        }
        using (Fixture fixture = new(17, multiRelease: true, loader: false)) {
            var request = fixture.Request with { Settings = fixture.Request.Settings with { UserJvmTokens = fixture.Request.Settings.UserJvmTokens.Add("-Djdk.util.jar.version=not-an-integer") } };
            Expect("MMC_CLASSPATH_MR_VERSION", () => MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult());
        }
        foreach (int major in new[] { 8, 17 }) {
            using Fixture fixture = new(major, laterTooNew: true);
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inspected = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            Require(inspected.EffectiveClasses.Single(c => c.ClassName == "fixture.Probe").ClasspathPosition == 0 &&
                inspected.CoreObservations.Count(j => j.Classes.Any(c => c.ClassName == "fixture.Probe" && c.Present)) == 3 && Run(plan).Mode == "first",
                "Unused later bytecode rejected a valid first-wins classpath or lost explicit presence facts.");
        }
        using (Fixture fixture = new(17, firstTooNew: true)) {
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            Expect("MMC_CLASSPATH_CLASS_VERSION", () => MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult());
            using var process = Process.Start(plan.CreateStartInfo()) ?? throw new Exception("Controlled incompatible-class Java did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Controlled incompatible-class Java timeout."); }
            Task.WaitAll(stdout, stderr);
            Require(process.ExitCode != 0 && stderr.Result.Contains("UnsupportedClassVersionError", StringComparison.Ordinal), "Effective first-winner rejection did not match actual JVM behavior.");
        }
    }
    private static void SessionNonceBudget() {
        using Fixture fixture = new(17, wrapperMode: MmcRuntimeWrapperMode.DirectMain);
        MmcRuntimeLaunchRequest WithArgument(string argument) => fixture.Request with { Settings = fixture.Request.Settings with { ExtraGameTokens = [argument] } };
        var baseline = MmcRuntimeLaunchBuilder.BuildAsync(WithArgument("")).GetAwaiter().GetResult();
        var start = baseline.CreateStartInfo();
        string nonce = MmcSessions.NonceArgumentPrefix + Guid.NewGuid().ToString("N");
        int padding = checked(32767 - (int)MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(start.FileName, start.ArgumentList.Prepend(nonce)) + 2);
        fixture.RemoveMutable(baseline);
        var boundary = MmcRuntimeLaunchBuilder.BuildAsync(WithArgument(new string('x', padding))).GetAwaiter().GetResult();
        start = boundary.CreateStartInfo();
        Require(!start.ArgumentList.Any(argument => argument.StartsWith(MmcSessions.NonceArgumentPrefix, StringComparison.Ordinal)), "Budget reservation inserted a fake nonce into runtime arguments.");
        Require(MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(start.FileName, start.ArgumentList.Prepend(nonce)) == 32767, "Final runtime arguments plus the actual-sized session nonce missed the exact Windows boundary.");
        fixture.RemoveMutable(boundary);
        Expect("MMC_RUNTIME_COMMAND_LENGTH", () => MmcRuntimeLaunchBuilder.BuildAsync(WithArgument(new string('x', padding + 1))).GetAwaiter().GetResult());
    }
    private static void LongArguments() {
        Require(MmcRuntimeWindowsCommandLine.LengthIncludingTerminator("java.exe", []) == 11, "Windows executable quote/NUL budget changed.");
        (string Argument, long Length)[] quoting = [("", 14), ("a b", 17), ("a\nb", 17), ("a\"b", 18), ("a\\\"b", 20), ("a \\", 18), ("😀", 14)];
        foreach (var sample in quoting) Require(MmcRuntimeWindowsCommandLine.LengthIncludingTerminator("java.exe", [sample.Argument]) == sample.Length, "Windows CRT argument quoting length changed.");
        MmcRuntimeWindowsCommandLine.Validate("java.exe", [new string('x', 32755)]);
        Expect("MMC_RUNTIME_COMMAND_LENGTH", () => MmcRuntimeWindowsCommandLine.Validate("java.exe", [new string('x', 32756)]));
        string probeExecutable = Environment.ProcessPath ?? throw new Exception("Native command-line probe executable missing.");
        VerifyActualCommandLength(probeExecutable, ["--command-line-probe", .. quoting.Select(sample => sample.Argument)]);
        int remaining = checked(32767 - (int)MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(probeExecutable, ["--command-line-probe"]) - 1);
        VerifyActualCommandLength(probeExecutable, ["--command-line-probe", new string('x', remaining)]);
        foreach (int major in new[] { 8, 17, 21 }) {
            using Fixture fixture = new(major, longClasspath: true);
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            Require(plan.OrderedGameClasspath.Length == 92 && fixture.Request.Paths.GameRoot.Length >= 139, "Deep GTNH-scale synthetic fixture was shortened.");
            Require(Path.GetFileName(plan.OrderedGameClasspath[^1]) == "main-1-client.jar", "Bound mainJar is no longer last in the game CP.");
            string absolute = string.Join(';', new[] { fixture.Request.Wrapper!.ActualPath, fixture.Request.Bridge!.ActualPath }.Concat(plan.OrderedGameClasspath).Select(p => p.Replace('\\', '/')));
            long originalCpFrameLowerBound = absolute.Length + ((Encoding.UTF8.GetByteCount(absolute) + 2L) / 3) * 4;
            Require(originalCpFrameLowerBound > 32767, "Synthetic fixture does not reproduce the old duplicated-CP command-line overflow.");
            var start = plan.CreateStartInfo();
            long length = MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(start.FileName, start.ArgumentList);
            Require(length <= 32767, "Deep GTNH-scale command line exceeds actual Windows quote/NUL limit.");
            var observed = Run(plan);
            Require(observed.Properties["java.specification.version"] == (major == 8 ? "1.8" : major.ToString()), "Deep fixture did not use the requested actual Java major.");
            Require(observed.Mode == "first" && observed.Loader == "fixture.TestLoader", "Deep CP lost shadow order or custom system loader.");
            Require(observed.Properties["fixture.loaderStartupCp"] == observed.Properties["java.class.path"], "Custom loader did not see the final game CP at VM initialization.");
            string[] vmCp = observed.Properties["java.class.path"].Split(';');
            Require(vmCp.All(p => !Path.IsPathRooted(p)), "Transport classpath still repeats the absolute GameRoot.");
            var resolved = vmCp.Select(p => Path.GetFullPath(p, fixture.Request.Paths.GameRoot)).ToArray();
            Require(resolved.TakeLast(92).SequenceEqual(plan.OrderedGameClasspath, StringComparer.OrdinalIgnoreCase), "Relative CP changed the exact bound actual files or order.");
            Require(Flag(observed, "--cpEquivalent") == string.Join(';', vmCp.TakeLast(92)), "Game classpath macro differs from the VM game CP.");
            Require(observed.Arguments.TakeLast(5).SequenceEqual(fixture.Request.Settings.ExtraGameTokens), "Compressed frame lost empty/Unicode/slash/quote arguments.");
            Require(observed.Properties["user.dir"].Equals(fixture.Request.Paths.GameRoot, StringComparison.OrdinalIgnoreCase), "Relative CP root is not bound to GameRoot.");
            string frame = start.ArgumentList[^1];
            Require(plan.RedactedArguments[^1] == "[sensitive argv frame]" && plan.Redact(frame) == "[redacted]", "Compressed sensitive frame escaped whole-frame redaction.");
            Require(!string.Join('|', plan.RedactedArguments).Contains(fixture.Request.Account.AccessToken, StringComparison.Ordinal), "Deep launch leaked live token in log-safe arguments.");
            var inspection = MmcClasspathInspector.ObserveAsync(plan).GetAwaiter().GetResult();
            Require(inspection.EffectiveClasses.Single(c => c.ClassName == "fixture.Probe").ClasspathPosition == 0, "Deep CP observation differs from executed shadow order.");
            fixture.RemoveMutable(plan);
            var second = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request with { Account = new("Second Player", new string('2', 32), "another-synthetic-token", "another-session", "msa", "{\"other\":\"account\"}") }).GetAwaiter().GetResult();
            Require(second.NonSensitiveArgumentFingerprint == plan.NonSensitiveArgumentFingerprint && second.RuntimeInputFingerprint == plan.RuntimeInputFingerprint, "Compressed account switch changed non-sensitive fingerprints.");
            Console.WriteLine("  Java " + major + ": 92 JARs, GameRoot " + fixture.Request.Paths.GameRoot.Length + " UTF-16 units, old CP/frame lower bound " + originalCpFrameLowerBound + ", actual command " + length + " including NUL.");
        }
        foreach (int major in new[] { 8, 17, 21 }) using (Fixture fixture = new(major)) {
            var sameDirectory = fixture.Request with { Settings = fixture.Request.Settings with { UserJvmTokens = fixture.Request.Settings.UserJvmTokens.Add("-Duser.dir=" + fixture.Request.Paths.GameRoot.Replace('\\', '/')) } };
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(sameDirectory).GetAwaiter().GetResult();
            Require(Run(plan).Properties["user.dir"] == fixture.Request.Paths.GameRoot, "Equivalent approved user.dir was not restored to canonical GameRoot.");
            byte[] oversized = new byte[MmcRuntimeBridgeTransport.MaximumFrameBytes + 1];
            using MemoryStream compressed = new(); using (var stream = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) stream.Write(oversized);
            using MemoryStream envelope = new(); WriteInt(envelope, 0x4d4d4333); WriteInt(envelope, oversized.Length); envelope.Write(compressed.ToArray());
            Require(BridgeFails(fixture, UrlBase64(envelope.ToArray())), "Compressed frame exceeding the decoded budget was accepted.");
            envelope.SetLength(0); WriteInt(envelope, 0x4d4d4333); WriteInt(envelope, 1); envelope.Write(compressed.ToArray());
            Require(BridgeFails(fixture, UrlBase64(envelope.ToArray())), "Compressed expansion beyond its declared/bounded length was accepted.");
            string[] limit = [new string('a', 262144), new string('b', 262144), new string('c', 262144), new string('d', 262099)];
            string maximum = MmcRuntimeBridgeTransport.Encode("fixture.Probe", limit);
            Require(maximum.Length < 4096, "Highly compressible maximum-size sensitive frame was not compressed.");
            Require(!BridgeFails(fixture, maximum), "Exact maximum decoded frame did not pass Java8-compatible bridge decoding.");
            byte[] encoded = Convert.FromBase64String(maximum.Replace('-', '+').Replace('_', '/') + new string('=', (4 - maximum.Length % 4) % 4));
            Require(BridgeFails(fixture, UrlBase64(encoded[..^1])), "Truncated zlib frame was accepted.");
            Require(BridgeFails(fixture, UrlBase64(encoded.Concat(new byte[] { 0 }).ToArray())), "Trailing compressed data was accepted.");
            ExpectArgument(() => MmcRuntimeBridgeTransport.Encode("fixture.Probe", limit.Append("x").ToArray()));
            ExpectArgument(() => MmcRuntimeBridgeTransport.Encode("fixture.Probe", [new string('x', MmcRuntimeBridgeTransport.MaximumStringBytes + 1)]));
        }
    }
    private static void VerifyActualCommandLength(string executable, string[] arguments) {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Exception("Native command-line probe did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Native command-line probe timeout."); }
        Task.WaitAll(output, errors);
        Require(process.ExitCode == 0 && long.TryParse(output.Result.Trim(), out long observed) && observed == MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(executable, arguments), "Predicted Windows command length differs from the actual child GetCommandLineW representation. Expected " + MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(executable, arguments) + ", observed " + output.Result.Trim() + "; " + errors.Result);
    }
    private static bool BridgeFails(Fixture fixture, string frame) {
        ProcessStartInfo start = new(fixture.Request.Java.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "-cp", fixture.Request.Bridge!.ActualPath + ";" + fixture.Request.Targets[0].ActualPath, MmcRuntimeBridgeTransport.EntryPoint, frame }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new Exception("Controlled bridge decoder did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Controlled decoder timeout."); }
        Task.WaitAll(output, errors);
        return process.ExitCode != 0;
    }
    private static void WriteInt(Stream output, int value) { Span<byte> bytes = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value); output.Write(bytes); }
    private static string UrlBase64(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static void EnvironmentBoundary() {
        // Exercises the genuine plan and its genuine CreateStartInfo(), so the mapping is proven to reach
        // the boundary the launcher actually starts, not merely a helper of the same shape.
        using (Fixture fixture = new(loader: false)) {
            // No mapping: the launcher-owned temporary directory is asserted and no package entry appears.
            var plain = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var baseline = plain.CreateStartInfo();
            Require(baseline.Environment["TEMP"] == plain.MutableDirectories.TemporaryDirectory && baseline.Environment["TMP"] == plain.MutableDirectories.TemporaryDirectory,
                "The launcher-owned temporary directory was not asserted on the real start info.");
            Require(!baseline.Environment.ContainsKey("PACK_ONE") && !baseline.Environment.ContainsKey("PACK_TWO"), "A mapping appeared without being declared.");
            fixture.RemoveMutable(plain);
        }
        using (Fixture fixture = new(loader: false)) {
            // An active mapping is overlaid literally and cannot displace the launcher boundary.
            var mapped = fixture.Request with { Settings = fixture.Request.Settings with { EnvironmentOverride = [
                new("PACK_ONE", "${INST_DIR}/literal"), new("PACK_TWO", "中文 value 😀"), new("PACK_EMPTY", "")] } };
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(mapped).GetAwaiter().GetResult();
            var observed = plan.CreateStartInfo();
            Require(observed.Environment["PACK_ONE"] == "${INST_DIR}/literal", "A package environment value was macro-expanded at the real boundary.");
            Require(observed.Environment["PACK_TWO"] == "中文 value 😀", "Unicode in a package environment value changed.");
            Require(observed.Environment["PACK_EMPTY"] == "", "An explicit empty package environment value was dropped.");
            Require(observed.Environment["TEMP"] == plan.MutableDirectories.TemporaryDirectory && observed.Environment["TMP"] == plan.MutableDirectories.TemporaryDirectory,
                "A package mapping displaced the launcher-owned temporary directory.");
            // The managed launch re-copies this same block under session admission and must keep it.
            MmcRuntimeLaunchBuilder.RevalidateFilesAsync(plan).GetAwaiter().GetResult();
            var revalidated = plan.CreateStartInfo();
            Require(revalidated.Environment["PACK_ONE"] == "${INST_DIR}/literal" && revalidated.Environment["TEMP"] == plan.MutableDirectories.TemporaryDirectory,
                "The environment block changed across file revalidation.");
            fixture.RemoveMutable(plan);
        }
        using (Fixture fixture = new(loader: false)) {
            // A reserved package name is refused by the genuine builder, not silently dropped.
            var reserved = fixture.Request with { Settings = fixture.Request.Settings with { EnvironmentOverride = [new("TEMP", @"C:\attacker")] } };
            Expect("MMC_RUNTIME_ENV_RESERVED", () => MmcRuntimeLaunchBuilder.BuildAsync(reserved).GetAwaiter().GetResult());
        }
    }
    private static void WrapperCommandBoundary() {
        // D6: the package wrapper composes with the launcher's own JavaWrapper/DirectMain mode; it never
        // replaces it. Both modes are exercised through the genuine plan and the genuine CreateStartInfo().
        foreach (var mode in new[] { MmcRuntimeWrapperMode.JavaWrapper, MmcRuntimeWrapperMode.DirectMain }) {
            // One fixture so the inner argv is compared against itself: two fixtures would differ in their
            // absolute owned paths, and a path difference would mask a real argv change.
            using Fixture fixture = new(multiRelease: false, wrapperMode: mode);
            // The un-wrapped baseline: this is the inner invocation the wrapper must carry unchanged.
            var plain = MmcRuntimeLaunchBuilder.BuildAsync(fixture.Request).GetAwaiter().GetResult();
            var inner = plain.CreateStartInfo();
            string innerEntry = inner.FileName;
            string[] innerArguments = [.. inner.ArgumentList];
            fixture.RemoveMutable(plain);

            var request = fixture.Request with { Settings = fixture.Request.Settings with { WrapperCommand = "wrapper.exe --flag \"two words\" ${INST_MC_DIR}" } };
            var plan = MmcRuntimeLaunchBuilder.BuildAsync(request).GetAwaiter().GetResult();
            var outer = plan.CreateStartInfo();
            // The wrapper is the entry, and the inner Java invocation is its child invocation: entry first, then
            // the exact inner argv. Nothing is joined into one string and nothing is re-split.
            Require(outer.FileName.EndsWith("wrapper.exe", StringComparison.OrdinalIgnoreCase), "The package wrapper was not the process entry.");
            Require(!outer.FileName.Equals(innerEntry, StringComparison.OrdinalIgnoreCase), "The wrapper did not replace the inner entry.");
            // The wrapper's own tokens are the entry plus its two arguments, then the inner entry and its argv.
            Require(outer.ArgumentList.Count == 3 + 1 + innerArguments.Length,
                "The wrapper did not re-emit exactly the inner argv: outer=" + outer.ArgumentList.Count + " inner=" + innerArguments.Length);
            Require(outer.ArgumentList[0] == "--flag" && outer.ArgumentList[1] == "two words", "Wrapper tokens were re-split or quoted differently.");
            // The variable reaches the wrapper in the same forward-slash Java form the stage commands use.
            Require(outer.ArgumentList[2] == MmcRuntimeCommandExecution.CommandPath(fixture.Request.Paths.GameRoot),
                "The wrapper did not expand its instance variable: got [" + outer.ArgumentList[2] + "]");
            Require(outer.ArgumentList[3] == innerEntry, "The inner executable was not passed as the wrapper's child entry.");
            Require(outer.ArgumentList.Skip(4).SequenceEqual(innerArguments), "The inner argv changed when it was passed through the wrapper.");
            // The wrapper mode is preserved inside the inner argv, so the two capabilities composed.
            string[] carried = [.. outer.ArgumentList.Skip(4)];
            if (mode == MmcRuntimeWrapperMode.JavaWrapper) Require(carried.Any(a => a.Contains("oolloo.jlw.Wrapper", StringComparison.Ordinal)), "The JavaWrapper inner argv was lost under a package wrapper.");
            else Require(carried.Contains("fixture.Probe"), "The DirectMain inner argv was lost under a package wrapper.");
            // The environment block and working directory are unchanged: the wrapper inherits the session boundary.
            Require(outer.Environment["TEMP"] == plan.MutableDirectories.TemporaryDirectory && outer.Environment["TMP"] == plan.MutableDirectories.TemporaryDirectory,
                "The wrapper displaced the launcher-owned temporary directory.");
            Require(outer.WorkingDirectory == fixture.Request.Paths.GameRoot, "The wrapper changed the frozen working directory.");
            Require(!outer.UseShellExecute && string.IsNullOrEmpty(outer.Arguments), "The wrapper used a shell or one concatenated argument string.");
            fixture.RemoveMutable(plan);
        }
        // A fresh fixture per refusal: each plan consumes its own owned mutable directory.
        using (Fixture refused = new(loader: false)) {
            var unknown = refused.Request with { Settings = refused.Request.Settings with { WrapperCommand = "wrapper.exe ${INST_NOT_A_REAL_VARIABLE}" } };
            Expect("MMC_RUNTIME_VARIABLE", () => MmcRuntimeLaunchBuilder.BuildAsync(unknown).GetAwaiter().GetResult().CreateStartInfo());
        }
        using (Fixture refused = new(loader: false)) {
            // A credential reference can never reach the boundary through cfg — the reader refuses it first — but
            // a hand-built request can present it, and the boundary still refuses to bind it rather than
            // silently substituting an empty value.
            var credential = refused.Request with { Settings = refused.Request.Settings with { WrapperCommand = "wrapper.exe ${auth_access_token}" } };
            Expect("MMC_RUNTIME_VARIABLE", () => MmcRuntimeLaunchBuilder.BuildAsync(credential).GetAwaiter().GetResult().CreateStartInfo());
        }
    }
    private static void ExpectArgument(Action operation) { try { operation(); } catch (ArgumentException) { return; } throw new Exception("Expected bounded transport argument refusal."); }    private static byte[] WithFile(byte[] originalJar, string name, byte[] contents) {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        using (var input = new MemoryStream(originalJar)) using (var jar = new ZipArchive(input, ZipArchiveMode.Read)) foreach (var entry in jar.Entries) {
            using var body = entry.Open(); using var copy = new MemoryStream(); body.CopyTo(copy); files.Add(entry.FullName, copy.ToArray());
        }
        files.Add(name, contents); return Jar(files);
    }
    private static byte[] WithClassVersion(byte[] originalJar, ushort major) {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        using (var input = new MemoryStream(originalJar)) using (var jar = new ZipArchive(input, ZipArchiveMode.Read)) foreach (var entry in jar.Entries) {
            using var body = entry.Open(); using var copy = new MemoryStream(); body.CopyTo(copy); files.Add(entry.FullName, copy.ToArray());
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(files["fixture/Probe.class"].AsSpan(6, 2), major);
        return Jar(files);
    }
    private static void Expect(string code, Action operation) { try { operation(); } catch (MmcRuntimeLaunchException error) when (error.Code == code) { return; } throw new Exception("Expected runtime refusal " + code); }
}
