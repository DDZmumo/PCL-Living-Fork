using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class ModpackInstallEngine<TLoader, TNetFile, TVersion, TProject, TIsolation, TOptiFine> {
    public TLoader InstallPackHMCL(string fileAddress, ZipArchive archive, string archiveBaseFolder) {
        JObject json;
        try { json = (JObject)archive.GetEntry(archiveBaseFolder + "modpack.json")!.Open().ReadString(Encoding.UTF8).DeserializeJson()!; }
        catch (Exception error) { throw new Exception("HMCL 整合包安装信息存在问题", error); }
        string instanceName = SelectName((string?)(json["name"] ?? new JValue("")))!;
        string versionFolder = Path.Combine(host.MinecraftFolder(), "versions", instanceName) + "\\";
        string installTemp = host.RequestTempFolder();
        List<TLoader> installLoaders = new() { host.TextIntTask("解压整合包文件", task => {
            ExtractModpackFiles(installTemp, fileAddress, task.Cancellation(), task.ProgressProvider(0.6));
            CopyOverrideDirectory(installTemp + archiveBaseFolder + "minecraft", versionFolder, null!);
        }, new MinecraftDownloadLoaderSettings { ProgressWeight = ExtractWeight(fileAddress), Block = false }) };
        if (json["gameVersion"] is null) throw new Exception("该 HMCL 整合包未提供游戏版本信息，无法安装！");
        List<TLoader> mergeLoaders = host.InstallGame(instanceName, versionFolder, new McComponents(json["gameVersion"]!.ToString(), new Dictionary<ModLoaders, string>()));
        return Start("HMCL 整合包安装：" + instanceName + " ", new List<TLoader> { PackCombo(installLoaders), GameCombo(mergeLoaders) }, versionFolder);
    }
    public TLoader InstallPackMCBBS(string fileAddress, ZipArchive archive, string archiveBaseFolder, string? instanceName = null) {
        JObject json;
        try {
            ZipArchiveEntry entry = (archive.GetEntry(archiveBaseFolder + "mcbbs.packmeta") ?? archive.GetEntry(archiveBaseFolder + "manifest.json"))!;
            json = (JObject)entry.Open().ReadString(Encoding.UTF8).DeserializeJson()!;
        } catch (Exception error) { throw new Exception("MCBBS 整合包安装信息存在问题", error); }
        if (instanceName is null) instanceName = SelectName((string?)(json["name"] ?? new JValue("")));
        string versionFolder = Path.Combine(host.MinecraftFolder(), "versions", instanceName!) + "\\";
        string installTemp = host.RequestTempFolder(), setupFile = versionFolder + "PCL\\Setup.ini";
        List<TLoader> installLoaders = new() { host.TextIntTask("解压整合包文件", task => {
            ExtractModpackFiles(installTemp, fileAddress, task.Cancellation(), task.ProgressProvider(0.6));
            CopyOverrideDirectory(installTemp + archiveBaseFolder + "overrides", versionFolder, null!);
            if (json["launchInfo"] is not null) {
                JObject launchInfo = (JObject)json["launchInfo"]!;
                if (launchInfo.ContainsKey("javaArgument")) host.WriteIni(setupFile, "VersionAdvanceJvm", string.Join(" ", launchInfo["javaArgument"]!));
                if (launchInfo.ContainsKey("launchArgument")) host.WriteIni(setupFile, "VersionAdvanceGame", string.Join(" ", launchInfo["launchArgument"]!));
            }
        }, new MinecraftDownloadLoaderSettings { ProgressWeight = ExtractWeight(fileAddress), Block = false }) };
        if (json["addons"] is null) throw new Exception("该 MCBBS 整合包未提供游戏版本附加信息，无法安装！");
        Dictionary<string, string> addons = new();
        foreach (JToken entry in json["addons"]!) addons.Add((string)entry["id"]!, (string)entry["version"]!);
        if (!addons.ContainsKey("game")) throw new Exception("该 MCBBS 整合包未提供游戏版本信息，无法安装！");
        if (addons.ContainsKey("quilt")) { host.Hint("PCL 不支持安装 Quilt 整合包！", true); throw new OperationCanceledException(); }
        Dictionary<ModLoaders, string> versions = new();
        if (addons.ContainsKey("forge")) versions[ModLoaders.Forge] = addons["forge"];
        if (addons.ContainsKey("neoforge")) versions[ModLoaders.NeoForge] = addons["neoforge"];
        if (addons.ContainsKey("fabric")) versions[ModLoaders.Fabric] = addons["fabric"];
        JObject? optiFineEntry = null;
        if (addons.ContainsKey("optifine")) {
            string minecraftVersion = addons["game"];
            if (minecraftVersion.EndsWithF(".0")) minecraftVersion = minecraftVersion.Substring(0, minecraftVersion.Length - 2);
            string suffix = "_HD_" + addons["optifine"].AfterLast("HD_") + ".jar";
            if (!host.OptiFineFinished()) host.WaitOptiFine();
            TOptiFine? entry = host.OptiFineEntries().FirstOrDefault(value => Same(value.Inherit, minecraftVersion) && value.FileName.EndsWithF(suffix));
            if (entry is null) throw new Exception("OptiFine 列表中没有整合包所需的 OptiFine " + addons["optifine"] + "（Minecraft " + addons["game"] + "）");
            optiFineEntry = JObject.FromObject(entry);
        }
        List<TLoader> mergeLoaders = host.InstallGame(instanceName!, versionFolder, new McComponents(addons["game"], versions, optiFineEntry));
        return Start("MCBBS 整合包安装：" + instanceName + " ", new List<TLoader> { PackCombo(installLoaders), GameCombo(mergeLoaders) }, versionFolder);
    }
    public TLoader InstallPackLauncherPack(string fileAddress, ZipArchive archive, string archiveBaseFolder) {
        Message("接下来请选择一个空文件夹，它会被安装到这个文件夹里。", "安装", "继续", wait: true);
        string? targetFolder = host.SelectInstallFolder();
        if (targetFolder is null) throw new OperationCanceledException();
        if (!DirectoryUtils.IsEmpty(targetFolder)) { host.Hint("请选择一个空文件夹作为安装目标！", true); throw new OperationCanceledException(); }
        List<TLoader> loaders = new() { host.TextIntTask("解压压缩包", task => {
            ExtractModpackFiles(targetFolder, fileAddress, task.Cancellation(), task.ProgressProvider(0.9));
            host.OpenExplorer(targetFolder); host.Sleep(400);
            string? launcher = null;
            foreach (string exeFile in DirectoryUtils.EnumerateFiles(targetFolder, searchPattern: "*.exe")) {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exeFile);
                Logger.Info("文件 " + exeFile + " 的产品名标识为 " + info.ProductName);
                if (Same(info.ProductName, "Plain Craft Launcher")) { launcher = exeFile; Logger.Info("发现整合包附带的 PCL 启动器：" + exeFile); }
                else if ((info.ProductName!.ContainsIgnoreCase("Launcher") || info.ProductName!.Contains("启动")) && !Same(info.ProductName, "Plain Craft Launcher Admin Manager") && launcher is null) {
                    launcher = exeFile; Logger.Info("发现整合包附带的疑似第三方启动器：" + exeFile);
                }
            }
            task.SetProgress(0.95);
            if (launcher is not null) {
                Logger.Info("找到压缩包中附带的启动器：" + launcher);
                if (Message("整合包里似乎自带了启动器，是否换用它继续安装？\r\n即将打开：" + launcher, "换用整合包启动器？", "换用", "不换用") == 1) {
                    host.StartProcess(launcher, "--wait"); Logger.Info("为换用整合包中的启动器启动，强制结束程序"); host.EndProgram(false); return;
                }
            } else Logger.Info("未找到压缩包中附带的启动器");
            string instanceName = PathUtils.GetLastPart(targetFolder);
            DirectoryUtils.Create(targetFolder + ".minecraft\\");
            host.AddFolder(targetFolder + ".minecraft\\" + archiveBaseFolder.Replace("/", "\\").TrimStart('\\'), instanceName, false);
            string modpackFile = DirectoryUtils.EnumerateFiles(targetFolder, true, "modpack.*").First();
            Logger.Info("调用 modpack 文件继续安装：" + modpackFile); ModpackInstall(modpackFile);
        }, new MinecraftDownloadLoaderSettings()) };
        return Start("解压压缩包", loaders, targetFolder, installState: false, checkDuplicate: false, ribble: true);
    }
    public TLoader InstallPackCompress(string fileAddress, ZipArchive archive) {
        Match? match = null;
        Regex regex = new(@"^.*\/(?=versions\/(?<ver>[^\/]+)\/(\k<ver>)\.json$)", RegexOptions.IgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries) { Match candidate = regex.Match("/" + entry.FullName); if (candidate.Success) { match = candidate; break; } }
        if (match is null) throw new Exception("文件结构不匹配，这可能不是 Minecraft 客户端压缩包？");
        string archiveBaseFolder = match.Value.Replace("/", "\\").TrimStart('\\');
        Logger.Info("检测到压缩包的 .minecraft 根目录：" + archiveBaseFolder + "，命中的版本名：" + match.Groups[1].Value);
        Message("接下来请选择一个空文件夹，它会被安装到这个文件夹里。", "安装", "继续", wait: true);
        string? targetFolder = host.SelectInstallFolder();
        if (targetFolder is null) throw new OperationCanceledException();
        if (targetFolder.Contains("!") || targetFolder.Contains(";")) { host.Hint("Minecraft 文件夹路径中不能含有感叹号或分号！", true); throw new OperationCanceledException(); }
        if (!DirectoryUtils.IsEmpty(targetFolder)) { host.Hint("请选择一个空文件夹作为安装目标！", true); throw new OperationCanceledException(); }
        List<TLoader> loaders = new() { host.TextIntTask("解压压缩包", task => {
            ExtractModpackFiles(targetFolder, fileAddress, task.Cancellation(), task.ProgressProvider(0.95));
            host.AddFolder(targetFolder + archiveBaseFolder, PathUtils.GetLastPart(targetFolder), false);
            host.Sleep(400); host.RunInUi(host.SelectInstancePage);
        }, new MinecraftDownloadLoaderSettings()) };
        return Start("解压压缩包", loaders, targetFolder, checkDuplicate: false, ribble: true);
    }
}
