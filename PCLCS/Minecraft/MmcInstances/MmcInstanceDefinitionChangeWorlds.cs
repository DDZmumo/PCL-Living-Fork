using System.Collections.Immutable;
using System.Security.Cryptography;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
// Used only inside DefinitionChange's synchronous quiescence/admission scope.
internal sealed class MmcDefinitionChangeWorldGuard : IDisposable {
    private sealed record Entry(string Path, bool Directory, string? Sha256 = null, long Size = 0);
    private readonly MmcInstancePaths paths;
    private readonly List<FileStream> pins = [];
    private readonly ImmutableArray<Entry> snapshot;
    private readonly List<string> worlds = [];
    internal MmcDefinitionChangeWorldGuard(MmcInstancePaths paths, CancellationToken token) {
        this.paths = paths;
        try { snapshot = Scan(token, pin: true); }
        catch { Dispose(); throw; }
    }
    internal ImmutableArray<string> Backup(Func<string, CancellationToken, IReadOnlyList<string>>? capture, CancellationToken token) {
        if (worlds.Count == 0) return [];
        if (capture is null) throw new IOException("Changing Minecraft version with existing worlds requires the host's existing WorldBackupEngine adapter.");
        AssertUnchanged(token);
        var result = capture(paths.GameRoot, token).ToImmutableArray();
        if (result.Length != worlds.Count || result.Any(string.IsNullOrWhiteSpace) || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new IOException("Existing world backup did not complete for every current world; the component change was not committed.");
        // The backup engine owns these locks while copying. Pin them only after
        // it returns, and keep them until this transaction finishes or rolls back.
        foreach (string world in worlds) {
            token.ThrowIfCancellationRequested();
            string file = Path.Combine(world, "session.lock"); MmcInstancePaths.EnsureNoLinks(file);
            if (File.Exists(file)) pins.Add(new(file, FileMode.Open, FileAccess.Read, FileShare.None));
        }
        AssertUnchanged(token); return result;
    }
    internal void AssertUnchanged(CancellationToken token) {
        if (!snapshot.SequenceEqual(Scan(token, pin: false)))
            throw new IOException("World files or their namespace changed after the fixed backup; the component change was not committed.");
    }
    private ImmutableArray<Entry> Scan(CancellationToken token, bool pin) {
        string saves = paths.ResolveRelative(MmcPathRole.Game, "saves");
        MmcInstancePaths.EnsureNoLinks(saves);
        if (File.Exists(saves)) throw new IOException("The world saves path is not a directory.");
        var entries = ImmutableArray.CreateBuilder<Entry>(); long total = 0;
        if (!Directory.Exists(saves)) return [];
        entries.Add(new("saves", true));
        foreach (string world in Directory.EnumerateDirectories(saves).Order(StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(world);
            if (Path.GetFileName(world).StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase)) continue;
            string level = Path.Combine(world, "level.dat"); MmcInstancePaths.EnsureNoLinks(level);
            if (!File.Exists(level)) continue;
            if (pin) worlds.Add(world);
            Stack<string> pending = new(); pending.Push(world);
            while (pending.TryPop(out string? directory)) {
                token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(directory);
                entries.Add(new(Relative(directory), true));
                if (entries.Count > MmcInstanceHistoryFiles.MaximumFiles) throw new IOException("World backup verification exceeds its directory budget.");
                foreach (var child in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(value => value.Name, StringComparer.Ordinal)) {
                    token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(child.FullName);
                    if ((child.Attributes & FileAttributes.Directory) != 0) { pending.Push(child.FullName); continue; }
                    if (directory == world && child.Name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) {
                        entries.Add(new(Relative(child.FullName), false, "session-lock")); continue;
                    }
                    FileStream input = new(child.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try {
                        long size = input.Length; total = checked(total + size);
                        if (size > MmcDurableFiles.MaximumFileBytes || total > MmcInstanceHistoryFiles.MaximumBytes)
                            throw new IOException("World backup verification exceeds its byte budget.");
                        string hash = Convert.ToHexStringLower(SHA256.HashData(input)); token.ThrowIfCancellationRequested();
                        if (input.Length != size) throw new IOException("A world file changed during backup verification.");
                        entries.Add(new(Relative(child.FullName), false, hash, size));
                        if (pin) { pins.Add(input); input = null!; }
                    } finally { input?.Dispose(); }
                    if (entries.Count > MmcInstanceHistoryFiles.MaximumFiles) throw new IOException("World backup verification exceeds its file budget.");
                }
            }
        }
        return entries.OrderBy(value => value.Path, StringComparer.Ordinal).ToImmutableArray();
        string Relative(string path) => Path.GetRelativePath(paths.GameRoot, path).Replace('\\', '/');
    }
    public void Dispose() { foreach (var pin in pins) pin.Dispose(); pins.Clear(); }
}
