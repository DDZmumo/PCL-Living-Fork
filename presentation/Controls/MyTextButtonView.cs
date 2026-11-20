using PCLLF.Presentation.Foundation;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyTextButton.vb; CE fixed 1a7653127d578efdb2a9388999a3fdc67d9e49d5 C# compared.
public class MyTextButtonView : Label {
    public delegate void ClickEventHandler(object sender, EventArgs? args);
    public event ClickEventHandler? Click;
    protected virtual void OnClick() => Click?.Invoke(this, null);
    public void PerformClick() {
        if (!IsEnabled) return;
        Services.Info("按下文本按钮：" + Text);
        OnClick(); Services.RaiseCustomEvent(this);
    }
    public int Uuid;
    private static ControlServiceHost Services => ControlServices.Current;
    public MyTextButtonView() {
        Uuid = Services.NewUuid(); SetResourceReference(ForegroundProperty, "ColorBrush1"); Background = Services.ThemeColor(ControlThemeColor.SemiTransparent).ToBrush();
        PreviewMouseLeftButtonDown += (_, args) => { IsMouseDown = true; args.Handled = true; };
        MouseLeave += (_, _) => IsMouseDown = false;
        PreviewMouseLeftButtonUp += (_, args) => {
            if (!IsMouseDown) return; IsMouseDown = false; Services.Info("按下文本按钮：" + Text); OnClick(); Services.RaiseCustomEvent(this); args.Handled = true;
        };
        MouseEnter += (_, _) => RefreshColor(); MouseLeave += (_, _) => RefreshColor(); IsEnabledChanged += (_, _) => RefreshColor();
        MouseLeftButtonDown += (_, _) => RefreshColor(); MouseLeftButtonUp += (_, _) => RefreshColor();
    
        if (IsInitialized) LegacyThemePalette.Watch(this, RefreshColor);
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, RefreshColor);
    }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MyTextButtonView), new PropertyMetadata("", (sender, args) => {
        if (string.Equals((string?)args.OldValue ?? "", (string?)args.NewValue ?? "", StringComparison.Ordinal)) return;
        MyTextButtonView view = (MyTextButtonView)sender;
        Services.Animations.Start(new[] { Services.Animations.Opacity(view, -view.Opacity, new(50)),
            Services.Animations.Code(() => view.Content = args.NewValue, after: true), Services.Animations.Opacity(view, 1, new(170)) }, "MyTextButton Text " + view.Uuid);
    }));
    public bool IsMouseDown;
    private string? colorName;
    private void RefreshColor() {
        string color; int time;
        if (IsMouseDown) { color = "ColorBrush4"; time = 30; }
        else if (IsMouseOver) { color = "ColorBrush3"; time = 100; }
        else { color = "ColorBrush1"; time = 200; }
        if (colorName == color) return; colorName = color;
        if (IsLoaded && Services.AnimationEnabled()) Services.Animations.Start(Services.Animations.ColorResource(this, ForegroundProperty, color, new(time)), "MyTextButton Color " + Uuid);
        else { Services.Animations.Stop("MyTextButton Color " + Uuid); SetResourceReference(ForegroundProperty, color); }
    }
}
