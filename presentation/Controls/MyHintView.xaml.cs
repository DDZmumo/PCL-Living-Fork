using PCLLF.Presentation.Foundation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace PCLLF.Presentation.Controls;

// LF MyHint.xaml(.vb), including close persistence and the original disposal sequence.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 MyHint.xaml.cs was compared; no CE theme substitution.
[ContentProperty(nameof(Inlines))]
public partial class MyHintView : Border {
    public int Uuid;
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    protected virtual double WpfSize(double pixels) => pixels / (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1);
    protected virtual bool ReadDismissed(string key) => throw new InvalidOperationException("The product settings adapter is required to read a dismissible hint.");
    protected virtual void WriteDismissed(string key) => throw new InvalidOperationException("The product settings adapter is required to dismiss a hint.");
    public MyHintView() {
        Uuid = Services.NewUuid();
        if (GetType().Assembly == typeof(MyHintView).Assembly) InitializeComponent();
        else if (!_contentLoaded) { _contentLoaded = true; CompiledControlLoader.Load(this, typeof(MyHintView), "controls/myhintview.xaml"); }
        Loaded += (_, _) => UpdateUI();
        Loaded += (_, _) => { if (CanClose && ReadDismissed(RelativeSetup)) Visibility = Visibility.Collapsed; };
        BtnClose.Click += (_, _) => { WriteDismissed(RelativeSetup); DisposeAnimated(false); };
        MouseLeftButtonUp += MouseUpHint;
        MouseLeftButtonDown += (_, _) => isMouseDown = true;
        MouseLeave += (_, _) => isMouseDown = false;
    
        if (IsInitialized) LegacyThemePalette.Watch(this, UpdateUI);
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, UpdateUI);
    }
    public bool HasBorder {
        get => BorderThickness.Top > 0;
        set => BorderThickness = value ? new Thickness(3, WpfSize(1), WpfSize(1), WpfSize(1)) : new Thickness(3, 0, 0, 0);
    }
    public enum Themes { Blue, Red, Yellow }
    private Themes colorType = Themes.Red;
    public Themes Theme { get => colorType; set { colorType = value; UpdateUI(); } }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void UpdateUI() {
        string role = Theme == Themes.Blue ? "Info" : Theme == Themes.Yellow ? "Warning" : "Danger";
        SetResourceReference(BackgroundProperty, "ColorBrush" + role + "Back");
        SetResourceReference(BorderBrushProperty, "ColorBrush" + role + "Border");
        LabText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush" + role + "Text");
        BtnClose.Foreground = LegacyThemePalette.Brush("ColorBrush" + role + "Text");
    }
    [Obsolete("IsWarn 已过时。请换用 Theme 属性。")]
    public bool IsWarn { get => Theme == Themes.Red; set => Theme = value ? Themes.Red : Themes.Blue; }
    public static readonly DependencyProperty IsWarnProperty = DependencyProperty.Register("IsWarn", typeof(bool), typeof(MyHintView),
        new PropertyMetadata(true, (sender, args) => ((MyHintView)sender).Theme = (bool)args.NewValue ? Themes.Red : Themes.Blue));
    public InlineCollection Inlines => LabText.Inlines;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MyHintView),
        new PropertyMetadata("", (sender, args) => ((MyHintView)sender).LabText.Text = (string)args.NewValue));
    public bool CanClose { get => BtnClose.Visibility == Visibility.Visible; set => BtnClose.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public string RelativeSetup { get; set; } = "";
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs e);
    public event ClickEventHandler? Click;
    protected virtual void OnClick(object sender, MouseButtonEventArgs args) => Click?.Invoke(sender, args);
    public void PerformClick() {
        if (!IsEnabled) return;
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
        OnClick(this, args);
        if (!args.Handled) Services.RaiseCustomEvent(this);
    }
    private bool isMouseDown;
    private void MouseUpHint(object sender, MouseButtonEventArgs args) {
        if (!isMouseDown) return; isMouseDown = false;
        Services.Info("按下提示条" + (string.IsNullOrEmpty(Name) ? "" : "：" + Name)); OnClick(sender, args);
        if (args.Handled) return; args.Handled = true; Services.RaiseCustomEvent(this);
    }
    public void DisposeAnimated(bool removeFromChildren, Action<object>? callback = null) {
        if (!IsHitTestVisible) return; IsHitTestVisible = false;
        Animations.Start(new[] {
            Animations.ScaleTransform(this, -0.08, new(200, ease: ControlEase.InFluent)),
            Animations.Opacity(this, -1, new(200, ease: ControlEase.OutFluent)),
            Animations.Height(this, -ActualHeight, new(150, 100, ControlEase.OutFluent)),
            Animations.Code(() => {
                if (removeFromChildren) {
                    if (Parent is not Panel parent) throw new MissingMemberException(Parent?.GetType().FullName, "Children");
                    parent.Children.Remove(this);
                } else Visibility = Visibility.Collapsed;
                callback?.Invoke(this);
            }, after: true)
        }, "MyCard Dispose " + Uuid);
    }
}
