using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation> {
    private Panel ClearComponentVersions(DownloadInstallComponent kind) {
        Panel panel = Component(kind).Versions;
        panel.Children.Clear();
        return panel;
    }

    public void OptiFineLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.OptiFine);
            if (State.VanillaName is null || !host.OptiFine.Finished()) return;
            List<TOptiFine> versions = new();
            foreach (TOptiFine version in host.OptiFine.Output() ?? []) {
                if (State.SelectedForge is not null && !IsOptiFineSuitForForge(version, State.SelectedForge)) continue;
                if (version.DisplayName.StartsWithF(State.VanillaName + " ")) versions.Add(version);
            }
            if (!versions.Any()) return;
            versions = LegacyMinecraftListOrder.SortByComparison(versions, (left, right) => { if (!left.IsPreview && right.IsPreview) return true; if (left.IsPreview && !right.IsPreview) return false; return McUtils.CompareVersionGE(left.DisplayName, right.DisplayName); });
            foreach (TOptiFine version in versions) panel.Children.Add(host.OptiFineRow(version, OptiFineSelected));
        } catch (Exception error) { Logger.Error(error, "可视化 OptiFine 安装版本列表出错"); }
    }
    public void OptiFineSelected(FrameworkElement row, EventArgs? args) { State.SelectedOptiFine = (TOptiFine)row.Tag; if (State.SelectedForge is not null && !IsOptiFineSuitForForge(State.SelectedOptiFine, State.SelectedForge)) State.SelectedForge = null; OptiFabricLoaded(); ForgeLoaded(); NeoForgeLoaded(); Component(DownloadInstallComponent.OptiFine).SetSwapped(true); host.ReloadSelected(); }
    public void OptiFineClear() { State.SelectedOptiFine = null; State.SelectedOptiFabric = null; Component(DownloadInstallComponent.OptiFabric).Clear.Visibility = Visibility.Collapsed; State.AutoSelectedOptiFabric = false; Component(DownloadInstallComponent.OptiFine).SetSwapped(true); ForgeLoaded(); NeoForgeLoaded(); host.ReloadSelected(); }
    public void LiteLoaderLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.LiteLoader);
            if (State.VanillaName is null || !host.LiteLoader.Finished()) return;
            foreach (TLite version in host.LiteLoader.Output() ?? [])
                if (Same(version.Inherit, State.VanillaName)) panel.Children.Add(host.LiteLoaderRow(version, LiteLoaderSelected));
        } catch (Exception error) { Logger.Error(error, "可视化 LiteLoader 安装版本列表出错"); }
    }
    public void LiteLoaderSelected(FrameworkElement row, EventArgs? args) { State.SelectedLiteLoader = (TLite)row.Tag; Component(DownloadInstallComponent.LiteLoader).SetSwapped(true); host.ReloadSelected(); }
    public void LiteLoaderClear() { State.SelectedLiteLoader = null; Component(DownloadInstallComponent.LiteLoader).SetSwapped(true); host.ReloadSelected(); }
    public void ForgeLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.Forge);
            if (State.VanillaName is null || host.Loading(DownloadInstallComponent.Forge)?.IsLoader() != true) return;
            DownloadInstallForgeSource<TForge> loader = host.Forge();
            if (!Same(State.VanillaName, loader.Input()) || !loader.Finished()) return;
            List<TForge> versions = (loader.Output() ?? []).Where(IsCurrentForgeInstaller)
                .Where(version => State.SelectedOptiFine is null || IsOptiFineSuitForForge(State.SelectedOptiFine, version))
                .OrderByDescending(version => version).ToList();
            if (versions.Count == 0) return;
            host.ForgePreload(panel, versions, ForgeSelected);
            foreach (TForge version in versions) panel.Children.Add(host.ForgeRow(version, ForgeSelected));
        } catch (Exception error) { Logger.Error(error, "可视化 Forge 安装版本列表出错"); }
    }
    public void ForgeSelected(FrameworkElement row, EventArgs? args) { State.SelectedForge = (TForge)row.Tag; Component(DownloadInstallComponent.Forge).SetSwapped(true); if (State.SelectedOptiFine is not null && !IsOptiFineSuitForForge(State.SelectedOptiFine, State.SelectedForge)) State.SelectedOptiFine = null; OptiFineLoaded(); host.ReloadSelected(); }
    public void ForgeClear() { State.SelectedForge = null; Component(DownloadInstallComponent.Forge).SetSwapped(true); OptiFineLoaded(); host.ReloadSelected(); }
    public void NeoForgeLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.NeoForge);
            if (State.VanillaName is null || !host.NeoForge.Finished()) return;
            List<TNeo> versions = (host.NeoForge.Output() ?? []).Where(version => Same(version.Inherit, State.VanillaName)).ToList();
            if (versions.Count == 0) return;
            host.NeoForgePreload(panel, versions, NeoForgeSelected);
            foreach (TNeo version in versions) panel.Children.Add(host.NeoForgeRow(version, NeoForgeSelected));
        } catch (Exception error) { Logger.Error(error, "可视化 NeoForge 安装版本列表出错"); }
    }
    public void NeoForgeSelected(FrameworkElement row, EventArgs? args) { State.SelectedNeoForge = (TNeo)row.Tag; Component(DownloadInstallComponent.NeoForge).SetSwapped(true); OptiFineLoaded(); host.ReloadSelected(); }
    public void NeoForgeClear() { State.SelectedNeoForge = null; Component(DownloadInstallComponent.NeoForge).SetSwapped(true); OptiFineLoaded(); host.ReloadSelected(); }
    public void FabricLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.Fabric);
            if (!host.Fabric.Finished()) return;
            foreach (JObject version in FabricLoaders()) panel.Children.Add(host.FabricRow(version, FabricSelected));
        } catch (Exception error) { Logger.Error(error, "可视化 Fabric 安装版本列表出错"); }
    }
    public void FabricSelected(FrameworkElement row, EventArgs? args) { State.SelectedFabric = ((JToken)row.Tag)["version"]!.ToString(); FabricApiLoaded(); OptiFabricLoaded(); Component(DownloadInstallComponent.Fabric).SetSwapped(true); host.ReloadSelected(); }
    public void FabricClear() { State.SelectedFabric = null; State.SelectedFabricApi = null; Component(DownloadInstallComponent.FabricApi).Clear.Visibility = Visibility.Collapsed; State.AutoSelectedFabricApi = false; State.SelectedOptiFabric = null; Component(DownloadInstallComponent.OptiFabric).Clear.Visibility = Visibility.Collapsed; State.AutoSelectedOptiFabric = false; Component(DownloadInstallComponent.Fabric).SetSwapped(true); host.ReloadSelected(); }
    public void FabricApiLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.FabricApi);
            if (!host.FabricApi.Finished() || State.VanillaName is null || State.SelectedFabric is null) return;
            List<TResource> versions = new();
            foreach (TResource version in host.FabricApi.Output() ?? []) if (IsFabricApiCompatible(version, State.VanillaName)) {
                if (!version.Display!.StartsWithF("[")) { Logger.Warn("已特判修改 Fabric API 显示名：" + version.Display); version.Display = "[" + State.VanillaName + "] " + version.Display; }
                versions.Add(version);
            }
            if (!versions.Any()) return; versions = versions.OrderByDescending(version => version.ReleaseDate).ToList();
            foreach (TResource version in versions) panel.Children.Add(host.FabricApiRow(version, FabricApiSelected));
            if (!State.AutoSelectedFabricApi) { State.AutoSelectedFabricApi = true; FrameworkElement row = host.Materialize((FrameworkElement)panel.Children[0]); Logger.Info("已自动选择 Fabric API：" + host.RowTitle(row)); FabricApiSelected(row, null); }
        } catch (Exception error) { Logger.Error(error, "可视化 Fabric API 安装版本列表出错"); }
    }
    public void FabricApiSelected(FrameworkElement row, EventArgs? args) { State.SelectedFabricApi = (TResource)row.Tag; Component(DownloadInstallComponent.FabricApi).SetSwapped(true); host.ReloadSelected(); }
    public void FabricApiClear() { State.SelectedFabricApi = null; Component(DownloadInstallComponent.FabricApi).SetSwapped(true); host.ReloadSelected(); }
    public void OptiFabricLoaded() {
        try {
            Panel panel = ClearComponentVersions(DownloadInstallComponent.OptiFabric);
            if (!host.OptiFabric.Finished() || State.VanillaName is null || State.SelectedFabric is null || State.SelectedOptiFine is null) return;
            List<TResource> versions = new(); foreach (TResource version in host.OptiFabric.Output() ?? []) if (IsOptiFabricCompatible(version, State.VanillaName)) versions.Add(version);
            if (!versions.Any()) return; versions = versions.OrderByDescending(version => version.ReleaseDate).ToList();
            foreach (TResource version in versions) panel.Children.Add(host.OptiFabricRow(version, OptiFabricSelected));
            if (State.AutoSelectedOptiFabric || (VanillaDrop >= 140 && VanillaDrop <= 150)) return;
            State.AutoSelectedOptiFabric = true; FrameworkElement row = host.Materialize((FrameworkElement)panel.Children[0]); Logger.Info("已自动选择 OptiFabric：" + host.RowTitle(row)); OptiFabricSelected(row, null);
        } catch (Exception error) { Logger.Error(error, "可视化 OptiFabric 安装版本列表出错"); }
    }
    public void OptiFabricSelected(FrameworkElement row, EventArgs? args) { State.SelectedOptiFabric = (TResource)row.Tag; Component(DownloadInstallComponent.OptiFabric).SetSwapped(true); host.ReloadSelected(); }
    public void OptiFabricClear() { State.SelectedOptiFabric = null; Component(DownloadInstallComponent.OptiFabric).SetSwapped(true); host.ReloadSelected(); }
}
