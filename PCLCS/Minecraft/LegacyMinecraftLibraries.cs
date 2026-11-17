using System;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class LegacyMinecraftService<TInstance, TFolder, TLibrary, TNetFile, TState, TCard, TFolderKind>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TFolder : LegacyMinecraftFolder<TFolderKind>, new() where TLibrary : LegacyMinecraftLibrary, new()
    where TNetFile : class where TState : struct, Enum where TCard : struct, Enum where TFolderKind : struct, Enum {
    public bool McJsonRuleCheck(JToken? ruleToken) {
        if (ruleToken == null) return true;
        bool required = false;
        foreach (JToken rule in ruleToken) {
            bool isRightRule = true;
            if (rule["os"] != null) {
                if (rule["os"]!["name"] != null) {
                    string osName = rule["os"]!["name"]!.ToString();
                    if (osName == "unknown") { }
                    else if (osName == "windows") {
                        if (rule["os"]!["version"] != null) isRightRule = isRightRule && host.OsVersion().RegexCheck(rule["os"]!["version"]!.ToString());
                    } else isRightRule = false;
                }
                if (rule["os"]!["arch"] != null) {
                    JToken arch = rule["os"]!["arch"]!;
                    // Keep the original JToken.Contains call; do not silently replace it with a new platform policy.
                    isRightRule = isRightRule && arch.ToString() != "x86" && !System.Linq.Enumerable.Contains<JToken>(arch, "arm");
                }
            }
            if (rule["features"] != null) {
                isRightRule = isRightRule && rule["features"]!["is_demo_user"] == null;
                if (((JObject)rule["features"]!).Children().Any(j => ((JProperty)j).Name.Contains("quick_play"))) isRightRule = false;
            }
            if (rule["action"]!.ToString() == "allow") {
                if (isRightRule) required = true;
            } else if (isRightRule) required = false;
        }
        return required;
    }

    public List<TLibrary> McLibListGet(TInstance instance, bool includeMainJar) {
        Logger.Info($"获取支持库列表：{instance.Name}");
        List<TLibrary> result = McLibListGetWithJson(instance.JsonObject);
        if (includeMainJar) {
            TInstance realInstance;
            string? requiredJar = instance.JsonObject["jar"]?.ToString();
            if (instance.IsHmclFormatJson || requiredJar == null) {
                TInstance original = instance;
                if (!((instance.Version.ModLoaders.HasFlagF(ModLoaders.Forge) || instance.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge)) && instance.Version.Vanilla.Major >= 17)) {
                    while (!string.IsNullOrEmpty(original.InheritName)) {
                        if (Operators.CompareString(original.InheritName, original.Name, false) == 0) break;
                        original = host.Instance.CreateInstance(McFolderSelected + "versions\\" + original.InheritName + "\\");
                    }
                }
                realInstance = host.Instance.CreateInstance(original.PathVersion);
            } else realInstance = host.Instance.CreateInstance(requiredJar);
            if (!FileUtils.Exists(realInstance.PathVersion + realInstance.Name + ".json")) {
                realInstance = instance;
                Logger.Warn($"可能缺少前置版本 {realInstance.Name}，找不到对应的 json 文件");
            }
            string? clientUrl, clientSha1;
            if (realInstance.JsonObject["downloads"] != null && realInstance.JsonObject["downloads"]!["client"] != null) {
                clientUrl = (string?)realInstance.JsonObject["downloads"]!["client"]!["url"];
                clientSha1 = (string?)realInstance.JsonObject["downloads"]!["client"]!["sha1"];
            } else { clientUrl = null; clientSha1 = null; }
            result.Add(new TLibrary {
                LocalPath = realInstance.PathVersion + realInstance.Name + ".jar", Size = 0, IsNatives = false, Url = clientUrl, SHA1 = clientSha1
            });
        }
        return result;
    }

    public List<TLibrary> McLibListGetWithJson(JObject jsonObject, bool keepSameNameDifferentVersionResult = false, string? customMcFolder = null) {
        customMcFolder ??= McFolderSelected;
        List<TLibrary> basicArray = [];
        foreach (JObject library in ((JArray)jsonObject["libraries"]!).Children()) {
            for (int i = library.Properties().Count() - 1; i >= 0; i--) {
                if (library.Properties().ElementAt(i).Value.Type == JTokenType.Null) library.Remove(library.Properties().ElementAt(i).Name);
            }
            if (!McJsonRuleCheck(library["rules"])) continue;
            string? rootUrl = (string?)library["url"];
            if (rootUrl != null) rootUrl += McLibGet((string)library["name"]!, false, true, customMcFolder).Replace("\\", "/");
            if (library["natives"] == null) {
                string localPath = McLibGet((string)library["name"]!, customMcFolder: customMcFolder);
                try {
                    if (library["downloads"] != null && library["downloads"]!["artifact"] != null) {
                        JToken artifact = library["downloads"]!["artifact"]!;
                        basicArray.Add(new TLibrary {
                            OriginalName = (string?)library["name"],
                            Url = (string?)(artifact["url"] ?? rootUrl),
                            LocalPath = artifact["path"] == null ? McLibGet((string)library["name"]!, customMcFolder: customMcFolder)
                                : customMcFolder + "libraries\\" + artifact["path"]!.ToString().Replace("/", "\\"),
                            Size = Conversions.ToLong(Conversion.Val(artifact["size"]!.ToString())),
                            IsNatives = false,
                            SHA1 = artifact["sha1"]?.ToString()
                        });
                    } else basicArray.Add(new TLibrary { OriginalName = (string?)library["name"], Url = rootUrl, LocalPath = localPath, Size = 0, IsNatives = false, SHA1 = null });
                } catch (Exception ex) {
                    Logger.Warn(ex, $"处理实际支持库列表失败（无 Natives，{library["name"] ?? "Nothing"}）");
                    basicArray.Add(new TLibrary { OriginalName = (string?)library["name"], Url = rootUrl, LocalPath = localPath, Size = 0, IsNatives = false, SHA1 = null });
                }
            } else if (library["natives"]!["windows"] != null) {
                string nativeName = (string)library["natives"]!["windows"]!;
                string FallbackNativePath() => McLibGet((string)library["name"]!, customMcFolder: customMcFolder)
                    .Replace(".jar", "-" + nativeName + ".jar").Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                try {
                    if (library["downloads"] != null && library["downloads"]!["classifiers"] != null && library["downloads"]!["classifiers"]![nativeName] != null) {
                        JToken native = library["downloads"]!["classifiers"]![nativeName]!;
                        basicArray.Add(new TLibrary {
                            OriginalName = (string?)library["name"],
                            Url = (string?)(native["url"] ?? rootUrl),
                            LocalPath = native["path"] == null ? FallbackNativePath() : $"{customMcFolder}libraries\\{native["path"]!.ToString().Replace("/", "\\")}",
                            Size = Conversions.ToLong(Conversion.Val(native["size"]!.ToString())), IsNatives = true, SHA1 = native["sha1"]!.ToString()
                        });
                    } else basicArray.Add(new TLibrary { OriginalName = (string?)library["name"], Url = rootUrl, Size = 0, IsNatives = true, SHA1 = null, LocalPath = FallbackNativePath() });
                } catch (Exception ex) {
                    Logger.Warn(ex, $"处理实际支持库列表失败（有 Natives，{library["name"] ?? "Nothing"}）");
                    basicArray.Add(new TLibrary { OriginalName = (string?)library["name"], Url = rootUrl, Size = 0, IsNatives = true, SHA1 = null, LocalPath = FallbackNativePath() });
                }
            }
        }
        Dictionary<string, TLibrary> resultArray = [];
        string GetVersion(TLibrary token) => PathUtils.GetLastPart(PathUtils.RemoveLastPart(token.LocalPath));
        for (int i = 0; i < basicArray.Count; i++) {
            string key = basicArray[i].Name + basicArray[i].IsNatives;
            if (resultArray.ContainsKey(key)) {
                string basicVersion = GetVersion(basicArray[i]);
                string resultVersion = GetVersion(resultArray[key]);
                if (basicVersion != resultVersion && keepSameNameDifferentVersionResult) {
                    Logger.Info($"发现疑似重复的支持库：{basicArray[i]} ({basicVersion}) 与 {resultArray[key]} ({resultVersion})");
                    resultArray.Add(key + host.NextUuid(), basicArray[i]);
                } else {
                    Logger.Info($"发现重复的支持库：{basicArray[i]} ({basicVersion}) 与 {resultArray[key]} ({resultVersion})，已忽略其中之一");
                    if (McUtils.CompareVersionGE(basicVersion, resultVersion)) resultArray[key] = basicArray[i];
                }
            } else resultArray.Add(key, basicArray[i]);
        }
        return resultArray.Values.ToList();
    }

    public string McLibGet(string original, bool withHead = true, bool ignoreLiteLoader = false, string? customMcFolder = null) {
        customMcFolder ??= McFolderSelected;
        string[] parts = original.Split(':');
        return (withHead ? customMcFolder + "libraries\\" : "") + $"{parts[0].Replace(".", "\\")}\\{parts[1]}\\{parts[2]}\\{parts.Skip(1).Join("-")}.jar";
    }

    public List<TNetFile> McLibNetFilesFromInstance(TInstance instance) {
        if (!instance.IsLoaded) instance.Load();
        List<TNetFile> result = [];
        try {
            TNetFile? mainJar = host.ClientJar(instance, true);
            if (mainJar != null) result.Add(mainJar);
        } catch (Exception ex) { Logger.Warn(ex, "版本缺失主 jar 文件所必须的信息"); }
        result.AddRange(McLibNetFilesFromTokens(McLibListGet(instance, false)));
        if (ReadInteger("VersionServerLogin", instance) == 3) {
            string targetFile = host.PureFolder() + "nide8auth.jar";
            try {
                Logger.Info("开始获取统一通行证下载信息");
                JObject info = (JObject)host.RequestJson(LegacyAuthenticationAgentSources.NideMetadataPrefix + ReadString("VersionServerNide", instance), null).DeserializeJson()!;
                result.Add(host.CreateNetFile([LegacyAuthenticationAgentSources.NideJar], targetFile, new FileChecker { Hash = info["jarHash"]!.ToString() }));
            } catch (Exception ex) {
                if (FileUtils.Exists(targetFile)) Logger.Warn(ex, "获取统一通行证下载信息失败");
                else throw new Exception("获取统一通行证下载信息失败", ex);
            }
        }
        if (ReadInteger("VersionServerLogin", instance) == 4) {
            string targetFile = host.PureFolder() + "authlib-injector.jar";
            try {
                Logger.Info("开始获取 Authlib-Injector 下载信息");
                JObject info = (JObject)host.RequestJson(LegacyAuthenticationAgentSources.AuthlibMetadata, LegacyAuthenticationAgentSources.AuthlibMetadataMirror).DeserializeJson()!;
                string address = info["download_url"]!.ToString().Replace("bmclapi2.bangbang93.com/mirrors/authlib-injector", "authlib-injector.yushi.moe");
                result.Add(host.CreateNetFile([address, address.Replace("authlib-injector.yushi.moe", "bmclapi2.bangbang93.com/mirrors/authlib-injector")],
                    targetFile, new FileChecker { Hash = info["checksums"]!["sha256"]!.ToString() }));
            } catch (Exception ex) {
                if (FileUtils.Exists(targetFile)) Logger.Warn(ex, "获取 Authlib-Injector 下载信息失败");
                else throw new Exception("获取 Authlib-Injector 下载信息失败", ex);
            }
        }
        if (ShouldIgnoreFileCheck(instance)) {
            Logger.Warn("设置要求尽量忽略文件检查，这可能会保留有误的文件");
            result = result.Where(file => {
                if (!FileUtils.Exists(host.DownloadPath(file))) return true;
                Logger.Warn($"跳过下载的支持库文件：{host.DownloadPath(file)}");
                return false;
            }).ToList();
        }
        return result;
    }

    public List<TNetFile> McLibNetFilesFromTokens(List<TLibrary> libs, string? customMcFolder = null) {
        customMcFolder ??= McFolderSelected;
        List<TNetFile> result = [];
        foreach (TLibrary token in libs) {
            FileChecker checker = token.Name!.ContainsIgnoreCase("labymod") ? new FileChecker()
                : new FileChecker { ActualSize = token.Size == 0 ? -1 : token.Size, Hash = token.SHA1 };
            List<string> urls = [];
            if (token.Url == null && token.Name == "net.minecraftforge:forge:universal")
                token.Url = "https://maven.minecraftforge.net" + token.LocalPath.Replace(customMcFolder + "libraries", "").Replace("\\", "/");
            if (token.Url != null) {
                urls.Add(token.Url);
                if (token.Url.Contains("launcher.mojang.com/v1/objects") || token.Url.Contains("client.txt") || token.Url.Contains(".tsrg"))
                    urls.AddRange(host.LauncherSources(token.Url));
                if (token.Url.Contains("maven")) {
                    string mirror = token.Url.Replace(token.Url.Substring(0, token.Url.IndexOfF("maven")), "https://bmclapi2.bangbang93.com/")
                        .Replace("maven.fabricmc.net", "maven").Replace("maven.minecraftforge.net", "maven").Replace("maven.neoforged.net/releases", "maven");
                    if (host.PreferMojang()) urls.Add(mirror); else urls.Insert(0, mirror);
                }
            }
            if (token.LocalPath.Contains("transformer-discovery-service")) {
                if (!FileUtils.Exists(token.LocalPath)) host.ExtractResource(token.LocalPath, "Transformer");
                Logger.Warn("已自动释放 Transformer Discovery Service");
                continue;
            }
            if (token.LocalPath.Contains("optifine\\OptiFine")) {
                string optifineBase = token.LocalPath.Replace(customMcFolder + "libraries\\optifine\\OptiFine\\", "").Split('_')[0] + "/" + PathUtils.GetLastPart(token.LocalPath).Replace("-", "_");
                optifineBase = "/maven/com/optifine/" + optifineBase;
                if (optifineBase.Contains("_pre")) optifineBase = optifineBase.Replace("com/optifine/", "com/optifine/preview_");
                urls.Add("https://bmclapi2.bangbang93.com" + optifineBase);
            } else if (urls.Count <= 2) {
                urls.AddRange(host.LibrarySources("https://libraries.minecraft.net" + token.LocalPath.Replace(customMcFolder + "libraries", "").Replace("\\", "/")));
            }
            result.Add(host.CreateNetFile(urls.Distinct(), token.LocalPath, checker));
        }
        return MeloongCore.Extensions.EnumerableExtensions.DistinctBy(result, host.DownloadPath).ToList();
    }

    public bool ShouldIgnoreFileCheck(TInstance instance) => ReadBoolean("VersionAdvanceAssetsV2", instance) || ReadInteger("VersionAdvanceAssets", instance) == 2;
}
