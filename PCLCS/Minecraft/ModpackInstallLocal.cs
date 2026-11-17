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
    public TLoader InstallPackMMC(string fileAddress, ZipArchive archive, string archiveBaseFolder) {
        if (host.InstallManagedMmc is not null) return host.InstallManagedMmc(new(fileAddress, host.MinecraftFolder()));
        JObject packJson; string packInstance;
        try {
            packJson = (JObject)archive.GetEntry(archiveBaseFolder + "mmc-pack.json")!.Open().ReadString(Encoding.UTF8).DeserializeJson()!;
            packInstance = archive.GetEntry(archiveBaseFolder + "instance.cfg")!.Open().ReadString(Encoding.UTF8);
        } catch (Exception error) { throw new Exception("MMC 整合包安装信息存在问题", error); }
        string instanceName = SelectName(packInstance.RegexSeek(@"(?<=\nname\=)[^\n]+") ?? "")!;
        string versionFolder = Path.Combine(host.MinecraftFolder(), "versions", instanceName) + "\\";
        string installTemp = host.RequestTempFolder(), setupFile = versionFolder + "PCL\\Setup.ini";
        List<TLoader> installLoaders = new() { host.TextIntTask("解压整合包文件", task => {
            ExtractModpackFiles(installTemp, fileAddress, task.Cancellation(), task.ProgressProvider(0.55));
            // Existing LF Prism behavior only: minecraft/ wins when both roots exist.
            string root = DirectoryUtils.Exists(installTemp + archiveBaseFolder + ".minecraft") && !DirectoryUtils.Exists(installTemp + archiveBaseFolder + "minecraft")
                ? installTemp + archiveBaseFolder + ".minecraft" : installTemp + archiveBaseFolder + "minecraft";
            CopyOverrideDirectory(root, versionFolder, null!);
            try {
                string mmcSetupFile = installTemp + archiveBaseFolder + "instance.cfg";
                if (FileUtils.Exists(mmcSetupFile)) {
                    List<string> lines = new();
                    foreach (string line in FileUtils.ReadAsLines(mmcSetupFile, true)) {
                        if (!line.Contains("=")) continue;
                        lines.Add(line.BeforeFirst("=") + ":" + line.AfterFirst("="));
                    }
                    FileUtils.Write(mmcSetupFile, string.Join("\r\n", lines));
                    if (host.ReadIniBoolean(mmcSetupFile, "OverrideCommands", false)) {
                        string? command = host.ReadIni(mmcSetupFile, "PreLaunchCommand", "");
                        if (!Same(command, "")) {
                            command = command!.Replace("\\\"", "\"").Replace("$INST_JAVA", "{java}java.exe")
                                .Replace("$INST_MC_DIR\\", "{minecraft}").Replace("$INST_MC_DIR", "{minecraft}")
                                .Replace("$INST_DIR\\", "{verpath}").Replace("$INST_DIR", "{verpath}").Replace("$INST_ID", "{name}").Replace("$INST_NAME", "{name}");
                            host.WriteIni(setupFile, "VersionAdvanceRun", command);
                            Logger.Info("迁移 MultiMC 版本独立设置：启动前执行命令：" + command);
                        }
                    }
                    if (host.ReadIniBoolean(mmcSetupFile, "JoinServerOnLaunch", false)) {
                        string server = host.ReadIni(mmcSetupFile, "JoinServerOnLaunchAddress", "")!.Replace("\\\"", "\"");
                        host.WriteIni(setupFile, "VersionServerEnter", server); Logger.Info("迁移 MultiMC 版本独立设置：自动进入服务器：" + server);
                    }
                    string? logo = host.ReadIni(mmcSetupFile, "iconKey", "");
                    if (!Same(logo, "") && FileUtils.Exists(installTemp + archiveBaseFolder + logo + ".png")) {
                        host.WriteIni(setupFile, "LogoCustom", true); host.WriteIni(setupFile, "Logo", "PCL\\Logo.png");
                        FileUtils.Copy(installTemp + archiveBaseFolder + logo + ".png", versionFolder + "PCL\\Logo.png");
                        Logger.Info("迁移 MultiMC 版本独立设置：版本图标（" + logo + ".png）");
                    }
                    string? jvmArgs = host.ReadIni(mmcSetupFile, "JvmArgs", "");
                    if (!Same(jvmArgs, "")) {
                        if (host.ReadIniBoolean(mmcSetupFile, "OverrideJavaArgs", false)) {
                            host.WriteIni(setupFile, "VersionAdvanceJvm", jvmArgs!); Logger.Info("迁移 MultiMC 版本独立设置：JVM 参数（覆盖）：" + jvmArgs);
                        } else {
                            jvmArgs += " " + host.SettingString("LaunchAdvanceJvm");
                            host.WriteIni(setupFile, "VersionAdvanceJvm", jvmArgs); Logger.Info("迁移 MultiMC 版本独立设置：JVM 参数（追加）：" + jvmArgs);
                        }
                    }
                }
            } catch (Exception error) { Logger.Warn(error, "读取 MMC 配置文件失败（" + installTemp + archiveBaseFolder + "instance.cfg）"); }
        }, new MinecraftDownloadLoaderSettings { ProgressWeight = ExtractWeight(fileAddress), Block = false }) };
        if (packJson["components"] is null) throw new Exception("该 MMC 整合包未提供游戏版本信息，无法安装！");
        string? minecraftVersion = null;
        Dictionary<ModLoaders, string> versions = new();
        foreach (JObject component in packJson["components"]!) {
            if (!component.ContainsKey("uid")) continue;
            string uid = component["uid"]!.ToString();
            switch (uid) {
                case "net.minecraft": minecraftVersion = (string?)component["version"]; break;
                case "net.minecraftforge": versions[ModLoaders.Forge] = (string)component["version"]!; break;
                case "net.neoforged": versions[ModLoaders.NeoForge] = (string)component["version"]!; break;
                case "net.fabricmc.fabric-loader": versions[ModLoaders.Fabric] = (string)component["version"]!; break;
                default: if (uid.StartsWithF("org.lwjgl")) Logger.Info("已跳过 LWJGL 项：" + uid); else NotifyIncompatibleLoader(uid); break;
            }
        }
        List<TLoader> mergeLoaders = host.InstallGame(instanceName, versionFolder, new McComponents(minecraftVersion!, versions));
        return Start("MMC 整合包安装：" + instanceName + " ", new List<TLoader> { PackCombo(installLoaders), GameCombo(mergeLoaders) }, versionFolder);
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
