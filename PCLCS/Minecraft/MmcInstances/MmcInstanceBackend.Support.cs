using System.Collections.Immutable;
using System.Security.Cryptography;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstanceBackend<TNetFile> where TNetFile : notnull {
    /// <summary>
    /// Publishes small host support inputs into an immutable content-addressed cache. This neither
    /// changes the package definition nor authorizes launch; final launch validation must include
    /// the returned support hashes as well as the prepared instance snapshot.
    /// </summary>
    public static async Task<ImmutableArray<MmcInstanceLaunchSupport>> PrepareLaunchSupportAsync(MmcPreparedInstanceLaunch snapshot,
        IReadOnlyList<MmcInstanceLaunchSupportInput> inputs, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count > 8) throw new IOException("Host support input count budget exceeded.");
        var verifiedDirectories = MmcInstancePaths.CreatePassDirectoryCache();
        List<(string Name, string? Staged, string Relative, string Hash, long Length)> prepared = new(inputs.Count);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase); long total = 0;
        MmcArtifactWorkspace? workspace = null;
        try {
            foreach (var input in inputs) {
                cancellationToken.ThrowIfCancellationRequested();
                if (input.FileName.Length is < 1 or > 128 || input.FileName is "." or ".." ||
                    input.FileName.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_' or '-')) || !names.Add(input.FileName) ||
                    !MmcDurableFiles.IsHash(input.ExpectedSha256) || input.Content.Length is < 1 or > 8 * 1024 * 1024)
                    throw new IOException("Host support name, digest or size is invalid.");
                total = checked(total + input.Content.Length); if (total > 32 * 1024 * 1024) throw new IOException("Host support aggregate byte budget exceeded.");
                // Own the small source snapshot before any asynchronous write can observe mutable caller memory.
                byte[] bytes = input.Content.ToArray(); string digest = Hash(bytes);
                if (!digest.Equals(input.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Host support bytes differ from their reviewed digest.");
                string relative = "PCL/MmcRuntime/" + snapshot.Directories.ViewKey + "/support/" + digest + "/" + input.FileName;
                string target = snapshot.Paths.ResolveRelative(MmcPathRole.Instance, relative, verifiedDirectories);
                string? staged = null;
                if (MmcInstancePaths.Attributes(target) is null) {
                    workspace ??= new MmcArtifactWorkspace(snapshot.Paths);
                    staged = workspace.NewFile();
                    await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true)) {
                        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
                    }
                }
                prepared.Add((input.FileName, staged, relative, digest, bytes.LongLength));
            }
            using var publication = MmcAdmissionGate.Acquire(snapshot.Paths.InstanceRoot, snapshot.Definition.InstanceId, TimeSpan.Zero, cancellationToken);
            ValidatePreparedLaunch(publication, snapshot, cancellationToken);
            var result = ImmutableArray.CreateBuilder<MmcInstanceLaunchSupport>(prepared.Count);
            foreach (var file in prepared) {
                cancellationToken.ThrowIfCancellationRequested();
                string target = snapshot.Paths.ResolveRelative(MmcPathRole.Instance, file.Relative, verifiedDirectories);
                if (MmcInstancePaths.Attributes(target) is null) {
                    if (file.Staged is null)
                        throw new IOException("Content-addressed support bytes disappeared before publication.");
                    AssertSupportFile(file.Staged, file.Hash, file.Length, verifiedDirectories);
                    MmcArtifactIo.Directory(Path.GetDirectoryName(target)!);
                    // CreateNew semantics: an existing third-party target is never silently replaced.
                    try { File.Move(file.Staged, target, false); }
                    catch (IOException) when (MmcInstancePaths.Attributes(target) is not null) { }
                }
                AssertSupportFile(target, file.Hash, file.Length, verifiedDirectories);
                result.Add(new(file.Name, target, file.Hash, file.Length));
            }
            return result.ToImmutable();
        } finally {
            workspace?.Dispose();
        }
    }
    private static void AssertSupportFile(string path, string hash, long size, ConcurrentDictionary<string, byte>? verifiedDirectories = null) {
        MmcInstancePaths.EnsureNoLinks(path, verifiedDirectories);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (input.Length != size)
            throw new IOException("Content-addressed support bytes are damaged; preserve them for explicit cache recovery.");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        if (!Convert.ToHexStringLower(digest).Equals(hash, StringComparison.Ordinal))
            throw new IOException("Content-addressed support bytes are damaged; preserve them for explicit cache recovery.");
    }
}
