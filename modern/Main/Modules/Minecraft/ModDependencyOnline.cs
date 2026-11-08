// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
using PCLCS;
using OnlineCore = PCLCS.ModDependencyOnlinePipeline<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ModDependencyOnline.LookupOptions, PCL.ModDependencyOnline.LookupCache>;

namespace PCL
{

    // 保留原 LF 注入入口与实际旧类型；网络匹配、缓存、预算、指纹及 HTTP 传输由 C# 实现。
    internal sealed class ModDependencyOnline
    {
        private ModDependencyOnline()
        {
        }

        internal class LookupOptions : ModDependencyLookupOptions<LookupCache>
        {
        }
        internal class LookupCache : ModDependencyLookupCache
        {
        }
        private static readonly LookupCache SharedCache = new LookupCache();
        private static readonly OnlineCore Pipeline = new OnlineCore(SharedCache, DefaultRequest, ModDependencyTransport.ReadArchive);

        // 保留原反射注入的异常类型和构造签名，C# Session 捕获同一基类及实际字段。
        private class HttpFailure : ModDependencyHttpFailure
        {
            public HttpFailure(int Code, int RetrySeconds) : base(Code, RetrySeconds)
            {
            }
        }

        internal static ModDependencyInspector.InspectionReport InspectWithOnline(string ModsFolder, ModDependencyInspector.InspectionContext Context = null, CancellationToken Token = default, LookupOptions Options = null)
        {
            return Pipeline.InspectWithOnline(ModsFolder, Context, Token, Options);
        }
        internal static ModDependencyInspector.InspectionReport Enrich(ModDependencyInspector.InspectionReport Report, string ModsFolder, CancellationToken Token = default, LookupOptions Options = null)
        {
            return Pipeline.Enrich(Report, ModsFolder, Token, Options);
        }
        internal static string SearchQuery(string FullPath)
        {
            return OnlineCore.SearchQuery(FullPath);
        }
        internal static bool IsAllowedApi(string Url)
        {
            return ModDependencyTransport.IsAllowedApi(Url);
        }
        internal static JToken DefaultRequest(string Url, HttpMethod Method, string Body, CancellationToken Token)
        {
            return ModDependencyTransport.DefaultRequest(Url, Method, Body, Token, SignRequest, (Code, RetrySeconds) => new HttpFailure(Code, RetrySeconds));
        }
        private static void SignRequest(string Url, HttpRequestMessage Request)
        {
            var SignedRequest = Request;
            PCL.ModSecret.SecretHeadersSign(Url, ref SignedRequest);
        }
        internal static void ResetIndexLookups()
        {
            Pipeline.ResetIndexLookups();
        }
        internal static ModDependencyInspector.InspectionReport RebuildWithOnline(string ModsFolder, ModDependencyInspector.InspectionContext Context, CancellationToken Token = default, LookupOptions Options = null)
        {
            return Pipeline.RebuildWithOnline(ModsFolder, Context, Token, Options);
        }
        // Original LF private reflection entry; the same Core operation is used by Enrich.
        private static void MapRelations(ModDependencyInspector.InspectionReport Report)
        {
            OnlineCore.MapRelations(Report);
        }
    }
}
