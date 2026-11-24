using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SchematicKit;

/// <summary>
///     客户端 JAR 的只读访问：方块状态、模型 JSON 与方块贴图。
///     所有条目按需读取并缓存，可以在后台线程使用；没有 JAR 时所有查询返回空，调用方退回近似形状。
/// </summary>
public sealed class SchematicAssets : IDisposable
{
    private const string TextureRoot = "assets/minecraft/textures/";

    private readonly Dictionary<string, JsonDocument?> _blockStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ZipArchive? _jar;

    private readonly Dictionary<string, JsonDocument?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZipArchiveEntry> _textures = new(StringComparer.Ordinal);

    public SchematicAssets(string? jarPath)
    {
        try
        {
            if (string.IsNullOrEmpty(jarPath) || !File.Exists(jarPath)) return;
            _jar = ZipFile.OpenRead(jarPath);
            IndexTextures(_jar);
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, $"读取客户端资源失败（{jarPath}）");
        }
    }

    /// <summary>可用贴图数量，0 表示没有可用的客户端资源。</summary>
    public int TextureCount => _textures.Count;

    public void Dispose() => _jar?.Dispose();

    /// <summary>按键名（如 <c>block/oak_planks</c>、<c>entity/chest/normal</c>）取贴图条目。</summary>
    public bool TryGetTextureEntry(string key, out ZipArchiveEntry entry) => _textures.TryGetValue(key, out entry!);

    /// <summary>读取方块状态 JSON，<paramref name="id" /> 已去掉命名空间。</summary>
    public JsonElement? GetBlockState(string id) =>
        GetJson(_blockStates, "assets/minecraft/blockstates/" + id + ".json");

    /// <summary>读取模型 JSON，<paramref name="path" /> 形如 <c>block/oak_stairs</c> 或 <c>minecraft:block/oak_stairs</c>。</summary>
    public JsonElement? GetModel(string path) =>
        GetJson(_models, "assets/minecraft/models/" + NormalizeModelPath(path) + ".json");

    private JsonElement? GetJson(Dictionary<string, JsonDocument?> cache, string entryPath)
    {
        if (cache.TryGetValue(entryPath, out var cached)) return cached?.RootElement;

        JsonDocument? document = null;
        try
        {
            var entry = _jar?.GetEntry(entryPath);
            if (entry is not null)
                using (var stream = entry.Open())
                {
                    document = JsonDocument.Parse(stream);
                }
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, $"解析资源失败（{entryPath}）");
        }

        cache[entryPath] = document;
        return document?.RootElement;
    }

    internal static string NormalizeModelPath(string path)
    {
        var value = path.Trim();
        if (value.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) value = value[10..];
        else
        {
            var colon = value.IndexOf(':');
            if (colon > 0) value = value[(colon + 1)..];
        }

        if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) value = value[..^5];
        if (value.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        return value;
    }

    /// <summary>模型里写的贴图引用（minecraft:block/oak_planks）转成贴图表里的键（block/oak_planks）。</summary>
    internal static string NormalizeTexturePath(string path)
    {
        var value = path.Trim();
        if (value.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) value = value[10..];
        else
        {
            var colon = value.IndexOf(':');
            if (colon > 0) value = value[(colon + 1)..];
        }

        if (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) value = value[9..];
        if (value.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase)) value = "block/" + value[7..];
        else if (!value.StartsWith("block/", StringComparison.OrdinalIgnoreCase) &&
                 !value.StartsWith("entity/", StringComparison.OrdinalIgnoreCase))
            value = "block/" + value;

        return value;
    }

    private void IndexTextures(ZipArchive jar)
    {
        foreach (var entry in jar.Entries)
        {
            var path = entry.FullName;
            if (!path.StartsWith(TextureRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

            var key = path[TextureRoot.Length..^4].Replace('\\', '/');
            // 旧版 blocks/ 目录与新版 block/ 等价，优先保留新版
            if (key.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase))
            {
                var modern = "block/" + key[7..];
                if (!_textures.ContainsKey(modern)) _textures[modern] = entry;
                continue;
            }

            _textures[key] = entry;
        }
    }
}
