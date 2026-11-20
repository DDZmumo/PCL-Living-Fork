// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PCLLF.Presentation.Controls;
using PCLLF.Wpf.Glass;

namespace PCL;

/// <summary>Creates a new WPF presentation tree from live controller-facing page semantics.</summary>
public static class GlassPresentation
{
    public static GlassPresentationSession Create(FrameworkElement source) => new(source, null);
    internal static GlassPresentationSession Create(FrameworkElement source, Func<MyCard, UIElement> cardSupplement, bool trackPageAppearance = false) =>
        new(source, cardSupplement, trackPageAppearance);
}

/// <summary>The source remains in its hidden backend tree; no source visual is hosted by View.</summary>
public sealed partial class GlassPresentationSession : IDisposable
{
    [ThreadStatic]
    private static ResourceDictionary _sharedResources;

    private static ResourceDictionary GetSharedResources() =>
        _sharedResources ??= new ResourceDictionary { Source = new Uri("/LivingPCL;component/Glass/GlassPresentationResources.xaml", UriKind.Relative) };

    internal static Style ShellScrollStyle => (Style)GetSharedResources()["GlassPresentationScrollViewer"];

    private static readonly DependencyProperty LifetimeProperty = DependencyProperty.RegisterAttached(
        "PresentationLifetime", typeof(Node), typeof(GlassPresentationSession));
    private readonly FrameworkElement _source;
    private readonly ContentControl _root;
    private readonly PageAppearance _pageAppearance;
    private readonly Dictionary<DependencyObject, Node> _nodes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
    private HashSet<DependencyObject> _alive;
    private bool _refreshing;
    private bool _disposed;
    private readonly HashSet<StackPanel> _responsiveStacks = new(ReferenceEqualityComparer.Instance);
    private (bool HighContrast, bool Dark, bool Material, double Density, Color? Accent, Color? OnAccent)? _lastPalette;
    private bool _blendPalette;
    public FrameworkElement View => _root;
    /// <summary>Optional host-owned row placed first in a projected card (shell-only settings that have no legacy control).</summary>
    internal Func<MyCard, UIElement> CardSupplement { get; set; }
    public IReadOnlyCollection<string> UnsupportedTypes => _unsupported;

    internal GlassPresentationSession(FrameworkElement source, Func<MyCard, UIElement> cardSupplement = null, bool trackPageAppearance = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Dispatcher.VerifyAccess();
        _source = source;
        if (trackPageAppearance) _pageAppearance = new PageAppearance();
        CardSupplement = cardSupplement;
        _root = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Focusable = false };
        _root.Resources.MergedDictionaries.Add(GetSharedResources());
        _root.Loaded += RootLoaded;
        _root.SizeChanged += RootSizeChanged;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || _refreshing || _pageAppearance?.IsHeld == true) return;
        _source.Dispatcher.VerifyAccess();
        _refreshing = true;
        try
        {
            _alive = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            ApplyPalette();
            UIElement next = Project(_source);
            if (!ReferenceEquals(_root.Content, next)) _root.Content = next;
            foreach (DependencyObject removed in _nodes.Keys.Where(key => !_alive.Contains(key)).ToArray())
            {
                _nodes[removed].Dispose();
                _nodes.Remove(removed);
            }
            _pageAppearance?.DiscardDetachedBindings();
        }
        finally { _alive = null; _refreshing = false; }
    }

    internal void HoldPageAppearance() { if (_pageAppearance is not null) _pageAppearance.IsHeld = true; }

    internal void ResumePageAppearance()
    {
        if (_pageAppearance?.IsHeld != true) return;
        _pageAppearance.IsHeld = false;
        _pageAppearance.UpdateTargets();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _source.Dispatcher.VerifyAccess();
        _disposed = true;
        _root.Loaded -= RootLoaded;
        _root.SizeChanged -= RootSizeChanged;
        foreach (Node node in _nodes.Values) node.Dispose();
        _nodes.Clear();
        _responsiveStacks.Clear();
        _root.Content = null;
    }

    private void RootLoaded(object sender, RoutedEventArgs args) => ApplyPalette();
    private void RootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged || _responsiveStacks.Count == 0) return;
        double width = args.NewSize.Width > 12 ? Math.Min(800, args.NewSize.Width - 12) : double.NaN;
        foreach (StackPanel stack in _responsiveStacks)
            stack.Width = width;
    }

    private bool Live(DependencyObject target)
    {
        if (_disposed || _pageAppearance?.IsHeld == true) return false;
        for (DependencyObject item = target; item != null; item = item is Visual ? VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item.GetValue(LifetimeProperty) is Node { IsDisposed: true }) return false;
        return true;
    }

    private static bool SettingsContext(DependencyObject source)
    {
        for (DependencyObject item = source; item != null; item = LogicalTreeHelper.GetParent(item))
        {
            string name = item.GetType().Name;
            if (name.StartsWith("PageSetup", StringComparison.Ordinal) || name is "PageInstanceSetup" or "PageInstanceOverall") return true;
        }
        return false;
    }

    private UIElement Project(DependencyObject source, bool cardContent = false)
    {
        if (source is null || Excluded(source)) return null;
        if (_alive != null && !_alive.Add(source)) return _nodes.TryGetValue(source, out Node repeated) ? repeated.View : null;
        if (!_nodes.TryGetValue(source, out Node node))
        {
            node = CreateNode(source);
            _nodes.Add(source, node);
            if (source is FrameworkElement original)
            {
                ApplyMetadata(original, node.View);
                if (node.View is FrameworkElement presentation && LazyLoadBehavior.HasViewportAction(original))
                {
                    IDisposable viewport = LazyLoadBehavior.BindPresentationViewport(original, presentation);
                    node.Cleanup.Add(viewport.Dispose);
                }
            }
        }
        node.CardContent = cardContent;
        node.Pull?.Invoke();
        if (source is FrameworkElement element && node.View is FrameworkElement target)
        {
            target.Margin = cardContent ? new Thickness() : Spacing(element);
            if (ReferenceEquals(source, _source)) target.Margin = new Thickness();
            if (!double.IsPositiveInfinity(element.MaxHeight)) target.MaxHeight = element.MaxHeight;
            if (element is ModpackDialogView) target.Height = !double.IsNaN(element.Height) && element.Height > 0 ? element.Height : double.NaN;
            if (element.ReadLocalValue(UIElement.IsHitTestVisibleProperty) is bool permission && element is not MyButtonView and not MyIconButtonView and not MyTextButtonView and not MyIconTextButtonView)
                target.SetCurrentValue(UIElement.IsHitTestVisibleProperty, permission);
            if (!ReferenceEquals(node.MenuSource, element.ContextMenu))
            {
                if (target.ContextMenu != null) target.ContextMenu.IsOpen = false;
                (target.ContextMenu as IDisposable)?.Dispose();
                node.MenuSource = element.ContextMenu;
                target.ContextMenu = element.ContextMenu is null ? null : ProjectContextMenu(element.ContextMenu, target);
            }
        }
        return node.View;
    }

    private static bool Excluded(DependencyObject source)
    {
        // The home page shows the original homepage content (PanCustom: preset, local or online cards) and the launch log
        // card as glass cards; its bottom-left overlay (dependency checks, last played world) lives in the home panel.
        if (source is FrameworkElement { Name: "PanMoreSettings" } && LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(source)) is Grid { Parent: PageLaunchRight }) return true;
        if (GlassClientUiPolicy.Excludes(source)) return true;
        if (source is FrameworkElement { Name: "PanOfficialTheme" or "CardLogo" }) return true;
        // Offline skins are managed in the skin library (account page) now; the original offline-skin card is retired.
        if (source is FrameworkElement { Name: "CardSkin" } && InPage<PageSetupLaunch>(source)) return true;
        if (source is FrameworkElement { Name: "ShapeLine" } decoration && AncestorMessage(decoration)) return true;
        if (source is Grid row && row.Children.OfType<DependencyObject>().Any(child =>
            (child is MyComboBox && SettingService.GetKey(child) is "UiShellStyle" or "UiBackgroundMode") ||
            child is MyButton { Name: "BtnBackgroundOpen" } ||
            (child is MySlider && SettingService.GetKey(child) == "UiLauncherTransparent"))) return true;
        return source is MyCheckBox checkbox && SettingService.GetKey(checkbox) is "UiLauncherLogo" or "UiBackgroundColorful";
    }

    private static bool InPage<TPage>(DependencyObject element) where TPage : class =>
        SourceAncestor<TPage>(element) is not null;

    private static T? SourceAncestor<T>(DependencyObject element) where T : class
    {
        for (DependencyObject? parent = element; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static bool AncestorMessage(FrameworkElement element)
    {
        for (DependencyObject parent = element; parent != null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent.GetType().Name.StartsWith("MyMsg", StringComparison.Ordinal)) return true;
        return false;
    }

    private void ApplyMetadata(FrameworkElement source, UIElement visual)
    {
        if (visual is not FrameworkElement target) return;
        target.Name = source.Name;
        Bind(target, UIElement.VisibilityProperty, source, nameof(UIElement.Visibility));
        // Original pages swap their own sub-panels (e.g. the install page's version list ↔ install options) by fading one
        // out and the other in while both stay Visible. Mirroring the panel's opacity keeps that sequence instead of
        // showing both panels on top of each other at full strength.
        if (source is Panel) Bind(target, UIElement.OpacityProperty, source, nameof(UIElement.Opacity));
        Bind(target, UIElement.IsEnabledProperty, source, nameof(UIElement.IsEnabled));
        Bind(target, FrameworkElement.DataContextProperty, source, nameof(FrameworkElement.DataContext));
        Bind(target, FrameworkElement.TagProperty, source, nameof(FrameworkElement.Tag));
        target.HorizontalAlignment = source.HorizontalAlignment;
        target.VerticalAlignment = source.VerticalAlignment;
        if (source.ReadLocalValue(UIElement.IsHitTestVisibleProperty) is bool permission && source is not MyButtonView and not MyIconButtonView and not MyTextButtonView and not MyIconTextButtonView)
            target.IsHitTestVisible = permission;
        if (!double.IsNaN(source.Width) && source.Width > 0) target.Width = source is MyIconButtonView ? Math.Max(38, source.Width) : source.Width;
        target.MinWidth = Math.Max(target.MinWidth, source.MinWidth);
        if (!double.IsPositiveInfinity(source.MaxWidth)) target.MaxWidth = source.MaxWidth;
        if (!double.IsPositiveInfinity(source.MaxHeight)) target.MaxHeight = source.MaxHeight;
        if (Grid.GetIsSharedSizeScope(source)) Grid.SetIsSharedSizeScope(target, true);
        if (source is ComboBox && SettingsContext(source) ||
            source is TextBox { AcceptsReturn: false } && double.IsNaN(source.Width) && SettingService.GetKey(source) is "LaunchArgumentTitle" or "LaunchArgumentInfo") {
            target.Width = 220;
            target.MaxWidth = Math.Min(target.MaxWidth, 220);
            target.MinWidth = Math.Min(target.MinWidth, 220);
            target.HorizontalAlignment = HorizontalAlignment.Right;
        }
        if (!double.IsNaN(source.Height) && source.Height > 0 && source is Image or System.Windows.Shapes.Shape or Canvas or ModpackDialogView or DataGrid) target.Height = source.Height;
        target.MinHeight = Math.Max(target.MinHeight, Math.Min(source.MinHeight, 64));
        target.ToolTip = source.ToolTip is string tip ? tip : (source.ToolTip as ToolTip)?.Content as string;
        Grid.SetRow(target, Grid.GetRow(source)); Grid.SetColumn(target, Grid.GetColumn(source));
        Grid.SetRowSpan(target, Grid.GetRowSpan(source)); Grid.SetColumnSpan(target, Grid.GetColumnSpan(source));
        DockPanel.SetDock(target, DockPanel.GetDock(source));
        Canvas.SetLeft(target, Canvas.GetLeft(source)); Canvas.SetTop(target, Canvas.GetTop(source));
        Canvas.SetRight(target, Canvas.GetRight(source)); Canvas.SetBottom(target, Canvas.GetBottom(source));
        Panel.SetZIndex(target, Panel.GetZIndex(source));
    }

    private static Thickness Spacing(FrameworkElement source)
    {
        Thickness margin = source.Margin;
        if (source is PCLCS.KeybindingPageRow) return new Thickness();
        if (IsFloatingCommandBar(source)) return new Thickness(20, 0, 20, 18);
        if (source is MyCard and not MySearchBox) return new Thickness(0, 0, 0, SettingsContext(source) ? 18 : 16);
        if (source is StackPanel && margin.Left >= 20 && margin.Right >= 20) return new Thickness(0, Math.Min(12, margin.Top), 0, Math.Min(16, margin.Bottom));
        return new Thickness(Math.Min(24, Math.Max(0, margin.Left)), Math.Min(24, Math.Max(0, margin.Top)), Math.Min(24, Math.Max(0, margin.Right)), Math.Min(24, Math.Max(0, margin.Bottom)));
    }

    private void Bind(DependencyObject target, DependencyProperty property, object source, string path, BindingMode mode = BindingMode.OneWay)
    {
        bool tracksAppearance = _pageAppearance is not null && mode == BindingMode.OneWay && source is UIElement &&
            (property == UIElement.VisibilityProperty || property == UIElement.OpacityProperty);
        var binding = new Binding(path) { Source = source, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        if (tracksAppearance) binding.Converter = _pageAppearance;
        BindingExpressionBase expression = BindingOperations.SetBinding(target, property, binding);
        if (tracksAppearance) _pageAppearance.Track(expression);
    }

    // The hidden native page still closes its own panels for loader state. Preserve the rendered
    // values of the outgoing projection from before that cleanup, while its glass samples stay live.
    private sealed class PageAppearance : IValueConverter
    {
        private readonly List<WeakReference<BindingExpressionBase>> _bindings = new();
        internal bool IsHeld { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => IsHeld ? Binding.DoNothing : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

        internal void Track(BindingExpressionBase binding) => _bindings.Add(new(binding));

        internal void DiscardDetachedBindings() =>
            _bindings.RemoveAll(item => !item.TryGetTarget(out BindingExpressionBase binding) || binding.Status == BindingStatus.Detached);

        internal void UpdateTargets()
        {
            DiscardDetachedBindings();
            foreach (var item in _bindings.ToArray())
                if (item.TryGetTarget(out BindingExpressionBase binding)) binding.UpdateTarget();
        }
    }
    private Style Style(string key) => (Style)_root.FindResource(key);
    private void TextStyle(TextBlock text) => text.Style = Style("GlassPresentationTextStyle");
    private static void Children(Panel panel, IReadOnlyList<UIElement> children)
    {
        UIElementCollection current = panel.Children;
        if (current.Count == children.Count)
        {
            int index = 0;
            while (index < current.Count && ReferenceEquals(current[index], children[index])) index++;
            if (index == current.Count) return;
        }
        var wanted = new HashSet<UIElement>(children, ReferenceEqualityComparer.Instance);
        for (int index = current.Count - 1; index >= 0; index--)
            if (!wanted.Contains(current[index])) current.RemoveAt(index);
        for (int index = 0; index < children.Count; index++)
        {
            UIElement child = children[index];
            if (index < current.Count && ReferenceEquals(current[index], child)) continue;
            int oldIndex = current.IndexOf(child);
            if (oldIndex >= 0) current.RemoveAt(oldIndex);
            current.Insert(index, child);
        }
    }

    internal void RefreshPalette()
    {
        if (!_disposed && _root.IsLoaded) ApplyPalette();
    }

    private void ApplyPalette()
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
            return;
        }
        Brush inherited = _root.TryFindResource("GlassTextBrush") as Brush;
        Brush theme = _source.TryFindResource("ColorBrush1") as Brush;
        Color sourceText = ThemeTransition.PaletteTarget(theme) ?? Color.FromRgb(32, 43, 58);
        Color text = ThemeTransition.PaletteTarget(inherited) ?? (sourceText.R + sourceText.G + sourceText.B > 480 ? Color.FromRgb(232, 238, 245) : Color.FromRgb(32, 43, 58));
        bool dark = (text.R + text.G + text.B) > 480;
        bool material = GlassMaterial.GetIsEnabled(_root) &&
            (GlassMaterial.GetWallpaperSource(_root) is not null || GlassMaterial.GetDesktopBackdropEnabled(_root));
        double density = material ? GlassMaterial.GetOpacityMultiplier(_root) : 1;
        Color? accentColor = ThemeTransition.PaletteTarget(_root.TryFindResource("GlassAccentBrush") as Brush);
        Color? onAccentColor = ThemeTransition.PaletteTarget(_root.TryFindResource("GlassAccentTextBrush") as Brush);
        var paletteKey = (false, dark, material, density, accentColor, onAccentColor);
        if (_lastPalette == paletteKey) return;
        _blendPalette = _lastPalette is { HighContrast: false } old && old.Dark != dark &&
            _root.IsLoaded && _root.IsVisible && ThemeTransition.CanBlendPalette;
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
        SetBrush("GlassPresentationSecondary", dark ? Color.FromRgb(184, 184, 184) : Color.FromRgb(105, 105, 105));
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
        if (accentColor is Color accent) SetBrush("GlassPresentationAccent", accent);
        // The label colour follows the accent's own lightness, so the neutral light accent always gets dark text even when
        // this presentation cannot see the shell's on-accent resource (white-on-light-grey was unreadable).
        // The presentation may be themed before it joins the shell tree, when the shell accent is not reachable yet;
        // then judge the label against the accent this presentation actually paints with.
        Color? effectiveAccent = accentColor ?? ThemeTransition.PaletteTarget(_root.TryFindResource("GlassPresentationAccent") as Brush);
        if (effectiveAccent is Color fillColor)
        {
            double luminance = (.2126 * fillColor.R + .7152 * fillColor.G + .0722 * fillColor.B) / 255;
            SetBrush("GlassPresentationOnAccent", luminance > .55 ? Color.FromRgb(26, 26, 26) : Colors.White);
        }
        else if (onAccentColor is Color onAccent) SetBrush("GlassPresentationOnAccent", onAccent);
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
        Color dockFill = dark ? Color.FromRgb(36, 38, 44) : Color.FromRgb(252, 253, 255);
        dockFill.A = Alpha(dark ? 222 : 234);
        SetBrush("GlassPresentationDockFill", dockFill);
        SetGradient("GlassPresentationDockEdge", dark ? Color.FromArgb(84, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0), dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(56, 0, 0, 0));
        Color badgeTint = effectiveAccent ?? (dark ? Colors.White : Colors.Black);
        SetBrush("GlassPresentationDockBadgeFill", Color.FromArgb(dark ? (byte)28 : (byte)18, badgeTint.R, badgeTint.G, badgeTint.B));
    }
    // Horizontal band: clear at the left and right ends, full strength across the middle.
    private void SetBand(string key, Color color)
    {
        if (_root.Resources[key] is LinearGradientBrush { GradientStops.Count: 4 } old && old.GradientStops[1].Color == color) return;
        Color clear = Color.FromArgb(0, color.R, color.G, color.B);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(clear, 0)); brush.GradientStops.Add(new GradientStop(color, .14));
        brush.GradientStops.Add(new GradientStop(color, .86)); brush.GradientStops.Add(new GradientStop(clear, 1));
        brush.Freeze();
        _root.Resources[key] = brush;
    }
    private void SetGradient(string key, Color top, Color bottom)
    {
        if (_root.Resources[key] is LinearGradientBrush { GradientStops.Count: 3 } old && old.GradientStops[0].Color == top && old.GradientStops[2].Color == bottom) return;
        // Hold the top color until the last few pixels so only the bottom edge reads as the shadowed side.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        brush.GradientStops.Add(new GradientStop(top, 0)); brush.GradientStops.Add(new GradientStop(top, .9)); brush.GradientStops.Add(new GradientStop(bottom, 1));
        brush.Freeze();
        _root.Resources[key] = brush;
    }
    private void SetBrush(string key, Color color)
    {
        var old = _root.Resources[key] as SolidColorBrush;
        var next = ThemeTransition.BlendPaletteBrush(old, color, _blendPalette);
        if (!ReferenceEquals(old, next)) _root.Resources[key] = next;
    }

    private sealed class Node : IDisposable
    {
        internal readonly UIElement View;
        internal Action Pull;
        internal bool CardContent;
        internal ContextMenu MenuSource;
        internal bool IsDisposed { get; private set; }
        internal readonly List<Action> Cleanup = new();
        internal Node(UIElement view) { View = view; view.SetValue(LifetimeProperty, this); }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            foreach (Action action in Cleanup) action();
            Cleanup.Clear();
            Retire(View, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance), this);
        }
        private static void Retire(DependencyObject target, HashSet<DependencyObject> visited, Node owner)
        {
            if (!visited.Add(target)) return;
            target.SetValue(LifetimeProperty, owner);
            if (target is FrameworkElement { ContextMenu: { } menu })
            {
                menu.IsOpen = false; Retire(menu, visited, owner); (menu as IDisposable)?.Dispose();
            }
            foreach (object child in LogicalTreeHelper.GetChildren(target))
                if (child is DependencyObject dependency) Retire(dependency, visited, owner);
            if (target is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(target); index++) Retire(VisualTreeHelper.GetChild(target, index), visited, owner);
            BindingOperations.ClearAllBindings(target);
            if (target is System.Windows.Controls.Primitives.ButtonBase button) button.Command = null;
            if (target is MenuItem item) item.Command = null;
            if (target is System.Windows.Documents.Hyperlink link) link.Command = null;
            if (target is UIElement element) element.IsEnabled = false;
            if (target is ContentElement content) content.IsEnabled = false;
        }
    }
}
