// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

/// <summary>Account presentation owns new views; provider controls retain their original state ownership.</summary>
public sealed class GlassAccountPanel : StackPanel, IDisposable {
    private readonly List<GlassPresentationSession> _sessions = new();
    public GlassAccountPanel(PageLaunchLeft source) {
        Margin = new Thickness(4); MinWidth = 260;
        foreach (FrameworkElement part in new FrameworkElement[] { source.PanTypeOne, source.PanType, source.PanLogin }) {
            var session = GlassPresentation.Create(part);
            _sessions.Add(session);
            session.View.Margin = new Thickness(0, 0, 0, 14);
            Children.Add(session.View);
        }
    }
    public void Refresh() { foreach (var session in _sessions) session.Refresh(); }
    public void Dispose() {
        foreach (var session in _sessions) session.Dispose();
        _sessions.Clear(); Children.Clear();
    }
}
