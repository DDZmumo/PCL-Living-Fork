using System.Text;
using PCLCS;

internal static class MmcManagementTests {
    private const string Id = "72d2c174-4c70-49c3-850d-c75e836586c5";
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC management empty evidence preserves legacy ownership", Empty);
        yield return ("MMC management supported envelopes agree on identity and generation", Valid);
        yield return ("MMC management pending recovery precedes malformed and unsupported definitions", PendingFirst);
        yield return ("MMC management residual hints and source records block legacy fallback", ResidualEvidence);
        yield return ("MMC management malformed existing definitions never become legacy", BrokenDefinitions);
        yield return ("MMC management future kind schema profile and layout are unsupported", FutureFormats);
        yield return ("MMC management projection identity and generation mismatches require repair", ProjectionMismatch);
        yield return ("MMC management missing projections require repair", MissingProjections);
        yield return ("MMC management duplicates invalid identity and unsafe layout are rejected", InvalidEnvelopes);
        yield return ("MMC management package markers cannot adopt an installed identity", PackageIdentity);
        yield return ("MMC management fingerprint binds exact evidence and observation state", Fingerprints);
        yield return ("MMC management input budget rejects oversized envelopes", Budget);
        yield return ("MMC management disk probe is read only and ignores loose pack metadata", ReadOnlyProbe);
        yield return ("MMC management disk probe distinguishes damaged ordinary config from MMC evidence", BrokenOrdinaryConfig);
        yield return ("MMC management disk probe retains ownership after definition deletion", DeletedDefinition);
        yield return ("MMC management disk probe observes pending entries and orphan sources", PendingAndSourceProbe);
        yield return ("MMC management reserved override artifact and runtime remnants prevent legacy fallback", ReservedStoreRemnants);
        yield return ("MMC management private import payload remnants remain positive ownership evidence", PayloadRemnant);
        yield return ("MMC empty transaction stores cannot clear import or lifecycle pending ownership", OuterPending);
        yield return ("MMC management disk probe duplicate hints remain positive ownership evidence", DuplicateHints);
        yield return ("MMC management disk probe rejects malformed UTF8 reserved files", InvalidUtf8);
    }

    private static string Hint(long generation = 1, string id = Id) =>
        $$"""{"schemaVersion":1,"managementKind":"Mmc","instanceId":"{{id}}","generation":{{generation}}} """;
    private static string Definition(long generation = 1) =>
        Hint(generation).TrimEnd().TrimEnd('}') + ",\"formatProfile\":\"prism-v1\",\"layout\":{\"gameRootLeaf\":\"minecraft\",\"layoutVersion\":1}}";
    private static MmcManagementSnapshot Snapshot() => new(new(true, Definition()), new(true, Hint()), new(true, Hint()));
    private static MmcManagementResult Evaluate(MmcManagementSnapshot snapshot) => MmcManagementReader.Evaluate(snapshot);
    private static void Empty() {
        MmcManagementSnapshot snapshot = new(MmcManagementDocument.Missing, MmcManagementDocument.Missing, MmcManagementDocument.Missing);
        Equal(MmcManagementState.Legacy, Evaluate(snapshot).State);
        Equal(true, Evaluate(snapshot).CanUseLegacyBackend);
        Equal(false, Evaluate(snapshot with { ObservationComplete = false }).CanUseLegacyBackend);
    }
    private static void Valid() {
        MmcManagementResult result = Evaluate(Snapshot());
        Equal(MmcManagementState.ManagedMmc, result.State);
        Equal(Id, result.Envelope!.InstanceId.ToString("D"));
        Equal("minecraft", result.Envelope.GameRootLeaf);
        Equal(false, result.CanUseLegacyBackend);
        Equal(0, result.Diagnostics.Count);
    }
    private static void PendingFirst() {
        foreach (string text in new[] { "{", Definition().Replace("\"schemaVersion\":1", "\"schemaVersion\":99") })
            Equal(MmcManagementState.PendingTransaction, Evaluate(Snapshot() with { Definition = new(true, text), HasPendingTransaction = true }).State);
    }
    private static void ResidualEvidence() {
        Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(Snapshot() with { Definition = MmcManagementDocument.Missing }).State);
        MmcManagementSnapshot empty = new(MmcManagementDocument.Missing, MmcManagementDocument.Missing, MmcManagementDocument.Missing);
        Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(empty with { HasSourceRecords = true }).State);
    }
    private static void BrokenDefinitions() {
        foreach (var document in new[] { new MmcManagementDocument(true, "{"), new(true, "[]"), new(true), new(true, Error: "unreadable") }) {
            var result = Evaluate(Snapshot() with { Definition = document });
            Equal(MmcManagementState.ManagedMmcRepairRequired, result.State); Equal(false, result.CanUseLegacyBackend);
        }
    }
    private static void FutureFormats() {
        foreach (string changed in new[] {
            Definition().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            Definition().Replace("\"Mmc\"", "\"FutureKind\""),
            Definition().Replace("prism-v1", "prism-v2"),
            Definition().Replace("\"layoutVersion\":1", "\"layoutVersion\":2")
        }) Equal(MmcManagementState.ManagedUnsupported, Evaluate(Snapshot() with { Definition = new(true, changed) }).State);
        Equal(MmcManagementState.ManagedUnsupported, Evaluate(Snapshot() with { VersionHint = new(true, Hint().Replace("\"Mmc\"", "\"Future\"")) }).State);
    }
    private static void ProjectionMismatch() {
        foreach (string hint in new[] { Hint(2), Hint(id: "12d2c174-4c70-49c3-850d-c75e836586c5") }) {
            Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(Snapshot() with { ConfigHint = new(true, hint) }).State);
            Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(Snapshot() with { VersionHint = new(true, hint) }).State);
        }
    }
    private static void MissingProjections() {
        Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(Snapshot() with { ConfigHint = MmcManagementDocument.Missing }).State);
        Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(Snapshot() with { VersionHint = MmcManagementDocument.Missing }).State);
    }
    private static void InvalidEnvelopes() {
        foreach (string changed in new[] {
            Definition().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            Definition().Replace(Id, Guid.Empty.ToString("D")), Definition().Replace(Id, Guid.NewGuid().ToString("N")),
            Definition().Replace("\"generation\":1", "\"generation\":0"),
            Definition().Replace("\"generation\":1", "\"generation\":\"1\""),
            Definition().Replace("\"gameRootLeaf\":\"minecraft\"", "\"gameRootLeaf\":\"../other\""),
            Definition().Replace("\"managementKind\"", "\"ManagementKind\"")
        }) Equal(MmcManagementState.ManagedMmcRepairRequired, Evaluate(Snapshot() with { Definition = new(true, changed) }).State);
    }
    private static void PackageIdentity() => Equal(MmcManagementState.ManagedMmcRepairRequired,
        Evaluate(Snapshot() with { Origin = MmcManagementEvidenceOrigin.PackageContent }).State);
    private static void Fingerprints() {
        string expected = Evaluate(Snapshot()).EvidenceFingerprint;
        Equal(expected, Evaluate(Snapshot()).EvidenceFingerprint);
        foreach (var changed in new[] {
            Snapshot() with { Definition = new(true, Definition(2)) },
            Snapshot() with { HasPendingTransaction = true }, Snapshot() with { HasSourceRecords = true },
            Snapshot() with { ObservationComplete = false },
            Snapshot() with { Definition = new(true, Definition(), "") }
        }) Equal(false, expected == Evaluate(changed).EvidenceFingerprint);
    }
    private static void Budget() => Equal(MmcManagementState.ManagedMmcRepairRequired,
        Evaluate(Snapshot() with { Definition = new(true, new string(' ', MmcManagementReader.MaximumDocumentBytes + 1)) }).State);
    private static void ReservedStoreRemnants() {
        foreach (string store in new[] { "MmcSource", "MmcOverrides", "MmcArtifacts", "MmcRuntime", "MmcTransactionHistory", "MmcSessions", "MmcSessionHistory", "MmcLifecycle", "MmcHistory", "MmcDefinitionChanges", "MmcPackageUpdateReceipts", "MmcProjectionBackups" }) {
            using Fixture fixture = new();
            string path = Path.Combine(fixture.Pcl, store);
            Directory.CreateDirectory(path);
            MmcManagementResult result = MmcManagementProbe.Read(fixture.Root);
            Equal(MmcManagementState.ManagedMmcRepairRequired, result.State);
            Equal(false, result.CanUseLegacyBackend);
            Directory.Delete(path);
            File.WriteAllText(path, "wrong role");
            result = MmcManagementProbe.Read(fixture.Root);
            Equal(false, result.CanUseLegacyBackend);
            Equal(false, result.ObservationComplete);
        }
    }
    private static void PayloadRemnant() {
        foreach (string receiptName in new[] { "MmcImportPayload.json", "MmcClonePrepared.json" }) {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Pcl);
        string path = Path.Combine(fixture.Pcl, receiptName);
        File.WriteAllText(path, "damaged payload record");
        Equal(false, MmcManagementProbe.Read(fixture.Root).CanUseLegacyBackend);
        File.Delete(path); Directory.CreateDirectory(path);
        var wrongRole = MmcManagementProbe.Read(fixture.Root);
        Equal(false, wrongRole.CanUseLegacyBackend); Equal(false, wrongRole.ObservationComplete);
        }
    }
    private static void OuterPending() {
        foreach (string marker in new[] { "MmcImportPending.json", "MmcLifecyclePending.json" }) {
            using Fixture fixture = new(); fixture.Install();
            Directory.CreateDirectory(Path.Combine(fixture.Pcl, "MmcTransactions"));
            Equal(MmcManagementState.ManagedMmc, MmcManagementProbe.Read(fixture.Root).State);
            File.WriteAllText(Path.Combine(fixture.Pcl, marker), "unknown or interrupted operation");
            var result = MmcManagementProbe.Read(fixture.Root);
            Equal(MmcManagementState.PendingTransaction, result.State); Equal(false, result.CanUseLegacyBackend);
        }
    }
    private static void ReadOnlyProbe() {
        using Fixture fixture = new();
        File.WriteAllText(Path.Combine(fixture.Root, "mmc-pack.json"), "{}");
        Equal(MmcManagementState.Legacy, MmcManagementProbe.Read(fixture.Root).State);
        fixture.Install();
        var before = fixture.Inventory();
        Equal(MmcManagementState.ManagedMmc, MmcManagementProbe.Read(fixture.Root).State);
        Equal(string.Join('|', before), string.Join('|', fixture.Inventory()));
    }
    private static void BrokenOrdinaryConfig() {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Pcl);
        File.WriteAllText(Path.Combine(fixture.Pcl, "config.json"), "{");
        MmcManagementResult result = MmcManagementProbe.Read(fixture.Root);
        Equal(MmcManagementState.Legacy, result.State); Equal(false, result.ObservationComplete); Equal(false, result.CanUseLegacyBackend);
    }
    private static void DeletedDefinition() {
        using Fixture fixture = new(); fixture.Install();
        File.Delete(Path.Combine(fixture.Pcl, "InstanceDefinition.json"));
        Equal(MmcManagementState.ManagedMmcRepairRequired, MmcManagementProbe.Read(fixture.Root).State);
    }
    private static void PendingAndSourceProbe() {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Pcl, "MmcSource"));
        Equal(MmcManagementState.ManagedMmcRepairRequired, MmcManagementProbe.Read(fixture.Root).State);
        Directory.CreateDirectory(Path.Combine(fixture.Pcl, "MmcTransactions", "unrecognized-journal"));
        Equal(MmcManagementState.PendingTransaction, MmcManagementProbe.Read(fixture.Root).State);
    }
    private static void DuplicateHints() {
        using Fixture fixture = new();
        File.WriteAllText(Path.Combine(fixture.Root, "sample.json"), "{\"pcllfManagement\":" + Hint() + ",\"pcllfManagement\":" + Hint() + "}");
        Equal(MmcManagementState.ManagedMmcRepairRequired, MmcManagementProbe.Read(fixture.Root).State);
    }
    private static void InvalidUtf8() {
        using Fixture fixture = new(); fixture.Install();
        File.WriteAllBytes(Path.Combine(fixture.Pcl, "InstanceDefinition.json"), [0xC3, 0x28]);
        Equal(MmcManagementState.ManagedMmcRepairRequired, MmcManagementProbe.Read(fixture.Root).State);
    }
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
    private sealed class Fixture : IDisposable {
        private readonly string scope = Path.Combine(Path.GetTempPath(), "pcllf-mmc-management-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Pcl => Path.Combine(Root, "PCL");
        public Fixture() { Root = Path.Combine(scope, "versions", "sample"); Directory.CreateDirectory(Root); }
        public void Install() {
            Directory.CreateDirectory(Pcl);
            File.WriteAllText(Path.Combine(Pcl, "InstanceDefinition.json"), Definition(), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(Pcl, "config.json"), "{\"MinecraftInstallManifest\":{\"pcllfManagement\":" + Hint() + "},\"ordinaryPreference\":42}");
            File.WriteAllText(Path.Combine(Root, "sample.json"), "{\"pcllfManagement\":" + Hint() + ",\"mainClass\":\"sample.Main\"}");
        }
        public string[] Inventory() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) + ":" + File.GetLastWriteTimeUtc(p).Ticks).ToArray();
        public void Dispose() { if (Directory.Exists(scope)) Directory.Delete(scope, true); }
    }
}
