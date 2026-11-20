using PCLLF.Presentation.Foundation;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyIconButton.xaml(.vb); original theme and event ordering are retained.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyIconButton.xaml.cs: structure reference only.
public partial class MyIconButtonView : Border {
    public delegate void ClickEventHandler(object sender, EventArgs e);
    public event ClickEventHandler? Click;
    protected virtual void OnClick(object sender, EventArgs e) => Click?.Invoke(sender, e);
    public int Uuid;
    private bool isMouseDown;
    private int suppressAnimation;
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;

    public MyIconButtonView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyIconButtonView).Assembly) InitializeComponent();
        else if (!_contentLoaded) {
            _contentLoaded = true;
            CompiledControlLoader.Load(this, typeof(MyIconButtonView), "controls/myiconbuttonview.xaml");
        }
        MouseLeftButtonUp += Button_MouseUp;
        MouseLeftButtonDown += Button_MouseDown;
        MouseLeftButtonUp += (_, _) => ReleaseMouse();
        MouseLeave += (_, _) => LeaveMouse();
        MouseEnter += (_, _) => RefreshAnim();
        MouseLeave += (_, _) => RefreshAnim();
        Loaded += (_, _) => RefreshAnim();
        IsEnabledChanged += (_, _) => RefreshAnim();
    
        if (IsInitialized) LegacyThemePalette.Watch(this, RefreshAnim);
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, RefreshAnim);
    }
    public string Logo {
        get => Path.Data!.ToString();
        set => Path.Data = (Geometry)new GeometryConverter().ConvertFromString(value)!;
    }
    private double logoScale = 1;
    public double LogoScale {
        get => logoScale;
        set { logoScale = value; if (Path is not null) Path.RenderTransform = new ScaleTransform(LogoScale, LogoScale); }
    }
    public enum Themes { Color, White, Black, Red, Custom }
    public Themes Theme { get; set; } = Themes.Color;
    private SolidColorBrush foreground = LegacyThemePalette.Brush("ColorBrushGray3");
    public SolidColorBrush Foreground {
        get => foreground;
        set { foreground = value; WithAnimationSuppressed(RefreshAnim); }
    }
    protected virtual void WithAnimationSuppressed(Action work) { suppressAnimation++; work(); suppressAnimation--; }
    public bool CanClick { get; set; } = true;
    public void PerformClick() {
        if (!IsEnabled || !CanClick) return;
        Services.Info("按下图标按钮" + (string.IsNullOrEmpty(Name) ? "" : "：" + Name));
        // Existing download actions consume the same routed payload as the pointer path.
        // Passing this payload to the semantic callback does not inject or route mouse input.
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
            RoutedEvent = MouseLeftButtonUpEvent, Source = this
        };
        OnClick(this, args);
        args.Handled = true;
        Services.RaiseCustomEvent(this);
    }
    private void Button_MouseUp(object sender, MouseButtonEventArgs args) {
        if (!CanClick || !isMouseDown) return;
        Services.Info("按下图标按钮" + (string.IsNullOrEmpty(Name) ? "" : "：" + Name));
        OnClick(sender, args);
        args.Handled = true;
        ReleaseMouse();
        Services.RaiseCustomEvent(this);
    }
    private void Button_MouseDown(object sender, MouseButtonEventArgs args) {
        if (!CanClick) return;
        isMouseDown = true;
        Focus();
        Animations.Start(Animations.ScaleTransform(PanBack, 0.8 - ((ScaleTransform)PanBack.RenderTransform).ScaleX,
            new(400, ease: ControlEase.OutFluent, power: ControlEasePower.Strong)), "MyIconButton Scale " + Uuid);
    }
    private void ReleaseMouse() {
        if (!CanClick) return;
        if (isMouseDown) {
            isMouseDown = false;
            Animations.Start(new[] {
                Animations.ScaleTransform(PanBack, 1.05 - ((ScaleTransform)PanBack.RenderTransform).ScaleX,
                    new(250, ease: ControlEase.OutBack, power: ControlEasePower.Weak)),
                Animations.ScaleTransform(PanBack, -0.05, new(250, ease: ControlEase.OutFluent, power: ControlEasePower.Strong))
            }, "MyIconButton Scale " + Uuid);
        }
        RefreshAnim();
    }
    private void LeaveMouse() {
        if (!CanClick) return;
        isMouseDown = false;
        Animations.Start(Animations.ScaleTransform(PanBack, 1 - ((ScaleTransform)PanBack.RenderTransform).ScaleX,
            new(250, ease: ControlEase.OutFluent)), "MyIconButton Scale " + Uuid);
        RefreshAnim();
    }
    private ControlColor Custom(double alpha) => new(alpha, Foreground.Color.R, Foreground.Color.G, Foreground.Color.B);
    public void RefreshAnim() {
        try {
            if (IsLoaded && Services.AnimationEnabled() && suppressAnimation == 0) {
                PanBack.Background ??= new ControlColor(0, 255, 255, 255).ToBrush();
                if (Path.Fill is null) {
                    switch (Theme) {
                        case Themes.White: Path.SetResourceReference(Shape.FillProperty, "ColorBrushTitleForeground"); break;
                        case Themes.Red: Path.Fill = new ControlColor(160, 255, 76, 76).ToBrush(); break;
                        case Themes.Black: Path.Fill = LegacyThemePalette.Color("ColorBrushGray1").WithAlpha(160).ToBrush(); break;
                        case Themes.Custom: Path.Fill = Custom(160).ToBrush(); break;
                    }
                }
                List<ControlAnimation> animations = new();
                if (!IsEnabled) animations.Add(Animations.ColorResource(Path, Shape.FillProperty, "ColorBrushGray5", new(120)));
                else if (IsMouseOver) {
                    switch (Theme) {
                        case Themes.Color: animations.Add(Animations.ColorResource(Path, Shape.FillProperty, "ColorBrush2", new(120))); break;
                        case Themes.White: animations.Add(Animations.ColorTo(PanBack, BackgroundProperty, new(50, 255, 255, 255), new(120))); break;
                        case Themes.Red: animations.Add(Animations.ColorTo(Path, Shape.FillProperty, new(255, 76, 76), new(120))); break;
                        case Themes.Black: animations.Add(Animations.ColorTo(Path, Shape.FillProperty, LegacyThemePalette.Color("ColorBrushGray1").WithAlpha(230), new(120))); break;
                        case Themes.Custom: animations.Add(Animations.ColorTo(Path, Shape.FillProperty, Custom(255), new(120))); break;
                    }
                } else {
                    switch (Theme) {
                        case Themes.Color:
                            animations.Add(Animations.ColorResource(Path, Shape.FillProperty, "ColorBrush4", new(150)));
                            PanBack.Background = new ControlColor(0, 255, 255, 255).ToBrush(); break;
                        case Themes.White:
                            animations.Add(Animations.ColorResource(Path, Shape.FillProperty, "ColorBrushTitleForeground", new(150)));
                            animations.Add(Animations.ColorTo(PanBack, BackgroundProperty, new(0, 255, 255, 255), new(150))); break;
                        case Themes.Red:
                            animations.Add(Animations.ColorTo(Path, Shape.FillProperty, new(160, 255, 76, 76), new(150)));
                            PanBack.Background = new ControlColor(0, 255, 255, 255).ToBrush(); break;
                        case Themes.Black:
                            animations.Add(Animations.ColorTo(Path, Shape.FillProperty, LegacyThemePalette.Color("ColorBrushGray1").WithAlpha(160), new(150)));
                            PanBack.Background = new ControlColor(0, 255, 255, 255).ToBrush(); break;
                        case Themes.Custom:
                            animations.Add(Animations.ColorTo(Path, Shape.FillProperty, Custom(160), new(150)));
                            PanBack.Background = new ControlColor(0, 255, 255, 255).ToBrush(); break;
                    }
                }
                Animations.Start(animations, "MyIconButton Color " + Uuid);
            } else {
                Animations.Stop("MyIconButton Color " + Uuid);
                if (IsEnabled) {
                    switch (Theme) {
                        case Themes.Color: Path.SetResourceReference(Shape.FillProperty, "ColorBrush5"); break;
                        case Themes.White: Path.SetResourceReference(Shape.FillProperty, "ColorBrushTitleForeground"); break;
                        case Themes.Red: Path.Fill = new ControlColor(160, 255, 76, 76).ToBrush(); break;
                        case Themes.Black: Path.Fill = LegacyThemePalette.Color("ColorBrushGray1").WithAlpha(160).ToBrush(); break;
                        case Themes.Custom: Path.Fill = Custom(160).ToBrush(); break;
                    }
                } else Path.SetResourceReference(Shape.FillProperty, "ColorBrushGray5");
                PanBack.Background = new ControlColor(0, 255, 255, 255).ToBrush();
            }
        } catch (Exception error) { Services.Warn(error, "刷新图标按钮动画状态出错"); }
    }
    public void DisposeAnimated(bool removeFromChildren, Action<object>? callback = null) {
        if (!IsHitTestVisible) return;
        IsHitTestVisible = false;
        Animations.Start(new[] {
            Animations.ScaleTransform(this, -1.5, new(200, ease: ControlEase.InFluent)),
            Animations.Code(() => {
                if (removeFromChildren) {
                    if (Parent is not Panel parent) throw new MissingMemberException(Parent?.GetType().FullName, "Children");
                    parent.Children.Remove(this);
                } else Visibility = Visibility.Collapsed;
                callback?.Invoke(this);
            }, after: true)
        }, "MyIconButton Dispose " + Uuid);
    }
}
