using System.Collections.Immutable;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed partial class MmcLegacyAdoptionService<TNetFile> where TNetFile : notnull {
    private static MmcSavedInstanceSources ValidateRuntime(MmcInstancePaths paths, MmcInstanceDefinition definition, CancellationToken token) {
        var observed = MmcManagementReader.Evaluate(MmcManagementProbe.Capture(paths.InstanceRoot) with { HasPendingTransaction = false });
        if (observed.State != MmcManagementState.ManagedMmc || observed.Envelope?.InstanceId != definition.InstanceId || observed.Envelope.Generation != definition.Generation)
            throw new IOException("转换模板的主定义与兼容投影不一致。");
        var saved = MmcSavedSourceStore.LoadAsync(paths, definition, token).GetAwaiter().GetResult();
        var env = definition.InstalledProjection!.RuntimeEnvironment ?? throw new IOException("新管理副本缺少明确的运行环境。");
        var result = MmcProfileCompiler.Compile(saved.Resolved.Resolution.Profile!, env);
        if (result.HasErrors || result.Profile is null || result.Profile.ProfileFingerprint != definition.InstalledProjection.ProfileFingerprint || result.Profile.RuntimePlanFingerprint != definition.InstalledProjection.RuntimePlanFingerprint)
            throw new IOException("保存的原包来源不能重建转换模板。");
        var plan = result.Profile;
        var active = plan.Classpath.Concat(plan.NativeArtifacts).Concat(plan.DownloadOnlyArtifacts).Concat(plan.AuxiliaryArtifacts).GroupBy(artifact => artifact.Key).Select(group => group.First()).ToImmutableArray();
        var declaredLocal = active.Where(artifact => artifact.Scope == MmcArtifactScope.InstanceLocal).Select(artifact => "libraries/" + artifact.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preserved = MmcPackageFilePreservation.CreateLibraryArtifacts(saved.Baseline).Where(artifact => !declaredLocal.Contains("libraries/" + artifact.RelativePath)).ToImmutableArray();
        var request = new MmcArtifactRequest(paths, plan.RuntimePlanFingerprint, active, preserved, MmcPublishedArtifactLocks.Create(definition), MmcArtifactIntent.RepairExisting, plan.Assets);
        using var prepared = new MmcArtifactMaterializer().PrepareAsync(request, cancellationToken: token).GetAwaiter().GetResult();
        if (!prepared.StagedMutations.IsEmpty) throw new IOException("转换模板的锁定运行环境发生变化；不会在转换时重新采用字节。");
        var facts = MmcArtifactBindingAdapter.Create(request, plan, prepared);
        var bound = MmcPlanBinder.Bind(definition, plan, facts.Facts,
            facts.ApplyTo(new(definition.Generation, definition.Generation, MmcBindingIntent.RepairExisting, definition.InstalledProjection.OutputJsonSha256, definition.Generation)),
            definition.ArtifactLocks, facts.DerivedOutputs, facts.AssetIndexes, publishedBaseline: definition, packageBaseline: saved.BaselineInput);
        if (bound.HasErrors || bound.Definition?.InstalledProjection?.BoundFingerprint != definition.InstalledProjection.BoundFingerprint)
            throw new IOException("转换模板不能通过原锁和当前实测字节的同代数验证。");
        return saved;
    }
    private static ImmutableDictionary<string, string> DerivedGame(MmcInstanceDefinition definition, MmcInstancePaths paths) =>
        definition.DerivedOutputs.Where(output => output.Kind == MmcDerivedOutputKind.AssetMappedResource && output.RuntimePlanFingerprint == definition.InstalledProjection!.RuntimePlanFingerprint &&
            output.TargetRelativePath.StartsWith(paths.GameRootLeaf + "/", StringComparison.OrdinalIgnoreCase))
            .ToImmutableDictionary(output => output.TargetRelativePath, output => output.Sha256!, StringComparer.OrdinalIgnoreCase);
    private static void RemoveAuthorPersonalPayload(MmcInstancePaths paths, MmcValidatedPackageBaseline baseline,
        IReadOnlyDictionary<string, string> derived, CancellationToken token) {
        var payload = baseline.Baseline.Files.Where(file => file.TargetRelativePath is not null &&
            (file.Role == PCLLF.MMCC.Core.MmcPackageFileRole.Game && file.TargetRelativePath.StartsWith(paths.GameRootLeaf + "/", StringComparison.OrdinalIgnoreCase) ||
                file.Role == PCLLF.MMCC.Core.MmcPackageFileRole.InstanceExtra)).ToDictionary(file => file.TargetRelativePath!, StringComparer.OrdinalIgnoreCase);
        MmcArtifactIo.Directory(paths.GameRoot);
        foreach (string file in MmcLegacyAdoptionBackup.Files(paths.GameRoot, token)) {
            string relative = Path.GetRelativePath(paths.InstanceRoot, file).Replace('\\', '/'); string? expected = derived.GetValueOrDefault(relative) ?? payload.GetValueOrDefault(relative)?.Sha256;
            if (expected is null || MmcDurableFiles.Observe(file) != expected) throw new IOException("新模板游戏目录含有非模板字节，不能删除或重新采用。");
        }
        foreach (var file in payload.Values) {
            token.ThrowIfCancellationRequested(); string target = paths.ResolveRelative(MmcPathRole.Instance, file.TargetRelativePath!);
            if (MmcDurableFiles.Observe(target) != file.Sha256) throw new IOException("新模板载荷缺失或变化，原实例和备份已保留。");
            if (!derived.ContainsKey(file.TargetRelativePath!)) File.Delete(target);
        }
        // Remove only now-empty game directories in this exact owned private root;
        // no old source directory is ever passed to deletion.
        List<string> directories = []; Stack<string> pending = new(); pending.Push(paths.GameRoot); int count = 0;
        while (pending.Count != 0) {
            string parent = pending.Pop(); MmcInstancePaths.EnsureNoLinks(parent);
            foreach (string path in Directory.EnumerateFileSystemEntries(parent)) {
                MmcInstancePaths.EnsureNoLinks(path); if (++count > MmcLegacyAdoptionSnapshot.MaximumEntries) throw new IOException("模板目录超过预算。");
                if (Directory.Exists(path)) { directories.Add(path); pending.Push(path); }
            }
        }
        foreach (string directory in directories.OrderByDescending(path => path.Length)) {
            MmcInstancePaths.EnsureNoLinks(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
    private static void WriteReady(MmcLegacyAdoptionJournal operation, MmcInstancePaths paths, MmcStoredPrimaryDefinition primary, ImmutableDictionary<string, string> derived) {
        var ready = new MmcLegacyAdoptionReady(1, operation.Intent.OperationId, primary.Definition.InstanceId, primary.Sha256,
            MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/config.json")) ?? throw new IOException("副本配置缺失。"),
            MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/Setup.ini")) ?? throw new IOException("副本 INI 缺失。"), derived);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ready);
        if (bytes.LongLength > 64 * 1024 * 1024) throw new IOException("转换完成载荷清单超过预算。");
        MmcDurableFiles.WriteNew(Path.Combine(operation.DirectoryPath, "ready.json"), bytes);
        MmcDurableFiles.WriteRecord(Path.Combine(operation.DirectoryPath, "ready.record"), new MmcLegacyAdoptionReadySeal(1, operation.Intent.OperationId, MmcDurableFiles.Hash(bytes), bytes.LongLength));
    }
    private static void VerifyReady(MmcLegacyAdoptionJournal operation, MmcInstancePaths paths, CancellationToken token) {
        var seal = MmcDurableFiles.ReadRecord<MmcLegacyAdoptionReadySeal>(Path.Combine(operation.DirectoryPath, "ready.record")).Value;
        if (seal.SchemaVersion != 1 || seal.OperationId != operation.Intent.OperationId || !MmcDurableFiles.IsHash(seal.Sha256) || seal.SizeBytes is < 1 or > 64 * 1024 * 1024)
            throw new IOException("转换载荷封存记录不支持或损坏。");
        string file = Path.Combine(operation.DirectoryPath, "ready.json"); MmcInstancePaths.EnsureNoLinks(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != seal.SizeBytes) throw new IOException("转换载荷清单长度变化。");
        byte[] bytes = new byte[checked((int)seal.SizeBytes)]; input.ReadExactly(bytes);
        if (MmcDurableFiles.Hash(bytes) != seal.Sha256) throw new IOException("转换载荷清单摘要变化。");
        var ready = JsonSerializer.Deserialize<MmcLegacyAdoptionReady>(bytes) ?? throw new IOException("转换载荷清单缺失。");
        var primary = operation.RequirePrimary(paths.InstanceRoot);
        if (ready.SchemaVersion != 1 || ready.OperationId != operation.Intent.OperationId || ready.InstanceId != primary.Definition.InstanceId || ready.PrimarySha256 != primary.Sha256 ||
            ready.DerivedGameFiles is null || ready.DerivedGameFiles.Count > MmcLegacyAdoptionSnapshot.MaximumEntries ||
            MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/config.json")) != ready.ConfigSha256 ||
            MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/Setup.ini")) != ready.IniSha256)
            throw new IOException("转换实例配置或身份在发布前变化。");
        var actualDerived = DerivedGame(primary.Definition, paths);
        if (ready.DerivedGameFiles.Count != actualDerived.Count || ready.DerivedGameFiles.Any(pair => actualDerived.GetValueOrDefault(pair.Key) != pair.Value)) throw new IOException("转换派生资源清单不属于当前定义。");
        var inventory = MmcLegacyAdoptionBackup.ReadInventory(operation.DirectoryPath, operation.Backup());
        if (inventory.Fingerprint != operation.Intent.SourceFingerprint || inventory.SourceRoot != operation.Intent.SourceRoot || inventory.GameRoot != operation.Intent.GameRoot)
            throw new IOException("转换个人快照不属于原来的确认。");
        MmcLegacyAdoptionBackup.VerifyPersonalFiles(inventory, paths, actualDerived, token);
    }
}
