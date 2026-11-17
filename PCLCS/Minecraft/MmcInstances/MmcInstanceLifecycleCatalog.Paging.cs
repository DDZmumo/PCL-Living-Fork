using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>An opaque, in-memory navigation boundary. It never grants authority to restore or purge an entry.</summary>
public sealed class MmcLifecycleCatalogCursor {
    internal string Root { get; }
    internal string Inventory { get; }
    internal string Boundary { get; }
    internal bool Before { get; }
    internal int PageSize { get; }
    internal long MaximumBytes { get; }
    internal MmcLifecycleCatalogCursor(string root, string inventory, string boundary, bool before, int pageSize, long maximumBytes) {
        Root = root; Inventory = inventory; Boundary = boundary; Before = before; PageSize = pageSize; MaximumBytes = maximumBytes;
    }
}
public sealed record MmcLifecycleCatalogPageRequest(string MinecraftRoot, MmcLifecycleCatalogCursor? Cursor = null, int PageSize = 32,
    long MaximumBytes = 64 * 1024 * 1024);
public sealed record MmcLifecycleCatalogPage(ImmutableArray<MmcLifecycleCatalogEntry> Entries,
    MmcLifecycleCatalogCursor? Previous, MmcLifecycleCatalogCursor? Next, bool NeedsRefresh = false, string? Diagnostic = null);

public static partial class MmcInstanceLifecycleCatalog {
    /// <summary>
    /// Ordinal operation-leaf order, at most PageSize entries and 64 MiB of pinned record/primary bytes per page.
    /// Only a page of candidate names is retained, even though the directory inventory is enumerated in full.
    /// Membership/directory changes invalidate every old cursor. Record contents are always inspected anew;
    /// the original entry fingerprint/identity checks still decide whether an action is allowed.
    /// </summary>
    public static MmcLifecycleCatalogPage ReadPage(MmcLifecycleCatalogPageRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Root(request.MinecraftRoot), store = Path.Combine(root, "PCL", "MmcLifecycle");
        if (request.PageSize is < 1 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(request), "每页记录数必须在 1 到 256 之间。");
        if (request.MaximumBytes is < 1 or > MaximumScanBytes) throw new ArgumentOutOfRangeException(nameof(request), "每页读取预算必须在 1 字节到 64 MiB 之间。");
        var cursor = request.Cursor;
        if (cursor is not null && (!string.Equals(cursor.Root, root, PathComparison) || cursor.PageSize != request.PageSize || cursor.MaximumBytes != request.MaximumBytes ||
            string.IsNullOrEmpty(cursor.Boundary) || Path.GetFileName(cursor.Boundary) != cursor.Boundary || cursor.Boundary is "." or ".."))
            throw new IOException("恢复列表游标不属于当前游戏文件夹或分页范围，请重新打开列表。");
        try {
            var inventory = Inventory(store, cursor, request.PageSize, cancellationToken);
            if (cursor is not null && inventory.Fingerprint != cursor.Inventory) return Changed();
            long remaining = request.MaximumBytes;
            List<MmcLifecycleCatalogEntry> entries = [];
            IEnumerable<string> candidates = cursor?.Before == true ? inventory.Candidates.Reverse() : inventory.Candidates;
            foreach (string leaf in candidates.Take(request.PageSize)) {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = Path.Combine(store, leaf);
                try { entries.Add(Inspect(root, directory, ref remaining, cancellationToken)); }
                catch (CatalogPageBudgetException) {
                    // Do not consume this boundary: a fresh page must retry the unread entry.
                    if (entries.Count == 0) entries.Add(Unknown(root, directory, "单条记录超过恢复列表读取预算。"));
                    break;
                }
                catch (Exception error) when (Expected(error)) { entries.Add(Unknown(root, directory, error.Message)); }
            }
            // Enumeration is not an atomic snapshot. Reject a page changed during inspection,
            // rather than showing a newly indexed row under an old selection.
            if (Inventory(store, null, 0, cancellationToken).Fingerprint != inventory.Fingerprint) return Changed();
            if (entries.Count == 0) return new([], null, null);
            var ordered = entries.OrderBy(entry => Path.GetFileName(entry.OperationDirectory), StringComparer.Ordinal).ToImmutableArray();
            string first = Path.GetFileName(ordered[0].OperationDirectory), last = Path.GetFileName(ordered[^1].OperationDirectory);
            MmcLifecycleCatalogCursor Boundary(string leaf, bool before) => new(root, inventory.Fingerprint, leaf, before, request.PageSize, request.MaximumBytes);
            return new(ordered, StringComparer.Ordinal.Compare(first, inventory.Minimum) > 0 ? Boundary(first, true) : null,
                StringComparer.Ordinal.Compare(last, inventory.Maximum) < 0 ? Boundary(last, false) : null);
        } catch (Exception error) when (Expected(error)) { return new([Unknown(root, store, error.Message)], null, null); }
    }

    private static MmcLifecycleCatalogPage Changed() => new([], null, null, true, "恢复记录目录已变化，请刷新后重新选择。未执行任何实例操作。");

    private sealed record CatalogInventory(string Fingerprint, SortedSet<string> Candidates, string? Minimum, string? Maximum);
    private static CatalogInventory Inventory(string store, MmcLifecycleCatalogCursor? cursor, int pageSize, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        MmcInstancePaths.EnsureNoLinks(store);
        SortedSet<string> candidates = new(StringComparer.Ordinal);
        if (MmcInstancePaths.Attributes(store) is null) return new("missing", candidates, null, null);
        if (!Directory.Exists(store)) throw new IOException("操作记录位置被文件占用。");
        byte[] accumulator = new byte[32]; long count = 0; string? minimum = null, maximum = null;
        // The XOR of leaf-bound SHA256 facts is independent of filesystem enumeration order.
        // This is only a change detector, never a source of action authority.
        using var iterator = Directory.EnumerateFileSystemEntries(store).GetEnumerator();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!iterator.MoveNext()) break;
            string path = iterator.Current, leaf = Path.GetFileName(path);
            string fact;
            try {
                FileAttributes attributes = File.GetAttributes(path);
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                fact = leaf + "\n" + (int)attributes + "\n" + info.CreationTimeUtc.Ticks + "\n" + info.LastWriteTimeUtc.Ticks;
            } catch (Exception error) when (Expected(error)) { fact = leaf + "\nunreadable:" + error.GetType().FullName; }
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fact));
            for (int i = 0; i < accumulator.Length; i++) accumulator[i] ^= hash[i];
            count = checked(count + 1);
            if (minimum is null || StringComparer.Ordinal.Compare(leaf, minimum) < 0) minimum = leaf;
            if (maximum is null || StringComparer.Ordinal.Compare(leaf, maximum) > 0) maximum = leaf;
            if (pageSize == 0 || cursor is not null && (cursor.Before ? StringComparer.Ordinal.Compare(leaf, cursor.Boundary) >= 0 : StringComparer.Ordinal.Compare(leaf, cursor.Boundary) <= 0)) continue;
            candidates.Add(leaf);
            if (candidates.Count > pageSize) candidates.Remove(cursor?.Before == true ? candidates.Min! : candidates.Max!);
        }
        token.ThrowIfCancellationRequested();
        string stamp = Convert.ToHexStringLower(accumulator) + ":" + count + ":" + Directory.GetCreationTimeUtc(store).Ticks;
        return new(stamp, candidates, minimum, maximum);
    }
}
