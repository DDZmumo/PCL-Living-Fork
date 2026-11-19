using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Accounts;

public sealed class LegacyCapeChoice { public string Text = null!; public bool Checked; }
public sealed class LegacySkinHost<TLegacy, TMs, TServer, TLoader> where TLoader : class {
    public LegacyLoginHost<TLegacy, TMs, TServer> Accounts = null!;
    public Func<TLoader, LegacyLoginTaskPort> Task = null!;
    public Func<TLoader, string?> Output = null!;
    public Func<TLoader, string> Name = null!;
    public Func<IEnumerable<TLoader>> SkinLoaders = null!;
    public Func<TLoader> LegacyLoader = null!, MicrosoftLoader = null!;
    public Func<string> ImagePath = null!, TempPath = null!;
    public Func<string, LegacyBitmap> CreateBitmap = null!;
    public Func<string, string, string?> SaveSkinPath = null!;
    public Action<string> ClearIniCache = null!;
    public Action<string, string, string> WriteIni = null!;
    public Func<TMs> MsLoggedInData = null!;
    public Func<IReadOnlyList<LegacyCapeChoice>, int?> SelectCape = null!;
}
public sealed class LegacySkinView<TLoader> where TLoader : class {
    public Func<TLoader> Loader = null!;
    public Func<FrameworkElement> Control = null!;
    public Func<double> Scale = null!, ShadowOpacity = null!;
    public Action<double, int, int> AnimateShadow = null!;
    public Action<string> SetToolTip = null!;
    public Action<object?> SetBackTag = null!;
    public Action<ImageSource?> SetFore = null!, SetBack = null!;
    public Func<Visibility> CapeVisibility = null!;
    public Action<Visibility> SetCapeVisibility = null!;
    public Action<bool> SetSaveEnabled = null!;
    public Action<object, MouseButtonEventArgs> Click = null!;
}
public sealed class LegacySkinServices<TLegacy, TMs, TServer, TLoader> where TLoader : class {
    internal readonly LegacySkinHost<TLegacy, TMs, TServer, TLoader> Host;
    public LegacySkinServices(LegacySkinHost<TLegacy, TMs, TServer, TLoader> host) => Host = host;
    public void Save(TLoader Loader) {
        string? address = Host.Output(Loader);
        LegacyLoginTaskPort task = Host.Task(Loader);
        if (task.State() != LegacyLoginTaskState.Finished) {
            Host.Accounts.Hint("皮肤正在获取中，请稍候！", LegacyLoginHint.Red);
            if (task.State() != LegacyLoginTaskState.Loading) task.Start(null, false);
            return;
        }
        try {
            string? destination = Host.SaveSkinPath("选取保存皮肤的位置", PathUtils.GetLastPart(address!));
            if (destination is null) return;
            if (destination.Contains("\\")) {
                FileUtils.Delete(destination);
                if (address!.StartsWithF(Host.ImagePath())) Host.CreateBitmap(address!).Save(destination);
                else FileUtils.Copy(address!, destination);
                Host.Accounts.Hint("皮肤保存成功！", LegacyLoginHint.Green);
            }
        } catch (Exception error) { Logger.Error(error, "保存皮肤失败", LogBehavior.Toast); }
    }
    public void RefreshCache(TLoader? sender = null) {
        bool running = false;
        foreach (TLoader loader in Host.SkinLoaders()) if (Host.Task(loader).State() == LegacyLoginTaskState.Loading) { running = true; break; }
        if (Host.Accounts.HasLaunchPage() && running) Host.Accounts.Hint("有正在获取中的皮肤，请稍后再试！", LegacyLoginHint.Blue);
        else Host.Accounts.RunInThread(() => {
            try {
                Host.Accounts.Hint("正在刷新皮肤……", LegacyLoginHint.Default); Logger.Info("正在清空皮肤缓存");
                if (DirectoryUtils.Exists(Host.TempPath() + @"Cache\Skin")) DirectoryUtils.Delete(Host.TempPath() + @"Cache\Skin");
                if (DirectoryUtils.Exists(Host.TempPath() + @"Cache\Uuid")) DirectoryUtils.Delete(Host.TempPath() + @"Cache\Uuid");
                Host.ClearIniCache(Host.TempPath() + @"Cache\Skin\IndexMs.ini"); Host.ClearIniCache(Host.TempPath() + @"Cache\Skin\IndexNide.ini"); Host.ClearIniCache(Host.TempPath() + @"Cache\Skin\IndexAuth.ini"); Host.ClearIniCache(Host.TempPath() + @"Cache\Uuid\Mojang.ini");
                foreach (TLoader loader in sender is not null ? new[] { sender } : new[] { Host.LegacyLoader(), Host.MicrosoftLoader() }) Host.Task(loader).Wait(null, true);
                Host.Accounts.Hint("已刷新皮肤！", LegacyLoginHint.Green);
            } catch (Exception error) { Logger.Error(error, "刷新皮肤缓存失败", LogBehavior.Alert); }
        });
    }
    public void ReloadCache(string SkinAddress) => Host.Accounts.RunInThread(() => {
        try {
            Host.WriteIni(Host.TempPath() + @"Cache\Skin\IndexMs.ini", Host.Accounts.ReadString("CacheMsV2Uuid"), SkinAddress);
            Logger.Info($"已写入皮肤地址缓存 {Host.Accounts.ReadString("CacheMsV2Uuid")} -> {SkinAddress}");
            foreach (TLoader loader in new[] { Host.MicrosoftLoader(), Host.LegacyLoader() }) Host.Task(loader).Wait(null, true);
            Host.Accounts.Hint("更改皮肤成功！", LegacyLoginHint.Green);
        } catch (Exception error) { Logger.Error(error, "更改正版皮肤后刷新皮肤失败"); }
    });
}
public sealed class LegacySkinControl<TLegacy, TMs, TServer, TLoader> where TLoader : class {
    private readonly LegacySkinServices<TLegacy, TMs, TServer, TLoader> services;
    private readonly LegacySkinView<TLoader> view;
    private LegacySkinHost<TLegacy, TMs, TServer, TLoader> Host => services.Host;
    private LegacyLoginHost<TLegacy, TMs, TServer> Accounts => Host.Accounts;
    private ControlAnimations Animations => ControlServices.Current.Animations;
    private string? address;
    private bool isMouseDown, isChanging;
    public LegacySkinControl(LegacySkinServices<TLegacy, TMs, TServer, TLoader> services, LegacySkinView<TLoader> view) { this.services = services; this.view = view; }
    public string? Address { get => address; set { address = value; view.SetToolTip(string.IsNullOrEmpty(address) ? "加载中" : "点击更换皮肤（右键查看更多选项）"); } }
    public bool HasCape { get => view.CapeVisibility() == Visibility.Collapsed; set => view.SetCapeVisibility(value ? Visibility.Visible : Visibility.Collapsed); }
    public void MouseEnter() => view.AnimateShadow(0.8 - view.ShadowOpacity(), 200, 100);
    public void MouseLeave() { view.AnimateShadow(0.2 - view.ShadowOpacity(), 200, 0); isMouseDown = false; ResetScale(1); }
    private void ResetScale(double target) => Animations.Start(new[] { Animations.ScaleTransform(view.Control(), target - view.Scale(), new(60, ease: ControlEase.OutFluent)) }, "Skin Scale");
    public void MouseDown() { isMouseDown = true; ResetScale(0.9); }
    public void MouseUp(object sender, MouseButtonEventArgs e) { ResetScale(1); if (isMouseDown) { isMouseDown = false; view.Click(sender, e); } }
    public void Save() => services.Save(view.Loader());
    public void SaveChecked() => view.SetSaveEnabled(string.IsNullOrEmpty(Address));
    public void RefreshClick() => services.RefreshCache(view.Loader());
    public void Load() => Load(Host.Output(view.Loader()));
    public bool Load(string? path) {
        Clear();
        try {
            if (string.IsNullOrEmpty(path)) throw new Exception("皮肤加载器 " + Host.Name(view.Loader()) + " 没有输出");
            if (!path.StartsWithF(Host.ImagePath()) && !FileUtils.Exists(path)) throw new FileNotFoundException("皮肤文件未找到", path);
            LegacyBitmap image;
            try { image = Host.CreateBitmap(path); }
            catch (Exception error) { Logger.Error(error, $"皮肤文件已损坏：{path}", LogBehavior.Toast); FileUtils.Delete(path); return false; }
            int scale = Conversions.ToInteger(image.Pic!.Width / 64d);
            if (image.Pic.Width < 32 || image.Pic.Height < 32) throw new Exception("图片大小不足，长为 " + image.Pic.Height + "，宽为 " + image.Pic.Width);
            ImageSource? fore = null;
            if (image.Pic.Width >= 64 && image.Pic.Height >= 32) {
                if ((image.Pic.GetPixel(1, 1).A == 0 || image.Pic.GetPixel(image.Pic.Width - 1, image.Pic.Height - 1).A == 0 || image.Pic.GetPixel(image.Pic.Width - 2, Conversions.ToInteger(image.Pic.Height / 2d - 2)).A == 0) ||
                    (image.Pic.GetPixel(1, 1) != image.Pic.GetPixel(scale * 41, scale * 9) && image.Pic.GetPixel(image.Pic.Width - 1, image.Pic.Height - 1) != image.Pic.GetPixel(scale * 41, scale * 9) && image.Pic.GetPixel(image.Pic.Width - 2, Conversions.ToInteger(image.Pic.Height / 2d - 2)) != image.Pic.GetPixel(scale * 41, scale * 9))) fore = image.Clip(scale * 40, scale * 8, scale * 8, scale * 8).ToImageSource();
            }
            ImageSource back = image.Clip(scale * 8, scale * 8, scale * 8, scale * 8).ToImageSource();
            view.SetFore(fore); view.SetBack(back);
            Address = path;
            view.SetBackTag(path);
            Logger.Info($"载入头像成功：{Host.Name(view.Loader())}");
            return true;
        } catch (Exception error) { Logger.Error(error, $"载入头像失败（{path ?? "null"},{Host.Name(view.Loader())}）", LogBehavior.Toast); return false; }
    }
    public void Clear() { Address = ""; view.SetBackTag(null); view.SetFore(null); view.SetBack(null); }
    public void ChangeCape() {
        if (isChanging) { Accounts.Hint("正在更改披风中，请稍候！", LegacyLoginHint.Default); return; }
        if (Accounts.MsLogin.State() == LegacyLoginTaskState.Failed) { Accounts.Hint("登录失败，无法更改披风！", LegacyLoginHint.Red); return; }
        if (Accounts.MsLogin.State() != LegacyLoginTaskState.Finished) Accounts.Hint("正在获取披风列表，请稍候……", LegacyLoginHint.Default);
        isChanging = true;
        Accounts.RunInNewThread(() => {
        Retry:
            try {
                if (Accounts.MsLogin.State() != LegacyLoginTaskState.Finished) Accounts.MsLogin.Wait(Host.MsLoggedInData(), false);
                if (Accounts.MsLogin.State() != LegacyLoginTaskState.Finished) { Accounts.Hint("登录失败，无法更改披风！", LegacyLoginHint.Red); return; }
                string token = Accounts.MsAccessToken(); string uuid = Accounts.MsUuid(); JObject data = (JObject)Accounts.MsProfileJson().DeserializeJson()!;
                int? selected = null;
                Accounts.RunInUiWait(() => {
                    try {
                        Dictionary<string, string> names = CapeNames();
                        List<LegacyCapeChoice> choices = new() { new() { Text = "无披风", Checked = !data["capes"]!.Any(c => c["state"]?.ToString() == "ACTIVE") } };
                        foreach (JToken cape in data["capes"]!) { string name = cape["alias"]!.ToString(); if (names.ContainsKey(name)) name = names[name]; choices.Add(new LegacyCapeChoice { Text = name, Checked = cape["state"]?.ToString() == "ACTIVE" }); }
                        selected = Host.SelectCape(choices);
                    } catch (Exception error) { Logger.Error(error, "获取玩家皮肤列表失败"); }
                });
                if (selected is null) return;
                string result = Accounts.RequestRetry("https://api.minecraftservices.com/minecraft/profile/capes/active", selected == 0 ? HttpMethod.Delete : HttpMethod.Put,
                    selected == 0 ? "" : new JObject(new JProperty("capeId", data["capes"]![selected.Value - 1]!["id"])).ToString(Formatting.None), "application/json", new Dictionary<string, string> { ["Authorization"] = "Bearer " + token });
                if (result.Contains("\"errorMessage\"")) { Accounts.Hint("更改披风失败：" + result.DeserializeJson()!["errorMessage"]!.ToString(), LegacyLoginHint.Red); return; }
                else {
                    Accounts.Hint("更改披风成功！", LegacyLoginHint.Green);
                    foreach (JToken cape in data["capes"]!) cape["state"] = "INACTIVE";
                    if (selected > 0) data["capes"]![selected.Value - 1]!["state"] = "ACTIVE";
                    Accounts.SetMsProfileJson(data.ToString()); Accounts.WriteSetting("CacheMsV2ProfileJson", Accounts.MsProfileJson());
                }
            } catch (Exception error) {
                if (error is HttpRequestCodeException request) {
                    if (request.StatusCode == HttpStatusCode.BadRequest) {
                        Logger.Warn(error, "更改披风时遭遇 400 错误");
                        if (request.Response?.Contains("\"error\"") == true) { Accounts.Hint("更改披风失败：" + request.Response.DeserializeJson()!["error"]!.ToString(), LegacyLoginHint.Red); return; }
                        else if (request.Response?.Contains("\"errorMessage\"") == true) { Accounts.Hint("更改披风失败：" + request.Response.DeserializeJson()!["errorMessage"]!.ToString(), LegacyLoginHint.Red); return; }
                    } else if (request.StatusCode == HttpStatusCode.Unauthorized) { Logger.Warn(error, "更改披风时遭遇 401 错误"); Accounts.Hint("正在重新登录，将在登录后自动更改披风……", LegacyLoginHint.Default); Accounts.MsLogin.Start(Host.MsLoggedInData(), true); goto Retry; }
                } else if (error.IsBadNetwork()) Accounts.Hint("更改披风失败：连接 Mojang 服务器超时，请稍后再试，或使用 VPN 改善网络环境", LegacyLoginHint.Red);
                else Logger.Error(error, "更改披风失败", LogBehavior.Toast);
            } finally { isChanging = false; }
        }, "Cape Change");
    }
    private static Dictionary<string, string> CapeNames() => new() {
        ["Migrator"] = "迁移者披风", ["MapMaker"] = "Realms 地图制作者披风", ["Moderator"] = "Mojira 管理员披风", ["Translator-Chinese"] = "Crowdin 中文翻译者披风", ["Translator"] = "Crowdin 翻译者披风", ["Cobalt"] = "Cobalt 披风", ["Vanilla"] = "原版披风", ["Minecon2011"] = "Minecon 2011 参与者披风", ["Minecon2012"] = "Minecon 2012 参与者披风", ["Minecon2013"] = "Minecon 2013 参与者披风", ["Minecon2015"] = "Minecon 2015 参与者披风", ["Minecon2016"] = "Minecon 2016 参与者披风", ["Cherry Blossom"] = "樱花披风", ["15th Anniversary"] = "15 周年纪念披风", ["Purple Heart"] = "紫色心形披风", ["Follower's"] = "追随者披风", ["MCC 15th Year"] = "MCC 15 周年披风", ["Minecraft Experience"] = "村民救援披风", ["Mojang Office"] = "Mojang 办公室披风", ["Home"] = "家园披风", ["Menace"] = "入侵披风", ["Yearn"] = "渴望披风", ["Common"] = "普通披风", ["Pan"] = "薄煎饼披风", ["Founder's"] = "创始人披风", ["Copper"] = "铜披风", ["Zombie Horse"] = "僵尸马披风", ["Builder"] = "建造者披风", ["Crafter"] = "工匠披风", ["Aurora"] = "极光披风"
    };
}
