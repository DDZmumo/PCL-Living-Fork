// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF PageInstanceResources.Backups.vb; only the existing backup engine/widgets stay hosted.
public sealed partial class ResourcePageController<TInstance, TEntry, TOnline>
    where TInstance : class where TEntry : InstanceResourceEntryData where TOnline : class {
    public List<JObject> WorldBackupPoints = new();
    private readonly Dictionary<string, StackPanel> WorldHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IResourcePageBackupGroup<TInstance>> BackupGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool BackupSelectionChanging;
    private IResourcePageCard? OrphanBackupCard;
    private StackPanel? OrphanBackupPanel;
    private int BottomBarShownCount;
    private int worldRefreshRevision;
    private bool worldRefreshQueued;
    private bool worldRefreshAgain;
    private bool hasWorldSnapshot;
    private CancellationTokenSource? worldRefresh;

    private void CancelWorldRefresh() {
        worldRefreshRevision++;
        worldRefresh?.Cancel();
        worldRefresh = null;
        worldRefreshQueued = worldRefreshAgain = false;
    }

    public void RefreshChangedResources() {
        if (Kind != 0 || !hasWorldSnapshot || Owner is null || CurrentRoot is null || CurrentFolder is null) { Refresh(); return; }
        if (!ReferenceEquals(Owner, Host.CurrentInstance()) || !SamePath(CurrentRoot, System.IO.Path.Combine(Host.GameFolder(Owner), "saves"))) { Refresh(); return; }
        if (worldRefresh is not null) { worldRefreshAgain = true; return; }
        if (worldRefreshQueued) return;
        worldRefreshQueued = true;
        int revision = worldRefreshRevision;
        View.Root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => {
            if (revision != worldRefreshRevision) return;
            worldRefreshQueued = false;
            if (Busy || !View.Root.IsLoaded || Owner is null) return;
            TInstance target = Owner;
            string root = CurrentRoot!, folder = CurrentFolder!, game = Host.GameFolder(target);
            var request = new Request { Instance = target, Root = root, Folder = folder, GameFolder = game };
            CancellationTokenSource source = new();
            worldRefresh = source;
            Host.Background(() => {
                Result? result = null;
                Exception? failure = null;
                try { result = Read(request, source.Token, () => source.IsCancellationRequested); }
                catch (Exception error) { failure = error; }
                Host.Dispatch(() => {
                    bool current = ReferenceEquals(worldRefresh, source);
                    if (current) worldRefresh = null;
                    bool canceled = source.IsCancellationRequested;
                    source.Dispose();
                    if (!current || canceled || revision != worldRefreshRevision || !View.Root.IsLoaded ||
                        !ReferenceEquals(Owner, target) || !ReferenceEquals(Host.CurrentInstance(), target) ||
                        !SamePath(CurrentFolder, folder) || !SamePath(Host.GameFolder(target), game)) return;
                    if (failure is not null) Host.Warn(failure, "增量更新存档列表失败");
                    else Apply(result);
                    if (worldRefreshAgain) { worldRefreshAgain = false; RefreshChangedResources(); }
                });
            }, "Refresh Changed Worlds");
        }));
    }

    private IEnumerable<IResourcePageRow<TEntry, TOnline>> VisibleResourceRows() {
        // Preserve visual order from the actual panel. Interface dictionaries are
        // lookup adapters, not the source of visible order or selection order.
        Dictionary<FrameworkElement, IResourcePageRow<TEntry, TOnline>> known = Rows.Values.ToDictionary(row => row.Element);
        IEnumerable<FrameworkElement> elements = Kind == 0
            ? View.PanWorlds.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<FrameworkElement>())
            : View.PanList.Children.OfType<FrameworkElement>();
        foreach (FrameworkElement element in elements)
            if (known.TryGetValue(element, out IResourcePageRow<TEntry, TOnline>? row)) yield return row;
    }

    private List<string> SelectedBackupIds() => Kind != 0
        ? new List<string>()
        : BackupGroups.Values.SelectMany(group => group.SelectedIds).Distinct(StringComparer.Ordinal).ToList();

    private void ClearBackupSelection() {
        BackupSelectionChanging = true;
        try { foreach (IResourcePageBackupGroup<TInstance> group in BackupGroups.Values) group.ClearSelection(false); }
        finally { BackupSelectionChanging = false; }
    }

    private void BackupSelectionChanged() {
        if (Rendering || BackupSelectionChanging || Busy) return;
        Rendering = true;
        try {
            SelectedPaths.Clear();
            foreach (IResourcePageRow<TEntry, TOnline> row in Rows.Values) row.Checked = false;
        } finally { Rendering = false; }
        SelectResource(null);
    }

    private IResourcePageBackupGroup<TInstance> BackupGroup(string name) {
        if (!BackupGroups.TryGetValue(name, out IResourcePageBackupGroup<TInstance>? group)) {
            group = View.CreateBackupGroup(BackupSelectionChanged, () => View.CommitBackupSettings());
            group.Element.Margin = new Thickness(39, 5, 0, 10);
            BackupGroups.Add(name, group);
        }
        group.Bind(Owner!, name, WorldBackupPoints.Where(point => string.Equals(point["world"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)).ToList());
        return group;
    }

    private static void MoveInto(Panel host, FrameworkElement element) {
        if (ReferenceEquals(element.Parent, host)) return;
        if (element.Parent is Panel previous) previous.Children.Remove(element);
        host.Children.Add(element);
    }

    private void ResetWorldGroups() {
        ClearBackupSelection();
        View.PanWorlds.Children.Clear();
        WorldHosts.Clear();
        Rows.Clear();
        BackupGroups.Clear();
        OrphanBackupPanel = null;
        OrphanBackupCard = null;
    }

    private void RenderWorldGroups(List<TEntry> visible, string query) {
        List<UIElement> desired = new();
        HashSet<string> visibleGroups = new(StringComparer.OrdinalIgnoreCase);
        foreach (TEntry entry in visible) {
            if (!Rows.TryGetValue(entry.FullPath!, out IResourcePageRow<TEntry, TOnline>? row)) {
                row = CreateResourceRow();
                Rows.Add(entry.FullPath!, row);
            }
            if (!ReferenceEquals(row.Entry, entry)) row.Bind(entry, null);
            row.Checked = SelectedPaths.Contains(entry.FullPath!);
            if (!WorldHosts.TryGetValue(entry.FullPath!, out StackPanel? panel)) {
                panel = new StackPanel();
                WorldHosts.Add(entry.FullPath!, panel);
            }
            MoveInto(panel, row.Element);
            if (Owner is not null) {
                MoveInto(panel, BackupGroup(entry.Name!).Element);
                visibleGroups.Add(entry.Name!);
            }
            desired.Add(panel);
        }
        List<string> names = WorldBackupPoints.Select(point => point["world"]?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !Entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                (query == "" || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(name => name).ToList();
        if (names.Count > 0 && Owner is not null) {
            if (OrphanBackupCard is null) {
                OrphanBackupPanel = new StackPanel { Margin = new Thickness(15, 40, 15, 8) };
                OrphanBackupCard = View.CreateBackupCard(OrphanBackupPanel);
                OrphanBackupCard.Element.Margin = new Thickness(0, 10, 0, 10);
                // CreateBackupCard provides the original swapped MyCard and attaches the panel.
            }
            OrphanBackupCard.Title = "仅有备份的存档（" + names.Count + "）";
            // MainTextBlock's original getter initializes the not-yet-loaded card.
            OrphanBackupCard.InitializeHeader();
            foreach (IResourcePageBackupGroup<TInstance> old in BackupGroups.Values.Where(group =>
                    ReferenceEquals(group.Element.Parent, OrphanBackupPanel) && !names.Contains(group.FolderName, StringComparer.OrdinalIgnoreCase)).ToList())
                OrphanBackupPanel!.Children.Remove(old.Element);
            foreach (string worldName in names) {
                IResourcePageBackupGroup<TInstance> group = BackupGroup(worldName);
                group.PrefixTitle(worldName);
                MoveInto(OrphanBackupPanel!, group.Element);
                visibleGroups.Add(worldName);
            }
            desired.Add(OrphanBackupCard.Element);
            View.LabEmpty.Visibility = Visibility.Collapsed;
        }
        foreach (UIElement element in View.PanWorlds.Children.OfType<UIElement>().Where(item => !desired.Contains(item)).ToList())
            View.PanWorlds.Children.Remove(element);
        for (int index = 0; index < desired.Count; index++) {
            UIElement element = desired[index];
            if (View.PanWorlds.Children.IndexOf(element) == index) continue;
            View.PanWorlds.Children.Remove(element);
            View.PanWorlds.Children.Insert(index, element);
        }
        foreach (IResourcePageBackupGroup<TInstance> group in BackupGroups.Values)
            if (!visibleGroups.Contains(group.FolderName)) group.ClearSelection(false);
        foreach (string key in WorldHosts.Keys.Where(value => !Entries.Any(entry => SamePath(entry.FullPath!, value))).ToList()) {
            WorldHosts.Remove(key);
            Rows.Remove(key);
        }
    }

    private void RefreshBackupActions() {
        List<string> ids = SelectedBackupIds();
        bool isBackup = ids.Count > 0;
        View.BtnBackupAll.IsEnabled = !Busy && !Host.BackupBusy() && Owner is not null && Entries.Count > 0;
        View.BtnBackupRestore.Visibility = isBackup ? Visibility.Visible : Visibility.Collapsed;
        View.BtnBackupRestore.IsEnabled = !Busy && !Host.BackupBusy() && ids.Count == 1;
        View.BtnExport.Visibility = isBackup ? Visibility.Collapsed : Visibility.Visible;
        if (isBackup) {
            View.BtnUpdate.Visibility = Visibility.Collapsed;
            View.BtnDelete.IsEnabled = !Busy && !Host.BackupBusy();
        }
        int count = isBackup ? ids.Count : SelectedPaths.Count;
        View.LabSelect.Text = isBackup ? "已选择 " + count + " 份存档备份" : "已选择 " + count + " 个资源";
        View.CardList.Element.Margin = new Thickness(0, 0, 0, count > 0 ? 95 : 15);
        BottomBarShownCount = View.RefreshSelectionBar(count, BottomBarShownCount, View.Root.IsLoaded && Host.AnimationEnabled());
    }

    public void BackupAll() {
        if (Owner is not null && View.CommitBackupSettings()) Host.StartBackup(Owner, null, null);
    }

    public void RestoreWorldBackup() {
        List<string> ids = SelectedBackupIds();
        if (ids.Count == 1) Host.StartBackup(Owner!, null, ids.Single());
    }
}
