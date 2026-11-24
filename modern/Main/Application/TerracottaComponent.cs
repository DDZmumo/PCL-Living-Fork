// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using JsonException = System.Text.Json.JsonException;
using JsonSerializer = System.Text.Json.JsonSerializer;
using System.Text.RegularExpressions;

namespace PCL;

/// <summary>
/// One Terracotta release for this machine: where its archive comes from and the SHA-256 of the archive and of the two
/// files it installs. The bundled release is pinned in source. A newer release installed from the update page has no
/// pinned file hashes before its first install; the install records them in a receipt, and every later start verifies
/// the files against that receipt.
/// </summary>
internal sealed record TerracottaRelease(string Version, string Architecture, long Size, string ArchiveHash,
    string? ExecutableHash, string? RuntimeHash, IReadOnlyList<string> Sources)
{
    internal string ExecutableName => $"terracotta-{Version}-windows-{Architecture}.exe";
    internal string ArchiveName => $"terracotta-{Version}-windows-{Architecture}-pkg.tar.gz";
    internal bool IsBundled => Version == TerracottaComponent.Version;
}

internal enum TerracottaHealth { Missing, Intact, Damaged }

internal static class TerracottaComponent
{
    /// <summary>The release this launcher was built and tested against; always available as the fallback.</summary>
    internal const string Version = "0.4.2";
    internal const string ProjectUrl = "https://github.com/burningtnt/Terracotta";
    internal const string ReleasePrefix = ProjectUrl + "/releases/download/";
    internal const long MaximumArchiveSize = 64L * 1024 * 1024;
    private const string RuntimeName = "VCRUNTIME140.DLL";
    private const string ReceiptName = "selected.json";
    private static readonly Regex VersionText = new(@"\A[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,6}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Text = new(@"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant);

    internal static bool IsSupportedPlatform => RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64;

    internal static string PlatformArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("陶瓦联机支持 Windows x64 和 ARM64。")
    };

    internal static bool IsValidVersion(string? version) => version is not null && VersionText.IsMatch(version);

    internal static TerracottaRelease Bundled()
    {
        string architecture = PlatformArchitecture;
        string suffix = $"v{Version}/terracotta-{Version}-windows-{architecture}-pkg.tar.gz";
        string[] sources = [ReleasePrefix + suffix, "https://gitee.com/burningtnt/Terracotta/releases/download/" + suffix];
        return architecture switch
        {
            "x86_64" => new(Version, architecture, 8135914,
                "07EBE139E3CA5F74576E58B1A96EFE59ABDFBE148D3F1A49BFDCA8B6F70745F0",
                "74C10568A7FEA9C1D38CF8D2D4CA90BAF1517F8E5A26C63D3349DB70BC449796",
                "475AB98B7722E965BD38C8FA6ED23502309582CCF294FF1061CB290C7988F0D1", sources),
            _ => new(Version, architecture, 7211225,
                "ACFAB0A87A02DEDC6DAB7C05303186C8907F56F815548B693FB3324358DA7D14",
                "782C2FA911488D487447694ACCA6B17FA68304C87023FB6814B83A167FC2845F",
                "606CE12F558832EBFD9389DC48C57964AC90EAFAB72721A083E062AFD3688626", sources)
        };
    }

    /// <summary>
    /// The release the lobby starts: a newer release installed from the update page while its receipt is valid,
    /// otherwise the bundled one. A receipt that is malformed, for another architecture or from another host is ignored.
    /// </summary>
    internal static TerracottaRelease Selected(string directory)
    {
        TerracottaRelease bundled = Bundled();
        string receipt = Path.Combine(directory, ReceiptName);
        try
        {
            if (!File.Exists(receipt) || new FileInfo(receipt).Length > 8192) return bundled;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(receipt));
            JsonElement root = document.RootElement;
            string? version = Read(root, "version"), architecture = Read(root, "architecture"), source = Read(root, "source");
            string? archiveHash = Read(root, "archiveHash"), executableHash = Read(root, "executableHash"), runtimeHash = Read(root, "runtimeHash");
            if (!IsValidVersion(version) || version == Version || architecture != bundled.Architecture ||
                !root.TryGetProperty("size", out JsonElement sizeValue) || !sizeValue.TryGetInt64(out long size) || size is <= 0 or > MaximumArchiveSize ||
                !IsSha256(archiveHash) || !IsSha256(executableHash) || !IsSha256(runtimeHash) ||
                source != ReleasePrefix + $"v{version}/terracotta-{version}-windows-{architecture}-pkg.tar.gz")
                return bundled;
            return new TerracottaRelease(version!, architecture!, size, archiveHash!, executableHash, runtimeHash, [source!]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return bundled;
        }

        static string? Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    internal static bool IsSha256(string? value) => value is not null && Sha256Text.IsMatch(value);

    internal static bool HasCachedComponent(string directory)
    {
        if (!IsSupportedPlatform) return false;
        TerracottaRelease release = Selected(directory);
        string installed = InstallDirectory(directory, release);
        return File.Exists(Path.Combine(installed, release.ExecutableName)) &&
            File.Exists(Path.Combine(installed, RuntimeName));
    }

    /// <summary>Checks the selected release's installed files against their recorded SHA-256.</summary>
    internal static async Task<TerracottaHealth> VerifyAsync(string directory, CancellationToken token)
    {
        TerracottaRelease release = Selected(directory);
        string installed = InstallDirectory(directory, release);
        string executable = Path.Combine(installed, release.ExecutableName), runtime = Path.Combine(installed, RuntimeName);
        if (!File.Exists(executable) && !File.Exists(runtime)) return TerracottaHealth.Missing;
        return await FileMatchesAsync(executable, release.ExecutableHash!, token).ConfigureAwait(false) &&
            await FileMatchesAsync(runtime, release.RuntimeHash!, token).ConfigureAwait(false)
            ? TerracottaHealth.Intact : TerracottaHealth.Damaged;
    }

    private static string InstallDirectory(string directory, TerracottaRelease release) => Path.Combine(directory, release.Version + "-" + release.Architecture);

    /// <summary>Returns the selected release's executable, downloading and verifying it first when it is missing or damaged.</summary>
    internal static async Task<string> EnsureAsync(string directory, CancellationToken token,
        Func<ModLoader.LoaderCombo<int>, CancellationToken, Task>? queueDownload = null) =>
        (await EnsureReleaseAsync(directory, Selected(directory), token, queueDownload).ConfigureAwait(false)).Executable;

    /// <summary>
    /// Downloads, verifies and installs a newer release, records its file hashes and selects it. The archive must match
    /// the SHA-256 the upstream release lists for it; the bundled release stays installed beside it as the fallback.
    /// </summary>
    internal static async Task<TerracottaRelease> InstallReleaseAsync(string directory, TerracottaRelease release, CancellationToken token,
        Func<ModLoader.LoaderCombo<int>, CancellationToken, Task>? queueDownload = null)
    {
        if (release.IsBundled || !IsValidVersion(release.Version) || release.Architecture != PlatformArchitecture ||
            !IsSha256(release.ArchiveHash) || release.Size is <= 0 or > MaximumArchiveSize || release.Sources.Count != 1 ||
            release.Sources[0] != ReleasePrefix + $"v{release.Version}/{release.ArchiveName}")
            throw new InvalidDataException("陶瓦联机更新的安装包信息无效。");
        (_, TerracottaRelease installed) = await EnsureReleaseAsync(directory, release with { ExecutableHash = null, RuntimeHash = null }, token, queueDownload)
            .ConfigureAwait(false);
        string receipt = Path.Combine(directory, ReceiptName), staged = receipt + ".tmp";
        await File.WriteAllTextAsync(staged, JsonSerializer.Serialize(new
        {
            version = installed.Version, architecture = installed.Architecture, size = installed.Size, source = installed.Sources[0],
            archiveHash = installed.ArchiveHash, executableHash = installed.ExecutableHash, runtimeHash = installed.RuntimeHash
        }), token).ConfigureAwait(false);
        File.Move(staged, receipt, overwrite: true);
        return installed;
    }

    /// <summary>Selects the bundled release again. The newer release's files stay on disk unless <paramref name="removeSelected"/> is set.</summary>
    internal static async Task UseBundledAsync(string directory, bool removeSelected, CancellationToken token)
    {
        TerracottaRelease selected = Selected(directory);
        File.Delete(Path.Combine(directory, ReceiptName));
        if (removeSelected && !selected.IsBundled) await DeleteFilesAsync(directory, selected, token).ConfigureAwait(false);
    }

    private static async Task<(string Executable, TerracottaRelease Release)> EnsureReleaseAsync(string directory, TerracottaRelease release, CancellationToken token,
        Func<ModLoader.LoaderCombo<int>, CancellationToken, Task>? queueDownload)
    {
        string installed = InstallDirectory(directory, release);
        string executable = Path.Combine(installed, release.ExecutableName);
        string runtime = Path.Combine(installed, RuntimeName);
        if (release.ExecutableHash is { } executableHash && release.RuntimeHash is { } runtimeHash &&
            await FileMatchesAsync(executable, executableHash, token).ConfigureAwait(false) &&
            await FileMatchesAsync(runtime, runtimeHash, token).ConfigureAwait(false))
            return (executable, release);

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(installed);
        string stage = Path.Combine(installed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string archive = Path.Combine(stage, release.ArchiveName);
        string stagedExecutable = Path.Combine(stage, release.ExecutableName);
        string stagedRuntime = Path.Combine(stage, RuntimeName);
        var payload = new ModNet.NetFile(release.Sources.ToArray(), archive,
            new MeloongCore.FileChecker { ActualSize = release.Size, Hash = release.ArchiveHash.ToLowerInvariant() }) {
            MaximumSizeBytes = release.Size, AllowCredentialHeaders = false
        };
        var download = new ModNet.LoaderDownload($"下载陶瓦联机 {release.Version} 安装包", [payload]) { ProgressWeight = 9, Block = true };
        ModLoader.LoaderCombo<int> task = null!;
        TerracottaRelease verified = release;
        var install = new ModLoader.LoaderTask<int, int>("校验并安装陶瓦联机组件", work =>
            verified = InstallAsync(archive, stagedExecutable, stagedRuntime, executable, runtime, release, task, work.CreateCancellationToken())
                .GetAwaiter().GetResult()) { ProgressWeight = 1, Block = true, InterruptOnCancel = false };
        task = new ModLoader.LoaderCombo<int>($"下载并安装陶瓦联机 {release.Version}", [download, install]) {
            OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly
        };
        try
        {
            if (queueDownload is null) task.Start(1);
            else await queueDownload(task, token).ConfigureAwait(false);
            using CancellationTokenRegistration registration = token.Register(task.Cancel);
            await Task.Run(() => task.WaitForExit(StartIfNeeded: false)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (verified.ExecutableHash is null || verified.RuntimeHash is null)
                throw new IOException("陶瓦联机组件没有安装完成。");
            return (executable, verified);
        }
        finally
        {
            if (task.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) task.Cancel();
            // Combo cancellation is immediate, while its children receive cancellation on a worker.
            while (download.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading)
                await Task.Delay(25).ConfigureAwait(false);
            await MmcDownloads.DrainAsync(download).ConfigureAwait(false);
            while (install.HasActiveWorkload == true) await Task.Delay(25).ConfigureAwait(false);
            try
            {
                File.Delete(archive);
                File.Delete(stagedExecutable);
                File.Delete(stagedRuntime);
                Directory.Delete(stage);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MeloongCore.Logger.Warn(error, "清理陶瓦联机安装暂存失败");
            }
        }
    }

    // Verifies the archive, extracts exactly the two expected files and checks them against the release's hashes; a
    // release installed for the first time has none yet and gets the hashes of what was extracted.
    private static async Task<TerracottaRelease> InstallAsync(string archive, string stagedExecutable, string stagedRuntime,
        string executable, string runtime, TerracottaRelease release, ModLoader.LoaderCombo<int> task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (task.State != ModBase.LoadState.Loading) throw new OperationCanceledException();
        if (new FileInfo(archive).Length != release.Size)
            throw new InvalidDataException("陶瓦联机安装包大小不符。");
        byte[] bytes = await File.ReadAllBytesAsync(archive, token).ConfigureAwait(false);
        if (bytes.Length != release.Size || Convert.ToHexString(SHA256.HashData(bytes)) != release.ArchiveHash)
            throw new InvalidDataException("陶瓦联机安装包的 SHA-256 校验失败。");
        await ExtractAsync(bytes, release, stagedExecutable, stagedRuntime, token).ConfigureAwait(false);
        string executableHash = await HashAsync(stagedExecutable, token).ConfigureAwait(false);
        string runtimeHash = await HashAsync(stagedRuntime, token).ConfigureAwait(false);
        if (release.ExecutableHash is { } expectedExecutable && executableHash != expectedExecutable ||
            release.RuntimeHash is { } expectedRuntime && runtimeHash != expectedRuntime)
            throw new InvalidDataException("陶瓦联机组件的文件校验失败。");
        lock (task.LockState)
        {
            token.ThrowIfCancellationRequested();
            if (task.State != ModBase.LoadState.Loading) throw new OperationCanceledException();
            File.Move(stagedRuntime, runtime, overwrite: true);
            File.Move(stagedExecutable, executable, overwrite: true);
        }
        return release with { ExecutableHash = executableHash, RuntimeHash = runtimeHash };
    }

    /// <summary>Removes the selected release's files (and selects the bundled release again if a newer one was selected).</summary>
    internal static async Task UninstallAsync(string directory, CancellationToken token)
    {
        TerracottaRelease release = Selected(directory);
        await DeleteFilesAsync(directory, release, token).ConfigureAwait(false);
        if (!release.IsBundled) File.Delete(Path.Combine(directory, ReceiptName));
    }

    private static async Task DeleteFilesAsync(string directory, TerracottaRelease release, CancellationToken token)
    {
        string installed = InstallDirectory(directory, release);
        // The native process can take a moment to release its executable after acknowledging shutdown.
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Delete(Path.Combine(installed, release.ExecutableName));
                File.Delete(Path.Combine(installed, RuntimeName));
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) when (attempt < 6) { await Task.Delay(125, token).ConfigureAwait(false); }
            catch (IOException error) { throw new IOException("无法卸载陶瓦联机组件，组件文件可能仍在使用。", error); }
        }
    }

    private static async Task ExtractAsync(byte[] archive, TerracottaRelease release, string executable, string runtime, CancellationToken token)
    {
        using var compressed = new GZipStream(new MemoryStream(archive, writable: false), CompressionMode.Decompress);
        using var tar = new TarReader(compressed);
        while (await tar.GetNextEntryAsync(cancellationToken: token).ConfigureAwait(false) is { } entry)
        {
            string target = entry.Name switch
            {
                RuntimeName => runtime,
                var name when name == release.ExecutableName => executable,
                _ => throw new InvalidDataException("陶瓦联机安装包包含非预期文件。")
            };
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is not { } data)
                throw new InvalidDataException("陶瓦联机安装包格式无效。");
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await data.CopyToAsync(output, token).ConfigureAwait(false);
        }
        if (!File.Exists(executable) || !File.Exists(runtime))
            throw new InvalidDataException("陶瓦联机安装包缺少组件文件。");
    }

    private static async Task<bool> FileMatchesAsync(string file, string hash, CancellationToken token) =>
        File.Exists(file) && await HashAsync(file, token).ConfigureAwait(false) == hash;

    private static async Task<string> HashAsync(string file, CancellationToken token)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }
}
