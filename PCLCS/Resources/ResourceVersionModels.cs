using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public enum ResourceReleaseTypes { Release = 1, Beta = 2, Alpha = 3 }
public enum ResourceDownloadReason { Standalone, Dependency, ModPack, Update }
public sealed record ModDownloadDependencyReference(string? ProjectId, string? VersionId);

/// <summary>Only LF's settings-dependent mirror/debug/platform primitives are supplied by the host.</summary>
public sealed class ResourceVersionServices {
    public Func<string, string> MirrorUrl = null!;
    public Func<bool> DebugMode = null!;
    public Func<string> ImageRoot = null!;
}

// LF ResourceVersion.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983, identical to
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8; LF LICENCE.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModComp.cs/CompFile was reviewed
// (CE Plain Craft Launcher 2/LICENCE; LF LICENCE); LF's mutable fields, enum identity and JSON schema remain authoritative.
public abstract partial class ResourceVersionModelData {
    protected ResourceVersionModelData(ResourceVersionServices services) { Services = services; }
    [JsonIgnore] public ResourceVersionServices Services { get; }
    public string? Id;
    public ResourceTypes ResourceType;
    public string? Display;
    public DateTime ReleaseDate;
    public int DownloadCount;
    public string? Version;
    public ModLoaders ModLoaders = ModLoaders.None;
    public List<string> GameVersions = null!;
    public string? FileName;
    public List<string>? DownloadUrls;
    public string? Hash;
    public int Size = -1;
    [JsonIgnore] public string? ProjectId;
    [JsonIgnore] public List<string>? ExactGameVersions;
    // Null identifies an older cache entry whose full dependency declarations
    // must be fetched before offering an automatic install.
    [JsonIgnore] public List<ModDownloadDependencyReference>? RequiredDependencyReferences;
    internal List<string> RawDependencies = new();
    public List<string> Dependencies = new();
    [JsonIgnore] public abstract int ReleaseTypeCode { get; set; }
    public string ReleaseTypeDisplay => ReleaseTypeCode switch {
        1 => "正式版", 2 => Services.DebugMode() ? "Beta 版" : "测试版",
        _ => Services.DebugMode() ? "Alpha 版" : "早期测试版"
    };
    public bool DownloadAvailable => FileName is not null && DownloadUrls is not null;
    public override string ToString() => $"{Id}: {FileName}";
}

/// <summary>The closed legacy enum is the original public field's only storage.</summary>
public partial class ResourceVersionData<TRelease> : ResourceVersionModelData where TRelease : struct, Enum {
    public ResourceVersionData(ResourceVersionServices services) : base(services) { }
    public TRelease ReleaseType;
    [JsonIgnore] public override int ReleaseTypeCode {
        get => Convert.ToInt32(ReleaseType);
        set => ReleaseType = (TRelease)Enum.ToObject(typeof(TRelease), value);
    }
}

/// <summary>Actual C# resource version model, sharing production rules with the typed legacy facade.</summary>
public sealed class ResourceVersionModel : ResourceVersionData<ResourceReleaseTypes> {
    public ResourceVersionModel(ResourceVersionServices services) : base(services) { }
}

public sealed class ResourceDownloadDescriptor {
    public IEnumerable<string> Urls = null!;
    public string Destination = null!;
    public string? Hash;
    public int Size;
}
