using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyLoading.xaml(.vb), including simulator and exact animation ordering.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Controls/MyLoading.xaml.cs compared;
// LF text, event order, error delay and loop behavior retained rather than CE localization/new DP policy.
public partial class MyLoadingView : Grid {
    public delegate void IsErrorChangedEventHandler(object sender, bool isError);
    public delegate void StateChangedEventHandler(object sender, MyLoadingState next, MyLoadingState old);
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs args);
    public event IsErrorChangedEventHandler? IsErrorChanged;
    public event StateChangedEventHandler? StateChanged;
    public event ClickEventHandler? Click;
    protected virtual void OnIsErrorChanged(bool error) => IsErrorChanged?.Invoke(this, error);
    protected virtual void OnStateChanged(MyLoadingState next, MyLoadingState old) => StateChanged?.Invoke(this, next, old);
    protected virtual void OnClick(object sender, MouseButtonEventArgs args) => Click?.Invoke(sender, args);
    public void PerformClick() {
        if (IsEnabled) OnClick(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
    }
    public bool AutoRun { get; set; } = true;
    private readonly int uuid;
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    protected virtual void UiPost(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.InvokeAsync(work); }
    protected virtual string DisplayError(Exception error) => MeloongCore.Extensions.ExceptionExtensions.GetDisplay(error, false);
    protected virtual ILoadingViewTrigger CreateDefaultState() => new LoadingStateSimulatorView();
    public SolidColorBrush Foreground { get => (SolidColorBrush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(SolidColorBrush), typeof(MyLoadingView));
    public MyLoadingView() {
        uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyLoadingView).Assembly) InitializeComponent();
        else if (!_contentLoaded) { _contentLoaded = true; CompiledControlLoader.Load(this, typeof(MyLoadingView), "controls/myloadingview.xaml"); }
        SetResourceReference(ForegroundProperty, "ColorBrush3");
        Loaded += (_, _) => RefreshText(); Loaded += (_, _) => InitState(); Loaded += (_, _) => RefreshState(); Unloaded += (_, _) => RefreshState();
        MouseLeftButtonUp += (sender, args) => OnClick(sender, args);
        // LF deliberately emits Click on release without consulting its unused down marker.
    }
    private bool showProgress;
    public bool ShowProgress { get => showProgress; set { if (showProgress == value) return; showProgress = value; RefreshText(); } }
    private string text = "加载中";
    public string Text { get => text; set { text = value; RefreshText(); } }
    private string textError = "加载失败";
    public string TextError { get => textError; set { textError = value; RefreshText(); } }
    public bool TextErrorInherit { get; set; } = true;
    private void RefreshText() => UiPost(() => {
        if (innerState == MyLoadingState.Error) {
            if (TextErrorInherit && State.IsLoader) { Exception? error = State.Error; LabText.Text = error is null ? "未知错误" : DisplayError(error); }
            else LabText.Text = TextError;
        } else LabText.Text = ShowProgress && State.IsLoader ? Text + " - " + Math.Floor(State.Progress * 100) + "%" : Text;
    });
    public enum MyLoadingState { Unloaded = -1, Run = 0, Stop = 1, Error = 2 }
    private ILoadingViewTrigger? state;
    private IDisposable? subscription;
    public ILoadingViewTrigger State {
        get { InitState(); return state!; }
        set { subscription?.Dispose(); state = value; subscription = value?.Subscribe((_, _) => RefreshState(), (_, _) => RefreshText()); RefreshState(); }
    }
    private void InitState() {
        if (state is not null) return;
        state = CreateDefaultState(); subscription = state.Subscribe((_, _) => RefreshState(), (_, _) => RefreshText());
        if (AutoRun) state.LoadingState = MyLoadingState.Run;
    }
    private void RefreshState() {
        if (state!.LoadingState == MyLoadingState.Run && !IsLoaded) InnerState = MyLoadingState.Stop;
        InnerState = state.LoadingState; OuterState = state.LoadingState; AniLoop();
    }
    private MyLoadingState outerState = MyLoadingState.Unloaded;
    private MyLoadingState OuterState {
        get => outerState;
        set {
            if (outerState == value) return; MyLoadingState old = outerState; outerState = value; OnStateChanged(value, old);
            if ((old == MyLoadingState.Error) != (value == MyLoadingState.Error)) { RefreshText(); OnIsErrorChanged(value == MyLoadingState.Error); }
        }
    }
    private MyLoadingState innerState = MyLoadingState.Unloaded;
    private MyLoadingState InnerState {
        get => innerState;
        set {
            if (innerState == value) return; MyLoadingState old = innerState; innerState = value; AniLoop();
            if ((old == MyLoadingState.Error) != (value == MyLoadingState.Error)) ErrorAnimation(value == MyLoadingState.Error);
        }
    }
    public bool HasAnimation { get; set; } = true;
    private bool isLooping, errorAnimationWaiting;
    private void AniLoop() {
        if (!HasAnimation || isLooping || innerState != MyLoadingState.Run || Animations.Speed > 10 || !IsLoaded) return;
        isLooping = true; errorAnimationWaiting = true;
        Animations.Start(new[] {
            Animations.RotateTransform(PathPickaxe, -20 - ((RotateTransform)PathPickaxe.RenderTransform).Angle, new(350, 250, ControlEase.InBack, ControlEasePower.Weak)),
            Animations.RotateTransform(PathPickaxe, 50, new(900, ease: ControlEase.OutFluent, after: true)),
            Animations.RotateTransform(PathPickaxe, 25, new(900, ease: ControlEase.OutElastic, power: ControlEasePower.Weak)),
            Animations.Code(() => { PathLeft.Opacity = 1; PathLeft.Margin = new Thickness(7, 41, 0, 0); PathRight.Opacity = 1; PathRight.Margin = new Thickness(14, 41, 0, 0); errorAnimationWaiting = false; }),
            Animations.Opacity(PathLeft, -1, new(100, 50)), Animations.X(PathLeft, -5, new(180, ease: ControlEase.OutFluent)), Animations.Y(PathLeft, -6, new(180, ease: ControlEase.OutFluent)),
            Animations.Opacity(PathRight, -1, new(100, 50)), Animations.X(PathRight, 5, new(180, ease: ControlEase.OutFluent)), Animations.Y(PathRight, -6, new(180, ease: ControlEase.OutFluent)),
            Animations.Code(() => { isLooping = false; AniLoop(); }, after: true)
        }, "MyLoader Loop " + uuid + "/" + Services.NewUuid());
    }
    private void ErrorAnimation(bool error) {
        if (error) {
            int wait = errorAnimationWaiting ? 400 : 0;
            Animations.Start(new[] {
                Animations.ColorResource(PanBack, ForegroundProperty, "ColorBrushRedLight", new(300)),
                Animations.Opacity(PathError, 1 - PathError.Opacity, new(100, 300 + wait)),
                Animations.ScaleTransform(PathError, 1 - ((ScaleTransform)PathError.RenderTransform).ScaleX, new(400, 300 + wait, ControlEase.OutBack))
            }, "MyLoader Error " + uuid);
        } else Animations.Start(new[] {
            Animations.Opacity(PathError, -PathError.Opacity, new(100)), Animations.ScaleTransform(PathError, .5 - ((ScaleTransform)PathError.RenderTransform).ScaleX, new(200)),
            Animations.ColorResource(PanBack, ForegroundProperty, "ColorBrush3", new(300))
        }, "MyLoader Error " + uuid);
    }
}
