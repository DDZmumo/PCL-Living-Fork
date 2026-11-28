using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Accounts;

public sealed class LegacyDeviceLoginHost<TConverter> {
    public Func<int> NewUuid = null!;
    public Func<TConverter, JObject> Content = null!;
    public Func<TConverter, bool> IsExited = null!, IsWarn = null!;
    public Action<TConverter, bool> SetExited = null!;
    public Action<TConverter, object> SetResult = null!;
    public Func<Exception> RestartException = null!;
    public Func<string> OAuthClientId = null!;
    public Func<bool> HasWaitingMessages = null!;
    public Func<FrameworkElement> Control = null!, MessagePanel = null!;
    public Func<TranslateTransform> Position = null!;
    public Func<RotateTransform> Rotation = null!;
    public Func<string> Title = null!, Caption = null!;
    public Action<string> SetTitle = null!, SetCaption = null!, SetWebEvent = null!, SetCopyEvent = null!;
    public Func<int, string> ButtonName = null!;
    public Action<int, string> SetButtonName = null!;
    public Action SetLineThickness = null!, RemoveControl = null!, ShowWindowToTop = null!;
    public Action? ClosePresentation;
    public Action<Action> RunInUi = null!;
    public Action<Action, string> RunInNewThread = null!;
    public Action<string> OpenWebsite = null!, ClipboardSet = null!, LaunchLog = null!;
    public Action<string, LegacyLoginHint> Hint = null!;
    public Func<string, HttpMethod, string, string, int, bool, bool, string> Request = null!;
}
public sealed class LegacyDeviceLoginDialog<TConverter> {
    private readonly LegacyDeviceLoginHost<TConverter> host;
    private TConverter converter = default!;
    private JObject data = null!;
    private string userCode = null!, deviceCode = null!, website = null!;
    public LegacyDeviceLoginDialog(LegacyDeviceLoginHost<TConverter> host) { this.host = host; }
    public void Initialize(TConverter Converter) {
        converter = Converter;
        data = host.Content(Converter);
        Init();
    }
    public void CancelClick() => Finished(new OperationCanceledException());
    public static bool ShouldDrag(double PositionY) => PositionY <= 2;
    private void Finished(object Result) {
        if (host.IsExited(converter)) return;
        host.SetExited(converter, true);
        host.SetResult(converter, Result);
        if (host.ClosePresentation is { } close) host.RunInUi(close);
    }
    private void Init() {
        userCode = (string)data["user_code"]!; deviceCode = (string)data["device_code"]!; website = (string)data["verification_uri"]!;
        host.SetTitle("登录 Minecraft");
        host.SetCaption($"登录网页将自动开启，请在网页中输入 {userCode}（已自动复制）。\r\n\r\n如果网络环境不佳，网页可能一直加载不出来，届时请使用加速器或 VPN 改善网络环境。\r\n你也可以用其他设备打开 {website} 并输入上述代码。");
        host.SetWebEvent(website); host.SetCopyEvent(userCode); host.RunInNewThread(WorkThread, "MyMsgLogin");
    }
    private void WorkThread() {
        Thread.Sleep(2000);
        if (host.IsExited(converter)) return;
        host.OpenWebsite(website); host.ClipboardSet(userCode); Thread.Sleep(checked((data["interval"]!.ToObject<int>() - 1) * 1000));
        int failures = 0;
        while (!host.IsExited(converter)) {
            try {
                string result = host.Request("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", HttpMethod.Post,
                    "grant_type=urn:ietf:params:oauth:grant-type:device_code&client_id=" + host.OAuthClientId() + "&device_code=" + deviceCode + "&scope=XboxLive.signin%20offline_access",
                    "application/x-www-form-urlencoded", checked(5000 + failures * 5000), false, true);
                JObject json = (JObject)result.DeserializeJson()!; host.LaunchLog($"令牌过期时间：{json["expires_in"]} 秒"); host.Hint("网页登录成功！", LegacyLoginHint.Green);
                Finished(new[] { json["access_token"]!.ToString(), json["refresh_token"]!.ToString() }); return;
            } catch (Exception error) {
                if (error is HttpRequestCodeException request) {
                    string response = request.Response!;
                    if (response.Contains("authorization_declined")) { Finished(new Exception("$你拒绝了 PCL 申请的权限……")); return; }
                    else if (response.Contains("expired_token")) { Finished(new Exception("$登录用时太长啦，重新试试吧！")); return; }
                    else if (response.Contains("Account security interrupt")) { Finished(new Exception("$该账号由于安全问题无法登陆，请前往微软账户页获取更多信息。")); return; }
                    else if (response.Contains("service abuse")) { Finished(new Exception("$非常抱歉，该账号已被微软封禁，无法登录。")); return; }
                    else if (response.Contains("AADSTS70000")) { Finished(host.RestartException()); return; }
                    else if (response.Contains("authorization_pending")) { Thread.Sleep(2000); continue; }
                }
                if (failures <= 2) { failures = checked(failures + 1); Logger.Warn(error, $"登录轮询第 {failures} 次失败"); Thread.Sleep(2000); }
                else { Finished(new Exception("登录轮询失败", error)); return; }
            }
        }
    }
}
