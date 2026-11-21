// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCLLF.Wpf.Glass;

/// <summary>
/// A clipped host-backdrop visual tree below WPF's client content. Uses the documented WinRT ABI
/// directly, avoiding a Windows App SDK dependency and keeping the existing WPF target framework.
/// Interface IDs and method order are from Windows SDK 10.0.26100 Windows.UI.Composition.h.
/// </summary>
internal sealed unsafe class GlassDesktopComposition : IDisposable {
    private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid InspectableId = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
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
    private static readonly Guid SourceParameterFactoryId = new("b3d9f276-aba3-4724-acf3-d0397464db1c");
    private static readonly Guid GraphicsEffectId = new("cb51c0ce-8fe6-4636-b202-861faa07d8f3");
    private static readonly Guid GraphicsEffectSourceId = new("2d8f9ddc-4339-4eb9-9216-f9deb75658a2");
    private static readonly Guid GraphicsEffectD2D1InteropId = new("2fc57384-a068-44d7-a331-30982fcf7177");
    private static readonly Guid PropertyValueStaticsId = new("629bdbc8-d932-4ff4-96b9-8d96c5c1e858");
    private static readonly Guid PropertyValueId = new("4bd682dd-7554-40e9-9a9b-82654ede7e62");
    private static readonly Guid GaussianBlurClsid = new("1feb6d69-2fe6-4ac9-8c58-1d7f93e7a6a5");
    private static readonly Guid ColorMatrixClsid = new("921f03d6-641c-47df-852d-b4bb6153ae11");
    private const float NearBlackAlphaScale = 255f / 24f;
    // An unavailable host sample can be opaque black even when its API calls succeed.
    // Keep RGB and ramp alpha to one over the darkest eight gray levels, revealing
    // the scene through the WPF tint where Windows supplied only black.
    private static readonly float[] BackdropColorMatrix = [
        1, 0, 0, NearBlackAlphaScale,
        0, 1, 0, NearBlackAlphaScale,
        0, 0, 1, NearBlackAlphaScale,
        0, 0, 0, 0,
        0, 0, 0, 0
    ];
    [ThreadStatic] private static nint _dispatcherQueueController;

    private ComReference? _compositor;
    private ComReference? _compositor5;
    private ComReference? _compositor6;
    private ComReference? _target;
    private ComReference? _container;
    private ComReference? _children;
    private ComReference? _hostBrush;
    private ComReference? _brush;
    private readonly List<SurfaceVisual> _surfaces = [];
    private bool _uninitialize;
    private bool _disposed;
    internal bool IsBlurredEffectBrush { get; private set; }
    internal string BlurDiagnostic { get; private set; } = string.Empty;

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
            using var rawHostBrush = GetObject(compositor3, 6); // ICompositor3.CreateHostBackdropBrush.
            _hostBrush = rawHostBrush.Query(BrushId);
            _brush = CreateBackdropEffectBrush(_compositor, _hostBrush);
        } catch {
            Dispose();
            throw;
        }
    }

    internal void SetRegions(IReadOnlyList<GlassWindowRegion> regions, Matrix toDevice) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_children is null || _compositor is null || _compositor5 is null || _compositor6 is null || _brush is null) return;
        // Keep existing visuals when the number of regions changes: removing and recreating them all left one frame
        // without any blur (a visible flash). Grow by adding visuals; park surplus ones fully transparent.
        while (_surfaces.Count < regions.Count) {
            var surface = new SurfaceVisual(_compositor, _compositor5, _compositor6, _brush);
            _surfaces.Add(surface);
            SetObject(_children, 9, surface.Visual.Pointer); // InsertAtTop.
        }
        for (var index = 0; index < _surfaces.Count; index++) {
            if (index < regions.Count) _surfaces[index].Update(regions[index], toDevice);
            else _surfaces[index].Update(_surfaces[index].Parked, toDevice);
        }
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
        _hostBrush?.Close();
        _hostBrush?.Dispose();
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
        private GlassWindowRegion _lastRegion;
        private Matrix _lastToDevice;
        private bool _hasUpdated;
        internal ComReference Visual { get; }
        /// <summary>The last shape, fully transparent: an unused visual waits like this instead of being destroyed.</summary>
        internal GlassWindowRegion Parked => _hasUpdated ? _lastRegion with { Opacity = 0 } : new GlassWindowRegion(new System.Windows.Rect(0, 0, 1, 1), 0, 0);

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
            if (_hasUpdated && _lastRegion == region && _lastToDevice == toDevice) return;
            _hasUpdated = true;
            _lastRegion = region;
            _lastToDevice = toDevice;
            var bounds = region.Bounds;
            var size = new Vector2((float)(bounds.Width * toDevice.M11), (float)(bounds.Height * toDevice.M22));
            var radius = Math.Min(region.CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);
            SetVector2(Visual, 36, size); // IVisual.put_Size.
            var offset = toDevice.Transform(bounds.TopLeft);
            SetVector3(Visual, 21, new Vector3((float)offset.X, (float)offset.Y, 0)); // put_Offset.
            Check(((delegate* unmanaged[Stdcall]<nint, float, int>)Visual.Method(23))(Visual.Pointer, (float)region.Opacity)); // IVisual.put_Opacity.
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

    private static ComReference GetFactory(string runtimeClass, Guid iid) {
        Check(WindowsCreateString(runtimeClass, runtimeClass.Length, out var name));
        try {
            Check(RoGetActivationFactory(name, in iid, out var result));
            return new ComReference(result);
        } finally { WindowsDeleteString(name); }
    }

    private ComReference CreateBackdropEffectBrush(ComReference compositor, ComReference hostBackdropBrush) {
        var effectFactoryId = new Guid("be5624af-ba7e-4510-9850-41c0b4ff74df");
        var effectBrushId = new Guid("bf7f795e-83cc-44bf-a447-3e3c071789ec");
        using var pvStatics = GetFactory("Windows.Foundation.PropertyValue", PropertyValueStaticsId);
        using var deviation = CreatePropertySingle(pvStatics, 28f);
        using var balanced = CreatePropertyUInt32(pvStatics, 1u); // D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED.
        using var hardBorder = CreatePropertyUInt32(pvStatics, 1u); // D2D1_BORDER_MODE_HARD.
        using var matrix = CreatePropertySingleArray(pvStatics, BackdropColorMatrix);
        // Keep the existing premultiplied RGB channels. The straight matrix mode
        // updates alpha without tinting or multiplying the backdrop colours again.
        using var straight = CreatePropertyUInt32(pvStatics, 2u); // D2D1_COLORMATRIX_ALPHA_MODE_STRAIGHT.
        using var clamp = CreateTrueProperty(pvStatics);
        using var sourceFactory = GetFactory("Windows.UI.Composition.CompositionEffectSourceParameter", SourceParameterFactoryId);
        Check(WindowsCreateString("Backdrop", 8, out var sourceName));
        try {
            nint rawSource = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)sourceFactory.Method(6))(sourceFactory.Pointer, sourceName, &rawSource));
            using var sourceParameter = new ComReference(rawSource);
            using var source = sourceParameter.Query(GraphicsEffectSourceId);
            using var blur = new ComReference((nint)BackdropEffect.Create(GaussianBlurClsid, source.Pointer,
                deviation.Pointer, balanced.Pointer, hardBorder.Pointer));
            using var blurredSource = blur.Query(GraphicsEffectSourceId);
            using var guarded = new ComReference((nint)BackdropEffect.Create(ColorMatrixClsid, blurredSource.Pointer,
                matrix.Pointer, straight.Pointer, clamp.Pointer));
            nint rawFactory = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)compositor.Method(11))(
                compositor.Pointer, guarded.Pointer, &rawFactory));
            using var createdFactory = new ComReference(rawFactory);
            using var effectFactory = createdFactory.Query(effectFactoryId);
            int loadStatus = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)effectFactory.Method(8))(effectFactory.Pointer, &loadStatus));
            BlurDiagnostic = $"loadStatus={loadStatus}";
            // Success or pending (DWM compiles the D2D shader asynchronously when it renders).
            if (loadStatus is not (0 or 2)) {
                int extendedError = 0;
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)effectFactory.Method(7))(effectFactory.Pointer, &extendedError);
                throw new COMException($"Backdrop effect factory load status: {loadStatus} (0x{extendedError:X8}).",
                    extendedError < 0 ? extendedError : unchecked((int)0x80004005));
            }
            using var rawBrush = GetObject(effectFactory, 6);
            using var effectBrush = rawBrush.Query(effectBrushId);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)effectBrush.Method(7))(
                effectBrush.Pointer, sourceName, hostBackdropBrush.Pointer));
            var compiled = effectBrush.Query(BrushId);
            IsBlurredEffectBrush = true;
            return compiled;
        } finally { WindowsDeleteString(sourceName); }
    }

    private static ComReference CreatePropertySingle(ComReference statics, float value) {
        nint raw = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, float, nint*, int>)statics.Method(14))(statics.Pointer, value, &raw));
        using var inspectable = new ComReference(raw);
        return inspectable.Query(PropertyValueId);
    }

    private static ComReference CreatePropertyUInt32(ComReference statics, uint value) {
        nint raw = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)statics.Method(11))(statics.Pointer, value, &raw));
        using var inspectable = new ComReference(raw);
        return inspectable.Query(PropertyValueId);
    }

    private static ComReference CreatePropertySingleArray(ComReference statics, float[] values) {
        nint raw = 0;
        fixed (float* data = values)
            Check(((delegate* unmanaged[Stdcall]<nint, uint, float*, nint*, int>)statics.Method(33))(statics.Pointer, (uint)values.Length, data, &raw));
        using var inspectable = new ComReference(raw);
        return inspectable.Query(PropertyValueId);
    }

    private static ComReference CreateTrueProperty(ComReference statics) {
        nint raw = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, byte, nint*, int>)statics.Method(17))(statics.Pointer, 1, &raw));
        using var inspectable = new ComReference(raw);
        return inspectable.Query(PropertyValueId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BackdropEffect {
        private static readonly nint* SharedInspectableVtbl = AllocateInspectableVtbl();
        private static readonly nint* SharedInteropVtbl = AllocateInteropVtbl();

        public nint InspectableVtbl;
        public nint InteropVtbl;
        public int RefCount;
        public Guid EffectId;
        public nint Source;
        public nint Property0;
        public nint Property1;
        public nint Property2;

        public static BackdropEffect* Create(Guid effectId, nint source, nint property0, nint property1, nint property2) {
            var ptr = (BackdropEffect*)NativeMemory.AllocZeroed((nuint)sizeof(BackdropEffect));
            ptr->InspectableVtbl = (nint)SharedInspectableVtbl;
            ptr->InteropVtbl = (nint)SharedInteropVtbl;
            ptr->RefCount = 1;
            ptr->EffectId = effectId;
            Marshal.AddRef(source); ptr->Source = source;
            Marshal.AddRef(property0); ptr->Property0 = property0;
            Marshal.AddRef(property1); ptr->Property1 = property1;
            Marshal.AddRef(property2); ptr->Property2 = property2;
            return ptr;
        }

        private static nint* AllocateInspectableVtbl() {
            var vtbl = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(BackdropEffect), sizeof(nint) * 8);
            vtbl[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
            vtbl[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            vtbl[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            vtbl[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)&GetIids;
            vtbl[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetRuntimeClassName;
            vtbl[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetTrustLevel;
            vtbl[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetName;
            vtbl[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&PutName;
            return vtbl;
        }

        private static nint* AllocateInteropVtbl() {
            var vtbl = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(BackdropEffect), sizeof(nint) * 9);
            vtbl[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&InteropQueryInterface;
            vtbl[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&InteropAddRef;
            vtbl[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&InteropRelease;
            vtbl[3] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, int>)&GetEffectId;
            vtbl[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint*, int*, int>)&GetNamedPropertyMapping;
            vtbl[5] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetPropertyCount;
            vtbl[6] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&GetProperty;
            vtbl[7] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&GetSource;
            vtbl[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetSourceCount;
            return vtbl;
        }

        private static int QueryInterfaceCore(nint selfPtr, Guid* iid, nint* result) {
            if (result is null || iid is null) return unchecked((int)0x80004003);
            var self = (BackdropEffect*)selfPtr;
            if (*iid == UnknownId || *iid == InspectableId || *iid == GraphicsEffectId || *iid == GraphicsEffectSourceId) {
                Interlocked.Increment(ref self->RefCount);
                *result = (nint)(&self->InspectableVtbl);
                return 0;
            }
            if (*iid == GraphicsEffectD2D1InteropId) {
                Interlocked.Increment(ref self->RefCount);
                *result = (nint)(&self->InteropVtbl);
                return 0;
            }
            *result = 0;
            return unchecked((int)0x80004002);
        }

        private static uint AddRefCore(nint selfPtr) => (uint)Interlocked.Increment(ref ((BackdropEffect*)selfPtr)->RefCount);

        public static uint ReleaseCore(nint selfPtr) {
            var self = (BackdropEffect*)selfPtr;
            int remaining = Interlocked.Decrement(ref self->RefCount);
            if (remaining == 0) {
                if (self->Source != 0) Marshal.Release(self->Source);
                if (self->Property0 != 0) Marshal.Release(self->Property0);
                if (self->Property1 != 0) Marshal.Release(self->Property1);
                if (self->Property2 != 0) Marshal.Release(self->Property2);
                NativeMemory.Free(self);
            }
            return (uint)remaining;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(nint selfPtr, Guid* iid, nint* result) => QueryInterfaceCore(selfPtr, iid, result);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(nint selfPtr) => AddRefCore(selfPtr);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(nint selfPtr) => ReleaseCore(selfPtr);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetIids(nint selfPtr, uint* count, nint* iids) {
            if (count is not null) *count = 0;
            if (iids is not null) *iids = 0;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetRuntimeClassName(nint selfPtr, nint* name) { if (name is not null) *name = 0; return 0; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetTrustLevel(nint selfPtr, int* level) { if (level is not null) *level = 0; return 0; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetName(nint selfPtr, nint* name) { if (name is not null) *name = 0; return 0; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int PutName(nint selfPtr, nint name) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int InteropQueryInterface(nint interopPtr, Guid* iid, nint* result) => QueryInterfaceCore(interopPtr - sizeof(nint), iid, result);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint InteropAddRef(nint interopPtr) => AddRefCore(interopPtr - sizeof(nint));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint InteropRelease(nint interopPtr) => ReleaseCore(interopPtr - sizeof(nint));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetEffectId(nint interopPtr, Guid* id) {
            if (id is null) return unchecked((int)0x80004003);
            *id = ((BackdropEffect*)(interopPtr - sizeof(nint)))->EffectId;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetNamedPropertyMapping(nint interopPtr, nint name, uint* index, int* mapping) => unchecked((int)0x80070057);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetPropertyCount(nint interopPtr, uint* count) {
            if (count is null) return unchecked((int)0x80004003);
            *count = 3;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetProperty(nint interopPtr, uint index, nint* value) {
            if (value is null) return unchecked((int)0x80004003);
            var self = (BackdropEffect*)(interopPtr - sizeof(nint));
            nint prop = index switch {
                0 => self->Property0,
                1 => self->Property1,
                2 => self->Property2,
                _ => 0
            };
            if (prop == 0) { *value = 0; return unchecked((int)0x80070057); }
            Marshal.AddRef(prop);
            *value = prop;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetSource(nint interopPtr, uint index, nint* source) {
            if (source is null) return unchecked((int)0x80004003);
            var self = (BackdropEffect*)(interopPtr - sizeof(nint));
            if (index != 0 || self->Source == 0) { *source = 0; return unchecked((int)0x80070057); }
            Marshal.AddRef(self->Source);
            *source = self->Source;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetSourceCount(nint interopPtr, uint* count) {
            if (count is null) return unchecked((int)0x80004003);
            *count = 1;
            return 0;
        }
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
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint className, in Guid iid, out nint factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
}
