using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Input;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

using PCLCS.Startup;

namespace PCLLF.Presentation.Windowing;

public sealed partial class LegacyMainWindowController<TStack, TInstance, TAnimation>
    where TStack : class, ILegacyMainPageStack, new() where TInstance : class where TAnimation : struct {
    public void KeyDown(KeyEventArgs args) {
        if (args.IsRepeat) return;
        if (args.SystemKey is Key.LeftAlt or Key.RightAlt) args.Handled = true;
        if (view.PanMsg().Children.Count > 0) {
            if (args.Key == Key.Enter) host.FirstDialog().First();
            else if (args.Key == Key.Escape) {
                LegacyMainDialog dialog = host.FirstDialog();
                if (!dialog.IsInputOrSelect && dialog.ThirdVisibility() == Visibility.Visible) dialog.Third();
                else if (dialog.SecondVisibility() == Visibility.Visible) dialog.Second();
                else dialog.First();
            }
            return;
        }
        if (args.Key == Key.Escape) {
            if (IsPage(PageCurrent, LegacyMainPage.Launch) && host.IsLaunching()) { host.CancelLaunch(); return; }
            TriggerPageBack();
        }
        if (args.Key == Key.F11 && IsPage(PageCurrent, LegacyMainPage.InstanceSelect)) { host.WriteHiddenVersions(!host.HiddenVersions()); host.ReloadInstanceList(true); return; }
        if (args.Key == Key.F12 && view.UsesGlassShell?.Invoke() != true) { host.WriteHiddenForceShow(!host.HiddenForceShow()); Hint(host.HiddenForceShow() ? "功能隐藏设置已暂时关闭！" : "功能隐藏设置已重新开启！", LegacyMainHint.Green); host.RefreshHidden(); return; }
        if (args.Key == Key.F5) { if (host.CanRefresh(PageLeft)) host.Refresh(PageLeft); if (host.CanRefresh(PageRight)) host.Refresh(PageRight); return; }
        if (args.Key == Key.Enter && IsPage(PageCurrent, LegacyMainPage.Launch)) { if (host.AprilEnabled() && !host.AprilGivenUp()) Hint("木大！"); else host.LaunchButton(); }
    }
    public void MouseDown(MouseButtonEventArgs args) {
        if (host.GlobalMessageCount() > 0 || host.WaitingMessages()) return;
        if (args.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) TriggerPageBack();
    }
    public void TriggerPageBack() {
        if (IsPage(PageCurrent, LegacyMainPage.Download) && PageCurrentSub == 0 && host.IsInstallSelection()) host.ExitInstallSelection();
        else PageBack();
    }
    public void Activated() {
        try {
            if (IsPage(PageCurrent, LegacyMainPage.InstanceSetup) && PageCurrentSub == 3) host.ReloadMods();
            else if (IsPage(PageCurrent, LegacyMainPage.InstanceSetup) && (PageCurrentSub == 1 || PageCurrentSub == 5)) host.ReloadCurrentJava();
            else if (IsPage(PageCurrent, LegacyMainPage.InstanceSelect)) host.ReloadInstanceList(false);
        } catch (Exception ex) { Logger.Error(ex, "切回窗口时出错"); }
        // LF's LEGACY_LINK clipboard-autojoin block returns unconditionally here (its revival TODO).
        // Keep it disabled in both builds rather than activating the unreachable legacy join code.
    }
    public void HandleDrag(object sender, DragEventArgs args, ref IDataObject? previousDragData, ref DragDropEffects previousDragEffects) {
        try {
            if (args.Handled && args.Effects != DragDropEffects.None) return;
            args.Handled = true;
            if (ReferenceEquals(args.Data, previousDragData)) { args.Effects = previousDragEffects; return; }
            args.Effects = DragDropEffects.None;
            if (args.Data.GetDataPresent(DataFormats.Text)) {
                string text = Conversions.ToString(args.Data.GetData(DataFormats.Text))!;
                if (text.StartsWithF("authlib-injector:yggdrasil-server:")) args.Effects = DragDropEffects.Copy;
                else if (text.StartsWithF("file:///")) args.Effects = DragDropEffects.Copy;
            } else if (args.Data.GetDataPresent(DataFormats.FileDrop)) {
                string[]? files = (string[]?)args.Data.GetData(DataFormats.FileDrop); if (files is not null && files.Length > 0) args.Effects = DragDropEffects.Link;
            }
            previousDragData = args.Data; previousDragEffects = args.Effects; Logger.Info($"设置拖放类型：{args.Effects}");
        } catch (Exception ex) { Logger.Error(ex, "处理拖放时出错"); }
    }
    public void Drop(object sender, DragEventArgs args) {
        try {
            ShowWindowToTop();
            if (args.Data.GetDataPresent(DataFormats.Text)) {
                try {
                    string text = Conversions.ToString(args.Data.GetData(DataFormats.Text))!; Logger.Info($"接受文本拖拽：{text}");
                    if (text.StartsWithF("authlib-injector:yggdrasil-server:")) {
                        args.Handled = true; args.Effects = DragDropEffects.Copy;
                        string server = StringUtils.FormUrlUnescape(text.Substring("authlib-injector:yggdrasil-server:".Length)); Logger.Info($"Authlib 拖拽：{server}");
                        if (!string.IsNullOrEmpty(host.ValidateHttp(server))) { Hint($"输入的 Authlib 验证服务器不符合网址格式（{server}）！", LegacyMainHint.Red); return; }
                        TInstance? target = IsPage(PageCurrent, LegacyMainPage.InstanceSetup) ? host.SettingsInstance() : host.SelectedInstance();
                        if (target is null) { Hint("请先下载游戏，再设置第三方登录！", LegacyMainHint.Red); return; }
                        if (server == "https://littleskin.cn/api/yggdrasil") {
                            if (Message($"是否要在版本 {host.InstanceName(target)} 中开启 LittleSkin 登录？\r\n你可以在 版本设置 → 设置 → 服务器选项 中修改登录方式。", "第三方登录开启确认", second: "取消") == 2) return;
                            host.WriteInstanceSetting("VersionServerLogin", 4, target); host.WriteInstanceSetting("VersionServerAuthServer", "https://littleskin.cn/api/yggdrasil", target);
                            host.WriteInstanceSetting("VersionServerAuthRegister", "https://littleskin.cn/auth/register", target); host.WriteInstanceSetting("VersionServerAuthName", "LittleSkin 登录", target);
                        } else {
                            if (Message($"是否要在版本 {host.InstanceName(target)} 中开启第三方登录？\r\n登录服务器：{server}\r\n\r\n你可以在 版本设置 → 设置 → 服务器选项 中修改登录方式。", "第三方登录开启确认", second: "取消") == 2) return;
                            host.WriteInstanceSetting("VersionServerLogin", 4, target); host.WriteInstanceSetting("VersionServerAuthServer", server, target);
                            host.WriteInstanceSetting("VersionServerAuthRegister", server.Replace("api/yggdrasil", "auth/register"), target); host.WriteInstanceSetting("VersionServerAuthName", "", target);
                        }
                        if (IsPage(PageCurrent, LegacyMainPage.InstanceSetup) && PageCurrentSub == 1) host.ReloadInstanceSetup();
                        else if (IsPage(PageCurrent, LegacyMainPage.Launch)) host.RefreshLaunchPage();
                    } else if (text.StartsWithF("file:///")) {
                        string file = WebUtility.UrlDecode(text)!.Substring("file:///".Length).Replace("/", @"\"); args.Handled = true; args.Effects = DragDropEffects.Copy; FileDrag(new List<string> { file });
                    }
                } catch (Exception ex) { Logger.Warn(ex, "无法接取文本拖拽事件"); return; }
            } else if (args.Data.GetDataPresent(DataFormats.FileDrop)) {
                object? files = args.Data.GetData(DataFormats.FileDrop);
                if (files is null) { Hint("请将文件解压后再拖入！", LegacyMainHint.Red); return; }
                args.Handled = true; args.Effects = DragDropEffects.Link; FileDrag((IEnumerable<string>)files);
            }
        } catch (Exception ex) { Logger.Error(ex, "接取拖拽事件失败"); }
    }
    public void FileDrag(IEnumerable<string> filePaths) => RunThread(() => {
        string file = filePaths.First(); Logger.Warn($"接受文件拖拽：{file}{(filePaths.Any() ? $" 等 {filePaths.Count()} 个文件" : "")}");
        if (DirectoryUtils.Exists(filePaths.First()) && !FileUtils.Exists(filePaths.First())) { Hint("请拖入一个文件，而非文件夹！", LegacyMainHint.Red); return; }
        else if (!FileUtils.Exists(filePaths.First())) { Hint("拖入的文件不存在：" + filePaths.First(), LegacyMainHint.Red); return; }
        List<string> paths = filePaths.ToList();
        if (filePaths.Count() > 1) foreach (string path in paths) { if (!new[] { "jar", "litemod", "disabled", "old" }.Contains(PathUtils.GetExtension(path))) { Hint("一次请只拖入一个文件！", LegacyMainHint.Red); return; } }
        string extension = PathUtils.GetExtension(file);
        if (extension == "xaml") {
            Logger.Info("文件后缀为 XAML，作为主页加载");
            if (FileUtils.Exists(Paths.Base + @"PCL\Custom.xaml") && Message("已存在一个主页文件，是否要将它覆盖？", "覆盖确认", "覆盖", "取消") == 2) return;
            FileUtils.Copy(file, Paths.Base + @"PCL\Custom.xaml"); host.Ui(() => { host.WriteSetting("UiCustomType", 1); host.RefreshCustomHome(); Hint("已加载主页自定义文件！", LegacyMainHint.Green); }); return;
        }
        if (host.InstallMods(paths)) return;
        if (new[] { "zip", "rar", "mrpack" }.Any(value => value == extension)) {
            Hint("正在读取压缩包……");
            try { Logger.Info("文件为压缩包，尝试作为整合包安装"); host.InstallModpack(file); host.Ui(() => host.GlobalNavigate((int)LegacyMainPage.TaskManager)); return; }
            catch (Exception ex) { if (ex.IsCanceled()) return; }
        }
        if (extension == "rar") { Hint("PCL 无法处理 rar 格式的压缩包，请在解压后重新压缩为 zip 格式再试！", LegacyMainHint.Red); return; }
        try {
            Logger.Info("尝试进行错误报告分析"); LegacyMainCrashAnalyzer analyzer = host.CreateCrashAnalyzer(); analyzer.Import(file);
            if (analyzer.Prepare()) { analyzer.Analyze(); analyzer.Output(true, new List<string>()); return; }
        } catch (Exception ex) { Logger.Error(ex, "自主错误报告分析失败"); }
        Hint("PCL 无法确定适合的操作……", LegacyMainHint.Red);
    }, "文件拖拽");
    public void DragTick() { if (host.DragControl() is null) return; if (Mouse.LeftButton != MouseButtonState.Pressed) DragStop(); }
    public void DragDoing() { if (host.DragControl() is null) return; if (Mouse.LeftButton == MouseButtonState.Pressed) host.DragControl()!.DragDoing(); else DragStop(); }
    public void DragStop() => host.Ui(() => { if (host.DragControl() is null) return; var control = host.DragControl(); host.ClearDragControl(); control!.DragStop(); });
    public void MouseMove(MouseEventArgs args) => host.WriteLastMouseArg(args);
}
