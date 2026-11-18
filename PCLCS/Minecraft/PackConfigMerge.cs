// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonDocument = System.Text.Json.JsonDocument;

namespace PCLCS;

public sealed record PackConfigConflict(string Key, string? PlayerValue, string? AuthorValue) {
    public static string ShortValue(string? value) {
        string text = value?.Replace("\r", " ").Replace("\n", " ") ?? "删除此键";
        return text.Length <= 72 ? text : text[..72] + "…";
    }
}

/// <summary>Compares verified original, local and incoming configuration documents by key.</summary>
public sealed class PackConfigMerge {
    internal const int MaximumFileBytes = 256 * 1024;
    private const int MaximumConflicts = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly JObject original, player, author;
    private readonly Func<JObject, byte[]> write;
    public IReadOnlyList<PackConfigConflict> Conflicts { get; }

    private PackConfigMerge(JObject original, JObject player, JObject author, Func<JObject, byte[]> write) {
        this.original = original;
        this.player = player;
        this.author = author;
        this.write = write;
        List<PackConfigConflict> conflicts = new();
        Merge(original, player, author, "", null, conflicts);
        Conflicts = new ReadOnlyCollection<PackConfigConflict>(conflicts);
    }

    internal static bool Supports(string relative) {
        if (!relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
            !relative.StartsWith("defaultconfigs/", StringComparison.OrdinalIgnoreCase)) return false;
        return Path.GetExtension(relative).ToLowerInvariant() is ".json" or ".toml" or ".properties";
    }

    internal static PackConfigMerge? TryCreate(string relative, byte[] original, byte[] player, byte[] author) {
        if (!Supports(relative) || new[] { original, player, author }.Any(bytes => bytes.Length > MaximumFileBytes)) return null;
        try {
            string before = Decode(original), local = Decode(player), after = Decode(author);
            PackConfigMerge? merge;
            if (Path.GetExtension(relative).Equals(".json", StringComparison.OrdinalIgnoreCase)) {
                merge = new(ParseJson(before), ParseJson(local), ParseJson(after), value => {
                    string newline = Newline(after);
                    string text = value.ToString(Formatting.Indented).Replace("\r\n", "\n").Replace("\n", newline);
                    return Encode(text + (after.EndsWith("\n", StringComparison.Ordinal) ? newline : ""), HasBom(author));
                });
            } else {
                bool toml = Path.GetExtension(relative).Equals(".toml", StringComparison.OrdinalIgnoreCase);
                var oldDocument = PackLineConfig.Parse(before, toml);
                var localDocument = PackLineConfig.Parse(local, toml);
                var newDocument = PackLineConfig.Parse(after, toml);
                if (toml && !PackLineConfig.CanCombine(oldDocument, localDocument, newDocument)) return null;
                merge = new(oldDocument.Values, localDocument.Values, newDocument.Values,
                    value => Encode(newDocument.Write(value, localDocument), HasBom(author)));
            }
            return merge.Conflicts.Count <= MaximumConflicts ? merge : null;
        } catch (Exception error) when (error is JsonException or System.Text.Json.JsonException or FormatException or DecoderFallbackException or RegexMatchTimeoutException or InvalidOperationException) {
            return null;
        }
    }

    internal byte[] Render(HashSet<string> useAuthor) =>
        write((JObject)Merge(original, player, author, "", useAuthor, null)!);

    private static JToken? Merge(JToken? original, JToken? player, JToken? author, string path,
        HashSet<string>? useAuthor, List<PackConfigConflict>? conflicts) {
        if (JToken.DeepEquals(player, original)) return author?.DeepClone();
        if (JToken.DeepEquals(author, original) || JToken.DeepEquals(player, author)) return player?.DeepClone();
        if ((original is JObject || original is null) && player is JObject local && author is JObject incoming) {
            JObject value = new();
            var names = incoming.Properties().Select(property => property.Name)
                .Concat(local.Properties().Select(property => property.Name))
                .Distinct(StringComparer.Ordinal);
            foreach (string name in names) {
                var item = Merge((original as JObject)?[name], local[name], incoming[name], path + "/" + Escape(name), useAuthor, conflicts);
                if (item is not null) value.Add(name, item);
            }
            return value;
        }
        conflicts?.Add(new(path, Display(player), Display(author)));
        return (useAuthor?.Contains(path) == true ? author : player)?.DeepClone();
    }

    internal static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");
    private static string? Display(JToken? value) => value?.ToString(Formatting.None);

    private static JObject ParseJson(string text) {
        // A strict UTF-8 JSON object, with no comments, duplicate keys or implicit date conversion.
        using var validated = JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
        return ReadJson(validated.RootElement) as JObject ?? throw new FormatException();
    }

    private static JToken ReadJson(System.Text.Json.JsonElement value) {
        switch (value.ValueKind) {
            case System.Text.Json.JsonValueKind.Object:
                JObject map = new();
                foreach (var property in value.EnumerateObject()) {
                    if (map.Property(property.Name, StringComparison.Ordinal) is not null) throw new FormatException();
                    map.Add(property.Name, ReadJson(property.Value));
                }
                return map;
            case System.Text.Json.JsonValueKind.Array:
                return new JArray(value.EnumerateArray().Select(ReadJson));
            case System.Text.Json.JsonValueKind.String:
                return new JValue(value.GetString());
            case System.Text.Json.JsonValueKind.Number:
                return new JRaw(value.GetRawText());
            case System.Text.Json.JsonValueKind.True:
            case System.Text.Json.JsonValueKind.False:
                return new JValue(value.GetBoolean());
            default:
                return JValue.CreateNull();
        }
    }

    private static bool HasBom(byte[] data) => data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf;
    private static string Decode(byte[] data) {
        int offset = HasBom(data) ? 3 : 0;
        string text = StrictUtf8.GetString(data, offset, data.Length - offset);
        if (text.IndexOf('\0') >= 0) throw new FormatException();
        return text;
    }
    private static byte[] Encode(string text, bool bom) => StrictUtf8.GetBytes(bom ? "\ufeff" + text : text);
    internal static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
