// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PCL;

internal sealed record LauncherReleaseEntry(string Version, string Name, string Info, JObject? Release,
    bool IsPrerelease, bool IsDevelopment, bool CanInstall, string? NotesReason);

internal sealed class LauncherReleaseCatalog {
    private static readonly IComparer<string> VersionComparer = Comparer<string>.Create(ModLauncherUpdate.CompareReleaseVersions);
    private IReadOnlyList<LauncherReleaseEntry> entries = Array.Empty<LauncherReleaseEntry>();
    private List<JObject> releases = new();
    private int revision;
    internal bool IsChecking { get; private set; }
    internal bool HasChecked { get; private set; }
    internal string? Error { get; private set; }
    internal JObject? LatestUpdateRelease { get; private set; }
    internal int Count => entries.Count;
    internal int DisplayRevision { get; private set; }
    internal event Action? Changed;
    internal Func<ModLauncherUpdate.UpdateSource, ModLauncherUpdate.UpdateChannel, List<JObject>> ReadReleases { get; set; } =
        (source, channel) => ModLauncherUpdate.GetPageReleases(source, channel);
    internal Action<JObject> RequestUpdate { get; set; } =
        release => ModLauncherUpdate.RequestSelectedUpdate(release, ModLauncherUpdate.PreferredUpdateSource());

    internal LauncherReleaseCatalog() => ApplyChannel();

    internal async Task CheckAsync() {
        if (IsChecking) return;
        int generation = Interlocked.Increment(ref revision);
        var source = ModLauncherUpdate.PreferredUpdateSource();
        releases.Clear();
        IsChecking = true;
        HasChecked = false;
        Error = null;
        ApplyChannel();

        List<JObject>? result = null;
        string? error = null;
        try {
            result = await Task.Run(() => ReadReleases(source, ModLauncherUpdate.UpdateChannel.StableAndPrerelease));
        } catch (Exception failure) {
            error = PclLogger.ModernizeRuntimeMessage(failure.GetDisplay(false));
        }
        if (generation == Volatile.Read(ref revision)) SetReleases(result ?? new List<JObject>(), error);
    }

    internal void Reset() {
        Interlocked.Increment(ref revision);
        releases.Clear();
        IsChecking = false;
        HasChecked = false;
        Error = null;
        ApplyChannel();
    }

    internal void SetReleases(IEnumerable<JObject> result, string? error) {
        releases = result.Select(entry => (JObject)entry.DeepClone()).ToList();
        IsChecking = false;
        HasChecked = true;
        Error = error;
        ApplyChannel();
    }

    internal void ApplyChannel() {
        string version = ModBase.VersionBaseName;
        var channel = ModLauncherUpdate.PreferredUpdateChannel();
        bool development = version == "dev_edition";
        var unfiltered = new List<LauncherReleaseEntry> {
            new(version, "PCL Living Fork", development ? "当前构建 · 本机开发版" : "当前已安装版本",
                null, !development && version.Contains('-'), development, false, null)
        };
        LatestUpdateRelease = null;
        foreach (JObject release in releases.OrderByDescending(entry => entry["tag_name"]?.ToString(), VersionComparer)) {
            string tag = release["tag_name"]?.ToString() ?? string.Empty;
            int comparison = ModLauncherUpdate.CompareReleaseVersions(tag, version);
            if (!development && comparison == 0) continue;
            bool preview = (bool?)release["prerelease"] == true;
            bool outsideChannel = preview && channel == ModLauncherUpdate.UpdateChannel.Stable;
            bool installable = ModLauncherUpdate.CanInstallPageRelease(release);
            bool update = installable && ModLauncherUpdate.NewerReleaseForChannel(tag, version, channel);
            if (update) LatestUpdateRelease ??= release;
            string info = !installable ? "仅可查看更新说明 · 无自动更新附件" : update ? "可更新 · 点击下载并更新" :
                outsideChannel ? "当前更新通道未启用 · 可查看更新说明" : comparison < 0 ? "历史版本 · 可查看更新说明" : "已发布 · 可查看更新说明";
            string reason = outsideChannel ? "当前仅接收正式版更新。可在上方更新通道中开启预发布版更新。" :
                !installable ? "此发布没有可用于自动更新的完整附件。" : "当前没有比此构建更新的版本。";
            unfiltered.Add(new(tag, release["name"]?.ToString() ?? string.Empty, info, release, preview, false, update, reason));
        }
        entries = unfiltered.OrderByDescending(entry => entry.Version, VersionComparer).ToArray();
        DisplayRevision++;
        Changed?.Invoke();
    }

    internal IReadOnlyList<LauncherReleaseEntry> Filter(string input) {
        string query = input.Trim();
        if (query.Length > 1 && (query[0] == 'v' || query[0] == 'V') && char.IsDigit(query[1])) query = query[1..];
        return query.Length == 0 ? entries : entries.Where(entry =>
            (entry.Version + " " + entry.Name).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
