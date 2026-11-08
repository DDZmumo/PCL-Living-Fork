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

public sealed class LegacyPageSetupLaunchController<TInstance, TSkin> where TInstance : class where TSkin : struct {
    private readonly LegacyPageSetupLaunchRuntime<TInstance, TSkin> runtime;
    public readonly LegacyPageSetupLaunchView View;
    private LegacySetupLaunchHost<TInstance, TSkin> Host => runtime.Host;
    private LegacySetupPageHost Common => Host.Common;
    private bool loaded;
    private int ramTextLeft = 2, ramTextRight = 1;
    public LegacyPageSetupLaunchController(LegacyPageSetupLaunchRuntime<TInstance, TSkin> runtime, LegacyPageSetupLaunchView view) { this.runtime = runtime; View = view; }
    private ScrollViewer PanBack => View.PanBack();
    private StackPanel PanMain => View.PanMain();
    private MyCheckBoxView CheckPersonalOptionsSync => View.CheckPersonalOptionsSync();
    private MyButtonView BtnImportPersonalOptions => View.BtnImportPersonalOptions();
    private MyCardView CardArgument => View.CardArgument();
    private TextBlock LabArgumentIndie => View.LabArgumentIndie();
    private MyComboBoxView ComboArgumentIndieV2 => View.ComboArgumentIndieV2();
    private MyComboBoxView ComboArgumentVisibie => View.ComboArgumentVisibie();
    private Grid PanArgumentWindow => View.PanArgumentWindow();
    private MyComboBoxView ComboArgumentWindowType => View.ComboArgumentWindowType();
    private MyTextBoxView TextArgumentWindowWidth => View.TextArgumentWindowWidth();
    private TextBlock LabArgumentWindowMiddle => View.LabArgumentWindowMiddle();
    private MyTextBoxView TextArgumentWindowHeight => View.TextArgumentWindowHeight();
    private MyRadioBoxView RadioRamType0 => View.RadioRamType0();
    private MyRadioBoxView RadioRamType1 => View.RadioRamType1();
    private MySliderView SliderRamCustom => View.SliderRamCustom();
    private MyCheckBoxView CheckArgumentRam => View.CheckArgumentRam();
    private Grid PanRamDisplay => View.PanRamDisplay();
    private ColumnDefinition ColumnRamUsed => View.ColumnRamUsed();
    private ColumnDefinition ColumnRamGame => View.ColumnRamGame();
    private ColumnDefinition ColumnRamEmpty => View.ColumnRamEmpty();
    private Rectangle RectRamUsed => View.RectRamUsed();
    private Rectangle RectRamGame => View.RectRamGame();
    private Rectangle RectRamEmpty => View.RectRamEmpty();
    private TextBlock LabRamUsedTitle => View.LabRamUsedTitle();
    private TextBlock LabRamGameTitle => View.LabRamGameTitle();
    private TextBlock LabRamUsed => View.LabRamUsed();
    private TextBlock LabRamTotal => View.LabRamTotal();
    private TextBlock LabRamGame => View.LabRamGame();
    private MyCardView CardSkin => View.CardSkin();
    private MyRadioBoxView RadioSkinType0 => View.RadioSkinType0();
    private MyRadioBoxView RadioSkinType1 => View.RadioSkinType1();
    private MyRadioBoxView RadioSkinType2 => View.RadioSkinType2();
    private MyRadioBoxView RadioSkinType3 => View.RadioSkinType3();
    private MyRadioBoxView RadioSkinType4 => View.RadioSkinType4();
    private Grid PanSkinID => View.PanSkinID();
    private MyButtonView BtnSkinSave => View.BtnSkinSave();
    private MyButtonView BtnSkinCache => View.BtnSkinCache();
    private Grid PanSkinChange => View.PanSkinChange();
    private MyButtonView BtnSkinChange => View.BtnSkinChange();
    private MyButtonView BtnSkinDelete => View.BtnSkinDelete();
    private MyIconButtonView BtnAdvanceJvmReset => View.BtnAdvanceJvmReset();
    private MyTextBoxView TextAdvanceJvm => View.TextAdvanceJvm();
    private MyTextBoxView TextAdvanceGame => View.TextAdvanceGame();
    private MyTextBoxView TextAdvanceRun => View.TextAdvanceRun();
    private MyCheckBoxView CheckAdvanceRunWait => View.CheckAdvanceRunWait();
    private Grid PanAdvanceJava => View.PanAdvanceJava();
    private MyComboBoxView ComboAdvanceJava => View.ComboAdvanceJava();
    private TextBlock LabAdvanceJava => View.LabAdvanceJava();
    private MyIconButtonView BtnAdvanceJavaSearch => View.BtnAdvanceJavaSearch();
    private FrameworkElement BtnSwitch => View.BtnSwitch();
    public void Loaded() {
        Host.ApplyInstanceVisibility(); PanBack.ScrollToHome(); RefreshRam(false);
        BtnSwitch.Visibility = Host.SelectedInstance() is null ? Visibility.Collapsed : Visibility.Visible;
        Common.AddAnimationSuppression(1); Refresh(); Common.AddAnimationSuppression(-1);
        if (loaded) return; loaded = true;
        var timer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
        timer.Tick += (_, _) => RefreshRam(true); timer.Start();
    }
    public void Refresh() {
        try {
            Common.RefreshSettings(View.Page());
            runtime.UpdateSkinType(); runtime.UpdateRamType(); UpdateJavaList();
        } catch (NullReferenceException error) { Logger.Error(error, "启动设置项存在异常，已被自动重置", LogBehavior.Alert); Reset(); }
        catch (Exception error) { Logger.Error(error, "重载启动设置时出错"); }
    }
    public void Reset() {
        try {
            Common.ResetSettings(View.Page()); Common.Write("LaunchArgumentIndieV2", Common.Default("LaunchArgumentIndieV2"));
            Configs.JavaList.Reset(); Configs.JavaRemovedList.Reset(); Host.StartJavaRefresh(); Common.Hint("已初始化启动设置！", LegacySetupHint.Green, true);
        } catch (Exception error) { Logger.Error(error, "初始化启动设置失败", LogBehavior.Alert); }
        Refresh();
    }
    public void PersonalSyncChanged(object sender, bool user) {
        if (!user || Common.AnimationSuppression() != 0) return;
        try {
            if (CheckPersonalOptionsSync.Checked && !Host.PersonalProfileExists()) {
                if (!ImportPersonalOptions()) { CheckPersonalOptionsSync.Checked = false; return; }
            }
            Common.Write("LaunchPersonalOptionsSync", CheckPersonalOptionsSync.Checked);
        } catch (Exception error) { CheckPersonalOptionsSync.Checked = false; Logger.Error(error, "启用个人游戏设置同步失败", LogBehavior.Toast); }
    }
    public bool ImportPersonalOptions() {
        TInstance? instance = Host.SelectedInstance();
        if (instance is null) { Common.Hint("请先选择一个已经运行过的游戏版本。", LegacySetupHint.Red, true); return false; }
        if (Common.Message($"以 {Host.InstanceName(instance)} 的游戏设置作为个人习惯来源？\r\n只导入当前勾选的类别；其他版本将在下次启动时合并这些设置。", "导入个人游戏设置", "导入", "取消") != 1) return false;
        Host.ImportPersonalOptions(instance); Common.Hint("个人游戏设置已导入。", LegacySetupHint.Green, true); return true;
    }
    public void ImportPersonalOptionsClick() { try { ImportPersonalOptions(); } catch (Exception error) { Logger.Error(error, "导入个人游戏设置失败", LogBehavior.Toast); } }
    public void ChangeSkinClick() { TSkin skin = Host.SelectSkin(); if (!Host.SkinValid(skin)) return; ChangeSkin(skin); }
    public void PreviewCustomSkin(bool raiseByMouse, ref bool handled) {
        if (!(Common.AnimationSuppression() == 0 && raiseByMouse)) return;
        if (FileUtils.Exists(Common.UserDataFolder() + "CustomSkin.png")) return;
        TSkin skin = Host.SelectSkin(); if (!Host.SkinValid(skin)) { handled = true; return; }
        if (!ChangeSkin(skin)) handled = true;
    }
    public bool ChangeSkin(TSkin skin) {
        try {
            string path = Common.UserDataFolder() + "CustomSkin.png";
            FileUtils.Copy(Host.SkinPath(skin), path);
            LegacyBitmap bitmap = Common.LoadBitmap(path);
            if (bitmap.Pic!.Width == 64 && bitmap.Pic.Height == 32) {
                System.Drawing.Image image = bitmap.Pic;
                var next = new System.Drawing.Bitmap(64, 64);
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(next)) graphics.DrawImageUnscaled(image, new System.Drawing.Point(0, 0));
                FileUtils.Delete(path); next.Save(path);
            }
            Common.Write("LaunchSkinSlim", Host.SkinSlim(skin)); return true;
        } catch (Exception error) { Logger.Error(error, "改变离线皮肤失败", LogBehavior.Alert); return false; }
        finally { Host.StartLegacySkin(true); }
    }
    public void DeleteSkin() {
        try { FileUtils.Delete(Common.UserDataFolder() + "CustomSkin.png"); RadioSkinType0.SetChecked(true, true); Common.Hint("离线皮肤已清空！", LegacySetupHint.Green, true); }
        catch (Exception error) { Logger.Error(error, "清空离线皮肤失败", LogBehavior.Alert); }
    }
    public void SaveSkin() => Host.SaveSkin();
    public void RefreshSkinCache() => Host.RefreshSkinCache();
    public void RefreshRam(bool showAnim) {
        if (LabRamGame is null || LabRamUsed is null || !Host.IsCurrentRamPage()) return;
        double game = Math.Round(runtime.GetRam(Host.SelectedInstance(), false), 5);
        double total = Math.Round((double)Host.TotalMemory() / 1024 / 1024 / 1024, 1);
        double available = Math.Round((double)Host.AvailableMemory() / 1024 / 1024 / 1024, 1);
        double actual = Math.Round(Math.Min(game, available), 5);
        double used = Math.Round(total - available, 5);
        double empty = Math.Round((total - used - game).Clamp(0d, 1000d), 1);
        if (total <= 1.5) SliderRamCustom.MaxValue = Convert.ToInt32(Math.Max(Math.Floor((total - 0.3) / 0.1), 1));
        else if (total <= 8) SliderRamCustom.MaxValue = Convert.ToInt32(Math.Floor((total - 1.5) / 0.5) + 12);
        else if (total <= 16) SliderRamCustom.MaxValue = Convert.ToInt32(Math.Floor((total - 8) / 1) + 25);
        else SliderRamCustom.MaxValue = Convert.ToInt32(Math.Floor((total - 16) / 2) + 33);
        static string Number(double value) => value == Math.Floor(value) ? LegacySetupText.Number(value) + ".0" : LegacySetupText.Number(value);
        LabRamGame.Text = Number(game) + " GB" + (game != actual ? " (可用 " + Number(actual) + " GB)" : "");
        LabRamUsed.Text = Number(used) + " GB"; LabRamTotal.Text = " / " + Number(total) + " GB";
        if (showAnim) Common.StartAnimations(new[] {
            new LegacySetupAnimation(ColumnRamUsed, LegacySetupAnimationKind.GridWidth, used - ColumnRamUsed.Width.Value, 800, LegacySetupEase.OutFluentStrong),
            new LegacySetupAnimation(ColumnRamGame, LegacySetupAnimationKind.GridWidth, actual - ColumnRamGame.Width.Value, 800, LegacySetupEase.OutFluentStrong),
            new LegacySetupAnimation(ColumnRamEmpty, LegacySetupAnimationKind.GridWidth, empty - ColumnRamEmpty.Width.Value, 800, LegacySetupEase.OutFluentStrong)
        }, "SetupLaunch Ram Grid");
        else { ColumnRamUsed.Width = new GridLength(used, GridUnitType.Star); ColumnRamGame.Width = new GridLength(actual, GridUnitType.Star); ColumnRamEmpty.Width = new GridLength(empty, GridUnitType.Star); }
    }
    public void RefreshRam() => RefreshRam(true);
    public void RefreshRamText() {
        double rectUsed = RectRamUsed.ActualWidth, total = PanRamDisplay.ActualWidth;
        double game = LabRamGame.ActualWidth, used = LabRamUsed.ActualWidth, totalLabel = LabRamTotal.ActualWidth;
        double gameTitle = LabRamGameTitle.ActualWidth, usedTitle = LabRamUsedTitle.ActualWidth;
        int left = rectUsed - 30 < used || rectUsed - 30 < usedTitle ? 0 : rectUsed - 25 < used + totalLabel ? 1 : 2;
        if (ramTextLeft != left) {
            ramTextLeft = left;
            switch (left) {
                case 0: Common.StartAnimations(new[] {
                    new LegacySetupAnimation(LabRamUsed, LegacySetupAnimationKind.Opacity, -LabRamUsed.Opacity, 100),
                    new LegacySetupAnimation(LabRamTotal, LegacySetupAnimationKind.Opacity, -LabRamTotal.Opacity, 100),
                    new LegacySetupAnimation(LabRamUsedTitle, LegacySetupAnimationKind.Opacity, -LabRamUsedTitle.Opacity, 100)
                }, "SetupLaunch Ram TextLeft"); break;
                case 1: Common.StartAnimations(new[] {
                    new LegacySetupAnimation(LabRamUsed, LegacySetupAnimationKind.Opacity, 1 - LabRamUsed.Opacity, 100),
                    new LegacySetupAnimation(LabRamTotal, LegacySetupAnimationKind.Opacity, -LabRamTotal.Opacity, 100),
                    new LegacySetupAnimation(LabRamUsedTitle, LegacySetupAnimationKind.Opacity, 0.7 - LabRamUsedTitle.Opacity, 100)
                }, "SetupLaunch Ram TextLeft"); break;
                case 2: Common.StartAnimations(new[] {
                    new LegacySetupAnimation(LabRamUsed, LegacySetupAnimationKind.Opacity, 1 - LabRamUsed.Opacity, 100),
                    new LegacySetupAnimation(LabRamTotal, LegacySetupAnimationKind.Opacity, 1 - LabRamTotal.Opacity, 100),
                    new LegacySetupAnimation(LabRamUsedTitle, LegacySetupAnimationKind.Opacity, 0.7 - LabRamUsedTitle.Opacity, 100)
                }, "SetupLaunch Ram TextLeft"); break;
            }
        }
        int right = total < game + 2 + rectUsed || total < gameTitle + 2 + rectUsed ? 0 : 1;
        if (right == 0) {
            if (Common.AnimationSuppression() == 0 && (ramTextRight != right || Common.AnimationRunning("SetupLaunch Ram TextRight"))) Common.StartAnimations(new[] {
                new LegacySetupAnimation(LabRamGame, LegacySetupAnimationKind.X, total - game - LabRamGame.Margin.Left, 100, LegacySetupEase.OutFluentWeak),
                new LegacySetupAnimation(LabRamGameTitle, LegacySetupAnimationKind.X, total - gameTitle - LabRamGameTitle.Margin.Left, 100, LegacySetupEase.OutFluentWeak)
            }, "SetupLaunch Ram TextRight");
            else { Common.StopAnimation("SetupLaunch Ram TextRight"); LabRamGame.Margin = new Thickness(total - game, 3, 0, 0); LabRamGameTitle.Margin = new Thickness(total - gameTitle, 0, 0, 5); }
        } else {
            if (Common.AnimationSuppression() == 0 && (ramTextRight != right || Common.AnimationRunning("SetupLaunch Ram TextRight"))) Common.StartAnimations(new[] {
                new LegacySetupAnimation(LabRamGame, LegacySetupAnimationKind.X, 2 + rectUsed - LabRamGame.Margin.Left, 100, LegacySetupEase.OutFluentWeak),
                new LegacySetupAnimation(LabRamGameTitle, LegacySetupAnimationKind.X, 2 + rectUsed - LabRamGameTitle.Margin.Left, 100, LegacySetupEase.OutFluentWeak)
            }, "SetupLaunch Ram TextRight");
            else { Common.StopAnimation("SetupLaunch Ram TextRight"); LabRamGame.Margin = new Thickness(2 + rectUsed, 3, 0, 0); LabRamGameTitle.Margin = new Thickness(2 + rectUsed, 0, 0, 5); }
        }
        ramTextRight = right;
    }
    public void UpdateJavaList() {
        if (ComboAdvanceJava is null || LabAdvanceJava is null || !ComboAdvanceJava.IsLoaded) return;
        ComboAdvanceJava.Items.Clear();
        if (Host.JavaRefreshRunning()) { BtnAdvanceJavaSearch.IsEnabled = false; ComboAdvanceJava.IsEnabled = false; LabAdvanceJava.Text = "搜索中 …"; return; }
        BtnAdvanceJavaSearch.IsEnabled = true; ComboAdvanceJava.IsEnabled = true;
        int count = Configs.JavaList.Get()!.Count; LabAdvanceJava.Text = count > 0 ? $"共有 {count} 个 Java …" : "未找到 Java，点击以导入已有的 Java";
        try {
            ConcurrentList<Java> entries = Configs.JavaList.Get()!;
            for (int i = 0; i < entries.Count; i = checked(i + 1)) {
                Java entry = entries[i]; MyListItemView item = Host.NewJavaItem();
                item.FontSize = 13; item.Height = 24; item.IsScaleAnimationEnabled = false; item.Type = MyListItemView.CheckType.Clickable; item.Tag = entry; item.Title = entry.ToString();
                item.MouseLeftButtonUp += (_, args) => args.Handled = true;
                Host.ConnectItemClick(item, () => Common.Hint("点击选项右侧的箭头可以进行排序，以控制 PCL 优先选择哪个 Java！", LegacySetupHint.Blue, true));
                ComboAdvanceJava.Items.Add(item);
                var buttons = new List<MyIconButtonView>();
                MyIconButtonView up = Host.NewJavaButton(); up.Logo = Host.JavaUpLogo; up.LogoScale = 0.95; up.Height = 24; up.Width = 24; up.IsEnabled = i > 0; up.ToolTip = "提高优先级"; ToolTipService.SetShowOnDisabled(up, true); Host.ConnectIconClick(up, () => MoveJavaInList(entry, -1)); buttons.Add(up);
                MyIconButtonView down = Host.NewJavaButton(); down.Logo = Host.JavaDownLogo; down.LogoScale = 0.95; down.Height = 24; down.Width = 24; down.IsEnabled = i < entries.Count - 1; down.ToolTip = "降低优先级"; ToolTipService.SetShowOnDisabled(down, true); Host.ConnectIconClick(down, () => MoveJavaInList(entry, 1)); buttons.Add(down);
                bool official = PathUtils.IsParentOf(Common.AppData() + ".minecraft\\runtime\\", entry.Folder);
                MyIconButtonView delete = Host.NewJavaButton(); delete.Logo = Host.JavaDeleteLogo; delete.Height = 24; delete.Width = 24; delete.IsEnabled = !official; delete.ToolTip = delete.IsEnabled ? "从列表中移除" : "无法移除官方 Java"; Host.ConnectIconClick(delete, () => Host.RemoveJava(entry)); ToolTipService.SetShowOnDisabled(delete, true); buttons.Add(delete);
                MyIconButtonView open = Host.NewJavaButton(); open.Logo = Host.JavaOpenLogo; open.LogoScale = 1.1; open.Height = 24; open.Width = 24; open.ToolTip = "打开文件夹"; Host.ConnectIconClick(open, () => Common.OpenExplorer(entry.JavaExePath)); buttons.Add(open);
                Host.SetJavaButtons(item, buttons);
            }
            MyListItemView import = Host.NewJavaItem(); import.FontSize = 13; import.Height = 24; import.IsScaleAnimationEnabled = false; import.Title = "导入电脑中已有的 Java…"; import.Type = MyListItemView.CheckType.Clickable;
            import.MouseLeftButtonUp += (_, args) => { args.Handled = true; Host.ImportJava(); }; ComboAdvanceJava.Items.Add(import);
        } catch (Exception error) { Logger.Error(error, "更新 Java 列表下拉框失败"); }
    }
    public void MoveJavaInList(Java entry, int offset) {
        Configs.JavaList.Edit((ref ConcurrentList<Java>? values) => { int index = values!.IndexOf(entry); int next = checked(index + offset); if (index < 0 || next < 0 || next >= values.Count) return; values.RemoveAt(index); values.Insert(next, entry); });
        Host.UpdateJavaLists();
    }
    public void SearchJava() => Host.SearchJava();
    public void WindowTypeUIRefresh() { Visibility visible = ComboArgumentWindowType.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed; TextArgumentWindowHeight.Visibility = visible; LabArgumentWindowMiddle.Visibility = visible; TextArgumentWindowWidth.Visibility = visible; }
    public void VisibilitySelectionChanged(SelectionChangedEventArgs args) {
        if (Common.AnimationSuppression() != 0) return;
        if (ComboArgumentVisibie.SelectedIndex == 0 && Common.Message("若在游戏启动后立即关闭启动器，崩溃检测、更改游戏标题等功能将失效。\r\n如果想保留这些功能，可以选择让启动器在游戏启动后隐藏，游戏退出后自动关闭。", "提醒", "继续", "取消") == 2) { if (args.RemovedItems.Count > 0) ComboArgumentVisibie.SelectedItem = args.RemovedItems[0]; }
    }
    public void MemoryOptimizationChanged() {
        if (Common.AnimationSuppression() != 0 || !CheckArgumentRam.Checked) return;
        if (Common.Message("内存优化会显著延长启动耗时，建议仅在内存不足时开启。\r\n如果你在使用机械硬盘，这还可能导致一小段时间的严重卡顿。" + (Host.HasAdminRole() ? "" : "\r\n\r\n每次启动游戏，PCL 都需要申请管理员权限以进行内存优化。\r\n若想自动授予权限，可以右键 PCL，打开 属性 → 兼容性 → 以管理员身份运行此程序。"), "提醒", "确定", "取消") == 2) CheckArgumentRam.Checked = false;
    }
    public void IsolationSelectionChanged() { if (Common.AnimationSuppression() == 0) Common.Message("本设置仅会对之后新安装的版本生效。\r\n如果要修改已安装的版本的隔离方式，请在它的版本独立设置中调整。"); }
    public void AdvanceRunChanged() => CheckAdvanceRunWait.Visibility = TextAdvanceRun.Text == "" ? Visibility.Collapsed : Visibility.Visible;
    public void AdvanceJvmChanged() => BtnAdvanceJvmReset.Visibility = string.Equals(TextAdvanceJvm.Text ?? "", Common.Default("LaunchAdvanceJvm") ?? "", StringComparison.Ordinal) ? Visibility.Hidden : Visibility.Visible;
    public void ResetAdvanceJvm() { Common.Reset("LaunchAdvanceJvm"); Refresh(); }
    public void ReplaceEnter(MyTextBoxView sender, TextChangedEventArgs args) {
        string next = MeloongCore.Extensions.StringExtensions.ReplaceLineEndings(sender.Text, " ", true); if (next == sender.Text) return;
        int caret = sender.CaretIndex; sender.Text = next; sender.CaretIndex = Math.Max(0, checked(caret - 1));
    }
    public void SwitchToInstance() { Host.LoadInstance(Host.SelectedInstance()!); Host.AssignSetupInstance(Host.SelectedInstance()!); Host.ShowInstanceSetup(); }
}
