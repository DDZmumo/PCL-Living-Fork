using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLLF.Presentation.Launch;

public sealed partial class LegacyLaunchLeft<TInstance, TAnimation> where TInstance : class where TAnimation : struct {
    private readonly LegacyLaunchLeftView view;
    private readonly LegacyLaunchLeftHost<TInstance> host;
    private readonly LegacyLaunchAnimations<TAnimation> animation;
    private bool isLoaded, loadFinished;
    private LegacyLoginPageKind currentPage;
    private int launchButtonState;
    private TInstance? launchButtonInstance;
    public LegacyLaunchLeft(LegacyLaunchLeftView view, LegacyLaunchLeftHost<TInstance> host, LegacyLaunchAnimations<TAnimation> animation) { this.view = view; this.host = host; this.animation = animation; }
    public void Loaded() {
        if (isLoaded) RefreshPage(true, false);
        view.AprilPosition().X = 0; view.AprilPosition().Y = 0;
        if (isLoaded) return;
        isLoaded = true; host.ChangeAnimationControl(1);
        host.BindListStates(RefreshButtonsUI); RefreshButtonsUI();
        host.RunInNewThread(() => {
            string? pack = null;
            if (FileUtils.Exists(host.BasePath() + "modpack.zip")) pack = host.BasePath() + "modpack.zip";
            if (FileUtils.Exists(host.BasePath() + "modpack.mrpack")) pack = host.BasePath() + "modpack.mrpack";
            if (pack is not null) { Logger.Warn($"需自动安装整合包：{pack}"); host.CreateCurrentFolder(); host.WaitForFolders(false); }
            if (string.IsNullOrEmpty(host.Folder()) || !DirectoryUtils.Exists(host.Folder())) {
                if (string.IsNullOrEmpty(host.Folder())) Logger.Info("没有已储存的 Minecraft 文件夹"); else Logger.Warn($"Minecraft 文件夹无效，该文件夹已不存在：{host.Folder()}");
                host.WaitForFolders(true); host.SetFolder(host.FirstFolderLocation());
            }
            if (host.ReadBoolean("SystemDebugDelay")) Thread.Sleep(host.RandomInteger(500, 3000));
            if (pack is not null) {
                try {
                    LegacyLaunchJobPort? installer = host.InstallModpack(pack); Logger.Info($"自动安装整合包已开始：{pack}"); installer!.Wait(false);
                    if (installer.State() == LegacyLaunchPanelState.Finished) { Logger.Info($"自动安装整合包成功，清理安装包：{pack}"); FileUtils.Delete(pack); }
                } catch (Exception error) { if (error.IsCanceled()) Logger.Info($"自动安装整合包已取消：{pack}"); else Logger.Error(error, $"自动安装整合包失败：{pack}", LogBehavior.Alert); }
            }
            string selection = host.ReadIniVersion(host.Folder() + "PCL.ini"); TInstance? instance = string.IsNullOrEmpty(selection) ? null : host.CreateInstance(selection);
            if (instance is null || !host.IsParentPath(host.InstancePath(instance)) || !host.CheckInstance(instance)) {
                Logger.Info($"当前选择的 Minecraft 版本无效：{(instance is null ? "null" : host.InstancePath(instance))}", instance is null ? LogBehavior.None : LogBehavior.ToastIfDebug);
                if (host.InstanceListState() != LegacyLaunchPanelState.Finished) host.WaitForInstanceFolder(host.Folder());
                if (!host.Instances().Any() || host.InstanceLogo(host.Instances().First()[0]).Contains("RedstoneBlock")) { instance = null; Logger.Info("无可用 Minecraft 版本"); }
                else { instance = host.Instances().First()[0]; Logger.Info($"自动选择 Minecraft 版本：{host.InstancePath(instance)}"); }
            }
            host.Dispatch(() => {
                host.SetSelected(instance); loadFinished = true; RefreshButtonsUI(); RefreshPage(false, false);
                if (string.IsNullOrEmpty(host.LoginAble())) host.StartAutoLogin();
                if (host.CommandLine().Contains("--test")) host.RunInThread(() => {
                    Thread.Sleep(500); host.Dispatch(() => host.TestNavigate(0, 0)); Thread.Sleep(500); host.Dispatch(() => host.TestNavigate(0, 1));
                    Thread.Sleep(500); host.Dispatch(() => host.TestNavigate(1, 0)); Thread.Sleep(500); host.Dispatch(() => host.TestNavigate(2, 0));
                    Thread.Sleep(500); host.Dispatch(VersionClick); Thread.Sleep(500); host.Dispatch(MoreClick); Thread.Sleep(500); host.EndProgramForce(123);
                });
            });
        }, "Version Check", ThreadPriority.AboveNormal);
        int loginType = host.LoginType(); if (loginType == 0 || loginType == 5) view.RequireNamedRadio("RadioLoginType" + loginType);
        RefreshPage(false, false); host.ChangeAnimationControl(-1);
    }
    public void PageChangeToLaunching() {
        switch (host.LoginType()) { case 0: view.Method().Text = "离线登录"; break; case 5: view.Method().Text = "正版登录"; break; case 2: view.Method().Text = "统一通行证"; break; case 3: view.Method().Text = "Authlib-Injector"; break; }
        view.Name().Text = host.InstanceName(host.Selected()!); view.Stage().Text = "初始化"; view.Title().Text = host.ExportScriptSafe() ? "正在导出启动脚本" : "正在启动游戏";
        view.Progress().Text = "0.00 %"; view.Progress().Opacity = 1; view.Download().Visibility = Visibility.Visible; view.ProgressLeft().Opacity = 0.6;
        view.Download().Visibility = Visibility.Visible; view.Download().Text = "0 B/s"; view.Download().Opacity = 0; view.Download().Visibility = Visibility.Collapsed; view.DownloadLeft().Opacity = 0; view.DownloadLeft().Visibility = Visibility.Collapsed;
        view.FinishedColumn().Width = new GridLength(0, GridUnitType.Star); view.UnfinishedColumn().Width = new GridLength(1, GridUnitType.Star);
        view.LaunchingHint().Opacity = 0; view.LaunchingHint().Visibility = Visibility.Collapsed; view.LaunchingInfo().Width = double.NaN;
        host.ResetLaunchProcessAndWatcher(); view.Hint().Text = host.RandomHint();
        view.Input().IsHitTestVisible = false; view.Launching().IsHitTestVisible = false; view.SetLoadingRun(true); view.Launching().Visibility = Visibility.Visible;
        animation.Start(new[] {
            animation.Opacity(view.Input(), 0, 50, 0, LegacyLaunchEase.Linear, false), animation.Opacity(view.Input(), -view.Input().Opacity, 110, 0, LegacyLaunchEase.InFluent, true),
            animation.ScaleTransform(view.Input(), 1.2 - ((ScaleTransform)view.Input().RenderTransform).ScaleX, 160, 0, LegacyLaunchEase.Linear, false),
            animation.Opacity(view.Launching(), 1 - view.Launching().Opacity, 150, 100, LegacyLaunchEase.Linear, false),
            animation.ScaleTransform(view.Launching(), 1 - ((ScaleTransform)view.Launching().RenderTransform).ScaleX, 500, 100, LegacyLaunchEase.OutBackWeak, false),
            animation.Code(() => view.Launching().IsHitTestVisible = true, 150, false)
        }, "Launch State Page", false);
    }
    public void PageChangeToLogin() {
        host.ReloadPage(GetPage(currentPage), false); view.Input().IsHitTestVisible = false; view.Launching().IsHitTestVisible = false; view.SetLoadingRun(false); view.Input().Visibility = Visibility.Visible;
        animation.Start(new[] {
            animation.Opacity(view.Launching(), -view.Launching().Opacity, 150, 0, LegacyLaunchEase.Linear, false), animation.ScaleTransform(view.Launching(), 0.8 - ((ScaleTransform)view.Launching().RenderTransform).ScaleX, 150, 0, LegacyLaunchEase.OutFluentWeak, false),
            animation.Opacity(view.Input(), 1 - view.Input().Opacity, 250, 50, LegacyLaunchEase.Linear, false), animation.ScaleTransform(view.Input(), 1 - ((ScaleTransform)view.Input().RenderTransform).ScaleX, 300, 50, LegacyLaunchEase.OutBackWeak, false),
            animation.Code(() => view.Input().IsHitTestVisible = true, 200, false)
        }, "Launch State Page", true);
    }
    private FrameworkElement? GetPage(LegacyLoginPageKind kind) {
        switch (kind) {
            case LegacyLoginPageKind.Legacy: case LegacyLoginPageKind.Nide: case LegacyLoginPageKind.NideSkin: case LegacyLoginPageKind.Auth: case LegacyLoginPageKind.AuthSkin: case LegacyLoginPageKind.Ms: case LegacyLoginPageKind.MsSkin:
                if (host.ReadPage(kind) is null) host.StorePage(kind, host.CreatePage(kind)); return host.ReadPage(kind);
            default: throw new ArgumentOutOfRangeException("Type", "即将切换的登录分页编号越界");
        }
    }
    private FrameworkElement? ChangePage(LegacyLoginPageKind kind, bool animate) {
        FrameworkElement? page = host.ReadPage(LegacyLoginPageKind.Ms);
        try {
            if (currentPage == kind) return page;
            page = GetPage(kind); animation.Stop("FrmLogin PageChange");
            if (page is not null && page.Parent is not null) page.SetValue(ContentPresenter.ContentProperty, null);
            if (animate) view.RenderDispatchWait(() => animation.Start(new[] {
                animation.Opacity(view.LoginPanel(), -view.LoginPanel().Opacity, 100, 0, LegacyLaunchEase.OutFluent, false),
                animation.Code(() => { host.ChangeAnimationControl(1); view.LoginChildren().Children.Clear(); view.LoginChildren().Children.Add(page); host.ChangeAnimationControl(-1); }, 100, false),
                animation.Opacity(view.LoginPanel(), 1, 100, 120, LegacyLaunchEase.InFluent, false)
            }, "FrmLogin PageChange", false));
            else { host.ChangeAnimationControl(1); view.LoginChildren().Children.Clear(); view.LoginChildren().Children.Add(page); host.ChangeAnimationControl(-1); }
            currentPage = kind; return page;
        } catch (Exception error) { Logger.Error(error, $"切换登录分页失败（{kind}）"); return page; }
    }
    public void RefreshPage(bool KeepInput, bool Anim) {
        LegacyLoginPageKind kind; int loginPage;
        if (host.Selected() is not null) { loginPage = host.ReadInstanceInteger("VersionServerLogin"); host.WriteSetting("LoginPageType", loginPage); }
        else loginPage = host.ReadInteger("LoginPageType");
        switch (loginPage) {
            case 0:
            Unknown:
                if (view.MicrosoftChecked()) { kind = string.IsNullOrEmpty(host.ReadString("CacheMsV2Access")) ? LegacyLoginPageKind.Ms : LegacyLoginPageKind.MsSkin; host.SetLoginType(5); }
                else { kind = LegacyLoginPageKind.Legacy; host.SetLoginType(0); }
                view.TypePanel().Visibility = Visibility.Visible; view.SingleTypePanel().Visibility = Visibility.Collapsed; view.MicrosoftRadio().Visibility = Visibility.Visible; view.OfflineRadio().Visibility = Visibility.Visible; break;
            case 1:
                kind = string.IsNullOrEmpty(host.ReadString("CacheMsV2Access")) ? LegacyLoginPageKind.Ms : LegacyLoginPageKind.MsSkin; host.SetLoginType(5);
                SingleType(host.ShieldLogo(), "正版登录"); view.MicrosoftRadio().Visibility = Visibility.Visible; view.OfflineRadio().Visibility = Visibility.Collapsed; break;
            case 2: kind = LegacyLoginPageKind.Legacy; host.SetLoginType(0); SingleType(host.OfflineLogo(), "离线登录"); break;
            case 3: kind = string.IsNullOrEmpty(host.ReadString("CacheNideAccess")) ? LegacyLoginPageKind.Nide : LegacyLoginPageKind.NideSkin; host.SetLoginType(2); SingleType(host.CardLogo(), "统一通行证登录"); break;
            case 4:
                kind = string.IsNullOrEmpty(host.ReadString("CacheAuthAccess")) ? LegacyLoginPageKind.Auth : LegacyLoginPageKind.AuthSkin; host.SetLoginType(3);
                SingleTypeVisual(host.CardLogo());
                view.SingleTypeLabel().Text = host.Selected() is null ? host.ReadString("CacheAuthServerName") : host.ReadInstanceString("VersionServerAuthName");
                if (string.IsNullOrEmpty(view.SingleTypeLabel().Text)) view.SingleTypeLabel().Text = "第三方登录"; break;
            default: Logger.Error($"未知的登录页面：{loginPage}", LogBehavior.Toast); goto Unknown;
        }
        if (currentPage == kind) return;
        host.ReloadPage(ChangePage(kind, Anim), KeepInput); view.CheckNamedRadio("RadioLoginType" + host.LoginType());
    }
    private void SingleType(string logo, string label) {
        SingleTypeVisual(logo); view.SingleTypeLabel().Text = label;
    }
    private void SingleTypeVisual(string logo) {
        view.TypePanel().Visibility = Visibility.Collapsed; view.SingleTypePanel().Visibility = Visibility.Visible; view.SingleTypePath().Data = (Geometry)new GeometryConverter().ConvertFromString(logo)!;
    }
    public void RadioChanged(bool RaiseByMouse) { if (RaiseByMouse) RefreshPage(true, true); }
    public void VersionClick() { if (host.LaunchState() == LegacyLaunchPanelState.Loading) return; host.NavigateInstances(); }
    public void LaunchButtonClick() {
        if (host.LaunchState() == LegacyLaunchPanelState.Loading || !view.LaunchButtonEnabled() || !host.RightPageAllowsLaunch()) return;
        if (host.AprilEnabled() && !host.AprilGiveUp()) { host.SetAprilGiveUp(true); host.WriteSetting("AprilYear", DateTime.Now.Year); host.ResetGlobalAprilTransforms(); host.RefreshAprilButton(); }
        if (view.LaunchButtonText() == "启动游戏") host.StartGame(); else if (view.LaunchButtonText() == "下载游戏") host.NavigateDownloadInstall();
    }
    public void RefreshButtonsUI() {
        if (!view.LaunchButtonLoaded()) return;
        int state;
        if (!loadFinished || host.InstanceListState() == LegacyLaunchPanelState.Loading || host.FolderListState() == LegacyLaunchPanelState.Loading) state = 0;
        else if (host.Selected() is null || !host.AllowsInstance(host.InstancePath(host.Selected()!))) state = host.ModpackMode() || (host.ReadBoolean("UiHiddenPageDownload") && !host.HiddenForceShow()) ? 1 : 2;
        else state = 3;
        if (state == launchButtonState && (host.Selected() is null ? "" : host.InstancePath(host.Selected()!) ?? "") == (launchButtonInstance is null ? "" : host.InstancePath(launchButtonInstance) ?? "")) goto ExitRefresh;
        launchButtonInstance = host.Selected(); launchButtonState = state;
        switch (state) {
            case 0: Logger.Info("启动按钮：正在加载 Minecraft 版本"); host.GlobalButtons.Text("正在加载"); host.GlobalButtons.EnableLaunch(false); host.GlobalButtons.VersionText("正在加载中，请稍候"); host.GlobalButtons.EnableVersion(false); host.GlobalButtons.MoreVisibility(Visibility.Collapsed); break;
            case 1: Logger.Info("启动按钮：无 Minecraft 版本，下载已禁用"); host.GlobalButtons.Text("启动游戏"); host.GlobalButtons.EnableLaunch(false); host.GlobalButtons.VersionText("未找到可用的游戏版本"); host.GlobalButtons.EnableVersion(true); host.GlobalButtons.MoreVisibility(Visibility.Collapsed); break;
            case 2: Logger.Info("启动按钮：无 Minecraft 版本，要求下载"); host.GlobalButtons.Text("下载游戏"); host.GlobalButtons.EnableLaunch(true); host.GlobalButtons.VersionText("未找到可用的游戏版本"); host.GlobalButtons.EnableVersion(true); host.GlobalButtons.MoreVisibility(Visibility.Collapsed); break;
            case 3: Logger.Info($"启动按钮：Minecraft 版本：{host.InstancePath(host.Selected()!)}"); host.GlobalButtons.Text("启动游戏"); host.GlobalButtons.EnableVersion(true); host.GlobalButtons.EnableLaunch(true); host.GlobalButtons.VersionText(host.InstanceName(host.Selected()!)); break;
        }
    ExitRefresh:
        host.ApplyModpackLayout(); host.GlobalButtons.VersionVisibility(host.ModpackMode() || host.HiddenForceShow() || !host.ReadBoolean("UiHiddenFunctionSelect") ? Visibility.Visible : Visibility.Collapsed);
        if (state == 3) host.GlobalButtons.MoreVisibility(host.ModpackMode() ? Visibility.Visible : host.GlobalButtons.ReadVersionVisibility());
    }
    public void CancelClick() {
        if (host.CancelLaunch is not null) { host.CancelLaunch(); return; }
        PCLCS.Applications.LaunchCancellationActions.Cancel(new() {
            HasOperation = host.HasRealLaunch, CancelOperation = () => host.RealLaunch().Cancel(),
            LogCancellation = () => host.LaunchLog("已取消启动"), HasWatcher = host.HasWatcher,
            KillWatcher = host.KillWatcher, ReadProcess = host.LaunchProcess
        });
    }
    public void MoreClick() {
        if (host.LaunchState() == LegacyLaunchPanelState.Loading) return;
        if (host.SelectedInstanceLocked()) { host.HintError("该版本正在被修改，暂时无法进入版本设置！"); return; }
        host.LoadInstance(host.Selected()!); host.SetSetupInstance(host.Selected()); host.NavigateInstanceSetup();
    }
}
