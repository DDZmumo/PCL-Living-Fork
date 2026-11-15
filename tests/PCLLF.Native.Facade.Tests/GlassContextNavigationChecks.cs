// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows;
using System.Windows.Controls;
using PCL;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

// Real Main controls, original BAML, original click pipeline and contextual projection.
internal static class GlassContextNavigationChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "glass-context-navigation");
        ModAnimation.AniControlEnabled++;
        try
        {
            Projection();
            RadioPreview();
            SelectedInstallClick();
        }
        finally
        {
            ModAnimation.AniControlEnabled--;
        }
        Console.WriteLine("PASS glass context navigation: actual visible rows/icons/selection; original Click/check/preview order, cancellation and selected install action; no pointer injection.");
    }

    private static IReadOnlyList<GlassSidebarItem> Build(MyPageLeft page) =>
        (IReadOnlyList<GlassSidebarItem>)Native.Static("PCL.GlassContextNavigation", "Build", page)!;

    private static void Projection()
    {
        var page = new MyPageLeft();
        var panel = new StackPanel();
        page.Children.Add(panel);
        var first = new MyListItem { Title = "概览", ToolTip = "当前实例的概览", Type = MyListItem.CheckType.RadioBox, Logo = "M0,0 H10 V10 H0 Z" };
        var second = new MyListItem { Title = "设置", Type = MyListItem.CheckType.RadioBox };
        var disabled = new MyListItem { Title = "不可用", Type = MyListItem.CheckType.RadioBox, IsEnabled = false };
        var hidden = new MyListItem { Title = "隐藏", Type = MyListItem.CheckType.RadioBox, Visibility = Visibility.Collapsed };
        var action = new MyListItem { Title = "文件夹管理", Type = MyListItem.CheckType.Clickable };
        panel.Children.Add(first); panel.Children.Add(second); panel.Children.Add(disabled); panel.Children.Add(hidden); panel.Children.Add(action);
        first.SetChecked(true, false, false);
        IReadOnlyList<GlassSidebarItem> items = Build(page);
        Native.Check(items.Select(item => item.Label).SequenceEqual(new[] { "概览", "设置", "不可用" }), "Context navigation order/visibility/deduplication differs from real source rows.");
        Native.Check(items[0].IsSelected && items[0].ToolTip == "当前实例的概览" && !string.IsNullOrWhiteSpace(items[0].IconData), "Context projection lost the actual selection, tooltip or logo geometry.");
        Native.Check(items[1].IconData is null && items[1].Image is null && !items[2].IsEnabled, "A missing logo was replaced with invented navigation data, or a disabled action was enabled.");
        var events = new List<string>();
        second.Click += (sender, args) => { Native.Check(ReferenceEquals(sender, second) && args is null, "Proxy click fabricated pointer input or changed its sender."); events.Add("click"); };
        second.Check += (_, args) => { Native.Check(args.RaiseByMouse, "Proxy activation lost the user-action flag."); events.Add("check"); };
        items[1].Invoked();
        Native.Check(!first.Checked && second.Checked && events.SequenceEqual(new[] { "click", "check" }), "Context proxy bypassed the actual Click/check pipeline or sibling radio selection.");
        events.Clear(); items[1].Invoked();
        Native.Check(events.SequenceEqual(new[] { "click" }), "An already selected row lost its Click action or emitted another check.");
        int unavailableClicks = 0; disabled.Click += (_, _) => unavailableClicks++;
        items[2].Invoked();
        Native.Check(unavailableClicks == 0 && !disabled.Checked, "A disabled original row was activated by its projection.");
        page.Width = 0;
        Native.Check(Build(page).Count == 0 && Build(new MyPageLeft()).Count == 0, "A blank/zero-width left page created a contextual rail.");
    }

    private static void RadioPreview()
    {
        var page = new MyPageLeft();
        var panel = new StackPanel(); page.Children.Add(panel);
        var first = new MyRadioButton { Text = "第一个", Logo = "M0,0 H10 V10 H0 Z" };
        var second = new MyRadioButton { Text = "第二个", Logo = "M0,0 L10,5 0,10 Z" };
        panel.Children.Add(first); panel.Children.Add(second); first.SetChecked(true, false, false);
        bool cancel = true;
        var events = new List<string>();
        second.PreviewClick += (_, args) => { Native.Check(args.RaiseByMouse, "Proxy radio preview lost its user-action flag."); events.Add("preview"); args.Handled = cancel; };
        second.Check += (_, user) => { Native.Check(user, "Proxy radio check lost its user-action flag."); events.Add("check"); };
        IReadOnlyList<GlassSidebarItem> items = Build(page);
        items[1].Invoked();
        Native.Check(first.Checked && !second.Checked && events.SequenceEqual(new[] { "preview" }), "Radio preview cancellation did not preserve the original selection.");
        cancel = false; events.Clear(); items[1].Invoked();
        Native.Check(!first.Checked && second.Checked && events.SequenceEqual(new[] { "preview", "check" }), "Radio proxy bypassed original preview/check ordering.");
    }

    private static void SelectedInstallClick()
    {
        var page = new PageDownloadLeft();
        object controller = Native.Property(typeof(PageDownloadLeft), "Service").GetValue(page)!;
        object host = Native.Get(controller, "host")!;
        int exits = 0;
        Native.Set(host, "ExitInstallSelection", (Action)(() => exits++));
        GlassSidebarItem install = Build(page).Single(item => item.Label == "原版游戏");
        Native.Check(install.IsSelected, "Actual install row should be selected by its original BAML.");
        install.Invoked(); install.Invoked();
        Native.Check(exits == 2, "Already selected install clicks no longer execute the real exit-install-selection handler.");
    }
}
