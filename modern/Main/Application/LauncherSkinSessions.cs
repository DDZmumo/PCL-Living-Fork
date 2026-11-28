// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Windows.Media;
using MeloongCore;
using PCLCS;
using PCLCS.Applications;
using PCLLF.Presentation.Launch;
using SkinLoader = PCL.ModLoader.LoaderTask<PCLLF.Presentation.Launch.LegacySkinRequest, string>;

namespace PCL;

internal static class LauncherSkinSessions
{
    internal static readonly LegacyLaunchSkins<SkinLoader> Skins = new(new() {
        ReadString = key => Settings.Get<string>(key),
        ReadInteger = key => Settings.Get<int>(key),
        WriteSetting = (key, value) => Settings.Set(key, value),
        OfflineLibrarySkin = SkinLibraryApplicationService.FindOfflineSkinFile,
        AccountInput = ReadAccountSkin,
        Input = task => task.Input,
        Output = (task, path) => task.Output = path,
        Canceled = (task, thread) => task.IsCanceledWithThread(thread),
        PublishAvatar = LauncherSkinAvatars.Publish,
        ImagePath = () => ModBase.PathImage,
        AppDataPath = () => Paths.AppDataThenName,
        OfflineUuid = name => global::Microsoft.VisualBasic.CompilerServices.Conversions.ToString(ModLaunch.McLoginLegacyUuid(name)),
        SkinSex = ModMinecraft.McSkinSex,
        DownloadSkin = ModMinecraft.McSkinDownload,
        SkinAddress = (kind, request) => request.UsesSkinSite
            ? GlassSkinSites.SkinAddress(request.Server, request.Id).GetAwaiter().GetResult() ?? throw new InvalidOperationException("未设置自定义皮肤")
            : ModMinecraft.McSkinGetAddress(request.Id, kind, request.Server),
        MojangUuid = (name, throwOnNotFound) => global::Microsoft.VisualBasic.CompilerServices.Conversions.ToString(ModLaunch.McLoginMojangUuid(name, throwOnNotFound)),
        Hint = text => ModMain.Hint(text)
    });

    internal static LegacySkinRequest SkinSiteRequest(SkinSiteCharacter character) =>
        new(character.Profile.Name, character.Profile.Id, character.Site.Server, UsesSkinSite: true);

    internal static ImageSource Avatar(string kind, LegacySkinRequest request) =>
        LauncherSkinAvatars.Read(kind, request) ?? GlassSkinHead.LastGood(kind, request);

    internal static string TextureFile(string kind, LegacySkinRequest request) =>
        LauncherSkinAvatars.ReadTexture(kind, request);

    private static LegacySkinRequest ReadAccountSkin(string kind)
    {
        var instance = ModMinecraft.McInstanceSelected;
        if (kind == "Auth" && GlassSkinSites.CurrentForLaunch() is { } character)
            return SkinSiteRequest(character);
        string prefix = kind == "Ms" ? "CacheMsV2" : "Cache" + kind;
        var cache = Settings.AccountSnapshot(prefix);
        string server = kind switch {
            "Auth" => instance is null ? cache.GetValueOrDefault("CacheAuthServerServer") as string ?? "" : Settings.Get<string>("VersionServerAuthServer", instance),
            "Nide" => instance is null ? cache.GetValueOrDefault("CacheNideServer") as string ?? "" : Settings.Get<string>("VersionServerNide", instance),
            _ => ""
        };
        if (kind == "Auth" && !AuthlibEndpoint.Same(AuthlibEndpoint.ForServer(server), cache.GetValueOrDefault(AuthlibEndpoint.CredentialServerSetting) as string))
            return new("", "", server);
        return new(cache.GetValueOrDefault(prefix + "Name") as string ?? "", cache.GetValueOrDefault(prefix + "Uuid") as string ?? "", server);
    }


    internal static void RefreshCache(SkinLoader loader)
    {
        if (LauncherSkinLoaders.All.Exists(task => task.State == ModBase.LoadState.Loading)) {
            ModMain.Hint("有正在获取中的皮肤，请稍后再试！");
            return;
        }
        ModBase.RunInThread(() => {
            try {
                ModMain.Hint("正在刷新皮肤……");
                string skin = ModBase.PathTemp + @"Cache\Skin";
                string uuid = ModBase.PathTemp + @"Cache\Uuid";
                if (DirectoryUtils.Exists(skin)) DirectoryUtils.Delete(skin);
                if (DirectoryUtils.Exists(uuid)) DirectoryUtils.Delete(uuid);
                foreach (string path in new[] { skin + @"\IndexMs.ini", skin + @"\IndexNide.ini",
                    skin + @"\IndexAuth.ini", uuid + @"\Mojang.ini" }) ModBase.IniClearCache(path);
                foreach (SkinLoader task in loader is null ? new[] { LauncherSkinLoaders.SkinLegacy, LauncherSkinLoaders.SkinMs } : new[] { loader })
                    task.WaitForExit(IsForceRestart: true);
                ModMain.Hint("已刷新皮肤！", ModMain.HintType.Green);
            }
            catch (Exception error) { Logger.Error(error, "刷新皮肤缓存失败", LogBehavior.Alert); }
        });
    }

    internal static void Save(SkinLoader loader)
    {
        if (loader.State != ModBase.LoadState.Finished || string.IsNullOrEmpty(loader.Output)) {
            ModMain.Hint("皮肤正在获取中，请稍候！", ModMain.HintType.Red);
            if (loader.State != ModBase.LoadState.Loading) loader.Start();
            return;
        }

        string address = loader.Output;
        string destination = MeloongCore.Wpf.Dialogs.SaveFile("选取保存皮肤的位置", PathUtils.GetLastPart(address));
        if (string.IsNullOrEmpty(destination)) return;
        try {
            if (address.StartsWith(ModBase.PathImage, StringComparison.OrdinalIgnoreCase)) new MyBitmap(address).Save(destination);
            else System.IO.File.Copy(address, destination, overwrite: true);
            ModMain.Hint("皮肤保存成功！", ModMain.HintType.Green);
        }
        catch (Exception error) { Logger.Error(error, "保存皮肤失败", LogBehavior.Toast); }
    }
}
