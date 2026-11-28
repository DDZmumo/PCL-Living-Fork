// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;

namespace PCL
{
    internal static class LocalResourceUi
    {
        // Sort modes shared by the resource pages: 1/2 added time newest/oldest first, 3/4 last played newest/oldest first,
        // 5/6 file name A→Z / Z→A.
        private static string SortLabel(int mode) => mode switch
        {
            1 => "添加时间 · 倒序", 2 => "添加时间 · 正序", 3 => "最后游玩 · 倒序", 4 => "最后游玩 · 正序",
            5 => "文件名 · A→Z", 6 => "文件名 · Z→A", _ => "默认排序"
        };

        /// <summary>Opens the sort choices under the sort button, styled like the glass combo box's list.</summary>
        internal static void ShowSortMenu(Button button, int current, bool lastPlayed, System.Action<int> choose)
        {
            int newest = lastPlayed ? 3 : 1;
            string time = lastPlayed ? "最后游玩" : "添加时间";
            int[] modes = { newest, newest + 1, 5, 6 };
            string[] labels = { time + " · 最近在前", time + " · 最早在前", "文件名 · A → Z", "文件名 · Z → A" };
            GlassChoicePopup.Show(button, labels, System.Array.IndexOf(modes, current), index => choose(modes[index]));
        }

        internal static void SetSortButton(Button button, int mode)
        {
            bool newest = mode is 1 or 3 or 6;
            string text = SortLabel(mode);
            var glyph = new System.Windows.Shapes.Path {
                Width = 16, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, Data = Geometry.Parse(newest
                    ? "M3 15L0 12L1.4 10.6L2 11.2V1H4V11.2L4.6 10.6L6 12Z M8 2H16V4H8Z M8 7H14V9H8Z M8 12H12V14H8Z"
                    : "M3 1L0 4L1.4 5.4L2 4.8V15H4V4.8L4.6 5.4L6 4Z M8 2H12V4H8Z M8 7H14V9H8Z M8 12H16V14H8Z")
            };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "GlassPresentationText");
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(FrameworkElement.StyleProperty, "GlassPresentationTextStyle");
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, label } };
            button.Margin = new Thickness(8, 2, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(button, text);
        }
        internal const string PlayLogo = "M52.1,164.5c-1.4,0-3.1-0.5-4.2-1.3c-2.6-1.7-4-4.2-4-7V43.8c0-2.9,1.6-5.8,4.1-7c1.2-0.8,2.7-1.2,4.1-1.2c1.5,0,2.9,0.4,4.2,1.2L153.1,93c0,0,0.1,0,0.1,0.1c2.6,1.7,4,4.2,4,7c0,3-1.7,5.8-4.2,7.1l-96.8,56.2C55.1,164,53.5,164.5,52.1,164.5z M60.4,142.1l72.1-42.1L60.4,58.2V142.1z";
    }
}
