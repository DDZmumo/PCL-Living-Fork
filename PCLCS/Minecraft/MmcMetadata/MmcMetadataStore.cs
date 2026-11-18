using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
public sealed record MmcMetadataKey(string Uid, string Version, bool Recommendation = false,
    string SourceIdentity = "prism-meta");

public sealed record MmcMetadataContent(MmcMetadataKey Key, string Source, string ContentUtf8,
    string Sha256, long ByteCount, bool FromCache);

public sealed record MmcMetadataStoreOptions {
    public int Parallelism { get; init; } = 4;
    public int MaximumRequests { get; init; } = 4096;
    public int MaximumItemBytes { get; init; } = 16 * 1024 * 1024;
    public long MaximumBatchBytes { get; init; } = 128L * 1024 * 1024;
    public string SourceIdentity { get; init; } = "prism-meta";
}

/// <summary>
/// Host-owned transport and immutable content cache. Exact source/UID/version pins
/// never silently change on a cache hit; an instance must persist its adopted
/// snapshots separately. This service does not choose recommended versions.
/// </summary>
public sealed class MmcMetadataStore {
    private readonly string root;
    private readonly Uri baseUri;
    private readonly HttpClient client;
    private readonly MmcMetadataStoreOptions options;
    private readonly SemaphoreSlim fetchGate;
    private readonly object keyGate = new();
    private readonly Dictionary<string, KeySlot> keySlots = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public MmcMetadataStore(string cacheRoot, Uri baseUri, HttpClient client,
        MmcMetadataStoreOptions? options = null) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!Path.IsPathFullyQualified(cacheRoot)) throw new ArgumentException("Cache root must be absolute.", nameof(cacheRoot));
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttps &&
            !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)) ||
            baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 || baseUri.UserInfo.Length != 0)
            throw new ArgumentException("Metadata needs a fixed HTTPS source, or loopback HTTP for tests.", nameof(baseUri));
        this.options = options ?? new();
        if (this.options.Parallelism is < 1 or > 16 || this.options.MaximumRequests < 1 ||
            this.options.MaximumItemBytes is < 1 or > 128 * 1024 * 1024 || this.options.MaximumBatchBytes < 1 ||
            string.IsNullOrWhiteSpace(this.options.SourceIdentity)) throw new ArgumentException("Invalid metadata limits.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        MmcInstancePaths.EnsureNoLinks(root);
        this.baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        this.client = client;
        fetchGate = new(this.options.Parallelism);
    }

    public async Task<IReadOnlyList<MmcMetadataContent>> FetchBatchAsync(IEnumerable<MmcMetadataKey> requests,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(requests);
        // Enumerate with a bound, including repeated requests, before allocating tasks.
        List<MmcMetadataKey> keys = [];
        HashSet<MmcMetadataKey> seen = [];
        int observed = 0;
        foreach (MmcMetadataKey key in requests) {
            if (++observed > options.MaximumRequests) throw new InvalidDataException("Metadata request budget exceeded.");
            ValidateKey(key);
            if (seen.Add(key)) keys.Add(key);
        }
        BatchBudget budget = new(options.MaximumBatchBytes);
        MmcMetadataContent[] output = new MmcMetadataContent[keys.Count];
        Exception? failure = null;
        using CancellationTokenSource batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try {
            await Parallel.ForEachAsync(Enumerable.Range(0, keys.Count), new ParallelOptions {
                MaxDegreeOfParallelism = options.Parallelism, CancellationToken = batch.Token
            }, async (index, token) => {
                try { output[index] = await FetchOneAsync(keys[index], budget, token).ConfigureAwait(false); }
                catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); batch.Cancel(); throw; }
            }).ConfigureAwait(false);
        } catch {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        return Array.AsReadOnly(output);
    }

    private async Task<MmcMetadataContent> FetchOneAsync(MmcMetadataKey key, BatchBudget budget, CancellationToken token) {
        string source = new Uri(baseUri, Uri.EscapeDataString(key.Uid) + "/" +
            (key.Recommendation ? "index" : Uri.EscapeDataString(key.Version)) + ".json").AbsoluteUri;
        string requestId = Hash(JsonSerializer.SerializeToUtf8Bytes(new {
            key.SourceIdentity, key.Uid, key.Version, key.Recommendation, Source = source
        }));
        string receiptPath = CachePath("requests", requestId + ".json");
        using IDisposable keyLease = await EnterKeyAsync(requestId, token).ConfigureAwait(false);
        // A store-wide semaphore also bounds simultaneous batches. Disk receipts
        // handle cross-store/process races without replacing a pinned snapshot.
        await fetchGate.WaitAsync(token).ConfigureAwait(false);
        try {
            token.ThrowIfCancellationRequested();
            if (File.Exists(receiptPath)) return ReadCached(key, source, receiptPath, budget);
            using HttpRequestMessage request = new(HttpMethod.Get, source);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared > options.MaximumItemBytes)
                throw new InvalidDataException("Metadata exceeds the declared byte limit.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream content = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) {
                if (content.Length + read > options.MaximumItemBytes) throw new InvalidDataException("Metadata exceeds the actual byte limit.");
                budget.Consume(read);
                content.Write(buffer, 0, read);
            }
            byte[] bytes = content.ToArray();
            string text = Utf8.GetString(bytes); // Preserve a possible BOM in the source digest.
            ValidateJson(text);
            string digest = Hash(bytes);
            token.ThrowIfCancellationRequested();
            WriteImmutable(CachePath("objects", digest + ".json"), bytes);
            Receipt receipt = new(1, key, source, digest, bytes.Length);
            WriteImmutable(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt));
            return new(key, source, text, digest, bytes.Length, false);
        } finally { fetchGate.Release(); }
    }

    private async Task<IDisposable> EnterKeyAsync(string key, CancellationToken token) {
        KeySlot slot;
        lock (keyGate) {
            if (!keySlots.TryGetValue(key, out slot!)) keySlots.Add(key, slot = new());
            slot.Users++;
        }
        bool acquired = false;
        try {
            await slot.Semaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            return new KeyLease(this, key, slot);
        } finally { if (!acquired) ReleaseKey(key, slot, false); }
    }

    private void ReleaseKey(string key, KeySlot slot, bool acquired) {
        if (acquired) slot.Semaphore.Release();
        lock (keyGate) {
            if (--slot.Users == 0) { keySlots.Remove(key); slot.Semaphore.Dispose(); }
        }
    }

    private MmcMetadataContent ReadCached(MmcMetadataKey key, string source, string path, BatchBudget budget) {
        try {
            byte[] receiptBytes = ReadBounded(path, 16 * 1024);
            using (JsonDocument parsed = JsonDocument.Parse(receiptBytes)) RejectDuplicateKeys(parsed.RootElement);
            Receipt receipt = JsonSerializer.Deserialize<Receipt>(receiptBytes) ?? throw new InvalidDataException("Missing metadata receipt.");
            if (receipt.SchemaVersion != 1 || receipt.Key != key || receipt.Source != source ||
                receipt.ByteCount < 0 || receipt.ByteCount > options.MaximumItemBytes ||
                receipt.Sha256.Length != 64 || receipt.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Invalid metadata receipt.");
            budget.Consume(receipt.ByteCount);
            byte[] bytes = ReadBounded(CachePath("objects", receipt.Sha256.ToLowerInvariant() + ".json"), receipt.ByteCount);
            if (bytes.Length != receipt.ByteCount || !Hash(bytes).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pinned metadata object is damaged; it cannot be silently re-adopted.");
            string text = Utf8.GetString(bytes);
            ValidateJson(text);
            return new(key, source, text, receipt.Sha256.ToLowerInvariant(), bytes.Length, true);
        } catch (Exception error) when (error is JsonException or DecoderFallbackException or NullReferenceException) {
            throw new InvalidDataException("Metadata cache is invalid.", error);
        }
    }

    private void ValidateKey(MmcMetadataKey key) {
        ArgumentNullException.ThrowIfNull(key);
        if (key.SourceIdentity != options.SourceIdentity || string.IsNullOrEmpty(key.Uid) || key.Uid.Length > 256 ||
            key.Uid is "." or ".." || key.Uid.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
            key.Version is null || key.Version.Length > 512 ||
            key.Version.Any(c => char.IsControl(c) || c is '/' or '\\') ||
            (key.Recommendation ? key.Version.Length != 0 : string.IsNullOrWhiteSpace(key.Version)))
            throw new InvalidDataException("Metadata key is not an exact supported source request.");
    }

    private string CachePath(string kind, string leaf) {
        string path = Path.Combine(root, kind, leaf);
        MmcInstancePaths.EnsureNoLinks(path);
        return path;
    }

    private static void WriteImmutable(string path, byte[] bytes) {
        MmcInstancePaths.EnsureNoLinks(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        MmcInstancePaths.EnsureNoLinks(directory);
        string temporary = Path.Combine(directory, ".mmc-write-" + Guid.NewGuid().ToString("N"));
        try {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                file.Write(bytes);
                file.Flush(true);
            }
            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path)) {
                byte[] prior = ReadBounded(path, bytes.Length);
                if (prior.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(prior), SHA256.HashData(bytes)))
                    throw new InvalidDataException("A fixed metadata source changed during concurrent adoption.");
            }
        } finally {
            // The only cleanup target is this exact, randomly named owned file.
            MmcInstancePaths.EnsureNoLinks(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] ReadBounded(string path, int limit) {
        MmcInstancePaths.EnsureNoLinks(path);
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("Cached metadata exceeds its byte limit.");
        using MemoryStream result = new();
        byte[] buffer = new byte[Math.Min(8192, limit + 1)];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) != 0) {
            if (result.Length + read > limit) throw new InvalidDataException("Cached metadata grew past its byte limit.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private static void ValidateJson(string text) {
        using JsonDocument document = JsonDocument.Parse(text.AsMemory().TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 128 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Metadata must be a JSON object.");
        RejectDuplicateKeys(document.RootElement);
    }

    private static void RejectDuplicateKeys(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject()) {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate metadata property.");
                RejectDuplicateKeys(property.Value);
            }
        } else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record Receipt(int SchemaVersion, MmcMetadataKey Key, string Source, string Sha256, int ByteCount);
    private sealed class KeySlot {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }
    private sealed class KeyLease(MmcMetadataStore owner, string key, KeySlot slot) : IDisposable {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.ReleaseKey(key, slot, true); }
    }
    private sealed class BatchBudget(long maximum) {
        private long used;
        private readonly object gate = new();
        internal void Consume(long count) {
            lock (gate) {
                if (count < 0 || used > maximum - count) throw new InvalidDataException("Cumulative metadata batch byte budget exceeded.");
                used += count;
            }
        }
    }
}
