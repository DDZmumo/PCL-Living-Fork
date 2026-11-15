using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImage = System.Drawing.Image;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingRotateFlipType = System.Drawing.RotateFlipType;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;

namespace PCLCS;

// LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 Modules/Base/MyBitmap.vb is
// the behavior source (repository LICENCE). Its URI normalization enhancement
// differs from PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 and is retained.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5
// Plain Craft Launcher 2/Modules/Base/MyBitmap.cs is a C# syntax/conversion
// reference (CE Plain Craft Launcher 2/LICENCE; LF LICENCE). Its instance cache and WIC WebP
// replacement are not reused: LF keeps the shared cache and original Imazen.
/// <summary>Complete LF bitmap conversion, resource/file load, crop, rotation and PNG save.</summary>
public class LegacyBitmap {
    // The old static-local cache owns separate wrappers whose only observable
    // value is the shared Bitmap. Retain that same cross-instance Pic identity.
    private static readonly ConcurrentDictionary<string, DrawingBitmap> packCache = new();

    // Single storage, inherited directly by the original-name VB adapter.
    public DrawingBitmap? Pic;

    public LegacyBitmap() { }

    public LegacyBitmap(string? filePathOrResourceName, LegacyBitmapHost host) {
        try {
            filePathOrResourceName = NormalizeResourceUri(filePathOrResourceName, host.ResourceAssemblyName);
            if (filePathOrResourceName?.StartsWith("pack://application:,,,/", StringComparison.OrdinalIgnoreCase) == true) {
                if (packCache.ContainsKey(filePathOrResourceName)) {
                    Pic = packCache[filePathOrResourceName];
                } else {
                    Pic = FromImageSource((ImageSource)new ImageSourceConverter().ConvertFromString(filePathOrResourceName)!);
                    packCache.TryAdd(filePathOrResourceName, Pic);
                }
            } else {
                // Use the same pinned Shared helper: retry, path policy and
                // FileShare.ReadWrite are unchanged; dispose this owned stream.
                using Stream input = FileUtils.ReadAsStream(filePathOrResourceName!);
                byte[] header = new byte[2];
                // LF intentionally ignores the count for its zero-filled two-byte
                // RI probe; ReadExactly would change short-file fallback behavior.
#pragma warning disable CA2022
                input.Read(header, 0, 2);
#pragma warning restore CA2022
                input.Seek(0, SeekOrigin.Begin);
                if (header[0] == 82 && header[1] == 73) {
                    host.PrepareWebP?.Invoke();
                    // LF dispatches on these first two bytes, not a new format probe.
                    byte[] fileBytes = new byte[input.Length];
                    // Keep LF's single FileUtils FileStream read and zero-filled
                    // remainder: the original decoder/fallback owns short-data errors.
#pragma warning disable CA2022
                    input.Read(fileBytes, 0, fileBytes.Length);
#pragma warning restore CA2022
                    Pic = WebPDecoder.DecodeFromBytes(fileBytes);
                } else {
                    Pic = new DrawingBitmap(input);
                }
            }
        } catch (Exception error) {
            // Do not broaden this inner cast/fallback: resource-lookup failures
            // still propagate from the original catch, and failed loads still
            // allocate LF's 1x1 placeholder before throwing the original message.
            Pic = host.FindBitmapResource(filePathOrResourceName);
            if (Pic is null) {
                Pic = new DrawingBitmap(1, 1);
                throw error is ArgumentException
                    ? new Exception($"图片格式不支持，或图片文件损坏（{filePathOrResourceName}）", error)
                    : new Exception($"加载 MyBitmap 意外失败（{filePathOrResourceName}）", error);
            }
            host.Warn(error, $"指定类型有误的 MyBitmap 加载（{filePathOrResourceName}）");
        }
    }

    public LegacyBitmap(ImageSource image) => Pic = FromImageSource(image);
    public LegacyBitmap(DrawingImage image) => Pic = (DrawingBitmap)image;
    public LegacyBitmap(DrawingBitmap image) => Pic = image;
    public LegacyBitmap(ImageBrush image) => Pic = FromImageBrush(image);

    public static string? NormalizeResourceUri(string? value, string resourceAssemblyName) {
        if (string.IsNullOrEmpty(value)) return value;
        string resourceRoot = "pack://application:,,,/" + resourceAssemblyName + ";component/";
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        value = Regex.Replace(value,
            @"\A(?:pack://application:,,,/|/)(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL);component/",
            resourceRoot, options);
        return Regex.Replace(value, @"\Apack://application:,,,/images/", resourceRoot + "Images/", options);
    }

    private static DrawingBitmap FromImageSource(ImageSource image) {
        using MemoryStream stream = new();
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image));
        encoder.Save(stream);
        return new DrawingBitmap(stream);
    }

    private static DrawingBitmap FromImageBrush(ImageBrush image) {
        using MemoryStream stream = new();
        BmpBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image.ImageSource));
        encoder.Save(stream);
        return new DrawingBitmap(stream);
    }

    public BitmapSource ToImageSource() {
        DrawingBitmap bitmap = Pic!;
        DrawingRectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, DrawingImageLockMode.ReadWrite, DrawingPixelFormat.Format32bppArgb);
        try {
            BitmapSource result = BitmapSource.Create(bitmap.Width, bitmap.Height, bitmap.HorizontalResolution, bitmap.VerticalResolution,
                PixelFormats.Bgra32, null, data.Scan0, checked(rect.Width * rect.Height * 4), data.Stride);
            result.Freeze();
            return result;
        } finally {
            bitmap.UnlockBits(data);
        }
    }

    public ImageBrush ToImageBrush() => new(ToImageSource());

    // A legacy subclass only changes the wrapper type, never the actual pixels.
    protected virtual LegacyBitmap Wrap(DrawingBitmap bitmap) => new(bitmap);

    public LegacyBitmap Clip(int x, int y, int width, int height) {
        DrawingBitmap bitmap = new(width, height, Pic!.PixelFormat);
        bitmap.SetResolution(Pic!.HorizontalResolution, Pic!.VerticalResolution);
        using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap)) {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.TranslateTransform(checked(-x), checked(-y));
            graphics.DrawImage(Pic!, new DrawingRectangle(0, 0, Pic!.Width, Pic!.Height));
        }
        return Wrap(bitmap);
    }

    public LegacyBitmap RotateFlip(DrawingRotateFlipType type) {
        DrawingBitmap bitmap = new(Pic!);
        bitmap.SetResolution(Pic!.HorizontalResolution, Pic.VerticalResolution);
        bitmap.RotateFlip(type);
        return Wrap(bitmap);
    }

    public void Save(string filePath) {
        BitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToImageSource()));
        using Stream output = FileUtils.CreateAsStream(filePath);
        encoder.Save(output);
    }

    private static class WebPDecoder {
        // Keep the original lazy assembly boundary even on the modern JIT:
        // plain files and pack resources must not load Imazen.WebP/native WebP.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static DrawingBitmap DecodeFromBytes(byte[] bytes) =>
            new Imazen.WebP.SimpleDecoder().DecodeFromBytes(bytes, bytes.Length);
    }
}
