using System.Net;
using System.Net.Http;
using System.Text;
using PCLCS;

internal static class MmcMetadataTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC metadata deduplicates exact requests and replays immutable cached bytes", () => Run(Cache));
        yield return ("MMC metadata batches preserve order and bound concurrent network requests", () => Run(Concurrency));
        yield return ("MMC metadata concurrent callers share one fixed-source fetch", () => Run(SingleFlight));
        yield return ("MMC damaged adopted metadata fails without network re-adoption", () => Run(DamagedCache));
        yield return ("MMC metadata cache isolates fixed sources and component versus index requests", () => Run(SourceKeys));
        yield return ("MMC exact index version and recommendation use separate pinned request identities", () => Run(IndexVersion));
        yield return ("MMC damaged receipt cannot spend a tiny batch budget reading a larger object", () => Run(ReceiptBudget));
        yield return ("MMC metadata rejects actual and cumulative oversized bodies before accepting cache entries", () => Run(Budgets));
        yield return ("MMC metadata rejects duplicate JSON and unsafe source keys", () => Run(InvalidInputs));
        yield return ("MMC canceled metadata batches leave no pinned partial and can retry", () => Run(Cancellation));
        yield return ("MMC host passes complete fixed batches to the real pure resolver without ordinary installer fallback", () => Run(Resolution));
        yield return ("MMC recommendation batches resolve completely beyond the graph-depth transport count", () => Run(RecommendationRounds));
    }

    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static readonly MmcMetadataKey Key = new("net.minecraft", "1.7.10");
    private const string Body = "{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}";

    private static async Task Cache() {
        using Fixture f = new(); using Handler handler = new((_, _) => Task.FromResult(Response(Body)));
        using HttpClient client = new(handler);
        MmcMetadataStore store = f.Store(client);
        var first = await store.FetchBatchAsync([Key, Key]);
        Require(first.Count == 1 && handler.Count == 1 && !first[0].FromCache, "Duplicate source request was fetched twice.");
        var second = await store.FetchBatchAsync([Key]);
        Require(second[0].FromCache && handler.Count == 1 && first[0].Sha256 == second[0].Sha256 && second[0].ContentUtf8 == Body,
            "Cache hit changed fixed content or performed network I/O.");
    }

    private static async Task Concurrency() {
        using Fixture f = new(); using Handler handler = new(async (_, token) => {
            await Task.Delay(40, token); return Response(Body);
        }); using HttpClient client = new(handler);
        MmcMetadataStore store = f.Store(client, new() { Parallelism = 2 });
        var keys = Enumerable.Range(1, 6).Select(n => new MmcMetadataKey("component." + n, "1")).ToArray();
        var results = await store.FetchBatchAsync(keys);
        Require(results.Select(x => x.Key).SequenceEqual(keys) && handler.Count == 6 && handler.MaximumActive == 2,
            "Metadata transport lost order or violated its parallel bound.");
    }

    private static async Task SingleFlight() {
        using Fixture f = new(); using Handler handler = new(async (_, token) => {
            await Task.Delay(50, token); return Response(Body);
        }); using HttpClient client = new(handler);
        MmcMetadataStore store = f.Store(client);
        await Task.WhenAll(store.FetchBatchAsync([Key]), store.FetchBatchAsync([Key]), store.FetchBatchAsync([Key]));
        Require(handler.Count == 1, "Concurrent callers fetched the same exact source more than once.");
    }

    private static async Task DamagedCache() {
        using Fixture f = new(); using Handler handler = new((_, _) => Task.FromResult(Response(Body)));
        using HttpClient client = new(handler); MmcMetadataStore store = f.Store(client);
        await store.FetchBatchAsync([Key]);
        string path = Directory.GetFiles(Path.Combine(f.Root, "objects")).Single();
        File.WriteAllText(path, "{}");
        await Throws<InvalidDataException>(() => store.FetchBatchAsync([Key]));
        Require(handler.Count == 1 && File.ReadAllText(path) == "{}", "Damaged pinned metadata was silently replaced.");
    }

    private static async Task SourceKeys() {
        using Fixture f = new(); List<string> urls = [];
        using Handler handler = new((request, _) => {
            lock (urls) urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(Body));
        }); using HttpClient client = new(handler);
        await f.Store(client).FetchBatchAsync([Key, new("net.minecraft", "", true)]);
        await f.Store(client, source: "https://other.invalid/v1/").FetchBatchAsync([Key]);
        Require(handler.Count == 3 && urls.Any(x => x.EndsWith("/net.minecraft/index.json", StringComparison.Ordinal)) &&
            urls.Any(x => x.StartsWith("https://other.invalid/", StringComparison.Ordinal)), "Source/index cache identities were conflated.");
    }

    private static async Task Budgets() {
        using Fixture f = new();
        using Handler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(Body))
        })); using HttpClient client = new(handler);
        await Throws<InvalidDataException>(() => f.Store(client, new() { MaximumItemBytes = 8 }).FetchBatchAsync([Key]));
        Require(!Directory.Exists(Path.Combine(f.Root, "requests")), "Oversized body acquired a pin.");
        await Throws<InvalidDataException>(() => f.Store(client, new() { MaximumBatchBytes = 8 }).FetchBatchAsync([Key]));
        MmcMetadataStore small = f.Store(client, new() { MaximumRequests = 1 });
        int previous = handler.Count;
        await Throws<InvalidDataException>(() => small.FetchBatchAsync([Key, Key]));
        Require(handler.Count == previous, "Unbounded request enumeration reached the transport.");
    }

    private static async Task IndexVersion() {
        using Fixture f = new(); using Handler handler = new((_, _) => Task.FromResult(Response(Body)));
        using HttpClient client = new(handler); MmcMetadataStore store = f.Store(client);
        MmcMetadataKey component = new("component.a", "index"), recommendation = new("component.a", "", true);
        await store.FetchBatchAsync([component]);
        await store.FetchBatchAsync([recommendation]);
        var again = await store.FetchBatchAsync([component, recommendation]);
        Require(handler.Count == 2 && again.Count == 2 && again.All(x => x.FromCache), "Exact version index collided with recommendation receipt.");
    }

    private static async Task ReceiptBudget() {
        using Fixture f = new(); using Handler handler = new((_, _) => Task.FromResult(Response(Body)));
        using HttpClient client = new(handler); MmcMetadataStore store = f.Store(client);
        await store.FetchBatchAsync([Key]);
        string receiptPath = Directory.GetFiles(Path.Combine(f.Root, "requests")).Single();
        var receipt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(receiptPath))!;
        receipt["ByteCount"] = 0;
        File.WriteAllText(receiptPath, receipt.ToJsonString());
        try {
            await f.Store(client, new() { MaximumBatchBytes = 1 }).FetchBatchAsync([Key]);
            throw new Exception("Expected pre-read cache length refusal.");
        } catch (InvalidDataException error) {
            Require(error.Message.Contains("byte limit", StringComparison.Ordinal), "Damaged byte count was detected only after reading the oversized cached object.");
        }
        Require(handler.Count == 1, "Damaged receipt triggered silent re-adoption.");
    }

    private static async Task InvalidInputs() {
        using Fixture f = new(); using Handler handler = new((_, _) => Task.FromResult(Response("{\"a\":1,\"a\":2}")));
        using HttpClient client = new(handler); MmcMetadataStore store = f.Store(client);
        await Throws<InvalidDataException>(() => store.FetchBatchAsync([Key]));
        foreach (MmcMetadataKey key in new[] { new MmcMetadataKey("../escape", "1"), new("a", "../v"),
            new("a", ""), new("a", "1", true), new("a", "1", SourceIdentity: "other") })
            await Throws<InvalidDataException>(() => store.FetchBatchAsync([key]));
        Require(handler.Count == 1, "An invalid key reached the transport.");
    }

    private static async Task Cancellation() {
        using Fixture f = new(); bool block = true;
        using Handler handler = new(async (_, token) => {
            if (block) await Task.Delay(Timeout.Infinite, token);
            return Response(Body);
        }); using HttpClient client = new(handler); MmcMetadataStore store = f.Store(client);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(80));
        await Throws<OperationCanceledException>(() => store.FetchBatchAsync([Key], cancel.Token));
        Require(!Directory.Exists(Path.Combine(f.Root, "requests")), "Canceled download published metadata.");
        block = false;
        var retry = await store.FetchBatchAsync([Key]);
        Require(retry.Count == 1 && retry[0].ContentUtf8 == Body, "Canceled request poisoned a subsequent retry.");
    }

    private static async Task Resolution() {
        using Fixture f = new();
        using Handler handler = new((_, _) => Task.FromResult(Response("{\"formatVersion\":1,\"uid\":\"net.minecraft\",\"version\":\"1.7.10\",\"mainClass\":\"example.Main\"}")));
        using HttpClient client = new(handler);
        const string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.7.10\"}]}";
        var input = new PCLLF.MMCC.Core.MmcPackageInput([new("mmc-pack.json", Encoding.UTF8.GetByteCount(manifest))],
            new Dictionary<string, string> { ["mmc-pack.json"] = manifest });
        var parsed = PCLLF.MMCC.Core.MmcPackageReader.Parse(input);
        Require(!parsed.HasErrors, "Fixture failed source parsing.");
        var resolved = await MmcMetadataResolution.ResolveAsync(parsed.Package!, f.Store(client));
        Require(resolved.Resolution.IsComplete && resolved.Snapshots.Length == 1 && handler.Count == 1,
            "Host failed to return its exact source snapshot to Core.");
        var replay = await MmcMetadataResolution.ResolveAsync(parsed.Package!, f.Store(client), resolved.Snapshots);
        Require(replay.Resolution.IsComplete && handler.Count == 1, "Adopted source replay unexpectedly fetched metadata.");
    }

    private static async Task RecommendationRounds() {
        using Fixture f = new();
        using Handler handler = new((request, _) => {
            string path = request.RequestUri!.AbsolutePath;
            bool minecraft = path.Contains("net.minecraft", StringComparison.Ordinal);
            string uid = minecraft ? "net.minecraft" : "example.dependency";
            string version = minecraft ? "1.7.10" : "1";
            string component = "{\"formatVersion\":1,\"uid\":\"" + uid + "\",\"version\":\"" + version + "\"" +
                (minecraft ? ",\"requires\":[{\"uid\":\"example.dependency\"}]" : "") + "}";
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
                return Task.FromResult(Response("{\"formatVersion\":1,\"uid\":\"" + uid + "\",\"versions\":[{\"version\":\"" + version +
                    "\",\"recommended\":true,\"sha256\":\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(component))).ToLowerInvariant() + "\"}]}"));
            return Task.FromResult(Response(component));
        }); using HttpClient client = new(handler);
        const string manifest = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\"}]}";
        var input = new PCLLF.MMCC.Core.MmcPackageInput([new("mmc-pack.json", Encoding.UTF8.GetByteCount(manifest))],
            new Dictionary<string, string> { ["mmc-pack.json"] = manifest });
        var parsed = PCLLF.MMCC.Core.MmcPackageReader.Parse(input);
        // JSON index syntax needs depth three; the component graph itself has
        // only one edge, but four source batches precede the final pure resolve.
        var resolved = await MmcMetadataResolution.ResolveAsync(parsed.Package!, f.Store(client),
            limits: new PCLLF.MMCC.Core.MmcReadLimits { MaxDepth = 3 });
        Require(resolved.Resolution.IsComplete && handler.Count == 4 && resolved.Snapshots.Length == 4,
            "Graph depth incorrectly limited index/exact-metadata transport rounds. Requests=" + handler.Count +
            " Diagnostics=" + string.Join("; ", resolved.Resolution.Diagnostics.Select(d => d.Code + ":" + d.Message)));
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
        private int count, active, maximum;
        internal int Count => count;
        internal int MaximumActive => maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Interlocked.Increment(ref count);
            int current = Interlocked.Increment(ref active);
            int old;
            do { old = maximum; if (old >= current) break; } while (Interlocked.CompareExchange(ref maximum, current, old) != old);
            try { return await respond(request, token); }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pcllf-mmc-metadata-" + Guid.NewGuid().ToString("N"));
        internal MmcMetadataStore Store(HttpClient client, MmcMetadataStoreOptions? options = null, string source = "https://unit.invalid/v1/") => new(Root, new Uri(source), client, options);
        public void Dispose() {
            string full = Path.GetFullPath(Root);
            string prefix = Path.Combine(Path.GetTempPath(), "pcllf-mmc-metadata-");
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected fixture cleanup root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
