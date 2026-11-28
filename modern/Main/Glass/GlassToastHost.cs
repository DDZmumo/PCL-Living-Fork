// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using PCLLF.Wpf.Motion;

namespace PCL;

internal enum GlassToastKind { Info, Success, Error }

/// <summary>Displays launcher notifications and owns their lifetime in the glass window.</summary>
internal sealed class GlassToastHost : StackPanel, IDisposable {
    private const int MaxToasts = 4;
    private const int MaxPendingHints = 20;
    private const double InsetRight = 34, InsetBottom = 24, ToastWidth = 380, OverlayGap = 12;
    private const double MaxMessageHeight = 144;
    private const double CardHeightOverhead = 30;
    private readonly Dictionary<string, Hint> _hints = new(StringComparer.Ordinal);
    private readonly TranslateTransform _placement = new();
    private double _messageHeight = MaxMessageHeight;
    private bool _dark = true;
    private bool _disposed;

    private sealed class Hint(GlassToastKind kind, long expiresAt) {
        internal GlassToastKind Kind { get; } = kind;
        internal long ExpiresAt { get; set; } = expiresAt;
        internal Toast? Current { get; set; }
    }

    internal GlassToastHost() {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        MaxWidth = ToastWidth;
        Margin = new Thickness(0, 0, InsetRight, InsetBottom);
        RenderTransform = _placement;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    internal void AvoidOverlays(Size viewport, Rect overlays) {
        double bottom = InsetBottom, right = InsetRight, width = ToastWidth;
        if (!overlays.IsEmpty) {
            double above = viewport.Height - overlays.Top + OverlayGap;
            if (above + ActualHeight + OverlayGap <= viewport.Height)
                bottom = Math.Max(bottom, above);
            else {
                right = Math.Max(right, viewport.Width - overlays.Left + OverlayGap);
                width = Math.Clamp(viewport.Width - right - OverlayGap, 0, ToastWidth);
            }
        }
        // Follow the player's animation without remeasuring the toast stack on every frame.
        _placement.X = InsetRight - right;
        _placement.Y = InsetBottom - bottom;
        if (MaxWidth != width) MaxWidth = width;
        double messageHeight = Math.Clamp(Math.Floor((viewport.Height - InsetBottom - OverlayGap) / MaxToasts) -
            CardHeightOverhead, 38, MaxMessageHeight);
        if (_messageHeight == messageHeight) return;
        _messageHeight = messageHeight;
        foreach (Hint hint in _hints.Values)
            if (hint.Current is { } toast) toast.MessageHeight = messageHeight;
    }

    internal void SetTheme(bool dark) {
        if (_dark == dark) return;
        _dark = dark;
        foreach (Hint hint in _hints.Values) hint.Current?.ApplyTheme(dark);
    }

    internal void Show(string text, GlassToastKind kind) {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        long now = Environment.TickCount64;
        long expiresAt = now + 800 + Math.Clamp(text.Length, 5, 60) * 180;
        if (_hints.TryGetValue(text, out Hint? duplicate)) duplicate.ExpiresAt = expiresAt;
        else if (_hints.Count < MaxPendingHints) _hints.Add(text, new Hint(kind, expiresAt));
        Refresh();
    }

    internal void Refresh() {
        if (_disposed || _hints.Count == 0) return;
        long now = Environment.TickCount64;
        int shown = 0;
        foreach ((string text, Hint hint) in _hints.ToArray()) {
            if (hint.Current?.IsReading == true) hint.ExpiresAt = Math.Max(hint.ExpiresAt, now + 1500);
            if (hint.ExpiresAt <= now) {
                _hints.Remove(text);
                hint.Current?.Leave();
            } else if (hint.Current is not null) {
                shown++;
            }
        }
        foreach ((string text, Hint hint) in _hints) {
            if (shown >= MaxToasts) break;
            if (hint.Current is not null) continue;
            hint.Current = new Toast(text, hint.Kind, _dark, _messageHeight, Remove, () => Dismiss(text, hint));
            Children.Add(hint.Current.View);
            hint.Current.Enter();
            shown++;
        }
    }

    internal void DismissAll() {
        foreach (Hint hint in _hints.Values) hint.Current?.Leave();
        _hints.Clear();
    }

    private void Dismiss(string text, Hint hint) {
        if (!_hints.TryGetValue(text, out Hint? current) || !ReferenceEquals(current, hint)) return;
        _hints.Remove(text);
        hint.Current?.Leave();
        Refresh();
    }

    private void Remove(FrameworkElement view) => Children.Remove(view);

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _hints.Clear();
        Children.Clear();
    }

    private sealed class Toast {
        private static readonly Geometry InfoGlyph = Frozen(Geometry.Parse("M10,8.5 V14 M10,5.6 V6"));
        private static readonly Geometry SuccessGlyph = Frozen(Geometry.Parse("M6,10.2 L8.8,13 14,7.4"));
        private static readonly Geometry ErrorGlyph = Frozen(Geometry.Parse("M10,5.6 V11 M10,13.8 V14.2"));
        private static readonly Geometry CloseGlyph = Frozen(Geometry.Parse("M1,1 L9,9 M9,1 L1,9"));
        private readonly Border _card;
        private readonly TextBlock _text;
        private readonly ScrollViewer _scroll;
        private readonly Button _close;
        private readonly TranslateTransform _offset = new();
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly ScaleTransform _collapse = new(1, 1);
        private readonly SpringMotion _motion;
        private readonly Action<FrameworkElement> _removed;
        internal bool Leaving { get; private set; }
        internal FrameworkElement View => _card;
        internal bool IsReading => _card.IsMouseOver || _card.IsKeyboardFocusWithin;
        internal double MessageHeight { set => _scroll.MaxHeight = value; }

        internal Toast(string text, GlassToastKind kind, bool dark, double messageHeight, Action<FrameworkElement> removed, Action dismiss) {
            _removed = removed;
            Color tone = kind switch {
                GlassToastKind.Success => Color.FromRgb(48, 209, 88),
                GlassToastKind.Error => Color.FromRgb(255, 69, 58),
                _ => Color.FromRgb(120, 120, 120),
            };
            var badge = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0) };
            badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(tone) });
            badge.Children.Add(new Path {
                Data = kind switch { GlassToastKind.Success => SuccessGlyph, GlassToastKind.Error => ErrorGlyph, _ => InfoGlyph },
                Stroke = Brushes.White, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            });
            _text = new TextBlock {
                Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"), VerticalAlignment = VerticalAlignment.Center,
            };
            _scroll = GlassDialogControls.Scroll();
            _scroll.Content = _text;
            _scroll.MaxHeight = messageHeight;
            _scroll.VerticalAlignment = VerticalAlignment.Center;
            _close = new Button {
                Style = GlassDialogControls.Style("GlassPresentationButton"), Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top, Content = new Path {
                    Data = CloseGlyph, Stretch = Stretch.Uniform, Width = 10, Height = 10, StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                },
                ToolTip = "关闭提示"
            };
            AutomationProperties.SetName(_close, "关闭提示");
            _close.Click += (_, _) => dismiss();
            var row = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            DockPanel.SetDock(_close, Dock.Right);
            row.Children.Add(_close);
            row.Children.Add(_scroll);
            _card = new Border {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 14, 10),
                Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Child = row, Opacity = 0,
                RenderTransform = new TransformGroup { Children = { _scale, _offset } }, RenderTransformOrigin = new Point(1, 1),
                LayoutTransform = _collapse,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = .28, Color = Colors.Black, RenderingBias = RenderingBias.Performance },
            };
            AutomationProperties.SetName(_card, text);
            ApplyTheme(dark);
            _motion = new SpringMotion(_card, 0, Apply, new SpringParameters(.42, .72));
        }

        internal void ApplyTheme(bool dark) {
            _card.Background = new SolidColorBrush(dark ? Color.FromArgb(245, 43, 43, 43) : Color.FromArgb(250, 255, 255, 255));
            _card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
            _text.Foreground = new SolidColorBrush(dark ? Color.FromRgb(242, 242, 242) : Color.FromRgb(31, 31, 31));
            _scroll.Foreground = _close.Foreground = _text.Foreground;
            ((Path)_close.Content).Stroke = _close.Foreground;
        }

        // A spring snaps while its owner is not yet loaded, so the entrance starts once the card is in the tree.
        internal void Enter() { if (_card.IsLoaded) _motion.Retarget(1); else _card.Loaded += OnLoaded; }
        private void OnLoaded(object sender, RoutedEventArgs args) { _card.Loaded -= OnLoaded; if (!Leaving) _motion.Retarget(1); }

        internal void Leave() {
            if (Leaving) return;
            Leaving = true;
            _card.IsHitTestVisible = false;
            _close.IsEnabled = false;
            _motion.Retarget(0);
            if (_motion.Value <= MotionTokens.VisibilityThreshold) _removed(_card);
        }

        // Enter: slide in from the right while growing from 92% with the spring's overshoot (Apple-style pop).
        // Leave: the same spring runs back while the card's height collapses, so the stack below glides up instead of jumping.
        private void Apply(double value) {
            double p = Math.Clamp(value, 0, 1.2);
            _card.Opacity = Math.Clamp(value, 0, 1);
            _offset.X = (1 - p) * 28;
            _scale.ScaleX = _scale.ScaleY = .92 + .08 * p;
            _collapse.ScaleY = Leaving ? Math.Clamp(value, 0, 1) : 1;
            if (Leaving && value <= MotionTokens.VisibilityThreshold) _removed(_card);
        }

        private static Geometry Frozen(Geometry geometry) { geometry.Freeze(); return geometry; }
    }
}
