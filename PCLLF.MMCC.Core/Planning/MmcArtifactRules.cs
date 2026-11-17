// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCLLF.MMCC.Core;

internal static partial class MmcArtifactRules {
    internal static MmcRuntimeEnvironment Normalize(MmcRuntimeEnvironment environment) {
        string architecture = environment.Architecture switch {
            "amd64" => "x86_64", "i386" or "i686" => "x86", "aarch64" => "arm64", "arm" or "armhf" => "arm32", _ => environment.Architecture
        };
        if (environment.OperatingSystem is not ("windows" or "linux" or "osx" or "freebsd") ||
            architecture is not ("x86_64" or "x86" or "arm64" or "arm32" or "ppc64le" or "riscv64") ||
            environment.JavaMajor < 0 || environment.JavaBitness is not (32 or 64) ||
            (architecture is "x86" or "arm32") != (environment.JavaBitness == 32))
            throw new MmcInputException("MMC_RUNTIME_ENVIRONMENT", "environment", "Unsupported or inconsistent Java runtime architecture/bitness.");
        return environment with { Architecture = architecture };
    }

    private static bool ClassifierMatches(string target, MmcRuntimeEnvironment environment) =>
        target == environment.OperatingSystem + "-" + environment.Architecture ||
        environment.Architecture is "x86" or "x86_64" && target == environment.OperatingSystem;

    internal static MmcArtifactReference? Library(JsonElement declaration, MmcResolvedComponent component,
        MmcArtifactRole role, MmcRuntimeEnvironment environment, MmcFieldSource source, List<MmcDiagnostic> diagnostics, string? packageLocalScopeSha256 = null) {
        string name = Text(declaration, "name") ?? throw new MmcInputException("MMC_LIBRARY", source.Source, "Library coordinate missing.");
        var coordinate = Coordinate(name, source.Source);
        if (declaration.TryGetProperty("rules", out var rules)) {
            bool active = rules.GetArrayLength() == 0;
            foreach (var rule in rules.EnumerateArray()) {
                bool match = true;
                if (rule.TryGetProperty("os", out var os)) {
                    string? target = Text(os, "name");
                    if (target is not null) match = ClassifierMatches(target, environment);
                    foreach (string ignored in new[] { "arch", "version" }) if (os.TryGetProperty(ignored, out _))
                        diagnostics.Add(new("MMC_RULE_IGNORED_OS_" + ignored.ToUpperInvariant(), MmcDiagnosticSeverity.Warning, source.Source,
                            $"prism-v1 preserves os.{ignored} without applying it; Java classifier and host hard constraints remain separate."));
                }
                if (match) active = Text(rule, "action") == "allow";
            }
            if (!active) return null;
        }
        string? nativeClassifier = null;
        bool native = declaration.TryGetProperty("natives", out var natives) && natives.EnumerateObject().Any();
        if (native) {
            nativeClassifier = Text(natives, environment.OperatingSystem + "-" + environment.Architecture);
            if (nativeClassifier is null && environment.Architecture is "x86" or "x86_64") nativeClassifier = Text(natives, environment.OperatingSystem);
            if (nativeClassifier is null) return null;
            nativeClassifier = nativeClassifier.Replace("${arch}", environment.JavaBitness.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (nativeClassifier.Contains("${", StringComparison.Ordinal)) throw new MmcInputException("MMC_NATIVE_CLASSIFIER", source.Source, "Unknown native classifier placeholder.");
            if (role == MmcArtifactRole.DownloadOnly) return null;
            if (role == MmcArtifactRole.MainJar) throw new MmcInputException("MMC_MAIN_JAR", source.Source, "Main JAR cannot be a native library.");
            role = MmcArtifactRole.Native;
        }
        string hint = Text(declaration, "MMC-hint") ?? "";
        if (hint is not ("" or "local" or "always-stale")) throw new MmcInputException("MMC_UNSUPPORTED_REQUIRED", source.Source, "Unknown library hint.");
        var scope = hint == "local" ? MmcArtifactScope.InstanceLocal : MmcArtifactScope.Shared;
        string classifier = nativeClassifier ?? coordinate.Classifier;
        string filename = Text(declaration, "MMC-filename") ?? coordinate.Artifact + "-" + coordinate.Version + (classifier.Length == 0 ? "" : "-" + classifier) + "." + coordinate.Extension;
        if (native) filename = filename.Replace("${arch}", environment.JavaBitness.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (filename.Contains("${", StringComparison.Ordinal)) throw new MmcInputException("MMC_ARTIFACT_PATH", source.Source, "Unknown filename placeholder.");
        Segment(filename, source.Source);
        string mavenPath = coordinate.Group.Replace('.', '/') + "/" + coordinate.Artifact + "/" + coordinate.Version + "/" + filename;
        Relative(mavenPath, source.Source);
        string relative = scope == MmcArtifactScope.InstanceLocal ? filename : mavenPath;
        JsonElement download = default;
        bool hasDownload = false;
        if (declaration.TryGetProperty("downloads", out var downloads)) {
            if (native) {
                if (!downloads.TryGetProperty("classifiers", out var classifiers) || !classifiers.TryGetProperty(classifier, out download))
                    throw new MmcInputException("MMC_NATIVE_DOWNLOAD", source.Source, "Active native variant has no matching classifier download.");
                hasDownload = true;
            } else if (downloads.TryGetProperty("artifact", out download)) hasDownload = true;
            else if (scope != MmcArtifactScope.InstanceLocal) throw new MmcInputException("MMC_LIBRARY_DOWNLOAD", source.Source, "Active library has no artifact download.");
        }
        string? sha1 = hasDownload ? Digest(download, "sha1", 40, source.Source) : null;
        string? sha256 = hasDownload ? Digest(download, "sha256", 64, source.Source) : null;
        long? size = hasDownload && download.TryGetProperty("size", out var length) ? length.GetInt64() : null;
        ImmutableArray<string> urls = [];
        if (scope == MmcArtifactScope.Shared) {
            string? url = hasDownload ? Text(download, "url") : Text(declaration, "MMC-absoluteUrl") ?? Text(declaration, "absoluteUrl");
            if (hasDownload && string.IsNullOrEmpty(url)) throw new MmcInputException("MMC_LIBRARY_DOWNLOAD", source.Source, "Explicit download descriptor has no URL.");
            if (string.IsNullOrEmpty(url)) url = (Text(declaration, "url") ?? "https://libraries.minecraft.net/").TrimEnd('/') + "/" + mavenPath;
            if (native) url = url.Replace("${arch}", environment.JavaBitness.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            urls = [Url(url, source.Source)];
        }
        if (hasDownload && Text(download, "path") is { Length: > 0 } declaredPath && declaredPath != mavenPath)
            diagnostics.Add(new("MMC_DOWNLOAD_PATH", MmcDiagnosticSeverity.Warning, source.Source, "Declared download.path is retained; prism-v1 storage follows its Maven coordinate/filename."));
        ImmutableArray<string> excludes = declaration.TryGetProperty("extract", out var extract) && extract.TryGetProperty("exclude", out var excluded)
            ? excluded.EnumerateArray().Select(e => e.GetString()!).ToImmutableArray() : [];
        foreach (string exclude in excludes) if (exclude.StartsWith('/') || exclude.Split('/').Any(x => x == "..") || exclude.Contains('\\'))
            throw new MmcInputException("MMC_NATIVE_EXTRACT", source.Source, "Unsafe extraction exclusion path.");
        IEnumerable<string?> keyInputs = [component.Uid, role.ToString(), scope.ToString(), coordinate.Name, relative,
            nativeClassifier, component.Patch.ContentSha256, environment.OperatingSystem + "-" + environment.Architecture];
        if (scope == MmcArtifactScope.InstanceLocal && component.SourceKind == MmcComponentSourceKind.PackageLocal && packageLocalScopeSha256 is { } originScope) {
            MmcDefinitionCodec.CheckHash(originScope);
            keyInputs = keyInputs.Concat(["mmc-measured-package-local-v1", originScope]);
        }
        string key = MmcFingerprint.Of(keyInputs);
        return new(key, component.Uid, component.Patch.ContentSha256, role, scope, coordinate, relative, urls, sha1, sha256,
            size, nativeClassifier, excludes, source);
    }

    internal static MmcArtifactReference Auxiliary(JsonElement descriptor, MmcResolvedComponent component, MmcArtifactRole role,
        string relative, MmcFieldSource source) {
        Relative(relative, source.Source);
        string? url = Text(descriptor, "url");
        string key = MmcFingerprint.Of([component.Uid, role.ToString(), relative, component.Patch.ContentSha256]);
        return new(key, component.Uid, component.Patch.ContentSha256, role, MmcArtifactScope.Shared, null, relative,
            url is { Length: > 0 } ? [Url(url, source.Source)] : [], Digest(descriptor, "sha1", 40, source.Source), Digest(descriptor, "sha256", 64, source.Source),
            descriptor.TryGetProperty("size", out var size) ? size.GetInt64() : null, null, [], source);
    }

    internal static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static MmcMavenCoordinate Coordinate(string text, string source) {
        string[] extensionParts = text.Split('@');
        string[] parts = extensionParts[0].Split(':');
        if (extensionParts.Length > 2 || parts.Length is < 3 or > 4) throw new MmcInputException("MMC_LIBRARY_COORDINATE", source, "Invalid Maven coordinate.");
        string extension = extensionParts.Length == 2 ? extensionParts[1] : "jar";
        foreach (string value in parts) Segment(value, source);
        Segment(extension, source);
        foreach (string group in parts[0].Split('.')) Segment(group, source);
        return new(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : "", extension);
    }
    internal static void Relative(string value, string source) {
        if (value.Length > 32767 || value.Contains('\\')) throw new MmcInputException("MMC_ARTIFACT_PATH", source, "Invalid artifact relative path.");
        foreach (string segment in value.Split('/')) Segment(segment, source);
    }
    private static void Segment(string value, string source) {
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (value.Length is 0 or > 255 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ') ||
            value.Any(c => c < 32 || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
            stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
            throw new MmcInputException("MMC_ARTIFACT_PATH", source, "Unsafe artifact path segment.");
    }
    private static string? Digest(JsonElement value, string key, int size, string source) {
        string? digest = Text(value, key);
        if (digest is null) return null;
        if (digest.Length != size || !digest.All(Uri.IsHexDigit)) throw new MmcInputException("MMC_ARTIFACT_DIGEST", source, "Malformed declared artifact digest.");
        return digest.ToLowerInvariant();
    }
    private static string Url(string value, string source) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new MmcInputException("MMC_ARTIFACT_URL", source, "Artifact URL must be an absolute HTTP(S) source without credentials.");
        return value;
    }

    internal static int CompareVersions(string left, string right) {
        var a = Sections(left); var b = Sections(right);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++) {
            (int Kind, string Text)? first = i < a.Length ? a[i] : null, second = i < b.Length ? b[i] : null;
            int result;
            if (first is null) result = second is null ? 0 : second.Value.Kind == 2 ? 1 : -1;
            else if (second is null) result = first.Value.Kind == 2 ? -1 : 1;
            else if (first.Value.Kind == 1 && second.Value.Kind == 1) {
                string x = first.Value.Text.TrimStart('0'), y = second.Value.Text.TrimStart('0');
                result = x.Length == y.Length ? string.CompareOrdinal(x, y) : x.Length.CompareTo(y.Length);
            } else result = string.CompareOrdinal(first.Value.Text, second.Value.Text);
            if (result != 0) return Math.Sign(result);
        }
        return 0;
    }
    private static (int Kind, string Text)[] Sections(string version) => VersionSections().Matches(version.Split('+')[0])
        .Select(m => (char.IsAsciiDigit(m.Value[0]) ? 1 : m.Value.Length > 1 && m.Value[0] is '-' or ' ' ? 2 : 0, m.Value)).ToArray();
    [GeneratedRegex(@"[- ][^0-9+ -]*|[0-9]+|[^0-9+ -]+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSections();
}
