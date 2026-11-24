#nullable disable
using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;
public sealed partial class LegacyLaunchEngine<TInstance, TState, TCard, TOptions, TData, TServer, TMicrosoft, TOffline, TWatcher>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard>
    where TState : struct, Enum where TCard : struct, Enum where TOptions : class, ILegacyLaunchOptions<TInstance>, new()
    where TData : class, ILegacyLaunchLoginData where TServer : TData, ILegacyLaunchServerData
    where TMicrosoft : TData, ILegacyLaunchMicrosoftData where TOffline : TData, ILegacyLaunchOfflineData where TWatcher : class {
public void McLaunchNatives(LegacyLaunchTask<int, int> Loader)
    {

        // 创建文件夹
        string Target = GetNativesFolder();
        DirectoryUtils.Create(Target);

        // 解压文件
        McLaunchLog("正在解压 Natives 文件");
        var ExistFiles = new List<string>();
        foreach (LegacyMinecraftLibrary Native in SelectedJavaLibraries(true))
        {
            if (!Native.IsNatives)
                continue;
            ZipArchive Zip;
            try
            {
                Zip = FileUtils.OpenZip(Native.LocalPath);
            }
            catch (InvalidDataException ex)
            {
                Logger.Warn(ex, $"打开 Natives 文件失败（{Native.LocalPath}）");
                FileUtils.Delete(Native.LocalPath);
                throw new Exception("无法打开 Natives 文件（" + Native.LocalPath + "），该文件可能已损坏，请重新尝试启动游戏");
            }
            foreach (var Entry in Zip.Entries)
            {
                string FileName = Entry.FullName;
                if (PathUtils.GetExtension(FileName) == "dll")
                {
                    // 实际解压文件的步骤
                    string FilePath = Target + FileName;
                    ExistFiles.Add(FilePath);
                    var OriginalFile = FileUtils.GetInfo(FilePath);
                    if (OriginalFile.Exists)
                    {
                        if (OriginalFile.Length == Entry.Length)
                        {
                            if (host.Debug())
                                McLaunchLog("无需解压：" + FilePath);
                            continue;
                        }
                        // 删除原文件
                        try
                        {
                            FileUtils.Delete(FilePath);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            McLaunchLog("删除原 dll 访问被拒绝，这通常代表有一个 MC 正在运行，跳过解压：" + FilePath);
                            McLaunchLog("实际的错误信息：" + ex.GetDisplay(true));
                            break;
                        }
                    }
                    // 解压新文件
                    FileUtils.Write(FilePath, Entry.Open());
                    McLaunchLog("已解压：" + FilePath);
                }
            }
            if (Zip is not null)
                Zip.Dispose();
        }

        // 删除多余文件
        foreach (string FileName in DirectoryUtils.EnumerateFiles(Target))
        {
            if (ExistFiles.Contains(FileName))
                continue;
            try
            {
                McLaunchLog("删除：" + FileName);
                FileUtils.Delete(FileName);
            }
            catch (UnauthorizedAccessException ex)
            {
                McLaunchLog("删除多余文件访问被拒绝，跳过删除步骤");
                McLaunchLog("实际的错误信息：" + ex.GetDisplay(true));
                return;
            }
        }

    }


public string GetNativesFolder(TInstance Instance = null)
    {
        bool x86 = McLaunchJavaSelected?.Is32Bit == true && (Instance is null || ReferenceEquals(Instance, McInstanceSelected));
        if (Instance is null)
            Instance = McInstanceSelected;
        string suffix = x86 ? "-natives-x86" : "-natives";
        string Result = PathUtils.AddSlashSuffix(PathUtils.ToShortPath(Instance.PathVersion)) + Instance.Name + suffix + @"\";
        if (!(host.IsGbkEncoding() || Result.IsAsciiOnly()))
        {
            string fallback = x86 ? @"natives-x86\" : @"natives\";
            Result = Paths.AppData + @".minecraft\bin\" + fallback;
            if (!Result.IsAsciiOnly())
            {
                Result = host.OsDrive() + @"ProgramData\PCL\" + fallback;
            }
        }
        DirectoryUtils.Create(Result); // 提前创建，这样 DirectoryUtils.Shorten 才有结果（否则在长路径下首次启动 Forge 会崩溃）
        return PathUtils.AddSlashSuffix(PathUtils.ToShortPath(Result));
    }


public void McLaunchPrerun()
    {

        // 使用高性能显卡
        if (Setting<bool>("LaunchAdvanceGraphicCard"))
        {
            try
            {
                host.SetGpuPreference(McLaunchJavaSelected.JavaExePath);
                host.SetGpuPreference(host.ExecutablePath());
            }
            catch (Exception ex)
            {
                if (host.IsAdmin())
                {
                    Logger.Warn(ex, "直接调整显卡设置失败");
                }
                else
                {
                    Logger.Warn(ex, "直接调整显卡设置失败，将以管理员权限重启 PCL 再次尝试");
                    try
                    {
                        if (host.RunAdminTaskDone($"--gpu \"{McLaunchJavaSelected.JavaExePath}\""))
                        {
                            McLaunchLog("以管理员权限重启 PCL 并调整显卡设置成功");
                        }
                        else
                        {
                            throw new Exception("调整过程中出现异常");
                        }
                    }
                    catch (Exception exx)
                    {
                        Logger.Error(exx, "调整显卡设置失败，Minecraft 可能会使用默认显卡运行", LogBehavior.Toast);
                    }
                }
            }
        }

        // 更新 launcher_profiles.json
        do
        {
            try
            {
                // 确保可用
                if (!(McLoginLoader.Output.Type == "Microsoft"))
                    break;
                host.CreateLauncherProfiles(host.SelectedFolder());
                // 构建需要替换的 Json 对象
                string ReplaceJsonString = "\n            {\n              \"authenticationDatabase\": {\n                \"00000111112222233333444445555566\": {\n                  \"username\": \"" + McLoginLoader.Output.Name.Replace("\"", "-") + "\",\n                  \"profiles\": {\n                    \"66666555554444433333222221111100\": {\n                        \"displayName\": \"" + McLoginLoader.Output.Name + "\"\n                    }\n                  }\n                }\n              },\n              \"clientToken\": \"" + McLoginLoader.Output.ClientToken + "\",\n              \"selectedUser\": {\n                \"account\": \"00000111112222233333444445555566\", \n                \"profile\": \"66666555554444433333222221111100\"\n              }\n            }";
                JObject ReplaceJson = (JObject)ReplaceJsonString.DeserializeJson();
                // 更新文件
                JObject Profiles = (JObject)FileUtils.ReadAsJson(host.SelectedFolder() + "launcher_profiles.json");
                Profiles.Merge(ReplaceJson);
                FileUtils.Write(host.SelectedFolder() + "launcher_profiles.json", Profiles.ToString(), encoding: LegacyEncoding("GB18030"));
                McLaunchLog("已更新 launcher_profiles.json");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "更新 launcher_profiles.json 失败，将在删除文件后重试");
                try
                {
                    FileUtils.Delete(host.SelectedFolder() + "launcher_profiles.json");
                    host.CreateLauncherProfiles(host.SelectedFolder());
                    // 构建需要替换的 Json 对象
                    string ReplaceJsonString = "\n                    {\n                      \"authenticationDatabase\": {\n                        \"00000111112222233333444445555566\": {\n                          \"username\": \"" + McLoginLoader.Output.Name.Replace("\"", "-") + "\",\n                          \"profiles\": {\n                            \"66666555554444433333222221111100\": {\n                                \"displayName\": \"" + McLoginLoader.Output.Name + "\"\n                            }\n                          }\n                        }\n                      },\n                      \"clientToken\": \"" + McLoginLoader.Output.ClientToken + "\",\n                      \"selectedUser\": {\n                        \"account\": \"00000111112222233333444445555566\", \n                        \"profile\": \"66666555554444433333222221111100\"\n                      }\n                    }";
                    JObject ReplaceJson = (JObject)ReplaceJsonString.DeserializeJson();
                    // 更新文件
                    JObject Profiles = (JObject)FileUtils.ReadAsJson(host.SelectedFolder() + "launcher_profiles.json");
                    Profiles.Merge(ReplaceJson);
                    FileUtils.Write(host.SelectedFolder() + "launcher_profiles.json", Profiles.ToString(), encoding: LegacyEncoding("GB18030"));
                    McLaunchLog("已在删除后更新 launcher_profiles.json");
                }
                catch (Exception exx)
                {
                    Logger.Error(exx, "更新 launcher_profiles.json 失败");
                }
            }
        }
        while (false);

        // 更新 options.txt
        string SetupFileAddress = McInstanceSelected.PathIndie + "options.txt";
        try
        {
            SetupFileAddress = PersonalOptionsFiles.ApplyDefaultLanguage(McInstanceSelected.PathIndie, Setting<bool>("ToolHelpChinese"),
                McInstanceSelected.Version, host.ClearIniCache,
                () => (int?)McInstanceSelected.JsonVersion?["world_version"]);
            // 窗口
            switch (Setting<int>("LaunchArgumentWindowType"))
            {
                case 0: // 全屏
                    {
                        host.WriteIni(SetupFileAddress, "fullscreen", "true");
                        break;
                    }
                case 1: // 默认
                        // 其他
                    {
                        break;
                    }

                default:
                    {
                        host.WriteIni(SetupFileAddress, "fullscreen", "false");
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "更新 options.txt 失败", LogBehavior.Toast);
        }
        if (CurrentLaunchOptions.SaveBatch is null)
        {
            CurrentLaunchOptions.PersonalOptions = null;
            try
            {
                CurrentLaunchOptions.PersonalOptions = host.PreparePersonal(McInstanceSelected);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "个人游戏设置同步未完成，保留现有游戏配置");
            }
        }

        // 离线皮肤 Alex 警告
        if (McInstanceSelected.Version.Vanilla.Major <= 7 && McInstanceSelected.Version.Vanilla.Major >= 2 && ((LegacyLaunchLoginType)McLoginLoader.Input.TypeCode) == LegacyLaunchLoginType.Legacy && (Setting<int>("LaunchSkinType") == 2 || Setting<int>("LaunchSkinType") == 4 && Setting<bool>("LaunchSkinSlim"))) // 1.2 ~ 1.7
                                                                                                                                                                                                                                                                                                                          // 离线登录
                                                                                                                                                                                                                                                                                                                          // 强制 Alex
                                                                                                                                                                                                                                                                                                                          // 或选用 Alex 皮肤
        {
            Hint("此 Minecraft 版本尚不支持 Alex 皮肤，你的皮肤可能会显示为 Steve！", LegacyLaunchHint.Red);
        }

        // 离线皮肤资源包
        try
        {
            string ZipFileAddress = McInstanceSelected.PathIndie + @"resourcepacks\PCL2 Skin.zip";
            bool NewTypeSetup = McInstanceSelected.Version.Vanilla.Major >= 13 || McInstanceSelected.Version.Vanilla.Major < 6;
            if (((LegacyLaunchLoginType)McLoginLoader.Input.TypeCode) == LegacyLaunchLoginType.Legacy && Setting<int>("LaunchSkinType") == 4 && FileUtils.Exists(Paths.AppDataThenName + "CustomSkin.png"))
            {
                string MetaFileAddress = host.TempFolder() + "pack.mcmeta";
                string PackPicAddress = host.TempFolder() + "pack.png";
                if (McInstanceSelected.Version.Vanilla.Major < 6)
                {
                    McLaunchLog("Minecraft 版本过老，尚不支持自定义离线皮肤");
                    goto IgnoreCustomSkin;
                }
                var PackVersionJToken = McInstanceSelected.JsonVersion?["pack_version"];
                if (PackVersionJToken is JObject)
                    PackVersionJToken = ((JObject)PackVersionJToken)["resource"]; // 从 1.17 开始，可以从 pack_version.resource 获取资源包版本
                int PackVersion = Conversions.ToInteger(Conversion.Val(PackVersionJToken?.ToString()));
                if (PackVersion <= 0) // 没有对应字段的老版本 MC，或兜底
                {
                    switch (McInstanceSelected.Version.Vanilla.Major)
                    {
                        case 6:
                        case 7:
                        case 8:
                            {
                                PackVersion = 1;
                                break;
                            }
                        case 9:
                        case 10:
                            {
                                PackVersion = 2;
                                break;
                            }
                        case 11:
                        case 12:
                            {
                                PackVersion = 3;
                                break;
                            }
                        case 13:
                        case 14:
                            {
                                PackVersion = 4;
                                break;
                            }
                        case 15:
                            {
                                PackVersion = 5;
                                break;
                            }
                        case 16:
                            {
                                PackVersion = McInstanceSelected.Version.Vanilla.Build <= 1 ? 5 : 6;
                                break;
                            }
                        case 17:
                            {
                                PackVersion = 7;
                                break;
                            }
                        case 18:
                            {
                                PackVersion = 8; // 旧版快照可能没有可用的资源包格式声明
                                break;
                            }

                        default:
                            {
                                PackVersion = 17;
                                break;
                            }
                    }
                }
                McLaunchLog($"正在构建自定义皮肤资源包，版本号 {PackVersion}，MC 版本 {McInstanceSelected.Version.Vanilla}");
                // 准备文件
                var Bit = host.LoadImage(host.ImagePath() + "Heads/Logo.png");
                Bit.Save(PackPicAddress);
                FileUtils.Write(MetaFileAddress, "{\"pack\":{\"pack_format\":" + PackVersion + ",\"description\":\"PCL 自定义离线皮肤资源包\"}}");
                var Skin = host.LoadImage(Paths.AppDataThenName + "CustomSkin.png");
                if ((McInstanceSelected.Version.Vanilla.Major == 6 || McInstanceSelected.Version.Vanilla.Major == 7) && Skin.Height == 64)
                {
                    McLaunchLog("该 Minecraft 版本不支持双层皮肤，已进行裁剪");
                    Skin = Skin.Clip(0, 0, 64, 32);
                }
                Skin.Save(Paths.Base + @"PCL\CustomSkin_Cliped.png");
                // 构建压缩文件
                var FilesToCompress = new Dictionary<string, string>() { { "pack.mcmeta", MetaFileAddress }, { "pack.png", PackPicAddress } };
                if (McInstanceSelected.Version.Vanilla >= new Version(19, 0, 3))
                {
                    // 1.19.3+ 使用复杂版本的替换
                    foreach (var SkinName in new[] { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" })
                        FilesToCompress.Add($"assets/minecraft/textures/entity/player/{(Setting<bool>("LaunchSkinSlim") ? "slim" : "wide")}/{SkinName}.png", Paths.Base + @"PCL\CustomSkin_Cliped.png");
                }
                else
                {
                    FilesToCompress.Add($"assets/minecraft/textures/entity/{(Setting<bool>("LaunchSkinSlim") ? "alex.png" : "steve.png")}", Paths.Base + @"PCL\CustomSkin_Cliped.png");
                }
                FileUtils.CreateZipFromFiles(ZipFileAddress, FilesToCompress);
                FileUtils.Delete(Paths.Base + @"PCL\CustomSkin_Cliped.png");
                // 更改设置文件
                host.ClearIniCache(SetupFileAddress);
                string EnabledResourcePack = host.ReadIni(SetupFileAddress, "resourcePacks", "[]").TrimStart('[').TrimEnd(']');
                if (NewTypeSetup)
                {
                    if (string.IsNullOrEmpty(EnabledResourcePack))
                        EnabledResourcePack = "\"vanilla\"";
                    var EnabledResourcePacks = new List<string>(EnabledResourcePack.Split(","));
                    var NewResourcePacks = new List<string>();
                    foreach (var Res in EnabledResourcePacks)
                    {
                        if (Res != "\"file/PCL2 Skin.zip\"" && !string.IsNullOrEmpty(Res))
                            NewResourcePacks.Add(Res);
                    }
                    NewResourcePacks.Add("\"file/PCL2 Skin.zip\"");
                    string Result = "[" + NewResourcePacks.Join(',') + "]";
                    host.WriteIni(SetupFileAddress, "resourcePacks", Result);
                }
                else
                {
                    var EnabledResourcePacks = new List<string>(EnabledResourcePack.Split(","));
                    var NewResourcePacks = new List<string>();
                    foreach (var Res in EnabledResourcePacks)
                    {
                        if (Res != "\"PCL2 Skin.zip\"" && !string.IsNullOrEmpty(Res))
                            NewResourcePacks.Add(Res);
                    }
                    NewResourcePacks.Add("\"PCL2 Skin.zip\"");
                    string Result = "[" + NewResourcePacks.Join(',') + "]";
                    host.WriteIni(SetupFileAddress, "resourcePacks", Result);
                }

            IgnoreCustomSkin:
                ;
            }

            else if (FileUtils.Exists(ZipFileAddress))
            {
                McLaunchLog("正在清空自定义皮肤资源包");
                // 删除压缩文件
                FileUtils.Delete(ZipFileAddress);
                // 更改设置文件
                host.ClearIniCache(SetupFileAddress);
                string EnabledResourcePack = host.ReadIni(SetupFileAddress, "resourcePacks", "[]").TrimStart('[').TrimEnd(']');
                if (NewTypeSetup)
                {
                    if (string.IsNullOrEmpty(EnabledResourcePack))
                        EnabledResourcePack = "\"vanilla\"";
                    var EnabledResourcePacks = new List<string>(EnabledResourcePack.Split(","));
                    EnabledResourcePacks.Remove("\"file/PCL2 Skin.zip\"");
                    string Result = "[" + EnabledResourcePacks.Join(',') + "]";
                    host.WriteIni(SetupFileAddress, "resourcePacks", Result);
                }
                else
                {
                    var EnabledResourcePacks = new List<string>(EnabledResourcePack.Split(","));
                    EnabledResourcePacks.Remove("\"PCL2 Skin.zip\"");
                    string Result = "[" + EnabledResourcePacks.Join(',') + "]";
                    host.WriteIni(SetupFileAddress, "resourcePacks", Result);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "离线皮肤资源包设置失败", LogBehavior.Toast);
        }

    }

}
