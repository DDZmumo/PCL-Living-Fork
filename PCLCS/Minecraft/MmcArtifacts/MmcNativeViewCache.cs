using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcArtifactMaterializer {
    private static string NativeSourceSet(PreparedInput[] inputs) => MmcDerivedProofs.ComputeNativeSourceSetFingerprint(
        inputs.Select(x => x.Artifact).ToArray(), inputs.Select(x => new MmcArtifactDigestFact(x.Artifact.Key, x.Artifact.SourceRevision,
            x.Lock.AdoptedSha256, x.Lock.SizeBytes)).ToArray());

    private async Task<ImmutableArray<MmcNativeFileBinding>?> ReuseNativeCacheAsync(MmcNativeViewCache? cache,
        PreparedInput[] sources, string viewKey, MmcInstancePaths paths, MmcRuntimeTargetClaims targetClaims,
        CancellationToken token) {
        if (sources.Length == 0 || cache is null || cache.ViewKey != viewKey || cache.SourceSetFingerprint != NativeSourceSet(sources)) return null;
        if (cache.CompleteFiles.IsDefault || cache.CompleteFiles.Length > limits.MaxNativeEntries) throw new IOException("Trusted native cache record exceeds its output budget.");
        var keys = sources.Select(x => x.Artifact.Key).ToHashSet(StringComparer.Ordinal);
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<(MmcNativeFileFact Fact, string Relative, string Expected, string Target)>(cache.CompleteFiles.Length);
        long bytes = 0;
        foreach (var fact in cache.CompleteFiles) {
            token.ThrowIfCancellationRequested();
            MmcArtifactIo.Relative(fact.RelativePath);
            if (fact.RelativePath.Contains('\\')) throw new IOException("Trusted native paths must use canonical separators.");
            if (!keys.Contains(fact.SourceArtifactKey) || !MmcArtifactIo.Digest(fact.Sha256, 64) || fact.SizeBytes < 0)
                throw new IOException("Trusted native cache record has invalid source or digest facts.");
            bytes = checked(bytes + fact.SizeBytes);
            if (bytes > limits.MaxNativeBytes) throw new IOException("Trusted native cache exceeds its byte budget.");
            string relative = "PCL/MmcRuntime/" + viewKey + "/natives/" + fact.RelativePath;
            string expected = fact.Sha256.ToLowerInvariant();
            if (facts.TryGetValue(relative, out string? old) && old != expected) throw new IOException("Trusted native cache target facts conflict.");
            facts[relative] = expected;
            string target = paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (MmcInstancePaths.Attributes(target) is null) return null;
            jobs.Add((fact, relative, expected, target));
        }
        var bindings = new MmcNativeFileBinding?[jobs.Count];
        await Task.Run(() => MmcInstancePaths.ForEachBounded(jobs, (index, job, ct) => {
            if (MmcInstancePaths.Attributes(job.Target) is null) return;
            var actual = MmcArtifactIo.HashSha256(job.Target, limits.MaxNativeBytes, ct);
            if (actual.Sha256 == job.Expected && actual.Size == job.Fact.SizeBytes)
                bindings[index] = new(job.Fact.SourceArtifactKey, job.Fact.RelativePath, job.Target, actual.Sha256, actual.Size);
        }, token), token).ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<MmcNativeFileBinding>(jobs.Count);
        foreach (var binding in bindings) {
            if (binding is null) return null;
            result.Add(binding);
        }
        foreach (var claim in facts) {
            if (targetClaims.TryGetValue(claim.Key, out string? old) && old != claim.Value) throw new IOException("Trusted native cache collides with another runtime input.");
            targetClaims[claim.Key] = claim.Value;
        }
        return result.ToImmutable();
    }

    private void ValidateNativeTree(ImmutableArray<MmcNativeFileBinding> expected, string viewKey, MmcInstancePaths paths,
        CancellationToken token) {
        string root = paths.ResolveRelative(MmcPathRole.Runtime, viewKey + "/natives");
        if (!MmcInstancePaths.CheckDirectoryRole(root)) return;
        var allowed = expected.Select(x => x.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        long prefixCharacters = 0; int count = 0;
        while (pending.Count != 0) {
            string directory = pending.Pop(); MmcInstancePaths.EnsureNoLinks(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                token.ThrowIfCancellationRequested(); MmcInstancePaths.EnsureNoLinks(entry);
                if (++count > limits.MaxNativeEntries * 2L) throw new IOException("Native runtime tree exceeds its node budget.");
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/'); MmcArtifactIo.Relative(relative);
                prefixCharacters += relative.Length;
                if (prefixCharacters > 32 * 1024 * 1024) throw new IOException("Native runtime tree exceeds its path budget.");
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (!allowed.Contains(entry) || !present.Add(entry))
                    throw new IOException("Native runtime view contains an unrecorded or duplicate file; it was preserved for explicit recovery.");
            }
        }
    }
}
