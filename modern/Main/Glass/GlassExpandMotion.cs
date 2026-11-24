// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>Interruptible body motion for the native Expander; expansion semantics stay on IsExpanded.</summary>
public static class GlassExpandMotion {
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GlassExpandMotion), new PropertyMetadata(false, OnEnabledChanged));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion", typeof(bool), typeof(GlassExpandMotion), new PropertyMetadata(false, OnReduceMotionChanged));
    public static readonly DependencyProperty CascadeAllItemsProperty = DependencyProperty.RegisterAttached(
        "CascadeAllItems", typeof(bool), typeof(GlassExpandMotion), new PropertyMetadata(false));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(GlassExpandMotion));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetReduceMotion(DependencyObject element) => (bool)element.GetValue(ReduceMotionProperty);
    public static void SetReduceMotion(DependencyObject element, bool value) => element.SetValue(ReduceMotionProperty, value);
    public static bool GetCascadeAllItems(DependencyObject element) => (bool)element.GetValue(CascadeAllItemsProperty);
    public static void SetCascadeAllItems(DependencyObject element, bool value) => element.SetValue(CascadeAllItemsProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) {
        if (element is not Expander expander) return;
        (element.GetValue(StateProperty) as State)?.Dispose();
        element.ClearValue(StateProperty);
        if ((bool)args.NewValue) element.SetValue(StateProperty, new State(expander));
    }
    private static void OnReduceMotionChanged(DependencyObject element, DependencyPropertyChangedEventArgs args) {
        if (element.GetValue(StateProperty) is State state) state.Update(true);
    }

    private sealed class State : IDisposable {
        private readonly Expander _owner;
        private readonly DependencyPropertyDescriptor _template = DependencyPropertyDescriptor.FromProperty(Control.TemplateProperty, typeof(Expander));
        private readonly DependencyPropertyDescriptor _content = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Expander));
        private FrameworkElement _viewport, _body;
        private SpringMotion _height, _opacity;
        private DispatcherOperation _pending;
        private double _desiredHeight;
        private bool _loadingRows;
        // True when the body has nothing but its own margin (rows are still being built after the toggle).
        private bool BodyEmpty => _body is null || _body.DesiredSize.Height <= _body.Margin.Top + _body.Margin.Bottom + 1;
        private bool _active, _binding, _disposed;

        internal State(Expander owner) {
            _owner = owner;
            owner.Loaded += OnLoaded;
            owner.Unloaded += OnUnloaded;
            if (owner.IsLoaded) Activate();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Activate();
        private void Activate() {
            if (_active || _disposed) return;
            _active = true;
            _owner.Expanded += OnExpansionChanged;
            _owner.Collapsed += OnExpansionChanged;
            _owner.SizeChanged += OnOwnerSizeChanged;
            _template?.AddValueChanged(_owner, OnTemplateChanged);
            _content?.AddValueChanged(_owner, OnContentChanged);
            BindParts();
        }

        private void BindParts() {
            if (!_active || _binding) return;
            _binding = true;
            try {
                ReleaseParts();
                _owner.ApplyTemplate();
                _viewport = _owner.Template?.FindName("PART_BodyViewport", _owner) as FrameworkElement;
                _body = _owner.Template?.FindName("PART_BodyContent", _owner) as FrameworkElement;
                if (_viewport is null || _body is null) return;
                _viewport.ClipToBounds = true;
                // The body is measured at its natural height inside the clipped viewport, so its size reports late content.
                _body.SizeChanged += OnBodySizeChanged;
                bool expanded = _owner.IsExpanded;
                _desiredHeight = MeasureBodyHeight();
                // A long list costs a heavy first frame; capping each step keeps that frame from skipping the reveal.
                _height = new SpringMotion(_owner, expanded ? _desiredHeight : 0, ApplyHeight, MotionTokens.Reveal) { MaxFrameSeconds = 1 / 40d };
                _opacity = new SpringMotion(_owner, expanded ? 1 : 0, value => {
                    if (_body is not null) _body.Opacity = Math.Clamp(value, 0, 1);
                }, MotionTokens.Reveal) { MaxFrameSeconds = 1 / 40d };
                // Applying a template must reflect the current value, never generate an initial expansion.
                Update(true);
            } finally { _binding = false; }
        }

        internal void Update(bool immediate = false) {
            if (!_active || _disposed || _height is null || _viewport is null || _body is null) return;
            bool expanded = _owner.IsExpanded;
            double currentHeight = _viewport.ActualHeight;
            _desiredHeight = MeasureBodyHeight();
            if (double.IsNaN(_viewport.Height)) {
                // Auto height is used only at rest. Capture it before returning to an animated height.
                _height.Snap(Math.Max(0, currentHeight));
                _viewport.Height = Math.Max(0, currentHeight);
            }
            if (expanded) _viewport.Visibility = Visibility.Visible;
            _viewport.IsHitTestVisible = expanded;
            bool snap = immediate || GetReduceMotion(_owner) || !MotionPreferences.AllowsAnimation;
            _height.Retarget(expanded ? _desiredHeight : 0, snap);
            _opacity.Retarget(expanded ? 1 : 0, snap);
        }

        private double MeasureBodyHeight() {
            if (_body is null || _viewport is null) return 0;
            double horizontalChrome = 0, verticalChrome = 0;
            if (_viewport is Border border) {
                horizontalChrome = border.Padding.Left + border.Padding.Right + border.BorderThickness.Left + border.BorderThickness.Right;
                verticalChrome = border.Padding.Top + border.Padding.Bottom + border.BorderThickness.Top + border.BorderThickness.Bottom;
            }
            double width = _viewport.ActualWidth > 0 ? _viewport.ActualWidth : _owner.ActualWidth;
            if (!double.IsFinite(width) || width <= 0) width = double.PositiveInfinity;
            // Measure everything inside the viewport (body plus any template chrome such as a header rule).
            UIElement measured = (_viewport as Border)?.Child ?? _body;
            measured.Measure(new Size(Math.Max(0, width - horizontalChrome), double.PositiveInfinity));
            double height = measured.DesiredSize.Height + verticalChrome;
            return double.IsFinite(height) ? Math.Max(0, height) : 0;
        }

        private void ApplyHeight(double value) {
            if (_viewport is null) return;
            _viewport.Height = Math.Max(0, value);
            if (!_owner.IsExpanded && value <= .01) _viewport.Visibility = Visibility.Collapsed;
            // An empty body (rows still loading) stays pinned at zero so their arrival animates instead of popping in.
            else if (_owner.IsExpanded && !BodyEmpty && _height is { IsRunning: false } && Math.Abs(value - _desiredHeight) <= .01)
                _viewport.Height = double.NaN; // Expanded content follows normal wrapping/resizing at rest.
        }

        private void OnExpansionChanged(object sender, RoutedEventArgs args) {
            if (!ReferenceEquals(args.Source, _owner)) return; // Ignore a nested expander's routed event.
            Update();
            _loadingRows = _owner.IsExpanded && BodyEmpty;
            if (_owner.IsExpanded && !_loadingRows) PlayCascade();
            QueueRefresh(); // Lazy content bindings can materialize after the native toggle event.
        }
        // Opening a section cascades its first screenful of rows in, the same rhythm as page items. Rows beyond that
        // are revealed by the height spring alone, so a list of hundreds of versions does not animate every row.
        private const int CascadeRows = 16;
        private void PlayCascade() {
            if (_body is null || GetReduceMotion(_owner)) return;
            _body.UpdateLayout();
            if (StaggeredEntrance.FindItemsPanel(_body) is System.Windows.Controls.Panel rows) {
                var visible = rows.Children.OfType<UIElement>().Where(row => row.Visibility == Visibility.Visible);
                StaggeredEntrance.Play(GetCascadeAllItems(_owner) ? visible : visible.Take(CascadeRows));
            }
        }

        private void OnTemplateChanged(object sender, EventArgs args) => BindParts();
        private void OnContentChanged(object sender, EventArgs args) => QueueRefresh();
        private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs args) {
            // Height is our output: observing it would create a layout feedback loop.
            if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) > .25 && _owner.IsExpanded) QueueRefresh();
        }
        private void QueueRefresh() {
            if (!_active || _disposed || _owner.Dispatcher.HasShutdownStarted || _pending?.Status == DispatcherOperationStatus.Pending) return;
            _pending = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                _pending = null;
                if (!_active || _disposed) return;
                if (Math.Abs(MeasureBodyHeight() - _desiredHeight) <= .5) return;
                // Content that arrived after the toggle (a deferred load) continues the running reveal and cascades in.
                bool grew = _owner.IsExpanded && _loadingRows;
                Update();
                _loadingRows = BodyEmpty;
                if (grew && !_loadingRows) PlayCascade();
            }));
        }

        private void OnUnloaded(object sender, RoutedEventArgs args) => Deactivate();
        private void Deactivate() {
            if (!_active) return;
            _active = false;
            if (_pending?.Status == DispatcherOperationStatus.Pending) _pending.Abort();
            _pending = null;
            _owner.Expanded -= OnExpansionChanged;
            _owner.Collapsed -= OnExpansionChanged;
            _owner.SizeChanged -= OnOwnerSizeChanged;
            _template?.RemoveValueChanged(_owner, OnTemplateChanged);
            _content?.RemoveValueChanged(_owner, OnContentChanged);
            ReleaseParts();
        }
        private void OnBodySizeChanged(object sender, SizeChangedEventArgs args) {
            if (_owner.IsExpanded && Math.Abs(args.NewSize.Height - args.PreviousSize.Height) > .5) QueueRefresh();
        }

        private void ReleaseParts() {
            if (_body is not null) _body.SizeChanged -= OnBodySizeChanged;
            _height?.Dispose(); _height = null;
            _opacity?.Dispose(); _opacity = null;
            if (_viewport is not null) {
                _viewport.Height = double.NaN;
                _viewport.Visibility = _owner.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                _viewport.IsHitTestVisible = _owner.IsExpanded;
            }
            if (_body is not null) _body.Opacity = 1;
            _viewport = null; _body = null;
        }
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            Deactivate();
            _owner.Loaded -= OnLoaded;
            _owner.Unloaded -= OnUnloaded;
        }
    }
}
