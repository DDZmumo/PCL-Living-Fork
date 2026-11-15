// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>
/// A clipped host-backdrop visual tree below WPF's client content. Uses the documented WinRT ABI
/// directly, avoiding a Windows App SDK dependency and keeping the existing WPF target framework.
/// Interface IDs and method order are from Windows SDK 10.0.26100 Windows.UI.Composition.h.
/// </summary>
internal sealed unsafe class GlassDesktopComposition : IDisposable {
    private static readonly Guid CompositorId = new("b403ca50-7f8c-4e83-985f-cc45060036d8");
    private static readonly Guid Compositor3Id = new("c9dd8ef0-6eb1-4e3c-a658-675d9c64d4ab");
    private static readonly Guid Compositor5Id = new("48ea31ad-7fcd-4076-a79c-90cc4b852c9b");
    private static readonly Guid Compositor6Id = new("7a38b2bd-cec8-4eeb-830f-d8d07aedebc3");
    private static readonly Guid DesktopInteropId = new("29e691fa-4567-4dca-b319-d0f207eb6807");
    private static readonly Guid TargetId = new("a1bea8ba-d726-4663-8129-6b5e7927ffa6");
    private static readonly Guid VisualId = new("117e202d-a859-4c89-873b-c2aa566788e3");
    private static readonly Guid BrushId = new("ab0d7608-30c0-40e9-b568-b60a6bd1fb46");
    private static readonly Guid GeometryId = new("e985217c-6a17-4207-abd8-5fd3dd612a9d");
    private static readonly Guid ClipId = new("1ccd2a52-cfc7-4ace-9983-146bb8eb6a3c");
    private static readonly Guid ClosableId = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    [ThreadStatic] private static nint _dispatcherQueueController;

    private ComReference? _compositor;
    private ComReference? _compositor5;
    private ComReference? _compositor6;
    private ComReference? _target;
    private ComReference? _container;
    private ComReference? _children;
    private ComReference? _brush;
    private readonly List<SurfaceVisual> _surfaces = [];
    private bool _uninitialize;
    private bool _disposed;

    internal GlassDesktopComposition(nint hwnd) {
        try {
            Check(RoInitialize(0)); // RO_INIT_SINGLETHREADED, matching the WPF dispatcher.
            _uninitialize = true;
            EnsureDispatcherQueue();
            using var activated = Activate("Windows.UI.Composition.Compositor");
            _compositor = activated.Query(CompositorId);
            _compositor5 = activated.Query(Compositor5Id);
            _compositor6 = activated.Query(Compositor6Id);
            using var interop = activated.Query(DesktopInteropId);
            nint rawTarget = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)interop.Method(3))(interop.Pointer, hwnd, 0, &rawTarget));
            using var returnedTarget = new ComReference(rawTarget);
            _target = returnedTarget.Query(TargetId);
            _container = GetObject(_compositor, 9); // ICompositor.CreateContainerVisual.
            using var root = _container.Query(VisualId);
            SetObject(_target, 7, root.Pointer); // ICompositionTarget.put_Root.
            _children = GetObject(_container, 6);
            using var compositor3 = activated.Query(Compositor3Id);
            using var hostBrush = GetObject(compositor3, 6);
            _brush = hostBrush.Query(BrushId);
        } catch {
            Dispose();
            throw;
        }
    }

    internal void SetRegions(IReadOnlyList<GlassWindowRegion> regions, Matrix toDevice) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_children is null || _compositor is null || _compositor5 is null || _compositor6 is null || _brush is null) return;
        if (_surfaces.Count != regions.Count) {
            Check(((delegate* unmanaged[Stdcall]<nint, int>)_children.Method(12))(_children.Pointer)); // RemoveAll.
            foreach (var surface in _surfaces) surface.Dispose();
            _surfaces.Clear();
            for (var index = 0; index < regions.Count; index++) {
                var surface = new SurfaceVisual(_compositor, _compositor5, _compositor6, _brush);
                _surfaces.Add(surface);
                SetObject(_children, 9, surface.Visual.Pointer); // InsertAtTop.
            }
        }
        for (var index = 0; index < regions.Count; index++) _surfaces[index].Update(regions[index], toDevice);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_target is not null) ((delegate* unmanaged[Stdcall]<nint, nint, int>)_target.Method(7))(_target.Pointer, 0);
        if (_children is not null) ((delegate* unmanaged[Stdcall]<nint, int>)_children.Method(12))(_children.Pointer);
        foreach (var surface in _surfaces) surface.Dispose();
        _surfaces.Clear();
        _children?.Dispose();
        _container?.Close();
        _container?.Dispose();
        _brush?.Close();
        _brush?.Dispose();
        _target?.Close();
        _target?.Dispose();
        _compositor6?.Dispose();
        _compositor5?.Dispose();
        _compositor?.Close();
        _compositor?.Dispose();
        if (_uninitialize) { RoUninitialize(); _uninitialize = false; }
    }

    private sealed class SurfaceVisual : IDisposable {
        private readonly ComReference _sprite;
        private readonly ComReference _geometry;
        private readonly ComReference _clip;
        internal ComReference Visual { get; }

        internal SurfaceVisual(ComReference compositor, ComReference compositor5, ComReference compositor6, ComReference brush) {
            _sprite = GetObject(compositor, 22); // ICompositor.CreateSpriteVisual.
            try {
                Visual = _sprite.Query(VisualId);
                SetObject(_sprite, 7, brush.Pointer); // ISpriteVisual.put_Brush.
                _geometry = GetObject(compositor5, 20); // CreateRoundedRectangleGeometry.
                using var baseGeometry = _geometry.Query(GeometryId);
                _clip = GetObject(compositor6, 7, baseGeometry.Pointer); // CreateGeometricClipWithGeometry.
                using var baseClip = _clip.Query(ClipId);
                SetObject(Visual, 15, baseClip.Pointer); // IVisual.put_Clip.
            } catch {
                Visual?.Dispose();
                _clip?.Close();
                _clip?.Dispose();
                _geometry?.Close();
                _geometry?.Dispose();
                _sprite.Close();
                _sprite.Dispose();
                throw;
            }
        }

        internal void Update(GlassWindowRegion region, Matrix toDevice) {
            var bounds = region.Bounds;
            var size = new Vector2((float)(bounds.Width * toDevice.M11), (float)(bounds.Height * toDevice.M22));
            var radius = Math.Min(region.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);
            SetVector2(Visual, 36, size); // IVisual.put_Size.
            SetVector3(Visual, 21, new Vector3((float)(bounds.X * toDevice.M11), (float)(bounds.Y * toDevice.M22), 0)); // put_Offset.
            SetVector2(_geometry, 7, new Vector2((float)(radius * toDevice.M11), (float)(radius * toDevice.M22))); // put_CornerRadius.
            SetVector2(_geometry, 11, size); // put_Size.
        }

        public void Dispose() {
            Visual.Dispose();
            _sprite.Close();
            _sprite.Dispose();
            _clip.Close();
            _clip.Dispose();
            _geometry.Close();
            _geometry.Dispose();
        }
    }

    private sealed class ComReference(nint pointer) : IDisposable {
        internal nint Pointer { get; private set; } = pointer != 0 ? pointer : throw new COMException("Windows returned an empty composition interface.");
        internal nint Method(int slot) => (*(nint**)Pointer)[slot];
        internal ComReference Query(Guid id) {
            Check(Marshal.QueryInterface(Pointer, in id, out var result));
            return new ComReference(result);
        }
        internal void Close() {
            var id = ClosableId;
            if (Pointer == 0 || Marshal.QueryInterface(Pointer, in id, out var closable) < 0) return;
            try { ((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)closable)[6])(closable); }
            finally { Marshal.Release(closable); }
        }
        public void Dispose() {
            if (Pointer == 0) return;
            Marshal.Release(Pointer);
            Pointer = 0;
        }
    }

    private static ComReference GetObject(ComReference source, int slot) {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)source.Method(slot))(source.Pointer, &result));
        return new ComReference(result);
    }
    private static ComReference GetObject(ComReference source, int slot, nint argument) {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)source.Method(slot))(source.Pointer, argument, &result));
        return new ComReference(result);
    }
    private static void SetObject(ComReference source, int slot, nint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)source.Method(slot))(source.Pointer, value));
    private static void SetVector2(ComReference source, int slot, Vector2 value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Vector2, int>)source.Method(slot))(source.Pointer, value));
    private static void SetVector3(ComReference source, int slot, Vector3 value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Vector3, int>)source.Method(slot))(source.Pointer, value));
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

    private static ComReference Activate(string runtimeClass) {
        Check(WindowsCreateString(runtimeClass, runtimeClass.Length, out var name));
        try {
            Check(RoActivateInstance(name, out var result));
            return new ComReference(result);
        } finally { WindowsDeleteString(name); }
    }

    private static void EnsureDispatcherQueue() {
        if (_dispatcherQueueController != 0) return;
        var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        // An existing host-owned DispatcherQueue is also valid; RoActivateInstance verifies usability.
        var result = CreateDispatcherQueueController(options, out var controller);
        if (result < 0 || controller == 0) return;
        _dispatcherQueueController = controller;
        Dispatcher.CurrentDispatcher.ShutdownFinished += OnDispatcherShutdown;
    }

    private static void OnDispatcherShutdown(object? sender, EventArgs args) {
        if (_dispatcherQueueController == 0) return;
        Marshal.Release(_dispatcherQueueController);
        _dispatcherQueueController = 0;
        if (sender is Dispatcher dispatcher) dispatcher.ShutdownFinished -= OnDispatcherShutdown;
    }

    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size; public int ThreadType; public int ApartmentType; }
    [DllImport("combase.dll")] private static extern int RoInitialize(uint initializationType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint className, out nint instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
}
