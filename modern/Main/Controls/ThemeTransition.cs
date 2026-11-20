// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCL;

// Retain only the current frame while the actual controls adopt the new palette.
// Repeated toggles capture the composited frame, so an interrupted fade stays continuous.
internal sealed class ThemeTransition {
    private static readonly ConditionalWeakTable<Grid, ThemeTransition> instances = new();
    private readonly Grid root;
    private Image overlay;
    private AnimationClock clock;
    private DispatcherOperation pending;

    private ThemeTransition(Grid root) {
        this.root = root;
        root.Unloaded += (_, _) => Clear();
        root.IsVisibleChanged += (_, _) => { if (!root.IsVisible) Clear(); };
        root.SizeChanged += (_, _) => Clear();
    }
    internal static void Apply(Grid root, Action change) {
        if (root is null) { change(); return; }
        instances.GetValue(root, target => new ThemeTransition(target)).Run(change);
    }
    private static bool MotionAllowed => ModAnimation.AniControlEnabled == 0 && ModAnimation.AniSpeed > 0 && ModAnimation.AniSpeed < 200 && SystemParameters.ClientAreaAnimation;

    internal static bool CanBlendPalette => MotionAllowed && !SystemParameters.HighContrast;

    // The visible WPF tree keeps its live image and blur planes. Transition the brushes those
    // planes already draw with, rather than covering the window with a color or a snapshot.
    private sealed record PaletteFade(Color Target, AnimationClock Clock);
    private static readonly ConditionalWeakTable<SolidColorBrush, PaletteFade> paletteFades = new();

    internal static Color? PaletteTarget(Brush brush) =>
        brush is SolidColorBrush solid
            ? paletteFades.TryGetValue(solid, out PaletteFade fade) ? fade.Target : solid.Color
            : null;

    internal static SolidColorBrush BlendPaletteBrush(SolidColorBrush brush, Color target, bool animate)
    {
        if (PaletteTarget(brush) == target) return brush;
        Color displayed = brush?.Color ?? target;
        if (brush is not null && paletteFades.TryGetValue(brush, out PaletteFade previous))
        {
            paletteFades.Remove(brush);
            brush.Color = displayed;
            brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            previous.Clock.Controller.Remove();
        }
        if (!animate || brush is null || displayed == target)
        {
            var settled = new SolidColorBrush(target);
            settled.Freeze();
            return settled;
        }

        if (brush.IsFrozen) brush = new SolidColorBrush(displayed);
        // Preserve the displayed value even before WPF starts the new animation clock.
        var animation = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(210) };
        animation.KeyFrames.Add(new LinearColorKeyFrame(displayed, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineColorKeyFrame(target, KeyTime.FromPercent(1), new KeySpline(.23, 1, .32, 1)));
        var clock = (AnimationClock)animation.CreateClock(true);
        clock.Controller.SpeedRatio = ModAnimation.AniSpeed;
        paletteFades.Add(brush, new PaletteFade(target, clock));
        clock.Completed += (_, _) =>
        {
            if (!paletteFades.TryGetValue(brush, out PaletteFade active) || !ReferenceEquals(active.Clock, clock)) return;
            paletteFades.Remove(brush);
            brush.Color = target;
            brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            clock.Controller.Remove();
        };
        brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock);
        return brush;
    }

    private void Run(Action change) {
        if (!MotionAllowed || !root.IsLoaded || !root.IsVisible || root.ActualWidth <= 0 || root.ActualHeight <= 0 || Window.GetWindow(root)?.WindowState == WindowState.Minimized) {
            Clear(); change(); return;
        }
        BitmapSource snapshot;
        try {
            root.UpdateLayout();
            double scale = Math.Min(VisualTreeHelper.GetDpi(root).DpiScaleX, Math.Sqrt(4000000d / (root.ActualWidth * root.ActualHeight)));
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth * scale)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root); bitmap.Freeze(); snapshot = bitmap;
        } catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is OutOfMemoryException || error is System.Runtime.InteropServices.COMException) {
            Clear(); change(); return; // Decoration must never prevent a theme change.
        }
        Clear();
        var image = new Image { Source = snapshot, Stretch = Stretch.Fill, IsHitTestVisible = false, Focusable = false, Tag = "theme-transition" };
        Grid.SetRowSpan(image, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(image, Math.Max(1, root.ColumnDefinitions.Count));
        Panel.SetZIndex(image, int.MaxValue);
        overlay = image;
        root.Children.Add(image);
        try { change(); }
        catch { Clear(); throw; }
        // Palette background refreshes are queued by the existing theme service.
        // Keep the old frame until those updates and layout have been rendered.
        pending = root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            pending = null;
            if (!ReferenceEquals(overlay, image)) return;
            if (!MotionAllowed) { Clear(); return; }
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(240) };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(.23, 1, .32, 1)));
            clock = (AnimationClock)animation.CreateClock(true);
            clock.Controller.SpeedRatio = ModAnimation.AniSpeed;
            clock.Completed += (_, _) => { if (ReferenceEquals(overlay, image)) Clear(); };
            image.ApplyAnimationClock(UIElement.OpacityProperty, clock);
        }));
    }
    private void Clear() {
        pending?.Abort(); pending = null;
        clock?.Controller.Remove(); clock = null;
        if (overlay is null) return;
        overlay.ApplyAnimationClock(UIElement.OpacityProperty, null);
        overlay.Source = null;
        root.Children.Remove(overlay);
        overlay = null;
    }
}
