// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>
/// Clips a window to a union of rounded client-coordinate rectangles. This controller
/// changes only the native window region; it creates no material or additional window.
/// Use one region owner per window and call its methods on the window's dispatcher.
/// </summary>
public sealed class GlassWindowRegionClip : IDisposable {
    private const int RgnOr = 2;
    private const int ErrorGenFailure = 31;
    private const int ErrorArithmeticOverflow = 534;

    private readonly Window _window;
    private readonly HwndSourceHook _hook;
    private HwndSource? _source;
    private nint _handle;
    private GlassWindowRegion[] _regions = [];
    private PixelRegion[]? _appliedRegions;
    private DispatcherOperation? _refreshOperation;
    private bool _hasRegions;
    private bool _ownsRegion;
    private bool _applying;
    private bool _disposed;

    private GlassWindowRegionClip(Window window) {
        _window = window;
        _hook = WindowHook;
        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
        window.SizeChanged += OnSizeChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
        if (PresentationSource.FromVisual(window) is HwndSource source) InitializeSource(source);
    }

    /// <summary>Attaches before or after the window's native source is initialized.</summary>
    public static GlassWindowRegionClip Attach(Window window) {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        return new GlassWindowRegionClip(window);
    }

    /// <summary>Zero after a successful region update; otherwise the native error code.</summary>
    public int LastRegionError { get; private set; }

    /// <summary>
    /// Sets rounded shapes in client DIPs. An empty sequence removes this controller's
    /// region. Opacity does not affect the native hit shape; omit unwanted shapes.
    /// </summary>
    public void SetRegions(IEnumerable<GlassWindowRegion> regions) {
        VerifyUsable();
        ArgumentNullException.ThrowIfNull(regions);
        GlassWindowRegion[] snapshot = regions.ToArray();
        foreach (GlassWindowRegion region in snapshot) {
            Rect bounds = region.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
                !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
                !double.IsFinite(region.CornerRadius) || region.CornerRadius < 0 ||
                !double.IsFinite(region.Opacity) || region.Opacity < 0 || region.Opacity > 1)
                throw new ArgumentOutOfRangeException(nameof(regions), "Window regions require finite, positive bounds and a nonnegative radius.");
        }
        _regions = snapshot;
        _hasRegions = true;
        if (_applying) QueueRefresh();
        else ApplyRegions();
    }

    public void Dispose() {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        if (_refreshOperation?.Status == DispatcherOperationStatus.Pending) _refreshOperation.Abort();
        _refreshOperation = null;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnLoaded;
        _window.SizeChanged -= OnSizeChanged;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
        _source?.RemoveHook(_hook);
        // Do not reset a pre-existing region if this controller never set one.
        if (_ownsRegion && _handle != 0 && IsWindow(_handle)) {
            if (SetWindowRgn(_handle, 0, true) == 0) RecordNativeError();
            else LastRegionError = 0;
        }
        _ownsRegion = false;
        _appliedRegions = null;
        _source = null;
        _handle = 0;
    }

    private void VerifyUsable() {
        _window.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void InitializeSource(HwndSource source) {
        if (_disposed || ReferenceEquals(source, _source)) return;
        _source?.RemoveHook(_hook);
        _source = source;
        _handle = source.Handle;
        _appliedRegions = null;
        _ownsRegion = false;
        source.AddHook(_hook);
        QueueRefresh();
    }

    private void OnSourceInitialized(object? sender, EventArgs args) {
        if (PresentationSource.FromVisual(_window) is HwndSource source) InitializeSource(source);
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => QueueRefresh();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) {
        _appliedRegions = null;
        QueueRefresh();
    }
    private void OnStateChanged(object? sender, EventArgs args) {
        _appliedRegions = null;
        QueueRefresh();
    }
    private void OnClosed(object? sender, EventArgs args) => Dispose();

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) {
        if (_disposed || _applying) return 0;
        // Observe after WPF handles the native change; never consume its sizing/DPI input.
        if (message is 0x0005 or 0x02E0) { // WM_SIZE / WM_DPICHANGED.
            _appliedRegions = null;
            QueueRefresh();
        } else if (message is 0x0047 or 0x0018) { // WM_WINDOWPOSCHANGED / WM_SHOWWINDOW.
            // SetWindowRgn itself generates position notifications. Keep the pixel cache
            // here so a deferred notification cannot start a region-update feedback loop.
            QueueRefresh();
        }
        return 0;
    }

    private void QueueRefresh() {
        if (_disposed || _window.Dispatcher.HasShutdownStarted ||
            _refreshOperation?.Status == DispatcherOperationStatus.Pending) return;
        _refreshOperation = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            _refreshOperation = null;
            if (!_disposed) ApplyRegions();
        }));
    }

    private void ApplyRegions() {
        if (_disposed || _applying || !_hasRegions || _handle == 0 ||
            _source?.CompositionTarget is not { } target || !IsWindow(_handle) || IsIconic(_handle)) return;
        var origin = new NativePoint();
        if (!ClientToScreen(_handle, ref origin) || !GetWindowRect(_handle, out NativeRect windowBounds)) {
            RecordNativeError();
            return;
        }
        var transform = target.TransformToDevice;
        PixelRegion[] pixels;
        try {
            int offsetX = checked(origin.X - windowBounds.Left);
            int offsetY = checked(origin.Y - windowBounds.Top);
            pixels = _regions.Select(region => {
                Rect bounds = region.Bounds;
                double radius = Math.Min(region.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);
                return new PixelRegion(
                    checked((int)Math.Floor(bounds.Left * transform.M11) + offsetX),
                    checked((int)Math.Floor(bounds.Top * transform.M22) + offsetY),
                    checked((int)Math.Ceiling(bounds.Right * transform.M11) + offsetX + 1),
                    checked((int)Math.Ceiling(bounds.Bottom * transform.M22) + offsetY + 1),
                    checked((int)Math.Round(radius * transform.M11 * 2)),
                    checked((int)Math.Round(radius * transform.M22 * 2)));
            }).ToArray();
        } catch (OverflowException) {
            LastRegionError = ErrorArithmeticOverflow;
            return;
        }
        if (_appliedRegions is not null && _appliedRegions.SequenceEqual(pixels)) {
            LastRegionError = 0;
            return;
        }
        _applying = true;
        try {
            if (pixels.Length == 0) {
                if (_ownsRegion && SetWindowRgn(_handle, 0, true) == 0) { RecordNativeError(); return; }
                _ownsRegion = false;
                _appliedRegions = pixels;
                LastRegionError = 0;
                return;
            }
            nint union = CreateRectRgn(0, 0, 0, 0);
            if (union == 0) { RecordNativeError(); return; }
            try {
                foreach (PixelRegion region in pixels) {
                    // GDI's zero-ellipse round rectangle still drops its final row/column.
                    // A rectangular native envelope must retain those pixels for WPF's
                    // antialiased mask and client-to-screen integer coordinate rounding.
                    nint shape = region.EllipseWidth == 0 || region.EllipseHeight == 0
                        ? CreateRectRgn(region.Left, region.Top, region.Right, region.Bottom)
                        : CreateRoundRectRgn(region.Left, region.Top, region.Right, region.Bottom, region.EllipseWidth, region.EllipseHeight);
                    if (shape == 0) { RecordNativeError(); return; }
                    try {
                        if (CombineRgn(union, union, shape, RgnOr) == 0) { RecordNativeError(); return; }
                    } finally { DeleteObject(shape); }
                }
                if (SetWindowRgn(_handle, union, false) == 0) { RecordNativeError(); return; }
                // A successful SetWindowRgn transfers ownership of the union to Windows.
                union = 0;
                _ownsRegion = true;
                _appliedRegions = pixels;
                LastRegionError = 0;
            } finally {
                if (union != 0) DeleteObject(union);
            }
        } finally { _applying = false; }
    }

    private void RecordNativeError() {
        int error = Marshal.GetLastWin32Error();
        LastRegionError = error == 0 ? ErrorGenFailure : error;
    }

    private readonly record struct PixelRegion(int Left, int Top, int Right, int Bottom, int EllipseWidth, int EllipseHeight);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
}
