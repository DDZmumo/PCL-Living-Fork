using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Windows;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS.Validation;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Accounts;

public sealed class LegacyLoggedInView {
    public Action<string> SetName = null!, SetEmail = null!;
    public Action<bool> ShowEmail = null!;
    public Action StartSkin = null!, OpenEditMenu = null!, OpenSkinMenu = null!, SaveSkin = null!, RefreshSkin = null!, ChangeCape = null!;
    public Func<FrameworkElement> EditButton = null!, ExitButton = null!, ButtonsPanel = null!;
    public Func<bool> EditMenuOpen = null!, SkinMenuOpen = null!, DataMouseOver = null!;
    public Func<LegacySelectedSkin> SelectSkin = null!;
    public Action<string> ReloadSkinCache = null!;
}
public sealed class LegacySelectedSkin { public bool IsVaild, IsSlim; public string LocalFile = null!; }

public sealed class LegacyLoggedInPage<TLegacy, TMs, TServer> {
    private readonly LegacyLoginHost<TLegacy, TMs, TServer> host;
    private readonly LegacyLoggedInView view;
    private readonly string kind;
    private bool isChanging;
    private ControlAnimations Animations => ControlServices.Current.Animations;
    public LegacyLoggedInPage(LegacyLoginHost<TLegacy, TMs, TServer> host, LegacyLoggedInView view, string Kind) { this.host = host; this.view = view; kind = Kind; }
    public void Loaded() => view.StartSkin();
    public void Reload(bool KeepInput) {
        view.SetName(host.ReadString("Cache" + kind + "Name"));
        if (kind == "MsV2") return;
        view.SetEmail(host.ReadString("Cache" + kind + "Username")); view.ShowEmail(!host.ReadBoolean("UiLauncherEmail"));
        if (kind == "Auth") Loaded();
    }
    public TMs GetMsLoginData() {
        Dictionary<string, object?> cached = host.AccountSnapshot("CacheMsV2");
        if (host.MsLogin.State() == LegacyLoginTaskState.Finished) return host.CreateMs(new LegacyLoginInput {
            PopulateMsBasic = true, PopulateMsFull = true,
            RefreshToken = Conversions.ToString(cached["CacheMsV2OAuthRefresh"]), UserName = Conversions.ToString(cached["CacheMsV2Name"]),
            AccessToken = Conversions.ToString(cached["CacheMsV2Access"]), Uuid = Conversions.ToString(cached["CacheMsV2Uuid"]), ProfileJson = Conversions.ToString(cached["CacheMsV2ProfileJson"])
        });
        return host.CreateMs(new LegacyLoginInput { PopulateMsBasic = true, RefreshToken = Conversions.ToString(cached["CacheMsV2OAuthRefresh"]), UserName = Conversions.ToString(cached["CacheMsV2Name"]) });
    }
    public TServer GetServerLoginData() {
        Dictionary<string, object?> cached = host.AccountSnapshot("Cache" + kind);
        string? server = kind == "Auth" ? (host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerAuthServer") : Conversions.ToString(cached["CacheAuthServerServer"])) + "/authserver" :
            "https://auth.mc-user.com:233/" + (host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerNide") : Conversions.ToString(cached["CacheNideServer"])) + "/authserver";
        return host.CreateServer(new LegacyLoginInput { Token = kind, BaseUrl = server, UserName = Conversions.ToString(cached["Cache" + kind + "Username"]), Password = Conversions.ToString(cached["Cache" + kind + "Pass"]), Description = kind == "Auth" ? "Authlib-Injector" : "统一通行证" });
    }
    public void ShowPanel() {
        if (kind == "MsV2") { Animations.Start(new[] { Animations.Opacity(view.ButtonsPanel(), 1 - view.ButtonsPanel().Opacity, new(120)) }, "PageLoginMsSkin Button"); return; }
        Animations.Start(new[] {
            Animations.Opacity(view.EditButton(), 1 - view.EditButton().Opacity, new(80)), Animations.Height(view.EditButton(), 25.5 - view.EditButton().Height, new(140, ease: ControlEase.OutFluent)), Animations.Height(view.EditButton(), -1.5, new(50, 140, ControlEase.InFluent)),
            Animations.Opacity(view.ExitButton(), 1 - view.ExitButton().Opacity, new(80)), Animations.Height(view.ExitButton(), 25.5 - view.ExitButton().Height, new(140, ease: ControlEase.OutFluent)), Animations.Height(view.ExitButton(), -1.5, new(50, 140, ControlEase.InFluent))
        }, "PageLogin" + kind + "Skin Button");
    }
    public void HidePanel() {
        if (kind == "MsV2") {
            if (view.EditMenuOpen() || view.SkinMenuOpen() || view.DataMouseOver()) return;
            Animations.Start(new[] { Animations.Opacity(view.ButtonsPanel(), -view.ButtonsPanel().Opacity, new(120)) }, "PageLoginMsSkin Button"); return;
        }
        Animations.Start(new[] {
            Animations.Opacity(view.EditButton(), -view.EditButton().Opacity, new(120, ease: ControlEase.OutFluent)), Animations.Height(view.EditButton(), 14 - view.EditButton().Height, new(120, ease: ControlEase.InFluent)),
            Animations.Opacity(view.ExitButton(), -view.ExitButton().Opacity, new(120, ease: ControlEase.OutFluent)), Animations.Height(view.ExitButton(), 14 - view.ExitButton().Height, new(120, ease: ControlEase.InFluent))
        }, "PageLogin" + kind + "Skin Button");
    }
    public void OpenEditMenu() => view.OpenEditMenu();
    public void EditClick(Func<TServer> GetLoginData, Action Reload) {
        if (kind == "MsV2") { view.OpenEditMenu(); return; }
        if (host.MainLogin.State() == LegacyLoginTaskState.Loading) {
            Logger.Warn("要求更换角色，但登录加载器繁忙");
            bool reselecting = host.IsReselectingServer(host.MainLogin.Input());
            host.Hint(reselecting ? "正在尝试更换，请稍候！" : "正在登录中，请稍后再更换角色！", reselecting ? LegacyLoginHint.Default : LegacyLoginHint.Red); return;
        }
        host.Hint("正在尝试更换，请稍候！", LegacyLoginHint.Default);
        try { host.SaveAccountGroup(new Dictionary<string, object?> { ["CacheAuthUuid"] = "", ["CacheAuthName"] = "" }); }
        catch (Exception) { Logger.Error("角色选择状态未能保存，原有状态已保留。", LogBehavior.Toast); return; }
        host.RunInThread(() => {
            try { TServer data = GetLoginData(); host.ForceReselectProfile(data, true); host.MainLogin.Wait(data, true); host.RunInUi(Reload); }
            catch (Exception error) { Logger.Error(error, "更换角色失败", LogBehavior.Toast); }
        });
    }
    public void ExitLogin() {
        try {
            var provider = kind == "MsV2" ? PCLCS.Applications.AccountProvider.Microsoft :
                kind == "Auth" ? PCLCS.Applications.AccountProvider.Auth : PCLCS.Applications.AccountProvider.Nide;
            if (host.SignOut is not null) host.SignOut(provider);
            else PCLCS.Applications.AccountSessionActions.SignOut(provider, new() {
                CancelMicrosoft = host.MsLogin.Cancel, CancelAuth = host.AuthLogin.Cancel, CancelNide = host.NideLogin.Cancel,
                ResetAuthInput = host.AuthLogin.ResetInput, ResetNideInput = host.NideLogin.ResetInput, Save = host.SaveAccountGroup
            });
            host.RefreshLaunchPage(false, true);
        } catch (Exception) { Logger.Error("退出登录未能保存，原有账号状态已保留。请稍后重试。", LogBehavior.Toast); }
    }
    public void SkinClick() {
        if (kind == "MsV2") { view.OpenSkinMenu(); return; }
        if (kind == "Nide") host.OpenWebsite("https://login.mc-user.com:233/" + (host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerNide") : host.ReadString("CacheNideServer")) + "/skin");
        else { string address = host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerAuthRegister") : host.ReadString("CacheAuthServerRegister"); if (string.IsNullOrEmpty(new LegacyValidateHttp().Validate(address))) host.OpenWebsite(address.Replace("/auth/register", "/user/closet")); }
    }
    public void SkinEditClick(Func<TMs> GetLoginData) {
        if (isChanging) { host.Hint("正在更改皮肤中，请稍候！", LegacyLoginHint.Default); return; }
        if (host.MainLogin.State() == LegacyLoginTaskState.Failed) { host.Hint("登录失败，无法更改皮肤！", LegacyLoginHint.Red); return; }
        LegacySelectedSkin info = view.SelectSkin(); if (!info.IsVaild) return;
        host.Hint("正在更改皮肤……", LegacyLoginHint.Default); isChanging = true;
        host.RunInNewThread(() => EditSkin(info, GetLoginData), "Ms Skin Upload");
    }
    private void EditSkin(LegacySelectedSkin info, Func<TMs> GetLoginData) {
    Retry:
        try {
            while (host.MsLogin.State() == LegacyLoginTaskState.Loading) Thread.Sleep(10);
            if (host.MsLogin.State() == LegacyLoginTaskState.Failed) throw new Exception("登录失败", host.MsLogin.Error());
            string token = host.ReadString("CacheMsV2Access"); string uuid = host.ReadString("CacheMsV2Uuid");
            string result = host.RequestRetry("https://api.minecraftservices.com/minecraft/profile/skins", HttpMethod.Post,
                new MultipartFormDataContent { { new StringContent(info.IsSlim ? "slim" : "classic"), "variant" }, { new ByteArrayContent(FileUtils.ReadAsBytes(info.LocalFile)), "file", PathUtils.GetLastPart(info.LocalFile) } }, null,
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["Accept"] = "*/*", ["User-Agent"] = "MojangSharp/0.1" });
            if (result.Contains("request requires user authentication")) { host.Hint("正在重新登录，将在登录后自动更改皮肤……", LegacyLoginHint.Default); host.MsLogin.Start(GetLoginData(), true); goto Retry; }
            Logger.Info("皮肤修改返回值：\r\n" + result); JObject json = (JObject)result.DeserializeJson()!;
            if (json.ContainsKey("errorMessage")) throw new Exception(json["errorMessage"]!.ToString());
            foreach (JObject skin in json["skins"]!) if (skin["state"]!.ToString() == "ACTIVE") { view.ReloadSkinCache((string)skin["url"]!); return; }
            throw new Exception("未知错误（" + result + "）");
        } catch (Exception error) {
            if (error is HttpRequestCodeException request) {
                if (request.StatusCode == HttpStatusCode.BadRequest) {
                    Logger.Warn(error, "更改皮肤时遭遇 400 错误");
                    if (request.Response?.Contains("\"error\"") == true) { host.Hint("更改皮肤失败：" + request.Response.DeserializeJson()!["error"]!.ToString(), LegacyLoginHint.Red); return; }
                    else if (request.Response?.Contains("\"errorMessage\"") == true) { host.Hint("更改皮肤失败：" + request.Response.DeserializeJson()!["errorMessage"]!.ToString(), LegacyLoginHint.Red); return; }
                } else if (request.StatusCode == HttpStatusCode.Unauthorized) { Logger.Warn(error, "更改皮肤时遭遇 401 错误"); host.Hint("正在重新登录，将在登录后自动更改皮肤……", LegacyLoginHint.Default); host.MsLogin.Start(GetLoginData(), true); goto Retry; }
            } else if (error.IsBadNetwork()) host.Hint("更改皮肤失败：连接 Mojang 服务器超时，请稍后再试，或使用 VPN 改善网络环境", LegacyLoginHint.Red);
            else Logger.Error(error, "更改皮肤失败", LogBehavior.Toast);
        } finally { isChanging = false; }
    }
    public void SaveSkin() => view.SaveSkin();
    public void RefreshSkin() => view.RefreshSkin();
    public void ChangeCape() => view.ChangeCape();
    public void RefreshCape(Func<TMs> GetLoginData) => host.RunInThread(() => {
        try { host.Hint("正在刷新披风列表……", LegacyLoginHint.Default); if (host.Launch.State() == LegacyLoginTaskState.Loading) host.MsLogin.Wait(null, false); else host.MsLogin.Wait(GetLoginData(), true); host.Hint("已刷新披风列表！", LegacyLoginHint.Green); }
        catch (Exception error) { Logger.Error(error, "刷新披风列表失败", LogBehavior.Toast); }
    });
}
