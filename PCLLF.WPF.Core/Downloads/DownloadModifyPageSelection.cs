using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class DownloadModifyPageController<TInstance, TState, TCard, TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TLoader, TLoading, TAnimation> {
    public void ReloadSelected() {
        if (State.IsRestoringComponents) return;
        if (Selection.IsInSelectPage && Selection.VanillaName is not null && State.PendingComponents.Any()) {
            State.IsRestoringComponents = true;
            try {
                bool processed = false;
                foreach (DownloadInstallComponent kind in new[] { DownloadInstallComponent.Fabric, DownloadInstallComponent.Forge, DownloadInstallComponent.NeoForge, DownloadInstallComponent.LiteLoader, DownloadInstallComponent.OptiFine, DownloadInstallComponent.FabricApi, DownloadInstallComponent.OptiFabric }) {
                    TLoading loading = host.LoadingControl(kind); if (!State.PendingComponents.TryGetValue(loading, out string? version) || !host.IsLoader(loading)) continue; TLoader loader = host.ReadLoader(loading);
                    bool Pending(DownloadInstallComponent component) => State.PendingComponents.ContainsKey(host.LoadingControl(component));
                    if (kind == DownloadInstallComponent.OptiFine) { if (new[] { DownloadInstallComponent.Fabric, DownloadInstallComponent.Forge, DownloadInstallComponent.NeoForge, DownloadInstallComponent.LiteLoader }.Any(Pending)) continue; }
                    else if (kind == DownloadInstallComponent.FabricApi) { if (Pending(DownloadInstallComponent.Fabric)) continue; }
                    else if (kind == DownloadInstallComponent.Forge) {
                        if (!McVersion.IsStandardFormat(Selection.VanillaName)) { State.PendingComponents.Remove(loading); continue; }
                        if (!Same(host.ForgeInput(loader), Selection.VanillaName)) continue;
                        if (host.LoaderState(loader) == host.States.Failed && Same(host.LoaderError(loader).Message, "无")) { State.PendingComponents.Remove(loading); continue; }
                    } else if (kind == DownloadInstallComponent.OptiFabric) { if (Pending(DownloadInstallComponent.Fabric) || Pending(DownloadInstallComponent.OptiFine)) continue; }
                    if (host.LoaderState(loader) != host.States.Finished) continue; State.PendingComponents.Remove(loading); processed = true;
                    if (kind == DownloadInstallComponent.Fabric) {
                        if (Selection.SelectedFabric is null && host.ComponentError(kind) is null) { IEnumerable<JObject> versions = host.FabricOutput()["loader"]!.OfType<JObject>(); Selection.SelectedFabric = (versions.FirstOrDefault(entry => Same(entry["version"]!.ToString(), version)) ?? versions.FirstOrDefault())?["version"]!.ToString(); }
                    } else if (kind == DownloadInstallComponent.OptiFine) {
                        if (Selection.SelectedOptiFine is null && host.ComponentError(kind) is null) { IEnumerable<TOptiFine> versions = host.OptiFineOutput().Where(entry => Same(entry.Inherit, Selection.VanillaName) && (Selection.SelectedForge is null || OptiFineSuits(entry, Selection.SelectedForge))); Selection.SelectedOptiFine = versions.FirstOrDefault(entry => Same(entry.InstanceName.AfterLast("-OptiFine_"), version)) ?? versions.OrderBy(entry => entry.IsPreview).ThenByDescending(entry => entry.DisplayName, Comparer<string>.Create(McUtils.CompareVersion)).FirstOrDefault(); }
                    } else if (kind == DownloadInstallComponent.Forge) {
                        if (Selection.SelectedForge is null && host.ComponentError(kind) is null) { IEnumerable<TForge> versions = host.ForgeOutput(loader)!.Where(entry => Same(entry.Inherit, Selection.VanillaName) && !Same(entry.Category, "universal") && !Same(entry.Category, "client") && (Selection.SelectedOptiFine is null || OptiFineSuits(Selection.SelectedOptiFine, entry))); Selection.SelectedForge = versions.FirstOrDefault(entry => Same(entry.VersionName, version)) ?? versions.OrderByDescending(entry => entry).FirstOrDefault(); }
                    } else if (kind == DownloadInstallComponent.NeoForge) {
                        if (Selection.SelectedNeoForge is null && host.ComponentError(kind) is null) { IEnumerable<TNeo> versions = host.NeoForgeOutput().Where(entry => Same(entry.Inherit, Selection.VanillaName)); Selection.SelectedNeoForge = versions.FirstOrDefault(entry => Same(entry.VersionName, version)) ?? versions.FirstOrDefault(); }
                    } else if (kind == DownloadInstallComponent.LiteLoader) {
                        if (Selection.SelectedLiteLoader is null && host.ComponentError(kind) is null) { IEnumerable<TLite> versions = host.LiteLoaderOutput().Where(entry => Same(entry.Inherit, Selection.VanillaName)); Selection.SelectedLiteLoader = versions.FirstOrDefault(entry => Same(entry.JsonToken["version"]!.ToString(), version)) ?? versions.FirstOrDefault(); }
                    } else if (kind == DownloadInstallComponent.FabricApi) {
                        if (Selection.SelectedFabricApi is null && host.ComponentError(kind) is null) { IEnumerable<TResource> versions = host.FabricApiOutput().Where(entry => ApiSuits(entry, Selection.VanillaName)); Selection.SelectedFabricApi = versions.FirstOrDefault(entry => Same(entry.Version, version) || Same(entry.Id, version) || Same(entry.Display, version)) ?? versions.OrderByDescending(entry => entry.ReleaseDate).FirstOrDefault(); }
                        if (Selection.SelectedFabricApi is not null) Selection.AutoSelectedFabricApi = true;
                    } else if (kind == DownloadInstallComponent.OptiFabric) {
                        if (Selection.SelectedOptiFabric is null && host.ComponentError(kind) is null) { IEnumerable<TResource> versions = host.OptiFabricOutput().Where(entry => OptiFabricSuits(entry, Selection.VanillaName)); Selection.SelectedOptiFabric = versions.FirstOrDefault(entry => Same(entry.Version, version) || Same(entry.Id, version) || Same(entry.Display, version)) ?? versions.OrderByDescending(entry => entry.ReleaseDate).FirstOrDefault(); }
                        if (Selection.SelectedOptiFabric is not null) Selection.AutoSelectedOptiFabric = true;
                    }
                }
                if (processed) host.BaseReloadLists();
            } finally { State.IsRestoringComponents = false; }
        }
        host.BaseReloadSelected(); if (State.InitialTarget is not null) RefreshComparisons(); RefreshModpackRestrictions();
    }
    public void RefreshComparisons() {
        McComponents current = host.Components(); host.View().MinecraftNewLogo(Selection.VanillaIcon); bool sameMinecraft = Same(State.InitialTarget!.MinecraftVersion, current.MinecraftVersion);
        DownloadModifyComparison C(int kind) => host.Comparison(kind);
        void Show(int kind, string? oldVersion, string? newVersion, bool restore) { var part = C(kind); ShowComparison(part.Compare, part.Current, oldVersion, newVersion, part.Label.Text, part.Restore, restore, part.Clear); }
        var minecraft = C(-1); ShowComparison(minecraft.Compare, minecraft.Current, State.InitialTarget.MinecraftVersion, current.MinecraftVersion, host.View().MinecraftVersion.Text, minecraft.Restore, true);
        TOptiFine? originalOptiFine = C(0).Restore.Tag as TOptiFine;
        Show(0, State.InitialTarget.OptiFineVersion, current.OptiFineVersion, State.InitialTarget.OptiFineEntry is null || sameMinecraft && C(0).MainSwap()!.Visibility == Visibility.Visible && originalOptiFine is not null && (Selection.SelectedForge is null || OptiFineSuits(originalOptiFine, Selection.SelectedForge)));
        Show(5, State.InitialTarget.FabricApiVersion, current.FabricApiVersion, State.InitialTarget.FabricApiVersion is null || sameMinecraft && Selection.SelectedFabric is not null && C(5).MainSwap()!.Visibility == Visibility.Visible);
        Show(6, State.InitialTarget.OptiFabricVersion, current.OptiFabricVersion, State.InitialTarget.OptiFabricVersion is null || sameMinecraft && Selection.SelectedFabric is not null && Selection.SelectedOptiFine is not null && C(6).MainSwap()!.Visibility == Visibility.Visible);
        foreach (var part in new[] { (ModLoaders.Forge, 2), (ModLoaders.NeoForge, 3), (ModLoaders.Fabric, 4), (ModLoaders.LiteLoader, 1) }) {
            State.InitialTarget.ModLoaderVersions.TryGetValue(part.Item1, out string? oldVersion); current.ModLoaderVersions.TryGetValue(part.Item1, out string? newVersion);
            bool restore = oldVersion is null || sameMinecraft && C(part.Item2).MainSwap()!.Visibility == Visibility.Visible && C(part.Item2).Restore.Tag is not null;
            if (part.Item1 == ModLoaders.Forge && oldVersion is not null && restore) restore = Selection.SelectedOptiFine is null || OptiFineSuits(Selection.SelectedOptiFine, (TForge)C(part.Item2).Restore.Tag);
            if (part.Item1 == ModLoaders.Fabric && oldVersion is not null) { string target = Selection.VanillaName!.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3"); restore = C(4).Card.Visibility == Visibility.Visible && C(4).MainSwap()!.Visibility == Visibility.Visible && host.FabricOutput()["loader"]!.OfType<JObject>().Any(version => Same(version["version"]!.ToString(), oldVersion)) && host.FabricOutput()["game"]!.OfType<JObject>().Any(version => Same(version["version"]!.ToString(), target)); }
            Show(part.Item2, oldVersion, newVersion, restore);
        }
        UpdateStartButton();
    }
    public void ShowComparison(Grid comparePanel, FrameworkElement currentPanel, string? oldVersion, string? newVersion, string newText, FrameworkElement restoreButton, bool canRestore, FrameworkElement? clearButton = null) {
        string? unavailable = null;
        if (newVersion is null) {
            foreach (int kind in new[] { 0, 2, 3, 4, 1, 5, 6 }) if (ReferenceEquals(restoreButton, host.Comparison(kind).Restore)) {
                unavailable = kind == 2 && !McVersion.IsStandardFormat(Selection.VanillaName) || (kind == 3 || kind == 4 || kind == 1) && host.Comparison(kind).Card.Visibility == Visibility.Collapsed ? "无" : host.ComponentError((DownloadInstallComponent)kind); break;
            }
            newText = unavailable ?? "可以添加";
        }
        bool pending = newVersion is null && (Same(newText, "获取中……") || newText.StartsWithF("获取失败：")); bool changed = !Same(oldVersion, newVersion) && !pending; canRestore = changed && oldVersion is not null && canRestore;
        comparePanel.Visibility = changed ? Visibility.Visible : Visibility.Collapsed; currentPanel.Visibility = changed ? Visibility.Collapsed : Visibility.Visible; restoreButton.Visibility = canRestore ? Visibility.Visible : Visibility.Collapsed;
        if (clearButton is not null) clearButton.ToolTip = oldVersion is null ? "取消安装" : "移除"; bool hasClear = clearButton is not null && clearButton.Visibility == Visibility.Visible;
        restoreButton.Margin = new Thickness(0, 5, canRestore && hasClear ? 60 : 32, 0); comparePanel.Margin = new Thickness(comparePanel.Margin.Left, comparePanel.Margin.Top, canRestore && hasClear ? 110 : 80, comparePanel.Margin.Bottom); if (!changed) return;
        if (oldVersion is not null) { DownloadModifyComparison card = host.ComparisonCard(comparePanel); if (card.Card.Visibility == Visibility.Collapsed) { card.SetSwapped(true); if (card.MainSwap() is not null) card.MainSwap()!.Visibility = Visibility.Collapsed; card.Card.Visibility = Visibility.Visible; } }
        DockPanel oldSide = (DockPanel)comparePanel.Children[0], newSide = (DockPanel)comparePanel.Children[2]; oldSide.Children[0].Visibility = oldVersion is null ? Visibility.Collapsed : Visibility.Visible; ((TextBlock)oldSide.Children[1]).Margin = oldVersion is null ? new Thickness(0) : new Thickness(7, 0, 0, 0);
        newSide.Children[0].Visibility = newVersion is null ? Visibility.Collapsed : Visibility.Visible; TextBlock label = (TextBlock)newSide.Children[1]; label.Margin = newVersion is null ? new Thickness(0) : new Thickness(7, 0, 0, 0); label.Text = newVersion is null ? "移除" : newText;
        if (newVersion is null && unavailable is not null) label.Inlines.Add(new Run("（" + (Same(unavailable, "无") ? "无可用版本" : unavailable) + "）") { Foreground = host.Gray4() });
    }
    public void RestoreComponent(FrameworkElement button) {
        if (ReferenceEquals(button, host.Comparison(-1).Restore)) {
            State.IsRestoringComponents = true;
            try {
                State.PendingComponents.Clear(); host.SelectMinecraft((JObject)button.Tag); if (State.ForgeVersionLoader is not null) host.BindForge(State.ForgeVersionLoader);
                Selection.SelectedOptiFine = host.Comparison(0).Restore.Tag as TOptiFine; Selection.SelectedForge = host.Comparison(2).Restore.Tag as TForge; Selection.SelectedNeoForge = host.Comparison(3).Restore.Tag as TNeo; Selection.SelectedFabric = host.Comparison(4).Restore.Tag as string; Selection.SelectedLiteLoader = host.Comparison(1).Restore.Tag as TLite; Selection.SelectedFabricApi = host.Comparison(5).Restore.Tag as TResource; Selection.SelectedOptiFabric = host.Comparison(6).Restore.Tag as TResource;
                Selection.AutoSelectedFabricApi = Selection.SelectedFabric is not null; Selection.AutoSelectedOptiFabric = Selection.SelectedFabric is not null && Selection.SelectedOptiFine is not null;
            } finally { State.IsRestoringComponents = false; }
            host.BaseReloadLists(); return;
        }
        if (ReferenceEquals(button, host.Comparison(0).Restore)) { Selection.SelectedOptiFine = button.Tag as TOptiFine; Selection.AutoSelectedOptiFabric = Selection.SelectedOptiFine is not null && Selection.SelectedFabric is not null; if (Selection.SelectedOptiFine is null) Selection.SelectedOptiFabric = null; }
        else if (ReferenceEquals(button, host.Comparison(2).Restore)) Selection.SelectedForge = button.Tag as TForge;
        else if (ReferenceEquals(button, host.Comparison(3).Restore)) Selection.SelectedNeoForge = button.Tag as TNeo;
        else if (ReferenceEquals(button, host.Comparison(4).Restore)) { Selection.SelectedFabric = button.Tag as string; Selection.AutoSelectedFabricApi = Selection.SelectedFabric is not null; Selection.AutoSelectedOptiFabric = Selection.SelectedFabric is not null && Selection.SelectedOptiFine is not null; if (Selection.SelectedFabric is null) { Selection.SelectedFabricApi = null; Selection.SelectedOptiFabric = null; } }
        else if (ReferenceEquals(button, host.Comparison(5).Restore)) { Selection.SelectedFabricApi = button.Tag as TResource; Selection.AutoSelectedFabricApi = true; }
        else if (ReferenceEquals(button, host.Comparison(6).Restore)) { Selection.SelectedOptiFabric = button.Tag as TResource; Selection.AutoSelectedOptiFabric = true; }
        else if (ReferenceEquals(button, host.Comparison(1).Restore)) Selection.SelectedLiteLoader = button.Tag as TLite;
        host.SwapRestoreParent(button, true); host.BaseReloadLists();
    }
}
