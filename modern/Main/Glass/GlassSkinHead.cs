// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCLLF.Presentation.Launch;

namespace PCL;

/// <summary>
/// Renders a skin's face as a flat 2D head with the outer (hat) layer in relief, like the "3D skin layers" look: the 8×8
/// face is flat, and every opaque hat pixel is a slightly larger raised block with a darker lower edge (thickness) and a
/// soft shadow on the face. The result is one bitmap drawn on whole pixels, so there are no seams between blocks;
/// display it with nearest-neighbour scaling. Results are cached per loaded layer image.
/// </summary>
internal static class GlassSkinHead
{
    // Face cell 16 px; the hat cell is 18 px (9/8), centred so it overhangs the face by 8 px on each side.
    private const int Cell = 16, HatCell = 18, Overhang = 8, Depth = 4, ShadowX = 1, ShadowY = 6;
    private const int Size = 8 * HatCell + Depth + 2; // 150
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, Entry> Cache = new();
    private sealed class Entry { internal ImageSource? Fore; internal BitmapSource? Image; }
    private sealed record SavedHead(BitmapSource? Image);
    private static readonly ConcurrentDictionary<string, SavedHead> LastGoodHeads = new(StringComparer.OrdinalIgnoreCase);

    private static string? HeadPath(string kind, LegacySkinRequest request) {
        if (string.IsNullOrWhiteSpace(request.Id) || (kind == "Legacy" && request.Name != "3")) return null;
        string server = request.Server ?? "";
        if (Uri.TryCreate(server, UriKind.Absolute, out var uri)) server = uri.AbsoluteUri.TrimEnd('/');
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\0{server}\0{request.Id}")));
        // Keep confirmed avatars even when the ordinary skin/download caches are refreshed.
        return Path.Combine(ModBase.PathTemp, "Cache", "AccountAvatars", id + ".png");
    }

    internal static BitmapSource? LastGood(string kind, LegacySkinRequest request) {
        if (HeadPath(kind, request) is not { } path) return null;
        return LastGoodHeads.GetOrAdd(path, static file => {
            try {
                if (!File.Exists(file)) return new SavedHead(null);
                using var input = File.OpenRead(file);
                var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return new SavedHead(frame);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException) {
                return new SavedHead(null);
            }
        }).Image;
    }

    internal static void Remember(string kind, LegacySkinRequest request, BitmapSource image) {
        if (HeadPath(kind, request) is not { } path) return;
        if (LastGoodHeads.TryGetValue(path, out var old) && ReferenceEquals(old.Image, image)) return;
        var saved = new SavedHead(image);
        LastGoodHeads[path] = saved;
        _ = Task.Run(() => {
            string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var output = File.Create(staged)) {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(output);
                }
                lock (LastGoodHeads) {
                    if (LastGoodHeads.TryGetValue(path, out var latest) && ReferenceEquals(saved, latest)) File.Move(staged, path, overwrite: true);
                }
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) {
                Logger.Warn(error, "保存账号头像缓存失败");
            } finally {
                try { File.Delete(staged); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });
    }

    /// <summary>
    /// Renders from the original skin control's two already-loaded layers (face <c>ImgBack</c>, hat <c>ImgFore</c>),
    /// which always show the account's actual skin. Each layer is sampled on an 8×8 grid, whatever its pixel size.
    /// </summary>
    internal static BitmapSource? Render(ImageSource? back, ImageSource? fore)
    {
        if (back is not BitmapSource face) return null;
        if (Cache.TryGetValue(face, out Entry? cached) && ReferenceEquals(cached.Fore, fore)) return cached.Image;
        BitmapSource? image = null;
        try
        {
            Color[]? facePixels = Sample(face);
            Color[]? hatPixels = fore is BitmapSource hat ? Sample(hat) : null;
            if (facePixels is not null)
            {
                var pixels = new Color[128];
                Array.Copy(facePixels, pixels, 64);
                if (hatPixels is not null) Array.Copy(hatPixels, 0, pixels, 64, 64);
                image = Draw(pixels);
            }
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException) { image = null; }
        Cache.AddOrUpdate(face, new Entry { Fore = fore, Image = image });
        return image;
    }

    // Centre of each of the 8×8 cells of a square layer image.
    private static Color[]? Sample(BitmapSource layer)
    {
        if (layer.PixelWidth < 8 || layer.PixelHeight < 8) return null;
        var bgra = layer.Format == PixelFormats.Bgra32 ? layer : new FormatConvertedBitmap(layer, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var data = new byte[stride * height];
        bgra.CopyPixels(data, stride, 0);
        var pixels = new Color[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int px = (int)((x + .5) * width / 8), py = (int)((y + .5) * height / 8), i = py * stride + px * 4;
                pixels[y * 8 + x] = Color.FromArgb(data[i + 3], data[i + 2], data[i + 1], data[i]);
            }
        return pixels;
    }

    private static BitmapSource? Draw(Color[]? pixels)
    {
        if (pixels is null) return null;
        var canvas = new byte[Size * Size * 4];
        int offset = 1; // keep the shadow inside the bitmap
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                Color face = pixels[y * 8 + x];
                if (face.A < 8) continue;
                Fill(canvas, offset + Overhang + x * Cell, Overhang + y * Cell, Cell, Cell, Color.FromRgb(face.R, face.G, face.B));
            }
        // Pass 0: shadows on the face; pass 1: each raised pixel's lower edge; pass 2: its face.
        for (int pass = 0; pass < 3; pass++)
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    Color hat = pixels[64 + y * 8 + x];
                    if (hat.A < 8) continue;
                    int left = offset + x * HatCell, top = y * HatCell;
                    if (pass == 0) Fill(canvas, left + ShadowX, top + ShadowY, HatCell, HatCell, Color.FromArgb((byte)(80 * hat.A / 255), 0, 0, 0));
                    else if (pass == 1) Fill(canvas, left, top + Depth, HatCell, HatCell, Shade(hat, .58));
                    else Fill(canvas, left, top, HatCell, HatCell, Shade(hat, 1.06));
                }
        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, canvas, Size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // Source-over blend in premultiplied BGRA.
    private static void Fill(byte[] canvas, int left, int top, int width, int height, Color color)
    {
        double alpha = color.A / 255d;
        for (int y = Math.Max(0, top); y < Math.Min(Size, top + height); y++)
            for (int x = Math.Max(0, left); x < Math.Min(Size, left + width); x++)
            {
                int i = (y * Size + x) * 4;
                double keep = 1 - alpha;
                canvas[i] = (byte)Math.Round(color.B * alpha + canvas[i] * keep);
                canvas[i + 1] = (byte)Math.Round(color.G * alpha + canvas[i + 1] * keep);
                canvas[i + 2] = (byte)Math.Round(color.R * alpha + canvas[i + 2] * keep);
                canvas[i + 3] = (byte)Math.Round(color.A + canvas[i + 3] * keep);
            }
    }

    private static Color Shade(Color color, double factor) => Color.FromArgb(color.A,
        (byte)Math.Clamp(color.R * factor, 0, 255), (byte)Math.Clamp(color.G * factor, 0, 255), (byte)Math.Clamp(color.B * factor, 0, 255));
}
