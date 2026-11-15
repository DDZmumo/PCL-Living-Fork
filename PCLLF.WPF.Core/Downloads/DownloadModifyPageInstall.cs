using System;
using System.Collections.Generic;
using System.Linq;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed partial class DownloadModifyPageController<TInstance, TState, TCard, TOptiFine, TForgeLike, TForge, TNeo, TLite, TResource, TRequest, TLoader, TLoading, TAnimation> {
    public void StartInstall(TRequest request) {
        if (!host.CanChangeMinecraft(State.InitialTarget?.MinecraftVersion, request.Components?.MinecraftVersion)) { host.Hint("整合包模式下不允许修改 Minecraft 主版本。", SelectionPageHint.Red); return; }
        UpdateStartButton(); if (!host.View().Start.IsEnabled) return;
        TInstance targetInstance = host.InitInput(); McComponents target = request.Components!; string name = request.NewInstanceName; bool changedName = !string.Equals(name, targetInstance.Name, StringComparison.Ordinal);
        request.NewInstanceName = changedName ? name : null!; request.InstanceFolder = targetInstance.PathVersion;
        List<string> warnings = new();
        if (host.ModpackEnabled() && !State.InitialTarget!.IsEquivalentTo(target)) warnings.Add("正在修改整合包的已安装组件。加载器、OptiFine 或辅助组件的版本变化可能导致 Mod 不兼容、整合包无法启动，请确认后继续。");
        if ((Selection.SelectedForge is not null || Selection.SelectedNeoForge is not null || Selection.SelectedFabric is not null) && !PathUtils.IsSame(targetInstance.PathIndie, targetInstance.PathVersion)) warnings.Add("该版本尚未开启版本隔离，可能与别的 MC 版本共用同一个 Mod 和存档文件夹。\r\n修改版本后，已有的 Mod、存档等需要手动迁移到新的游戏目录。");
        bool changedMinecraft = !Same(State.InitialTarget!.MinecraftVersion, target.MinecraftVersion);
        if (!State.IsVanillaVersionReliable && !changedMinecraft) warnings.Add($"PCL 未能精准识别该版本，请确认它是不是 Minecraft {State.InitialTarget.MinecraftVersion}，如果不正确，请取消并选择正确的版本！");
        if (!Configs.HintModifyInstanceWarning.Get()) warnings.Add("如果该版本安装了 PCL 不兼容的加载器，或经过第三方修改，这些改动将会丢失！");
        bool changedLoader = false;
        foreach (ModLoaders loader in State.InitialTarget.ModLoaderVersions.Keys.Union(target.ModLoaderVersions.Keys)) {
            bool hasCurrent = State.InitialTarget.ModLoaderVersions.TryGetValue(loader, out string? currentVersion), hasTarget = target.ModLoaderVersions.TryGetValue(loader, out string? targetVersion);
            if (hasCurrent != hasTarget || !Same(currentVersion, targetVersion)) { changedLoader = true; break; }
        }
        if ((changedLoader || changedMinecraft) && State.InitialTarget.ModLoaderVersions.Any()) warnings.Add("现有的 Mod、资源包等在修改后可能会不兼容！");
        if (warnings.Any() && host.Message(new SelectionPageMessage { Text = "请注意：\r\n• " + string.Join("\r\n• ", warnings), Title = "修改确认", Button1 = "开始修改", Button2 = "取消", IsWarn = true }) != 1) return;
        if (!Configs.HintModifyInstanceWarning.Get()) Configs.HintModifyInstanceWarning.Set(true);
        if (State.IsVanillaVersionReliable && State.InitialTarget.IsEquivalentTo(target)) {
            try {
                string oldPath = targetInstance.PathVersion; TInstance renamed = targetInstance.Rename(name);
                if (host.InstancePage() is not null && PathUtils.IsSame(host.InstancePage()!.PathVersion, oldPath)) host.SetInstancePage(renamed);
                if (host.SelectedInstance() is not null && PathUtils.IsSame(host.SelectedInstance()!.PathVersion, oldPath)) host.SetSelectedInstance(renamed);
                host.ReloadJava(); host.Hint("重命名成功！", SelectionPageHint.Green); host.RefreshInstances(); host.PageBack();
            } catch (Exception error) { Logger.Error(error, "重命名版本失败", LogBehavior.Alert); }
        } else if (host.Modify(request, targetInstance)) host.PageBack();
    }
}
