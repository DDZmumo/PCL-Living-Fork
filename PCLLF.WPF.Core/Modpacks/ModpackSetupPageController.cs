// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;

namespace PCLCS;

// LF PageSetupModpack is LF-only; no CE/PCL setup-page replacement is used.
public sealed class ModpackSetupPageHost<TCheck> where TCheck : FrameworkElement {
    public Func<bool> Refreshing = null!, Enabled = null!;
    public Action<bool> SetRefreshing = null!, SetChecked = null!;
    public Action<int> AdjustAnimation = null!;
    public Func<int> Animation = null!;
    public Action TouchTitle = null!, RefreshSettings = null!, ScrollHome = null!;
    public TextBlock VersionText = null!;
    public Func<string> Brand = null!, Version = null!;
    public Action<TCheck, bool> Toggle = null!;
    public Action<string> HintWithoutLog = null!;
}
public sealed class ModpackSetupPageController<TCheck> where TCheck : FrameworkElement {
    private readonly ModpackSetupPageHost<TCheck> host;
    public ModpackSetupPageController(ModpackSetupPageHost<TCheck> host) { this.host = host; }
    public void Loaded() {
        host.SetRefreshing(true); host.AdjustAnimation(1);
        try { host.TouchTitle(); host.VersionText.Text = host.Brand() + " " + host.Version(); host.RefreshSettings(); host.SetChecked(host.Enabled()); host.ScrollHome(); }
        finally { host.AdjustAnimation(-1); host.SetRefreshing(false); }
    }
    public void ModeChanged(TCheck sender, bool user) => host.Toggle(sender, user);
    public void CacheChanged() { if (!host.Refreshing() && host.Animation() == 0) host.HintWithoutLog("缓存文件夹将在重启启动器后生效。"); }
}
