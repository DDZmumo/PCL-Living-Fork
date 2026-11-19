// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PCL;

/// <summary>Current launcher-wallpaper state shown in Settings → Personalization.</summary>
internal readonly record struct GlassWallpaperStatus(string Detail, bool CanClear);

/// <summary>
/// The shell-only wallpaper row placed at the top of the projected background card. It has no legacy control:
/// choosing and clearing are delegated to the shell, which owns the setting and the wallpaper cache.
/// </summary>
internal sealed class GlassWallpaperSettingRow : Grid {
    private readonly Func<GlassWallpaperStatus> _readStatus;
    private readonly TextBlock _detail;
    private readonly Button _clear;

    internal GlassWallpaperSettingRow(Action choose, Action clear, Func<GlassWallpaperStatus> readStatus) {
        ArgumentNullException.ThrowIfNull(choose);
        ArgumentNullException.ThrowIfNull(clear);
        ArgumentNullException.ThrowIfNull(readStatus);
        _readStatus = readStatus;
        Name = "LauncherWallpaperSetting";
        Margin = new Thickness(0, 0, 0, 12);
        MinHeight = 44;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var title = new TextBlock { Text = "启动器壁纸", FontSize = 14, TextWrapping = TextWrapping.NoWrap };
        title.SetResourceReference(StyleProperty, "GlassPresentationTextStyle");
        _detail = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        _detail.SetResourceReference(StyleProperty, "GlassPresentationTextStyle");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        text.Children.Add(title);
        text.Children.Add(_detail);
        Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(actions, 1);
        _clear = ActionButton("清除", "清除启动器壁纸", clear);
        _clear.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(_clear);
        actions.Children.Add(ActionButton("选择文件…", "选择启动器壁纸图片或视频", choose));
        Children.Add(actions);
        Refresh();
    }

    /// <summary>Re-reads the shell state; called whenever the owning card is refreshed.</summary>
    internal GlassWallpaperSettingRow Refresh() {
        GlassWallpaperStatus status = _readStatus();
        _detail.Text = status.Detail;
        _detail.ToolTip = status.Detail;
        _clear.IsEnabled = status.CanClear;
        return this;
    }

    private Button ActionButton(string text, string automationName, Action invoke) {
        var button = new Button { Content = text, MinWidth = 72 };
        button.SetResourceReference(StyleProperty, "GlassPresentationButton");
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => { invoke(); Refresh(); };
        return button;
    }
}
