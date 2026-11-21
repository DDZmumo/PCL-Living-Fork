using PCLLF.Presentation.Foundation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCLCS.Validation;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyTextBox.vb; actual LF templates remain in Application.xaml until that resource owner migrates.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Controls/MyTextBox.cs was compared;
// its FluentValidation rules and localization are not reused. The real LF C# LegacyValidation executes the original typed rules.
public class MyTextBoxView : TextBox {
    public bool HasBackground { get; set; } = true;
    public bool ShowValidateResult { get; set; } = true;
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(MyTextBoxView), new PropertyMetadata(new CornerRadius(3)));
    private TextBlock? wrong;
    private TextBlock? Wrong { get { if (Template is null) return null; return wrong ??= (TextBlock?)Template.FindName("labWrong", this); } }
    private TextBlock? hint;
    private TextBlock? Hint { get { if (Template is null) return null; return hint ??= (TextBlock?)Template.FindName("labHint", this); } }
    public override void OnApplyTemplate() {
        base.OnApplyTemplate();
        wrong = hint = null;
        if (!Same(HintText, "")) RefreshHint();
    }
    public int Uuid;
    public static event EventHandler? ValidateChanged;
    protected virtual void OnValidateChanged() => ValidateChanged?.Invoke(this, new EventArgs());
    private List<RoutedEventHandler>? changedEventList;
    public List<RoutedEventHandler> ChangedEventList => changedEventList ??= new();
    public event RoutedEventHandler ValidatedTextChanged { add => ChangedEventList.Add(value); remove => ChangedEventList.Remove(value); }
    private void RaiseValidatedTextChanged(object sender, TextChangedEventArgs args) {
        foreach (RoutedEventHandler handler in ChangedEventList) if (handler is not null) handler(sender, args);
    }
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    protected virtual void UiPost(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.InvokeAsync(work); }
    protected virtual void UiWait(Action work) { if (Dispatcher.CheckAccess()) work(); else Dispatcher.Invoke(work); }
    protected virtual void StartWorker(Action work, string name) {
        new Thread(() => { try { work(); } catch (Exception error) { ReportError(error, name + "：线程执行失败"); } }) { Name = name, Priority = ThreadPriority.Normal }.Start();
    }
    protected virtual void ReportError(Exception error, string message) => Services.Error(error, message);
    protected virtual ControlColor ReadTextColor(bool enabled) {
        Color color = ((SolidColorBrush)FindResource(enabled ? "ColorBrushGray1" : "ColorBrushGray4")).Color;
        return new(color.A, color.R, color.G, color.B);
    }
    public MyTextBoxView() {
        Uuid = Services.NewUuid();
        Loaded += (_, _) => Validate(); TextChanged += (sender, args) => TextChangedHandler((MyTextBoxView)sender, args);
        IsKeyboardFocusWithinChanged += (_, _) => RefreshHint();
        IsEnabledChanged += (_, _) => RefreshColor(); MouseEnter += (_, _) => RefreshColor(); MouseLeave += (_, _) => RefreshColor();
        GotFocus += (_, _) => RefreshColor(); LostFocus += (_, _) => RefreshColor(); IsEnabledChanged += (_, _) => RefreshTextColor();
        KeyUp += (_, args) => { if (args.Key == Key.Enter) Services.RaiseCustomEvent(this); };
    
        if (IsInitialized) LegacyThemePalette.Watch(this, () => { RefreshColor(); RefreshTextColor(); });
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, () => { RefreshColor(); RefreshTextColor(); });
    }
    public string ValidateResult { get => (string)GetValue(ValidateResultProperty); set => SetValue(ValidateResultProperty, value); }
    public static readonly DependencyProperty ValidateResultProperty = DependencyProperty.Register(nameof(ValidateResult), typeof(string), typeof(MyTextBoxView),
        new PropertyMetadata("", (sender, args) => sender.SetValue(IsValidatedPropertyKey, string.IsNullOrEmpty((string?)args.NewValue))));
    public bool IsValidated => (bool)GetValue(IsValidatedProperty);
    private static readonly DependencyPropertyKey IsValidatedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsValidated), typeof(bool), typeof(MyTextBoxView), new PropertyMetadata(true));
    public static readonly DependencyProperty IsValidatedProperty = IsValidatedPropertyKey.DependencyProperty;
    private Collection<LegacyValidationRule>? validateRules;
    private bool rulesInitialized;
    public Collection<LegacyValidationRule> ValidateRules {
        get { if (!rulesInitialized) { validateRules = new(); rulesInitialized = true; } return validateRules!; }
        set { validateRules = value; rulesInitialized = true; Validate(); }
    }
    protected virtual IEnumerable<LegacyValidationRule> ReadValidateRules() => ValidateRules;
    public void Validate() {
        ValidateResult = LegacyValidation.Validate(Text, ReadValidateRules());
        if (shown != (IsValidated ? ValidateState.Success : ValidateState.FailedAndShowDetail)) {
            if (IsLoaded && Wrong is not null) ChangeValidateResult(IsValidated, true);
            else StartWorker(() => { Thread.Sleep(30); UiPost(() => ChangeValidateResult(IsValidated, false)); }, "DelayedValidate Change");
        }
        if (ShowValidateResult && !IsValidated) {
            if (IsLoaded && Wrong is not null) Wrong.Text = ValidateResult;
            else StartWorker(() => {
                bool finished = false;
                while (!finished) {
                    Thread.Sleep(20);
                    UiWait(() => { if (Wrong is not null) { Wrong.Text = ValidateResult; finished = true; } if (!IsLoaded) finished = true; });
                }
            }, "DelayedValidate Text");
        }
    }
    public void ForceShowAsSuccess() { isTextChanged = false; ChangeValidateResult(IsValidated, true); }
    private enum ValidateState { NotInited, Success, FailedButTextNotChanged, FailedAndShowDetail, FailedAndHideDetail, NotLoaded }
    private ValidateState shown = ValidateState.NotInited;
    private void ChangeValidateResult(bool successful, bool loaded) {
        if (loaded && Services.AnimationEnabled() && Wrong is not null) {
            if (successful || !isTextChanged) {
                shown = successful ? ValidateState.Success : ValidateState.FailedButTextNotChanged;
                Animations.Start(new[] {
                    Animations.Opacity(Wrong, -Wrong.Opacity, new(150)), Animations.Height(Wrong, -Wrong.Height, new(150, ease: ControlEase.OutFluent)),
                    Animations.Code(() => Wrong!.Visibility = Visibility.Collapsed, after: true)
                }, "MyTextBox Validate " + Uuid);
            } else if (ShowValidateResult) {
                shown = ValidateState.FailedAndShowDetail; Wrong.Visibility = Visibility.Visible;
                Animations.Start(new[] { Animations.Opacity(Wrong, 1 - Wrong.Opacity, new(150)), Animations.Height(Wrong, 21 - Wrong.Height, new(150, ease: ControlEase.OutFluent)) }, "MyTextBox Validate " + Uuid);
            } else shown = ValidateState.FailedAndHideDetail;
        } else shown = ValidateState.NotLoaded;
        RefreshColor(); OnValidateChanged();
    }
    private bool isTextChanged;
    public string HintText { get => (string)GetValue(HintTextProperty); set => SetValue(HintTextProperty, value); }
    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(nameof(HintText), typeof(string), typeof(MyTextBoxView),
        new PropertyMetadata("", (sender, _) => ((MyTextBoxView)sender).RefreshHint()));
    private void RefreshHint() {
        if (Hint is { } label) label.Text = !IsKeyboardFocusWithin && Same(Text, "") ? HintText : "";
    }
    private void TextChangedHandler(MyTextBoxView sender, TextChangedEventArgs args) {
        try {
            RefreshHint();
            isTextChanged = IsLoaded; Validate(); if (!IsValidated) return; RaiseValidatedTextChanged(sender, args);
        } catch (Exception error) { ReportError(error, "进行输入验证时出错"); }
    }
    private void RefreshColor() {
        try {
            if (TemplatedParent is MyComboBoxView) return;
            string foreground, background; int time;
            if (IsEnabled) {
                if (IsValidated || !isTextChanged) {
                    if (IsFocused) { foreground = "ColorBrush3"; background = "ColorBrush7"; time = 10; }
                    else if (IsMouseOver) { foreground = "ColorBrush4"; background = "ColorBrush7"; time = 100; }
                    else { foreground = "ColorBrushBg0"; background = "ColorBrushHalfWhite"; time = 100; }
                } else { foreground = "ColorBrushRedLight"; background = "ColorBrushRedBack"; time = 200; }
            } else { foreground = "ColorBrushGray5"; background = "ColorBrushGray6"; time = 200; }
            if (!HasBackground) background = "ColorBrushTransparent";
            if (IsLoaded && Services.AnimationEnabled()) Animations.Start(new[] { Animations.ColorResource(this, BorderBrushProperty, foreground, new(time)), Animations.ColorResource(this, BackgroundProperty, background, new(time)) }, "MyTextBox Color " + Uuid);
            else { Animations.Stop("MyTextBox Color " + Uuid); SetResourceReference(BorderBrushProperty, foreground); SetResourceReference(BackgroundProperty, background); }
        } catch (Exception error) { Services.Warn(error, "文本框颜色改变出错"); }
    }
    private void RefreshTextColor() {
        ControlColor color = ReadTextColor(IsEnabled);
        if (((SolidColorBrush)Foreground).Color.R == color.R) return;
        if (IsLoaded && Services.AnimationEnabled() && !Same(Text, "")) Animations.Start(Animations.ColorResource(this, ForegroundProperty, IsEnabled ? "ColorBrushGray1" : "ColorBrushGray4", new(200)), "MyTextBox TextColor " + Uuid);
        else { Animations.Stop("MyTextBox TextColor " + Uuid); Foreground = color.ToBrush(); }
    }
}
