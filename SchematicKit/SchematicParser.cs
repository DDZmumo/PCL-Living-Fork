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
    internal static SchematicData Read(NbtCompound root, SchematicFormat format, CancellationToken token)
    {
        var data = new SchematicData();
        try
        {
            switch (format)
            {
                case SchematicFormat.Litematic:
                    ReadLitematic(root, data, token);
                    break;
                case SchematicFormat.Sponge:
                    ReadSchem(root, data, token);
                    break;
                case SchematicFormat.Structure:
                    ReadStructure(root, data, token);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            data.Blocks.Clear();
            data.Error = ex.Message;
            SchematicOptions.Log(ex, "解析投影方块失败");
        }

        return data;
    }

    private static void ReadStructure(NbtCompound root, SchematicData data, CancellationToken token)
    {

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
            token.ThrowIfCancellationRequested();
            if (tag is not NbtCompound block) continue;
            var pos = block.Get<NbtList>("pos");
            var state = block.Get<NbtInt>("state");
            if (pos is null || pos.Count < 3 || state is null) continue;

            var x = pos[0].IntValue;
            var y = pos[1].IntValue;
            var z = pos[2].IntValue;
            var stateIndex = state.Value;
            if (stateIndex < 0 || stateIndex >= states.Length) continue;

            AddBlock(data, x, y, z, states[stateIndex]);
        }
    }

    private static void ReadSchem(NbtCompound root, SchematicData data, CancellationToken token)
    {
        root = root.Get<NbtCompound>("Schematic") ?? root;
        data.Width = root.Get<NbtShort>("Width")?.Value ?? 0;
        data.Height = root.Get<NbtShort>("Height")?.Value ?? 0;
        data.Length = root.Get<NbtShort>("Length")?.Value ?? 0;
        int volume = Volume(data.Width, data.Height, data.Length);
        var blocks = root.Get<NbtCompound>("Blocks");
        var palette = (blocks ?? root).Get<NbtCompound>("Palette");
        var bytes = (blocks?.Get<NbtByteArray>("Data") ?? root.Get<NbtByteArray>("BlockData"))?.Value;
        if (palette is null || bytes is null) throw new InvalidDataException("投影文件缺少 Sponge 方块数据。");

        var states = new Dictionary<int, (string Name, string? Properties)>();
        foreach (var tag in palette)
            if (tag is NbtInt index && index.Value >= 0) states.Add(index.Value, SplitState(tag.Name ?? ""));
        int offset = 0;
        for (int index = 0; index < volume; index++) {
            token.ThrowIfCancellationRequested();
            if (!states.TryGetValue(ReadVarInt(bytes, ref offset), out var state))
                throw new InvalidDataException("Sponge 方块状态索引无效。");
            AddBlock(data, index % data.Width, index / data.Width / data.Length, index / data.Width % data.Length, state);
        }
    }

    private static void ReadLitematic(NbtCompound root, SchematicData data, CancellationToken token)
    {

        var regions = root.Get<NbtCompound>("Regions");
        if (regions is null) return;

        long totalVolume = 0;
        foreach (var regionTag in regions)
        {
            token.ThrowIfCancellationRequested();
            if (regionTag is not NbtCompound region) continue;
            var sizeTag = region.Get<NbtCompound>("Size");
            var paletteTag = region.Get<NbtList>("BlockStatePalette");
            var statesTag = region.Get<NbtLongArray>("BlockStates");
            if (sizeTag is null || paletteTag is null || statesTag is null) continue;

            // Litematica 的 Size 可能带符号（部分版本用负数表示包围盒方向），取绝对值
            var signedX = sizeTag.Get<NbtInt>("x")?.Value ?? 0;
            var signedY = sizeTag.Get<NbtInt>("y")?.Value ?? 0;
            var signedZ = sizeTag.Get<NbtInt>("z")?.Value ?? 0;
            var sizeX = Math.Abs(signedX);
            var sizeY = Math.Abs(signedY);
            var sizeZ = Math.Abs(signedZ);
            if (sizeX == 0 || sizeY == 0 || sizeZ == 0) continue;

            // 多区域原理图用 Position 作为局部坐标原点，叠加后各区域才能对齐
            var positionTag = region.Get<NbtCompound>("Position");
            var posX = positionTag?.Get<NbtInt>("x")?.Value ?? 0;
            var posY = positionTag?.Get<NbtInt>("y")?.Value ?? 0;
            var posZ = positionTag?.Get<NbtInt>("z")?.Value ?? 0;
            if (signedX < 0) posX = checked(posX + signedX + 1);
            if (signedY < 0) posY = checked(posY + signedY + 1);
            if (signedZ < 0) posZ = checked(posZ + signedZ + 1);
            int volume = Volume(sizeX, sizeY, sizeZ);
            totalVolume += volume;
            if (totalVolume > MaxVolume) throw new InvalidDataException("投影区域总体积超过预览上限。");

            var states = new (string Name, string? Properties)[paletteTag.Count];
            for (var i = 0; i < paletteTag.Count; i++)
                states[i] = ReadPaletteEntry(paletteTag[i]);

            var bits = Math.Max(2, (int)Math.Ceiling(Math.Log2(Math.Max(1, paletteTag.Count))));
            var values = statesTag.Value;
            if (values.LongLength * 64 < (long)volume * bits) throw new InvalidDataException("投影方块状态数据不完整。");

            for (var y = 0; y < sizeY; y++)
            for (var z = 0; z < sizeZ; z++)
            for (var x = 0; x < sizeX; x++)
            {
                var blockIndex = (y * sizeZ + z) * sizeX + x;
                token.ThrowIfCancellationRequested();
                var stateIndex = ReadLongBits(values, blockIndex * bits, bits);
                if (stateIndex < 0 || stateIndex >= states.Length) continue;

                AddBlock(data, checked(posX + x), checked(posY + y), checked(posZ + z), states[stateIndex]);
            }
        }
    }

    private const int MaxVolume = 8_000_000;

    private static int Volume(int x, int y, int z)
    {
        if (x <= 0 || y <= 0 || z <= 0 || (long)x * y > MaxVolume / z)
            throw new InvalidDataException("投影尺寸超过预览上限。");
        return x * y * z;
    }

    private static void AddBlock(SchematicData data, int x, int y, int z, (string Name, string? Properties) state)
    {
        if (string.IsNullOrEmpty(state.Name) || !IsSolid(state.Name)) return;
        if (x is < -1_000_000 or > 1_000_000 || y is < -1_000_000 or > 1_000_000 || z is < -1_000_000 or > 1_000_000 ||
            data.Blocks.Count >= 500_000) throw new InvalidDataException("方块数量或坐标超过预览上限。");
        data.Blocks.Add(new SchematicBlock(x, y, z, state.Name, state.Properties));
    }

    private static int ReadVarInt(byte[] data, ref int offset)
    {
        int value = 0;
        for (int shift = 0; shift <= 28 && offset < data.Length; shift += 7) {
            int next = data[offset++];
            if (shift == 28 && next > 7) break;
            value |= (next & 0x7f) << shift;
            if (next < 128) return value;
        }
        throw new InvalidDataException("Sponge 方块数据不完整。");
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
