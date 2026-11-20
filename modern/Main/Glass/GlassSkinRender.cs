// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using PCLCS.Applications;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>Skin texture helpers: normalised 64×64 pixels and a flat full-body front view.</summary>
internal static class GlassSkinTexture
{
    /// <summary>The skin as 64×64 BGRA pixels; a legacy 64×32 skin gets its left limbs mirrored from the right ones.</summary>
    internal static uint[] Normalise(BitmapSource texture)
    {
        var source = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var raw = new uint[width * height];
        source.CopyPixels(raw, width * 4, 0);
        // Sample on the 64-unit grid whatever the resolution (HD skins are integer multiples).
        double unit = width / 64d;
        var pixels = new uint[64 * 64];
        int rows = height * 64 / width >= 64 ? 64 : 32;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < 64; x++)
                pixels[y * 64 + x] = raw[Math.Min(height - 1, (int)(y * unit)) * width + Math.Min(width - 1, (int)(x * unit))];
        if (rows == 32)
        {
            MirrorLimb(pixels, 0, 16, 16, 48, 4);   // right leg → left leg
            MirrorLimb(pixels, 40, 16, 32, 48, 4);  // right arm → left arm
        }
        return pixels;
    }

    // Copies a 4×12×4 limb's six faces mirrored (and its side faces swapped), as Minecraft does for 64×32 skins.
    private static void MirrorLimb(uint[] p, int u, int v, int du, int dv, int w)
    {
        const int d = 4, h = 12;
        void Copy(int sx, int sy, int dx, int dy, int cw, int ch)
        {
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    p[(dy + y) * 64 + dx + x] = p[(sy + y) * 64 + sx + cw - 1 - x];
        }
        Copy(u + d, v, du + d, dv, w, d);                         // top
        Copy(u + d + w, v, du + d + w, dv, w, d);                 // bottom
        Copy(u, v + d, du + d + w, dv + d, d, h);                 // right side → left side
        Copy(u + d, v + d, du + d, dv + d, w, h);                 // front
        Copy(u + d + w, v + d, du, dv + d, d, h);                 // left side → right side
        Copy(u + d + w + d, v + d, du + d + w + d, dv + d, w, h); // back
    }

    /// <summary>
    /// Whether the skin is drawn for slim (3 px) arms: on such skins the right arm's last back column (x 54–55, rows
    /// 20–31) and the end of its top/bottom strip (x 50–51, rows 16–19) are left fully transparent.
    /// </summary>
    internal static bool LooksSlim(uint[] skin)
    {
        for (int y = 20; y < 32; y++)
            for (int x = 54; x < 56; x++)
                if (skin[y * 64 + x] >> 24 != 0) return false;
        for (int y = 16; y < 20; y++)
            for (int x = 50; x < 52; x++)
                if (skin[y * 64 + x] >> 24 != 0) return false;
        return true;
    }

    /// <summary>
    /// The front of the whole player with its outer layer in relief (like the account head): each outer part is drawn a
    /// little larger than its base part, centred on it, over a soft shadow, so hat, jacket, sleeves and trousers stand out.
    /// Rendered at 10 px per skin pixel; works off the UI thread.
    /// </summary>
    internal static BitmapSource FrontViewLayered(uint[] skin, SkinModel model)
    {
        const int cell = 10, margin = 6;
        int arm = model == SkinModel.Slim ? 3 : 4;
        int width = 16 * cell + 2 * margin, height = 32 * cell + 2 * margin;
        var canvas = new uint[width * height];

        void Blend(int x, int y, uint color)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            uint alpha = color >> 24;
            if (alpha == 0) return;
            int index = y * width + x;
            uint under = canvas[index];
            if (alpha == 255 || under >> 24 == 0) { canvas[index] = alpha == 255 ? color : color; return; }
            uint outA = alpha + (under >> 24) * (255 - alpha) / 255;
            uint Mix(int shift) => ((((color >> shift) & 255) * alpha + ((under >> shift) & 255) * (255 - alpha)) / 255) << shift;
            canvas[index] = (outA << 24) | Mix(16) | Mix(8) | Mix(0);
        }
        void Fill(double left, double top, double right, double bottom, uint color)
        {
            for (int y = (int)Math.Round(top); y < (int)Math.Round(bottom); y++)
                for (int x = (int)Math.Round(left); x < (int)Math.Round(right); x++) Blend(x, y, color);
        }
        // One part's front face: (sx, sy) in the skin, w×h pixels, placed at (dx, dy) in body pixels; grow > 0 draws it
        // enlarged by that many screen pixels on every side (outer layer), with a shadow first.
        void Part(int sx, int sy, int dx, int dy, int w, int h, double grow)
        {
            double left = margin + dx * cell, top = margin + dy * cell;
            double scaleX = (w * cell + 2 * grow) / (w * cell), scaleY = (h * cell + 2 * grow) / (h * cell);
            double originX = left - grow, originY = top - grow;
            for (int pass = grow > 0 ? 0 : 1; pass < 2; pass++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint color = skin[(sy + y) * 64 + sx + x];
                        if (color >> 24 == 0) continue;
                        double l = originX + x * cell * scaleX, t = originY + y * cell * scaleY;
                        double r = l + cell * scaleX, b = t + cell * scaleY;
                        if (pass == 0) Fill(l + 1, t + 3, r + 1, b + 3, 0x30000000); // shadow under the raised layer
                        else Fill(l, t, r, b, color);
                    }
        }
        // Base layer.
        Part(8, 8, 4, 0, 8, 8, 0); Part(20, 20, 4, 8, 8, 12, 0);
        Part(44, 20, 4 - arm, 8, arm, 12, 0); Part(36, 52, 12, 8, arm, 12, 0);
        Part(4, 20, 4, 20, 4, 12, 0); Part(20, 52, 8, 20, 4, 12, 0);
        // Outer layer in relief: legs and arms first, then the jacket, the hat on top.
        Part(4, 36, 4, 20, 4, 12, 2); Part(4, 52, 8, 20, 4, 12, 2);
        Part(20, 36, 4, 8, 8, 12, 2);
        Part(44, 36, 4 - arm, 8, arm, 12, 2); Part(52, 52, 12, 8, arm, 12, 2);
        Part(40, 8, 4, 0, 8, 8, 4);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, Premultiply(canvas), width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static uint[] Premultiply(uint[] pixels)
    {
        var result = new uint[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint c = pixels[i], a = c >> 24;
            if (a == 255 || a == 0) { result[i] = a == 0 ? 0 : c; continue; }
            uint P(int shift) => ((((c >> shift) & 255) * a) / 255) << shift;
            result[i] = (a << 24) | P(16) | P(8) | P(0);
        }
        return result;
    }

    /// <summary>The front of the whole player (16×32 px, outer layer over the base), for cards.</summary>
    internal static BitmapSource FrontView(uint[] skin, SkinModel model)
    {
        int arm = model == SkinModel.Slim ? 3 : 4;
        var canvas = new uint[16 * 32];
        void Blit(int sx, int sy, int dx, int dy, int w, int h)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint color = skin[(sy + y) * 64 + sx + x];
                    uint alpha = color >> 24;
                    if (alpha == 0) continue;
                    int index = (dy + y) * 16 + dx + x;
                    if (alpha == 255) { canvas[index] = color; continue; }
                    uint under = canvas[index];
                    uint Mix(int shift) => ((((color >> shift) & 255) * alpha + ((under >> shift) & 255) * (255 - alpha)) / 255) << shift;
                    canvas[index] = 0xFF000000 | Mix(16) | Mix(8) | Mix(0);
                }
        }
        // Base layer, then the outer layer, part by part (front faces).
        Blit(8, 8, 4, 0, 8, 8); Blit(20, 20, 4, 8, 8, 12);
        Blit(44, 20, 4 - arm, 8, arm, 12); Blit(36, 52, 12, 8, arm, 12);
        Blit(4, 20, 4, 20, 4, 12); Blit(20, 52, 8, 20, 4, 12);
        Blit(40, 8, 4, 0, 8, 8); Blit(20, 36, 4, 8, 8, 12);
        Blit(44, 36, 4 - arm, 8, arm, 12); Blit(52, 52, 12, 8, arm, 12);
        Blit(4, 36, 4, 20, 4, 12); Blit(4, 52, 8, 20, 4, 12);
        var bitmap = BitmapSource.Create(16, 32, 96, 96, PixelFormats.Bgra32, null, canvas, 16 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A cape's outer face (10×16 units), for cards.</summary>
    internal static BitmapSource CapeFront(BitmapSource cape)
    {
        double unit = cape.PixelWidth / 64d;
        var crop = new CroppedBitmap(cape, new Int32Rect((int)unit, (int)unit, (int)(10 * unit), (int)(16 * unit)));
        crop.Freeze();
        return crop;
    }

    /// <summary>Any texture (a cape) upscaled by nearest neighbour to about 512 px wide, so its 3D faces stay crisp.</summary>
    internal static BitmapSource UpscaledTexture(BitmapSource texture)
    {
        var source = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        int factor = Math.Max(1, 512 / Math.Max(1, width));
        if (factor == 1) return source;
        var raw = new uint[width * height];
        source.CopyPixels(raw, width * 4, 0);
        int bigWidth = width * factor, bigHeight = height * factor;
        var big = new uint[bigWidth * bigHeight];
        for (int y = 0; y < bigHeight; y++)
            for (int x = 0; x < bigWidth; x++)
                big[y * bigWidth + x] = raw[(y / factor) * width + x / factor];
        var bitmap = BitmapSource.Create(bigWidth, bigHeight, 96, 96, PixelFormats.Bgra32, null, big, bigWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The skin upscaled 8× by nearest neighbour, so the 3D faces stay crisp.</summary>
    internal static BitmapSource Upscaled(uint[] skin, int factor = 8)
    {
        int size = 64 * factor;
        var big = new uint[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                big[y * size + x] = skin[(y / factor) * 64 + x / factor];
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, big, size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// The player in 3D: textured boxes for head, body, arms and legs with the outer layer slightly inflated around each,
/// an optional cape. Drag sideways to turn it (it glides on a spring when let go); the limbs swing gently as if
/// walking while the viewer is on screen.
/// </summary>
internal sealed class GlassSkinViewer : Grid
{
    private readonly Viewport3D _viewport = new() { ClipToBounds = false, IsHitTestVisible = false };
    private readonly ModelVisual3D _visual = new();
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), -24);
    private readonly AxisAngleRotation3D _rightArm = new(new Vector3D(1, 0, 0), 0), _leftArm = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _rightLeg = new(new Vector3D(1, 0, 0), 0), _leftLeg = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _capeSwing = new(new Vector3D(1, 0, 0), 8);
    private readonly SpringMotion _yawMotion;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private Point? _dragStart;
    private double _dragOrigin;
    private bool _animating;

    internal GlassSkinViewer()
    {
        Background = Brushes.Transparent; // the whole area takes the drag
        Cursor = Cursors.SizeWE;
        _viewport.Camera = new PerspectiveCamera(new Point3D(0, 17, 58), new Vector3D(0, -.06, -1), new Vector3D(0, 1, 0), 34);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(214, 214, 214)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(70, 70, 70), new Vector3D(-.4, -.5, -1)));
        _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(_visual);
        Children.Add(_viewport);
        _yawMotion = new SpringMotion(this, _yaw.Angle, value => _yaw.Angle = value, new SpringParameters(.6, .86));
        MouseLeftButtonDown += (_, args) => { _dragStart = args.GetPosition(this); _dragOrigin = _yaw.Angle; CaptureMouse(); args.Handled = true; };
        MouseMove += (_, args) =>
        {
            if (_dragStart is not Point start || !IsMouseCaptured) return;
            _yawMotion.Snap(_dragOrigin + (args.GetPosition(this).X - start.X) * .9);
        };
        MouseLeftButtonUp += (_, _) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); _dragStart = null; };
        Loaded += (_, _) => SetAnimating(IsVisible);
        IsVisibleChanged += (_, _) => SetAnimating(IsVisible);
        Unloaded += (_, _) => SetAnimating(false);
    }

    /// <summary>Shows <paramref name="skin"/> (normalised 64×64 pixels) with an optional cape texture; null clears.</summary>
    internal void Show(uint[]? skin, SkinModel model, BitmapSource? cape)
    {
        if (skin is null) { _visual.Content = null; SetAnimating(false); return; }
        var material = new DiffuseMaterial(new ImageBrush(GlassSkinTexture.Upscaled(skin)) { ViewportUnits = BrushMappingMode.Absolute });
        material.Freeze();
        var model3D = new Model3DGroup();
        int arm = model == SkinModel.Slim ? 3 : 4;
        // Base boxes first, outer layers after (drawn later so their transparent texels reveal the base).
        var head = Part(model3D, null);
        Box(head, material, 0, 0, -4, 24, -4, 8, 8, 8, 0);
        var body = Part(model3D, null);
        Box(body, material, 16, 16, -4, 12, -2, 8, 12, 4, 0);
        var rightArm = Part(model3D, new RotateTransform3D(_rightArm, new Point3D(-4 - arm / 2d, 22, 0)));
        Box(rightArm, material, 40, 16, -4 - arm, 12, -2, arm, 12, 4, 0);
        var leftArm = Part(model3D, new RotateTransform3D(_leftArm, new Point3D(4 + arm / 2d, 22, 0)));
        Box(leftArm, material, 32, 48, 4, 12, -2, arm, 12, 4, 0);
        var rightLeg = Part(model3D, new RotateTransform3D(_rightLeg, new Point3D(-2, 12, 0)));
        Box(rightLeg, material, 0, 16, -4, 0, -2, 4, 12, 4, 0);
        var leftLeg = Part(model3D, new RotateTransform3D(_leftLeg, new Point3D(2, 12, 0)));
        Box(leftLeg, material, 16, 48, 0, 0, -2, 4, 12, 4, 0);
        Box(head, material, 32, 0, -4, 24, -4, 8, 8, 8, .5);
        Box(body, material, 16, 32, -4, 12, -2, 8, 12, 4, .25);
        Box(rightArm, material, 40, 32, -4 - arm, 12, -2, arm, 12, 4, .25);
        Box(leftArm, material, 48, 48, 4, 12, -2, arm, 12, 4, .25);
        Box(rightLeg, material, 0, 32, -4, 0, -2, 4, 12, 4, .25);
        Box(leftLeg, material, 0, 48, 0, 0, -2, 4, 12, 4, .25);
        if (cape is not null)
        {
            var capeMaterial = new DiffuseMaterial(new ImageBrush(GlassSkinTexture.UpscaledTexture(cape)) { ViewportUnits = BrushMappingMode.Absolute });
            RenderOptions.SetBitmapScalingMode((ImageBrush)capeMaterial.Brush, BitmapScalingMode.NearestNeighbor);
            capeMaterial.Freeze();
            var capePart = Part(model3D, new RotateTransform3D(_capeSwing, new Point3D(0, 24, -2.5)));
            // The cape hangs behind the body, its outer face (the texture's "front" of a 10×16×1 box) facing away.
            Box(capePart, capeMaterial, 0, 0, -5, 8, -3.1, 10, 16, 1, 0, flipFacing: true, textureWidth: 64, textureHeight: 32);
        }
        var turn = new Transform3DGroup();
        turn.Children.Add(new RotateTransform3D(_yaw, new Point3D(0, 16, 0)));
        model3D.Transform = turn;
        _visual.Content = model3D;
        SetAnimating(IsVisible);
    }

    private static Model3DGroup Part(Model3DGroup root, Transform3D? pivot)
    {
        var part = new Model3DGroup();
        if (pivot is not null) part.Transform = pivot;
        root.Children.Add(part);
        return part;
    }

    // One textured box from Minecraft's unfolded layout at (u, v): [top][bottom] over [right][front][left][back].
    // Coordinates are skin pixels, y up, the player facing +z; inflate grows the box on every side (outer layer).
    private static void Box(Model3DGroup target, Material material, int u, int v, double x, double y, double z, int w, int h, int d,
        double inflate, bool flipFacing = false, int textureWidth = 64, int textureHeight = 64)
    {
        double x0 = x - inflate, x1 = x + w + inflate, y0 = y - inflate, y1 = y + h + inflate, z0 = z - inflate, z1 = z + d + inflate;
        if (flipFacing) (z0, z1) = (z1, z0);
        var mesh = new MeshGeometry3D();
        void Face(Point3D tl, Point3D tr, Point3D br, Point3D bl, double tu, double tv, double tw, double th, bool mirror = false)
        {
            int start = mesh.Positions.Count;
            mesh.Positions.Add(tl); mesh.Positions.Add(tr); mesh.Positions.Add(br); mesh.Positions.Add(bl);
            const double inset = .02; // keeps neighbouring texels from bleeding along the edges
            double left = (tu + inset) / textureWidth, right = (tu + tw - inset) / textureWidth;
            double top = (tv + inset) / textureHeight, bottom = (tv + th - inset) / textureHeight;
            if (mirror) (left, right) = (right, left);
            mesh.TextureCoordinates.Add(new Point(left, top)); mesh.TextureCoordinates.Add(new Point(right, top));
            mesh.TextureCoordinates.Add(new Point(right, bottom)); mesh.TextureCoordinates.Add(new Point(left, bottom));
            foreach (int index in new[] { 0, 3, 2, 0, 2, 1 }) mesh.TriangleIndices.Add(start + index);
        }
        Face(new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1), u + d, v + d, w, h);                    // front
        Face(new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0), u + d + w + d, v + d, w, h);            // back
        Face(new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0), u, v + d, d, h);                        // right side (−x)
        Face(new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1), u + d + w, v + d, d, h);                // left side (+x)
        Face(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), u + d, v, w, d);                        // top
        Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), u + d + w, v, w, d);                    // bottom
        mesh.Freeze();
        var geometry = new GeometryModel3D(mesh, material) { BackMaterial = inflate > 0 || flipFacing ? material : null };
        geometry.Freeze();
        target.Children.Add(geometry);
    }

    private void SetAnimating(bool on)
    {
        on = on && IsLoaded && _visual.Content is not null && MotionPreferences.AllowsAnimation;
        if (on == _animating) return;
        _animating = on;
        if (on) { _clock.Start(); CompositionTarget.Rendering += OnFrame; }
        else { _clock.Stop(); CompositionTarget.Rendering -= OnFrame; }
    }

    // A slow walk: arms and legs swing in opposition, the cape sways a little behind.
    private void OnFrame(object? sender, EventArgs args)
    {
        double t = _clock.Elapsed.TotalSeconds * 3.2;
        double swing = Math.Sin(t) * 22;
        _rightArm.Angle = swing; _leftArm.Angle = -swing;
        _rightLeg.Angle = -swing * .9; _leftLeg.Angle = swing * .9;
        _capeSwing.Angle = 9 + Math.Abs(Math.Sin(t)) * 5;
    }
}
