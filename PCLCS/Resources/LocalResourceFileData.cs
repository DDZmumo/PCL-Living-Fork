using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed class LocalResourceFileServices<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    public Func<JObject, TProject> ReadProject = null!;
    public Func<JObject, TVersion> ReadVersion = null!;
    public Func<bool> HideUpdates = null!, DisableInstanceUpdates = null!;
    public Func<string, string> ReadHashCache = null!;
    public Action<string, string> WriteHashCache = null!;
    public Func<string, byte[]> ReadBytes = null!;
    public Func<string, string> Sha1 = null!;
}

// Original LF LocalResourceFile.vb, LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// identical PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8; LF LICENCE.
// Fixed CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModLocalComp.cs reviewed;
// LF's FileInfo/cache key, mutable metadata and event timing remain authoritative.
public class LocalResourceFileData<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    private readonly LocalResourceFileServices<TProject, TVersion> services;
    public readonly FileInfo File;
    [Newtonsoft.Json.JsonIgnore] public string StorageKey => PathUtils.RemoveExtendedPrefix(File.FullName);
    [Newtonsoft.Json.JsonIgnore] public IReadOnlyCollection<string> DeclaredModIds = Array.Empty<string>();
    public LocalResourceFileData(FileInfo file, LocalResourceFileServices<TProject, TVersion> services) { File = file; this.services = services; }
    public bool IsEnabled => new[] { ".jar", ".zip", ".litemod" }.Contains(File.Extension.Lower());
    public string EnabledName => File.Name.Replace(".disabled", "").Replace(".old", "");
    public string EnabledFullName => $"{File.DirectoryName}\\{EnabledName}";
    protected virtual void NotifyUpdated() { }
    private string? _Display, _Description;
    public string Display {
        get {
            if (_Display is null) _Display = ProjectVersion?.Display;
            if (_Display is null) _Display = PathUtils.GetFileNameWithoutExtension(EnabledName);
            return _Display;
        }
        set {
            if (_Display is null && value is not null && !value.Contains("modname") && value.Lower() != "name" && value.Length > 1 && Conversion.Val(value).ToString() != value)
                _Display = value;
        }
    }
    public string? Description {
        get => _Description;
        set {
            if (_Description is null && value is not null && value.Length > 2) {
                _Description = value.Trim('\n');
                if (_Description.Lower().LastIndexOfAny("qwertyuiopasdfghjklzxcvbnm0123456789".ToCharArray()) == _Description.Length - 1) _Description += ".";
            }
        }
    }
    public string? _Version;
    public string? Version {
        get { if (_Version is null) _Version = ProjectVersion?.Version; return _Version; }
        set {
            if (_Version is not null && _Version.RegexCheck("[0-9.\\-]+")) return;
            if (value is not null && value.ContainsIgnoreCase("version")) value = "version";
            _Version = value;
        }
    }
    private TProject? _Project;
    public TProject? Project { get => _Project; set { _Project = value; NotifyUpdated(); } }
    public TVersion? ProjectVersion;
    private TVersion? _UpdateFile;
    public TVersion? UpdateFile { get => _UpdateFile; set { _UpdateFile = value; NotifyUpdated(); } }
    public List<string> ChangelogUrls = new();
    public bool OnlineDataLoaded;
    public JObject ToJson() {
        JObject json = new();
        if (Project is not null) json.Add("Project", Project.ToJson());
        json.Add("ChangelogUrls", new JArray(ChangelogUrls));
        json.Add("OnlineDataLoaded", OnlineDataLoaded);
        if (ProjectVersion is not null) json.Add("Version", ProjectVersion.ToCacheJson());
        if (UpdateFile is not null) json.Add("UpdateFile", UpdateFile.ToCacheJson());
        return json;
    }
    public void FromJson(JObject json) {
        OnlineDataLoaded = (bool)json["OnlineDataLoaded"]!;
        if (json.ContainsKey("Project")) Project = services.ReadProject((JObject)json["Project"]!);
        if (json.ContainsKey("ChangelogUrls")) ChangelogUrls = json["ChangelogUrls"]!.ToObject<List<string>>()!;
        if (json.ContainsKey("Version")) ProjectVersion = services.ReadVersion((JObject)json["Version"]!);
        if (json.ContainsKey("UpdateFile")) UpdateFile = services.ReadVersion((JObject)json["UpdateFile"]!);
    }
    public bool HasUpdate => !services.HideUpdates() && !services.DisableInstanceUpdates() && UpdateFile is not null;
    private uint? _CurseForgeHash;
    public uint CurseForgeHash {
        get {
            if (_CurseForgeHash is null) {
                string cacheKey = $"{EnabledFullName}-{File.LastWriteTime.ToLongTimeString()}-{File.Length}-C".GetStableHashCode().ToString();
                string cached = services.ReadHashCache(cacheKey);
                if (cached != "" && cached.RegexCheck(@"^\d+$")) { _CurseForgeHash = Conversions.ToUInteger(cached); return _CurseForgeHash.Value; }
                List<byte> data = new();
                foreach (byte b in services.ReadBytes(File.FullName)) {
                    if (b == 9 || b == 10 || b == 13 || b == 32) continue;
                    data.Add(b);
                }
                uint hash = Fingerprint(data);
                _CurseForgeHash = hash;
                services.WriteHashCache(cacheKey, hash.ToString());
            }
            return _CurseForgeHash.Value;
        }
    }
    // Exact original MurmurHash2 after whitespace removal; no new streaming/cache policy.
    public static uint Fingerprint(IList<byte> data) {
        int length = data.Count;
        uint h = 1U ^ (uint)length;
        int i = 0;
        unchecked {
            for (; i <= length - 4; i += 4) {
                uint k = data[i] | (uint)data[i + 1] << 8 | (uint)data[i + 2] << 16 | (uint)data[i + 3] << 24;
                k *= 0x5BD1E995U; k ^= k >> 24; k *= 0x5BD1E995U;
                h *= 0x5BD1E995U; h ^= k;
            }
            switch (length - i) {
                case 3: h ^= data[i] | (uint)data[i + 1] << 8; h ^= (uint)data[i + 2] << 16; h *= 0x5BD1E995U; break;
                case 2: h ^= data[i] | (uint)data[i + 1] << 8; h *= 0x5BD1E995U; break;
                case 1: h ^= data[i]; h *= 0x5BD1E995U; break;
            }
            h ^= h >> 13; h *= 0x5BD1E995U; h ^= h >> 15;
        }
        return h;
    }
    private string? _ModrinthHash;
    public string ModrinthHash {
        get {
            if (_ModrinthHash is null) {
                string cacheKey = $"{EnabledFullName}-{File.LastWriteTime.ToLongTimeString()}-{File.Length}-M".GetStableHashCode().ToString();
                string cached = services.ReadHashCache(cacheKey);
                if (cached != "") { _ModrinthHash = cached; return _ModrinthHash; }
                _ModrinthHash = services.Sha1(File.FullName);
                services.WriteHashCache(cacheKey, _ModrinthHash);
            }
            return _ModrinthHash;
        }
    }
    public override string ToString() => File.FullName;
    public override bool Equals(object? obj) => obj is LocalResourceFileData<TProject, TVersion> target && PathUtils.IsSame(File.FullName, target.File.FullName);
    // LF never supplied a path hash; preserve reference hashing, despite path-based Equals.
    public override int GetHashCode() => base.GetHashCode();
}
