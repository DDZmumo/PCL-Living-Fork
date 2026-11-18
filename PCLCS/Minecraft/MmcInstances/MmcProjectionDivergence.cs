using System.Collections.Immutable;
using System.Text;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed record MmcProjectionDivergence(MmcInstanceRepairSelection Selection, string ExpectedProjectionSha256,
    string ObservedProjectionSha256, string ObservedJson, string ExpectedMainClass, ImmutableArray<string> ExpectedJvmArguments,
    ImmutableArray<string> ExpectedGameArguments, ImmutableArray<string> ExpectedClasspath, string BackupRelativePath);

public static partial class MmcInstanceRepairSelections {
    public static MmcInstanceRepairSelection CaptureDivergedProjection(string versionRoot, Guid expectedInstanceId) {
        var selected = CaptureCore(versionRoot, expectedInstanceId, false, true);
        return selected.DivergedProjectionSha256 is not null ? selected : throw new IOException("There is no changed MMC projection to inspect.");
    }
    public static Task<MmcProjectionDivergence> InspectDivergedProjectionAsync(string versionRoot, Guid expectedInstanceId,
        CancellationToken cancellationToken = default) => Task.Run(() => {
        var selected = CaptureDivergedProjection(versionRoot, expectedInstanceId);
        using var lease = MmcAdmissionGate.Acquire(selected.VersionRoot, selected.InstanceId, TimeSpan.Zero, cancellationToken);
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease); Revalidate(lease, selected);
        var paths = MmcInstancePaths.Resolve(selected.VersionRoot);
        var primary = MmcSavedSourceStore.ReadPrimary(paths);
        byte[] bytes = ProjectionBytes(paths);
        if (MmcDurableFiles.Hash(bytes) != selected.DivergedProjectionSha256 || primary.Sha256 != selected.PrimarySha256)
            throw new IOException("Projection or primary changed during inspection.");
        var saved = MmcSavedSourceStore.LoadAsync(paths, primary.Definition, cancellationToken).GetAwaiter().GetResult();
        var compiled = MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution, primary.Definition, []);
        Revalidate(lease, selected);
        return new MmcProjectionDivergence(selected, primary.Definition.InstalledProjection!.OutputJsonSha256,
            selected.DivergedProjectionSha256, new UTF8Encoding(false, true).GetString(bytes), compiled.MainClass,
            compiled.JvmArguments.Select(argument => argument.Value).ToImmutableArray(), compiled.GameArguments.Select(argument => argument.Value).ToImmutableArray(),
            compiled.Classpath.Select(artifact => artifact.Coordinate?.Name ?? artifact.RelativePath).ToImmutableArray(), ProjectionBackupPath(selected.DivergedProjectionSha256));
    }, CancellationToken.None);

    internal static string ProjectionBackupPath(string hash) {
        if (!MmcDurableFiles.IsHash(hash)) throw new IOException("A projection backup needs an exact SHA256.");
        return "PCL/MmcProjectionBackups/" + hash + ".json";
    }
    private static byte[] ProjectionBytes(MmcInstancePaths paths) {
        string relative = Path.GetFileName(paths.InstanceRoot) + ".json";
        byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, relative, MmcManagementReader.MaximumDocumentBytes);
        using var json = MmcManagementReader.ParseJson(new UTF8Encoding(false, true).GetString(bytes));
        if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new IOException("The changed version JSON is not an object.");
        return bytes;
    }
}
