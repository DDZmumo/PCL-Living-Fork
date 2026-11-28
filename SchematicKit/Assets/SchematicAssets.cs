using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SchematicKit;

/// <summary>
///     原版、模组 JAR 和 KubeJS 资源目录的只读资源索引。资源按命名空间查找，JSON 和贴图在使用时读取。
///     KubeJS 的 <c>kubejs/assets</c> 在游戏里相当于优先级最高的资源包：模型、方块状态和贴图优先取它的，
///     语言文件则与模组的合并（先查 KubeJS，再查模组）。
/// </summary>
public sealed class SchematicAssets : IDisposable
{
    private const int MaxIndexedResources = 200_000;
    private const int MaxPackFileBytes = 8 * 1024 * 1024;
    private readonly List<ZipArchive> _archives = [];
    private readonly Dictionary<string, JsonDocument?> _json = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    // KubeJS 资源：先只记文件路径，用到时才读入（见 TryGetPackEntry）。
    private readonly Dictionary<string, string> _packFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZipArchiveEntry?> _packEntries = new(StringComparer.OrdinalIgnoreCase);
    // Every namespace some loaded resource belongs to: a block whose namespace is absent comes from a mod this instance lacks.
    private readonly HashSet<string> _namespaces = new(StringComparer.OrdinalIgnoreCase) { "minecraft" };

    /// <summary>原版、模组或 KubeJS 资源里有没有这个命名空间（没有通常说明实例没装对应模组）。</summary>
    internal bool HasNamespace(string scope) => _namespaces.Contains(scope);

    public SchematicAssets(string? jarPath) : this(jarPath, null, default) { }

    internal SchematicAssets(string? jarPath, string? modsDirectory, CancellationToken token)
    {
        try {
            if (!string.IsNullOrEmpty(jarPath) && File.Exists(jarPath)) IndexArchive(jarPath, token);
            if (string.IsNullOrEmpty(modsDirectory)) return;
            IndexKubeJs(modsDirectory, token);
            if (!Directory.Exists(modsDirectory)) return;
            string[] mods;
            try {
                mods = Directory.EnumerateFiles(modsDirectory, "*", new EnumerationOptions {
                    IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                }).Where(path => Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(2048).ToArray();
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                SchematicOptions.Log(error, "读取实例的模组资源列表失败");
                return;
            }
            foreach (var mod in mods) {
                token.ThrowIfCancellationRequested();
                if (_entries.Count >= MaxIndexedResources) break;
                IndexArchive(mod, token);
            }
        } catch {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var document in _json.Values) document?.Dispose();
        foreach (var archive in _archives) archive.Dispose();
    }

    /// <summary>按键名（如 <c>block/oak_planks</c>、<c>entity/chest/normal</c>）取贴图条目。</summary>
    public bool TryGetTextureEntry(string key, out ZipArchiveEntry entry)
    {
        var (scope, path) = SplitId(key);
        if (TryGetEntry($"assets/{scope}/textures/{path}.png", out entry)) return true;
        return path.StartsWith("block/", StringComparison.Ordinal) &&
            TryGetEntry($"assets/{scope}/textures/blocks/{path[6..]}.png", out entry);
    }

    public JsonElement? GetBlockState(string id) => GetJsonForId("blockstates", NormalizeBlockId(id));

    /// <summary>读取模型 JSON，<paramref name="path" /> 形如 <c>block/oak_stairs</c> 或 <c>minecraft:block/oak_stairs</c>。</summary>
    public JsonElement? GetModel(string path) => GetJsonForId("models", NormalizeModelPath(path));

    internal string? GetModBlockName(string scope, string blockPath)
    {
        string key = $"block.{scope}.{blockPath.Replace('/', '.')}";
        var language = SchematicOptions.EffectiveBlockLanguage;
        return Translate(language) ?? (language == "en_us" ? null : Translate("en_us"));

        // Language files merge like in game: a KubeJS entry wins, the mod's own file fills in the rest.
        string? Translate(string locale)
        {
            string path = $"assets/{scope}/lang/{locale}.json";
            return Lookup(TryGetPackEntry(path, out var pack) ? Parse("pack|" + path, pack, 4 * 1024 * 1024) : null) ??
                Lookup(_entries.TryGetValue(path, out var archived) ? Parse(path, archived, 4 * 1024 * 1024) : null);
        }

        string? Lookup(JsonElement? language) => language is { ValueKind: JsonValueKind.Object } dictionary &&
            dictionary.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // A resource by its path inside assets/: KubeJS first (it overrides mods in game), then the indexed JARs.
    private bool TryGetEntry(string entryPath, out ZipArchiveEntry entry) =>
        TryGetPackEntry(entryPath, out entry) || _entries.TryGetValue(entryPath, out entry!);

    private JsonElement? GetJsonForId(string kind, string id)
    {
        if (id.Length == 0) return null;
        var (scope, path) = SplitId(id);
        return GetJson($"assets/{scope}/{kind}/{path}.json", 1024 * 1024);
    }

    private JsonElement? GetJson(string entryPath, int limit)
    {
        if (_json.TryGetValue(entryPath, out var cached)) return cached?.RootElement;
        return TryGetEntry(entryPath, out var entry) ? Parse(entryPath, entry, limit) : null;
    }

    private JsonElement? Parse(string cacheKey, ZipArchiveEntry entry, int limit)
    {
        if (_json.TryGetValue(cacheKey, out var cached)) return cached?.RootElement;
        string entryPath = entry.FullName;
        JsonDocument? document = null;
        try
        {
            using var stream = ReadEntry(entry, limit);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, $"解析资源失败（{entryPath}）");
        }

        _json[cacheKey] = document;
        return document?.RootElement;
    }

    // kubejs/assets sits next to the instance's mods folder. Only the paths are indexed here.
    private void IndexKubeJs(string modsDirectory, CancellationToken token)
    {
        string? gameFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(modsDirectory));
        if (string.IsNullOrEmpty(gameFolder)) return;
        string root = Path.Combine(gameFolder, "kubejs", "assets");
        if (!Directory.Exists(root)) return;
        try {
            foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden
            })) {
                token.ThrowIfCancellationRequested();
                if (_packFiles.Count >= MaxIndexedResources) break;
                string entryPath = "assets/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!Supported(entryPath)) continue;
                _packFiles.TryAdd(entryPath, file);
                _namespaces.Add(entryPath.Split('/', 3)[1]);
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            SchematicOptions.Log(error, "读取 KubeJS 资源列表失败");
        }
    }

    // Reads a KubeJS file the first time it is asked for, as a one-entry archive so it is used like a JAR entry.
    private bool TryGetPackEntry(string entryPath, out ZipArchiveEntry entry)
    {
        entry = null!;
        if (_packEntries.TryGetValue(entryPath, out var known)) { entry = known!; return known is not null; }
        if (!_packFiles.TryGetValue(entryPath, out var file)) return false;
        ZipArchiveEntry? made = null;
        try {
            if (new FileInfo(file).Length <= MaxPackFileBytes) {
                var buffer = new MemoryStream();
                using (var writer = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
                    using var target = writer.CreateEntry(entryPath, CompressionLevel.NoCompression).Open();
                    using var source = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    source.CopyTo(target);
                }
                buffer.Position = 0;
                var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
                _archives.Add(archive);
                made = archive.Entries[0];
            }
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) {
            SchematicOptions.Log(error, $"读取 KubeJS 资源失败（{entryPath}）");
        }
        _packEntries[entryPath] = made;
        entry = made!;
        return made is not null;
    }

    // assets/<namespace>/<textures|models|blockstates|lang>/<path> with a valid id, the same kinds a JAR is indexed for.
    private static bool Supported(string entryPath)
    {
        var parts = entryPath.Split('/', 4);
        if (parts.Length != 4 || parts[0] != "assets" || !ValidId(parts[1], parts[3])) return false;
        return parts[2] == "textures" ? parts[3].EndsWith(".png", StringComparison.OrdinalIgnoreCase) :
            (parts[2] is "models" or "blockstates" or "lang") && parts[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    internal static MemoryStream ReadEntry(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("客户端资源超过读取上限。");
        using var source = entry.Open();
        var result = new MemoryStream();
        try {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0) {
                if (result.Length + count > limit) throw new InvalidDataException("客户端资源超过读取上限。");
                result.Write(buffer, 0, count);
            }
            result.Position = 0;
            return result;
        } catch { result.Dispose(); throw; }
    }

    internal static string NormalizeModelPath(string path)
    {
        var (scope, value) = SplitId(path.Trim().ToLowerInvariant());
        if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) value = value[..^5];
        if (value.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        return ValidId(scope, value) ? WithNamespace(scope, value) : "";
    }

    /// <summary>模型里写的贴图引用（minecraft:block/oak_planks）转成贴图表里的键（block/oak_planks）。</summary>
    internal static string NormalizeTexturePath(string path)
    {
        var (scope, value) = SplitId(path.Trim().ToLowerInvariant());
        if (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) value = value[9..];
        if (value.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase)) value = "block/" + value[7..];
        return ValidId(scope, value) ? WithNamespace(scope, value) : "";
    }

    internal static string NormalizeBlockId(string name)
    {
        int cut = name.IndexOfAny(['[', '{']);
        if (cut >= 0) name = name[..cut];
        var (scope, path) = SplitId(name.Trim().ToLowerInvariant());
        return ValidId(scope, path) ? WithNamespace(scope, path) : "";
    }

    internal static (string Namespace, string Path) SplitId(string id)
    {
        int separator = id.IndexOf(':');
        return separator < 0 ? ("minecraft", id) : (id[..separator], id[(separator + 1)..]);
    }

    internal static string WithNamespace(string scope, string path) => scope == "minecraft" ? path : scope + ":" + path;

    private static bool ValidId(string scope, string path) => scope.Length is > 0 and <= 128 && scope is not ("." or "..") && scope.All(IdCharacter) &&
        path.Length is > 0 and <= 1024 && path.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..") && segment.All(IdCharacter));

    private static bool IdCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.';

    private void IndexArchive(string path, CancellationToken token)
    {
        ZipArchive? archive = null;
        bool keep = false;
        try {
            archive = OpenArchive(path);
            foreach (var entry in archive.Entries) {
                token.ThrowIfCancellationRequested();
                if (_entries.Count >= MaxIndexedResources) break;
                if (!entry.FullName.StartsWith("assets/", StringComparison.Ordinal)) continue;
                if (!Supported(entry.FullName)) continue;
                _namespaces.Add(entry.FullName.Split('/', 3)[1]);
                if (_entries.TryAdd(entry.FullName, entry) && !keep) {
                    _archives.Add(archive);
                    keep = true;
                }
            }
        } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) {
            SchematicOptions.Log(error, "读取方块资源 JAR 失败");
        } finally {
            if (!keep) archive?.Dispose();
        }
    }

    private static ZipArchive OpenArchive(string path)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try { return new ZipArchive(stream, ZipArchiveMode.Read); }
        catch { stream.Dispose(); throw; }
    }
}
