// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>仅原 MyLocalModItem 的还原点专用属性；实际字段和控件身份归原宿主。</summary>
public sealed class EnvironmentHistoryPagePointView {
    public FrameworkElement Root = null!, UpdateButton = null!, ResourceLogo = null!, PathLogo = null!;
    public Func<JObject?> Point = null!;
    public Action<string> SetTitle = null!, SetSubTitle = null!, SetPathLogo = null!;
    public Action<string?> SetDescription = null!;
    public Func<string?> Title = null!, SubTitle = null!, Description = null!;
    public Action<List<string>> SetTags = null!;
    public Func<string> ImageRoot = null!;
}

/// <summary>LF 还原点行的原显示、标签与图标，通用模组和资源行分支保持独立。</summary>
// LF MyLocalModItem.xaml.vb RestorePoint branches at
// f4aa0fae62c3ee8af24c81e61844c8fdd13c8983. No CE code reused.
public static class EnvironmentHistoryPagePointPresenter {
    public static void Bind(JObject value, Action clearResource, Action clearEntry, Action<JObject> setPoint, Action refresh) {
        clearResource();
        clearEntry();
        setPoint(value);
        refresh();
    }
    public static void Refresh(EnvironmentHistoryPagePointView view) {
        view.SetTitle(view.Point()!["label"]!.ToString());
        view.SetSubTitle("  |  " + ((DateTime)view.Point()!["created"]!).ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        view.SetDescription(view.Point()!["description"]?.ToString());
        view.SetTags((bool)view.Point()!["healthy"]! ? new List<string> { "正常运行" } : new List<string>());
        // Read the real getters after their setters; native controls may normalize text.
        view.Root.ToolTip = view.Title() + view.SubTitle() + "\r\n" + view.Description();
        view.UpdateButton.Visibility = Visibility.Collapsed;
        view.ResourceLogo.Visibility = Visibility.Collapsed;
        view.PathLogo.Visibility = Visibility.Visible;
        view.SetPathLogo(view.ImageRoot() + "Icons/NoIcon.png");
    }
}
