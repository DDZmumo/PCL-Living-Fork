using System.Collections.Immutable;
using System.Security.Cryptography;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public enum MmcPackageFileRole { Metadata, Game, LocalLibrary, InstanceExtra, ForeignLauncherMetadata }
public sealed record MmcPackageFileObservation(string RelativePath, MmcPackageFileRole Role,
    string Sha256, string Sha1, long SizeBytes, uint Crc32, string? StagedPath,
    string? ArchiveRelativePath = null, string? TargetRelativePath = null);

/// <summary>Fixed original package bytes and staged initial-import data; never writes an existing player file.</summary>
public sealed class MmcPreparedPackageFiles : IDisposable {
    private readonly MmcArtifactWorkspace workspace;
    private bool retainedForRecovery;
    public string StageRoot => workspace.Root;
    public ImmutableArray<MmcPackageFileObservation> Files { get; }
    public ImmutableArray<MmcFileMutation> InitialGameMutations { get; }
    public IReadOnlyDictionary<string, MmcArtifactSource> LocalLibrarySources { get; }

    internal MmcPreparedPackageFiles(MmcArtifactWorkspace workspace, ImmutableArray<MmcPackageFileObservation> files,
        ImmutableArray<MmcFileMutation> mutations, Dictionary<string, MmcArtifactSource> libraries) {
        this.workspace = workspace; Files = files; InitialGameMutations = mutations;
        LocalLibrarySources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, MmcArtifactSource>(libraries);
    }
    internal void RetainForImportRecovery() => retainedForRecovery = true;
    public void Dispose() { if (!retainedForRecovery) workspace.Dispose(); }
}

public static class MmcPackageFileStager {
    /// <summary>Caller must reserve a new owned instance root before preparing an initial import.</summary>
    public static async Task<MmcPreparedPackageFiles> PrepareInitialAsync(MmcArchiveSource source,
        MmcInstancePaths paths, CancellationToken cancellationToken = default,
        IProgress<MmcArtifactProgress>? progress = null) {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(paths);
        if (paths.GameRootLeaf != source.Package.GameRootLeaf) throw new InvalidDataException("Initial package and target game-root layouts differ.");
        MmcArtifactWorkspace workspace = new(paths);
        try {
            var files = ImmutableArray.CreateBuilder<MmcPackageFileObservation>();
            var mutations = ImmutableArray.CreateBuilder<MmcFileMutation>();
            Dictionary<string, MmcArtifactSource> libraries = new(StringComparer.OrdinalIgnoreCase);
            var rawMetadata = source.Metadata.ToDictionary(m => m.RelativePath, StringComparer.OrdinalIgnoreCase);
            MmcArtifactIo io = new(new(), progress);
            foreach (MmcArchiveEntryObservation file in source.ArchiveEntries.Where(entry => !entry.IsDirectory)) {
                cancellationToken.ThrowIfCancellationRequested();
                bool inside = file.Path.StartsWith(source.Package.RelativeRoot, StringComparison.OrdinalIgnoreCase);
                string relative = inside ? file.Path[source.Package.RelativeRoot.Length..] : file.Path;
                var classified = MmcPackageFileBaselineCodec.Classify(source.Package.RelativeRoot, file.Path);
                var role = Enum.Parse<MmcPackageFileRole>(classified.Role.ToString());
                if (inside && rawMetadata.TryGetValue(relative, out var metadata)) {
                    files.Add(new(relative, MmcPackageFileRole.Metadata, metadata.Sha256,
                        Convert.ToHexStringLower(SHA1.HashData(metadata.Bytes.AsSpan())), file.SizeBytes, file.Crc32, null, file.Path));
                    continue;
                }
                string staged = workspace.NewFile();
                await using Stream input = await source.OpenArchiveFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
                var observed = await io.ExtractAsync(input, staged, file.Path, file.SizeBytes, file.Crc32,
                    4L * 1024 * 1024 * 1024, cancellationToken, "package-extract").ConfigureAwait(false);
                files.Add(new(relative, role, observed.Sha256, observed.Sha1, observed.Size, file.Crc32, staged, file.Path, classified.TargetRelativePath));
                if (role == MmcPackageFileRole.LocalLibrary) {
                    libraries.Add(classified.TargetRelativePath!["libraries/".Length..], new(workspace.Root, staged));
                } else if (classified.TargetRelativePath is { } targetRelative) {
                    string target = paths.ResolveRelative(MmcPathRole.Instance, targetRelative);
                    if (MmcInstancePaths.Attributes(target) is not null)
                        throw new IOException("Initial import target already exists; preserving existing player data: " + targetRelative);
                    mutations.Add(new(targetRelative, null, null, observed.Sha256,
                        Path.GetRelativePath(paths.InstanceRoot, staged).Replace('\\', '/')));
                }
            }
            return new(workspace, files.ToImmutable(), mutations.ToImmutable(), libraries);
        } catch { workspace.Dispose(); throw; }
    }

}
