// Independently authored fixtures. Host test suite retains its existing license.
using System.Collections.Immutable;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using PCLLF.MMCC.Core;

internal static class MmcProfileTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC source boundary reads optional cfg and ordered metadata requests", OptionalCfg);
        yield return ("MMC legacy cfg includes first line BOM equals escaped hash and General marker", LegacyCfg);
        yield return ("MMC Qt scalar cfg preserves quotes continuations and escaped Unicode", QtCfg);
        yield return ("MMC cfg command aliases and inactive dangerous values are explicit", CfgCapabilities);
        yield return ("MMC cfg active environment mapping is decoded in source order", CfgEnvironment);
        yield return ("MMC cfg inactive environment value is preserved and never applied", CfgEnvironmentInactive);
        yield return ("MMC cfg environment malformed list separators and duplicate names fail closed", CfgEnvironmentMalformed);
        yield return ("MMC cfg environment refuses unverified encodings and launcher-managed names", CfgEnvironmentRefused);
        yield return ("MMC cfg console and PermGen requirements get a difference notice not a substitution", CfgDifferenceNotices);
        yield return ("MMC cfg difference notices stay inactive until the source activates the requirement", CfgDifferenceInactive);
        yield return ("MMC cfg package wrapper is admitted on the same terms as the landed command stages", CfgWrapperCommand);
        yield return ("MMC cfg malformed booleans duplicate keys variants and unknown behavior fail closed", CfgInvalid);
        yield return ("MMC archive root discovery does not depend on directory entries", Roots);
        yield return ("MMC ambiguous roots and missing source metadata fail closed", AmbiguousRoots);
        yield return ("MMC Windows traversal device names and file collisions are rejected", UnsafePaths);
        yield return ("MMC directory spelling aliases preserve both nonconflicting leaves", DirectoryAliases);
        yield return ("MMC implicit parent and explicit directory file role collisions are rejected", FileRoles);
        yield return ("MMC incoming LF ownership records cannot claim managed ownership", ReservedOwnership);
        yield return ("MMC strict JSON rejects duplicate keys including nested escaped spellings", DuplicateJson);
        yield return ("MMC malformed manifest shapes and unknown format return diagnostics", ManifestShapes);
        yield return ("MMC local component version and non-disableable state preserve original declarations", LocalComponents);
        yield return ("MMC patch uid fileId and duplicate identities are checked", PatchIdentity);
        yield return ("MMC dependency requests retain exact version and author order", Dependencies);
        yield return ("MMC conflicting disabled cyclic and exact-version dependency graphs fail", InvalidGraphs);
        yield return ("MMC enabled unknown launch capabilities fail without silently discarding fields", Capabilities);
        yield return ("MMC library roots remain distinct and JVM array tokens remain intact", LibrariesAndTokens);
        yield return ("MMC Java candidate declarations retain discrete positive integers", JavaCandidates);
        yield return ("MMC metadata limits apply per item cumulatively and to input structure", MetadataBudgets);
        yield return ("MMC archive sizes components edges and JSON depth are bounded", StructureBudgets);
        yield return ("MMC cumulative raw and logical prefix path budgets reject product-size allocation", PathBudgets);
        yield return ("MMC results survive source mutation and detached JSON document disposal", Immutability);
    }

    private const string Manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\",\"important\":true}]}";
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static MmcPackageInput Input(string manifest = Manifest, string? cfg = null,
        Dictionary<string, string>? patches = null, string root = "", params MmcArchiveEntry[] extra) {
        Dictionary<string, string> content = new() { [root + "mmc-pack.json"] = manifest };
        if (cfg is not null) content[root + "instance.cfg"] = cfg;
        if (patches is not null) foreach ((string file, string text) in patches) content[root + "patches/" + file] = text;
        return new(content.Select(x => new MmcArchiveEntry(x.Key, System.Text.Encoding.UTF8.GetByteCount(x.Value))).Concat(extra).ToArray(), content);
    }
    private static string Pack(params object[] components) => JsonSerializer.Serialize(new { formatVersion = 1, components });
    private static string Patch(string uid, string? version = "1", object[]? requires = null, object[]? conflicts = null) =>
        JsonSerializer.Serialize(new { formatVersion = 1, uid, version, requires = requires ?? [], conflicts = conflicts ?? [] });
    private static MmcPackage Good(MmcPackageInput input, MmcReadLimits? limits = null) {
        MmcParseResult result = MmcPackageReader.Parse(input, limits);
        Require(!result.HasErrors && result.Package is not null, "Unexpected rejection: " + string.Join(" | ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return result.Package!;
    }
    private static void Error(MmcPackageInput input, string code, MmcReadLimits? limits = null) {
        MmcParseResult result = MmcPackageReader.Parse(input, limits);
        string cfg = input.Metadata.TryGetValue("instance.cfg", out string? text) ? text.Replace("\n", "\\n") : "";
        Require(result.HasErrors && result.Diagnostics.Any(d => d.Code == code && d.Severity == MmcDiagnosticSeverity.Error),
            $"Missing {code} for [{cfg}]: " + string.Join(" | ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }
    private static void OptionalCfg() {
        MmcPackage result = Good(Input());
        Require(result.Configuration.Format == MmcConfigurationFormat.Absent && result.Configuration.Name is null, "Absent cfg was fabricated.");
        Require(result.MissingMetadata is [{ Uid: "net.minecraft", Version: "1.7.10" }], "Explicit metadata request lost.");
        Require(result.ManifestSha256.Length == 64, "Source hash missing.");
    }
    private static void LegacyCfg() {
        MmcPackage result = Good(Input(cfg: "\uFEFFname=First=Name\r\n[General]\r\nnotes=one\\nline\\t\\#literal # comment\r\nOverrideGameTime=false\nOverrideNativeWorkarounds=false"));
        Require(result.Configuration.Name == "First=Name" && result.Configuration.Values["notes"] == "one\nline\t#literal", "Legacy first line/equal/escape changed.");
        Require(Good(Input(cfg: "name=\"a,b\"" )).Configuration.Name == "a,b", "Legacy conditional quote removal changed.");
        Require(Good(Input(cfg: "name=\"plain\"" )).Configuration.Name == "\"plain\"", "Legacy plain quotes were rewritten.");
    }
    private static void QtCfg() {
        string cfg = "[General]\nConfigVersion\t=1.3\nname=\"Hello; world,a=b\"\nnotes=\\x4e2d\\x6587\\nline\nJvmArgs=\"-Dpath=C:\\\\game files\"\\\n -Dnext=true\niconKey=@@fixture";
        var values = Good(Input(cfg: cfg)).Configuration.Values;
        Require(values["name"] == "Hello; world,a=b" && values["notes"] == "中文\nline", "Qt scalar escapes changed.");
        Require(values["JvmArgs"] == "-Dpath=C:\\game files-Dnext=true", "Continuation/quoted spaces changed.");
        Require(values["iconKey"] == "@fixture", "Escaped at sign changed.");
    }
    private static void CfgCapabilities() {
        Good(Input(cfg: "OverrideCommands=false\nPreLaunchCommand=ignored\nOverrideEnv=false\nEnv=@Variant(ignored)"));
        // A landed stage command is admitted, and the package wrapper is admitted on the same terms now that
        // its host composition and fixture landed with it (contract §8 target state).
        Require(Good(Input(cfg: "OverrideCommands=true\nPreLaunchCommand=active")).Configuration.Values["PreLaunchCommand"] == "active",
            "An implemented stage command was not admitted.");
        Require(Good(Input(cfg: "OverrideCommands=true\nWrapperCommand=active")).Configuration.Values["WrapperCommand"] == "active",
            "An admitted package wrapper was not preserved.");
        // The legacy alias activates the same gate, so it reaches the same verdict.
        Require(Good(Input(cfg: "OverrideLaunchCmd=true\nWrapperCommand=active")).Configuration.Values["WrapperCommand"] == "active",
            "The legacy alias did not admit the package wrapper.");
        var result = MmcPackageReader.Parse(Input(cfg: "OverrideCommands=false\nOverrideLaunchCmd=true\nPostExitCommand=inactive"));
        Require(!result.HasErrors && result.Diagnostics.Any(d => d.Code == "MMC_CFG_ALIAS"), "New alias precedence changed.");
        Error(Input(cfg: "OverrideNativeWorkarounds=true"), "MMC_UNSUPPORTED_REQUIRED");
    }
    // An active mapping must decode to ordered literal entries. Order is what the host overlay depends on.
    private static void CfgEnvironment() {
        var legacy = Good(Input(cfg: "OverrideEnv=true\nEnv=FIRST=alpha\\nSECOND=beta\\nTHIRD=gamma")).Configuration;
        Require(legacy.EnvironmentOverride.SequenceEqual(new[] { new MmcEnvironmentVariable("FIRST", "alpha"), new MmcEnvironmentVariable("SECOND", "beta"), new MmcEnvironmentVariable("THIRD", "gamma") }),
            "Legacy environment order or values changed.");
        Require(legacy.Values["Env"] == "FIRST=alpha\nSECOND=beta\nTHIRD=gamma", "Legacy environment source text was not preserved.");
        // The Qt list form separates on an unquoted comma; every other key still refuses one.
        var qt = Good(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=ALPHA=one,BETA=two, GAMMA=th ree ")).Configuration;
        Require(qt.EnvironmentOverride.SequenceEqual(new[] { new MmcEnvironmentVariable("ALPHA", "one"), new MmcEnvironmentVariable("BETA", "two"), new MmcEnvironmentVariable("GAMMA", "th ree") }),
            "Qt environment list lost order, trimmed a value, or dropped a separator.");
        Error(Input(cfg: "ConfigVersion=1.3\nname=a,b"), "MMC_CFG_LIST");
        var quoted = Good(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=\"A=1,b=2\",C=3")).Configuration;
        Require(quoted.EnvironmentOverride.SequenceEqual(new[] { new MmcEnvironmentVariable("A", "1,b=2"), new MmcEnvironmentVariable("C", "3") }),
            "A quoted comma was not kept inside its element.");
        // Escapes in the environment key use the same table as the scalar decoder, including \\, \\x and \\;.
        var escaped = Good(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=PATH=C\\\\tools\\\\bin,HEX=\\x4e2d\\x6587,SEMI=a\\;b")).Configuration;
        Require(escaped.EnvironmentOverride.SequenceEqual(new[] { new MmcEnvironmentVariable("PATH", "C\\tools\\bin"), new MmcEnvironmentVariable("HEX", "中文"), new MmcEnvironmentVariable("SEMI", "a;b") }),
            "Environment escapes drifted from the scalar escape table.");
        // The JSON list form is its own escaping domain and preserves element order.
        var json = Good(Input(cfg: "OverrideEnv=true\nEnv=[\"JSON=one\",\"SECOND=two\"]")).Configuration;
        Require(json.EnvironmentOverride.SequenceEqual(new[] { new MmcEnvironmentVariable("JSON", "one"), new MmcEnvironmentVariable("SECOND", "two") }),
            "JSON environment list lost order or content.");
        // Values are literal: launch-variable syntax is never expanded in an environment entry.
        var literal = Good(Input(cfg: "OverrideEnv=true\nEnv=LITERAL=${INST_DIR}")).Configuration;
        Require(literal.EnvironmentOverride is [{ Name: "LITERAL", Value: "${INST_DIR}" }], "An environment value was macro-expanded.");
        Require(Good(Input(cfg: "OverrideEnv=true\nEnv=BOOLISH=1")).Configuration.EnvironmentOverride is [{ Value: "1" }], "A literal environment value was coerced.");
        // The General section marker and a trailing comment keep the legacy rules for this key too.
        var sectioned = Good(Input(cfg: "[General]\nOverrideEnv=true\nEnv=SECTIONED=yes # comment")).Configuration;
        Require(sectioned.EnvironmentOverride is [{ Name: "SECTIONED", Value: "yes" }], "The legacy comment rule changed for an environment entry.");
    }
    private static void CfgEnvironmentInactive() {
        // An inactive mapping is retained verbatim and never becomes an active override.
        foreach (string value in new[] { "@Variant(ignored)", "A=1", "A=1\\nB=2" }) {
            var inactive = Good(Input(cfg: "OverrideEnv=false\nEnv=" + value)).Configuration;
            Require(inactive.EnvironmentOverride.IsEmpty, "An inactive environment mapping was applied: " + value);
            Require(inactive.Values.ContainsKey("Env"), "An inactive environment value was dropped.");
        }
        // The envelope text and the JSON text keep the original source spelling when inactive.
        Require(Good(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=false\nEnv=@Variant(ignored)")).Configuration.Values["Env"] == "@Variant(ignored)",
            "An inactive variant envelope was not preserved verbatim.");
        Require(Good(Input(cfg: "OverrideEnv=false\nEnv=[\"A=1\"]")).Configuration.Values["Env"] == "A=1", "A JSON list did not decode to its element text.");
        // A reserved name that is not active is preserved rather than refused.
        var reserved = Good(Input(cfg: "OverrideEnv=false\nEnv=TEMP=elsewhere")).Configuration;
        Require(reserved.EnvironmentOverride.IsEmpty && reserved.Values["Env"] == "TEMP=elsewhere", "An inactive reserved name was not preserved.");
        // Env without OverrideEnv is likewise preserved but inactive.
        Require(Good(Input(cfg: "Env=A=1")).Configuration.EnvironmentOverride.IsEmpty, "A mapping without OverrideEnv became active.");
        // An active but blank mapping declares no override and is not an error.
        foreach (string blank in new[] { "OverrideEnv=true\nEnv=", "OverrideEnv=true\nEnv=   " }) {
            var empty = Good(Input(cfg: blank)).Configuration;
            Require(empty.EnvironmentOverride.IsEmpty, "A blank active mapping became an override.");
            Require(empty.Values["Env"].Length == 0 || string.IsNullOrWhiteSpace(empty.Values["Env"]), "A blank active mapping was rewritten.");
        }
        // No Env key at all leaves the mapping empty so the host base environment is untouched.
        Require(Good(Input(cfg: "name=x")).Configuration.EnvironmentOverride.IsEmpty, "An absent mapping was fabricated.");
        Require(Good(Input()).Configuration.EnvironmentOverride.IsEmpty, "An absent cfg fabricated a mapping.");
    }
    private static void CfgEnvironmentMalformed() {
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=A=1,,B=2"), "MMC_CFG_ENV");
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=A=1,"), "MMC_CFG_ENV");
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=,A=1"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv=NOEQUALS"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv==value"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv=A=1\\nB\\nC=3"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv=A=1\\n\\nB=2"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv=A=1\\nA=2"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv=a=1\\nA=2"), "MMC_CFG_ENV");
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=\"unterminated"), "MMC_CFG_ENV");
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=A=\\q"), "MMC_CFG_ESCAPE");
        Error(Input(cfg: "OverrideEnv=true\nEnv=A=1\\"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=true\nEnv=[\"A=1\",2]"), "MMC_CFG_ENV_ENCODING");
        Error(Input(cfg: "OverrideEnv=true\nEnv=[\"A=1\""), "MMC_CFG_ENV_ENCODING");
        Error(Input(cfg: "OverrideEnv=true\nEnv={\"a\":1}"), "MMC_CFG_ENV");
        Error(Input(cfg: "OverrideEnv=garbage\nEnv=A=1"), "MMC_CFG_BOOLEAN");
        // A malformed list or illegal escape is refused at parse time even while inactive: the source
        // declares a value the format contract cannot represent, so it cannot be silently kept as false.
        Error(Input(cfg: "OverrideEnv=false\nEnv=[bad json"), "MMC_CFG_ENV_ENCODING");
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=false\nEnv=A=\\q"), "MMC_CFG_ESCAPE");
        Error(Input(cfg: "ConfigVersion=1.3\nEnv=A=1\nEnv=B=2"), "MMC_CFG");
    }
    private static void CfgEnvironmentRefused() {
        // A launcher-managed name is refused, not silently ignored and not silently overwritten.
        foreach (string name in new[] { "TEMP", "TMP", "temp", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH", "ClassPath" })
            Error(Input(cfg: "OverrideEnv=true\nEnv=" + name + "=value"), "MMC_CFG_ENV_RESERVED");
        // A variant envelope has no verified decoder here; an active one is refused rather than guessed.
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=@Variant(ignored)"), "MMC_CFG_ENV_ENCODING");
        // A varied casing of the envelope marker is refused by the same rule. The envelope body is opaque,
        // so cfg-level escaping is not applied to it while inactive and only its envelope class is judged.
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=@Variant(\\0\\0\\0\\x09\\0\\0\\0\\x04)"), "MMC_CFG_ENV_ENCODING");
        // A doubled at sign is a literal '@', not an envelope, and stays decodable.
        Require(Good(Input(cfg: "ConfigVersion=1.3\nOverrideEnv=true\nEnv=LITERAL=@@notAvariant")).Configuration.EnvironmentOverride is [{ Name: "LITERAL", Value: "@notAvariant" }],
            "The escaped literal at sign was not decoded.");
    }
    // A requirement this launcher cannot honour is kept and reported. Neither of these groups may be
    // re-expressed as a capability the host happens to have: a console preference is not launcher
    // visibility, and a PermGen size is not a Metaspace size.
    private static void CfgDifferenceNotices() {
        // An active console requirement is retained and reported, with the substitution named as forbidden.
        var console = Good(Input(cfg: "OverrideConsole=true\nShowConsole=true\nAutoCloseConsole=false")).Configuration;
        var reported = MmcPackageReader.Parse(Input(cfg: "OverrideConsole=true\nShowConsole=true\nAutoCloseConsole=false"));
        Require(!reported.HasErrors, "An active console requirement was refused instead of reported.");
        var notice = reported.Diagnostics.Single(d => d.Code == MmcDisplayDifference.ConsoleCode);
        Require(notice.Severity == MmcDiagnosticSeverity.Warning, "The console difference was not a Warning.");
        Require(notice.Message.Contains("ShowConsole") && notice.Message.Contains("AutoCloseConsole"), "The console notice did not name the declared keys.");
        Require(notice.Message.Contains("this launcher's own window"), "The console notice did not rule out the launcher-visibility substitution.");
        Require(!notice.Message.Contains("ShowConsoleOnError"), "The console notice named a key the source did not declare.");
        Require(console.Values["ShowConsole"] == "true" && console.Values["AutoCloseConsole"] == "false", "A console value was rewritten or dropped.");
        // Declaring only some of the keys reports only those keys.
        var partial = MmcPackageReader.Parse(Input(cfg: "OverrideConsole=true\nShowConsoleOnError=true"));
        Require(partial.Diagnostics.Single(d => d.Code == MmcDisplayDifference.ConsoleCode).Message.Contains("ShowConsoleOnError"), "A declared console key was omitted.");
        // A blank value expresses no requirement and is not reported.
        Require(!MmcPackageReader.Parse(Input(cfg: "OverrideConsole=true\nShowConsole=  ")).Diagnostics.Any(d => d.Code == MmcDisplayDifference.ConsoleCode),
            "A blank console value was reported as a requirement.");
        // No console key at all leaves the notice absent even when the gate is on.
        Require(!MmcPackageReader.Parse(Input(cfg: "OverrideConsole=true")).Diagnostics.Any(d => d.Code == MmcDisplayDifference.ConsoleCode),
            "An absent console declaration produced a notice.");
        // A declared PermGen size is reported and its value is preserved exactly, never converted.
        var permGen = MmcPackageReader.Parse(Input(cfg: "OverrideMemory=true\nMinMemAlloc=512\nMaxMemAlloc=4096\nPermGen=256"));
        Require(!permGen.HasErrors, "A declared PermGen size was refused instead of reported.");
        var generation = permGen.Diagnostics.Single(d => d.Code == MmcDisplayDifference.PermanentGenerationCode);
        Require(generation.Severity == MmcDiagnosticSeverity.Warning, "The PermGen difference was not a Warning.");
        Require(generation.Message.Contains("never substitutes a Metaspace"), "The PermGen notice did not rule out the Metaspace substitution.");
        Require(generation.Message.Contains("not claim the requirement was migrated"), "The PermGen notice still permitted a migration claim.");
        Require(permGen.Package!.Configuration.Values["PermGen"] == "256", "The declared PermGen value was rewritten.");
        // The two groups report under distinct codes so a caller can act on either independently.
        Require(MmcDisplayDifference.ConsoleCode != MmcDisplayDifference.PermanentGenerationCode, "The two differences share a code.");
        // A malformed PermGen is still refused by the existing numeric rule, not silently reported.
        Error(Input(cfg: "OverrideMemory=true\nPermGen=-1"), "MMC_CFG");
        Error(Input(cfg: "OverrideMemory=true\nPermGen=abc"), "MMC_CFG");
    }
    // An inactive requirement keeps exactly the pre-existing behaviour: the value is retained and no notice
    // is raised, because a source that is switched off is not asking for anything.
    private static void CfgDifferenceInactive() {
        foreach (string cfg in new[] { "OverrideConsole=false\nShowConsole=true", "ShowConsole=true", "OverrideConsole=false\nAutoCloseConsole=true" }) {
            var inactive = MmcPackageReader.Parse(Input(cfg: cfg));
            Require(!inactive.HasErrors, "An inactive console declaration became an error: " + cfg);
            Require(!inactive.Diagnostics.Any(d => d.Code == MmcDisplayDifference.ConsoleCode && d.Severity == MmcDiagnosticSeverity.Error),
                "An inactive console declaration was refused: " + cfg);
            Require(inactive.Package!.Configuration.Values.ContainsKey("ShowConsole") || inactive.Package.Configuration.Values.ContainsKey("AutoCloseConsole"),
                "An inactive console value was dropped: " + cfg);
        }
        // An inactive PermGen keeps its value and raises no notice.
        var permGen = MmcPackageReader.Parse(Input(cfg: "OverrideMemory=false\nPermGen=256"));
        Require(!permGen.HasErrors && permGen.Package!.Configuration.Values["PermGen"] == "256", "An inactive PermGen value was not preserved.");
        Require(!permGen.Diagnostics.Any(d => d.Code == MmcDisplayDifference.PermanentGenerationCode), "An inactive PermGen size produced an active notice.");
        // PermGen without its gate is likewise preserved and unreported.
        var ungated = MmcPackageReader.Parse(Input(cfg: "PermGen=256"));
        Require(ungated.Package!.Configuration.Values["PermGen"] == "256" && !ungated.Diagnostics.Any(d => d.Code == MmcDisplayDifference.PermanentGenerationCode),
            "A PermGen size without OverrideMemory was reported as active.");
    }
    // The capability gate for the package wrapper (contract §8 target state): it is admitted exactly like the
    // other landed stages, and its still-refused cases are the ones the reader can decide from text alone.
    private static void CfgWrapperCommand() {
        // An active wrapper is admitted and its text is preserved verbatim for the host to use.
        var admitted = Good(Input(cfg: "OverrideCommands=true\nWrapperCommand=wrapper.exe --flag value")).Configuration;
        Require(admitted.Values["WrapperCommand"] == "wrapper.exe --flag value", "An admitted wrapper text was rewritten.");
        // The legacy alias activates the same gate, so it reaches the same verdict.
        Require(Good(Input(cfg: "OverrideLaunchCmd=true\nWrapperCommand=wrapper.exe")).Configuration.Values["WrapperCommand"] == "wrapper.exe",
            "The legacy alias did not admit the package wrapper.");
        // An inactive source keeps its value and is never admitted, exactly as the other stages behave.
        var inactive = Good(Input(cfg: "OverrideCommands=false\nWrapperCommand=wrapper.exe")).Configuration;
        Require(inactive.Values["WrapperCommand"] == "wrapper.exe", "An inactive wrapper value was dropped.");
        // The wrapper still obeys the command text rules: NUL, length and credential binding are refused.
        Error(Input(cfg: "ConfigVersion=1.3\nOverrideCommands=true\nWrapperCommand=abc\\x0000def"), "MMC_CFG_COMMAND");
        Error(Input(cfg: "OverrideCommands=true\nWrapperCommand=" + new string('a', 16385)), "MMC_CFG_COMMAND");
        foreach (string credential in new[] { "auth_access_token", "auth_session", "access_token", "client_token" })
            Error(Input(cfg: "OverrideCommands=true\nWrapperCommand=wrapper.exe ${" + credential + "}"), "MMC_CFG_COMMAND_CREDENTIAL");
        // An empty or blank wrapper declares no wrapper and is not an error.
        Require(Good(Input(cfg: "OverrideCommands=true\nWrapperCommand=")).Configuration.Values["WrapperCommand"].Length == 0, "A blank wrapper was fabricated.");
        // A malformed Boolean gate is still refused by the existing rule.
        Error(Input(cfg: "OverrideCommands=garbage\nWrapperCommand=wrapper.exe"), "MMC_CFG_BOOLEAN");
    }
    private static void CfgInvalid() {
        Error(Input(cfg: "OverrideMemory=garbage"), "MMC_CFG_BOOLEAN");
        Error(Input(cfg: "name=a\nname=b"), "MMC_CFG");
        Error(Input(cfg: "ConfigVersion=1.3\nname=\"unterminated"), "MMC_CFG");
        Error(Input(cfg: "ConfigVersion=1.3\nname=a,b"), "MMC_CFG_LIST");
        Error(Input(cfg: "ConfigVersion=1.3\nJvmArgs=@Variant(data)"), "MMC_CFG_VARIANT");
        Error(Input(cfg: "ConfigVersion=9"), "MMC_UNSUPPORTED_FORMAT");
        Error(Input(cfg: "FutureLaunchHook=run"), "MMC_UNSUPPORTED_REQUIRED");
        Error(Input(cfg: "MaxMemAlloc=-10"), "MMC_CFG");
    }
    private static void Roots() {
        Require(Good(Input(root: "nested/", extra: [new("nested/.minecraft/config/file")])).GameRootLeaf == ".minecraft", "Implicit game directory lost.");
        Require(Good(Input(root: "nested/", extra: [new("nested/.minecraft/config/file"), new("nested/minecraft/config/file")])).GameRootLeaf == "minecraft", "Dual-tree precedence changed.");
        Require(Good(Input(root: "nested/")).RelativeRoot == "nested/", "Relative instance root changed.");
    }
    private static void AmbiguousRoots() {
        Error(Input(extra: [new("second/mmc-pack.json")]), "MMC_ROOT");
        Error(new([], new Dictionary<string, string>()), "MMC_ROOT");
        Error(new([new("mmc-pack.json")], new Dictionary<string, string>()), "MMC_METADATA_MISSING");
        Error(new([new("mmc-pack.json")], new Dictionary<string, string> { ["orphan.json"] = "{}" }), "MMC_METADATA_SOURCE");
    }
    private static void UnsafePaths() {
        foreach (string path in new[] { "../evil", "a/../../evil", "/root", "C:/file", "a./file", "AUX.txt", "x/COM1/file", "x//file", "a:stream", "bad?name", "COM¹.jar", "LPT².txt", "CONIN$.bin", "CONOUT$" })
            Error(Input(extra: [new(path)]), "MMC_PATH");
        Error(Input(extra: [new("mods/A.jar"), new("mods/a.jar")]), "MMC_PATH_COLLISION");
    }
    private static void DirectoryAliases() {
        MmcPackageInput input = Input(extra: [new("config/Foo/", 0, true), new("config/foo/", 0, true), new("config/Foo/a"), new("config/foo/b")]);
        MmcParseResult result = MmcPackageReader.Parse(input);
        Require(!result.HasErrors && result.Diagnostics.Any(d => d.Code == "MMC_DIRECTORY_ALIAS") && result.Package!.ArchiveEntries.Length == input.Entries.Count, "Pure directory aliases were lost or rejected.");
        Good(Input(extra: [new("Config/Thing/a"), new("config/thing/b")]));
        Error(Input(extra: [new("config/Foo/a"), new("config/foo/A")]), "MMC_PATH_COLLISION");
    }
    private static void FileRoles() {
        Error(Input(extra: [new("mods"), new("mods/file.jar")]), "MMC_PATH_COLLISION");
        Error(Input(extra: [new("MODS/file.jar"), new("mods")]), "MMC_PATH_COLLISION");
        Error(Input(extra: [new("data", 0, true), new("DATA")]), "MMC_PATH_COLLISION");
        Error(Input(extra: [new("minecraft")]), "MMC_DIRECTORY_ROLE");
        Error(Input(extra: [new("libraries")]), "MMC_DIRECTORY_ROLE");
    }
    private static void ReservedOwnership() {
        Error(Input(extra: [new("PCL/InstanceDefinition.json")]), "MMC_RESERVED_PATH");
        Error(Input(extra: [new("PCL/MmcTransactions/fake/log")]), "MMC_RESERVED_PATH");
    }
    private static void DuplicateJson() {
        Error(Input("{\"formatVersion\":1,\"formatVersion\":1,\"components\":[]}"), "MMC_DUPLICATE_JSON_KEY");
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"libraries\":[{\"name\":\"a:b:1\",\"downloads\":{\"a\":1,\"\\u0061\":2}}]}" }), "MMC_DUPLICATE_JSON_KEY");
    }
    private static void ManifestShapes() {
        foreach (string manifest in new[] { "{\"formatVersion\":\"1\",\"components\":[]}", "{\"formatVersion\":2,\"components\":[]}" }) Error(Input(manifest), "MMC_UNSUPPORTED_FORMAT");
        foreach (string manifest in new[] { "[]", "{\"formatVersion\":1,\"components\":{}}", "{\"formatVersion\":1,\"components\":[null]}" }) Error(Input(manifest), "MMC_STRUCTURE");
        Error(Input(Pack(new { uid = "a", version = "1" }, new { uid = "a", version = "2" })), "MMC_DUPLICATE_UID");
        Error(Input(Pack(new { uid = "../bad", version = "1" })), "MMC_STRUCTURE");
    }
    private static void LocalComponents() {
        var input = Input(Pack(new { uid = "a", version = "request", cachedVersion = "hint", disabled = true, important = true },
            new { uid = "b", disabled = true, dependencyOnly = true }), patches: new() { ["a.json"] = Patch("a", "local"), ["b.json"] = Patch("b", "effective") });
        var result = Good(input);
        Require(result.Components.Select(c => c.Uid).SequenceEqual(["a", "b"]), "Author order changed.");
        Require(result.Components[0] is { RequestedVersion: "request", CachedVersion: "hint", Disabled: true, EffectiveEnabled: true, EffectiveVersion: "local" }, "Declaration or local override lost.");
        Require(result.Components[1].EffectiveEnabled && result.MissingMetadata.IsEmpty, "dependencyOnly incorrectly disabled or local overwritten by remote.");
    }
    private static void PatchIdentity() {
        Good(Input(patches: new() { ["net.minecraft.json"] = "{\"fileId\":\"net.minecraft\",\"version\":\"1.7.10\"}" }));
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"fileId\":\"other\"}" }), "MMC_STRUCTURE");
        Error(Input(patches: new() { ["net.minecraft.json"] = Patch("different") }), "MMC_PATCH_UID");
    }
    private static void Dependencies() {
        var result = Good(Input(Pack(new { uid = "consumer", version = "1" }, new { uid = "a", version = "1" }), patches: new() {
            ["consumer.json"] = Patch("consumer", requires: [new { uid = "missing", equals = "2", suggests = "3" }])
        }));
        Require(result.Components[0].Uid == "consumer" && result.MissingMetadata.Any(r => r.Uid == "missing" && r.Version == "2"), "Exact dependency or original component order lost.");
        var emptyExactExisting = Good(Input(Pack(new { uid = "consumer", version = "1" }, new { uid = "dep", version = "1" }), patches: new() {
            ["consumer.json"] = Patch("consumer", requires: [new { uid = "dep", equals = "", suggests = "1" }]), ["dep.json"] = Patch("dep")
        }));
        Require(emptyExactExisting.Components[0].LocalPatch!.Requires[0].EqualsVersion is null && emptyExactExisting.Components[0].LocalPatch!.Fields["requires"][0].GetProperty("equals").GetString() == "", "Empty exact constraint or its source representation changed.");
        var emptyExactMissing = Good(Input(Pack(new { uid = "consumer", version = "1" }), patches: new() {
            ["consumer.json"] = Patch("consumer", requires: [new { uid = "dep", equals = "", suggests = "1" }])
        }));
        Require(emptyExactMissing.MissingMetadata is [{ Uid: "dep", Version: "1" }], "Empty exact version suppressed a valid suggestion.");
        Require(Good(Input(Pack(new { uid = "unknown" }))).MissingMetadata is [{ Kind: MmcMetadataRequestKind.Recommendation, Version: "" }], "Unspecified version must request a fixed recommendation index.");
    }
    private static void InvalidGraphs() {
        string pack = Pack(new { uid = "a", version = "1" }, new { uid = "b", version = "1" });
        Error(Input(pack, patches: new() { ["a.json"] = Patch("a", requires: [new { uid = "b", equals = "2" }]), ["b.json"] = Patch("b") }), "MMC_EXACT_VERSION");
        Error(Input(Pack(new { uid = "a", version = "1" }, new { uid = "b", version = "1", disabled = true }), patches: new() {
            ["a.json"] = Patch("a", requires: [new { uid = "b" }]), ["b.json"] = Patch("b") }), "MMC_DISABLED_DEPENDENCY");
        Error(Input(pack, patches: new() { ["a.json"] = Patch("a", requires: [new { uid = "b" }]), ["b.json"] = Patch("b", requires: [new { uid = "a" }]) }), "MMC_DEPENDENCY_CYCLE");
        Error(Input(pack, patches: new() { ["a.json"] = Patch("a", conflicts: [new { uid = "b" }]), ["b.json"] = Patch("b") }), "MMC_COMPONENT_CONFLICT");
    }
    private static void Capabilities() {
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"futureLaunch\":true}" }), "MMC_UNSUPPORTED_REQUIRED");
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"+traits\":[\"legacyFML\"]}" }), "MMC_UNSUPPORTED_REQUIRED");
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"libraries\":[{\"name\":\"a:b:1\",\"rules\":[{\"action\":\"allow\",\"futureCondition\":true}]}]}" }), "MMC_UNSUPPORTED_REQUIRED");
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"libraries\":[{\"name\":\"a:b:1\",\"downloads\":{\"artifact\":{\"size\":-1}}}]}" }), "MMC_STRUCTURE");
        Good(Input(Pack(new { uid = "disabled", version = "1", disabled = true }), patches: new() { ["disabled.json"] = "{\"uid\":\"disabled\",\"futureLaunch\":true}" }));
        Good(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"name\":\"fixture\",\"description\":\"descriptive\",\"volatile\":true}" }));
    }
    private static void LibrariesAndTokens() {
        string patch = "{\"uid\":\"net.minecraft\",\"mainClass\":\"fixture.Main\",\"+jvmArgs\":[\"-Dpath=中文 space\",\"\",\"-Dsame=1\",\"-Dsame=1\"],\"libraries\":[{\"name\":\"fixture:lib:1\",\"MMC-hint\":\"local\"}]}";
        var result = Good(Input(patches: new() { ["net.minecraft.json"] = patch }, extra: [new("libraries/lib-1.jar", 2), new("minecraft/libraries/lib-1.jar", 3)]));
        JsonElement args = result.Components[0].LocalPatch!.Fields["+jvmArgs"];
        Require(args.GetArrayLength() == 4 && args[0].GetString() == "-Dpath=中文 space" && args[1].GetString() == "", "JVM array values were split/deduplicated.");
    }
    private static void JavaCandidates() {
        var result = Good(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"compatibleJavaMajors\":[17,21,25,26],\"compatibleJavaName\":\"fixture-runtime\"}" }));
        Require(result.Components[0].LocalPatch!.Fields["compatibleJavaMajors"].EnumerateArray().Select(e => e.GetInt32()).SequenceEqual([17, 21, 25, 26]), "Discrete candidate source changed.");
        foreach (string value in new[] { "[0]", "[\"17\"]", "[17.5]", "{}" })
            Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"compatibleJavaMajors\":" + value + "}" }), "MMC_STRUCTURE");
    }
    private static void MetadataBudgets() {
        Error(Input(cfg: "name=中文"), "MMC_METADATA_BUDGET", new() { MaxMetadataBytes = 10 });
        Error(Input(cfg: "name=fixture"), "MMC_METADATA_BUDGET", new() { MaxTotalMetadataBytes = Manifest.Length + 5 });
        Error(Input(cfg: "name=\uD800"), "MMC_METADATA_ENCODING");
        Error(Input(extra: [new("file")]), "MMC_ARCHIVE_BUDGET", new() { MaxArchiveEntries = 1 });
    }
    private static void StructureBudgets() {
        Error(Input(extra: [new("huge", 500)]), "MMC_ARCHIVE_BUDGET", new() { MaxEntryBytes = 300 });
        Error(Input(extra: [new("a", 100), new("b", 100)]), "MMC_ARCHIVE_BUDGET", new() { MaxArchiveBytes = Manifest.Length + 150 });
        Error(Input(Pack(new { uid = "a", version = "1" }, new { uid = "b", version = "1" })), "MMC_COMPONENT_BUDGET", new() { MaxComponents = 1 });
        Error(Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"description\":{\"nested\":{\"tooDeep\":true}}}" }), "MMC_JSON", new() { MaxDepth = 2 });
        Error(Input(patches: new() { ["net.minecraft.json"] = Patch("net.minecraft", requires: [new { uid = "a", equals = "1" }, new { uid = "b", equals = "1" }]) }), "MMC_DEPENDENCY_BUDGET", new() { MaxDependencyEdges = 1 });
    }
    private static void PathBudgets() {
        Error(Input(), "MMC_PATH_BUDGET", new() { MaxTotalPathCharacters = 20 }); // metadata path is counted too
        Error(Input(extra: [new("a/b/c/file")]), "MMC_PATH_BUDGET", new() { MaxLogicalPathCharacters = 25 });
        // Repeated prefix construction is charged before allocating substrings,
        // even when its logical directory node was already present.
        Error(Input(extra: [new("repeat/deep/a"), new("repeat/deep/b")]), "MMC_PATH_BUDGET", new() { MaxLogicalPathCharacters = 60 });
    }
    private static void Immutability() {
        MmcPackageInput input = Input(patches: new() { ["net.minecraft.json"] = "{\"uid\":\"net.minecraft\",\"+jvmArgs\":[\"first\"]}" });
        var result = Good(input);
        ((Dictionary<string, string>)input.Metadata)["patches/net.minecraft.json"] = "corrupt";
        ((MmcArchiveEntry[])input.Entries)[0] = new("evil");
        Require(result.Components[0].LocalPatch!.Fields["+jvmArgs"][0].GetString() == "first" && result.ArchiveEntries[0].Path == "mmc-pack.json", "Result retains mutable source alias.");
        var replay = Good(Input());
        Require(replay.ManifestSha256 == Good(Input()).ManifestSha256, "Same source produced different digest.");
        Require(typeof(MmcPackageReader).Assembly.GetReferencedAssemblies().All(a => a.Name is not "PCLCS" and not "Main" && !a.Name!.StartsWith("Presentation", StringComparison.Ordinal)), "Core acquired a host reference.");
    }
}
