// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Windows;
using System.Windows.Media;

namespace PCL;

/// <summary>Which launcher edition a release belongs to, and the WPF edition's mark.</summary>
internal static class LauncherEdition
{
    private static Geometry? _mark;
    private static bool _loaded;

    /// <summary>The WPF edition: development builds and releases from 1.6.0 on (earlier ones are the classic edition).</summary>
    internal static bool IsWpf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        string text = version.Trim();
        if (text.Equals("dev_edition", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
        int dash = text.IndexOf('-');
        if (dash >= 0) text = text.Substring(0, dash);
        return Version.TryParse(text.Split('.').Length == 1 ? text + ".0" : text, out Version? parsed) && parsed >= new Version(1, 6);
    }

    /// <summary>The WPF edition's mark (from PCLLF.Logo.xaml), to be filled with the theme's text colour.</summary>
    internal static Geometry? WpfMark
    {
        get
        {
            if (_loaded) return _mark;
            _loaded = true;
            try
            {
                var drawing = (DrawingImage)Application.LoadComponent(new Uri("/LivingPCL;component/Images/Branding/PCLLF.Logo.xaml", UriKind.Relative));
                var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
                // Keep the full 1254 square (the transparent frame) so the mark keeps its own margins when stretched.
                foreach (GeometryDrawing part in ((DrawingGroup)drawing.Drawing).Children.OfType<GeometryDrawing>())
                    group.Children.Add(part.Brush is SolidColorBrush { Color.A: 0 } ? Frame(part.Geometry.Bounds) : part.Geometry.Clone());
                group.Freeze();
                _mark = group;
            }
            catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or NotSupportedException or InvalidCastException) { _mark = null; }
            return _mark;
        }
    }

    // A frame that sets the bounds without painting: two zero-area points at opposite corners.
    private static Geometry Frame(Rect bounds) => new GeometryGroup { Children = {
        new LineGeometry(bounds.TopLeft, bounds.TopLeft), new LineGeometry(bounds.BottomRight, bounds.BottomRight) } };
}
