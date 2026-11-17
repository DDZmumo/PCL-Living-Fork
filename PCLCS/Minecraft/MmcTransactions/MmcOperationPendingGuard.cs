using System.Collections.Immutable;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>Fresh outer-operation checks. An exact owner permit never bypasses a real file transaction or another operation marker.</summary>
public static class MmcOperationPendingGuard {
    private static readonly string[] Markers = ["PCL/MmcImportPending.json", "PCL/MmcLifecyclePending.json", "PCL/MmcLegacyAdoptionPending.json"];
    public static void AssertNoForeignPending(MmcAdmissionLease lease) {
        lease.AssertHeld(); var paths = MmcInstancePaths.Resolve(lease.VersionRoot);
        MmcPendingOperationPermit.AssertRequiredMarkers(lease);
        foreach (string relative in Markers) {
            string path = paths.ResolveRelative(MmcPathRole.Instance, relative);
            if (MmcInstancePaths.Attributes(path) is null) continue;
            if (!MmcPendingOperationPermit.Allows(lease, relative, path)) throw new IOException("实例存在尚未完成的 MMC 外层操作。");
        }
    }
}

internal sealed partial class MmcPendingOperationPermit : IDisposable {
    [ThreadStatic] private static MmcPendingOperationPermit? current;
    private readonly MmcPendingOperationPermit? previous;
    private readonly MmcAdmissionLease lease;
    private readonly ImmutableDictionary<string, string> markers;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private bool disposed;
    private MmcPendingOperationPermit(MmcAdmissionLease lease, ImmutableDictionary<string, string> markers) {
        lease.AssertHeld(); this.lease = lease; this.markers = markers; previous = current; current = this;
    }
    // Only a living exclusive reservation can mint this initial-publication capability. There is
    // deliberately no public factory accepting arbitrary marker paths, hashes or instance IDs.
    internal static MmcPendingOperationPermit ForReservation(MmcAdmissionLease lease, MmcOwnedImportReservation owner) =>
        new(lease, ImmutableDictionary<string, string>.Empty.Add("PCL/MmcImportPending.json", owner.ValidatePendingPermit(lease)));
    private bool Matches(MmcAdmissionLease candidate) => !disposed && Environment.CurrentManagedThreadId == threadId && ReferenceEquals(lease, candidate);
    internal static void AssertRequiredMarkers(MmcAdmissionLease lease) {
        for (var permit = current; permit is not null; permit = permit.previous) if (permit.Matches(lease)) {
            foreach (var marker in permit.markers) {
                string path = MmcInstancePaths.Resolve(lease.VersionRoot).ResolveRelative(MmcPathRole.Instance, marker.Key);
                if (!MarkerMatches(path, marker.Value)) throw new IOException("MMC 外层操作的所有权标记已变化。");
            }
        }
    }
    internal static bool Allows(MmcAdmissionLease lease, string relative, string path) {
        for (var permit = current; permit is not null; permit = permit.previous)
            if (permit.Matches(lease) && permit.markers.TryGetValue(relative, out string? hash) && MarkerMatches(path, hash)) return true;
        return false;
    }
    private static bool MarkerMatches(string path, string hash) {
        MmcInstancePaths.EnsureNoLinks(path);
        if (MmcInstancePaths.Attributes(path) is not FileAttributes attrs || (attrs & FileAttributes.Directory) != 0 || new FileInfo(path).Length > MmcManagementReader.MaximumDocumentBytes) return false;
        return MmcDurableFiles.Observe(path) == hash;
    }
    public void Dispose() {
        if (disposed) return;
        lease.AssertHeld();
        if (Environment.CurrentManagedThreadId != threadId || !ReferenceEquals(current, this)) throw new InvalidOperationException("MMC 操作许可不能跨线程或乱序释放。");
        disposed = true; current = previous;
    }
}
