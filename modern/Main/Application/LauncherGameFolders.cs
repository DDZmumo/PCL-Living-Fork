// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MeloongCore;
using MeloongCore.Wpf;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;
using Folder = PCL.ModMinecraft.McFolder;
using Instance = PCL.ModMinecraft.McInstance;
using FolderRules = PCLCS.FolderSelectionPageController<PCL.ModMinecraft.McFolder, PCL.ModMinecraft.McFolder.Types,
    PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType>;

namespace PCL;

internal static class LauncherGameFolders
{
    private static readonly FolderSelectionPageHost<Folder, Instance> Host = new() {
        ModpackEnabled = () => ModModpackMode.Enabled,
        Folders = () => ModMinecraft.McFolderList,
        AnyLocked = () => ModMinecraft.McInstanceLock.IsAnyLocked,
        Downloading = includeHidden => ModNet.HasDownloadingTask(includeHidden),
        BaseFolder = () => Paths.Base,
        SelectedFolder = () => ModMinecraft.McFolderSelected,
        SetSelectedFolder = path => ModMinecraft.McFolderSelected = path,
        ReadFolders = () => Settings.Get<string>("LaunchFolders"),
        WriteFolders = folders => Settings.Set("LaunchFolders", folders),
        RunThread = work => ModBase.RunInThread(work),
        RunNewThread = (work, name, priority) => ModBase.RunInNewThread(work, name, priority),
        RefreshFolders = () => ModMinecraft.McFolderListLoader.Start(IsForceRestart: true),
        RefreshInstances = RefreshInstances,
        Permission = path => ModBase.CheckPermission(path),
        SelectFolder = (title, multi) => Dialogs.SelectFolder(title, multi),
        Input = PromptName,
        Message = Ask,
        Hint = Hint,
        NewInstance = path => new Instance(path),
        WriteInstanceInteger = (key, value, instance) => Settings.Set(key, value, instance),
        WriteInstanceBoolean = (key, value, instance) => Settings.Set(key, value, instance),
        CreateProfiles = ModMinecraft.McFolderLauncherProfilesJsonCreate,
        WriteIni = (file, key, value) => ModBase.WriteIni(file, key, Conversions.ToString(value)),
        NextUuid = ModBase.GetUuid
    };

    internal static IReadOnlyList<Folder> Read() => ModModpackMode.Enabled ? ModModpackMode.CreatePackFolderList() : ModMinecraft.McFolderList;
    internal static void CreateStartupFolder() => FolderRules.CreateInCurrentPath(Host);
    internal static void Create() => FolderRules.Create(Host);
    internal static void Add() => FolderRules.Add(Host);
    internal static void Add(string path, string name, bool showHint) => FolderRules.AddFolder(path, name, showHint, Host);
    internal static void Change(Folder folder) => WithCurrent(folder, current => FolderRules.SelectFolder(current, Host));
    internal static void Rename(Folder folder) => WithCurrent(folder, current => FolderRules.RenameFolder(current, Host));
    internal static void Remove(Folder folder) => WithCurrent(folder, current => FolderRules.RemoveFolder(current, Host));
    internal static void Delete(Folder folder) => WithCurrent(folder, current => FolderRules.DeleteFolder(current, Host));
    internal static void Refresh(Folder folder) => WithCurrent(folder, current => FolderRules.Refresh(current.Location, Host));
    internal static void Open(Folder folder) => WithCurrent(folder, current => ModBase.OpenExplorer(current.Location));

    internal static void RefreshInstances(bool force) =>
        ModLoader.LoaderFolderRun(ModMinecraft.McInstanceListLoader, ModMinecraft.McFolderSelected,
            force ? ModLoader.LoaderFolderRunType.ForceRun : ModLoader.LoaderFolderRunType.RunOnUpdated,
            MaxDepth: 1, ExtraPath: @"versions\");

    private static void WithCurrent(Folder folder, Action<Folder> execute)
    {
        var current = Read().FirstOrDefault(item => PathUtils.IsSame(item.Location, folder.Location));
        if (current is not null) execute(current);
    }

    internal static int Ask(SelectionPageMessage request) =>
        ModMain.MyMsgBox(request.Text, request.Title, request.Button1, request.Button2, request.Button3, request.IsWarn);

    internal static void Hint(string text, SelectionPageHint tone) =>
        ModMain.Hint(text, tone == SelectionPageHint.Red ? ModMain.HintType.Red :
            tone == SelectionPageHint.Blue ? ModMain.HintType.Blue : ModMain.HintType.Green);

    private static string? PromptName(SelectionPageInput request)
    {
        Collection<Validate> rules = request.DeleteConfirmation
            ? new() { new ValidateSame("Potato", $"请输入 {ModBase.vbLQ}Potato{ModBase.vbRQ}", IgnoreCase: true) }
            : new() { new ValidateNullOrWhiteSpace(), new ValidateLength(1, 30), new ValidateExcept(new[] { ">", "|" }) };
        return ModMain.MyMsgBoxInput(request.Title, request.Caption, request.Default, rules,
            Button1: request.Button1, Button2: request.Button2, IsWarn: request.IsWarn);
    }
}
