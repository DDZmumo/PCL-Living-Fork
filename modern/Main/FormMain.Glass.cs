// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PCLLF.Wpf.Glass;

namespace PCL;

public partial class FormMain {
    /// <summary>Builds a page's left panel ahead of its first visit (startup preload while the splash shows).</summary>
    internal static void PrewarmPage(PageType page) {
        try { EnsureLeftPage((int)page); }
        catch (Exception error) { Logger.Warn(error, "预先构建页面失败"); }
    }

    private GlassShellIntegration glassShell;
    private bool _glassResizing, _saveGlassResize;
    internal static bool GlassShellPreferred => Settings.Get<int>("UiShellStyle") == 1;
    internal bool GlassShellActive => glassShell is not null;
    /// <summary>The glass account page is open over the launch page.</summary>
    internal bool GlassAccountPageOpen => glassShell?.View.IsAccountWorkspaceOpen == true;
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

    /// <summary>Detail pages keep the sidebar of the page they were opened from.</summary>
    internal static bool IsGlassDetailPage(PageType page) => page is PageType.ResourceDetail or PageType.HelpDetail;

    /// <summary>
    /// While a detail page is shown, the most recent stacked page that owns a contextual sidebar
    /// (for example 资源中心 → 模组 → 资源详情 keeps 资源中心). Null on ordinary pages.
    /// </summary>
    internal PageType? GlassContextOrigin {
        get {
            if (!IsGlassDetailPage(PageCurrent.Page)) return null;
            foreach (PageStackData stacked in PageStack)
                if (!IsGlassDetailPage(stacked.Page) && GlassContextLeft(stacked.Page) is not null) return stacked.Page;
            return null;
        }
    }

    internal static MyPageLeft GlassContextLeft(PageType page) => page switch {
        PageType.Download => ModMain.FrmDownloadLeft,
        PageType.Favorites => ModMain.FrmFavoritesLeft,
        PageType.Setup => ModMain.FrmSetupLeft,
        PageType.Other => ModMain.FrmOtherLeft,
        PageType.InstanceSetup => ModMain.FrmInstanceLeft,
        _ => null,
    };

    private static readonly ConditionalWeakTable<MyPageLeft, Dictionary<object, PageSubType>> GlassSubPages = new();

    /// <summary>The sub-page whose navigation row is <paramref name="row"/>, used to return from a detail page.</summary>
    internal static PageSubType? GlassContextSubPage(PageType page, object row) {
        if (GlassContextLeft(page) is not { } left) return null;
        var map = GlassSubPages.GetValue(left, _ => BuildGlassSubPages(page));
        return map.TryGetValue(row, out PageSubType sub) ? sub : null;
    }

    private static Dictionary<object, PageSubType> BuildGlassSubPages(PageType page)
    {
        var map = new Dictionary<object, PageSubType>(ReferenceEqualityComparer.Instance);
        foreach (PageSubType sub in Enum.GetValues<PageSubType>().Distinct()) {
            object item;
            try {
                item = page switch {
                    PageType.Download => ModMain.FrmDownloadLeft?.NavigationItem(sub),
                    PageType.Favorites => ModMain.FrmFavoritesLeft?.NavigationItem(sub),
                    PageType.InstanceSetup => ModMain.FrmInstanceLeft?.NavigationItem(sub),
                    PageType.Setup => ItemAt(ModMain.FrmSetupLeft?.PanItem, sub),
                    PageType.Other => ItemAt(ModMain.FrmOtherLeft?.PanItem, sub),
                    _ => null,
                };
            } catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidOperationException or KeyNotFoundException) {
                continue; // That page does not define this sub-page.
            }
            if (item is not null) map.TryAdd(item, sub);
        }
        return map;

        static object ItemAt(System.Windows.Controls.Panel panel, PageSubType sub)
            => panel is not null && (int)sub >= 0 && (int)sub < panel.Children.Count ? panel.Children[(int)sub] : null;
    }

    private void PrepareGlassShell() {
        if (!GlassShellPreferred) return;
        // Alpha follows the separated rounded surfaces and their gap; panel bases are painted by the launcher.
        AllowsTransparency = true;
        glassShell = new GlassShellIntegration(this);
    }
    private void AttachGlassShell() => glassShell?.Attach();
    internal void RefreshGlassShell() => glassShell?.Refresh(true);
    internal bool TryRefreshGlassTheme(bool dark, Action apply) => glassShell?.TryRefreshTheme(dark, apply) == true;

    internal void SetGlassResizing(bool resizing) {
        if (_glassResizing == resizing) return;
        _glassResizing = resizing;
        if (resizing) {
            _saveGlassResize = IsSizeSaveable;
            IsSizeSaveable = false;
        } else {
            IsSizeSaveable = _saveGlassResize;
            if (_saveGlassResize) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => {
                if (IsLoaded && !_glassResizing) FormMain_SizeChanged();
            }));
        }
        glassShell?.SetWindowResizing(resizing);
    }
}
