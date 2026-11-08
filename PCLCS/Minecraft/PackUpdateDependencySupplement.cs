// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using static PCLCS.PackUpdateModInspection;

namespace PCLCS;

internal sealed class PackUpdateDependencyCandidate {
    internal string Platform = "", ProjectId = "", Name = "";
    internal ResourceVersionModelData Version = null!;
    internal List<PackUpdateDependencyCandidate> Alternatives = new();
}

internal sealed class PackUpdateDependencyPreparation {
    internal List<PackUpdateDependencyCandidate> Candidates = new();
    internal List<string> Notes = new();
}

// Prepares metadata against the caller's virtual final mods directory. Installation,
// payload verification and the final combined graph belong to the update transaction.
internal static class PackUpdateDependencySupplement {
    private sealed class Cache : ModDependencyLookupCache { public Cache() { } }
    private sealed class Options : ModDependencyLookupOptions<Cache> { public Options() { } }
    private sealed class Item : ModDependencyAutoFillItemData<ResourceVersionModelData, Item> { public Item() { } }
    private sealed class Activation : DependencyActivationItemData<Mod> { public Activation() { } }
    private sealed class Plan : ModDependencyAutoFillPlanData<Report, Item, Activation> { public Plan() { } }

    internal static PackUpdateDependencyPreparation Prepare(string modsRoot, Report report,
        ISet<string> personalRoots, ISet<string> protectedProjects,
        Func<JObject, ResourceTypes, ResourceVersionModelData> parseVersion,
        Func<string, HttpMethod?, string?, JToken> request, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(personalRoots);
        ArgumentNullException.ThrowIfNull(protectedProjects);
        ArgumentNullException.ThrowIfNull(parseVersion);
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(modsRoot);
        var paths = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var personal = new HashSet<string>(personalRoots.Select(Path.GetFullPath), paths);
        var protectedIds = new HashSet<string>(protectedProjects, StringComparer.Ordinal);
        foreach (string path in personal) EnsureWithin(root, path);
        foreach (Mod mod in report.Mods) EnsureWithin(root, mod.RootPath);
        // Never refresh the installed instance's old author manifest during preparation.
        report.PackOrigin = null;
        PackUpdateDependencyPreparation result = new();
        if (personal.Count == 0) return result;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        int requests = 0;
        JToken? BoundedRequest(string url, HttpMethod method, string? body, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested(); budget.Token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref requests) > 64) throw new IOException("个人前置查询达到请求上限。");
            JToken response = request(url, method, body);
            cancellation.ThrowIfCancellationRequested(); budget.Token.ThrowIfCancellationRequested();
            return response;
        }
        var pipeline = new ModDependencyOnlinePipeline<InspectionContext, Mod, Dependency, Edge, Issue, Impact, Report,
            Project, OnlineDependency, OnlineFile, Options, Cache>(new Cache(), BoundedRequest);
        var host = new ModDependencyAutoFillHost<Report, ResourceVersionModelData, object, Plan, Options> {
            ParseVersion = raw => parseVersion(raw, ResourceTypes.Mod),
            ResourceType = version => version.ResourceType,
            DownloadAvailable = version => version.DownloadAvailable,
            Size = version => version.Size, GameVersions = version => version.GameVersions,
            ModLoaders = version => version.ModLoaders, SetModLoaders = (version, loaders) => version.ModLoaders = loaders,
            Id = version => version.Id, FileName = version => version.FileName,
            Hash = version => version.Hash, Display = version => version.Display,
            DownloadUrls = version => version.DownloadUrls ?? new(),
            SetDownloadUrls = (version, urls) => version.DownloadUrls = urls,
            ReleaseType = version => version.ReleaseTypeCode, ReleaseDate = version => version.ReleaseDate,
            DefaultRequest = BoundedRequest, SafeName = SafeName,
            EnsureWithin = (folder, path) => { EnsureWithin(root, path); EnsureWithin(folder, path); },
            FileHash = (path, md5) => Hash(root, path, md5, budget.Token),
            Enrich = (current, folder, cancellation, options) => {
                if (!paths.Equals(Path.GetFullPath(folder), root)) throw new IOException("前置查询目录不是虚拟更新目录。");
                pipeline.Enrich(current, folder, cancellation, options);
                foreach (OnlineFile file in current.OnlineFiles) {
                    EnsureWithin(root, file.FullPath);
                    if (!personal.Contains(Path.GetFullPath(file.FullPath))) file.Matches.Clear();
                }
                current.PackOrigin = null;
            },
            ToNetFile = (_, _, _, _) => throw PreparationOnly(),
            Download = (_, _, _) => throw PreparationOnly(),
            DeleteStage = (_, _) => throw PreparationOnly(),
            RefreshOptions = () => throw PreparationOnly(),
            ApplyTransaction = (_, _, _, _) => throw PreparationOnly(),
            Info = message => result.Notes.Add(message),
            Warn = (error, message) => result.Notes.Add(message + "：" + error.Message),
            CandidateMismatch = (index, message) => new DependencyCandidateMismatchException(index, message)
        };
        var engine = new ModDependencyAutoFillEngine<InspectionContext, Mod, Dependency, Edge, Issue, Impact, Report,
            Project, OnlineDependency, OnlineFile, ResourceVersionModelData, object, Item, Plan, Activation, Options, Cache>(host);
        var options = new Options { Request = BoundedRequest, ComparePlatformDependencies = true,
            MaxSearchFiles = 0, MaxFiles = 512, MaxRequests = 40, TimeBudgetMs = 12000,
            RequestTimeoutMs = 2000, MaxTotalBytes = 2L * 1024 * 1024 * 1024 };
        try {
            Plan plan = engine.BuildPlan(root, report, budget.Token, options);
            token.ThrowIfCancellationRequested(); budget.Token.ThrowIfCancellationRequested();
            result.Notes.AddRange(plan.Notes);
            result.Notes.AddRange(report.OnlineNotes);
            if (plan.Enables.Count > 0) result.Notes.Add("已禁用的组件保持禁用，本次只准备新增个人前置候选。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Item item in plan.Items) {
                token.ThrowIfCancellationRequested();
                string identity = Identity(item);
                if (item.Replacement is not null || protectedIds.Contains(identity)) {
                    result.Notes.Add("前置候选属于作者组合，未替换或另装作者项目：" + identity);
                    continue;
                }
                if (!seen.Add(identity)) continue;
                PackUpdateDependencyCandidate candidate = Convert(item);
                candidate.Alternatives.AddRange(item.Alternatives.Where(alternative => alternative.Replacement is null &&
                    Identity(alternative) == identity && !protectedIds.Contains(Identity(alternative))).Select(Convert));
                result.Candidates.Add(candidate);
            }
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            result.Candidates.Clear(); result.Notes.Add("个人前置查询超时，未准备自动安装。");
        } catch (IOException error) {
            token.ThrowIfCancellationRequested();
            result.Candidates.Clear(); result.Notes.Add("个人前置查询未完成：" + error.Message);
        }
        token.ThrowIfCancellationRequested();
        result.Notes = result.Notes.Distinct(StringComparer.Ordinal).ToList();
        return result;
    }

    private static string Identity(Item item) => item.Platform.ToLowerInvariant() + ":" + item.ProjectId;
    private static PackUpdateDependencyCandidate Convert(Item item) => new() {
        Platform = item.Platform, ProjectId = item.ProjectId, Name = item.Name, Version = item.Version
    };
    private static InvalidOperationException PreparationOnly() => new("更新前置准备阶段不允许安装、启用或提交文件。");
    private static void EnsureWithin(string root, string path) {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("前置准备文件不在虚拟更新目录中。");
    }
    private static string SafeName(string name) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name.EndsWith('.') || name.EndsWith(' ') || name is "." or "..")
            throw new IOException("前置候选文件名无效。");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9') throw new IOException("前置候选文件名为系统保留名称。");
        return name;
    }
    private static string Hash(string root, string path, bool md5, CancellationToken token) {
        EnsureWithin(root, path);
        using var hash = IncrementalHash.CreateHash(md5 ? HashAlgorithmName.MD5 : HashAlgorithmName.SHA1);
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return System.Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
