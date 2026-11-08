// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.IO;
using System.Windows.Media.Imaging;

namespace PCLCS;

/// <summary>LF 原 WPF 图片解码；同步加载并冻结后交回原调用线程。</summary>
public static class InstanceResourceImages {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore.DecodeImage.
    public static BitmapSource Decode(Stream stream, int width) {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
