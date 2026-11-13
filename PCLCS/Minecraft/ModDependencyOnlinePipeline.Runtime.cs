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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class ModDependencyOnlinePipeline<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile,TOptions,TCache> {
    // A remote project's loader ID is established by a content-verified archive,
    // never by its title, slug, filename, author or source repository.
    private static void VerifyRuntimeIdentities(Session work) {
        if (work.ArchiveRequest is null || !work.Report.Context.LoaderKnown ||
            ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.IsLegacyForge(work.Report.Context)) return;
        var localProviders = work.Report.Edges.Where(edge => edge.Owner.Enabled &&
                edge.Requirement.KindValue is not (ModDependencyKind.Conflict or ModDependencyKind.Incompatible) && edge.Evaluated && !edge.ConditionBypassed &&
                edge.StateValue == ModDependencyMatchState.Satisfied && edge.Providers.Any(provider => !provider.IsBuiltin && provider.Enabled && !provider.IncompatibleLoader))
            .ToLookup(edge => ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.FilePathKey(edge.Owner.RootPath), StringComparer.OrdinalIgnoreCase);
        bool Alternative(TDependency dependency) => dependency.Children.Count > 0 &&
            (dependency.Join == "any" || dependency.Children.Any(Alternative));
        // Give explicit replacement/alternative declarations the bounded proof
        // budget before unrelated project-ID gaps; reuse every cached proof.
        var candidates = work.Report.OnlineFiles.Where(file => file.Enabled && file.IsSupplementTarget)
            .Select(file => new { File = file, Edges = localProviders[ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.FilePathKey(file.FullPath)].ToArray() })
            .Where(value => value.Edges.Length > 0)
            .OrderByDescending(value => value.Edges.Any(edge => Alternative(edge.Requirement) || edge.Providers.Any(provider =>
                provider.Provides.Keys.Any(id => id != provider.Id && ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.DependencyMentions(edge.Requirement,new[] { id })))));
        foreach (var candidate in candidates) {
            var file = candidate.File;
            foreach (var requirement in file.Matches.Where(match => match.IsExact).SelectMany(match => match.Dependencies)
                .Where(requirement => requirement.Kind == "required" && requirement.EnabledPaths.Count == 0 && requirement.VerifiedLoaderIds.Count == 0)) {
                string scope = work.Report.Context.MinecraftVersion + "|" + string.Join(",",work.Report.Context.LoaderVersions.Keys.OrderBy(key=>key,StringComparer.Ordinal));
                string key = "loader-ids|"+requirement.Platform+"|"+requirement.ProjectId+"|"+requirement.VersionId+"|"+scope;
                if (work.Cache.Read(key,work.Now()) is JArray saved) {
                    requirement.VerifiedLoaderIds.AddRange(saved.OfType<JValue>().Select(value=>value.Value as string).OfType<string>().Where(value=>!string.IsNullOrWhiteSpace(value)));
                    continue;
                }
                if (work.ArchiveReads >= 2) continue;
                work.Check();
                try {
                    JToken? version = ReferenceVersion(requirement,work);
                    JObject? archive = ReferenceFile(requirement,version);
                    if (archive is null) continue;
                    string url = (string?)archive["url"] ?? "";
                    long size = (long?)archive["size"] ?? 0;
                    string hash = (string?)archive["hash"] ?? "";
                    if (size <= 0 || size > 4L*1024*1024 || !ModDependencyTransport.IsAllowedArchive(url) ||
                        hash.Length is not (32 or 40 or 128) || hash.Any(value=>!Uri.IsHexDigit(value))) continue;
                    if (work.Report.OnlineRequests >= Math.Max(1,Math.Min(work.Options.RefreshAllFiles ? 256 : 20,work.Options.MaxRequests))) return;
                    work.ArchiveReads++; work.Report.OnlineRequests++;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(work.Deadline.Token);
                    deadline.CancelAfter(Math.Max(50,Math.Min(3000,work.Options.RequestTimeoutMs)));
                    byte[]? bytes = work.ArchiveRequest(url,size,deadline.Token);
                    work.Check();
                    if (bytes is null || bytes.LongLength != size) throw new InvalidDataException("Metadata archive size mismatch.");
                    string actual = Convert.ToHexString(hash.Length == 32 ? MD5.HashData(bytes) : hash.Length == 40 ? SHA1.HashData(bytes) : SHA512.HashData(bytes));
                    if (!actual.Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Metadata archive hash mismatch.");
                    var parsed = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>
                        .ReadArchiveBytes(bytes,work.Report.Context,work.Deadline.Token);
                    // Old manifest/core-plugin hints may be inconclusive while a
                    // modern top-level descriptor still proves the project's ID.
                    string[] ids = parsed.Mods.Where(mod => !mod.IsMetadataOnly && !mod.IsNested && !mod.IncompatibleLoader &&
                        new[] { "Fabric", "Quilt", "Forge", "NeoForge" }.Contains(mod.Format,StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(mod.Id))
                        .SelectMany(mod=>ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.Identities(mod).Keys).Distinct(StringComparer.Ordinal).ToArray();
                    requirement.VerifiedLoaderIds.AddRange(ids);
                    work.Cache.Write(key,new JArray(ids),work.Now(),work.Lifetime(true));
                } catch (OperationCanceledException) {
                    work.Check();
                    work.Cache.Write(key,new JArray(),work.Now(),TimeSpan.FromSeconds(30));
                    work.Note("部分跨平台前置的元数据验证超时，保留原始声明供核对。");
                } catch (Exception error) when (error is IOException || error is HttpRequestException || error is JsonException ||
                    error is FormatException || error is ArgumentException || error is InvalidOperationException || error is OverflowException) {
                    work.Cache.Write(key,new JArray(),work.Now(),TimeSpan.FromSeconds(30));
                    work.Note("部分跨平台前置的元数据未能验证，保留原始声明供核对。");
                }
            }
        }
    }
    private static JToken? ReferenceVersion(TOnlineDependency requirement,Session work) {
        string platform=requirement.Platform, project=requirement.ProjectId ?? "";
        string game=work.Report.Context.MinecraftVersion ?? "";
        if (platform == "Modrinth" && ValidModrinthId(project)) {
            if (ValidModrinthId(requirement.VersionId)) {
                JToken? version=work.Cache.Read("archive-version|Modrinth|"+requirement.VersionId,work.Now()) ??
                    work.Ask(platform,"https://api.modrinth.com/v2/version/"+Uri.EscapeDataString(requirement.VersionId),HttpMethod.Get,null!);
                return S(version?["project_id"]) == project && S(version?["id"]) == requirement.VersionId ? version : null;
            }
            if (game.Length==0) return null;
            JToken? versions=work.Ask(platform,"https://api.modrinth.com/v2/project/"+Uri.EscapeDataString(project)+"/version?game_versions="+
                Uri.EscapeDataString(new JArray(game).ToString(Formatting.None)),HttpMethod.Get,null!);
            return A(versions).FirstOrDefault(version => S(version["project_id"])==project &&
                A(version["loaders"]).Any(loader=>work.Report.Context.LoaderVersions.Keys.Any(key=>LoaderMatches(key,S(loader)))));
        }
        if (platform == "CurseForge" && ValidCurseForgeId(project) && game.Length>0) {
            JToken? versions=work.Ask(platform,"https://api.curseforge.com/v1/mods/"+project+"/files?gameVersion="+Uri.EscapeDataString(game)+"&pageSize=50",HttpMethod.Get,null!);
            return A(versions?["data"]).FirstOrDefault(version=>S(version["modId"])==project &&
                A(version["gameVersions"]).Any(loader=>work.Report.Context.LoaderVersions.Keys.Any(key=>LoaderMatches(key,S(loader)))));
        }
        return null;
    }
    private static bool LoaderMatches(string key,string loader) => key.Equals(loader,StringComparison.OrdinalIgnoreCase) ||
        key=="fabricloader" && loader.Equals("fabric",StringComparison.OrdinalIgnoreCase) ||
        key=="quilt_loader" && loader.Equals("quilt",StringComparison.OrdinalIgnoreCase);
    private static JObject? ReferenceFile(TOnlineDependency requirement,JToken? version) {
        if (version is null) return null;
        if (requirement.Platform=="Modrinth") {
            JToken? file=A(version["files"]).OrderByDescending(file=>(bool?)file["primary"]==true)
                .FirstOrDefault(file=>(long?)file["size"] is >0 and <=4L*1024*1024 && !string.IsNullOrWhiteSpace((string?)file["hashes"]?["sha1"]));
            return file is null ? null : new JObject { ["url"]=file["url"], ["size"]=file["size"], ["hash"]=file["hashes"]!["sha1"] };
        }
        JToken? hash=A(version["hashes"]).FirstOrDefault(hash=>(int?)hash["algo"]==1) ?? A(version["hashes"]).FirstOrDefault(hash=>(int?)hash["algo"]==2);
        return hash is null ? null : new JObject { ["url"]=version["downloadUrl"], ["size"]=version["fileLength"], ["hash"]=hash["value"] };
    }
}
