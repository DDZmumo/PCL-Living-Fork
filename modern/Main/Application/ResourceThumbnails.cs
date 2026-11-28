// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using MeloongCore.Extensions;
using PCLCS;

namespace PCL;

internal static class ResourceThumbnails
{
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> InFlight = new(StringComparer.Ordinal);

    internal static Task<ImageSource?> ReadAsync(ResourceProject project)
    {
        string? source = null, fallback = null;
        ResourcePresentation.ApplyLogo(project, value => source = value, value => fallback = value);
        return IsWebAddress(source) ? ReadSharedAsync(source!, fallback) : Task.FromResult<ImageSource?>(null);
    }

    internal static Task<ImageSource?> ReadLocalResourceAsync(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? ReadSharedAsync(uri.AbsoluteUri, null) : Task.FromResult<ImageSource?>(null);

    private static async Task<ImageSource?> ReadSharedAsync(string address, string? fallback)
    {
        var pending = InFlight.GetOrAdd(address, key => new Lazy<Task<ImageSource?>>(() => ReadAsync(key, fallback)));
        try { return await pending.Value.ConfigureAwait(false); }
        finally { InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<ImageSource?>>>(address, pending)); }
    }

    private static bool IsWebAddress(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var address) &&
        address.Scheme is "https" or "http";

    internal static string? CachedPath(ResourceProject project) =>
        IsWebAddress(project.LogoUrl) ? CachePath(project.LogoUrl!) : null;

    private static string CachePath(string address) => Path.Combine(ModBase.PathTemp, "MyImage", address.GetStableHashCode() + ".png");

    private static async Task<ImageSource?> ReadAsync(string address, string? fallback)
    {
        await Downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                string path = CachePath(address);
                var cached = InstanceResourceStore.LoadImage(path, 64);
                if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7)) return cached;
                try
                {
                    string[] urls = IsWebAddress(fallback) && fallback != address ? [address, fallback!] : [address];
                    ModNet.NetDownloadByLoader(urls, path, SimulateBrowserHeaders: true);
                    return InstanceResourceStore.LoadImage(path, 64) ?? cached;
                }
                catch (Exception error)
                {
                    MeloongCore.Logger.Warn(error, "加载资源图标失败");
                    return cached;
                }
            }).ConfigureAwait(false);
        }
        finally { Downloads.Release(); }
    }
}
