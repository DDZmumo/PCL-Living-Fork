using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF ResourceSearcher.vb f4aa0fae62c3ee8af24c81e61844c8fdd13c8983, LF LICENCE.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 differs only by LF Map classId=17.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModComp.cs request/storage reviewed;
// LF paging, partial errors, legacy Forge and mutable public request/result fields retained.
public class ResourceSearchResultData<TProject> where TProject : ResourceProjectModelData {
    public int CurseForgeOffset, CurseForgeTotal = -1, ModrinthOffset, ModrinthTotal = -1;
    public int ModrinthDirectOffset, ModrinthDirectTotal;
    public List<TProject> Results = new();
    public string? ErrorMessage;
}
public class ResourceSearchRequestData<TProject, TResult>
    where TProject : ResourceProjectModelData where TResult : ResourceSearchResultData<TProject> {
    public TResult Storage;
    public int TargetResultCount;
    public int PresentationPageSize;
    public ResourceTypes Type;
    public string Tag = "";
    public ModLoaders ModLoaders = ModLoaders.None;
    public string? GameVersion, SearchText;
    public ResourcePlatforms Sources = ResourcePlatforms.Any;
    public ResourceSearchRequestData(ResourceTypes type, TResult storage, int targetResultCount) { Type = type; Storage = storage; TargetResultCount = targetResultCount; }
    public bool CanContinue {
        get {
            if (Tag.StartsWithF("/") || !Sources.HasFlagF(ResourcePlatforms.CurseForge)) Storage.CurseForgeTotal = 0;
            if (Tag.EndsWithF("/") || !Sources.HasFlagF(ResourcePlatforms.Modrinth)) {
                Storage.ModrinthTotal = 0;
                Storage.ModrinthDirectTotal = 0;
            }
            if (Storage.CurseForgeTotal == -1 || Storage.ModrinthTotal == -1) return true;
            return Storage.CurseForgeOffset < Storage.CurseForgeTotal || Storage.ModrinthOffset < Storage.ModrinthTotal ||
                Storage.ModrinthDirectOffset < Storage.ModrinthDirectTotal;
        }
    }
    public string GetCurseForgeAddress(string? searchText, bool ignoreModLoaderFilter, int limit = 40) {
        if (Tag.StartsWithF("/")) Storage.CurseForgeTotal = 0;
        string address = $"https://api.curseforge.com/v1/mods/search?gameId=432&sortField=2&sortOrder=desc&pageSize={limit}";
        switch (Type) {
            case ResourceTypes.Mod: address += "&classId=6"; break;
            case ResourceTypes.ModPack: address += "&classId=4471"; break;
            case ResourceTypes.DataPack: address += "&classId=6945"; break;
            case ResourceTypes.Shader: address += "&classId=6552"; break;
            case ResourceTypes.ResourcePack: address += "&classId=12"; break;
            case ResourceTypes.Map: address += "&classId=17"; break;
        }
        if ((Tag ?? "") != "") address += "&categoryId=" + Tag!.BeforeFirst("/");
        if (ModLoaders != ModLoaders.None && !ignoreModLoaderFilter) address += "&modLoaderType=" + Resource.ToCurseForgeModLoaderType(ModLoaders.Flags().First());
        if (!string.IsNullOrEmpty(GameVersion)) address += "&gameVersion=" + GameVersion;
        if (!string.IsNullOrEmpty(searchText)) address += "&searchFilter=" + StringUtils.UrlEscape(searchText!);
        if (Storage.CurseForgeOffset > 0) address += "&index=" + Storage.CurseForgeOffset;
        return address;
    }
    public string GetModrinthAddress(string? searchText, bool ignoreModLoaderFilter, int limit = 40) {
        if (Tag.EndsWithF("/")) Storage.ModrinthTotal = 0;
        string address = $"https://api.modrinth.com/v2/search?limit={limit}&index=relevance";
        if (!string.IsNullOrEmpty(searchText)) address += "&query=" + StringUtils.UrlEscape(searchText!);
        if (Storage.ModrinthOffset > 0) address += "&offset=" + Storage.ModrinthOffset;
        List<string> facets = new() { "[\"project_type:" + Type.ToString().Lower() + "\"]" };
        if (!string.IsNullOrEmpty(Tag)) facets.Add("[\"categories:'" + Tag.AfterLast("/") + "'\"]");
        if (ModLoaders != ModLoaders.None && !ignoreModLoaderFilter) facets.Add("[\"categories:" + string.Join("\",\"categories:", ModLoaders.Flags().Select(f => f.ToString().Lower())) + "\"]");
        if (!string.IsNullOrEmpty(GameVersion)) facets.Add("[\"versions:'" + GameVersion + "'\"]");
        return address + "&facets=[" + string.Join(",", facets) + "]";
    }
    public bool SameQuery(ResourceSearchRequestData<TProject, TResult> request) => Type == request.Type &&
        (Tag ?? "") == (request.Tag ?? "") && ModLoaders == request.ModLoaders && Sources == request.Sources && (GameVersion ?? "") == (request.GameVersion ?? "") && (SearchText ?? "") == (request.SearchText ?? "");
    public override bool Equals(object? obj) => obj is ResourceSearchRequestData<TProject, TResult> request &&
        TargetResultCount == request.TargetResultCount && PresentationPageSize == request.PresentationPageSize && SameQuery(request);
    public override int GetHashCode() => (Type, Tag, ModLoaders, GameVersion, SearchText, Sources).GetHashCode();
}
public sealed class ResourceSearchHost<TProject> where TProject : ResourceProjectModelData {
    public Func<JObject, TProject> CreateProject = null!;
    public Func<string, JToken> Request = null!;
    public Func<Action, string, Thread> StartThread = null!;
    public Func<string, string> SimplifyChinese = null!;
    public Func<List<WikiEntry>> WikiEntries = null!;
    public Action<string> Info = null!, Warning = null!;
    public Action<Exception, string> Warn = null!;
}
public sealed class ResourceSearchTask {
    public Func<bool> IsCanceled = null!;
    public Func<double> Progress = null!;
    public Action<double> SetProgress = null!;
}
