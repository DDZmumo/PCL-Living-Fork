using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF implementation. See the repository LICENCE.
internal static class WindowsJavaDiscovery {
    private static readonly (string Key, string Value, string Suffix)[] RegistrationLocations = [
        (@"SOFTWARE\JavaSoft\Java Development Kit", "JavaHome", ""),
        (@"SOFTWARE\JavaSoft\Java Runtime Environment", "JavaHome", ""),
        (@"SOFTWARE\JavaSoft\JDK", "JavaHome", ""),
        (@"SOFTWARE\JavaSoft\JRE", "JavaHome", ""),
        (@"SOFTWARE\Eclipse Adoptium\JDK", "Path", @"hotspot\MSI"),
        (@"SOFTWARE\Eclipse Adoptium\JRE", "Path", @"hotspot\MSI"),
        (@"SOFTWARE\Eclipse Foundation\JDK", "Path", @"hotspot\MSI"),
        (@"SOFTWARE\AdoptOpenJDK\JDK", "Path", @"hotspot\MSI"),
        (@"SOFTWARE\AdoptOpenJDK\JRE", "Path", @"hotspot\MSI"),
        (@"SOFTWARE\AdoptOpenJDK\JDK", "Path", @"openj9\MSI"),
        (@"SOFTWARE\AdoptOpenJDK\JRE", "Path", @"openj9\MSI"),
        (@"SOFTWARE\Microsoft\JDK", "Path", @"hotspot\MSI"),
        (@"SOFTWARE\Azul Systems\Zulu", "InstallationPath", ""),
        (@"SOFTWARE\BellSoft\Liberica", "InstallationPath", ""),
        (@"SOFTWARE\Semeru\JDK", "Path", @"openj9\MSI"),
        (@"SOFTWARE\Semeru\JRE", "Path", @"openj9\MSI")
    ];

    internal static readonly string[] VendorDirectories = [
        "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Amazon Corretto",
        "Zulu", "Azul Systems", "BellSoft", "Semeru", "IBM Semeru"
    ];

    internal static IEnumerable<string> RegisteredHomes(CancellationToken token) {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var homes = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser }) {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                token.ThrowIfCancellationRequested();
                try {
                    using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var (key, value, suffix) in RegistrationLocations) {
                        token.ThrowIfCancellationRequested();
                        try {
                            using RegistryKey? installation = root.OpenSubKey(key);
                            if (installation is null) continue;
                            foreach (string version in installation.GetSubKeyNames()) {
                                token.ThrowIfCancellationRequested();
                                string subkey = suffix.Length == 0 ? version : version + "\\" + suffix;
                                using RegistryKey? entry = installation.OpenSubKey(subkey);
                                if (entry?.GetValue(value) is string home) homes.Add(home);
                            }
                        } catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException) {
                            // Another installer may remove/update this registration while scanning.
                        }
                    }
                } catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException) {
                    // The other registry view and the filesystem search remain available.
                }
            }
        }
        return homes;
    }

    internal static IEnumerable<string> DirectFolders(string? path, IEnumerable<string?> homes) {
        foreach (string raw in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            string? directory = LocalDirectory(raw);
            if (directory is not null && File.Exists(Path.Combine(directory, "java.exe"))) yield return directory;
        }
        foreach (string? home in homes) {
            string? directory = LocalDirectory(home);
            if (directory is null) continue;
            string bin = Path.Combine(directory, "bin");
            if (File.Exists(Path.Combine(bin, "java.exe"))) yield return bin;
            else if (File.Exists(Path.Combine(directory, "java.exe"))) yield return directory;
        }
    }

    private static string? LocalDirectory(string? raw) {
        string? value = raw?.Trim(' ', '\t', '"');
        if (string.IsNullOrWhiteSpace(value)) return null;
        try {
            value = Environment.ExpandEnvironmentVariables(value);
            if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            string full = Path.GetFullPath(value);
            if (full.Split(Path.DirectorySeparatorChar).Contains("WindowsApps", StringComparer.OrdinalIgnoreCase))
                return null;
            return full;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) {
            return null;
        }
    }

    internal static string ActualExecutable(string path) {
        if (OperatingSystem.IsWindows()) {
            try {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return MmcCanonicalPaths.ForHandle(handle);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return path;
    }

    internal static string? ProbeStamp(string executable) {
        try {
            string resolved = ActualExecutable(executable);
            var launcher = new FileInfo(resolved);
            string? bin = Path.GetDirectoryName(executable);
            string? runtime = bin is null ? null : Path.GetDirectoryName(bin);
            if (!launcher.Exists || bin is null || runtime is null) return null;
            var parts = new List<string> { "java-probe/2", resolved.ToUpperInvariant() };
            AddFile(new FileInfo(Path.Combine(bin, "javac.exe")));
            AddFile(launcher);
            AddFile(new FileInfo(Path.Combine(runtime, "release")));
            bool hasRuntimeFiles = false;
            foreach (string relative in new[] { @"lib\modules", @"lib\rt.jar", @"jre\lib\rt.jar" })
                hasRuntimeFiles |= AddFile(new FileInfo(Path.Combine(runtime, relative)));
            foreach (string flavor in new[] { "server", "client", "default" }) {
                hasRuntimeFiles |= AddFile(new FileInfo(Path.Combine(bin, flavor, "jvm.dll")));
                hasRuntimeFiles |= AddFile(new FileInfo(Path.Combine(runtime, "jre", "bin", flavor, "jvm.dll")));
            }
            if (!hasRuntimeFiles) return null; // Without a visible runtime, the executable may be a forwarding shim.
            foreach (string key in new[] { "JAVA_HOME", "JDK_HOME", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" })
                parts.Add(Environment.GetEnvironmentVariable(key) ?? "");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));

            bool AddFile(FileInfo file) {
                bool exists = file.Exists;
                parts.Add(exists ? FormattableString.Invariant($"{file.Length}:{file.LastWriteTimeUtc.Ticks}") : "-");
                return exists;
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return null;
        }
    }

    internal static Task<string> RunVersionProbeAsync(string executable, CancellationToken token) {
        ProcessStartInfo start = new(PathUtils.ToShortPath(executable)) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        return ReadProbeAsync(start, TimeSpan.FromSeconds(15), token);
    }

    internal static async Task<string> ReadProbeAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new IOException("Java 检测进程未启动");
        using SafeWaitHandle? job = TryOwnProbe(process);
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        try {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"Java 检测进程退出代码为 {process.ExitCode}");
            return stdout.Result + "\n" + stderr.Result;
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            throw new TimeoutException("Java 检测超时");
        } finally {
            deadline.Cancel();
            if (job is not null) MmcSessionProcessTree.TryTerminateJobHandle(job, out _);
            if (!process.HasExited) {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    private static SafeWaitHandle? TryOwnProbe(Process process) {
        if (!OperatingSystem.IsWindows()) return null;
        try {
            SafeWaitHandle handle = MmcSessionProcessTree.CreateConfiguredJobHandle(limitActiveProcesses: false);
            if (MmcSessionProcessTree.TryAssignProcessToJob(handle, process, out _)) return handle;
            handle.Dispose();
        } catch (Win32Exception) { }
        return null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader input, CancellationToken token) {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) {
            if (result.Length + count > 1024 * 1024) throw new IOException("Java 检测输出超过大小限制");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    internal static string? Architecture(string? architecture) => architecture?.ToLowerInvariant() switch {
        "amd64" or "x86_64" or "x86-64" or "x64" => "x64",
        "aarch64" or "arm64" => "ARM64",
        "x86" or "x32" or "x86_32" or "x86-32" or "i386" or "i486" or "i586" or "i686" => "x86",
        _ => architecture
    };

    internal static string? Distribution(string? vendor, string? vendorVersion, string? runtimeName) {
        string identity = $"{vendor} {vendorVersion} {runtimeName}".ToLowerInvariant();
        if (identity.Contains("semeru", StringComparison.Ordinal) && identity.Contains("certified", StringComparison.Ordinal))
            return "IBM Semeru Certified";
        foreach (var (marker, name) in new (string, string)[] {
            ("temurin", "Eclipse Temurin"), ("adoptium", "Eclipse Temurin"), ("adoptopenjdk", "AdoptOpenJDK"),
            ("zulu prime", "Azul Zulu Prime"), ("zing", "Azul Zulu Prime"),
            ("zulu", "Azul Zulu"), ("azul", "Azul Zulu"), ("liberica", "BellSoft Liberica"), ("bellsoft", "BellSoft Liberica"),
            ("corretto", "Amazon Corretto"), ("amazon", "Amazon Corretto"), ("microsoft", "Microsoft Build of OpenJDK"),
            ("semeru", "IBM Semeru"), ("openlogic", "OpenLogic"), ("ojdkbuild", "OJDKBuild"),
            ("bisheng", "BiSheng JDK"), ("bi sheng", "BiSheng JDK"), ("trava", "Trava OpenJDK"), ("eliya", "Eliya JDK"),
            ("mandrel", "Mandrel"), ("gluon", "Gluon GraalVM"), ("graalvm", "GraalVM"),
            ("dragonwell", "Alibaba Dragonwell"), ("alibaba", "Alibaba Dragonwell"),
            ("kona", "Tencent Kona"), ("tencent", "Tencent Kona"), ("sapmachine", "SAP SapMachine"),
            ("jetbrains", "JetBrains Runtime"), ("red hat", "Red Hat OpenJDK"), ("eclipse openj9", "Eclipse OpenJ9")
        }) {
            if (identity.Contains(marker, StringComparison.Ordinal)) return name;
        }
        if (identity.Contains("oracle", StringComparison.Ordinal))
            return runtimeName?.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase) == true ? "Oracle OpenJDK" : "Oracle Java";
        return vendor;
    }

    internal static string? VirtualMachine(string? name, string? version) {
        if (name?.Contains("openj9", StringComparison.OrdinalIgnoreCase) == true ||
            version?.Contains("openj9", StringComparison.OrdinalIgnoreCase) == true) return "OpenJ9";
        if (name?.Contains("hotspot", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Contains("openjdk", StringComparison.OrdinalIgnoreCase) == true) return "HotSpot";
        return name;
    }
}
