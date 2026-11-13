// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>
/// Difference notices for cfg fields this launcher has no equivalent capability for.
///
/// Such a field is a real package requirement, so it is never dropped and never reinterpreted as
/// something the host can do: the original value stays in <see cref="MmcConfiguration.Values"/> and the
/// player is told, under one stable code, that the requirement has no effect here and why.
///
/// Each notice is produced as a value rather than written straight into a diagnostics list, so the
/// parse-time notice and the user-visible host notice are the same statement under the same code and
/// cannot drift apart.
/// </summary>
public static class MmcDisplayDifference {
    /// <summary>The code for independent console preferences, which this launcher does not model.</summary>
    public const string ConsoleCode = "MMC_CFG_CONSOLE_DIFFERENCE";

    /// <summary>
    /// The code for a declared permanent-generation size, reported separately because its cause and
    /// remedy differ from the console notice.
    /// </summary>
    public const string PermanentGenerationCode = "MMC_CFG_PERMGEN_DIFFERENCE";

    /// <summary>
    /// The independent console preference keys a package may declare. None of them is modelled by this
    /// launcher: there is no package-console window to show, close or keep open.
    /// </summary>
    public static readonly ImmutableArray<string> ConsoleFields = ["ShowConsole", "AutoCloseConsole", "ShowConsoleOnError"];

    /// <summary>
    /// The console keys this source actually declares a value for. A key that is absent, or present but
    /// blank, expresses no requirement and is therefore not reported.
    /// </summary>
    public static ImmutableArray<string> DeclaredConsoleFields(IReadOnlyDictionary<string, string> values) =>
        ConsoleFields.Where(field => values.TryGetValue(field, out string? value) && !string.IsNullOrWhiteSpace(value)).ToImmutableArray();

    /// <summary>
    /// The user-facing explanation for the console group. It states that the requirement is retained but
    /// inert, and it names the substitution that must never be made: this launcher's own window is a
    /// different thing from the package's console window, so closing the launcher cannot honour it.
    /// </summary>
    public static string DescribeConsole(IEnumerable<string> declared) =>
        "Console preferences have no effect in this launcher and were retained unchanged: " +
        string.Join(", ", declared) + ". A package console window is not this launcher's own window, so no " +
        "launcher-visibility setting is derived from it.";

    /// <summary>
    /// The user-facing explanation for a declared permanent-generation size. It names the exact
    /// substitution that must never happen, so a later reader cannot mistake this notice for a migration.
    /// </summary>
    public const string PermanentGenerationMessage =
        "The declared PermGen size was retained unchanged and applies to no memory option. This launcher " +
        "never substitutes a Metaspace or other permanent-generation option for a PermGen value, and it does " +
        "not claim the requirement was migrated.";

    /// <summary>
    /// The console difference, or <see langword="null"/> when the source expresses no console requirement.
    /// Only an active <c>OverrideConsole</c> is reported, so an inactive declaration keeps its existing
    /// behaviour of being retained without a notice.
    /// </summary>
    public static MmcDiagnostic? ConsoleDifference(IReadOnlyDictionary<string, string> values, bool overrideConsole, string source) {
        if (!overrideConsole) return null;
        ImmutableArray<string> declared = DeclaredConsoleFields(values);
        return declared.IsEmpty ? null : new(ConsoleCode, MmcDiagnosticSeverity.Warning, source, DescribeConsole(declared));
    }

    /// <summary>
    /// The permanent-generation difference, or <see langword="null"/> when the source declares no
    /// permanent-generation size.
    ///
    /// This is a difference notice rather than a refusal on purpose. Whether a permanent generation exists
    /// is a property of the Java that will run the game, which this layer cannot know: the same declaration
    /// is meaningless on a modern Java and meaningful on a Java old enough to have the generation. Refusing
    /// every declared <c>PermGen</c> here would reject a package that legitimately targets such a Java,
    /// while reporting it keeps the requirement visible without pretending to honour it.
    /// </summary>
    public static MmcDiagnostic? PermanentGenerationDifference(IReadOnlyDictionary<string, string> values, bool overrideMemory, string source) {
        if (!overrideMemory) return null;
        if (!values.TryGetValue("PermGen", out string? declared) || string.IsNullOrWhiteSpace(declared)) return null;
        return new(PermanentGenerationCode, MmcDiagnosticSeverity.Warning, source + ":PermGen", PermanentGenerationMessage);
    }
}
