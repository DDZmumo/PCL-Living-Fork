using PCLCS;

internal static class MmcOperationPendingGuardTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC outer pending markers block direct transaction recovery and sessions even with an empty inner store", ForeignMarkers);
        yield return ("MMC owned publication permits bind the exact lease and marker without admitting another outer operation", OwnedPermit);
    }
    private static void ForeignMarkers() {
        using Fixture fixture = new(); string root = Path.Combine(fixture.Root, "versions", "source"); Directory.CreateDirectory(Path.Combine(root, "PCL", "MmcTransactions"));
        using var lease = MmcAdmissionGate.Acquire(root, Guid.NewGuid(), TimeSpan.Zero);
        foreach (string name in new[] { "MmcImportPending.json", "MmcLifecyclePending.json" }) {
            string marker = Path.Combine(root, "PCL", name); File.WriteAllText(marker, "{damaged");
            Rejected(() => MmcFileTransactions.AssertNoPending(lease)); Rejected(() => MmcSessions.AssertNoActive(lease));
            Rejected(() => MmcFileTransactions.RecoverPending(lease)); Rejected(() => MmcFileTransactions.Recover(lease, Guid.NewGuid()));
            File.Delete(marker);
        }
        MmcFileTransactions.AssertNoPending(lease);
    }
    private static void OwnedPermit() {
        using Fixture fixture = new(); string target = Path.Combine(fixture.Root, "versions", "target");
        using var owner = MmcOwnedImportReservation.Create(target, Guid.NewGuid(), new string('a',64));
        using var lease = MmcAdmissionGate.Acquire(owner.TemporaryRoot, owner.InstanceId, TimeSpan.Zero);
        Rejected(() => MmcFileTransactions.AssertNoPending(lease));
        using var permit = owner.EnterPending(lease);
        MmcFileTransactions.AssertNoPending(lease); MmcSessions.AssertNoActive(lease);
        using (var otherLease = MmcAdmissionGate.Acquire(owner.TemporaryRoot, owner.InstanceId, TimeSpan.Zero)) Rejected(() => MmcFileTransactions.AssertNoPending(otherLease));
        string foreign = Path.Combine(owner.TemporaryRoot,"PCL","MmcLifecyclePending.json"); File.WriteAllText(foreign,"{}");
        Rejected(() => MmcFileTransactions.AssertNoPending(lease)); File.Delete(foreign);
        string marker = Path.Combine(owner.TemporaryRoot,"PCL","MmcImportPending.json"); File.AppendAllText(marker,"\n");
        Rejected(() => MmcFileTransactions.AssertNoPending(lease));
    }
    private static void Rejected(Action action) { try { action(); } catch(IOException) { return; } throw new Exception("Expected a fresh outer-operation rejection."); }
    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(),"pcllf-mmc-pending-"+Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if(Directory.Exists(Root)) Directory.Delete(Root,true); }
    }
}
