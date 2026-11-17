using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>Production uses only LF's official service endpoints. Loopback is an explicit fixture-only policy.</summary>
public sealed class MmcPlatformApiPolicy {
    private readonly Uri modrinth, curseForge;
    public bool IsLoopbackFixture { get; }
    public static MmcPlatformApiPolicy Production { get; } = new(new("https://api.modrinth.com/v2/"), new("https://api.curseforge.com/v1/"), false);
    private MmcPlatformApiPolicy(Uri modrinth, Uri curseForge, bool fixture) { this.modrinth = modrinth; this.curseForge = curseForge; IsLoopbackFixture = fixture; }
    public static MmcPlatformApiPolicy ForLoopbackFixtures(Uri modrinthV2, Uri curseForgeV1) {
        foreach (var uri in new[] { modrinthV2, curseForgeV1 })
            if (!uri.IsAbsoluteUri || !uri.IsLoopback || uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/'))
                throw new ArgumentException("Fixture API bases must be explicit HTTP loopback directories.");
        return new(modrinthV2, curseForgeV1, true);
    }
    internal string Endpoint(ResourcePlatforms platform, string relative) => new Uri(platform switch {
        ResourcePlatforms.Modrinth => modrinth, ResourcePlatforms.CurseForge => curseForge,
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    }, relative).AbsoluteUri;
    internal void ReleaseAddress(string address) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            (!IsLoopbackFixture && uri.Scheme != "https") || (IsLoopbackFixture && (uri.Scheme != "http" || !uri.IsLoopback)))
            throw new IOException("A release file needs HTTPS, or explicit fixture loopback HTTP.");
    }
}

// Presentation never grants origin authority and is omitted from transaction fingerprints.
public sealed record MmcPlatformReleaseDisplay(string Name, string Version, ImmutableArray<string> GameVersions,
    int ReleaseTypeCode, DateTime Published);
public sealed record MmcPlatformReleaseDescriptor(ResourcePlatforms Platform, string CanonicalProjectKey, string ProjectId,
    string VersionId, MmcPlatformReleaseFile File, string MetadataSha256,
    [property: System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] MmcPlatformReleaseDisplay? Display = null);

/// <summary>Exact-file adapter over ResourceCatalog's existing metadata request. No HTTP client, credentials, UI or downloader.</summary>
public sealed class MmcPlatformOriginCatalog {
    private sealed class RequestBudget {
        private int requests;
        private long bytes;
        internal void Begin() { if (++requests > 512) throw new IOException("Platform operation exceeds its request budget."); }
        internal void Add(int count) { bytes = checked(bytes + count); if (bytes > 64L * 1024 * 1024) throw new IOException("Platform operation exceeds its total metadata budget."); }
    }
    private readonly Func<Uri, CancellationToken, Task<string>> request;
    private readonly MmcPlatformApiPolicy policy;
    internal MmcPlatformOriginCatalog(Func<Uri, CancellationToken, Task<string>> request, MmcPlatformApiPolicy policy) {
        this.request = request ?? throw new ArgumentNullException(nameof(request)); this.policy = policy;
    }
    public static MmcPlatformOriginCatalog ForLoopbackFixtures(Func<Uri, CancellationToken, Task<string>> request, MmcPlatformApiPolicy policy) {
        if (policy is null || !policy.IsLoopbackFixture) throw new ArgumentException("Only an explicit loopback policy can use the fixture transport.");
        return new(request, policy);
    }
    public async Task<ImmutableArray<MmcPlatformReleaseDescriptor>> GetReleaseAsync(ResourcePlatforms platform, string projectId,
        string exactVersionId, CancellationToken token = default) {
        var release = await FetchAsync(platform, projectId, exactVersionId, token).ConfigureAwait(false);
        return release.Files.Select(file => Describe(platform, release, file)).ToImmutableArray();
    }
    private async Task<MmcPlatformRelease> FetchAsync(ResourcePlatforms platform, string projectId, string versionId, CancellationToken token, RequestBudget? budget = null) {
        budget ??= new();
        var identity = new MmcPlatformProjectIdentity(platform.ToString(), projectId, versionId); MmcPlatformOriginCodec.ValidateIdentity(identity);
        string projectSource = policy.Endpoint(platform, platform == ResourcePlatforms.Modrinth ? "project/" + projectId : "mods/" + projectId);
        string versionSource = policy.Endpoint(platform, platform == ResourcePlatforms.Modrinth ? "version/" + versionId : "mods/" + projectId + "/files/" + versionId);
        JToken project = await RequestAsync(projectSource, token, budget).ConfigureAwait(false);
        JToken version = await RequestAsync(versionSource, token, budget).ConfigureAwait(false);
        if (platform == ResourcePlatforms.CurseForge) { project = project["data"] ?? throw new IOException("Missing project response."); version = version["data"] ?? throw new IOException("Missing file response."); }
        var result = MmcPlatformOriginCodec.ReadRelease(identity, projectSource, versionSource, Json(project), Json(version));
        foreach (var file in result.Files) policy.ReleaseAddress(file.DownloadUrl);
        return result;
    }
    internal static MmcPlatformReleaseDescriptor Describe(ResourcePlatforms platform, MmcPlatformRelease release, MmcPlatformReleaseFile file) =>
        new(platform, ResourceProjectKey.Get(platform, release.Identity.ProjectId), release.Identity.ProjectId, release.Identity.VersionId!, file, Fingerprint(release), Display(platform, release));
    private static MmcPlatformReleaseDisplay Display(ResourcePlatforms platform, MmcPlatformRelease release) {
        var data = JObject.Parse(release.VersionJson);
        bool modrinth = platform == ResourcePlatforms.Modrinth;
        string Clean(JToken? value, int maximum = 200) => value?.Type == JTokenType.String
            ? new string(value.ToString().Where(character => !char.IsControl(character)).Take(maximum).ToArray()).Trim() : "";
        string name = Clean(data[modrinth ? "name" : "displayName"]);
        string version = Clean(data[modrinth ? "version_number" : "displayName"]);
        var games = data[modrinth ? "game_versions" : "gameVersions"] is JArray array
            ? array.Take(128).Select(value => Clean(value, 64)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToImmutableArray() : [];
        int type = modrinth ? Clean(data["version_type"]) switch { "release" => 1, "beta" => 2, "alpha" => 3, _ => 0 }
            : data["releaseType"]?.Type == JTokenType.Integer && int.TryParse(data["releaseType"]!.ToString(), out int code) && code is 1 or 2 or 3 ? code : 0;
        _ = DateTime.TryParse(data[modrinth ? "date_published" : "fileDate"]?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date);
        return new(name, version, games, type, date);
    }
    internal static string Fingerprint(MmcPlatformRelease value) => MmcDurableFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new {
        Protocol = "PCLLF.MmcPlatformRelease/v1", value.Identity, value.ProjectSource, value.VersionSource, value.Files
    }));
    internal async Task<MmcPlatformRelease> RecheckAsync(MmcPlatformReleaseDescriptor selected, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(selected);
        var release = await FetchAsync(selected.Platform, selected.ProjectId, selected.VersionId, token).ConfigureAwait(false);
        var actual = Describe(selected.Platform, release, MmcPlatformOriginCodec.SelectFile(release, selected.File));
        if (actual with { Display = null } != selected with { Display = null }) throw new IOException("The selected project/version/file metadata changed; refresh the selection.");
        return release;
    }
    internal async Task<ImmutableArray<MmcPlatformReleaseDescriptor>> ListAsync(MmcPlatformOriginEvidence verified, CancellationToken token) {
        var platform = ParsePlatform(verified.Identity.Platform); string project = verified.Identity.ProjectId;
        RequestBudget budget = new();
        string url = policy.Endpoint(platform, platform == ResourcePlatforms.Modrinth ? "project/" + project + "/version?include_changelog=false" : "mods/" + project + "/files?pageSize=50&index=0");
        var response = await RequestAsync(url, token, budget).ConfigureAwait(false); var descriptors = ImmutableArray.CreateBuilder<MmcPlatformReleaseDescriptor>();
        var ids = new List<string>();
        if (platform == ResourcePlatforms.Modrinth) Add(response);
        else {
            int index = 0, pages = 0;
            while (true) {
                Add(response["data"] ?? throw new IOException("Missing release list."));
                var pagination = response["pagination"] ?? throw new IOException("Missing release pagination.");
                int count = (int?)pagination["resultCount"] ?? -1, total = (int?)pagination["totalCount"] ?? -1;
                if (count < 0 || count > 50 || total < 0 || total > 1024 || count == 0 && index < total || ++pages > 24) throw new IOException("Release pagination is invalid or exceeds its budget.");
                index += count; if (index >= total) break;
                response = await RequestAsync(policy.Endpoint(platform, "mods/" + project + "/files?pageSize=50&index=" + index.ToString(CultureInfo.InvariantCulture)), token, budget).ConfigureAwait(false);
            }
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new IOException("Release list contains duplicate identities.");
        foreach (string id in ids) {
            token.ThrowIfCancellationRequested();
            // Each exact version is independently re-fetched; display/list objects never grant identity.
            var release = await FetchAsync(platform, project, id, token, budget).ConfigureAwait(false);
            foreach (var file in release.Files) descriptors.Add(Describe(platform, release, file));
            if (descriptors.Count > 4096) throw new IOException("Release file list exceeds its budget.");
        }
        return descriptors.ToImmutable();
        void Add(JToken data) {
            if (data is not JArray array || array.Count > 1024 || ids.Count + array.Count > 1024) throw new IOException("Release list exceeds its budget.");
            foreach (var item in array) ids.Add((string?)item["id"] ?? throw new IOException("A release has no stable ID."));
        }
    }
    /// <summary>All hash results are candidates. The caller must select one, even when projects share the same ZIP.</summary>
    public async Task<ImmutableArray<MmcPlatformReleaseDescriptor>> FindModrinthCandidatesAsync(string originalZip, CancellationToken token = default) {
        var measured = await MmcPlatformOriginFiles.MeasureAsync(originalZip, token).ConfigureAwait(false);
        RequestBudget budget = new();
        var response = await RequestAsync(policy.Endpoint(ResourcePlatforms.Modrinth, "version_file/" + measured.Sha512 + "?algorithm=sha512&multiple=true"), token, budget).ConfigureAwait(false);
        var versions = response is JArray array ? array : new JArray(response);
        if (versions.Count > 128) throw new IOException("Hash candidate results exceed their budget.");
        var result = ImmutableArray.CreateBuilder<MmcPlatformReleaseDescriptor>();
        foreach (var version in versions) {
            string projectId = (string?)version["project_id"] ?? throw new IOException("Hash candidate has no project ID.");
            string versionId = (string?)version["id"] ?? throw new IOException("Hash candidate has no version ID.");
            var exact = await FetchAsync(ResourcePlatforms.Modrinth, projectId, versionId, token, budget).ConfigureAwait(false);
            foreach (var file in exact.Files.Where(file => file.Sha512 == measured.Sha512 && file.SizeBytes == measured.SizeBytes && (file.Sha1 is null || file.Sha1 == measured.Sha1)))
                result.Add(Describe(ResourcePlatforms.Modrinth, exact, file));
        }
        return result.Distinct().ToImmutableArray();
    }
    private async Task<JToken> RequestAsync(string url, CancellationToken token, RequestBudget budget) {
        token.ThrowIfCancellationRequested(); budget.Begin();
        // Always await the actual transport: no request producer survives cancellation/selection cleanup.
        string text;
        try { text = await request(new Uri(url), token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or WebException or InvalidOperationException or AggregateException) { throw new IOException("Platform metadata is currently unavailable."); }
        token.ThrowIfCancellationRequested();
        int count = text is null ? 0 : new UTF8Encoding(false, true).GetByteCount(text);
        if (text is null || count > MmcPlatformOriginCodec.MaximumMetadataBytes) throw new IOException("Platform response exceeds its byte budget.");
        budget.Add(count);
        using StringReader input = new(text); using JsonTextReader reader = new(input) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        return JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    }
    private static string Json(JToken value) {
        string text = value?.ToString(Formatting.None) ?? throw new IOException("Missing platform response.");
        if (new UTF8Encoding(false, true).GetByteCount(text) > MmcPlatformOriginCodec.MaximumMetadataBytes) throw new IOException("Platform response exceeds its byte budget.");
        return text;
    }
    internal static ResourcePlatforms ParsePlatform(string value) => value switch {
        "Modrinth" => ResourcePlatforms.Modrinth, "CurseForge" => ResourcePlatforms.CurseForge,
        _ => throw new IOException("Unsupported platform identity.")
    };
}
