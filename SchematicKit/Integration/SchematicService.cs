using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace SchematicKit;

public sealed class SchematicBlockCount(string blockId, string name, int count, TextureAtlas atlas)
{
    private BitmapSource? _icon;

    public string BlockId { get; } = blockId;
    public string Name { get; } = name;
    public int Count { get; } = count;
    internal BlockModel? Model { get; init; }
    public BitmapSource Icon => _icon ??= atlas.GetBlockStatisticsIcon(blockId, Model);
    /// <summary>该方块没有可用资源，3D 预览里不显示它。</summary>
    public bool IsMissing { get; } = atlas.IsMissing(blockId);
    /// <summary>缺资源是因为实例没装它的模组（而不是模组本身没给它模型，如伪装方块）。</summary>
    public bool IsModAbsent { get; } = atlas.IsMissing(blockId) && !atlas.HasMod(blockId);
    /// <summary>未显示的原因，显示在统计列表的提示里；有资源时为 null。</summary>
    public string? MissingReason => !IsMissing ? null : IsModAbsent
        ? "当前实例没有安装这个方块的模组，未在 3D 预览中显示"
        : "这个方块没有可显示的模型或贴图（如伪装类方块），未在 3D 预览中显示";
}

public sealed class SchematicSession(SchematicData data, SchematicMetadata metadata, TextureAtlas? textures, string info, SchematicBlockCount[] blockCounts) : IDisposable
{
    public SchematicData Data { get; } = data;
    public SchematicMetadata Metadata { get; } = metadata;
    public TextureAtlas? Textures { get; } = textures;
    public string Info { get; } = info;
    public IReadOnlyList<SchematicBlockCount> BlockCounts { get; } = blockCounts;
    public void Dispose() => Textures?.Dispose();
}

/// <summary>Read a selected local file once and prepare the data shared by metadata and 3D preview.</summary>
public static class SchematicService
{
    public static Task<SchematicSession> LoadAsync(string path, string? jarPath, CancellationToken token = default) =>
        LoadAsync(path, jarPath, null, token);

    public static Task<SchematicSession> LoadAsync(string path, string? jarPath, string? modsDirectory, CancellationToken token = default) =>
        Task.Run(() => {
            SchematicFormat format = SchematicFormats.GetFormat(path);
            if (format == SchematicFormat.Unknown) throw new InvalidDataException("不支持的投影文件格式。");
            var root = SchematicNbt.Read(path, token);
            var metadata = SchematicMetadata.Read(root, format);
            var data = SchematicParser.Read(root, format, token);
            TextureAtlas? textures = null;
            try {
                if (SchematicViewer3D.CanRender(data))
                    textures = TextureAtlas.Build(data.Blocks.Select(block => block.Name).Distinct(), jarPath, modsDirectory, token);
                SchematicBlockCount[] counts = textures is null ? [] : data.Blocks.GroupBy(block => block.Name, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count()).Select(group => {
                        token.ThrowIfCancellationRequested();
                        return new SchematicBlockCount(group.Key, textures.GetBlockName(group.Key, jarPath), group.Count(), textures) {
                            Model = textures.Models.Get(group.Key, group.First().Properties)
                        };
                    }).ToArray();
                token.ThrowIfCancellationRequested();
                return new SchematicSession(data, metadata, textures, BuildInfo(path, data, metadata), counts);
            } catch {
                textures?.Dispose();
                throw;
            }
        }, token);

    private static string BuildInfo(string path, SchematicData data, SchematicMetadata metadata)
    {
        var strings = SchematicStrings.Current;
        var file = new FileInfo(path);
        var dimensions = data.IsEmpty ? metadata.EnclosingSize : $"{data.Width} × {data.Height} × {data.Length}";
        var lines = new List<string> { metadata.BuildDetailText(path, file.Length) };
        if (metadata.EnclosingSize is null && dimensions is not null) lines.Add(strings.EnclosingDimensions + dimensions);
        if (metadata.TotalBlocks is null && !data.IsEmpty) lines.Add(strings.TotalBlocks + data.Blocks.Count.ToString("N0", CultureInfo.CurrentCulture));
        if (metadata.ModifiedTime is null) lines.Add(strings.ModifiedTime + file.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
        return (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
    }
}
