// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.VisualBasic.CompilerServices;
namespace PCLCS;
public sealed partial class ModDependencyOnlinePipeline<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile,TOptions,TCache> {

        private static bool CachedMatches(FileData File, string Platform, Session Work)
        {
            var Value = Work.Cache.Read("file|" + Platform + "|" + File.Info.Sha1, Work.Now());
            if (Value is null)
                return false;
            File.Info.Matches.AddRange((IEnumerable<TOnlineProject>)Value.ToObject<List<TOnlineProject>>());
            return true;
        }
        private static void StoreMatches(FileData File, string Platform, Session Work)
        {
            var Values = File.Info.Matches.Where(Item => (Item.Platform ?? "") == (Platform ?? "")).ToList();
            Work.Cache.Write("file|" + Platform + "|" + File.Info.Sha1, JArray.FromObject(Values), Work.Now(), Work.Lifetime(Values.Count > 0));
        }
        private static void MatchModrinth(List<FileData> Files, Session Work)
        {
            var Pending = Files.Where(Item => !CachedMatches(Item, "Modrinth", Work)).ToList();
            if (Pending.Count == 0)
                return;
            for (int Offset = 0, loopTo = Pending.Count - 1; Offset <= loopTo; Offset += 100)
            {
                var Batch = Pending.Skip(Offset).Take(100).ToList();
                JObject Response = Work.Ask("Modrinth", "https://api.modrinth.com/v2/version_files", HttpMethod.Post, new JObject(new JProperty("algorithm", "sha1"), new JProperty("hashes", new JArray(Batch.Select(Item => Item.Info.Sha1).Distinct()))).ToString(Formatting.None)) as JObject;
                if (Response is null)
                    return;
                foreach (var File in Batch)
                {
                    JObject Version = Response[File.Info.Sha1] as JObject;
                    if (Version is not null && ValidModrinthId(S(Version["project_id"])) && ValidModrinthId(S(Version["id"])) && A(Version["files"]).Any(Item => S(Item["hashes"]?["sha1"]).Equals(File.Info.Sha1, StringComparison.OrdinalIgnoreCase) && (Item["size"] is null || (S(Item["size"]) ?? "") == (File.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""))))
                    {
                        var Match = new TOnlineProject()
                        {
                            Platform = "Modrinth",
                            ProjectId = S(Version["project_id"]),
                            VersionId = S(Version["id"]),
                            ProjectName = S(Version["project_id"]),
                            VersionName = Caption(S(Version["version_number"])),
                            IsExact = true,
                            MatchBasis = "SHA1 精确文件匹配",
                            Website = "https://modrinth.com/mod/" + Uri.EscapeDataString(S(Version["project_id"]))
                        };
                        foreach (var Requirement in A(Version["dependencies"]).Take(64))
                        {
                            string Id = S(Requirement["project_id"]);
                            string VersionId = S(Requirement["version_id"]);
                            string Kind = S(Requirement["dependency_type"]);
                            if (!new[] { "required", "optional", "incompatible", "embedded" }.Contains(Kind))
                                continue;
                            Match.Dependencies.Add(new TOnlineDependency()
                            {
                                Platform = "Modrinth",
                                ProjectId = ValidModrinthId(Id) ? Id : "",
                                VersionId = ValidModrinthId(VersionId) ? VersionId : "",
                                FileName = Caption(S(Requirement["file_name"])),
                                Kind = Kind
                            });
                        }
                        File.Info.Matches.Add(Match);
                    }
                    StoreMatches(File, "Modrinth", Work);
                }
            }
        }
        private static void MatchCurseForge(List<FileData> Files, Session Work)
        {
            var Pending = Files.Where(Item => !CachedMatches(Item, "CurseForge", Work)).ToList();
            if (Pending.Count == 0)
                return;
            for (int Offset = 0, loopTo = Pending.Count - 1; Offset <= loopTo; Offset += 100)
            {
                var Batch = Pending.Skip(Offset).Take(100).ToList();
                foreach (var File in Batch)
                {
                    Work.Check();
                    Fingerprint(File, Work);
                }
                var Response = Work.Ask("CurseForge", "https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post, new JObject(new JProperty("fingerprints", new JArray(Batch.Select(Item => Item.Fingerprint).Distinct()))).ToString(Formatting.None));
                if (Response is null)
                    return;
                foreach (var Raw in A(Response["data"]?["exactMatches"]))
                {
                    JObject Version = Raw["file"] as JObject;
                    string Id = S(Raw["id"]);
                    if (Version is null || !ValidCurseForgeId(Id) || (S(Version["modId"]) ?? "") != (Id ?? "") || !ValidCurseForgeId(S(Version["id"])))
                        continue;
                    if (Version["gameId"] is not null && S(Version["gameId"]) != "432")
                        continue;
                    foreach (var File in Batch)
                    {
                        if ((S(Version["fileFingerprint"]) ?? "") != (File.Fingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "") || Version["fileLength"] is not null && (S(Version["fileLength"]) ?? "") != (File.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "") || !A(Version["hashes"]).Any(Hash => S(Hash["algo"]) == "1" && S(Hash["value"]).Equals(File.Info.Sha1, StringComparison.OrdinalIgnoreCase) || S(Hash["algo"]) == "2" && S(Hash["value"]).Equals(File.Md5, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var Match = new TOnlineProject()
                        {
                            Platform = "CurseForge",
                            ProjectId = Id,
                            VersionId = S(Version["id"]),
                            ProjectName = Id,
                            VersionName = Caption(S(Version["displayName"])),
                            IsExact = true,
                            MatchBasis = "指纹及内容哈希精确匹配",
                            Website = "https://www.curseforge.com/projects/" + Id
                        };
                        foreach (var Requirement in A(Version["dependencies"]).Take(64))
                        {
                            string DependencyId = S(Requirement["modId"]);
                            string Kind;
                            switch (S(Requirement["relationType"]) ?? "")
                            {
                                case "2":
                                    {
                                        Kind = "optional";
                                        break;
                                    }
                                case "3":
                                    {
                                        Kind = "required";
                                        break;
                                    }
                                case "5":
                                    {
                                        Kind = "incompatible";
                                        break;
                                    }
                                case "1":
                                case "6":
                                    {
                                        Kind = "embedded";
                                        break;
                                    }

                                default:
                                    {
                                        continue;
                                    }
                            }
                            if (ValidCurseForgeId(DependencyId))
                                Match.Dependencies.Add(new TOnlineDependency() { Platform = "CurseForge", ProjectId = DependencyId, Kind = Kind });
                        }
                        if (!File.Info.Matches.Any(Item => Item.Platform == "CurseForge" && (Item.ProjectId ?? "") == (Id ?? "") && (Item.VersionId ?? "") == (Match.VersionId ?? "")))
                            File.Info.Matches.Add(Match);
                    }
                }
                foreach (var File in Batch)
                    StoreMatches(File, "CurseForge", Work);
            }
        }

        private static void ResolveVersionProjects(List<FileData> Files, Session Work)
        {
            var Requirements = Files.SelectMany(File => File.Info.Matches).Where(Match => Match.Platform == "Modrinth").SelectMany(Match => Match.Dependencies).Where(Item => ValidModrinthId(Item.VersionId)).ToList();
            var Ids = new List<string>();
            foreach (var Requirement in Requirements)
            {
                JObject Cached = Work.Cache.Read("version|Modrinth|" + Requirement.VersionId, Work.Now()) as JObject;
                if (Cached is not null && (string.IsNullOrEmpty(Requirement.ProjectId) || (Requirement.ProjectId ?? "") == (S(Cached["project"]) ?? "")))
                {
                    Requirement.ProjectId = S(Cached["project"]);
                    Requirement.VersionName = S(Cached["name"]);
                }
                else if (!Ids.Contains(Requirement.VersionId))
                {
                    Ids.Add(Requirement.VersionId);
                }
            }
            if (Ids.Count == 0)
                return;
            for (int Offset = 0, loopTo = Ids.Count - 1; Offset <= loopTo; Offset += 100)
            {
                var Batch = Ids.Skip(Offset).Take(100).ToList();
                var Response = Work.Ask("Modrinth", "https://api.modrinth.com/v2/versions?ids=" + JsonIds(Batch), HttpMethod.Get, null);
                if (Response is null)
                    return;
                foreach (var Version in A(Response))
                {
                    string Id = S(Version["id"]);
                    string Project = S(Version["project_id"]);
                    if (!Batch.Contains(Id) || !ValidModrinthId(Project))
                        continue;
                    string Name = Caption(S(Version["version_number"]));
                    Work.Cache.Write("version|Modrinth|" + Id, new JObject(new JProperty("project", Project), new JProperty("name", Name)), Work.Now(), Work.Lifetime(true));
                    Work.Cache.Write("archive-version|Modrinth|" + Id, Version, Work.Now(), Work.Lifetime(true));
                    foreach (var Requirement in Requirements.Where(Item => (Item.VersionId ?? "") == (Id ?? "")))
                    {
                        if (string.IsNullOrEmpty(Requirement.ProjectId) || (Requirement.ProjectId ?? "") == (Project ?? ""))
                        {
                            Requirement.ProjectId = Project;
                            Requirement.VersionName = Name;
                        }
                        else
                        {
                            Work.Report.OnlineIncomplete = true;
                            Work.Note("平台依赖的项目与指定版本不一致，已保留原始声明供核对。");
                        }
                    }
                }
            }
        }
        private static void ResolveProjects(List<FileData> Files, string Platform, Session Work)
        {
            var Matches = Files.SelectMany(File => File.Info.Matches).Where(Match => (Match.Platform ?? "") == (Platform ?? "")).ToList();
            var Requirements = Matches.SelectMany(Match => Match.Dependencies).ToList();
            var Ids = Matches.Select(Match => Match.ProjectId).Concat(Requirements.Select(Item => Item.ProjectId)).Where(Id => !string.IsNullOrEmpty(Id)).Distinct().ToList();
            var Projects = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var Pending = new List<string>();
            foreach (var Id in Ids)
            {
                JObject Cached = Work.Cache.Read("project|" + Platform + "|" + Id, Work.Now()) as JObject;
                if (Cached is not null)
                {
                    Projects[Id] = Cached;
                }
                else
                {
                    Pending.Add(Id);
                }
            }
            if (Pending.Count > 0)
            {
                for (int Offset = 0, loopTo = Pending.Count - 1; Offset <= loopTo; Offset += 100)
                {
                    var Batch = Pending.Skip(Offset).Take(100).ToList();
                    JToken Response;
                    if (Platform == "Modrinth")
                    {
                        Response = Work.Ask(Platform, "https://api.modrinth.com/v2/projects?ids=" + JsonIds(Batch), HttpMethod.Get, null);
                    }
                    else
                    {
                        Response = Work.Ask(Platform, "https://api.curseforge.com/v1/mods", HttpMethod.Post, new JObject(new JProperty("modIds", new JArray(Batch.Select(Id => int.Parse(Id, System.Globalization.CultureInfo.InvariantCulture))))).ToString(Formatting.None));
                        Response = Response?["data"];
                    }
                    foreach (var Project in A(Response))
                    {
                        string Id = S(Project["id"]);
                        if (!Batch.Contains(Id))
                            continue;
                        bool Valid = Platform == "Modrinth" ? S(Project["project_type"]) == "mod" : IsCurseForgeModProject(Project);
                        string Title = Caption(Platform == "Modrinth" ? S(Project["title"]) : S(Project["name"]));
                        string Website = Platform == "Modrinth" ? "https://modrinth.com/mod/" + Uri.EscapeDataString(Id) : IsCurseForgeWebsite(S(Project["links"]?["websiteUrl"])) ? S(Project["links"]["websiteUrl"]) : "https://www.curseforge.com/projects/" + Id;
                        var Slim = new JObject(new JProperty("valid", Valid), new JProperty("title", Title), new JProperty("website", Website));
                        Projects[Id] = Slim;
                        Work.Cache.Write("project|" + Platform + "|" + Id, Slim, Work.Now(), Work.Lifetime(true));
                    }
                }
            }
            foreach (var File in Files)
                File.Info.Matches.RemoveAll(Match => (Match.Platform ?? "") == (Platform ?? "") && Projects.ContainsKey(Match.ProjectId) && !Projects[Match.ProjectId]["valid"].Value<bool>());
            foreach (var Match in Matches)
            {
                JObject Project = null;
                if (Projects.TryGetValue(Match.ProjectId, out Project) && !string.IsNullOrEmpty(S(Project["title"])))
                {
                    Match.ProjectName = S(Project["title"]);
                    Match.Website = S(Project["website"]);
                }
            }
            foreach (var Requirement in Requirements)
            {
                JObject Project = null;
                if (Projects.TryGetValue(Requirement.ProjectId, out Project) && Project["valid"].Value<bool>())
                {
                    Requirement.ProjectName = S(Project["title"]);
                    Requirement.Website = S(Project["website"]);
                }
                else if (!string.IsNullOrEmpty(Requirement.ProjectId))
                {
                    Requirement.ProjectName = Requirement.ProjectId;
                    Requirement.Website = Platform == "Modrinth" ? "https://modrinth.com/mod/" + Uri.EscapeDataString(Requirement.ProjectId) : "https://www.curseforge.com/projects/" + Requirement.ProjectId;
                }
            }
        }
        private static void SearchCandidates(List<FileData> Files, Session Work)
        {
            foreach (var File in Files.Where(Item => Item.Info.IsSupplementTarget && Item.Info.Matches.Count == 0).Take(Math.Max(0, Math.Min(4, Work.Options.MaxSearchFiles))))
            {
                string Query = SearchQuery(File.Info.FullPath);
                if (Query.Length < 3)
                    continue;
                foreach (var Platform in new[] { "Modrinth", "CurseForge" })
                {
                    Work.Check();
                    string Key = "search|" + Platform + "|" + File.Info.Sha1 + "|" + Query;
                    var Cached = Work.Cache.Read(Key, Work.Now());
                    if (Cached is not null)
                    {
                        File.Info.Candidates.AddRange((IEnumerable<TOnlineProject>)Cached.ToObject<List<TOnlineProject>>());
                        continue;
                    }
                    JToken Response;
                    IEnumerable<JToken> Values;
                    if (Platform == "Modrinth")
                    {
                        Response = Work.Ask(Platform, "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(Query) + "&limit=3&facets=" + Uri.EscapeDataString("[[\"project_type:mod\"]]"), HttpMethod.Get, null);
                        Values = A(Response?["hits"]);
                    }
                    else
                    {
                        Response = Work.Ask(Platform, "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&pageSize=3&searchFilter=" + Uri.EscapeDataString(Query), HttpMethod.Get, null);
                        Values = A(Response?["data"]);
                    }
                    if (Response is null)
                        continue;
                    var Candidates = new List<TOnlineProject>();
                    foreach (var Project in Values.Take(Math.Max(1, Math.Min(3, Work.Options.CandidatesPerPlatform))))
                    {
                        string Id = Platform == "Modrinth" ? S(Project["project_id"]) : S(Project["id"]);
                        if (Platform == "Modrinth" ? !ValidModrinthId(Id) || S(Project["project_type"]) != "mod" : !ValidCurseForgeId(Id) || !IsCurseForgeModProject(Project))
                            continue;
                        Candidates.Add(new TOnlineProject()
                        {
                            Platform = Platform,
                            ProjectId = Id,
                            IsExact = false,
                            MatchBasis = "文件名搜索候选",
                            ProjectName = Caption(Platform == "Modrinth" ? S(Project["title"]) : S(Project["name"])),
                            Website = Platform == "Modrinth" ? "https://modrinth.com/mod/" + Uri.EscapeDataString(Id) : IsCurseForgeWebsite(S(Project["links"]?["websiteUrl"])) ? S(Project["links"]["websiteUrl"]) : "https://www.curseforge.com/projects/" + Id
                        });
                    }
                    File.Info.Candidates.AddRange((IEnumerable<TOnlineProject>)Candidates);
                    Work.Cache.Write(Key, JArray.FromObject(Candidates), Work.Now(), Work.Lifetime(false));
                }
            }
        }
        public static void MapRelations(TReport Report)
        {
            foreach (var File in Report.OnlineFiles)
            {
                File.Status = File.Matches.Count > 0 ? "已按内容精确匹配到下载源版本。" : "未找到精确匹配；候选项目需要手动核对。";
                foreach (var Match in File.Matches.Where(Item => Item.IsExact))
                {
                    foreach (var Requirement in Match.Dependencies)
                    {
                        Requirement.ProjectPaths.Clear();
                        Requirement.MatchedPaths.Clear();
                        Requirement.EnabledPaths.Clear();
                        if (!string.IsNullOrEmpty(Requirement.ProjectId))
                        {
                            foreach (var Provider in Report.OnlineFiles.Where(Item => Item.Matches.Any(Project => Project.IsExact && (Project.Platform ?? "") == (Requirement.Platform ?? "") && (Project.ProjectId ?? "") == (Requirement.ProjectId ?? ""))))
                            {
                                Requirement.ProjectPaths.Add(Provider.FullPath);
                                if (!string.IsNullOrEmpty(Requirement.VersionId) && !Provider.Matches.Any(Project => Project.IsExact && (Project.Platform ?? "") == (Requirement.Platform ?? "") && (Project.ProjectId ?? "") == (Requirement.ProjectId ?? "") && (Project.VersionId ?? "") == (Requirement.VersionId ?? "")))
                                    continue;
                                Requirement.MatchedPaths.Add(Provider.FullPath);
                                if (Provider.Enabled)
                                    Requirement.EnabledPaths.Add(Provider.FullPath);
                            }
                        }
                        var runtime = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.RuntimeProvidersForPlatformDependency(Report, File.FullPath, Requirement);
                        bool loaderSatisfied = Requirement.Kind == "required" && runtime.Count > 0;
                        if (loaderSatisfied && Requirement.MatchedPaths.Count == 0) {
                            Requirement.Status = "本地加载器声明已满足：" + string.Join("、", runtime.Select(item=>item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName)) + "；平台指定文件仅作参考。";
                        }
                        else if (Requirement.EnabledPaths.Count > 0)
                        {
                            Requirement.Status = "精确映射到启用文件：" + string.Join("、", Requirement.EnabledPaths.Select(Value => Path.GetFileName(Value)));
                        }
                        else if (Requirement.MatchedPaths.Count > 0)
                        {
                            Requirement.Status = "仅映射到禁用文件，需核对：" + string.Join("、", Requirement.MatchedPaths.Select(Value => Path.GetFileName(Value)));
                        }
                        else if (Requirement.ProjectPaths.Count > 0)
                        {
                            Requirement.Status = "已安装同一项目的其他版本：" + string.Join("、", Requirement.ProjectPaths.Select(Value => Path.GetFileName(Value))) + "；平台指定版本 " + (string.IsNullOrEmpty(Requirement.VersionName) ? "" : Requirement.VersionName + " ") + "[" + Requirement.VersionId + "]，需核对版本声明，以本地加载器检查结果为准。";
                        }
                        else
                        {
                            Requirement.Status = "未在本次精确匹配中找到，需核对；未据此判定缺失或版本错误";
                        }
                    }
                }
            }
        }

}
