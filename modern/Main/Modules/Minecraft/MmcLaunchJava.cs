#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF Main integration; governed by the repository LICENCE.
public enum MmcLaunchJavaState { CurrentView, VariantRequired }
public sealed record MmcLaunchJavaSelection(Java Java, MmcRuntimeEnvironment Environment, MmcRuntimeJavaSnapshot RuntimeJava) {
    public MmcLaunchJavaState State { get; init; }
    public bool RequiresVariantAdoption => State == MmcLaunchJavaState.VariantRequired;
    public MmcCompiledProfile CompiledProfile { get; init; } = null!;
    public ImmutableArray<MmcDiagnostic> Diagnostics { get; init; } = [];
}
public sealed class MmcLaunchJavaException(string code, string message) : InvalidOperationException(message) {
    public string Code { get; } = code;
}

/// <summary>Read-only selection/probing before session admission. No ordinary MC/Forge inference, preference reset, installation or catalogue mutation.</summary>
public static class MmcLaunchJava {
    private static readonly string[] ProbeArguments = ["-Dsun.stdout.encoding=UTF-8", "-Dsun.stderr.encoding=UTF-8", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-XshowSettings:properties", "-version"];
    private static readonly string[] IdentityProperties = ["java.home", "java.version", "java.runtime.version", "java.specification.version", "java.vendor", "java.vm.name", "java.vm.vendor", "java.vm.version", "os.arch", "sun.arch.data.model", "file.encoding", "native.encoding"];
    private sealed record Image(string Sha256, string Architecture, int Bitness, ushort Machine, ushort OptionalMagic);
    private sealed record Probed(Java Java, MmcRuntimeJavaSnapshot Runtime);

    public static Task<MmcLaunchJavaSelection> SelectAsync(ModMinecraft.McInstance instance, MmcInstanceDefinition definition,
        MmcResolvedProfile resolved, MmcLaunchJavaPreference preference, CancellationToken token,
        ImmutableArray<MmcJavaConstraint> accountConstraints = default, ImmutableArray<MmcJavaConstraint> platformConstraints = default,
        Version? minimumVersion = null) =>
        SelectFromCandidatesAsync(instance, definition, resolved, preference, Configs.JavaList.Get()?.ToArray() ?? [], token, accountConstraints, platformConstraints, minimumVersion);

    // Only the catalogue read is substitutable for narrow facade checks. All candidate execution,
    // PE inspection, checksums, policy compilation and publication matching remain genuine.
    public static async Task<MmcLaunchJavaSelection> SelectFromCandidatesAsync(ModMinecraft.McInstance instance, MmcInstanceDefinition definition,
        MmcResolvedProfile resolved, MmcLaunchJavaPreference preference, IReadOnlyList<Java> catalogue, CancellationToken token,
        ImmutableArray<MmcJavaConstraint> accountConstraints = default, ImmutableArray<MmcJavaConstraint> platformConstraints = default,
        Version? minimumVersion = null) {
        ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(preference); ArgumentNullException.ThrowIfNull(catalogue);
        token.ThrowIfCancellationRequested();
        var validity = MmcDefinitionCodec.Validate(definition);
        if (!validity.IsEmpty || definition.SourceBundle.ResolutionFingerprint != resolved.SourceFingerprint) Fail("MMC_JAVA_DEFINITION", "MMC 已发布定义与保留组件来源不一致。");
        if (!Enum.IsDefined(preference.Mode) || catalogue.Count > 512) Fail("MMC_JAVA_PREFERENCE", "Java 选择模式或候选数量无效。");
        ValueRange<Version>? range = null;
        if (preference.VersionRange is not null) {
            try { range = ValueRange<Version>.FromString(preference.VersionRange, s => StringUtils.ParseVersionWithDefaults(s)); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { Fail("MMC_JAVA_PREFERENCE", "自定义 Java 版本范围无效，请重新设置。"); }
            if (range!.IsEmpty()) Fail("MMC_JAVA_PREFERENCE", "自定义 Java 版本范围为空。");
        }
        string instanceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.PathVersion));
        List<MmcDiagnostic> diagnostics = [];
        string? preferred = null;
        if (!string.IsNullOrWhiteSpace(preference.Path)) {
            try { preferred = BinFolder(preference.Path); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) {
                if (preference.Force) Fail("MMC_JAVA_PREFERENCE", "强制指定的 Java 路径无效，请重新选择。");
                diagnostics.Add(new("MMC_JAVA_RECOMMENDATION", MmcDiagnosticSeverity.Information, "java", "包内 Java 路径仅作为建议，当前机器无法使用该路径。"));
            }
        } else if (preference.Force) Fail("MMC_JAVA_PREFERENCE", "已选择强制 Java，但没有指定可执行文件或目录。");
        List<string> folders = [];
        if (preferred is not null) folders.Add(preferred);
        if (!preference.Force) {
            if (preference.InstanceFolderOnly) folders.AddRange(await Task.Run(() => InstanceFolders(instanceRoot, token), token).ConfigureAwait(false));
            else folders.AddRange(catalogue.Where(j => j is not null).Select(j => j.Folder));
        }
        if (preference.InstanceFolderOnly) folders = folders.Where(folder => Within(folder, instanceRoot)).ToList();
        folders = folders.GroupBy(CanonicalPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        var constraints = (accountConstraints.IsDefault ? [] : accountConstraints).Concat(platformConstraints.IsDefault ? [] : platformConstraints).ToImmutableArray();
        MmcLaunchJavaSelection? variant = null;
        foreach (string folder in folders) {
            token.ThrowIfCancellationRequested();
            try {
                Probed actual = await ProbeAsync(folder, token).ConfigureAwait(false);
                if (minimumVersion is not null && (actual.Java.Version is null || actual.Java.Version < minimumVersion)) {
                    if (preference.Force) Fail("MMC_JAVA_PREFERENCE_INCOMPATIBLE", "强制 Java 不满足认证或宿主要求的完整版本约束。");
                    continue;
                }
                if (actual.Java.Version is null || range is not null && !range.Contains(actual.Java.Version)) { if (preference.Force) Fail("MMC_JAVA_PREFERENCE_INCOMPATIBLE", "强制 Java 不满足玩家设置的版本范围。"); continue; }
                var environment = new MmcRuntimeEnvironment("windows", actual.Runtime.Architecture, actual.Runtime.Major, actual.Runtime.Bitness);
                var compiled = MmcProfileCompiler.Compile(resolved, environment);
                if (compiled.HasErrors || compiled.Profile is null) { if (preference.Force) Fail("MMC_JAVA_PREFERENCE_INCOMPATIBLE", "强制 Java 不满足整合包组件声明或其平台规则。"); continue; }
                var policy = MmcJavaPolicyEvaluator.Create(compiled.Profile.JavaPolicy.Kind == MmcJavaPolicyKind.DeclaredCandidates ? compiled.Profile.JavaPolicy.AllowedMajors : [],
                    compiled.Profile.JavaPolicy.RecommendedRuntimeName, compiled.Profile.JavaPolicy.HardConstraints.Concat(constraints).ToArray());
                if (compiled.Profile.JavaPolicy.Kind == MmcJavaPolicyKind.Unsatisfiable || !policy.Allows(actual.Runtime.Major)) { if (preference.Force) Fail("MMC_JAVA_PREFERENCE_INCOMPATIBLE", "强制 Java 不满足整合包、账户或平台的实际约束。"); continue; }
                bool existing = compiled.Profile.RuntimePlanFingerprint == definition.InstalledProjection!.RuntimePlanFingerprint;
                var selection = new MmcLaunchJavaSelection(actual.Java, environment, actual.Runtime) {
                    State = existing ? MmcLaunchJavaState.CurrentView : MmcLaunchJavaState.VariantRequired, CompiledProfile = compiled.Profile,
                    Diagnostics = diagnostics.ToImmutableArray().AddRange(existing ? [] : new[] { new MmcDiagnostic("MMC_JAVA_VARIANT_REQUIRED", MmcDiagnosticSeverity.Information, "java", "所选 Java 合法，但需要先采用新的运行环境变体；尚未授权使用旧运行视图启动。") })
                };
                if (preference.Force || existing) return selection;
                variant ??= selection;
            } catch (MmcLaunchJavaException error) when (!preference.Force && error.Code is "MMC_JAVA_PROBE" or "MMC_JAVA_IMAGE") {
                diagnostics.Add(new(error.Code, MmcDiagnosticSeverity.Warning, "java", error.Message));
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) {
                if (preference.Force) Fail("MMC_JAVA_PREFERENCE", "强制指定的 Java 无法完成实际检查，请重新选择。");
                diagnostics.Add(new("MMC_JAVA_PROBE", MmcDiagnosticSeverity.Warning, "java", "候选 Java 无法完成实际可执行文件检查，已继续检查其他候选。"));
            }
        }
        if (variant is not null) return variant with { Diagnostics = diagnostics.ToImmutableArray().AddRange(variant.Diagnostics.Where(d => d.Code == "MMC_JAVA_VARIANT_REQUIRED")) };
        Fail("MMC_JAVA_NO_COMPATIBLE", preference.InstanceFolderOnly ? "实例目录中没有通过实际检查且符合组件声明的 Java，请修改目录或 Java 设置。" : "当前 Java 列表中没有通过实际检查且符合整合包、账户与平台约束的 Java，请下载或导入兼容版本后重试。");
        return null!;
    }

    private static async Task<Probed> ProbeAsync(string folder, CancellationToken token) {
        var java = new Java(BinFolder(folder)); // fresh object: never trust/overwrite catalogue cache
        Image before = await ImageAsync(java.JavaExePath, token).ConfigureAwait(false);
        string? output = null;
        bool usable = await java.CheckAsync(token, async (file, cancellation) => output = await CleanProbeAsync(file, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        Image after = await ImageAsync(java.JavaExePath, token).ConfigureAwait(false);
        if (before != after) Fail("MMC_JAVA_IMAGE", "Java 入口在实际检查期间发生变化，请重试选择。");
        if (!usable || java.Version is null || output is null) Fail("MMC_JAVA_PROBE", "候选 Java 未通过当前机器的实际运行检查。");
        var properties = Properties(output);
        if (!properties.TryGetValue("os.arch", out string? architecture) || NormalizeArchitecture(architecture) != before.Architecture ||
            !properties.TryGetValue("sun.arch.data.model", out string? model) || model != before.Bitness.ToString(CultureInfo.InvariantCulture) ||
            !properties.TryGetValue("java.specification.version", out string? specification) || ParseMajor(specification) != java.Version.Major)
            Fail("MMC_JAVA_PROBE", "Java 实际报告的版本、架构或位数与入口 PE/版本检查不一致。");
        string identity = Fingerprint(new[] { "PCLLF.MmcCleanJavaProbe/v1", CanonicalPath(java.JavaExePath), before.Sha256, before.Architecture,
            before.Bitness.ToString(CultureInfo.InvariantCulture), before.Machine.ToString("x4", CultureInfo.InvariantCulture), before.OptionalMagic.ToString("x4", CultureInfo.InvariantCulture), java.Version.ToString() }
            .Concat(ProbeArguments).Concat(IdentityProperties.SelectMany(key => new[] { key, properties.GetValueOrDefault(key) })));
        return new(java, new(java.JavaExePath, before.Sha256, identity, java.Version.Major, before.Architecture, before.Bitness));
    }
    private static async Task<string> CleanProbeAsync(string executable, CancellationToken token) {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true), WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in ProbeArguments) start.ArgumentList.Add(argument);
        foreach (string key in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new IOException("Java probe process did not start.");
        var stdout = ReadBoundedAsync(process.StandardOutput, token); var stderr = ReadBoundedAsync(process.StandardError, token);
        try {
            await Task.WhenAll(process.WaitForExitAsync(token), stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Java probe process did not succeed.");
            return stdout.Result + "\n" + stderr.Result;
        } catch {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader input, CancellationToken token) {
        StringBuilder value = new(); char[] buffer = new char[4096];
        while (true) { int count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); if (count == 0) break; if (value.Length + count > 1024 * 1024) throw new IOException("Java probe output exceeds its bound."); value.Append(buffer, 0, count); }
        return value.ToString();
    }
    private static Dictionary<string, string> Properties(string output) {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) { string text = line.Trim(); int split = text.IndexOf(" = ", StringComparison.Ordinal); if (split > 0) result[text[..split]] = text[(split + 3)..].Trim(); }
        return result;
    }
    private static async Task<Image> ImageAsync(string executable, CancellationToken token) {
        using FileStream input = new(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 64 or > 64 * 1024 * 1024) Fail("MMC_JAVA_IMAGE", "Java 入口大小无效或超过检查上限。");
        byte[] header = new byte[64]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3c));
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x5a4d || offset < 64 || offset > input.Length - 26) Fail("MMC_JAVA_IMAGE", "Java 入口不是受支持的 Windows PE 程序。");
        input.Position = offset; byte[] pe = new byte[26]; await input.ReadExactlyAsync(pe, token).ConfigureAwait(false);
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4)), optionalBytes = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(20)), magic = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(24));
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x4550 || optionalBytes < 2 || offset + 24L + optionalBytes > input.Length) Fail("MMC_JAVA_IMAGE", "Java PE 头损坏。");
        (string architecture, int bitness) = machine switch { 0x8664 => ("x86_64", 64), 0xaa64 => ("arm64", 64), 0x14c => ("x86", 32), _ => throw new MmcLaunchJavaException("MMC_JAVA_IMAGE", "Java PE 架构不受当前启动流程支持。") };
        if (magic != (bitness == 64 ? 0x20b : 0x10b)) Fail("MMC_JAVA_IMAGE", "Java PE 可选头与实际位数不一致。");
        input.Position = 0; string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        return new(hash, architecture, bitness, machine, magic);
    }
    private static string NormalizeArchitecture(string value) => value switch { "amd64" or "x86_64" => "x86_64", "x86" or "i386" or "i686" => "x86", "aarch64" or "arm64" => "arm64", _ => value };
    private static int ParseMajor(string value) { if (value.StartsWith("1.", StringComparison.Ordinal)) value = value[2..]; return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ? major : -1; }
    private static string BinFolder(string value) {
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Java preference path must be absolute.");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (File.Exists(path) || Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) {
            if (!new[] { "java.exe", "javaw.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Java executable name is unsupported.");
            return Path.GetDirectoryName(path)!;
        }
        if (File.Exists(Path.Combine(path, "java.exe"))) return path;
        if (File.Exists(Path.Combine(path, "bin", "java.exe"))) return Path.Combine(path, "bin");
        throw new FileNotFoundException("No Java executable in the selected folder.");
    }
    private static string CanonicalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant();
    private static bool Within(string folder, string root) {
        string candidate = CanonicalPath(folder), parent = CanonicalPath(root);
        return candidate == parent || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static List<string> InstanceFolders(string root, CancellationToken token) {
        List<string> result = []; Queue<string> directories = new(); directories.Enqueue(root); int visited = 0, entries = 0;
        while (directories.TryDequeue(out string? directory)) {
            token.ThrowIfCancellationRequested();
            if (++visited > 8192) Fail("MMC_JAVA_SEARCH_BUDGET", "实例目录 Java 搜索超过目录上限，请直接指定 Java。");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                token.ThrowIfCancellationRequested(); if (++entries > 100000) Fail("MMC_JAVA_SEARCH_BUDGET", "实例目录 Java 搜索超过条目上限，请直接指定 Java。");
                var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) directories.Enqueue(path);
                else if (Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetDirectoryName(path)!);
            }
        }
        return result;
    }
    private static string Fingerprint(IEnumerable<string?> values) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); Span<byte> length = stackalloc byte[4];
        foreach (string? value in values) { byte[] bytes = Encoding.UTF8.GetBytes(value ?? ""); BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new MmcLaunchJavaException(code, message);
}
