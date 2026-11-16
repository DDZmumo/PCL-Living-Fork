using PCLLF.Presentation.Foundation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLLF.Presentation.Controls;

// LF MyExtraButton.xaml(.vb); CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 source compared.
// Original left/right event order, two-button held state, progress, visibility and Ribble spelling retained.
public partial class MyExtraButtonView : Grid {
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs args);
    public event ClickEventHandler? Click, RightClick;
    protected virtual void OnClick(object sender, MouseButtonEventArgs args) => Click?.Invoke(sender, args);
    protected virtual void OnRightClick(object sender, MouseButtonEventArgs args) => RightClick?.Invoke(sender, args);
    public int Uuid;
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    protected virtual void UiPost(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.InvokeAsync(work); }
    private string? logo = "";
    public string? Logo { get => logo; set { if (string.Equals(value ?? "", logo ?? "", StringComparison.Ordinal)) return; logo = value; Path.Data = (Geometry)new GeometryConverter().ConvertFromString(value!)!; } }
    private double logoScale = 1;
    public double LogoScale { get => logoScale; set { logoScale = value; if (Path is not null) Path.RenderTransform = new ScaleTransform(LogoScale, LogoScale); } }
    private bool show;
    private double progress;
    public double Progress {
        get => progress;
        set { if (progress == value) return; progress = value; if (value < .0001) PanProgress.Visibility = Visibility.Collapsed;
            else { PanProgress.Visibility = Visibility.Visible; RectProgress.Rect = new Rect(0, 40 * (1 - value), 40, 40 * value); } }
    }
    public MyExtraButtonView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyExtraButtonView).Assembly) InitializeComponent();
        else if (!_contentLoaded) { _contentLoaded = true; CompiledControlLoader.Load(this, typeof(MyExtraButtonView), "controls/myextrabuttonview.xaml"); }
        PanClick.MouseLeftButtonUp += LeftClick; PanClick.MouseRightButtonUp += RightClickMouse;
        PanClick.MouseLeftButtonDown += (_, _) => LeftDown(); PanClick.MouseRightButtonDown += (_, _) => RightDown();
        PanClick.MouseLeftButtonUp += (_, _) => LeftUp(); PanClick.MouseRightButtonUp += (_, _) => RightUp(); PanClick.MouseLeave += (_, _) => Leave();
        PanClick.MouseEnter += (_, _) => RefreshColor(); PanClick.MouseLeave += (_, _) => RefreshColor(); Loaded += (_, _) => RefreshColor(); IsEnabledChanged += (_, _) => RefreshColor();
    
        if (IsInitialized) LegacyThemePalette.Watch(this, RefreshColor);
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, RefreshColor);
    }
    public bool Show {
        get => show;
        set { if (show == value) return; show = value; UiPost(() => {
            if (value) { Visibility = Visibility.Visible; Animations.Start(new[] {
                Animations.ScaleTransform(this, .3 - ((ScaleTransform)RenderTransform).ScaleX, new(500, 60, ControlEase.OutFluent, ControlEasePower.Weak)),
                Animations.ScaleTransform(this, .7, new(500, 60, ControlEase.OutBack, ControlEasePower.Weak)),
                Animations.Height(this, 50 - Height, new(200, ease: ControlEase.OutFluent, power: ControlEasePower.Weak))
            }, "MyExtraButton MainScale " + Uuid); }
            else Animations.Start(new[] {
                Animations.ScaleTransform(this, -((ScaleTransform)RenderTransform).ScaleX, new(100, ease: ControlEase.InFluent, power: ControlEasePower.Weak)),
                Animations.Height(this, -Height, new(400, 100, ControlEase.OutFluent)), Animations.Code(() => Visibility = Visibility.Collapsed, after: true)
            }, "MyExtraButton MainScale " + Uuid);
            IsHitTestVisible = value;
        }); }
    }
    public delegate bool ShowCheckDelegate();
    public ShowCheckDelegate? ShowCheck;
    protected virtual bool HasShowCheck => ShowCheck is not null;
    protected virtual bool CheckShow() => ShowCheck!();
    public void ShowRefresh() { if (HasShowCheck) Show = CheckShow(); }
    private bool leftHeld, rightHeld;
    public bool CanRightClick { get; set; }
    /// <summary>Logical activation for a replacement view; no pointer event or legacy chrome is fabricated.</summary>
    public void PerformClick(bool right = false) {
        if (!IsEnabled || right && !CanRightClick) return;
        if (right) OnRightClick(this, null!);
        else {
            OnClick(this, null!);
            Services.RaiseCustomEvent(this);
        }
    }
    private string ToolTipSuffix() => Operators.ConditionalCompareObjectEqual(ToolTip, "", false) ? "" : "：" + Conversions.ToString(ToolTip);
    private void LeftClick(object sender, MouseButtonEventArgs args) { if (!leftHeld) return; Services.Info("按下附加按钮" + ToolTipSuffix()); OnClick(sender, args); args.Handled = true; LeftUp(); }
    private void RightClickMouse(object sender, MouseButtonEventArgs args) { if (!rightHeld) return; Services.Info("右键按下附加按钮" + ToolTipSuffix()); OnRightClick(sender, args); args.Handled = true; RightUp(); }
    private void PressScale() => Animations.Start(new[] {
        Animations.ScaleTransform(PanScale, .85 - ((ScaleTransform)PanScale.RenderTransform).ScaleX, new(800, ease: ControlEase.OutFluent, power: ControlEasePower.Strong)),
        Animations.ScaleTransform(PanScale, -.05, new(60, ease: ControlEase.OutFluent))
    }, "MyExtraButton Scale " + Uuid);
    private void LeftDown() { if (!leftHeld && !rightHeld) PressScale(); leftHeld = true; Focus(); }
    private void RightDown() { if (!CanRightClick) return; if (!leftHeld && !rightHeld) PressScale(); rightHeld = true; Focus(); }
    private void ResetScale(int time, ControlEase ease) => Animations.Start(Animations.ScaleTransform(PanScale, 1 - ((ScaleTransform)PanScale.RenderTransform).ScaleX, new(time, ease: ease)), "MyExtraButton Scale " + Uuid);
    private void LeftUp() { if (!rightHeld) ResetScale(300, ControlEase.OutBack); if (leftHeld) Services.RaiseCustomEvent(this); leftHeld = false; RefreshColor(); }
    private void RightUp() { if (!CanRightClick) return; if (!leftHeld) ResetScale(300, ControlEase.OutBack); rightHeld = false; RefreshColor(); }
    private void Leave() { leftHeld = false; rightHeld = false; ResetScale(500, ControlEase.OutFluent); RefreshColor(); }
    public void RefreshColor() {
        try {
            string color = !IsEnabled ? "ColorBrushGray4" : IsMouseOver ? "ColorBrush4" : "ColorBrush3"; int time = !IsEnabled || IsMouseOver ? 120 : 150;
            if (IsLoaded && Services.AnimationEnabled()) Animations.Start(Animations.ColorResource(PanColor, BackgroundProperty, color, new(time)), "MyExtraButton Color " + Uuid);
            else { Animations.Stop("MyExtraButton Color " + Uuid); PanColor.SetResourceReference(BackgroundProperty, color); }
        } catch (Exception error) { Services.Warn(error, "刷新图标按钮颜色出错"); }
    }
    public void Ribble() => UiPost(() => {
        Border shape = new() { CornerRadius = new CornerRadius(1000), BorderThickness = new Thickness(.001), Opacity = .5, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform() };
        shape.SetResourceReference(Border.BackgroundProperty, "ColorBrush5"); PanScale.Children.Insert(0, shape);
        Animations.Start(new[] { Animations.ScaleTransform(shape, 13, ControlAnimationSpec.InoutFluent(1000, ControlEasePower.Strong, .3)),
            Animations.Opacity(shape, -shape.Opacity, new(1000)), Animations.Code(() => PanScale.Children.Remove(shape), after: true) }, "ExtraButton Ribble " + Services.NewUuid());
    });
}
