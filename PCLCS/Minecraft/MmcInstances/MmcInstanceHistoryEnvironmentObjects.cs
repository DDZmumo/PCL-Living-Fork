namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstanceHistoryEnvironmentObjects {
    internal static void AssertMappedHealthy(MmcInstancePaths paths, PCLLF.MMCC.Core.MmcInstanceDefinition definition) {
        foreach (var value in definition.DerivedOutputs.Where(value => value.Kind == PCLLF.MMCC.Core.MmcDerivedOutputKind.AssetMappedResource &&
            value.RuntimePlanFingerprint == definition.InstalledProjection!.RuntimePlanFingerprint)) {
            string? actual = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, value.TargetRelativePath));
            if (actual is not null && actual != value.Sha256) throw new IOException("An edited mapped asset cannot be adopted as ordinary history environment data.");
        }
    }
    internal static string Relative(string sha256) {
        if (!MmcDurableFiles.IsHash(sha256)) throw new IOException("History environment object digest is invalid.");
        return "PCL/MmcHistory/objects/sha256/" + sha256;
    }
    internal static string Resolve(MmcInstancePaths paths, MmcHistoryFile file) => paths.ResolveRelative(MmcPathRole.Instance, Relative(file.Sha256));
    internal static void Capture(MmcInstancePaths paths, IEnumerable<MmcHistoryFile> files, CancellationToken token) {
        using var workspace = new MmcArtifactWorkspace(paths); var io = new MmcArtifactIo(new(), null);
        HashSet<string> copied = new(StringComparer.Ordinal);
        foreach (var file in files) {
            token.ThrowIfCancellationRequested(); if (!copied.Add(file.Sha256)) continue;
            string target = Resolve(paths, file); var attributes = MmcInstancePaths.Attributes(target);
            if (attributes is null) {
                string staged = workspace.NewFile();
                io.CopyAsync(paths.ResolveRelative(MmcPathRole.Instance, file.RelativePath), staged, file.RelativePath, file.Sha256, null, file.SizeBytes, token).GetAwaiter().GetResult();
                MmcArtifactIo.Directory(Path.GetDirectoryName(target)!);
                try { File.Move(staged, target, false); } catch (IOException) when (File.Exists(target)) { /* Verify the immutable raced object below. */ }
            }
            Verify(paths, file, token); // Existing unknown objects are never overwritten or adopted.
        }
    }
    internal static void Verify(MmcInstancePaths paths, MmcHistoryFile file, CancellationToken token) {
        if (file.SizeBytes < 0 || file.SizeBytes > MmcDurableFiles.MaximumFileBytes) throw new IOException("History environment object size is invalid.");
        var measured = MmcArtifactIo.HashAsync(Resolve(paths, file), MmcDurableFiles.MaximumFileBytes, token).GetAwaiter().GetResult();
        if (measured.Sha256 != file.Sha256 || measured.Size != file.SizeBytes) throw new IOException("History environment object is missing or damaged.");
    }
}
