using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyCard.vb; CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyCard.cs
// was compared for structure. LF Grid/Border, theme, height timing and lazy content remain unchanged.
public class MyCardView : Grid {
    private readonly Grid mainGrid;
    public MyDropShadowView MainChrome { get; }
    private readonly Border mainBorder;
    public UIElement BorderChild { get => mainBorder.Child; set => mainBorder.Child = value; }
    private TextBlock? mainTextBlock;
    public TextBlock MainTextBlock { get { Init(); return mainTextBlock!; } set => mainTextBlock = value; }
    private Path? mainSwap;
    public Path MainSwap { get { Init(); return mainSwap!; } set => mainSwap = value; }
    public int Uuid;
    public InlineCollection Inlines => MainTextBlock.Inlines;
    public CornerRadius CornerRadius {
        get => MainChrome.CornerRadius;
        set { MainChrome.CornerRadius = value; mainBorder.CornerRadius = value; }
    }
    public string Title {
        get => (string)GetValue(TitleProperty);
        set { SetValue(TitleProperty, value); if (mainTextBlock is not null) MainTextBlock.Text = value; }
    }
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(MyCardView), new PropertyMetadata(""));
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    protected virtual MyDropShadowView CreateChrome() => new();
    protected virtual Border CreateCardBackground() {
        var border = new Border { CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
        border.SetResourceReference(Border.BackgroundProperty, "ColorBrushCard");
        return border;
    }
    protected virtual double WpfSize(double pixels) => pixels / (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1);
    protected virtual bool HasMainWindow => Application.Current?.MainWindow is not null;
    protected virtual void PostLoaded(Action work) => Dispatcher.InvokeAsync(work, DispatcherPriority.Loaded);

    public MyCardView() {
        Uuid = Services.NewUuid();
        MainChrome = CreateChrome();
        MainChrome.Margin = new Thickness(-3, -3, -3, -3 - WpfSize(1));
        MainChrome.ShadowRadius = 3; MainChrome.Opacity = DropShadowIdleOpacity; MainChrome.CornerRadius = new CornerRadius(5);
        MainChrome.SetResourceReference(MyDropShadowView.ColorProperty, "ColorObjectShadow");
        Children.Insert(0, MainChrome);
        mainBorder = CreateCardBackground();
        Children.Insert(1, mainBorder);
        mainGrid = new Grid(); Children.Add(mainGrid);
        Loaded += (_, _) => Init();
        MouseEnter += (_, _) => MouseEnterCard();
        MouseLeave += (_, _) => MouseLeaveCard();
        SizeChanged += MySizeChanged;
        MouseLeftButtonDown += MouseDownCard;
        MouseLeftButtonUp += (_, _) => MouseUpCard();
        MouseLeave += (_, _) => isSwapMouseDown = false;
        MouseMove += (_, _) => UpdateCursorStyle();
    }
    private bool isLoad;
    private void Init() {
        if (isLoad) return;
        isLoad = true;
        if (MainTextBlock is null) {
            MainTextBlock = new TextBlock { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(15, 12, 0, 0), FontWeight = FontWeights.Bold, FontSize = 13, IsHitTestVisible = false };
            MainTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
            MainTextBlock.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this, Mode = BindingMode.OneWay });
            mainGrid.Children.Add(MainTextBlock);
        }
        if (CanSwap || SwapControl is not null) {
            if (SwapControl is null && Children.Count > 3) SwapControl = (FrameworkElement)Children[3];
            MainSwap = new Path { HorizontalAlignment = HorizontalAlignment.Right, Stretch = Stretch.Uniform, Height = 6, Width = 10,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 17, 16, 0),
                Data = (Geometry)new GeometryConverter().ConvertFromString("M2,4 l-2,2 10,10 10,-10 -2,-2 -8,8 -8,-8 z")!,
                RenderTransform = new RotateTransform(180), RenderTransformOrigin = new Point(0.5, 0.5) };
            MainSwap.SetResourceReference(Path.FillProperty, "ColorBrush1"); mainGrid.Children.Add(MainSwap);
        }
        if (IsSwapped && SwapControl is not null) {
            MainSwap.RenderTransform = new RotateTransform(SwapLogoRight ? 270 : 0);
            SwapControl.Visibility = Visibility.Collapsed;
            bool rawUseAnimation = UseAnimation; UseAnimation = false; Height = SwapedHeight;
            Animations.Stop("MyCard Height " + Uuid); isHeightAnimating = false;
            PostLoaded(() => UseAnimation = rawUseAnimation);
        }
    }
    private Action<Panel>? contentBuilder;
    public void SetContentBuilder(Action<Panel> builder) => contentBuilder = builder;
    public void SetContentBuilder<T>(IEnumerable<T> items, Func<T, UIElement> itemBuilder) => SetContentBuilder(stack => {
        foreach (T item in items) stack.Children.Add(itemBuilder(item));
        stack.Children.Add(new FrameworkElement { Height = 18 });
    });
    public void BuildContent() {
        if (contentBuilder is null || SwapControl is not Panel stack) return;
        try { contentBuilder(stack); contentBuilder = null; }
        catch { stack.Children.Clear(); throw; }
        TriggerForceResize();
    }
    private const double DropShadowIdleOpacity = 0.07, DropShadowHoverOpacity = 0.4;
    public bool HasMouseAnimation { get; set; } = true;
    private void MouseEnterCard() {
        if (!HasMouseAnimation) return;
        List<ControlAnimation> animations = new();
        if (MainTextBlock is not null) animations.Add(Animations.ColorResource(MainTextBlock, TextBlock.ForegroundProperty, "ColorBrush2", new(90)));
        if (MainSwap is not null) animations.Add(Animations.ColorResource(MainSwap, Path.FillProperty, "ColorBrush2", new(90)));
        animations.Add(Animations.ColorResource(MainChrome, MyDropShadowView.ColorProperty, "ColorObject4", new(90)));
        animations.Add(Animations.Opacity(MainChrome, DropShadowHoverOpacity - MainChrome.Opacity, new(90)));
        Animations.Start(animations, "MyCard Mouse " + Uuid);
    }
    private void MouseLeaveCard() {
        if (!HasMouseAnimation) return;
        List<ControlAnimation> animations = new();
        if (MainTextBlock is not null) animations.Add(Animations.ColorResource(MainTextBlock, TextBlock.ForegroundProperty, "ColorBrush1", new(90)));
        if (MainSwap is not null) animations.Add(Animations.ColorResource(MainSwap, Path.FillProperty, "ColorBrush1", new(90)));
        animations.Add(Animations.ColorResource(MainChrome, MyDropShadowView.ColorProperty, "ColorObjectShadow", new(90)));
        animations.Add(Animations.Opacity(MainChrome, DropShadowIdleOpacity - MainChrome.Opacity, new(90)));
        Animations.Start(animations, "MyCard Mouse " + Uuid);
    }
    public bool UseAnimation { get; set; } = true;
    private bool isHeightAnimating;
    private double actualUsedHeight;
    private void MySizeChanged(object sender, SizeChangedEventArgs args) {
        if (!UseAnimation) return;
        double delta = (IsSwapped ? SwapedHeight : args.NewSize.Height) - args.PreviousSize.Height;
        if (args.PreviousSize.Height == 0 || isHeightAnimating || Math.Abs(delta) < 1 || ActualHeight == 0) return;
        StartHeightAnimation(delta, args.PreviousSize.Height);
    }
    private void StartHeightAnimation(double delta, double previousHeight) {
        if (isHeightAnimating || !HasMainWindow) return;
        List<ControlAnimation> animations = new(); double absDelta = Math.Abs(delta);
        if (absDelta <= 800) animations.Add(Animations.Height(this, delta, new(150, ease: ControlEase.OutFluent, power: ControlEasePower.ExtraStrong)));
        else {
            int easeLength = 0, easeTime, initSpeed;
            if (delta < 0 && absDelta - easeLength > 5000 * 0.1) {
                easeLength = 200; easeTime = 150; initSpeed = Convert.ToInt32(Math.Round((absDelta - easeLength) / 0.1));
            } else if (delta > 0 && absDelta - easeLength > 5000 * 0.6) {
                initSpeed = 5000; easeLength = Convert.ToInt32(Math.Round(absDelta - initSpeed * 0.3)); easeTime = 400;
            } else { easeLength = 150; easeTime = 200; initSpeed = 4000; }
            animations.Add(Animations.Height(this, (absDelta - easeLength) * Math.Sign(delta), new((absDelta - easeLength) / initSpeed * 1000)));
            animations.Add(Animations.Height(this, easeLength * Math.Sign(delta),
                ControlAnimationSpec.FluentWithInitial(easeTime, initSpeed, easeTime / 1000.0, easeLength)));
        }
        animations.Add(Animations.Code(() => {
            isHeightAnimating = false; Height = actualUsedHeight;
            if (IsSwapped && SwapControl is not null) SwapControl.Visibility = Visibility.Collapsed;
        }, after: true));
        Animations.Start(animations, "MyCard Height " + Uuid);
        isHeightAnimating = true; actualUsedHeight = IsSwapped ? SwapedHeight : Height; Height = previousHeight;
    }
    public void TriggerForceResize() {
        Height = IsSwapped ? SwapedHeight : double.NaN; Animations.Stop("MyCard Height " + Uuid); isHeightAnimating = false;
    }
    public FrameworkElement? SwapControl;
    public bool CanSwap { get; set; }
    private bool isSwapped;
    public bool IsSwapped {
        get => isSwapped;
        set {
            if (isSwapped == value) return; isSwapped = value;
            if (SwapControl is null) return;
            if (!IsSwapped) BuildContent();
            if (!IsLoaded) return;
            SwapControl.Visibility = Visibility.Visible; TriggerForceResize(); UpdateCursorStyle();
            Animations.Start(Animations.RotateTransform(MainSwap, (isSwapped ? (SwapLogoRight ? 270 : 0) : 180) - ((RotateTransform)MainSwap.RenderTransform).Angle,
                new(250, ease: ControlEase.OutFluent, power: ControlEasePower.ExtraStrong)), "MyCard Swap " + Uuid, true);
        }
    }
    [Obsolete("IsSwaped 存在拼写错误，请换用 IsSwapped 属性。")]
    public bool IsSwaped { get => (bool)GetValue(IsSwapedProperty); set => SetValue(IsSwapedProperty, value); }
    public static readonly DependencyProperty IsSwapedProperty = DependencyProperty.Register("IsSwaped", typeof(bool), typeof(MyCardView),
        new PropertyMetadata(false, (sender, args) => { if (sender is MyCardView card) card.IsSwapped = (bool)args.NewValue; }));
    public bool SwapLogoRight { get; set; }
    private bool isSwapMouseDown, isCustomMouseDown;
    public event EventHandler<ControlPreviewEventArgs>? PreviewSwap;
    public event EventHandler<ControlPreviewEventArgs>? Swap;
    protected virtual object CreateSwapArgs() => new ControlPreviewEventArgs(true);
    protected virtual bool OnPreviewSwap(object args) { PreviewSwap?.Invoke(this, (ControlPreviewEventArgs)args); return ((ControlPreviewEventArgs)args).Handled; }
    protected virtual void OnSwap(object args) => Swap?.Invoke(this, (ControlPreviewEventArgs)args);
    public const int SwapedHeight = 40;
    public void PerformClick() {
        if (!IsEnabled) return;
        Services.RaiseCustomEvent(this);
        if (SwapControl is null && (!CanSwap || !IsSwapped)) return;
        object args = CreateSwapArgs();
        if (OnPreviewSwap(args)) return;
        IsSwapped = !IsSwapped;
        Services.Info((IsSwapped ? "折叠卡片" : "展开卡片") + (Title is null ? "" : "：" + Title));
        OnSwap(args);
    }
    private void MouseDownCard(object sender, MouseButtonEventArgs args) {
        double pos = Mouse.GetPosition(this).Y;
        if (!IsSwapped && (pos > (IsSwapped ? SwapedHeight : SwapedHeight - 6) || (pos == 0 && !IsMouseDirectlyOver))) return;
        isCustomMouseDown = true;
        if (!IsSwapped && (SwapControl is null || pos > (IsSwapped ? SwapedHeight : SwapedHeight - 6) || (pos == 0 && !IsMouseDirectlyOver))) return;
        isSwapMouseDown = true;
    }
    private void MouseUpCard() {
        if (!isCustomMouseDown) return; isCustomMouseDown = false; Services.RaiseCustomEvent(this);
        if (!isSwapMouseDown) return; isSwapMouseDown = false;
        double pos = Mouse.GetPosition(this).Y;
        if (!IsSwapped && (SwapControl is null || pos > (IsSwapped ? SwapedHeight : SwapedHeight - 6) || (pos == 0 && !IsMouseDirectlyOver))) return;
        object args = CreateSwapArgs(); if (OnPreviewSwap(args)) { isSwapMouseDown = false; return; }
        IsSwapped = !IsSwapped; Services.Info((IsSwapped ? "折叠卡片" : "展开卡片") + (Title is null ? "" : "：" + Title)); OnSwap(args);
    }
    private void UpdateCursorStyle() {
        if (mainSwap is null) { Cursor = null; return; }
        double pos = Mouse.GetPosition(this).Y;
        Cursor = MainSwap.Visibility == Visibility.Visible && (IsSwapped || (pos <= SwapedHeight - 6 && !(pos == 0 && !IsMouseDirectlyOver))) ? Cursors.Hand : null;
    }
    public void DisposeAnimated(bool removeFromChildren, Action<object>? callback = null) {
        if (IsHitTestVisible) {
            IsHitTestVisible = false;
            Animations.Start(new[] {
                Animations.ScaleTransform(this, -0.08, new(200, ease: ControlEase.InFluent)),
                Animations.Opacity(this, -1, new(200, ease: ControlEase.OutFluent)),
                Animations.Height(this, -ActualHeight, new(150, 100, ControlEase.OutFluent)),
                Animations.Code(() => { if (!RemoveOrCollapse(removeFromChildren)) return; callback?.Invoke(this); }, after: true)
            }, "MyCard Dispose " + Uuid);
        } else { if (!RemoveOrCollapse(removeFromChildren)) return; callback?.Invoke(this); }
    }
    private bool RemoveOrCollapse(bool removeFromChildren) {
        if (removeFromChildren) {
            if (Parent is null) return false;
            if (Parent is not Panel parent) throw new MissingMemberException(Parent.GetType().FullName, "Children");
            parent.Children.Remove(this);
        } else Visibility = Visibility.Collapsed;
        return true;
    }
}
