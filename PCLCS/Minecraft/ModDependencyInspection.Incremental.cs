// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCLCS;

public sealed partial class ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile> {
    public static partial class Index {
        private sealed class InventoryChangedException : IOException { }
        private sealed class ArchiveStamp {
            public string Path;
            public long Length, LastWrite, Created;
            public int Reparse;
            public bool SameContent(ArchiveStamp other) => other is not null && Reparse == 0 && other.Reparse == 0 &&
                Length == other.Length && LastWrite == other.LastWrite && Created == other.Created;
        }
        private sealed class ArchiveSnapshot {
            public ArchiveStamp File;
            public List<TMod> Mods;
            public List<string> Notices;
            public bool MetadataIncomplete;
            // Disabled-only scans may intentionally suppress parse diagnostics. Only a
            // complete enabled scan proves that changing state can reuse those results.
            public bool CompleteEnabledMetadata;
        }
        private static string EnabledPath(string path) {
            if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return path[..^9];
            if (path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return path[..^4];
            return path;
        }

        private static void RelinkArchives(Document saved, TReport replacement) {
            if (saved.Archives is null) return;
            var previous = saved.Tree.Mods;
            if (previous.Count != replacement.Mods.Count ||
                !previous.Zip(replacement.Mods, (left, right) => left.Key == right.Key).All(value => value)) {
                saved.Archives = null;
                return;
            }
            try {
                var positions = previous.Select((mod, index) => (mod, index)).ToDictionary(value => value.mod, value => value.index);
                foreach (var archive in saved.Archives)
                    archive.Mods = archive.Mods.Select(mod => replacement.Mods[positions[mod]]).ToList();
            } catch (Exception error) when (error is ArgumentException || error is KeyNotFoundException || error is NullReferenceException) {
                saved.Archives = null;
            }
        }

        private static bool CanReuse(Document saved, string root, TContext context) {
            if (saved?.Archives is null) return false;
            try {
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nodes = new HashSet<TMod>(saved.Tree.Mods);
                int count = 0;
                foreach (var archive in saved.Archives) {
                    if (archive?.File?.Path is null || archive.Mods is null || archive.Notices is null || archive.Notices.Any(value => value is null) ||
                        !files.Add(FilePathKey(archive.File.Path))) return false;
                    foreach (var mod in archive.Mods) {
                        if (mod is null || !nodes.Remove(mod) || !SameFilePath(mod.RootPath, archive.File.Path)) return false;
                        count++;
                    }
                }
                return count == saved.Tree.Mods.Count && nodes.Count == 0 &&
                    Valid(saved, root, ParseScope(context), new Inventory { Signature = saved.Fingerprint, Files = files });
            } catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is NullReferenceException) {
                return false;
            }
        }

        private static TReport ScanIncremental(string root, TContext context, CancellationToken token, bool force,
            Inventory before, Document saved, out List<ArchiveSnapshot> archives) {
            var previous = !force && CanReuse(saved, root, context) ? saved.Archives : new List<ArchiveSnapshot>();
            var byPath = previous.ToDictionary(value => FilePathKey(value.File.Path), StringComparer.OrdinalIgnoreCase);
            var renamed = previous.Where(value => !before.Files.Contains(FilePathKey(value.File.Path)))
                .GroupBy(value => EnabledPath(FilePathKey(value.File.Path)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            var unambiguous = before.Files.GroupBy(EnabledPath, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var consumed = new HashSet<ArchiveSnapshot>();
            var snapshots = new List<ArchiveSnapshot>();
            int read = 0, reused = 0;
            var tree = ScanRawCore(root, context, token, force, path => {
                token.ThrowIfCancellationRequested();
                if (!before.Stamps.TryGetValue(FilePathKey(path), out var stamp)) throw new InventoryChangedException();
                byPath.TryGetValue(stamp.Path, out var prior);
                if (prior is null && unambiguous.Contains(EnabledPath(stamp.Path)) &&
                    renamed.TryGetValue(EnabledPath(stamp.Path), out var candidates) && candidates.Length == 1)
                    prior = candidates[0];
                bool changedState = prior is not null && ArchiveEnabled(prior.File.Path) != ArchiveEnabled(stamp.Path);
                TReport metadata;
                bool completeEnabledMetadata;
                if (prior is not null && !consumed.Contains(prior) && stamp.SameContent(prior.File) && (!changedState || prior.CompleteEnabledMetadata)) {
                    consumed.Add(prior);
                    metadata = new TReport { Context = context, Mods = prior.Mods, Notices = prior.Notices, MetadataIncomplete = prior.MetadataIncomplete };
                    completeEnabledMetadata = prior.CompleteEnabledMetadata;
                    foreach (var mod in metadata.Mods) { mod.RootPath = stamp.Path; mod.Enabled = ArchiveEnabled(stamp.Path); }
                    if (!string.Equals(prior.File.Path, stamp.Path, StringComparison.Ordinal))
                        metadata.Notices = metadata.Notices.Select(value => value.Replace(Path.GetFileName(prior.File.Path), Path.GetFileName(stamp.Path))).ToList();
                    reused++;
                } else {
                    metadata = ReadFileMetadata(path, context, token, force);
                    completeEnabledMetadata = ArchiveEnabled(path) && !metadata.MetadataIncomplete && !metadata.IndexReadFailed;
                    read++;
                }
                snapshots.Add(new ArchiveSnapshot { File = stamp, Mods = metadata.Mods, Notices = metadata.Notices,
                    MetadataIncomplete = metadata.MetadataIncomplete, CompleteEnabledMetadata = completeEnabledMetadata });
                return metadata;
            });
            // ScanRawCore builds a new graph, including nested-provider selection, and discards
            // stale online path mappings exactly as a fresh scan would after inventory changes.
            tree.IndexArchivesRead = read;
            tree.IndexArchivesReused = reused;
            archives = snapshots;
            return tree;
        }
    }
}
