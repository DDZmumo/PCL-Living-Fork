// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public class ModDependencyLookupOptions<TCache> where TCache : ModDependencyLookupCache {
    public Func<string, HttpMethod, string?, CancellationToken, JToken?>? Request;
    public Func<string, long, CancellationToken, byte[]?>? ArchiveRequest;
    public Func<string, uint>? Fingerprint;
    public Func<DateTime>? Clock = () => DateTime.UtcNow;
    public TCache? Cache;
    public string? TargetFile;
    public bool ComparePlatformDependencies, RefreshAllFiles;
    public Action<int,int>? Progress;
    public int TimeBudgetMs = 6000, RequestTimeoutMs = 2000, MaxRequests = 12, MaxFiles = 64, MaxSearchFiles = 3, CandidatesPerPlatform = 3;
    public long MaxFileBytes = 128L * 1024 * 1024, MaxTotalBytes = 256L * 1024 * 1024;
    public TimeSpan CacheLifetime = TimeSpan.FromHours(6), NegativeCacheLifetime = TimeSpan.FromMinutes(10);
}

public class ModDependencyLookupCache {
    private sealed class CacheEntry {
        internal JToken Data = null!;
        internal DateTime Expires, Created;
    }
    private readonly Dictionary<string, CacheEntry> Entries = new(StringComparer.Ordinal);
    private readonly object Gate = new();
    public JToken? Read(string key, DateTime now) {
        lock (Gate) {
            if (!Entries.TryGetValue(key, out CacheEntry? value)) return null;
            if (value.Expires <= now) { Entries.Remove(key); return null; }
            return value.Data.DeepClone();
        }
    }
    public void Write(string key, JToken data, DateTime now, TimeSpan lifetime) {
        lock (Gate) {
            foreach (string expired in Entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) Entries.Remove(expired);
            if (Entries.Count >= 512 && !Entries.ContainsKey(key)) Entries.Remove(Entries.OrderBy(pair => pair.Value.Created).First().Key);
            Entries[key] = new CacheEntry { Data = data.DeepClone(), Created = now, Expires = now.Add(lifetime) };
        }
    }
    public void Clear() { lock (Gate) Entries.Clear(); }
}

public class ModDependencyHttpFailure : Exception {
    public int Code, RetrySeconds;
    public ModDependencyHttpFailure(int code, int retrySeconds) : base("HTTP " + code) { Code = code; RetrySeconds = retrySeconds; }
}

// LF Modules/Minecraft/vb at 50b028da5fcad2226d74d901a41d8c43a249bfb2,
// is the behavior source (MPL-2.0 for PCLLF-original code). PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 has no such module.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Plain Craft Launcher 2/Modules/Minecraft/ModCompDependency.cs
// and PCL.Core/Minecraft/ResourceProject/ModDependencyResolver.cs were reviewed (CE LICENSE/PCL LICENCE).
// Their platform project/version selection differs from LF's bounded exact-content supplement; no policy is copied.
public sealed partial class ModDependencyOnlinePipeline<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
    where TContext : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ContextData, new()
    where TMod : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ModData, new()
    where TDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.DependencyData, new()
    where TEdge : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.EdgeData, new()
    where TIssue : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.IssueData, new()
    where TImpact : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ImpactData, new()
    where TReport : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.ReportData, new()
    where TOnlineProject : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineProjectData, new()
    where TOnlineDependency : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineDependencyData, new()
    where TOnlineFile : ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile>.OnlineFileData, new()
    where TOptions : ModDependencyLookupOptions<TCache>, new()
    where TCache : ModDependencyLookupCache, new() {
    private readonly TCache sharedCache;
    private readonly Func<string, HttpMethod, string?, CancellationToken, JToken?> defaultRequest;
    private readonly Func<string, long, CancellationToken, byte[]?>? defaultArchiveRequest;
    public ModDependencyOnlinePipeline(TCache sharedCache, Func<string, HttpMethod, string?, CancellationToken, JToken?> defaultRequest) {
        this.sharedCache = sharedCache; this.defaultRequest = defaultRequest;
    }
    public ModDependencyOnlinePipeline(TCache sharedCache, Func<string, HttpMethod, string?, CancellationToken, JToken?> defaultRequest,
        Func<string, long, CancellationToken, byte[]?> archiveRequest) : this(sharedCache, defaultRequest) { defaultArchiveRequest = archiveRequest; }

#nullable disable
        public void ResetIndexLookups()
        {
            sharedCache.Clear();
        }

        private class FileData
        {
            public TOnlineFile Info;
            public string Md5;
            public long Length;
            public long FilteredLength;
            public uint Fingerprint;
            public bool HasFingerprint;
        }
        private class LookupBudgetException : Exception
        {
        }
        private class Session : IDisposable
        {
            public readonly TOptions Options;
            public readonly TReport Report;
            public readonly TCache Cache;
            public readonly CancellationToken CallerToken;
            public readonly CancellationTokenSource Deadline;
            public readonly string TargetFile;
            public readonly Stopwatch Timer = Stopwatch.StartNew();
            public readonly Func<string, long, CancellationToken, byte[]> ArchiveRequest;
            public int ArchiveReads;
            private readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.Ordinal);
            private readonly Func<string,HttpMethod,string,CancellationToken,JToken> nativeRequest;
            public Session(TReport Report, CancellationToken Token, TOptions Options, string TargetFile, TCache sharedCache, Func<string,HttpMethod,string,CancellationToken,JToken> nativeRequest,
                Func<string,long,CancellationToken,byte[]> nativeArchiveRequest)
            {
                this.Report = Report;
                this.nativeRequest = nativeRequest;
                this.Options = Options;
                ArchiveRequest = Options.ArchiveRequest ?? (Options.Request is null ? nativeArchiveRequest : null);
                this.TargetFile = TargetFile;
                CallerToken = Token;
                Cache = Options.Cache ?? sharedCache;
                Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                Deadline.CancelAfter(TimeLimitMs);
            }
            public int TimeLimitMs
            {
                get
                {
                    return Math.Max(100, Math.Min(Options.RefreshAllFiles ? 180000 : 10000, Options.TimeBudgetMs));
                }
            }
            public int FileLimit
            {
                get
                {
                    return Math.Max(1, Math.Min(Options.RefreshAllFiles ? 4096 : 512, Options.MaxFiles));
                }
            }
            public DateTime Now()
            {
                return (Options.Clock ?? (() => DateTime.UtcNow))().ToUniversalTime();
            }
            public void Check()
            {
                Deadline.Token.ThrowIfCancellationRequested();
                if (Timer.ElapsedMilliseconds >= TimeLimitMs)
                    throw new OperationCanceledException(Deadline.Token);
            }
            public JToken Ask(string Platform, string Url, HttpMethod Method, string Body)
            {
                Check();
                if (!IsAllowedApi(Url))
                    throw new ArgumentException("Unsupported dependency API address.");
                if (Blocked.Contains(Platform))
                    return null;
                if (Cache.Read("cooldown|" + Platform, Now()) is not null)
                {
                    Report.OnlineIncomplete = true;
                    Note(Platform + " 暂缓请求；已保留缓存和本地结果。");
                    Blocked.Add(Platform);
                    return null;
                }
                if (Report.OnlineRequests >= Math.Max(1, Math.Min(Options.RefreshAllFiles ? 256 : 20, Options.MaxRequests)))
                    throw new LookupBudgetException();
                Report.OnlineRequests += 1;
                using (var RequestDeadline = CancellationTokenSource.CreateLinkedTokenSource(Deadline.Token))
                {
                    RequestDeadline.CancelAfter(Math.Max(50, Math.Min(3000, Options.RequestTimeoutMs)));
                    try
                    {
                        var Request = Options.Request ?? nativeRequest;
                        var Result = Request(Url, Method, Body, RequestDeadline.Token);
                        Check();
                        if (Result is null)
                            Report.OnlineIncomplete = true;
                        return Result;
                    }
                    catch (OperationCanceledException )
                    {
                        if (Deadline.IsCancellationRequested || CallerToken.IsCancellationRequested)
                            throw;
                        Report.OnlineIncomplete = true;
                        Blocked.Add(Platform);
                        Cache.Write("cooldown|" + Platform, new JValue(true), Now(), TimeSpan.FromSeconds(30d));
                        Note(Platform + " 查询超时；保留本地检查结果。");
                        return null;
                    }
                    catch (ModDependencyHttpFailure ex)
                    {
                        Report.OnlineIncomplete = true;
                        Blocked.Add(Platform);
                        int Seconds = ex.Code == 429 ? Math.Max(60, Math.Min(900, ex.RetrySeconds)) : 30;
                        Cache.Write("cooldown|" + Platform, new JValue(true), Now(), TimeSpan.FromSeconds(Seconds));
                        Note(Platform + (ex.Code == 429 ? " 请求过于频繁，已停止本轮查询。" : " 查询失败（HTTP " + ex.Code + "）；保留本地结果。"));
                        return null;
                    }
                    catch (Exception )
                    {
                        Report.OnlineIncomplete = true;
                        Blocked.Add(Platform);
                        Cache.Write("cooldown|" + Platform, new JValue(true), Now(), TimeSpan.FromSeconds(30d));
                        Note(Platform + " 暂不可用；保留本地检查结果。");
                        return null;
                    }
                }
            }
            public void Note(string Value)
            {
                if (!Report.OnlineNotes.Contains(Value))
                    Report.OnlineNotes.Add(Value);
            }
            public TimeSpan Lifetime(bool Positive)
            {
                var Value = Positive ? Options.CacheLifetime : Options.NegativeCacheLifetime;
                return TimeSpan.FromSeconds(Math.Max(1d, Math.Min(Positive ? 86400 : 3600, Value.TotalSeconds)));
            }
            public void Dispose()
            {
                Deadline.Dispose();
            }
        }

        public TReport InspectWithOnline(string modsFolder, TContext context = null, CancellationToken token = default, TOptions options = null)
        {
            return Enrich(ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.Inspect(modsFolder, context, token), modsFolder, token, options);
        }

        // Manual refresh covers enabled and disabled files, with larger bounded batches than startup checks.
        public TReport RebuildWithOnline(string ModsFolder, TContext Context, CancellationToken Token = default, TOptions Options = null)
        {
            ResetIndexLookups();
            var Report = ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.RebuildIndex(ModsFolder, Context, Token);
            if (Report.IndexReadFailed)
                return Report;
            var Settings = Options ?? new TOptions();
            Settings.RefreshAllFiles = true;
            Settings.ComparePlatformDependencies = true;
            Settings.TargetFile = null;
            Settings.TimeBudgetMs = 180000;
            Settings.MaxRequests = 256;
            Settings.MaxFiles = 4096;
            Settings.MaxSearchFiles = 0;
            Settings.MaxFileBytes = 512L * 1024L * 1024L;
            Settings.MaxTotalBytes = 16L * 1024L * 1024L * 1024L;
            return Enrich(Report, ModsFolder, Token, Settings);
        }

        public TReport Enrich(TReport report, string modsFolder, CancellationToken token = default, TOptions options = null)
        {
            if (report is null)
                throw new ArgumentNullException(nameof(report));
            var Settings = options ?? new TOptions();
            string TargetFile = ExplicitTarget(Settings.TargetFile, modsFolder, report, Settings.ComparePlatformDependencies);
            if (!string.IsNullOrWhiteSpace(Settings.TargetFile) && TargetFile is null)
                return report;
            if (!report.MetadataIncomplete && TargetFile is null && !Settings.ComparePlatformDependencies)
                return report;
            bool CanCache = Settings.Request is null && Settings.Fingerprint is null;
            if (CanCache && report.OnlineAttempted && !report.OnlineCancelled && (TargetFile is null && (report.OnlineIndexScope == "all-files" || (report.OnlineIndexScope ?? "") == ((Settings.ComparePlatformDependencies ? "all" : "incomplete") ?? "")) || TargetFile is not null && report.OnlineFiles.Any(Item => Item.IsSupplementTarget && ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.SameFilePath(Item.FullPath, TargetFile))))
            {
                token.ThrowIfCancellationRequested();
                report.OnlineRequests = 0;
                return report;
            }
            var PreviousFiles = CanCache ? report.OnlineFiles.ToList() : new List<TOnlineFile>();
            string PreviousScope = report.OnlineIndexScope;
            report.OnlineAttempted = true;
            report.OnlineCancelled = false;
            report.OnlineTimedOut = false;
            report.OnlineIncomplete = false;
            report.OnlineRequests = 0;
            report.OnlineFiles.Clear();
            report.OnlineNotes.Clear();
            using (var Work = new Session(report, token, Settings, TargetFile, sharedCache, defaultRequest, defaultArchiveRequest))
            {
                report.OnlineCheckedAtUtc = Work.Now();
                try
                {
                    Work.Check();
                    var Files = ReadFiles(modsFolder, Work);
                    if (!Files.Any(Item => Item.Info.IsSupplementTarget))
                    {
                        Work.Note("没有找到适合联网补充的本地文件。");
                        return report;
                    }
                    report.OnlineFiles.AddRange(Files.Select(Item => Item.Info));
                    MatchModrinth(Files, Work);
                    MatchCurseForge(Files, Work);
                    ResolveVersionProjects(Files, Work);
                    ResolveProjects(Files, "Modrinth", Work);
                    ResolveProjects(Files, "CurseForge", Work);
                    SearchCandidates(Files, Work);
                    MapRelations(report);
                    VerifyRuntimeIdentities(Work);
                }
                catch (OperationCanceledException )
                {
                    report.OnlineIncomplete = true;
                    report.OnlineCancelled = token.IsCancellationRequested;
                    report.OnlineTimedOut = !report.OnlineCancelled;
                    Work.Note(report.OnlineCancelled ? "联网补充已取消，本地检查结果已保留。" : "联网补充达到总时间预算，已停止后续请求。");
                }
                catch (LookupBudgetException )
                {
                    report.OnlineIncomplete = true;
                    Work.Note("联网补充达到请求数量上限，已停止后续请求。");
                }
                catch (Exception )
                {
                    report.OnlineIncomplete = true;
                    Work.Note("联网补充未完成，本地检查结果已保留。");
                }
                finally
                {
                    if (CanCache)
                    {
                        foreach (var Previous in PreviousFiles)
                        {
                            if (!report.OnlineFiles.Any(Item => ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.SameFilePath(Item.FullPath, Previous.FullPath)))
                                report.OnlineFiles.Add(Previous);
                        }
                        report.OnlineIndexScope = TargetFile is null ? Settings.RefreshAllFiles ? "all-files" : Settings.ComparePlatformDependencies ? "all" : "incomplete" : PreviousScope == "all" || PreviousScope == "all-files" ? PreviousScope : "file";
                    }
                    MapRelations(report);
                    if (CanCache)
                        ModDependencyInspection<TContext,TMod,TDependency,TEdge,TIssue,TImpact,TReport,TOnlineProject,TOnlineDependency,TOnlineFile>.Index.RememberOnline(modsFolder, report, token);
                }
            }
            return report;
        }

        public static string SearchQuery(string fullPath)
        {
            string Value = Path.GetFileName(fullPath);
            if (Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                Value = Path.GetFileNameWithoutExtension(Value);
            Value = Path.GetFileNameWithoutExtension(Value);
            Value = Regex.Replace(Value, @"(?i)(?:^|[-_+ \[\]])(?:fabric|forge|neoforge|quilt)(?=$|[-_+ \[\]])", " ");
            Value = Regex.Replace(Value, @"(?i)[-_+ ]+(?:mc[-_ ]*)?v?\d+(?:\.\d+)+(?:[-_+][A-Za-z0-9.-]+)?$", "");
            Value = Regex.Replace(Value, @"[^\p{L}0-9 ]", " ");
            Value = Regex.Replace(Value, @"\s+", " ").Trim();
            if (Value.Length > 64)
                Value = Value.Substring(0, 64).Trim();
            return Regex.IsMatch(Value, @"^\d+$") ? "" : Value;
        }
        private static string S(JToken Value)
        {
            return Value is null || Value.Type == JTokenType.Null ? "" : Value.ToString();
        }
        private static string Caption(string Value)
        {
            Value = Regex.Replace(Value ?? "", @"[\r\n\t]+", " ").Trim();
            return Value.Length > 160 ? Value.Substring(0, 160) + "…" : Value;
        }
        private static IEnumerable<JToken> A(JToken Value)
        {
            return Value is JArray ? ((JArray)Value).AsEnumerable() : Enumerable.Empty<JToken>();
        }
        private static bool ValidModrinthId(string Value)
        {
            return Regex.IsMatch(Value ?? "", "^[A-Za-z0-9]{1,64}$");
        }
        private static bool ValidCurseForgeId(string Value)
        {
            int Number;
            return int.TryParse(Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out Number) && Number > 0;
        }
        private static string JsonIds(IEnumerable<string> Ids)
        {
            return Uri.EscapeDataString(new JArray(Ids).ToString(Formatting.None));
        }
        public static bool IsAllowedApi(string url)
        {
            Uri Address = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Address) || Address.Scheme != "https" || !Address.IsDefaultPort || !string.IsNullOrEmpty(Address.UserInfo))
                return false;
            return Address.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase) && Address.AbsolutePath.StartsWith("/v2/", StringComparison.Ordinal) || Address.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase) && Address.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal);
        }
        private static bool IsCurseForgeWebsite(string Url)
        {
            Uri Address = null;
            return Uri.TryCreate(Url, UriKind.Absolute, out Address) && Address.Scheme == "https" && Address.IsDefaultPort && string.IsNullOrEmpty(Address.UserInfo) && new[] { "www.curseforge.com", "curseforge.com" }.Contains(Address.Host, StringComparer.OrdinalIgnoreCase) && Address.AbsolutePath.StartsWith("/minecraft/mc-mods/", StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsCurseForgeModProject(JToken Project)
        {
            if (Project["classId"] is not null && Project["classId"].Type != JTokenType.Null)
                return S(Project["classId"]) == "6";
            return IsCurseForgeWebsite(S(Project["links"]?["websiteUrl"]));
        }

}
