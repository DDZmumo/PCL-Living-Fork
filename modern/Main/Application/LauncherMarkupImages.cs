// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCL;

internal static class LauncherMarkupImages
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly SemaphoreSlim Downloads = new(4);

    internal static string CachePath(string tempRoot, string address) =>
        Path.Combine(tempRoot, "MyImage", address.GetStableHashCode() + ".png");

    internal static bool IsRemote(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && LauncherContentDownload.IsHttps(uri);

    internal static Task<(BitmapSource? Image, bool Fresh)> ReadCacheAsync(string path, CancellationToken cancellation) =>
        Task.Run<(BitmapSource? Image, bool Fresh)>(() => {
            try {
                DateTime written = File.GetLastWriteTimeUtc(path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length is <= 0 or > LauncherContentDownload.ImageBytes) return default;
                var bytes = new byte[(int)file.Length];
                file.ReadExactly(bytes);
                cancellation.ThrowIfCancellationRequested();
                return (Decode(bytes), DateTime.UtcNow - written < CacheLifetime);
            } catch (Exception error) when (error is not OperationCanceledException) {
                // A stale or damaged cache must not block loading the primary or backup image.
                return default;
            }
        }, cancellation);

    internal static async Task<BitmapSource?> DownloadAsync(string source, string? fallback, string? cachePath,
        CancellationToken cancellation, HttpClient? transport = null)
    {
        await Downloads.WaitAsync(cancellation).ConfigureAwait(false);
        try {
            foreach (string? address in new[] { source, fallback == source ? null : fallback }) {
                if (!IsRemote(address)) continue;
                try {
                    byte[] bytes = await LauncherContentDownload.FetchAsync(address!, LauncherContentDownload.ImageBytes,
                        cancellation, transport).ConfigureAwait(false);
                    return await Task.Run(() => {
                        BitmapSource image = Decode(bytes);
                        cancellation.ThrowIfCancellationRequested();
                        if (cachePath is not null) {
                            try { FileUtils.WriteAtomic(cachePath, bytes); }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                                Logger.Warn("无法写入主页图片缓存（" + error.GetType().Name + "）");
                            }
                        }
                        return image;
                    }, cancellation).ConfigureAwait(false);
                } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) {
                    Logger.Warn("无法加载自定义内容的图片（" + error.GetType().Name + "）");
                }
            }
            return null;
        } finally { Downloads.Release(); }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return DecodeWebP(bytes);
        using var input = new MemoryStream(bytes, writable: false);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnDemand).Frames[0];
        CheckSize(frame.PixelWidth, frame.PixelHeight);
        var image = new WriteableBitmap(frame);
        image.Freeze();
        return image;
    }

    private static BitmapSource DecodeWebP(byte[] bytes)
    {
        Application.PrepareWebP();
        int width = 0, height = 0;
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try {
            if (Imazen.WebP.Extern.NativeMethods.WebPGetInfo(pin.AddrOfPinnedObject(), (nuint)bytes.Length, ref width, ref height) == 0)
                throw new InvalidDataException("无法读取 WebP 图片尺寸。");
            CheckSize(width, height);
        } finally { pin.Free(); }
        using var bitmap = new Imazen.WebP.SimpleDecoder().DecodeFromBytes(bytes, bytes.Length);
        return new MyBitmap(bitmap).ToImageSource();
    }

    private static void CheckSize(int width, int height)
    {
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 16_000_000)
            throw new InvalidDataException("图片尺寸超出上限。");
    }
}
