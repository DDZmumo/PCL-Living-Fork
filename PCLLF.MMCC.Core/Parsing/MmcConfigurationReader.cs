// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PCLLF.MMCC.Core;

internal static class MmcConfigurationReader {
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal) {
        "ConfigVersion", "InstanceType", "name", "iconKey", "notes", "OverrideJavaLocation", "JavaPath",
        "OverrideJavaArgs", "JvmArgs", "OverrideMemory", "MinMemAlloc", "MaxMemAlloc", "PermGen",
        "OverrideWindow", "LaunchMaximized", "MinecraftWinWidth", "MinecraftWinHeight", "JoinServerOnLaunch",
        "JoinServerOnLaunchAddress", "OverrideCommands", "OverrideLaunchCmd", "PreLoadCommand", "PreLaunchCommand",
        "WrapperCommand", "PostExitCommand", "OverrideEnv", "Env", "OverrideConsole", "ShowConsole",
        "AutoCloseConsole", "ShowConsoleOnError", "JavaArchitecture", "JavaRealArchitecture", "JavaVersion",
        "JavaVendor", "JavaSignature", "lastLaunchTime", "totalTimePlayed", "ExportAuthor", "ExportName",
        "ExportOptionalFiles", "ExportSummary", "ExportVersion", "ManagedPack", "ManagedPackID", "ManagedPackName",
        "ManagedPackType", "ManagedPackVersionID", "ManagedPackVersionName", "OverrideGameTime", "OverrideNativeWorkarounds"
    };
    private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal) {
        "OverrideJavaLocation", "OverrideJavaArgs", "OverrideMemory", "OverrideWindow", "LaunchMaximized",
        "JoinServerOnLaunch", "OverrideCommands", "OverrideLaunchCmd", "OverrideEnv", "OverrideConsole",
        "ShowConsole", "AutoCloseConsole", "ShowConsoleOnError", "ManagedPack", "OverrideGameTime", "OverrideNativeWorkarounds"
    };

    internal static MmcConfiguration Parse(string? text, string source, List<MmcDiagnostic> diagnostics) {
        if (text is null) return new(MmcConfigurationFormat.Absent, ImmutableDictionary<string, string>.Empty);
        string[] lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Select the decoding contract before interpreting escaping; old MMC files
        // have different quote/comment rules from newer QSettings scalar files.
        bool qt = lines.Any(line => line.Contains('=') && line[..line.IndexOf('=')].Trim() == "ConfigVersion");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        HashSet<string> variants = new(StringComparer.Ordinal);
        // The environment key is the only cfg value with list semantics, so it is decoded by its own
        // element-aware decoder below instead of the scalar decoder every other key uses.
        ImmutableArray<string> environmentElements = [];
        bool environmentEnvelope = false;
        int environmentOccurrences = 0;
        string section = "";
        for (int i = 0; i < lines.Length; i++) {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']')) {
                // Legacy writers also emitted the conventional General marker.
                // Other legacy sections remain unsupported rather than flattening keys.
                section = line[1..^1];
                if (section == "General") section = "";
                else if (!qt) Bad(source, "Non-General legacy sections are unsupported.");
                continue;
            }
            if (qt) {
                while (TrailingSlashes(line) % 2 == 1) {
                    if (++i == lines.Length) Bad(source, "Unterminated continuation.");
                    line = line[..^1] + lines[i].TrimStart();
                }
            }
            int separator = line.IndexOf('=');
            if (separator <= 0) Bad(source, $"Expected key=value at line {i + 1}.");
            string key = line[..separator].Trim();
            if (key.Any(char.IsControl)) Bad(source, "Invalid key.");
            if (section.Length != 0) key = section + "/" + key;
            string encoded = line[(separator + 1)..].Trim();
            if (qt && encoded.StartsWith('@') && !encoded.StartsWith("@@", StringComparison.Ordinal)) variants.Add(key);
            string value;
            if (key == "Env") {
                // List forms are decoded eagerly with their own escaping domain, so an illegal escape or a
                // malformed list is refused deterministically. A Qt variant envelope has no verified decoder
                // here: its body is opaque, so it keeps its original spelling and is refused only if the
                // source activates it, never merely for being inactive.
                environmentOccurrences++;
                environmentEnvelope = qt && encoded.StartsWith('@') && !encoded.StartsWith("@@", StringComparison.Ordinal);
                if (environmentEnvelope) value = encoded;
                else {
                    environmentElements = encoded.TrimStart().StartsWith('[') ? MmcEnvironmentReader.JsonElements(encoded, source)
                        : qt ? MmcEnvironmentReader.QtElements(encoded, source) : MmcEnvironmentReader.LegacyElements(encoded, source);
                    value = string.Join("\n", environmentElements);
                }
            } else value = qt ? DecodeQt(encoded, source) : DecodeLegacy(encoded, source);
            if (!values.TryAdd(key, value)) Bad(source, $"Duplicate configuration key: {key}.");
        }
        if (values.TryGetValue("ConfigVersion", out string? version) && version is not ("1.1" or "1.2" or "1.3"))
            throw new MmcInputException("MMC_UNSUPPORTED_FORMAT", source, "Unsupported ConfigVersion.");
        foreach ((string key, string value) in values) {
            if (!KnownKeys.Contains(key)) {
                MmcPatchReader.Unsupported("cfg:" + key, source, true, diagnostics);
                continue;
            }
            if (BooleanKeys.Contains(key)) Boolean(value, key, source);
            if (key is "MinMemAlloc" or "MaxMemAlloc" or "PermGen" or "MinecraftWinWidth" or "MinecraftWinHeight")
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0) Bad(source, $"Invalid nonnegative integer: {key}.");
            if (variants.Contains(key) && key != "Env")
                throw new MmcInputException("MMC_CFG_VARIANT", source, $"Qt variant for {key} requires a dedicated decoder.");
        }
        bool commandOverride = ReadBool(values, "OverrideCommands", source, "OverrideLaunchCmd");
        if (values.ContainsKey("OverrideCommands") && values.ContainsKey("OverrideLaunchCmd") &&
            values["OverrideCommands"] != values["OverrideLaunchCmd"])
            diagnostics.Add(new("MMC_CFG_ALIAS", MmcDiagnosticSeverity.Warning, source, "OverrideCommands takes precedence over OverrideLaunchCmd."));
        if (commandOverride) {
            // A stage command whose phase is actually implemented is admitted once its text is a decodable
            // command within the instance budget and binds no credential variable. WrapperCommand is admitted on
            // the same terms: it is the package wrapper entry, and the host composes it with (never instead of)
            // its own JavaWrapper/DirectMain mode.
            foreach (string command in new[] { "PreLoadCommand", "PreLaunchCommand", "WrapperCommand", "PostExitCommand" })
                if (values.TryGetValue(command, out string? active) && !string.IsNullOrWhiteSpace(active))
                    MmcCommandReader.Validate(active, command, source);
        }
        // An active mapping is decoded into its ordered structure. A requirement this host cannot honour
        // (unverified encoding, launcher-managed name, ambiguous duplicate) is refused, never dropped;
        // an inactive source keeps its original value untouched.
        ImmutableArray<MmcEnvironmentVariable> environmentOverride = [];
        if (ReadBool(values, "OverrideEnv", source) && !string.IsNullOrWhiteSpace(values.GetValueOrDefault("Env"))) {
            if (environmentOccurrences != 1) Bad(source, "An active environment mapping must be declared exactly once.");
            if (environmentEnvelope)
                throw MmcEnvironmentReader.EncodingError(source, "The Qt variant envelope has no verified decoder.");
            environmentOverride = MmcEnvironmentReader.Parse(environmentElements, source);
        }
        foreach (string key in new[] { "OverrideGameTime", "OverrideNativeWorkarounds" })
            if (ReadBool(values, key, source)) MmcPatchReader.Unsupported("cfg:" + key, source, true, diagnostics);
        // Two capability groups have no equivalent here. The requirement is a real one, so the value is kept
        // exactly as read and the difference is reported; nothing is silently dropped and nothing is
        // re-expressed as a capability this launcher happens to have.
        var displayDifference = MmcDisplayDifference.ConsoleDifference(values, ReadBool(values, "OverrideConsole", source), source);
        if (displayDifference is not null) diagnostics.Add(displayDifference);
        var permanentGeneration = MmcDisplayDifference.PermanentGenerationDifference(values, ReadBool(values, "OverrideMemory", source), source);
        if (permanentGeneration is not null) diagnostics.Add(permanentGeneration);
        return new(qt ? MmcConfigurationFormat.QtScalar : MmcConfigurationFormat.Legacy, values.ToImmutableDictionary(StringComparer.Ordinal)) { EnvironmentOverride = environmentOverride };
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, string source, string? alias = null) =>
        values.TryGetValue(key, out string? value) ? Boolean(value, key, source) :
        alias is not null && values.TryGetValue(alias, out value) && Boolean(value, alias, source);

    private static bool Boolean(string value, string key, string source) => value.ToLowerInvariant() switch {
        "true" or "1" => true, "false" or "0" => false,
        _ => throw new MmcInputException("MMC_CFG_BOOLEAN", source, $"Invalid Boolean: {key}.")
    };

    private static int TrailingSlashes(string line) {
        int count = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) count++;
        return count;
    }

    private static string DecodeLegacy(string value, string source) {
        StringBuilder decoded = new();
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '#') break;
            if (c == '\\') {
                if (++i == value.Length) Bad(source, "Dangling legacy escape.");
                c = value[i] switch { 'n' => '\n', 't' => '\t', _ => value[i] };
            }
            decoded.Append(c);
        }
        string result = decoded.ToString().Trim();
        if (result.Length >= 2 && result[0] == '"' && result[^1] == '"' && result.IndexOfAny([';', '=', ',']) >= 0) result = result[1..^1];
        return result;
    }

    private static string DecodeQt(string value, string source) {
        StringBuilder decoded = new();
        bool quote = false;
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '"') { quote = !quote; continue; }
            if (!quote && c == ';') break;
            if (!quote && c == ',') throw new MmcInputException("MMC_CFG_LIST", source, "A Qt list requires a dedicated decoder; quote scalar commas.");
            if (c != '\\') { decoded.Append(c); continue; }
            if (++i == value.Length) Bad(source, "Dangling Qt escape.");
            c = value[i];
            if (c == 'x') {
                int start = i + 1;
                int end = start;
                while (end < value.Length && end - start < 4 && Uri.IsHexDigit(value[end])) end++;
                if (start == end) Bad(source, "Empty hexadecimal escape.");
                decoded.Append((char)int.Parse(value.AsSpan(start, end - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i = end - 1;
                continue;
            }
            decoded.Append(c switch {
                'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v',
                '\\' => '\\', '"' => '"', ';' => ';', ',' => ',', '#' => '#',
                _ => throw new MmcInputException("MMC_CFG_ESCAPE", source, "Unsupported Qt escape.")
            });
        }
        if (quote) Bad(source, "Unterminated quoted scalar.");
        string result = decoded.ToString();
        return result.StartsWith("@@", StringComparison.Ordinal) ? result[1..] : result;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Bad(string source, string message) => throw new MmcInputException("MMC_CFG", source, message);
}
