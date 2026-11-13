// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

/// <summary>
/// Host-side invariant names an active cfg environment mapping may never redeclare. They are refused
/// explicitly instead of being applied and then silently overwritten, because the launcher owns the
/// per-session temporary directory and the execution-input boundary (design §7.1: a requirement this
/// host cannot honour is refused, never dropped).
/// </summary>
public static class MmcEnvironmentPolicy {
    /// <summary>Compared case-insensitively: a managed Windows process environment is case-insensitive.</summary>
    public static ImmutableHashSet<string> ReservedNames { get; } = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "TEMP", "TMP", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH");

    public static bool IsReserved(string name) => ReservedNames.Contains(name);
}

/// <summary>
/// Decodes the <c>Env</c> key of an instance.cfg into an ordered name/value mapping. Every supported
/// encoding has its own escaping contract; an encoding without a verified decoder is refused rather than
/// guessed. Values stay literal: cfg escaping is undone, but launch-variable macro expansion is a
/// different channel (single-token <c>+jvmArgs</c>, <c>minecraftArguments</c> strings) and never applies here.
/// </summary>
internal static class MmcEnvironmentReader {
    /// <summary>
    /// Qt list/scalar form. The escape table matches the cfg scalar decoder exactly; the only added
    /// capability is that an unquoted comma separates list elements instead of being refused, because the
    /// environment key is a list. Every other key keeps the stricter scalar contract.
    /// </summary>
    internal static ImmutableArray<string> QtElements(string value, string source) {
        var elements = ImmutableArray.CreateBuilder<string>();
        StringBuilder current = new();
        bool quote = false;
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '"') { quote = !quote; continue; }
            if (!quote && c == ';') break;
            if (!quote && c == ',') { elements.Add(current.ToString()); current.Clear(); continue; }
            if (c != '\\') { current.Append(c); continue; }
            if (++i == value.Length) Bad(source, "Dangling Qt escape.");
            c = value[i];
            if (c == 'x') {
                int start = i + 1, end = start;
                while (end < value.Length && end - start < 4 && Uri.IsHexDigit(value[end])) end++;
                if (start == end) Bad(source, "Empty hexadecimal escape.");
                current.Append((char)int.Parse(value.AsSpan(start, end - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i = end - 1;
                continue;
            }
            current.Append(c switch {
                'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v',
                '\\' => '\\', '"' => '"', ';' => ';', ',' => ',', '#' => '#',
                _ => throw new MmcInputException("MMC_CFG_ESCAPE", source, "Unsupported Qt escape.")
            });
        }
        if (quote) Bad(source, "Unterminated quoted scalar.");
        elements.Add(current.ToString());
        return elements.ToImmutable();
    }

    /// <summary>Legacy cfg form: one element, with the comment and conditional-quote rules unchanged.</summary>
    internal static ImmutableArray<string> LegacyElements(string value, string source) {
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
        return [result];
    }

    /// <summary>JSON list form. The JSON document is its own escaping domain, so cfg decoding is not applied to it.</summary>
    internal static ImmutableArray<string> JsonElements(string encoded, string source) {
        JsonDocument document;
        try { document = JsonDocument.Parse(encoded); }
        catch (JsonException) { throw EncodingError(source, "The environment JSON value is not well-formed."); }
        using (document) {
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw EncodingError(source, "The environment JSON value must be an array.");
            var result = ImmutableArray.CreateBuilder<string>();
            foreach (JsonElement item in document.RootElement.EnumerateArray()) {
                if (item.ValueKind != JsonValueKind.String) throw EncodingError(source, "Environment JSON elements must be strings.");
                result.Add(item.GetString()!);
            }
            return result.ToImmutable();
        }
    }

    /// <summary>
    /// Validates decoded elements into the ordered mapping. Only an entirely blank value declares no
    /// mapping; a blank element among others is a malformed list separator and is refused. A repeated name
    /// is refused because source order would otherwise not determine the result.
    /// </summary>
    internal static ImmutableArray<MmcEnvironmentVariable> Parse(ImmutableArray<string> elements, string source) {
        if (elements.Length == 0 || elements.All(string.IsNullOrWhiteSpace)) return [];
        var result = ImmutableArray.CreateBuilder<MmcEnvironmentVariable>();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string element in elements) {
            if (string.IsNullOrWhiteSpace(element)) Bad(source, "A blank environment element is a malformed list separator.");
            foreach (string line in element.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
                string entry = line.Trim();
                if (entry.Length == 0) Bad(source, "A blank environment entry is a malformed list separator.");
                int separator = entry.IndexOf('=');
                if (separator <= 0) Bad(source, "An environment entry must be NAME=VALUE.");
                string name = entry[..separator].Trim();
                if (name.Length == 0 || name.Any(char.IsControl) || name.Contains('=')) Bad(source, "Invalid environment variable name.");
                if (MmcEnvironmentPolicy.IsReserved(name)) throw new MmcInputException("MMC_CFG_ENV_RESERVED", source,
                    $"The active environment mapping cannot redeclare the launcher-managed variable {name}.");
                if (!names.Add(name)) Bad(source, $"Duplicate environment variable in the active mapping: {name}.");
                // The cfg contract spells a literal leading at sign in a *value* as a doubled one.
                string value = entry[(separator + 1)..];
                if (value.StartsWith("@@", StringComparison.Ordinal)) value = value[1..];
                result.Add(new(name, value));
            }
        }
        return result.ToImmutable();
    }

    /// <summary>An active mapping whose encoding has no verified decoder in this repository.</summary>
    internal static MmcInputException EncodingError(string source, string detail) => new("MMC_CFG_ENV_ENCODING", source,
        "An active environment mapping uses an encoding this reader has no verified decoder for. " + detail);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Bad(string source, string message) => throw new MmcInputException("MMC_CFG_ENV", source, message);
}
