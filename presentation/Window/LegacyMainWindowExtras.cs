using System;
using System.Windows;
using MeloongCore;
using PCLLF.Presentation.Controls;

using PCLCS.Startup;

namespace PCLLF.Presentation.Windowing;

public sealed partial class LegacyMainWindowController<TStack, TInstance, TAnimation>
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    public void AprilGiveup() {
        if (!host.AprilEnabled() || host.AprilGivenUp()) return;
        Hint("=D", LegacyMainHint.Green); host.WriteAprilGivenUp(true);
        if (host.AprilScale() is { } scale) { scale.ScaleX = 1; scale.ScaleY = 1; }
    }
    public bool AprilShowCheck() => host.AprilEnabled() && !host.AprilGivenUp() && IsPage(PageCurrent, LegacyMainPage.Launch);
    public void ShutdownClick() {
        try { if (host.HasLaunchOperation()) host.CancelLaunchOperation(); foreach (Action kill in host.WatcherKills()) kill(); Hint("已关闭运行中的 Minecraft！", LegacyMainHint.Green); }
        catch (Exception ex) { Logger.Error(ex, "强制关闭所有 Minecraft 失败"); }
    }
    public bool ShutdownShowCheck() => host.HasRunningMinecraft();
}
