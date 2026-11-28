using System.Globalization;
using System.IO;
using fNbt;

namespace SchematicKit;

/// <summary>
///     投影文件的元数据（尺寸、方块数、作者、时间等），只读 NBT 头，不解析方块。
///     实例资源列表可以直接用它显示详情，不必依赖启动器自己的 ModLocalComp。
/// </summary>
public sealed class SchematicMetadata
{
    public SchematicFormat Format { get; init; }

    /// <summary>Litematica 的原理图版本号，或 Sponge 的 Version。</summary>
    public int? Version { get; init; }

    /// <summary>Minecraft 数据版本（DataVersion）。</summary>
    public int? DataVersion { get; init; }

    public string? Name { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? Length { get; init; }

    public long? TotalBlocks { get; init; }

    public long? TotalVolume { get; init; }

    public int? RegionCount { get; init; }

    public DateTimeOffset? CreatedTime { get; init; }

    public DateTimeOffset? ModifiedTime { get; init; }

    /// <summary>形如 <c>9 × 5 × 9</c>；没有尺寸信息时为 null。</summary>
    public string? EnclosingSize =>
        Width is { } width && Height is { } height && Length is { } length
            ? $"{width} × {height} × {length}"
            : null;

    /// <summary>读取元数据；文件损坏时返回只带 Format 的空对象，不抛异常。</summary>
    internal static SchematicMetadata Read(NbtCompound root, SchematicFormat format)
    {
        var builder = new Builder { Format = format };
        try
        {
            builder.Read(root, format);
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, "读取投影元数据失败");
        }

        return builder.ToMetadata();
    }

    private sealed class Builder
    {
        public SchematicFormat Format;
        public int? Version;
        public int? DataVersion;
        public string? Name;
        public string? Author;
        public string? Description;
        public int? Width;
        public int? Height;
        public int? Length;
        public long? TotalBlocks;
        public long? TotalVolume;
        public int? RegionCount;
        public long? CreatedTicks;
        public long? ModifiedTicks;

        public void Read(NbtCompound root, SchematicFormat format)
        {
            switch (format)
            {
                case SchematicFormat.Litematic:
                    ReadLitematic(root);
                    break;
                case SchematicFormat.Sponge:
                    ReadSponge(root);
                    break;
                case SchematicFormat.Structure:
                    ReadStructure(root);
                    break;
                case SchematicFormat.Mcedit:
                    ReadMcedit(root);
                    break;
            }
        }

        private static long? Number(NbtCompound compound, string name)
        {
            NbtTag? tag = compound?[name];
            return tag switch
            {
                NbtByte value => value.Value,
                NbtShort value => value.Value,
                NbtInt value => value.Value,
                NbtLong value => value.Value,
                NbtFloat value => (long)value.Value,
                NbtDouble value => (long)value.Value,
                _ => null
            };
        }

        private void ReadLitematic(NbtCompound root)
        {
            Version = (int?)Number(root, "Version");

            var metadata = root.Get<NbtCompound>("Metadata");
            if (metadata is null) return;

            Name = Clean(metadata.Get<NbtString>("Name")?.Value, "Unnamed");
            Author = Clean(metadata.Get<NbtString>("Author")?.Value);
            Description = Clean(metadata.Get<NbtString>("Description")?.Value);
            CreatedTicks = metadata.Get<NbtLong>("TimeCreated")?.Value;
            ModifiedTicks = metadata.Get<NbtLong>("TimeModified")?.Value;

            var enclosing = metadata.Get<NbtCompound>("EnclosingSize");
            if (enclosing is not null)
            {
                Width = (int)Math.Abs(Number(enclosing, "x") ?? 0);
                Height = (int)Math.Abs(Number(enclosing, "y") ?? 0);
                Length = (int)Math.Abs(Number(enclosing, "z") ?? 0);
            }

            RegionCount = (int?)Number(metadata, "RegionCount");
            TotalBlocks = Number(metadata, "TotalBlocks");
            TotalVolume = Number(metadata, "TotalVolume");
        }

        private void ReadSponge(NbtCompound root)
        {
            root = root.Get<NbtCompound>("Schematic") ?? root;
            Version = (int?)Number(root, "Version");
            DataVersion = (int?)Number(root, "DataVersion");

            Width = (int?)Number(root, "Width");
            Height = (int?)Number(root, "Height");
            Length = (int?)Number(root, "Length");
            TotalVolume = (long)(Width ?? 0) * (Height ?? 0) * (Length ?? 0);
            TotalBlocks = TotalVolume;
            if (root.Get<NbtCompound>("Palette") is not null) RegionCount = 1;

            var metadata = root.Get<NbtCompound>("Metadata");
            if (metadata is null) return;
            Name = Clean(metadata.Get<NbtString>("Name")?.Value);
            Author = Clean(metadata.Get<NbtString>("Author")?.Value);
        }

        private void ReadStructure(NbtCompound root)
        {
            Author = Clean(root.Get<NbtString>("author")?.Value);
            DataVersion = (int?)Number(root, "DataVersion");
            Version = root.Get<NbtInt>("version")?.Value;

            var size = root.Get<NbtList>("size");
            if (size is not null && size.Count >= 3)
            {
                Length = size[0].IntValue;
                Height = size[1].IntValue;
                Width = size[2].IntValue;
                TotalVolume = (long)Length * Height * Width;
            }

            var blocks = root.Get<NbtList>("blocks");
            if (blocks is not null) TotalBlocks = blocks.Count(tag => tag.TagType == NbtTagType.Compound);
            if (root.Get<NbtList>("palette") is not null) RegionCount = 1;
        }

        private void ReadMcedit(NbtCompound root)
        {
            Width = (int?)Number(root, "Width");
            Height = (int?)Number(root, "Height");
            Length = (int?)Number(root, "Length");
            TotalVolume = (long)(Width ?? 0) * (Height ?? 0) * (Length ?? 0);
        }

        private static string? Clean(string? value, string? ignored = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (ignored is not null && string.Equals(value, ignored, StringComparison.Ordinal)) return null;
            return value.Trim();
        }

        public SchematicMetadata ToMetadata() => new()
        {
            Format = Format,
            Version = Version,
            DataVersion = DataVersion,
            Name = Name,
            Author = Author,
            Description = Description,
            Width = Width,
            Height = Height,
            Length = Length,
            TotalBlocks = TotalBlocks,
            TotalVolume = TotalVolume,
            RegionCount = RegionCount,
            CreatedTime = ToTime(CreatedTicks),
            ModifiedTime = ToTime(ModifiedTicks)
        };

        private static DateTimeOffset? ToTime(long? unixMilliseconds)
        {
            if (unixMilliseconds is not { } value || value <= 0) return null;
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>把元数据整理成一段可直接显示的多行文本。</summary>
    public string BuildDetailText(string? filePath = null, long? fileSize = null)
    {
        var strings = SchematicStrings.Current;
        var lines = new List<string>();
        if (filePath is not null)
            lines.Add(string.Format(CultureInfo.CurrentCulture, strings.FileInfo, Path.GetFileName(filePath),
                fileSize is { } size ? SchematicService.FormatSize(size) : "?"));
        if (Description is not null) lines.Add(strings.Description + Description);
        if (Author is not null) lines.Add(string.Format(CultureInfo.CurrentCulture, strings.Author, Author));

        lines.Add(strings.DetailInfo);
        if (Name is not null) lines.Add(strings.OriginalName + Name);
        if (Version is not null) lines.Add(strings.Version + Version.Value);
        if (DataVersion is not null) lines.Add(strings.DataVersion + DataVersion.Value);
        if (EnclosingSize is not null) lines.Add(strings.EnclosingDimensions + EnclosingSize);
        if (TotalBlocks is { } blocks) lines.Add(strings.TotalBlocks + blocks.ToString("N0", CultureInfo.CurrentCulture));
        if (TotalVolume is { } volume) lines.Add(strings.TotalVolume + volume.ToString("N0", CultureInfo.CurrentCulture));
        if (RegionCount is { } regions) lines.Add(strings.RegionCount + regions);
        if (CreatedTime is { } created) lines.Add(strings.CreatedTime + created.ToString("g", CultureInfo.CurrentCulture));
        if (ModifiedTime is { } modified) lines.Add(strings.ModifiedTime + modified.ToString("g", CultureInfo.CurrentCulture));
        lines.Add(strings.FileType + Format switch
        {
            SchematicFormat.Litematic => strings.FileTypeLitematic,
            SchematicFormat.Sponge => strings.FileTypeSponge,
            SchematicFormat.Structure => strings.FileTypeNbt,
            SchematicFormat.Mcedit => strings.FileTypeMcedit,
            _ => strings.FileTypeUnknown
        });
        return string.Join(Environment.NewLine, lines);
    }
}
