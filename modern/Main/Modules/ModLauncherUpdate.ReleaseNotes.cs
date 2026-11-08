// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCL;

public static partial class ModLauncherUpdate
{
    internal sealed class CurrentReleaseNotes
    {
        internal string Version = "", Markdown = "", Url = "";
        internal bool FromRelease;
    }

    internal static bool UsesPublishedReleaseNotes(string version, bool prerelease, ModBase.BuildTypes buildType)
    {
        Version core = null; string preview = null;
        return buildType == ModBase.BuildTypes.Release && !prerelease && version != "dev_edition" &&
            ParseReleaseVersion(version, ref core, ref preview) && preview == "";
    }

    internal static CurrentReleaseNotes ReadCurrentReleaseNotes(string version, string summary, UpdateSource source, UpdateTransport transport = null)
    {
        var result = new CurrentReleaseNotes { Version = version, Markdown = summary ?? "", Url = ModBase.LauncherReleasesUrl };
        Version core = null; string preview = null;
        if (version == "dev_edition" || !ParseReleaseVersion(version, ref core, ref preview) || preview != "") return result;
        string tag = "v" + version;
        result.Url += "/tag/" + Uri.EscapeDataString(tag);
        transport ??= new UpdateTransport();
        string exact = "https://api.github.com/repos/" + Repository + "/releases/tags/" + Uri.EscapeDataString(tag);
        foreach (string endpoint in source == UpdateSource.Cnb ? new[] { CnbManifestUrl, exact } : new[] { exact, CnbManifestUrl })
        {
            try
            {
                JObject release = transport.ReadJson(endpoint);
                ValidateReleaseMetadata(release, UpdateChannel.Stable);
                if (!string.Equals(release["tag_name"]?.ToString(), tag, StringComparison.Ordinal) || release["body"]?.Type != JTokenType.String)
                    continue;
                string body = release["body"]!.ToString();
                if (string.IsNullOrWhiteSpace(body) || body.Length > 1024 * 1024) continue;
                result.Markdown = body; result.FromRelease = true;
                return result;
            }
            catch (Exception error) { error.ThrowIfCanceled(); }
        }
        return result;
    }

    internal static bool TryShowCurrentReleaseNotes(bool automatic, string summary, UpdateTransport transport = null)
    {
        if (automatic && FormMain.IsCurrentUpdateLogSuppressed) return true;
        CurrentReleaseNotes notes = UsesPublishedReleaseNotes(ModBase.VersionBaseName, ModBase.ReleaseBaselineIsPrerelease, ModBase.BuildType)
            ? ReadCurrentReleaseNotes(ModBase.VersionBaseName, summary, PreferredUpdateSource(), transport)
            : new CurrentReleaseNotes { Version = ModBase.VersionBaseName, Markdown = summary ?? "", Url = ModBase.LauncherReleasesUrl };
        string caption = "基于 PCL 官方开源版的第三方版本。\r\n" +
            (notes.FromRelease ? "以下为当前版本的完整发布说明。" : "以下为当前版本的本地简述。");
        int choice = ModMain.MyMsgBoxConfirmContent(caption, ModBase.LauncherDisplayName + " " + notes.Version + " 更新说明", "确定",
            () => new ReleaseNotesPromptView(notes.Markdown, FormMain.IsCurrentUpdateLogSuppressed, FormMain.PersistUpdateLogSuppression),
            CancelText: "查看发布页", IsWarn: false);
        if (choice == 2) ModBase.OpenWebsite(notes.Url);
        return true;
    }
}
