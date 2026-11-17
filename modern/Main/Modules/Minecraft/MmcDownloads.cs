using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;
using DownloadRuntime = PCLCS.Network.LegacyNetworkRuntime<PCL.ModNet.NetSource, PCL.ModNet.NetThread, PCL.ModNet.NetFile,
    PCL.ModNet.LoaderDownload, PCL.ModNet.NetManagerClass, PCL.ModLoader.LoaderBase, PCL.ModNet.NetState, PCL.ModBase.LoadState, PCL.MyLoading.MyLoadingState>;

namespace PCL;

// Original LF host integration; governed by the repository LICENCE.
public static class MmcDownloads {
    private static readonly ConditionalWeakTable<ModNet.NetFile, MmcArtifactDownloadSpec> Specifications = new();
    public static Task DrainAsync(ModNet.LoaderDownload loader) => DownloadRuntime.WaitForDownloadQuiescenceAsync(loader);
    public static MmcMetadataStore CreateMetadataStore(string cacheRoot, Uri endpoint = null, MmcMetadataStoreOptions options = null) =>
        new(cacheRoot, endpoint ?? new Uri("https://meta.prismlauncher.org/v1/"), ModNet.StandardMetadataClient, options);

    public static MmcArtifactPrefetchAdapter<ModNet.NetFile> CreatePrefetcher(MmcArtifactCacheRoots roots,
        ProgressProvider progress = null, Action<ModNet.LoaderDownload> onLoaderCreated = null, MmcArtifactLimits limits = null) =>
        new(roots, BuildFile, (files, token) => RunBatchAsync(files, token, progress, onLoaderCreated), limits);

    public static ModNet.NetFile BuildFile(MmcArtifactDownloadSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.MaximumSizeBytes < 0 || spec.ExpectedSize < 0 || spec.ExpectedSize > spec.MaximumSizeBytes ||
            !ValidDigest(spec.ExpectedSha256, 64) || !ValidDigest(spec.ExpectedSha1, 40) || !ValidDigest(spec.ExpectedMd5, 32)) throw new ArgumentException("MMC download constraints are invalid.", nameof(spec));
        if (!Path.IsPathFullyQualified(spec.DownloadPath)) throw new ArgumentException("MMC download target must be absolute.", nameof(spec));
        var urls = spec.Urls.SelectMany(url => Expand(spec.Artifact, url)).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) throw new ArgumentException("MMC download has no source.", nameof(spec));
        var file = new ModNet.NetFile(urls, spec.DownloadPath, new FileChecker {
            ActualSize = spec.ExpectedSize ?? -1, MinSize = 0, Hash = spec.ExpectedSha256 ?? spec.ExpectedSha1 ?? spec.ExpectedMd5
        }) { MaximumSizeBytes = spec.MaximumSizeBytes, AllowCredentialHeaders = false };
        Specifications.Add(file, spec);
        return file;
    }

    /// <summary>
    /// One genuine loader owns the whole batch. Work and WaitForExit run off the UI thread.
    /// Cancellation aborts only this loader, then drains all writers to its targets before returning.
    /// If another loader still shares a file, its work is not aborted; draining waits for it.
    /// onLoaderCreated runs on this background worker and must not synchronously wait for UI.
    /// </summary>
    public static Task RunBatchAsync(ImmutableArray<ModNet.NetFile> files, CancellationToken token = default,
        ProgressProvider progress = null, Action<ModNet.LoaderDownload> onLoaderCreated = null) {
        if (files.IsDefault) throw new ArgumentException("MMC download batch is absent.", nameof(files));
        if (files.IsEmpty) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        var specs = files.Select(file => Specifications.TryGetValue(file, out var spec) ? spec : throw new ArgumentException("Use BuildFile for MMC batch inputs.", nameof(files))).ToArray();
        var byPath = specs.ToDictionary(spec => Path.GetFullPath(spec.DownloadPath), StringComparer.OrdinalIgnoreCase);
        return Task.Run(async () => {
            token.ThrowIfCancellationRequested();
            var loader = new ModNet.LoaderDownload("下载 MMC 运行文件", files);
            Exception failure = null;
            CancellationTokenRegistration cancellation = default;
            object cancellationGate = new(); Task cancelWork = null;
            void QueueCancel() { lock (cancellationGate) cancelWork ??= Task.Run(loader.Cancel); }
            try {
                onLoaderCreated?.Invoke(loader);
                lock (loader.LockState) { token.ThrowIfCancellationRequested(); loader.Start(); }
                cancellation = token.Register(QueueCancel);
                while (loader.State == ModBase.LoadState.Loading) {
                    foreach (var file in loader.Files) {
                        if (byPath.TryGetValue(Path.GetFullPath(file.LocalPath), out var spec) &&
                            (file.FileSize > spec.MaximumSizeBytes || Interlocked.Read(ref file.DownloadDone) > spec.MaximumSizeBytes))
                            loader.Failed(new IOException("MMC 下载超过调用方的文件大小限制。"));
                    }
                    progress?.Set(loader.Progress);
                    await Task.Delay(10).ConfigureAwait(false);
                }
                loader.WaitForExit(StartIfNeeded: false);
            } catch (Exception error) {
                failure = error;
                if (loader.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) loader.Failed(error);
            } finally {
                cancellation.Dispose();
                if (loader.State == ModBase.LoadState.Loading) QueueCancel();
                Task pendingCancel; lock (cancellationGate) pendingCancel = cancelWork;
                if (pendingCancel is not null) {
                    try { await pendingCancel.ConfigureAwait(false); }
                    catch (Exception cancelError) {
                        failure ??= cancelError;
                        if (loader.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading) loader.Failed(cancelError);
                    }
                }
                await DrainAsync(loader).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            foreach (var spec in specs) {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(spec.DownloadPath);
                if (!info.Exists || info.Length > spec.MaximumSizeBytes || spec.ExpectedSize is { } size && info.Length != size)
                    throw new IOException("MMC 下载完成后的实际文件大小不符合约束。");
                // FileChecker carries the stronger digest during transfer. When both are declared,
                // preserve both checks rather than treating the second declaration as an observation.
                foreach (string digest in new[] { spec.ExpectedSha256, spec.ExpectedSha1, spec.ExpectedMd5 }.OfType<string>()) {
                    string error = new FileChecker { Hash = digest, ActualSize = spec.ExpectedSize ?? -1 }.Check(spec.DownloadPath);
                    if (error is not null) throw new IOException(error);
                }
            }
            progress?.Set(1);
        }, CancellationToken.None);
    }

    private static bool ValidDigest(string value, int length) => value is null || value.Length == length && value.All(Uri.IsHexDigit);
    private static IEnumerable<string> Expand(MmcArtifactReference artifact, string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("MMC source is not an ordinary HTTP(S) URI.");
        return artifact.Role == MmcArtifactRole.AssetIndex || MmcArtifactMaterializer.IsAssetObjectReference(artifact) ? ModDownload.DlSourceAssetsGet(url) :
            artifact.Role == MmcArtifactRole.MainJar ? ModDownload.DlSourceLauncherOrMetaGet(url) : ModDownload.DlSourceLibraryGet(url);
    }
}
