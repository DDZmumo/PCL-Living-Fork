using PCLCS;

// Original LF host regressions; no system volume is modified or reformatted.
internal static class MmcInstancePurgeRevisionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC purge rejects ReFS/network/unknown filesystems and unusable NTFS file identities", LocalNtfsIdentity);
    }
    private static void LocalNtfsIdentity() {
        foreach (string fileSystem in new[] { "ReFS", "FAT32", "" }) Unsupported(() => MmcPurgeHandle.RequireLocalNtfsIdentity(fileSystem, @"C:\owned\file", 1));
        Unsupported(() => MmcPurgeHandle.RequireLocalNtfsIdentity("NTFS", @"\\server\share\file", 1));
        foreach (ulong identity in new[] { 0UL, ulong.MaxValue }) {
            try { MmcPurgeHandle.RequireLocalNtfsIdentity("NTFS", @"C:\owned\file", identity); }
            catch (IOException) { continue; }
            throw new Exception("Unusable NTFS identity was accepted.");
        }
        MmcPurgeHandle.RequireLocalNtfsIdentity("NTFS", @"C:\owned\file", 1);
    }
    private static void Unsupported(Action action) {
        try { action(); } catch (PlatformNotSupportedException) { return; }
        throw new Exception("Unsupported purge filesystem was accepted.");
    }
}
