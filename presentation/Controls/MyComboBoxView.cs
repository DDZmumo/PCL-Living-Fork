using PCLLF.Presentation.Foundation;
using System;
using System.Linq;
using Microsoft.VisualBasic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyComboBox.vb and its existing Application.xaml templates, not CE's new HintText DP/localization.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Controls/MyComboBox.cs was compared for C# structure.
public class MyComboBoxView : ComboBox {
    public delegate void TextChangedEventHandler(object sender, TextChangedEventArgs? args);
    public event TextChangedEventHandler? TextChanged;
    protected virtual void OnTextChanged(object sender, TextChangedEventArgs? args) => TextChanged?.Invoke(sender, args);
    public int Uuid;
    private MyTextBoxView? textBox;
    public string HintText { get; set; } = "";
    private static ControlServiceHost Services => ControlServices.Current;
    private static ControlAnimations Animations => Services.Animations;
    protected virtual double MainWindowOpacity => Application.Current.MainWindow!.Opacity;
    protected virtual void ReportError(Exception error, string message) => Services.Error(error, message);
    public MyComboBoxView() {
        Uuid = Services.NewUuid(); rawText = Conversions.ToString(SelectedItem);
        PreviewMouseLeftButtonDown += (_, _) => isMouseDown = true;
        PreviewMouseLeftButtonUp += (_, _) => isMouseDown = false; MouseLeave += (_, _) => isMouseDown = false;
        IsEnabledChanged += (_, _) => RefreshColor(); MouseEnter += (_, _) => RefreshColor(); MouseLeave += (_, _) => RefreshColor();
        PreviewMouseLeftButtonDown += (_, _) => RefreshColor(); PreviewMouseLeftButtonUp += (_, _) => RefreshColor(); GotKeyboardFocus += (_, _) => RefreshColor();
        DropDownOpened += (_, _) => OpenDropDown(); DropDownClosed += (_, _) => Width = realWidth;
        SelectionChanged += (_, _) => { if (IsLoaded && Services.AnimationEnabled()) Services.RaiseCustomEvent(this); };
    
        if (IsInitialized) LegacyThemePalette.Watch(this, RefreshColor);
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, RefreshColor);
    }
    public override void OnApplyTemplate() {
        if (textBox is { } previous) {
            rawText = previous.Text;
            previous.RemoveHandler(LostFocusEvent, new RoutedEventHandler(OnEditorLostFocus));
            previous.ChangedEventList.Remove(OnEditorChanged);
            textBox = null;
        }
        base.OnApplyTemplate();
        if (IsEditable && GetTemplateChild("PART_EditableTextBox") is MyTextBoxView editor) {
            try {
                textBox = editor;
                editor.AddHandler(LostFocusEvent, new RoutedEventHandler(OnEditorLostFocus));
                editor.ChangedEventList.Add(OnEditorChanged);
                editor.Tag = Tag;
                if (Same(Text, "")) editor.Text = rawText; else RaiseTextChanged(this, null);
                if (HintText.Length > 0) editor.HintText = HintText;
            } catch (Exception error) { ReportError(error, "初始化可编辑文本框失败（" + (Name ?? "") + "）"); }
        }
        RefreshColor();
    }
    private void OnEditorLostFocus(object sender, RoutedEventArgs args) => RefreshColor();
    private void OnEditorChanged(object sender, RoutedEventArgs args) => RaiseTextChanged(sender, (TextChangedEventArgs)args);
    private string? rawText;
    private static bool Same(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    public new string Text {
        get => IsEditable ? (textBox is null ? rawText ?? "" : textBox.Text ?? "") : (SelectedItem ?? "").ToString()!;
        set { if (!IsEditable) throw new NotSupportedException("该 ComboBox 不支持修改文本。"); if (textBox is null) rawText = value; else textBox.Text = value; }
    }
    private bool isMouseDown;
    public void RefreshColor() {
        string foreground, background; int time;
        if (IsEnabled) {
            if (isMouseDown || IsDropDownOpen || (IsEditable && textBox?.IsFocused == true)) { foreground = "ColorBrush3"; background = "ColorBrush7"; time = 10; }
            else if (IsMouseOver) { foreground = "ColorBrush4"; background = "ColorBrush7"; time = 100; }
            else { foreground = "ColorBrushBg0"; background = "ColorBrushHalfWhite"; time = 100; }
        } else { foreground = "ColorBrushGray5"; background = "ColorBrushGray6"; time = 200; }
        if (IsLoaded && Services.AnimationEnabled()) Animations.Start(new[] { Animations.ColorResource(this, ForegroundProperty, foreground, new(time)), Animations.ColorResource(this, BackgroundProperty, background, new(time)) }, "MyComboBox Color " + Uuid);
        else { Animations.Stop("MyComboBox Color " + Uuid); SetResourceReference(ForegroundProperty, foreground); SetResourceReference(BackgroundProperty, background); }
    }
    private double realWidth;
    private void OpenDropDown() {
        realWidth = Width; Width = ActualWidth;
        try { ((Grid)Template.FindName("PanPopup", this)).Opacity = MainWindowOpacity; }
        catch (Exception error) { ReportError(error, "设置下拉框透明度失败"); }
    }
    private bool isTextChanging;
    private void RaiseTextChanged(object sender, TextChangedEventArgs? args) {
        if (!isTextChanging && IsEditable && SelectedItem is not null && !Same(Text, SelectedItem.ToString())) {
            string text = Text; int start = textBox!.SelectionStart; isTextChanging = true; SelectedItem = null; Text = text; textBox.SelectionStart = start; isTextChanging = false;
        }
        OnTextChanged(sender, args);
    }
    public void RefreshSettingValue(string newValue, Func<DependencyObject, string?> readValue, Func<string> readKey, Action<string> warn) {
        DependencyObject? target = Items.OfType<DependencyObject>().FirstOrDefault(item => Conversion.Val(readValue(item) ?? "-1") == Conversions.ToDouble(newValue));
        if (target is not null) SelectedItem = target;
        else if (Items.Count > Conversions.ToDouble(newValue)) SelectedIndex = Conversions.ToInteger(newValue);
        else { warn("尝试显示设置 " + readKey() + "，但没有找到设置值 " + newValue + " 的对应项"); SelectedIndex = -1; SelectedItem = null; }
    }
    public string? CurrentSettingValue(Func<DependencyObject, string?> readValue) {
        if (SelectedItem is null) return null;
        return SelectedItem is DependencyObject item ? readValue(item) ?? Conversions.ToString(SelectedIndex) : Conversions.ToString(SelectedIndex);
    }
    public ContentPresenter ContentPresenter => (ContentPresenter)Template.FindName("PART_Content", this);
}
