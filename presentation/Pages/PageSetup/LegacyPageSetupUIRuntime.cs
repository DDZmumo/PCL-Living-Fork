using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Controls;

namespace PCLCS;

public sealed class LegacyPageSetupUIRuntime {
    public readonly LegacySetupUiHost Host;
    private LegacySetupPageHost Common => Host.Common;
    public bool HiddenForceShow { get => Host.ReadHiddenForceShow(); set { Host.WriteHiddenForceShow(value); HiddenRefresh(); } }
    public LegacyPageSetupUIRuntime(LegacySetupUiHost host) => Host = host;
    private bool Read(string key) => Conversions.ToBoolean(Common.Read(key));
    private static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public void BackgroundRefresh(bool isHint, bool refresh) {
        try {
            if (Conversions.ToInteger(Common.Read("UiBackgroundMode")) == 1) {
                if (Host.HasMain()) Host.ApplyBackground(Host.MainPanel(), Host.MainBackground());
                if (Host.CurrentUI() is not null) Host.CurrentUI()!.BackgroundRefreshUI(false, 0);
                return;
            }
            DirectoryUtils.Create(Common.BaseFolder() + "PCL\\Pictures\\");
            var pictures = new List<string>();
            foreach (string file in DirectoryUtils.EnumerateFiles(Common.BaseFolder() + "PCL\\Pictures\\", true)) {
                string extension = PathUtils.GetExtension(file); if (extension != "ini" && extension != "db") pictures.Add(file);
            }
            if (!pictures.Any()) {
                if (refresh) {
                    Host.UseDefaultBackground(Host.MainBackground());
                    Host.ApplyBackground(Host.MainPanel(), Host.MainBackground()); Host.UpdateBackgroundAndTitle();
                    if (isHint) Common.Hint("已恢复默认角色背景！", LegacySetupHint.Green, true);
                }
                if (Host.CurrentUI() is not null) Host.CurrentUI()!.BackgroundRefreshUI(true, 0);
            } else {
                if (refresh) {
                    string address = Host.RandomPicture(pictures);
                    try {
                        Logger.Info("加载背景图片：" + address);
                        Host.MarkCustomBackground(Host.MainBackground());
                        Host.MainBackground().Background = Common.LoadBitmap(address).ToImageBrush();
                        Host.ApplyBackground(Host.MainPanel(), Host.MainBackground()); Host.UpdateBackgroundAndTitle();
                        if (isHint) Common.Hint("背景图片已刷新：" + PathUtils.GetLastPart(address), LegacySetupHint.Green, false);
                    } catch (Exception error) {
                        if (error.Message.Contains("参数无效")) Logger.Error($"刷新背景图片失败，该图片文件可能并非标准格式。\r\n你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。\r\n文件：{address}", LogBehavior.Alert);
                        else Logger.Error(error, $"刷新背景图片失败（{address}）", LogBehavior.Alert);
                    }
                }
                if (Host.CurrentUI() is not null) Host.CurrentUI()!.BackgroundRefreshUI(true, pictures.Count);
            }
        } catch (Exception error) { Logger.Error(error, "刷新背景图片时出现未知错误"); }
    }
    private LegacyPageSetupUIView CurrentView => Host.CurrentUI()!.View;
    public void OnMainPageTypeChanged() {
        if (Host.CurrentUI() is null) return;
        switch (Conversions.ToInteger(Common.Read("UiCustomType"))) {
            case 0:
                CurrentView.PanCustomPreset().Visibility = Visibility.Collapsed; CurrentView.PanCustomLocal().Visibility = Visibility.Collapsed; CurrentView.PanCustomNet().Visibility = Visibility.Collapsed; CurrentView.HintCustom().Visibility = Visibility.Collapsed; CurrentView.HintCustomWarn().Visibility = Visibility.Collapsed;
                break;
            case 1:
                CurrentView.PanCustomPreset().Visibility = Visibility.Collapsed; CurrentView.PanCustomLocal().Visibility = Visibility.Visible; CurrentView.PanCustomNet().Visibility = Visibility.Collapsed; CurrentView.HintCustom().Visibility = Visibility.Visible; CurrentView.HintCustomWarn().Visibility = Read("HintCustomWarn") ? Visibility.Collapsed : Visibility.Visible;
                CurrentView.HintCustom().Text = "从 PCL 文件夹下的 Custom.xaml 读取主页内容。\r\n你可以手动编辑该文件，向主页添加文本、图片、常用网站、快捷启动等功能。";
                Host.SetHomeHintEvent(CurrentView.HintCustom(), false);
                break;
            case 2:
                CurrentView.PanCustomPreset().Visibility = Visibility.Collapsed; CurrentView.PanCustomLocal().Visibility = Visibility.Collapsed; CurrentView.PanCustomNet().Visibility = Visibility.Visible; CurrentView.HintCustom().Visibility = Visibility.Visible; CurrentView.HintCustomWarn().Visibility = Read("HintCustomWarn") ? Visibility.Collapsed : Visibility.Visible;
                CurrentView.HintCustom().Text = "从指定网址联网获取主页内容。服主也可以用于动态更新服务器公告。\r\n如果你制作了稳定运行的联网主页，可以点击这条提示投稿，若合格即可加入预设！";
                Host.SetHomeHintEvent(CurrentView.HintCustom(), true); Host.SetHomeHintEventData(CurrentView.HintCustom(), "https://github.com/Meloong-Git/PCL/discussions/2528");
                break;
            case 3:
                CurrentView.PanCustomPreset().Visibility = Visibility.Visible; CurrentView.PanCustomLocal().Visibility = Visibility.Collapsed; CurrentView.PanCustomNet().Visibility = Visibility.Collapsed; CurrentView.HintCustom().Visibility = Visibility.Collapsed; CurrentView.HintCustomWarn().Visibility = Visibility.Collapsed;
                break;
        }
        CurrentView.CardCustom().TriggerForceResize();
    }
    public void HiddenRefresh() {
        if (Host.Shell(LegacySetupShellElement.TitlePanel) is null || !Host.Shell(LegacySetupShellElement.TitlePanel).IsLoaded) return;
        try {
            if (!HiddenForceShow && Read("UiHiddenPageDownload") && Read("UiHiddenPageLink") && Read("UiHiddenPageSetup") && Read("UiHiddenPageOther") && Read("UiHiddenPageFavorites")) Host.Shell(LegacySetupShellElement.TitlePanel).Visibility = Visibility.Collapsed;
            else {
                Host.Shell(LegacySetupShellElement.TitlePanel).Visibility = Visibility.Visible;
                Host.Shell(LegacySetupShellElement.TitleDownload).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenPageDownload"));
                Host.Shell(LegacySetupShellElement.TitleLink).Visibility = Visibility.Collapsed;
                Host.Shell(LegacySetupShellElement.TitleSetup).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenPageSetup"));
                Host.Shell(LegacySetupShellElement.TitleOther).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenPageOther"));
                Host.Shell(LegacySetupShellElement.TitleFavorites).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenPageFavorites"));
            }
            Host.RefreshLaunchButtons();
            if (Host.CurrentUI() is not null) Host.CurrentUI()!.View.CardSwitch().Visibility = Hidden(!HiddenForceShow && Read("UiHiddenFunctionHidden"));
            if (Host.HasShell(LegacySetupShellPart.InstanceLeft)) Host.Shell(LegacySetupShellElement.ModifyInstance).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenFunctionInstanceModify"));
            if (Host.HasShell(LegacySetupShellPart.SetupLeft)) {
                Host.Shell(LegacySetupShellElement.SetupLaunch).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenSetupLaunch"));
                Host.Shell(LegacySetupShellElement.SetupUI).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenSetupUi"));
                Host.Shell(LegacySetupShellElement.SetupLink).Visibility = Visibility.Collapsed;
                Host.Shell(LegacySetupShellElement.SetupSystem).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenSetupSystem"));
                int available = 0;
                if (!Read("UiHiddenSetupLaunch")) available = checked(available + 1);
                if (!Read("UiHiddenSetupUi")) available = checked(available + 1);
                if (!Read("UiHiddenSetupSystem")) available = checked(available + 1);
                Host.Shell(LegacySetupShellElement.SetupNavigation).Visibility = Hidden(available < 2 && !HiddenForceShow);
            }
            int otherAvailable = 0;
            if (!Read("UiHiddenOtherHelp")) otherAvailable = checked(otherAvailable + 1);
            if (!Read("UiHiddenOtherAbout")) otherAvailable = checked(otherAvailable + 1);
            if (!Read("UiHiddenOtherTest")) otherAvailable = checked(otherAvailable + 1);
            if (Host.HasShell(LegacySetupShellPart.OtherLeft)) {
                Host.Shell(LegacySetupShellElement.OtherHelp).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenOtherHelp"));
                Host.Shell(LegacySetupShellElement.OtherAbout).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenOtherAbout"));
                Host.Shell(LegacySetupShellElement.OtherTest).Visibility = Hidden(!HiddenForceShow && Read("UiHiddenOtherTest"));
                Host.Shell(LegacySetupShellElement.OtherNavigation).Visibility = Hidden(otherAvailable < 2 && !HiddenForceShow);
            }
            ((MyRadioButtonView)Host.Shell(LegacySetupShellElement.TitleOther)).Text = otherAvailable == 1 && !HiddenForceShow ? !Read("UiHiddenOtherHelp") ? "帮助" : !Read("UiHiddenOtherAbout") ? "关于" : "工具百宝箱" : "更多";
            if (Host.IsPage(LegacySetupShellPage.InstanceSelect)) Host.RefreshEmptyDownload();
            if (Host.IsPage(LegacySetupShellPage.Launch)) Host.RefreshLaunchButtons();
            if (Host.IsPage(LegacySetupShellPage.InstanceSetup) && Host.HasDisabledModsPage()) Host.RefreshDisabledModsDownload();
            if (Host.CurrentUI() is not null) Host.CurrentUI()!.View.CardSwitch().Title = HiddenForceShow ? "功能隐藏（已暂时关闭，按 F12 以重新启用）" : "功能隐藏";
            Host.ApplyModpackVisibility();
        } catch (Exception error) { Logger.Error(error, "刷新功能隐藏项目失败"); }
    }
}
