using System.IO;
using fNbt;

namespace SchematicKit;

/// <summary>投影里的单个方块。Properties 保存方块状态（如 <c>facing=east,type=bottom</c>），用于还原楼梯、台阶等形状。</summary>
public readonly record struct SchematicBlock(int X, int Y, int Z, string Name, string? Properties = null);

/// <summary>解析结果：包围盒尺寸与方块列表。解析失败时 <see cref="Error" /> 里是给用户看的原因。</summary>
public sealed class SchematicData
{
    public int Width;
    public int Height;
    public int Length;
    public List<SchematicBlock> Blocks { get; } = [];
    public string? Error;

    public bool IsEmpty => Blocks.Count == 0;

    /// <summary>方块在三个轴上的实际包围范围；没有方块时返回 false。</summary>
    public bool TryGetBounds(out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
    {
        if (Blocks.Count == 0)
        {
            minX = minY = minZ = maxX = maxY = maxZ = 0;
            return false;
        }

        minX = minY = minZ = int.MaxValue;
        maxX = maxY = maxZ = int.MinValue;
        foreach (var block in Blocks)
        {
            minX = Math.Min(minX, block.X);
            minY = Math.Min(minY, block.Y);
            minZ = Math.Min(minZ, block.Z);
            maxX = Math.Max(maxX, block.X);
            maxY = Math.Max(maxY, block.Y);
            maxZ = Math.Max(maxZ, block.Z);
        }

        return true;
    }
}

/// <summary>投影文件格式。</summary>
public enum SchematicFormat
{
    Unknown,
    Litematic,
    Sponge,
    Structure,
    Mcedit
}

/// <summary>支持的文件扩展名与格式判断。</summary>
public static class SchematicFormats
{
    public static readonly string[] Extensions = [".litematic", ".schem", ".nbt", ".schematic"];

    public static bool IsSchematicFile(string path) => GetFormat(path) != SchematicFormat.Unknown;

    public static SchematicFormat GetFormat(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".litematic" => SchematicFormat.Litematic,
            ".schem" => SchematicFormat.Sponge,
            ".nbt" => SchematicFormat.Structure,
            ".schematic" => SchematicFormat.Mcedit,
            _ => SchematicFormat.Unknown
        };
    }
}

/// <summary>
///     只读的投影解析器：把 .litematic / .schem / .nbt 转换成方块列表，不修改原文件。
///     旧版 .schematic（MCEdit）与 Sponge v2/v3 的 varint 编码暂不支持，会通过
///     <see cref="SchematicData.Error" /> 说明原因。
/// </summary>
public static class SchematicParser
{
    /// <summary>读取文件并提取方块列表；任何异常都会转成 <see cref="SchematicData.Error" />，不向外抛。</summary>
    public static SchematicData Read(string path)
    {
        var data = new SchematicData();
        try
        {
            switch (SchematicFormats.GetFormat(path))
            {
                case SchematicFormat.Litematic:
                    ReadLitematic(path, data);
                    break;
                case SchematicFormat.Sponge:
                    ReadSchem(path, data);
                    break;
                case SchematicFormat.Structure:
                    ReadStructure(path, data);
                    break;
                case SchematicFormat.Mcedit:
                    data.Error = "MCEdit/WorldEdit 旧版 schematic 暂不支持预览";
                    break;
                default:
                    data.Error = "不支持的原理图格式";
                    break;
            }

            // 原实现直接使用文件头里写的尺寸，多区域 litematic 会被最后一个区域覆盖。
            // 这里统一按实际方块重新算包围盒，尺寸与 3D 预览的取景才会一致。
            if (data.TryGetBounds(out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ))
            {
                data.Width = maxX - minX + 1;
                data.Height = maxY - minY + 1;
                data.Length = maxZ - minZ + 1;
            }
        }
        catch (Exception ex)
        {
            data.Error = ex.Message;
            SchematicOptions.Log(ex, $"读取投影失败（{path}）");
        }

        return data;
    }

    private static NbtCompound? LoadRoot(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var nbt = new NbtFile();
        nbt.LoadFromStream(stream, NbtCompression.AutoDetect);
        return nbt.RootTag as NbtCompound;
    }

    private static void ReadStructure(string path, SchematicData data)
    {
        var root = LoadRoot(path);
        if (root is null) return;

        var size = root.Get<NbtList>("size");
        if (size is null || size.Count < 3) return;
        data.Length = size[0].IntValue;
        data.Height = size[1].IntValue;
        data.Width = size[2].IntValue;

        var palette = root.Get<NbtList>("palette");
        var blocks = root.Get<NbtList>("blocks");
        if (palette is null || blocks is null) return;

        var states = new (string Name, string? Properties)[palette.Count];
        for (var i = 0; i < palette.Count; i++)
            states[i] = ReadPaletteEntry(palette[i]);

        foreach (var tag in blocks)
        {
            if (tag is not NbtCompound block) continue;
            var pos = block.Get<NbtList>("pos");
            var state = block.Get<NbtInt>("state");
            if (pos is null || pos.Count < 3 || state is null) continue;

            var x = pos[0].IntValue;
            var y = pos[1].IntValue;
            var z = pos[2].IntValue;
            var stateIndex = state.Value;
            if (stateIndex < 0 || stateIndex >= states.Length) continue;

            if (IsSolid(states[stateIndex].Name))
                data.Blocks.Add(new SchematicBlock(x, y, z, states[stateIndex].Name, states[stateIndex].Properties));
        }
    }

    private static void ReadSchem(string path, SchematicData data)
    {
        var root = LoadRoot(path);
        if (root is null) return;

        var widthTag = root.Get<NbtShort>("Width");
        var heightTag = root.Get<NbtShort>("Height");
        var lengthTag = root.Get<NbtShort>("Length");
        if (widthTag is null || heightTag is null || lengthTag is null) return;

        data.Width = widthTag.Value;
        data.Height = heightTag.Value;
        data.Length = lengthTag.Value;

        var palette = root.Get<NbtCompound>("Palette");
        var blockData = root.Get<NbtByteArray>("BlockData");
        if (palette is null || blockData is null)
        {
            data.Error = "Sponge v1 schematic 暂不支持预览";
            return;
        }

        // 索引 -> 方块名与方块状态；Sponge 的调色板键名形如 minecraft:oak_stairs[facing=east,type=bottom]
        var paletteCount = palette.Count;
        var states = new (string Name, string? Properties)[paletteCount];
        foreach (var tag in palette)
            if (tag is NbtInt index && index.Value >= 0 && index.Value < paletteCount)
                states[index.Value] = SplitState(tag.Name ?? "");

        var bits = Math.Max(2, (int)Math.Ceiling(Math.Log2(Math.Max(1, paletteCount))));
        var bytes = blockData.Value;

        for (var y = 0; y < data.Height; y++)
        for (var z = 0; z < data.Length; z++)
        for (var x = 0; x < data.Width; x++)
        {
            var blockIndex = (y * data.Length + z) * data.Width + x;
            var stateIndex = ReadBits(bytes, blockIndex * bits, bits);
            if (stateIndex < 0 || stateIndex >= paletteCount) continue;

            var state = states[stateIndex];
            if (string.IsNullOrEmpty(state.Name)) continue;
            if (!IsSolid(state.Name)) continue;

            data.Blocks.Add(new SchematicBlock(x, y, z, state.Name, state.Properties));
        }
    }

    private static void ReadLitematic(string path, SchematicData data)
    {
        var root = LoadRoot(path);
        if (root is null) return;

        var regions = root.Get<NbtCompound>("Regions");
        if (regions is null) return;

        foreach (var regionTag in regions)
        {
            if (regionTag is not NbtCompound region) continue;
            var sizeTag = region.Get<NbtCompound>("Size");
            var paletteTag = region.Get<NbtList>("BlockStatePalette");
            var statesTag = region.Get<NbtLongArray>("BlockStates");
            if (sizeTag is null || paletteTag is null || statesTag is null) continue;

            // Litematica 的 Size 可能带符号（部分版本用负数表示包围盒方向），取绝对值
            var sizeX = Math.Abs(sizeTag.Get<NbtInt>("x")?.Value ?? 0);
            var sizeY = Math.Abs(sizeTag.Get<NbtInt>("y")?.Value ?? 0);
            var sizeZ = Math.Abs(sizeTag.Get<NbtInt>("z")?.Value ?? 0);
            if (sizeX == 0 || sizeY == 0 || sizeZ == 0) continue;

            // 多区域原理图用 Position 作为局部坐标原点，叠加后各区域才能对齐
            var positionTag = region.Get<NbtCompound>("Position");
            var posX = positionTag?.Get<NbtInt>("x")?.Value ?? 0;
            var posY = positionTag?.Get<NbtInt>("y")?.Value ?? 0;
            var posZ = positionTag?.Get<NbtInt>("z")?.Value ?? 0;

            var states = new (string Name, string? Properties)[paletteTag.Count];
            for (var i = 0; i < paletteTag.Count; i++)
                states[i] = ReadPaletteEntry(paletteTag[i]);

            var bits = Math.Max(2, (int)Math.Ceiling(Math.Log2(Math.Max(1, paletteTag.Count))));
            var values = statesTag.Value;

            for (var y = 0; y < sizeY; y++)
            for (var z = 0; z < sizeZ; z++)
            for (var x = 0; x < sizeX; x++)
            {
                var blockIndex = (y * sizeZ + z) * sizeX + x;
                var stateIndex = ReadLongBits(values, blockIndex * bits, bits);
                if (stateIndex < 0 || stateIndex >= states.Length) continue;

                var state = states[stateIndex];
                if (!IsSolid(state.Name)) continue;

                data.Blocks.Add(new SchematicBlock(posX + x, posY + y, posZ + z, state.Name, state.Properties));
            }
        }
    }

    private static int ReadBits(byte[] data, int bitIndex, int bitCount)
    {
        if (bitCount <= 0 || bitCount > 32) return 0;
        var result = 0;
        for (var i = 0; i < bitCount; i++)
        {
            var globalBit = bitIndex + i;
            var byteIndex = globalBit >> 3;
            if (byteIndex >= data.Length) return 0;
            var bit = (data[byteIndex] >> (globalBit & 7)) & 1;
            result |= bit << i;
        }

        return result;
    }

    private static int ReadLongBits(long[] data, int bitIndex, int bitCount)
    {
        if (bitCount <= 0 || bitCount > 32) return 0;
        var result = 0UL;
        for (var i = 0; i < bitCount; i++)
        {
            var globalBit = bitIndex + i;
            var longIndex = globalBit >> 6;
            if (longIndex >= data.Length) return 0;
            var bit = (int)((ulong)data[longIndex] >> (globalBit & 63)) & 1;
            result |= (ulong)bit << i;
        }

        return (int)result;
    }

    private static (string Name, string? Properties) ReadPaletteEntry(NbtTag? tag)
    {
        if (tag is not NbtCompound entry) return ("minecraft:stone", null);
        var name = entry.Get<NbtString>("Name")?.Value ?? "minecraft:stone";
        var properties = entry.Get<NbtCompound>("Properties");
        if (properties is null) return (name, null);

        var parts = new List<string>();
        foreach (var child in properties)
            if (child is NbtString value)
                parts.Add(child.Name + "=" + value.Value);
        return (name, parts.Count == 0 ? null : string.Join(",", parts));
    }

    /// <summary>把 Sponge 的调色板键名拆成方块名与状态串。</summary>
    private static (string Name, string? Properties) SplitState(string raw)
    {
        var open = raw.IndexOf('[');
        if (open < 0 || !raw.EndsWith("]", StringComparison.Ordinal)) return (raw, null);
        return (raw[..open], raw[(open + 1)..^1]);
    }

    private static bool IsSolid(string name)
    {
        var id = name;
        var cut = id.IndexOfAny(['[', '{']);
        if (cut >= 0) id = id[..cut];
        id = id.Trim().ToLowerInvariant();
        if (id.StartsWith("minecraft:", StringComparison.Ordinal)) id = id[10..];
        // 只排除三种空气，不能用包含判断：oak_stairs 这类名字里也含有 "air"
        return id is not ("air" or "cave_air" or "void_air" or "structure_void");
    }
}
