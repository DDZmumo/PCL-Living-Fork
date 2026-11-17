using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF ModModpack.vb (f4aa0fae62c3ee8af24c81e61844c8fdd13c8983), LF LICENCE.
// Fixed PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 differs by LF pack-mode,
// update-baseline and exported-isolation behavior, which is retained here.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5
// Plain Craft Launcher 2/Modules/Minecraft/ModModpack.cs was reviewed selectively
// (CE Plain Craft Launcher 2/LICENCE; LF LICENCE). Its restricted-feature policy, localized
// text, new loaders and differing download/extraction policy were not imported.
public sealed partial class ModpackInstallEngine<TLoader, TNetFile, TVersion, TProject, TIsolation, TOptiFine>
    where TLoader : class where TNetFile : class where TVersion : ResourceVersionModelData
    where TProject : ResourceProjectModelData where TIsolation : InstanceCloneExportChoiceData
    where TOptiFine : MinecraftOptiFineEntryData {
    private readonly ModpackInstallHost<TLoader, TNetFile, TVersion, TProject, TIsolation, TOptiFine> host;
    public ModpackInstallEngine(ModpackInstallHost<TLoader, TNetFile, TVersion, TProject, TIsolation, TOptiFine> host) { this.host = host; }
    private static bool Same(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    private static IEnumerable<JToken> Values(JToken? value) => value ?? (IEnumerable<JToken>)Array.Empty<JToken>();
    private int Message(string text, string title = "提示", string button1 = "确定", string button2 = "", bool warn = false, bool wait = false) =>
        host.Message(new ModpackMessage { Text = text, Title = title, Button1 = button1, Button2 = button2, IsWarn = warn, ForceWait = wait });
    private string? SelectName(string? candidate) {
        string parent = host.MinecraftFolder() + "versions";
        ModpackNameValidator validator = host.NameValidator(parent);
        if (!Same(validator.Validate(candidate), "")) candidate = "";
        if (Same(candidate, "")) candidate = validator.Prompt();
        if (string.IsNullOrEmpty(candidate)) throw new OperationCanceledException();
        return candidate;
    }
    public void ModpackInstall() {
        if (host.PackMode()) return;
        string? file = host.SelectPackFile();
        if (string.IsNullOrEmpty(file)) return;
        host.RunInThread(() => {
            try { ModpackInstall(file!); }
            catch (Exception error) { error.ThrowIfCanceled(); host.Alert(error, "手动安装整合包失败"); }
        });
    }
    public TLoader ModpackInstall(string file, string? instanceName = null, string? logo = null, JObject? source = null) {
        if (host.PackMode() && !PathUtils.IsSame(file, host.BaseFolder() + "modpack.mrpack") && !PathUtils.IsSame(file, host.BaseFolder() + "modpack.zip")) {
            host.Hint("整合包模式下不支持导入其他整合包。请关闭整合包模式后再导入。", false);
            throw new OperationCanceledException();
        }
        Logger.Info("整合包安装请求：" + (file ?? "null"));
        ZipArchive? archive = null;
        string archiveBaseFolder = "";
        try {
            string targetFolder = host.MinecraftFolder() + "versions\\" + instanceName + "\\";
            if (targetFolder.Contains("!") || targetFolder.Contains(";")) { host.Hint("游戏路径中不能含有感叹号或分号：" + targetFolder, true); throw new OperationCanceledException(); }
            int packType = -1;
            try {
                if (host.InstallManagedMmc is not null && MmcBoundedArchiveClassifier.IsMmcCandidate(file!)) {
                    Logger.Info("整合包种类：MMC（完整组件与实例后端）");
                    return host.InstallManagedMmc(new(file!, host.MinecraftFolder(), instanceName, logo, source));
                }
                archive = FileUtils.OpenZip(file!);
                // Preserve LF's root-before-one-directory scan and exact marker priorities.
                if (archive.GetEntry("mcbbs.packmeta") is not null) packType = 3;
                else if (archive.GetEntry("mmc-pack.json") is not null) packType = 2;
                else if (archive.GetEntry("modrinth.index.json") is not null) packType = 4;
                else if (archive.GetEntry("manifest.json") is ZipArchiveEntry manifest) {
                    JObject json = (JObject)manifest.Open().ReadString(Encoding.UTF8).DeserializeJson()!;
                    packType = json["addons"] is null ? 0 : 3;
                } else if (archive.GetEntry("modpack.json") is not null) packType = 1;
                else if (archive.GetEntry("modpack.zip") is not null || archive.GetEntry("modpack.mrpack") is not null) packType = 9;
                else foreach (ZipArchiveEntry entry in archive.Entries) {
                    string[] parts = entry.FullName.Split('/');
                    archiveBaseFolder = parts[0] + "/";
                    if (parts.Length != 2) continue;
                    if (parts[1] == "mcbbs.packmeta") { packType = 3; break; }
                    if (parts[1] == "mmc-pack.json") { packType = 2; break; }
                    if (parts[1] == "modrinth.index.json") { packType = 4; break; }
                    if (parts[1] == "manifest.json") {
                        JObject json = (JObject)entry.Open().ReadString(Encoding.UTF8).DeserializeJson()!;
                        if (json["addons"] is null) packType = 0;
                        else { packType = 3; archiveBaseFolder = "overrides/"; }
                        break;
                    }
                    if (parts[1] == "modpack.json") { packType = 1; break; }
                    if (parts[1] == "modpack.zip" || parts[1] == "modpack.mrpack") { packType = 9; break; }
                }
            } catch (Exception error) {
                if (error.GetDisplay(true).Contains("Error.WinIOError")) throw new Exception("打开整合包文件失败", error);
                if (PathUtils.GetExtension(file!) == "rar") throw new Exception("PCL 无法处理 rar 格式的压缩包，请在解压后重新压缩为 zip 格式再试", error);
                throw new Exception("打开整合包文件失败，文件可能损坏或为不支持的压缩包格式", error);
            }
            switch (packType) {
                case 0: Logger.Info("整合包种类：CurseForge"); return InstallPackCurseForge(file!, archive, archiveBaseFolder, instanceName, logo, source);
                case 1: Logger.Info("整合包种类：HMCL"); return InstallPackHMCL(file!, archive, archiveBaseFolder);
                case 2: Logger.Info("整合包种类：MMC"); return InstallPackMMC(file!, archive, archiveBaseFolder);
                case 3: Logger.Info("整合包种类：MCBBS"); return InstallPackMCBBS(file!, archive, archiveBaseFolder, instanceName);
                case 4: Logger.Info("整合包种类：Modrinth"); return InstallPackModrinth(file!, archive, archiveBaseFolder, instanceName, logo, source);
                case 9: Logger.Info("整合包种类：带启动器的压缩包"); return InstallPackLauncherPack(file!, archive, archiveBaseFolder);
                default: Logger.Info("整合包种类：未能识别，假定为压缩包"); return InstallPackCompress(file!, archive);
            }
        } finally { archive?.Dispose(); }
    }
    public void ExtractModpackFiles(string installTemp, string fileAddress, CancellationToken cancellation = default, ProgressProvider? progress = null) {
        DirectoryUtils.Delete(installTemp);
        cancellation.ThrowIfCancellationRequested();
        FileUtils.ExtractToDirectory(fileAddress, installTemp, cancellation, progress);
    }
    public void CopyOverrideDirectory(string overridesFolder, string versionFolder, TLoader loader, TIsolation? isolation = null, string? gameFolder = null) {
        overridesFolder = PathUtils.AddSlashSuffix(overridesFolder); versionFolder = PathUtils.AddSlashSuffix(versionFolder);
        if (isolation is not null) { host.CopyIsolatedOverrides(overridesFolder, versionFolder, gameFolder ?? versionFolder, isolation); return; }
        if (DirectoryUtils.Exists(overridesFolder)) {
            Logger.Info("处理整合包覆写文件夹：" + overridesFolder + " → " + versionFolder); DirectoryUtils.Copy(overridesFolder, versionFolder);
        } else Logger.Info("整合包中没有覆写文件夹：" + overridesFolder);
        string overridesIni = overridesFolder + "PCL\\Setup.ini", versionIni = versionFolder + "PCL\\Setup.ini";
        if (FileUtils.Exists(overridesIni)) {
            host.WriteIni(overridesIni, "VersionArgumentIndie", 1); host.WriteIni(overridesIni, "VersionArgumentIndieV2", true); host.WriteIni(overridesIni, "IsStar", false);
            FileUtils.Copy(overridesIni, versionIni);
        } else { host.WriteIni(versionIni, "VersionArgumentIndie", 1); host.WriteIni(versionIni, "VersionArgumentIndieV2", true); }
        host.ClearIniCache(versionIni);
    }
    public void NotifyIncompatibleLoader(string loaderName) {
        if (Message("整合包中存在不兼容的加载器：" + loaderName + "\r\n如果你知道如何手动安装它，可以先选择跳过，然后在整合包下载结束后手动安装。",
            "不兼容的加载器", "取消", "不安装 " + loaderName + " 并继续") == 1) throw new OperationCanceledException();
    }
    private double ExtractWeight(string file) => FileUtils.GetInfo(file).Length / 1024.0 / 1024.0 / 6.0;
    private TLoader PackCombo(List<TLoader> installers, bool block = false) => host.TextCombo("整合包安装", installers,
        new MinecraftDownloadLoaderSettings { Show = false, Block = block, ProgressWeight = installers.Sum(host.Weight) });
    private TLoader GameCombo(List<TLoader> installers) => host.TextCombo("游戏安装", installers,
        new MinecraftDownloadLoaderSettings { Show = false, ProgressWeight = installers.Sum(host.Weight) });
    private TLoader Start(string name, List<TLoader> loaders, string folder, bool installState = true, bool checkDuplicate = true, bool ribble = false) {
        if (checkDuplicate && host.TaskbarNames().Any(value => Same(value, name))) { host.Hint("该整合包正在安装中！", true); throw new OperationCanceledException(); }
        TLoader loader = host.TextCombo(name, loaders, new MinecraftDownloadLoaderSettings());
        if (installState) host.AttachInstallState(loader);
        host.Start(loader, folder); host.AddTaskbar(loader); host.RefreshDownload();
        if (ribble) host.RibbleDownload();
        return loader;
    }
    private void SetLogo(string? logo, string versionFolder) {
        if (logo is null || !FileUtils.Exists(logo)) return;
        FileUtils.Copy(logo, versionFolder + "PCL\\Logo.png");
        host.WriteIni(versionFolder + "PCL\\Setup.ini", "Logo", "PCL\\Logo.png"); host.WriteIni(versionFolder + "PCL\\Setup.ini", "LogoCustom", "True");
        Logger.Info("已设置整合包 Logo：" + logo);
    }
    private void DeleteOriginalPacks(string fileAddress, string versionFolder) {
        foreach (string file in new[] { versionFolder + "原始整合包.zip", versionFolder + "原始整合包.mrpack" }) if (FileUtils.Exists(file)) { Logger.Info("删除原始整合包文件：" + file); FileUtils.Delete(file); }
        if (FileUtils.Exists(fileAddress) && PathUtils.GetFileNameWithoutExtension(fileAddress) == "modpack") { Logger.Info("删除安装整合包文件：" + fileAddress); FileUtils.Delete(fileAddress); }
    }
}
