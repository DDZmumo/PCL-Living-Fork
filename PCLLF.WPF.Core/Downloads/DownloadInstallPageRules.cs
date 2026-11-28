using System;
using System.Linq;
using Microsoft.VisualBasic.CompilerServices;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class DownloadInstallPageController<TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TAnimation> {
    public string? GetLoaderError(DownloadInstallLoadingView? loader) {
        if (loader is null || !loader.IsLoader()) return "获取中……";
        int state = loader.LoadingState();
        if (state == host.LoadingRun) return "获取中……";
        if (state == host.LoadingError) { string message = loader.Error().Message; return Same(message, "无") ? "无" : "获取失败：" + message; }
        return state == host.LoadingUnloaded ? "未知错误，状态为 Unloaded" : null;
    }
    private string? LoaderError(DownloadInstallComponent kind) => GetLoaderError(host.Loading(kind));
    public string? LoadOptiFineGetError() {
        if (State.SelectedNeoForge is not null) return "与 NeoForge 不兼容";
        if (State.SelectedForge is not null && McUtils.CompareVersionGE(State.VanillaName!, "1.13") && McUtils.CompareVersionGE("1.14.3", State.VanillaName!)) return "与 Forge 不兼容";
        if (State.SelectedFabric is not null && McUtils.CompareVersion(State.VanillaName!, "1.20.4") > 0) return "与 Fabric 不兼容";
        if (LoaderError(DownloadInstallComponent.OptiFine) is { } error) return error;
        if (!host.OptiFine.Finished()) return "获取中……";
        bool any = false, required = false;
        foreach (TOptiFine version in host.OptiFine.Output() ?? []) {
            if (!version.DisplayName.StartsWithF(State.VanillaName + " ")) continue; any = true;
            if (State.SelectedForge is null || IsOptiFineSuitForForge(version, State.SelectedForge)) return null;
            if (version.RequiredForgeVersion is not null) required = true;
        }
        return !any ? "无" : required ? "仅兼容特定版本的 Forge" : "与 Forge 不兼容";
    }
    public static bool IsOptiFineSuitForForge(TOptiFine optiFine, TForge forge) {
        if (!Same(forge.Inherit, optiFine.Inherit) || optiFine.RequiredForgeVersion is null) return false;
        if (string.IsNullOrWhiteSpace(optiFine.RequiredForgeVersion)) return true;
        return optiFine.RequiredForgeVersion!.Contains(".") ? McUtils.CompareVersion(forge.Version.ToString(), optiFine.RequiredForgeVersion) == 0 : Operators.ConditionalCompareObjectEqual(forge.Version.Revision, optiFine.RequiredForgeVersion, false);
    }
    public string? LoadLiteLoaderGetError() {
        if (LoaderError(DownloadInstallComponent.LiteLoader) is { } error) return error;
        if (!host.LiteLoader.Finished()) return "获取中……";
        return host.LiteLoader.Output()?.Any(version => Same(version.Inherit, State.VanillaName)) == true ? null : "无";
    }

    private bool IsCurrentForgeInstaller(TForge version) => Same(version.Inherit, State.VanillaName) &&
        !Same(version.Category, "universal") && !Same(version.Category, "client");

    public string? LoadForgeGetError() {
        if (State.VanillaName is not { } minecraft || !McVersion.IsStandardFormat(minecraft) ||
            McUtils.CompareVersionGE("1.5.1", minecraft) && McUtils.CompareVersionGE(minecraft, "1.1")) return "无";
        DownloadInstallForgeSource<TForge> loader = host.Forge();
        if (!Same(minecraft, loader.Input())) return "获取中……";
        if (LoaderError(DownloadInstallComponent.Forge) is { } error) return error;
        if (!loader.Finished()) return "获取中……";
        bool any = false;
        foreach (TForge version in (loader.Output() ?? []).Where(IsCurrentForgeInstaller)) {
            any = true;
            if (State.SelectedNeoForge is not null) return "与 NeoForge 不兼容";
            if (State.SelectedFabric is not null) return "与 Fabric 不兼容";
            if (State.SelectedOptiFine is not null && McUtils.CompareVersionGE(minecraft, "1.13") && McUtils.CompareVersionGE("1.14.3", minecraft)) return "与 OptiFine 不兼容";
            if (State.SelectedOptiFine is not null && !IsOptiFineSuitForForge(State.SelectedOptiFine, version)) continue;
            return null;
        }
        return any ? "与 OptiFine 不兼容" : "无";
    }
    public string? LoadNeoForgeGetError() {
        if (State.SelectedOptiFine is not null) return "与 OptiFine 不兼容";
        if (State.SelectedForge is not null) return "与 Forge 不兼容";
        if (State.SelectedFabric is not null) return "与 Fabric 不兼容";
        if (LoaderError(DownloadInstallComponent.NeoForge) is { } error) return error;
        if (!host.NeoForge.Finished()) return "获取中……";
        return host.NeoForge.Output()?.Any(version => Same(version.Inherit, State.VanillaName)) == true ? null : "无";
    }

    private System.Collections.Generic.IEnumerable<JObject> FabricLoaders() {
        if (State.VanillaName is not { } minecraft || host.Fabric.Output() is not { } output ||
            output["game"] is not JArray games || output["loader"] is not JArray loaders) return [];
        string normalized = minecraft.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3");
        return games.OfType<JObject>().Any(game => game["version"] is JValue { Value: string name } && Same(name, normalized))
            ? loaders.OfType<JObject>().Where(loader => loader["version"]?.Type == JTokenType.String)
            : [];
    }

    public string? LoadFabricGetError() {
        if (State.SelectedOptiFine is not null && McUtils.CompareVersionGE(State.VanillaName!, "1.20.5")) return "与 OptiFine 不兼容";
        if (LoaderError(DownloadInstallComponent.Fabric) is { } error) return error;
        if (!host.Fabric.Finished()) return "获取中……";
        if (!FabricLoaders().Any()) return "无";
        if (State.SelectedForge is not null) return "与 Forge 不兼容";
        if (State.SelectedNeoForge is not null) return "与 NeoForge 不兼容";
        return null;
    }
    public static bool IsFabricApiCompatible(TResource fabricApi, string? minecraftVersion) {
        string? name = fabricApi.Display;
        try {
            if (name is null || minecraftVersion is null) return false;
            name = name.Lower(); string target = minecraftVersion.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3").Lower();
            if (name.StartsWithF("[" + target + "]")) return true;
            if (!name.Contains("/") || !name.Contains("]")) return false;
            foreach (string part in name.BeforeFirst("]").TrimStart('[').Split('/')) if (Same(part, target)) return true;
            var left = name.BeforeFirst("]").RegexSearch("[a-z/]+|[0-9/]+").ToList(); var right = target.BeforeFirst("]").RegexSearch("[a-z/]+|[0-9/]+").ToList();
            int index = 0;
            while (true) {
                if (left.Count - 1 < index && right.Count - 1 < index) return true;
                string l = left.Count - 1 < index ? "-1" : left[index], r = right.Count - 1 < index ? "-1" : right[index];
                if (!l.Contains("/")) { if (!Same(l, r)) return false; } else if (!l.Contains(r)) return false;
                index = checked(index + 1);
            }
        } catch (Exception error) { Logger.Warn(error, "判断 Fabric API 版本适配性出错（" + name + ", " + minecraftVersion + "）"); return false; }
    }
    public string? LoadFabricApiGetError() {
        if (LoaderError(DownloadInstallComponent.FabricApi) is { } error) return error;
        if (!host.FabricApi.Finished()) return State.SelectedFabric is null ? "需要安装 Fabric" : "获取中……";
        return host.FabricApi.Output()?.Any(file => IsFabricApiCompatible(file, State.VanillaName)) == true
            ? State.SelectedFabric is null ? "需要安装 Fabric" : null : "无";
    }
    public static bool IsOptiFabricCompatible(TResource modFile, string? minecraftVersion) {
        try { return minecraftVersion is not null && modFile.GameVersions.Contains(minecraftVersion); }
        catch (Exception error) { Logger.Warn(error, "判断 OptiFabric 版本适配性出错（" + minecraftVersion + "）"); return false; }
    }
    public string? LoadOptiFabricGetError() {
        if (VanillaDrop >= 140 && VanillaDrop <= 150) return "不兼容老版本 Fabric，请手动下载 OptiFabric Origins";
        if (LoaderError(DownloadInstallComponent.OptiFabric) is { } error) return error;
        if (!host.OptiFabric.Finished()) {
            if (State.SelectedFabric is null && State.SelectedOptiFine is null) return "需要安装 OptiFine 与 Fabric";
            if (State.SelectedFabric is null) return "需要安装 Fabric"; if (State.SelectedOptiFine is null) return "需要安装 OptiFine"; return "获取中……";
        }
        foreach (TResource version in host.OptiFabric.Output() ?? []) {
            if (!IsOptiFabricCompatible(version, State.VanillaName)) continue;
            if (State.SelectedFabric is null && State.SelectedOptiFine is null) return "需要安装 OptiFine 与 Fabric";
            if (State.SelectedFabric is null) return "需要安装 Fabric"; if (State.SelectedOptiFine is null) return "需要安装 OptiFine"; return null;
        }
        return "无";
    }
    public void PreviewSwap(DownloadInstallComponent kind, Action cancel) {
        string? error = kind switch { DownloadInstallComponent.OptiFine => LoadOptiFineGetError(), DownloadInstallComponent.LiteLoader => LoadLiteLoaderGetError(), DownloadInstallComponent.Forge => LoadForgeGetError(), DownloadInstallComponent.NeoForge => LoadNeoForgeGetError(), DownloadInstallComponent.Fabric => LoadFabricGetError(), DownloadInstallComponent.FabricApi => LoadFabricApiGetError(), _ => LoadOptiFabricGetError() };
        if (error is not null) cancel();
    }
}
