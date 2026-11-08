// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public class DependencyActivationItemData<TMod> where TMod : class {
    public string Name = null!, Source = null!, Destination = null!, Sha1 = null!;
    public TMod? Owner;
    public List<string> RequiredBy = new(), Requirements = new(), ResolvedIssues = new();
}
public sealed class DependencyReplacementData {
    public string Source = null!, BeforeHash = null!, AuthorRelative = null!, ProjectIdentity = null!;
}
public interface IDependencyReplacementItem { bool HasAuthorReplacement { get; } }
public class ModDependencyAutoFillItemData<TVersion, TItem> : IDependencyReplacementItem where TVersion : class where TItem : class {
    public string Platform = null!, ProjectId = null!, Name = null!;
    public TVersion Version = null!;
    public JObject Raw = null!;
    public List<string> RequiredBy = new();
    public List<TItem> Alternatives = new();
    public DependencyReplacementData? Replacement;
    public bool HasAuthorReplacement => Replacement is not null;
}
public class ModDependencyAutoFillPlanData<TReport, TItem, TActivation> where TReport : class where TItem : class where TActivation : class {
    public string Root = null!;
    public TReport Report = null!;
    public string Stamp = null!, Caption = null!;
    public List<TItem> Items = new();
    public List<TActivation> Enables = new();
    public List<string> Notes = new();
    public bool AuthorReplacementConfirmed;
    public string ActionText => Items.Exists(item => item is IDependencyReplacementItem replacement && replacement.HasAuthorReplacement) ? "验证并替换后启动" : Enables.Count > 0 ? Items.Count > 0 ? "启用并补全后启动" : "启用并启动" : Items.Count > 0 ? "补全并启动" : "返回 Mod 管理";
}
public class DependencyCandidateMismatchException : IOException {
    public readonly int Index;
    public DependencyCandidateMismatchException(int index, string message) : base(message) { Index = index; }
}
public sealed class DependencyDownloadStep<TNetFile> where TNetFile : class {
    public string Name = null!;
    public TNetFile File = null!;
    public bool Block;
    public double ProgressWeight;
}
public sealed class DependencyDownloadBatch<TNetFile> where TNetFile : class {
    public string Name = null!;
    public List<DependencyDownloadStep<TNetFile>> Steps = new();
}

/// <summary>Original model/loader/platform primitives; inspection, planning and the file transaction remain C#.</summary>
public sealed class ModDependencyAutoFillHost<TReport, TVersion, TNetFile, TPlan, TOptions>
    where TReport : class where TVersion : class where TNetFile : class where TPlan : class where TOptions : class {
    public Func<JObject, TVersion> ParseVersion = null!;
    public Func<TVersion, ResourceTypes> ResourceType = null!;
    public Func<TVersion, bool> DownloadAvailable = null!;
    public Func<TVersion, long> Size = null!;
    public Func<TVersion, IEnumerable<string>?> GameVersions = null!;
    public Func<TVersion, ModLoaders> ModLoaders = null!;
    public Action<TVersion, ModLoaders> SetModLoaders = null!;
    public Func<TVersion, string?> Id = null!, FileName = null!, Hash = null!, Display = null!;
    public Func<TVersion, List<string>> DownloadUrls = null!;
    public Action<TVersion, List<string>> SetDownloadUrls = null!;
    public Func<TVersion, int> ReleaseType = null!;
    public Func<TVersion, DateTime> ReleaseDate = null!;
    public Func<TVersion, string, string?, ModLoaders, TNetFile> ToNetFile = null!;
    public Action<TReport, string, CancellationToken, TOptions> Enrich = null!;
    public Func<string, HttpMethod, string?, CancellationToken, JToken?> DefaultRequest = null!;
    public Func<string, string> SafeName = null!;
    public Action<string, string> EnsureWithin = null!, DeleteStage = null!;
    public Func<string, bool, string> FileHash = null!;
    public Action<TPlan, List<TNetFile>, CancellationToken> Download = null!;
    public Action<string> Info = null!;
    public Action<Exception, string> Warn = null!;
    public Action RefreshOptions = null!;
    public Func<int, string, DependencyCandidateMismatchException> CandidateMismatch = null!;
    public Func<string, string, List<Tuple<string, string?, string?>>, CancellationToken, string>? ApplyTransaction;
}
