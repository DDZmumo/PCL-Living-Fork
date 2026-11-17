using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstancePackageOwnership {
    internal static byte[]? Read(MmcInstancePaths paths, MmcInstanceDefinition definition) {
        if (definition.PackageOwnershipReference is not { } reference) return null;
        byte[] bytes = MmcSavedSourceStore.ReadBounded(paths, reference.ObjectRelativePath, 16 * 1024 * 1024);
        var errors = MmcPackageOwnershipCodec.Validate(bytes, reference, definition.Origin.PackageSha256);
        if (!errors.IsEmpty) throw new IOException("MMC package ownership is damaged or unsupported: " + string.Join("; ", errors.Select(value => value.Code)));
        return bytes;
    }
}
