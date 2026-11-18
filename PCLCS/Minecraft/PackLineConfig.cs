// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>Retains the incoming layout when a complete line-based configuration can be understood.</summary>
internal sealed class PackLineConfig {
    private const string BasicString = @"""(?:[^""\\\x00-\x1f\x7f]|\\(?:[""\\btnfr]|u[0-9a-fA-F]{4}))*""";
    private const string LiteralString = @"'[^'\x00-\x1f\x7f]*'";
    private const string Key = "[A-Za-z0-9_-]+|" + BasicString + "|" + LiteralString;
    private const string Decimal = @"[+-]?(?:0|[1-9](?:_?[0-9])*)(?:\.[0-9](?:_?[0-9])*)?(?:[eE][+-]?[0-9](?:_?[0-9])*)?";
    private const string Scalar = "(?:" + BasicString + "|" + LiteralString + "|true|false|" + Decimal + ")";
    private const string Array = @"\[[ \t]*(?:" + Scalar + @"(?:[ \t]*,[ \t]*" + Scalar + @")*[ \t]*,?)?[ \t]*\]";
    private static readonly Regex TomlHeader = Pattern(@"\A[ \t]*\[[ \t]*(?<table>(?:" + Key + @")(?:[ \t]*\.[ \t]*(?:" + Key + @"))*)[ \t]*\][ \t]*(?:#.*)?\z");
    private static readonly Regex TomlPart = Pattern(Key);
    private static readonly Regex TomlAssignment = Pattern(@"\A(?<prefix>[ \t]*(?<key>" + Key + @")[ \t]*=[ \t]*)(?<value>" + Scalar + "|" + Array + @")(?<suffix>[ \t]*(?:#.*)?)\z");
    private static readonly Regex PropertyAssignment = Pattern(@"\A(?<prefix>[ \t\f]*(?<key>[A-Za-z0-9_.-]+)(?:[ \t\f]*[=:][ \t\f]*|[ \t\f]+))(?<value>.*)\z");

    private sealed record Field(string Key, string Table, int Line, string Prefix, string Suffix);
    private sealed record Table(string Header, int End);
    private readonly string[] lines;
    private readonly string newline;
    private readonly bool finalNewline;
    private readonly Dictionary<string, Field> fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Table> tables = new(StringComparer.Ordinal);
    public JObject Values { get; } = new();

    private static Regex Pattern(string expression) => new(expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private PackLineConfig(string text, bool toml) {
        newline = PackConfigMerge.Newline(text);
        finalNewline = text.EndsWith("\n", StringComparison.Ordinal);
        string normalized = text.Replace("\r\n", "\n");
        if (normalized.Contains('\r')) throw new FormatException();
        lines = (finalNewline ? normalized[..^1] : normalized).Split('\n');
        string table = "", header = "";
        for (int index = 0; index < lines.Length; index++) {
            string line = lines[index];
            string trimmed = line.TrimStart(' ', '\t', '\f');
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) ||
                !toml && trimmed.StartsWith("!", StringComparison.Ordinal)) continue;
            if (toml && trimmed.StartsWith("[", StringComparison.Ordinal)) {
                Match match = TomlHeader.Match(line);
                if (!match.Success || !tables.TryAdd(table, new(header, index))) throw new FormatException();
                table = string.Join(".", TomlPart.Matches(match.Groups["table"].Value).Cast<Match>().Select(part => CanonicalKey(part.Value)));
                header = line;
                continue;
            }
            var assignment = (toml ? TomlAssignment : PropertyAssignment).Match(line);
            if (!assignment.Success) throw new FormatException();
            string name = assignment.Groups["key"].Value, value = assignment.Groups["value"].Value;
            if (toml) name = CanonicalKey(name);
            else if (value.EndsWith("\\", StringComparison.Ordinal)) throw new FormatException();
            string key = table.Length == 0 ? name : table + "." + name;
            if (!fields.TryAdd(key, new(key, table, index, assignment.Groups["prefix"].Value, assignment.Groups["suffix"].Value))) throw new FormatException();
            Values.Add(key, new JValue(value));
        }
        if (!tables.TryAdd(table, new(header, lines.Length))) throw new FormatException();
    }

    internal static PackLineConfig Parse(string text, bool toml) => new(text, toml);

    private static string CanonicalKey(string raw) {
        string name = raw[0] switch {
            '\'' => raw[1..^1],
            '"' => JsonConvert.DeserializeObject<string>(raw)!,
            _ => raw
        };
        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ?
            name : JsonConvert.ToString(name);
    }

    internal static bool CanCombine(params PackLineConfig[] documents) {
        // A scalar in one version and a table in another requires a file-level decision.
        var keys = documents.SelectMany(document => document.fields.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var occupied = new HashSet<string>(documents.SelectMany(document => document.tables.Keys), StringComparer.Ordinal);
        foreach (string key in keys)
            if (occupied.Contains(key) || occupied.Any(table => table.StartsWith(key + ".", StringComparison.Ordinal))) return false;
        return true;
    }

    internal string Write(JObject result, PackLineConfig player) {
        var changes = fields.Values.ToDictionary(field => field.Line);
        var added = result.Properties().Where(property => !fields.ContainsKey(property.Name))
            .Select(property => player.fields[property.Name]).GroupBy(field => field.Table).ToArray();
        var inserts = added.Where(group => tables.ContainsKey(group.Key))
            .GroupBy(group => tables[group.Key].End).ToDictionary(group => group.Key,
                group => group.SelectMany(fields => fields).Select(Render).ToArray());
        List<string> output = new();
        for (int index = 0; index <= lines.Length; index++) {
            if (inserts.TryGetValue(index, out var extra)) output.AddRange(extra);
            if (index == lines.Length) break;
            if (!changes.TryGetValue(index, out var field)) output.Add(lines[index]);
            else if (result[field.Key] is not null) output.Add(Render(field));
        }
        foreach (var group in added.Where(group => !tables.ContainsKey(group.Key))) {
            output.Add(player.tables[group.Key].Header);
            output.AddRange(group.Select(Render));
        }
        return string.Join(newline, output) + (finalNewline && output.Count != 0 ? newline : "");

        string Render(Field field) => field.Prefix + result[field.Key]!.Value<string>() + field.Suffix;
    }
}
