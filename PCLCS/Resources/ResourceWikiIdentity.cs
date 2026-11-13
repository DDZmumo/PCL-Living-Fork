// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PCLCS;

/// <summary>Database labels are hints; a stale slug cannot replace platform identity.</summary>
public static class ResourceWikiIdentity {
    private sealed class Lookup {
        internal readonly int Count;
        internal readonly Dictionary<(ResourcePlatforms,string),List<WikiEntry>> Exact=new();
        internal readonly Dictionary<string,List<WikiEntry>> Folded=new(StringComparer.Ordinal);
        internal Lookup(IReadOnlyList<WikiEntry> entries) {
            Count=entries.Count;
            foreach(var entry in entries)foreach(var pair in entry.Slugs) {
                if(string.IsNullOrWhiteSpace(pair.Value))continue;
                var key=(pair.Key,pair.Value);
                if(!Exact.TryGetValue(key,out var exact))Exact[key]=exact=new();exact.Add(entry);
                string fold=Fold(pair.Value);
                if(!Folded.TryGetValue(fold,out var folded))Folded[fold]=folded=new();folded.Add(entry);
            }
        }
    }
    private static readonly ConditionalWeakTable<IReadOnlyList<WikiEntry>,Lookup> Lookups=new();
    public static string EnglishName(WikiEntry entry) {
        string text=entry.ChineseName?.Trim()??"";if(!text.EndsWith(")",StringComparison.Ordinal))return "";
        int depth=0;
        for(int i=text.Length-1;i>=0;i--) {
            if(text[i]==')')depth++;
            else if(text[i]=='('&&--depth==0)return i>0&&text[i-1]==' '?text.Substring(i+1,text.Length-i-2):"";
        }
        return "";
    }
    private static string Fold(string? value)=>new string((value??"").Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    private static string NameKey(string? value) {
        value=Regex.Replace(value??"",@"\s*[\[(]\s*(?:forge|fabric|quilt|neoforge)(?:\s*[/+,&]\s*(?:forge|fabric|quilt|neoforge))*\s*[\])]\s*$","",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        string Acronym(string title)=>string.Concat(Regex.Matches(Regex.Replace(title,@"(?<=[a-z])(?=[A-Z])"," "),@"\b[A-Za-z0-9]").Cast<Match>().Select(match=>match.Value)).ToUpperInvariant();
        var prefix=Regex.Match(value,@"^\s*\[([A-Z][A-Z0-9]{1,11})\]\s*(.+)$");
        if(prefix.Success&&prefix.Groups[1].Value==Acronym(prefix.Groups[2].Value))value=prefix.Groups[2].Value;
        var suffix=Regex.Match(value,@"^(.+?)\s+\(([A-Z][A-Z0-9]{1,11})\)\s*$");
        if(suffix.Success&&suffix.Groups[2].Value==Acronym(suffix.Groups[1].Value))value=suffix.Groups[1].Value;
        return Fold(value);
    }
    public static bool NameCompatible(WikiEntry entry,string? rawName) {
        string expected=EnglishName(entry);
        if(string.IsNullOrWhiteSpace(rawName))return false;
        if(expected.Length==0)return true;
        string expectedKey=NameKey(expected),actualKey=NameKey(rawName);
        if(expectedKey.Length==0||actualKey.Length==0)return false;
        if(expectedKey==actualKey)return true;
        // A platform may add a separated branding prefix. Match only a whole
        // name against the other's tail, never two different branded tails.
        string Tail(string text) {var parts=Regex.Split(text,@"\s+(?:~+|\||:)\s+");return parts.Length>1?NameKey(parts[^1]):"";}
        return Tail(expected)==actualKey||Tail(rawName)==expectedKey;
    }
    private static WikiEntry? Unique(IEnumerable<WikiEntry> entries) {
        var groups=entries.GroupBy(entry=>entry.Id).Take(2).ToArray();return groups.Length==1?groups[0].First():null;
    }
    public static WikiEntry? Resolve(IReadOnlyList<WikiEntry> entries,ResourcePlatforms platform,string? slug,string? rawName) {
        if(string.IsNullOrWhiteSpace(slug)||Fold(slug).Length==0||string.IsNullOrWhiteSpace(rawName))return null;
        var lookup=Lookups.GetValue(entries,key=>new Lookup(key));
        if(lookup.Count!=entries.Count){Lookups.Remove(entries);lookup=Lookups.GetValue(entries,key=>new Lookup(key));}
        if(lookup.Exact.TryGetValue((platform,slug),out var exact)) {
            var compatible=exact.Where(entry=>NameCompatible(entry,rawName)).ToArray();
            var named=compatible.Where(entry=>EnglishName(entry).Length>0).ToArray();
            if(compatible.Length>0)return Unique(named.Length>0?named:compatible);
        }
        // Recover separators/platform slug drift only with an independently
        // matching original name. Multiple wiki identities remain ambiguous.
        return lookup.Folded.TryGetValue(Fold(slug),out var candidates)
            ?Unique(candidates.Where(entry=>EnglishName(entry).Length>0&&NameCompatible(entry,rawName))):null;
    }
    public static bool DirectMatch(WikiEntry entry,ResourcePlatforms platform,string? slug,string? rawName)=>
        !string.IsNullOrWhiteSpace(slug)&&entry.Slugs.TryGetValue(platform,out var expected)&&
        !string.IsNullOrWhiteSpace(expected)&&Fold(expected)==Fold(slug)&&NameCompatible(entry,rawName);
}
