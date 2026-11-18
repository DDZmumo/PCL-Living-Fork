using System;
using System.Collections.Generic;
using System.IO;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal sealed class MmcRuntimeTargetClaims {
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> roles = new(StringComparer.OrdinalIgnoreCase);
    private readonly long maximumNodes;
    private long prefixCharacters;
    internal MmcRuntimeTargetClaims(long maximumNodes) { this.maximumNodes = maximumNodes; }
    internal bool TryGetValue(string key, out string? value) => files.TryGetValue(key.Replace('\\', '/'), out value);
    internal string this[string key] { set => Claim(key, value); }
    private void Claim(string key, string digest) {
        key = key.Replace('\\', '/'); MmcArtifactIo.Relative(key);
        if (files.TryGetValue(key, out string? previous) && previous != digest) throw new IOException("Runtime target content collision.");
        string[] segments = key.Split('/'); int prefixLength = 0;
        for (int index = 0; index < segments.Length; index++) {
            prefixLength += segments[index].Length + (index == 0 ? 0 : 1); prefixCharacters += prefixLength;
            if (prefixCharacters > 32 * 1024 * 1024) throw new IOException("Runtime target prefix budget exceeded.");
            string prefix = string.Join("/", segments, 0, index + 1); bool directory = index < segments.Length - 1;
            if (roles.TryGetValue(prefix, out bool existing) && existing != directory) throw new IOException("Runtime target file/directory role collision.");
            if (roles.Count >= maximumNodes && !roles.ContainsKey(prefix)) throw new IOException("Runtime implicit target node budget exceeded.");
            roles[prefix] = directory;
        }
        files[key] = digest;
    }
}
