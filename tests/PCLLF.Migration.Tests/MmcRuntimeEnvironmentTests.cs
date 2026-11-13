// Independently authored fixtures. Host test suite retains its existing license.
using PCLCS;
using PCLLF.MMCC.Core;

internal static class MmcRuntimeEnvironmentTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC runtime environment package mapping is overlaid in source order", OverlayOrder);
        yield return ("MMC runtime environment without a mapping leaves the inherited environment unchanged", NoMapping);
        yield return ("MMC runtime environment launcher temporary directory outranks every package entry", TemporaryBoundary);
        yield return ("MMC runtime environment refuses reserved and malformed entries at the process boundary", Refusals);
        yield return ("MMC runtime environment values stay literal across the process boundary", Literality);
    }

    // Deliberately long and obviously launcher-owned, so accidental passthrough is visible.
    private const string Temporary = @"C:\lf-owned\session-1\tmp";
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception {
        try { action(); } catch (Exception error) when (error is T) { return; }
        catch (Exception error) { throw new Exception("Expected " + typeof(T).Name + ", got " + error.GetType().Name + ": " + error.Message); }
        throw new Exception("Expected " + typeof(T).Name + ": " + message);
    }
    private static MmcEnvironmentVariable Entry(string name, string value) => new(name, value);

    /// <summary>Starts from a realistic inherited block so base-environment preservation is observable.</summary>
    private static Dictionary<string, string?> Base() => new(StringComparer.Ordinal) {
        ["PATH"] = @"C:\Windows\system32",
        ["APPDATA"] = @"C:\Users\player\AppData\Roaming",
        ["USERNAME"] = "player",
        ["TEMP"] = Temporary,
        ["TMP"] = Temporary
    };

    private static void OverlayOrder() {
        var environment = Base();
        environment["SHARED"] = "base-value";
        MmcRuntimeEnvironmentOverlay.Apply(environment, [Entry("FIRST", "1"), Entry("SECOND", "2"), Entry("THIRD", "3")], Temporary);
        Require(environment["FIRST"] == "1" && environment["SECOND"] == "2" && environment["THIRD"] == "3", "A package entry was lost.");
        // Every entry survives; order is what makes a repeated name deterministic, and Core already
        // refuses duplicates, so the overlay only has to be a stable ordered pass.
        var ordered = environment.Keys.Where(key => key is "FIRST" or "SECOND" or "THIRD").ToArray();
        Require(ordered is ["FIRST", "SECOND", "THIRD"], "Package entries were not inserted in source order: " + string.Join(",", ordered));
        // A package entry may replace a host-inherited non-reserved value.
        MmcRuntimeEnvironmentOverlay.Apply(environment, [Entry("SHARED", "package-value")], Temporary);
        Require(environment["SHARED"] == "package-value", "A package entry did not override an inherited value.");
        // The base entries the package did not name are untouched.
        Require(environment["PATH"] == @"C:\Windows\system32" && environment["USERNAME"] == "player", "An unnamed inherited entry changed.");
    }

    private static void NoMapping() {
        // An instance with no active OverrideEnv/Env must behave exactly as before this feature existed.
        var expected = Base();
        var actual = Base();
        MmcRuntimeEnvironmentOverlay.Apply(actual, [], Temporary);
        Require(actual.Count == expected.Count, "An empty mapping changed the entry count.");
        foreach (var pair in expected) Require(actual[pair.Key] == pair.Value, "An empty mapping changed " + pair.Key);
        // The launcher's own channels are still removed and its temporary directory still asserted.
        var polluted = Base();
        polluted["CLASSPATH"] = @"C:\stale.jar"; polluted["JAVA_TOOL_OPTIONS"] = "-javaagent:evil";
        polluted["JDK_JAVA_OPTIONS"] = "-Xmx1g"; polluted["_JAVA_OPTIONS"] = "-Xmx1g";
        MmcRuntimeEnvironmentOverlay.Apply(polluted, [], Temporary);
        foreach (string key in new[] { "CLASSPATH", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS" })
            Require(!polluted.ContainsKey(key), "An execution-input channel survived: " + key);
        Require(polluted["TEMP"] == Temporary && polluted["TMP"] == Temporary, "The launcher temporary directory was not asserted.");
    }

    private static void TemporaryBoundary() {
        // A package mapping can never move the launcher-managed temporary directory, in any casing.
        foreach (string name in new[] { "TEMP", "TMP", "temp", "Tmp", "tEmP" }) {
            var environment = Base();
            Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(environment, [Entry(name, @"C:\attacker\tmp")], Temporary),
                "A launcher-managed temporary variable was accepted: " + name);
            Require(environment["TEMP"] == Temporary && environment["TMP"] == Temporary, "The temporary directory moved for " + name);
        }
        // The refusal happens before the offending entry is written, so a later entry cannot run.
        var partial = Base();
        Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(partial, [Entry("EARLY", "kept"), Entry("TEMP", "x"), Entry("LATE", "never")], Temporary), "Reserved name accepted.");
        Require(!partial.ContainsKey("LATE"), "An entry after the refused one was applied.");
        // Execution-input channels are refused as package entries rather than silently removed.
        foreach (string name in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" })
            Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(Base(), [Entry(name, "-javaagent:evil")], Temporary), "Execution-input channel accepted: " + name);
        // A valid mapping leaves the launcher temporary directory exactly as the launcher set it.
        var good = Base();
        MmcRuntimeEnvironmentOverlay.Apply(good, [Entry("HELLO", "world")], Temporary);
        Require(good["TEMP"] == Temporary && good["TMP"] == Temporary, "A valid mapping displaced the temporary directory.");
    }

    private static void Refusals() {
        Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(Base(), default, Temporary), "A default mapping was accepted.");
        Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(Base(), [Entry("BAD=NAME", "v")], Temporary), "A name containing '=' was accepted.");
        Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(Base(), [Entry("", "v")], Temporary), "An empty name was accepted.");
        Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(Base(), [Entry("NULL", "a\0b")], Temporary), "A NUL value was accepted.");
        Throws<MmcRuntimeLaunchException>(() => MmcRuntimeEnvironmentOverlay.Apply(Base(), [Entry("CONTROL\u0001", "v")], Temporary), "A control character in a name was accepted.");
    }

    private static void Literality() {
        // Environment values are not launch arguments: macro syntax and quotes pass through unchanged.
        var environment = Base();
        MmcRuntimeEnvironmentOverlay.Apply(environment, [
            Entry("LITERAL", "${INST_DIR}/mods"),
            Entry("QUOTED", "\"a b\""),
            Entry("TRAILING", "tail\\"),
            Entry("UNICODE", "中文 path 😀"),
            Entry("EMPTY", ""),
            Entry("EQUALS", "a=b=c"),
            Entry("SPACED", "  padded  ")
        ], Temporary);
        Require(environment["LITERAL"] == "${INST_DIR}/mods", "An environment value was macro-expanded.");
        Require(environment["QUOTED"] == "\"a b\"", "Quotes in an environment value were stripped.");
        Require(environment["TRAILING"] == "tail\\", "A trailing backslash was rewritten.");
        Require(environment["UNICODE"] == "中文 path 😀", "Unicode in an environment value changed.");
        Require(environment["EMPTY"] == "", "An explicit empty environment value was dropped.");
        Require(environment["EQUALS"] == "a=b=c", "Only the first '=' may separate the name from the value.");
        Require(environment["SPACED"] == "  padded  ", "An environment value was trimmed.");
    }
}
