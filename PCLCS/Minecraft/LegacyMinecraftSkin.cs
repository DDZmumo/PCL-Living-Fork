using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class LegacyMinecraftService<TInstance, TFolder, TLibrary, TNetFile, TState, TCard, TFolderKind>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TFolder : LegacyMinecraftFolder<TFolderKind>, new() where TLibrary : LegacyMinecraftLibrary, new()
    where TNetFile : class where TState : struct, Enum where TCard : struct, Enum where TFolderKind : struct, Enum {
    private readonly object skinDownloadLock = new();

    public LegacyMinecraftSkinInfo McSkinSelect() {
        string? fileName = host.SelectSkin();
        if (string.IsNullOrEmpty(fileName)) return new LegacyMinecraftSkinInfo { IsVaild = false };
        try {
            (double width, double height) = host.ImageSize(fileName!);
            if (width != 64 || (height != 32 && height != 64)) {
                host.Hint("皮肤图片大小应为 64x32 像素或 64x64 像素！", true);
                return new LegacyMinecraftSkinInfo { IsVaild = false };
            }
            System.IO.FileInfo fileInfo = FileUtils.GetInfo(fileName!);
            if (fileInfo.Length > 24 * 1024) {
                host.Hint("皮肤文件大小需小于 24 KB，而所选文件大小为 " + Math.Round(fileInfo.Length / 1024.0, 2) + " KB", true);
                return new LegacyMinecraftSkinInfo { IsVaild = false };
            }
        } catch (Exception ex) {
            Logger.Error(ex, "皮肤文件存在错误", LogBehavior.Toast);
            return new LegacyMinecraftSkinInfo { IsVaild = false };
        }
        int isSlim = host.SelectSkinModel();
        if (isSlim == 3) {
            host.Hint("请在皮肤下载页面确认皮肤种类后再使用此皮肤！", false);
            return new LegacyMinecraftSkinInfo { IsVaild = false };
        }
        return new LegacyMinecraftSkinInfo { IsVaild = true, IsSlim = isSlim == 2, LocalFile = fileName };
    }

    public string McSkinGetAddress(string uuid, string? type, string? server = null) {
        if (string.IsNullOrEmpty(uuid)) throw new Exception("UUID 为空。");
        if (uuid.StartsWithF("00000") && type != "Auth") throw new Exception("离线 UUID 无正版皮肤文件：" + uuid);
        string url;
        switch (type) {
            case "Mojang": case "Ms": url = "https://sessionserver.mojang.com/session/minecraft/profile/"; break;
            case "Nide":
                server ??= host.SelectedInstance() == null ? ReadString("CacheNideServer") : ReadString("VersionServerNide", host.SelectedInstance());
                url = "https://auth.mc-user.com:233/" + server + "/sessionserver/session/minecraft/profile/";
                break;
            case "Auth":
                server ??= host.SelectedInstance() == null || string.IsNullOrEmpty(ReadString("VersionServerAuthServer", host.SelectedInstance())) ? ReadString("CacheAuthServerServer") :
                    ReadString("VersionServerAuthServer", host.SelectedInstance());
                url = server?.TrimEnd('/') + "/sessionserver/session/minecraft/profile/";
                break;
            default: throw new ArgumentException("皮肤地址种类无效：" + (type ?? "null"));
        }
        string cacheKey = type is "Auth" or "Nide" ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + "-" + uuid : uuid;
        string index = host.TempFolder() + "Cache\\Skin\\Index" + type + ".ini";
        string? cachedAddress = ReadIni(index, cacheKey);
        if (!string.IsNullOrEmpty(cachedAddress)) return cachedAddress;
        string skinString = host.RequestJson(url + uuid, null);
        if (string.IsNullOrEmpty(skinString)) throw new Exception("皮肤返回值为空，可能是未设置自定义皮肤的用户");
        string? skinValue = null;
        bool foundTextures = false;
        try {
            foreach (JToken property in skinString.DeserializeJson()!["properties"]!) {
                if ((string?)property["name"] == "textures") {
                    skinValue = (string?)property["value"];
                    foundTextures = true;
                    break;
                }
            }
            if (!foundTextures) throw new Exception("未从皮肤返回值中找到符合条件的 Property");
        } catch (Exception ex) {
            Logger.Warn(ex, $"无法完成解析的皮肤返回值，可能是未设置自定义皮肤的用户：{skinString}");
            throw new Exception("皮肤返回值中不包含皮肤数据项，可能是未设置自定义皮肤的用户", ex);
        }
        skinString = Encoding.UTF8.GetString(Convert.FromBase64String(skinValue!));
        JObject skinJson = (JObject)skinString.Lower().DeserializeJson()!;
        if (skinJson["textures"] == null || skinJson["textures"]!["skin"] == null || skinJson["textures"]!["skin"]!["url"] == null)
            throw new Exception("用户未设置自定义皮肤");
        string skinUrl = skinJson["textures"]!["skin"]!["url"]!.ToString();
        skinValue = skinUrl.Contains("minecraft.net/") ? skinUrl.Replace("http://", "https://") : skinUrl;
        WriteIni(index, cacheKey, skinValue);
        Logger.Info($"UUID {uuid} 对应的皮肤文件为 {skinValue}");
        return skinValue;
    }

    public string McSkinDownload(string address) {
        _ = PathUtils.GetLastPart(address);
        string fileAddress = host.TempFolder() + "Cache\\Skin\\" + address.GetStableHashCode() + ".png";
        lock (skinDownloadLock) {
            if (!FileUtils.Exists(fileAddress)) {
                host.DownloadFile(address, fileAddress + ".PCLDownloading");
                FileUtils.Move(fileAddress + ".PCLDownloading", fileAddress);
                Logger.Info($"皮肤下载成功：{fileAddress}");
            }
            return fileAddress;
        }
    }

    public void McDownloadClientUpdateHint(string versionName, JObject json) {
        try {
            JToken? version = null;
            foreach (JToken token in json["versions"]!) {
                if (token["id"] != null && token["id"]!.ToString() == versionName) { version = token; break; }
            }
            if (version == null) return;
            DateTime time = (DateTime)version["releaseTime"]!;
            string text = $"新版本：{versionName}\r\n" + ((DateTime.Now - time).TotalDays > 1
                ? "更新时间：" + time : "更新于：" + StringUtils.FormatTimeSpan(time - DateTime.Now, false));
            int result = host.ShowUpdateNotice(new LegacyMinecraftUpdateNotice {
                Text = text,
                ThirdButton = (DateTime.Now - time).TotalHours > 3 ? "更新日志" : "",
                ThirdAction = () => host.ShowUpdateLog(version)
            });
            if (result == 2) host.OpenDownload(versionName);
        } catch (Exception ex) {
            Logger.Error(ex, $"Minecraft 更新提示发送失败（{versionName ?? "Nothing"}）");
        }
    }

    public string FilterAccessToken(string raw, char filterChar) {
        if (raw.Contains("accessToken ")) {
            foreach (string token in raw.RegexSearch("(?<=accessToken ([^ ]{5}))[^ ]+(?=[^ ]{5})"))
                raw = raw.Replace(token, new string(filterChar, token.Length));
        }
        string? accessToken = host.AccessToken();
        if (accessToken != null && accessToken.Length >= 10 && raw.ContainsIgnoreCase(accessToken) && Microsoft.VisualBasic.CompilerServices.Operators.CompareString(host.LoginUuid(), host.AccessToken(), false) != 0) {
            raw = raw.Replace(accessToken, Strings.Left(accessToken, 5) + new string(filterChar, accessToken.Length - 10) + Strings.Right(accessToken, 5));
        }
        return raw;
    }
}

public static class LegacyMinecraftText {
    public static string GetMcFoolName(string name) {
        name = name.Lower();
        if (name.StartsWithF("2.0")) return "2013 | 这个秘密计划了两年的更新将游戏推向了一个新高度！";
        if (name == "15w14a") return "2015 | 作为一款全年龄向的游戏，我们需要和平，需要爱与拥抱。";
        if (name == "1.rv-pre1") return "2016 | 是时候将现代科技带入 Minecraft 了！";
        if (name == "3d shareware v1.34") return "2019 | 我们从地下室的废墟里找到了这个开发于 1994 年的杰作！";
        if (name.StartsWithF("20w14inf") || name == "20w14∞") return "2020 | 我们加入了 20 亿个新的维度，让无限的想象变成了现实！";
        if (name == "22w13oneblockatatime") return "2022 | 一次一个方块更新！迎接全新的挖掘、合成与骑乘玩法吧！";
        if (name == "23w13a_or_b") return "2023 | 研究表明：玩家喜欢作出选择——越多越好！";
        if (name == "24w14potato") return "2024 | 毒马铃薯一直都被大家忽视和低估，于是我们超级加强了它！";
        if (name == "25w14craftmine") return "2025 | 你可以合成任何东西——包括合成你的世界！";
        if (name == "26w14a") return "2026 | 为什么需要物品栏？让方块们跟着你走吧！";
        return "";
    }
    public static string McSkinSex(string uuid) {
        if (uuid.Length != 32) return "Steve";
        int a = int.Parse(uuid[7].ToString(), NumberStyles.AllowHexSpecifier);
        int b = int.Parse(uuid[15].ToString(), NumberStyles.AllowHexSpecifier);
        int c = int.Parse(uuid[23].ToString(), NumberStyles.AllowHexSpecifier);
        int d = int.Parse(uuid[31].ToString(), NumberStyles.AllowHexSpecifier);
        return ((a ^ b ^ c ^ d) % 2) != 0 ? "Alex" : "Steve";
    }
    public static string FilterUserName(string raw, char filterChar) {
        if (raw.Contains(":\\Users\\")) raw = System.Text.RegularExpressions.Regex.Replace(raw, "(?<=:\\\\Users\\\\)[^\\\\]+", token => new string(filterChar, token.Length));
        if (raw.Contains(":/Users/")) raw = System.Text.RegularExpressions.Regex.Replace(raw, "(?<=:/Users/)[^/]+", token => new string(filterChar, token.Length));
        return raw;
    }
}
