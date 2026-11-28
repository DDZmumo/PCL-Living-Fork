using System.Windows.Media;

namespace SchematicKit;

/// <summary>
///     没有客户端贴图时的方块近似色（从启动器自带的地图配色移植）。
///     只按方块 ID 里的关键词粗略分色，够 3D 预览在缺少 JAR 时区分材质。
/// </summary>
public static class BlockColors
{
    private static readonly Color Default = Color.FromRgb(128, 128, 128);

    public static Color Get(string? blockName)
    {
        if (string.IsNullOrEmpty(blockName)) return Default;

        var block = blockName;
        if (block.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) block = block[10..];
        var cut = block.IndexOfAny(['[', '{']);
        if (cut >= 0) block = block[..cut];

        // 楼梯、台阶、栅栏等变体沿用基础材质的颜色
        var baseName = StripShapeSuffix(block);
        if (IsWood(baseName)) return Color.FromRgb(150, 110, 66);

        if (block.Contains("lava", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(220, 100, 20);
        if (block.Contains("water", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(44, 98, 190);
        if (block.Contains("grass", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("moss", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(92, 148, 68);
        if (block.Contains("leaves", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(60, 118, 48);
        if (block.Contains("sand", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(214, 199, 148);
        if (block.Contains("gravel", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(130, 124, 114);
        if (block.Contains("snow", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("ice", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("powder", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(232, 240, 245);
        if (block.Contains("stone", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("cobble", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("deepslate", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("andesite", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(120, 120, 120);
        if (block.Contains("dirt", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("mud", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(118, 84, 52);
        if (block.Contains("wood", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("plank", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("log", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(150, 110, 66);
        if (block.Contains("terracotta", StringComparison.OrdinalIgnoreCase) ||
            block.Contains("concrete", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(160, 120, 100);
        if (block.Contains("netherrack", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(110, 50, 50);
        if (block.Contains("end_stone", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(210, 208, 160);
        if (block.Contains("glass", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(170, 200, 210);
        if (block.Contains("wool", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(220, 220, 220);
        if (block.Contains("brick", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(150, 84, 70);
        if (block.Contains("quartz", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(232, 228, 220);
        if (block.Contains("copper", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(184, 116, 84);
        if (block.Contains("gold", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(240, 200, 60);
        if (block.Contains("iron", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(200, 200, 200);
        return Default;
    }

    private static readonly string[] ShapeSuffixes =
    [
        "_fence_gate", "_pressure_plate", "_trapdoor", "_stairs", "_slab", "_wall",
        "_fence", "_door", "_button", "_carpet", "_pane"
    ];

    private static readonly HashSet<string> Woods = new(StringComparer.OrdinalIgnoreCase)
    {
        "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "pale_oak",
        "bamboo", "crimson", "warped"
    };

    private static string StripShapeSuffix(string id)
    {
        foreach (var suffix in ShapeSuffixes)
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return id[..^suffix.Length];
        return id;
    }

    private static bool IsWood(string name)
    {
        if (Woods.Contains(name)) return true;
        return name.Contains("wood", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("plank", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("log", StringComparison.OrdinalIgnoreCase);
    }
}
