using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed record MmcClasspathClassSelection(string ClassName, string ArtifactKey, string EntryPath,
    int ClasspathPosition, int Release, int ClassMajorVersion);
public sealed record MmcClasspathInspection(ImmutableArray<MmcJarClassObservation> CoreObservations,
    ImmutableArray<MmcClasspathClassSelection> EffectiveClasses) {
    public ImmutableArray<MmcManifestInputAbsence> ManifestInputs { get; init; } = [];
}

/// <summary>Reads only locked classpath JARs and selected class headers; never loads classes or scans a mod directory.</summary>
public static partial class MmcClasspathInspector {
    private sealed record Found(string Path, int Release, int Major);
    private sealed record JarResult(MmcJarClassObservation Observation, Dictionary<string, Found> Found, ImmutableArray<MmcManifestInputAbsence> ManifestInputs);
    private sealed record ScannedClassCandidate(string EntryPath, int Release, byte[]? Header, bool Truncated);
    private sealed record ScannedJar(string Key, string Digest, Dictionary<string, ScannedClassCandidate> Matched, ImmutableArray<MmcManifestInputAbsence> ManifestInputs);
    private sealed record JarRuntimePolicy(int JavaMajor, int MaximumRelease, bool MultiReleaseEnabled, bool Preview);

    public static async Task<MmcClasspathInspection> ObserveAsync(MmcRuntimeLaunchPlan launch, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? additionalClassNames = null) {
        ArgumentNullException.ThrowIfNull(launch);
        string[] required = launch.RequiredClassNames.Concat(additionalClassNames ?? []).Distinct(StringComparer.Ordinal).ToArray();
        ValidateNames(required);
        var profile = launch.Request.BoundPlan.CompiledProfile;
        if ((long)required.Length * profile.Classpath.Length > 65536) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_BUDGET", "Classpath observation matrix exceeds its bound.");
        var inputs = launch.Inputs.ToDictionary(i => i.Identity, StringComparer.Ordinal);
        var policy = RuntimePolicy(launch.Request.Java.Major, launch.EffectiveJvmArguments);
        var requiredEntries = BuildRequiredEntryMap(required);
        var jobs = profile.Classpath.Select((artifact, index) => (artifact, index))
            .GroupBy(x => x.artifact.Key)
            .Select(group => {
                var first = group.First();
                return (first.artifact, first.index, expected: inputs["artifact:" + first.artifact.Key], root: ManifestOwnerRoot(launch.Request, first.artifact.Scope));
            }).ToArray();
        var scanned = new ScannedJar[jobs.Length];
        await Task.Run(() => MmcInstancePaths.ForEachBounded(jobs, (slot, job, token) => {
            scanned[slot] = ScanJar(job.artifact.Key, job.expected.ActualPath, job.expected.Sha256, job.expected.SizeBytes,
                requiredEntries, policy, token, job.root);
        }, cancellationToken), cancellationToken).ConfigureAwait(false);

        var observations = ImmutableArray.CreateBuilder<MmcJarClassObservation>(jobs.Length);
        Dictionary<string, MmcClasspathClassSelection> effective = new(StringComparer.Ordinal);
        var manifestInputs = ImmutableArray.CreateBuilder<MmcManifestInputAbsence>(); long manifestCharacters = 0;
        for (int i = 0; i < jobs.Length; i++) {
            var job = jobs[i];
            var jar = scanned[i];
            foreach (var fact in jar.ManifestInputs) {
                manifestCharacters = checked(manifestCharacters + fact.Reference.Length + fact.TargetPath.Length);
                if (manifestInputs.Count >= 65536 || manifestCharacters > 16 * 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST_BUDGET", "Manifest absence proof exceeds its launch budget.");
                manifestInputs.Add(fact);
            }
            var (observation, found) = ResolveScannedJar(jar, required, policy, name => !effective.ContainsKey(name));
            observations.Add(observation);
            foreach (var entry in found) if (!effective.ContainsKey(entry.Key)) effective.Add(entry.Key,
                new(entry.Key, job.artifact.Key, entry.Value.Path, job.index, entry.Value.Release, entry.Value.Major));
        }
        foreach (string name in launch.RequiredClassNames) if (!effective.ContainsKey(name)) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CLASS_MISSING", "A required launch class is absent from its locked ordered classpath.");
        return new(observations.ToImmutable(), required.Where(effective.ContainsKey).Select(name => effective[name]).ToImmutableArray()) { ManifestInputs = manifestInputs.ToImmutable() };
    }

    internal static async Task VerifySupportDoesNotShadowAsync(MmcRuntimeLaunchRequest request, ImmutableArray<string> required, IReadOnlyList<string> effectiveJvmArguments, CancellationToken token) {
        ValidateNames(required);
        var policy = RuntimePolicy(request.Java.Major, effectiveJvmArguments);
        var items = new[] { ("support:wrapper", request.Wrapper!, "oolloo.jlw.Wrapper"), ("support:bridge", request.Bridge!, MmcRuntimeBridgeTransport.EntryPoint) };
        string[] query = required.Append(items[0].Item3).Append(items[1].Item3).Distinct(StringComparer.Ordinal).ToArray();
        var requiredEntries = BuildRequiredEntryMap(query);
        var scanned = new ScannedJar[items.Length];
        await Task.Run(() => MmcInstancePaths.ForEachBounded(items, (slot, item, ct) => {
            scanned[slot] = ScanJar(item.Item1, item.Item2.ActualPath, item.Item2.Sha256, item.Item2.SizeBytes,
                requiredEntries, policy, ct, manifestOwnerRoot: null);
        }, token), token).ConfigureAwait(false);
        for (int i = 0; i < items.Length; i++) {
            string ownClass = items[i].Item3;
            var (_, found) = ResolveScannedJar(scanned[i], query, policy, name => name == ownClass);
            if (required.Any(found.ContainsKey)) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_SUPPORT_SHADOW", "A host support JAR shadows a required game launch class.");
            if (!found.ContainsKey(ownClass)) throw new MmcRuntimeLaunchException("MMC_RUNTIME_SUPPORT_ENTRY", "An owned support JAR is missing its required host entrypoint.");
        }
    }

    private static Task<JarResult> ReadJarAsync(string key, string path, string expectedSha256, long expectedSize,
        string[] required, JarRuntimePolicy policy, HashSet<string> compatibilityRequired, CancellationToken token, string? manifestOwnerRoot = null) {
        var scanned = ScanJar(key, path, expectedSha256, expectedSize, BuildRequiredEntryMap(required), policy, token, manifestOwnerRoot);
        var (observation, found) = ResolveScannedJar(scanned, required, policy, compatibilityRequired.Contains);
        return Task.FromResult(new JarResult(observation, found, scanned.ManifestInputs));
    }

    private static Dictionary<string, string> BuildRequiredEntryMap(string[] required) =>
        required.ToDictionary(name => name.Replace('.', '/') + ".class", name => name, StringComparer.Ordinal);

    private static ScannedJar ScanJar(string key, string path, string expectedSha256, long expectedSize,
        Dictionary<string, string> requiredEntries, JarRuntimePolicy policy, CancellationToken token,
        string? manifestOwnerRoot) {
        MmcRuntimeFiles.Hash(expectedSha256); MmcInstancePaths.EnsureNoLinks(path);
        using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        string digest = MmcRuntimeFiles.HashStream(source, token);
        if (!digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) || source.Length != expectedSize)
            throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CHANGED", "A classpath/support JAR differs from its adopted bytes.");
        source.Position = 0;
        var index = MmcReadOnlyJarPreflight.Inspect(source);
        source.Position = 0;
        using ZipArchive archive = new(source, ZipArchiveMode.Read, true, new UTF8Encoding(false, true));
        if (archive.Entries.Count != index.Names.Length || archive.Entries.Where((entry, position) => entry.FullName != index.Names[position]).Any()) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_ZIP", "Classpath ZIP index changed during bounded inspection.");
        var manifests = archive.Entries.Where(e => e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (manifests.Length > 1) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "Ambiguous JAR manifest entries.");
        var manifest = manifests.Length == 0 ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ReadManifest(manifests[0]);
        var absent = manifest.TryGetValue("Class-Path", out string? referenced)
            ? MmcManifestClasspath.ObserveAbsent(key, path, digest, manifestOwnerRoot, referenced, token) : ImmutableArray<MmcManifestInputAbsence>.Empty;
        bool multiRelease = policy.MultiReleaseEnabled && manifest.TryGetValue("Multi-Release", out string? enabled) && enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
        Dictionary<string, (ZipArchiveEntry Entry, int Release)> selected = new(StringComparer.Ordinal);
        HashSet<string> relevantEntries = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries) {
            token.ThrowIfCancellationRequested();
            string candidate = entry.FullName; int release = 0;
            if (candidate.StartsWith("META-INF/versions/", StringComparison.Ordinal)) {
                if (!multiRelease) continue;
                int separator = candidate.IndexOf('/', "META-INF/versions/".Length);
                if (separator < 0 || !int.TryParse(candidate.AsSpan("META-INF/versions/".Length, separator - "META-INF/versions/".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out release) ||
                    release < 9 || release > policy.MaximumRelease || !candidate.AsSpan("META-INF/versions/".Length, separator - "META-INF/versions/".Length).SequenceEqual(release.ToString(System.Globalization.CultureInfo.InvariantCulture))) continue;
                candidate = candidate[(separator + 1)..];
            }
            if (!requiredEntries.TryGetValue(candidate, out string? name)) continue;
            if (!relevantEntries.Add(name + ":" + release)) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CLASS_AMBIGUOUS", "Duplicate effective class entry in one locked JAR.");
            if (!selected.TryGetValue(name, out var previous) || release > previous.Release) selected[name] = (entry, release);
        }
        Dictionary<string, ScannedClassCandidate> matched = new(selected.Count, StringComparer.Ordinal);
        foreach (var item in selected) {
            token.ThrowIfCancellationRequested();
            using Stream input = item.Value.Entry.Open();
            byte[] header = new byte[8];
            int totalRead = 0;
            while (totalRead < header.Length) {
                int read = input.Read(header, totalRead, header.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            bool truncated = totalRead < header.Length;
            matched.Add(item.Key, new(item.Value.Entry.FullName, item.Value.Release, truncated ? null : header, truncated));
        }
        return new(key, digest, matched, absent);
    }

    private static (MmcJarClassObservation Observation, Dictionary<string, Found> Found) ResolveScannedJar(
        ScannedJar jar, string[] required, JarRuntimePolicy policy, Func<string, bool> checkClassVersion) {
        Dictionary<string, Found> found = new(jar.Matched.Count, StringComparer.Ordinal);
        foreach (var item in jar.Matched) {
            if (!checkClassVersion(item.Key)) {
                // The JVM never defines this shadowed required class. Keep its
                // explicit presence/path without rejecting an unused header.
                found.Add(item.Key, new(item.Value.EntryPath, item.Value.Release, 0));
                continue;
            }
            if (item.Value.Truncated || item.Value.Header is null)
                throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CLASS_HEADER", "Truncated required class header.");
            byte[] header = item.Value.Header;
            int minor = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)), major = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0xcafebabe || major < 45 || major > policy.JavaMajor + 44 || minor == 65535 && (!policy.Preview || major != policy.JavaMajor + 44))
                throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CLASS_VERSION", "Required class bytecode does not match the selected Java/preview capability.");
            found.Add(item.Key, new(item.Value.EntryPath, item.Value.Release, major));
        }
        var observation = new MmcJarClassObservation(jar.Key, jar.Digest,
            required.Select(name => found.TryGetValue(name, out var value) ? new MmcJarClassPresence(name, true, value.Path) : new(name, false)).ToImmutableArray());
        return (observation, found);
    }

    /// <summary>Call synchronously at final admission after runtime input revalidation, while retaining the instance gate/pins.</summary>
    public static void RevalidateManifestInputs(MmcRuntimeLaunchPlan launch, MmcClasspathInspection inspection, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(launch); ArgumentNullException.ThrowIfNull(inspection); cancellationToken.ThrowIfCancellationRequested();
        if (inspection.ManifestInputs.IsDefault || inspection.ManifestInputs.Length > 65536)
            throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST_BUDGET", "Manifest absence proof is missing or exceeds its bound.");
        var inputs = launch.Inputs.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var artifacts = launch.Request.BoundPlan.CompiledProfile.Classpath.GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        long characters = 0;
        foreach (var fact in inspection.ManifestInputs) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inputs.TryGetValue("artifact:" + fact.ArtifactKey, out var input) || !artifacts.TryGetValue(fact.ArtifactKey, out var artifact) ||
                !Path.GetFullPath(input.ActualPath).Equals(fact.JarPath, MmcRuntimeFiles.PathComparison) || input.Sha256 != fact.JarSha256)
                throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST_INPUT", "Manifest absence proof does not belong to the held classpath input.");
            string root = ManifestOwnerRoot(launch.Request, artifact.Scope);
            if (!root.Equals(fact.OwnedRoot, MmcRuntimeFiles.PathComparison)) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST_INPUT", "Manifest absence proof changed its owned role.");
            characters = checked(characters + fact.Reference.Length + fact.TargetPath.Length);
            if (characters > 16 * 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST_BUDGET", "Manifest absence proof exceeds its name budget.");
            string resolved = MmcManifestClasspath.Resolve(fact.JarPath, root, fact.Reference);
            if (!resolved.Equals(fact.TargetPath, MmcRuntimeFiles.PathComparison)) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST_INPUT", "Manifest absence proof resolved to another target.");
            MmcManifestClasspath.RequireAbsent(root, resolved);
        }
    }
    private static string ManifestOwnerRoot(MmcRuntimeLaunchRequest request, MmcArtifactScope scope) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope == MmcArtifactScope.InstanceLocal ? request.Paths.LocalLibraryRoot : Path.Combine(request.Paths.RuntimeRoot, request.Directories.ViewKey)));

    // Oracle JarFile implementation properties, applied once at VM startup.
    // 'force' enables runtime versioning but does not invent a missing manifest
    // attribute. Standard URL class loaders already request runtimeVersion().
    private static JarRuntimePolicy RuntimePolicy(int javaMajor, IEnumerable<string> jvm) {
        string? version = null, enabled = null; bool preview = false;
        foreach (string token in jvm) {
            if (token == "--enable-preview") preview = true;
            if (token == "-Djdk.util.jar.version") version = "";
            else if (token.StartsWith("-Djdk.util.jar.version=", StringComparison.Ordinal)) version = token["-Djdk.util.jar.version=".Length..];
            if (token == "-Djdk.util.jar.enableMultiRelease") enabled = "";
            else if (token.StartsWith("-Djdk.util.jar.enableMultiRelease=", StringComparison.Ordinal)) enabled = token["-Djdk.util.jar.enableMultiRelease=".Length..];
        }
        if (javaMajor < 9) return new(javaMajor, 8, false, preview);
        int maximum = version is null ? javaMajor : Math.Clamp(JavaInteger(version), 8, javaMajor);
        return new(javaMajor, maximum, enabled != "false", preview);
    }
    private static int JavaInteger(string value) {
        int index = 0; bool negative = false;
        if (value.StartsWith('+') || value.StartsWith('-')) { negative = value[0] == '-'; index++; }
        if (index == value.Length) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MR_VERSION", "The effective JAR runtime-version property is not a Java integer.");
        long result = 0, limit = negative ? 2147483648L : int.MaxValue;
        for (; index < value.Length; index++) {
            int digit = System.Globalization.CharUnicodeInfo.GetDecimalDigitValue(value[index]);
            if (digit < 0 || result > (limit - digit) / 10) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MR_VERSION", "The effective JAR runtime-version property is not a Java integer.");
            result = result * 10 + digit;
        }
        return negative ? (int)-result : (int)result;
    }

    private static Dictionary<string, string> ReadManifest(ZipArchiveEntry entry) {
        if (entry.Length > 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "JAR manifest exceeds its byte budget.");
        using Stream input = entry.Open(); using MemoryStream bytes = new(); byte[] buffer = new byte[4096];
        while (true) { int read = input.Read(buffer, 0, buffer.Length); if (read == 0) break; if (bytes.Length + read > 1024 * 1024) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "JAR manifest grew beyond its byte budget."); bytes.Write(buffer, 0, read); }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "JAR manifest is not valid UTF-8."); }
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase); string? current = null;
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) {
            if (line.Length == 0) break;
            if (line[0] == ' ') { if (current is null) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "Invalid manifest continuation."); result[current] += line[1..]; continue; }
            int split = line.IndexOf(':'); if (split <= 0) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "Invalid manifest attribute.");
            current = line[..split]; if (!result.TryAdd(current, line[(split + 1)..].TrimStart(' '))) throw new MmcRuntimeLaunchException("MMC_CLASSPATH_MANIFEST", "Duplicate manifest main attribute.");
        }
        return result;
    }
    private static void ValidateNames(IEnumerable<string> names) {
        foreach (string name in names) if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '{' or '}' or ' '))
            throw new MmcRuntimeLaunchException("MMC_CLASSPATH_CLASS", "Invalid required class binary name.");
    }
}
