using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation> {
    public void LoadMinecraftOnFinish() { State.MinecraftVersions = (JArray)host.ClientList()["versions"]!.DeepClone(); host.ShowMinecraftList(); TrySelectWaitingVersion(); }
    public void ShowMinecraftVersionList() {
        ExitSelectPage();
        try {
            Dictionary<string, List<JObject>> groups = new() { ["正式版"] = new(), ["预览版"] = new(), ["远古版"] = new(), ["愚人节版"] = new() };
            foreach (JObject version in State.MinecraftVersions!) {
                string? type = (string?)version["type"];
                switch (type) {
                    case "release": type = "正式版"; break;
                    case "snapshot":
                        type = "预览版";
                        string id = version["id"]!.ToString();
                        if (id.StartsWithF("1.") && !id.Lower().Contains("combat") && !id.Lower().Contains("rc") && !id.Lower().Contains("experimental") && !id.Lower().Contains("pre")) { type = "正式版"; version["type"] = "release"; }
                        switch (version["id"]!.ToString().Lower()) {
                            case "20w14infinite": case "20w14∞": type = "愚人节版"; version["id"] = "20w14∞"; version["type"] = "special"; version.Add("lore", host.FoolName(version["id"]!)); break;
                            case "3d shareware v1.34": case "1.rv-pre1": case "15w14a": case "2.0": case "22w13oneblockatatime": case "23w13a_or_b": case "24w14potato": case "25w14craftmine": case "26w14a":
                                type = "愚人节版"; version["type"] = "special"; version.Add("lore", host.FoolName(version["id"]!)); break;
                            default: DateTime date = version["releaseTime"]!.Value<DateTime>().ToUniversalTime().AddHours(2); if (date.Month == 4 && date.Day == 1) { type = "愚人节版"; version["type"] = "special"; } break;
                        }
                        break;
                    case "special": type = "愚人节版"; break;
                    default: type = "远古版"; break;
                }
                groups[type].Add(version);
            }
            foreach (var pair in groups.ToList()) groups[pair.Key] = pair.Value.OrderByDescending(value => value["releaseTime"]!.Value<DateTime>()).ToList();
            DownloadInstallPageView view = host.View(); view.Minecraft.Children.Clear();
            List<JObject> pinned = new();
            JObject release = (JObject)groups["正式版"][0].DeepClone(); release["lore"] = "最新正式版，发布于 " + release["releaseTime"]!.Value<DateTime>().ToString("yyyy'/'MM'/'dd HH':'mm"); pinned.Add(release);
            if (groups["正式版"][0]["releaseTime"]!.Value<DateTime>() < groups["预览版"][0]["releaseTime"]!.Value<DateTime>()) {
                JObject snapshot = (JObject)groups["预览版"][0].DeepClone(); snapshot["lore"] = "最新预览版，发布于 " + snapshot["releaseTime"]!.Value<DateTime>().ToString("yyyy'/'MM'/'dd HH':'mm"); pinned.Add(snapshot);
            }
            FrameworkElement latest = host.MinecraftCard("最新版本", view.Minecraft, pinned, (row, args) => host.SelectMinecraft(row, args), true);
            view.Minecraft.Children.Insert(0, latest);
            foreach (var pair in groups) if (pair.Value.Any()) view.Minecraft.Children.Add(host.MinecraftCard(pair.Key, view.Minecraft, pair.Value, (row, args) => host.SelectMinecraft(row, args), false));
        } catch (Exception error) { Logger.Error(error, "可视化安装版本列表出错"); }
    }
    public void TrySelectWaitingVersion() {
        if (State.VersionWaitingSelect is null) return;
        foreach (JObject version in State.MinecraftVersions!) if (Same(version["id"]!.ToString(), State.VersionWaitingSelect)) {
            Logger.Info("自动选择 MC 版本：" + State.VersionWaitingSelect); host.SelectMinecraft(host.Materialize(host.MinecraftRow(version, null)), null); State.VersionWaitingSelect = null; return;
        }
    }
    public McComponents GetSelectedComponents() => new(State.VanillaName!, State.SelectedOptiFine is null ? null : JObject.FromObject(State.SelectedOptiFine),
        State.SelectedForge?.VersionName, State.SelectedNeoForge?.VersionName, State.SelectedFabric, State.SelectedFabricApi?.Version ?? State.SelectedFabricApi?.Id ?? State.SelectedFabricApi?.Display,
        State.SelectedOptiFabric?.Version ?? State.SelectedOptiFabric?.Id ?? State.SelectedOptiFabric?.Display, State.SelectedLiteLoader?.JsonToken?["version"]!.ToString());
    public void NameKeyDown(Key key) { if (key == Key.Enter && State.IsInSelectPage && host.View().Start.IsEnabled) StartClick(); }
    public void StartClick() {
        string name = host.View().Name.Text;
        TRequest request = new() { NewInstanceName = name, InstanceFolder = host.MinecraftFolder() + "versions\\" + name + "\\", Components = GetSelectedComponents(),
            MinecraftJson = State.VanillaData!["url"]!.ToString(), ForgeEntry = State.SelectedForge, NeoForgeEntry = State.SelectedNeoForge, FabricApi = State.SelectedFabricApi, OptiFabric = State.SelectedOptiFabric, LiteLoaderEntry = State.SelectedLiteLoader };
        host.StartRequested(request);
    }
    public void StartInstall(TRequest request) {
        if ((State.SelectedForge is not null || State.SelectedNeoForge is not null || State.SelectedFabric is not null) && (host.IsolationSetting() == 0 || host.IsolationSetting() == 2)) {
            if (host.Message(new ModpackMessage { Text = "你尚未开启版本隔离，多个 MC 版本会共用同一个 Mod 文件夹。\r\n因此，游戏可能会因为读取到与当前版本不符的 Mod 而崩溃。\r\n推荐先在 设置 → 启动选项 → 默认版本隔离 中开启版本隔离！", Title = "版本隔离提示", Button1 = "取消下载", Button2 = "继续" }) == 1) return;
        }
        if (!host.Install(request)) return;
        ExitSelectPage();
    }
}
