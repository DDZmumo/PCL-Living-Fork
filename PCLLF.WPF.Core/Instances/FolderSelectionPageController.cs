using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCLCS;

public sealed class SelectionPageInput {
    public string Title = null!, Caption = "", Button1 = "确定", Button2 = "取消";
    public string? Default;
    public bool DeleteConfirmation, IsWarn;
}
public sealed class FolderSelectionPageHost<TFolder, TInstance> where TFolder : class where TInstance : class {
    public Func<Panel> Panel = null!, OwnerPanel = null!;
    public Func<List<TFolder>> Folders = null!;
    public Func<bool> ModpackEnabled = null!, CurrentPageExists = null!, AnyLocked = null!;
    public Func<bool, bool> Downloading = null!;
    public Func<TFolder> PackFolder = null!;
    public Func<string> BaseFolder = null!, SelectedFolder = null!, ReadFolders = null!;
    public Action<string> SetSelectedFolder = null!, WriteFolders = null!;
    public Action<Action> SubscribePreviewFinish = null!, UiWait = null!, RunThread = null!;
    public Action<Action, string, ThreadPriority> RunNewThread = null!;
    public Action RefreshFolders = null!;
    public Action<bool> RefreshInstances = null!;
    public Func<string, bool> Permission = null!;
    public Func<string, bool, IEnumerable<string>> SelectFolder = null!;
    public Func<SelectionPageInput, string?> Input = null!;
    public Func<SelectionPageMessage, int> Message = null!;
    public Action<string, SelectionPageHint> Hint = null!;
    public Func<string, ContextMenu> ParseMenu = null!;
    public Action<ContextMenu, string> BindMenu = null!;
    public Func<TFolder, ContextMenu?, FrameworkElement> FolderRow = null!;
    public Func<int, FrameworkElement> ActionRow = null!;
    public Func<Action, FrameworkElement> ConfigureButton = null!;
    public Action<FrameworkElement, IEnumerable<FrameworkElement>> SetButtons = null!;
    public Action<FrameworkElement, Action<FrameworkElement, bool, Action>> BindChanged = null!;
    public Action<FrameworkElement, Action> BindClick = null!;
    public Action<FrameworkElement, bool, bool> CheckRow = null!;
    public Func<FrameworkElement, bool> RowChecked = null!;
    public Func<object, FrameworkElement> MenuRow = null!;
    public Func<FrameworkElement, string> RowInfo = null!;
    public Action InstallPack = null!;
    public Func<string, TInstance> NewInstance = null!;
    public Action<string, int, TInstance> WriteInstanceInteger = null!;
    public Action<string, bool, TInstance> WriteInstanceBoolean = null!;
    public Action<string> CreateProfiles = null!, OpenExplorer = null!;
    public Action<string, string, object> WriteIni = null!;
    public Func<int> NextUuid = null!;
}
// LF folder selection and destructive-operation guards, LF LICENCE. Original folder order and persisted LaunchFolders syntax are unchanged.
public sealed partial class FolderSelectionPageController<TFolder, TKind, TInstance, TState, TCard>
    where TFolder : LegacyMinecraftFolder<TKind> where TKind : struct, Enum
    where TInstance : LegacyMcInstance<TInstance, TState, TCard> where TState : struct, Enum where TCard : struct, Enum {
    private readonly FolderSelectionPageHost<TFolder, TInstance> host;
    private bool firstLoad = true;
    private List<TFolder>? lastFolders;
    public FolderSelectionPageController(FolderSelectionPageHost<TFolder, TInstance> host) { this.host = host; }
    private static int Kind(TFolder folder) => Convert.ToInt32(folder.Type);
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    public void Initialize() => host.SubscribePreviewFinish(() => { if (host.CurrentPageExists()) host.UiWait(Render); });
    public void Loaded() { if (firstLoad) Render(); firstLoad = false; }
    public void RefreshForModpackMode() { lastFolders = null; Render(); }
    private static TextBlock Heading(string text) => new() { Text = text, Margin = new Thickness(13, 18, 5, 4), Opacity = .6, FontSize = 12 };
    public void Render() {
        try {
            if (host.ModpackEnabled()) { RenderPackFolderList(); return; }
            if (lastFolders is not null && lastFolders.SequenceEqual(host.Folders())) {
                bool equal = true; for (int i = 0; i < lastFolders.Count; i++) if (!lastFolders[i].Equals(host.Folders()[i])) { equal = false; break; }
                if (equal) return;
            }
            lastFolders = host.Folders();
            host.Panel().Children.Clear(); host.Panel().Children.Add(Heading("文件夹列表"));
            foreach (TFolder folder in host.Folders().ToArray()) {
                int kind = Kind(folder); ContextMenu? menu = kind >= 0 && kind <= 2 ? host.ParseMenu(MenuXml[kind]) : null;
                if ((kind == 0 || kind == 1) && PathUtils.IsSame(folder.Location, host.BaseFolder() + ".minecraft\\") && host.Folders().IsSingle()) ((MenuItem)menu!.FindName("Delete")!).Header = "清空";
                if (kind != 0) host.BindMenu(menu!, "Remove");
                host.BindMenu(menu!, "Open"); host.BindMenu(menu!, "Delete"); host.BindMenu(menu!, "Rename"); host.BindMenu(menu!, "Refresh");
                FrameworkElement row = host.FolderRow(folder, menu);
                host.BindChanged(row, (item, mouse, cancel) => ChangeFolder(item, mouse, cancel, host));
                FrameworkElement button = host.ConfigureButton(() => { menu!.PlacementTarget = row; menu.IsOpen = true; }); host.SetButtons(row, new[] { button });
                host.Panel().Children.Add(row); Logger.Info($"有效的 Minecraft 文件夹：{folder.Name} > {folder.Location}");
            }
            host.Panel().Children.Add(Heading("添加或导入"));
            if (!DirectoryUtils.Exists(host.BaseFolder() + ".minecraft\\")) { FrameworkElement create = host.ActionRow(0); host.Panel().Children.Add(create); host.BindClick(create, () => Create(host)); }
            FrameworkElement add = host.ActionRow(1); host.Panel().Children.Add(add); host.BindClick(add, () => Add(host));
            FrameworkElement install = host.ActionRow(2); host.Panel().Children.Add(install); host.BindClick(install, host.InstallPack);
            host.Panel().Children.Add(new FrameworkElement { Height = 10, IsHitTestVisible = false });
            for (int i = 0; i < host.Folders().Count; i++) if (PathUtils.IsSame(host.Folders()[i].Location, host.SelectedFolder())) { host.CheckRow((FrameworkElement)host.Panel().Children[i + 1], true, false); return; }
            if (!host.Folders().Any()) throw new ArgumentNullException("没有可用的 Minecraft 文件夹");
            host.SetSelectedFolder(host.Folders().First().Location); host.CheckRow((FrameworkElement)host.Panel().Children[1], true, false);
        } catch (Exception error) { Logger.Error(error, "构建 Minecraft 文件夹列表 UI 出错"); }
        finally { host.RefreshInstances(false); }
    }
    public void RenderPackFolderList() {
        TFolder folder = host.PackFolder(); host.OwnerPanel().Children.Clear(); host.OwnerPanel().Children.Add(Heading("文件夹列表"));
        FrameworkElement row = host.FolderRow(folder, null); host.CheckRow(row, true, true); host.OwnerPanel().Children.Add(row);
    }
    public static void Add(FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return;
        string? newFolder = "";
        if (host.AnyLocked() || host.Downloading(false)) { host.Hint("请在其他任务完成后，再添加游戏文件夹！", SelectionPageHint.Red); return; }
        try {
            newFolder = host.SelectFolder("选择 Minecraft 文件夹", false).FirstOrDefault(); if (newFolder is null) return;
            if (newFolder.Contains("!") || newFolder.Contains(";")) { host.Hint("Minecraft 文件夹路径中不能含有感叹号或分号！", SelectionPageHint.Red); return; }
            string defaultName = PathUtils.GetLastPart(newFolder);
            if (Same(defaultName, ".minecraft")) { string parent = PathUtils.RemoveLastPart(newFolder); defaultName = parent.EndsWithF(":") ? "" : PathUtils.GetLastPart(parent); }
            if (defaultName.Length > 40) defaultName = defaultName.Substring(0, 39);
            string? name = host.Input(new SelectionPageInput { Title = "输入显示名称", Caption = "输入该文件夹在左边栏列表中显示的名称。", Default = defaultName });
            if (string.IsNullOrWhiteSpace(name)) return;
            AddFolder(newFolder, name!, true, host);
        } catch (Exception error) { Logger.Error(error, $"添加文件夹失败（{newFolder}）"); }
    }
    public static void AddFolder(string folderPath, string displayName, bool showHint, FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return;
        host.RunThread(() => {
            try {
                if (host.ModpackEnabled()) return; folderPath = PathUtils.AddSlashSuffix(folderPath);
                if (!host.Permission(folderPath)) { if (showHint) { host.Hint("添加文件夹失败：PCL 没有访问该文件夹的权限！", SelectionPageHint.Red); return; } throw new Exception("PCL 没有访问文件夹的权限：" + folderPath); }
                if (!host.Permission(folderPath + "versions\\")) foreach (string folder in DirectoryUtils.EnumerateDirectories(folderPath, true)) if (host.Permission(folder + "\\versions\\")) { folderPath = folder + "\\"; break; }
                List<string> folders = new(host.ReadFolders().Split('|')); bool added = false, replace = false;
                for (int i = 0; i < folders.Count; i++) {
                    string folder = folders[i]; if (Same(folder, "")) continue;
                    if (!PathUtils.IsSame(folder.Split('>')[1], folderPath)) continue; added = true;
                    if (Same(folder.Split('>')[0], displayName)) { if (showHint) host.Hint("此文件夹已在列表中！", SelectionPageHint.Blue); return; }
                    folders[i] = displayName + ">" + folderPath; replace = true; if (showHint) host.Hint("文件夹名称已更新为 " + displayName + " ！", SelectionPageHint.Green); break;
                }
                if (!added) folders.Add(displayName + ">" + folderPath); if (host.ModpackEnabled()) return;
                host.WriteFolders(string.Join("|", folders)); host.SetSelectedFolder(folderPath); host.RefreshFolders(); if (replace) return;
                if (showHint) host.Hint("文件夹 " + displayName + " 已添加！", SelectionPageHint.Green);
                DirectoryInfo mods = DirectoryUtils.GetInfo(folderPath + "mods\\"); if (!(mods.Exists && mods.EnumerateFiles().Count() >= 3)) return;
                DirectoryInfo versions = DirectoryUtils.GetInfo(folderPath + "versions\\"); if (!(versions.Exists && versions.EnumerateDirectories().Count() <= 3)) return;
                foreach (DirectoryInfo folder in versions.EnumerateDirectories()) {
                    TInstance instance = host.NewInstance(folder.FullName); instance.Load(); if (!instance.Modable) continue;
                    DirectoryInfo indieMods = DirectoryUtils.GetInfo(instance.PathVersion + "mods\\"); if (indieMods.Exists && indieMods.EnumerateFiles().Any()) return;
                    host.WriteInstanceInteger("VersionArgumentIndie", 2, instance); host.WriteInstanceBoolean("VersionArgumentIndieV2", false, instance); Logger.Warn($"已自动关闭单版本隔离：{instance.Name}");
                }
            } catch (Exception error) { Logger.Error(error, "向文件夹列表中添加新文件夹失败"); }
        });
    }
    public static void Create(FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return;
        if (host.AnyLocked() || host.Downloading(false)) { host.Hint("请在其他任务完成后，再创建游戏文件夹！", SelectionPageHint.Red); return; }
        CreateInCurrentPath(host); host.Hint("新建 .minecraft 文件夹成功！", SelectionPageHint.Green);
    }
    public static void CreateInCurrentPath(FolderSelectionPageHost<TFolder, TInstance> host) {
        host.SetSelectedFolder(host.BaseFolder() + ".minecraft\\"); if (!DirectoryUtils.Exists(host.SelectedFolder())) { DirectoryUtils.Create(host.SelectedFolder() + "versions\\"); host.CreateProfiles(host.SelectedFolder()); }
        AddFolder(host.SelectedFolder(), PathUtils.GetLastPart(host.BaseFolder()), false, host);
    }
    public static void Remove(object sender, FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return;
        try {
            TFolder target = (TFolder)host.MenuRow(sender).Tag;
            if (Kind(target) == 2) {
                if (host.AnyLocked() || host.Downloading(false)) { host.Hint("请在其他任务完成后，再移除游戏文件夹！", SelectionPageHint.Red); return; }
                switch (host.Message(new SelectionPageMessage { Text = "是否需要清理 PCL 在该文件夹中的配置文件？\r\n这包括各个版本的独立设置（如自定义图标、第三方登录配置）等，对游戏本身没有影响。", Title = "配置文件清理", Button1 = "删除", Button2 = "保留", Button3 = "取消" })) {
                    case 1: FileUtils.Delete(target.Location + "PCL.ini"); foreach (string instance in DirectoryUtils.EnumerateDirectories(target.Location + "versions\\")) DirectoryUtils.Delete(Path.Combine(instance, "PCL"), true); break;
                    case 2: break; case 3: return;
                }
            }
            string? deleted = RemoveFromSetup(target, host); host.Hint(Kind(target) == 2 ? "文件夹 " + deleted + " 已从列表中移除！" : "文件夹名称已复原！", SelectionPageHint.Green); host.RefreshFolders();
        } catch (Exception error) { Logger.Error(error, "从列表中移除游戏文件夹失败"); }
    }
    public static void Delete(object sender, FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return;
        if (host.AnyLocked() || host.Downloading(false)) { host.Hint("请在其他任务完成后，再删除游戏文件夹！", SelectionPageHint.Red); return; }
        TFolder target = (TFolder)host.MenuRow(sender).Tag;
        string text = (Kind(target) == 0 || Kind(target) == 1) && Same(target.Location, host.BaseFolder() + ".minecraft\\") && host.Folders().IsSingle() ? "清空" : "删除";
        if (host.Message(new SelectionPageMessage { Text = "你确定要" + text + "这个文件夹吗？\r\n目标文件夹：" + target.Location + "\r\n\r\n该文件夹中的游戏存档、游戏版本，以及 MC 之外的其他文件，都会永久丢失，不可恢复！", Title = "警告", Button1 = "取消", Button2 = "确认", Button3 = "取消" }) != 2) return;
        if (host.Input(new SelectionPageInput { Title = "删除确认", Caption = "该文件夹中的游戏存档、游戏版本，以及 MC 之外的其他文件，都会永久丢失，不可恢复！\r\n目标文件夹：" + target.Location + "\r\n\r\n如果确实要删除，请在下面输入【Potato】这个单词以继续。", DeleteConfirmation = true, Button1 = "永久删除文件夹", Button2 = "取消", IsWarn = true }) is null) return;
        RemoveFromSetup(target, host);
        host.RunNewThread(() => {
            try { host.Hint("正在" + text + "文件夹 " + target.Name + "！", SelectionPageHint.Blue); DirectoryUtils.Delete(target.Location); if (Same(text, "清空")) DirectoryUtils.Create(target.Location); host.Hint("已" + text + "文件夹 " + target.Name + "！", SelectionPageHint.Green); }
            catch (Exception error) { Logger.Error(error, $"{text}文件夹 {target.Name} 失败", LogBehavior.Toast); }
            finally { host.RefreshFolders(); }
        }, "Folder Delete " + host.NextUuid(), ThreadPriority.BelowNormal);
    }
    public static string? RemoveFromSetup(TFolder target, FolderSelectionPageHost<TFolder, TInstance> host) {
        string? deleted = null; List<string> folders = host.ReadFolders().Split('|', true).ToList();
        foreach (string folder in folders.ToList()) { if (!folder.Contains(">") || !PathUtils.IsSame(folder.AfterFirst(">"), target.Location)) continue; deleted = folder.BeforeFirst(">"); folders.Remove(folder); break; }
        host.WriteFolders(string.Join("|", folders)); return deleted;
    }
    public static void Open(object sender, FolderSelectionPageHost<TFolder, TInstance> host) => host.OpenExplorer(host.RowInfo(host.MenuRow(sender)));
    public static void RefreshClick(object sender, FolderSelectionPageHost<TFolder, TInstance> host) { TFolder data = (TFolder)host.MenuRow(sender).Tag; Refresh(data.Location, host); }
    public static void Refresh(string folder, FolderSelectionPageHost<TFolder, TInstance> host) { host.WriteIni(folder + "PCL.ini", "InstanceCache", ""); if (PathUtils.IsSame(folder, host.SelectedFolder())) host.RefreshInstances(true); }
    public static void Rename(object sender, FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return; TFolder folder = (TFolder)host.MenuRow(sender).Tag;
        try {
            string? name = host.Input(new SelectionPageInput { Title = "输入新名称", Default = folder.Name! }); if (string.IsNullOrWhiteSpace(name)) return;
            List<string> folders = new(host.ReadFolders().Split('|')); bool added = false;
            for (int i = 0; i < folders.Count; i++) { string current = folders[i]; if (Same(current, "")) continue; if (!PathUtils.IsSame(current.Split('>')[1], folder.Location)) continue; added = true; if (Same(current.Split('>')[0], name)) return; folders[i] = name + ">" + folder.Location; break; }
            if (!added) folders.Add(name + ">" + folder.Location); host.Hint("文件夹名称已更新为 " + name + " ！", SelectionPageHint.Green); host.WriteFolders(string.Join("|", folders)); host.RefreshFolders();
        } catch (Exception error) { Logger.Error(error, "重命名文件夹失败"); }
    }
    public static void ChangeFolder(FrameworkElement sender, bool mouse, Action cancel, FolderSelectionPageHost<TFolder, TInstance> host) {
        if (host.ModpackEnabled()) return; if (!mouse || !host.RowChecked(sender)) return;
        if (host.AnyLocked() || host.Downloading(true)) { host.Hint("请在其他任务完成后，再切换游戏文件夹！", SelectionPageHint.Red); cancel(); return; }
        host.SetSelectedFolder(((TFolder)sender.Tag).Location); host.RefreshFolders(); host.RefreshInstances(false);
    }
}
