using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS.Validation;

namespace PCLLF.Presentation.Accounts;

public sealed class LegacyOfflineLoginPage<TLegacy, TMs, TServer> {
    private readonly LegacyLoginHost<TLegacy, TMs, TServer> host;
    private readonly LegacyOfflineLoginView view;
    public LegacyOfflineLoginPage(LegacyLoginHost<TLegacy, TMs, TServer> host, LegacyOfflineLoginView view) { this.host = host; this.view = view; }
    public void Loaded() => view.StartSkin();
    public void Reload(bool KeepInput) {
        if (KeepInput && view.IsReloaded()) {
            string input = view.Name().Trim();
            view.SetNames(string.IsNullOrEmpty(host.ReadString("LoginLegacyName")) ? null : host.ReadString("LoginLegacyName").Split("¨"));
            view.SetName(input);
        } else {
            if (string.IsNullOrEmpty(host.ReadString("LoginLegacyName"))) view.SetNames(null);
            else { view.SetNames(host.ReadString("LoginLegacyName").ToString().Split("¨")); view.SetName(host.ReadString("LoginLegacyName").ToString().BeforeFirst("¨").Trim()); }
        }
        view.SetReloaded(true);
    }
    public TLegacy GetLoginData(bool PageExists, Func<string> ReadName) {
        string name = PageExists ? ReadName().Replace("¨", "").Trim() : "";
        return host.CreateLegacy(new LegacyLoginInput { UserName = name, SkinType = host.ReadInteger("LaunchSkinType"), SkinName = host.ReadString("LaunchSkinID") });
    }
    public string IsVaild(TLegacy LoginData) {
        if (host.LegacyName(LoginData).Trim() == "") return "玩家名不能为空！";
        if (host.LegacyName(LoginData).Contains("\"")) return "玩家名不能包含英文引号！";
        if (host.HasSelectedInstance() && host.SelectedVersionValid() && host.SelectedVanillaVersion() >= new Version(20, 0, 3) && host.LegacyName(LoginData).Trim().Length > 16) return "自 1.20.3 起，玩家名至多只能包含 16 个字符！";
        return "";
    }
    public static bool SuppressSpace(int CaretIndex, int TextLength) => CaretIndex == TextLength || CaretIndex == 0;
    public void TextChanged() {
        if (host.ReadInteger("LaunchSkinType") == 0) view.RestartSkin();
        view.ShowChineseHint(!view.Name().RegexCheck("^[0-9A-Za-z_]*$"));
        view.InvalidateMeasureWhenLoaded();
    }
    public void SkinClick() {
        if ((host.ReadBoolean("UiHiddenPageSetup") || host.ReadBoolean("UiHiddenSetupLaunch")) && !host.HiddenForceShow()) host.Hint("启动设置已被禁用！", LegacyLoginHint.Red);
        else host.OpenLaunchSettings();
    }
}

public sealed class LegacyServerLoginPage<TLegacy, TMs, TServer> {
    private readonly LegacyLoginHost<TLegacy, TMs, TServer> host;
    private readonly LegacyServerLoginView view;
    private readonly bool auth;
    private bool firstLoad = true;
    private string? displayedAuthEndpoint;
    public LegacyServerLoginPage(LegacyLoginHost<TLegacy, TMs, TServer> host, LegacyServerLoginView view, bool Auth) { this.host = host; this.view = view; auth = Auth; }
    private string Prefix => auth ? "Auth" : "Nide";
    private string AuthEndpoint() => PCLCS.AuthlibEndpoint.ForServer(host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerAuthServer") : host.ReadString("CacheAuthServerServer"));
    public void Reload(bool KeepInput) {
        if (auth) {
            string endpoint = AuthEndpoint();
            if (!PCLCS.AuthlibEndpoint.Same(displayedAuthEndpoint, endpoint)) {
                view.SetPassword("");
                KeepInput = false;
            }
            displayedAuthEndpoint = endpoint;
        }
        view.SetRemember(host.ReadBoolean("LoginRemember"));
        if (KeepInput && !firstLoad) {
            string input = view.Name(); view.SetNames(string.IsNullOrEmpty(host.ReadString("Login" + Prefix + "Email")) ? null : host.ReadString("Login" + Prefix + "Email").Split("¨")); view.SetName(input);
        } else {
            if (string.IsNullOrEmpty(host.ReadString("Login" + Prefix + "Email"))) view.SetNames(null);
            else {
                view.SetNames(host.ReadString("Login" + Prefix + "Email").Split("¨")); view.SetName(host.ReadString("Login" + Prefix + "Email").BeforeFirst("¨"));
                if (host.ReadBoolean("LoginRemember")) view.SetPassword(auth ? RememberedAuthPassword(view.Name()) : host.ReadString("Login" + Prefix + "Pass").BeforeFirst("¨").Trim());
            }
        }
        firstLoad = false;
    }
    public TServer GetLoginData(Func<bool> PageExists, Func<string> ReadName, Func<string> ReadPassword) {
        string server = auth ? AuthEndpoint() :
            "https://auth.mc-user.com:233/" + (host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerNide") : host.ReadString("CacheNideServer")) + "/authserver";
        bool exists = PageExists();
        bool sameEndpoint = !auth || displayedAuthEndpoint is null || PCLCS.AuthlibEndpoint.Same(displayedAuthEndpoint, server);
        return host.CreateServer(new LegacyLoginInput { Token = Prefix, BaseUrl = server, UserName = exists ? ReadName().Replace("¨", "").Trim() : "", Password = exists && sameEndpoint ? ReadPassword().Replace("¨", "").Trim() : "", Description = auth ? "Authlib-Injector" : "统一通行证" });
    }
    public string IsVaild(TServer LoginData) {
        if (string.IsNullOrEmpty(host.ServerName(LoginData))) return "账号不能为空！";
        if (string.IsNullOrEmpty(host.ServerPassword(LoginData))) return "密码不能为空！";
        return "";
    }
    public void NameChanged(string SenderText) { if (string.IsNullOrEmpty(SenderText)) view.SetPassword(""); if (host.AnimationDisabled() == 0) host.WriteSetting("Cache" + Prefix + "Access", ""); }
    public void PasswordChanged() { if (host.AnimationDisabled() == 0) host.WriteSetting("Cache" + Prefix + "Access", ""); }
    public void SelectionChanged(int SelectedIndex) {
        if (SelectedIndex == -1 || !host.ReadBoolean("LoginRemember")) { view.SetPassword(""); return; }
        if (auth) {
            string[] names = host.ReadString("LoginAuthEmail").Split('¨');
            view.SetPassword(SelectedIndex < names.Length ? RememberedAuthPassword(names[SelectedIndex]) : "");
        } else view.SetPassword(host.ReadString("LoginNidePass").Split('¨')[SelectedIndex].Trim());
    }
    private string RememberedAuthPassword(string name) {
        var cache = host.AccountSnapshot("CacheAuth");
        return PCLCS.AuthlibEndpoint.Same(AuthEndpoint(), cache.GetValueOrDefault(PCLCS.AuthlibEndpoint.CredentialServerSetting) as string) &&
            string.Equals(name, cache.GetValueOrDefault("CacheAuthUsername") as string, StringComparison.OrdinalIgnoreCase)
            ? cache.GetValueOrDefault("CacheAuthPass") as string ?? "" : "";
    }
    public void RememberChanged(string Key, bool Checked) { if (host.AnimationDisabled() == 0) host.WriteSetting(Key, Checked); }
    public void UpdateLinkText() => view.SetLinkContent(string.IsNullOrEmpty(view.Name()) ? "注册账号" : "找回密码");
    private string RegisterAddress() => host.HasSelectedInstance() ? host.ReadInstanceString("VersionServerAuthRegister") : host.ReadString("CacheAuthServerRegister");
    public void LinkClick() {
        if (auth) {
            if (Operators.ConditionalCompareObjectEqual(view.LinkContent(), "注册账号", false)) host.OpenWebsite(RegisterAddress());
            else { string address = RegisterAddress(); host.OpenWebsite(address.Replace("/auth/register", "/auth/forgot")); }
        }
        else host.OpenWebsite(Operators.ConditionalCompareObjectEqual(view.LinkContent(), "注册账号", false) ? "https://login.mc-user.com:233/" + host.ReadInstanceString("VersionServerNide") + "/register" : "https://login.mc-user.com:233/account/login");
    }
    public void ReloadRegisterButton() => view.ShowRegister(string.IsNullOrEmpty(new LegacyValidateHttp().Validate(RegisterAddress())));
}

public sealed class LegacyMicrosoftLoginPage<TLegacy, TMs, TServer> {
    private readonly LegacyLoginHost<TLegacy, TMs, TServer> host;
    private readonly LegacyMicrosoftLoginView view;
    public LegacyMicrosoftLoginPage(LegacyLoginHost<TLegacy, TMs, TServer> host, LegacyMicrosoftLoginView view) { this.host = host; this.view = view; }
    public void Reload(bool KeepInput) {
        int before = view.SelectedIndex(); view.ClearItems(); view.AddNewAccount();
        try { foreach (var account in JObject.Parse(host.ReadString("LoginMsJson"))) view.AddAccount(account.Key, () => account.Value!.ToString()); }
        catch (Exception) { Logger.Error("微软账号列表暂时无法读取，原始数据已保留。", LogBehavior.Toast); }
        view.SelectIndex(KeepInput ? Math.Max(0, before) : 0);
    }
    public void SelectionChanged() {
        if (host.AnimationDisabled() != 0 || view.SelectedItem() is null || !view.HasPresenter()) return;
        if (view.IsAccountItem(view.SelectedItem())) view.SetPresenterContent(view.AccountName(view.SelectedItem()));
        else if (view.IsNewAccountItem(view.SelectedItem())) view.SetPresenterContent(view.NewAccountContent(view.SelectedItem()));
    }
    public TMs GetLoginData(bool PageExists) {
        if (!PageExists) return host.CreateMs(new LegacyLoginInput { PopulateMsBasic = true, RefreshToken = host.ReadString("CacheMsV2OAuthRefresh"), UserName = host.ReadString("CacheMsV2Name") });
        TMs result = default!;
        host.RunInUiWait(() => {
            if (view.SelectedIndex() == 0) result = host.CreateMs(new LegacyLoginInput());
            else { object? item = view.SelectedItem(); result = host.CreateMs(new LegacyLoginInput { PopulateMsBasic = true, RefreshToken = view.AccountToken(item), UserName = view.AccountName(item) }); }
        });
        return result;
    }
    public string IsVaild(TMs LoginData) => string.IsNullOrEmpty(host.MsRefreshToken(LoginData)) ? "请在登录账号后再启动游戏！" : "";
    public void LoginClick(Func<TMs> GetLoginData) {
        view.EnableAccounts(false); view.EnableLogin(false); view.SetLoginText("0%");
        host.RunInNewThread(() => {
            try {
                host.MsLogin.Start(GetLoginData(), true);
                while (host.MsLogin.State() == LegacyLoginTaskState.Loading) { host.RunInUi(() => view.SetLoginText(Math.Round(host.MsLogin.Progress() * 100) + "%")); Thread.Sleep(50); }
                if (host.MsLogin.State() == LegacyLoginTaskState.Finished) host.RunInUi(() => host.RefreshLaunchPage(false, true));
                else if (host.MsLogin.State() == LegacyLoginTaskState.Canceled) throw new OperationCanceledException();
                else if (host.MsLogin.Error() is null) throw new Exception("未知错误！");
                else throw new Exception(host.MsLogin.Error()!.Message, host.MsLogin.Error());
            } catch (Exception error) {
                if (error.IsCanceled()) { }
                else if (error.Message.StartsWithF("$")) host.Hint(error.Message.TrimStart('$'), LegacyLoginHint.Red);
                else if (error is AuthenticationException && error.Message.Contains("SSL/TLS")) Logger.Error(error, "正版登录验证失败，请考虑在 [设置 → 其他] 中关闭 [在正版登录时验证 SSL 证书]，然后再试。\r\n\r\n原始错误信息：", LogBehavior.Alert);
                else Logger.Error(error, "正版登录尝试失败", LogBehavior.Alert);
            } finally { host.RunInUi(() => { view.EnableAccounts(true); view.EnableLogin(true); view.SetLoginText("登录"); }); }
        }, "Ms Login");
    }
}
