// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using OnlineEngine = PCLCS.InstanceResourceOnlineFiles<PCL.InstanceResourceEntry, PCL.ResourceVersion, PCL.ModNet.NetFile, PCL.InstanceResourceOnlineInfo>;
using OnlineHost = PCLCS.InstanceResourceOnlineHost<PCL.ResourceVersion, PCL.ModNet.NetFile>;

namespace PCL
{

    internal class InstanceResourceOnlineInfo : InstanceResourceOnlineInfoData<InstanceResourceEntry, ResourceVersion>
    {
        internal ResourceProject Project;
    }

    // 原 ResourceVersion / NetFile / LoaderDownload 与注入式网络入口继续由 LF 宿主提供。
    internal sealed class InstanceResourceUpdates
    {
        private static readonly OnlineEngine Files = new OnlineEngine(PathUtils.RemoveExtendedPrefix, CreateHost(), () => new InstanceResourceOnlineInfo());

        private static OnlineHost CreateHost()
        {
            return new OnlineHost()
            {
                ParseVersion = new Func<JObject, int, ResourceVersion>((Data, Kind) => ResourceVersion.FromPlatformJson(Data, Kind == (int)InstanceResourceKind.ResourcePacks ? ResourceTypes.ResourcePack : ResourceTypes.Shader)),
                DownloadAvailable = new Func<ResourceVersion, bool>(Value => Value.DownloadAvailable),
                Size = new Func<ResourceVersion, int>(Value => Value.Size),
                ReleaseType = new Func<ResourceVersion, int>(Value => (int)Value.ReleaseType),
                Hash = new Func<ResourceVersion, string>(Value => Value.Hash),
                FileName = new Func<ResourceVersion, string>(Value => Value.FileName),
                Version = new Func<ResourceVersion, string>(Value => Value.Version),
                Display = new Func<ResourceVersion, string>(Value => Value.Display),
                DownloadUrls = new Func<ResourceVersion, List<string>>(Value => Value.DownloadUrls),
                ReleaseDate = new Func<ResourceVersion, DateTime>(Value => Value.ReleaseDate),
                ToNetFile = new Func<ResourceVersion, string, string, ModNet.NetFile>((Value, FileName, GameVersion) => Value.ToNetFile(FileName, ResourceVersion.DownloadReason.Update, GameVersion)),
                Request = new Func<string, HttpMethod, string, JToken>((Url, Method, Body) => ModDownload.DlModRequest(Url, Method, Body, Body is null ? null : "application/json")),
                Fingerprint = new Func<string, uint>(FileName => new LocalResourceFile(FileName).CurseForgeHash),
                Download = DownloadVersions,
                Warn = new Action<Exception, string>((ErrorValue, Message) => Logger.Warn(ErrorValue, Message))
            };
        }

        private static void DownloadVersions(List<ModNet.NetFile> Files, CancellationToken Cancellation)
        {
            var Loader = new ModNet.LoaderDownload("下载资源更新", Files);
            using (var Registration = Cancellation.Register(Loader.Cancel))
            {
                Loader.WaitForExit(c: Cancellation);
            }
        }

        internal static bool Supported(InstanceResourceKind Kind)
        {
            return OnlineEngine.Supported((int)Kind);
        }
        internal static string FileHash(string FileName, bool Md5 = false)
        {
            return OnlineEngine.FileHash(FileName, Md5);
        }
        internal static List<InstanceResourceOnlineInfo> Check(string Root, IEnumerable<InstanceResourceEntry> Items, InstanceResourceKind Kind, string GameVersion, CancellationToken Token, Func<string, HttpMethod, string, JToken> Request = null, Func<string, uint> Fingerprint = null)
        {
            var result = Files.Check(Root, Items, (int)Kind, GameVersion, Token, Request, Fingerprint);
            foreach (var info in result) {
                Token.ThrowIfCancellationRequested();
                if (info.ProjectMetadata is null || info.CurrentVersion is null ||
                    !ResourceFavoriteIdentity.TryCreate(info.ProjectPlatform, info.ProjectId, out var expected)) continue;
                try {
                    var project = new ResourceProject(info.ProjectMetadata);
                    if (ResourceFavorites.CanFavorite(project) && !string.IsNullOrWhiteSpace(project.RawName) &&
                        ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out var actual) && actual == expected)
                        info.Project = project;
                } catch (Exception error) {
                    // Partial project responses may still be useful to the updater.
                    // They must not become an unrelated or incomplete favorite.
                    Logger.Warn(error, "读取已匹配资源的项目信息失败", LogBehavior.None);
                }
            }
            return result;
        }
        internal static void Install(string Root, IEnumerable<InstanceResourceOnlineInfo> Infos, string Recycle, InstanceResourceKind Kind, string GameVersion, CancellationToken Token, Action<List<ModNet.NetFile>, CancellationToken> Download = null)
        {
            Files.Install(Root, Infos, Recycle, (int)Kind, GameVersion, Token, Download);
        }
    }
}
