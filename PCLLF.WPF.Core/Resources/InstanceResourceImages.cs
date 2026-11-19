// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCLCS;

/// <summary>LF 原 WPF 图片解码；同步加载并冻结后交回原调用线程。</summary>
public static class InstanceResourceImages {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore.DecodeImage.
    public static BitmapSource Decode(Stream stream, int width) {
        // Decode at full size first: some codecs (WebP icons from modpack sites) drop the alpha channel when asked to
        // scale while decoding, which turns transparent corners black. Convert to BGRA, then scale down.
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        if (width > 0 && frame.PixelWidth > width) {
            double scale = (double)width / frame.PixelWidth;
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        // Materialise the result so the decoder and its stream are not kept alive.
        var result = new WriteableBitmap(frame);
        result.Freeze();
        return result;
    }
}
