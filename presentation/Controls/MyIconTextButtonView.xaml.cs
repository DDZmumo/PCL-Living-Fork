using PCLLF.Presentation.Foundation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace PCLLF.Presentation.Controls;

// LF MyIconTextButton.xaml(.vb); fixed CE C# compared, retaining LF enum/DP/event policies.
[ContentProperty(nameof(Inlines))]
public partial class MyIconTextButtonView : Border {
    public int Uuid;
    public delegate void CheckEventHandler(object sender, bool raiseByMouse);
    private CheckEventHandler? check, change;
    public event CheckEventHandler Check { add => check += value; remove => check -= value; }
    public event CheckEventHandler Change { add => change += value; remove => change -= value; }
    public event EventHandler<ControlPreviewEventArgs>? Click;
    protected virtual void OnClick() => Click?.Invoke(this, new(true));
    public void PerformClick() {
        if (!IsEnabled) return;
        Services.Info("按下带图标按钮：" + Text);
        OnClick(); Services.RaiseCustomEvent(this);
    }
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    public MyIconTextButtonView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyIconTextButtonView).Assembly) InitializeComponent();
        else if (!_contentLoaded) { _contentLoaded = true; CompiledControlLoader.Load(this, typeof(MyIconTextButtonView), "controls/myicontextbuttonview.xaml"); }
        MouseLeftButtonUp += (_, _) => { if (!isMouseDown) return; Services.Info("按下带图标按钮：" + Text); isMouseDown = false; OnClick(); Services.RaiseCustomEvent(this); RefreshColor(); };
        MouseLeftButtonDown += (_, _) => { isMouseDown = true; RefreshColor(); }; MouseLeave += (_, _) => { isMouseDown = false; RefreshColor(); };
        MouseEnter += (_, _) => RefreshColor(); Loaded += (_, _) => RefreshColor(); IsEnabledChanged += (_, _) => RefreshColor();
    
        if (IsInitialized) LegacyThemePalette.Watch(this, () => RefreshColor());
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, () => RefreshColor());
    }
    public string Logo { get => ShapeLogo.Data!.ToString(); set => ShapeLogo.Data = (Geometry)new GeometryConverter().ConvertFromString(value)!; }
    private double logoScale = 1;
    public double LogoScale { get => logoScale; set { logoScale = value; if (ShapeLogo is not null) ShapeLogo.RenderTransform = new ScaleTransform(LogoScale, LogoScale); } }
    public InlineCollection Inlines => LabText.Inlines;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MyIconTextButtonView), new PropertyMetadata((sender, args) => { if (sender is not null) ((MyIconTextButtonView)sender).LabText.Text = (string)args.NewValue; }));
    public enum ColorState { Black, Highlight }
    public ColorState ColorType { get => ReadColorType(); set { if (ColorType == value) return; WriteColorType(value); RefreshColor(); } }
    public static readonly DependencyProperty ColorTypeProperty = DependencyProperty.Register(nameof(ColorType), typeof(ColorState), typeof(MyIconTextButtonView), new PropertyMetadata(ColorState.Black));
    protected virtual ColorState ReadColorType() => (ColorState)GetValue(ColorTypeProperty);
    protected virtual void WriteColorType(ColorState value) => SetValue(ColorTypeProperty, value);
    private bool isMouseDown;
    private void RefreshColor(object? sender = null, object? args = null) {
        try {
            if (ColorType is not ColorState.Black and not ColorState.Highlight) return;
            if (IsLoaded && Services.AnimationEnabled() && !false.Equals(args)) {
                if (isMouseDown) Animations.Start(Animations.ColorResource(this, BackgroundProperty, "ColorBrush6", new(70)), "MyIconTextButton Color " + Uuid);
                else if (IsMouseOver) {
                    SetForeground("ColorBrush3", 100); Animations.Start(Animations.ColorResource(this, BackgroundProperty, "ColorBrushBg1", new(100)), "MyIconTextButton Color " + Uuid);
                } else {
                    SetForeground(IsEnabled ? ColorType == ColorState.Black ? "ColorBrush1" : "ColorBrush3" : "ColorBrushGray5", IsEnabled ? 150 : 100);
                    Animations.Start(Animations.ColorTo(this, BackgroundProperty, Services.ThemeColor(ControlThemeColor.SemiTransparent), new(150)), "MyIconTextButton Color " + Uuid);
                }
            } else {
                Animations.Stop("MyIconTextButton Checked " + Uuid); Animations.Stop("MyIconTextButton Color " + Uuid);
                Background = Services.ThemeColor(ControlThemeColor.SemiTransparent).ToBrush();
                string color = IsEnabled ? ColorType == ColorState.Black ? "ColorBrush1" : "ColorBrush3" : "ColorBrushGray5";
                ShapeLogo.SetResourceReference(Path.FillProperty, color); LabText.SetResourceReference(TextBlock.ForegroundProperty, color);
            }
        } catch (Exception error) { Services.Warn(error, "刷新带图标按钮颜色出错"); }
    }
    private void SetForeground(string color, int time) => Animations.Start(new[] {
        Animations.ColorResource(ShapeLogo, Path.FillProperty, color, new(time)), Animations.ColorResource(LabText, TextBlock.ForegroundProperty, color, new(time))
    }, "MyIconTextButton Checked " + Uuid);
}
