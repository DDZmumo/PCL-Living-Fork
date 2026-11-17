using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
/// <summary>A private sibling root. Failure never recursively removes a version root; its pending ownership survives.</summary>
public sealed partial class MmcOwnedImportReservation : IDisposable {
    private MmcImportOperation? operation;
    private FileStream? producerUse;
    public string? OperationDirectory => operation?.DirectoryPath;
    public void Dispose() { producerUse?.Dispose(); producerUse = null; }
    private void AssertProducerUse() {
        if (producerUse is null || !producerUse.CanRead || operation is null) throw new IOException("Import reservation has no live producer/recovery use lease.");
        operation.VerifySameOwner();
    }
    private readonly byte[] marker, ignore;
    private readonly Dictionary<string, string> cleanupFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> cleanupDirectories = new(StringComparer.OrdinalIgnoreCase) { "", "PCL" };
    internal MmcImportOperation CurrentOperation => operation ?? throw new IOException("Import producer has no operation owner.");
    private Guid producerTransactionId;
    internal void RecordProducerTransaction(Guid transactionId) { producerTransactionId = transactionId; RecordCreatedDirectory("PCL/MmcTransactions"); }
    internal void PrepareWorkspaceParent() {
        AssertProducerUse(); MmcArtifactIo.Directory(MmcInstancePaths.Resolve(TemporaryRoot).ResolveRelative(MmcPathRole.Runtime, ".staging"));
        RecordCreatedDirectory("PCL/MmcRuntime/.staging");
    }
    internal void RecordCreatedDirectory(string relative) {
        if (relative.Length != 0) _ = MmcInstancePaths.Resolve(TemporaryRoot).ResolveRelative(MmcPathRole.Instance, relative);
        cleanupDirectories.Add(relative);
        for (int index = relative.LastIndexOf('/'); index > 0; index = relative.LastIndexOf('/', index - 1)) cleanupDirectories.Add(relative[..index]);
    }
    internal void RecordArtifactProduct(MmcArtifactLock artifact) {
        if (!MmcDurableFiles.IsHash(artifact.AdoptedSha256) || artifact.RecoveryObjectRelativePath != "PCL/MmcArtifacts/sha256/" + artifact.AdoptedSha256)
            throw new IOException("An original artifact cleanup fact needs its verified producer digest and object path.");
        cleanupFiles[artifact.RecoveryObjectRelativePath] = artifact.AdoptedSha256;
        RecordCreatedDirectory("PCL/MmcArtifacts/sha256");
    }
    public string FinalRoot { get; }
    public string TemporaryRoot { get; }
    public Guid InstanceId { get; }
    public string InstanceName => Path.GetFileName(FinalRoot);
    public string? RecoveryRoot {
        get {
            if (Directory.Exists(TemporaryRoot)) return TemporaryRoot;
            if (Directory.Exists(FinalRoot)) {
                try { VerifyMarker(FinalRoot); return FinalRoot; } catch (IOException) { }
            }
            return null;
        }
    }
    private readonly string packageSha256;
    private MmcOwnedImportReservation(string finalRoot, string temporaryRoot, Guid id, string packageSha256, byte[] marker, byte[] ignore) {
        FinalRoot = finalRoot; TemporaryRoot = temporaryRoot; InstanceId = id; this.packageSha256 = packageSha256; this.marker = marker; this.ignore = ignore;
        cleanupFiles.Add("PCL/MmcImportPending.json", MmcDurableFiles.Hash(marker));
        cleanupFiles.Add(".pclignore", MmcDurableFiles.Hash(ignore));
    }
    public static MmcOwnedImportReservation Create(string finalRoot, Guid instanceId, string packageSha256, MmcImportReservationPurpose purpose = MmcImportReservationPurpose.InitialImport) {
        if (instanceId == Guid.Empty || !MmcDurableFiles.IsHash(packageSha256)) throw new ArgumentException("New import needs an identity and package digest.");
        string target = MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(finalRoot));
        using var gate = MmcAdmissionGate.Acquire(target, instanceId, TimeSpan.Zero);
        if (MmcInstancePaths.Attributes(target) is not null) throw new IOException("Existing instance target is preserved; choose a new name.");
        string parent = Path.GetDirectoryName(target)!; MmcArtifactIo.Directory(parent);
        string root = Path.Combine(parent, ".pcllf-mmc-import-" + Guid.NewGuid().ToString("N"));
        MmcInstancePaths.ValidateVersionRoot(root);
        if (MmcInstancePaths.Attributes(root) is not null) throw new IOException("Private import reservation unexpectedly exists.");
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, InstanceId = instanceId, PackageSha256 = packageSha256, FinalRoot = target, TemporaryRoot = root });
        byte[] ignore = Encoding.UTF8.GetBytes("PCLLF.MmcImport/v1\n" + instanceId.ToString("D") + "\n" + packageSha256);
        var reservation = new MmcOwnedImportReservation(target, root, instanceId, packageSha256, marker, ignore);
        var owned = MmcImportOperation.Create(root, target, instanceId, packageSha256, marker, ignore, purpose);
        reservation.operation = owned.Operation; reservation.producerUse = owned.Use;
        try {
            CreateExclusiveDirectory(root); Directory.CreateDirectory(Path.Combine(root, "PCL"));
            MmcDurableFiles.WriteNew(Path.Combine(root, "PCL", "MmcImportPending.json"), marker); MmcDurableFiles.WriteNew(Path.Combine(root, ".pclignore"), ignore);
            return reservation;
        } catch { reservation.Dispose(); throw; }
    }
    /// <summary>Call only after all preparation workspaces have been consumed and disposed.</summary>
    public MmcInstancePaths Publish(string gameRootLeaf, IProgress<MmcInstanceBackendProgress>? progress = null) {
        AssertProducerUse();
        List<MmcConfigurationQuiescence> pauses = [];
        List<MmcAdmissionLease> gates = [];
        try {
            foreach (string root in new[] { TemporaryRoot, FinalRoot }.Order(StringComparer.OrdinalIgnoreCase)) pauses.Add(MmcConfigurationCoordinator.Quiesce(root));
            foreach (string root in new[] { TemporaryRoot, FinalRoot }.Order(StringComparer.OrdinalIgnoreCase)) gates.Add(MmcAdmissionGate.Acquire(root, InstanceId, TimeSpan.Zero));
            var quiet = pauses.Single(value => value.State.Root.Equals(TemporaryRoot, StringComparison.OrdinalIgnoreCase));
            var finalQuiet = pauses.Single(value => value.State.Root.Equals(FinalRoot, StringComparison.OrdinalIgnoreCase));
            VerifyMarker(TemporaryRoot);
            if (MmcInstancePaths.Attributes(FinalRoot) is not null) throw new IOException("The final name was claimed by another instance; preserving both roots.");
            var paths = MmcInstancePaths.Resolve(TemporaryRoot, gameRootLeaf);
            var primary = MmcSavedSourceStore.ReadPrimary(paths);
            if (primary.Definition.InstanceId != InstanceId || primary.Definition.Generation != 1 || primary.Definition.Origin.PackageSha256 != packageSha256)
                throw new IOException("The private import definition does not belong to its reservation.");
            var ownGate = gates.Single(gate => gate.VersionRoot.Equals(TemporaryRoot, StringComparison.OrdinalIgnoreCase));
            using var pendingPermit = EnterPending(ownGate);
            MmcFileTransactions.AssertNoPending(ownGate); MmcSessions.AssertNoActive(ownGate);
            // The only permitted pending observation here is this already hash-verified reservation.
            // Real transaction/session stores have just been checked independently under both gates.
            var observation = MmcManagementProbe.CaptureAtPhysicalRoot(TemporaryRoot, InstanceName) with { HasPendingTransaction = false };
            var ownership = MmcManagementReader.Evaluate(observation);
            if (ownership.State != MmcManagementState.ManagedMmc || ownership.Envelope?.InstanceId != InstanceId) throw new IOException("Private import projections are not ready for publication.");
            VerifyInitialPayload(paths, primary.Definition, progress);
            string staging = paths.ResolveRelative(MmcPathRole.Runtime, ".staging");
            if (Directory.Exists(staging)) {
                if (Directory.EnumerateFileSystemEntries(staging).Any()) throw new IOException("Prepared workspaces remain; refusing to move active absolute paths.");
                Directory.Delete(staging, false);
            }
            // Both absolute roots are strict children of this same verified versions parent. This is
            // an atomic no-overwrite sibling rename, not a recursive cleanup of player directories.
            if (Path.GetDirectoryName(TemporaryRoot) != Path.GetDirectoryName(FinalRoot)) throw new IOException("Import publication must stay under its reserved parent.");
            operation!.SealPublication(primary.Sha256, payloadManifestHash ?? throw new IOException("Import payload was not sealed."), gameRootLeaf);
            lock (finalQuiet.State.Sync) if (finalQuiet.State.Patches.Count != 0 || finalQuiet.State.IniPatches.Count != 0) throw new IOException("Final name has pending preferences from an older identity.");
            for (int attempt = 0; ; attempt++) {
                lock (quiet.State.Sync) {
                    if (quiet.State.PauseCount == 0 || quiet.State.Inflight != 0) throw new IOException("Import publication requires drained configuration writers.");
                }
                VerifyMarker(TemporaryRoot);
                MmcFileTransactions.AssertNoPending(ownGate); MmcSessions.AssertNoActive(ownGate);
                MmcInstancePaths.EnsureNoLinks(TemporaryRoot); MmcInstancePaths.EnsureNoLinks(FinalRoot);
                if (MmcInstancePaths.Attributes(FinalRoot) is not null) throw new IOException("The final name was claimed while publication was waiting; preserving both roots.");
                if (MmcSavedSourceStore.ReadPrimary(paths).Sha256 != primary.Sha256) throw new IOException("Private import authority changed while publication was waiting.");
                if (attempt != 0) VerifyInitialPayload(paths, primary.Definition, progress);
                try { Directory.Move(TemporaryRoot, FinalRoot); break; }
                catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 3 &&
                    error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) is 5 or 32 or 33) {
                    // A short-lived reader can deny rename even after our own producers drained.
                    // Keep both gates/ownership; retry only this no-overwrite move and revalidate
                    // its exact authority and payload before every subsequent attempt.
                    progress?.Report(new("publication-sharing-wait"));
                    Thread.Sleep(100 * (attempt + 1));
                }
            }
            VerifyMarker(FinalRoot);
            progress?.Report(new("private-import-moved"));
            var finalGate = gates.Single(gate => gate.VersionRoot.Equals(FinalRoot, StringComparison.OrdinalIgnoreCase));
            using (var finalPermit = EnterPending(finalGate)) {
                MmcConfigurationCoordinator.CompletePathRelocation(quiet, finalQuiet, ownGate, finalGate, InstanceId, primary.Sha256);
                MmcConfigurationCoordinator.FlushRelocatedPreferences(finalQuiet, finalGate, InstanceId, primary.Sha256);
                operation.Complete();
                File.Delete(Path.Combine(FinalRoot, ".pclignore"));
                progress?.Report(new("private-import-marker-cleanup"));
                File.Delete(Path.Combine(FinalRoot, "PCL", "MmcImportPending.json"));
            }
            return MmcInstancePaths.Resolve(FinalRoot, gameRootLeaf);
        } finally { for (int i = gates.Count - 1; i >= 0; i--) gates[i].Dispose(); for (int i = pauses.Count - 1; i >= 0; i--) pauses[i].Dispose(); }
    }
    private void VerifyMarker(string root, bool allowMissingIgnore = false) {
        var paths = MmcInstancePaths.Resolve(root);
        string? ignoreHash = MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, ".pclignore"));
        if (MmcDurableFiles.Observe(paths.ResolveRelative(MmcPathRole.Instance, "PCL/MmcImportPending.json")) != Convert.ToHexStringLower(SHA256.HashData(marker)) ||
            !(allowMissingIgnore && ignoreHash is null) && ignoreHash != Convert.ToHexStringLower(SHA256.HashData(ignore)))
            throw new IOException("Import ownership markers changed; preserving the directory for recovery.");
    }
    internal MmcPendingOperationPermit EnterPending(MmcAdmissionLease lease) => MmcPendingOperationPermit.ForReservation(lease, this);
    internal string ValidatePendingPermit(MmcAdmissionLease lease) {
        AssertProducerUse();
        lease.AssertHeld();
        if (lease.InstanceId != InstanceId || lease.VersionRoot != TemporaryRoot && lease.VersionRoot != FinalRoot) throw new IOException("初始发布许可不属于该准入租约。");
        if (lease.VersionRoot == FinalRoot) {
            var publication = operation!.Publication() ?? throw new IOException("Final import has no sealed publication.");
            if (MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(FinalRoot, publication.Value.GameRootLeaf)).Sha256 != publication.Value.PrimarySha256) throw new IOException("Final import definition changed.");
            VerifyMarker(FinalRoot, allowMissingIgnore: true);
        } else VerifyMarker(TemporaryRoot);
        return MmcDurableFiles.Hash(marker);
    }
    internal static void CreateExclusiveDirectory(string path) {
        // Directory.CreateDirectory adopts an existing directory and is not an ownership primitive.
        bool created = OperatingSystem.IsWindows() ? CreateDirectoryW(path, IntPtr.Zero) : Mkdir(path, 448) == 0;
        if (!created) throw new IOException("Private import directory could not be exclusively created.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
