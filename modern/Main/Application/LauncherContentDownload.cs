// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL;

internal static class LauncherContentDownload
{
    internal const int MarkupBytes = 2 * 1024 * 1024;
    internal const int HelpMetadataBytes = 64 * 1024;
    internal const int VersionBytes = 4096;
    internal const int ImageBytes = 8 * 1024 * 1024;

    private static readonly HttpClient Client = new(new SafeRedirectHandler(new HttpClientHandler {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }));

    internal static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", Host.Length: > 0, UserInfo.Length: 0 };

    internal static async Task<byte[]> FetchAsync(string address, int maximum, CancellationToken cancellation = default, HttpClient? transport = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsHttps(uri))
            throw new InvalidDataException("联网内容需要使用 HTTPS 地址。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PCL-Living-Fork");
            using var response = await (transport ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw TooLarge(maximum);
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await ReadAsync(input, maximum, deadline.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
            throw new TimeoutException("读取联网内容超时。");
        }
    }

    internal static async Task<string> FetchTextAsync(string address, int maximum, CancellationToken cancellation = default) =>
        DecodeText(await FetchAsync(address, maximum, cancellation).ConfigureAwait(false));

    internal static string ReadFile(string path, int maximum)
    {
        using var input = File.OpenRead(path);
        if (input.Length > maximum) throw TooLarge(maximum);
        return DecodeText(ReadAsync(input, maximum, CancellationToken.None).GetAwaiter().GetResult());
    }

    private static async Task<byte[]> ReadAsync(Stream input, int maximum, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try {
            while (true) {
                int count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)), cancellation).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                if (count > maximum - output.Length) throw TooLarge(maximum);
                output.Write(buffer, 0, count);
            }
        } finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static InvalidDataException TooLarge(int maximum) => new($"内容大小超过 {maximum / 1024} KiB。");

    private static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
