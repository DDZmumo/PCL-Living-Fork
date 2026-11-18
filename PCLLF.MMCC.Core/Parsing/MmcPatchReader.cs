// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

internal static class MmcPatchReader {
    // This is an explicit P1 read capability table, not a claim of an implemented launcher.
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) {
        "formatVersion", "uid", "fileId", "version", "name", "description", "releaseTime", "type", "order", "volatile",
        "requires", "conflicts", "mainClass", "mainJar", "assetIndex", "assets", "minecraftArguments", "+jvmArgs",
        "+tweakers", "libraries", "+libraries", "mavenFiles", "compatibleJavaMajors", "compatibleJavaName", "+traits", "traits", "logging"
    };
    private static readonly HashSet<string> LibraryFields = new(StringComparer.Ordinal) {
        "name", "url", "MMC-hint", "MMC-filename", "MMC-absoluteUrl", "absoluteUrl", "downloads", "natives", "rules", "extract"
    };
    private static readonly HashSet<string> Traits = new(StringComparer.Ordinal) { "FirstThreadOnMacOS", "texturepacks", "no-texturepacks" };

    internal static MmcPatch Parse(string text, string source, MmcReadLimits limits,
        bool enabled, List<MmcDiagnostic> diagnostics) {
        using var document = MmcJson.Read(text, source, limits.MaxDepth);
        JsonElement root = document.RootElement;
        MmcJson.FormatVersion(root, source, false);
        string? uid = MmcJson.String(root, "uid", source);
        string? oldId = MmcJson.String(root, "fileId", source);
        if (uid is null && oldId is null) MmcJson.Fail(source, "Patch must declare uid or fileId.");
        if (uid is not null && oldId is not null && uid != oldId) MmcJson.Fail(source, "uid and fileId disagree.");
        uid ??= oldId;
        ValidateUid(uid!, source);
        string? version = MmcJson.String(root, "version", source);
        bool deferredLibrarySemantics = false;
        foreach (JsonProperty field in root.EnumerateObject()) {
            if (!Fields.Contains(field.Name)) {
                Unsupported(field.Name, source, enabled, diagnostics);
                continue;
            }
            switch (field.Name) {
                case "mainClass": case "minecraftArguments": case "compatibleJavaName": case "assets":
                    MmcJson.String(root, field.Name, source); break;
                case "+jvmArgs": case "+tweakers":
                    MmcJson.Strings(field.Value, source, field.Name); break;
                case "compatibleJavaMajors":
                    if (field.Value.ValueKind != JsonValueKind.Array) MmcJson.Fail(source, "compatibleJavaMajors must be an array.");
                    foreach (JsonElement major in field.Value.EnumerateArray())
                        if (major.ValueKind != JsonValueKind.Number || !major.TryGetInt32(out int n) || n < 1)
                            MmcJson.Fail(source, "compatibleJavaMajors requires positive integer majors.");
                    break;
                case "mainJar":
                    if (field.Value.ValueKind != JsonValueKind.Null) Library(field.Value, source, enabled, diagnostics, ref deferredLibrarySemantics);
                    break;
                case "libraries": case "+libraries": case "mavenFiles":
                    if (field.Value.ValueKind != JsonValueKind.Array) MmcJson.Fail(source, $"{field.Name} must be an array.");
                    if (field.Value.GetArrayLength() > limits.MaxArchiveEntries) MmcJson.Fail(source, "Declared artifact budget exceeded.");
                    foreach (JsonElement library in field.Value.EnumerateArray()) Library(library, source, enabled, diagnostics, ref deferredLibrarySemantics);
                    break;
                case "assetIndex":
                    if (field.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) MmcJson.Fail(source, "assetIndex must be an object or null.");
                    if (field.Value.ValueKind == JsonValueKind.Object) {
                        CheckObjectFields(field.Value, ["id", "url", "sha1", "size", "totalSize"], "assetIndex", source, enabled, diagnostics);
                        foreach (string name in new[] { "id", "url", "sha1" }) MmcJson.String(field.Value, name, source);
                        CheckSizes(field.Value, source);
                    }
                    break;
                case "logging":
                    Logging(field.Value, source, enabled, diagnostics);
                    break;
                case "+traits": case "traits":
                    foreach (string trait in MmcJson.Strings(field.Value, source, field.Name))
                        if (!Traits.Contains(trait)) Unsupported("trait:" + trait, source, enabled, diagnostics);
                    break;
            }
        }
        if (deferredLibrarySemantics) diagnostics.Add(new("MMC_DEFERRED_LIBRARY_SEMANTICS", MmcDiagnosticSeverity.Information, source,
            "Library nested shapes are checked and retained; platform filtering, classifier resolution, extraction and execution semantics require the P2 compiler."));
        return new(source, Hash(text), uid!, version,
            Requirements(root, "requires", source), Requirements(root, "conflicts", source), MmcJson.Fields(root));
    }

    private static void Library(JsonElement value, string source, bool enabled, List<MmcDiagnostic> diagnostics, ref bool deferredLibrarySemantics) {
        if (value.ValueKind != JsonValueKind.Object) MmcJson.Fail(source, "Library must be an object.");
        string name = MmcJson.String(value, "name", source, true)!;
        if (value.EnumerateObject().Any(p => p.Name is "rules" or "downloads" or "natives" or "extract"))
            deferredLibrarySemantics = true;
        string[] coordinate = name.Split('@')[0].Split(':');
        if (coordinate.Length is < 3 or > 4 || coordinate.Any(string.IsNullOrWhiteSpace)) MmcJson.Fail(source, "Invalid library coordinate.");
        foreach (JsonProperty field in value.EnumerateObject()) {
            if (!LibraryFields.Contains(field.Name)) Unsupported("library:" + field.Name, source, enabled, diagnostics);
            else if (field.Name is "url" or "MMC-hint" or "MMC-filename" or "MMC-absoluteUrl" or "absoluteUrl") MmcJson.String(value, field.Name, source);
            else if (field.Name is "downloads" or "natives" or "extract") {
                if (field.Value.ValueKind != JsonValueKind.Object) MmcJson.Fail(source, $"library {field.Name} must be an object.");
                if (field.Name == "downloads") {
                    CheckObjectFields(field.Value, ["artifact", "classifiers"], "downloads", source, enabled, diagnostics);
                    if (field.Value.TryGetProperty("artifact", out JsonElement artifact)) Download(artifact, source, enabled, diagnostics);
                    if (field.Value.TryGetProperty("classifiers", out JsonElement classifiers)) {
                        if (classifiers.ValueKind != JsonValueKind.Object) MmcJson.Fail(source, "classifiers must be an object.");
                        foreach (JsonProperty classifier in classifiers.EnumerateObject()) Download(classifier.Value, source, enabled, diagnostics);
                    }
                } else if (field.Name == "natives") {
                    foreach (JsonProperty platform in field.Value.EnumerateObject())
                        if (platform.Value.ValueKind != JsonValueKind.String) MmcJson.Fail(source, "Native classifier must be a string.");
                } else {
                    CheckObjectFields(field.Value, ["exclude"], "extract", source, enabled, diagnostics);
                    if (field.Value.TryGetProperty("exclude", out JsonElement exclude)) MmcJson.Strings(exclude, source, "exclude");
                }
            } else if (field.Name == "rules") {
                if (field.Value.ValueKind != JsonValueKind.Array) MmcJson.Fail(source, "Library rules must be an array.");
                foreach (JsonElement rule in field.Value.EnumerateArray()) {
                    CheckObjectFields(rule, ["action", "os"], "rule", source, enabled, diagnostics);
                    string? action = MmcJson.String(rule, "action", source, true);
                    if (action is not ("allow" or "disallow")) MmcJson.Fail(source, "Rule action must be allow or disallow.");
                    if (rule.TryGetProperty("os", out JsonElement os)) {
                        CheckObjectFields(os, ["name", "arch", "version"], "rule.os", source, enabled, diagnostics);
                        foreach (string osField in new[] { "name", "arch", "version" }) MmcJson.String(os, osField, source);
                        if (os.TryGetProperty("version", out _)) diagnostics.Add(new("MMC_RULE_OS_VERSION", MmcDiagnosticSeverity.Warning, source, "prism-v1 retains os.version but does not apply it; host hard constraints remain separate."));
                    }
                }
            }
        }
    }

    private static void Download(JsonElement value, string source, bool enabled, List<MmcDiagnostic> diagnostics) {
        CheckObjectFields(value, ["path", "url", "sha1", "sha256", "size"], "download", source, enabled, diagnostics);
        foreach (string key in new[] { "path", "url", "sha1", "sha256" }) MmcJson.String(value, key, source);
        CheckSizes(value, source);
    }

    private static void Logging(JsonElement value, string source, bool enabled, List<MmcDiagnostic> diagnostics) {
        CheckObjectFields(value, ["argument", "file", "type"], "logging", source, enabled, diagnostics);
        MmcJson.String(value, "argument", source, true);
        string type = MmcJson.String(value, "type", source, true)!;
        if (type != "log4j2-xml") Unsupported("logging:" + type, source, enabled, diagnostics);
        if (!value.TryGetProperty("file", out var file)) MmcJson.Fail(source, "logging requires a file descriptor.");
        CheckObjectFields(file, ["id", "url", "sha1", "sha256", "size"], "logging.file", source, enabled, diagnostics);
        foreach (string key in new[] { "id", "url" }) MmcJson.String(file, key, source, true);
        foreach (string key in new[] { "sha1", "sha256" }) MmcJson.String(file, key, source);
        CheckSizes(file, source);
    }

    private static void CheckSizes(JsonElement value, string source) {
        foreach (string key in new[] { "size", "totalSize" })
            if (value.TryGetProperty(key, out JsonElement size) && (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes < 0))
                MmcJson.Fail(source, $"{key} must be a nonnegative integer.");
    }

    private static void CheckObjectFields(JsonElement value, string[] known, string context, string source, bool enabled, List<MmcDiagnostic> diagnostics) {
        if (value.ValueKind != JsonValueKind.Object) MmcJson.Fail(source, $"{context} must be an object.");
        foreach (JsonProperty field in value.EnumerateObject())
            if (!known.Contains(field.Name, StringComparer.Ordinal)) Unsupported(context + ":" + field.Name, source, enabled, diagnostics);
    }

    internal static ImmutableArray<MmcRequirement> Requirements(JsonElement root, string key, string source) {
        if (!root.TryGetProperty(key, out JsonElement values)) return [];
        if (values.ValueKind != JsonValueKind.Array) MmcJson.Fail(source, $"{key} must be an array.");
        var result = ImmutableArray.CreateBuilder<MmcRequirement>();
        foreach (JsonElement item in values.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) MmcJson.Fail(source, $"{key} entries must be objects.");
            foreach (JsonProperty field in item.EnumerateObject())
                if (field.Name is not ("uid" or "equals" or "suggests")) MmcJson.Fail(source, $"Unsupported {key} field: {field.Name}.");
            string uid = MmcJson.String(item, "uid", source, true)!;
            ValidateUid(uid, source);
            string? exact = MmcJson.String(item, "equals", source);
            string? suggested = MmcJson.String(item, "suggests", source);
            // An explicitly empty constraint contributes no version requirement.
            // Nonempty strings are preserved exactly; Fields retains raw input.
            result.Add(new(uid, exact == "" ? null : exact, suggested == "" ? null : suggested));
        }
        return result.ToImmutable();
    }

    internal static void ValidateUid(string uid, string source) {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 512 || uid.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || uid is "." or "..")
            MmcJson.Fail(source, "Invalid component UID.");
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static void ValidateCapabilities(MmcPatch patch, bool enabled, List<MmcDiagnostic> diagnostics) {
        bool deferred = false;
        foreach ((string name, JsonElement value) in patch.Fields) {
            if (!Fields.Contains(name)) Unsupported(name, patch.Source, enabled, diagnostics);
            if (name is "+traits" or "traits") foreach (string trait in MmcJson.Strings(value, patch.Source, name))
                if (!Traits.Contains(trait)) Unsupported("trait:" + trait, patch.Source, enabled, diagnostics);
            if (name is "libraries" or "+libraries" or "mavenFiles") foreach (JsonElement library in value.EnumerateArray()) Library(library, patch.Source, enabled, diagnostics, ref deferred);
            if (name == "mainJar" && value.ValueKind != JsonValueKind.Null) Library(value, patch.Source, enabled, diagnostics, ref deferred);
            if (name == "logging") Logging(value, patch.Source, enabled, diagnostics);
        }
    }

    internal static void Unsupported(string field, string source, bool enabled, List<MmcDiagnostic> diagnostics) =>
        diagnostics.Add(new("MMC_UNSUPPORTED_REQUIRED", enabled ? MmcDiagnosticSeverity.Error : MmcDiagnosticSeverity.Warning,
            source, $"Unsupported capability {field}; an inactive source is retained but cannot be enabled without validation."));
}
