// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using MeloongCore;
using PCLLF.Wpf.Glass;
using Folder = PCL.ModMinecraft.McFolder;

namespace PCL;

internal sealed partial class GlassShellIntegration
{
    private GlassInstanceSelectionPage? _instanceSelection;
    private readonly Dictionary<FormMain.PageSubType, GlassLocalResourcesPage> _localResources = new();
    private GlassLocalResourcesPage? _activeLocalResources;

    private static bool IsLocalResourceRoute(FormMain.PageType route, FormMain.PageSubType sub) =>
        route == FormMain.PageType.InstanceSetup && sub is >= FormMain.PageSubType.InstanceSaves and <= FormMain.PageSubType.InstanceSchematics;

    internal bool RefreshLocalResources()
    {
        if (_activeLocalResources is not { } resources || _owner.PageCurrent.Page != FormMain.PageType.InstanceSetup ||
            _directPage?.SubPage != _owner.PageCurrentSub) return false;
        resources.Refresh();
        return true;
    }

    private void OpenLocalResources(FormMain.PageSubType sub)
    {
        if (!_localResources.TryGetValue(sub, out var page))
        {
            page = new GlassLocalResourcesPage(_owner, (InstanceResourceKind)((int)sub - (int)FormMain.PageSubType.InstanceSaves));
            page.Scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) _owner.RefreshBackToTop(); };
            _localResources.Add(sub, page);
        }
        _activeLocalResources = page;
        page.Open(_owner.GlassPreviewState is null);
        View.SetPageContent(page);
    }
    internal bool ShowHiddenInstances
    {
        get => _instanceSelection?.ShowHidden == true;
        set { if (_instanceSelection is not null) _instanceSelection.ShowHidden = value; }
    }

    private void OpenInstanceSelection()
    {
        if (_instanceSelection is null)
        {
            _instanceSelection = new GlassInstanceSelectionPage(_owner);
            _instanceSelection.Scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) _owner.RefreshBackToTop(); };
        }
        _instanceSelection.Open(_owner.GlassPreviewState is null);
        View.SetPageContent(_instanceSelection);
    }

    private static IReadOnlyList<GlassSidebarItem> GameFolderSidebar()
    {
        bool packMode = ModModpackMode.Enabled;
        IReadOnlyList<Folder> folders = LauncherGameFolders.Read();
        var rows = new List<GlassSidebarItem> { new() { Label = "文件夹列表", IsHeader = true } };
        foreach (Folder folder in folders)
        {
            Action<FrameworkElement>? menu = packMode ? null : target => ShowFolderMenu(folder, target, folders.Count == 1);
            rows.Add(new GlassSidebarItem {
                Label = folder.Name,
                ToolTip = folder.Location,
                IconData = ModBase.Logo.IconButtonOpen,
                IsSelected = packMode || PathUtils.IsSame(folder.Location, ModMinecraft.McFolderSelected),
                Invoked = () => LauncherGameFolders.Change(folder),
                ContextRequested = menu is null ? null : target => ShowFolderMenu(folder, target, folders.Count == 1, atPointer: true),
                Actions = menu is null ? Array.Empty<GlassSidebarAction>() : [
                    new GlassSidebarAction { ToolTip = "文件夹操作", IconData = ModBase.Logo.IconButtonSetup, TargetInvoked = menu }
                ]
            });
        }
        if (packMode) return rows;
        rows.Add(new GlassSidebarItem { Label = "添加或导入", IsHeader = true });
        const string plus = "M11,3 H13 V11 H21 V13 H13 V21 H11 V13 H3 V11 H11 Z";
        if (!Directory.Exists(Path.Combine(Paths.Base, ".minecraft")))
            rows.Add(Command("新建游戏文件夹", plus, LauncherGameFolders.Create));
        rows.Add(Command("添加已有文件夹", ModBase.Logo.IconButtonOpen, LauncherGameFolders.Add));
        rows.Add(Command("安装整合包", ResourceBrowserCatalog.Find(FormMain.PageSubType.DownloadPack)!.Icon, ModModpack.ModpackInstall));
        return rows;

        static GlassSidebarItem Command(string title, string icon, Action run) =>
            new() { Label = title, IconData = icon, Invoked = run, IsCommand = true };
    }

    private static void ShowFolderMenu(Folder folder, FrameworkElement target, bool onlyFolder, bool atPointer = false)
    {
        var options = new List<GlassChoicePopup.Option>();
        if (folder.Type == Folder.Types.RenamedVanilla)
            options.Add(new("复原名称", () => LauncherGameFolders.Remove(folder), ModBase.Logo.IconButtonReset));
        options.Add(new("重命名", () => LauncherGameFolders.Rename(folder), ModBase.Logo.IconButtonEdit));
        options.Add(new("打开", () => LauncherGameFolders.Open(folder), ModBase.Logo.IconButtonOpen));
        options.Add(new("刷新", () => LauncherGameFolders.Refresh(folder), ModBase.Logo.IconButtonRefresh));
        if (folder.Type == Folder.Types.Custom)
            options.Add(new("移出列表", () => LauncherGameFolders.Remove(folder), GlassSelectionDock.DisableIcon));
        bool clear = onlyFolder && folder.Type != Folder.Types.Custom && PathUtils.IsSame(folder.Location, Path.Combine(Paths.Base, ".minecraft"));
        options.Add(new(clear ? "清空" : "删除", () => LauncherGameFolders.Delete(folder), ModBase.Logo.IconButtonDelete));
        GlassChoicePopup.Show(target, options, placement: atPointer
            ? System.Windows.Controls.Primitives.PlacementMode.MousePoint : System.Windows.Controls.Primitives.PlacementMode.Bottom);
    }
}
