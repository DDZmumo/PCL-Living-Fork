using PCLCS;
using PCLLF.MMCC.Core;

internal static partial class MmcInstanceBackendTests {
    internal static bool TryRunPrivatePayloadImportProbe(string[] args, out int exitCode) {
        exitCode = 0;
        if (args.Length != 1) return false;
        bool probe = args[0] == "--private-payload-import-probe";
        bool launchChecks = args[0] == "--mmc-launch-validation-checks";
        if (!probe && !launchChecks && args[0] != "--private-payload-path-checks") return false;
        try {
            if (probe) {
                BulkPayloadImport();
                Console.WriteLine("PASS explicit private payload import probe (one original 2003-file import case).");
            } else if (launchChecks) {
                (string Name, Action Run)[] focused = [
                    ("MMC parallel prepared launch validation reuses valid support and unwraps tail-file tamper as IOException", ParallelLaunchValidationDetectsTailCorruption),
                    ("MMC final launch validation rechecks replaced ancestor junctions even after a clean validation pass", LaunchValidationRejectsReplacedAncestorJunction),
                    ("MMC preloaded launch sources, baseline disk tamper detection, and preserved target repair stay strict", PreloadedSourcesAndPreservedRepairInvariants)
                ];
                foreach (var test in focused) { test.Run(); Console.WriteLine("PASS " + test.Name); }
                Console.WriteLine($"RESULT focused MMC launch/repair validation checks: {focused.Length}/{focused.Length} passed.");
            } else {
                (string Name, Action Run)[] existing = [
                    ("original private payload complete/cancel/conflict", PrivatePayload),
                    ("original initial cancellation/post-decision corruption", BulkPayloadFailure),
                    ("original post-decision cancellation completes publication", BulkPayloadPostDecisionCancel)
                ];
                int passed = 0, unavailable = 0;
                foreach (var test in PrivatePayloadPathCases().Concat(existing)) {
                    try {
                        test.Run(); passed++; Console.WriteLine("PASS " + test.Name);
                    } catch (PayloadLinkFixtureUnavailableException error) {
                        unavailable++; Console.WriteLine("UNAVAILABLE " + test.Name + ": " + error.Message);
                    }
                }
                Console.WriteLine($"RESULT selected private payload path checks: {passed} passed, {unavailable} unavailable, {passed + unavailable} total; unavailable cases are not passes. The separate 2003-file probe was not repeated.");
            }
        } catch (Exception error) {
            Console.Error.WriteLine("FAIL " + args[0] + ": " + error);
            exitCode = 1;
        }
        return true;
    }

    private static IEnumerable<(string Name, Action Run)> PrivatePayloadPathCases() {
        foreach (string mode in new[] { "source-link", "source-leaf-junction", "source-parent-link", "target-parent-link", "target-claimed", "source-changed", "existing-source-link", "existing-source-leaf-junction" }) {
            string attack = mode;
            yield return ("MMC private payload refuses " + attack + " without adopting unknown bytes", () => PrivatePayloadPathInterferenceAsync(attack).GetAwaiter().GetResult());
        }
    }

    private sealed class PayloadLinkFixtureUnavailableException : Exception {
        internal PayloadLinkFixtureUnavailableException(Exception inner)
            : base("File-symlink construction is unavailable (privilege/access denied); the production link gate was not exercised by this case.", inner) { }
    }

    private static async Task PrivatePayloadPathInterferenceAsync(string mode) {
        using Fixture fixture = new();
        using var source = await MmcArchiveSourceReader.ReadAsync(fixture.Archive);
        using var reservation = MmcOwnedImportReservation.Create(fixture.VersionRoot, Guid.NewGuid(), source.PackageSha256);
        var paths = MmcInstancePaths.Resolve(reservation.TemporaryRoot, source.Package.GameRootLeaf);
        using var prepared = await MmcPackageFileStager.PrepareInitialAsync(source, paths);
        byte[] baselineBytes = MmcPackageFileBaselineCodec.Serialize(Baseline(source, prepared));
        var resolved = MmcComponentResolver.Resolve(source.Package, []);
        using var writer = MmcSourceBundleWriter.Prepare(source, new(resolved, []), paths);
        var reference = writer.StageAuthorBaseline(baselineBytes, prepared.Files.Length);
        var proof = MmcPackageFileBaselineCodec.Read(new(baselineBytes), reference, source.PackageSha256, source.Package.RelativeRoot);
        if (proof.HasErrors || proof.Baseline is null) throw new Exception("Private payload interference baseline is invalid.");

        var first = prepared.InitialGameMutations.OrderBy(value => value.RelativePath, StringComparer.Ordinal).First();
        string input = paths.ResolveRelative(MmcPathRole.Instance, first.NewSourceRelativePath!);
        string target = paths.ResolveRelative(MmcPathRole.Instance, first.RelativePath);
        string outside = Path.Combine(fixture.Scope, "outside"); Directory.CreateDirectory(outside);
        string unknown = Path.Combine(outside, "unknown.bin"); File.WriteAllText(unknown, "unknown bytes to preserve");
        string manifest = Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPayload.json");
        string owner = Path.Combine(prepared.StageRoot, "owner.txt"); byte[] ownerBytes = File.ReadAllBytes(owner);
        string originalBody = File.ReadAllText(input);
        string backup = Path.Combine(fixture.Scope, "parked-source");
        string? link = null; bool directoryLink = false, injected = false, beforeMove = false;
        bool existingSourceLink = mode.StartsWith("existing-source-", StringComparison.Ordinal);
        byte[]? changedInput = null; string? outsideBefore = null;
        string OutsideSnapshot() => string.Join("|", Directory.EnumerateFiles(outside).Order(StringComparer.Ordinal)
            .Select(file => Path.GetFileName(file) + ":" + MmcDurableFiles.Observe(file)));

        void Inject() {
            if (injected) throw new Exception("The controlled payload attack ran twice.");
            injected = true;
            switch (mode) {
                case "source-link":
                case "existing-source-link":
                    File.Move(input, backup);
                    // Matching author bytes ensure a missing link check cannot be hidden by a hash failure.
                    string linkedFile = Path.Combine(outside, "author-matching.bin"); File.WriteAllText(linkedFile, originalBody);
                    try { File.CreateSymbolicLink(input, linkedFile); }
                    catch (IOException error) when ((error.HResult & 0xffff) == 1314) { throw new PayloadLinkFixtureUnavailableException(error); }
                    catch (UnauthorizedAccessException error) { throw new PayloadLinkFixtureUnavailableException(error); }
                    link = input;
                    break;
                case "source-leaf-junction":
                case "existing-source-leaf-junction":
                    File.Move(input, backup);
                    // This is a real leaf reparse point without Windows symlink privilege. The
                    // exact rejection message below must come from EnsureNoLinks, before File.Open
                    // could instead fail because the replaced leaf is a directory.
                    MmcInstancePathsTests.CreateDirectoryLink(input, outside); link = input; directoryLink = true;
                    break;
                case "source-parent-link":
                    string inputParent = Path.GetDirectoryName(input)!;
                    Directory.Move(inputParent, backup);
                    File.WriteAllText(Path.Combine(outside, Path.GetFileName(input)), originalBody);
                    MmcInstancePathsTests.CreateDirectoryLink(inputParent, outside); link = inputParent; directoryLink = true;
                    break;
                case "target-parent-link":
                    string targetParent = Path.GetDirectoryName(target)!;
                    Directory.CreateDirectory(targetParent);
                    // The destination leaf is absent outside: Attributes(target) cannot mask a
                    // missing parent-link check merely by reporting an occupied target.
                    Equal(false, File.Exists(Path.Combine(outside, Path.GetFileName(target))));
                    Equal(0, Directory.GetFileSystemEntries(targetParent).Length);
                    Directory.Delete(targetParent, false);
                    MmcInstancePathsTests.CreateDirectoryLink(targetParent, outside);
                    link = targetParent; directoryLink = true;
                    break;
                case "target-claimed":
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "claimed destination bytes");
                    break;
                case "source-changed":
                    changedInput = Enumerable.Repeat((byte)'x', checked((int)new FileInfo(input).Length)).ToArray();
                    File.WriteAllBytes(input, changedInput);
                    break;
                default: throw new Exception("Unknown controlled payload attack.");
            }
            outsideBefore = OutsideSnapshot();
        }

        try {
            if (existingSourceLink) Inject();
            IOException? rejected = null;
            try {
                reservation.AdoptPreparedGameFiles(prepared, proof.Baseline, checkpoint: (phase, index) => {
                    if (phase != MmcInitialPayloadCheckpoint.BeforeMove || index != 0) return;
                    beforeMove = true;
                    if (!existingSourceLink) Inject();
                });
            } catch (IOException error) { rejected = error; }
            if (rejected is null) throw new Exception("The controlled payload attack was accepted: " + mode);
            Equal(true, injected);
            Equal(false, Directory.Exists(fixture.VersionRoot));
            Equal(true, File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "MmcImportPending.json")));
            Equal(false, File.Exists(Path.Combine(paths.InstanceRoot, "PCL", "InstanceDefinition.json")));
            if (mode.EndsWith("link", StringComparison.Ordinal) || mode.EndsWith("junction", StringComparison.Ordinal)) Equal("MMC 路径包含链接或重解析点。", rejected.Message);
            else if (mode == "source-changed") Equal("Private payload bytes changed after their initial CRC/hash observation.", rejected.Message);
            else Equal("Private payload target was claimed; existing bytes were preserved.", rejected.Message);

            // Even a source linked before adoption reaches the actual read boundary now. Its
            // rejection must retain the already-persisted inventory and every staged owner fact.
            Equal(true, beforeMove); Equal(true, File.Exists(manifest));
            byte[] manifestBytes = File.ReadAllBytes(manifest);
            prepared.Dispose();
            Equal(true, File.Exists(owner)); Equal(true, ownerBytes.SequenceEqual(File.ReadAllBytes(owner)));
            Equal(true, manifestBytes.SequenceEqual(File.ReadAllBytes(manifest)));
            if (mode == "target-claimed") Equal("claimed destination bytes", File.ReadAllText(target));
            else Equal(false, File.Exists(target));
            if (changedInput is not null) Equal(true, changedInput.SequenceEqual(File.ReadAllBytes(input)));
            else if (!directoryLink && link is null) Equal(originalBody, File.ReadAllText(input));
            Equal("unknown bytes to preserve", File.ReadAllText(unknown));
            Equal(outsideBefore, OutsideSnapshot());
            Equal(false, File.Exists(Path.Combine(outside, Path.GetFileName(target))));

            reservation.Dispose();
            var entry = MmcImportRecovery.Read(Path.GetDirectoryName(Path.GetDirectoryName(fixture.VersionRoot)!)!).Entries.Single();
            Equal(MmcImportRecoveryState.OriginalRequired, entry.State); Equal(false, entry.CanRecover);
            var recovery = await MmcImportRecovery.RecoverAsync(entry);
            Equal(MmcInstanceBackendStatus.NeedsRecovery, recovery.Status);
            Equal(false, Directory.Exists(fixture.VersionRoot));
            Equal("unknown bytes to preserve", File.ReadAllText(unknown));
            Equal(outsideBefore, OutsideSnapshot());
            Equal(true, File.Exists(owner));
            Equal(true, manifestBytes.SequenceEqual(File.ReadAllBytes(manifest)));
            if (mode == "target-claimed") Equal("claimed destination bytes", File.ReadAllText(target));
            if (changedInput is not null) Equal(true, changedInput.SequenceEqual(File.ReadAllBytes(input)));
        } finally {
            // Only remove the exact link created by this fixture; never recurse through it.
            if (link is not null) {
                if (directoryLink) Directory.Delete(link, false); else File.Delete(link);
            }
            if (File.Exists(backup)) File.Move(backup, input);
            else if (Directory.Exists(backup)) Directory.Move(backup, Path.GetDirectoryName(input)!);
        }
    }
}
