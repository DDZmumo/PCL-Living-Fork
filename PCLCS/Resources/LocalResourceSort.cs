using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCLCS;

public enum LocalResourceSortOrder { Default, AddedNewest, AddedOldest, NameAscending, NameDescending }

/// <summary>File/directory creation time estimates local addition, not installation history.</summary>
public static class LocalResourceSort {
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> source, LocalResourceSortOrder order, Func<T, FileSystemInfo> file) {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (file is null) throw new ArgumentNullException(nameof(file));
        if (order is not (LocalResourceSortOrder.Default or LocalResourceSortOrder.AddedNewest or LocalResourceSortOrder.AddedOldest
            or LocalResourceSortOrder.NameAscending or LocalResourceSortOrder.NameDescending))
            throw new ArgumentOutOfRangeException(nameof(order));
        // Default also preserves fuzzy-search relevance and performs no file lookup.
        if (order == LocalResourceSortOrder.Default) return source.ToArray();
        Entry<T>[] entries = source.Select((value, index) => Read(value, index, file)).ToArray();
        if (order is LocalResourceSortOrder.NameAscending or LocalResourceSortOrder.NameDescending)
            return ByName(entries, order == LocalResourceSortOrder.NameAscending, entry => entry.Name, entry => entry.Path)
                .Select(entry => entry.Value).ToArray();
        return ByTime(entries, order == LocalResourceSortOrder.AddedNewest, entry => entry.Time, entry => entry.Name, entry => entry.Path)
            .Select(entry => entry.Value).ToArray();
    }

    public static IReadOnlyList<T> ByTime<T>(IEnumerable<T> source, bool newestFirst, Func<T, DateTime?> timestamp, Func<T, string> name, Func<T, string> path) {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (timestamp is null) throw new ArgumentNullException(nameof(timestamp));
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (path is null) throw new ArgumentNullException(nameof(path));
        // Snapshot each key once. Sorting neither mutates the source nor re-reads metadata during comparisons.
        var entries = source.Select((value, index) => new Entry<T> {
            Value = value, Index = index, Time = timestamp(value), Name = name(value) ?? "", Path = path(value) ?? ""
        }).ToArray();
        IOrderedEnumerable<Entry<T>> knownFirst = entries.OrderBy(entry => !entry.Time.HasValue);
        IOrderedEnumerable<Entry<T>> timed = newestFirst
            ? knownFirst.ThenByDescending(entry => entry.Time)
            : knownFirst.ThenBy(entry => entry.Time);
        return timed.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Index).Select(entry => entry.Value).ToArray();
    }

    /// <summary>File-name order as Explorer and the original PCL show it: numbers by value ("mod2" before "mod10"), case ignored.</summary>
    public static IReadOnlyList<T> ByName<T>(IEnumerable<T> source, bool ascending, Func<T, string> name, Func<T, string> path) {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (path is null) throw new ArgumentNullException(nameof(path));
        var entries = source.Select((value, index) => (Value: value, Index: index, Name: name(value) ?? "", Path: path(value) ?? "")).ToArray();
        var named = ascending
            ? entries.OrderBy(entry => entry.Name, NaturalNameComparer.Instance)
            : entries.OrderByDescending(entry => entry.Name, NaturalNameComparer.Instance);
        return named.ThenBy(entry => entry.Path, StringComparer.Ordinal).ThenBy(entry => entry.Index).Select(entry => entry.Value).ToArray();
    }

    private static Entry<T> Read<T>(T value, int index, Func<T, FileSystemInfo> file) {
        Entry<T> entry = new() { Value = value, Index = index };
        try {
            FileSystemInfo info = file(value);
            if (info is null) return entry;
            entry.Name = info.Name; entry.Path = info.FullName;
            info.Refresh();
            if (info.Exists) {
                DateTime created = info.CreationTimeUtc;
                // The Windows missing-file sentinel can be returned after a concurrent removal.
                if (created > DateTime.FromFileTimeUtc(0)) entry.Time = created;
            }
        } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return entry;
    }

    /// <summary>Compares names with digit runs taken as numbers and everything else case-insensitively in the user's culture.</summary>
    public sealed class NaturalNameComparer : IComparer<string> {
        public static readonly NaturalNameComparer Instance = new();
        public int Compare(string? x, string? y) {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length) {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j])) {
                    int startX = i, startY = j;
                    while (i < x.Length && x[i] == '0') i++;
                    while (j < y.Length && y[j] == '0') j++;
                    int digitsX = i, digitsY = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    int lengthX = i - digitsX, lengthY = j - digitsY;
                    if (lengthX != lengthY) return lengthX.CompareTo(lengthY);
                    int digits = string.CompareOrdinal(x, digitsX, y, digitsY, lengthX);
                    if (digits != 0) return digits;
                    int zeros = (digitsX - startX).CompareTo(digitsY - startY);
                    if (zeros != 0) return zeros;
                    continue;
                }
                int text = string.Compare(x, i, y, j, 1, StringComparison.CurrentCultureIgnoreCase);
                if (text != 0) return text;
                i++; j++;
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    private sealed class Entry<T> {
        public T Value = default!;
        public int Index;
        public DateTime? Time;
        public string Name = "", Path = "";
    }
}
