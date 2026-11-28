// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCLCS.Validation;
using PCLLF.Presentation.Accounts;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

internal sealed record DialogChoice(string Label, string? Help = null, bool Selected = false);

internal interface IGlassDialogContent {
    double PreferredWidth { get; }
    bool ShowRequestCaption => false;
}

internal interface IGlassDialogConfirmation {
    bool CanConfirm { get; }
    event EventHandler? ConfirmationChanged;
}

/// <summary>Shows requests from the launcher message queue with native glass controls.</summary>
internal sealed class GlassMessageDialog : Grid, IGlassModalState, IGlassModalTone, IDisposable {
    private readonly ModMain.MyMsgBoxConverter _request;
    private readonly Action<GlassMessageDialog> _removed;
    private readonly GlassPageResources _resources;
    private readonly GlassSurface _card;
    private readonly SpringMotion _motion;
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _scale = new();
    private readonly Button[] _buttons;
    private readonly TextBox? _input;
    private readonly TextBlock? _validation;
    private readonly DialogContentHost? _nativeContentHost;
    private readonly IGlassDialogConfirmation? _confirmation;
    private readonly LegacyDeviceLoginDialog<ModMain.MyMsgBoxConverter>? _deviceLogin;
    private int? _selection;
    private bool _deviceLoginStarted, _removalQueued, _disposed;

    internal string Title => _request.Title ?? string.Empty;
    internal bool IsDeviceLogin => _deviceLogin is not null;
    bool IGlassModalState.IsModalOpen => !_request.IsExited;
    bool IGlassModalTone.IsWarning => _request.IsWarn;

    internal GlassMessageDialog(ModMain.MyMsgBoxConverter request, Action drag, Action<GlassMessageDialog> removed) {
        _request = request;
        _removed = removed;
        if (request.Type == ModMain.MyMsgBoxType.Login) {
            request.Title = "登录 Minecraft";
            request.Button1 = "打开网页";
            request.Button2 = "复制代码";
            request.Button3 = "取消";
        }
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = _resources.Text(Title);
        title.FontSize = 21;
        title.FontWeight = FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.Wrap;
        title.Margin = new Thickness(2, 0, 2, 15);
        if (request.IsWarn) title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationWarnText");
        title.MouseLeftButtonDown += (_, _) => drag();
        content.Children.Add(title);

        TextBlock? message = null;
        FrameworkElement bodyView;
        double width = request.Title == "Minecraft 出现错误" ? 860 : 570;
        if (request.ContentFactory is { } factory) {
            FrameworkElement customContent = factory();
            if (customContent is not IGlassDialogContent dialogContent)
                throw new InvalidOperationException("弹窗内容未提供新版布局信息。");
            width = dialogContent.PreferredWidth;
            _confirmation = customContent as IGlassDialogConfirmation;
            _nativeContentHost = new DialogContentHost(customContent);
            _nativeContentHost.Resources[typeof(CheckBox)] = _resources.Style("GlassPresentationCheckBox");
            _nativeContentHost.Resources[typeof(ComboBox)] = _resources.Style("GlassPresentationComboBox");
            _nativeContentHost.Resources[typeof(TextBlock)] = _resources.Style("GlassPresentationTextStyle");
            _nativeContentHost.Resources[typeof(TextBox)] = _resources.Style("GlassPresentationTextBox");
            if (dialogContent.ShowRequestCaption && !string.IsNullOrWhiteSpace(request.Text)) {
                var caption = _resources.Text(request.Text);
                caption.TextWrapping = TextWrapping.Wrap;
                caption.Margin = new Thickness(0, 0, 0, 10);
                var body = new Grid();
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                body.RowDefinitions.Add(new RowDefinition());
                body.Children.Add(caption);
                Grid.SetRow(_nativeContentHost, 1);
                body.Children.Add(_nativeContentHost);
                bodyView = body;
            } else bodyView = _nativeContentHost;
            // Stacked forms and notices may extend past the body row. Leave grid/document views in the finite row
            // so their own scrolling regions continue to size against the dialog.
            if (customContent is StackPanel) bodyView = CreateBodyScroll(bodyView, smooth: false);
        } else {
            var body = new StackPanel();
            message = _resources.Text(request.Text);
            if (!string.IsNullOrWhiteSpace(request.Text) || request.Type == ModMain.MyMsgBoxType.Login) {
                message.TextWrapping = TextWrapping.Wrap;
                message.Margin = new Thickness(0, 0, 0, 6);
                body.Children.Add(message);
            }
            if (request.Type == ModMain.MyMsgBoxType.Input) {
                _input = new TextBox { Style = _resources.Style("GlassPresentationTextBox"), Text = request.Content as string ?? string.Empty,
                    Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 3, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetName(_input, request.HintText.Length > 0 ? request.HintText : Title);
                var placeholder = _resources.Note(request.HintText, 13);
                placeholder.Margin = new Thickness(12, 3, 12, 0);
                placeholder.VerticalAlignment = VerticalAlignment.Center;
                placeholder.IsHitTestVisible = false;
                GlassPageResources.BindTextPlaceholder(_input, placeholder);
                body.Children.Add(new Grid { Children = { _input, placeholder } });
                _validation = _resources.Note(string.Empty, 12.5);
                _validation.Margin = new Thickness(0, 6, 0, 0);
                _validation.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
                body.Children.Add(_validation);
            } else if (request.Content is IReadOnlyList<DialogChoice> choices) {
                for (int index = 0; index < choices.Count; index++) {
                    int choiceIndex = index;
                    DialogChoice choice = choices[index];
                    var radio = new RadioButton { Content = choice.Label, ToolTip = choice.Help,
                        Style = _resources.Style("GlassPresentationRadioBox"), IsChecked = choice.Selected, Margin = new Thickness(0, 4, 0, 4) };
                    if (choice.Selected) _selection = choiceIndex;
                    radio.Checked += (_, _) => { _selection = choiceIndex; _buttons[0].IsEnabled = true; };
                    body.Children.Add(radio);
                }
            }
            bodyView = CreateBodyScroll(body, smooth: true);
        }
        Grid.SetRow(bodyView, 1);
        content.Children.Add(bodyView);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _buttons = [CreateButton(request.Button1, 1), CreateButton(request.Button2, 2), CreateButton(request.Button3, 3)];
        if (request.Type == ModMain.MyMsgBoxType.Select) _buttons[0].IsEnabled = _selection.HasValue;
        if (_confirmation is not null) {
            _confirmation.ConfirmationChanged += OnConfirmationChanged;
            OnConfirmationChanged(this, EventArgs.Empty);
        }
        foreach (Button button in _buttons) actions.Children.Add(button);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        if (_input is not null) {
            _input.TextChanged += (_, _) => Validate();
            Validate();
        }
        if (request.Type == ModMain.MyMsgBoxType.Login) {
            _deviceLogin = new LegacyDeviceLoginDialog<ModMain.MyMsgBoxConverter>(new() {
                NewUuid = ModBase.GetUuid,
                Content = value => (JObject)value.Content!,
                IsExited = value => value.IsExited,
                SetExited = (value, exited) => value.IsExited = exited,
                SetResult = (value, result) => value.Result = result,
                RestartException = () => new ModBase.RestartException(),
                OAuthClientId = () => ModSecret.OAuthClientId,
                ClosePresentation = BeginClose,
                SetTitle = value => title.Text = request.Title = value,
                SetCaption = value => message!.Text = value,
                SetWebEvent = address => request.Button1Action = () => ModBase.OpenWebsite(address),
                SetCopyEvent = code => request.Button2Action = () => ModBase.ClipboardSet(code),
                RunInUi = work => ModBase.RunInUi(work),
                RunInNewThread = (work, name) => ModBase.RunInNewThread(work, name),
                OpenWebsite = ModBase.OpenWebsite,
                ClipboardSet = code => ModBase.ClipboardSet(code),
                LaunchLog = value => ModLaunch.McLaunchLog(value),
                Hint = (text, tone) => ModMain.Hint(text, tone switch {
                    PCLLF.Presentation.Accounts.LegacyLoginHint.Red => ModMain.HintType.Red,
                    PCLLF.Presentation.Accounts.LegacyLoginHint.Green => ModMain.HintType.Green,
                    _ => ModMain.HintType.Blue
                }),
                Request = (url, method, content, contentType, timeout, makeLog, requireJson) =>
                    ModNet.NetRequestByClient(url, method, Content: content, ContentType: contentType, Timeout: timeout, MakeLog: makeLog, RequireJson: requireJson)
            });
            request.Button3Action = CancelDeviceLogin;
        }

        _card = new GlassSurface { Child = content, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1), BlurRadius = 30, HighlightOpacity = 0,
            Width = width, Padding = new Thickness(30, 25, 30, 23),
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = new TransformGroup { Children = { _scale, _slide } } };
        GlassMaterial.SetDesktopBackdropEnabled(_card, false);
        _card.SetResourceReference(Border.BackgroundProperty, request.IsWarn ? "GlassPresentationWarnFill" : "GlassPresentationDialogFill");
        _card.SetResourceReference(Border.BorderBrushProperty, request.IsWarn ? "GlassPresentationWarnEdge" : "GlassPresentationDialogEdge");
        _card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 36, ShadowDepth = 10,
            Direction = 270, Opacity = .22, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            Color = request.IsWarn ? Color.FromRgb(90, 10, 10) : Colors.Black };
        Children.Add(_card);
        _motion = new SpringMotion(_card, 0, ApplyMotion, new SpringParameters(.44, .76));
        SizeChanged += (_, args) => {
            _card.MaxWidth = Math.Max(0, args.NewSize.Width - 48);
            _card.MaxHeight = Math.Max(0, args.NewSize.Height - 40);
        };
        Loaded += (_, _) => {
            RefreshPalette();
            if (_request.IsExited || _disposed) return;
            _motion.Retarget(1);
            if (_input is not null) { _input.Focus(); _input.CaretIndex = _input.Text.Length; }
            else _buttons[0].Focus();
        };
        if (_deviceLogin is not null) Loaded += StartDeviceLogin;
    }

    private void StartDeviceLogin(object sender, RoutedEventArgs args) {
        Loaded -= StartDeviceLogin;
        if (_disposed || _request.IsExited) return;
        try {
            _deviceLogin!.Initialize(_request);
            _deviceLoginStarted = true;
        }
        catch (Exception error) {
            _request.Result = error;
            _request.IsExited = true;
            BeginClose();
        }
    }

    private void CancelDeviceLogin() {
        if (_deviceLoginStarted) _deviceLogin!.CancelClick();
        else {
            _request.Result = new OperationCanceledException();
            _request.IsExited = true;
            BeginClose();
        }
    }

    internal void RefreshPalette() => _resources.ApplyPalette();

    private ScrollViewer CreateBodyScroll(FrameworkElement body, bool smooth) {
        var scroll = new ScrollViewer { Content = body, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.SetIsEnabled(scroll, smooth);
        return scroll;
    }

    private void OnConfirmationChanged(object? sender, EventArgs args) =>
        _buttons[0].IsEnabled = _confirmation?.CanConfirm != false;

    private Button CreateButton(string? text, int index) {
        bool warning = _request.IsWarn && _request.WarningButton == index;
        bool primary = index == 1 && !warning && !string.IsNullOrEmpty(_request.Button2);
        var button = new Button { Content = text ?? string.Empty, Style = _resources.Style(primary ? "GlassPresentationAccentButton" : "GlassPresentationButton"), MinWidth = 88,
            Margin = new Thickness(8, 5, 0, 0), Padding = new Thickness(13, 0, 13, 0),
            Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible };
        if (warning)
            button.SetResourceReference(Control.ForegroundProperty, "GlassPresentationDanger");
        button.Click += (_, _) => Invoke(index);
        return button;
    }

    private bool Validate() {
        if (_input is null) return true;
        string error = _request.ValidateRules is { } rules ? LegacyValidation.Validate(_input.Text, rules) : string.Empty;
        _validation!.Text = error;
        _validation.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return _buttons[0].IsEnabled = error.Length == 0;
    }

    internal void HandleKey(Key key) {
        if (_request.IsExited || _disposed) return;
        if (key == Key.Enter) {
            if (Keyboard.FocusedElement is not TextBox { AcceptsReturn: true }) Invoke(1);
        }
        else if (key == Key.Escape) {
            if (IsDeviceLogin) CancelDeviceLogin();
            else {
                _request.Dismiss();
                BeginClose();
            }
        }
    }

    private void Invoke(int index) {
        if (_request.IsExited || _disposed || index == 1 && (!Validate() || !_buttons[0].IsEnabled || _confirmation?.CanConfirm == false)) return;
        Action? action = index switch { 1 => _request.Button1Action, 2 => _request.Button2Action, _ => _request.Button3Action };
        if (action is not null) { action(); return; }
        _request.Result = _request.Type switch {
            ModMain.MyMsgBoxType.Input => index == 1 ? _input?.Text : null,
            ModMain.MyMsgBoxType.Select => index == 1 ? _selection : null,
            _ => index
        };
        _request.IsExited = true;
        BeginClose();
    }

    private void BeginClose() {
        if (_disposed) return;
        _request.WaitFrame.Continue = false;
        IsHitTestVisible = false;
        _motion.Retarget(0);
    }

    private void ApplyMotion(double value) {
        _card.Opacity = Math.Clamp(value, 0, 1);
        _scale.ScaleX = _scale.ScaleY = .96 + .04 * value;
        _slide.Y = 20 * (1 - value);
        if (value == 0 && _request.IsExited && !_removalQueued) {
            _removalQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { if (!_disposed) _removed(this); }));
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _motion.Dispose();
        if (_confirmation is not null) _confirmation.ConfirmationChanged -= OnConfirmationChanged;
        _nativeContentHost?.Detach();
        _request.Dismiss(IsDeviceLogin ? new OperationCanceledException() : null);
    }

    private sealed class DialogContentHost : ContentControl {
        private readonly FrameworkElement _body;
        private readonly double _originalMaxHeight;

        internal DialogContentHost(FrameworkElement body) {
            _body = body;
            _originalMaxHeight = body.MaxHeight;
            Content = body;
            Focusable = false;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
        }

        protected override Size MeasureOverride(Size constraint) {
            // Fixed-height factory views need their own finite limit so their internal viewport fits the body row.
            double limit = Math.Min(_originalMaxHeight, constraint.Height);
            if (_body.MaxHeight != limit) _body.SetCurrentValue(MaxHeightProperty, limit);
            return base.MeasureOverride(constraint);
        }

        internal void Detach() {
            Content = null;
            _body.SetCurrentValue(MaxHeightProperty, _originalMaxHeight);
        }
    }
}
