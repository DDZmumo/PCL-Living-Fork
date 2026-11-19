#nullable disable
using System;
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
public string McLoginName()
    {
        // 根据当前登录方式优先返回
        switch (Setting<LegacyLaunchLoginType>("LoginType"))
        {
            case LegacyLaunchLoginType.Ms:
                {
                    if (!string.IsNullOrEmpty(Setting<string>("CacheMsV2Name")))
                        return Setting<string>("CacheMsV2Name");
                    break;
                }
            case LegacyLaunchLoginType.Legacy:
                {
                    if (!string.IsNullOrEmpty(Setting<string>("LoginLegacyName")))
                        return Setting<string>("LoginLegacyName").ToString().BeforeFirst("¨");
                    break;
                }
            case LegacyLaunchLoginType.Nide:
                {
                    if (!string.IsNullOrEmpty(Setting<string>("CacheNideName")))
                        return Setting<string>("CacheNideName");
                    break;
                }
            case LegacyLaunchLoginType.Auth:
                {
                    if (!string.IsNullOrEmpty(Setting<string>("CacheAuthName")))
                        return Setting<string>("CacheAuthName");
                    break;
                }
        }
        // 查找所有可能的项
        if (!string.IsNullOrEmpty(Setting<string>("CacheMsV2Name")))
            return Setting<string>("CacheMsV2Name");
        if (!string.IsNullOrEmpty(Setting<string>("CacheNideName")))
            return Setting<string>("CacheNideName");
        if (!string.IsNullOrEmpty(Setting<string>("CacheAuthName")))
            return Setting<string>("CacheAuthName");
        if (!string.IsNullOrEmpty(Setting<string>("LoginLegacyName")))
            return Setting<string>("LoginLegacyName").ToString().BeforeFirst("¨");
        return null;
    }


public string McLoginAble()
    {
        switch (Setting<LegacyLaunchLoginType>("LoginType"))
        {
            case LegacyLaunchLoginType.Ms:
                {
                    if (string.IsNullOrEmpty(Setting<string>("CacheMsV2OAuthRefresh")))
                    {
                        return host.CurrentLoginValidation(5);
                    }
                    else
                    {
                        return "";
                    }
                }
            case LegacyLaunchLoginType.Legacy:
                {
                    return host.CurrentLoginValidation(0);
                }
            case LegacyLaunchLoginType.Nide:
                {
                    if (string.IsNullOrEmpty(Setting<string>("CacheNideAccess")))
                    {
                        return host.CurrentLoginValidation(2);
                    }
                    else
                    {
                        return "";
                    }
                }
            case LegacyLaunchLoginType.Auth:
                {
                    if (string.IsNullOrEmpty(Setting<string>("CacheAuthAccess")))
                    {
                        return host.CurrentLoginValidation(3);
                    }
                    else
                    {
                        return "";
                    }
                }

            default:
                {
                    return "未知的登录方式";
                }
        }
    }


public string McLoginAble(TData LoginData)
    {
        switch (((LegacyLaunchLoginType)LoginData.TypeCode))
        {
            case LegacyLaunchLoginType.Ms:
                {
                    return host.LoginValidation(5, LoginData);
                }
            case LegacyLaunchLoginType.Legacy:
                {
                    return host.LoginValidation(0, LoginData);
                }
            case LegacyLaunchLoginType.Nide:
                {
                    return host.LoginValidation(2, LoginData);
                }
            case LegacyLaunchLoginType.Auth:
                {
                    return host.LoginValidation(3, LoginData);
                }

            default:
                {
                    return "未知的登录方式";
                }
        }
    }


public TData McLoginInput()
    {
        TData LoginData = null;
        var LoginType = Setting<LegacyLaunchLoginType>("LoginType");
        try
        {
            switch (LoginType)
            {
                case LegacyLaunchLoginType.Legacy:
                    {
                        LoginData = host.LoginInput(0, false);
                        break;
                    }
                case LegacyLaunchLoginType.Ms:
                    {
                        if (string.IsNullOrEmpty(Setting<string>("CacheMsV2OAuthRefresh")))
                        {
                            LoginData = host.LoginInput(5, false);
                        }
                        else
                        {
                            LoginData = host.LoginInput(5, true);
                        }

                        break;
                    }
                case LegacyLaunchLoginType.Nide:
                    {
                        if (string.IsNullOrEmpty(Setting<string>("CacheNideAccess")))
                        {
                            LoginData = host.LoginInput(2, false);
                        }
                        else
                        {
                            LoginData = host.LoginInput(2, true);
                        }

                        break;
                    }
                case LegacyLaunchLoginType.Auth:
                    {
                        if (string.IsNullOrEmpty(Setting<string>("CacheAuthAccess")))
                        {
                            LoginData = host.LoginInput(3, false);
                        }
                        else
                        {
                            LoginData = host.LoginInput(3, true);
                        }

                        break;
                    }
            }
            return LoginData;
        }
        catch (Exception ex)
        {
            throw new Exception($"获取登录输入信息失败（{LoginType}）", ex);
        }
    }


public void McLoginMsStart(LegacyLaunchTask<TMicrosoft, ILegacyLaunchLoginResult> Data)
    {
        var Input = Data.Input;
        string LogUsername = Input.UserName;
        McLaunchLog("登录方式：正版（" + (string.IsNullOrEmpty(LogUsername) ? "尚未登录" : LogUsername) + "）");
        Data.Progress = 0.05d;
        // 检查是否已经登录完成
        var Cached = host.AccountSnapshot("CacheMsV2");
        long ExpiresAt = Conversions.ToLong(Cached["CacheMsV2Expires"]);
        if (!Data.IsForceRestarting && ExpiresAt > 0L && ExpiresAt > ToUnixTimestampUtc(DateTime.UtcNow) && (Input.UserName ?? "") == (Conversions.ToString(Cached["CacheMsV2Name"]) ?? "")) // 不要求强行重启
                                                                                                                                                                                       // AccessToken 尚未过期
        {
            Data.Output = new LegacyLaunchLoginResult()
            {
                Name = Input.UserName,
                Type = "Microsoft",
                AccessToken = Conversions.ToString(Cached["CacheMsV2Access"]),
                Uuid = Conversions.ToString(Cached["CacheMsV2Uuid"]),
                ClientToken = Conversions.ToString(Cached["CacheMsV2Uuid"]),
                ProfileJson = Conversions.ToString(Cached["CacheMsV2ProfileJson"])
            };
            McLaunchLog("无需登录，AccessToken 尚未过期");
            goto SkipLogin;
        }
        // 尝试登录
        (string OAuthAccessToken, string OAuthRefreshToken) OAuthTokens;
        if (string.IsNullOrEmpty(Input.OAuthRefreshToken)) OAuthTokens = MsLoginStep1New(Data);
        else {
            OAuthTokens = MsLoginStep1Refresh(Input.OAuthRefreshToken);
            if (OAuthTokens.OAuthAccessToken == "Relogin") OAuthTokens = MsLoginStep1New(Data);
        }
        if (Data.IsCanceled)
            throw new OperationCanceledException();
        Data.Progress = 0.25d;
        if (Data.IsCanceled)
            throw new OperationCanceledException();
        string OAuthAccessToken = OAuthTokens.OAuthAccessToken;
        string OAuthRefreshToken = OAuthTokens.OAuthRefreshToken;
        string XBLToken = MsLoginStep2(OAuthAccessToken);
        Data.Progress = 0.4d;
        if (Data.IsCanceled)
            throw new OperationCanceledException();
        var Tokens = MsLoginStep3(XBLToken);
        Data.Progress = 0.55d;
        if (Data.IsCanceled)
            throw new OperationCanceledException();
        var LoginResult = MsLoginStep4(Tokens);
        ExpiresAt = LoginResult.ExpiresAt;
        Data.Progress = 0.7d;
        if (Data.IsCanceled)
            throw new OperationCanceledException();
        MsLoginStep5(LoginResult.AccessToken);
        Data.Progress = 0.85d;
        if (Data.IsCanceled)
            throw new OperationCanceledException();
        var Result = MsLoginStep6(LoginResult.AccessToken);
        Data.Progress = 0.98d;
        // 输出登录结果
        host.SaveMicrosoftSession(new Dictionary<string, object>() { { "CacheMsV2OAuthRefresh", OAuthRefreshToken }, { "CacheMsV2Access", LoginResult.AccessToken }, { "CacheMsV2Uuid", Result.UUID }, { "CacheMsV2Name", Result.UserName }, { "CacheMsV2ProfileJson", Result.ProfileJson }, { "CacheMsV2Expires", LoginResult.ExpiresAt } }, Input.UserName, () => Data.IsCanceled);
        Data.Output = new LegacyLaunchLoginResult() { AccessToken = LoginResult.AccessToken, Name = Result.UserName, Uuid = Result.UUID, Type = "Microsoft", ClientToken = Result.UUID, ProfileJson = Result.ProfileJson };
    // 结束
    SkipLogin:
        ;

        McLaunchLog($"微软登录结束，AccessToken 将在 {DateTimeOffset.FromUnixTimeSeconds(ExpiresAt).LocalDateTime} 过期");
        host.WriteSetting("HintBuy", true); // 关闭正版购买提示
    }


public void McLoginServerStart(LegacyLaunchTask<TServer, ILegacyLaunchLoginResult> Data) {
        TServer Input = Data.Input;
        bool NeedRefresh = false, WasRefreshed = false;
        McLaunchLog("登录方式：" + Input.Description);
        Data.Progress = 0.05;
        var Cached = host.AccountSnapshot("Cache" + Input.Token);
        bool refresh = !Data.Input.ForceReselectProfile && CanReuseAuthCache(Input, Cached) &&
            (Conversions.ToString(Cached["Cache" + Input.Token + "Username"]) ?? "") == (Data.Input.UserName ?? "") &&
            (Conversions.ToString(Cached["Cache" + Input.Token + "Pass"]) ?? "") == (Data.Input.Password ?? "") &&
            !string.IsNullOrEmpty(Conversions.ToString(Cached["Cache" + Input.Token + "Access"])) &&
            !string.IsNullOrEmpty(Conversions.ToString(Cached["Cache" + Input.Token + "Client"])) &&
            !string.IsNullOrEmpty(Conversions.ToString(Cached["Cache" + Input.Token + "Uuid"])) &&
            !string.IsNullOrEmpty(Conversions.ToString(Cached["Cache" + Input.Token + "Name"]));
        if (refresh) {
            try {
                if (Data.IsCanceled) throw new OperationCanceledException();
                McLoginRequestValidate(Data);
                goto LoginFinish;
            } catch (Exception ex) {
                Logger.Warn(ex, "验证登录失败");
                if (ex.IsBadNetwork()) throw new Exception("$登录失败：你的网络环境不佳，导致难以连接到海外服务器。\r\n请稍后再试，或使用加速器、VPN 改善网络环境。");
            }
            Data.Progress = 0.25;
        }
        while (true) {
            if (refresh) {
                try {
                    if (Data.IsCanceled) throw new OperationCanceledException();
                    McLoginRequestRefresh(Data, NeedRefresh);
                    goto LoginFinish;
                } catch (Exception ex) {
                    McLaunchLog("刷新登录失败：" + ex.GetDisplay(true));
                    if (WasRefreshed) throw new Exception("二轮刷新登录失败", ex);
                }
                Data.Progress = NeedRefresh ? 0.85 : 0.45;
            }
            try {
                if (Data.IsCanceled) throw new OperationCanceledException();
                NeedRefresh = McLoginRequestLogin(Data);
            } catch (Exception ex) { McLaunchLog("登录失败：" + ex.GetDisplay(true)); throw; }
            if (!NeedRefresh) break;
            McLaunchLog("重新进行刷新登录"); WasRefreshed = true; Data.Progress = 0.65; refresh = true;
        }
    LoginFinish:
        Data.Progress = 0.95;
        try { host.UpdateServerAccounts(Input.Token, Input.UserName, Input.Password, false); }
        catch { Logger.Error("保存第三方账号记录失败，原有记录已保留。", LogBehavior.Toast); }
    }

private static bool CanReuseAuthCache(TServer input, Dictionary<string, object> cached) =>
    input.Token != "Auth" || AuthlibEndpoint.Same(input.BaseUrl, cached.GetValueOrDefault(AuthlibEndpoint.CredentialServerSetting) as string);

private Dictionary<string, object> ReadBoundServerCache(TServer input) {
    var cached = host.AccountSnapshot("Cache" + input.Token);
    if (!CanReuseAuthCache(input, cached)) throw new InvalidOperationException("认证服务器已改变，请重新登录当前服务器。");
    return cached;
}

private void SaveServerLogin(LegacyLaunchTask<TServer, ILegacyLaunchLoginResult> data) {
    string prefix = "Cache" + data.Input.Token;
    var values = new Dictionary<string, object> {
        [prefix + "Access"] = data.Output.AccessToken, [prefix + "Client"] = data.Output.ClientToken,
        [prefix + "Uuid"] = data.Output.Uuid, [prefix + "Name"] = data.Output.Name,
        [prefix + "Username"] = data.Input.UserName, [prefix + "Pass"] = data.Input.Password
    };
    if (data.Input.Token == "Auth") values[AuthlibEndpoint.CredentialServerSetting] = data.Input.BaseUrl;
    host.SaveAccountGroup(values, () => data.IsCanceled);
}

public void McLoginLegacyStart(LegacyLaunchTask<TOffline, ILegacyLaunchLoginResult> Data)
    {
        var Input = Data.Input;
        McLaunchLog("登录方式：离线（" + Input.UserName + "）");
        Data.Progress = 0.1d;
        {
            var withBlock = Data.Output;
            withBlock.Name = Input.UserName;
            withBlock.Uuid = McLoginLegacyUuidWithCustomSkin(Input.UserName, Input.SkinType, Input.SkinName);
            withBlock.Type = "Legacy";
        }
        // 将结果扩展到所有项目中
        Data.Output.AccessToken = Data.Output.Uuid;
        Data.Output.ClientToken = Data.Output.Uuid;
        // 保存启动记录
        host.UpdateLegacyNames(Names =>
        {
            Names.RemoveAll(Name => Name.Equals(Input.UserName, StringComparison.OrdinalIgnoreCase));
            Names.Insert(0, Input.UserName);
        });
    }


public void McLoginRequestValidate(LegacyLaunchTask<TServer, ILegacyLaunchLoginResult> Data)
    {
        McLaunchLog("验证登录开始（Validate, " + Data.Input.Token + "）");
        // 提前缓存信息，否则如果在登录请求过程中退出登录，设置项目会被清空，导致输出存在空值
        var Cached = ReadBoundServerCache(Data.Input);
        string AccessToken = Conversions.ToString(Cached["Cache" + Data.Input.Token + "Access"]);
        string ClientToken = Conversions.ToString(Cached["Cache" + Data.Input.Token + "Client"]);
        string Uuid = Conversions.ToString(Cached["Cache" + Data.Input.Token + "Uuid"]);
        string Name = Conversions.ToString(Cached["Cache" + Data.Input.Token + "Name"]);
        // 发送登录请求
        var RequestData = new JObject(new JProperty("accessToken", AccessToken), new JProperty("clientToken", ClientToken), new JProperty("requestUser", true));
        SendRequest(Data.Input.BaseUrl + "/validate", HttpMethod.Post, Content: RequestData.ToString(Newtonsoft.Json.Formatting.None), Headers: new[,] { { "Accept-Language", "zh-CN" } }, ContentType: "application/json; charset=utf-8"); // 没有返回值的
        // 将登录结果输出
        Data.Output.AccessToken = AccessToken;
        Data.Output.ClientToken = ClientToken;
        Data.Output.Uuid = Uuid;
        Data.Output.Name = Name;
        Data.Output.Type = Data.Input.Token;
        // 不更改缓存，直接结束
        McLaunchLog("验证登录成功（Validate, " + Data.Input.Token + "）");
    }


public void McLoginRequestRefresh(LegacyLaunchTask<TServer, ILegacyLaunchLoginResult> Data, bool RequestUser)
    {
        McLaunchLog("刷新登录开始（Refresh, " + Data.Input.Token + "）");
        var Cached = ReadBoundServerCache(Data.Input);
        JObject LoginJson = (JObject)SendRequest(Data.Input.BaseUrl + "/refresh", HttpMethod.Post, Content: new JObject(new JProperty("selectedProfile", new JObject(new JProperty("name", Conversions.ToString(Cached["Cache" + Data.Input.Token + "Name"])), new JProperty("id", Conversions.ToString(Cached["Cache" + Data.Input.Token + "Uuid"])))), new JProperty("accessToken", Conversions.ToString(Cached["Cache" + Data.Input.Token + "Access"])), new JProperty("requestUser", true)).ToString(Newtonsoft.Json.Formatting.None), Headers: new[,] { { "Accept-Language", "zh-CN" } }, ContentType: "application/json; charset=utf-8", RequireJson: true).DeserializeJson();
        // 将登录结果输出
        if (LoginJson["selectedProfile"] is null)
            throw new Exception("选择的角色 " + Conversions.ToString(Cached["Cache" + Data.Input.Token + "Name"]) + " 无效！");
        Data.Output.AccessToken = LoginJson["accessToken"].ToString();
        Data.Output.ClientToken = LoginJson["clientToken"].ToString();
        Data.Output.Uuid = LoginJson["selectedProfile"]["id"].ToString();
        Data.Output.Name = LoginJson["selectedProfile"]["name"].ToString();
        Data.Output.Type = Data.Input.Token;
        // 保存缓存
        SaveServerLogin(Data);
        McLaunchLog("刷新登录成功（Refresh, " + Data.Input.Token + "）");
    }


public bool McLoginRequestLogin(LegacyLaunchTask<TServer, ILegacyLaunchLoginResult> Data)
    {
        try
        {
            bool NeedRefresh = false;
            McLaunchLog("登录开始（Login, " + Data.Input.Token + "）");
            var RequestData = new JObject(new JProperty("agent", new JObject(new JProperty("name", "Minecraft"), new JProperty("version", 1))), new JProperty("username", Data.Input.UserName), new JProperty("password", Data.Input.Password), new JProperty("requestUser", true));
            JObject LoginJson = (JObject)SendRequest(Data.Input.BaseUrl + "/authenticate", HttpMethod.Post, Content: RequestData.ToString(Newtonsoft.Json.Formatting.None), Headers: new[,] { { "Accept-Language", "zh-CN" } }, ContentType: "application/json; charset=utf-8", RequireJson: true).DeserializeJson();
            // 检查登录结果
            if (LoginJson["availableProfiles"].Count() == 0)
            {
                if (Data.Input.ForceReselectProfile)
                    Hint("你还没有创建角色，无法更换！", LegacyLaunchHint.Red);
                throw new Exception("$你还没有创建角色，请在创建角色后再试！");
            }
            else if (Data.Input.ForceReselectProfile && LoginJson["availableProfiles"].IsSingle())
            {
                Hint("你的账户中只有一个角色，无法更换！", LegacyLaunchHint.Red);
            }
            string SelectedName = null;
            string SelectedId = null;
            if ((LoginJson["selectedProfile"] is null || Data.Input.ForceReselectProfile) && LoginJson["availableProfiles"].Count() > 1)
            {
                // 要求选择档案；优先从缓存读取
                NeedRefresh = true;
                var cached = host.AccountSnapshot("Cache" + Data.Input.Token);
                string CacheId = CanReuseAuthCache(Data.Input, cached) ? cached.GetValueOrDefault("Cache" + Data.Input.Token + "Uuid") as string : null;
                foreach (var Profile in LoginJson["availableProfiles"])
                {
                    if ((Profile["id"].ToString() ?? "") == (CacheId ?? ""))
                    {
                        SelectedName = Profile["name"].ToString();
                        SelectedId = Profile["id"].ToString();
                        McLaunchLog("根据缓存选择的角色：" + SelectedName);
                    }
                }
                // 缓存无效，要求玩家选择
                if (SelectedName is null)
                {
                    McLaunchLog("要求玩家选择角色");
                    host.DispatchWait(() =>
{
    var SelectionJson = LoginJson["availableProfiles"].ToList();
    int SelectedIndex = host.SelectProfile((JArray)LoginJson["availableProfiles"]);
    SelectedName = SelectionJson[SelectedIndex]["name"].ToString();
    SelectedId = SelectionJson[SelectedIndex]["id"].ToString();
});
                    McLaunchLog("玩家选择的角色：" + SelectedName);
                }
            }
            else
            {
                SelectedName = LoginJson["selectedProfile"]["name"].ToString();
                SelectedId = LoginJson["selectedProfile"]["id"].ToString();
            }
            // 将登录结果输出
            Data.Output.AccessToken = LoginJson["accessToken"].ToString();
            Data.Output.ClientToken = LoginJson["clientToken"].ToString();
            Data.Output.Name = SelectedName;
            Data.Output.Uuid = SelectedId;
            Data.Output.Type = Data.Input.Token;
            // 保存缓存
            SaveServerLogin(Data);
            McLaunchLog("登录成功（Login, " + Data.Input.Token + "）");
            return NeedRefresh;
        }
        catch (Exception ex)
        {
            string AllMessage = ex.GetDisplay(false);
            Logger.Info(ex, "登录失败原始错误信息");
            // 读取服务器返回的错误
            if (ex is HttpRequestCodeException)
            {
                string ErrorMessage = null;
                try
                {
                    string Response = ((HttpRequestCodeException)ex).Response;
                    if (Response.ContainsIgnoreCase("errorMessage"))
                        ErrorMessage = Response.DeserializeJson()["errorMessage"].ToString();
                }
                catch
                {
                }
                if (!string.IsNullOrWhiteSpace(ErrorMessage))
                {
                    if (ErrorMessage.Contains("密码错误") || ErrorMessage.ContainsIgnoreCase("Incorrect username or password"))
                    {
                        // 密码错误，退出登录 (#5090)
                        McLaunchLog("密码错误，退出登录");
                        switch (((LegacyLaunchLoginType)Data.Input.TypeCode))
                        {
                            case LegacyLaunchLoginType.Auth:
                                {
                                    host.Dispatch(() => host.ExitServerLogin(3));
                                    break;
                                }
                            case LegacyLaunchLoginType.Nide:
                                {
                                    host.Dispatch(() => host.ExitServerLogin(2));
                                    break;
                                }
                        }
                    }
                    throw new Exception("$登录失败：" + ErrorMessage);
                }
            }
            // 通用关键字检测
            if (AllMessage.Contains("(403)"))
            {
                switch (((LegacyLaunchLoginType)Data.Input.TypeCode))
                {
                    case LegacyLaunchLoginType.Auth:
                        {
                            throw new Exception("$登录失败，以下为可能的原因：" + Constants.vbCrLf + " - 输入的账号或密码错误。" + Constants.vbCrLf + " - 登录尝试过于频繁，导致被暂时屏蔽。请不要操作，等待 10 分钟后再试。" + Constants.vbCrLf + " - 只注册了账号，但没有在皮肤站新建角色。");
                        }
                    case LegacyLaunchLoginType.Nide:
                        {
                            throw new Exception("$登录失败，以下为可能的原因：" + Constants.vbCrLf + " - 输入的账号或密码错误。" + Constants.vbCrLf + " - 密码错误次数过多，导致被暂时屏蔽。请不要操作，等待 10 分钟后再试。" + Constants.vbCrLf + (Data.Input.UserName.Contains("@") ? "" : " - 登录账号应为邮箱或统一通行证账号，而非游戏角色 ID。" + Constants.vbCrLf) + " - 只注册了账号，但没有加入对应服务器。");
                        }
                }
            }
            else if (ex.IsBadNetwork())
            {
                throw new Exception("$登录失败：你的网络环境不佳，导致难以连接到海外服务器。" + Constants.vbCrLf + "请稍后再试，或使用加速器、VPN 改善网络环境。");
            }
            else if (ex.Message.StartsWithF("$"))
            {
                throw;
            }
            else
            {
                throw new Exception("登录失败：" + ex.Message, ex);
            }
            return false;
        }
    }

    public void McLoginStart(LegacyLaunchTask<TData, ILegacyLaunchLoginResult> data) {
        McLaunchLog("登录加载已开始");
        string check = McLoginAble(data.Input);
        if (!string.IsNullOrEmpty(check)) throw new ArgumentException(check);
        switch ((LegacyLaunchLoginType)data.Input.TypeCode) {
            case LegacyLaunchLoginType.Ms: {
                var loader = host.MicrosoftLoader(); loader.WaitForExit((TMicrosoft)data.Input, McLoginLoader, data.IsForceRestarting); data.Output = loader.Output; break;
            }
            case LegacyLaunchLoginType.Legacy: {
                var loader = host.OfflineLoader(); loader.WaitForExit((TOffline)data.Input, McLoginLoader, data.IsForceRestarting); data.Output = loader.Output; break;
            }
            case LegacyLaunchLoginType.Nide: case LegacyLaunchLoginType.Auth: {
                var loader = host.ServerLoader(data.Input.TypeCode); loader.WaitForExit((TServer)data.Input, McLoginLoader, data.IsForceRestarting); data.Output = loader.Output; break;
            }
            default: throw new NullReferenceException("未获取登录加载器。");
        }
        host.Dispatch(host.RefreshLoginPage);
        McLaunchLog("登录加载已结束");
    }

}
