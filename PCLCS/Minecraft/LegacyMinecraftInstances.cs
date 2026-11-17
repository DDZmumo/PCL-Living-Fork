using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

public sealed partial class LegacyMinecraftService<TInstance, TFolder, TLibrary, TNetFile, TState, TCard, TFolderKind>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TFolder : LegacyMinecraftFolder<TFolderKind>, new() where TLibrary : LegacyMinecraftLibrary, new()
    where TNetFile : class where TState : struct, Enum where TCard : struct, Enum where TFolderKind : struct, Enum {
    public void InitMcInstanceList(string pathMc, Func<bool> isCanceled) {
        try {
            host.StoreInstances(new Dictionary<TCard, List<TInstance>>());
            List<string> folderList = [];
            if (DirectoryUtils.Exists(pathMc + "versions")) {
                try {
                    foreach (DirectoryInfo folder in LegacyMinecraftDirectoryScanner.GetDirectories(pathMc + "versions")) folderList.Add(folder.Name);
                } catch (Exception ex) {
                    throw new Exception("无法读取版本文件夹，可能是由于没有权限（" + pathMc + "versions）", ex);
                }
            }
            if (!folderList.Any()) {
                WriteIni(pathMc + "PCL.ini", "InstanceCache", "");
            } else {
                int folderListCheck = (int)((Versions.McInstanceCacheVersion + "#" + folderList.Join('#')).GetStableHashCode() % (int.MaxValue - 1));
                Dictionary<TCard, List<TInstance>>? result = null;
                if (!host.ForceRefresh() && Conversion.Val(ReadIni(pathMc + "PCL.ini", "InstanceCache")) == folderListCheck)
                    result = InitMcInstanceListWithCache(pathMc);
                if (result != null) {
                    host.StoreInstances(result);
                } else {
                    host.StoreForceRefresh(false);
                    Logger.Info("文件夹列表变更，重载所有版本");
                    WriteIni(pathMc + "PCL.ini", "InstanceCache", Conversions.ToString(folderListCheck));
                    host.StoreInstances(InitMcInstanceListWithoutCache(pathMc));
                }
                host.StoreFirstLoad(false);
            }
            if (isCanceled()) return;
            if (host.Instances().Any(v => LegacyMinecraftCodes.Number(v.Key) != 6)) {
                string? savedSelection = ReadIni(pathMc + "PCL.ini", "Version");
                if (!string.IsNullOrEmpty(savedSelection)) {
                    foreach (KeyValuePair<TCard, List<TInstance>> card in host.Instances()) {
                        foreach (TInstance instance in card.Value) {
                            if (Operators.CompareString(instance.Name, savedSelection, false) == 0 && instance.StateCode != 0) {
                                SelectInstance(instance);
                                Logger.Info($"选择该文件夹储存的 Minecraft 版本：{host.SelectedInstance()!.PathVersion}");
                                return;
                            }
                        }
                    }
                }
                if (host.Instances().First().Value[0].StateCode != 0) {
                    SelectInstance(host.Instances().First().Value[0]);
                    Logger.Info($"自动选择 Minecraft 版本：{host.SelectedInstance()!.PathVersion}");
                }
            } else {
                SelectInstance(null);
                Logger.Info("未找到可用 Minecraft 版本");
            }
            if (ReadBoolean("SystemDebugDelay")) host.RandomDelay(200, 3000);
        } catch (Exception ex) {
            if (isCanceled() || ex.IsCanceled()) return;
            WriteIni(pathMc + "PCL.ini", "InstanceCache", "");
            Logger.Error(ex, "加载 .minecraft 版本列表失败");
        }
    }

    public Dictionary<TCard, List<TInstance>>? InitMcInstanceListWithCache(string folder) {
        Dictionary<TCard, List<TInstance>> results = [];
        try {
            int cardCount = Conversions.ToInteger(ReadIni(folder + "PCL.ini", "CardCount", "-1"));
            if (cardCount == -1) return null;
            for (int i = 0; i < cardCount; i++) {
                TCard cardType = Card(Conversions.ToInteger(ReadIni(folder + "PCL.ini", "CardKey" + (i + 1), ":")));
                List<TInstance> instanceList = [];
                foreach (string versionName in ReadIni(folder + "PCL.ini", "CardValue" + (i + 1), ":")!.Split(':')) {
                    if (versionName == "") continue;
                    string versionsFolder = $"{folder}versions\\{versionName}\\";
                    if (!Directory.Exists(versionsFolder) || MmcImportVisibility.IsPrivateReservation(versionsFolder)) continue;
                    if (MmcInstanceLifecycleVisibility.IsCompletedQuarantine(versionsFolder)) continue;
                    MmcInstanceInspection managed = MmcInstanceInspector.Read(versionsFolder);
                    if (!managed.IsLegacy || !managed.Management.ObservationComplete) {
                        if (!Directory.Exists(versionsFolder)) continue;
                        instanceList.Add(host.Instance.CreateInstance(versionsFolder).Load());
                        continue;
                    }
                    if (!LegacyMinecraftDirectoryScanner.HasFiles(DirectoryUtils.GetInfo(versionsFolder))) continue;
                    if (FileUtils.Exists(versionsFolder + ".pclignore")) {
                        if (host.FirstLoad()) {
                            Logger.Info($"清理残留的忽略项目：{versionsFolder}");
                            FileUtils.Delete(versionsFolder + ".pclignore");
                        } else {
                            Logger.Info($"跳过要求忽略的项目：{versionsFolder}");
                            continue;
                        }
                    }
                    try {
                        TInstance instance = host.Instance.CreateInstance(versionsFolder);
                        instanceList.Add(instance);
                        string setup = instance.PathVersion + "PCL\\Setup.ini";
                        instance.Info = ReadIni(setup, "CustomInfo", "")!;
                        if (string.IsNullOrEmpty(instance.Info)) instance.Info = ReadIni(setup, "Info", instance.Info)!;
                        instance.Logo = host.Instance.ReadVersionLogo(setup, instance.LogoForStorage);
                        instance.StateCode = Conversions.ToInteger(ReadIni(setup, "State", Conversions.ToString(instance.StateCode)));
                        instance.IsStar = Conversions.ToBoolean(ReadIni(setup, "IsStar", Conversions.ToString(false)));
                        // Preserve LF's existing root-level DisplayType cache read.
                        instance.CardCode = Conversions.ToInteger(ReadIni(folder + "PCL\\Setup.ini", "DisplayType", "0"));
                        McInstallManifest? manifest = instance.StateCode == 0 ? null : Configs.McInstallManifest.Get(instance.Config);
                        if (manifest != null && manifest.SchemaVersion <= Versions.McInstallManifestVersion && manifest.ManagedFiles.Any(file => file.Component != null)) {
                            McInstallManifest latestManifest = manifest.RefreshModComponents(instance.PathIndie);
                            if (!ReferenceEquals(latestManifest, manifest)) {
                                manifest = latestManifest;
                                Configs.McInstallManifest.Set(manifest, instance.Config);
                                instance.Config.Save();
                            }
                        }
                        string? cachedVanillaName = ReadIni(setup, "VersionVanillaName", "Unknown");
                        if (instance.StateCode != 0 && (manifest != null || cachedVanillaName != "Unknown")) {
                            McVersion version;
                            if (manifest != null) {
                                version = new McVersion(manifest.Components) { OptiFineAsMod = manifest.ManagedFiles.Any(file => file.Component == "OptiFine") };
                            } else {
                                string? ReadCachedInfo(string key) {
                                    string? value = ReadIni(setup, key, null);
                                    return string.IsNullOrEmpty(value) ? null : value;
                                }
                                version = new McVersion(cachedVanillaName!) { OptiFine = ReadCachedInfo("VersionOptiFine") };
                                foreach (ModLoaders loader in new[] { ModLoaders.Fabric, ModLoaders.Forge, ModLoaders.NeoForge }) {
                                    string? cachedVersion = ReadCachedInfo($"Version{loader}");
                                    if (cachedVersion != null) version.ModLoaderVersions[loader] = cachedVersion;
                                }
                                if (Conversions.ToBoolean(ReadIni(setup, "VersionLiteLoader", Conversions.ToString(false))))
                                    version.ModLoaderVersions[ModLoaders.LiteLoader] = "未知版本";
                            }
                            version.ReleaseTime = Conversions.ToDate(ReadIni(setup, "ReleaseTime", Conversions.ToString(new DateTime(1970, 1, 1, 15, 0, 0))));
                            version.IsFool = version.IsFool || instance.StateCode == 3;
                            version.IsSnapshot = version.IsSnapshot || instance.StateCode == 2 || instance.Name.ContainsIgnoreCase("combat");
                            instance.Version = version;
                            if (manifest != null) instance.RefreshInfo();
                        } else {
                            instance.Load();
                        }
                        if (instance.StateCode == 0) {
                            string oldDescription = instance.Info;
                            instance.StateCode = 1;
                            instance.Check();
                            string? customInfo = ReadIni(setup, "CustomInfo");
                            if (instance.StateCode == 1 || (string.IsNullOrEmpty(customInfo) && Operators.CompareString(oldDescription, instance.Info, false) != 0)) {
                                Logger.Info($"版本 {instance.Name} 的错误状态已变更，新的状态为：{instance.Info}");
                                return null;
                            }
                        }
                        if (string.IsNullOrEmpty(instance.Logo)) {
                            Logger.Info($"版本 {instance.Name} 未被加载");
                            return null;
                        }
                    } catch (Exception ex) {
                        Logger.Warn(ex, $"读取版本加载缓存失败（{versionName}）");
                        return null;
                    }
                }
                if (instanceList.Any()) results.Add(cardType, instanceList);
            }
            return results;
        } catch (Exception ex) {
            Logger.Warn(ex, "读取版本缓存失败");
            return null;
        }
    }

    public Dictionary<TCard, List<TInstance>> InitMcInstanceListWithoutCache(string folder) {
        List<TInstance> instanceList = [];
        foreach (DirectoryInfo versionFolderInfo in LegacyMinecraftDirectoryScanner.GetDirectories(folder + "versions")) {
            string versionFolder = versionFolderInfo.FullName + "\\";
            if (!Directory.Exists(versionFolder)) continue;
            MmcInstanceInspection managed = MmcInstanceInspector.Read(versionFolder);
            if (!managed.IsLegacy || !managed.Management.ObservationComplete) {
                if (!Directory.Exists(versionFolder)) continue;
                TInstance managedInstance = host.Instance.CreateInstance(versionFolder);
                instanceList.Add(managedInstance);
                managedInstance.Load();
                continue;
            }
            if (!LegacyMinecraftDirectoryScanner.HasFiles(versionFolderInfo)) {
                Logger.Info($"跳过空文件夹或已清理的版本：{versionFolderInfo.FullName}");
                continue;
            }
            if ((versionFolderInfo.Name == "cache" || versionFolderInfo.Name == "BLClient" || versionFolderInfo.Name == "PCL") &&
                !FileUtils.Exists(versionFolderInfo.FullName + "\\" + versionFolderInfo.Name + ".json")) {
                Logger.Info($"跳过可能不是版本文件夹的项目：{versionFolderInfo.FullName}");
                continue;
            }
            if (FileUtils.Exists(versionFolder + ".pclignore")) {
                if (host.FirstLoad()) {
                    Logger.Info($"清理残留的忽略项目：{versionFolder}");
                    FileUtils.Delete(versionFolder + ".pclignore");
                } else {
                    Logger.Info($"跳过要求忽略的项目：{versionFolder}");
                    continue;
                }
            }
            TInstance instance = host.Instance.CreateInstance(versionFolder);
            instanceList.Add(instance);
            instance.Load();
        }
        Dictionary<TCard, List<TInstance>> results = [];
        try {
            Dictionary<TCard, List<TInstance>> original = [];
            List<TInstance> stars = [];
            foreach (TInstance instance in instanceList.ToList()) {
                if (!instance.IsStar || instance.CardCode == 1) continue;
                stars.Add(instance);
                instanceList.Remove(instance);
            }
            if (stars.Any()) original.Add(Card(-1), stars);
            Filter(ref instanceList, original, [0], 6);
            Filter(ref instanceList, original, [3], 5);
            Filter(ref instanceList, original, [6, 7, 8, 9], 2);
            List<TInstance> useful = [];
            List<TInstance> rubbish = [];
            Filter(ref instanceList, [5], rubbish);
            TInstance? largest = MeloongCore.Extensions.EnumerableExtensions.MaxBy(
                instanceList.Where(v => v.StateCode == 1 || v.StateCode == 2), v => v.ReleaseTime);
            if (largest != null && largest.StateCode == 2) {
                useful.Add(largest);
                instanceList.Remove(largest);
            }
            Filter(ref instanceList, [2], rubbish);
            Dictionary<string, TInstance> newer = [];
            List<int> drops = [];
            foreach (TInstance instance in instanceList) {
                if (!instance.Version.Valid) continue;
                if (!drops.Contains(instance.Version.Drop)) drops.Add(instance.Version.Drop);
                string key = instance.Version.Drop + "-" + instance.StateCode;
                if (!newer.ContainsKey(key)) {
                    newer.Add(key, instance);
                    continue;
                }
                if (instance.StateCode == 4) {
                    if (instance.Version.OptiFineCode > newer[key].Version.OptiFineCode) newer[key] = instance;
                } else if (instance.ReleaseTime > newer[key].ReleaseTime) newer[key] = instance;
            }
            foreach (int drop in drops) {
                string optifine = drop + "-4", vanilla = drop + "-1";
                if (newer.ContainsKey(optifine) && newer.ContainsKey(vanilla)) {
                    TInstance vanillaInstance = newer[vanilla];
                    TInstance optifineInstance = newer[optifine];
                    if (vanillaInstance.Version.Drop > optifineInstance.Version.Drop) {
                        useful.Add(vanillaInstance);
                        instanceList.Remove(vanillaInstance);
                    }
                    useful.Add(optifineInstance);
                    instanceList.Remove(optifineInstance);
                } else if (newer.ContainsKey(optifine)) {
                    useful.Add(newer[optifine]);
                    instanceList.Remove(newer[optifine]);
                } else if (newer.ContainsKey(vanilla)) {
                    useful.Add(newer[vanilla]);
                    instanceList.Remove(newer[vanilla]);
                }
            }
            rubbish.AddRange(instanceList);
            if (useful.Any()) original.Add(Card(3), useful);
            if (rubbish.Any()) original.Add(Card(4), rubbish);
            foreach (KeyValuePair<TCard, List<TInstance>> pair in original) {
                foreach (TInstance instance in pair.Value) {
                    TCard realType = instance.CardCode == 0 || LegacyMinecraftCodes.Number(pair.Key) == -1 ? pair.Key : instance.DisplayType;
                    if (!results.ContainsKey(realType)) results.Add(realType, []);
                    results[realType].Add(instance);
                }
            }
        } catch (Exception ex) {
            results.Clear();
            Logger.Error(ex, "分类版本列表失败");
        }
        Dictionary<TCard, List<TInstance>> sorted = [];
        foreach (int rule in new[] { -1, 2, 3, 4, 5, 6, 1 }) {
            if (results.ContainsKey(Card(rule))) sorted.Add(Card(rule), results[Card(rule)]);
        }
        results = sorted;
        foreach (int code in new[] { -1, 2, 3, 4, 5 }) {
            TCard cardType = Card(code);
            if (!results.ContainsKey(cardType)) continue;
            int ComponentCode(TInstance instance) {
                if (instance.Version.ForgelikeCode > 0) return instance.Version.ForgelikeCode;
                if (instance.Version.HasOptiFine) return instance.Version.OptiFineCode;
                return 0;
            }
            results[cardType] = LegacyMinecraftListOrder.SortByComparison(results[cardType], (left, right) => {
                if ((left.ReleaseTime.Year >= 2000 || right.ReleaseTime.Year >= 2000) && left.ReleaseTime != right.ReleaseTime)
                    return left.ReleaseTime > right.ReleaseTime;
                foreach (ModLoaders loader in new[] { ModLoaders.Fabric, ModLoaders.NeoForge, ModLoaders.Forge }) {
                    if (left.Version.ModLoaders.HasFlagF(loader) != right.Version.ModLoaders.HasFlagF(loader)) return left.Version.ModLoaders.HasFlagF(loader);
                }
                if (left.Version.HasOptiFine != right.Version.HasOptiFine) return left.Version.HasOptiFine;
                if (left.Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader) != right.Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)) return left.Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader);
                if (ComponentCode(left) != ComponentCode(right)) return ComponentCode(left) > ComponentCode(right);
                return Operators.CompareString(left.Name, right.Name, false) > 0;
            });
        }
        WriteIni(folder + "PCL.ini", "CardCount", Conversions.ToString(results.Count));
        for (int i = 0; i < results.Count; i++) {
            WriteIni(folder + "PCL.ini", "CardKey" + (i + 1), Conversions.ToString(LegacyMinecraftCodes.Number(results.Keys.ElementAt(i))));
            string value = "";
            foreach (TInstance instance in results.Values.ElementAt(i)) value += instance.Name + ":";
            WriteIni(folder + "PCL.ini", "CardValue" + (i + 1), value);
        }
        return results;
    }

    private static void Filter(ref List<TInstance> instances, Dictionary<TCard, List<TInstance>> target, int[] formula, int cardType) {
        List<TInstance> keep = instances.Where(v => formula.Contains(v.StateCode)).ToList();
        if (keep.Any()) {
            target.Add(Card(cardType), keep);
            instances = instances.Except(keep).ToList();
        }
    }
    private static void Filter(ref List<TInstance> instances, int[] formula, List<TInstance> keep) {
        keep.AddRange(instances.Where(v => formula.Contains(v.StateCode)));
        if (keep.Any()) instances = instances.Except(keep).ToList();
    }
}
