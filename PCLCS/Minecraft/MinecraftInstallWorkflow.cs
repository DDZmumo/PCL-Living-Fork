using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed partial class MinecraftInstallEngine<TLoader, TNetFile, TInstance, TState, TCard, TOptiFine, TForgeLike, TForge, TNeo, TLite, TLib, TResource, TRequest> {
    public bool McInstallStart(TRequest request) {
        try { TLoader loader = Combo<string>(request.NewInstanceName + " 安装", McInstallLoadersGet(request)); Begin(loader, request.InstanceFolder, true); return true; }
        catch (Exception error) { Logger.Error(error, "开始合并安装失败"); return false; }
    }
    public bool McModificationStart(TRequest request, TInstance instance) {
        try {
            MmcInstanceInspector.AssertLegacyOperationAllowed(instance.PathVersion, "组件修改");
            string targetFolder = instance.PathIndie;
            List<TLoader> loaders = new() { Task<int, int>("检查 Natives 文件占用", _ => {
                foreach (string file in DirectoryUtils.EnumerateFiles(host.NativesFolder(instance), includeSubDirectories: true, searchPattern: "*.dll"))
                    if (!FileUtils.IsAvailable(file)) throw new Exception("Natives 文件正在被占用，请关闭游戏后再次尝试：" + file);
            }, show: false) };
            string buildFolder = host.RequestTempFolder(false) + instance.Name + "\\";
            TRequest build = new() { NewInstanceName = instance.Name, InstanceFolder = buildFolder, Components = request.Components, MinecraftJson = request.MinecraftJson, ForgeEntry = request.ForgeEntry,
                NeoForgeEntry = request.NeoForgeEntry, FabricApi = request.FabricApi, OptiFabric = request.OptiFabric, LiteLoaderEntry = request.LiteLoaderEntry, IsIntermediateOutput = true };
            loaders.AddRange(McInstallLoadersGet(build));
            bool renameFailed = false;
            loaders.Add(Task<int, int>("修改版本", _ => {
                McInstallCommit(instance, Configs.McInstallManifest.Get(host.NewInstance(buildFolder).Config)!, buildFolder, targetFolder);
                TInstance? updated;
                if (request.NewInstanceName is null) updated = host.NewInstance(instance.PathVersion).Load();
                else {
                    try { updated = instance.Rename(request.NewInstanceName); }
                    catch (Exception error) {
                        renameFailed = true; Logger.Warn(error, "版本修改成功，但重命名为 " + request.NewInstanceName + " 失败：" + error.GetDisplay(false), LogBehavior.None);
                        updated = host.NewInstance(instance.PathVersion).Load();
                    }
                }
                updated ??= host.NewInstance(instance.PathVersion).Load();
                host.RunInUiWait(() => {
                    host.WriteIni(host.MinecraftFolder() + "PCL.ini", "InstanceCache", "");
                    TInstance? page = host.InstancePage();
                    if (page is not null && PathUtils.IsSame(page.PathVersion, instance.PathVersion)) host.SetInstancePage(updated);
                    TInstance? selected = host.SelectedInstance();
                    if (selected is not null && PathUtils.IsSame(selected.PathVersion, instance.PathVersion)) host.SetSelectedInstance(updated);
                });
            }, 1, block: true));
            TLoader loader = Combo<string>(instance.Name + " 修改", loaders);
            host.Loaders.OnStateChanged(loader, current => {
                int state = host.Loaders.State(current);
                if (state == host.States.Finished) Hint(host.Loaders.Name(current) + (renameFailed ? "成功，但重命名失败！" : "成功！"), 2);
                else if (state == host.States.Failed) Message(host.Loaders.Error(current)!.GetDisplay(true), host.Loaders.Name(current) + "失败", true);
                else if (state == host.States.Canceled) Hint(host.Loaders.Name(current) + "已取消！");
            });
            RedoableWorker worker = new(host.FileWorkload(loader, instance.PathVersion));
            if (host.TryAcquire(worker, instance.PathVersion)) {
                worker.Stopped += () => host.RunInUi(host.RefreshInstanceList);
                worker.Start(); host.RefreshInstanceList();
            } else { Logger.Error("版本被其他 Worker 占用，版本修改失败"); return false; }
            host.Loaders.AddTaskbar<string>(loader); host.RefreshDownload(); host.RibbleDownload(); return true;
        } catch (Exception error) { host.RefreshInstanceList(); Logger.Error(error, "开始修改版本失败", LogBehavior.Toast); return false; }
    }
    public void McInstallCommit(TInstance instance, McInstallManifest newManifest, string buildFolder, string targetFolder) {
        MmcInstanceCapabilities.RequireLegacy(instance.PathVersion, "组件修改");
        MmcInstanceCapabilities.RequireLegacyContainingInstance(targetFolder, "组件修改");
        host.BeforeMinecraftChange(instance, instance.Version.VanillaName, newManifest.Components.MinecraftVersion, CancellationToken.None);
        host.CaptureEnvironment(instance, "修改游戏组件前", CancellationToken.None);
        IEnumerable<McInstallManifest.ManagedFile> oldFiles = Configs.McInstallManifest.Get(instance.Config)?.ManagedFiles ?? Array.Empty<McInstallManifest.ManagedFile>();
        IReadOnlyList<McInstallManifest.ManagedFile> newFiles = newManifest.ManagedFiles;
        List<string> deletes = new(); List<(string Source, string Target)> moves = new();
        foreach (var file in newFiles) moves.Add((Path.Combine(buildFolder, file.RelativePath), Path.Combine(targetFolder, file.RelativePath)));
        HashSet<string> newPaths = new(newFiles.Select(file => file.RelativePath));
        foreach (var file in oldFiles) if (!newPaths.Contains(file.RelativePath)) deletes.Add(PathUtils.ForCompare(Path.Combine(targetFolder, file.RelativePath)));
        if (DirectoryUtils.Exists(buildFolder + "resources\\")) DirectoryUtils.Copy(buildFolder + "resources\\", targetFolder + "resources\\");
        string oldJson = instance.GetJsonPath();
        deletes.Add(oldJson); deletes.Add(Path.ChangeExtension(oldJson, ".jar")); deletes.Add(instance.PathVersion + instance.Name + ".jar");
        deletes = deletes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        instance.Config.Save();
        foreach (string path in deletes) FileUtils.Delete(path, true);
        FileUtils.Move(buildFolder + instance.Name + ".jar", instance.PathVersion + instance.Name + ".jar");
        FileUtils.Move(buildFolder + instance.Name + ".json", oldJson);
        foreach (var file in moves) { DirectoryUtils.Create(PathUtils.RemoveLastPart(file.Target)); FileUtils.Copy(file.Source, file.Target); }
        List<McInstallManifest.ManagedFile> managed = newFiles.Select(file => new McInstallManifest.ManagedFile(file.RelativePath, file.Sha1, FileUtils.GetInfo(Path.Combine(targetFolder, file.RelativePath)).LastWriteTimeUtc, file.Component)).ToList();
        Configs.McInstallManifest.Set(new McInstallManifest(newManifest.SchemaVersion, newManifest.Components, newManifest.LastModifiedUtc, managed), instance.Config); instance.Config.Save();
    }
    public void LoaderStateChangedHintOnly(TLoader loader) {
        int state = host.Loaders.State(loader);
        if (state == host.States.Finished) Hint(host.Loaders.Name(loader) + "成功！", 2);
        else if (state == host.States.Failed) Hint(host.Loaders.Name(loader) + "失败：" + host.Loaders.Error(loader)!.GetDisplay(false), 1);
        else if (state == host.States.Canceled) Hint(host.Loaders.Name(loader) + "已取消！");
    }
    public void McInstallState(TLoader loader) {
        int state = host.Loaders.State(loader);
        if (state == host.States.Finished) { host.WriteIni(host.MinecraftFolder() + "PCL.ini", "InstanceCache", ""); Hint(host.Loaders.Name(loader) + "成功！", 2); }
        else if (state == host.States.Failed) Message(host.Loaders.Error(loader)!.GetDisplay(true), host.Loaders.Name(loader) + "失败", true);
        else if (state == host.States.Canceled) Hint(host.Loaders.Name(loader) + "已取消！");
        else if (state == host.States.Loading) return;
        McInstallFailedClearFolder(loader); host.RefreshInstanceList();
    }
    public void McInstallFailedClearFolder(object loader) {
        int state = host.CleanupState(loader);
        if (state != host.States.Failed && state != host.States.Canceled) return;
        host.RunInThread(() => {
            try {
                Thread.Sleep(1000);
                MmcInstanceInspection management = MmcInstanceInspector.Read(host.CleanupInput(loader));
                if (!management.IsLegacy || !management.Management.CanUseLegacyBackend) {
                    Logger.Warn("保留 MMC 或尚未确认归属的安装现场，等待其专用恢复流程：" + host.CleanupInput(loader));
                    return;
                }
                if (DirectoryUtils.Exists(host.CleanupInput(loader) + "saves\\") || DirectoryUtils.Exists(host.CleanupInput(loader) + "versions\\")) Logger.Warn("由于版本已被独立启动，不清理版本文件夹：" + host.CleanupInput(loader));
                else { Logger.Warn("由于下载失败或取消，清理版本文件夹：" + host.CleanupInput(loader)); DirectoryUtils.Delete(host.CleanupInput(loader)); }
            } catch (Exception error) { Logger.Warn(error, "下载失败或取消后清理版本文件夹失败"); }
        });
    }
}
