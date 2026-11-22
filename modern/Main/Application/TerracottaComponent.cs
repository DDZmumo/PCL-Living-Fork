// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PCL;

internal static class TerracottaComponent
{
    internal const string Version = "0.4.2";
    internal const string ProjectUrl = "https://github.com/burningtnt/Terracotta";
    private const string RuntimeName = "VCRUNTIME140.DLL";

    private sealed record Package(string Architecture, int Size, string ArchiveHash, string ExecutableHash, string RuntimeHash)
    {
        internal string ExecutableName => $"terracotta-{Version}-windows-{Architecture}.exe";
        internal string ArchiveName => $"terracotta-{Version}-windows-{Architecture}-pkg.tar.gz";
    }

    private static Package ForWindows() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => new("x86_64", 8135914,
            "07EBE139E3CA5F74576E58B1A96EFE59ABDFBE148D3F1A49BFDCA8B6F70745F0",
            "74C10568A7FEA9C1D38CF8D2D4CA90BAF1517F8E5A26C63D3349DB70BC449796",
            "475AB98B7722E965BD38C8FA6ED23502309582CCF294FF1061CB290C7988F0D1"),
        Architecture.Arm64 => new("arm64", 7211225,
            "ACFAB0A87A02DEDC6DAB7C05303186C8907F56F815548B693FB3324358DA7D14",
            "782C2FA911488D487447694ACCA6B17FA68304C87023FB6814B83A167FC2845F",
            "606CE12F558832EBFD9389DC48C57964AC90EAFAB72721A083E062AFD3688626"),
        _ => throw new PlatformNotSupportedException("陶瓦联机支持 Windows x64 和 ARM64。")
    };

    internal static bool HasCachedComponent(string directory)
    {
        if (RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64)) return false;
        Package package = ForWindows();
        string installed = InstallDirectory(directory, package);
        return File.Exists(Path.Combine(installed, package.ExecutableName)) &&
            File.Exists(Path.Combine(installed, RuntimeName));
    }

    private static string InstallDirectory(string directory, Package package) => Path.Combine(directory, Version + "-" + package.Architecture);

    internal static async Task<string> EnsureAsync(string directory, CancellationToken token,
        Func<ModLoader.LoaderCombo<int>, CancellationToken, Task>? queueDownload = null)
    {
        Package package = ForWindows();
        string installed = InstallDirectory(directory, package);
        string executable = Path.Combine(installed, package.ExecutableName);
        string runtime = Path.Combine(installed, RuntimeName);
        if (await FileMatchesAsync(executable, package.ExecutableHash, token).ConfigureAwait(false) &&
            await FileMatchesAsync(runtime, package.RuntimeHash, token).ConfigureAwait(false))
            return executable;

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(installed);
        string stage = Path.Combine(installed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string archive = Path.Combine(stage, package.ArchiveName);
        string stagedExecutable = Path.Combine(stage, package.ExecutableName);
        string stagedRuntime = Path.Combine(stage, RuntimeName);
        string suffix = $"/releases/download/v{Version}/{package.ArchiveName}";
        var payload = new ModNet.NetFile([ProjectUrl + suffix, "https://gitee.com/burningtnt/Terracotta" + suffix], archive,
            new MeloongCore.FileChecker { ActualSize = package.Size, Hash = package.ArchiveHash.ToLowerInvariant() }) {
            MaximumSizeBytes = package.Size, AllowCredentialHeaders = false
        };
        var download = new ModNet.LoaderDownload("下载陶瓦联机安装包", [payload]) { ProgressWeight = 9, Block = true };
        ModLoader.LoaderCombo<int> task = null!;
        var install = new ModLoader.LoaderTask<int, int>("校验并安装陶瓦联机组件", work =>
            InstallAsync(archive, stagedExecutable, stagedRuntime, executable, runtime, package, task, work.CreateCancellationToken())
                .GetAwaiter().GetResult()) { ProgressWeight = 1, Block = true, InterruptOnCancel = false };
        task = new ModLoader.LoaderCombo<int>("下载并安装陶瓦联机", [download, install]) {
            OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly
        };
        try
        {
            if (queueDownload is null) task.Start(1);
            else await queueDownload(task, token).ConfigureAwait(false);
            using CancellationTokenRegistration registration = token.Register(task.Cancel);
            await Task.Run(() => task.WaitForExit(StartIfNeeded: false)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return executable;
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

    private static async Task InstallAsync(string archive, string stagedExecutable, string stagedRuntime,
        string executable, string runtime, Package package, ModLoader.LoaderCombo<int> task, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (task.State != ModBase.LoadState.Loading) throw new OperationCanceledException();
        if (new FileInfo(archive).Length != package.Size)
            throw new InvalidDataException("陶瓦联机安装包大小不符。");
        byte[] bytes = await File.ReadAllBytesAsync(archive, token).ConfigureAwait(false);
        if (bytes.Length != package.Size || Convert.ToHexString(SHA256.HashData(bytes)) != package.ArchiveHash)
            throw new InvalidDataException("陶瓦联机安装包的 SHA-256 校验失败。");
        await ExtractAsync(bytes, package, stagedExecutable, stagedRuntime, token).ConfigureAwait(false);
        if (!await FileMatchesAsync(stagedExecutable, package.ExecutableHash, token).ConfigureAwait(false) ||
            !await FileMatchesAsync(stagedRuntime, package.RuntimeHash, token).ConfigureAwait(false))
            throw new InvalidDataException("陶瓦联机组件的文件校验失败。");
        lock (task.LockState)
        {
            token.ThrowIfCancellationRequested();
            if (task.State != ModBase.LoadState.Loading) throw new OperationCanceledException();
            File.Move(stagedRuntime, runtime, overwrite: true);
            File.Move(stagedExecutable, executable, overwrite: true);
        }
    }

    internal static async Task UninstallAsync(string directory, CancellationToken token)
    {
        Package package = ForWindows();
        string installed = InstallDirectory(directory, package);
        // The native process can take a moment to release its executable after acknowledging shutdown.
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Delete(Path.Combine(installed, package.ExecutableName));
                File.Delete(Path.Combine(installed, RuntimeName));
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) when (attempt < 6) { await Task.Delay(125, token).ConfigureAwait(false); }
            catch (IOException error) { throw new IOException("无法卸载陶瓦联机组件，组件文件可能仍在使用。", error); }
        }
    }

    private static async Task ExtractAsync(byte[] archive, Package package, string executable, string runtime, CancellationToken token)
    {
        using var compressed = new GZipStream(new MemoryStream(archive, writable: false), CompressionMode.Decompress);
        using var tar = new TarReader(compressed);
        while (await tar.GetNextEntryAsync(cancellationToken: token).ConfigureAwait(false) is { } entry)
        {
            string target = entry.Name switch
            {
                RuntimeName => runtime,
                var name when name == package.ExecutableName => executable,
                _ => throw new InvalidDataException("陶瓦联机安装包包含非预期文件。")
            };
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is not { } data)
                throw new InvalidDataException("陶瓦联机安装包格式无效。");
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await data.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }

    private static async Task<bool> FileMatchesAsync(string file, string hash, CancellationToken token)
    {
        if (!File.Exists(file)) return false;
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) == hash;
    }
}
