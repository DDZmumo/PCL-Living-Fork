using PCLLF.Presentation.Foundation;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace PCLLF.Presentation.Controls;

// LF Controls/MyRadioButton.xaml.vb behavior and visuals; see root LICENCE.
// C# structure reference only: CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5,
// Plain Craft Launcher 2/Controls/MyRadioButton.xaml.cs; CE LICENSE and Plain Craft Launcher 2/LICENCE.
[ContentProperty("Inlines")]
public partial class MyRadioButtonView : Border {
    public delegate void CheckEventHandler(object sender, bool raiseByMouse);
    public delegate void PreviewClickEventHandler(object sender, ControlPreviewEventArgs e);
    public event CheckEventHandler? Check;
    public event PreviewClickEventHandler? PreviewClick;
    public int Uuid;

    public MyRadioButtonView() {
        Uuid = ControlServices.Current.NewUuid();
        if (GetType().Assembly == typeof(MyRadioButtonView).Assembly) InitializeComponent();
        else if (!_contentLoaded) {
            _contentLoaded = true;
            CompiledControlLoader.Load(this, typeof(MyRadioButtonView), "controls/myradiobuttonview.xaml");
        }
        MouseLeftButtonUp += (_, _) => Radiobox_MouseUp();
        MouseLeftButtonDown += (_, _) => Radiobox_MouseDown();
        MouseLeave += (_, _) => Radiobox_MouseLeave();
        MouseEnter += RefreshColor;
        MouseLeave += RefreshColor;
        Loaded += RefreshColor;
    
        if (IsInitialized) LegacyThemePalette.Watch(this, () => RefreshColor());
        else Initialized += (_, _) => LegacyThemePalette.Watch(this, () => RefreshColor());
    }

    public string Logo {
        get => ShapeLogo.Data!.ToString();
        set => ShapeLogo.Data = (Geometry)new GeometryConverter().ConvertFromString(value)!;
    }
    private double _LogoScale = 1;
    public double LogoScale {
        get => _LogoScale;
        set {
            _LogoScale = value;
            if (ShapeLogo is not null) ShapeLogo.RenderTransform = new ScaleTransform { ScaleX = LogoScale, ScaleY = LogoScale };
        }
    }

    private bool _Checked;
    public bool Checked {
        get => _Checked;
        set => SetChecked(value, false, true);
    }
    public void SetChecked(bool value, bool raiseByMouse, bool anime) {
        try {
            bool isChanged = false;
            if (_Checked != value) { _Checked = value; isChanged = true; }
            if (Parent is null) return;
            if (Parent is not Panel panel) throw new MissingMemberException(Parent.GetType().FullName, "Children");
            List<MyRadioButtonView> radioboxList = new();
            int checkedCount = 0;
            foreach (UIElement control in panel.Children) {
                if (control is MyRadioButtonView radio) {
                    radioboxList.Add(radio);
                    if (radio.Checked) checkedCount++;
                }
            }
            switch (checkedCount) {
                case 0:
                    radioboxList[0].Checked = true;
                    break;
                case > 1:
                    if (Checked) {
                        foreach (MyRadioButtonView radio in radioboxList)
                            if (radio.Checked && !radio.Equals(this)) radio.Checked = false;
                    } else {
                        bool firstChecked = false;
                        foreach (MyRadioButtonView radio in radioboxList) {
                            if (radio.Checked) {
                                if (firstChecked) radio.Checked = false;
                                else firstChecked = true;
                            }
                        }
                    }
                    break;
            }
            if (!isChanged) return;
            RefreshColor(null, anime);
            if (Checked) OnCheck(raiseByMouse);
            ControlServices.Current.RaiseCustomEvent(this);
        } catch (Exception error) {
            ControlServices.Current.Error(error, "单选按钮勾选改变错误");
        }
    }
    protected virtual void OnCheck(bool raiseByMouse) => Check?.Invoke(this, raiseByMouse);
    protected virtual bool RaisePreviewClick(bool raiseByMouse) {
        ControlPreviewEventArgs args = new(raiseByMouse);
        PreviewClick?.Invoke(this, args);
        return args.Handled;
    }

    public InlineCollection Inlines => LabText.Inlines;
    public string Text {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(MyRadioButtonView),
        new PropertyMetadata((sender, args) => {
            if (sender is not null) ((MyRadioButtonView)sender).LabText.Text = (string)args.NewValue;
        }));
    public enum ColorState { White, Highlight }
    private ColorState _ColorType = ColorState.White;
    public ColorState ColorType {
        get => _ColorType;
        set { _ColorType = value; RefreshColor(); }
    }

    private bool IsMouseDown;
    private void Radiobox_MouseUp() {
        if (Checked) return;
        if (!IsMouseDown) return;
        IsMouseDown = false;
        ActivateRadioButton();
    }
    /// <summary>Uses the same preview/check pipeline for keyboard or proxy navigation.</summary>
    protected void ActivateRadioButton() {
        if (!IsEnabled || Checked) return;
        ControlServices.Current.Info($"按下单选按钮：{Text}");
        if (RaisePreviewClick(true)) return;
        SetChecked(true, true, true);
    }
    private void Radiobox_MouseDown() {
        if (Checked) return;
        IsMouseDown = true;
        RefreshColor();
    }
    private void Radiobox_MouseLeave() { IsMouseDown = false; }

    private const int AnimationTimeOfMouseIn = 90;
    private const int AnimationTimeOfMouseOut = 150;
    private const int AnimationTimeOfCheck = 120;
    private void RefreshColor(object? obj = null, object? e = null) {
        try {
            ControlServiceHost services = ControlServices.Current;
            ControlAnimations animations = services.Animations;
            ControlColor titleForeground = LegacyThemePalette.Color("ColorBrushTitleForeground");
            if (IsLoaded && services.AnimationEnabled() && !false.Equals(e)) {
                switch (ColorType) {
                    case ColorState.White:
                        if (Checked) {
                            animations.Start(new[] {
                                animations.ColorResource(ShapeLogo, Path.FillProperty, "ColorBrush3", new(AnimationTimeOfCheck)),
                                animations.ColorResource(LabText, TextBlock.ForegroundProperty, "ColorBrush3", new(AnimationTimeOfCheck))
                            }, "MyRadioButton Checked " + Uuid);
                            animations.Start(animations.ColorTo(this, BackgroundProperty, LegacyThemePalette.Color("ColorBrushTitleSelected"), new(AnimationTimeOfCheck)), "MyRadioButton Color " + Uuid);
                        } else if (IsMouseDown) {
                            animations.Start(animations.ColorTo(this, BackgroundProperty, titleForeground.WithAlpha(120), new(60)), "MyRadioButton Color " + Uuid);
                        } else if (IsMouseOver) {
                            animations.Start(new[] {
                                animations.ColorTo(ShapeLogo, Path.FillProperty, titleForeground, new(AnimationTimeOfMouseIn)),
                                animations.ColorTo(LabText, TextBlock.ForegroundProperty, titleForeground, new(AnimationTimeOfMouseIn))
                            }, "MyRadioButton Checked " + Uuid);
                            animations.Start(animations.ColorTo(this, BackgroundProperty, titleForeground.WithAlpha(50), new(AnimationTimeOfMouseIn)), "MyRadioButton Color " + Uuid);
                        } else {
                            animations.Start(new[] {
                                animations.ColorTo(ShapeLogo, Path.FillProperty, titleForeground, new(AnimationTimeOfMouseOut)),
                                animations.ColorTo(LabText, TextBlock.ForegroundProperty, titleForeground, new(AnimationTimeOfMouseOut))
                            }, "MyRadioButton Checked " + Uuid);
                            animations.Start(animations.ColorTo(this, BackgroundProperty, services.ThemeColor(ControlThemeColor.SemiTransparent), new(AnimationTimeOfMouseOut)), "MyRadioButton Color " + Uuid);
                        }
                        break;
                    case ColorState.Highlight:
                        if (Checked) {
                            animations.Start(new[] {
                                animations.ColorTo(ShapeLogo, Path.FillProperty, LegacyThemePalette.Color("ColorBrushOnAccent"), new(AnimationTimeOfCheck)),
                                animations.ColorTo(LabText, TextBlock.ForegroundProperty, LegacyThemePalette.Color("ColorBrushOnAccent"), new(AnimationTimeOfCheck))
                            }, "MyRadioButton Checked " + Uuid);
                            animations.Start(animations.ColorResource(this, BackgroundProperty, "ColorBrush3", new(AnimationTimeOfCheck)), "MyRadioButton Color " + Uuid);
                        } else if (IsMouseDown) {
                            animations.Start(animations.ColorResource(this, BackgroundProperty, "ColorBrush6", new(AnimationTimeOfMouseIn)), "MyRadioButton Color " + Uuid);
                        } else if (IsMouseOver) {
                            animations.Start(new[] {
                                animations.ColorResource(ShapeLogo, Path.FillProperty, "ColorBrush3", new(AnimationTimeOfMouseIn)),
                                animations.ColorResource(LabText, TextBlock.ForegroundProperty, "ColorBrush3", new(AnimationTimeOfMouseIn))
                            }, "MyRadioButton Checked " + Uuid);
                            animations.Start(animations.ColorResource(this, BackgroundProperty, "ColorBrush7", new(AnimationTimeOfMouseIn)), "MyRadioButton Color " + Uuid);
                        } else {
                            animations.Start(new[] {
                                animations.ColorResource(ShapeLogo, Path.FillProperty, "ColorBrush3", new(AnimationTimeOfMouseOut)),
                                animations.ColorResource(LabText, TextBlock.ForegroundProperty, "ColorBrush3", new(AnimationTimeOfMouseOut))
                            }, "MyRadioButton Checked " + Uuid);
                            animations.Start(animations.ColorTo(this, BackgroundProperty, services.ThemeColor(ControlThemeColor.SemiTransparent), new(AnimationTimeOfMouseOut)), "MyRadioButton Color " + Uuid);
                        }
                        break;
                }
            } else {
                animations.Stop("MyRadioButton Checked " + Uuid);
                animations.Stop("MyRadioButton Color " + Uuid);
                switch (ColorType) {
                    case ColorState.White:
                        if (Checked) {
                            SetResourceReference(BackgroundProperty, "ColorBrushTitleSelected");
                            ShapeLogo.SetResourceReference(Path.FillProperty, "ColorBrush3");
                            LabText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3");
                        } else {
                            Background = services.ThemeColor(ControlThemeColor.SemiTransparent).ToBrush();
                            ShapeLogo.SetResourceReference(Path.FillProperty, "ColorBrushTitleForeground");
                            LabText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushTitleForeground");
                        }
                        break;
                    case ColorState.Highlight:
                        if (Checked) {
                            SetResourceReference(BackgroundProperty, "ColorBrush3");
                            ShapeLogo.SetResourceReference(Path.FillProperty, "ColorBrushOnAccent");
                            LabText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushOnAccent");
                        } else {
                            Background = services.ThemeColor(ControlThemeColor.SemiTransparent).ToBrush();
                            ShapeLogo.SetResourceReference(Path.FillProperty, "ColorBrush3");
                            LabText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3");
                        }
                        break;
                }
            }
        } catch (Exception error) {
            ControlServices.Current.Warn(error, "刷新单选按钮颜色出错");
        }
    }
}
