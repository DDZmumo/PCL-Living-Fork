using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// Complete LF PageDownloadModify.vb: installed-component recovery, selection/recovery, comparison and modification submission.
// LF remains authoritative. Fixed PCL/CE comparisons and license limits are documented in the frozen batch report.
public sealed partial class DownloadModifyPageController<TInstance, TState, TCard, TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TLoader, TLoading, TAnimation>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard> where TState : struct, Enum where TCard : struct, Enum
    where TOptiFine : MinecraftOptiFineEntryData where TForgeLike : MinecraftForgelikeEntryData
    where TForge : TForgeLike, IMinecraftForgeVersionFields where TNeo : TForgeLike, IMinecraftNeoForgeFields
    where TLite : MinecraftLiteLoaderEntryData where TResource : ResourceVersionModelData
    where TRequest : MinecraftInstallRequestData<TForge, TNeo, TResource, TLite>, new()
    where TLoader : class where TLoading : class {
    private readonly DownloadModifyPageHost<TOptiFine, TForge, TNeo, TLite, TResource, TRequest, TInstance, TLoader, TLoading> host;
    private readonly DownloadModifyAnimation<TAnimation> animation;
    private DownloadInstallSelection<TOptiFine, TForge, TNeo, TLite, TResource> Selection => host.Selection;
    private DownloadModifyPageState<TLoader, TLoading> State => host.State;
    private bool isLoad;
    public DownloadModifyPageController(DownloadModifyPageHost<TOptiFine, TForge, TNeo, TLite, TResource, TRequest, TInstance, TLoader, TLoading> host, DownloadModifyAnimation<TAnimation> animation) { this.host = host; this.animation = animation; }
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    private static bool OptiFineSuits(TOptiFine optiFine, TForge forge) => DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation>.IsOptiFineSuitForForge(optiFine, forge);
    private static bool ApiSuits(TResource resource, string? minecraft) => DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation>.IsFabricApiCompatible(resource, minecraft);
    private static bool OptiFabricSuits(TResource resource, string? minecraft) => DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation>.IsOptiFabricCompatible(resource, minecraft);
    public void LoaderInit() {
        host.BaseControlInit();
        host.SetPackName(new TextBlock { Text = "正在读取整合包信息", TextWrapping = TextWrapping.Wrap }); host.SetPackInfo(new TextBlock { Margin = new Thickness(0, 5, 0, 0), Opacity = .6, TextWrapping = TextWrapping.Wrap });
        WrapPanel buttons = new() { Visibility = Visibility.Collapsed }; host.SetPackButtons(buttons);
        // Update actions now live on the instance sidebar's dedicated update page.
        // Keep the metadata adapters alive for the installed-component loader.
        host.InitializeLoader();
    }
    public void Init() { host.View().ScrollHome(); if (isLoad) host.Restart(); isLoad = true; }
    public void Unloaded() => host.Cancel();
    public void LoadInstanceFinished(DownloadModifyTask<TInstance> task) {
        ShowPackInfo(task.Output() as JObject); State.OriginalName = task.Input().Name; State.CustomLogo = null;
        if (host.ReadIniBoolean(task.Input().PathVersion + "PCL\\Setup.ini", "LogoCustom")) State.CustomLogo = task.Input().Logo!.EndsWithF("PCL\\Logo.png") ? task.Input().PathVersion + "PCL\\Logo.png" : task.Input().Logo;
        FrameworkElement row = host.MinecraftRow(Selection.VanillaData!); Selection.VanillaName = host.RowTitle(row); Selection.VanillaIcon = host.RowLogo(row);
        DownloadModifyPageView view = host.View(); view.MinecraftVersion.Text = Selection.VanillaName; view.Name.Text = State.OriginalName; Selection.IsSelectNameEdited = !string.Equals(State.OriginalName, host.SelectName(), StringComparison.Ordinal); view.ValidateName(State.OriginalName);
        host.Comparison(-1).Card.Visibility = Visibility.Visible; view.BackButton.Visibility = Visibility.Collapsed; view.Logo.Margin = new Thickness(15, 15, 0, 0); view.Name.Margin = new Thickness(56, 16, 16, 0); view.NameLabel.Text = $"原名 :   {State.OriginalName}"; UpdateOriginalNameVisibility(); view.StartText("开始修改"); view.SetStartLogo();
        Selection.AutoSelectedFabricApi = Selection.SelectedFabric is not null; Selection.AutoSelectedOptiFabric = Selection.SelectedFabric is not null && Selection.SelectedOptiFine is not null;
        if (State.ForgeVersionLoader is not null) host.BindForge(State.ForgeVersionLoader); host.BasePrepareSelection(); host.BaseReloadLists();
        view.Minecraft.Visibility = Visibility.Collapsed; view.Minecraft.Opacity = 0; view.Minecraft.RenderTransform = new TranslateTransform(-50, 0); view.Select.Opacity = 1; view.Select.RenderTransform = new TranslateTransform(); view.Back.IsHitTestVisible = true; view.AllBack.IsHitTestVisible = true; State.InitialTarget = host.Components();
        host.Comparison(-1).Restore.Tag = Selection.VanillaData; host.Comparison(0).Restore.Tag = Selection.SelectedOptiFine; host.Comparison(2).Restore.Tag = Selection.SelectedForge; host.Comparison(3).Restore.Tag = Selection.SelectedNeoForge; host.Comparison(4).Restore.Tag = Selection.SelectedFabric; host.Comparison(5).Restore.Tag = Selection.SelectedFabricApi; host.Comparison(6).Restore.Tag = Selection.SelectedOptiFabric; host.Comparison(1).Restore.Tag = Selection.SelectedLiteLoader;
        view.MinecraftOldLogo(Selection.VanillaIcon); host.Comparison(-1).OldLabel.Text = view.MinecraftVersion.Text;
        foreach (int kind in new[] { 0, 2, 3, 4, 1, 5, 6 }) host.Comparison(kind).OldLabel.Text = Selected(kind) is null ? "无" : host.Comparison(kind).Label.Text;
        RefreshComparisons();
    }
    public string? GetSelectLogo() => State.CustomLogo ?? host.BaseLogo();
    public void ShowMinecraftVersionList() {
        if (host.ModpackEnabled()) return;
        if (Selection.IsInSelectPage) {
            McComponents components = host.Components();
            foreach (var part in new[] { (DownloadInstallComponent.OptiFine, components.OptiFineVersion), (DownloadInstallComponent.FabricApi, components.FabricApiVersion), (DownloadInstallComponent.OptiFabric, components.OptiFabricVersion), (DownloadInstallComponent.Forge, Selection.SelectedForge?.VersionName), (DownloadInstallComponent.NeoForge, Selection.SelectedNeoForge?.VersionName), (DownloadInstallComponent.Fabric, Selection.SelectedFabric), (DownloadInstallComponent.LiteLoader, Selection.SelectedLiteLoader?.JsonToken?["version"]!.ToString()) }) if (part.Item2 is not null) State.PendingComponents[host.LoadingControl(part.Item1)] = part.Item2;
        }
        host.BaseShowMinecraftList();
    }
    private object? Selected(int kind) => kind switch { 0 => Selection.SelectedOptiFine, 1 => Selection.SelectedLiteLoader, 2 => Selection.SelectedForge, 3 => Selection.SelectedNeoForge, 4 => Selection.SelectedFabric, 5 => Selection.SelectedFabricApi, 6 => Selection.SelectedOptiFabric, _ => Selection.VanillaData };
    public void RefreshModpackRestrictions() { host.Comparison(-1).Card.IsHitTestVisible = !host.ModpackEnabled(); host.View().ChangeMinecraft.Visibility = host.ModpackEnabled() ? Visibility.Collapsed : Visibility.Visible; }
    public void UpdateStartButton() {
        DownloadModifyPageView view = host.View(); view.Start.IsEnabled = view.NameValidated() && State.InitialTarget is not null && !State.PendingComponents.Any() && (!State.IsVanillaVersionReliable || !State.InitialTarget.IsEquivalentTo(host.Components()) || !string.Equals(view.Name.Text, State.OriginalName, StringComparison.Ordinal));
    }
    public void UpdateOriginalNameVisibility() {
        DownloadModifyPageView view = host.View(); bool changed = !string.Equals(view.Name.Text, State.OriginalName, StringComparison.Ordinal); Visibility visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        if (view.NameLabel.Visibility == visibility) return; view.NameLabel.Visibility = visibility; view.NameRestore.Visibility = visibility; int right = changed ? 40 : 16;
        if (Selection.IsInSelectPage && animation.Native.Disabled() == 0) animation.Native.Start(new[] { animation.Double(delta => view.Name.Margin = new Thickness(56, 16, view.Name.Margin.Right + delta, 0), right - view.Name.Margin.Right, 150, 0, DownloadPageEase.OutFluent) }, "DownloadModify Name Margin", false);
        else { animation.Native.Stop("DownloadModify Name Margin"); view.Name.Margin = new Thickness(56, 16, right, 0); }
    }
    public void RestoreOriginalName() { host.View().Name.Text = State.OriginalName; Selection.IsSelectNameEdited = !string.Equals(State.OriginalName, host.SelectName(), StringComparison.Ordinal); }
    public void MinecraftClick(Action handled) { ShowMinecraftVersionList(); handled(); }
    public void ShowPackInfo(JObject? data) {
        bool recognized = data is not null && data["error"] is null; host.PackButtons()!.Visibility = recognized ? Visibility.Visible : Visibility.Collapsed; host.PackInfo()!.Visibility = recognized ? Visibility.Visible : Visibility.Collapsed;
        if (!recognized) { host.PackName()!.Text = data is null ? "未检测到整合包" : "整合包信息读取失败"; return; }
        host.PackName()!.Text = (data!["name"]?.ToString() ?? "整合包") + (string.IsNullOrWhiteSpace(data["version"]?.ToString()) ? "" : "  |  " + data["version"]!.ToString()); JObject? source = data["source"] as JObject; host.PackInfo()!.Text = (source?["platform"]?.ToString() ?? "本地整合包") + " · Minecraft " + data["minecraft"]?.ToString();
    }
}
