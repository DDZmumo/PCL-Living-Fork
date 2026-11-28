// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using MeloongCore;
using MeloongCore.Wpf;
using PCLCS;
using Host = PCLCS.ResourcePageHost<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;
using Request = PCLCS.ResourcePageController<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>.Request;

namespace PCL;

internal static class InstanceResourceApplication
{
    internal static Host CreateHost(InstanceResourceKind kind, InstanceResourceScanCache<InstanceResourceEntry> cache,
        Func<bool> active, Func<bool> animate, Action<Request> start, Action cancel, Func<bool> failed,
        Action<bool, Action, KeyEventHandler> connect) => new()
    {
        CurrentInstance = () => LauncherInstanceContext.Instance,
        GameFolder = instance => instance.PathIndie,
        VersionPath = instance => instance.PathVersion,
        GameVersion = instance => instance.Version.VanillaName,
        AllowsInstance = instance => ModModpackMode.AllowsInstance(instance.PathVersion),
        IsLocked = ModMinecraft.McInstanceLock.IsLocked,
        NeedsSchematicLoader = instance => instance.Version.ModLoaders == ModLoaders.None,
        QuickPlaySupported = QuickPlayWorlds.Supported,
        HasRunningGame = () => ModWatcher.HasRunningMinecraft,
        ModpackEnabled = () => ModModpackMode.Enabled,
        IsCurrentPage = active, AnimationEnabled = animate, BackupBusy = () => WorldBackups.IsBusy,
        SamePath = (left, right) => PathUtils.IsSame(left, right),
        RemoveExtendedPrefix = PathUtils.RemoveExtendedPrefix,
        Dispatch = action => ModBase.RunInUi(action),
        Background = (run, name) => ModBase.RunInNewThread(run, name),
        Warn = (error, message) => Logger.Warn(error, message),
        Error = (error, message) => Logger.Error(error, message, LogBehavior.Toast),
        Hint = (text, success) => ModMain.Hint(text, success ? ModMain.HintType.Green : ModMain.HintType.Red),
        Message = (text, title, first, second, warning) => ModMain.MyMsgBox(text, title, first, second, IsWarn: warning),
        Input = PromptName,
        SelectFiles = (title, multi, extensions) => Dialogs.SelectFile(title, multi, filter: new[] { (extensions, "资源文件") }).ToArray(),
        SaveFile = (title, name, extension) => Dialogs.SaveFile(title, name, filter: new[] { (extension, "资源文件") }),
        SelectFolder = (title, multi) => Dialogs.SelectFolder(title, multi).ToArray(),
        OpenExplorer = path => ModBase.OpenExplorer(path),
        OpenWebsite = address => ModBase.OpenWebsite(address),
        CreateDirectory = path => DirectoryUtils.Create(path),
        OpenProcess = info => Process.Start(info),
        Navigate = Browse,
        QuickPlay = (instance, name) => QuickPlayWorlds.Start(instance, name),
        DescribeWorld = WorldMetadata.Describe,
        Scan = (root, folder, type, cancel) => InstanceResourceStore.ScanCached(root, folder, (InstanceResourceKind)type, cancel, cache),
        UpdatesSupported = type => InstanceResourceUpdates.Supported((InstanceResourceKind)type),
        CheckOnline = (root, items, type, version, cancel) => InstanceResourceUpdates.Check(root, items, (InstanceResourceKind)type, version, cancel),
        InstallOnline = (root, items, type, version, cancel) => InstanceResourceUpdates.Install(root, items, (InstanceResourceKind)type, version, cancel),
        OnlineEntry = info => info.Entry, HasUpdate = info => info.HasUpdate,
        ProjectName = info => info.ProjectName, Website = info => info.Website, Status = info => info.Status, Tags = info => info.Tags,
        BackupPoints = path => WorldBackups.Summaries(path),
        StartBackup = (instance, ids, restore) => WorldBackupsUi.Start(instance, RestoreId: restore, DeleteIds: ids),
        StartLoader = start, CancelLoader = cancel, LoaderFailed = failed,
        ClearScanCache = () => { cache.Clear(); if (kind == InstanceResourceKind.Saves) WorldBackups.ClearSummaryCache(); },
        BindExternalEvents = connect
    };

    private static string? PromptName(string title, string text, string initial, string? folder, bool strict)
    {
        if (folder is not null) return ModMain.MyMsgBoxInput(title, text, initial, new Collection<Validate> { new ValidateFolderName(folder) });
        if (strict) return ModMain.MyMsgBoxInput(title, text, initial, new Collection<Validate> { new ValidateNullOrWhiteSpace(), new ValidateLength(1, 120) });
        return ModMain.MyMsgBoxInput(title, text, initial);
    }

    private static void Browse(int target, ModMinecraft.McInstance instance, string query)
    {
        if (target == 0) {
            if (ModMinecraft.McInstanceLock.BlocksNavigation(instance)) {
                ModMain.Hint("该版本暂时无法进入修改页面。", ModMain.HintType.Red);
                return;
            }
            LauncherInstanceContext.Instance = instance;
            ModMain.FrmMain.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceModify);
            return;
        }
        FormMain.PageSubType category = target switch {
            1 => FormMain.PageSubType.DownloadMod, 2 => FormMain.PageSubType.DownloadResourcePack, _ => FormMain.PageSubType.DownloadShader
        };
        ModMain.FrmMain.BrowseResources(category, instance, query);
    }
}
