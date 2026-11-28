// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MeloongCore.Wpf;
using PCLCS;
using McInstance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal static class ResourceDownloadOperations
{
    internal static readonly Dictionary<ResourceTypes, string> CachedFolders = new();
    internal static readonly ResourceDescriptionTranslation Descriptions = new();

    internal static ResourceDetailDownloadHost<ModLoader.LoaderBase, ModNet.NetFile, ResourceVersion, McInstance> CreateHost() => new() {
        Loaders = ModDownloadLib.SharedInstallLoaders,
        States = new MinecraftInstallStates {
            Waiting = (int)ModBase.LoadState.Waiting, Loading = (int)ModBase.LoadState.Loading,
            Finished = (int)ModBase.LoadState.Finished, Failed = (int)ModBase.LoadState.Failed, Canceled = (int)ModBase.LoadState.Canceled
        },
        CachedFolders = CachedFolders,
        MinecraftFolder = () => ModMinecraft.McFolderSelected,
        SelectedInstance = () => ModMinecraft.McInstanceSelected,
        Instances = () => ModMinecraft.McInstanceList.Values.SelectMany(instances => instances),
        InstanceListFinished = () => ModMinecraft.McInstanceListLoader.State == ModBase.LoadState.Finished,
        LoadInstanceList = () => ModLoader.LoaderFolderRun(ModMinecraft.McInstanceListLoader, ModMinecraft.McFolderSelected,
            ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\", WaitForExit: true),
        RefreshDownload = () => ModMain.FrmMain?.RefreshTaskButton(),
        RibbleDownload = () => ModMain.FrmMain?.RefreshTaskButton(pulse: true),
        TaskManager = () => ModMain.FrmMain?.PageChange(FormMain.PageType.TaskManager),
        RefreshCurrentResources = () => ModMain.FrmMain?.RefreshGlassLocalResources(),
        TranslateSetting = () => Settings.Get<int>("ToolDownloadTranslateV2"),
        NameValidator = folder => {
            var rule = new ValidateFolderName(folder);
            return new DownloadNameValidator {
                Validate = rule.Validate,
                Prompt = initial => ModMain.MyMsgBoxInput("输入版本名称", "", initial, new Collection<Validate> { rule })
            };
        },
        NetFile = (version, target, reason, gameVersion, loaders) => version.ToNetFile(target,
            (ResourceVersion.DownloadReason)reason, gameVersion, loaders),
        InstallMod = ModDownloadInstaller.Start,
        InstallPack = (target, name, logo, source) => ModModpack.ModpackInstall(target, name, logo, source),
        MapDownloadFolder = MapDownloadInstaller.GetDownloadFolder,
        InstallMap = MapDownloadInstaller.Install,
        InterruptOnCancel = (loader, value) => ((ModLoader.LoaderTask<int, string>)loader).InterruptOnCancel = value,
        StateHint = ModDownloadLib.LoaderStateChangedHintOnly,
        ClearFailedFolder = loader => ModDownloadLib.McInstallFailedClearFolder(loader),
        Hint = (text, kind) => ModMain.Hint(text, kind == 1 ? ModMain.HintType.Red : kind == 2 ? ModMain.HintType.Green : ModMain.HintType.Blue),
        SaveFile = (name, folder, extension, description) => Dialogs.SaveFile("选择保存位置", name, folder, new[] { (extension, description) }),
        Explorer = target => ModBase.OpenExplorer(target),
        RunInNewThread = (work, name) => ModBase.RunInNewThread(work, name),
        RunInUi = work => ModBase.RunInUi(work)
    };
}
