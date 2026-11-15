using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF PageDownloadInstall.xaml.vb, identical to fixed PCL
// 5ef81e490ae46b1100d61e33d0183094c7a80ca8 at LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983;
// LF LICENCE. CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 PageDownloadInstall.xaml.cs
// reviewed (CE LICENSE/application LICENCE). LF compatibility rules, lazy native rows,
// virtual override dispatch, public selection fields and original animations remain authoritative.
public sealed partial class DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation>
    where TOptiFine : MinecraftOptiFineEntryData
    where TForgeLike : MinecraftForgelikeEntryData
    where TForge : TForgeLike, IMinecraftForgeVersionFields
    where TNeo : TForgeLike, IMinecraftNeoForgeFields
    where TLite : MinecraftLiteLoaderEntryData
    where TResource : ResourceVersionModelData
    where TRequest : MinecraftInstallRequestData<TForge, TNeo, TResource, TLite>, new() {
    private readonly DownloadInstallPageHost<TOptiFine, TForge, TNeo, TLite, TResource, TRequest> host;
    private readonly DownloadPageAnimationHost<TAnimation> animation;
    private DownloadInstallSelection<TOptiFine, TForge, TNeo, TLite, TResource> State => host.State;
    private bool isLoad, ongoing;
    public DownloadInstallPageController(DownloadInstallPageHost<TOptiFine, TForge, TNeo, TLite, TResource, TRequest> host, DownloadPageAnimationHost<TAnimation> animation) { this.host = host; this.animation = animation; }
    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    public int VanillaDrop => McVersion.VersionToDrop(State.VanillaName);
    private DownloadInstallComponentView Component(DownloadInstallComponent kind) => host.Component(kind);
    public void ControlInit() { host.SuppressStartAnimation(true); host.BindLoaders(); host.RefreshRecommended(); }
    public void LoaderInit() { ControlInit(); host.InitializeLoader(); }
    public void Init() {
        if (isLoad) { State.IsSelectNameEdited = false; ExitSelectPage(); if (host.ClientFinished()) TrySelectWaitingVersion(); }
        isLoad = true; host.View().ScrollHome(); host.OptiFine.Start(); host.LiteLoader.Start(); host.Fabric.Start(); host.NeoForge.Start();
        host.InitializeNameValidation(); host.ReloadSelected();
    }
    public void PrepareSelectPage() {
        State.IsInSelectPage = true; DownloadInstallPageView view = host.View(); view.Inner.Margin = new Thickness(25, 10, 25, 40);
        view.Select.Visibility = Visibility.Visible; view.Select.IsHitTestVisible = true; view.Minecraft.IsHitTestVisible = false; view.Back.IsHitTestVisible = false; view.ScrollHome();
        host.SuppressStartAnimation(false); host.StartShow(true);
        foreach (DownloadInstallComponent kind in Enum.GetValues(typeof(DownloadInstallComponent))) Component(kind).SetSwapped(true);
        foreach (FrameworkElement control in host.AnimationControls(view.Select)) { control.Opacity = 1; if (control.RenderTransform is null || control.RenderTransform is TranslateTransform) control.RenderTransform = new TranslateTransform(); }
    }
    public void EnterSelectPage() {
        if (State.IsInSelectPage) return; State.AutoSelectedFabricApi = false; State.AutoSelectedOptiFabric = false; PrepareSelectPage();
        if (McVersion.IsStandardFormat(State.VanillaName)) host.StartForge(State.VanillaName!);
        host.FabricApi.Start(); host.OptiFabric.Start();
        DownloadInstallPageView view = host.View();
        animation.Start(new[] {
            animation.Opacity(view.Minecraft, -view.Minecraft.Opacity, 70, 10, DownloadPageEase.None),
            animation.TranslateX(view.Minecraft, -50 - ((TranslateTransform)view.Minecraft.RenderTransform).X, 90, 10, DownloadPageEase.None),
            animation.Code(() => { view.ScrollHome(); ReloadComponentLists(); view.Minecraft.Visibility = Visibility.Collapsed; }, 0, true),
            animation.Opacity(view.Select, 1 - view.Select.Opacity, 70, 100, DownloadPageEase.None),
            animation.TranslateX(view.Select, -((TranslateTransform)view.Select.RenderTransform).X, 160, 100, DownloadPageEase.OutFluentExtraStrong),
            animation.Code(() => view.Back.IsHitTestVisible = true, 0, true)
        }, "FrmDownloadInstall SelectPageSwitch", true);
    }
    public void ReloadComponentLists() { host.View().ValidateName(); OptiFineLoaded(); LiteLoaderLoaded(); ForgeLoaded(); NeoForgeLoaded(); FabricLoaded(); FabricApiLoaded(); OptiFabricLoaded(); host.ReloadSelected(); }
    public void ExitSelectPage() {
        if (!State.IsInSelectPage) return; State.IsInSelectPage = false;
        DownloadInstallPageView view = host.View(); view.Inner.Margin = new Thickness(25, 10, 25, 25);
        host.SuppressStartAnimation(true); host.StartShow(false); ClearSelected(); view.Minecraft.Visibility = Visibility.Visible; view.Select.IsHitTestVisible = false; view.Minecraft.IsHitTestVisible = true; view.Back.IsHitTestVisible = false; view.ScrollHome();
        animation.Start(new[] {
            animation.Opacity(view.Select, -view.Select.Opacity, 70, 10, DownloadPageEase.None),
            animation.TranslateX(view.Select, 50 - ((TranslateTransform)view.Select.RenderTransform).X, 90, 10, DownloadPageEase.None),
            animation.Code(view.ScrollHome, 0, true),
            animation.Opacity(view.Minecraft, 1 - view.Minecraft.Opacity, 70, 100, DownloadPageEase.None),
            animation.TranslateX(view.Minecraft, -((TranslateTransform)view.Minecraft.RenderTransform).X, 160, 100, DownloadPageEase.OutFluentExtraStrong),
            animation.Code(() => { view.Select.Visibility = Visibility.Collapsed; view.Back.IsHitTestVisible = true; }, 0, true)
        }, "FrmDownloadInstall SelectPageSwitch", false);
    }
    public void MinecraftSelected(FrameworkElement row, MouseButtonEventArgs? args) { State.VanillaName = host.RowTitle(row); State.VanillaData = (JObject)row.Tag; State.VanillaIcon = host.RowLogo(row); host.View().MinecraftVersion.Text = State.VanillaName; EnterSelectPage(); }
    public void ClearSelected() {
        State.VanillaName = null; State.VanillaData = null; State.VanillaIcon = null; State.SelectedOptiFine = null; State.SelectedLiteLoader = null; State.SelectedForge = null; State.SelectedNeoForge = null; State.SelectedFabric = null; State.SelectedFabricApi = null; State.SelectedOptiFabric = null;
        foreach (DownloadInstallComponent kind in Enum.GetValues(typeof(DownloadInstallComponent))) Component(kind).Clear.Visibility = Visibility.Collapsed;
    }
    public void SetPanelVisibility(Grid panel, bool visible) {
        if (Operators.ConditionalCompareObjectEqual(panel.Tag, visible.ToString(), false)) return;
        panel.Tag = visible.ToString();
        animation.Start(visible ? new[] {
            animation.TranslateY(panel, -((TranslateTransform)panel.RenderTransform).Y, 150, 0, DownloadPageEase.OutFluent), animation.Opacity(panel, 1 - panel.Opacity, 60, 0, DownloadPageEase.None)
        } : new[] {
            animation.TranslateY(panel, 6 - ((TranslateTransform)panel.RenderTransform).Y, 60, 0, DownloadPageEase.None), animation.Opacity(panel, -panel.Opacity, 60, 0, DownloadPageEase.None)
        }, "PageDownloadInstall Visibility " + panel.Name, false);
    }
    private void RenderChoice(DownloadInstallComponent kind, string? error, Func<bool> hasSelected, Func<string?> text, Func<bool>? extraSwap = null) {
        DownloadInstallComponentView view = Component(kind);
        view.MainSwap().Visibility = error is null ? Visibility.Visible : Visibility.Collapsed;
        if (error is not null || (extraSwap is not null && extraSwap())) view.SetSwapped(true);
        SetPanelVisibility(view.Info, view.IsSwapped());
        bool has = hasSelected(); view.Clear.Visibility = has ? Visibility.Visible : Visibility.Collapsed; view.Image.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        view.Label.Text = has ? text() : error ?? "可以添加"; view.Label.Foreground = has ? host.Gray1() : host.Gray4();
    }
    public void ReloadSelected() {
        if (State.VanillaName is null || ongoing) return; ongoing = true;
        SelectNameUpdate(); DownloadInstallPageView view = host.View(); view.Logo(host.SelectLogo()); view.MinecraftLogo(State.VanillaIcon);
        RenderChoice(DownloadInstallComponent.OptiFine, LoadOptiFineGetError(), () => State.SelectedOptiFine is not null, () => State.SelectedOptiFine!.DisplayName.Replace(State.VanillaName + " ", ""));
        if (VanillaDrop >= 130) Component(DownloadInstallComponent.LiteLoader).Card.Visibility = Visibility.Collapsed;
        else { Component(DownloadInstallComponent.LiteLoader).Card.Visibility = Visibility.Visible; RenderChoice(DownloadInstallComponent.LiteLoader, LoadLiteLoaderGetError(), () => State.SelectedLiteLoader is not null, () => State.SelectedLiteLoader!.Inherit); }
        if (!McVersion.IsStandardFormat(State.VanillaName)) Component(DownloadInstallComponent.Forge).Card.Visibility = Visibility.Collapsed;
        else { Component(DownloadInstallComponent.Forge).Card.Visibility = Visibility.Visible; RenderChoice(DownloadInstallComponent.Forge, LoadForgeGetError(), () => State.SelectedForge is not null, () => State.SelectedForge!.VersionName); }
        if (State.VanillaData!["releaseTime"]!.ToObject<DateTime>() < new DateTime(2023, 6, 11)) Component(DownloadInstallComponent.NeoForge).Card.Visibility = Visibility.Collapsed;
        else { Component(DownloadInstallComponent.NeoForge).Card.Visibility = Visibility.Visible; RenderChoice(DownloadInstallComponent.NeoForge, LoadNeoForgeGetError(), () => State.SelectedNeoForge is not null, () => State.SelectedNeoForge!.VersionName); }
        if (VanillaDrop <= 130) Component(DownloadInstallComponent.Fabric).Card.Visibility = Visibility.Collapsed;
        else { Component(DownloadInstallComponent.Fabric).Card.Visibility = Visibility.Visible; RenderChoice(DownloadInstallComponent.Fabric, LoadFabricGetError(), () => State.SelectedFabric is not null, () => State.SelectedFabric!.Replace("+build", "")); }
        if (State.SelectedFabric is null) Component(DownloadInstallComponent.FabricApi).Card.Visibility = Visibility.Collapsed;
        else { Component(DownloadInstallComponent.FabricApi).Card.Visibility = Visibility.Visible; RenderChoice(DownloadInstallComponent.FabricApi, LoadFabricApiGetError(), () => State.SelectedFabricApi is not null, () => State.SelectedFabricApi!.Display!.Split(']')[1].Replace("Fabric API ", "").Replace(" build ", ".").Split('+').First().Trim(), () => State.SelectedFabric is null); }
        if (State.SelectedFabric is null || State.SelectedOptiFine is null) Component(DownloadInstallComponent.OptiFabric).Card.Visibility = Visibility.Collapsed;
        else { Component(DownloadInstallComponent.OptiFabric).Card.Visibility = Visibility.Visible; RenderChoice(DownloadInstallComponent.OptiFabric, LoadOptiFabricGetError(), () => State.SelectedOptiFabric is not null, () => State.SelectedOptiFabric!.Display!.Lower().Replace("optifabric-", "").Replace(".jar", "").Trim().TrimStart('v'), () => State.SelectedFabric is null); }
        view.HintFabricApi.Visibility = State.SelectedFabric is not null && State.SelectedFabricApi is null ? Visibility.Visible : Visibility.Collapsed;
        if (State.SelectedFabric is not null && State.SelectedOptiFine is not null && State.SelectedOptiFabric is null) {
            bool old = VanillaDrop >= 140 && VanillaDrop <= 150; view.HintOptiFabric.Visibility = old ? Visibility.Collapsed : Visibility.Visible; view.HintOptiFabricOld.Visibility = old ? Visibility.Visible : Visibility.Collapsed;
        } else { view.HintOptiFabric.Visibility = Visibility.Collapsed; view.HintOptiFabricOld.Visibility = Visibility.Collapsed; }
        view.HintModOptiFine.Visibility = VanillaDrop >= 160 && State.SelectedOptiFine is not null && (State.SelectedForge is not null || State.SelectedFabric is not null) ? Visibility.Visible : Visibility.Collapsed;
        ongoing = false;
    }
    public string? GetSelectLogo() => State.SelectedFabric is not null ? "pack://application:,,,/images/Blocks/Fabric.png" : State.SelectedForge is not null ? "pack://application:,,,/images/Blocks/Anvil.png" : State.SelectedNeoForge is not null ? "pack://application:,,,/images/Blocks/NeoForge.png" : State.SelectedLiteLoader is not null ? "pack://application:,,,/images/Blocks/Egg.png" : State.SelectedOptiFine is not null ? "pack://application:,,,/images/Blocks/GrassPath.png" : State.VanillaIcon;
    public string? GetSelectName() {
        string? name = State.VanillaName;
        if (State.SelectedFabric is not null) name += "-Fabric " + State.SelectedFabric.BeforeFirst("+");
        if (State.SelectedForge is not null) name += "-Forge_" + State.SelectedForge.VersionName;
        if (State.SelectedNeoForge is not null) name += "-NeoForge_" + State.SelectedNeoForge.VersionName.BeforeFirst("+");
        if (State.SelectedLiteLoader is not null) name += "-LiteLoader";
        if (State.SelectedOptiFine is not null) name += "-OptiFine_" + State.SelectedOptiFine.DisplayName.Replace(State.VanillaName + " ", "").Replace(" ", "_");
        return name;
    }
    public void SelectNameUpdate() { if (State.IsSelectNameEdited) return; host.View().Name.Text = GetSelectName(); State.IsSelectNameEdited = false; }
    public void NameTextChanged() => State.IsSelectNameEdited = true;
    public void NameValidateChanged() { DownloadInstallPageView view = host.View(); view.Start.IsEnabled = view.NameValidated(); view.NameHintText(view.NameValidationResult()); view.NameHint.Visibility = view.NameValidated() ? Visibility.Collapsed : Visibility.Visible; }
}
