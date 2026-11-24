// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCLLF.Wpf.Glass;

/// <summary>A shared compositor brush over the same live VideoDrawing as the wallpaper.</summary>
public sealed class LiveVideoFrost {
    private readonly VideoDrawing _video;
    private readonly DrawingVisual _plane = new();
    private readonly BlurEffect _blur = new() { RenderingBias = RenderingBias.Performance };
    private readonly ImageBrush _tiles;
    private readonly VisualBrush _sample;
    private readonly GeometryDrawing _material;
    private (Size Video, Size Plane, double Radius) _layout;
    public DrawingImage Source { get; }

    public LiveVideoFrost(DrawingImage source) {
        _video = (VideoDrawing)source.Drawing;
        _tiles = new ImageBrush(source) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.FlipXY, Stretch = Stretch.Fill };
        _plane.Effect = _blur;
        var root = new ContainerVisual();
        root.Children.Add(_plane);
        _sample = new VisualBrush(root) { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        _material = new GeometryDrawing { Brush = _sample };
        Source = new DrawingImage(_material);
        WallpaperBlurCache.RegisterVideoFrame(Source);
        SetViewport(_video.Rect.Width, _video.Rect.Height, false, 0);
    }

    public void SetViewport(double width, double height, bool stretch, double extraBlur) {
        Size video = _video.Rect.Size;
        if (video.Width <= 0 || video.Height <= 0 || width <= 0 || height <= 0) return;
        double cover = Math.Max(width / video.Width, height / video.Height);
        double displayWidth = stretch ? width : cover * video.Width;
        double displayHeight = stretch ? height : cover * video.Height;
        double scale = Math.Min(1, Math.Min(320 / displayWidth, 240 / displayHeight));
        var plane = new Size(displayWidth * scale, displayHeight * scale);
        double radius = Math.Sqrt(24 * 24 + extraBlur * extraBlur) * scale;
        if (_layout == (video, plane, radius)) return;
        _layout = (video, plane, radius);
        Rect viewbox = new(new Point(), plane);
        _tiles.Viewport = _sample.Viewbox = viewbox;
        _blur.Radius = radius;
        _material.Geometry = new RectangleGeometry(new Rect(new Point(), video));
        viewbox.Inflate(Math.Ceiling(radius) + 1, Math.Ceiling(radius) + 1);
        using DrawingContext drawing = _plane.RenderOpen();
        drawing.DrawRectangle(_tiles, null, viewbox);
    }
}
