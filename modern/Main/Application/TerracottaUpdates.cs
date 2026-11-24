// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PCL;

/// <summary>The newest upstream Terracotta release and, when it can be installed safely, its package for this machine.</summary>
internal sealed record TerracottaUpstream(string Version, DateTimeOffset? Published, string Notes, string Page,
    TerracottaRelease? Package, string? Unavailable)
{
    internal bool IsNewerThan(string version) => TerracottaUpdates.Compare(Version, version) > 0;
}

/// <summary>
/// Reads the latest release of upstream Terracotta from the GitHub API. Nothing here installs anything: a package is
/// offered only when the release lists this machine's archive under the expected name, on the project's own release
/// path, within the size limit and with the SHA-256 digest GitHub publishes for release assets.
/// </summary>
internal static class TerracottaUpdates
{
    internal const string LatestApi = "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly HttpClient client = CreateClient();

    internal static async Task<TerracottaUpstream> CheckAsync(CancellationToken token) =>
        await CheckWithAsync(client, token).ConfigureAwait(false);

    internal static async Task<TerracottaUpstream> CheckWithAsync(HttpClient http, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode is 403 or 429)
            throw new HttpRequestException("GitHub 暂时限制了查询次数，请稍后再试。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("陶瓦联机的发布信息过大。");
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        for (int read; (read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0;)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new InvalidDataException("陶瓦联机的发布信息过大。");
            buffer.Write(chunk, 0, read);
        }
        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return Parse(document.RootElement, TerracottaComponent.PlatformArchitecture);
    }

    /// <summary>Validates one GitHub release object. Throws when it is not a usable release at all.</summary>
    internal static TerracottaUpstream Parse(JsonElement release, string architecture)
    {
        string tag = Text(release, "tag_name") ?? "";
        string version = tag.StartsWith('v') ? tag[1..] : tag;
        if (!TerracottaComponent.IsValidVersion(version)) throw new InvalidDataException("陶瓦联机最新发布的版本号无法识别。");
        DateTimeOffset? published = DateTimeOffset.TryParse(Text(release, "published_at"), out DateTimeOffset date) ? date : null;
        string page = Text(release, "html_url") is { } url && url.StartsWith(TerracottaComponent.ProjectUrl + "/releases/", StringComparison.Ordinal)
            ? url : TerracottaComponent.ProjectUrl + "/releases";
        string notes = Text(release, "body") ?? "";

        string expected = $"terracotta-{version}-windows-{architecture}-pkg.tar.gz";
        string? unavailable = null;
        TerracottaRelease? package = null;
        JsonElement? asset = release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(item => Text(item!.Value, "name") == expected) : null;
        if (asset is not { } found) unavailable = "这个版本没有提供适用于本机的 Windows 安装包。";
        else
        {
            string source = TerracottaComponent.ReleasePrefix + $"{tag}/{expected}";
            string? digest = Text(found, "digest");
            long size = found.TryGetProperty("size", out JsonElement sizeValue) && sizeValue.TryGetInt64(out long value) ? value : 0;
            string hash = digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToUpperInvariant() : "";
            if (Text(found, "browser_download_url") != source) unavailable = "安装包不在陶瓦联机项目的发布地址下，已拒绝。";
            else if (size is <= 0 or > TerracottaComponent.MaximumArchiveSize) unavailable = "安装包大小异常，已拒绝。";
            else if (!TerracottaComponent.IsSha256(hash)) unavailable = "上游没有为安装包提供 SHA-256 校验值，无法安全安装。";
            else if (tag != "v" + version) unavailable = "发布标签与版本号不一致，已拒绝。";
            else package = new TerracottaRelease(version, architecture, size, hash, null, null, [source]);
        }
        return new TerracottaUpstream(version, published, notes, page, package, unavailable);
    }

    /// <summary>Compares two dotted versions numerically.</summary>
    internal static int Compare(string left, string right) =>
        System.Version.TryParse(left, out Version? a) && System.Version.TryParse(right, out Version? b) ? a.CompareTo(b) : string.CompareOrdinal(left, right);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Living-Fork/1.0");
        return result;
    }
}
