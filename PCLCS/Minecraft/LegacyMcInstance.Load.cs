using System;
using System.IO;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public partial class LegacyMcInstance<TSelf, TState, TCard>
    where TSelf : LegacyMcInstance<TSelf, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum {
    private string? Ini(string key, string? defaultValue = "") => host.ReadIni(PathVersion + "PCL\\Setup.ini", key, defaultValue);
    private void WriteIni(string key, string? value) => host.WriteIni(PathVersion + "PCL\\Setup.ini", key, value);

    public bool Check() {
        MmcInstanceInspection managed = MmcInstanceInspector.Read(PathVersion);
        UsesMmcBackend = !managed.IsLegacy;
        if (!managed.IsLegacy || !managed.Management.ObservationComplete) return ApplyManagedView(managed);
        if (!DirectoryUtils.Exists(PathVersion)) {
            StateCode = 0;
            Info = "未找到版本 " + Name;
            return false;
        }
        try {
            if (JsonObject["mainClass"] == null) throw new Exception("版本 JSON 中没有 mainClass 项！");
        } catch (Exception ex) {
            Logger.Warn(ex, $"版本 JSON 可用性检查失败（{PathVersion}）");
            JsonText = "";
            JsonObject = null!;
            Info = ex.Message;
            StateCode = 0;
            return false;
        }
        try {
            if (string.IsNullOrEmpty(Version.VanillaName)) throw new Exception("无法获取版本号，结果为空");
        } catch (Exception ex) {
            Logger.Warn(ex, $"版本号获取失败（{Name}）");
            StateCode = 0;
            Info = "版本号获取失败：" + ex.GetDisplay(false);
            return false;
        }
        try {
            if (!string.IsNullOrEmpty(InheritName)) {
                if (!FileUtils.Exists($"{PathUtils.RemoveLastPart(PathVersion)}\\{InheritName}\\{InheritName}.json")) {
                    StateCode = 0;
                    Info = "需要安装 " + InheritName + " 作为前置版本";
                    return false;
                }
            }
        } catch (Exception ex) {
            Logger.Warn(ex, $"依赖版本检查出错（{Name}）");
            StateCode = 0;
            Info = "未知错误：" + ex.GetDisplay(false);
            return false;
        }
        StateCode = 1;
        return true;
    }

    public TSelf Load() {
        try {
            MmcInstanceInspection managed = MmcInstanceInspector.Read(PathVersion);
            UsesMmcBackend = !managed.IsLegacy;
            if (!managed.IsLegacy || !managed.Management.ObservationComplete) {
                ApplyManagedView(managed);
                return Self;
            }
            if (Check()) {
                switch (Version.VanillaName) {
                    case "Unknown": StateCode = 0; break;
                    case "Old": StateCode = 5; break;
                    default:
                        if (Version.IsFool) StateCode = 3;
                        else if (Version.IsSnapshot) StateCode = 2;
                        if (Version.HasOptiFine) StateCode = 4;
                        if (Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)) StateCode = 8;
                        if (Version.ModLoaders.HasFlagF(ModLoaders.Fabric)) StateCode = 9;
                        else if (Version.ModLoaders.HasFlagF(ModLoaders.Forge)) StateCode = 6;
                        else if (Version.ModLoaders.HasFlagF(ModLoaders.NeoForge)) StateCode = 7;
                        break;
                }
            }
            Logo = host.ReadVersionLogo(PathVersion + "PCL\\Setup.ini", "");
            if (string.IsNullOrEmpty(Logo) || !Conversions.ToBoolean(Ini("LogoCustom", Conversions.ToString(false)))) {
                Logo = host.ImagePath() + (StateCode switch {
                    1 => "Blocks/Grass.png",
                    2 => "Blocks/CommandBlock.png",
                    5 => "Blocks/CobbleStone.png",
                    6 => "Blocks/Anvil.png",
                    7 => "Blocks/NeoForge.png",
                    9 => "Blocks/Fabric.png",
                    4 => "Blocks/GrassPath.png",
                    8 => "Blocks/Egg.png",
                    3 => "Blocks/GoldBlock.png",
                    _ => "Blocks/RedstoneBlock.png"
                });
            }
            RefreshInfo();
            IsStar = Conversions.ToBoolean(Ini("IsStar", Conversions.ToString(false)));
            CardCode = Conversions.ToInteger(Ini("DisplayType", "0"));
            if (DirectoryUtils.Exists(PathVersion)) {
                WriteIni("State", Conversions.ToString(StateCode));
                WriteIni("Info", Info);
                host.WriteVersionLogo(PathVersion + "PCL\\Setup.ini", LogoForStorage);
            }
            if (StateCode != 0 && Version.Reliable) {
                WriteIni("ReleaseTime", ReleaseTime.ToString("yyyy'-'MM'-'dd HH':'mm"));
                WriteIni("VersionFabric", Version.ModLoaders.HasFlagF(ModLoaders.Fabric) ? Version.ModLoaderVersions[ModLoaders.Fabric] : null);
                WriteIni("VersionOptiFine", Version.OptiFineAsMod ? null : Version.OptiFine);
                WriteIni("VersionLiteLoader", Conversions.ToString(Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)));
                WriteIni("VersionForge", Version.ModLoaders.HasFlagF(ModLoaders.Forge) ? Version.ModLoaderVersions[ModLoaders.Forge] : null);
                WriteIni("VersionNeoForge", Version.ModLoaders.HasFlagF(ModLoaders.NeoForge) ? Version.ModLoaderVersions[ModLoaders.NeoForge] : null);
                WriteIni("VersionVanillaName", Version.VanillaName);
                WriteIni("VersionVanilla", Version.Vanilla.ToString());
            }
        } catch (Exception ex) {
            Info = ex.GetDisplay(false);
            Logo = host.ImagePath() + "Blocks/RedstoneBlock.png";
            StateCode = 0;
            Logger.Warn(ex, $"加载版本失败（{Name}）");
        } finally {
            IsLoaded = true;
        }
        return Self;
    }
    public bool IsLoaded;

    public void RefreshInfo() {
        string? customInfo = Ini("CustomInfo");
        if (!string.IsNullOrEmpty(customInfo)) {
            Info = customInfo!;
        } else {
            Info = VersionDisplayName();
            if (StateCode == 3) Info = LegacyMinecraftText.GetMcFoolName(Version.VanillaName);
            else if (StateCode != 0) {
                if (host.ReadInteger("VersionServerLogin", Self) == 3) Info += ", 统一通行证验证";
                if (host.ReadInteger("VersionServerLogin", Self) == 4) Info += ", Authlib 验证";
            }
        }
    }

    public string VersionDisplayName() {
        string modLoaderInfo = "";
        if (Version.ModLoaders.HasFlagF(ModLoaders.Forge)) modLoaderInfo += ", Forge" + (Version.ModLoaderVersions[ModLoaders.Forge] == "未知版本" ? "" : " " + Version.ModLoaderVersions[ModLoaders.Forge]);
        if (Version.ModLoaders.HasFlagF(ModLoaders.NeoForge)) modLoaderInfo += ", NeoForge" + (Version.ModLoaderVersions[ModLoaders.NeoForge] == "未知版本" ? "" : " " + Version.ModLoaderVersions[ModLoaders.NeoForge]);
        if (Version.ModLoaders.HasFlagF(ModLoaders.Fabric)) modLoaderInfo += ", Fabric" + (Version.ModLoaderVersions[ModLoaders.Fabric] == "未知版本" ? "" : " " + Version.ModLoaderVersions[ModLoaders.Fabric]);
        if (Version.HasOptiFine) modLoaderInfo += ", OptiFine" + (Version.OptiFine == "未知版本" ? "" : " " + Version.OptiFine!.Replace("-", " ").Replace("_", " "));
        if (Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)) modLoaderInfo += ", LiteLoader";
        string info;
        switch (StateCode) {
            case 2: case 1: case 6: case 7: case 9: case 4: case 8:
                if (Version.VanillaName.ContainsIgnoreCase("pre")) info = "预发布版 " + Version.VanillaName;
                else if (Version.VanillaName.ContainsIgnoreCase("rc")) info = "发布候选 " + Version.VanillaName;
                else if (Version.VanillaName.ContainsIgnoreCase("experimental")) info = "实验性快照" + Version.VanillaName;
                else if (Version.VanillaName == "pending") info = "实验性快照";
                else if (Version.IsSnapshot) info = Version.Reliable ? "快照版 " + Version.VanillaName.Replace("-snapshot", "") : "快照版";
                else info = Version.Reliable ? "正式版 " + Version.VanillaName : "正式版";
                break;
            case 5: info = "远古版本"; break;
            case 3: info = "愚人节版本 " + Version.VanillaName; break;
            case 0: return Info;
            default: return "发生了未知错误，请向作者反馈此问题";
        }
        return (info + modLoaderInfo).Replace("_", "-");
    }

    public TSelf Rename(string newName) {
        MmcInstanceInspector.AssertLegacyOperationAllowed(PathVersion, "重命名");
        string oldName = Name;
        string oldPath = PathVersion;
        string newPath = PathUtils.AddSlashSuffix(Path.Combine(PathUtils.RemoveLastPart(oldPath), newName));
        JObject renameJson;
        string oldJsonPath = GetJsonPath();
        try {
            renameJson = (JObject)FileUtils.ReadAsJson(oldJsonPath)!;
        } catch (Exception ex) {
            Logger.Warn(ex, "在重命名读取 JSON 时失败");
            renameJson = JsonObject;
        }
        Config.Save();
        ResetSettingsCache();
        DirectoryUtils.Move(oldPath, newPath);
        if (DirectoryUtils.Exists($"{newPath}{oldName}-natives")) DirectoryUtils.Move($"{newPath}{oldName}-natives", $"{newPath}{newName}-natives");
        if (FileUtils.Exists($"{newPath}{oldName}.jar")) FileUtils.Move($"{newPath}{oldName}.jar", $"{newPath}{newName}.jar");
        if (FileUtils.Exists(newPath + "PCL\\Setup.ini"))
            FileUtils.Write(newPath + "PCL\\Setup.ini", FileUtils.ReadAsString(newPath + "PCL\\Setup.ini").Replace(oldPath, newPath));
        if (Operators.CompareString(host.ReadIni(host.SelectedFolder() + "PCL.ini", "Version", ""), oldName, false) == 0)
            host.WriteIni(host.SelectedFolder() + "PCL.ini", "Version", newName);
        try {
            renameJson["id"] = newName;
            FileUtils.Delete(newPath + PathUtils.GetLastPart(oldJsonPath));
            FileUtils.Write(newPath + newName + ".json", renameJson.ToString());
        } catch (Exception ex) {
            Logger.Warn(ex, "重命名版本 json 失败");
        }
        return host.CreateInstance(newPath).Load();
    }
}
