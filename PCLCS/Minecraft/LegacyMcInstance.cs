using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>LF's actual instance state and parser. The legacy nested McInstance keeps its CLR identity and enum field types.</summary>
public partial class LegacyMcInstance<TSelf, TState, TCard>
    where TSelf : LegacyMcInstance<TSelf, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum {
    private readonly Func<LegacyMinecraftInstanceHost<TSelf>> hostProvider;
    private LegacyMinecraftInstanceHost<TSelf> host => hostProvider();
    private TSelf Self => (TSelf)this;
    public string PathVersion { get; }
    public string PathIndie {
        get {
            MmcInstanceInspection managed = MmcInstanceInspector.Read(PathVersion);
            if (!managed.IsLegacy || !managed.Management.ObservationComplete) {
                if (!managed.IsConsistent) throw new IOException(managed.Error ?? "实例管理状态尚未确认，不能推导为公共游戏目录。");
                return PathUtils.AddSlashSuffix(managed.Paths!.GameRoot);
            }
            if (!host.HasSaved("VersionArgumentIndieV2", Self)) {
                if (!IsLoaded) Load();
                bool ShouldBeIndie() {
                    if (host.HasSaved("VersionArgumentIndie", Self) && host.ReadInteger("VersionArgumentIndie", Self) > 0) {
                        Logger.Info($"版本隔离初始化（{Name}）：从老的版本独立设置中迁移");
                        return host.ReadInteger("VersionArgumentIndie", Self) == 1;
                    }
                    DirectoryInfo modFolder = DirectoryUtils.GetInfo(PathVersion + "mods\\");
                    DirectoryInfo saveFolder = DirectoryUtils.GetInfo(PathVersion + "saves\\");
                    if ((modFolder.Exists && modFolder.EnumerateFiles().Any()) || (saveFolder.Exists && saveFolder.EnumerateDirectories().Any())) {
                        Logger.Info($"版本隔离初始化（{Name}）：版本文件夹下存在 mods 或 saves 文件夹，自动开启");
                        return true;
                    }
                    bool isRelease = StateCode != 3 && StateCode != 5 && StateCode != 2;
                    Logger.Info($"版本隔离初始化（{Name}）：从全局默认设置中（{host.ReadInteger("LaunchArgumentIndieV2", null)}）判断，State {State}，IsRelease {isRelease}，Modable {Modable}");
                    return host.ReadInteger("LaunchArgumentIndieV2", null) switch {
                        0 => false,
                        1 => Modable,
                        2 => !isRelease,
                        3 => !isRelease || Modable,
                        _ => true
                    };
                }
                host.WriteBoolean("VersionArgumentIndieV2", ShouldBeIndie(), Self);
            }
            return host.ReadBoolean("VersionArgumentIndieV2", Self) ? PathVersion : host.SelectedFolder();
        }
    }
    public string Name {
        get {
            if (name == null && PathVersion != "") name = PathUtils.GetLastPart(PathVersion);
            return name!;
        }
    }
    private string? name;
    public string Info = "该版本未被加载，请向作者反馈此问题";
    public TState State;
    internal int StateCode { get => LegacyMinecraftCodes.Number(State); set => State = LegacyMinecraftCodes.Value<TState>(value); }
    public string? Logo { get => host.NormalizeResourceUri(logo); set => logo = host.LogoForStorage(value); }
    protected internal string? LogoForStorage => logo;
    private string? logo;
    public bool IsStar;
    public TCard DisplayType;
    internal int CardCode { get => LegacyMinecraftCodes.Number(DisplayType); set => DisplayType = LegacyMinecraftCodes.Value<TCard>(value); }
    public bool Modable {
        get {
            if (!IsLoaded) Load();
            return Version.ModLoaders.Flags().Any() || CardCode == 2;
        }
    }
    public McVersion Version {
        get {
            if (version != null) return version;
            try {
                McVersion result = new(Name, JsonObject, JsonVersion);
                version = result;
                if (result.VanillaName == "Unknown") Info = "PCL 无法识别该版本的 MC 版本号";
            } catch (Exception ex) {
                Logger.Warn(ex, "识别 Minecraft 版本时出错");
                version = new McVersion("Unknown");
                Info = "无法识别：" + ex.Message;
            }
            return version;
        }
        set => version = value;
    }
    private McVersion? version;
    public DateTime ReleaseTime => Version.ReleaseTime;

    public string GetJsonPath() {
        string? jsonPath = PathVersion + Name + ".json";
        if (!FileUtils.Exists(jsonPath)) {
            jsonPath = null;
            foreach (string candidatePath in DirectoryUtils.EnumerateFiles(PathVersion, searchPattern: "*.json")) {
                try {
                    JObject candidate = (JObject)FileUtils.ReadAsJson(candidatePath)!;
                    if (!candidate.ContainsKey("mainClass")) continue;
                    if (!candidate.ContainsKey("type")) continue;
                    if (!candidate.ContainsKey("id")) continue;
                } catch { continue; }
                jsonPath = candidatePath;
                break;
            }
            if (jsonPath == null) throw new Exception($"未找到版本 JSON 文件：{PathVersion}{Name}.json");
            Logger.Warn($"未找到同名版本 JSON，自动换用 {jsonPath}");
        }
        return jsonPath;
    }
    public string JsonText {
        get {
            bool FastJsonCheck(string json) {
                string trimmedJson = json.Trim();
                return trimmedJson.StartsWithF("{") && trimmedJson.EndsWithF("}");
            }
            if (jsonText == null) {
                string jsonPath = GetJsonPath();
                jsonText = FileUtils.TryReadAsString(jsonPath) ?? "";
                if (!FastJsonCheck(jsonText)) {
                    if (host.OnUiThread()) {
                        Logger.Warn("版本 JSON 文件为空或有误，由于代码在主线程运行，将不再进行重试");
                        jsonText.DeserializeJson();
                    } else {
                        Logger.Warn($"版本 JSON 文件为空或有误，将在 2s 后重试读取（{jsonPath}）");
                        Thread.Sleep(2000);
                        jsonText = FileUtils.ReadAsString(jsonPath);
                        if (!FastJsonCheck(jsonText)) jsonText.DeserializeJson();
                    }
                }
            }
            return jsonText;
        }
        set => jsonText = value;
    }
    private string? jsonText;
    public JObject JsonObject {
        get {
            if (jsonObject == null) {
                string text = JsonText;
                try {
                    jsonObject = (JObject)text.DeserializeJson()!;
                    if (jsonObject.ContainsKey("patches") && !jsonObject.ContainsKey("time")) {
                        IsHmclFormatJson = true;
                        JObject? currentObject = null;
                        List<JObject> subjsonList = [];
                        foreach (JObject subjson in jsonObject["patches"]!) subjsonList.Add(subjson);
                        subjsonList = LegacyMinecraftListOrder.SortByComparison(subjsonList, (left, right) =>
                            Conversion.Val((left["priority"] ?? "0").ToString()) < Conversion.Val((right["priority"] ?? "0").ToString()));
                        foreach (JObject subjson in subjsonList) {
                            string? id = (string?)subjson["id"];
                            if (id != null) {
                                Logger.Info($"合并 HMCL 分支项：{id}");
                                if (currentObject != null) currentObject.Merge(subjson);
                                else currentObject = subjson;
                            } else {
                                Logger.Info("存在为空的 HMCL 分支项");
                            }
                        }
                        jsonObject = currentObject!;
                        jsonObject["id"] = Name;
                        if (jsonObject.ContainsKey("inheritsFrom")) jsonObject.Remove("inheritsFrom");
                    }
                    string? instanceNameInheritFrom = null;
                    try {
                        instanceNameInheritFrom = jsonObject["inheritsFrom"] == null ? "" : jsonObject["inheritsFrom"]!.ToString();
                        if (Operators.CompareString(instanceNameInheritFrom, Name, false) == 0) {
                            Logger.Warn($"自引用的继承版本：{Name}");
                            instanceNameInheritFrom = "";
                        } else {
                            while (!string.IsNullOrEmpty(instanceNameInheritFrom)) {
                                TSelf inheritInstance = host.CreateInstance(instanceNameInheritFrom!);
                                if (Operators.CompareString(inheritInstance.InheritName, instanceNameInheritFrom, false) == 0)
                                    throw new Exception("版本依赖项出现嵌套：" + instanceNameInheritFrom);
                                instanceNameInheritFrom = inheritInstance.InheritName;
                                JArray currentLib = (JArray)jsonObject["libraries"]!.DeepClone();
                                foreach (JToken libToken in inheritInstance.JsonObject["libraries"]!) currentLib.Add(libToken);
                                inheritInstance.JsonObject.Merge(jsonObject);
                                jsonObject = inheritInstance.JsonObject;
                                jsonObject["libraries"] = currentLib;
                            }
                        }
                    } catch (Exception ex) {
                        Logger.Warn(ex, $"合并版本依赖项 JSON 失败（{instanceNameInheritFrom ?? "null"}）");
                    }
                } catch (Exception ex) {
                    throw new Exception("初始化版本 JSON 时失败（" + (Name ?? "null") + "）", ex);
                }
            }
            return jsonObject;
        }
        set => jsonObject = value;
    }
    private JObject? jsonObject;
    public bool IsOldJson => JsonObject["minecraftArguments"] != null && (string?)JsonObject["minecraftArguments"] != "";
    public bool IsHmclFormatJson { get; set; }
    public JObject? JsonVersion {
        get {
            if (!jsonVersionInited) {
                jsonVersionInited = true;
                try {
                    if (FileUtils.Exists($"{PathVersion}{Name}.jar")) {
                        using ZipArchive archive = FileUtils.OpenZip(PathVersion + Name + ".jar");
                        ZipArchiveEntry? entry = archive.GetEntry("version.json");
                        if (entry != null) {
                            using StreamReader stream = new(entry.Open());
                            jsonVersion = (JObject?)stream.ReadToEnd().DeserializeJson();
                        }
                    }
                } catch (Exception ex) {
                    Logger.Warn(ex, $"从版本 jar 中读取 version.json 失败（{PathVersion}{Name}.jar）");
                }
            }
            return jsonVersion;
        }
    }
    private bool jsonVersionInited;
    private JObject? jsonVersion;
    public string InheritName {
        get {
            if (inheritName == null) {
                inheritName = (JsonObject["inheritsFrom"] ?? "").ToString();
                if (JsonText.Contains("liteloader") && Version.VanillaName != Name && !JsonText.Contains("logging")) {
                    if (Operators.CompareString((JsonObject["jar"] ?? Version.VanillaName).ToString(), Version.VanillaName, false) == 0)
                        inheritName = Version.VanillaName;
                }
                if (IsHmclFormatJson) inheritName = "";
            }
            return inheritName;
        }
    }
    private string? inheritName;
    public IInstanceConfigProvider Config;
    public void ResetSettingsCache() {
        host.ClearIniCache(PathIndie + "options.txt");
        host.ClearIniCache(PathVersion + "PCL\\Setup.ini");
        Config.DiscardCache();
        host.ClearInstanceSettingsCache(Self);
    }
    public LegacyMcInstance(string nameOrFullPath, LegacyMinecraftInstanceHost<TSelf> host)
        : this(nameOrFullPath, () => host) { }
    public LegacyMcInstance(string nameOrFullPath, Func<LegacyMinecraftInstanceHost<TSelf>> hostProvider) {
        this.hostProvider = hostProvider;
        nameOrFullPath = PathUtils.RemoveExtendedPrefix(nameOrFullPath);
        // A full path did not initialize ModMinecraft's module/loader fields in LF. Keep its platform host lazy.
        PathVersion = PathUtils.AddSlashSuffix((nameOrFullPath.Contains(":") ? "" : host.SelectedFolder() + "versions\\") + nameOrFullPath);
        Config = new InstanceConfigProvider(PathVersion);
    }
    public override bool Equals(object? obj) => obj is TSelf instance && PathUtils.IsSame(PathVersion, instance.PathVersion);
    public override int GetHashCode() => base.GetHashCode();
}
