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
        using var view = new PCLLF.Wpf.Glass.GlassLauncherView();
        var watermark = (StackPanel)view.FindName("DevelopmentMark");
        Native.Check(ReferenceEquals(watermark.Parent, view.FindName("BodyBackdropLayer")) && !watermark.IsHitTestVisible,
            "The development mark must stay on the noninteractive background.");
        if (string.Equals(ModBase.VersionBaseName, "dev_edition", StringComparison.Ordinal))
        {
            var text = watermark.Children.OfType<TextBlock>().Select(label => label.Text).ToArray();
            Native.Check(watermark.Visibility == Visibility.Visible && text.Length == 3 && text[0] == "DevEdition" &&
                text[1] == "基线版本 " + ModBase.ReleaseBaselineVersion &&
                text[2].StartsWith("构建于 ", StringComparison.Ordinal) &&
                DateTime.TryParseExact(text[2][4..], "yyyy.MM.dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _), "The development mark lost its baseline or dot-separated build time.");
        }
        else Native.Check(watermark.Visibility == Visibility.Collapsed, "The development mark appeared in an official build.");
        int activations = 0;
        view.SetSidebar(new[] {
            new PCLLF.Wpf.Glass.GlassSidebarItem { Label = "版本配置", IsHeader = true },
            new PCLLF.Wpf.Glass.GlassSidebarItem {
                Label = "设置", IconData = PCL.ModBase.Logo.IconButtonSetup, IsSelected = true, Invoked = () => activations++
            }
        });
        var panel = (System.Windows.Controls.StackPanel)view.FindName("SidebarPanel");
        var button = panel.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<Button>())
            .Single(action => System.Windows.Automation.AutomationProperties.GetName(action) == "设置");
        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, button));
        Native.Check(activations == 1, "Native contextual navigation did not invoke one route.");
        environment.AssertNoForbiddenIo();
    }
}
