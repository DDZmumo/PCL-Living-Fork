using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

/// <summary>Complete LF folder, instance-list, skin, library and asset pipeline; module state is read and written in place.</summary>
public sealed partial class LegacyMinecraftService<TInstance, TFolder, TLibrary, TNetFile, TState, TCard, TFolderKind>(
    LegacyMinecraftHost<TInstance, TFolder, TLibrary, TNetFile, TCard> host)
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TFolder : LegacyMinecraftFolder<TFolderKind>, new()
    where TLibrary : LegacyMinecraftLibrary, new()
    where TNetFile : class
    where TState : struct, Enum where TCard : struct, Enum where TFolderKind : struct, Enum {
    private object? selectedInstanceLast = 0;
    private string? ReadString(string key, TInstance? instance = null) => host.Instance.ReadString(key, instance);
    private int ReadInteger(string key, TInstance? instance = null) => host.Instance.ReadInteger(key, instance);
    private bool ReadBoolean(string key, TInstance? instance = null) => host.Instance.ReadBoolean(key, instance);
    private string? ReadIni(string file, string key, string? defaultValue = "") => host.Instance.ReadIni(file, key, defaultValue);
    private void WriteIni(string file, string key, string? value) => host.Instance.WriteIni(file, key, value);
    private static TCard Card(int value) => LegacyMinecraftCodes.Value<TCard>(value);


    public string McFolderSelected {
        get {
            if (host.ModpackEnabled()) return host.PackFolder();
            if (host.FolderCache() == null) SetMcFolder(ReadString("LaunchFolderSelect")!);
            return host.FolderCache()!;
        }
        set => SetMcFolder(value);
    }
    private void SetMcFolder(string value) {
        if (host.ModpackEnabled()) return;
        if (PathUtils.IsSame(host.FolderCache(), value.Replace("$", Paths.Base))) return;
        host.StoreFolderCache(value.Replace("$", Paths.Base));
        host.StoreFolderCache(PathUtils.AddSlashSuffix(PathUtils.ForCompare(host.FolderCache()!)));
        host.Instance.WriteString("LaunchFolderSelect", value.Replace(Paths.Base, "$"), null);
        Logger.Info($"当前选择的 Minecraft 文件夹：{host.FolderCache()}");
    }

    private readonly LegacyMinecraftFolderLoader<TFolder, TFolderKind> folderLoader = new(
        new LegacyMinecraftFolderLoaderHost<TFolder> {
            ModpackEnabled = () => host.ModpackEnabled(),
            PackFolders = () => host.PackFolders(),
            Folders = () => host.Folders(),
            StoreFolders = value => host.StoreFolders(value),
            ReadString = key => host.Instance.ReadString(key, null),
            ReadBoolean = key => host.Instance.ReadBoolean(key, null),
            WriteString = (key, value) => host.Instance.WriteString(key, value, null),
            CheckPermission = folder => host.CheckPermission(folder),
            Hint = (text, isError) => host.Hint(text, isError),
            Message = (text, title, warn) => host.Message(text, title, warn),
            RandomDelay = (minimum, maximum) => host.RandomDelay(minimum, maximum)
        });

    public void McFolderListLoad() => folderLoader.Load();

    public void RepairSharedMinecraftIcons() {
        try {
            List<string> folders = [Paths.Base, Path.Combine(Paths.Base, ".minecraft"), Path.Combine(Paths.AppData, ".minecraft")];
            string? selected = ReadString("LaunchFolderSelect");
            if (!string.IsNullOrWhiteSpace(selected)) folders.Add(selected!.Replace("$", Paths.Base));
            foreach (string entry in ReadString("LaunchFolders")!.Split('|')) {
                int separator = entry.IndexOf('>');
                if (separator >= 0 && separator < entry.Length - 1) folders.Add(entry.Substring(separator + 1));
            }
            int repaired = host.RepairFolderIcons(folders);
            if (repaired > 0) Logger.Info($"已修复 {repaired} 个共享版本的内置图标配置");
        } catch (Exception ex) {
            Logger.Warn(ex, "检查共享版本图标兼容性失败");
        }
    }

    public void McFolderLauncherProfilesJsonCreate(string folder) => LegacyMinecraftLauncherProfiles.Create(folder);

    public void SelectInstance(TInstance? value) {
        if (ReferenceEquals(selectedInstanceLast, value)) return;
        host.StoreSelectedInstance(value);
        selectedInstanceLast = value;
        Logger.Info($"当前选择的 Minecraft 版本：{value?.PathVersion ?? "无"}");
        WriteIni(McFolderSelected + "PCL.ini", "Version", value?.Name ?? "");
        host.RefreshRecentWorlds();
        if (value == null) return;
        host.ClearDownloadFolderCache();
        if (host.AnimationControlEnabled() == 0 &&
            Operators.CompareString(ReadString("VersionServerNide", value), ReadString("CacheNideServer"), false) != 0 &&
            ReadInteger("VersionServerLogin", value) == 3) {
            host.Instance.WriteString("CacheNideAccess", "", null);
            Logger.Info("服务器改变，要求重新登录统一通行证");
        }
        if (ReadInteger("VersionServerLogin", value) == 3)
            host.Instance.WriteString("CacheNideServer", ReadString("VersionServerNide", value), null);
        if (host.AnimationControlEnabled() == 0 &&
            Operators.CompareString(ReadString("VersionServerAuthServer", value), ReadString("CacheAuthServerServer"), false) != 0 &&
            ReadInteger("VersionServerLogin", value) == 4) {
            host.Instance.WriteString("CacheAuthAccess", "", null);
            Logger.Info("服务器改变，要求重新登录 Authlib-Injector");
        }
        if (ReadInteger("VersionServerLogin", value) == 4) {
            host.Instance.WriteString("CacheAuthServerServer", ReadString("VersionServerAuthServer", value), null);
            host.Instance.WriteString("CacheAuthServerName", ReadString("VersionServerAuthName", value), null);
            host.Instance.WriteString("CacheAuthServerRegister", ReadString("VersionServerAuthRegister", value), null);
        }
    }
}

/// <summary>LF's cancellation-time directory disappearance policy, retaining the original narrow exception handling.</summary>
public static class LegacyMinecraftDirectoryScanner {
    public static DirectoryInfo[] GetDirectories(string folder) {
        // MeloongCore may return extended-namespace DirectoryInfo paths. Keep
        // MMC classification bound to the caller's logical root and actual leaf.
        try { return DirectoryUtils.GetInfo(folder).GetDirectories().Where(item =>
            Directory.Exists(Path.Combine(folder, item.Name)) &&
            !MmcImportVisibility.IsPrivateReservation(Path.Combine(folder, item.Name)) &&
            !MmcInstanceLifecycleVisibility.IsCompletedQuarantine(Path.Combine(folder, item.Name))).ToArray(); }
        catch (DirectoryNotFoundException) { return []; }
    }
    public static bool HasFiles(DirectoryInfo folder) {
        try { return folder.EnumerateFiles().Any(); }
        catch (DirectoryNotFoundException) { return false; }
    }
}
