using System.Windows.Media;

namespace SchematicKit;

/// <summary>
///     控件配色。默认是一套中性浅色（接近 Fluent），宿主可以调用 <see cref="Apply" /> 换成自己的配色。
///     主题在控件构造时读取，切换主题后重建页面即可生效。
/// </summary>
public static class SchematicTheme
{
    /// <summary>浅色配色，默认。</summary>
    public static SchematicPalette Light { get; } = new(
        WindowBackground: Color.FromRgb(0xF6, 0xF7, 0xF9),
        Surface: Color.FromRgb(0xFF, 0xFF, 0xFF),
        SurfaceHover: Color.FromRgb(0xEE, 0xF1, 0xF6),
        Border: Color.FromRgb(0xE1, 0xE5, 0xEC),
        TextPrimary: Color.FromRgb(0x21, 0x25, 0x2B),
        TextSecondary: Color.FromRgb(0x55, 0x5D, 0x69),
        TextMuted: Color.FromRgb(0x8A, 0x93, 0xA0),
        Accent: Color.FromRgb(0x2F, 0x6F, 0xED),
        AccentHover: Color.FromRgb(0x25, 0x5F, 0xD6),
        AccentForeground: Color.FromRgb(0xFF, 0xFF, 0xFF),
        PreviewBackground: Color.FromRgb(0x1E, 0x21, 0x26));

    /// <summary>深色配色，适合游戏启动器一类本来就偏暗的界面。</summary>
    public static SchematicPalette Dark { get; } = new(
        WindowBackground: Color.FromRgb(0x1B, 0x1D, 0x21),
        Surface: Color.FromRgb(0x25, 0x28, 0x2E),
        SurfaceHover: Color.FromRgb(0x2F, 0x33, 0x3A),
        Border: Color.FromRgb(0x3A, 0x3F, 0x47),
        TextPrimary: Color.FromRgb(0xF2, 0xF3, 0xF5),
        TextSecondary: Color.FromRgb(0xC3, 0xC8, 0xD0),
        TextMuted: Color.FromRgb(0x8B, 0x92, 0x9D),
        Accent: Color.FromRgb(0x4C, 0x8D, 0xFF),
        AccentHover: Color.FromRgb(0x6A, 0xA0, 0xFF),
        AccentForeground: Color.FromRgb(0x10, 0x14, 0x1A),
        PreviewBackground: Color.FromRgb(0x12, 0x14, 0x17));

    public static Brush WindowBackground { get; private set; } = Create(Light.WindowBackground);

    public static Brush Surface { get; private set; } = Create(Light.Surface);

    public static Brush SurfaceHover { get; private set; } = Create(Light.SurfaceHover);

    public static Brush Border { get; private set; } = Create(Light.Border);

    public static Brush TextPrimary { get; private set; } = Create(Light.TextPrimary);

    public static Brush TextSecondary { get; private set; } = Create(Light.TextSecondary);

    public static Brush TextMuted { get; private set; } = Create(Light.TextMuted);

    public static Brush Accent { get; private set; } = Create(Light.Accent);

    public static Brush AccentHover { get; private set; } = Create(Light.AccentHover);

    public static Brush AccentForeground { get; private set; } = Create(Light.AccentForeground);

    public static Brush PreviewBackground { get; private set; } = Create(Light.PreviewBackground);

    /// <summary>浮层底色（半透明黑），用于压在 3D 预览上的信息卡。</summary>
    public static Brush OverlayBackground { get; private set; } =
        Create(Color.FromArgb(0xD8, 0x1E, 0x21, 0x26));

    public static void Apply(SchematicPalette palette)
    {
        WindowBackground = Create(palette.WindowBackground);
        Surface = Create(palette.Surface);
        SurfaceHover = Create(palette.SurfaceHover);
        Border = Create(palette.Border);
        TextPrimary = Create(palette.TextPrimary);
        TextSecondary = Create(palette.TextSecondary);
        TextMuted = Create(palette.TextMuted);
        Accent = Create(palette.Accent);
        AccentHover = Create(palette.AccentHover);
        AccentForeground = Create(palette.AccentForeground);
        PreviewBackground = Create(palette.PreviewBackground);
    }

    public static Brush Create(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>一套配色。</summary>
public sealed record SchematicPalette(
    Color WindowBackground,
    Color Surface,
    Color SurfaceHover,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted,
    Color Accent,
    Color AccentHover,
    Color AccentForeground,
    Color PreviewBackground);
