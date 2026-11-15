using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.VisualBasic;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class ResourceDetailPageController<TProject, TVersion, TCard, TResourceItem, TInstance, TState, TInstanceCard, TLoader, TNetFile, TAnimation> {
    private static string SafeDisplay(string name) => name.Replace("\\", "＼").Replace("/", "／").Replace("|", "｜").Replace(":", "：").Replace("<", "＜").Replace(">", "＞").Replace("*", "＊").Replace("?", "？").Replace("\"", "").Replace("： ", "：");
    public void InstallClick(FrameworkElement sender, EventArgs args) {
        try {
            TVersion file = (TVersion)sender.Tag;
            string name = Project.Platform + " 整合包下载：" + Project.TranslatedName + " ";
            string packName = SafeDisplay(Project.TranslatedName!.Replace(".zip", "").Replace(".rar", "").Replace(".mrpack", ""));
            DownloadNameValidator validator = download.NameValidator(download.MinecraftFolder() + "versions");
            if (!Same(validator.Validate(packName), "")) packName = "";
            string? instanceName = validator.Prompt(packName);
            if (string.IsNullOrEmpty(instanceName)) return;
            string target = download.MinecraftFolder() + "versions\\" + instanceName + "\\原始整合包." + (Project.Platform == ResourcePlatforms.CurseForge ? "zip" : "mrpack");
            string logo = host.HeaderLogo(ResourceItem!);
            List<TLoader> loaders = new() {
                download.Loaders.Download("下载整合包文件", new() { download.NetFile(file, target, ResourceDownloadReason.Standalone, file.GameVersions.FirstOrDefault(), file.ModLoaders) }, new MinecraftDownloadLoaderSettings { ProgressWeight = 10, Block = true }),
                download.Loaders.NewTask<int, int>("准备安装整合包", _ => download.InstallPack(target, instanceName!, FileUtils.Exists(logo) ? logo : null,
                    new JObject(new JProperty("platform", Project.Platform.ToString()), new JProperty("project", Project.Id), new JProperty("version", file.Id))), new MinecraftDownloadLoaderSettings { ProgressWeight = 0.1 })
            };
            TLoader loader = download.Loaders.Combo<string>(name, loaders, new MinecraftDownloadLoaderSettings());
            download.Loaders.OnStateChanged(loader, current => {
                int state = download.Loaders.State(current);
                if (state == download.States.Failed) download.Hint(download.Loaders.Name(current) + "失败：" + download.Loaders.Error(current)!.GetDisplay(false), 1);
                else if (state == download.States.Canceled) download.Hint(download.Loaders.Name(current) + "已取消！", 0);
                else if (state == download.States.Loading) return;
                download.ClearFailedFolder(current);
            });
            download.Loaders.Start(loader, download.MinecraftFolder() + "versions\\" + instanceName + "\\"); download.Loaders.AddTaskbar<string>(loader);
            download.RefreshDownload(); download.TaskManager();
        } catch (Exception error) { Logger.Error(error, "下载资源整合包失败"); }
    }
    private bool InstanceSuitable(TVersion file, TInstance instance, ModLoaders allowedLoaders) {
        if (!instance.IsLoaded) instance.Load();
        if (file.ResourceType == ResourceTypes.Mod || file.ResourceType == ResourceTypes.DataPack) {
            if (!file.GameVersions.Any(version => {
                bool fileSnapshot = version.Contains("预览版") || !version.Contains(".");
                bool instanceSnapshot = instance.Version.VanillaName.Contains("snapshot") || !instance.Version.VanillaName.Contains(".");
                if (fileSnapshot != instanceSnapshot) return false;
                if (!fileSnapshot && !instanceSnapshot) return Same(version, instance.Version.VanillaName);
                bool fileNew = version.Contains(".") && Conversion.Val(version.BeforeFirst(".")) > 1, instanceNew = instance.Version.Drop > 250;
                if (fileNew != instanceNew) return false;
                if (fileNew && instanceNew) return Same(version.BeforeFirst(" "), instance.Version.VanillaName.BeforeFirst("-"));
                return version.Contains("w") && instance.Version.VanillaName.Contains("w") ? Same(version, instance.Version.VanillaName) : true;
            })) return false;
        }
        return allowedLoaders == ModLoaders.None || allowedLoaders.Flags().Intersect(instance.Version.ModLoaders.Flags()).Any();
    }
    public void SaveClick(object sender, EventArgs args) {
        FrameworkElement element = (FrameworkElement)sender;
        TVersion file = (TVersion)(host.IsVersionRow(element) ? element : (FrameworkElement)element.Parent).Tag;
        download.RunInNewThread(() => {
            try {
                string? description = file.ResourceType switch { ResourceTypes.ModPack => "整合包", ResourceTypes.Mod => "Mod ", ResourceTypes.ResourcePack => "资源包", ResourceTypes.Shader => "光影包", ResourceTypes.DataPack => "数据包", ResourceTypes.Map => "地图", _ => null };
                string? defaultFolder = null, mapSaves = null;
                if (file.ResourceType != ResourceTypes.ModPack) {
                    string? subFolder = file.ResourceType switch { ResourceTypes.Mod => "mods\\", ResourceTypes.ResourcePack => "resourcepacks\\", ResourceTypes.Shader => "shaderpacks\\", ResourceTypes.DataPack => "", ResourceTypes.Map => "saves\\", _ => null };
                    ModLoaders allowed = file.ModLoaders != ModLoaders.None ? file.ModLoaders : Project.ModLoaders != ModLoaders.None ? Project.ModLoaders : ModLoaders.None;
                    Logger.Info(description + "要求的加载器种类：" + allowed);
                    if (file.ResourceType == ResourceTypes.Mod) {
                        defaultFolder = ChooseModDownloadFolder(DownloadTargetInstance, download.SelectedInstance(), download.CachedFolders.ContainsKey(ResourceTypes.Mod) ? download.CachedFolders[ResourceTypes.Mod] : null, instance => InstanceSuitable(file, instance, allowed));
                        if (defaultFolder is not null) DirectoryUtils.Create(defaultFolder);
                    }
                    if (defaultFolder is not null) Logger.Info("使用目标版本的 Mod 文件夹：" + defaultFolder);
                    else if (file.ResourceType != ResourceTypes.Map && download.CachedFolders.ContainsKey(file.ResourceType)) {
                        defaultFolder = download.CachedFolders[file.ResourceType]; Logger.Info("使用上次下载时的文件夹作为默认下载位置：" + defaultFolder);
                    } else if (download.SelectedInstance() is not null && InstanceSuitable(file, download.SelectedInstance()!, allowed)) {
                        defaultFolder = download.SelectedInstance()!.PathIndie + subFolder; DirectoryUtils.Create(defaultFolder); Logger.Info("使用当前版本作为默认下载位置：" + defaultFolder);
                    } else {
                        bool needLoad = !download.InstanceListFinished();
                        if (needLoad) { download.Hint("正在查找适合的游戏版本……", 0); download.LoadInstanceList(); }
                        IEnumerable<DirectoryInfo> suitable = download.Instances().Where(instance => InstanceSuitable(file, instance, allowed)).Select(instance => DirectoryUtils.GetInfo(instance.PathIndie + subFolder));
                        if (suitable.Any()) {
                            DirectoryInfo selected = suitable.OrderByDescending(folder => folder.Exists ? folder.LastWriteTimeUtc : DateTime.MinValue).ThenByDescending(folder => folder.Exists ? folder.GetFiles().Length : -1).First();
                            defaultFolder = PathUtils.RemoveExtendedPrefix(selected.FullName); DirectoryUtils.Create(defaultFolder); Logger.Info("使用适合的游戏版本作为默认下载位置：" + defaultFolder);
                        } else {
                            defaultFolder = download.MinecraftFolder(); if (file.ResourceType == ResourceTypes.Map) defaultFolder = Path.Combine(defaultFolder, "saves");
                            if (needLoad) download.Hint("当前 MC 文件夹中没有找到合适的版本！", 0); else Logger.Info("由于当前版本不兼容，使用当前的 MC 文件夹作为默认下载位置");
                        }
                    }
                }
                if (file.ResourceType == ResourceTypes.Map) { mapSaves = defaultFolder; defaultFolder = download.MapDownloadFolder(mapSaves!); }
                string? fileName;
                if (Same(Project.TranslatedName, Project.RawName)) fileName = file.FileName;
                else {
                    string chinese = SafeDisplay(Project.TranslatedName!.BeforeFirst(" (").BeforeFirst(" - "));
                    fileName = download.TranslateSetting() switch { 0 => "【" + chinese + "】" + file.FileName, 1 => "[" + chinese + "] " + file.FileName, 2 => chinese + "-" + file.FileName, 3 => file.FileName + "-" + chinese, _ => file.FileName };
                }
                if (file.ResourceType == ResourceTypes.Mod) fileName = fileName!.Replace("~", "-");
                download.RunInUi(() => {
                    string extension = file.ResourceType == ResourceTypes.Mod ? PathUtils.GetExtension(file.FileName!) == "litemod" ? "litemod" : "jar" : PathUtils.GetExtension(file.FileName!) == "mrpack" ? "mrpack" : "zip";
                    string? target = download.SaveFile(fileName, defaultFolder, extension, description + "文件");
                    if (target is null) return;
                    if (file.ResourceType == ResourceTypes.Mod && download.InstallMod is not null) {
                        download.CachedFolders[ResourceTypes.Mod] = PathUtils.RemoveLastPart(target);
                        download.InstallMod(Project,file,target,TargetResourceType == ResourceTypes.Any ? ResourceDownloadReason.Dependency : ResourceDownloadReason.Standalone,DownloadTargetInstance);
                        return;
                    }
                    string name = description + "下载：" + PathUtils.GetFileNameWithoutExtension(target) + " ";
                    if (file.ResourceType != ResourceTypes.Map && !PathUtils.IsSame(PathUtils.RemoveLastPart(target), defaultFolder)) download.CachedFolders[file.ResourceType] = PathUtils.RemoveLastPart(target);
                    List<TLoader> loaders = new() { download.Loaders.Download("下载文件", new() { download.NetFile(file, target, TargetResourceType == ResourceTypes.Any ? ResourceDownloadReason.Dependency : ResourceDownloadReason.Standalone, null, ModLoaders.None) }, new MinecraftDownloadLoaderSettings { ProgressWeight = 6, Block = true }) };
                    if (file.ResourceType == ResourceTypes.Map) loaders.Add(CreateMapInstallLoader(download, target, mapSaves!));
                    TLoader loader = download.Loaders.Combo<int>(name, loaders, new MinecraftDownloadLoaderSettings());
                    download.Loaders.OnStateChanged(loader, current => { download.StateHint(current); if (download.Loaders.State(current) == download.States.Finished && file.ResourceType == ResourceTypes.ModPack) download.Explorer(target); });
                    download.Loaders.Start(loader, 1); download.Loaders.AddTaskbar<int>(loader); download.RefreshDownload(); download.RibbleDownload();
                });
            } catch (Exception error) { Logger.Error(error, "保存资源文件失败"); }
        }, "Download Resource Detail Save");
    }
    public static TLoader CreateMapInstallLoader(ResourceDetailDownloadHost<TLoader, TNetFile, TVersion, TInstance> download, string target, string savesFolder) {
        TLoader loader = download.Loaders.NewTask<int, string>("解压安装地图存档", task => {
            try { task.Output = download.InstallMap(target, savesFolder, task.Cancellation()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new IOException("地图已下载，但自动解压安装失败，原 ZIP 已保留：" + target + "\r\n" + error.Message, error); }
        }, new MinecraftDownloadLoaderSettings { ProgressWeight = 2 });
        download.InterruptOnCancel(loader, false);
        download.Loaders.OnStateChanged(loader, current => {
            if (download.Loaders.State(current) != download.States.Finished) return;
            download.Hint("地图已解压安装：" + Path.GetFileName(download.Loaders.WrapTask<int, string>(loader).Output), 2); download.RefreshCurrentResources();
        });
        return loader;
    }
}
