using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal sealed record MmcCloneFileFact(string RelativePath, string Sha256, long SizeBytes);
internal sealed record MmcCloneInventory(ImmutableArray<MmcCloneFileFact> Files, ImmutableArray<string> Directories, long TotalBytes);
internal sealed record MmcClonePreferences(JObject Config, string Ini, string Fingerprint);

internal static class MmcInstanceCloneSnapshot {
    internal const int MaximumFiles = 200000;
    internal const long MaximumBytes = 512L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AccountFiles = new(["accounts.json", "accounts.cfg", "launcher_accounts.json", "launcher_profiles.json", "multimc.cfg", "prismlauncher.cfg"], StringComparer.OrdinalIgnoreCase);

    internal static MmcClonePreferences Preferences(MmcConfigurationQuiescence quiet, Guid id) {
        var state = quiet.State;
        var context = new MmcConfigurationContextStamp(MmcConfigurationContextKind.Managed, id);
        JObject config = MmcConfigurationCoordinator.ReadConfig(state.Root); string ini = MmcIniConfiguration.ReadIni(state.Root);
        lock (state.Sync) {
            state.AssertContext(context);
            foreach (var pair in state.Patches.Where(pair => pair.Value.Context == context && !MmcConfigurationCoordinator.IsStructuralKey(pair.Key))) {
                if (pair.Value.Remove) config.Remove(pair.Key); else config[pair.Key] = pair.Value.Value?.DeepClone() ?? JValue.CreateNull();
            }
            foreach (var pair in state.IniPatches.Where(pair => pair.Value.Context == context && !MmcIniConfiguration.IsStructural(pair.Key)))
                ini = LegacyBaseOperations.ReplaceIniValue(ini, pair.Key, pair.Value.Value);
        }
        return new(config, ini, Hash(Encoding.UTF8.GetBytes(config.ToString(Formatting.None) + "\n" + ini)));
    }

    internal static MmcCloneInventory Capture(MmcInstancePaths paths, MmcInstanceDefinition definition, MmcInstanceCloneOptions options, CancellationToken token) {
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase); HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        var derived = definition.DerivedOutputs.Select(value => value.TargetRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only instance stores are reachable. Never follow global assets/libraries/Java references.
        foreach (string store in new[] { "PCL/MmcSource", "PCL/MmcOverrides", "PCL/MmcArtifacts", "libraries" }) Walk(store, false);
        foreach (string game in new[] { "minecraft", ".minecraft" }) Walk(game, true);
        foreach (string path in Directory.EnumerateFileSystemEntries(paths.InstanceRoot)) {
            token.ThrowIfCancellationRequested(); string leaf = Path.GetFileName(path);
            if (leaf.Equals("PCL", StringComparison.OrdinalIgnoreCase) || leaf.Equals("libraries", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("minecraft", StringComparison.OrdinalIgnoreCase) || leaf.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals(Path.GetFileName(paths.InstanceRoot) + ".json", StringComparison.OrdinalIgnoreCase) || leaf.Equals(".pclignore", StringComparison.OrdinalIgnoreCase) || AccountFiles.Contains(leaf)) continue;
            Walk(leaf, false);
        }
        if (File.Exists(paths.ResolveRelative(MmcPathRole.Instance, "PCL/Logo.png"))) Add("PCL/Logo.png");
        var measured = ImmutableArray.CreateBuilder<MmcCloneFileFact>(); long total = 0;
        foreach (string relative in files.Keys.Order(StringComparer.Ordinal)) {
            var fact = Measure(paths.ResolveRelative(MmcPathRole.Instance, relative), relative, token);
            total = checked(total + fact.SizeBytes); if (total > MaximumBytes) throw new IOException("MMC clone exceeds the existing 512 GiB copy budget.");
            measured.Add(fact);
        }
        return new(measured.ToImmutable(), directories.Order(StringComparer.Ordinal).ToImmutableArray(), total);

        void Walk(string relative, bool gameTree, int depth = 0) {
            token.ThrowIfCancellationRequested(); if (depth > 64) throw new IOException("MMC clone directory depth exceeds its budget.");
            string target = paths.ResolveRelative(MmcPathRole.Instance, relative); var attributes = MmcInstancePaths.Attributes(target); if (attributes is null) return;
            if (gameTree) {
                string[] parts = relative.Split('/'); string child = parts.Length > 1 ? parts[1] : "";
                if (!options.CopySaves && child.Equals("saves", StringComparison.OrdinalIgnoreCase) || !options.CopyScreenshots && child.Equals("screenshots", StringComparison.OrdinalIgnoreCase) ||
                    !options.CopyServers && parts.Length == 2 && (child.Equals("servers.dat", StringComparison.OrdinalIgnoreCase) || child.Equals("servers.dat_old", StringComparison.OrdinalIgnoreCase)) || parts.Length == 2 && AccountFiles.Contains(child)) return;
            }
            if (derived.Contains(relative)) return; // Rebuild exact owned derived outputs, preserve unknown personal neighbors.
            if ((attributes.Value & FileAttributes.Directory) == 0) { Add(relative); return; }
            if (!directories.Add(relative)) throw new IOException("Duplicate MMC clone directory identity.");
            if (directories.Count > MaximumFiles) throw new IOException("MMC clone directory count exceeds its budget.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(target)) Walk(relative + "/" + Path.GetFileName(entry), gameTree, depth + 1);
        }
        void Add(string relative) {
            string target = paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (!files.TryAdd(relative, target) || files.Count > MaximumFiles) throw new IOException("MMC clone file identity/count is invalid.");
        }
    }

    internal static void Verify(MmcInstancePaths paths, MmcCloneInventory expected, MmcInstanceDefinition definition, MmcInstanceCloneOptions options, CancellationToken token) {
        var actual = Capture(paths, definition, options, token);
        if (!expected.Files.SequenceEqual(actual.Files) || !expected.Directories.SequenceEqual(actual.Directories)) throw new IOException("The clone source changed during copying; the private target was preserved.");
    }
    internal static MmcCloneFileFact Measure(string path, string relative, CancellationToken token) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        if (input.Length > MaximumBytes) throw new IOException("MMC clone file exceeds the existing aggregate copy budget.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[(int)Math.Min(131072L, Math.Max(1L, input.Length))]; long size = 0; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); size = checked(size + count); if (size > MaximumBytes) throw new IOException("Clone input grew beyond its copy budget."); hash.AppendData(buffer, 0, count); }
        return new(relative, Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }
    internal static void Copy(MmcInstancePaths source, MmcInstancePaths target, MmcCloneInventory inventory, CancellationToken token,
        IProgress<MmcInstanceCloneProgress>? progress, Action<MmcInstanceCloneCheckpoint, int>? checkpoint, Action? validateOwnership = null) {
        foreach (string directory in inventory.Directories) { token.ThrowIfCancellationRequested(); validateOwnership?.Invoke(); MmcArtifactIo.Directory(target.ResolveRelative(MmcPathRole.Instance, directory)); }
        int completed = 0; long bytes = 0;
        foreach (var file in inventory.Files) {
            token.ThrowIfCancellationRequested(); validateOwnership?.Invoke(); string inputPath = source.ResolveRelative(MmcPathRole.Instance, file.RelativePath), outputPath = target.ResolveRelative(MmcPathRole.Instance, file.RelativePath);
            MmcArtifactIo.Directory(Path.GetDirectoryName(outputPath)!);
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan))
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan)) {
                byte[] buffer = new byte[(int)Math.Min(131072L, Math.Max(1L, file.SizeBytes))]; long copied = 0; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); copied = checked(copied + count); if (copied > file.SizeBytes) throw new IOException("Clone source grew during copying."); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            if (Measure(outputPath, file.RelativePath, token) != file) throw new IOException("Clone target bytes differ from its current-source snapshot.");
            bytes = checked(bytes + file.SizeBytes); completed++; progress?.Report(new("copying-current-instance", completed, inventory.Files.Length, bytes, inventory.TotalBytes));
            checkpoint?.Invoke(MmcInstanceCloneCheckpoint.FileCopied, completed - 1);
        }
    }
    internal static void VerifyCopied(MmcInstancePaths paths, MmcCloneInventory inventory, CancellationToken token) {
        foreach (var file in inventory.Files) if (Measure(paths.ResolveRelative(MmcPathRole.Instance, file.RelativePath), file.RelativePath, token) != file)
            throw new IOException("A copied clone input changed before final publication.");
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
