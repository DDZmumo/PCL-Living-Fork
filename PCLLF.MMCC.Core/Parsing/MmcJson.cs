// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

internal sealed class MmcInputException(string code, string source, string message) : Exception(message) {
    internal string Code { get; } = code;
    internal string SourcePath { get; } = source;
}

internal static class MmcJson {
    internal static JsonDocument Read(string text, string source, int depth) {
        JsonDocument? document = null;
        try {
            document = JsonDocument.Parse(text.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = depth });
            Validate(document.RootElement, source);
            if (document.RootElement.ValueKind != JsonValueKind.Object) Fail(source, "Root must be an object.");
            return document;
        } catch (JsonException e) {
            document?.Dispose();
            throw new MmcInputException("MMC_JSON", source, $"Invalid JSON at line {e.LineNumber}, byte {e.BytePositionInLine}.");
        } catch {
            document?.Dispose();
            throw;
        }
    }

    private static void Validate(JsonElement value, string source) {
        if (value.ValueKind == JsonValueKind.Object) {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty item in value.EnumerateObject()) {
                if (!names.Add(item.Name)) throw new MmcInputException("MMC_DUPLICATE_JSON_KEY", source, $"Duplicate JSON key: {item.Name}.");
                Validate(item.Value, source);
            }
        } else if (value.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in value.EnumerateArray()) Validate(item, source);
        }
    }

    internal static ImmutableDictionary<string, JsonElement> Fields(JsonElement value) =>
        value.EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    internal static string? String(JsonElement obj, string key, string source, bool required = false) {
        if (!obj.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) {
            if (required) Fail(source, $"Missing string: {key}.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String) Fail(source, $"{key} must be a string.");
        string result = value.GetString()!;
        if (required && string.IsNullOrWhiteSpace(result)) Fail(source, $"{key} cannot be empty.");
        return result;
    }

    internal static bool Boolean(JsonElement obj, string key, string source) {
        if (!obj.TryGetProperty(key, out JsonElement value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail(source, $"{key} must be a Boolean.");
        return value.GetBoolean();
    }

    internal static void FormatVersion(JsonElement obj, string source, bool required) {
        if (!obj.TryGetProperty("formatVersion", out JsonElement value)) {
            if (required) Fail(source, "Missing formatVersion.");
            return;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int format) || format != 1)
            throw new MmcInputException("MMC_UNSUPPORTED_FORMAT", source, "Only formatVersion 1 is supported.");
    }

    internal static ImmutableArray<string> Strings(JsonElement value, string source, string field) {
        if (value.ValueKind != JsonValueKind.Array) Fail(source, $"{field} must be an array.");
        var items = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.String) Fail(source, $"{field} must contain strings.");
            items.Add(item.GetString()!);
        }
        return items.ToImmutable();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Fail(string source, string message) => throw new MmcInputException("MMC_STRUCTURE", source, message);
}
