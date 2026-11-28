// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>The two note states share one baseline. Their measured sizes stay fixed while the capsule changes size.</summary>
internal sealed class GlassFavoriteNote : Panel
{
    private const double LineHeight = 20, IconSize = 12, IconLeft = 10, TextLeft = 29, RightInset = 12, TopInset = 5, BottomInset = 6;
    private static readonly Geometry IconGeometry = Geometry.Parse("M4,3 H14 L19,8 V21 H4 Z M14,3 V8 H19 M8,13 H15 M8,17 H13");
    private readonly Border _plate = new() { CornerRadius = new CornerRadius(10) };
    private readonly Path _icon = new() { Data = IconGeometry, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private readonly TextBlock _summary;
    private readonly TextBlock _body;
    private readonly SpringMotion _motion;
    private double _progress;

    internal GlassFavoriteNote(string note, bool expanded)
    {
        Note = note;
        IsExpanded = expanded;
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        _plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSelected");
        _icon.SetResourceReference(Shape.StrokeProperty, "GlassPresentationAccent");
        _summary = Label("备注", "GlassPresentationSecondary", TextWrapping.NoWrap);
        _body = Label(ResourceNotes.Quote(note), "GlassPresentationText", TextWrapping.Wrap);
        Children.Add(_plate);
        Children.Add(_icon);
        Children.Add(_summary);
        Children.Add(_body);
        AutomationProperties.SetName(this, "备注：" + note);
        SetToolTip();
        _motion = new SpringMotion(this, expanded ? 1 : 0, ApplyProgress, MotionTokens.Toggle);
        MouseLeftButtonUp += (_, args) => {
            args.Handled = true;
            SetExpanded(!IsExpanded);
        };
    }

    internal string Note { get; }
    internal bool IsExpanded { get; private set; }

    internal void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        SetToolTip();
        _motion.Retarget(expanded ? 1 : 0);
    }

    private void SetToolTip() => ToolTip = IsExpanded ? "点击收起备注" : "点击展开备注";

    private void ApplyProgress(double value)
    {
        _progress = Math.Clamp(value, 0, 1);
        _summary.Opacity = 1 - _progress;
        _body.Opacity = _progress;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var textSize = new Size(Math.Max(0, availableSize.Width - TextLeft - RightInset), double.PositiveInfinity);
        _summary.Measure(textSize);
        _body.Measure(textSize);
        _plate.Measure(availableSize);
        _icon.Measure(new Size(IconSize, IconSize));
        Size closed = _summary.DesiredSize, open = _body.DesiredSize;
        return new Size(TextLeft + RightInset + closed.Width + (open.Width - closed.Width) * _progress,
            TopInset + BottomInset + Math.Max(LineHeight, closed.Height + (open.Height - closed.Height) * _progress));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _plate.Arrange(new Rect(finalSize));
        _icon.Arrange(new Rect(IconLeft, TopInset + (LineHeight - IconSize) / 2, IconSize, IconSize));
        _summary.Arrange(new Rect(new Point(TextLeft, TopInset), _summary.DesiredSize));
        _body.Arrange(new Rect(new Point(TextLeft, TopInset), _body.DesiredSize));
        return finalSize;
    }

    private static TextBlock Label(string text, string foreground, TextWrapping wrapping)
    {
        var label = new TextBlock { Text = text, FontSize = 13, LineHeight = LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = wrapping, TextTrimming = TextTrimming.None, IsHitTestVisible = false };
        label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return label;
    }
}
