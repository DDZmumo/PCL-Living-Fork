using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host integration; governed by the repository LICENCE.
public sealed partial class MmcInstancePackageUpdateBackend<TNetFile> where TNetFile : notnull {
    private static MmcInstanceOrigin SelectedPlatformOrigin(MmcInstancePackageUpdateRequest request, MmcInstanceOrigin measured) {
        if (request.VerifiedOrigin is not { } verified) return measured with { PlatformProjectIdentity = null, PlatformEvidenceReference = null };
        if (verified.Instance != new MmcPlatformInstanceSelection(request.VersionRoot, request.ExpectedInstanceId, request.ExpectedGeneration, request.ExpectedPrimarySha256))
            throw new IOException("Verified release evidence cannot be moved to another selected publication with a copied request.");
        var origin = measured with { OriginalName = verified.Release.File.FileName,
            PlatformProjectIdentity = new(verified.Release.Platform.ToString(), verified.Release.ProjectId, verified.Release.VersionId), PlatformEvidenceReference = verified.Reference };
        _ = MmcPlatformOriginCodec.Read(verified.Evidence.ToArray(), origin); return origin;
    }
    private static void VerifyPlatformOrigin(MmcInstancePackageUpdateRequest request, string measuredSha256) {
        if (request.VerifiedOrigin is { }) _ = SelectedPlatformOrigin(request, new(measuredSha256, Path.GetFileName(request.ArchivePath)));
    }
    private static void RecheckPlatformOrigin(MmcInstancePackageUpdatePreview preview, CancellationToken token) {
        if (preview.Selection.VerifiedOrigin is not { } verified) {
            if (preview.Source.Origin.PlatformProjectIdentity is not null || preview.Source.Origin.PlatformEvidenceReference is not null)
                throw new IOException("A local ZIP update cannot retain a previous platform identity.");
            return;
        }
        _ = verified.Catalog.RecheckAsync(verified.Release, token).GetAwaiter().GetResult();
        var actual = MmcPlatformOriginFiles.MeasureAsync(preview.Selection.ArchivePath, token).GetAwaiter().GetResult();
        var evidence = MmcPlatformOriginCodec.Read(verified.Evidence.ToArray(), preview.Source.Origin);
        if (actual != evidence.Package || actual.Sha256 != preview.PackageSha256) throw new IOException("The exact selected online package changed after preview.");
        using var archive = MmcArchiveSourceReader.ReadAsync(preview.Selection.ArchivePath, cancellationToken: token).GetAwaiter().GetResult();
        if (archive.PackageSha256 != actual.Sha256) throw new IOException("The online package changed during container verification.");
    }
}
