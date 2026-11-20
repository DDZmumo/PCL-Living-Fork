// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>
/// "Tide" progress for a card: a translucent accent water level rising with the progress (two gentle swells on its
/// surface), a large percentage, a caption and an optional stop button. While shown it fades out the card's normal
/// content (<see cref="Covers"/>). Used for signing in and for launching the game.
/// Place it over the whole card; it clips itself to the card's rounded corners.
/// </summary>
internal sealed class GlassTideProgress : Grid
{
    private readonly Path _water = new() { IsHitTestVisible = false, Opacity = .2 };
    private readonly StreamGeometry _waterGeometry = new();
    private readonly Point[] _waterPoints = new Point[34];
    private readonly TextBlock _percent = new() { FontSize = 46, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _caption = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, Margin = new Thickness(16, 6, 16, 0) };
    private readonly Button _stop;
    private readonly StackPanel _center = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly SpringMotion _level, _presence;
    private double _phase, _target = -1, _radius;
    private bool _rendering;
    private TimeSpan? _lastFrame;

    /// <summary>The card's normal content, faded out while the progress shows.</summary>
    internal UIElement? Covers { get; set; }
    /// <summary>Size of the percentage figure (46 by default; small hosts such as the rail use a compact one).</summary>
    internal double PercentSize { get => _percent.FontSize; set => _percent.FontSize = value; }
    internal event EventHandler? StopRequested;

    /// <summary>
    /// Narrow hosts (the splash rail): the percentage is set like a poster — turned on its side, fixed in the middle of
    /// the host — as a solid figure over an offset outline echo of itself, with a small "%"; it pops when it changes.
    /// </summary>
    internal bool PosterFigure
    {
        get => _poster;
        set
        {
            _poster = value;
            _center.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _posterHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) RenderPoster("0.00");
        }
    }
    private bool _poster;
    private readonly Grid _posterHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Path _posterSolid = new(), _posterEcho = new() { StrokeThickness = 1.1, Opacity = .45 };
    private readonly ScaleTransform _ridePop = new(1, 1);
    private SpringMotion? _popMotion;
    private string _posterText = "";
    private int _lastPoppedPercent = -1;
    private static readonly Typeface PosterTypeface = new(new FontFamily("Segoe UI Variable Display, Bahnschrift, Segoe UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.SemiCondensed);

    // The figure as outlines: digits large, "%" small and raised, laid out left to right, then the whole group turned
    // a quarter so it reads bottom to top along the rail. The echo is the same outline, stroked only, offset down-right.
    private void RenderPoster(string figure)
    {
        if (figure == _posterText) return;
        _posterText = figure;
        double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int dot = figure.IndexOf('.');
        FormattedText Format(string text, double size) => new(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, PosterTypeface, size, Brushes.Black, pixels);
        var digits = Format(figure[..dot], 40);
        var decimals = Format(figure[dot..], 26);
        var unit = Format("%", 16);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(digits.BuildGeometry(new Point(0, 0)));
        double decimalsStart = digits.WidthIncludingTrailingWhitespace + 1;
        group.Children.Add(decimals.BuildGeometry(new Point(decimalsStart, digits.Baseline - decimals.Baseline)));
        group.Children.Add(unit.BuildGeometry(new Point(decimalsStart + decimals.WidthIncludingTrailingWhitespace + 2, digits.Baseline - unit.Baseline - 1)));
        // Glyph outlines carry the font's side bearings and line space; move the ink itself to the origin so centring
        // (after the quarter turn) centres the figure, not its text box.
        Rect ink = group.Bounds;
        group.Transform = new TranslateTransform(-ink.X, -ink.Y);
        group.Freeze();
        _posterSolid.Data = group; _posterEcho.Data = group;
    }

    internal GlassTideProgress(double cornerRadius, string? stopText = null)
    {
        _radius = cornerRadius;
        IsHitTestVisible = false; Opacity = 0; Visibility = Visibility.Hidden;
        _water.SetResourceReference(Shape.FillProperty, "GlassAccentBrush");
        _water.Data = _waterGeometry;
        _percent.SetResourceReference(TextBlock.ForegroundProperty, "GlassTextBrush");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush");
        foreach (TextBlock text in new[] { _percent, _caption }) text.FontFamily = new FontFamily("Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI");
        Children.Add(_water);
        _center.Children.Add(_percent); _center.Children.Add(_caption);
        _stop = StopButton(stopText ?? "终止");
        _stop.Visibility = stopText is null ? Visibility.Collapsed : Visibility.Visible;
        _stop.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        _center.Children.Add(_stop);
        Children.Add(_center);
        _posterSolid.SetResourceReference(Shape.FillProperty, "GlassTextBrush");
        _posterEcho.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush");
        _posterEcho.RenderTransform = new TranslateTransform(4, 4);
        _posterHost.Children.Add(_posterEcho); _posterHost.Children.Add(_posterSolid);
        _posterHost.RenderTransformOrigin = new Point(.5, .5);
        _posterHost.LayoutTransform = new RotateTransform(-90);
        _posterHost.RenderTransform = _ridePop;
        Children.Add(_posterHost);
        SizeChanged += (_, _) => { Clip = new RectangleGeometry(new Rect(RenderSize), _radius, _radius); Draw(); };
        _level = new SpringMotion(this, 0, _ => { if (!_rendering) Draw(); }, new SpringParameters(.9, 1)) { MaxFrameSeconds = 1 / 30d };
        _presence = new SpringMotion(this, 0, ApplyPresence, new SpringParameters(.35, 1));
        Loaded += (_, _) => UpdateRendering();
        IsVisibleChanged += (_, _) => UpdateRendering();
        Unloaded += (_, _) => SetRendering(false);
    }

    /// <summary>Shows or hides the progress; <paramref name="progress"/> is 0–1, or null while it is not known yet.</summary>
    internal void Update(bool active, double? progress, string caption)
    {
        bool snap = !MotionPreferences.AllowsAnimation || !IsLoaded;
        if (active)
        {
            double value = progress is double amount && double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;
            int hundredths = value == 1 ? 10000 : Math.Min(9999, (int)((decimal)value * 10000));
            string figure = (hundredths / 100d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            _percent.Text = figure + "%";
            if (_poster && _posterText != figure)
            {
                RenderPoster(figure);
                int wholePercent = hundredths / 100;
                bool wholePercentChanged = wholePercent != _lastPoppedPercent;
                _lastPoppedPercent = wholePercent;
                if (wholePercentChanged && MotionPreferences.AllowsAnimation && IsLoaded)
                {
                    _popMotion ??= new SpringMotion(this, 1, v => _ridePop.ScaleX = _ridePop.ScaleY = v, new SpringParameters(.28, .55));
                    _popMotion.Snap(1.14); _popMotion.Retarget(1);
                }
            }
            _caption.Text = caption;
            _caption.Visibility = string.IsNullOrWhiteSpace(caption) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(this, $"{caption} {_percent.Text}");
            if (Math.Abs(value - _target) > .001) { _target = value; _level.Retarget(Math.Max(.04, value), snap); }
            Visibility = Visibility.Visible;
        }
        IsHitTestVisible = active;
        double presence = active ? 1 : 0;
        if (_presence.Target != presence) _presence.Retarget(presence, snap);
    }

    private void ApplyPresence(double value)
    {
        double p = Math.Clamp(value, 0, 1);
        Opacity = p;
        if (Covers is not null) Covers.Opacity = 1 - p;
        if (p <= .01 && !IsHitTestVisible) { Visibility = Visibility.Hidden; _target = -1; _level.Snap(0); }
        UpdateRendering();
    }

    private void UpdateRendering() => SetRendering(IsLoaded && IsVisible && Opacity > .01 && MotionPreferences.AllowsAnimation);

    private void SetRendering(bool rendering)
    {
        if (rendering == _rendering) return;
        _rendering = rendering;
        _lastFrame = null;
        if (rendering) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || _lastFrame == frame.RenderingTime) return;
        if (_lastFrame is { } previous)
            _phase += Math.Clamp((frame.RenderingTime - previous).TotalSeconds, 0, 1 / 30d) * 2.7;
        _lastFrame = frame.RenderingTime;
        Draw();
    }

    // Water surface: two gentle sine swells across the card; its level follows the progress spring.
    private void Draw()
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        // The swells reach 9 px either side of the level: at 100 % the whole surface clears the top edge.
        const double swell = 10;
        double level = (height + swell) * (1 - Math.Clamp(_level.Value, 0, 1)) - swell;
        for (int i = 0; i <= 32; i++)
        {
            double t = i / 32d;
            _waterPoints[i] = new Point(width * t, level + 6 * Math.Sin(_phase + t * Math.PI * 2) + 3 * Math.Sin(_phase * 1.7 + t * Math.PI * 4));
        }
        _waterPoints[^1] = new Point(width, height);
        using StreamGeometryContext geometry = _waterGeometry.Open();
        geometry.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
        geometry.PolyLineTo(_waterPoints, isStroked: false, isSmoothJoin: false);
    }

    private static Button StopButton(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI") };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GlassTextBrush");
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        chrome.SetResourceReference(Border.BackgroundProperty, "GlassSelectedBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.PaddingProperty, new Thickness(18, 0, 18, 0));
        chrome.SetValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .86, "Chrome"));
        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(UIElement.RenderTransformProperty, new ScaleTransform(.96, .96), "Chrome"));
        template.Triggers.Add(hover); template.Triggers.Add(press);
        var button = new Button { Template = template, Content = label, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Cursor = Cursors.Hand };
        AutomationProperties.SetName(button, text);
        return button;
    }
}
