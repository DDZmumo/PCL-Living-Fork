using System.Net.Http;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF implementation. See the repository LICENCE.
public sealed class ManagedJavaRuntimeStore
{
    private const string ManifestName = "PCLLF.JavaRuntime.json";
    private static readonly SemaphoreSlim changes = new(1, 1);
    private readonly string root;
    private readonly HttpClient client;
    private readonly MmcJavaPinStore pins;

    private sealed record Manifest(int Schema, JavaRuntimePackage Package, string Sha256, string Executable, string? Sha512 = null);
    private sealed record Installation(string Directory, Manifest Receipt, string Executable);

    // Owns the installation reservation and staged ZIP across the download and install tasks.
    public sealed class PreparedInstall : IDisposable
    {
        private FileStream? packageGate;
        private int installing;
        internal readonly ManagedJavaRuntimeStore Store;
        internal readonly JavaRuntimePackage Package;
        internal readonly JavaRuntimeArchive Archive;
        internal readonly string Target;
        internal readonly string? Stage, ExistingExecutable;

        internal PreparedInstall(ManagedJavaRuntimeStore store, JavaRuntimePackage package, JavaRuntimeArchive archive,
            string target, string? stage, string? existingExecutable, FileStream packageGate)
        {
            Store = store; Package = package; Archive = archive; Target = target;
            Stage = stage; ExistingExecutable = existingExecutable; this.packageGate = packageGate;
        }

        internal void BeginInstall()
        {
            if (packageGate is null || Interlocked.Exchange(ref installing, 1) != 0)
                throw new InvalidOperationException("Java 安装准备已被使用或释放。");
        }

        public void Dispose()
        {
            FileStream? gate = Interlocked.Exchange(ref packageGate, null);
            if (gate is not null) Store.ReleaseInstall(Stage, gate);
        }
    }

    public static string DefaultRoot => Path.Combine(Paths.Base, "PCL", "Java");

    public ManagedJavaRuntimeStore(string root, HttpClient client, MmcJavaPinStore pins)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Java 安装路径必须是绝对路径。", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.client = client;
        this.pins = pins;
    }

    public static bool IsManaged(string root, Java java) => GetInstalledPackage(root, java) is not null;

    public static JavaRuntimePackage? GetInstalledPackage(string root, Java java) => FindInstallation(root, java)?.Receipt.Package;

    internal static IEnumerable<string> InstalledFolders(string root, CancellationToken token)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (string directory in PackageDirectories(full))
        {
            token.ThrowIfCancellationRequested();
            if (ReadInstallation(directory) is { } installation)
                yield return Path.GetDirectoryName(installation.Executable)!;
        }
    }

    public Task<Java> InstallAsync(JavaRuntimePackage package, JavaRuntimeArchive archive, CancellationToken token,
        Action<double>? progress = null, Action<Action>? commit = null) =>
        InstallCoreAsync(package, archive, token, progress, commit, static (java, ct) => java.CheckAsync(ct));

    internal async Task<Java> InstallCoreAsync(JavaRuntimePackage package, JavaRuntimeArchive archive, CancellationToken token,
        Action<double>? progress, Action<Action>? commit, Func<Java, CancellationToken, Task<bool>> probe)
    {
        using PreparedInstall prepared = await PrepareInstallAsync(package, archive, token,
            FileTaskProgress.Slice(progress, 0, .72)).ConfigureAwait(false);
        return await InstallPreparedCoreAsync(prepared, token, FileTaskProgress.Slice(progress, .72, 1), commit, probe)
            .ConfigureAwait(false);
    }

    public async Task<PreparedInstall> PrepareInstallAsync(JavaRuntimePackage package, JavaRuntimeArchive archive,
        CancellationToken token, Action<double>? progress = null)
    {
        if (!ValidPackage(package) || !archive.HasValidChecksum)
            throw new InvalidDataException("Java 安装包标识或校验值无效。");
        await changes.WaitAsync(token).ConfigureAwait(false);
        string? stage = null;
        FileStream? packageGate = null;
        try
        {
            MmcInstancePaths.EnsureNoLinks(root);
            Directory.CreateDirectory(root);
            packageGate = await LockPackageAsync(package.Id, token).ConfigureAwait(false);
            string target = Path.Combine(root, package.Id.ToLowerInvariant());
            MmcInstancePaths.EnsureNoLinks(target);
            if (Directory.Exists(target))
            {
                Installation installed = ReadInstallation(target) ?? throw new IOException("Java 安装目录已被其他文件占用。");
                if (!installed.Receipt.Package.IsSameArchive(package) ||
                    !string.Equals(installed.Receipt.Sha256, archive.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(installed.Receipt.Sha512, archive.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("已安装的 Java 与选择的安装包不符。");
                progress?.Invoke(1);
                return new(this, package, archive, target, null, installed.Executable, packageGate);
            }

            stage = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
            MmcInstancePaths.EnsureNoLinks(stage);
            Directory.CreateDirectory(stage);
            await ManagedJavaArchive.DownloadAsync(client, package, archive, stage, progress, token).ConfigureAwait(false);
            return new(this, package, archive, target, stage, null, packageGate);
        }
        catch
        {
            ReleaseInstall(stage, packageGate);
            throw;
        }
    }

    public Task<Java> InstallPreparedAsync(PreparedInstall prepared, CancellationToken token,
        Action<double>? progress = null, Action<Action>? commit = null) =>
        InstallPreparedCoreAsync(prepared, token, progress, commit, static (java, ct) => java.CheckAsync(ct));

    internal async Task<Java> InstallPreparedCoreAsync(PreparedInstall prepared, CancellationToken token,
        Action<double>? progress, Action<Action>? commit, Func<Java, CancellationToken, Task<bool>> probe)
    {
        if (!ReferenceEquals(prepared.Store, this)) throw new ArgumentException("Java 安装目录不一致。", nameof(prepared));
        prepared.BeginInstall();
        token.ThrowIfCancellationRequested();
        JavaRuntimePackage package = prepared.Package;
        if (prepared.ExistingExecutable is { } existingExecutable)
        {
            var existing = new Java(Path.GetDirectoryName(existingExecutable)!);
            await VerifyJavaAsync(existing, package, probe, token).ConfigureAwait(false);
            Publish(() => Register(existing), commit, token);
            progress?.Invoke(1);
            return existing;
        }

        string stage = prepared.Stage!;
        string payload = Path.Combine(stage, "payload");
        MmcInstancePaths.EnsureNoLinks(payload);
        Directory.CreateDirectory(payload);
        await ManagedJavaArchive.VerifyAndExtractAsync(stage, payload, package, prepared.Archive, progress, token)
            .ConfigureAwait(false);
        string executable = FindJavaExecutable(payload);
        var verified = new Java(Path.GetDirectoryName(executable)!);
        await VerifyJavaAsync(verified, package, probe, token).ConfigureAwait(false);
        string relative = Path.GetRelativePath(payload, executable).Replace('\\', '/');
        var receipt = new Manifest(1, package, prepared.Archive.Sha256.ToLowerInvariant(), relative, prepared.Archive.Sha512?.ToLowerInvariant());
        using (var output = new FileStream(Path.Combine(payload, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: token).ConfigureAwait(false);

        string target = prepared.Target;
        Java? result = null;
        Publish(() =>
        {
            MmcInstancePaths.EnsureNoLinks(payload);
            MmcInstancePaths.EnsureNoLinks(target);
            Directory.Move(payload, target);
            try
            {
                result = verified.RelocateDiscoveryResult(Path.GetDirectoryName(ManagedJavaArchive.ResolveInside(target, relative))!);
                Register(result);
            }
            catch
            {
                Directory.Move(target, payload);
                throw;
            }
        }, commit, token);
        progress?.Invoke(1);
        return result!;
    }

    private void ReleaseInstall(string? stage, FileStream? packageGate)
    {
        try { if (stage is not null) DeleteTemporaryDirectory(stage); }
        finally
        {
            try { packageGate?.Dispose(); }
            finally { changes.Release(); }
        }
    }

    public async Task UninstallAsync(Java java, Func<bool> canUninstall, CancellationToken token, Action<Action>? commit = null)
    {
        await changes.WaitAsync(token).ConfigureAwait(false);
        string? removed = null;
        var leases = new List<IDisposable>();
        try
        {
            Installation installation = FindInstallation(root, java) ??
                throw new IOException("这套 Java 不在启动器管理的安装目录中。");
            using FileStream packageGate = await LockPackageAsync(installation.Receipt.Package.Id, token).ConfigureAwait(false);
            installation = FindInstallation(root, java) ?? throw new IOException("这套 Java 已经卸载。");
            MmcJavaPinStore.ValidateMaintenanceTarget(installation.Directory);
            foreach (string home in Directory.EnumerateFiles(installation.Directory, "java.exe", SearchOption.AllDirectories)
                .Select(JavaHome).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                leases.Add(pins.EnterMaintenance(home, TimeSpan.FromSeconds(5), token));
            string retired = Path.Combine(root, ".uninstall-" + Guid.NewGuid().ToString("N"));
            Publish(() =>
            {
                if (!canUninstall()) throw new IOException("请在游戏退出、启动任务结束后再卸载 Java。");
                lock (Configs.JavaList)
                {
                    string prefix = installation.Directory + Path.DirectorySeparatorChar;
                    var current = Configs.JavaList.Get()!;
                    ConcurrentList<Java> remaining = [..current.Where(entry =>
                        !entry.JavaExePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
                    MmcInstancePaths.EnsureNoLinks(installation.Directory);
                    MmcInstancePaths.EnsureNoLinks(retired);
                    Directory.Move(installation.Directory, retired);
                    removed = retired;
                    try { Configs.JavaList.Set(remaining); }
                    catch
                    {
                        Directory.Move(retired, installation.Directory);
                        removed = null;
                        Configs.JavaList.Set(current);
                        throw;
                    }
                }
            }, commit, token);
        }
        finally
        {
            try { if (removed is not null) DeleteTemporaryDirectory(removed); }
            finally
            {
                for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
                changes.Release();
            }
        }
    }

    private async Task<FileStream> LockPackageAsync(string id, CancellationToken token)
    {
        string directory = Path.Combine(root, ".locks");
        MmcInstancePaths.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, id.ToLowerInvariant() + ".lock");
        for (int retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            MmcInstancePaths.EnsureNoLinks(file);
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (retry < 49) { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }

    private static string JavaHome(string executable)
    {
        string folder = Path.GetDirectoryName(executable)!;
        return string.Equals(Path.GetFileName(folder), "bin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(folder)! : folder;
    }

    private static void Publish(Action publish, Action<Action>? commit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (commit is null) publish();
        else commit(publish);
    }

    private static void Register(Java java)
    {
        lock (Configs.JavaList)
        {
            var previous = Configs.JavaList.Get()!;
            var exclusions = Configs.JavaRemovedList.Get()!;
            var current = previous.ToList();
            var remaining = exclusions.Where(path => !PathUtils.IsSame(path, java.Folder)).ToList();
            int index = current.IndexOf(java);
            if (index < 0) current.Add(java);
            else current[index] = java;
            try
            {
                if (remaining.Count != exclusions.Count) Configs.JavaRemovedList.Set(remaining);
                Configs.JavaList.Set([..current]);
            }
            catch
            {
                if (remaining.Count != exclusions.Count) Configs.JavaRemovedList.Set(exclusions);
                Configs.JavaList.Set(previous);
                throw;
            }
        }
    }

    private static async Task VerifyJavaAsync(Java java, JavaRuntimePackage package, Func<Java, CancellationToken, Task<bool>> probe,
        CancellationToken token)
    {
        if (!await probe(java, token).ConfigureAwait(false) || java.Version?.Major != package.MajorVersion ||
            !string.Equals(java.Architecture, package.Architecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Java 检测失败，或实际版本／架构与安装包不符。");
    }

    private static string FindJavaExecutable(string payload)
    {
        string[] homes = [payload, ..Directory.EnumerateDirectories(payload)];
        string[] candidates = homes.Select(home => Path.Combine(home, "bin", "java.exe")).Where(File.Exists).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("Java 安装包中没有唯一的 bin/java.exe。");
        MmcInstancePaths.EnsureNoLinks(candidates[0]);
        return candidates[0];
    }

    private static Installation? FindInstallation(string root, Java java)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string exe = Path.GetFullPath(java.JavaExePath);
        string relative = Path.GetRelativePath(full, exe);
        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        if (separator <= 0 || !ValidId(relative[..separator])) return null;
        Installation? installation = ReadInstallation(Path.Combine(full, relative[..separator]));
        return installation is not null && PathUtils.IsSame(installation.Executable, exe) ? installation : null;
    }

    private static Installation? ReadInstallation(string directory)
    {
        string id = Path.GetFileName(directory);
        if (!ValidId(id)) return null;
        try
        {
            string file = Path.Combine(directory, ManifestName);
            MmcInstancePaths.EnsureNoLinks(file);
            if (!File.Exists(file)) return null;
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 16_384) return null;
            var receipt = JsonSerializer.Deserialize<Manifest>(input);
            if (receipt is not { Schema: 1, Package: { }, Executable: { } } || !ValidPackage(receipt.Package) ||
                !id.Equals(receipt.Package.Id, StringComparison.OrdinalIgnoreCase) ||
                !(receipt.Sha512 is null ? ManagedJavaArchive.IsSha256(receipt.Sha256) :
                    string.IsNullOrEmpty(receipt.Sha256) && ManagedJavaArchive.IsSha512(receipt.Sha512)))
                return null;
            string executable = ManagedJavaArchive.ResolveInside(directory, receipt.Executable);
            MmcInstancePaths.EnsureNoLinks(executable);
            if (!Path.GetFileName(executable).Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), "bin", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable)) return null;
            return new(directory, receipt, executable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    private static string[] PackageDirectories(string root)
    {
        try
        {
            MmcInstancePaths.EnsureNoLinks(root);
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            if (!string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory)) return;
            MmcJavaPinStore.ValidateMaintenanceTarget(directory);
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Logger.Warn(error, "清理 Java 临时安装文件失败"); }
    }

    private static bool ValidId(string? value) => value is { Length: >= 1 and <= 32 } && value.All(Uri.IsHexDigit);

    private static bool ValidPackage(JavaRuntimePackage package) => ValidId(package.Id) &&
        package.MajorVersion is >= JavaRuntimeCatalog.MinimumMajor and <= JavaRuntimeCatalog.MaximumMajor &&
        package.Architecture is "x64" or "arm64" or "x86" &&
        package.PackageType is "jdk" or "jre" && package.Size is >= 0 and <= ManagedJavaArchive.MaximumBytes &&
        package.Distribution is not null && JavaRuntimeCatalog.Distributions.ContainsKey(package.Distribution);
}
