// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCL;

/// <summary>A rounded sample of the launcher body for a surface drawn above it.</summary>
internal sealed class GlassSceneFrost : Canvas
{
    private const double BlurPadding = 36;
    private readonly Border _slice = new() {
        Effect = new BlurEffect { Radius = 24, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        CacheMode = new BitmapCache { RenderAtScale = .5, EnableClearType = false }
    };
    private readonly VisualBrush _body = new() { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute,
        AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
    private readonly RectangleGeometry _clip = new();

    internal GlassSceneFrost()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Clip = _clip;
        _slice.Background = _body;
        Children.Add(_slice);
    }

    // The surface shares this canvas's bounds. The frame and scene must be in a separate visual subtree.
    internal void UpdateBackdrop(FrameworkElement surface, Border frame, FrameworkElement scene, double radius)
    {
        if (!surface.IsVisible || surface.Opacity <= .01 || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
        {
            ClearBackdrop();
            return;
        }
        Rect bounds = new(surface.RenderSize);
        Rect body = frame.TransformToVisual(surface).TransformBounds(new Rect(frame.RenderSize));
        Rect crop = bounds;
        crop.Inflate(BlurPadding, BlurPadding);
        crop.Intersect(body);
        if (crop.IsEmpty || crop.Width <= 0 || crop.Height <= 0)
        {
            ClearBackdrop();
            return;
        }

        if (_clip.Rect != bounds) _clip.Rect = bounds;
        if (_clip.RadiusX != radius) _clip.RadiusX = _clip.RadiusY = radius;
        if (GetLeft(_slice) != crop.Left) SetLeft(_slice, crop.Left);
        if (GetTop(_slice) != crop.Top) SetTop(_slice, crop.Top);
        if (_slice.Width != crop.Width) _slice.Width = crop.Width;
        if (_slice.Height != crop.Height) _slice.Height = crop.Height;
        bool left = Math.Abs(crop.Left - body.Left) < .01, top = Math.Abs(crop.Top - body.Top) < .01;
        bool right = Math.Abs(crop.Right - body.Right) < .01, bottom = Math.Abs(crop.Bottom - body.Bottom) < .01;
        CornerRadius corners = frame.CornerRadius;
        var sliceCorners = new CornerRadius(left && top ? corners.TopLeft : 0, right && top ? corners.TopRight : 0,
            right && bottom ? corners.BottomRight : 0, left && bottom ? corners.BottomLeft : 0);
        if (_slice.CornerRadius != sliceCorners) _slice.CornerRadius = sliceCorners;
        // The absolute viewbox is in the source visual's parent space, including its layout offset.
        Visual brushSpace = VisualTreeHelper.GetParent(scene) as Visual ?? scene;
        Rect viewbox = surface.TransformToVisual(brushSpace).TransformBounds(crop);
        if (_body.Viewbox != viewbox) _body.Viewbox = viewbox;
        if (!ReferenceEquals(_body.Visual, scene)) _body.Visual = scene;
        Visibility = Visibility.Visible;
    }

    internal void ClearBackdrop()
    {
        if (_body.Visual is not null) _body.Visual = null;
        Visibility = Visibility.Collapsed;
    }
}
