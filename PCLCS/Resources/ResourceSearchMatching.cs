using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic;
using MeloongCore.Extensions;

namespace PCLCS;

// Private resource matcher copied from LF ModBase.Search, including its destructive
// selection-sort/tie order. The shared public VB helper is neither changed nor used
// as a resource-business callback, so modern hosts execute the real matching in C#.
internal sealed class ResourceSearchSource {
    internal string[] Aliases;
    internal double Weight;
    internal ResourceSearchSource(string[] aliases, double weight) { Aliases = aliases; Weight = weight; }
    internal ResourceSearchSource(string text, double weight) : this(new[] { text }, weight) { }
}
internal sealed class ResourceSearchEntry<T> {
    internal T Item = default!;
    internal List<ResourceSearchSource> Sources = null!;
    internal double Similarity;
    internal bool AbsoluteRight;
}
internal static class ResourceSearchMatching {
    private static double Similarity(string source, string query) {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query)) return 0;
        int qp = 0; double lenSum = 0;
        StringBuilder str = new StringBuilder(source.Length).Append(source.Lower().Replace(" ", ""));
        query = query.Lower().Replace(" ", "");
        int sourceLength = str.Length, queryLength = query.Length;
        if (queryLength == 0) return 0;
        while (qp < queryLength) {
            int sp = 0, lenMax = 0, spMax = 0, currentSourceLength = str.Length;
            while (sp < currentSourceLength) {
                int len = 0;
                while (qp + len < queryLength && sp + len < currentSourceLength && str[sp + len] == query[qp + len]) len++;
                if (len > lenMax) { lenMax = len; spMax = sp; }
                sp += len > 0 ? len : 1;
            }
            if (lenMax > 0) {
                str.Remove(spMax, lenMax);
                double incWeight = Math.Pow(1.4, 3 + lenMax) - 3.6;
                incWeight *= 1 + 0.3 * Math.Max(0, 3 - Math.Abs(qp - spMax));
                lenSum += incWeight;
            }
            qp += lenMax > 0 ? lenMax : 1;
        }
        return lenSum / queryLength * (3 / Math.Sqrt(sourceLength + 15)) * (queryLength <= 2 ? 3 - queryLength : 1);
    }
    internal static List<ResourceSearchEntry<T>> Search<T>(List<ResourceSearchEntry<T>> entries, string query, int maxBlurCount, double minBlurSimilarity) {
        List<ResourceSearchEntry<T>> results = new();
        if (!entries.Any()) return results;
        string[] parts = query.Split(" ", true);
        List<ResourceSearchEntry<T>> candidates = new();
        foreach (ResourceSearchEntry<T> entry in entries) {
            double sum = 0, totalWeight = 0;
            foreach (ResourceSearchSource source in entry.Sources) {
                if (source.Aliases.Any()) sum += source.Aliases.Max(alias => Similarity(alias, query)) * source.Weight;
                totalWeight += source.Weight;
            }
            entry.Similarity = sum / totalWeight;
            entry.AbsoluteRight = parts.All(part => entry.Sources.Any(source => source.Aliases.Any(alias => alias.Replace(" ", "").ContainsIgnoreCase(part))));
            if (entry.AbsoluteRight || entry.Similarity >= minBlurSimilarity) candidates.Add(entry);
        }
        List<ResourceSearchEntry<T>> sorted = new();
        while (candidates.Any()) {
            ResourceSearchEntry<T> highest = candidates[0];
            for (int i = 1; i < candidates.Count; i++) {
                ResourceSearchEntry<T> next = candidates[i];
                if (next.AbsoluteRight != highest.AbsoluteRight ? next.AbsoluteRight : next.Similarity > highest.Similarity) highest = next;
            }
            candidates.Remove(highest); sorted.Add(highest);
        }
        int blurCount = 0;
        foreach (ResourceSearchEntry<T> entry in sorted) {
            if (entry.AbsoluteRight) results.Add(entry);
            else { if (blurCount == maxBlurCount) break; results.Add(entry); blurCount++; }
        }
        return results;
    }
    internal static IEnumerable<string> ExtractWords(ResourceSearchEntry<WikiEntry> result, ResourcePlatforms source) {
        List<string> candidates = new();
        string english = ResourceWikiIdentity.EnglishName(result.Item);
        if (english.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            candidates.Add(english.BeforeFirst(" - ").Replace("-", " ").Replace("/", " ").Replace(":", " ").Replace("(", " ").Replace(")", ""));
        else if (result.Item.Slugs.ContainsKey(source)) candidates.Add(result.Item.Slugs[source].Replace("-", " ").Replace("/", " "));
        candidates = candidates.SelectMany(c => c.Split(' ')).Select(w => w.TrimStart('{', '[', '(').TrimEnd('}', ']', ')').Lower()).Where(w => {
            if (w.Length <= 1) return false;
            if (new[] { "the", "of", "mod", "and", "forge", "fabric", "for", "quilt", "neoforge" }.Contains(w)) return false;
            if (Conversion.Val(w) > 0) return false;
            if (!w.IsAsciiOnly()) return false;
            return true;
        }).Distinct().ToList();
        Func<string, bool> canForm = null!;
        canForm = s => candidates.Contains(s) || candidates.Any(c => s.StartsWith(c) && canForm(s.Substring(c.Length)));
        candidates = candidates.Where(w => !candidates.Any(c => c.Length < w.Length && w.StartsWith(c) && canForm(w.Substring(c.Length)))).ToList();
        return candidates;
    }
    internal static List<ResourceSearchEntry<WikiEntry>> WikiEntries(IEnumerable<WikiEntry> entries, ResourcePlatforms source, Func<string, string> simplify) {
        List<ResourceSearchEntry<WikiEntry>> result = new();
        foreach (WikiEntry entry in entries) {
            string slug = entry.Slugs.GetOrDefault(source) ?? "";
            string english = ResourceWikiIdentity.EnglishName(entry);
            if (string.IsNullOrWhiteSpace(slug) && string.IsNullOrWhiteSpace(english)) continue;
            result.Add(new ResourceSearchEntry<WikiEntry> { Item = entry, Sources = entry.ChineseName is not null ? new() {
                new ResourceSearchSource(simplify(entry.ChineseName.BeforeFirst(" (")).Split('/', true), 1),
                new ResourceSearchSource(simplify(english) + " " + slug, 0.5)
            } : new() { new ResourceSearchSource(slug, 0.5) } });
        }
        return result;
    }
}
