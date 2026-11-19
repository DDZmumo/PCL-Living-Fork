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

public sealed class LegacyPageSetupUIController {
    private readonly LegacyPageSetupUIRuntime runtime;
    public readonly LegacyPageSetupUIView View;
    private LegacySetupUiHost Host => runtime.Host;
    private LegacySetupPageHost Common => Host.Common;
    private bool loaded;
    public LegacyPageSetupUIController(LegacyPageSetupUIRuntime runtime, LegacyPageSetupUIView view) { this.runtime = runtime; View = view; }
    private ScrollViewer PanBack => View.PanBack();
    private StackPanel PanMain => View.PanMain();
    private MyCardView CardLauncher => View.CardLauncher();
    private MySliderView SliderLauncherTransparent => View.SliderLauncherTransparent();
    private Border PanOfficialTheme => View.PanOfficialTheme();
    private MyButtonView BtnOfficialTheme => View.BtnOfficialTheme();
    private MyCardView CardBackground => View.CardBackground();
    private MyComboBoxView ComboBackgroundMode => View.ComboBackgroundMode();
    private Grid PanBackgroundSuit => View.PanBackgroundSuit();
    private Grid PanBackgroundOpacity => View.PanBackgroundOpacity();
    private MySliderView SliderBackgroundOpacity => View.SliderBackgroundOpacity();
    private Grid PanBackgroundBlur => View.PanBackgroundBlur();
    private MySliderView SliderBackgroundBlur => View.SliderBackgroundBlur();
    private MyCheckBoxView CheckBackgroundColorful => View.CheckBackgroundColorful();
    private MyButtonView BtnBackgroundOpen => View.BtnBackgroundOpen();
    private MyButtonView BtnBackgroundRefresh => View.BtnBackgroundRefresh();
    private MyButtonView BtnBackgroundClear => View.BtnBackgroundClear();
    private MyCardView CardMusic => View.CardMusic();
    private Grid PanMusicVolume => View.PanMusicVolume();
    private MySliderView SliderMusicVolume => View.SliderMusicVolume();
    private StackPanel PanMusicDetail => View.PanMusicDetail();
    private MyCheckBoxView CheckMusicRandom => View.CheckMusicRandom();
    private MyCheckBoxView CheckMusicAuto => View.CheckMusicAuto();
    private MyCheckBoxView CheckMusicStart => View.CheckMusicStart();
    private MyCheckBoxView CheckMusicStop => View.CheckMusicStop();
    private MyButtonView BtnMusicOpen => View.BtnMusicOpen();
    private MyButtonView BtnMusicRefresh => View.BtnMusicRefresh();
    private MyButtonView BtnMusicClear => View.BtnMusicClear();
    private MyCardView CardLogo => View.CardLogo();
    private MyRadioBoxView RadioLogoType0 => View.RadioLogoType0();
    private MyRadioBoxView RadioLogoType1 => View.RadioLogoType1();
    private MyRadioBoxView RadioLogoType2 => View.RadioLogoType2();
    private MyRadioBoxView RadioLogoType3 => View.RadioLogoType3();
    private MyCheckBoxView CheckLogoLeft => View.CheckLogoLeft();
    private Grid PanLogoText => View.PanLogoText();
    private MyTextBoxView TextLogoText => View.TextLogoText();
    private Grid PanLogoChange => View.PanLogoChange();
    private MyButtonView BtnLogoChange => View.BtnLogoChange();
    private MyButtonView BtnLogoDelete => View.BtnLogoDelete();
    private MyCardView CardCustom => View.CardCustom();
    private MyRadioBoxView RadioCustomType0 => View.RadioCustomType0();
    private MyRadioBoxView RadioCustomType3 => View.RadioCustomType3();
    private MyRadioBoxView RadioCustomType1 => View.RadioCustomType1();
    private MyRadioBoxView RadioCustomType2 => View.RadioCustomType2();
    private MyHintView HintCustom => View.HintCustom();
    private MyHintView HintCustomWarn => View.HintCustomWarn();
    private WrapPanel PanCustomLocal => View.PanCustomLocal();
    private MyButtonView BtnCustomRefresh => View.BtnCustomRefresh();
    private MyButtonView BtnCustomFile => View.BtnCustomFile();
    private MyButtonView BtnCustomTutorial => View.BtnCustomTutorial();
    private MyButtonView BtnCustomFolder => View.BtnCustomFolder();
    private Grid PanCustomNet => View.PanCustomNet();
    private MyTextBoxView TextCustomNet => View.TextCustomNet();
    private Grid PanCustomPreset => View.PanCustomPreset();
    private MyCheckBoxView CheckRecentWorlds => View.CheckRecentWorlds();
    private MyCardView CardSwitch => View.CardSwitch();
    private MyCheckBoxView CheckHiddenPageDownload => View.CheckHiddenPageDownload();
    private MyCheckBoxView CheckHiddenPageLink => View.CheckHiddenPageLink();
    private MyCheckBoxView CheckHiddenPageSetup => View.CheckHiddenPageSetup();
    private MyCheckBoxView CheckHiddenPageOther => View.CheckHiddenPageOther();
    private MyCheckBoxView CheckHiddenSetupLaunch => View.CheckHiddenSetupLaunch();
    private MyCheckBoxView CheckHiddenSetupLink => View.CheckHiddenSetupLink();
    private MyCheckBoxView CheckHiddenSetupUI => View.CheckHiddenSetupUI();
    private MyCheckBoxView CheckHiddenSetupSystem => View.CheckHiddenSetupSystem();
    private MyCheckBoxView CheckHiddenOtherHelp => View.CheckHiddenOtherHelp();
    private MyCheckBoxView CheckHiddenOtherAbout => View.CheckHiddenOtherAbout();
    private MyCheckBoxView CheckHiddenOtherTest => View.CheckHiddenOtherTest();
    private MyCheckBoxView CheckLauncherEmail => View.CheckLauncherEmail();
    private MyCheckBoxView CheckHiddenFunctionSelect => View.CheckHiddenFunctionSelect();
    private MyCheckBoxView CheckHiddenFunctionModUpdate => View.CheckHiddenFunctionModUpdate();
    private MyCheckBoxView CheckHiddenFunctionHidden => View.CheckHiddenFunctionHidden();
    private MyCheckBoxView CheckHiddenFunctionModify => View.CheckHiddenFunctionModify();
    public void Loaded() {
        PanBack.ScrollToHome(); Common.AddAnimationSuppression(1); Refresh(); Common.AddAnimationSuppression(-1);
        if (loaded) return; loaded = true; SliderLoad();
    }
    public void Refresh() {
        try {
            Common.RefreshSettings(View.Page()); runtime.BackgroundRefresh(false, false);
            CheckLogoLeft.Visibility = RadioLogoType0.Checked ? Visibility.Visible : Visibility.Collapsed;
            PanLogoText.Visibility = RadioLogoType2.Checked ? Visibility.Visible : Visibility.Collapsed;
            PanLogoChange.Visibility = RadioLogoType3.Checked ? Visibility.Visible : Visibility.Collapsed;
            MusicRefreshUI(); runtime.OnMainPageTypeChanged();
        } catch (NullReferenceException error) { Logger.Error(error, "个性化设置项存在异常，已被自动重置", LogBehavior.Alert); Reset(); }
        catch (Exception error) { Logger.Error(error, "重载个性化设置时出错"); }
    }
    public void Reset() {
        try { Common.ResetSettings(View.Page()); Common.Reset("UiLauncherTheme"); Logger.Info("已初始化个性化设置！"); Common.Hint("已初始化个性化设置", LegacySetupHint.Green, false); }
        catch (Exception error) { Logger.Error(error, "初始化个性化设置失败", LogBehavior.Alert); }
        Refresh();
    }
    public void OpenBackground() => Common.OpenExplorer(Common.BaseFolder() + "PCL\\Pictures\\");
    public void RefreshBackground() => runtime.BackgroundRefresh(true, true);
    public void BackgroundRefreshUI(bool show, int count) {
        if (PanBackgroundOpacity is null) return;
        CheckBackgroundColorful.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetBackgroundEditorsVisible(show);
        if (show) {
            BtnBackgroundClear.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CardBackground.Title = count > 0 ? "背景图片（" + count + " 张）" : "背景图片（默认角色背景）";
        } else {
            BtnBackgroundClear.Visibility = Visibility.Collapsed; CardBackground.Title = "背景图片（原生白底）";
        }
        CardBackground.TriggerForceResize();
    }

    public void RefreshLauncherWallpaperUI(bool hasWallpaper) {
        if (PanBackgroundOpacity is null) return;
        SetBackgroundEditorsVisible(hasWallpaper);
        CardBackground.Title = "背景图片";
        CardBackground.TriggerForceResize();
    }

    private void SetBackgroundEditorsVisible(bool show) {
        Visibility visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PanBackgroundOpacity.Visibility = visibility;
        PanBackgroundBlur.Visibility = visibility;
        PanBackgroundSuit.Visibility = visibility;
    }
    public void ClearBackground() {
        if (Common.Message("即将删除背景图片文件夹中的所有文件。\r\n此操作不可撤销，是否确定？", "警告", button2: "取消", warning: true) == 1) {
            Common.DeleteDirectory(Common.BaseFolder() + "PCL\\Pictures"); runtime.BackgroundRefresh(false, true);
            Common.Hint(Conversions.ToInteger(Common.Read("UiBackgroundMode")) == 1 ? "自定义背景已清空！" : "自定义背景已清空，已恢复默认角色背景！", LegacySetupHint.Green, true);
        }
    }
    public void ChangeLogo() {
        string? file = Common.SelectImage("选择图片", new[] { "png", "jpg", "jpeg", "gif", "webp" }, "常用图片文件").FirstOrDefault();
        if (string.IsNullOrEmpty(file)) return;
        string target = Common.BaseFolder() + "PCL\\Logo.png";
        try { FileUtils.Copy(file!, target); Host.TitleLogo().Source = null; Host.TitleLogo().Source = target; }
        catch (Exception error) {
            if (error.Message.Contains("参数无效")) Logger.Error($"改变标题栏图片失败，该图片文件可能并非标准格式。\r\n你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。\r\n文件：{target}", LogBehavior.Alert);
            else Logger.Error(error, "设置标题栏图片失败", LogBehavior.Alert);
            Host.TitleLogo().Source = null;
        }
    }
    public void PreviewCustomLogo(bool raiseByMouse, ref bool handled) {
        if (!(Common.AnimationSuppression() == 0 && raiseByMouse)) return;
        while (true) {
            string target = Common.BaseFolder() + "PCL\\Logo.png";
            if (FileUtils.Exists(target)) {
                try { Host.TitleLogo().Source = null; Host.TitleLogo().Source = target; }
                catch (Exception error) {
                    if (error.Message.Contains("参数无效")) Logger.Error($"改变标题栏图片失败，该图片文件可能并非标准格式。\r\n你可以尝试使用画图打开该文件并重新保存，这会让图片变为标准格式。\r\n文件：{target}", LogBehavior.Alert);
                    else Logger.Error(error, "调整标题栏图片失败", LogBehavior.Alert);
                    Host.TitleLogo().Source = null; handled = true;
                    try { FileUtils.Delete(target); } catch (Exception cleanup) { Logger.Error(cleanup, "清理错误的标题栏图片失败", LogBehavior.Alert); }
                }
                return;
            }
            string? file = Common.SelectImage("选择图片", new[] { "png", "jpeg", "jpg", "gif", "webp" }, "常用图片文件").FirstOrDefault();
            if (string.IsNullOrEmpty(file)) { Host.TitleLogo().Source = null; handled = true; }
            else {
                try { FileUtils.Copy(file!, target); continue; }
                catch (Exception error) { Logger.Error(error, "复制标题栏图片失败", LogBehavior.Alert); }
            }
            return;
        }
    }
    public void DeleteLogo() {
        try { FileUtils.Delete(Common.BaseFolder() + "PCL\\Logo.png"); RadioLogoType1.SetChecked(true, true); Common.Hint("标题栏图片已清空！", LegacySetupHint.Green, true); }
        catch (Exception error) { Logger.Error(error, "清空标题栏图片失败", LogBehavior.Alert); }
    }
    public void OpenMusic() => Common.OpenExplorer(Common.BaseFolder() + "PCL\\Musics\\");
    public void RefreshMusic() => Host.RefreshMusic(true);
    public void MusicRefreshUI() {
        if (PanBackgroundOpacity is null) return;
        if (Host.MusicAll().Any()) {
            PanMusicVolume.Visibility = Visibility.Visible; PanMusicDetail.Visibility = Visibility.Visible; BtnMusicClear.Visibility = Visibility.Visible;
            CardMusic.Title = "背景音乐（" + DirectoryUtils.EnumerateFiles(Common.BaseFolder() + "PCL\\Musics\\", true).Count(file => !new[] { "ini", "jpg", "txt", "cfg", "lrc", "db", "png" }.Contains(PathUtils.GetExtension(file))) + " 首）";
        } else { PanMusicVolume.Visibility = Visibility.Collapsed; PanMusicDetail.Visibility = Visibility.Collapsed; BtnMusicClear.Visibility = Visibility.Collapsed; CardMusic.Title = "背景音乐"; }
        CardMusic.TriggerForceResize();
    }
    public void ClearMusic() {
        if (Common.Message("即将删除背景音乐文件夹中的所有文件。\r\n此操作不可撤销，是否确定？", "警告", button2: "取消", warning: true) == 1) Common.InThread(() => {
            Common.Hint("正在删除背景音乐……", LegacySetupHint.Blue, true); Host.ClearMusicPlayer(); Host.AssignWaitingMusic(new List<string>()); Host.AssignAllMusic(new List<string>()); Common.Sleep(200);
            try { Common.DeleteDirectory(Common.BaseFolder() + "PCL\\Musics"); Common.Hint("背景音乐已删除！", LegacySetupHint.Green, true); }
            catch (Exception error) { Logger.Error(error, "删除背景音乐失败", LogBehavior.Alert); }
            try { DirectoryUtils.Create(Common.BaseFolder() + "PCL\\Musics\\"); Common.InUi(() => Host.RefreshMusic(false)); }
            catch (Exception error) { Logger.Error(error, "重建背景音乐文件夹失败", LogBehavior.Alert); }
        });
    }
    public void MusicStartChanged() { if (Common.AnimationSuppression() != 0) return; if (CheckMusicStart.Checked) CheckMusicStop.Checked = false; }
    public void MusicStopChanged() { if (Common.AnimationSuppression() != 0) return; if (CheckMusicStop.Checked) CheckMusicStart.Checked = false; }
    public void CreateCustomFile() {
        try {
            if (FileUtils.Exists(Common.BaseFolder() + "PCL\\Custom.xaml") && Common.Message("当前已存在布局文件，继续生成教学文件将会覆盖现有布局文件！", "覆盖确认", "继续", "取消", warning: true) == 2) return;
            Common.ExtractResource(Common.BaseFolder() + "PCL\\Custom.xaml", "Custom"); Common.Hint("教学文件已生成！", LegacySetupHint.Green, true); Common.OpenExplorer(Common.BaseFolder() + "PCL\\Custom.xaml");
        } catch (Exception error) { Logger.Error(error, "生成教学文件失败"); }
    }
    public void RefreshCustom() { Host.RefreshHome(); Common.Hint("已刷新主页！", LegacySetupHint.Green, true); }
    public void CustomTutorial() => Common.Message("1. 点击 生成教学文件 按钮，这会在 PCL 文件夹下生成 Custom.xaml 布局文件。\r\n2. 使用记事本等工具打开这个文件并进行修改，修改完记得保存。\r\n3. 点击 刷新主页 按钮，查看主页现在长啥样了。\r\n\r\n你可以在生成教学文件后直接刷新主页，对照着进行修改，更有助于理解。\r\n直接将主页文件拖进 PCL 窗口也可以快捷加载。", "主页自定义教程");
    public void OpenCustomFolder() { string path = Common.BaseFolder() + "PCL\\Custom.xaml"; Common.OpenExplorer(FileUtils.Exists(path) ? path : Common.BaseFolder() + "PCL\\"); }
    public void HiddenSetupMain() {
        if (CheckHiddenPageSetup.Checked) { CheckHiddenSetupLaunch.Checked = true; CheckHiddenSetupSystem.Checked = true; CheckHiddenSetupLink.Checked = true; CheckHiddenSetupUI.Checked = true; }
        else if (Conversions.ToBoolean(Common.Read("UiHiddenSetupLaunch")) && Conversions.ToBoolean(Common.Read("UiHiddenSetupUi")) && Conversions.ToBoolean(Common.Read("UiHiddenSetupSystem")) && Conversions.ToBoolean(Common.Read("UiHiddenSetupLink"))) {
            CheckHiddenSetupLaunch.Checked = false; CheckHiddenSetupSystem.Checked = false; CheckHiddenSetupLink.Checked = false; CheckHiddenSetupUI.Checked = false;
        }
    }
    public void HiddenSetupSub() => CheckHiddenPageSetup.Checked = Conversions.ToBoolean(Common.Read("UiHiddenSetupLaunch")) && Conversions.ToBoolean(Common.Read("UiHiddenSetupUi")) && Conversions.ToBoolean(Common.Read("UiHiddenSetupSystem")) && Conversions.ToBoolean(Common.Read("UiHiddenSetupLink"));
    public void HiddenOtherMain() {
        if (CheckHiddenPageOther.Checked) { CheckHiddenOtherAbout.Checked = true; CheckHiddenOtherHelp.Checked = true; CheckHiddenOtherTest.Checked = true; }
        else if (Conversions.ToBoolean(Common.Read("UiHiddenOtherHelp")) && Conversions.ToBoolean(Common.Read("UiHiddenOtherAbout")) && Conversions.ToBoolean(Common.Read("UiHiddenOtherTest"))) { CheckHiddenOtherAbout.Checked = false; CheckHiddenOtherHelp.Checked = false; CheckHiddenOtherTest.Checked = false; }
    }
    public void HiddenOtherSub(object sender, bool user) => CheckHiddenPageOther.Checked = Conversions.ToBoolean(Common.Read("UiHiddenOtherHelp")) && Conversions.ToBoolean(Common.Read("UiHiddenOtherAbout")) && Conversions.ToBoolean(Common.Read("UiHiddenOtherTest"));
    public void HiddenHint(object sender, bool user) {
        if (Common.AnimationSuppression() == 0 && Conversions.ToBoolean(NewLateBinding.LateGet(sender, null, "Checked", Array.Empty<object>(), null, null, null))) Common.Hint("按 F12 即可暂时关闭功能隐藏设置。千万别忘了，要不然设置就改不回来了……", LegacySetupHint.Blue, true);
    }
    public void SliderLoad() {
        View.SliderPanelOpacity().GetHintText = new Func<object, object>(value => LegacySetupText.Number(Math.Round(Conversions.ToDouble(Operators.MultiplyObject(value, 0.1)))) + "%");
        SliderMusicVolume.GetHintText = new Func<object, object>(value => LegacySetupText.Number(Math.Ceiling(Conversions.ToDouble(Operators.MultiplyObject(value, 0.1)))) + "%");
        SliderLauncherTransparent.GetHintText = new Func<object, object>(value => LegacySetupText.Number(Math.Round(Conversions.ToDouble(Operators.AddObject(40, Operators.MultiplyObject(value, 0.1))))) + "%");
        SliderBackgroundOpacity.GetHintText = new Func<object, object>(value => LegacySetupText.Number(Math.Round(Conversions.ToDouble(Operators.MultiplyObject(value, 0.1)))) + "%");
        SliderBackgroundBlur.GetHintText = new Func<object, object>(value => LegacySetupText.String(value) + " 像素");
    }
}
