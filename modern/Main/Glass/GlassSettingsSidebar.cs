// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.Collections.Generic;
using PCLLF.Wpf.Glass;
using static PCL.FormMain.PageSubType;

namespace PCL;

internal static class GlassSettingsSidebar {
    internal static IReadOnlyList<GlassSidebarItem> Build(FormMain window) {
        bool pack = ModModpackMode.Enabled;
        var choices = new List<GlassSidebarItem> {
            Item("启动", SetupLaunch, GlassSidebarIcons.Launch, reset: !pack),
            Item("Java", SetupJava, GlassJavaGraphics.Icon),
            Item(pack ? "启动器" : "其他", SetupSystem, GlassSidebarIcons.System, reset: !pack),
            Item("更新", SetupUpdate, ModBase.Logo.IconButtonRefresh),
            Item("游戏设置同步", SetupPersonal, ModBase.Logo.IconButtonPersonalSettings)
        };
        if (!pack)
            choices.Insert(2, Item("个性化", SetupUI, GlassSidebarIcons.Personalization, reset: true));
        return choices;

        GlassSidebarItem Item(string text, FormMain.PageSubType route, string icon, bool reset = false) => new() {
            Label = text,
            IconData = icon,
            IsSelected = window.GlassSettingsSubPage == route,
            Invoked = () => window.PageChange(FormMain.PageType.Setup, route),
            Actions = reset ? [new GlassSidebarAction {
                ToolTip = "初始化本页设置", IconData = ModBase.Logo.IconButtonRefresh,
                Invoked = () => Reset(window, route, text)
            }] : Array.Empty<GlassSidebarAction>()
        };
    }

    private static void Reset(FormMain window, FormMain.PageSubType route, string label) {
        if (route == SetupUI) {
            window.PageChange(FormMain.PageType.Setup, SetupUI);
            window.ResetGlassPersonalization();
            return;
        }
        if (ModMain.MyMsgBox($"是否要初始化 {label} 页面的所有设置？该操作不可撤销。",
            "初始化确认", "初始化", "取消", IsWarn: true) != 1) return;
        if (route == SetupLaunch) window.ResetGlassLaunchOptions();
        else (ModMain.FrmSetupSystem ??= new PageSetupSystem()).Reset();
        window.PageChange(FormMain.PageType.Setup, route);
    }
}
