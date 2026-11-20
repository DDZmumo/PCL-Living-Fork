using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

// Original LF overall page plus LF mode/component/logo compatibility. Fixed CE
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 corresponding .xaml.cs reviewed under
// Plain Craft Launcher 2/LICENCE; LF model, icons, settings and destructive-operation boundaries remain.
public sealed class InstanceOverallPageController<TInstance, TState, TCard, TItem, TCombo>
    where TInstance : LegacyMcInstance<TInstance, TState, TCard> where TState : struct, Enum where TCard : struct, Enum where TItem : Grid where TCombo : class {
    private readonly InstanceOverallPageView<TItem> view;
    private readonly InstanceOverallPageHost<TInstance, TItem, TCombo> host;
    private readonly Action<int> animation;
    public InstanceOverallPageController(InstanceOverallPageView<TItem> view, InstanceOverallPageHost<TInstance, TItem, TCombo> host, Action<int> animation, Func<int> animationValue) { this.view = view; this.host = host; this.animation = animation; Animation = animationValue; }
    private readonly Func<int> Animation;
    private static bool Same(string? a, string? b) => (a ?? "") == (b ?? "");
    private string Setup => host.Instance().PathVersion + @"PCL\Setup.ini";
    private void InvalidateList() => host.WriteIni(host.MinecraftFolder() + "PCL.ini", "InstanceCache", "");
    public void Loaded() { view.ScrollHome(); Reload(); if (view.IsLoaded()) return; view.SetLoaded(true); view.ResizeDisplay(); }
    public void RefreshModpackVisibility() {
        Visibility hidden = host.ModpackEnabled() ? Visibility.Collapsed : Visibility.Visible;
        view.Delete.Visibility = hidden; view.Star.Visibility = hidden; view.Installed.Visibility = host.ModpackEnabled() ? Visibility.Visible : Visibility.Collapsed;
        if (view.Header() is not null) {
            view.SetHeaderInfo(view.Header()!, host.ModpackEnabled() ? "" : view.OriginalHeaderInfo());
            foreach (Canvas logo in view.Header()!.Children.OfType<Canvas>()) {
                if (logo.Background is not ImageBrush brush || brush.Stretch == Stretch.Uniform) continue;
                ImageBrush uniform = brush.CloneCurrentValue(); uniform.Stretch = Stretch.Uniform; logo.Background = uniform;
            }
        }
    }
    public void Reload() {
        animation(1);
        view.DisplayType.SelectedIndex = Conversions.ToInteger(host.ReadIni(Setup, "DisplayType", "0"));
        view.SetStarText(host.Instance().IsStar ? "从收藏夹中移除" : "加入收藏夹");
        view.FolderMods.Visibility = host.Instance().Modable ? Visibility.Visible : Visibility.Collapsed;
        view.HeaderPanel.Children.Clear(); view.SetHeader(host.CreateHeader(host.Instance())); view.SetOriginalHeaderInfo(view.HeaderInfo(view.Header()!));
        view.Header()!.IsHitTestVisible = false; view.HeaderPanel.Children.Add(view.Header()); host.RefreshInstalled(); RefreshModpackVisibility(); host.RefreshPageName();
        view.DisplayLogo.SelectedIndex = 0; string logo = host.ReadLogo(Setup); bool custom = Conversions.ToBoolean(host.ReadIni(Setup, "LogoCustom", "False"));
        if (custom) foreach (FrameworkElement selection in view.DisplayLogo.Items) {
            if (Same(host.StorageLogo(Conversions.ToString(selection.Tag)!), logo) || Same(Conversions.ToString(selection.Tag), @"PCL\Logo.png") && logo.EndsWithF(@"PCL\Logo.png")) { view.DisplayLogo.SelectedItem = selection; break; }
        }
        animation(-1);
    }
    public void DisplayTypeChanged() {
        if (!(view.IsLoaded() && Animation() == 0)) return;
        if (view.DisplayType.SelectedIndex != 1) {
            try {
                host.WriteIni(Setup, "DisplayType", Conversions.ToString(view.DisplayType.SelectedIndex));
                host.Instance().DisplayType = (TCard)Enum.ToObject(typeof(TCard), Conversions.ToInteger(host.ReadIni(Setup, "DisplayType", "0")));
                host.RefreshModDisabled(); InvalidateList(); host.RefreshInstanceList();
            } catch (Exception ex) { host.Error(ex, $"修改版本分类失败（{host.Instance().Name}）"); }
            Reload();
        } else {
            try {
                if (!host.HideHintSeen()) {
                    if (host.Confirm("确认要从版本列表中隐藏该版本吗？隐藏该版本后，它将不再出现于 PCL 显示的版本列表中。\r\n此后，在版本列表页面按下 F11 才可以查看被隐藏的版本。", "隐藏版本提示", false) != 1) { view.DisplayType.SelectedIndex = 0; return; }
                    host.MarkHideHint();
                }
                host.WriteIni(Setup, "DisplayType", "1"); InvalidateList(); host.RefreshInstanceList();
            } catch (Exception ex) { host.Error(ex, $"隐藏版本 {host.Instance().Name} 失败"); }
        }
    }
    public void Description() {
        try {
            string oldInfo = host.ReadIni(Setup, "CustomInfo", ""); string? info = host.InputDescription("更改描述", oldInfo);
            if (info is not null && !Same(oldInfo, info)) host.WriteIni(Setup, "CustomInfo", info);
            host.SetInstance(host.LoadInstance(host.Instance().Name)); Reload(); host.RefreshInstanceList();
        } catch (Exception ex) { host.ErrorAlert(ex, $"版本 {host.Instance().Name} 描述更改失败"); }
    }
    public void Rename() {
        try {
            TInstance target = host.Instance();
            if (!MmcInstanceInspector.Read(target.PathVersion).IsLegacy && host.BeginManagedRename is not null) {
                host.BeginManagedRename(target); return;
            }
            MmcInstanceCapabilities.RequireLegacy(host.Instance().PathVersion, "重命名");
            if (host.Locked(host.Instance())) { host.HintRed("该版本正在被修改，请稍后再重命名！"); return; }
            string oldName = host.Instance().Name; string? name = host.InputName(oldName, host.MinecraftFolder() + "versions");
            if (string.IsNullOrWhiteSpace(name)) return;
            TInstance renamed = host.Rename(host.Instance(), name); host.HintGreen("重命名成功！"); host.SetInstance(renamed);
            if (host.SelectedInstance() is not null && host.SelectedInstance()!.Equals(host.Instance())) host.WriteIni(host.MinecraftFolder() + "PCL.ini", "Version", name!);
            Reload(); host.RefreshInstanceList();
        } catch (Exception ex) { host.ErrorAlert(ex, "重命名版本失败"); }
    }
    public void LogoChanged() {
        if (!(view.IsLoaded() && Animation() == 0)) return;
        try {
            if (ReferenceEquals(view.DisplayLogo.SelectedItem, view.CustomLogo)) {
                string? file = host.SelectImage(); if (string.IsNullOrEmpty(file)) { Reload(); return; }
                FileUtils.Copy(file!, host.Instance().PathVersion + @"PCL\Logo.png");
            } else FileUtils.Delete(host.Instance().PathVersion + @"PCL\Logo.png");
        } catch (Exception ex) { host.Error(ex, $"更改自定义版本图标失败（{host.Instance().Name}）"); }
        try {
            string logo = Conversions.ToString(((FrameworkElement)view.DisplayLogo.SelectedItem).Tag)!; host.WriteLogo(Setup, logo); host.WriteIni(Setup, "LogoCustom", Conversions.ToString(!Same(logo, "")));
            InvalidateList(); host.SetInstance(host.LoadInstance(host.Instance().Name)); Reload(); host.RefreshInstanceList();
        } catch (Exception ex) { host.Error(ex, $"更改版本图标失败（{host.Instance().Name}）"); }
    }
    public void Star() {
        try {
            host.WriteIni(Setup, "IsStar", Conversions.ToString(!host.Instance().IsStar)); host.SetInstance(host.LoadInstance(host.Instance().Name)); Reload(); host.ForceListRefresh(); host.RefreshInstanceList();
        } catch (Exception ex) { host.ErrorAlert(ex, $"版本 {host.Instance().Name} 收藏状态更改失败"); }
    }
    public void OpenInstanceFolder(TInstance instance) => host.Explorer(instance.PathVersion);
    public void OpenGameFolder(string leaf) { string folder = host.Instance().PathIndie + leaf + "\\"; DirectoryUtils.Create(folder); host.Explorer(folder); }
    public void Script() {
        try {
            MmcInstanceCapabilities.RequireLegacy(host.Instance().PathVersion, "导出旧启动脚本");
            string? path = host.SaveScript("选择脚本保存位置", "启动 " + host.Instance().Name + ".bat"); if (path is null) return;
            if (host.LaunchLoading()) { host.HintRed("请在当前启动任务结束后再试！"); return; }
            if (host.LaunchScript(host.Instance(), path)) host.Hint(host.LegacyLogin() ? "正在导出启动脚本……" : "正在导出启动脚本……（注意，使用脚本启动可能会导致登录失效！）");
        } catch (Exception ex) { host.ErrorAlert(ex, $"导出启动脚本失败（{host.Instance().Name}）"); }
    }
    public void Repair() {
        try {
            var target = host.Instance();
            if (host.Locked(target)) { host.HintRed("该版本正在被修改，请稍后再补全文件！"); return; }
            // Dispatch before legacy settings reads and library inference. Damaged,
            // pending and unsupported MMC evidence must never fall through to Forge fix.
            if (!MmcManagementProbe.Read(target.PathVersion).CanUseLegacyBackend && host.BeginManagedRepair is not null) {
                host.BeginManagedRepair(target); return;
            }
            MmcInstanceCapabilities.RequireLegacy(target.PathVersion, "旧版本文件补全");
            if (host.IgnoreFileCheck(host.Instance())) { host.HintBlue("请先关闭 [版本设置 → 设置 → 高级启动选项 → 关闭文件校验]，然后再尝试补全文件！"); return; }
            foreach (string ongoing in host.TaskNames()) if (ongoing == host.Instance().Name + " 文件补全") { host.HintRed("正在处理中，请稍候！"); return; }
            string name = host.Instance().Name + " 文件补全"; TCombo loader = host.CreateFixLoader(host.Instance(), name);
            host.HookState(loader, () => {
                int state = host.LoaderState(loader);
                if (state == host.Finished) host.HintGreen(host.LoaderName(loader) + "成功！");
                else if (state == host.Failed) host.HintRed(host.LoaderName(loader) + "失败：" + host.LoaderError(loader));
                else if (state == host.Canceled) host.HintBlue(host.LoaderName(loader) + "已取消！");
            });
            host.StartLoader(loader, host.Instance().Name); host.TaskbarAdd(loader); host.RefreshDownload(); host.RibbleDownload();
        } catch (Exception ex) { host.ErrorAlert(ex, $"尝试补全文件失败（{host.Instance().Name}）"); }
    }
    public void Delete() {
        if (host.ModpackEnabled()) return;
        TInstance? target = null;
        try {
            target = host.Instance();
            if (target.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed && host.BeginManagedDelete is not null) {
                string managedPath = target.PathVersion;
                Guid managedId = target.Config.ContextStamp.InstanceId;
                host.BeginManagedDelete(target, host.Shift(), () => {
                    host.RefreshInstanceList();
                    if (host.IsCurrentPage() && host.Instance() is TInstance shown && PathUtils.IsSame(shown.PathVersion, managedPath) &&
                        shown.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed && shown.Config.ContextStamp.InstanceId == managedId) host.PageBack();
                });
                return;
            }
            MmcInstanceCapabilities.RequireLegacy(target.PathVersion, "删除");
            if (host.Locked(target)) { host.HintRed("该版本正在被修改，请稍后再删除！"); return; }
            bool shift = host.Shift(); bool hintIndie = Convert.ToInt32(target.State) != 0 && !Same(target.PathIndie, host.MinecraftFolder());
            DirectoryInfo folder = DirectoryUtils.GetInfo(target.PathIndie + @"saves\");
            List<string> saves = hintIndie && folder.Exists ? folder.EnumerateDirectories().OrderByDescending(save => save.LastWriteTime).Select(save => $"{save.Name}（上次修改：{StringUtils.FormatTimeSpan(save.LastWriteTime - DateTime.Now, false)}）").ToList() : new List<string>();
            string text = $"你确定要{(shift ? "永久" : "")}删除版本 {target.Name} 吗？" + (hintIndie ? "\r\n该版本对应的存档、资源包、Mod 等文件也将被一并删除！" + (saves.Any() ? "\r\n\r\n这会删除以下存档：\r\n· " + string.Join("\r\n· ", saves) : "") : "");
            if (host.Confirm(text, "版本删除确认", hintIndie || shift) != 1) return;
            string targetPath = target.PathVersion;
            target.ResetSettingsCache();
            host.DeleteConfirmed(target, !shift, () => {
                host.HintGreen("已删除版本 " + target.Name + "！");
                host.RefreshInstanceList();
                // Browsing another instance during deletion must not navigate
                // that newer page away when the original operation completes.
                if (host.IsCurrentPage() && host.Instance() is TInstance current && PathUtils.IsSame(current.PathVersion, targetPath)) host.PageBack();
            });
        } catch (OperationCanceledException ex) { host.Warn(ex, $"删除版本 {target?.Name ?? "未知版本"} 被主动取消"); }
        catch (Exception ex) { host.ErrorAlert(ex, $"删除版本 {target?.Name ?? "未知版本"} 失败"); }
    }
}
