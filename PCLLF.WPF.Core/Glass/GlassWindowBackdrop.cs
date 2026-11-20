// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PCLLF.Wpf.Glass;

public enum GlassBackdropStatus {
    WaitingForHandle,
    DesktopAcrylic,
    Wallpaper,
    WallpaperOnly,
    TransparencyDisabled,
    HighContrast,
    UnsupportedPlatform,
    LayeredWindowUnsupported,
    Failed,
    Disposed
}

public sealed class GlassBackdropOptions {
    public bool UseDarkTheme { get; init; }
    public bool PreferCompositionBackdrop { get; init; } = true;
    public bool RespectSystemTransparency { get; init; } = true;
    /// <summary>
    /// Owns the main HWND region. When disabled, the caller also paints fallback
    /// surfaces inside its own mask, so this controller keeps the window background transparent.
    /// </summary>
    public bool ManageWindowRegion { get; init; } = true;
    /// <summary>Resolves the composition clips from the current native client size in DIPs, before WPF has arranged its content.</summary>
    public Func<Size, IReadOnlyList<GlassWindowRegion>>? RegionsForClientSize { get; init; }
    public Color LightFallbackColor { get; init; } = Color.FromRgb(242, 245, 248);
    public Color DarkFallbackColor { get; init; } = Color.FromRgb(31, 37, 43);
}

/// <summary>A rounded region in client-area device-independent units.</summary>
public readonly record struct GlassWindowRegion(Rect Bounds, double CornerRadius, double Opacity = 1) {
    public GlassWindowRegion(Rect bounds, double cornerRadius) : this(bounds, cornerRadius, 1) { }
    public void Deconstruct(out Rect bounds, out double cornerRadius) { bounds = Bounds; cornerRadius = CornerRadius; }
}

public enum GlassBackdropBackend { None, HostBackdropComposition, DwmSystemBackdrop }

/// <summary>
/// Owns a genuine Windows desktop backdrop. A layered WPF HWND uses a clipped native host-backdrop
/// companion; a non-layered HWND uses the full-window DWM Desktop Acrylic fallback.
/// </summary>
public sealed class GlassWindowBackdrop : IDisposable {
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmSystemBackdropNone = 1;
    private const int DwmSystemBackdropTransientWindow = 3; // Desktop Acrylic, not wallpaper-only Mica.
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const int RgnOr = 2;
    private const int RegionError = 0;

    private readonly Window _window;
    private readonly GlassBackdropOptions _options;
    private readonly Brush? _originalBackground;
    private readonly HwndSourceHook _hook;
    private readonly DependencyPropertyDescriptor? _windowStateDescriptor;
    private HwndSource? _source;
    private nint _handle;
    private GlassWindowRegion[] _regions = [];
    private PixelRegion[]? _appliedRegions;
    private bool _wallpaperMode;
    private bool _darkTheme;
    private bool _disposed;
    private bool _refreshQueued;
    private bool _regionRefreshQueued;
    private bool _usedDwmPolicy;
    private Color? _originalTargetBackground;
    private GlassCompositionHost? _compositionHost;

    private GlassWindowBackdrop(Window window, GlassBackdropOptions options) {
        _window = window;
        _options = options;
        _darkTheme = options.UseDarkTheme;
        _originalBackground = window.Background;
        _hook = WindowHook;
        _windowStateDescriptor = DependencyPropertyDescriptor.FromProperty(Window.WindowStateProperty, typeof(Window));
        _windowStateDescriptor?.AddValueChanged(window, OnWindowStatePropertyChanged);
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.StateChanged += OnStateChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        if (PresentationSource.FromVisual(window) is HwndSource source) InitializeSource(source);
    }

    public static GlassWindowBackdrop Attach(Window window, GlassBackdropOptions? options = null) {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        return new GlassWindowBackdrop(window, options ?? new GlassBackdropOptions());
    }

    public GlassBackdropStatus Status { get; private set; } = GlassBackdropStatus.WaitingForHandle;
    public int LastHResult { get; private set; }
    public int LastRegionError { get; private set; }
    public bool HasDesktopBackdrop => Status == GlassBackdropStatus.DesktopAcrylic;
    public nint NativeBackdropHandle => _compositionHost?.Handle ?? 0;
    public GlassBackdropBackend Backend { get; private set; }
    public bool SupportsSeparatedRegions => Backend == GlassBackdropBackend.HostBackdropComposition;
    public string DiagnosticMessage { get; private set; } = "Waiting for the WPF window handle.";
    public event EventHandler? StatusChanged;

    public void SetWallpaperMode(bool enabled) {
        VerifyUsable();
        if (_wallpaperMode == enabled) return;
        _wallpaperMode = enabled;
        Refresh();
    }

    public void SetDarkTheme(bool enabled) {
        VerifyUsable();
        if (_darkTheme == enabled) return;
        _darkTheme = enabled;
        // A clipped host composition already has a live blur. This shell paints its own
        // theme tints, so leave that host and the extended window frame in place.
        if (!_options.ManageWindowRegion && Backend != GlassBackdropBackend.DwmSystemBackdrop) {
            int dark = enabled ? 1 : 0;
            DwmSetWindowAttribute(_handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            return;
        }
        Refresh();
    }

    /// <summary>
    /// Sets the union of the floating shapes. Empty clears the custom native region
    /// when ManageWindowRegion is enabled; otherwise only the backdrop shapes change.
    /// Callers retain their own WM_NCHITTEST hook for dragging/resizing; this utility never consumes it.
    /// With window-region management enabled, only points inside these shapes receive
    /// mouse input, so include any desired resize grip.
    /// </summary>
    /// <summary>
    /// How present the native backdrop is (0 hidden … 1 full), multiplied into every region's opacity. The backdrop is
    /// drawn by a separate host window that Windows shows at once, while the WPF content needs a render pass; holding it
    /// at 0 until the content is up (startup, restore from minimized) and then fading it in keeps the blur from arriving
    /// before the interface.
    /// </summary>
    public double Presence { get; private set; } = 1;

    public void SetPresence(double presence) {
        VerifyUsable();
        presence = Math.Clamp(double.IsFinite(presence) ? presence : 1, 0, 1);
        if (Math.Abs(presence - Presence) < .001) return;
        Presence = presence;
        SyncCompositionHost();
    }

    public void SetRegions(IEnumerable<GlassWindowRegion>? regions) {
        VerifyUsable();
        var snapshot = regions?.ToArray() ?? [];
        foreach (var region in snapshot) {
            if (region.Bounds.IsEmpty || !double.IsFinite(region.Bounds.X) || !double.IsFinite(region.Bounds.Y) ||
                !double.IsFinite(region.Bounds.Width) || !double.IsFinite(region.Bounds.Height) ||
                region.Bounds.Width <= 0 || region.Bounds.Height <= 0 || !double.IsFinite(region.CornerRadius) || region.CornerRadius < 0 ||
                !double.IsFinite(region.Opacity) || region.Opacity < 0 || region.Opacity > 1)
                throw new ArgumentOutOfRangeException(nameof(regions), "Glass regions require finite, positive bounds and a nonnegative radius.");
        }
        if (_regions.SequenceEqual(snapshot)) return;
        _regions = snapshot;
        ApplyRegions();
        SyncCompositionHost();
    }

    public void Refresh() {
        VerifyUsable();
        GlassSystemPreferences.Refresh();
        if (_handle == 0 || _source?.CompositionTarget is not { } target) return;

        var oldStatus = Status;
        var oldBackend = Backend;
        Backend = GlassBackdropBackend.None;
        var highContrast = SystemParameters.HighContrast;
        var transparency = !_options.RespectSystemTransparency || GlassSystemPreferences.TransparencyEnabled;
        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var layered = (GetExtendedStyle(_handle) & WsExLayered) != 0;

        if (highContrast) SetStatus(GlassBackdropStatus.HighContrast, "Windows high contrast uses an opaque system surface.");
        else if (!transparency) SetStatus(GlassBackdropStatus.TransparencyDisabled, "Windows transparency effects are disabled; using opaque material.");
        else if (_wallpaperMode) SetStatus(GlassBackdropStatus.Wallpaper, "Launcher wallpaper is sampled and blurred by each GlassSurface; native backdrop is disabled.");
        else if (!GlassMaterial.GetDesktopBackdropEnabled(_window)) SetStatus(GlassBackdropStatus.WallpaperOnly, "Desktop material is disabled; use application wallpaper or opaque surfaces.");
        else if (!supported) SetStatus(GlassBackdropStatus.UnsupportedPlatform, "Documented Desktop Acrylic requires Windows 11 build 22621 or newer; using opaque material.");
        else if (layered || _window.AllowsTransparency) {
            if (_options.PreferCompositionBackdrop) {
                try {
                    _compositionHost ??= new GlassCompositionHost(_handle);
                    Backend = GlassBackdropBackend.HostBackdropComposition;
                    SetStatus(GlassBackdropStatus.DesktopAcrylic, "Native host-backdrop composition blurs the live desktop inside each rounded surface; gaps remain transparent.");
                } catch (Exception exception) when (exception is COMException or Win32Exception or DllNotFoundException or EntryPointNotFoundException) {
                    LastHResult = exception.HResult;
                    SetStatus(GlassBackdropStatus.Failed, $"Native backdrop composition failed (0x{exception.HResult:X8}): {exception.Message}");
                }
            } else SetStatus(GlassBackdropStatus.LayeredWindowUnsupported, "A layered WPF window requires the clipped host-backdrop composition backend.");
        } else {
            Backend = GlassBackdropBackend.DwmSystemBackdrop;
            SetStatus(GlassBackdropStatus.DesktopAcrylic, "Windows Desktop Acrylic is active (DWMWA_SYSTEMBACKDROP_TYPE=3); this fallback paints the full window bounds.");
        }

        var wantsDwm = Backend == GlassBackdropBackend.DwmSystemBackdrop;
        var backdropType = wantsDwm ? DwmSystemBackdropTransientWindow : DwmSystemBackdropNone;
        var dark = _darkTheme ? 1 : 0;
        DwmSetWindowAttribute(_handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var backdropResult = supported ? DwmSetWindowAttribute(_handle, DwmwaSystemBackdropType, ref backdropType, sizeof(int)) : 0;
        if (Status != GlassBackdropStatus.Failed) LastHResult = backdropResult;
        if (wantsDwm && backdropResult < 0) {
            SetStatus(GlassBackdropStatus.Failed, $"Windows rejected Desktop Acrylic (HRESULT 0x{LastHResult:X8}); using opaque material.");
            wantsDwm = false;
            Backend = GlassBackdropBackend.None;
        }

        if (wantsDwm) {
            // WindowStyle=None otherwise leaves NC rendering disabled and returns a flat gray result.
            var enabled = 2; // DWMNCRP_ENABLED.
            DwmSetWindowAttribute(_handle, 2, ref enabled, sizeof(int));
            _usedDwmPolicy = true;
        }
        var margins = new Margins(wantsDwm ? -1 : 0);
        var frameResult = DwmExtendFrameIntoClientArea(_handle, ref margins);
        if (wantsDwm && frameResult < 0) {
            LastHResult = frameResult;
            SetStatus(GlassBackdropStatus.Failed, $"Windows rejected client-area composition (HRESULT 0x{frameResult:X8}); using opaque material.");
            Backend = GlassBackdropBackend.None;
            backdropType = DwmSystemBackdropNone;
            DwmSetWindowAttribute(_handle, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
        }

        var hasTransparentMaterial = Status is GlassBackdropStatus.DesktopAcrylic or GlassBackdropStatus.Wallpaper;
        var fallback = highContrast ? SystemColors.WindowColor : _darkTheme ? _options.DarkFallbackColor : _options.LightFallbackColor;
        // An external region/mask owner supplies its own opaque fallback surfaces.
        // A window-wide background would bypass that mask and fill its transparent gap.
        var transparentWindow = hasTransparentMaterial || !_options.ManageWindowRegion;
        target.BackgroundColor = transparentWindow ? Colors.Transparent : fallback;
        _window.SetCurrentValue(Window.BackgroundProperty, transparentWindow ? Brushes.Transparent : new SolidColorBrush(fallback));
        GlassMaterial.SetIsEnabled(_window, hasTransparentMaterial);
        ApplyRegions();
        SyncCompositionHost();
        if (oldStatus != Status || oldBackend != Backend) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() {
        if (_disposed) return;
        _window.Dispatcher.VerifyAccess();
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.StateChanged -= OnStateChanged;
        _windowStateDescriptor?.RemoveValueChanged(_window, OnWindowStatePropertyChanged);
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _source?.RemoveHook(_hook);
        _compositionHost?.Dispose();
        _compositionHost = null;
        if (_handle != 0 && IsWindow(_handle)) {
            var none = DwmSystemBackdropNone;
            DwmSetWindowAttribute(_handle, DwmwaSystemBackdropType, ref none, sizeof(int));
            var margins = new Margins(0);
            DwmExtendFrameIntoClientArea(_handle, ref margins);
            if (_usedDwmPolicy) { var automatic = 0; DwmSetWindowAttribute(_handle, 2, ref automatic, sizeof(int)); }
            if (_options.ManageWindowRegion) SetWindowRgn(_handle, 0, true);
            if (_source?.CompositionTarget is { } target && _originalTargetBackground is { } original) target.BackgroundColor = original;
        }
        _window.SetCurrentValue(Window.BackgroundProperty, _originalBackground);
        _window.ClearValue(GlassMaterial.IsEnabledProperty);
        _source = null;
        _handle = 0;
        Backend = GlassBackdropBackend.None;
        SetStatus(GlassBackdropStatus.Disposed, "Backdrop controller disposed.");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VerifyUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
    }

    private void SetStatus(GlassBackdropStatus status, string message) {
        Status = status;
        DiagnosticMessage = message;
    }

    private void OnSourceInitialized(object? sender, EventArgs args) {
        if (PresentationSource.FromVisual(_window) is HwndSource source) InitializeSource(source);
    }

    private void InitializeSource(HwndSource source) {
        _source = source;
        _handle = source.Handle;
        _originalTargetBackground = source.CompositionTarget?.BackgroundColor;
        source.AddHook(_hook);
        Refresh();
    }

    private void OnClosed(object? sender, EventArgs args) => Dispose();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => SyncCompositionHost();
    private void OnStateChanged(object? sender, EventArgs args) => SyncCompositionHost();
    // Window.StateChanged/IsIconic can lag the DP during foreground taskbar minimize animation.
    private void OnWindowStatePropertyChanged(object? sender, EventArgs args) => SyncCompositionHost();
    private void SyncCompositionHost() {
        if (_disposed) return;
        UseCompositionHost(host => host.SyncBounds(
            () => !_disposed && Presence > .001 && Backend == GlassBackdropBackend.HostBackdropComposition && _window.IsVisible && _window.WindowState != WindowState.Minimized,
            pixels => {
                if (_source?.CompositionTarget is not { } target) return;
                Matrix toDevice = target.TransformToDevice;
                Size clientSize = new(pixels.Width / toDevice.M11, pixels.Height / toDevice.M22);
                IReadOnlyList<GlassWindowRegion>? regions = _options.RegionsForClientSize?.Invoke(clientSize);
                IReadOnlyList<GlassWindowRegion> shown = regions is { Count: > 0 } ? regions : _regions;
                double presence = Presence;
                host.SetRegions(presence >= .999 ? shown : shown.Select(region => region with { Opacity = region.Opacity * presence }).ToArray(), toDevice);
            }));
    }

    private void UseCompositionHost(Action<GlassCompositionHost> operation) {
        if (_compositionHost is not { } host) return;
        try { operation(host); }
        catch (Exception exception) when (exception is COMException or Win32Exception) {
            // Device loss or region allocation failure must never escape a native window hook.
            _compositionHost = null;
            host.Dispose();
            Backend = GlassBackdropBackend.None;
            LastHResult = exception.HResult;
            SetStatus(GlassBackdropStatus.Failed, $"Desktop composition became unavailable (0x{exception.HResult:X8}); using opaque material.");
            var fallback = SystemParameters.HighContrast ? SystemColors.WindowColor : _darkTheme ? _options.DarkFallbackColor : _options.LightFallbackColor;
            if (_source?.CompositionTarget is { } target) target.BackgroundColor = _options.ManageWindowRegion ? fallback : Colors.Transparent;
            _window.SetCurrentValue(Window.BackgroundProperty, _options.ManageWindowRegion ? new SolidColorBrush(fallback) : Brushes.Transparent);
            GlassMaterial.SetIsEnabled(_window, false);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs args) => QueueRefresh();
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => QueueRefresh();

    private void QueueRefresh() {
        if (_disposed || _refreshQueued || _window.Dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            _refreshQueued = false;
            if (!_disposed) Refresh();
        }));
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) {
        // Never consume input, resize, or DPI messages owned by the shell.
        if (message is 0x031E or 0x031A or 0x001A) QueueRefresh(); // DWM composition, theme, settings.
        else if (message is 0x02E0 or 0x0005) { // DPI changed / size.
            _appliedRegions = null;
            QueueRegionRefresh();
        }
        if (message is 0x0047 or 0x0018 or 0x0006 or 0x001C or 0x0005) SyncCompositionHost(); // Move, show, activate, minimize.
        if (message == 0x0047) QueueRegionRefresh(); // Reconcile after Win32 commits pending state/topmost changes.
        return 0;
    }

    private void QueueRegionRefresh() {
        if (_disposed || _regionRefreshQueued || _window.Dispatcher.HasShutdownStarted) return;
        _regionRefreshQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            _regionRefreshQueued = false;
            if (!_disposed) { ApplyRegions(); SyncCompositionHost(); }
        }));
    }

    private void ApplyRegions() {
        if (_handle == 0 || _source?.CompositionTarget is not { } target) return;
        var transform = target.TransformToDevice;
        if (!_options.ManageWindowRegion) {
            LastRegionError = 0;
            return;
        }
        var origin = new NativePoint();
        if (!ClientToScreen(_handle, ref origin) || !GetWindowRect(_handle, out var windowBounds)) return;
        var offsetX = origin.X - windowBounds.Left;
        var offsetY = origin.Y - windowBounds.Top;
        var pixels = _regions.Select(region => {
            var bounds = region.Bounds;
            return new PixelRegion(
                (int)Math.Floor(bounds.Left * transform.M11) + offsetX,
                (int)Math.Floor(bounds.Top * transform.M22) + offsetY,
                (int)Math.Ceiling(bounds.Right * transform.M11) + offsetX,
                (int)Math.Ceiling(bounds.Bottom * transform.M22) + offsetY,
                (int)Math.Round(Math.Min(region.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2) * transform.M11 * 2),
                (int)Math.Round(Math.Min(region.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2) * transform.M22 * 2));
        }).ToArray();
        if (_appliedRegions is not null && _appliedRegions.SequenceEqual(pixels)) return;
        LastRegionError = 0;
        if (pixels.Length == 0) {
            if (SetWindowRgn(_handle, 0, true) != 0) _appliedRegions = pixels;
            else LastRegionError = Marshal.GetLastWin32Error();
            return;
        }

        var union = CreateRectRgn(0, 0, 0, 0);
        if (union == 0) {
            LastRegionError = Marshal.GetLastWin32Error();
            return;
        }
        try {
            foreach (var region in pixels) {
                var shape = CreateRoundRectRgn(region.Left, region.Top, region.Right + 1, region.Bottom + 1, region.EllipseWidth, region.EllipseHeight);
                if (shape == 0) {
                    LastRegionError = Marshal.GetLastWin32Error();
                    return;
                }
                try {
                    if (CombineRgn(union, union, shape, RgnOr) == RegionError) {
                        LastRegionError = Marshal.GetLastWin32Error();
                        return;
                    }
                } finally {
                    DeleteObject(shape);
                }
            }
            if (SetWindowRgn(_handle, union, true) == 0) {
                LastRegionError = Marshal.GetLastWin32Error();
                return;
            }
            // Windows owns the region after a successful SetWindowRgn call.
            union = 0;
            _appliedRegions = pixels;
        } finally {
            if (union != 0) DeleteObject(union);
        }
    }

    private readonly record struct PixelRegion(int Left, int Top, int Right, int Bottom, int EllipseWidth, int EllipseHeight);
    [StructLayout(LayoutKind.Sequential)] private struct Margins(int width) {
        public int Left = width;
        public int Right = width;
        public int Top = width;
        public int Bottom = width;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    private static long GetExtendedStyle(nint hwnd) => nint.Size == 8 ? GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() : GetWindowLong(hwnd, GwlExStyle);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
}
