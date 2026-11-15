using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>Typed access to LF settings and platform primitives. Every instance keeps its actual model and config provider.</summary>
public sealed class LegacyMinecraftInstanceHost<TInstance> where TInstance : class {
    public Func<string> SelectedFolder = null!;
    public Func<string, TInstance> CreateInstance = null!;
    public Func<string, TInstance?, string?> ReadString = null!;
    public Func<string, TInstance?, int> ReadInteger = null!;
    public Func<string, TInstance?, bool> ReadBoolean = null!;
    public Func<string, TInstance, bool> HasSaved = null!;
    public Action<string, string?, TInstance?> WriteString = null!;
    public Action<string, int, TInstance> WriteInteger = null!;
    public Action<string, bool, TInstance> WriteBoolean = null!;
    public Func<string, string, string?, string?> ReadIni = null!;
    public Action<string, string, string?> WriteIni = null!;
    public Action<string> ClearIniCache = null!;
    public Action<TInstance> ClearInstanceSettingsCache = null!;
    public Func<bool> OnUiThread = null!;
    public Func<string> ImagePath = null!;
    public Func<string?, string?> NormalizeResourceUri = null!;
    public Func<string?, string?> LogoForStorage = null!;
    public Func<string, string?, string?> ReadVersionLogo = null!;
    public Action<string, string?> WriteVersionLogo = null!;
}

public sealed class LegacyMinecraftHost<TInstance, TFolder, TLibrary, TNetFile, TCard> where TInstance : class where TNetFile : class where TCard : struct, Enum {
    public LegacyMinecraftInstanceHost<TInstance> Instance = null!;
    public Func<bool> ModpackEnabled = null!;
    public Func<string> PackFolder = null!;
    public Func<List<TFolder>> PackFolders = null!;
    // Preserve the original module's public fields and private reflection entry points as the only state.
    public Func<string?> FolderCache = null!;
    public Action<string> StoreFolderCache = null!;
    public Func<List<TFolder>> Folders = null!;
    public Action<List<TFolder>> StoreFolders = null!;
    public Func<TInstance?> SelectedInstance = null!;
    public Action<TInstance?> StoreSelectedInstance = null!;
    public Func<Dictionary<TCard, List<TInstance>>> Instances = null!;
    public Action<Dictionary<TCard, List<TInstance>>> StoreInstances = null!;
    public Func<bool> ForceRefresh = null!;
    public Action<bool> StoreForceRefresh = null!;
    public Func<bool> FirstLoad = null!;
    public Action<bool> StoreFirstLoad = null!;
    public Func<int> AnimationControlEnabled = null!;
    public Action RefreshRecentWorlds = null!;
    public Action ClearDownloadFolderCache = null!;
    public Action<string> CheckPermission = null!;
    public Action<int, int> RandomDelay = null!;
    public Func<IEnumerable<string>, int> RepairFolderIcons = null!;
    public Action<string, bool> Hint = null!;
    public Action<string, string, bool> Message = null!;
    public Func<string?> SelectSkin = null!;
    public Func<string, (double Width, double Height)> ImageSize = null!;
    public Func<int> SelectSkinModel = null!;
    public Func<string, string?, string> RequestJson = null!;
    public Action<string, string> DownloadFile = null!;
    public Func<string> TempFolder = null!;
    public Func<string> PureFolder = null!;
    public Func<string> OsVersion = null!;
    public Func<int> NextUuid = null!;
    public Func<TInstance, bool, TNetFile?> ClientJar = null!;
    public Func<IEnumerable<string>, string, FileChecker, TNetFile> CreateNetFile = null!;
    public Func<TNetFile, string> DownloadPath = null!;
    public Func<bool> PreferMojang = null!;
    public Func<string, IEnumerable<string>> LauncherSources = null!;
    public Func<string, IEnumerable<string>> LibrarySources = null!;
    public Func<string, IEnumerable<string>> AssetSources = null!;
    public Action<string, string> ExtractResource = null!;
    public Func<string?> AccessToken = null!;
    public Func<string?> LoginUuid = null!;
    public Func<LegacyMinecraftUpdateNotice, int> ShowUpdateNotice = null!;
    public Action<JToken> ShowUpdateLog = null!;
    public Action<string> OpenDownload = null!;
}

public sealed class LegacyMinecraftUpdateNotice {
    public string Text = null!;
    public string ThirdButton = null!;
    public Action ThirdAction = null!;
}

public sealed class LegacyMinecraftSkinInfo {
    public bool IsSlim;
    public string? LocalFile;
    // Original misspelling is part of LF's public DTO.
    public bool IsVaild;
}

public class LegacyMinecraftFolder<TKind> where TKind : struct, Enum {
    public string? Name;
    public string Location = null!;
    public TKind Type;
    public override bool Equals(object? obj) => obj is LegacyMinecraftFolder<TKind> folder &&
        Microsoft.VisualBasic.CompilerServices.Operators.CompareString(Name, folder.Name, false) == 0 &&
        PathUtils.IsSame(Location, folder.Location) && EqualityComparer<TKind>.Default.Equals(Type, folder.Type);
    // LF originally retained Object's identity hash despite its value equality.
    public override int GetHashCode() => base.GetHashCode();
    public override string ToString() => Location;
}

public class LegacyMinecraftLibrary {
    public string LocalPath = null!;
    public long Size;
    public bool IsNatives;
    public string? SHA1;
    public string? Url {
        get => url;
        set => url = string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private string? url;
    public string? OriginalName;
    public string? Name {
        get {
            if (OriginalName == null) return null;
            List<string> parts = new(OriginalName.Split(':'));
            parts.RemoveAt(2);
            return string.Join(":", parts);
        }
    }
    public override string ToString() => (IsNatives ? "[Native] " : "") + StringUtils.FormatByteSize(Size) + " | " + LocalPath;
}

/// <summary>LF snapshot/write reservation stored in the one original owner dictionary.</summary>
public sealed class LegacyMinecraftInstanceLease {
    public IWorker Worker = null!;
    public bool BlocksNavigation;
}
public sealed class LegacyMinecraftInstanceLocks(ConcurrentDictionary<string, LegacyMinecraftInstanceLease> lockedPaths) {
    public bool IsAnyLocked => !lockedPaths.IsEmpty;
    public bool TryAcquire(IWorker worker, string instancePath) => Acquire(worker, instancePath, true);
    public bool TryAcquireSnapshot(IWorker worker, string instancePath) => Acquire(worker, instancePath, false);
    private bool Acquire(IWorker worker, string instancePath, bool blocksNavigation) {
        instancePath = PathUtils.ForCompare(instancePath);
        LegacyMinecraftInstanceLease held = new() { Worker = worker, BlocksNavigation = blocksNavigation };
        if (!lockedPaths.TryAdd(instancePath, held)) return false;
        worker.Stopped += () => ((ICollection<KeyValuePair<string, LegacyMinecraftInstanceLease>>)lockedPaths).Remove(new KeyValuePair<string, LegacyMinecraftInstanceLease>(instancePath, held));
        return true;
    }
    public bool IsLocked(string? instancePath) => instancePath != null && lockedPaths.ContainsKey(PathUtils.ForCompare(instancePath));
    public void Release(IWorker worker, string instancePath) {
        string path = PathUtils.ForCompare(instancePath);
        if (lockedPaths.TryGetValue(path, out LegacyMinecraftInstanceLease? held) && ReferenceEquals(held.Worker, worker))
            ((ICollection<KeyValuePair<string, LegacyMinecraftInstanceLease>>)lockedPaths).Remove(new KeyValuePair<string, LegacyMinecraftInstanceLease>(path, held));
    }
    public bool BlocksNavigation(string? instancePath) => instancePath != null && lockedPaths.TryGetValue(PathUtils.ForCompare(instancePath), out LegacyMinecraftInstanceLease? held) && held.BlocksNavigation;
}

internal static class LegacyMinecraftCodes {
    public static T Value<T>(int value) where T : struct, Enum => (T)Enum.ToObject(typeof(T), value);
    public static int Number<T>(T value) where T : struct, Enum => Convert.ToInt32(value);
}

// Original ModBase.SortByComparison selection order, including input removal and first-equal behavior.
// Shared by the instance engines and WPF catalog adapters. Callers retain the original
// destructive list consumption and equal-item ordering; this is not a general LINQ sort.
public static class LegacyMinecraftListOrder {
    public static List<T> SortByComparison<T>(IList<T> list, Func<T, T, bool> sortRule) {
        List<T> result = [];
        while (list.Count != 0) {
            T highest = list[0];
            for (int i = 1; i < list.Count; i++) if (sortRule(list[i], highest)) highest = list[i];
            list.Remove(highest);
            result.Add(highest);
        }
        return result;
    }
}
