// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using SchematicKit;
using Instance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal static class LauncherSchematicApplication
{
    static LauncherSchematicApplication() => SchematicOptions.ErrorLog = (error, message) => Logger.Warn(error, message);

    internal static Instance[] Instances() => ModMinecraft.McInstanceList.Values.SelectMany(group => group)
        // A fresh scan hands out instances that are not loaded yet; they load on demand (PathIndie), so keep them all.
        .Where(instance => instance is not null && ModModpackMode.AllowsInstance(instance.PathVersion))
        .DistinctBy(instance => PathUtils.RemoveExtendedPrefix(instance.PathVersion), StringComparer.OrdinalIgnoreCase)
        .OrderBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static async Task<SchematicSession> PreviewAsync(Instance owner, InstanceResourceEntry entry, CancellationToken token)
    {
        string gameFolder = owner.PathIndie;
        string root = Path.Combine(gameFolder, "schematics");
        string? jar = await Task.Run(() => {
            if (entry.IsDirectory || !SchematicFormats.IsSchematicFile(entry.FullPath)) throw new InvalidDataException("请选择投影文件。");
            InstanceResourceStore.ValidateEntry(root, entry);
            try { return FindClientJar(owner); }
            catch (Exception error) {
                Logger.Warn(error, "无法读取投影预览所用的客户端贴图");
                return null;
            }
        }, token).ConfigureAwait(false);
        return await SchematicService.LoadAsync(entry.FullPath, jar, Path.Combine(gameFolder, "mods"), token).ConfigureAwait(false);
    }

    private static string? FindClientJar(Instance instance)
    {
        string? versions = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(instance.PathVersion));
        if (versions is null) return null;
        var visited = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Count < 32 && visited.Add(instance.PathVersion)) {
            string jar = Path.Combine(instance.PathVersion, instance.Name + ".jar");
            if (File.Exists(jar)) return jar;
            string? inherited = instance.InheritName;
            if (string.IsNullOrWhiteSpace(inherited) || inherited != Path.GetFileName(inherited) || inherited is "." or "..") break;
            instance = new Instance(Path.Combine(versions, inherited));
        }
        return null;
    }
}
