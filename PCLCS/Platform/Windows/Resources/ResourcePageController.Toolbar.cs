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

public sealed partial class ResourcePageController<TInstance, TEntry, TOnline> {
    private bool toolbarLayoutActive;
    private void ApplyResourceLayout() {
        View.CardList.Title = " ";
        if (View.SortHeader is not null) {
            View.SortHeader.Margin = new Thickness(15, 13, 15, 0);
            View.PanFilter.Margin = new Thickness(0);
            View.PanResourceList.Margin = new Thickness(20, 7, 18, 22);
        } else {
            View.PanFilter.Margin = new Thickness(15, 13, 0, 0);
            View.PanResourceList.Margin = new Thickness(20, 48, 18, 22);
        }
        if (View.SortControl is not null) {
            View.SortControl.IsEnabled = true;
            View.SortControl.ToolTip = Kind == 0
                ? "点击切换最后游玩时间的正序／倒序；倒序为最近游玩在前。时间取 level.dat 的 LastPlayed，未知时间排在最后。"
                : "点击切换添加时间的正序／倒序；倒序为最近添加在前。时间取本地创建时间，复制、解压和迁移可能改变它。";
        }
        RefreshResourceToolbar();
    }

    public void RefreshResourceToolbar() {
        Grid grid = View.PanManageButtons;
        if (toolbarLayoutActive || grid.ActualWidth <= 0) return;
        toolbarLayoutActive = true;
        try {
            // These are the resource toolbar MyButton children, in XAML order.
            List<FrameworkElement> buttons = new[] { View.BtnOpenFolder.Element, View.BtnImportFile.Element,
                View.BtnDownload.Element, View.BtnSelectAll.Element, View.BtnBackupAll.Element }
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

}
