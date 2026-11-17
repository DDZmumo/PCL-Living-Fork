using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PCLCS;

// Real host catalog, journal and instance backend; one tiny imported instance per case.
internal static class MmcLifecycleCatalogPagingTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC catalog reaches the 257th real quarantine in stable forward/backward pages and binds cursor scope", CompletePaging);
        yield return ("MMC catalog invalidates changed membership and re-reads same-leaf records without granting stale authority", Changes);
        yield return ("MMC catalog byte budgets shrink pages without skipping records, isolate oversized rows and cancel real reads", BudgetAndCancellation);
    }
    private static void CompletePaging() {
        using Fixture fixture = new(); var source = fixture.Import();
        var deleted = new MmcInstanceLifecycleBackend().DeleteAsync(new(fixture.Source, source.Definition!.InstanceId, 1, source.PrimarySha256!)).GetAwaiter().GetResult();
        Check(deleted.Status == MmcInstanceBackendStatus.Succeeded, deleted.Failure ?? "Fixture delete failed.");
        string operation = Path.GetFileName(deleted.OperationDirectory!);
        Check(StringComparer.Ordinal.Compare(256.ToString("x32"), operation) < 0, "Real GUID fixture must follow the tiny sequential diagnostic GUIDs.");
        // Create in reverse order: filesystem enumeration order must never select the page.
        for (int i = 256; i > 0; i--) fixture.Bad(i);
        var legacy = MmcInstanceLifecycleCatalog.Read(fixture.Game);
        Check(legacy.Truncated && legacy.Entries.Length == 256 && legacy.Entries.All(value => !value.CanRestore), "Compatibility preview lost its bound or reached the wrong slice.");
        var request = new MmcLifecycleCatalogPageRequest(fixture.Game);
        var first = MmcInstanceLifecycleCatalog.ReadPage(request);
        var second = MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = first.Next });
        var returned = MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = second.Previous });
        Check(Leaves(first).SequenceEqual(Leaves(returned)) && first.Previous is null, "Backward page did not return the original stable range.");
        List<string> all = []; var page = first;
        do {
            Check(!page.NeedsRefresh && page.Entries.Length <= 32, "A stable page was invalidated or exceeded its count budget.");
            all.AddRange(Leaves(page));
            if (page.Next is null) break;
            page = MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = page.Next });
        } while (true);
        Check(all.Count == 257 && all.Distinct(StringComparer.Ordinal).Count() == 257 && all.SequenceEqual(all.Order(StringComparer.Ordinal)), "Paging skipped, duplicated or reordered an operation.");
        Check(page.Entries.Single().CanRestore && page.Entries.Single().InstanceId == source.Definition.InstanceId, "The 257th real owned quarantine is unreachable.");
        Denied(() => MmcInstanceLifecycleCatalog.ReadPage(request with { MinecraftRoot = fixture.Root, Cursor = first.Next }));
        Denied(() => MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = first.Next, PageSize = 16 }));
        Denied(() => MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = first.Next, MaximumBytes = 1024 }));
        Check(typeof(MmcLifecycleCatalogCursor).GetConstructors().Length == 0, "An arbitrary path cursor became a public input.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Cancelled(() => MmcInstanceLifecycleCatalog.ReadPage(request, cancel.Token));
    }
    private static void Changes() {
        using Fixture fixture = new(); var source = fixture.Import();
        var deleted = new MmcInstanceLifecycleBackend().DeleteAsync(new(fixture.Source, source.Definition!.InstanceId, 1, source.PrimarySha256!)).GetAwaiter().GetResult();
        Check(deleted.Status == MmcInstanceBackendStatus.Succeeded, "Changes fixture delete failed.");
        for (int i = 1; i <= 3; i++) fixture.Bad(i);
        var request = new MmcLifecycleCatalogPageRequest(fixture.Game, PageSize: 2);
        var first = MmcInstanceLifecycleCatalog.ReadPage(request);
        fixture.Bad(4);
        Check(MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = first.Next }).NeedsRefresh, "A newly created record did not invalidate the old cursor.");
        first = MmcInstanceLifecycleCatalog.ReadPage(request);
        Directory.Delete(fixture.BadPath(4), true);
        Check(MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = first.Next }).NeedsRefresh, "A deleted record did not invalidate the old cursor.");
        first = MmcInstanceLifecycleCatalog.ReadPage(request);
        Directory.Delete(fixture.BadPath(1), true); fixture.Bad(1);
        Directory.SetCreationTimeUtc(fixture.BadPath(1), DateTime.UtcNow.AddMinutes(-5));
        Check(MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = first.Next }).NeedsRefresh, "A replaced operation directory reused a stale cursor.");
        string intent = Path.Combine(deleted.OperationDirectory!, "intent.record");
        byte[] saved = File.ReadAllBytes(intent);
        var preview = MmcInstanceLifecycleCatalog.ReadPage(new(fixture.Game)).Entries.Single(value => value.CanRestore);
        File.AppendAllText(intent, "changed");
        var fresh = MmcInstanceLifecycleCatalog.ReadPage(new(fixture.Game));
        Check(fresh.Entries.Single(value => value.OperationDirectory == deleted.OperationDirectory).State == MmcLifecycleCatalogState.Unknown &&
            fresh.Entries.All(value => !value.CanRestore), "A same-leaf content replacement inherited the old row's action.");
        Denied(() => MmcInstanceLifecycleCatalog.Execute(preview, true));
        Check(Directory.Exists(deleted.CurrentRoot) && !Directory.Exists(fixture.Source), "Stale execution changed the quarantine.");
        File.WriteAllBytes(intent, saved);
        File.WriteAllBytes(Path.Combine(fixture.BadPath(1), "intent.record"), new byte[64 * 1024 + 1]);
        var one = new MmcLifecycleCatalogPageRequest(fixture.Game, PageSize: 1);
        var bad = MmcInstanceLifecycleCatalog.ReadPage(one);
        Check(bad.Entries.Single().State == MmcLifecycleCatalogState.Unknown && bad.Next is not null, "An oversized row prevented later navigation.");
        while (bad.Next is not null) bad = MmcInstanceLifecycleCatalog.ReadPage(one with { Cursor = bad.Next });
        Check(bad.Entries.Single().CanRestore, "An earlier bad record poisoned the later valid quarantine.");
    }
    private static void BudgetAndCancellation() {
        using Fixture fixture = new(); var source = fixture.Import(); var backend = new MmcInstanceLifecycleBackend();
        for (int i = 0; i < 3; i++) {
            using var cancel = new CancellationTokenSource();
            var aborted = backend.DeleteAsync(new(fixture.Source, source.Definition!.InstanceId, 1, source.PrimarySha256!) {
                Checkpoint = (phase, _) => { if (phase == MmcLifecycleCheckpoint.IntentPrepared) cancel.Cancel(); }
            }, cancel.Token).GetAwaiter().GetResult();
            Check(aborted.Status == MmcInstanceBackendStatus.Cancelled && Directory.Exists(fixture.Source), "Production abort did not retain its original instance.");
        }
        string[] operations = Directory.GetDirectories(fixture.Store).Order(StringComparer.Ordinal).ToArray();
        long rowBytes = new FileInfo(Path.Combine(fixture.Source, "PCL", "InstanceDefinition.json")).Length +
            2 * new FileInfo(Path.Combine(operations[0], "intent.record")).Length + new FileInfo(Path.Combine(operations[0], "completed.record")).Length;
        var request = new MmcLifecycleCatalogPageRequest(fixture.Game, PageSize: 3, MaximumBytes: rowBytes + 1);
        List<string> found = []; var page = MmcInstanceLifecycleCatalog.ReadPage(request);
        do {
            Check(page.Entries.Length == 1 && page.Entries[0].State == MmcLifecycleCatalogState.Completed, "Byte-budget exhaustion mislabelled or skipped an unread legal row.");
            found.AddRange(Leaves(page));
            if (page.Next is null) break;
            page = MmcInstanceLifecycleCatalog.ReadPage(request with { Cursor = page.Next });
        } while (true);
        Check(found.SequenceEqual(operations.Select(Path.GetFileName)), "A shortened page lost its first unread boundary.");
        var tooSmall = MmcInstanceLifecycleCatalog.ReadPage(request with { MaximumBytes = 1 });
        Check(tooSmall.Entries.Single().State == MmcLifecycleCatalogState.Unknown && tooSmall.Next is not null, "A single over-budget row blocked all subsequent pages.");
        // This is an actual bounded record hash/read, not a sleeping substitute backend.
        File.WriteAllBytes(Path.Combine(operations[0], "purge-plan.record"), new byte[16 * 1024 * 1024]);
        using var duringRead = new CancellationTokenSource(); duringRead.CancelAfter(1);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Cancelled(() => MmcInstanceLifecycleCatalog.ReadPage(new(fixture.Game), duringRead.Token));
        Check(timer.Elapsed < TimeSpan.FromSeconds(5), "Cancellation did not finish the real enumerator/record read.");
    }
    private static string[] Leaves(MmcLifecycleCatalogPage page) => page.Entries.Select(value => Path.GetFileName(value.OperationDirectory)).ToArray();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Denied(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected a scoped/stale catalog refusal."); }
    private static void Cancelled(Action action) { try { action(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation of real catalog work."); }

    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pcllf-mmc-catalog-" + Guid.NewGuid().ToString("N"));
        internal string Game => Path.Combine(Root, "minecraft");
        internal string Source => Path.Combine(Game, "versions", "source");
        internal string Store => Path.Combine(Game, "PCL", "MmcLifecycle");
        private readonly HttpClient client = new(new NoNetwork());
        internal Fixture() { Directory.CreateDirectory(Game); }
        internal string BadPath(int number) => Path.Combine(Store, number.ToString("x32"));
        internal void Bad(int number) { Directory.CreateDirectory(BadPath(number)); File.WriteAllText(Path.Combine(BadPath(number), "intent.record"), "broken record"); }
        internal MmcInstanceInspection Import() {
            byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"); string hash = Convert.ToHexStringLower(SHA1.HashData(index));
            using (var zip = ZipFile.Open(Path.Combine(Root, "pack.zip"), ZipArchiveMode.Create)) {
                void Add(string name, string content) { using var output = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); output.Write(content); }
                Add("pack/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
                Add("pack/patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"main.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/index.json","sha1":"{{hash}}","size":{{index.Length}}},"compatibleJavaMajors":[8]}""");
                Add("pack/libraries/main.jar", "local runtime bytes"); Add("pack/minecraft/saves/world/level.dat", "world bytes");
            }
            var backend = new MmcInstanceBackend<MmcArtifactDownloadSpec>(new(Path.Combine(Root, "metadata"), new Uri("https://metadata.invalid/"), client),
                new(Path.Combine(Root, "libs"), Path.Combine(Root, "assets"), Path.Combine(Root, "cache")), value => value,
                async (files, token) => { foreach (var file in files) await File.WriteAllTextAsync(file.DownloadPath, "{\"objects\":{}}", new UTF8Encoding(false), token); });
            var result = backend.ImportAsync(new(Path.Combine(Root, "pack.zip"), Source, "source", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            Check(result.Status == MmcInstanceBackendStatus.Succeeded, result.Failure ?? "Fixture import failed."); return MmcInstanceInspector.Read(Source);
        }
        public void Dispose() { client.Dispose(); InstanceConfigProviders.FlushAndDisposeWithin(Root); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class NoNetwork : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new IOException("Catalog fixture has no network.");
    }
}
