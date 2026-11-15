using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed partial class ModpackExportPageController<TInstance, TOption, TCheck, TLocal, TProject, TVersion, TCombo>
    where TInstance : class where TOption : ModpackExportOptionData where TCheck : FrameworkElement
    where TLocal : LocalResourceFileData<TProject, TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData where TCombo : class {
    public const string Separator = "==============================================================";
    private readonly ModpackExportPageView<TCheck> view;
    private readonly ModpackExportPageState state;
    private readonly ModpackExportPageHost<TInstance, TOption, TCombo> host;
    public ModpackExportPageController(ModpackExportPageView<TCheck> view, ModpackExportPageState state, ModpackExportPageHost<TInstance, TOption, TCombo> host) { this.view = view; this.state = state; this.host = host; }
    public void Initialized() { host.JavaWorker().Stopped += () => host.Ui(RefreshJavaInfo); }
    public void Loaded() {
        host.AdjustAnimation(1);
        if (!PathUtils.IsSame(state.CurrentInstance(), host.VersionFolder(host.Instance()))) RefreshAll();
        host.SetHelpEvent(host.ReleaseBuild() ? "指南/整合包制作 - Public.json" : "指南/整合包制作 - Snapshot.json");
        host.AdjustAnimation(-1);
    }
    public void RefreshAll() {
        host.Info("刷新导出页面");
        view.HintOptiFine.Visibility = host.Version(host.Instance()).HasOptiFine && !host.Version(host.Instance()).OptiFineAsMod ? Visibility.Visible : Visibility.Collapsed;
        state.SetCurrentInstance(host.VersionFolder(host.Instance()));
        view.Name.Text = ""; view.SetNameHint(host.Name(host.Instance())); view.Version.Text = ""; view.SetVersionHint("1.0.0");
        view.SetChecked(view.IncludeAssets, false); view.SetChecked(view.Modrinth, false); view.SetChecked(view.Isolation, true);
        GetOption(view.Basic).Description = host.VersionDescription(host.Instance());
        ResetConfigOverrides(); ReloadAllSubOptions(); RefreshOptions(); RefreshJavaInfo(); view.ScrollHome();
    }
    public TOption GetOption(TCheck check) => (TOption)check.Tag;
    public IEnumerable<TCheck> AllOptions(bool includeHidden) {
        foreach (UIElement element in view.Options.Children) {
            if (!includeHidden && element.Visibility != Visibility.Visible) continue;
            if (element is TCheck check) yield return check;
            else if (element is StackPanel panel) foreach (UIElement sub in panel.Children) {
                if (!includeHidden && sub.Visibility != Visibility.Visible) continue;
                if (sub is TCheck item) yield return item;
            }
        }
    }
    public void ReloadAllSubOptions() {
        ReloadSubOptions(view.ResourcePacks, true, true, "resourcepacks", "texturepacks");
        ReloadSubOptions(view.Saves, false, true, "saves"); ReloadSubOptions(view.Shaders, true, true, "shaderpacks");
    }
    public void ReloadSubOptions(StackPanel panel, bool compressed, bool folders, params string[] names) {
        panel.Children.Clear();
        foreach (string name in names) {
            DirectoryInfo target = DirectoryUtils.GetInfo(host.GameRoot(host.Instance()) + name);
            if (!target.Exists) continue;
            if (compressed) foreach (FileInfo file in target.EnumerateFiles("*.zip").Concat(target.EnumerateFiles("*.rar"))) {
                if (state.BlackList().Any(file.Name.Contains)) continue;
                TOption option = host.CreateOption(); option.Title = file.Name; option.DefaultChecked = true; option.Rules = StringUtils.LikePatternEscape($"{name}/{file.Name}");
                panel.Children.Add(view.CreateCheck(option));
            }
            if (folders) foreach (DirectoryInfo sub in target.EnumerateDirectories().OrderByDescending(folder => folder.LastWriteTime)) {
                if (state.BlackList().Any(sub.Name.Contains)) continue;
                if (!sub.EnumerateFileSystemInfos().Any()) continue;
                TOption option = host.CreateOption(); option.Title = sub.Name; option.DefaultChecked = true; option.Rules = StringUtils.LikePatternEscape($"{name}/{sub.Name}/");
                TCheck check = view.CreateCheck(option);
                if (compressed) GetOption(check).Description = "文件夹";
                if (ReferenceEquals(panel, view.Saves)) GetOption(check).Description = sub.LastWriteTime.ToString("yyyy'/'MM'/'dd HH':'mm");
                panel.Children.Add(check);
            }
        }
    }
    public void RefreshOptions() {
        bool ValidDirectory(DirectoryInfo folder) {
            try { return folder.Exists && folder.EnumerateFileSystemInfos().Any(info => !state.BlackList().Any(info.Name.Contains)); }
            catch { return false; }
        }
        DirectoryInfo root = DirectoryUtils.GetInfo(host.GameRoot(host.Instance()));
        List<string> entries = root.EnumerateFiles().Select(file => file.Name).ToList();
        foreach (DirectoryInfo sub in root.EnumerateDirectories().Where(ValidDirectory)) {
            entries.Add(sub.Name + "\\"); entries.AddRange(sub.EnumerateFiles().Select(file => $"{sub.Name}\\{file.Name}"));
            entries.AddRange(sub.EnumerateDirectories().Where(ValidDirectory).Select(folder => $"{sub.Name}\\{folder.Name}\\"));
        }
        host.Info($"共发现 {entries.Count} 个可行的二级文件/文件夹");
        bool Visible(TOption option) {
            if (option.RequireOptiFine && !host.Version(host.Instance()).HasOptiFine) return false;
            if (option.RequireModLoader && !host.Modable(host.Instance())) return false;
            if (option.RequireModLoaderOrOptiFine && !host.Version(host.Instance()).HasOptiFine && !host.Modable(host.Instance())) return false;
            return ModpackExportPipeline<TInstance, TLocal, TProject, TVersion>.StandardizeLines((option.Rules ?? option.ShowRules)!.Split('|'), true).Any(rule => {
                if (rule.StartsWithF("!")) return false;
                try { if (entries.Any(entry => ModpackExportPipeline<TInstance, TLocal, TProject, TVersion>.Like(entry, rule))) return true; }
                catch (Exception ex) { host.ErrorToast(ex, $"错误的规则：{rule}"); return false; }
                string path = rule.Trim('*', '?');
                if (path.Split('\\', true).Length >= 3) {
                    if (path.EndsWithF("\\")) return ValidDirectory(DirectoryUtils.GetInfo(host.GameRoot(host.Instance()) + path));
                    return FileUtils.Exists(host.GameRoot(host.Instance()) + path);
                }
                return false;
            });
        }
        foreach (TCheck check in AllOptions(true)) {
            TOption option = GetOption(check);
            if (string.IsNullOrEmpty(option.Rules) && string.IsNullOrEmpty(option.ShowRules)) { check.Visibility = Visibility.Visible; view.SetChecked(check, option.DefaultChecked); }
            else { bool pass = Visible(option); check.Visibility = pass ? Visibility.Visible : Visibility.Collapsed; view.SetChecked(check, option.DefaultChecked && pass); }
            view.Inlines(check).Clear();
            if (check.Visibility != Visibility.Visible) continue;
            view.Inlines(check).Add(new Run(option.Title));
            if (!string.IsNullOrEmpty(option.Description)) view.Inlines(check).Add(new Run("   " + option.Description) { Foreground = view.Gray() });
        }
    }
    public void RefreshJavaInfo() {
        int javaType = host.JavaType(host.Instance()); RedoableWorker<Java> worker = host.JavaWorker();
        Java? java = javaType == 2 && worker.LastSucceeded && !worker.Running ? worker.LastResult : null;
        if (java is null) view.Java.Visibility = Visibility.Collapsed;
        else {
            view.Java.Visibility = Visibility.Visible; view.Inlines(view.Java).Clear(); view.Inlines(view.Java).Add(new Run(GetOption(view.Java).Title));
            string folder = PathUtils.AddSlashSuffix(PathUtils.RemoveLastPart(java.Folder.Replace(PathUtils.RemoveLastPart(PathUtils.RemoveLastPart(host.VersionFolder(host.Instance()))), ""))).TrimStart('\\');
            view.Inlines(view.Java).Add(new Run("   " + $"Java {java.Version!.Major} ({java.Version})：{folder}") { Foreground = view.Gray() });
        }
        if (javaType == 3) {
            view.Export.IsEnabled = true; view.HintJava.Visibility = Visibility.Visible;
            hostJavaHint($"该版本设置要求 {host.QuoteLeft()}使用指定的 Java{host.QuoteRight()}，这在导出后很可能会失效。建议修改为 {host.QuoteLeft()}使用版本文件夹中的 Java{host.QuoteRight()}。", false);
        } else if (javaType == 2 && worker.HasSucceeded && worker.LastResult is null) {
            view.Export.IsEnabled = false; view.HintJava.Visibility = Visibility.Visible;
            hostJavaHint($"该版本设置要求 {host.QuoteLeft()}使用版本文件夹中的 Java{host.QuoteRight()}，但版本文件夹中没能找到任何 Java。", true);
        } else { view.Export.IsEnabled = true; view.HintJava.Visibility = Visibility.Collapsed; }
    }
    private void hostJavaHint(string text, bool red) => view.SetJavaHint(text, red);
    public void NameGotFocus() { if (view.Name.Text == "") { view.Name.Text = view.NameHint(); view.Name.SelectionStart = view.Name.Text.Length; } }
    public void IncludeChanged() {
        if (view.Checked(view.IncludeAssets)) view.SetChecked(view.Modrinth, false);
        view.Modrinth.IsEnabled = !view.Checked(view.IncludeAssets);
    }
    public void ModrinthChanged() { view.HintModrinth.Visibility = view.Checked(view.Modrinth) && view.Checked(view.Launcher) ? Visibility.Visible : Visibility.Collapsed; }
}
