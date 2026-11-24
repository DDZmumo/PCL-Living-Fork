using System.IO;

namespace SchematicKit;

/// <summary>
///     一个 Minecraft 实例在投影功能里需要的全部信息。
///     宿主不需要把自己的实例类型塞进库里，构造好这几个路径即可。
/// </summary>
public sealed class SchematicInstance
{
    /// <summary>实例名，同时决定默认的 JAR 文件名。</summary>
    public required string Name { get; init; }

    /// <summary>版本目录（<c>.minecraft\versions\&lt;名称&gt;</c>），用于找客户端 JAR。</summary>
    public required string InstanceDirectory { get; init; }

    /// <summary>游戏目录：隔离实例等于版本目录，否则等于 <c>.minecraft</c>。schematics 文件夹在它下面。</summary>
    public required string GameDirectory { get; init; }

    /// <summary>客户端 JAR 路径；没有时贴图退回近似纯色。</summary>
    public string? JarPath { get; init; }

    /// <summary>实例图标路径，可为 null。</summary>
    public string? LogoPath { get; init; }

    /// <summary>投影文件夹：<c>&lt;GameDirectory&gt;\schematics</c>。</summary>
    public string SchematicFolder => Path.Combine(GameDirectory, "schematics");

    /// <summary>读取该实例 schematics 文件夹（含子文件夹）里的投影文件，失败时返回空列表。</summary>
    public IReadOnlyList<string> EnumerateSchematicFiles()
    {
        try
        {
            var folder = SchematicFolder;
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(SchematicFormats.IsSchematicFile)
                .ToList();
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, $"扫描投影文件夹失败（{SchematicFolder}）");
            return [];
        }
    }

    /// <summary>投影数量；实例选择列表用它排序。</summary>
    public int CountSchematics() => EnumerateSchematicFiles().Count;
}

/// <summary>实例列表来源。宿主用自己的实例管理器实现它，即可把投影页接进现有界面。</summary>
public interface ISchematicInstanceProvider
{
    /// <summary>当前可用的实例；可以每次调用都重新扫描。</summary>
    IReadOnlyList<SchematicInstance> GetInstances();

    /// <summary>启动器当前选中的实例名，用于投影页默认打开哪个实例；没有则为 null。</summary>
    string? SelectedInstanceName { get; }
}

/// <summary>
///     默认实现：扫描 <c>&lt;Minecraft 目录&gt;\versions</c> 下的所有文件夹。
///     版本目录里存在 mods / saves / options.txt 时按「版本隔离」处理，schematics 就在版本目录下。
/// </summary>
public sealed class FolderInstanceProvider : ISchematicInstanceProvider
{
    private readonly string _minecraftDirectory;

    public FolderInstanceProvider(string minecraftDirectory, string? selectedInstanceName = null)
    {
        _minecraftDirectory = minecraftDirectory;
        SelectedInstanceName = selectedInstanceName;
    }

    public string? SelectedInstanceName { get; set; }

    public IReadOnlyList<SchematicInstance> GetInstances()
    {
        var result = new List<SchematicInstance>();
        var versions = Path.Combine(_minecraftDirectory, "versions");
        if (!Directory.Exists(versions)) return result;

        foreach (var directory in Directory.GetDirectories(versions).OrderBy(Path.GetFileName,
                     StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name)) continue;
            var isolated = IsIsolated(directory);
            var jarPath = Path.Combine(directory, name + ".jar");
            result.Add(new SchematicInstance
            {
                Name = name,
                InstanceDirectory = directory,
                GameDirectory = isolated ? directory : _minecraftDirectory,
                JarPath = File.Exists(jarPath) ? jarPath : null,
                LogoPath = File.Exists(Path.Combine(directory, "PCL", "Logo.png"))
                    ? Path.Combine(directory, "PCL", "Logo.png")
                    : null
            });
        }

        return result;
    }

    private static bool IsIsolated(string versionDirectory)
    {
        try
        {
            if (File.Exists(Path.Combine(versionDirectory, "options.txt"))) return true;
            if (Directory.Exists(Path.Combine(versionDirectory, "saves"))) return true;
            // 版本目录里已经有 schematics，说明这个实例本来就把游戏数据放在版本目录
            if (Directory.Exists(Path.Combine(versionDirectory, "schematics"))) return true;
            var mods = Path.Combine(versionDirectory, "mods");
            return Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any();
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>把一组已经准备好的实例直接交给投影页，适合宿主自己管理实例列表的情况。</summary>
public sealed class StaticInstanceProvider : ISchematicInstanceProvider
{
    private readonly IReadOnlyList<SchematicInstance> _instances;

    public StaticInstanceProvider(IReadOnlyList<SchematicInstance> instances, string? selectedInstanceName = null)
    {
        _instances = instances;
        SelectedInstanceName = selectedInstanceName;
    }

    public string? SelectedInstanceName { get; set; }

    public IReadOnlyList<SchematicInstance> GetInstances() => _instances;
}
