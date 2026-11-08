// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF ModpackUpdate/ModpackUpdateFlow at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983
// and LF fixes at 07dd3503b38b39095c8b92b40ae0454907387f9f; MPL-2.0 for PCLLF-original code. Both modules are LF
// additions, absent in PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8. Fixed CE stable
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Plain Craft Launcher 2/Modules/Minecraft/
// ModModpack.cs reviewed for same-source importing only (that directory's LICENCE).
// CE has no equivalent LF author-origin/backup transaction; no CE update policy is adopted.
public class PackUpdateFileData {
    public string? Relative, Hash, Identity;
    // Platform project identity and exact platform file/version identity are distinct.
    public string? FileIdentity;
    public List<string> Urls = new();
    public string? Payload;
    public bool OptionalFile;
}
public class PackUpdatePackageData<TFile> where TFile : PackUpdateFileData {
    public string? Name, Version, Minecraft;
    public Dictionary<ModLoaders, string> Loaders = new();
    public Dictionary<string, TFile> Files = new(StringComparer.OrdinalIgnoreCase);
    public JObject? Source;
}
public enum PackUpdateOwnership { AuthorUnchanged, AuthorRenamed, PersonalModified, PersonalAdded, Disabled, Deleted, OptionalAbsent, Ambiguous, Unknown }
public enum PackUpdateChoice { PreservePersonal, UseAuthor }
public enum PackUpdateOperation { AuthorChange, DisablePersonal, PersonalDependency, EnablePersonal }
public enum PackUpdateAssociation { DeclaredPath, VerifiedModHash, RecordedMod, PlatformProject, PersonalComponent, Unknown }
public enum PackUpdatePreviewActionKind { EnableRequired, DisableIncompatible, UseAuthorVersion }
public sealed class PackUpdatePreviewAction {
    public string Relative = "", Details = "";
    public PackUpdatePreviewActionKind Kind;
    public bool Selected;
}
public sealed class PackUpdatePreviewReview {
    public PackDependencyGraphResult Dependencies = new();
    public List<PackUpdatePreviewAction> Actions = new();
}
public class PackUpdateChangeData<TFile> where TFile : PackUpdateFileData {
    public string? Relative, Before;
    public string? BaselineRelative, LocalRelative, TargetBefore;
    public TFile? Incoming;
    public bool Conflict;
    // An optional author-action override is not a file/provenance conflict.
    public bool AllowPreserveAuthor;
    public PackUpdateOperation Operation;
    public PackUpdateAssociation Association;
    public bool KeepDisabled = true;
    public bool PersonalCompatible;
    public bool PersonalComponentMatched;
    public bool PreviewOnly, DisableAfterUpdate, PreviewAuthorRepair;
    public string? DisabledDestination;
    public string? Reason;
    public PackUpdateOwnership Ownership;
    public bool DisplayOnly, AllowAuthor = true;
    public PackUpdateChoice Choice;
    public List<string> Candidates = new();
    public bool CanChoose => !DisplayOnly && AllowAuthor && Ownership != PackUpdateOwnership.Ambiguous && (Conflict || AllowPreserveAuthor);
    public bool ApplyAuthor => !DisplayOnly && AllowAuthor && (!DisableAfterUpdate || Ownership is not (PackUpdateOwnership.Ambiguous or PackUpdateOwnership.Unknown)) && (!PreviewOnly || DisableAfterUpdate || Ownership == PackUpdateOwnership.Disabled && !KeepDisabled) &&
        (!(Conflict || AllowPreserveAuthor) || AllowAuthor && Choice == PackUpdateChoice.UseAuthor);
    // Updating a deliberately disabled mod never implicitly enables it.
    public string DestinationRelative => Operation == PackUpdateOperation.DisablePersonal ? DisabledDestination! :
        (Relative ?? "") + (Incoming is not null && (DisableAfterUpdate || Ownership == PackUpdateOwnership.Disabled && KeepDisabled) ? ".disabled" : "");
    public PackUpdateChangeData<TFile> SnapshotForPreview() {
        var copy = (PackUpdateChangeData<TFile>)MemberwiseClone(); copy.Candidates = new(Candidates); return copy;
    }
}
public class PackUpdatePlanData<TPackage, TChange> where TPackage : class where TChange : class {
    public string? Root;
    public TPackage? Package;
    public List<TChange> Changes = new();
    // Unchanged author files remain outside the normal delta, but can be enabled/disabled explicitly in the preview.
    public List<TChange> PreviewChanges = new();
    public JObject? Baseline;
    public Dictionary<string, string?> Snapshot = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings = new(), RuntimeWarnings = new();
    // Unknown declarations require an explicit acknowledgement, never an implicit compatibility claim.
    public bool AllowUnverifiedDependencies;
    public PackDependencyGraphResult? DependencyCheck;
    public HashSet<string> ObservedModFiles = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class PackUpdateHost<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    public Func<TFile> CreateFile = null!;
    public Func<TPackage> CreatePackage = null!;
    public Func<TChange> CreateChange = null!;
    public Func<TPlan> CreatePlan = null!;
    public Action<string, string> EnsureWithin = null!;
    public Action<string, byte[]> AtomicWrite = null!;
    public Func<string, HttpMethod?, string?, JToken> Request = null!;
    public Func<JObject, ResourceTypes, ResourceVersionModelData> ParseVersion = null!;
    public Func<List<string>, string, string, TNetFile> CreateDownload = null!;
    public Action<List<TNetFile>, CancellationToken> Download = null!;
    public Func<string> TempFolder = null!;
    public Func<string, string> InstanceGameRoot = null!;
    public Action<Exception, string> Warn = null!;
}
public sealed class PackUpdateDownloadHost<TNetFile, TLoader, TProgress> where TLoader : class where TProgress : class {
    public Func<string, List<TNetFile>, TLoader> CreateLoader = null!;
    public Func<TLoader, object> LockState = null!;
    public Action<TLoader> Cancel = null!, Start = null!;
    public Action<TLoader, CancellationToken, TProgress?, bool> Wait = null!;
    public Action<TProgress, double> SetProgress = null!;
}
public sealed class PackUpdateDownloads<TNetFile, TLoader, TProgress> where TLoader : class where TProgress : class {
    private readonly PackUpdateDownloadHost<TNetFile, TLoader, TProgress> host;
    public PackUpdateDownloads(PackUpdateDownloadHost<TNetFile, TLoader, TProgress> host) { this.host = host; }
    public void Run(List<TNetFile> files, CancellationToken token, TProgress? progress) {
        token.ThrowIfCancellationRequested();
        TLoader loader = host.CreateLoader("下载整合包更新文件", files);
        using (token.Register(() => host.Cancel(loader))) {
            lock (host.LockState(loader)) { token.ThrowIfCancellationRequested(); host.Start(loader); }
            host.Wait(loader, token, progress, false);
            if (progress is not null) host.SetProgress(progress, 1);
        }
    }
}
