using System.IO;

namespace SchematicKit;

/// <summary>把投影文件复制进某个实例的 schematics 文件夹，供「导入投影」按钮使用。</summary>
public static class SchematicLibrary
{
    /// <summary>
    ///     复制文件到 <paramref name="instance" /> 的 schematics 文件夹；不会覆盖同名文件，
    ///     冲突时会自动加 <c> (1)</c> 后缀。返回实际写入的文件路径。
    /// </summary>
    public static IReadOnlyList<string> Import(SchematicInstance instance, IEnumerable<string> sourceFiles)
    {
        var folder = instance.SchematicFolder;
        Directory.CreateDirectory(folder);

        var written = new List<string>();
        foreach (var source in sourceFiles)
        {
            try
            {
                if (!File.Exists(source) || !SchematicFormats.IsSchematicFile(source)) continue;
                var target = UniquePath(folder, Path.GetFileName(source));
                File.Copy(source, target, false);
                written.Add(target);
            }
            catch (Exception ex)
            {
                SchematicOptions.Log(ex, $"导入投影失败（{source}）");
            }
        }

        return written;
    }

    private static string UniquePath(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        if (!File.Exists(target)) return target;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10_000; index++)
        {
            target = Path.Combine(folder, $"{name} ({index}){extension}");
            if (!File.Exists(target)) return target;
        }

        return Path.Combine(folder, $"{name} ({Guid.NewGuid():N}){extension}");
    }
}
