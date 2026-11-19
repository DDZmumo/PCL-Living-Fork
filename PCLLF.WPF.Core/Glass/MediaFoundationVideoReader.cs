// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace PCLLF.Wpf.Glass;

/// <summary>Decodes a local file to top-down RGB32 on one background thread.</summary>
internal sealed unsafe class MediaFoundationVideoReader : IDisposable {
    private const uint AllStreams = 0xffff_fffe;
    private const uint VideoStream = 0xffff_fffc;
    private const uint Error = 0x1, EndOfStream = 0x2, CurrentTypeChanged = 0x20;
    private const uint Rgb32Format = 22;
    private static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid HardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32Type = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid Buffer2D = new("33ae5ea6-4316-436f-8ddd-d73d22f829ec");
    private nint _reader;
    private int _width, _height, _pitch;
    private bool _comStarted, _mediaStarted;

    internal readonly record struct Frame(byte[] Pixels, int Width, int Height, long Timestamp);

    internal MediaFoundationVideoReader(string path) {
        try {
            Check(CoInitializeEx(0, 0)); // COINIT_MULTITHREADED
            _comStarted = true;
            Check(MFStartup(0x00020070, 0));
            _mediaStarted = true;
            Check(MFCreateAttributes(out nint attributes, 2));
            try {
                SetUInt32(attributes, AdvancedVideoProcessing, 1);
                SetUInt32(attributes, HardwareTransforms, 1);
                Check(MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes, out _reader));
            } finally { Release(attributes); }
            var select = (delegate* unmanaged[Stdcall]<nint, uint, int, int>)Method(_reader, 4); // SetStreamSelection
            Check(select(_reader, AllStreams, 0));
            Check(select(_reader, VideoStream, 1));
            SetOutputSize(0, 0);
            ReadOutputType();
            double scale = Math.Min(1920d / _width, 1080d / _height);
            if (scale < 1) {
                SetOutputSize(Math.Max(2, (int)(_width * scale / 2) * 2), Math.Max(2, (int)(_height * scale / 2) * 2));
                ReadOutputType();
            }
            ValidateSize();
        } catch { Dispose(); throw; }
    }

    private void SetOutputSize(int width, int height) {
        Check(MFCreateMediaType(out nint type));
        try {
            SetGuid(type, MajorType, VideoType);
            SetGuid(type, Subtype, Rgb32Type);
            if (width != 0) {
                Guid key = FrameSize;
                ulong size = ((ulong)(uint)width << 32) | (uint)height;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Method(type, 22))(type, &key, size)); // SetUINT64
            }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Method(_reader, 7))(_reader, VideoStream, 0, type)); // SetCurrentMediaType
        } finally { Release(type); }
    }

    private void ReadOutputType() {
        nint type = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(_reader, 6))(_reader, VideoStream, &type)); // GetCurrentMediaType
        try {
            Guid key = FrameSize;
            ulong size = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Method(type, 8))(type, &key, &size)); // GetUINT64
            _width = checked((int)(size >> 32));
            _height = checked((int)(size & uint.MaxValue));
            if (_width <= 0 || _height <= 0) throw new InvalidDataException("视频画面的尺寸无效。");
            key = DefaultStride;
            uint pitch = 0;
            int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Method(type, 7))(type, &key, &pitch); // GetUINT32
            if (result < 0) Check(MFGetStrideForBitmapInfoHeader(Rgb32Format, (uint)_width, out _pitch));
            else _pitch = unchecked((int)pitch);
        } finally { Release(type); }
    }

    private void ValidateSize() {
        if (_width > 1920 || _height > 1080)
            throw new NotSupportedException("系统解码器未提供适合壁纸的画面尺寸。");
    }

    /// <summary>Transfers an array-pool buffer to the caller on each decoded frame. A null frame can be a stream tick.</summary>
    internal bool Read(out Frame? frame) {
        frame = null;
        uint flags = 0;
        long timestamp = 0;
        nint sample = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint*, long*, nint*, int>)Method(_reader, 9))
            (_reader, VideoStream, 0, 0, &flags, &timestamp, &sample)); // ReadSample
        try {
            if ((flags & Error) != 0) throw new IOException("读取视频帧失败。");
            if ((flags & CurrentTypeChanged) != 0) { ReadOutputType(); ValidateSize(); }
            if (sample == 0) return (flags & EndOfStream) == 0;
            int stride = _width * 4;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(stride * _height);
            nint buffer = 0;
            try {
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(sample, 41))(sample, &buffer)); // ConvertToContiguousBuffer
                CopyBuffer(buffer, pixels, stride);
                frame = new(pixels, _width, _height, timestamp);
                return true;
            } catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
            finally { Release(buffer); }
        } finally { Release(sample); }
    }

    private void CopyBuffer(nint buffer, byte[] pixels, int stride) {
        Guid key = Buffer2D;
        if (Marshal.QueryInterface(buffer, ref key, out nint rows) >= 0) {
            try {
                nint top = 0, start = 0;
                int pitch = 0;
                uint length = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int*, nint*, uint*, int>)Method(rows, 10))
                    (rows, 1, &top, &pitch, &start, &length)); // IMF2DBuffer2::Lock2DSize, read-only
                try { CopyRows(start, top, length, pitch, pixels, stride); }
                finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(rows, 4))(rows)); }
                return;
            } finally { Release(rows); }
        }
        nint data = 0;
        uint lengthBytes = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, uint*, int>)Method(buffer, 3))(buffer, &data, 0, &lengthBytes)); // IMFMediaBuffer::Lock
        try {
            nint top = _pitch < 0 ? data - (nint)_pitch * (_height - 1) : data;
            CopyRows(data, top, lengthBytes, _pitch, pixels, stride);
        } finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(buffer, 4))(buffer)); }
    }

    private void CopyRows(nint start, nint top, uint length, int pitch, byte[] pixels, int stride) {
        long first = top - start, last = first + (long)pitch * (_height - 1);
        if (start == 0 || Math.Abs((long)pitch) < stride || Math.Min(first, last) < 0 || Math.Max(first, last) + stride > length)
            throw new InvalidDataException("视频解码缓冲区大小与画面不符。");
        if (pitch == stride) { Marshal.Copy(top, pixels, 0, stride * _height); return; }
        for (int y = 0; y < _height; y++) Marshal.Copy(top + (nint)y * pitch, pixels, y * stride, stride);
    }

    internal void Rewind() {
        Guid timeFormat = Guid.Empty;
        var position = new PropVariant { Type = 20, Value = 0 }; // VT_I8, zero in 100 ns units
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Method(_reader, 8))(_reader, &timeFormat, &position)); // SetCurrentPosition
    }

    public void Dispose() {
        Release(_reader);
        _reader = 0;
        if (_mediaStarted) { _mediaStarted = false; MFShutdown(); }
        if (_comStarted) { _comStarted = false; CoUninitialize(); }
    }

    private static nint Method(nint pointer, int slot) => (*(nint**)pointer)[slot];
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(nint pointer) { if (pointer != 0) Marshal.Release(pointer); }
    private static void SetUInt32(nint target, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Method(target, 21))(target, &key, value));
    private static void SetGuid(nint target, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(target, 24))(target, &key, &value));

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal long Value; }

    [DllImport("ole32")] private static extern int CoInitializeEx(nint reserved, int coinit);
    [DllImport("ole32")] private static extern void CoUninitialize();
    [DllImport("mfplat")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat")] private static extern int MFShutdown();
    [DllImport("mfplat")] private static extern int MFCreateAttributes(out nint attributes, uint count);
    [DllImport("mfplat")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat")] private static extern int MFGetStrideForBitmapInfoHeader(uint format, uint width, out int stride);
    [DllImport("mfreadwrite", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
}
