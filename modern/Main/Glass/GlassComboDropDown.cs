// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>Routes wheel input and dismisses a glass combo box when its containing page scrolls.</summary>
public static class GlassComboDropDown
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(GlassComboDropDown), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(GlassComboDropDown));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ComboBox combo) return;
        (combo.GetValue(StateProperty) as State)?.Dispose();
        combo.ClearValue(StateProperty);
        if ((bool)args.NewValue) combo.SetValue(StateProperty, new State(combo));
    }

    private sealed class State : IDisposable
    {
        private const double OptionScrollPerNotch = 72;
        private readonly ComboBox _combo;
        private readonly List<ScrollViewer> _hosts = new();
        private ScrollViewer? _options;

        internal State(ComboBox combo)
        {
            _combo = combo;
            combo.DropDownOpened += OnOpened;
            combo.DropDownClosed += OnClosed;
            combo.Unloaded += OnUnloaded;
            if (combo.IsDropDownOpen) OnOpened(combo, EventArgs.Empty);
        }

        private void OnOpened(object? sender, EventArgs args)
        {
            Detach();
            for (DependencyObject? parent = VisualTreeHelper.GetParent(_combo); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not ScrollViewer host) continue;
                SmoothScroll.Stop(host);
                host.ScrollChanged += OnHostScrolled;
                _hosts.Add(host);
            }
            _combo.PreviewMouseWheel += OnCapturedWheel;
            _options = _combo.Template?.FindName("DropDownScroll", _combo) as ScrollViewer;
            if (_options is not null) _options.PreviewMouseWheel += OnOptionsWheel;
        }

        private void OnOptionsWheel(object sender, MouseWheelEventArgs args)
        {
            if (!_combo.IsDropDownOpen || args.Delta == 0 || sender is not ScrollViewer options) return;
            // The Glass combo used pixel offsets for wheel input before the native-page migration.
            double offset = options.VerticalOffset - args.Delta / 120d * OptionScrollPerNotch;
            options.ScrollToVerticalOffset(Math.Clamp(offset, 0, options.ScrollableHeight));
            args.Handled = true;
        }

        private void OnCapturedWheel(object sender, MouseWheelEventArgs args)
        {
            // Subtree capture redirects wheel input outside the popup to the combo's main visual tree.
            // If the host did not consume it in preview, let the native ScrollViewer handle it.
            if (!_combo.IsDropDownOpen || args.Delta == 0 || args.OriginalSource is not Visual origin ||
                (!ReferenceEquals(origin, _combo) && !_combo.IsAncestorOf(origin))) return;
            foreach (ScrollViewer host in _hosts)
            {
                if (args.Delta > 0 ? host.VerticalOffset <= 0 : host.VerticalOffset >= host.ScrollableHeight) continue;
                var forwarded = new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = Mouse.MouseWheelEvent };
                host.RaiseEvent(forwarded);
                if (!forwarded.Handled) continue;
                args.Handled = true;
                return;
            }
        }

        private void OnHostScrolled(object sender, ScrollChangedEventArgs args)
        {
            if (_combo.IsDropDownOpen && ReferenceEquals(args.OriginalSource, sender) &&
                (args.VerticalChange != 0 || args.HorizontalChange != 0))
                _combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        }

        private void OnClosed(object? sender, EventArgs args) => Detach();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();

        private void Detach()
        {
            _combo.PreviewMouseWheel -= OnCapturedWheel;
            if (_options is not null) _options.PreviewMouseWheel -= OnOptionsWheel;
            _options = null;
            foreach (ScrollViewer host in _hosts) host.ScrollChanged -= OnHostScrolled;
            _hosts.Clear();
        }

        public void Dispose()
        {
            Detach();
            _combo.DropDownOpened -= OnOpened;
            _combo.DropDownClosed -= OnClosed;
            _combo.Unloaded -= OnUnloaded;
        }
    }
}
