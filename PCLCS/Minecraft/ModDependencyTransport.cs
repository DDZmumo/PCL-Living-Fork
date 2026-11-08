// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>The LF exact-host signing boundary remains supplied by the launcher; transport behavior is C#.</summary>
public static class ModDependencyTransport {
    public static bool IsAllowedArchive(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.Scheme == "https" &&
        address.IsDefaultPort && address.UserInfo == "" &&
        (address.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) && address.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal) ||
         (address.Host.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase) || address.Host.Equals("media.forgecdn.net", StringComparison.OrdinalIgnoreCase)) && address.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal));
    public static byte[] ReadArchive(string url, long maximumBytes, CancellationToken token) {
        if (!IsAllowedArchive(url) || maximumBytes <= 0 || maximumBytes > 4L * 1024 * 1024) throw new InvalidDataException("Unsupported metadata archive.");
        // Artifact reads are unsigned and cannot forward API credentials or follow redirects.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PCL-Living-Fork/1.0");
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Metadata archive exceeds limit.");
        using var input = response.Content.ReadAsStreamAsync(token).GetAwaiter().GetResult();
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = input.ReadAsync(buffer, 0, buffer.Length, token).GetAwaiter().GetResult()) > 0) {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Metadata archive exceeds limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public static bool IsAllowedApi(string? url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) return false;
        Uri address = parsed!;
        if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo != "") return false;
        return (address.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase) && address.AbsolutePath.StartsWith("/v2/", StringComparison.Ordinal)) ||
            (address.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase) && address.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal));
    }
    public static JToken DefaultRequest(string url, HttpMethod method, string? body, CancellationToken token,
        Action<string, HttpRequestMessage> signRequest, Func<int, int, ModDependencyHttpFailure> httpFailure) {
        if (!IsAllowedApi(url)) throw new ArgumentException("Unsupported dependency API address.");
        // Original signed headers never follow redirects; signing does not replace the request being sent.
        using HttpClientHandler handler = new() { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        using HttpClient client = new(handler);
        using HttpRequestMessage request = new(method, url);
        client.Timeout = Timeout.InfiniteTimeSpan;
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        signRequest(url, request);
        using HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) {
            int retrySeconds = 60;
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                retrySeconds = Convert.ToInt32(Math.Min(900, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds));
            else if (response.Headers.RetryAfter?.Date.HasValue == true)
                retrySeconds = Convert.ToInt32(Math.Min(900, (response.Headers.RetryAfter!.Date!.Value - DateTimeOffset.UtcNow).TotalSeconds));
            throw httpFailure((int)response.StatusCode, retrySeconds);
        }
        using Stream input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using MemoryStream buffer = new();
        byte[] bytes = new byte[8192];
        while (true) {
            int count = input.ReadAsync(bytes, 0, bytes.Length, token).GetAwaiter().GetResult();
            if (count == 0) break;
            if (buffer.Length + count > 4L * 1024 * 1024) throw new InvalidDataException("Dependency API response exceeds limit.");
            buffer.Write(bytes, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return JToken.Parse(new UTF8Encoding(false, true).GetString(buffer.ToArray()));
    }
}
