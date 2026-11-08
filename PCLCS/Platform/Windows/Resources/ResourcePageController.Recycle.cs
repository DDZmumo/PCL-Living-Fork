// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

// LF PageInstanceResources.Recycle.vb; original widgets and services are typed adapters.
public sealed partial class ResourcePageController<TInstance, TEntry, TOnline>
    where TInstance : class where TEntry : InstanceResourceEntryData where TOnline : class {
    public bool IsRecycleMode;
    private bool toolbarLayoutActive;

    public void SetRecycleMode(bool value) {
        if (Busy || CurrentRoot is null) return;
        Host.CancelLoader();
        CancelOnline();
        ClearSelection();
        IsRecycleMode = value;
        Filter = 0;
        Entries.Clear();
        Rows.Clear();
        View.PanList.Children.Clear();
        foreach (IResourcePageThumbnail<TEntry> tile in ScreenshotRows.Values) tile.ReleaseThumbnail();
        ScreenshotRows.Clear();
        View.PanScreenshots.Children.Clear();
        WorldBackupPoints.Clear();
        ResetWorldGroups();
        Online.Clear();
        View.SetSearch("");
        CurrentFolder = CurrentRoot;
        SetStatus(null);
        ApplyRecycleMode();
        View.ScrollHome();
        Refresh();
    }

    private void ApplyRecycleMode() {
        View.BtnRecycleBack.Visibility = IsRecycleMode ? Visibility.Visible : Visibility.Collapsed;
        View.BtnRecycleBack.IsEnabled = !Busy && CurrentRoot is not null;
        View.BtnRestore.Visibility = IsRecycleMode ? Visibility.Collapsed : Visibility.Visible;
        View.BtnRestore.ToolTip = "查看删除的资源，可还原或彻底删除";
        View.BtnOpenFolder.Text = IsRecycleMode ? "打开回收站文件夹" : "打开文件夹";
        View.BtnOpenFolder.ToolTip = IsRecycleMode ? RecycleRoot : CurrentFolder;
        View.BtnOpenFolder.MinWidth = IsRecycleMode ? 150 : 110;
        View.BtnImportFile.Text = IsRecycleMode ? "还原所选" : "从文件安装";
        View.BtnImportFile.ToolTip = IsRecycleMode ? "还原到删除前的位置，同名资源不覆盖" : "选择本地资源文件安装，也可拖入列表";
        View.BtnBackupAll.Text = IsRecycleMode ? "清空回收站" : "立即备份";
        View.BtnExport.Text = IsRecycleMode ? "还原" : "导出";
        View.BtnExport.Logo = IsRecycleMode ? View.RestoreLogo : View.SaveLogo;
        View.BtnDelete.Text = IsRecycleMode ? "彻底删除" : "删除";
        View.CardList.Title = IsRecycleMode ? "回收站" : " ";
        if (View.SortHeader is not null) {
            View.SortHeader.Margin = new Thickness(15, IsRecycleMode ? 40 : 13, 15, 0);
            View.PanFilter.Margin = new Thickness(0);
            View.PanResourceList.Margin = new Thickness(20, 7, 18, 22);
        } else {
            View.PanFilter.Margin = new Thickness(15, IsRecycleMode ? 40 : 13, 0, 0);
            View.PanResourceList.Margin = new Thickness(20, IsRecycleMode ? 75 : 48, 18, 22);
        }
        if (View.SortControl is not null) {
            View.SortControl.IsEnabled = !IsRecycleMode;
            View.SortControl.ToolTip = IsRecycleMode ? "回收站按删除记录顺序显示；删除时间不作为添加时间或最后游玩时间。" :
                Kind == 0 ? "点击切换最后游玩时间的正序／倒序；倒序为最近游玩在前。时间取 level.dat 的 LastPlayed，未知时间排在最后。" :
                "点击切换添加时间的正序／倒序；倒序为最近添加在前。时间取本地创建时间，复制、解压和迁移可能改变它。";
        }
        if (!IsRecycleMode) {
            RefreshResourceToolbar();
            return;
        }
        View.CardList.Title = "回收站";
        View.SetSearchHint("搜索已删除的资源");
        View.BackupSettings.Visibility = Visibility.Collapsed;
        View.LabBackupStatus.Visibility = Visibility.Collapsed;
        View.BtnBackupAll.Visibility = Visibility.Visible;
        View.BtnBackupAll.IsEnabled = !Busy && Entries.Count > 0;
        View.BtnImportFile.IsEnabled = !Busy && SelectedPaths.Count > 0;
        View.PanManageButtons.ColumnDefinitions[4].SharedSizeGroup = "Button";
        View.BtnDownload.Visibility = Visibility.Collapsed;
        View.PanManageButtons.ColumnDefinitions[2].SharedSizeGroup = null;
        View.BtnUpdate.Visibility = Visibility.Collapsed;
        View.BtnUp.Visibility = Visibility.Collapsed;
        View.BtnBackupRestore.Visibility = Visibility.Collapsed;
        View.LabEmpty.Text = View.GetSearch() == "" ? "回收站为空。" : "没有找到匹配的删除记录。";
        View.FilterUpdates.Visibility = Visibility.Collapsed;
        RefreshResourceToolbar();
    }

    public void RefreshResourceToolbar() {
        Grid grid = View.PanManageButtons;
        if (toolbarLayoutActive || grid.ActualWidth <= 0) return;
        toolbarLayoutActive = true;
        try {
            // These are exactly the six original MyButton children, in XAML order.
            List<FrameworkElement> buttons = new[] { View.BtnOpenFolder.Element, View.BtnImportFile.Element,
                View.BtnDownload.Element, View.BtnSelectAll.Element, View.BtnBackupAll.Element, View.BtnRestore.Element }
                .Where(button => button.Visibility == Visibility.Visible).ToList();
            int columns = 1;
            for (int candidate = Math.Min(buttons.Count, grid.ColumnDefinitions.Count); candidate >= 1; candidate--) {
                double[] widths = new double[candidate];
                for (int index = 0; index < buttons.Count; index++) {
                    FrameworkElement button = buttons[index];
                    widths[index % candidate] = Math.Max(widths[index % candidate], button.MinWidth + button.Margin.Left + button.Margin.Right);
                }
                if (widths.Sum() <= grid.ActualWidth) { columns = candidate; break; }
            }
            for (int index = 0; index < grid.ColumnDefinitions.Count; index++) {
                grid.ColumnDefinitions[index].SharedSizeGroup = null;
                grid.ColumnDefinitions[index].Width = index < columns ? GridLength.Auto : new GridLength(0);
            }
            grid.RowDefinitions.Clear();
            int rowCount = (int)Math.Ceiling(buttons.Count / (double)columns);
            for (int index = 0; index < rowCount; index++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) });
            for (int index = 0; index < buttons.Count; index++) {
                Grid.SetColumn(buttons[index], index % columns);
                Grid.SetRow(buttons[index], index / columns);
                buttons[index].Height = 35;
                buttons[index].VerticalAlignment = VerticalAlignment.Top;
            }
            grid.Height = Math.Max(35, rowCount * 45 - 10);
        } finally { toolbarLayoutActive = false; }
    }

    public void ReturnToResources() => SetRecycleMode(false);

    public void RestoreRecycleSelection(IEnumerable<TEntry>? items = null) {
        if (!CanChange()) return;
        string[] ids = (items ?? Selection()).Select(item => item.RecycleId!).ToArray();
        if (ids.Length == 0) return;
        string root = CurrentRoot!, recycle = RecycleRoot!;
        RunOperation("还原资源", token => Host.RestoreRecycle(root, recycle, ids, token));
    }

    private void PurgeRecycleSelection(IEnumerable<TEntry> items) {
        if (!CanChange()) return;
        List<TEntry> selectedItems = items.ToList();
        string root = CurrentRoot!, recycle = RecycleRoot!;
        if (selectedItems.Count == 0) return;
        if (Host.Message("将永久删除选中的 " + selectedItems.Count + " 个资源及其所有内容。此操作无法从回收站还原。",
                "彻底删除资源", "彻底删除", "取消", true) != 1) return;
        string[] ids = selectedItems.Select(item => item.RecycleId!).ToArray();
        RunOperation("彻底删除资源", token => Host.PurgeRecycle(root, recycle, ids, token));
    }
}
