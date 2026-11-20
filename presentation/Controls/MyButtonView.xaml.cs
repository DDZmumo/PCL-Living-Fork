using PCLLF.Presentation.Foundation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace PCLLF.Presentation.Controls;

/// <summary>The original LF button visual, state machine and animation plan.</summary>
// LF Controls/MyButton.xaml(.vb), governed by ../../LICENCE.
// C# constructor/partial migration reference: CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5,
// Plain Craft Launcher 2/Controls/MyButton.xaml.cs. CE LICENSE and its PCL LICENCE
// remain distinct; LF resource lookup, event ordering and appearance are retained.
[ContentProperty(nameof(Inlines))]
public partial class MyButtonView : Border {
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs args);
    public event ClickEventHandler? Click;
    public enum ColorState { Normal = 0, Highlight = 1, Red = 2 }
    public int Uuid;
    private ColorState colorType = ColorState.Normal;
    private bool isMouseDown;
    private const int AnimationColorIn = 100, AnimationColorOut = 200;
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;

    public MyButtonView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyButtonView).Assembly) InitializeComponent();
        else if (!_contentLoaded) {
            _contentLoaded = true;
            CompiledControlLoader.Load(this, typeof(MyButtonView), "controls/mybuttonview.xaml");
        }
        // Preserve LF Handles order: the click is raised before the release animation.
        MouseEnter += RefreshColor;
        MouseLeave += RefreshColor;
        Loaded += RefreshColor;
        IsEnabledChanged += (_, _) => RefreshColor();
        MouseLeftButtonUp += ButtonMouseUpClick;
        MouseLeftButtonDown += ButtonMouseDown;
        MouseEnter += (_, _) => ButtonMouseEnter();
        MouseLeftButtonUp += (_, _) => ButtonMouseUpScale();
        MouseLeave += (_, _) => ButtonMouseLeave();
    
        if (IsInitialized) LegacyThemePalette.Watch(this, () => RefreshColor());
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, () => RefreshColor());
    }

    public InlineCollection Inlines => LabText.Inlines;
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MyButtonView),
        new PropertyMetadata((sender, args) => {
            if (sender is not null) ((MyButtonView)sender).LabText.Text = (string?)args.NewValue;
        }));
    public Thickness TextPadding { get => LabText.Padding; set => LabText.Padding = value; }
    public ColorState ColorType { get => colorType; set { colorType = value; RefreshColor(); } }
    public new static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(MyButtonView),
        new PropertyMetadata((sender, args) => {
            if (sender is not null) ((MyButtonView)sender).PanFore.Padding = (Thickness)args.NewValue;
        }));
    public new Thickness Padding { get => PanFore.Padding; set => PanFore.Padding = value; }
    public Transform RealRenderTransform { get => PanFore.RenderTransform; set => PanFore.RenderTransform = value; }

    protected virtual void OnClick(object sender, MouseButtonEventArgs args) => Click?.Invoke(sender, args);

    /// <summary>Runs the button command from an alternate presentation without synthesizing routed input.</summary>
    public void PerformClick() {
        if (!IsEnabled) return;
        Services.Info("按下按钮：" + Text);
        OnClick(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
        Services.RaiseCustomEvent(this);
    }

    private void RefreshColor(object? sender = null, EventArgs? args = null) {
        try {
            string name = "MyButton Color " + Uuid;
            if (IsLoaded && Services.AnimationEnabled()) {
                if (IsEnabled) {
                    string? key = ColorType switch {
                        ColorState.Normal => IsMouseOver ? "ColorBrush3" : "ColorBrush1",
                        ColorState.Highlight => IsMouseOver ? "ColorBrush3" : "ColorBrush2",
                        ColorState.Red => IsMouseOver ? "ColorBrushRedLight" : "ColorBrushRedDark",
                        _ => null
                    };
                    if (key is not null) Animations.Start(Animations.ColorResource(PanFore, BorderBrushProperty, key,
                        new ControlAnimationSpec(IsMouseOver ? AnimationColorIn : AnimationColorOut)), name);
                } else {
                    Animations.Start(Animations.ColorTo(PanFore, BorderBrushProperty, Services.ThemeColor(ControlThemeColor.Gray4),
                        new ControlAnimationSpec(AnimationColorOut)), name);
                }
            } else {
                Animations.Stop(name);
                if (IsEnabled) {
                    string? key = ColorType switch {
                        ColorState.Normal => IsMouseOver ? "ColorBrush3" : "ColorBrush1",
                        ColorState.Highlight => IsMouseOver ? "ColorBrush3" : "ColorBrush2",
                        ColorState.Red => IsMouseOver ? "ColorBrushRedLight" : "ColorBrushRedDark",
                        _ => null
                    };
                    if (key is not null) PanFore.SetResourceReference(BorderBrushProperty, key);
                } else PanFore.BorderBrush = Services.ThemeColor(ControlThemeColor.Gray4).ToBrush();
            }
        } catch (Exception error) { Services.Warn(error, "刷新按钮颜色出错"); }
    }

    private void ButtonMouseUpClick(object sender, MouseButtonEventArgs args) {
        if (!isMouseDown) return;
        Services.Info("按下按钮：" + Text);
        OnClick(sender, args);
        Services.RaiseCustomEvent(this);
    }
    private void ButtonMouseDown(object sender, MouseButtonEventArgs args) {
        isMouseDown = true;
        Focus();
        Animations.Start(new[] {
            Animations.ScaleTransform(PanFore, 0.955 - ((ScaleTransform)PanFore.RenderTransform).ScaleX,
                new ControlAnimationSpec(80, ease: ControlEase.OutFluent, power: ControlEasePower.ExtraStrong)),
            Animations.ScaleTransform(PanFore, -0.01,
                new ControlAnimationSpec(700, ease: ControlEase.OutFluent))
        }, "MyButton Scale " + Uuid);
    }
    private void ButtonMouseEnter() => Animations.Start(Animations.ColorResource(PanFore, BackgroundProperty,
        colorType == ColorState.Red ? "ColorBrushRedBack" : "ColorBrush7", new ControlAnimationSpec(AnimationColorIn)), "MyButton Background " + Uuid);
    private void ButtonMouseUpScale() {
        if (!isMouseDown) return;
        isMouseDown = false;
        Animations.Start(new[] {
            Animations.ScaleTransform(PanFore, 1 - ((ScaleTransform)PanFore.RenderTransform).ScaleX,
                new ControlAnimationSpec(300, 10, ControlEase.OutFluent))
        }, "MyButton Scale " + Uuid);
    }
    private void ButtonMouseLeave() {
        Animations.Start(Animations.ColorResource(PanFore, BackgroundProperty, "ColorBrushHalfWhite",
            new ControlAnimationSpec(AnimationColorOut)), "MyButton Background " + Uuid);
        if (!isMouseDown) return;
        isMouseDown = false;
        Animations.Start(Animations.ScaleTransform(PanFore, 1 - ((ScaleTransform)PanFore.RenderTransform).ScaleX,
            new ControlAnimationSpec(800, ease: ControlEase.OutFluent, power: ControlEasePower.Strong)), "MyButton Scale " + Uuid);
    }
}
