using System.Globalization;
using System.IO;

namespace SchematicKit;

/// <summary>投影列表里的一行。</summary>
/// <param name="Path">完整路径。</param>
/// <param name="RelativePath">相对 schematics 文件夹的路径，用于显示子文件夹。</param>
/// <param name="Size">文件大小（字节）。</param>
/// <param name="Modified">最后修改时间。</param>
public sealed record SchematicFileEntry(string Path, string RelativePath, long Size, DateTime Modified)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>列表排序方式。</summary>
public enum SchematicSortOrder
{
    ModifiedTime,
    FileName
}

/// <summary>一次预览会话：解析结果 + 贴图集 + 详情文本。用完必须 Dispose（贴图集持有 JAR 句柄）。</summary>
public sealed class SchematicSession : IDisposable
{
    internal SchematicSession(string path, SchematicData data, SchematicMetadata metadata, TextureAtlas? textures,
        string info)
    {
        Path = path;
        Data = data;
        Metadata = metadata;
        Textures = textures;
        Info = info;
    }

    public string Path { get; }

    public SchematicData Data { get; }

    public SchematicMetadata Metadata { get; }

    /// <summary>贴图集；不满足 3D 预览条件时为 null。</summary>
    public TextureAtlas? Textures { get; }

    /// <summary>多行详情文本，直接放进预览的「详情」浮层。</summary>
    public string Info { get; }

    public bool CanRender3D => Textures is not null;

    public void Dispose() => Textures?.Dispose();
}

/// <summary>
///     投影功能的业务入口：实例列表、文件扫描、后台解析与贴图提取。
///     所有重活都在后台线程完成，返回的对象可以直接交给 UI。
/// </summary>
public sealed class SchematicService
{
    private readonly ISchematicInstanceProvider _provider;

    public SchematicService(ISchematicInstanceProvider provider)
    {
        _provider = provider;
        Reload();
    }

    public IReadOnlyList<SchematicInstance> Instances { get; private set; } = [];

    public SchematicInstance? Current { get; private set; }

    public event EventHandler? Changed;

    /// <summary>重新读取实例列表，并保持当前实例（或退回默认实例）。</summary>
    public void Reload()
    {
        Instances = _provider.GetInstances();
        var previous = Current?.Name;
        var target = Instances.FirstOrDefault(item => item.Name == previous)
                     ?? FindPreferred(_provider.SelectedInstanceName)
                     ?? Instances.FirstOrDefault();
        Current = target;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>选中实例；为 null 时按「有投影的优先」重新挑一个。</summary>
    public void Select(SchematicInstance? instance)
    {
        Current = instance ?? FindPreferred(_provider.SelectedInstanceName) ?? Instances.FirstOrDefault();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>优先当前实例；当前实例没有投影时退回第一个有投影的实例。</summary>
    public SchematicInstance? FindPreferred(string? preferredName = null)
    {
        var target = preferredName ?? _provider.SelectedInstanceName;
        return Instances.FirstOrDefault(item => item.Name == target && item.CountSchematics() > 0)
               ?? Instances.FirstOrDefault(item => item.CountSchematics() > 0)
               ?? Instances.FirstOrDefault(item => item.Name == target);
    }

    /// <summary>扫描当前实例的投影文件。</summary>
    public IReadOnlyList<SchematicFileEntry> Scan(SchematicSortOrder order = SchematicSortOrder.ModifiedTime,
        string? search = null)
    {
        if (Current is null) return [];
        return Scan(Current, order, search);
    }

    /// <summary>扫描指定实例的投影文件，支持按文件名过滤。</summary>
    public IReadOnlyList<SchematicFileEntry> Scan(SchematicInstance instance, SchematicSortOrder order,
        string? search = null)
    {
        try
        {
            var folder = instance.SchematicFolder;
            if (!Directory.Exists(folder)) return [];

            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(SchematicFormats.IsSchematicFile);
            if (!string.IsNullOrWhiteSpace(search))
                files = files.Where(path => Path.GetFileName(path)
                    .Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

            var ordered = order == SchematicSortOrder.ModifiedTime
                ? files.OrderByDescending(File.GetLastWriteTime)
                : files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

            return ordered.Select(path =>
            {
                var info = new FileInfo(path);
                var relative = path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    ? path[folder.Length..].TrimStart('\\', '/')
                    : info.Name;
                return new SchematicFileEntry(path, relative, info.Length, info.LastWriteTime);
            }).ToList();
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, "扫描投影文件夹失败");
            return [];
        }
    }

    /// <summary>在后台解析文件并提取贴图。调用方负责 Dispose 返回的会话。</summary>
    public Task<SchematicSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var jarPath = ResolveJarPath(path);
        return LoadAsync(path, jarPath, cancellationToken);
    }

    public Task<SchematicSession> LoadAsync(SchematicFileEntry entry, CancellationToken cancellationToken = default) =>
        LoadAsync(entry.Path, cancellationToken);

    /// <summary>指定客户端 JAR 的版本，宿主已经知道实例时用这个重载更准确。</summary>
    public async Task<SchematicSession> LoadAsync(string path, string? jarPath,
        CancellationToken cancellationToken = default)
    {
        var data = await Task.Run(() => SchematicParser.Read(path), cancellationToken).ConfigureAwait(false);
        var metadata = await Task.Run(() => SchematicMetadata.Read(path), cancellationToken).ConfigureAwait(false);
        TextureAtlas? textures = null;
        if (SchematicViewer3D.CanRender(data))
            textures = await Task.Run(() => TextureAtlas.Build(
                data.Blocks.Select(block => block.Name).Distinct(), jarPath), cancellationToken).ConfigureAwait(false);

        var info = SchematicInfo.BuildPreviewInfo(path, data);
        return new SchematicSession(path, data, metadata, textures, info);
    }

    /// <summary>按当前实例推断 JAR；路径不在当前实例下时按目录结构猜。</summary>
    public string? ResolveJarPath(string schematicPath)
    {
        if (Current is not null)
        {
            var folder = Current.SchematicFolder;
            if (schematicPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return Current.JarPath;
        }

        return SchematicInfo.GuessJarPath(schematicPath);
    }

    public SchematicMetadata ReadMetadata(string path) => SchematicMetadata.Read(path);
}

/// <summary>显示用的格式化与详情文本。</summary>
public static class SchematicInfo
{
    /// <summary>字节数转 <c>12.3 KB</c> 这样的文本。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            value /= 1024d;
            if (value < 1024d || unit == units[^1])
                return value.ToString("0.#", CultureInfo.CurrentCulture) + " " + unit;
        }

        return bytes + " B";
    }

    /// <summary>预览「详情」浮层的内容：文件、尺寸、方块数、修改时间。</summary>
    public static string BuildPreviewInfo(string path, SchematicData data)
    {
        var strings = SchematicStrings.Current;
        var file = new FileInfo(path);
        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, strings.FileInfo, file.Name, FormatSize(file.Length))
        };

        var width = data.Width;
        var height = data.Height;
        var length = data.Length;
        if (data.TryGetBounds(out _, out _, out _, out _, out _, out _))
        {
            var bounds = data.Blocks;
            var minX = bounds.Min(block => block.X);
            var minY = bounds.Min(block => block.Y);
            var minZ = bounds.Min(block => block.Z);
            width = bounds.Max(block => block.X) - minX + 1;
            height = bounds.Max(block => block.Y) - minY + 1;
            length = bounds.Max(block => block.Z) - minZ + 1;
        }

        lines.Add(strings.EnclosingDimensions + $"{width} × {height} × {length}");
        lines.Add(strings.TotalBlocks + data.Blocks.Count.ToString("N0", CultureInfo.CurrentCulture));
        lines.Add(strings.ModifiedTime + file.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    ///     从 <c>...\versions\&lt;名称&gt;\schematics\...</c> 反推出 <c>&lt;名称&gt;.jar</c>。
    ///     宿主一般直接传 <see cref="SchematicInstance.JarPath" />，这里只作兜底。
    /// </summary>
    public static string? GuessJarPath(string schematicPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(schematicPath));
            while (!string.IsNullOrEmpty(directory))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is not null &&
                    string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(directory);
                    var jar = Path.Combine(directory, name + ".jar");
                    return File.Exists(jar) ? jar : null;
                }

                directory = parent;
            }
        }
        catch
        {
            // 路径异常时静默退回无 JAR
        }

        return null;
    }
}
