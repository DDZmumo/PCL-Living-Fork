// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using PCLLF.Wpf.Glass;

namespace PCL;

public partial class FormMain {
    private GlassShellIntegration glassShell;
    internal static bool GlassShellPreferred => Settings.Get<int>("UiShellStyle") == 1;
    internal bool GlassShellActive => glassShell is not null;
    internal int GlassPageCode => (int)PageCurrent.Page;
    internal string GlassPageTitle => PageNameGet(PageCurrent);
    internal Func<GlassLauncherViewState> GlassPreviewState { get; set; }
    internal GlassLauncherView GlassView => glassShell?.View;
    internal System.Windows.FrameworkElement GlassResizer(int index) => (System.Windows.FrameworkElement)ResizerPart(index);
    internal static void ApplyConfiguredOpacity(object value) {
        double opacity = Microsoft.VisualBasic.CompilerServices.Conversions.ToInteger(value) / 1000d + 0.4d;
        if (ModMain.FrmMain is not FormMain window) return;
        if (window.glassShell is not null) window.glassShell.SetMaterialDensity(opacity);
        else window.Opacity = opacity;
    }

    private void PrepareGlassShell() {
        if (!GlassShellPreferred) return;
        // Alpha follows the separated rounded surfaces and their gap; panel bases are painted by the launcher.
        AllowsTransparency = true;
        glassShell = new GlassShellIntegration(this);
    }
    private void AttachGlassShell() => glassShell?.Attach();
    internal void RefreshGlassShell() => glassShell?.Refresh(true);
}
