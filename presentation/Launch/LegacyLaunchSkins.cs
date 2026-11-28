using System;
using System.Threading;
using System.IO;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLLF.Presentation.Launch;

public readonly record struct LegacySkinRequest(string Name, string Id, string Server = "", bool UsesSkinSite = false);

// LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 PageLaunchLeft.xaml.vb;
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 is the source baseline.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Pages/PageLaunch/PageLaunchLeft.xaml.cs
// and PageLaunchRight.xaml.cs were reviewed for C# structure (CE Apache LICENSE /
// PCL LICENCE). CE profiles, localization, custom-home live watchers and revised
// decisions are not imported. LF repository LICENCE applies.
public sealed class LegacyLaunchSkinHost<TTask> {
    public Func<string, string> ReadString = null!;
    public Func<string, int> ReadInteger = null!;
    public Action<string, object> WriteSetting = null!;
    public Func<string, string?>? OfflineLibrarySkin;
    public Func<string, LegacySkinRequest> AccountInput = null!;
    public Func<TTask, LegacySkinRequest> Input = null!;
    public Action<TTask, string> Output = null!;
    public Func<TTask, Thread, bool> Canceled = null!;
    public Func<string, LegacySkinRequest, string, bool, Func<bool>, bool>? PublishAvatar;
    public Func<string> ImagePath = null!, AppDataPath = null!;
    public Func<string, string> OfflineUuid = null!, SkinSex = null!, DownloadSkin = null!;
    public Func<string, LegacySkinRequest, string> SkinAddress = null!;
    public Func<string, bool, string> MojangUuid = null!;
    public Action<string> Hint = null!;
}
public sealed class LegacyLaunchSkins<TTask> {
    private readonly LegacyLaunchSkinHost<TTask> host;
    public LegacyLaunchSkins(LegacyLaunchSkinHost<TTask> host) => this.host = host;
    public LegacySkinRequest Input(string Kind) => Kind == "Legacy" ? OfflineInput() : host.AccountInput(Kind);
    public string OfflineAccountName() => host.ReadString("LoginLegacyName").BeforeFirst("¨").Replace("¨", "").Trim();
    private LegacySkinRequest OfflineInput() {
        string player = OfflineAccountName();
        if (host.OfflineLibrarySkin?.Invoke(player) is { } file) return new("4", file);
        // Offline skins come from the skin library. Without one, the head is the default skin the game itself shows for
        // this player (by UUID); of the retired offline-skin modes only Steve / Alex still match the game (they pick
        // the UUID), so "official skin by name" and "custom file" no longer change the head.
        int kind = host.ReadInteger("LaunchSkinType");
        return kind is 1 or 2 ? new(kind.ToString(), "") : new("0", player);
    }
    private string DefaultSkin(string user) => host.ImagePath() + "Skins/" + host.SkinSex(host.OfflineUuid(user)) + ".png";
    public string OfflineDefaultSkin(string player) => host.ReadInteger("LaunchSkinType") switch {
        1 => host.ImagePath() + "Skins/Steve.png",
        2 => host.ImagePath() + "Skins/Alex.png",
        _ => DefaultSkin(player)
    };
    public void Official(TTask Data, string Kind) {
        Thread worker = Thread.CurrentThread;
        LegacySkinRequest request = host.Input(Data);
        string user = request.Name;
        string fallback = Kind == "Auth" ? host.ImagePath() + "Skins/Steve.png" : DefaultSkin(user);
        string path;
        bool fetched = false;
        string description = Kind == "Ms" ? "微软正版" : Kind == "Nide" ? "统一通行证" : " Authlib-Injector";
        if (string.IsNullOrEmpty(user)) {
            Logger.Info("获取" + description + "皮肤失败，ID 为空");
            Finish(Data, Kind, request, fallback, worker);
            return;
        }
        try {
            string address = host.SkinAddress(Kind, request);
            if (!IsCurrent(Data, request, worker)) return;
            path = host.DownloadSkin(address);
            fetched = true;
        } catch (Exception error) {
            if (error.IsCanceled() || !IsCurrent(Data, request, worker)) return;
            path = Kind == "Ms" ? fallback : host.ImagePath() + "Skins/Steve.png";
            if (error.GetDisplay(false).Contains("(429)")) {
                Logger.Error("获取" + (Kind == "Ms" ? "正版" : description) + $"皮肤失败（{user}）：获取皮肤太过频繁，请 5 分钟后再试！", LogBehavior.Toast);
            } else if (error.GetDisplay(false).Contains("未设置自定义皮肤")) {
                Logger.Info("用户未设置自定义皮肤，跳过皮肤加载");
            } else Logger.Error(error, "获取" + description + $"皮肤失败（{user}）", LogBehavior.Toast);
        }
        Finish(Data, Kind, request, path, worker, fetched);
    }

    private bool IsCurrent(TTask task, LegacySkinRequest request, Thread worker) => !host.Canceled(task, worker) && host.Input(task) == request;

    private void Finish(TTask task, string kind, LegacySkinRequest request, string path, Thread worker, bool fetched = false) {
        if (!IsCurrent(task, request, worker)) return;
        host.Output(task, path);
        host.PublishAvatar?.Invoke(kind, request, path, fetched, () => IsCurrent(task, request, worker));
    }
    public void Offline(TTask Data) {
        Thread worker = Thread.CurrentThread;
        LegacySkinRequest request = host.Input(Data);
        string path;
        bool fetched = false;
        switch (Conversions.ToInteger(request.Name)) {
            case 0: path = DefaultSkin(request.Id); break;
            case 1:
            UseDefault:
                path = host.ImagePath() + "Skins/Steve.png"; break;
            case 2: path = host.ImagePath() + "Skins/Alex.png"; break;
            case 3:
                string id = request.Id;
                if (string.IsNullOrEmpty(id) || id.Length < 2) goto case 1;
                try {
                    string result = host.MojangUuid(id, true);
                    if (!IsCurrent(Data, request, worker)) return;
                    result = host.SkinAddress("Mojang", request with { Id = result });
                    if (!IsCurrent(Data, request, worker)) return;
                    path = host.DownloadSkin(result);
                    fetched = true;
                } catch (Exception error) {
                    if (error.IsCanceled() || !IsCurrent(Data, request, worker)) return;
                    path = DefaultSkin(id);
                    if (error.GetDisplay(false).Contains("(429)")) Logger.Info($"获取离线登录使用的正版皮肤失败（{id}）：获取皮肤太过频繁，请 5 分钟后再试！");
                    else Logger.Warn(error, $"获取离线登录使用的正版皮肤失败（{id}）");
                }
                break;
            case 4:
                if (!string.IsNullOrEmpty(request.Id)) { path = File.Exists(request.Id) ? request.Id : host.ImagePath() + "Skins/Steve.png"; break; }
                if (!FileUtils.Exists(host.AppDataPath() + "CustomSkin.png")) {
                    host.Hint("未找到离线皮肤自定义文件，可能它已被删除。PCL 将使用默认的 Steve 皮肤！"); host.WriteSetting("LaunchSkinType", 1); goto UseDefault;
                }
                path = host.AppDataPath() + "CustomSkin.png"; break;
            default: goto case 1;
        }
        Finish(Data, "Legacy", request, path, worker, fetched);
    }
}
