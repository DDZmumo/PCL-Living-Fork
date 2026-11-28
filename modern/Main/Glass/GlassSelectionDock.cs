// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

public sealed class GlassSelectionDock : Grid, IDisposable
{
    internal const string UpdateIcon = "M640 768H384l-32-32V509H213L190 454l298-298h45l298 298L810 509h-138v226z m-224-64h192V477l32-32h93L512 223 290 445H384l32 32zM352 831h320v64h-320z";
    internal const string EnableIcon = "M512 0a512 512 0 1 0 512 512A512 512 0 0 0 512 0z m0 921.6a409.6 409.6 0 1 1 409.6-409.6 409.6 409.6 0 0 1-409.6 409.6z M716.8 339.968l-256 253.44L328.192 460.8A51.2 51.2 0 0 0 256 532.992l168.448 168.96a51.2 51.2 0 0 0 72.704 0l289.28-289.792A51.2 51.2 0 0 0 716.8 339.968z";
    internal const string DisableIcon = "M508 990.4c-261.6 0-474.4-212-474.4-474.4S246.4 41.6 508 41.6s474.4 212 474.4 474.4S769.6 990.4 508 990.4zM508 136.8c-209.6 0-379.2 169.6-379.2 379.2 0 209.6 169.6 379.2 379.2 379.2s379.2-169.6 379.2-379.2C887.2 306.4 717.6 136.8 508 136.8zM697.6 563.2 318.4 563.2c-26.4 0-47.2-21.6-47.2-47.2 0-26.4 21.6-47.2 47.2-47.2l379.2 0c26.4 0 47.2 21.6 47.2 47.2C744.8 542.4 724 563.2 697.6 563.2z";
    internal const string DeleteIcon = "M520.192 0C408.43 0 317.44 82.87 313.563 186.734H52.736c-29.038 0-52.663 21.943-52.663 49.079s23.625 49.152 52.663 49.152h58.075v550.473c0 103.35 75.118 187.757 167.717 187.757h472.43c92.599 0 167.716-83.894 167.716-187.757V285.477h52.59c29.038 0 52.59-21.943 52.663-49.08-0.073-27.135-23.625-49.151-52.663-49.151H726.235C723.237 83.017 631.955 0 520.192 0zM404.846 177.957c3.803-50.03 50.176-89.015 107.447-89.015 57.197 0 103.57 38.985 106.788 89.015H404.92zM284.379 933.669c-33.353 0-69.997-39.351-69.997-95.525v-549.01H833.39v549.522c0 56.247-36.645 95.525-69.998 95.525H284.379v-0.512z M357.23 800.695a48.274 48.274 0 0 0 47.616-49.006V471.7a48.274 48.274 0 0 0-47.543-49.08 48.274 48.274 0 0 0-47.69 49.006V751.69c0 27.282 20.846 49.006 47.617 49.006z m166.62 0a48.274 48.274 0 0 0 47.688-49.006V471.7a48.274 48.274 0 0 0-47.689-49.08 48.274 48.274 0 0 0-47.543 49.006V751.69c0 27.282 21.431 49.006 47.543 49.006z m142.92 0a48.274 48.274 0 0 0 47.543-49.006V471.7a48.274 48.274 0 0 0-47.543-49.08 48.274 48.274 0 0 0-47.616 49.006V751.69c0 27.282 20.773 49.006 47.543 49.006z";
    internal const string CancelIcon = "M867.648 951.296 512 595.648l-355.648 355.648c-11.52 11.52-30.272 11.52-41.856 0L72.64 909.44c-11.52-11.52-11.52-30.272 0-41.856L428.352 512 72.64 156.352c-11.52-11.52-11.52-30.272 0-41.856l41.856-41.856c11.52-11.52 30.272-11.52 41.856 0L512 428.288l355.648-355.648c11.52-11.52 30.272-11.52 41.856 0l41.856 41.856c11.52 11.52 11.52 30.272 0 41.856L595.648 512l355.648 355.648c11.52 11.52 11.52 30.272 0 41.856l-41.856 41.856C897.984 962.88 879.168 962.88 867.648 951.296L867.648 951.296z";
    private readonly WrapPanel _items = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _leading = Divider(), _trailing = Divider();
    private readonly Border _statusBadge;
    private readonly SpringMotion _motion;
    private readonly ScaleTransform _scale = new();
    private Button? _dismiss;
    private readonly GlassSceneFrost _frost = new();
    private FrameworkElement? _scene;
    internal TranslateTransform Position { get; } = new();
    internal TextBlock Status { get; } = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, MaxWidth = 240,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    public GlassSelectionDock()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(20, 0, 20, 18);
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        SetZIndex(this, 12);
        RenderTransformOrigin = new Point(.5, 1);
        RenderTransform = new TransformGroup { Children = { _scale, Position } };
        var shadow = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(1), Opacity = .52,
            IsHitTestVisible = false, Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270,
                ShadowDepth = 7, BlurRadius = 24, Opacity = .36, RenderingBias = RenderingBias.Performance } };
        shadow.SetResourceReference(Border.BackgroundProperty, "GlassPresentationPopup");
        Children.Add(shadow);
        // Reconstruct the wallpaper under transparent areas of the page, then blur the visible rows over it.
        var backing = new GlassSurface { CornerRadius = new CornerRadius(14), BlurRadius = 24, IsHitTestVisible = false };
        backing.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        Children.Add(backing);
        Children.Add(_frost);
        var surface = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 5, 6, 5),
            BorderThickness = new Thickness(1), Child = _items };
        surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDockFill");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationDockEdge");
        Children.Add(surface);
        var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
        Status.SetResourceReference(FrameworkElement.StyleProperty, "GlassPresentationTextStyle");
        Status.TextWrapping = TextWrapping.NoWrap;
        Status.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        _statusBadge = new Border { CornerRadius = new CornerRadius(9), Height = 32, Padding = new Thickness(10, 0, 11, 0),
            Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, Status } } };
        _statusBadge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDockBadgeFill");
        _items.Children.Add(_statusBadge);
        _items.Children.Add(_leading);
        _items.Children.Add(_trailing);
        _motion = new SpringMotion(this, 0, Draw, new SpringParameters(.24, .86));
        IsVisibleChanged += OnDockVisibilityChanged;
    }

    internal void SetBackdropSource(FrameworkElement scene) => _scene = scene;

    private void OnDockVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible) { LayoutUpdated += OnDockLayout; RefreshFrost(); }
        else { LayoutUpdated -= OnDockLayout; _frost.ClearBackdrop(); }
    }

    private void OnDockLayout(object? sender, EventArgs args) => RefreshFrost();

    private void RefreshFrost()
    {
        if (IsVisible && _scene is { IsVisible: true } scene && GlassMaterial.AllowsTransparency(this) &&
            PresentationSource.FromVisual(this) is { } source && ReferenceEquals(PresentationSource.FromVisual(scene), source))
            _frost.UpdateBackdrop(this, scene, scene, 14);
        else _frost.ClearBackdrop();
    }

    private static Border Divider()
    {
        var line = new Border { Width = 1, Height = 16, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        return line;
    }

    internal Button AddNamedCommand(FrameworkElement page, string name, string caption, string icon, bool danger = false, bool dismiss = false)
    {
        var action = AddCommand(new Button { Name = name, Content = caption }, icon, danger, dismiss);
        page.RegisterName(name, action);
        return action;
    }

    internal (Button Clear, Button Cancel) AddKeyCaptureCommands(FrameworkElement page, string clearName, string cancelName)
    {
        page.RegisterName("LabCapture", Status);
        Status.MaxWidth = 340;
        return (AddNamedCommand(page, clearName, "取消绑定", ModBase.Logo.IconButtonDelete),
            AddNamedCommand(page, cancelName, "取消修改", ModBase.Logo.IconButtonCross, dismiss: true));
    }

    internal Button AddCommand(Button action, string icon, bool danger = false, bool dismiss = false)
    {
        action.Style = GlassDialogControls.Style("GlassPresentationDockButton");
        action.SetResourceReference(ContentControl.ContentTemplateProperty, "GlassResourceSelectionCommand");
        action.Width = double.NaN;
        action.Height = action.MinHeight = 32;
        action.MinWidth = 0;
        action.Padding = new Thickness(9, 0, 10, 0);
        action.Margin = new Thickness(2, 0, 2, 0);
        SetIcon(action, icon);
        if (danger) action.SetResourceReference(Control.BackgroundProperty, "GlassPresentationDangerFill");
        action.SetResourceReference(Control.ForegroundProperty, danger ? "GlassPresentationDangerText" : dismiss ? "GlassPresentationSecondary" : "GlassPresentationText");
        if (dismiss) { _dismiss = action; _items.Children.Add(action); }
        else _items.Children.Insert(_items.Children.IndexOf(_trailing), action);
        action.IsVisibleChanged += (_, _) => UpdateDividers();
        UpdateDividers();
        return action;
    }

    internal static void SetIcon(Button action, string icon)
    {
        if (string.IsNullOrWhiteSpace(icon) || Equals(action.Tag, icon)) return;
        action.Tag = icon;
    }

    private void UpdateDividers()
    {
        bool actions = _items.Children.OfType<Button>().Any(b => b != _dismiss && b.Visibility == Visibility.Visible);
        bool cancel = _dismiss?.Visibility == Visibility.Visible;
        _leading.Visibility = actions || cancel ? Visibility.Visible : Visibility.Collapsed;
        _trailing.Visibility = actions && cancel ? Visibility.Visible : Visibility.Collapsed;
    }

    internal double ScrollClearance(ScrollViewer scroll)
    {
        if (!IsHitTestVisible || ActualHeight <= 0 || Parent is not FrameworkElement host ||
            scroll.ActualHeight <= 0 || !host.IsAncestorOf(scroll)) return 0;
        // Use the dock's resting bounds so the scroll extent stays steady during its entrance.
        double dockTop = host.ActualHeight - Margin.Bottom - ActualHeight;
        double viewportBottom = scroll.TranslatePoint(new Point(0, scroll.ActualHeight - scroll.Padding.Bottom), host).Y;
        const double gap = 6;
        return Math.Ceiling(Math.Max(0, viewportBottom - dockTop + gap));
    }

    internal void ShowSelection(int count, bool animate)
    {
        if (count > 0)
        {
            _statusBadge.ToolTip = Status.Text.Length > 22 ? Status.Text : null;
            Visibility = Visibility.Visible;
        }
        IsHitTestVisible = count > 0;
        _motion.Retarget(count > 0 ? 1 : 0, !animate);
    }

    private void Draw(double progress)
    {
        Opacity = Math.Clamp(progress, 0, 1);
        Position.Y = 12 * (1 - progress);
        _scale.ScaleX = _scale.ScaleY = .96 + .04 * progress;
        if (IsVisible) RefreshFrost();
        if (progress <= 0 && !IsHitTestVisible) Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        IsVisibleChanged -= OnDockVisibilityChanged;
        LayoutUpdated -= OnDockLayout;
        _scene = null;
        _frost.ClearBackdrop();
        _motion.Dispose();
    }
}
