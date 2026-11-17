using System.Collections.Immutable;
using System.Text.Json;
using Newtonsoft.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class MmcInstancePackageUpdateBackend<TNetFile> where TNetFile : notnull {
    private static string Fingerprint(MmcInstancePackageUpdatePreview preview, CancellationToken token) {
        var plan = preview.Plan;
        var payloads = plan.Package!.Files.Values.Concat(plan.Changes.Concat(plan.PreviewChanges).Where(value => value.Incoming is not null).Select(value => value.Incoming!))
            .Where(value => value.Payload is not null).GroupBy(value => value.Payload!, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.Ordinal);
        var fixedPayloads = new List<MmcDefinitionChangeSourceCopy>();
        foreach (var payload in payloads) {
            token.ThrowIfCancellationRequested(); string path = Path.GetFullPath(payload.Key);
            MmcInstancePackageUpdatePlanning<TNetFile>.Within(preview.Job.Root, path);
            var measured = MmcArtifactIo.HashAsync(path, MmcDurableFiles.MaximumFileBytes, token).GetAwaiter().GetResult();
            fixedPayloads.Add(new(path, payload.First().Relative ?? "", measured.Sha256, measured.Size));
        }
        return MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "PCLLF.MmcZipUpdatePreview/v1",
            preview.Selection, preview.Choices, preview.PackageSha256, preview.Job.Owner, preview.Source,
            preview.Components, preview.RuntimeChanges, FileChoices = MmcInstancePackageUpdatePlanning<TNetFile>.Describe(plan), preview.Warnings, preview.Errors,
            preview.RequiresChoices, preview.ChangesMinecraftVersion, preview.LocalSnapshot, preview.CandidateSnapshot,
            Overrides = preview.Overrides.OrderBy(value => value.Uid, StringComparer.Ordinal),
            Snapshots = preview.Snapshots.OrderBy(value => value.Uid, StringComparer.Ordinal).ThenBy(value => value.Version, StringComparer.Ordinal).ThenBy(value => value.Kind),
            SourceObjects = preview.SourceObjects.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { value.Key, Sha256 = MmcDurableFiles.Hash(value.Value.ToArray()) }),
            SourceCopies = preview.SourceCopies.OrderBy(value => value.TargetRelativePath, StringComparer.Ordinal), preview.LocalInputs,
            Baseline = plan.Baseline?.ToString(Formatting.None), PackageSource = plan.Package.Source?.ToString(Formatting.None), plan.Package.Minecraft,
            Loaders = plan.Package.Loaders.OrderBy(value => value.Key),
            AuthorFiles = plan.Package.Files.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { value.Key, value.Value.Hash, value.Value.Identity,
                value.Value.FileIdentity, value.Value.OptionalFile, value.Value.Urls, value.Value.Payload }),
            GameSnapshot = plan.Snapshot.OrderBy(value => value.Key, StringComparer.Ordinal), ObservedMods = plan.ObservedModFiles.Order(StringComparer.Ordinal),
            plan.AllowUnverifiedDependencies,
            EffectiveChoices = plan.Changes.Concat(plan.PreviewChanges).OrderBy(MmcInstancePackageUpdatePlanning<TNetFile>.ChangeId, StringComparer.Ordinal).Select(value => new {
                Id = MmcInstancePackageUpdatePlanning<TNetFile>.ChangeId(value), value.Relative, value.BaselineRelative, value.LocalRelative, value.Before, value.TargetBefore,
                value.Conflict, value.AllowPreserveAuthor, value.AllowAuthor, value.DisplayOnly, value.Operation, value.Association, value.KeepDisabled, value.PersonalCompatible,
                value.PersonalComponentMatched, value.PreviewOnly, value.DisableAfterUpdate, value.PreviewAuthorRepair, value.DisabledDestination, value.Choice,
                Candidates = value.Candidates.Order(StringComparer.Ordinal), value.Reason
            }), Payloads = fixedPayloads }));
    }
    private static void VerifyPreview(MmcInstancePackageUpdatePreview preview, string expected, CancellationToken token) {
        if (!MmcDurableFiles.IsHash(expected) || preview.PreviewSha256 != expected || Fingerprint(preview, token) != expected)
            throw new IOException("The selected MMC ZIP preview/source/choices changed.");
        preview.Job.VerifyCandidate(preview.CandidateSnapshot, token);
        using var lease = MmcAdmissionGate.Acquire(preview.Selection.VersionRoot, preview.Selection.ExpectedInstanceId, TimeSpan.Zero, token);
        var current = MmcInstanceHistoryStore.AssertCurrent(lease, preview.Selection.ExpectedInstanceId, preview.Selection.ExpectedGeneration, preview.Selection.ExpectedPrimarySha256);
        var paths = MmcInstancePaths.Resolve(preview.Selection.VersionRoot, current.Definition.Layout.GameRootLeaf);
        if (!preview.LocalSnapshot.SequenceEqual(MmcInstancePackageUpdatePlanning<TNetFile>.LocalSnapshot(paths, token)))
            throw new IOException("The complete local update snapshot changed after preview.");
        MmcInstancePackageUpdatePlanning<TNetFile>.Engine(Path.Combine(preview.Job.Root, "preview")).ValidateSnapshot(preview.Plan, token);
        VerifyFixedLocals(preview.LocalInputs, token);
    }
    private static IEnumerable<MmcFileMutation> StageSourceObjects(MmcInstancePaths paths, MmcInstancePackageUpdatePreview preview,
        MmcArtifactWorkspace workspace, CancellationToken token) {
        var io = new MmcArtifactIo(new(), null);
        foreach (var value in preview.SourceObjects) {
            token.ThrowIfCancellationRequested(); string digest = MmcDurableFiles.Hash(value.Value.ToArray());
            string? before = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, value.Key));
            if (before == digest) continue; if (before is not null) throw new IOException("Immutable new-package metadata already has unknown bytes.");
            string staged = workspace.NewFile(); MmcDurableFiles.WriteNew(staged, value.Value.AsSpan());
            yield return new(value.Key, null, null, digest, Relative(paths, staged));
        }
        foreach (var value in preview.SourceCopies) {
            token.ThrowIfCancellationRequested(); string? before = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, value.TargetRelativePath));
            if (before == value.Sha256) continue; if (before is not null) throw new IOException("Immutable new-package author evidence already has unknown bytes.");
            string staged = workspace.NewFile(); io.CopyAsync(value.AbsolutePath, staged, value.TargetRelativePath, value.Sha256, null, value.SizeBytes, token).GetAwaiter().GetResult();
            yield return new(value.TargetRelativePath, null, null, value.Sha256, Relative(paths, staged));
        }
    }
    private static string Relative(MmcInstancePaths paths, string absolute) => Path.GetRelativePath(paths.InstanceRoot, absolute).Replace('\\', '/');
}
