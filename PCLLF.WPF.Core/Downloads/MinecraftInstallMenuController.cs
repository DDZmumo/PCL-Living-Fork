using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class MinecraftInstallMenuController<TView, TVirtual, TClick, TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource>
    where TView : FrameworkElement
    where TOptiFine : MinecraftOptiFineEntryData
    where TForgeLike : MinecraftForgelikeEntryData
    where TForge : TForgeLike, IMinecraftForgeVersionFields
    where TNeo : TForgeLike, IMinecraftNeoForgeFields
    where TLite : MinecraftLiteLoaderEntryData
    where TResource : ResourceVersionModelData {
    private readonly MinecraftInstallMenuHost<TView, TVirtual, TClick, TOptiFine, TForgeLike, TLite> host;
    public MinecraftInstallMenuController(MinecraftInstallMenuHost<TView, TVirtual, TClick, TOptiFine, TForgeLike, TLite> host) { this.host = host; }
    private static bool Same(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
    private TVirtual Virtual(Func<MinecraftInstallListItem> render, TClick click, Action<object, EventArgs>? content = null) => host.Virtualize(() => host.CreateItem(render(), click, content), 42);
    private static TTag Tag<TTag>(object sender) {
        FrameworkElement view = (FrameworkElement)sender;
        if (view.Tag is not null) return (TTag)view.Tag;
        FrameworkElement parent = (FrameworkElement)view.Parent;
        return (TTag)(parent.Tag ?? ((FrameworkElement)parent.Parent).Tag);
    }
    private static TView View(object sender) {
        if (sender is TView view) return view;
        FrameworkElement parent = (FrameworkElement)((FrameworkElement)sender).Parent;
        return parent is TView item ? item : (TView)parent.Parent;
    }
    private static TLite LiteTag(object sender) {
        object tag = ((FrameworkElement)sender).Tag;
        return tag is TLite info ? info : (TLite)((FrameworkElement)tag).Tag;
    }
    private MinecraftInstallMenuButton Button(MinecraftInstallMenuIcon icon, string tip, Action<object, RoutedEventArgs> click, double? scale = null, object? tag = null) =>
        new() { Icon = icon, ToolTip = tip, Click = click, LogoScale = scale, Tag = tag };
    public TVirtual McDownloadListItem(JObject entry, TClick click, bool saveOnly) => Virtual(() => {
        string logo = (string?)entry["type"] switch { "release" => "Grass.png", "snapshot" => "CommandBlock.png", "special" => "GoldBlock.png", _ => "CobbleStone.png" };
        string info = entry["lore"] is null ? entry["releaseTime"]!.Value<DateTime>().ToString("yyyy'/'MM'/'dd HH':'mm") : entry["lore"]!.ToString();
        if (entry["url"]!.ToString().Contains("pcl")) info = "[PCL 特供下载] " + info;
        return new() { Logo = host.ImageRoot() + "Blocks/" + logo, Title = entry["id"]!.ToString(), Info = info, Tag = entry };
    }, click, saveOnly ? McDownloadSaveMenuBuild : McDownloadMenuBuild);
    public void McDownloadSaveMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() {
        Button(MinecraftInstallMenuIcon.Server, "下载服务端", McDownloadMenuSaveServer, 1), Button(MinecraftInstallMenuIcon.Info, "更新日志", McDownloadMenuLog, 1.05)
    });
    public void McDownloadMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() {
        Button(MinecraftInstallMenuIcon.Save, "另存为", McDownloadMenuSave), Button(MinecraftInstallMenuIcon.Info, "更新日志", McDownloadMenuLog, 1.05), Button(MinecraftInstallMenuIcon.Server, "下载服务端", McDownloadMenuSaveServer, 1)
    });
    public void McDownloadMenuLog(object sender, RoutedEventArgs args) => McUpdateLogShow(Tag<JToken>(sender));
    public void McDownloadMenuSaveServer(object sender, RoutedEventArgs args) { TView view = View(sender); host.SaveMinecraft(() => (host.Title(view), (JToken)view.Tag), true); }
    public void McDownloadMenuSave(object sender, RoutedEventArgs args) { TView view = View(sender); host.SaveMinecraft(() => (host.Title(view), (JToken)view.Tag), false); }
    public TVirtual OptiFineDownloadListItem(TOptiFine entry, TClick click, bool saveOnly) => Virtual(() => new() {
        Title = entry.DisplayName, Info = (entry.IsPreview ? "测试版" : "正式版") + (Same(entry.ReleaseTime, "") ? "" : "，发布于 " + entry.ReleaseTime) +
            (entry.RequiredForgeVersion is null ? "，不兼容 Forge" : Same(entry.RequiredForgeVersion, "") ? "" : "，兼容 Forge " + entry.RequiredForgeVersion),
        Logo = host.ImageRoot() + "Blocks/GrassPath.png", Tag = entry
    }, click, saveOnly ? OptiFineSaveContMenuBuild : OptiFineContMenuBuild);
    public void OptiFineSaveContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() { Button(MinecraftInstallMenuIcon.Info, "更新日志", OptiFineLog_Click, 1.05) });
    public void OptiFineContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() { Button(MinecraftInstallMenuIcon.Save, "另存为", OptiFineSave_Click), Button(MinecraftInstallMenuIcon.Info, "更新日志", OptiFineLog_Click, 1.05) });
    public void OptiFineLog_Click(object sender, RoutedEventArgs args) => host.OpenWebsite("https://optifine.net/changelog?f=" + Tag<TOptiFine>(sender).FileName);
    public void OptiFineSave_Click(object sender, RoutedEventArgs args) => host.SaveOptiFine(Tag<TOptiFine>(sender));
    public TVirtual LiteLoaderDownloadListItem(TLite entry, TClick click, bool saveOnly) => Virtual(() => new() {
        Title = entry.Inherit, Info = (entry.IsPreview ? "测试版" : "稳定版") + (Same(entry.ReleaseTime, "") ? "" : "，发布于 " + entry.ReleaseTime), Logo = host.ImageRoot() + "Blocks/Egg.png", Tag = entry
    }, click, saveOnly ? LiteLoaderSaveContMenuBuild : LiteLoaderContMenuBuild);
    public void LiteLoaderSaveContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, ((TLite)((FrameworkElement)sender).Tag).IsLegacy ? new() : new() { Button(MinecraftInstallMenuIcon.List, "查看全部版本", LiteLoaderAll_Click, tag: sender) });
    public void LiteLoaderContMenuBuild(object sender, EventArgs args) {
        List<MinecraftInstallMenuButton> buttons = new() { Button(MinecraftInstallMenuIcon.Save, "保存安装器", LiteLoaderSave_Click, tag: sender) };
        if (!((TLite)((FrameworkElement)sender).Tag).IsLegacy) buttons.Add(Button(MinecraftInstallMenuIcon.List, "查看全部版本", LiteLoaderAll_Click, tag: sender));
        host.SetButtons(sender, buttons);
    }
    public void LiteLoaderAll_Click(object sender, RoutedEventArgs args) => host.OpenWebsite("https://jenkins.liteloader.com/view/" + LiteTag(sender).Inherit);
    public void LiteLoaderSave_Click(object sender, RoutedEventArgs args) => host.SaveLiteLoader(LiteTag(sender));
    public void ForgeDownloadListItemPreload(StackPanel stack, List<TForge> entries, TClick click, bool saveOnly) {
        if (entries.Count == 1) return;
        TForge? fresh = entries.Any() ? entries[0] : null;
        if (!entries.Any()) Logger.Info("未找到可用的 Forge 版本");
        TForge? recommended = null;
        foreach (TForge entry in entries) if (entry.IsRecommended) recommended = entry;
        if (fresh is not null && ReferenceEquals(fresh, recommended)) fresh = null;
        if (recommended is not null) { TView view = host.Initialize(ForgeDownloadListItem(recommended, click, saveOnly)); host.SetInfo(view, "推荐版" + (Same(host.Info(view), "") ? "" : "，" + host.Info(view))); stack.Children.Add(view); }
        if (fresh is not null) { TView view = host.Initialize(ForgeDownloadListItem(fresh, click, saveOnly)); host.SetInfo(view, "最新版" + (Same(host.Info(view), "") ? "" : "，" + host.Info(view))); stack.Children.Add(view); }
        Separator(stack, entries.Count);
    }
    private static void Separator(StackPanel stack, int count) => stack.Children.Add(new TextBlock { Text = "全部版本 (" + count + ")", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 13, 0, 4) });
    public TVirtual ForgeDownloadListItem(TForge entry, TClick click, bool saveOnly) => Virtual(() => new() {
        Title = entry.VersionName, Info = string.Join("，", new[] { Same(entry.ReleaseTime, "") ? "" : "发布于 " + entry.ReleaseTime, host.DebugMode() ? "种类：" + entry.Category : "" }.Where(value => !Same(value, ""))), Logo = host.ImageRoot() + "Blocks/Anvil.png", Tag = entry
    }, click, saveOnly ? ForgeSaveContMenuBuild : ForgeContMenuBuild);
    public void ForgeContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() { Button(MinecraftInstallMenuIcon.Save, "另存为", ForgeSave_Click), Button(MinecraftInstallMenuIcon.Info, "更新日志", ForgeLog_Click, 1.05) });
    public void ForgeSaveContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() { Button(MinecraftInstallMenuIcon.Info, "更新日志", ForgeLog_Click, 1.05) });
    public void ForgeLog_Click(object sender, RoutedEventArgs args) { TForge version = Tag<TForge>(sender); host.OpenWebsite("https://files.minecraftforge.net/maven/net/minecraftforge/forge/" + version.Inherit + "-" + version.VersionName + "/forge-" + version.Inherit + "-" + version.VersionName + "-changelog.txt"); }
    public void ForgeSave_Click(object sender, RoutedEventArgs args) => host.SaveForgelike(Tag<TForge>(sender));
    public void NeoForgeDownloadListItemPreload(StackPanel stack, List<TNeo> entries, TClick click, bool saveOnly) {
        if (entries.Count == 1) return;
        TNeo? stable = null, beta = null;
        if (entries.Any()) foreach (TNeo entry in entries.ToList()) { if (entry.IsBeta) { beta ??= entry; } else { stable = entry; break; } }
        else Logger.Warn("未找到可用的 NeoForge 版本");
        if (stable is not null) { TView view = host.Initialize(NeoForgeDownloadListItem(stable, click, saveOnly)); host.SetInfo(view, Same(host.Info(view), "") ? "最新稳定版" : "最新" + host.Info(view)); stack.Children.Add(view); }
        if (beta is not null) { TView view = host.Initialize(NeoForgeDownloadListItem(beta, click, saveOnly)); host.SetInfo(view, Same(host.Info(view), "") ? "最新测试版" : "最新" + host.Info(view)); stack.Children.Add(view); }
        Separator(stack, entries.Count);
    }
    public TVirtual NeoForgeDownloadListItem(TNeo entry, TClick click, bool saveOnly) => Virtual(() => new() {
        Title = entry.VersionName, Info = entry.IsBeta ? "测试版" : "稳定版", Logo = host.ImageRoot() + "Blocks/NeoForge.png", Tag = entry
    }, click, saveOnly ? NeoForgeSaveContMenuBuild : NeoForgeContMenuBuild);
    public void NeoForgeContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() { Button(MinecraftInstallMenuIcon.Save, "另存为", NeoForgeSave_Click), Button(MinecraftInstallMenuIcon.Info, "更新日志", NeoForgeLog_Click, 1.05) });
    public void NeoForgeSaveContMenuBuild(object sender, EventArgs args) => host.SetButtons(sender, new() { Button(MinecraftInstallMenuIcon.Info, "更新日志", NeoForgeLog_Click, 1.05) });
    public void NeoForgeLog_Click(object sender, RoutedEventArgs args) { TNeo info = Tag<TNeo>(sender); host.OpenWebsite(MinecraftDownloadEntries.NeoForgeUrlBase(info, info.ApiName) + "-changelog.txt"); }
    public void NeoForgeSave_Click(object sender, RoutedEventArgs args) => host.SaveForgelike(Tag<TNeo>(sender));
    public TVirtual FabricDownloadListItem(JObject entry, TClick click) => Virtual(() => new() {
        Title = entry["version"]!.ToString().Replace("+build", ""), Info = entry["stable"]!.ToObject<bool>() ? "稳定版" : "测试版", Logo = host.ImageRoot() + "Blocks/Fabric.png", Tag = entry
    }, click);
    public TVirtual FabricApiDownloadListItem(TResource entry, TClick click) => Virtual(() => new() {
        Title = entry.Display!.Split(']')[1].Replace("Fabric API ", "").Replace(" build ", ".").BeforeFirst("+").Trim(), Info = entry.ReleaseTypeDisplay + "，发布于 " + entry.ReleaseDate.ToString("yyyy'/'MM'/'dd HH':'mm"), Logo = host.ImageRoot() + "Blocks/Fabric.png", Tag = entry
    }, click);
    public TVirtual OptiFabricDownloadListItem(TResource entry, TClick click) => Virtual(() => new() {
        Title = entry.Display!.Lower().Replace("optifabric-", "").Replace(".jar", "").Trim().TrimStart('v'), Info = entry.ReleaseTypeDisplay + "，发布于 " + entry.ReleaseDate.ToString("yyyy'/'MM'/'dd HH':'mm"), Logo = host.ImageRoot() + "Blocks/OptiFabric.png", Tag = entry
    }, click);
}
