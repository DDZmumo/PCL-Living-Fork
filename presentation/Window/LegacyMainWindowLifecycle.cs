using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Animation;
using PCLLF.Presentation.Controls;

using PCLCS;
using PCLCS.Startup;

namespace PCLLF.Presentation.Windowing;

public sealed partial class LegacyMainWindowController<TStack, TInstance, TAnimation>
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    private const string ForkReleaseHighlights = "- PCLLF 原创独立代码采用 MPL-2.0，关于页可离线查看许可与来源\r\n- 个人基础设置同步独立侧栏，全局和版本设置均支持编辑后保存\r\n- 修复跨平台前置误报和模组更新漏报，保留本地依赖版本范围检查\r\n- 首页更多设置可统一收起面板，展开沿用 PCL 回弹动画\r\n- 修复深色主题提示框，完善原生开关、键位和拖动条对照\r\n- 预发布精简版适用于 Windows x64，运行前需 .NET 10 Desktop Runtime";
    // Session scope: at most one automatic version/update note per launcher start.
    private int updateLogPromptIssued;
    public bool ShouldAutoShowUpdateLog() => LegacyUpdateLogPrompt.ShouldShow(host.ReadString, host.UpdateLogIdentity());
    public void ShowAutomaticUpdateLog() {
        if (!ShouldAutoShowUpdateLog() || Interlocked.CompareExchange(ref updateLogPromptIssued, 1, 0) != 0) return;
        ShowReleaseLog(true);
    }
    public void ConstructBeforeXaml() {
        host.WriteStartTick(host.Clock()); host.AssignGlobalWindow(); host.CreateLaunchLeft(); host.CreateLaunchRight();
        int last = host.ReadInteger("SystemLastVersionReg");
        if (last < host.VersionCode) Upgrade(last); else if (last > host.VersionCode) Downgrade(last);
        if (!host.HasSaved("LaunchArgumentIndieV2")) {
            if (host.HasSaved("LaunchArgumentIndie")) {
                Logger.Info("从老 PCL 迁移版本隔离"); host.WriteSetting("LaunchArgumentIndieV2", host.ReadInteger("LaunchArgumentIndie"));
            } else if (host.HasIniKey("Setup", "LaunchVersionSelect")) {
                Logger.Info("从老 PCL 升级，但此前未调整版本隔离，使用老的版本隔离默认值"); host.WriteSetting("LaunchArgumentIndieV2", host.DefaultValue("LaunchArgumentIndie"));
            } else {
                Logger.Info("全新的 PCL，使用新的版本隔离默认值"); host.WriteSetting("LaunchArgumentIndieV2", host.DefaultValue("LaunchArgumentIndieV2"));
            }
        }
        host.ThemeRefresh(host.ReadInteger("UiLauncherTheme"));
        Window.AddHandler(DragDrop.DragEnterEvent, host.DragHandler, true); Window.AddHandler(DragDrop.DragOverEvent, host.DragHandler, true);
    }
    public void ConstructAfterXaml() {
        host.ApplyPackVisibility(Window); if (host.PackEnabled()) UpdateTitleBar(); Window.Opacity = 0;
        if (host.IsAdmin()) {
            LegacyMainDragHelperPort helper = host.CreateDragHelper();
            Window.SourceInitialized += (_, _) => { WindowInteropHelper window = new(Window); helper.SetSource(HwndSource.FromHwnd(window.Handle)); helper.AddHook(); };
            Window.Closing += (_, _) => helper.RemoveHook(); helper.ConnectDrop(() => FileDrag(helper.Files()));
        }
        MyPageLeftView left = host.LeftPage((int)LegacyMainPage.Launch).Element;
        MyPageRightView right = host.RightPage((int)LegacyMainPage.Launch);
        if (left.Parent is not null) left.SetValue(ContentPresenter.ContentProperty, null);
        if (right.Parent is not null) right.SetValue(ContentPresenter.ContentProperty, null);
        view.PanMainLeft().Child = left; PageLeft = left; view.PanMainRight().Child = right; PageRight = right; right.NativePageState = MyPageRightView.PageStates.ContentStay;
        if (host.BuildType == 100) Hint("[开发者模式] PCL 正以开发者模式运行，这可能会造成严重的性能下降，请务必立即向开发者反馈此问题！", LegacyMainHint.Red);
        if (host.IsDebug()) Hint("[调试模式] PCL 正以调试模式运行，这可能会导致性能下降，若无必要请不要开启！");
        host.StartFolderLoader(); Logger.Info($"第二阶段加载用时：{checked(host.Clock() - host.StartTick())} ms");
    }
    public void Loaded() {
        host.WriteStartTick(host.Clock()); host.WriteHandle(new WindowInteropHelper(Window).Handle);
        UpdateBackgroundAndTitleBar(); host.RefreshHidden(); host.RefreshBackground(false, true); host.RefreshMusic(false, true);
        view.ExtraDownload().SetShowCheck(DownloadShowCheck); view.ExtraBack().SetShowCheck(BackShowCheck); view.ExtraApril().SetShowCheck(AprilShowCheck); view.ExtraShutdown().SetShowCheck(ShutdownShowCheck); view.ExtraApril().ShowRefresh();
        WindowResizer resizer = host.CreateResizer(Window);
        resizer.addResizerDown(view.Resizer(0)); resizer.addResizerLeft(view.Resizer(1)); resizer.addResizerLeftDown(view.Resizer(2)); resizer.addResizerLeftUp(view.Resizer(3));
        resizer.addResizerRight(view.Resizer(4)); resizer.addResizerRightDown(view.Resizer(5)); resizer.addResizerRightUp(view.Resizer(6)); resizer.addResizerUp(view.Resizer(7));
        if (host.RandomInteger(1, 1000) == 233) view.ShapeTitleLogo().Data = (Geometry)new GeometryConverter().ConvertFromString("M26,29 v-25 h6 a7,7 180 0 1 0,14 h-6 M83,6.5 a10,11.5 180 1 0 0,18 M48,2.5 v24.5 h13.5")!;
        host.ThemeRefreshMain();
        try { Window.Height = host.ReadInteger("WindowHeight"); Window.Width = host.ReadInteger("WindowWidth"); }
        catch (Exception ex) { Logger.Error(ex, "读取窗口默认大小失败", LogBehavior.Toast); Window.Height = Window.MinHeight + 100; Window.Width = Window.MinWidth + 100; }
        Window.Topmost = false;
        if (host.HasStartWindow()) host.CloseStartWindow(new TimeSpan(0, 0, 0, 0, Conversions.ToInteger(400 / Animations.Speed())));
        Window.Top = (host.PixelToWpf(host.WorkingAreaHeight()) - Window.Height) / 2; Window.Left = (host.PixelToWpf(host.WorkingAreaWidth()) - Window.Width) / 2;
        host.WriteSizeSaveable(true); ShowWindowToTop();
        HwndSource source = (HwndSource)PresentationSource.FromVisual(Window); source.AddHook(host.WindowHook);
        AniStart([
            AaCode(() => AniControlEnabled = checked(AniControlEnabled - 1), 50),
            AaOpacity(Window, host.ReadInteger("UiLauncherTransparent") / 1000d + 0.4, 250, 100),
            AaDouble(i => view.TransformPos().Y += i, -view.TransformPos().Y, 600, 100, Back(AnimationEasePower.Weak)),
            AaDouble(i => view.TransformRotate().Angle += i, -view.TransformRotate().Angle, 500, 100, Back(AnimationEasePower.Weak)),
            AaCode(() => { view.PanBack().RenderTransform = null; host.WriteLoaded(true); Logger.Info($"DPI：{host.Dpi()}，系统版本：{Environment.OSVersion.VersionString}，PCL 位置：{host.ExecutablePath()}"); }, after: true)
        ], "Form Show");
        host.StartAnimationLoop(); host.StartMainTimer();
        RunThread(() => {
            const int eulaVersion = 2;
            if (host.ReadInteger("SystemEulaVersion") < eulaVersion) {
                switch (Message(host.ReadInteger("SystemEulaVersion") == 0 ? "在使用 PCL 前，请先阅读用户协议与免责声明。" : "PCL 的用户协议与免责声明已更新。\r\n请阅读更新后的用户协议与免责声明。", "协议授权", "同意", "拒绝", "查看用户协议与免责声明", () => host.OpenWebsite("https://shimo.im/docs/rGrd8pY8xWkt6ryW"))) {
                    case 1: host.WriteSetting("SystemEulaVersion", eulaVersion); break;
                    case 2: EndProgram(false); break;
                }
            }
            try { host.JavaInitialize(); System.Threading.Thread.Sleep(100); host.StartClientList(); RunThread(host.ClearTaskTemp, "TryClearTaskTemp", ThreadPriority.BelowNormal); }
            catch (Exception ex) { Logger.Error(ex, "初始化加载池运行失败"); }
            CheckStartupUpdates();
        }, "初始化", ThreadPriority.Lowest);
        Logger.Info($"第三阶段加载用时：{checked(host.Clock() - host.StartTick())} ms");
    }
    public void CheckStartupUpdates() {
        if (host.VersionBranchMain() == "OpenSource") ShowAutomaticUpdateLog();
        host.CheckUpdates();
    }
    public void ShowUpdateLog() => ShowReleaseLog(false);
    private void ShowReleaseLog(bool automatic) => RunThread(() => {
        if (host.ShowReleaseNotes(automatic, ForkReleaseHighlights)) return;
        string content = $"{host.LauncherName()} {host.VersionBaseName()}\r\n\r\n" + ForkReleaseHighlights;
        switch (Message(content, host.LauncherName() + " 更新说明", "确定", "查看更新日志")) {
            case 2: host.OpenWebsite(host.ReleasesUrl()); break;
        }
    }, "Fork UpdateLog Output");
    public void Upgrade(int lastVersion) {
        Logger.Info($"版本号从 {lastVersion} 升高到 {host.VersionCode}"); host.WriteSetting("SystemLastVersionReg", host.VersionCode);
        string settingKey = host.BuildType == 0 ? "SystemHighestAlphaVersionReg" : "SystemHighestBetaVersionReg";
        int highest = host.ReadInteger(settingKey);
        if (highest < host.VersionCode) { host.WriteSetting(settingKey, host.VersionCode); Logger.Info($"最高版本号从 {highest} 升高到 {host.VersionCode}"); }
        if (host.ReadInteger("LaunchArgumentWindowType") == 5) host.WriteSetting("LaunchArgumentWindowType", 1);
        if (highest <= 207) {
            List<string> unlocked = new() { "2" }; unlocked.AddRange(new List<string>(host.ReadString("UiLauncherThemeHide").ToString().Split('|'))); unlocked.AddRange(new List<string>(host.ReadString("UiLauncherThemeHide2").ToString().Split('|')));
            host.WriteSetting("UiLauncherThemeHide2", unlocked.Distinct().ToList().Join('|'));
        }
        if (lastVersion <= 115 && host.ReadString("UiLauncherThemeHide2").ToString().Split('|').Contains("13")) {
            List<string> unlocked = new(host.ReadString("UiLauncherThemeHide2").ToString().Split('|')); unlocked.Remove("13"); host.WriteSetting("UiLauncherThemeHide2", unlocked.Join('|'));
            Message("由于新版 PCL 修改了欧皇彩的解锁方式，你需要重新解锁欧皇彩。\r\n多谢各位的理解啦！", "重新解锁提醒");
        }
        if (lastVersion <= 152 && host.ReadString("UiLauncherThemeHide2").ToString().Split('|').Contains("12")) {
            List<string> unlocked = new(host.ReadString("UiLauncherThemeHide2").ToString().Split('|')); unlocked.Remove("12"); host.WriteSetting("UiLauncherThemeHide2", unlocked.Join('|'));
            Message("由于新版 PCL 修改了滑稽彩的解锁方式，你需要重新解锁滑稽彩。\r\n多谢各位的理解啦！", "重新解锁提醒");
        }
        if (lastVersion <= 161 && FileUtils.Exists(Paths.Base + @"PCL\CustomSkin.png") && !FileUtils.Exists(Paths.AppDataThenName + "CustomSkin.png")) { FileUtils.Copy(Paths.Base + @"PCL\CustomSkin.png", Paths.AppDataThenName + "CustomSkin.png"); Logger.Info("已移动离线自定义皮肤 (162)"); }
        if (lastVersion <= 263 && FileUtils.Exists(host.TempFolder() + "CustomSkin.png") && !FileUtils.Exists(Paths.AppDataThenName + "CustomSkin.png")) { FileUtils.Copy(host.TempFolder() + "CustomSkin.png", Paths.AppDataThenName + "CustomSkin.png"); Logger.Info("已移动离线自定义皮肤 (264)"); }
        if (lastVersion <= 205) { host.WriteSetting("UiHiddenOtherHelp", false); Logger.Info("已解除帮助页面的隐藏"); }
        if (host.HasSaved("ToolDownloadTranslate") && !host.HasSaved("ToolDownloadTranslateV2")) { host.WriteSetting("ToolDownloadTranslateV2", checked(host.ReadInteger("ToolDownloadTranslate") + 1)); Logger.Info("已从老版本迁移 Mod 命名设置"); }
        if (lastVersion <= 381 && host.HasSaved("LaunchAdvanceJvm") && Operators.ConditionalCompareObjectEqual(host.ReadString("LaunchAdvanceJvm").ToString().Replace("-XX:+UseG1GC ", "").Replace("-XX:-UseAdaptiveSizePolicy ", "").Trim(), host.DefaultValue("LaunchAdvanceJvm"), false)) { host.ResetSetting("LaunchAdvanceJvm"); Logger.Info("已重置 JVM 参数设置"); }
        if (lastVersion <= 0 || highest >= host.VersionCode) return;
        ShowAutomaticUpdateLog();
    }
    public void Downgrade(int lastVersion) { Logger.Info($"版本号从 {lastVersion} 降低到 {host.VersionCode}"); host.WriteSetting("SystemLastVersionReg", host.VersionCode); }
}
