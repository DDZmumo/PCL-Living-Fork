// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCLLF.Wpf.Glass;
using PathIcon = System.Windows.Shapes.Path;

namespace PCL;

[ContentProperty(nameof(Children))]
public sealed class LauncherMarkupCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(LauncherMarkupCard),
        new FrameworkPropertyMetadata("", Update));
    public static readonly DependencyProperty CanSwapProperty = DependencyProperty.Register(nameof(CanSwap), typeof(bool), typeof(LauncherMarkupCard),
        new FrameworkPropertyMetadata(false, Update));
    public static readonly DependencyProperty IsSwappedProperty = DependencyProperty.Register(nameof(IsSwapped), typeof(bool), typeof(LauncherMarkupCard),
        new FrameworkPropertyMetadata(false, Update));

    // Authored MyCard children share a Grid, so margins and Z indices can overlay artwork and labels.
    private readonly Grid _body = new();
    private readonly TextBlock _heading = new() { FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(15, 12, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Expander _expander;
    private readonly Grid _layout = new();
    private readonly GlassSurface _surface;
    private TextBlock? _helpHeading;
    private bool _pressed;

    public LauncherMarkupCard()
    {
        _expander = new Expander { Style = GlassDialogControls.Style("GlassAuthoredCardExpander") };
        _expander.Expanded += (sender, args) => { if (ReferenceEquals(sender, args.Source)) SetCurrentValue(IsSwappedProperty, false); };
        _expander.Collapsed += (sender, args) => { if (ReferenceEquals(sender, args.Source)) SetCurrentValue(IsSwappedProperty, true); };
        _expander.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnExpanderHeaderClick));
        _surface = GlassPageResources.Plate(_layout, new Thickness());
        // In an authored card the edge lies behind the content; margins are measured from the outside.
        var edge = _surface.BorderThickness;
        _layout.Margin = new Thickness(-edge.Left, -edge.Top, -edge.Right, -edge.Bottom);
        Content = _surface;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        MouseLeftButtonDown += (_, args) => _pressed = !CanSwap && (IsSwapped || IsOverLegacyHeader(args));
        MouseLeftButtonUp += (_, _) => { if (_pressed) { _pressed = false; this.RaiseCustomEvent(); } };
        MouseLeave += (_, _) => _pressed = false;
        Refresh();
    }

    public UIElementCollection Children => _body.Children;
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool CanSwap { get => (bool)GetValue(CanSwapProperty); set => SetValue(CanSwapProperty, value); }
    public bool IsSwapped { get => (bool)GetValue(IsSwappedProperty); set => SetValue(IsSwappedProperty, value); }

    internal void ApplyHelpCardLayout()
    {
        if (!CanSwap || Content is not GlassSurface surface) return;
        _layout.Margin = new Thickness();
        _expander.Style = GlassDialogControls.Style("GlassPresentationExpander");
        GlassPageLayout.SetCardFrame(surface, GlassPageLayout.CardRole.Expander);
        surface.Margin = new Thickness(0, 0, 0, GlassPageLayout.CardGap(GlassPageLayout.CardRole.Expander));
        _helpHeading = new TextBlock { Text = Title, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        _expander.Header = _helpHeading;
    }

    private void OnExpanderHeaderClick(object sender, RoutedEventArgs args)
    {
        if (_expander.Template?.FindName("HeaderToggle", _expander) is ToggleButton header &&
            ReferenceEquals(args.OriginalSource, header)) this.RaiseCustomEvent();
    }

    private bool IsOverLegacyHeader(MouseButtonEventArgs args)
    {
        double y = args.GetPosition(this).Y;
        // Match the click area of the old 40-DIP card header, which excluded its bottom 6 DIPs.
        return y <= 34 && (y != 0 || IsMouseDirectlyOver);
    }

    private static void Update(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((LauncherMarkupCard)target).Refresh();
    private void Refresh()
    {
        _heading.Text = Title;
        _heading.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
        if (_helpHeading is null) _expander.Header = Title;
        else _helpHeading.Text = Title;
        if (CanSwap && !_layout.Children.Contains(_expander)) {
            _layout.Children.Clear();
            _expander.Content = _body;
            _layout.Children.Add(_expander);
            if (_helpHeading is not null) GlassPageLayout.SetCardFrame(_surface, GlassPageLayout.CardRole.Expander);
        }
        else if (!CanSwap && !_layout.Children.Contains(_body)) {
            _expander.Content = null;
            _layout.Children.Clear();
            _layout.Children.Add(_heading);
            _layout.Children.Add(_body);
            _surface.Padding = new Thickness();
            _surface.CornerRadius = new CornerRadius(5);
        }
        if (CanSwap) _expander.IsExpanded = !IsSwapped;
    }
}

public class LauncherMarkupButton : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LauncherMarkupButton),
        new FrameworkPropertyMetadata("", RefreshLabel));
    public static readonly DependencyProperty LogoProperty = DependencyProperty.Register(nameof(Logo), typeof(string), typeof(LauncherMarkupButton),
        new FrameworkPropertyMetadata("", RefreshLabel));
    public static readonly DependencyProperty LogoScaleProperty = DependencyProperty.Register(nameof(LogoScale), typeof(double), typeof(LauncherMarkupButton),
        new FrameworkPropertyMetadata(1d, RefreshLabel));
    public static readonly DependencyProperty ColorTypeProperty = DependencyProperty.Register(nameof(ColorType), typeof(string), typeof(LauncherMarkupButton),
        new FrameworkPropertyMetadata("", (target, _) => ((LauncherMarkupButton)target).RefreshStyle()));

    public LauncherMarkupButton() => RefreshStyle();

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Logo { get => (string)GetValue(LogoProperty); set => SetValue(LogoProperty, value); }
    public double LogoScale { get => (double)GetValue(LogoScaleProperty); set => SetValue(LogoScaleProperty, value); }
    public string ColorType { get => (string)GetValue(ColorTypeProperty); set => SetValue(ColorTypeProperty, value); }
    public string? Theme { get; set; }
    public CustomEvent.EventType EventType { get => CustomEventService.GetEventType(this); set => CustomEventService.SetEventType(this, value); }
    public string EventData { get => CustomEventService.GetEventData(this); set => CustomEventService.SetEventData(this, value); }

    protected virtual string? StyleKey => ColorType is "Highlight" or "Accent" ? "GlassPresentationAccentButton" : "GlassPresentationButton";
    private void RefreshStyle()
    {
        if (StyleKey is { } key) SetResourceReference(StyleProperty, key);
    }
    private static void RefreshLabel(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((LauncherMarkupButton)target).BuildLabel();

    protected virtual void BuildLabel()
    {
        if (BuildIcon() is not { } icon) { SetCurrentValue(ContentProperty, Text); return; }
        if (string.IsNullOrEmpty(Text)) { SetCurrentValue(ContentProperty, icon); return; }
        var label = new TextBlock { Text = Text, Margin = new Thickness(7, 0, 0, this is LauncherMarkupIconTextButton ? 1 : 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        SetCurrentValue(ContentProperty, new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } });
    }

    protected FrameworkElement? BuildIcon(double size = 16)
    {
        if (string.IsNullOrEmpty(Logo)) return null;
        var scale = new ScaleTransform(Math.Clamp(LogoScale, .25, 4), Math.Clamp(LogoScale, .25, 4));
        if (Logo.StartsWith("M", StringComparison.OrdinalIgnoreCase) || Logo.StartsWith("F", StringComparison.OrdinalIgnoreCase) && Logo.Contains(' ')) {
            try {
                var path = new PathIcon { Width = size, Height = size, Data = Geometry.Parse(Logo), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                path.RenderTransformOrigin = new Point(.5, .5);
                path.RenderTransform = scale;
                path.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
                return path;
            }
            catch (FormatException) { }
        }
        return new LauncherMarkupImage { Width = size, Height = size, Source = Logo, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale };
    }

    protected override void OnClick()
    {
        base.OnClick();
        this.RaiseCustomEvent();
    }
}

// Theme styles remain underneath authored styles, including styles which only set text and commands.
public sealed class LauncherMarkupTextButton : LauncherMarkupButton
{
    static LauncherMarkupTextButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LauncherMarkupTextButton),
        new FrameworkPropertyMetadata(typeof(LauncherMarkupTextButton)));
    protected override string? StyleKey => null;
}

public sealed class LauncherMarkupIconTextButton : LauncherMarkupButton
{
    static LauncherMarkupIconTextButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LauncherMarkupIconTextButton),
        new FrameworkPropertyMetadata(typeof(LauncherMarkupIconTextButton)));
    protected override string? StyleKey => null;
}

public sealed class LauncherMarkupListItem : LauncherMarkupButton
{
    static LauncherMarkupListItem() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LauncherMarkupListItem),
        new FrameworkPropertyMetadata(typeof(LauncherMarkupListItem)));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(LauncherMarkupListItem),
        new FrameworkPropertyMetadata("", UpdateLabel));
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(nameof(Info), typeof(string), typeof(LauncherMarkupListItem),
        new FrameworkPropertyMetadata("", UpdateLabel));
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };

    public LauncherMarkupListItem() => BuildLabel();
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Info { get => (string)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public string? Type { get; set; }
    public bool IsScaleAnimationEnabled { get; set; } = true;
    public UIElementCollection Buttons => _actions.Children;
    protected override string? StyleKey => null;

    private static void UpdateLabel(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((LauncherMarkupListItem)target).BuildLabel();
    protected override void BuildLabel()
    {
        if (_actions.Parent is Panel previous) previous.Children.Remove(_actions);
        var row = new DockPanel { Children = { _actions } };
        DockPanel.SetDock(_actions, Dock.Right);
        if (BuildIcon(32) is { } icon) { icon.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(icon); }
        var detail = new TextBlock { Text = Info, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12,
            Visibility = string.IsNullOrEmpty(Info) ? Visibility.Collapsed : Visibility.Visible };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
            new TextBlock { Text = Title, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }, detail } });
        Content = row;
    }
}

public sealed class LauncherMarkupNotice : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LauncherMarkupNotice),
        new FrameworkPropertyMetadata("", Update));
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(nameof(Theme), typeof(string), typeof(LauncherMarkupNotice),
        new FrameworkPropertyMetadata("Blue", Update));
    public static readonly DependencyProperty IsWarnProperty = DependencyProperty.Register(nameof(IsWarn), typeof(bool), typeof(LauncherMarkupNotice),
        new FrameworkPropertyMetadata(false, Update));
    public LauncherMarkupNotice()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        PreviewMouseLeftButtonUp += (_, _) => this.RaiseCustomEvent();
        Refresh();
    }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Theme { get => (string)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public bool IsWarn { get => (bool)GetValue(IsWarnProperty); set => SetValue(IsWarnProperty, value); }
    public CustomEvent.EventType EventType { get => CustomEventService.GetEventType(this); set => CustomEventService.SetEventType(this, value); }
    public string EventData { get => CustomEventService.GetEventData(this); set => CustomEventService.SetEventData(this, value); }
    private static void Update(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((LauncherMarkupNotice)target).Refresh();
    private void Refresh() => Content = GlassDialogControls.Notice(Text,
        Theme == "Red" ? DialogTone.Error : IsWarn || Theme == "Yellow" ? DialogTone.Warning : DialogTone.Information);
}

public sealed class LauncherMarkupImage : Image
{
    public new static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(LauncherMarkupImage),
        new FrameworkPropertyMetadata("", Update));
    public static readonly DependencyProperty FallbackSourceProperty = DependencyProperty.Register(nameof(FallbackSource), typeof(string),
        typeof(LauncherMarkupImage), new FrameworkPropertyMetadata(null, Update));
    public static readonly DependencyProperty LoadingSourceProperty = DependencyProperty.Register(nameof(LoadingSource), typeof(string),
        typeof(LauncherMarkupImage), new FrameworkPropertyMetadata("pack://application:,,,/images/Icons/NoIcon.png", Update));
    public static readonly DependencyProperty EnableCacheProperty = DependencyProperty.Register(nameof(EnableCache), typeof(bool),
        typeof(LauncherMarkupImage), new FrameworkPropertyMetadata(true, Update));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double),
        typeof(LauncherMarkupImage), new FrameworkPropertyMetadata(0d, (target, _) => ((LauncherMarkupImage)target).UpdateClip()),
        value => value is double radius && double.IsFinite(radius) && radius >= 0);
    private CancellationTokenSource? _loading;
    private RectangleGeometry? _roundedClip;
    public LauncherMarkupImage()
    {
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => _loading?.Cancel();
        SizeChanged += (_, _) => UpdateClip();
    }
    public new string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string? FallbackSource { get => (string?)GetValue(FallbackSourceProperty); set => SetValue(FallbackSourceProperty, value); }
    public string? LoadingSource { get => (string?)GetValue(LoadingSourceProperty); set => SetValue(LoadingSourceProperty, value); }
    public bool EnableCache { get => (bool)GetValue(EnableCacheProperty); set => SetValue(EnableCacheProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    private void UpdateClip()
    {
        if (CornerRadius <= 0) {
            if (_roundedClip is not null) { Clip = null; _roundedClip = null; }
            return;
        }
        var geometry = _roundedClip ??= new RectangleGeometry();
        geometry.Rect = new Rect(RenderSize);
        geometry.RadiusX = geometry.RadiusY = Math.Min(CornerRadius, Math.Min(ActualWidth, ActualHeight) / 2);
        if (!ReferenceEquals(Clip, geometry)) Clip = geometry;
    }

    private static void Update(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var image = (LauncherMarkupImage)target;
        if (image.IsLoaded) image.Load();
    }

    private async void Load()
    {
        _loading?.Cancel();
        using var request = new CancellationTokenSource();
        _loading = request;
        try {
            string? source = Source, fallback = FallbackSource;
            if (string.IsNullOrWhiteSpace(source)) { base.Source = null; return; }
            if (ReadEmbedded(source) is { } embedded) { base.Source = embedded; return; }
            if (!LauncherMarkupImages.IsRemote(source) && !LauncherMarkupImages.IsRemote(fallback)) {
                base.Source = ReadEmbedded(fallback);
                return;
            }
            string? path = EnableCache && LauncherMarkupImages.IsRemote(source) ? LauncherMarkupImages.CachePath(ModBase.PathTemp, source) : null;
            (BitmapSource? Image, bool Fresh) cache = path is null ? default : await LauncherMarkupImages.ReadCacheAsync(path, request.Token);
            request.Token.ThrowIfCancellationRequested();
            base.Source = cache.Image ?? ReadEmbedded(LoadingSource);
            if (cache.Fresh) return;

            ImageSource? downloaded = await LauncherMarkupImages.DownloadAsync(source, fallback, path, request.Token);
            request.Token.ThrowIfCancellationRequested();
            base.Source = downloaded ?? cache.Image ?? ReadEmbedded(fallback) ?? base.Source;
        } catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) {
            MeloongCore.Logger.Warn("无法加载自定义内容的图片（" + error.GetType().Name + "）");
        } finally {
            if (ReferenceEquals(_loading, request)) _loading = null;
        }
    }

    internal static string? EmbeddedImageAddress(string address)
    {
        string normalized = MyBitmap.NormalizeResourceUri(address);
        string prefix = ModBase.PathImage;
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string resource = Uri.UnescapeDataString(normalized[prefix.Length..]);
        if (resource.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0 || Array.Exists(resource.Split('/'), part => part is "" or "." or ".."))
            return null;
        return normalized;
    }

    private static ImageSource? ReadEmbedded(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || EmbeddedImageAddress(address) is not { } resource) return null;
        try { return new MyBitmap(resource); }
        catch (Exception error) {
            MeloongCore.Logger.Warn("无法加载自定义内容的内置图片（" + error.GetType().Name + "）");
            return null;
        }
    }
}

public sealed class LauncherMarkupLoading : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LauncherMarkupLoading),
        new FrameworkPropertyMetadata("加载中"));

    public LauncherMarkupLoading()
    {
        MinWidth = MinHeight = 50;
        SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationAccent");
        var icon = new BlockCurveLoader { Width = 38, Height = 38, Margin = new Thickness(0, 4.5, 0, 4.5),
            HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetBinding(BlockCurveLoader.ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(TextElement.ForegroundProperty) });
        var label = new TextBlock { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(TextElement.ForegroundProperty) });
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Text)) { Source = this });
        Children.Add(icon);
        Children.Add(label);
    }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
