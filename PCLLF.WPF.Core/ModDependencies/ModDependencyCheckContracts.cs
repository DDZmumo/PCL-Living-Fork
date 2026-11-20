// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCLCS;

/// <summary>Only the existing LF custom controls and typed platform reads cross this boundary.</summary>
public sealed class ModDependencyCheckViewHost<TVersion, TIssue, TCard> where TCard : FrameworkElement {
    public Func<TVersion, string?> VersionDisplay = null!;
    public Func<TIssue, string> IssueKey = null!;
    public Func<string, Thickness, TCard> CreateCard = null!;
    public Action<TCard, UIElement> AddCardChild = null!;
    public Action<TCard, UIElement> CollapseDetails = null!;
    public Action<TCard> EnsureCaption = null!;
    public Func<UIElement, ScrollViewer> CreateScroll = null!;
    public Func<string, Thickness, FrameworkElement> CreateWarningHint = null!;
    public Func<ImageSource> DefaultIcon = null!;
}

public class ModDependencyImpactItemData {
    public string? Name, FileName;
    public ImageSource? Icon;
    public System.Collections.Generic.List<string> Reasons = new();
}
