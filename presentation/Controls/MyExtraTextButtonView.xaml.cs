using PCLLF.Presentation.Foundation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace PCLLF.Presentation.Controls;

// LF MyExtraTextButton.xaml(.vb); same fixed CE C# source compared without changing LF visuals/timing.
[ContentProperty(nameof(Inlines))]
public partial class MyExtraTextButtonView : Grid {
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs args);
    public event ClickEventHandler? Click;
    protected virtual void OnClick(object sender, MouseButtonEventArgs args) => Click?.Invoke(sender, args);
    public int Uuid;
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    protected virtual void UiPost(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.InvokeAsync(work); }
    private string? logo = "";
    public string? Logo { get => logo; set { if (string.Equals(value ?? "", logo ?? "", StringComparison.Ordinal)) return; logo = value; Path.Data = (Geometry)new GeometryConverter().ConvertFromString(value!)!; } }
    private double logoScale = 1;
    public double LogoScale { get => logoScale; set { logoScale = value; if (Path is not null) Path.RenderTransform = new ScaleTransform(LogoScale, LogoScale); } }
    private bool show;
    public InlineCollection Inlines => LabText.Inlines;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MyExtraTextButtonView), new PropertyMetadata((sender, args) => { if (sender is not null) ((MyExtraTextButtonView)sender).LabText.Text = (string)args.NewValue; }));
    public MyExtraTextButtonView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyExtraTextButtonView).Assembly) InitializeComponent();
        else if (!_contentLoaded) { _contentLoaded = true; CompiledControlLoader.Load(this, typeof(MyExtraTextButtonView), "controls/myextratextbuttonview.xaml"); }
        PanClick.MouseLeftButtonUp += LeftClick;
        PanClick.MouseLeftButtonDown += (_, _) => LeftDown(); PanClick.MouseLeftButtonUp += (_, _) => LeftUp(); PanClick.MouseRightButtonUp += (_, _) => RightUp();
        PanClick.MouseLeave += (_, _) => { leftHeld = false; ResetScale(500, ControlEase.OutFluent); RefreshColor(); };
        PanClick.MouseEnter += (_, _) => RefreshColor(); PanClick.MouseLeave += (_, _) => RefreshColor(); Loaded += (_, _) => RefreshColor(); IsEnabledChanged += (_, _) => RefreshColor();
    
        if (IsInitialized) LegacyThemePalette.Watch(this, RefreshColor);
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, RefreshColor);
    }
    public bool Show {
        get => show;
        set { if (show == value) return; show = value; UiPost(() => {
            if (value) { Opacity = 0; Animations.Start(new[] {
                Animations.Opacity(this, 1 - Opacity, new(80, 50)),
                Animations.ScaleTransform(this, .15 - ((ScaleTransform)RenderTransform).ScaleX, new(400, 50, ControlEase.OutBack)),
                Animations.ScaleTransform(this, .85, new(160, 50, ControlEase.OutFluent, ControlEasePower.Middle))
            }, "MyExtraTextButton MainScale " + Uuid); }
            else Animations.Start(new[] { Animations.Opacity(this, -Opacity, new(50, 50)),
                Animations.ScaleTransform(this, -((ScaleTransform)RenderTransform).ScaleX, new(100, ease: ControlEase.InFluent, power: ControlEasePower.Weak)) }, "MyExtraTextButton MainScale " + Uuid);
            IsHitTestVisible = value;
        }); }
    }
    private bool leftHeld;
    public void PerformClick() {
        if (!IsEnabled || !Show) return;
        Activate(PanClick, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = PanClick
        });
    }
    private void Activate(object sender, MouseButtonEventArgs args) { Services.Info("按下附加图标按钮：" + Text); OnClick(sender, args); args.Handled = true; Services.RaiseCustomEvent(this); }
    private void LeftClick(object sender, MouseButtonEventArgs args) { if (!leftHeld) return; Activate(sender, args); LeftUp(); }
    private void LeftDown() {
        if (!leftHeld) Animations.Start(new[] {
            Animations.ScaleTransform(PanScale, .85 - ((ScaleTransform)PanScale.RenderTransform).ScaleX, new(800, ease: ControlEase.OutFluent, power: ControlEasePower.Strong)),
            Animations.ScaleTransform(PanScale, -.05, new(60, ease: ControlEase.OutFluent)) }, "MyExtraTextButton Scale " + Uuid);
        leftHeld = true; Focus();
    }
    private void ResetScale(int time, ControlEase ease) => Animations.Start(Animations.ScaleTransform(PanScale, 1 - ((ScaleTransform)PanScale.RenderTransform).ScaleX, new(time, ease: ease)), "MyExtraTextButton Scale " + Uuid);
    private void LeftUp() { ResetScale(300, ControlEase.OutBack); leftHeld = false; RefreshColor(); }
    private void RightUp() { if (!leftHeld) ResetScale(300, ControlEase.OutBack); RefreshColor(); }
    public void RefreshColor() {
        try {
            string color = !IsEnabled ? "ColorBrushGray4" : IsMouseOver ? "ColorBrush4" : "ColorBrush3"; int time = !IsEnabled || IsMouseOver ? 120 : 150;
            if (IsLoaded && Services.AnimationEnabled()) Animations.Start(Animations.ColorResource(PanColor, BackgroundProperty, color, new(time)), "MyExtraTextButton Color " + Uuid);
            else { Animations.Stop("MyExtraTextButton Color " + Uuid); PanColor.SetResourceReference(BackgroundProperty, color); }
        } catch (Exception error) { Services.Warn(error, "刷新附加图标按钮颜色出错"); }
    }
}
