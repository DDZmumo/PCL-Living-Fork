using PCLLF.Presentation.Foundation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyListItem.xaml(.vb) is the state/event/layout source (repository LICENCE).
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyListItem.xaml.cs was compared only;
// CE's changed 20px/ScaleY selection marker and extra controls are deliberately not imported.
[ContentProperty(nameof(Inlines))]
public partial class MyListItemView : Grid {
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs e);
    public event ClickEventHandler? Click;
    public event ClickEventHandler? LogoClick;
    public event EventHandler<ControlPreviewEventArgs>? Check;
    public event EventHandler<ControlPreviewEventArgs>? Changed;
    protected virtual void OnClick(object sender, MouseButtonEventArgs args) => Click?.Invoke(sender, args);
    protected virtual void OnLogoClick(object sender, MouseButtonEventArgs args) => LogoClick?.Invoke(sender, args);
    protected virtual bool OnCheck(bool user) { ControlPreviewEventArgs args = new(user); Check?.Invoke(this, args); return args.Handled; }
    protected virtual bool OnChanged(bool user) { ControlPreviewEventArgs args = new(user); Changed?.Invoke(this, args); return args.Handled; }
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;

    public MyListItemView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyListItemView).Assembly) InitializeComponent();
        else if (!_contentLoaded) { _contentLoaded = true; CompiledControlLoader.Load(this, typeof(MyListItemView), "controls/mylistitemview.xaml"); }
        SizeChanged += (_, _) => OnItemSizeChanged();
        PreviewMouseLeftButtonUp += ButtonMouseUp;
        PreviewMouseLeftButtonDown += ButtonMouseDown;
        MouseLeave += ButtonMouseLeave;
        PreviewMouseLeftButtonUp += ButtonMouseLeave;
        MouseEnter += RefreshColor; MouseLeave += RefreshColor;
        PreviewMouseLeftButtonDown += RefreshColor; PreviewMouseLeftButtonUp += RefreshColor;
        Loaded += ItemLoaded;
    
        if (IsInitialized) LegacyThemePalette.Watch(this, () => RefreshColor(null, EventArgs.Empty));
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, () => RefreshColor(null, EventArgs.Empty));
    }
    protected virtual double WpfSize(double pixels) => pixels / (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1);
    protected virtual FrameworkElement CreateOnlineImage(string address) => throw new InvalidOperationException("The product image loader is required for online list-item logos.");
    protected virtual Brush ReadBitmap(string address) => throw new InvalidOperationException("The product bitmap adapter is required for file list-item logos.");
    protected virtual Brush ReadIdleBrush(bool hover) => (Brush)FindResource(hover ? "ColorBrushBg1" : "ColorBrush7");
    protected virtual bool HasCustomEvent(object sender) => false;
    protected virtual bool IsHelpEvent => false;
    protected virtual void PopulateHelpItem() => throw new InvalidOperationException("The product help adapter is required for a help list item.");
    protected virtual void ClearHelpEvent() => throw new InvalidOperationException("The product custom-event adapter is required to clear a help event.");
    protected virtual void ReportHelpError(Exception error) => Services.Error(error, "设置帮助 MyListItem 失败");
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    private static readonly Regex LineEndings = new(@"\r\n|[\n\r\f\u0085\u2028\u2029]", RegexOptions.Compiled);
    private static string SingleLine(string value) => LineEndings.Replace(value, "");
    private Border? rectBack;
    public Border RectBack {
        get {
            if (rectBack is null) {
                Border border = new() {
                    Name = "RectBack", CornerRadius = new CornerRadius(IsScaleAnimationEnabled || Height > 40 ? 6 : 0),
                    RenderTransform = IsScaleAnimationEnabled ? new ScaleTransform(0.8, 0.8) : null!,
                    RenderTransformOrigin = new Point(0.5, 0.5), BorderThickness = new Thickness(WpfSize(1)),
                    SnapsToDevicePixels = true, IsHitTestVisible = false, Opacity = 0
                };
                border.SetResourceReference(Border.BackgroundProperty, "ColorBrush7");
                border.SetResourceReference(Border.BorderBrushProperty, "ColorBrush6");
                SetColumnSpan(border, 999); SetRowSpan(border, 999); Children.Insert(0, border); rectBack = border;
            }
            return rectBack;
        }
    }
    public FrameworkElement? ButtonStack;
    public FrameworkElement? PathLogo;
    public Border? RectCheck;
    private TextBlock? labInfo;
    public TextBlock LabInfo {
        get {
            if (labInfo is null) {
                TextBlock lab = new() {
                    Name = "LabInfo", SnapsToDevicePixels = false, UseLayoutRounding = false, HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed, FontSize = 12,
                    Margin = new Thickness(4, 0, 0, 0), Opacity = 0.6
                };
                SetColumn(lab, 3); SetRow(lab, 2); Children.Add(lab); labInfo = lab;
            }
            return labInfo;
        }
    }
    public int Uuid;
    private bool isScaleAnimationEnabled = true;
    public bool IsScaleAnimationEnabled {
        get => isScaleAnimationEnabled;
        set { isScaleAnimationEnabled = value; if (rectBack is not null) RectBack.CornerRadius = new CornerRadius(value ? 6 : 0); }
    }
    public int PaddingLeft { get => Convert.ToInt32(Math.Round(ColumnPaddingLeft.Width.Value)); set => ColumnPaddingLeft.Width = new GridLength(value); }
    public int MinPaddingRight { get; set; } = 4;
    private IEnumerable<MyIconButtonView>? buttons;
    public IEnumerable<MyIconButtonView> Buttons {
        get => buttons!;
        set {
            buttons = value;
            if (ButtonStack is not null) { Children.Remove(ButtonStack); ButtonStack = null; }
            switch (value.Count()) {
                case 0: break;
                case 1:
                    foreach (MyIconButtonView button in value) {
                        if (button.Height.Equals(double.NaN)) button.Height = 25;
                        if (button.Width.Equals(double.NaN)) button.Width = 25;
                        button.Opacity = 0; button.Margin = new Thickness(0, 0, 5, 0); button.SnapsToDevicePixels = false;
                        button.HorizontalAlignment = HorizontalAlignment.Right; button.VerticalAlignment = VerticalAlignment.Center;
                        button.SnapsToDevicePixels = false; button.UseLayoutRounding = false;
                        SetColumnSpan(button, 10); SetRowSpan(button, 10); Children.Add(button); ButtonStack = button;
                    }
                    break;
                default:
                    ButtonStack = new StackPanel { Opacity = 0, Margin = new Thickness(0, 0, 5, 0), SnapsToDevicePixels = false,
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = false };
                    SetColumnSpan(ButtonStack, 10); SetRowSpan(ButtonStack, 10);
                    foreach (MyIconButtonView button in value) {
                        if (button.Height.Equals(double.NaN)) button.Height = 25;
                        if (button.Width.Equals(double.NaN)) button.Width = 25;
                        ((StackPanel)ButtonStack).Children.Add(button);
                    }
                    Children.Add(ButtonStack); break;
            }
        }
    }
    public InlineCollection Inlines => LabTitle.Inlines;
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, SingleLine(value)); }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(MyListItemView));
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(MyListItemView), new PropertyMetadata(14.0));
    private string info = "";
    public string Info {
        get => info;
        set { if (Same(info, value)) return; value = SingleLine(value); info = value; LabInfo.Text = value; LabInfo.Visibility = Same(value, "") ? Visibility.Collapsed : Visibility.Visible; }
    }
    private string logo = "";
    public string Logo {
        get => logo;
        set {
            if (Same(logo, value)) return; logo = value;
            if (PathLogo is not null) Children.Remove(PathLogo);
            if (!Same(logo, "")) {
                if (logo.StartsWith("http", StringComparison.OrdinalIgnoreCase)) {
                    PathLogo = CreateOnlineImage(logo); PathLogo.Tag = this; PathLogo.IsHitTestVisible = LogoClickable;
                    PathLogo.RenderTransformOrigin = new Point(0.5, 0.5); PathLogo.RenderTransform = new ScaleTransform(LogoScale, LogoScale);
                    PathLogo.SnapsToDevicePixels = true; PathLogo.UseLayoutRounding = false;
                    RenderOptions.SetBitmapScalingMode(PathLogo, BitmapScalingMode.Linear);
                } else if (IsBitmapLogo()) {
                    PathLogo = new Canvas { Tag = this, IsHitTestVisible = LogoClickable, Background = ReadBitmap(logo),
                        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(LogoScale, LogoScale),
                        SnapsToDevicePixels = true, UseLayoutRounding = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                    RenderOptions.SetBitmapScalingMode(PathLogo, BitmapScalingMode.Linear);
                } else {
                    PathLogo = new Path { Tag = this, IsHitTestVisible = LogoClickable, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform, Data = (Geometry)new GeometryConverter().ConvertFromString(logo)!,
                        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(LogoScale, LogoScale), SnapsToDevicePixels = false, UseLayoutRounding = false };
                    PathLogo.SetBinding(Path.FillProperty, new Binding(nameof(Foreground)) { Source = this });
                }
                SetColumn(PathLogo, 2); SetRowSpan(PathLogo, 4); OnItemSizeChanged(); Children.Add(PathLogo);
                if (LogoClickable) {
                    PathLogo.MouseLeave += (_, _) => isLogoDown = false;
                    PathLogo.MouseLeftButtonDown += (_, _) => isLogoDown = true;
                    PathLogo.MouseLeftButtonUp += (sender, args) => { if (isLogoDown) { isLogoDown = false; OnLogoClick(((FrameworkElement)sender).Tag, args); } };
                }
            }
            ColumnLogo.Width = new GridLength((Same(logo, "") ? 0 : 34) + (Height < 40 ? 0 : 4));
        }
    }
    private bool IsBitmapLogo() => (logo?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ?? false) || (logo?.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ?? false) || (logo?.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ?? false) || (logo?.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ?? false);
    private double logoScale = 1;
    public double LogoScale { get => logoScale; set { logoScale = value; if (PathLogo is not null) PathLogo.RenderTransform = new ScaleTransform(LogoScale, LogoScale); } }
    public bool LogoClickable { get; set; }
    private bool isLogoDown;
    public enum CheckType { None, Clickable, RadioBox, CheckBox }
    private CheckType type = CheckType.None;
    public CheckType Type {
        get => type;
        set {
            if (type == value) return; type = value;
            ColumnCheck.Width = new GridLength(type is CheckType.None or CheckType.Clickable ? (Height < 40 ? 4 : 2) : 6);
            if (type is CheckType.None or CheckType.Clickable) {
                if (RectCheck is not null) { Children.Remove(RectCheck); RectCheck = null; }
                SetChecked(false, false, false);
            } else if (RectCheck is null) {
                RectCheck = new Border { Width = 5, Height = Checked ? double.NaN : 0, CornerRadius = new CornerRadius(2),
                    VerticalAlignment = Checked ? VerticalAlignment.Stretch : VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
                    UseLayoutRounding = false, SnapsToDevicePixels = false, Margin = Checked ? new Thickness(-1, 6, 0, 6) : new Thickness(-1, 0, 0, 0) };
                RectCheck.SetResourceReference(Border.BackgroundProperty, "ColorBrush3"); SetRowSpan(RectCheck, 4); Children.Add(RectCheck);
            }
        }
    }
    private void OnItemSizeChanged() {
        ColumnCheck.Width = new GridLength(type is CheckType.None or CheckType.Clickable ? (Height < 40 ? 4 : 2) : 6);
        ColumnLogo.Width = new GridLength((Same(logo, "") ? 0 : 34) + (Height < 40 ? 0 : 4));
        if (PathLogo is not null) PathLogo.Margin = IsBitmapLogo() ? new Thickness(4, 5, 3, 5) : new Thickness(Height < 40 ? 6 : 8, 8, Height < 40 ? 4 : 6, 8);
        LabTitle.Margin = new Thickness(4, 0, 0, Height < 40 ? 0 : 2);
    }
    private bool isChecked;
    public bool Checked { get => isChecked; set => SetChecked(value, false, value != isChecked); }
    public void SetChecked(bool value, bool user, bool anime) {
        try {
            bool rawValue = isChecked;
            if (Type == CheckType.RadioBox) {
                if (IsInitialized && value != isChecked) {
                    isChecked = value;
                    if (OnChanged(user)) { isChecked = rawValue; return; }
                }
                isChecked = value;
            } else {
                if (value == isChecked) return; isChecked = value;
                if (IsInitialized && OnChanged(user)) { isChecked = rawValue; return; }
            }
            if (value && OnCheck(user)) return;
            if (Type == CheckType.RadioBox) {
                if (Parent is null) return;
                if (Parent is not Panel panel) throw new MissingMemberException(Parent.GetType().FullName, "Children");
                List<MyListItemView> radios = new(); int checkedCount = 0;
                foreach (UIElement child in panel.Children) {
                    FrameworkElement control = MyVirtualizingElementView.TryInit((FrameworkElement)child);
                    if (control is MyListItemView radio && radio.Type == CheckType.RadioBox) { radios.Add(radio); if (radio.Checked) checkedCount++; }
                }
                switch (checkedCount) {
                    case 0: radios[0].Checked = true; break;
                    case > 1:
                        if (Checked) {
                            foreach (MyListItemView radio in radios) if (radio.Checked && !radio.Equals(this)) radio.Checked = false;
                        } else {
                            bool firstChecked = false;
                            foreach (MyListItemView radio in radios) if (radio.Checked) { if (firstChecked) radio.Checked = false; else firstChecked = true; }
                        }
                        break;
                }
            }
            if (IsLoaded && Services.AnimationEnabled() && anime) {
                List<ControlAnimation> animations = new();
                if (Checked) {
                    if (RectCheck is not null) {
                        double delta = ActualHeight - RectCheck.ActualHeight - 12;
                        animations.Add(Animations.Height(RectCheck, delta * 0.4, new(200, ease: ControlEase.OutFluent, power: ControlEasePower.Weak)));
                        animations.Add(Animations.Height(RectCheck, delta * 0.6, new(300, ease: ControlEase.OutBack, power: ControlEasePower.Weak)));
                        animations.Add(Animations.Opacity(RectCheck, 1 - RectCheck.Opacity, new(30)));
                        RectCheck.VerticalAlignment = VerticalAlignment.Center; RectCheck.Margin = new Thickness(-1, 0, 0, 0);
                    }
                    animations.Add(Animations.ColorResource(this, ForegroundProperty, Height < 40 ? "ColorBrush3" : "ColorBrush2", new(200)));
                } else {
                    if (RectCheck is not null) {
                        animations.Add(Animations.Height(RectCheck, -RectCheck.ActualHeight, new(120, ease: ControlEase.InFluent, power: ControlEasePower.Weak)));
                        animations.Add(Animations.Opacity(RectCheck, -RectCheck.Opacity, new(70, 40)));
                        RectCheck.VerticalAlignment = VerticalAlignment.Center;
                    }
                    animations.Add(Animations.ColorResource(this, ForegroundProperty, "ColorBrush1", new(120)));
                }
                Animations.Start(animations, "MyListItem Checked " + Uuid);
            } else {
                Animations.Stop("MyListItem Checked " + Uuid);
                if (Checked) {
                    if (RectCheck is not null) { RectCheck.Height = double.NaN; RectCheck.Margin = new Thickness(-1, 6, 0, 6); RectCheck.Opacity = 1; RectCheck.VerticalAlignment = VerticalAlignment.Stretch; }
                    SetResourceReference(ForegroundProperty, Height < 40 ? "ColorBrush3" : "ColorBrush2");
                } else {
                    if (RectCheck is not null) { RectCheck.Height = 0; RectCheck.Margin = new Thickness(-1, 0, 0, 0); RectCheck.Opacity = 0; RectCheck.VerticalAlignment = VerticalAlignment.Center; }
                    SetResourceReference(ForegroundProperty, "ColorBrush1");
                }
            }
        } catch (Exception error) { Services.Warn(error, "设置 Checked 失败"); }
    }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(MyListItemView), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(52, 61, 74))));
    public Action<MyListItemView, EventArgs>? ContentHandler;
    protected virtual bool HasContentHandler => ContentHandler is not null;
    protected virtual void InvokeContentHandler(EventArgs args) => ContentHandler!(this, args);
    protected virtual void ClearContentHandler() => ContentHandler = null;
    private bool isMouseDown;
    private void ButtonMouseUp(object sender, MouseButtonEventArgs args) {
        if (!isMouseDown) return;
        ActivateItem(args);
    }
    // A keyboard/proxy activation shares the real click pipeline without synthesizing mouse input.
    // Its legacy navigation Click subscribers receive null because there is no pointer event.
    protected void ActivateItem(MouseButtonEventArgs? args = null) {
        if (!IsEnabled || Type == CheckType.None) return;
        OnClick(this, args!);
        if (args?.Handled == true) return;
        if (HasCustomEvent(this)) {
            Services.RaiseCustomEvent(this);
            if (args is not null) args.Handled = true;
            return;
        }
        switch (Type) {
            case CheckType.Clickable: Services.Info("按下单击列表项：" + Title); break;
            case CheckType.RadioBox: Services.Info("按下单选列表项：" + Title); if (!Checked) SetChecked(true, true, true); break;
            case CheckType.CheckBox: Services.Info("按下复选列表项（" + (!Checked ? "True" : "False") + "）：" + Title); SetChecked(!Checked, true, true); break;
        }
    }
    private void ButtonMouseDown(object sender, MouseButtonEventArgs args) {
        if (IsMouseDirectlyOver && Type != CheckType.None) { isMouseDown = true; if (ButtonStack is not null) ButtonStack.IsHitTestVisible = false; }
    }
    private void ButtonMouseLeave(object sender, EventArgs args) { isMouseDown = false; if (ButtonStack is not null) ButtonStack.IsHitTestVisible = true; }
    private string? stateLast;
    public bool IsMouseOverAnimationEnabled = true;
    public void RefreshColor(object? sender, EventArgs args) {
        if (HasContentHandler) { InvokeContentHandler(args); ClearContentHandler(); }
        string stateNew; int time;
        if (isMouseDown && !(Type == CheckType.RadioBox && Checked)) { stateNew = "MouseDown"; time = 120; }
        else if (IsMouseOver && IsMouseOverAnimationEnabled) { stateNew = "MouseOver"; time = 120; }
        else { stateNew = "Idle"; time = 180; }
        if (Same(stateLast, stateNew)) return; stateLast = stateNew;
        if (IsLoaded && Services.AnimationEnabled()) {
            List<ControlAnimation> animations = new();
            if (IsMouseOver && IsMouseOverAnimationEnabled) {
                if (ButtonStack is not null) {
                    animations.Add(Animations.Opacity(ButtonStack, 1 - ButtonStack.Opacity, new(time * 0.7, time * 0.3)));
                    animations.Add(Animations.Double(delta => ColumnPaddingRight.Width = new GridLength(Math.Max(0, ColumnPaddingRight.Width.Value + delta)),
                        Math.Max(MinPaddingRight, 5 + Buttons.Count() * 25) - ColumnPaddingRight.Width.Value, new(time * 0.3, time * 0.7)));
                }
                animations.Add(Animations.ColorResource(RectBack, Border.BackgroundProperty, isMouseDown ? "ColorBrush6" : "ColorBrushBg1", new(time)));
                animations.Add(Animations.Opacity(RectBack, 1 - RectBack.Opacity, new(time, ease: ControlEase.OutFluent)));
                if (IsScaleAnimationEnabled) {
                    animations.Add(Animations.ScaleTransform(RectBack, 1 - ((ScaleTransform)RectBack.RenderTransform).ScaleX, new(time * 1.6, ease: ControlEase.OutFluent)));
                    animations.Add(Animations.ScaleTransform(this, (isMouseDown ? 0.98 : 1) - ((ScaleTransform)RenderTransform).ScaleX,
                        new(time * (isMouseDown ? 0.9 : 1.2), ease: ControlEase.OutFluent)));
                }
            } else {
                if (ButtonStack is not null) {
                    animations.Add(Animations.Opacity(ButtonStack, -ButtonStack.Opacity, new(time * 0.4)));
                    animations.Add(Animations.Double(delta => ColumnPaddingRight.Width = new GridLength(Math.Max(0, ColumnPaddingRight.Width.Value + delta)),
                        MinPaddingRight - ColumnPaddingRight.Width.Value, new(time * 0.4)));
                }
                animations.Add(Animations.Opacity(RectBack, -RectBack.Opacity, new(time)));
                if (IsScaleAnimationEnabled) {
                    animations.Add(Animations.ColorResource(RectBack, Border.BackgroundProperty, isMouseDown ? "ColorBrush6" : "ColorBrush7", new(time)));
                    animations.Add(Animations.ScaleTransform(this, 1 - ((ScaleTransform)RenderTransform).ScaleX, new(time * 3, ease: ControlEase.OutFluent)));
                    animations.Add(Animations.ScaleTransform(RectBack, 0.996 - ((ScaleTransform)RectBack.RenderTransform).ScaleX, new(time, ease: ControlEase.OutFluent)));
                    animations.Add(Animations.ScaleTransform(RectBack, -0.246, new(1, after: true)));
                }
            }
            Animations.Start(animations, "ListItem Color " + Uuid);
        } else {
            if (IsMouseOver && IsMouseOverAnimationEnabled) {
                if (ButtonStack is not null) { ButtonStack.Opacity = 1; ColumnPaddingRight.Width = new GridLength(Math.Max(MinPaddingRight, 5 + Buttons.Count() * 25)); }
                RectBack.Background = ReadIdleBrush(true); RectBack.Opacity = 1; RectBack.RenderTransform = new ScaleTransform(1, 1); RenderTransform = new ScaleTransform(1, 1);
            } else {
                if (ButtonStack is not null) { ButtonStack.Opacity = 0; ColumnPaddingRight.Width = new GridLength(MinPaddingRight); }
                RenderTransform = new ScaleTransform(1, 1);
                if (rectBack is not null) {
                    if (IsScaleAnimationEnabled) RectBack.RenderTransform = new ScaleTransform(0.75, 0.75);
                    RectBack.Background = ReadIdleBrush(false); RectBack.Opacity = 0;
                }
            }
            Animations.Stop("ListItem Color " + Uuid);
        }
    }
    private void ItemLoaded(object sender, RoutedEventArgs args) {
        SetResourceReference(ForegroundProperty, Checked ? (Height < 40 ? "ColorBrush3" : "ColorBrush2") : "ColorBrush1");
        ColumnPaddingRight.Width = new GridLength(MinPaddingRight);
        if (IsHelpEvent && !(!Same(Title, "") && !Same(Info, ""))) {
            try { PopulateHelpItem(); }
            catch (Exception error) { ReportHelpError(error); ClearHelpEvent(); }
        }
    }
    public override string ToString() => Title;
}
