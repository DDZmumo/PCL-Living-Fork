// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Keep historical quarantine coverage on the current catalog and cleanup backend.
internal static class MmcQuarantineCleanupChecks
{
    private const string World = "minecraft/saves/player-world/level.dat";
    private const string ReplacementWorld = "minecraft/saves/replacement-world/level.dat";

    internal static void Run(FixtureEnvironment environment)
    {
        using var files = new FileTaskScope(environment);
        string root = Directory.CreateDirectory(Path.Combine(files.Root, "quarantine-cleanup")).FullName;
        string game = Directory.CreateDirectory(Path.Combine(root, "minecraft")).FullName;
        files.SelectMinecraft(game);
        string source = OwnedPath(root, MmcLifecycleChecks.ImportOwned(root, game));
        Put(source, World, "quarantined world");
        Directory.CreateDirectory(Path.Combine(source, "minecraft", "empty-player-directory"));
        var original = MmcInstanceInspector.Read(source);
        var backend = new MmcInstanceLifecycleBackend();
        var deleted = backend.DeleteAsync(new(source, original.Definition!.InstanceId,
            original.Definition.Generation, original.PrimarySha256!)).GetAwaiter().GetResult();
        Native.Check(deleted is { Status: MmcInstanceBackendStatus.Succeeded, Quarantined: true,
            CurrentRoot: not null, OperationDirectory: not null } && !Directory.Exists(source),
            "The owned fixture did not enter the historical quarantine: " + deleted.Failure);
        string quarantine = OwnedPath(root, deleted.CurrentRoot!);
        string operation = OwnedPath(root, deleted.OperationDirectory!);
        string adjacent = Path.Combine(game, "versions", "adjacent");
        string shared = Path.Combine(game, "saves", "shared-world");
        Put(adjacent, World, "neighbor world");
        Put(shared, "level.dat", "shared world");
        string[] adjacentBefore = Tree(adjacent), sharedBefore = Tree(shared);
        string[] journalBefore = Tree(operation);
        string[] contentsBefore = Tree(quarantine);

        MmcLifecycleCatalogEntry Catalog() => MmcInstanceLifecycleCatalog.Read(game).Entries
            .Single(entry => string.Equals(Native.Full(entry.OperationDirectory), Native.Full(operation), StringComparison.OrdinalIgnoreCase));
        MmcInstancePurgePreview Preview() => MmcInstanceLifecycleCatalog.PreviewPurgeAsync(Catalog()).GetAwaiter().GetResult();
        MmcInstancePurgeResult Purge(MmcInstancePurgeRequest request, CancellationToken token = default) =>
            backend.PurgeAsync(request, token).GetAwaiter().GetResult();
        void AssertRestorable(string[] expected)
        {
            Native.Check(Tree(quarantine).SequenceEqual(expected) && Tree(operation).SequenceEqual(journalBefore) &&
                !Directory.Exists(source), "Uncommitted cleanup changed files or left a purge record.");
            Native.Check(Catalog() is { State: MmcLifecycleCatalogState.Quarantined, CanRestore: true, CanPurge: true, CanRecover: false },
                "Uncommitted cleanup made the historical quarantine non-restorable.");
        }

        var preview = Preview();
        Native.Check(preview.Entries.Any(entry => entry.RelativePath == World && !entry.Directory && entry.Sha256 is not null) &&
            preview.Entries.Any(entry => entry.RelativePath == "minecraft/empty-player-directory" && entry.Directory),
            "The exact cleanup preview omitted the player world or the empty directory.");
        var approved = new MmcInstancePurgeRequest(preview, preview.SnapshotFingerprint,
            MmcInstancePurgeIntent.PermanentlyDeleteQuarantinedInstance);
        Native.Check(Purge(approved with { ConfirmedPreviewFingerprint = "" }) is
            { Status: MmcInstanceBackendStatus.Rejected, DecisionRecorded: false, Completed: false },
            "Cleanup accepted a preview without confirmation.");
        AssertRestorable(contentsBefore);

        Put(quarantine, World, "world changed after preview");
        string[] changedContents = Tree(quarantine);
        Native.Check(Purge(approved) is { Status: MmcInstanceBackendStatus.Rejected, DecisionRecorded: false, Completed: false },
            "Cleanup accepted a stale preview after a quarantined file changed.");
        AssertRestorable(changedContents);

        preview = Preview();
        approved = approved with { Preview = preview, ConfirmedPreviewFingerprint = preview.SnapshotFingerprint };
        using (var earlyCancellation = new CancellationTokenSource()) {
            bool reachedDecision = false;
            var cancelled = Purge(approved with { BeforeCommitDecision = () => {
                reachedDecision = true;
                earlyCancellation.Cancel();
            } }, earlyCancellation.Token);
            Native.Check(reachedDecision && cancelled is { Status: MmcInstanceBackendStatus.Cancelled, DecisionRecorded: false, Completed: false },
                "Cancellation at the commit boundary did not abort the undecided cleanup.");
        }
        AssertRestorable(changedContents);

        using var lateCancellation = new CancellationTokenSource();
        string[]? replacementBefore = null;
        var completed = Purge(approved with { Checkpoint = (phase, _) => {
            if (phase != MmcInstancePurgeCheckpoint.CommitDecided) return;
            Native.Check(Tree(quarantine).SequenceEqual(changedContents), "Cleanup modified quarantined content before its decision.");
            Put(source, ReplacementWorld, "new source owner world");
            Put(source, "managed.json", """{"id":"replacement"}""");
            replacementBefore = Tree(source);
            lateCancellation.Cancel();
        } }, lateCancellation.Token);
        Native.Check(completed is { Status: MmcInstanceBackendStatus.Succeeded, DecisionRecorded: true, Completed: true,
            RemainingPaths.IsEmpty: true, QuarantineRoot: null } && !Directory.Exists(quarantine),
            "The decided cleanup did not finish after late cancellation: " + completed.Failure);
        Native.Check(Catalog() is { State: MmcLifecycleCatalogState.Purged, CanRestore: false, CanRecover: false, CanPurge: false },
            "The real catalog allowed a second operation on the completed cleanup.");
        Native.Check(replacementBefore is not null && Tree(source).SequenceEqual(replacementBefore) &&
            Tree(adjacent).SequenceEqual(adjacentBefore) && Tree(shared).SequenceEqual(sharedBefore),
            "Cleanup touched a recreated source or another world's bytes.");
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        Console.WriteLine("PASS MMC historical quarantine cleanup: exact preview, no confirmation, stale files, both cancellation boundaries and preserved worlds.");
    }

    private static string OwnedPath(string root, string path)
    {
        string actual = Native.Full(path);
        Native.Check(actual.StartsWith(Native.Full(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "The cleanup fixture escaped its owned root.");
        return actual;
    }

    private static void Put(string root, string relative, string text)
    {
        string target = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, text);
    }

    private static string[] Tree(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" +
            (Directory.Exists(path) ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .ToArray();
}
