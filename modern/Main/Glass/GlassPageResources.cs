// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>Resources and material colors shared by the glass pages.</summary>
internal sealed class GlassPageResources {
    private readonly FrameworkElement root;
    private readonly FrameworkElement? source;
    private readonly Action<ResourceDictionary>? paletteChanged;

    internal GlassPageResources(FrameworkElement root, FrameworkElement? source = null, Action<ResourceDictionary>? paletteChanged = null)
    {
        this.root = root;
        this.source = source;
        this.paletteChanged = paletteChanged;
        // Pages are often themed before they join the shell, where the wallpaper material is not inherited yet and the
        // palette falls back to opaque fills. Re-read it whenever the page enters the shell so cards stay frosted glass.
        root.Loaded += (_, _) => ApplyPalette();
    }

    [ThreadStatic]
    private static ResourceDictionary? _sharedResources;

    internal static ResourceDictionary GetSharedResources() =>
        _sharedResources ??= new ResourceDictionary { Source = new Uri("/LivingPCL;component/Glass/GlassPresentationResources.xaml", UriKind.Relative) };

    internal Style Style(string key) => (Style)root.FindResource(key);

    internal sealed class SliderDragHint : IDisposable
    {
        private readonly Slider _slider;
        private ToolTip? _hint;
        private Thumb? _thumb;
        private string? _valueText;

        internal SliderDragHint(Slider slider)
        {
            _slider = slider;
            _slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted), handledEventsToo: true);
            _slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), handledEventsToo: true);
            _slider.Unloaded += OnUnloaded;
            _slider.IsVisibleChanged += OnAvailabilityChanged;
            _slider.IsEnabledChanged += OnAvailabilityChanged;
        }

        internal void SetText(string? text)
        {
            _valueText = text;
            if (_hint is null) return;
            if (text is null) Close();
            else if (!Equals(_hint.Content, text)) _hint.Content = text;
        }

        private void OnDragStarted(object sender, DragStartedEventArgs e)
        {
            if (e.OriginalSource is not Thumb thumb || _valueText is null || !_slider.IsVisible || !_slider.IsEnabled) return;
            Close();
            _thumb = thumb;
            _hint = new ToolTip {
                Style = _slider.TryFindResource(typeof(ToolTip)) as Style ?? GlassCommandChrome.ToolTipStyle(!ModTheme.IsDarkTheme),
                PlacementTarget = _slider, Placement = PlacementMode.Custom, CustomPopupPlacementCallback = PlaceDragHint,
                Content = _valueText, IsHitTestVisible = false, Focusable = false
            };
            _hint.Closed += (owner, _) => ((ToolTip)owner).PlacementTarget = null;
            UpdatePosition(this, EventArgs.Empty);
            _slider.LayoutUpdated += UpdatePosition;
            _hint.IsOpen = true;
        }

        private void OnDragCompleted(object sender, DragCompletedEventArgs e) => Close();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Close();
        private void OnAvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Close(); }

        private void UpdatePosition(object sender, EventArgs e)
        {
            if (_hint is not { } hint || _thumb is not { } thumb) return;
            var anchor = new Rect(thumb.TranslatePoint(new Point(), _slider), thumb.RenderSize);
            if (hint.PlacementRectangle != anchor) hint.PlacementRectangle = anchor;
        }

        private static CustomPopupPlacement[] PlaceDragHint(Size popup, Size thumb, Point offset)
        {
            double x = (thumb.Width - popup.Width) / 2;
            return [new(new Point(x, -popup.Height - 5), PopupPrimaryAxis.Horizontal),
                    new(new Point(x, thumb.Height + 5), PopupPrimaryAxis.Horizontal)];
        }

        private void Close()
        {
            if (_hint is not { } hint) return;
            _slider.LayoutUpdated -= UpdatePosition;
            _hint = null;
            _thumb = null;
            hint.IsOpen = false;
        }

        public void Dispose()
        {
            Close();
            _slider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
            _slider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
            _slider.Unloaded -= OnUnloaded;
            _slider.IsVisibleChanged -= OnAvailabilityChanged;
            _slider.IsEnabledChanged -= OnAvailabilityChanged;
        }
    }

    internal static void BindTextPlaceholder(TextBox input, TextBlock placeholder)
    {
        var visibility = new MultiBinding { Mode = BindingMode.OneWay, Converter = TextPlaceholderVisibility.Instance };
        visibility.Bindings.Add(new Binding(nameof(TextBox.Text)) { Source = input });
        visibility.Bindings.Add(new Binding(nameof(UIElement.IsKeyboardFocusWithin)) { Source = input });
        visibility.Bindings.Add(new Binding(nameof(TextBlock.Text)) { Source = placeholder });
        placeholder.SetBinding(UIElement.VisibilityProperty, visibility);
    }

    private sealed class TextPlaceholderVisibility : IMultiValueConverter
    {
        internal static readonly TextPlaceholderVisibility Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values switch
        {
            // An IME can display preedit even while Text still contains the last committed value.
            [_, true, string { Length: > 0 }] => Visibility.Hidden,
            [string { Length: 0 }, false, string { Length: > 0 }] => Visibility.Visible,
            _ => Visibility.Collapsed
        };

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    internal static Button Pill(string text, bool primary, bool readableWhenDisabled = false)
    {
        var surface = new FrameworkElementFactory(typeof(Border), "Surface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var label = new FrameworkElementFactory(typeof(TextBlock), "Label");
        label.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        label.SetValue(TextBlock.FontFamilyProperty, new TemplateBindingExtension(Control.FontFamilyProperty));
        label.SetValue(TextBlock.FontSizeProperty, new TemplateBindingExtension(Control.FontSizeProperty));
        label.SetValue(TextBlock.FontWeightProperty, new TemplateBindingExtension(Control.FontWeightProperty));
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(label);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .88, "Surface"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        if (readableWhenDisabled)
        {
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("GlassPresentationField"), "Surface"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("GlassPresentationControlStroke"), "Surface"));
            disabled.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new DynamicResourceExtension("GlassPresentationSecondary"), "Label"));
            disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Arrow));
        }
        else disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .55));
        template.Triggers.Add(hover); template.Triggers.Add(disabled);
        var scale = new ScaleTransform(1, 1);
        var button = new Button { Template = template, Content = text, Height = 44, MinWidth = primary ? 196 : 150, Padding = new Thickness(24, 0, 24, 0), FontSize = 15,
            FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale };
        button.SetResourceReference(Control.FontFamilyProperty, "GlassPresentationFont");
        button.SetResourceReference(Control.BackgroundProperty, primary ? "GlassPresentationAccent" : "GlassPresentationPrimaryFill");
        button.SetResourceReference(Control.ForegroundProperty, primary ? "GlassPresentationOnAccent" : "GlassPresentationText");
        button.SetResourceReference(Control.BorderBrushProperty, primary ? "GlassPresentationAccentStroke" : "GlassPresentationPrimaryEdge");
        var press = new SpringMotion(button, 1, value => scale.ScaleX = scale.ScaleY = value, new SpringParameters(.3, .58));
        button.PreviewMouseLeftButtonDown += (_, _) => press.Retarget(.94, !MotionPreferences.AllowsAnimation);
        button.PreviewMouseLeftButtonUp += (_, _) => press.Retarget(1, !MotionPreferences.AllowsAnimation);
        button.MouseLeave += (_, _) => press.Retarget(1, !MotionPreferences.AllowsAnimation);
        AutomationProperties.SetName(button, text);
        return button;
    }

    // An arc of the ring, starting at 12 o'clock and running clockwise for `share` of the circle.

    internal static string Preview(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = Regex.Replace(raw.Trim(), @"^(#{1,6}\s*|[-*+]\s+|\d+\.\s+|>\s*)", "");
            line = Regex.Replace(line, @"\[(.*?)\]\(.*?\)", "$1");
            line = Regex.Replace(line, @"\*\*|__|`", "");
            if (line.Length == 0) continue;
            string start = raw.TrimStart();
            lines.Add(start.StartsWith("-") || start.StartsWith("*") || start.StartsWith("+") ? "·  " + line : line);
            if (lines.Count >= 10) break;
        }
        return lines.Count == 0 ? "此版本没有附带更新说明。" : string.Join("\n", lines);
    }

    internal TextBlock Text(string? text) => new() { Text = text ?? string.Empty, Style = Style("GlassPresentationTextStyle") };

    internal TextBlock Note(string text, double size) {
        var note = Text(text);
        note.FontSize = size;
        note.TextWrapping = TextWrapping.Wrap;
        note.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        return note;
    }

    internal StackPanel Section(string title, string description) {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var heading = Text(title);
        heading.FontSize = 16;
        heading.FontWeight = FontWeights.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        if (description.Length > 0) {
            var note = Note(description, 12);
            note.Margin = new Thickness(0, 3, 0, 0);
            header.Children.Add(note);
        }
        return header;
    }

    internal static GlassSurface Plate(UIElement child, Thickness margin)
    {
        var plate = new GlassSurface { Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 16, 20, 18), Margin = margin, BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        plate.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return plate;
    }

    internal static GlassSurface Card(UIElement child, GlassPageLayout.CardRole role, GlassPageLayout.Mode context = GlassPageLayout.Mode.Cards) {
        GlassSurface plate = Plate(child, new Thickness(0, 0, 0, GlassPageLayout.CardGap(role, context)));
        GlassPageLayout.SetCardFrame(plate, role);
        return plate;
    }

    internal static GlassSurface FormPlate(UIElement child) => Card(child, GlassPageLayout.CardRole.Java);

    private (bool HighContrast, bool Dark, bool Material, double Density, Color? Accent, Color? OnAccent)? _lastPalette;
    private bool _blendPalette;

    internal static Color DefaultAccent(bool dark) => dark ? Color.FromRgb(230, 230, 230) : Color.FromRgb(43, 43, 43);

    internal void ApplyPalette()
    {
        if (SystemParameters.HighContrast)
        {
            var contrastKey = (true, false, false, 0d, (Color?)null, (Color?)null);
            if (_lastPalette == contrastKey) return;
            _lastPalette = contrastKey;
            _blendPalette = false;
            // Read native colors directly: the legacy theme owns/shadows the public
            // SystemColors resource keys in Application.Resources.
            SetBrush("GlassPresentationText", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationSecondary", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationFill", SystemColors.WindowColor);
            SetBrush("GlassPresentationField", SystemColors.WindowColor);
            SetBrush("GlassPresentationHover", SystemColors.HighlightColor);
            SetBrush("GlassPresentationSelected", SystemColors.HighlightColor);
            SetBrush("GlassPresentationEdge", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationInputEdge", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationFocusEdge", SystemColors.HighlightColor);
            SetBrush("GlassPresentationPressed", SystemColors.ControlDarkColor);
            SetBrush("GlassPresentationPopup", SystemColors.WindowColor);
            SetBrush("GlassPresentationSeparator", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationTrack", SystemColors.ControlDarkColor);
            SetBrush("GlassPresentationThumbFill", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationAccent", SystemColors.HighlightColor);
            SetBrush("GlassPresentationOnAccent", SystemColors.HighlightTextColor);
            SetBrush("GlassPresentationDanger", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationFieldFocused", SystemColors.WindowColor);
            SetBrush("GlassPresentationControlStroke", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationAccentStroke", SystemColors.HighlightTextColor);
            SetBrush("GlassPresentationDockFill", SystemColors.WindowColor);
            SetBrush("GlassPresentationDockEdge", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationDockBadgeFill", SystemColors.WindowColor);
            SetBrush("GlassPresentationDialogFill", SystemColors.WindowColor);
            SetBrush("GlassPresentationDialogEdge", SystemColors.WindowTextColor);
            SetBrush("GlassPresentationWarnFill", SystemColors.WindowColor);
            SetBrush("GlassPresentationWarnEdge", SystemColors.HighlightColor);
            SetBrush("GlassPresentationWarnText", SystemColors.WindowTextColor);
            paletteChanged?.Invoke(root.Resources);
            return;
        }
        Brush? inherited = root.TryFindResource("GlassTextBrush") as Brush;
        Brush? theme = (source ?? root).TryFindResource("ColorBrush1") as Brush;
        Color sourceText = ThemeTransition.PaletteTarget(theme) ?? Color.FromRgb(32, 43, 58);
        Color text = ThemeTransition.PaletteTarget(inherited) ?? (sourceText.R + sourceText.G + sourceText.B > 480 ? Color.FromRgb(232, 238, 245) : Color.FromRgb(32, 43, 58));
        bool dark = (text.R + text.G + text.B) > 480;
        bool material = GlassMaterial.GetIsEnabled(root) &&
            (GlassMaterial.GetWallpaperSource(root) is not null || GlassMaterial.GetDesktopBackdropEnabled(root) || (GlassMaterial.GetAlphaFallback(root) && GlassMaterial.AllowsTransparency(root)));
        double density = material ? GlassMaterial.GetOpacityMultiplier(root) : 1;
        Color? accentColor = ThemeTransition.PaletteTarget(root.TryFindResource("GlassAccentBrush") as Brush);
        Color? onAccentColor = ThemeTransition.PaletteTarget(root.TryFindResource("GlassAccentTextBrush") as Brush);
        var paletteKey = (false, dark, material, density, accentColor, onAccentColor);
        if (_lastPalette == paletteKey) return;
        _blendPalette = _lastPalette is { HighContrast: false } old && old.Dark != dark &&
            root.IsLoaded && root.IsVisible && ThemeTransition.CanBlendPalette;
        _lastPalette = paletteKey;

        byte Alpha(int value) => material ? (byte)Math.Clamp((int)Math.Round(value * density), 0, 255) : (byte)255;
        // A touch lighter than the shell's main surface so cards and setting groups read as separate panes even on the
        // default (non-wallpaper) background, where both sit on the same dark acrylic.
        Color fill = dark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(250, 250, 251);
        // The shell owns frost. These ordinary borders reveal that layer rather than
        // repainting an almost-opaque card; density is applied once, never to text.
        // Over a frosted wallpaper the card still needs enough body for text to read comfortably.
        fill.A = Alpha(170);
        SetBrush("GlassPresentationText", dark ? Color.FromRgb(244, 244, 244) : Color.FromRgb(36, 36, 36));
        SetBrush("GlassPresentationSecondary", dark ? Color.FromRgb(184, 184, 184) : GlassCommandChrome.LightSecondaryTextColor);
        SetBrush("GlassPresentationFill", fill);
        // Message dialogs: a denser frosted sheet with a lit top edge. Warnings use the same sheet tinted red.
        SetBrush("GlassPresentationDialogFill", dark ? Color.FromArgb(206, 34, 34, 37) : Color.FromArgb(214, 250, 250, 251));
        SetGradient("GlassPresentationDialogEdge", dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(200, 255, 255, 255), dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        SetBrush("GlassPresentationWarnFill", dark ? Color.FromArgb(214, 54, 24, 27) : Color.FromArgb(222, 253, 237, 237));
        SetGradient("GlassPresentationWarnEdge", dark ? Color.FromArgb(120, 255, 110, 105) : Color.FromArgb(120, 226, 84, 80), dark ? Color.FromArgb(40, 255, 110, 105) : Color.FromArgb(70, 200, 60, 60));
        SetBrush("GlassPresentationWarnText", dark ? Color.FromRgb(255, 133, 126) : Color.FromRgb(196, 48, 46));
        // Controls have their own neutral filled surface, distinct from the section card.
        // The field is intentionally not another white/blue outlined card.
        // Fluent control ramp: rest / hover / pressed, plus a recessed surface while a text field has focus.
        if (material)
        {
            // Over a wallpaper, controls are translucent tints of the frosted card beneath them (a see-through glass
            // layer) rather than opaque grey slabs. The card already carries the blur, so fields add none of their own.
            // Dark mode recesses controls with a translucent black: a white tint lifted bright wallpapers to mid grey,
            // where the light label lost its contrast.
            SetBrush("GlassPresentationField", dark ? Color.FromArgb(72, 0, 0, 0) : Color.FromArgb(140, 255, 255, 255));
            SetBrush("GlassPresentationHover", dark ? Color.FromArgb(52, 0, 0, 0) : Color.FromArgb(185, 255, 255, 255));
            SetBrush("GlassPresentationPressed", dark ? Color.FromArgb(96, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255));
            SetBrush("GlassPresentationFieldFocused", dark ? Color.FromArgb(90, 16, 16, 16) : Color.FromArgb(215, 255, 255, 255));
        }
        else
        {
            SetBrush("GlassPresentationField", dark ? Color.FromRgb(56, 56, 56) : Colors.White);
            SetBrush("GlassPresentationHover", dark ? Color.FromRgb(64, 64, 64) : Color.FromRgb(249, 249, 249));
            SetBrush("GlassPresentationPressed", dark ? Color.FromRgb(50, 50, 50) : Color.FromRgb(245, 245, 245));
            SetBrush("GlassPresentationFieldFocused", dark ? Color.FromRgb(30, 30, 30) : Colors.White);
        }
        SetBrush("GlassPresentationTrack", dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(46, 0, 0, 0));
        // Elevation strokes: the lit edge sits on top in dark mode and the shadowed edge at the bottom in light mode.
        SetGradient("GlassPresentationControlStroke", dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0), dark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(42, 0, 0, 0));
        SetGradient("GlassPresentationAccentStroke", Color.FromArgb(36, 255, 255, 255), Color.FromArgb(60, 0, 0, 0));
        SetBand("GlassPresentationHoverBand", dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0));
        SetBrush("GlassPresentationInputEdge", Colors.Transparent);
        SetBrush("GlassPresentationFocusEdge", dark ? Color.FromArgb(100, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0));
        // Selection is an accent tint (system blue), never a second hue; strokes stay neutral like Fluent card/control strokes.
        SetBrush("GlassPresentationSelected", dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0));
        SetBrush("GlassPresentationEdge", dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        SetBrush("GlassPresentationPopup", dark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(247, 247, 248));
        SetBrush("GlassPresentationSeparator", dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));
        SetBrush("GlassPresentationThumbFill", dark ? Color.FromRgb(232, 238, 245) : Colors.White);
        Color effectiveAccent = accentColor ?? DefaultAccent(dark);
        SetBrush("GlassPresentationAccent", effectiveAccent);
        double luminance = (.2126 * effectiveAccent.R + .7152 * effectiveAccent.G + .0722 * effectiveAccent.B) / 255;
        SetBrush("GlassPresentationOnAccent", onAccentColor ?? (luminance > .55 ? Color.FromRgb(26, 26, 26) : Colors.White));
        SetBrush("GlassPresentationDanger", Color.FromRgb(198, 82, 81));
        // Action tones: a tinted glass fill and edge per warning level (danger = destructive, caution = rewrites data).
        SetBrush("GlassPresentationPrimaryFill", dark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(200, 255, 255, 255));
        SetBrush("GlassPresentationPrimaryEdge", dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        SetBrush("GlassPresentationDangerText", dark ? Color.FromRgb(255, 138, 128) : Color.FromRgb(196, 43, 28));
        SetBrush("GlassPresentationDangerFill", dark ? Color.FromArgb(30, 255, 69, 58) : Color.FromArgb(20, 255, 59, 48));
        SetBrush("GlassPresentationDangerEdge", dark ? Color.FromArgb(60, 255, 105, 97) : Color.FromArgb(54, 215, 0, 21));
        SetBrush("GlassPresentationCautionText", dark ? Color.FromRgb(255, 204, 102) : Color.FromRgb(158, 98, 0));
        SetBrush("GlassPresentationCautionFill", dark ? Color.FromArgb(26, 255, 179, 64) : Color.FromArgb(22, 255, 159, 10));
        SetBrush("GlassPresentationCautionEdge", dark ? Color.FromArgb(54, 255, 196, 92) : Color.FromArgb(50, 201, 125, 0));
        // Floating command docks (multi-select & keybinding capture) hover over cards and rows with denser frost and a crisp rim.
        Color dockFill = fill;
        dockFill.A = (byte)Math.Clamp(Math.Round((dark ? 124 : 154) * density), 0, 255);
        SetBrush("GlassPresentationDockFill", dockFill);
        SetGradient("GlassPresentationDockEdge", dark ? Color.FromArgb(84, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0), dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(56, 0, 0, 0));
        Color badgeTint = effectiveAccent;
        SetBrush("GlassPresentationDockBadgeFill", Color.FromArgb(dark ? (byte)28 : (byte)18, badgeTint.R, badgeTint.G, badgeTint.B));
        paletteChanged?.Invoke(root.Resources);
    }
    // Horizontal band: clear at the left and right ends, full strength across the middle.
    private void SetBand(string key, Color color)
    {
        if (root.Resources[key] is LinearGradientBrush { GradientStops.Count: 4 } old && old.GradientStops[1].Color == color) return;
        Color clear = Color.FromArgb(0, color.R, color.G, color.B);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(clear, 0)); brush.GradientStops.Add(new GradientStop(color, .14));
        brush.GradientStops.Add(new GradientStop(color, .86)); brush.GradientStops.Add(new GradientStop(clear, 1));
        brush.Freeze();
        root.Resources[key] = brush;
    }
    private void SetGradient(string key, Color top, Color bottom)
    {
        if (root.Resources[key] is LinearGradientBrush { GradientStops.Count: 3 } old && old.GradientStops[0].Color == top && old.GradientStops[2].Color == bottom) return;
        // Hold the top color until the last few pixels so only the bottom edge reads as the shadowed side.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        brush.GradientStops.Add(new GradientStop(top, 0)); brush.GradientStops.Add(new GradientStop(top, .9)); brush.GradientStops.Add(new GradientStop(bottom, 1));
        brush.Freeze();
        root.Resources[key] = brush;
    }
    private void SetBrush(string key, Color color)
    {
        var old = root.Resources[key] as SolidColorBrush;
        var next = ThemeTransition.BlendPaletteBrush(old, color, _blendPalette);
        if (!ReferenceEquals(old, next)) root.Resources[key] = next;
    }
}
