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
