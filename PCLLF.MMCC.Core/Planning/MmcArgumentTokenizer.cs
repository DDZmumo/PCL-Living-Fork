// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text;

namespace PCLLF.MMCC.Core;

/// <summary>Tokenizes cfg/user command strings; native minecraftArguments and +jvmArgs array members never pass through this parser.</summary>
public static class MmcArgumentTokenizer {
    public static ImmutableArray<string> Tokenize(string value) {
        ArgumentNullException.ThrowIfNull(value);
        List<string> result = [];
        StringBuilder token = new();
        char quote = '\0';
        bool present = false;
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '\0') throw new FormatException("NUL in command string.");
            if (char.IsWhiteSpace(c) && quote == '\0') {
                if (present) { result.Add(token.ToString()); token.Clear(); present = false; }
                continue;
            }
            present = true;
            if (c == '\\' && quote != '\'') {
                int end = i;
                while (end < value.Length && value[end] == '\\') end++;
                int count = end - i;
                if (end < value.Length && value[end] == '"') {
                    token.Append('\\', count / 2);
                    if (count % 2 == 1) token.Append('"');
                    else quote = quote == '"' ? '\0' : '"';
                    i = end;
                } else { token.Append('\\', count); i = end - 1; }
                continue;
            }
            if (c is '"' or '\'') {
                if (quote == '\0') { quote = c; continue; }
                if (quote == c) { quote = '\0'; continue; }
            }
            token.Append(c);
        }
        if (quote != '\0') throw new FormatException("Unterminated command-string quote.");
        if (present) result.Add(token.ToString());
        return result.ToImmutableArray();
    }
}
