// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore.Extensions;

namespace PCL
{

    // 每一跳都重新检查目的地；HttpClientHandler 的自动重定向不会清除 x-api-key。
    internal sealed class SafeRedirectHandler : DelegatingHandler
    {

        public SafeRedirectHandler(HttpMessageHandler Transport) : base(Transport)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Cancellation)
        {
            var Current = Request;
            try
            {
                for (int Hop = 0; Hop <= 10; Hop++)
                {
                    Cancellation.ThrowIfCancellationRequested();
                    var Response = await base.SendAsync(Current, Cancellation).ConfigureAwait(false);
                    int Status = (int)Response.StatusCode;
                    if (!new[] { 301, 302, 303, 307, 308 }.Contains(Status) || Response.Headers.Location is null)
                        return Response;
                    HttpRequestMessage Following = null;
                    try
                    {
                        if (Hop == 10)
                            throw new HttpRequestException("重定向次数过多。");
                        var Destination = new Uri(Current.RequestUri, Response.Headers.Location);
                        if (!new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps }.Contains(Destination.Scheme) || !string.IsNullOrEmpty(Destination.UserInfo) || (Current.RequestUri.Scheme ?? "") == (Uri.UriSchemeHttps ?? "") && (Destination.Scheme ?? "") != (Uri.UriSchemeHttps ?? ""))
                        {
                            throw new HttpRequestException("已拒绝不安全的重定向地址。");
                        }
                        Following = await Current.CloneAsync().ConfigureAwait(false);
                        Following.RequestUri = Destination;
                        // 沿用自动重定向的 Authorization 清理行为；API Key 仅能留在可信 API 主机。
                        Following.Headers.Authorization = null;
                        if (!BuildCredentials.IsCurseForgeRequest(Destination))
                            Following.Headers.Remove("x-api-key");
                        if ((Current.RequestUri.Authority ?? "") != (Destination.Authority ?? ""))
                        {
                            Following.Headers.Remove("Cookie");
                            Following.Headers.Remove("Proxy-Authorization");
                        }
                        if ((Status == 301 || Status == 302) && Current.Method == HttpMethod.Post || Status == 303 && Current.Method != HttpMethod.Head)
                        {
                            Following.Method = HttpMethod.Get;
                            Following.Content?.Dispose();
                            Following.Content = null;
                        }
                    }
                    catch
                    {
                        Following?.Dispose();
                        throw;
                    }
                    finally
                    {
                        Response.Dispose();
                    }
                    if (!ReferenceEquals(Current, Request))
                        Current.Dispose();
                    Current = Following;
                }
                throw new HttpRequestException("重定向次数过多。");
            }
            finally
            {
                if (!ReferenceEquals(Current, Request))
                    Current.Dispose();
            }
        }
    }
}