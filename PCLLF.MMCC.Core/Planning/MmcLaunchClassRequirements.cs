// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>Pure final-token launch requirements. No command-string re-tokenization or host observations can remove a plan requirement.</summary>
public static class MmcLaunchClassRequirements {
    public const int MaximumClasses = 256;
    private const int MaximumTokens = 65_536;
    public static ImmutableArray<string> Collect(string mainClass, IEnumerable<string> jvmArguments, IEnumerable<string> gameArguments,
        IEnumerable<string>? declaredTweakers = null, IEnumerable<string>? additionalRequiredClasses = null) {
        ArgumentNullException.ThrowIfNull(jvmArguments); ArgumentNullException.ThrowIfNull(gameArguments);
        List<string> required = [mainClass]; string? loader = null; int tokens = 0; long characters = 0;
        void Token(string value) {
            if (value is null || value.Contains('\0') || ++tokens > MaximumTokens || (characters += value.Length) > 16 * 1024 * 1024)
                Bad("Launch argument token budget/shape is invalid.");
        }
        foreach (string value in jvmArguments) {
            Token(value);
            if (value.StartsWith("-Djava.system.class.loader=", StringComparison.Ordinal)) loader = value["-Djava.system.class.loader=".Length..];
        }
        if (loader is not null) required.Add(loader);
        bool waiting = false;
        foreach (string value in gameArguments) {
            Token(value);
            if (waiting) { required.Add(value); waiting = false; }
            else if (value == "--tweakClass") waiting = true;
            else if (value.StartsWith("--tweakClass=", StringComparison.Ordinal)) required.Add(value["--tweakClass=".Length..]);
            if (required.Count > MaximumTokens) Bad("Required launch class observation count exceeds its budget.");
        }
        if (waiting) Bad("Missing tweak class argument value.");
        foreach (var group in new[] { declaredTweakers ?? [], additionalRequiredClasses ?? [] }) foreach (string name in group) {
            required.Add(name);
            if (required.Count > MaximumTokens) Bad("Required launch class observation count exceeds its budget.");
        }
        if (required.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 512 ||
            name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or ':' or '{' or '}' or '"' or '\'' or '=' or '[' or ']' or ';') ||
            name.StartsWith("pcllf.mmc.runtime.", StringComparison.Ordinal)))
            Bad("Required launch class binary name is invalid or reserved.");
        var unique = required.Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (unique.Length > MaximumClasses) Bad("Required launch class count exceeds its budget.");
        return unique;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Bad(string message) => throw new FormatException(message);
}
