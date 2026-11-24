using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;

namespace PCLCS;

public sealed record JavaRuntimePackage(string Id, string Distribution, string Version, int MajorVersion,
                                        string Architecture, string PackageType, string FileName, long Size) {
    public bool IsSameArchive(JavaRuntimePackage other) =>
        Id.Equals(other.Id, StringComparison.OrdinalIgnoreCase) &&
        Distribution == other.Distribution && Version == other.Version && MajorVersion == other.MajorVersion &&
        Architecture == other.Architecture && PackageType == other.PackageType && FileName == other.FileName;
}

public sealed record JavaRuntimeArchive(Uri DownloadUri, string Sha256, string? Sha512 = null) {
    internal bool HasValidChecksum => Sha512 is null ? ManagedJavaArchive.IsSha256(Sha256) :
        string.IsNullOrEmpty(Sha256) && ManagedJavaArchive.IsSha512(Sha512);
}

public sealed class JavaRuntimeCatalog(HttpClient client) {
    private static readonly Uri Api = new("https://api.foojay.io/disco/v3.0/");
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private const int MaxChecksumBytes = 4096;
    private static readonly Regex PackageId = new(@"\A[0-9a-fA-F]{1,32}\z");
    private static readonly Regex ZipFileName = new(@"\A[a-zA-Z0-9][a-zA-Z0-9._+-]{0,191}\.[zZ][iI][pP]\z");
    private static readonly Regex VersionNumber = new(@"\A([0-9]{1,4})(?:[._][0-9]{1,8}){0,4}(?:\+[0-9]{1,9})?\z");
    private static readonly Regex RuntimeVersion = new(
        @"\A(?<core>[0-9]+(?:[._][0-9]+){0,3})(?:(?:\+|-b)(?<build>[0-9]+))?(?:-LTS)?\z", RegexOptions.IgnoreCase);
    private static readonly Regex ChecksumLine = new(@"\A([0-9a-fA-F]{128}|[0-9a-fA-F]{64})(?:[ \t]+\*?(.+))?\z");
    private static readonly Regex HexSha256 = new(@"\A[0-9a-fA-F]{64}\z");
    private static readonly Regex CorrettoAlias = new(@"\Aamazon-corretto-[0-9]+-(?:x64|x86|aarch64)-windows-(?:jdk|jre)\.zip\z");
    private static readonly Regex CorrettoTag = new(@"\A[0-9]+(?:\.[0-9]+){2,4}\z");
    private static readonly Regex LibericaTag = new(@"\A[0-9]+(?:[.u][0-9]+)*(?:\+[0-9]+)?\z");
    private static readonly Regex CorrettoReleaseHash = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])");

    public static IReadOnlyDictionary<string, string> Distributions { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal) {
            ["temurin"] = "Eclipse Temurin",
            ["zulu"] = "Azul Zulu",
            ["liberica"] = "BellSoft Liberica",
            ["corretto"] = "Amazon Corretto",
            ["microsoft"] = "Microsoft Build of OpenJDK",
            ["semeru"] = "IBM Semeru",
            ["sap_machine"] = "SAP SapMachine",
            ["oracle_open_jdk"] = "Oracle OpenJDK",
            ["oracle"] = "Oracle Java",
            ["graalvm"] = "GraalVM",
            ["graalvm_community"] = "GraalVM Community",
            ["jetbrains"] = "JetBrains Runtime",
            ["dragonwell"] = "Alibaba Dragonwell",
            ["kona"] = "Tencent Kona",
            ["redhat"] = "Red Hat OpenJDK",
            ["ojdk_build"] = "OJDKBuild",
            ["aoj"] = "AdoptOpenJDK",
            ["aoj_openj9"] = "AdoptOpenJDK OpenJ9",
            ["mandrel"] = "Mandrel",
            ["liberica_native"] = "BellSoft Liberica Native Image Kit"
        });
    public const int MinimumMajor = 8;
    public const int MaximumMajor = 99;

    // A manually installed runtime can have the same release and kind without being the exact same archive.
    public static bool MatchesLocalRuntime(JavaRuntimePackage package, Java java) {
        if (!Distributions.TryGetValue(package.Distribution, out string? distribution) ||
            !string.Equals(java.Distribution, distribution, StringComparison.OrdinalIgnoreCase) ||
            NormalizeArchitecture(java.Architecture) != package.Architecture || java.PackageType != package.PackageType ||
            !File.Exists(java.JavaExePath)) return false;
        return ParseRuntimeVersion(package.Version) is { } expected && ParseRuntimeVersion(java.RuntimeVersion) == expected;
    }

    private static (Version Core, int? Build)? ParseRuntimeVersion(string? value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return null;
        if (value.StartsWith("1.", StringComparison.Ordinal)) value = value.Substring(2); // Java 8's 1.8.0_xxx-bNN form.
        Match match = RuntimeVersion.Match(value);
        if (!match.Success) return null;
        string core = match.Groups["core"].Value.Replace('_', '.');
        if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out Version? version)) return null;
        int? build = null;
        if (match.Groups["build"].Success) {
            if (!int.TryParse(match.Groups["build"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int buildNumber)) return null;
            build = buildNumber;
        }
        return (new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)), build);
    }

    public async Task<IReadOnlyList<JavaRuntimePackage>> SearchAsync(int major, string architecture,
        string? distribution, string? packageType, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (major is < MinimumMajor or > MaximumMajor)
            throw new ArgumentOutOfRangeException(nameof(major), $"请输入 {MinimumMajor} 至 {MaximumMajor} 的 Java 主版本。");
        string arch = NormalizeArchitecture(architecture) ??
            throw new ArgumentException("请选择 x64、ARM64 或 x86 架构。", nameof(architecture));
        distribution = NormalizeFilter(distribution);
        packageType = NormalizeFilter(packageType);
        if (distribution != null && !Distributions.ContainsKey(distribution))
            throw new ArgumentException("所选 Java 发行版不受支持。", nameof(distribution));
        if (packageType != null && packageType is not ("jdk" or "jre"))
            throw new ArgumentException("请选择 JDK 或 JRE。", nameof(packageType));

        // A bare version selects the original release unless latest=available is also supplied.
        var query = new List<string> {
            $"version={major.ToString(CultureInfo.InvariantCulture)}", "latest=available",
            "operating_system=windows", $"architecture={(arch == "arm64" ? "aarch64" : arch)}",
            "archive_type=zip", "release_status=ga", "directly_downloadable=true"
        };
        IEnumerable<string> vendors = distribution == null ? Distributions.Keys : new[] { distribution };
        query.AddRange(vendors.Select(vendor => $"distro={vendor}"));
        if (packageType != null) query.Add($"package_type={packageType}");

        const string source = "Foojay Java 下载目录";
        var uri = new Uri(Api, $"packages?{string.Join("&", query)}");
        JArray rows = await ReadResultAsync(uri, source, token).ConfigureAwait(false);
        var packages = new List<JavaRuntimePackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JObject row in rows.OfType<JObject>()) {
            token.ThrowIfCancellationRequested();
            string vendor = Field(row, "distribution");
            string type = Field(row, "package_type");
            if (!Distributions.ContainsKey(vendor) || (distribution != null && vendor != distribution) ||
                type is not ("jdk" or "jre") || (packageType != null && type != packageType) ||
                Integer(row, "major_version") != major || NormalizeArchitecture(Field(row, "architecture")) != arch ||
                Field(row, "operating_system") != "windows" || Field(row, "archive_type") != "zip" ||
                Field(row, "release_status") != "ga" || !IsTrue(row, "directly_downloadable") ||
                !IsTrue(row, "free_use_in_production")) continue;

            string id = Field(row, "id");
            string version = Field(row, "java_version");
            string fileName = Field(row, "filename");
            long? size = Integer(row, "size");
            // The Corretto latest alias can change while the catalogue still has old size/version metadata.
            if (!PackageId.IsMatch(id) || !MatchesVersion(version, major) || !ZipFileName.IsMatch(fileName) ||
                (vendor == "corretto" && CorrettoAlias.IsMatch(fileName)) || size is null or < -1 or > ManagedJavaArchive.MaximumBytes || !seen.Add(id)) continue;

            packages.Add(new JavaRuntimePackage(id.ToLowerInvariant(), vendor, version, major, arch, type, fileName,
                                                Math.Max(0, size.Value)));
        }
        return packages.AsReadOnly();
    }

    public async Task<JavaRuntimeArchive> ResolveAsync(JavaRuntimePackage package, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!PackageId.IsMatch(package.Id) || !Distributions.ContainsKey(package.Distribution) ||
            !ZipFileName.IsMatch(package.FileName))
            throw new ArgumentException("Java 安装包信息无效，请重新查询。", nameof(package));

        const string source = "Foojay Java 安装包信息";
        var uri = new Uri(Api, $"ids/{package.Id.ToLowerInvariant()}");
        JArray rows = await ReadResultAsync(uri, source, token).ConfigureAwait(false);
        if (rows.Count != 1 || rows[0] is not JObject detail || Field(detail, "filename") != package.FileName)
            throw new InvalidDataException("Foojay 的安装包信息已变化，请重新查询。");

        Uri download = HttpsUri(Field(detail, "direct_download_uri")) ??
            throw new InvalidDataException("该 Java 安装包缺少有效的 HTTPS 下载链接。");
        string kind = Field(detail, "checksum_type").Trim().Replace("-", "");
        bool useSha512 = kind.Equals("sha512", StringComparison.OrdinalIgnoreCase);
        if (useSha512 || kind.Equals("sha256", StringComparison.OrdinalIgnoreCase)) {
            int length = useSha512 ? 128 : 64;
            string supplied = Field(detail, "checksum").Trim();
            string? checksum = supplied.Length == length && supplied.All(Uri.IsHexDigit) ? supplied.ToLowerInvariant() : null;
            if (checksum == null && HttpsUri(Field(detail, "checksum_uri")) is Uri checksumUri) {
                string text = await ReadTextAsync(checksumUri, MaxChecksumBytes, "Java 安装包校验值", token)
                    .ConfigureAwait(false);
                checksum = ParseChecksumFile(text, package.FileName, length);
            }
            if (checksum is not null) return useSha512 ? new JavaRuntimeArchive(download, "", checksum) : new JavaRuntimeArchive(download, checksum);
        }
        string? sha256 = package.Distribution switch {
            "liberica" => await ReadLibericaSha256Async(package, download, token).ConfigureAwait(false),
            "corretto" => await ReadCorrettoSha256Async(package, download, token).ConfigureAwait(false),
            _ => await ReadOfficialGitHubSha256Async(package, download, token).ConfigureAwait(false)
        };
        token.ThrowIfCancellationRequested();
        if (sha256 == null) throw new InvalidDataException("该 Java 安装包未提供可用的 SHA-256 或 SHA-512 校验值。");
        return new JavaRuntimeArchive(download, sha256);
    }

    private async Task<string?> ReadOfficialGitHubSha256Async(JavaRuntimePackage package, Uri download, CancellationToken token) {
        if (!download.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = download.AbsolutePath.TrimStart('/').Split('/');
        if (parts.Length != 6 || parts[2] != "releases" || parts[3] != "download" ||
            Uri.UnescapeDataString(parts[5]) != package.FileName) return null;

        string major = package.MajorVersion.ToString(CultureInfo.InvariantCulture);
        string repo = parts[0] + "/" + parts[1];
        bool official = package.Distribution switch {
            "semeru" => repo.Equals($"ibmruntimes/semeru{major}-binaries", StringComparison.OrdinalIgnoreCase),
            "sap_machine" => repo.Equals("SAP/SapMachine", StringComparison.OrdinalIgnoreCase),
            "dragonwell" => repo.Equals($"dragonwell-project/dragonwell{major}", StringComparison.OrdinalIgnoreCase) ||
                major == "8" && repo.Equals("alibaba/dragonwell8", StringComparison.OrdinalIgnoreCase),
            "kona" => repo.Equals($"Tencent/TencentKona-{major}", StringComparison.OrdinalIgnoreCase),
            "aoj" or "aoj_openj9" => repo.Equals($"AdoptOpenJDK/openjdk{major}-binaries", StringComparison.OrdinalIgnoreCase),
            "mandrel" => repo.Equals("graalvm/mandrel", StringComparison.OrdinalIgnoreCase),
            "liberica_native" => repo.Equals("bell-sw/LibericaNIK", StringComparison.OrdinalIgnoreCase),
            "ojdk_build" => repo.Equals("ojdkbuild/ojdkbuild", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!official) return null;
        string tag = Uri.UnescapeDataString(parts[4]);
        if (tag.Length is 0 or > 128) return null;
        var uri = new Uri($"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        JObject release = await ReadJsonAsync(uri, "Java 官方发布信息", token).ConfigureAwait(false);
        if (release["assets"] is not JArray assets) return null;
        var exact = assets.OfType<JObject>().FirstOrDefault(asset => Field(asset, "name") == package.FileName &&
            (package.Size <= 0 || Integer(asset, "size") == package.Size));
        if (exact is null) return null;
        string digest = Field(exact, "digest");
        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && NormalizeSha256(digest.Substring(7)) is { } hash)
            return hash;

        foreach (JObject asset in assets.OfType<JObject>()) {
            string name = Field(asset, "name");
            if (name != package.FileName + ".sha256" && name != package.FileName + ".sha256.txt") continue;
            Uri? checksumUri = HttpsUri(Field(asset, "browser_download_url"));
            if (checksumUri is null || ReleaseTag(checksumUri, "github.com", $"/{repo}/releases/download/", name) != tag) continue;
            string text = await ReadTextAsync(checksumUri, MaxChecksumBytes, "Java 官方校验值", token).ConfigureAwait(false);
            if (ParseChecksumFile(text, package.FileName) is { } published) return published;
        }
        return null;
    }

    private async Task<string?> ReadLibericaSha256Async(JavaRuntimePackage package, Uri download, CancellationToken token) {
        // Foojay exposes BellSoft's SHA-1. Fetch the exact asset's SHA-256 digest from BellSoft's GitHub release.
        string? tag = ReleaseTag(download, "github.com", "/bell-sw/Liberica/releases/download/", package.FileName);
        if (tag == null || !LibericaTag.IsMatch(tag)) return null;
        var uri = new Uri($"https://api.github.com/repos/bell-sw/Liberica/releases/tags/{Uri.EscapeDataString(tag)}");
        JObject release = await ReadJsonAsync(uri, "Liberica 发布信息", token).ConfigureAwait(false);
        if (release["assets"] is not JArray assets) return null;
        foreach (JObject asset in assets.OfType<JObject>()) {
            if (Field(asset, "name") != package.FileName ||
                (package.Size > 0 && Integer(asset, "size") != package.Size)) continue;
            string digest = Field(asset, "digest");
            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return NormalizeSha256(digest.Substring("sha256:".Length));
        }
        return null;
    }

    private async Task<string?> ReadCorrettoSha256Async(JavaRuntimePackage package, Uri download, CancellationToken token) {
        // AWS publishes checksums for each version next to the exact resource link in the GitHub release.
        string? tag = ReleaseTag(download, "corretto.aws", "/downloads/resources/", package.FileName);
        if (tag == null || !CorrettoTag.IsMatch(tag)) return null;
        string major = package.MajorVersion.ToString(CultureInfo.InvariantCulture);
        var uri = new Uri($"https://api.github.com/repos/corretto/corretto-{major}/releases/tags/{tag}");
        JObject release = await ReadJsonAsync(uri, "Corretto 发布信息", token).ConfigureAwait(false);
        string expectedLink = $"[{package.FileName}]({download.AbsoluteUri})";
        foreach (string line in Field(release, "body").Split('\n')) {
            string[] cells = line.Split('|');
            for (int index = 0; index + 1 < cells.Length; index++) {
                if (cells[index].Trim() != expectedLink) continue;
                Match hash = CorrettoReleaseHash.Match(cells[index + 1]);
                if (hash.Success) return hash.Value.ToLowerInvariant();
            }
        }
        return null;
    }

    private static string? ReleaseTag(Uri download, string host, string prefix, string fileName) {
        if (!download.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            !download.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string[] suffix = download.AbsolutePath.Substring(prefix.Length).Split('/');
        if (suffix.Length != 2 || Uri.UnescapeDataString(suffix[1]) != fileName) return null;
        return Uri.UnescapeDataString(suffix[0]);
    }

    private async Task<JArray> ReadResultAsync(Uri uri, string source, CancellationToken token) {
        JObject json = await ReadJsonAsync(uri, source, token).ConfigureAwait(false);
        return json["result"] as JArray ?? throw new InvalidDataException($"{source}返回的数据缺少结果列表。");
    }

    private async Task<JObject> ReadJsonAsync(Uri uri, string source, CancellationToken token) {
        string text = await ReadTextAsync(uri, MaxJsonBytes, source, token).ConfigureAwait(false);
        try {
            return JObject.Parse(text);
        } catch (JsonException ex) {
            throw new InvalidDataException($"{source}返回的数据格式无效。", ex);
        }
    }

    private async Task<string> ReadTextAsync(Uri uri, int maxBytes, string source, CancellationToken token) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PCL-Living-Fork");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is Uri finalUri && !IsHttps(finalUri))
                throw new InvalidDataException($"{source}的响应未通过 HTTPS 获取。");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException($"{source}的响应超出大小限制。");

            using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[Math.Min(8192, maxBytes + 1)];
            while (true) {
                int remaining = maxBytes - (int)output.Length;
                int count = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining + 1), deadline.Token)
                    .ConfigureAwait(false);
                if (count == 0) break;
                if (count > remaining)
                    throw new InvalidDataException($"{source}的响应超出大小限制。");
                output.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length).TrimStart('\uFEFF');
        } catch (OperationCanceledException ex) when (!token.IsCancellationRequested) {
            throw new TimeoutException($"{source}请求超时，请重试。", ex);
        } catch (Exception ex) when (ex is HttpRequestException or IOException) {
            var status = (ex as HttpRequestException)?.StatusCode;
            string message = status.HasValue ? $"{source}请求失败（HTTP {(int)status.Value}）。" :
                $"无法读取{source}，请检查网络后重试。";
            throw new HttpRequestException(message, ex, status);
        }
    }

    private static string? ParseChecksumFile(string text, string fileName, int length = 64) {
        string? hash = null;
        foreach (string line in text.Split('\n')) {
            Match match = ChecksumLine.Match(line.Trim());
            if (!match.Success || match.Groups[1].Length != length || (match.Groups[2].Success && match.Groups[2].Value != fileName)) continue;
            string value = match.Groups[1].Value.ToLowerInvariant();
            if (hash != null && hash != value) return null;
            hash = value;
        }
        return hash;
    }

    private static bool MatchesVersion(string value, int major) {
        string normalized = value.StartsWith("1.", StringComparison.Ordinal) ? value.Substring(2) : value;
        Match match = VersionNumber.Match(normalized);
        return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == major;
    }

    private static string? NormalizeArchitecture(string? value) => NormalizeFilter(value) switch {
        "x64" or "amd64" or "x86-64" or "x86_64" => "x64",
        "arm64" or "aarch64" => "arm64",
        "x86" or "x32" or "x86_32" or "x86-32" or "i386" or "i486" or "i586" or "i686" => "x86",
        _ => null
    };

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeSha256(string value) => HexSha256.IsMatch(value) ? value.ToLowerInvariant() : null;
    private static Uri? HttpsUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && IsHttps(uri) ? uri : null;
    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static string Field(JObject row, string key) =>
        row[key] is JValue { Type: JTokenType.String, Value: string value } ? value : "";
    private static long? Integer(JObject row, string key) =>
        row[key] is JValue { Type: JTokenType.Integer, Value: long value } ? value : null;
    private static bool IsTrue(JObject row, string key) =>
        row[key] is JValue { Type: JTokenType.Boolean, Value: true };
}
