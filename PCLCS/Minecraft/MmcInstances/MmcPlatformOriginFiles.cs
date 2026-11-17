using System.Security.Cryptography;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcPlatformOriginFiles {
    internal static async Task<MmcPlatformPackageMeasurement> MeasureAsync(string originalZip, CancellationToken token) {
        string path = Path.GetFullPath(originalZip); MmcInstancePaths.EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 1 or > MmcPlatformOriginCodec.MaximumPackageBytes) throw new IOException("Original package exceeds its byte budget.");
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512); byte[] buffer = new byte[131072]; long size = 0; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
            size = checked(size + count); if (size > MmcPlatformOriginCodec.MaximumPackageBytes) throw new IOException("Original package grew beyond its byte budget.");
            sha256.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count); sha512.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return new(Convert.ToHexStringLower(sha256.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(sha512.GetHashAndReset()), size);
    }
    internal static MmcPlatformOriginEvidence? ReadSaved(MmcInstancePaths paths, MmcInstanceDefinition definition) {
        if (definition.Origin.PlatformEvidenceReference is not { } reference) return null;
        byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, reference.ObjectRelativePath, MmcPlatformOriginCodec.MaximumEvidenceBytes);
        try { return MmcPlatformOriginCodec.Read(bytes, definition.Origin); }
        catch (ArgumentException error) { throw new InvalidDataException("The fixed platform origin evidence is damaged.", error); }
    }
}
