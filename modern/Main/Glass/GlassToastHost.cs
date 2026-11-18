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

/// <summary>Severity of a mirrored hint, read from the original hint's colour.</summary>
internal enum GlassToastKind { Info, Success, Error }

/// <summary>
/// Mirrors the original hint panel as toast cards in the bottom-right corner of the main body. The original
/// runtime keeps ownership of queueing, duplicate handling and timing: a toast appears while its source hint is
/// alive (Tag[0] == true) and leaves when the source expires or is removed.
/// </summary>
internal sealed class GlassToastHost : StackPanel, IDisposable {
    private const int MaxToasts = 4;
    private readonly Panel _source;
    private readonly Dictionary<Border, Toast> _toasts = new(ReferenceEqualityComparer.Instance);
    private readonly EventHandler _sourceLayoutUpdated;
    private bool _dark = true;
    private bool _disposed;

    internal GlassToastHost(Panel source) {
        _source = source;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        IsHitTestVisible = false;
        MaxWidth = 380;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        // Hints are added by the original runtime on the UI thread; its own layout pass makes the mirror immediate.
        _sourceLayoutUpdated = (_, _) => Refresh();
        _source.LayoutUpdated += _sourceLayoutUpdated;
    }

    internal void SetTheme(bool dark) {
        if (_dark == dark) return;
        _dark = dark;
        foreach (Toast toast in _toasts.Values) toast.ApplyTheme(dark);
    }

    internal void Refresh() {
        if (_disposed || _source.Children.Count == 0 && _toasts.Count == 0) return;
        var alive = new HashSet<Border>(ReferenceEqualityComparer.Instance);
        foreach (Border hint in _source.Children.OfType<Border>()) {
            if (hint.Tag is not object[] { Length: > 0 } tag || tag[0] is not true || hint.Child is not TextBlock { Text: { Length: > 0 } text }) continue;
            alive.Add(hint);
            if (_toasts.TryGetValue(hint, out Toast? existing)) { existing.SetText(text); continue; }
            if (_toasts.Values.Count(toast => !toast.Leaving) >= MaxToasts) continue;
            var toast = new Toast(text, KindOf(hint), _dark, Remove);
            _toasts.Add(hint, toast);
            Children.Add(toast.View);
            toast.Enter();
        }
        foreach ((Border hint, Toast toast) in _toasts.ToArray())
            if (!alive.Contains(hint)) { _toasts.Remove(hint); toast.Leave(); }
    }

    private void Remove(FrameworkElement view) => Children.Remove(view);

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _source.LayoutUpdated -= _sourceLayoutUpdated;
        _toasts.Clear();
        Children.Clear();
    }

    // The original hint paints a blend of its type colour and white, so the dominant channel identifies the type.
    private static GlassToastKind KindOf(Border hint) {
        if (hint.Background is not GradientBrush { GradientStops.Count: > 0 } brush) return GlassToastKind.Info;
        Color color = brush.GradientStops[^1].Color;
        if (color.G > color.R && color.G > color.B) return GlassToastKind.Success;
        if (color.R > color.G && color.R > color.B) return GlassToastKind.Error;
        return GlassToastKind.Info;
    }

    private sealed class Toast {
        private static readonly Geometry InfoGlyph = Frozen(Geometry.Parse("M10,8.5 V14 M10,5.6 V6"));
        private static readonly Geometry SuccessGlyph = Frozen(Geometry.Parse("M6,10.2 L8.8,13 14,7.4"));
        private static readonly Geometry ErrorGlyph = Frozen(Geometry.Parse("M10,5.6 V11 M10,13.8 V14.2"));
        private readonly Border _card;
        private readonly TextBlock _text;
        private readonly TranslateTransform _offset = new();
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly ScaleTransform _collapse = new(1, 1);
        private readonly SpringMotion _motion;
        private readonly Action<FrameworkElement> _removed;
        internal bool Leaving { get; private set; }
        internal FrameworkElement View => _card;

        internal Toast(string text, GlassToastKind kind, bool dark, Action<FrameworkElement> removed) {
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
                Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"), VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(_text);
            _card = new Border {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 14, 10),
                Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Child = row, Opacity = 0,
                RenderTransform = new TransformGroup { Children = { _scale, _offset } }, RenderTransformOrigin = new Point(1, 1),
                LayoutTransform = _collapse, ToolTip = text,
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
        }

        internal void SetText(string text) {
            if (_text.Text == text) return;
            _text.Text = text; _card.ToolTip = text; AutomationProperties.SetName(_card, text);
        }

        // A spring snaps while its owner is not yet loaded, so the entrance starts once the card is in the tree.
        internal void Enter() { if (_card.IsLoaded) _motion.Retarget(1); else _card.Loaded += OnLoaded; }
        private void OnLoaded(object sender, RoutedEventArgs args) { _card.Loaded -= OnLoaded; if (!Leaving) _motion.Retarget(1); }

        internal void Leave() {
            if (Leaving) return;
            Leaving = true;
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
