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
using System.Windows.Media;

namespace PCLLF.Wpf.Glass;

/// <summary>
/// A no-activation, no-redirection HWND below the layered WPF HWND. DirectComposition otherwise
/// occupies WPF's airspace and covers its content. This companion is lifetime-owned by the controller;
/// it cannot use the main HWND as its Win32 owner because owned popups must stay above their owner.
/// </summary>
internal sealed class GlassCompositionHost : IDisposable {
    private const string ClassName = "PCLLF.Glass.DesktopBackdrop.1";
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private const long WsExTopmost = 0x00000008;
    private static readonly object ClassGate = new();
    private static readonly WindowProcedure Procedure = BackdropWindowProcedure;
    private static bool _classRegistered;
    private readonly nint _owner;
    private readonly GlassDesktopComposition _composition;
    private GlassWindowRegion[] _regions = [];
    private Matrix _transform = Matrix.Identity;
    private Matrix _appliedTransform;
    private bool _regionsDirty;
    private bool _disposed;
    private NativeRect _lastClientBounds;

    internal nint Handle { get; private set; }

    private static readonly Guid SessionDisplayStatus = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    internal static nint WatchDisplayPower(nint hwnd) => RegisterPowerSettingNotification(hwnd, in SessionDisplayStatus, 0);
    internal static void UnwatchDisplayPower(nint registration) {
        if (registration != 0) UnregisterPowerSettingNotification(registration);
    }

    internal static bool IsResetNotification(int message, nint reason, nint data) {
        if (message is 0x007E or 0x031E) return true; // WM_DISPLAYCHANGE, WM_DWMCOMPOSITIONCHANGED.
        if (message != 0x0218) return false;
        if (reason == 0x12 || reason == 0x07 || reason == 0x0A) return true; // Resume or AC/battery source change.
        return reason == 0x8013 && data != 0 && Marshal.PtrToStructure<Guid>(data) == SessionDisplayStatus &&
               Marshal.ReadInt32(data, 16) == sizeof(uint) && Marshal.ReadInt32(data, 20) == 1;
    }

    internal GlassCompositionHost(nint owner) {
        _owner = owner;
        RegisterWindowClass();
        // TOOLWINDOW | TRANSPARENT | NOACTIVATE | NOREDIRECTIONBITMAP. This is decoration,
        // not a second application window: leave its accessible/title text empty as well.
        Handle = CreateWindowEx(0x082000A0, ClassName, string.Empty, 0x80000000,
            0, 0, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var yes = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, 17, ref yes, sizeof(int))); // USE_HOSTBACKDROPBRUSH.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(Handle, ref margins));
            EnableHostBackdropAccent(Handle);
            _composition = new GlassDesktopComposition(Handle);
        } catch {
            DestroyWindow(Handle);
            Handle = 0;
            throw;
        }
    }

    internal bool HasBlurredEffectBrush => _composition.IsBlurredEffectBrush;

    internal void SetRegions(IReadOnlyList<GlassWindowRegion> regions, Matrix toDevice) {
        if (_transform == toDevice && _regions.SequenceEqual(regions)) return;
        _regions = regions.ToArray();
        _transform = toDevice;
        _regionsDirty = true;
    }

    internal void SyncBounds(Func<bool> shouldBeVisible, Action<Size>? updateRegions = null, bool reserveLeftInset = false) {
        if (_disposed || Handle == 0) return;
        if (!shouldBeVisible() || !IsWindowVisible(_owner) || IsIconic(_owner)) {
            // Reconcile the actual native state even after a reentrant show/hide sequence.
            ShowWindow(Handle, 0);
            return;
        }
        var origin = new NativePoint();
        if (!ClientToScreen(_owner, ref origin) || !GetClientRect(_owner, out var client)) return;
        if (client.Right <= 0 || client.Bottom <= 0) { ShowWindow(Handle, 0); return; }
        var clientPixels = new Size(client.Right, client.Bottom);
        updateRegions?.Invoke(clientPixels);
        Rect visible = VisiblePixelBounds(_regions, _transform, clientPixels);
        if (visible.IsEmpty) { ShowWindow(Handle, 0); return; }

        // Keep the HWND fixed while the rail travels into the left inset. Resizing the HWND
        // on every frame can leave newly exposed pixels without host backdrop sampling.
        // The rounded composition clips still follow the arranged surfaces.
        if (reserveLeftInset) visible = new Rect(0, visible.Top, visible.Right, visible.Height);

        var bounds = new NativeRect {
            Left = origin.X + (int)visible.Left, Top = origin.Y + (int)visible.Top,
            Right = origin.X + (int)visible.Right, Bottom = origin.Y + (int)visible.Bottom
        };
        Matrix positioned = _transform;
        positioned.OffsetX -= visible.Left;
        positioned.OffsetY -= visible.Top;
        if (_regionsDirty || _appliedTransform != positioned) {
            _composition.SetRegions(_regions, positioned);
            _appliedTransform = positioned;
            _regionsDirty = false;
        }
        var ownerTopmost = (GetExtendedStyle(_owner) & WsExTopmost) != 0;
        var hostTopmost = (GetExtendedStyle(Handle) & WsExTopmost) != 0;
        if (ownerTopmost != hostTopmost)
            SetWindowPos(Handle, ownerTopmost ? new nint(-1) : new nint(-2), 0, 0, 0, 0, NoActivate | NoOwnerZOrder | 0x0001 | 0x0002);
        // Insert immediately after the main HWND, preserving its position relative to other apps.
        bool boundsChanged = !_lastClientBounds.Equals(bounds);
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        bool sizeChanged = width != _lastClientBounds.Right - _lastClientBounds.Left ||
                           height != _lastClientBounds.Bottom - _lastClientBounds.Top;
        bool wasVisible = IsWindowVisible(Handle);
        if (boundsChanged || GetWindow(_owner, 2) != Handle || !wasVisible) {
            uint flags = NoActivate | NoOwnerZOrder | 0x0040; // SWP_SHOWWINDOW.
            // Resizing must invalidate the newly exposed backdrop pixels. Only skip repainting for a move.
            if (wasVisible && !sizeChanged) flags |= 0x0008; // SWP_NOREDRAW.
            if (!boundsChanged) flags |= 0x0001 | 0x0002; // SWP_NOSIZE | SWP_NOMOVE.
            else if (!sizeChanged) flags |= 0x0001; // SWP_NOSIZE during pure window drags.
            if (!SetWindowPos(Handle, _owner, bounds.Left, bounds.Top, width, height, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!shouldBeVisible() || !IsWindowVisible(_owner) || IsIconic(_owner)) {
            ShowWindow(Handle, 0);
            return;
        }
        _lastClientBounds = bounds;
    }

    internal static Rect VisiblePixelBounds(IReadOnlyList<GlassWindowRegion> regions, Matrix toDevice, Size clientPixels) {
        // Never call SetWindowRgn on a WS_EX_NOREDIRECTIONBITMAP host-backdrop HWND: GDI window regions
        // can force DWM out of backdrop sampling. Crop the HWND to its visible coverage and let the
        // composition clips continue to paint the exact rounded shapes.
        Rect bounds = Rect.Empty;
        foreach (var region in regions) {
            if (region.Opacity <= .001) continue;
            Rect pixels = region.Bounds;
            pixels.Transform(toDevice);
            bounds.Union(pixels);
        }
        bounds.Intersect(new Rect(clientPixels));
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Rect.Empty;
        return new Rect(new Point(Math.Floor(bounds.Left), Math.Floor(bounds.Top)),
            new Point(Math.Ceiling(bounds.Right), Math.Ceiling(bounds.Bottom)));
    }

    private static unsafe void EnableHostBackdropAccent(nint hwnd) {
        var accent = new AccentPolicy { AccentState = 5 }; // ACCENT_ENABLE_HOSTBACKDROP.
        var data = new WindowCompositionAttributeData {
            Attribute = 19, // WCA_ACCENT_POLICY.
            Data = (nint)(&accent),
            SizeOfData = sizeof(AccentPolicy)
        };
        SetWindowCompositionAttribute(hwnd, ref data);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (Handle != 0) ShowWindow(Handle, 0);
        _composition.Dispose();
        if (Handle != 0) { DestroyWindow(Handle); Handle = 0; }
    }

    private static void RegisterWindowClass() {
        lock (ClassGate) {
            if (_classRegistered) return;
            var windowClass = new WindowClass {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), ClassName = ClassName,
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Background = 0 // No class brush may fill the transparent composition gaps/corners.
            };
            if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _classRegistered = true;
        }
    }

    private static nint BackdropWindowProcedure(nint hwnd, uint message, nint wParam, nint lParam) => message switch {
        0x0084 => new nint(-1), // HTTRANSPARENT; backdrop never receives input.
        0x0021 => new nint(3), // MA_NOACTIVATE.
        0x0014 => new nint(1), // No GDI background; only the clipped composition tree paints.
        _ => DefWindowProc(hwnd, message, wParam, lParam)
    };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass {
        public uint Size; public uint Style; public nint Procedure; public int ClassExtra; public int WindowExtra;
        public nint Instance; public nint Icon; public nint Cursor; public nint Background;
        public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttributeData { public int Attribute; public nint Data; public int SizeOfData; }
    private static long GetExtendedStyle(nint hwnd) => nint.Size == 8 ? GetWindowLongPtr(hwnd, -20).ToInt64() : GetWindowLong(hwnd, -20);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] private static extern nint RegisterPowerSettingNotification(nint hwnd, in Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint notification);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
