using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace SchematicKit;

/// <summary>Orthographic thumbnails of the model faces already resolved for the schematic.</summary>
internal static class BlockIconProjection
{
    private const int Size = 48;
    private const int BytesPerPixel = 4;
    private static readonly Point[] FullUv = [new(0, 0), new(16, 0), new(16, 16), new(0, 16)];

    internal static BitmapSource Create(BitmapSource top, BitmapSource side) => Render([
        new Face(new(0, 1, 0), new(1, 1, 0), new(0, 1, 1), FullUv, top),
        new Face(new(0, 1, 1), new(1, 1, 1), new(0, 0, 1), FullUv, side),
        new Face(new(1, 1, 1), new(1, 1, 0), new(1, 0, 1), FullUv, side)
    ]);

    internal static BitmapSource Create(IReadOnlyList<BlockFaceQuad> quads, Func<string, BitmapSource> texture) =>
        Render(quads.Select(quad => new Face(quad.P0, quad.P1, quad.P3, quad.Uv, texture(quad.Texture))));

    private static BitmapSource Render(IEnumerable<Face> faces)
    {
        int stride = Size * BytesPerPixel;
        var pixels = new byte[Size * stride];
        var depth = new double[Size * Size];
        Array.Fill(depth, double.NegativeInfinity);
        var projected = new List<ProjectedFace>();
        Rect bounds = Rect.Empty;
        foreach (Face face in faces)
        {
            Point3D origin = Isometric(face.Start);
            Vector3D across = Isometric(face.Right) - origin;
            Vector3D down = Isometric(face.End) - origin;
            var area = Bounds(origin, across, down);
            if (!double.IsFinite(area.Left + area.Top + area.Width + area.Height) ||
                Math.Abs(across.X * down.Y - across.Y * down.X) <= 1e-9) continue;
            bounds.Union(area);
            Vector3D normal = Vector3D.CrossProduct(face.Right - face.Start, face.End - face.Start);
            double x = Math.Abs(normal.X), y = Math.Abs(normal.Y), z = Math.Abs(normal.Z);
            double light = (.64d * x + y + .82d * z) / (x + y + z);
            projected.Add(new ProjectedFace(face, origin, across, down, light));
        }

        if (projected.Count > 0 && double.IsFinite(bounds.Width + bounds.Height))
        {
            // Fit both axes with the same scale so slabs, rods and full cubes keep their proportions.
            double scale = (Size - 4) / Math.Max(bounds.Width, bounds.Height);
            double left = Size / 2d - (bounds.Left + bounds.Width / 2d) * scale;
            double top = Size / 2d - (bounds.Top + bounds.Height / 2d) * scale;
            var tiles = new Dictionary<BitmapSource, byte[]>();
            foreach (ProjectedFace face in projected)
            {
                if (!tiles.TryGetValue(face.Source.Texture, out byte[]? tile))
                    tiles.Add(face.Source.Texture, tile = ReadTile(face.Source.Texture));
                DrawFace(pixels, depth, tile, face, scale, left, top);
            }
        }

        var image = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static byte[] ReadTile(BitmapSource tile)
    {
        int stride = TextureAtlas.TileSize * BytesPerPixel;
        var pixels = new byte[TextureAtlas.TileSize * stride];
        tile.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static Point3D Isometric(Point3D point) => new((point.X - point.Z) / Math.Sqrt(2d),
        (point.X + point.Z - 2d * point.Y) / Math.Sqrt(6d), point.X + point.Y + point.Z);

    private static Rect Bounds(Point3D origin, Vector3D across, Vector3D down)
    {
        double left = origin.X + Math.Min(0, across.X) + Math.Min(0, down.X);
        double top = origin.Y + Math.Min(0, across.Y) + Math.Min(0, down.Y);
        return new Rect(left, top, Math.Abs(across.X) + Math.Abs(down.X), Math.Abs(across.Y) + Math.Abs(down.Y));
    }

    private static void DrawFace(byte[] destination, double[] depth, byte[] texture, ProjectedFace face, double scale, double left, double top)
    {
        const int tileSize = TextureAtlas.TileSize;
        var origin = new Point(face.Origin.X * scale + left, face.Origin.Y * scale + top);
        var horizontal = new Vector(face.Across.X * scale, face.Across.Y * scale);
        var vertical = new Vector(face.Down.X * scale, face.Down.Y * scale);
        Rect bounds = Bounds(new Point3D(origin.X, origin.Y, 0), new Vector3D(horizontal.X, horizontal.Y, 0),
            new Vector3D(vertical.X, vertical.Y, 0));
        double area = Vector.CrossProduct(horizontal, vertical);
        Point uvOrigin = face.Source.Uv[0];
        Vector uvRight = face.Source.Uv[1] - uvOrigin;
        Vector uvDown = face.Source.Uv[3] - uvOrigin;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Top)); y < Math.Min(Size, Math.Ceiling(bounds.Bottom)); y++)
        for (int x = Math.Max(0, (int)Math.Floor(bounds.Left)); x < Math.Min(Size, Math.Ceiling(bounds.Right)); x++)
        {
            var offset = new Vector(x + .5 - origin.X, y + .5 - origin.Y);
            double u = Vector.CrossProduct(offset, vertical) / area;
            double v = Vector.CrossProduct(horizontal, offset) / area;
            if (u is < 0 or >= 1 || v is < 0 or >= 1) continue;
            int pixel = y * Size + x;
            double distance = face.Origin.Z + u * face.Across.Z + v * face.Down.Z;
            if (distance <= depth[pixel]) continue;
            Point uv = TextureAtlas.NormalizeModelUv(uvOrigin + uvRight * u + uvDown * v);
            int tileX = Math.Min(tileSize - 1, (int)(uv.X * tileSize));
            int tileY = Math.Min(tileSize - 1, (int)(uv.Y * tileSize));
            int source = (tileY * tileSize + tileX) * BytesPerPixel;
            if (texture[source + 3] == 0) continue;
            depth[pixel] = distance;
            int target = pixel * BytesPerPixel;
            destination[target] = (byte)(texture[source] * face.Light);
            destination[target + 1] = (byte)(texture[source + 1] * face.Light);
            destination[target + 2] = (byte)(texture[source + 2] * face.Light);
            destination[target + 3] = texture[source + 3];
        }
    }

    private readonly record struct Face(Point3D Start, Point3D Right, Point3D End, Point[] Uv, BitmapSource Texture);
    private readonly record struct ProjectedFace(Face Source, Point3D Origin, Vector3D Across, Vector3D Down, double Light);
}
