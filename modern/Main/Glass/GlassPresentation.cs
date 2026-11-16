// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PCLLF.Presentation.Controls;

namespace PCL;

/// <summary>Creates a new WPF presentation tree from live controller-facing page semantics.</summary>
public static class GlassPresentation
{
    public static GlassPresentationSession Create(FrameworkElement source) => new(source);
}

/// <summary>The source remains in its hidden backend tree; no source visual is hosted by View.</summary>
public sealed partial class GlassPresentationSession : IDisposable
{
    private static readonly DependencyProperty LifetimeProperty = DependencyProperty.RegisterAttached(
        "PresentationLifetime", typeof(Node), typeof(GlassPresentationSession));
    private readonly FrameworkElement _source;
    private readonly ContentControl _root;
    private readonly Dictionary<DependencyObject, Node> _nodes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
    private HashSet<DependencyObject> _alive;
    private bool _refreshing;
    private bool _disposed;
    public FrameworkElement View => _root;
    public IReadOnlyCollection<string> UnsupportedTypes => _unsupported;

    internal GlassPresentationSession(FrameworkElement source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Dispatcher.VerifyAccess();
        _source = source;
        _root = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Focusable = false };
        _root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LivingPCL;component/Glass/GlassPresentationResources.xaml", UriKind.Relative) });
        _root.Loaded += RootLoaded;
        _root.SizeChanged += RootSizeChanged;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || _refreshing) return;
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
        }
        finally { _alive = null; _refreshing = false; }
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
        _root.Content = null;
    }

    private void RootLoaded(object sender, RoutedEventArgs args) => Refresh();
    private void RootSizeChanged(object sender, SizeChangedEventArgs args) => Refresh();

    private bool Live(DependencyObject target)
    {
        if (_disposed) return false;
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
                if (node.View is FrameworkElement presentation)
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
        if (source is FrameworkElement { Name: "PanOfficialTheme" or "CardLogo" }) return true;
        if (source is FrameworkElement { Name: "ShapeLine" } decoration && AncestorMessage(decoration)) return true;
        if (source is Grid row && row.Children.OfType<DependencyObject>().Any(child =>
            (child is MyComboBox && SettingService.GetKey(child) == "UiShellStyle") ||
            (child is MySlider && SettingService.GetKey(child) == "UiLauncherTransparent"))) return true;
        return source is MyCheckBox checkbox && SettingService.GetKey(checkbox) is "UiLauncherLogo" or "UiBackgroundColorful";
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
        if (!double.IsNaN(source.Height) && source.Height > 0 && source is Image or System.Windows.Shapes.Shape or Canvas) target.Height = source.Height;
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
        if (source is MyCard) return new Thickness(0, 0, 0, SettingsContext(source) ? 18 : 16);
        if (source is StackPanel && margin.Left >= 20 && margin.Right >= 20) return new Thickness(0, Math.Min(12, margin.Top), 0, Math.Min(16, margin.Bottom));
        return new Thickness(Math.Min(24, Math.Max(0, margin.Left)), Math.Min(24, Math.Max(0, margin.Top)), Math.Min(24, Math.Max(0, margin.Right)), Math.Min(24, Math.Max(0, margin.Bottom)));
    }

    private static void Bind(DependencyObject target, DependencyProperty property, object source, string path, BindingMode mode = BindingMode.OneWay)
        => BindingOperations.SetBinding(target, property, new Binding(path) { Source = source, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
    private Style Style(string key) => (Style)_root.FindResource(key);
    private void TextStyle(TextBlock text) => text.Style = Style("GlassPresentationTextStyle");
    private static void Children(Panel panel, IReadOnlyList<UIElement> children)
    {
        for (int index = panel.Children.Count - 1; index >= 0; index--)
            if (!children.Contains(panel.Children[index])) panel.Children.RemoveAt(index);
        for (int index = 0; index < children.Count; index++)
        {
            UIElement child = children[index];
            if (panel.Children.IndexOf(child) == index) continue;
            if (panel.Children.Contains(child)) panel.Children.Remove(child);
            panel.Children.Insert(index, child);
        }
    }

    private void ApplyPalette()
    {
        Brush inherited = _root.TryFindResource("GlassTextBrush") as Brush;
        Brush theme = _source.TryFindResource("ColorBrush1") as Brush;
        Color sourceText = (theme as SolidColorBrush)?.Color ?? Color.FromRgb(32, 43, 58);
        Color text = (inherited as SolidColorBrush)?.Color ?? (sourceText.R + sourceText.G + sourceText.B > 480 ? Color.FromRgb(232, 238, 245) : Color.FromRgb(32, 43, 58));
        bool dark = (text.R + text.G + text.B) > 480;
        Color fill = dark ? Color.FromArgb(224, 22, 33, 48) : Color.FromArgb(223, 245, 246, 248);
        if (_root.TryFindResource("GlassCardBrush") is SolidColorBrush surfaceFill) fill = surfaceFill.Color;
        SetBrush("GlassPresentationText", Color.FromArgb(255, text.R, text.G, text.B));
        SetBrush("GlassPresentationSecondary", dark ? Color.FromRgb(162, 177, 196) : Color.FromRgb(105, 118, 133));
        SetBrush("GlassPresentationFill", fill);
        SetBrush("GlassPresentationField", dark ? Color.FromArgb(238, 30, 43, 60) : Color.FromArgb(239, 255, 255, 255));
        SetBrush("GlassPresentationHover", dark ? Color.FromArgb(245, 41, 58, 76) : Color.FromArgb(247, 255, 255, 255));
        SetBrush("GlassPresentationSelected", dark ? Color.FromArgb(90, 55, 125, 145) : Color.FromArgb(38, 55, 125, 145));
        SetBrush("GlassPresentationEdge", dark ? Color.FromArgb(58, 177, 196, 215) : Color.FromArgb(38, 53, 71, 91));
        SetBrush("GlassPresentationPopup", dark ? Color.FromRgb(22, 33, 48) : Color.FromRgb(245, 246, 248));
        SetBrush("GlassPresentationSeparator", dark ? Color.FromArgb(32, 194, 207, 222) : Color.FromArgb(22, 53, 71, 91));
    }
    private void SetBrush(string key, Color color)
    {
        if (_root.Resources[key] is SolidColorBrush old && old.Color == color) return;
        _root.Resources[key] = new SolidColorBrush(color);
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
