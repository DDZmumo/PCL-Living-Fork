// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Buffers.Binary;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS.Applications;

public enum SkinTextureKind { Skin, Cape }
public enum SkinModel { Classic, Slim }
public sealed record SkinLibraryEntry(string Id, string Name, SkinTextureKind Kind, int Width, int Height, SkinModel Model, DateTime UpdatedUtc);
public sealed record SkinImportError(string File, string Message);
public sealed record SkinImportResult(IReadOnlyList<SkinLibraryEntry> Entries, IReadOnlyList<SkinImportError> Errors);
public sealed record OfflineSkinAppearance(string? SkinId = null, string? CapeId = null);
public sealed record SkinLibraryTexture(byte[] Png, SkinModel Model);
public sealed record OfflineSkinTextures(SkinLibraryTexture? Skin, byte[]? Cape);

/// <summary>Owns LF's local PNG files, per-file model flags and per-player assignments.</summary>
public sealed class SkinLibraryStore(string rootDirectory, Action<byte[]> decodePng) {
    public const int MaximumTextureBytes = 4 * 1024 * 1024;
    private const int MaximumCapePixels = 4 * 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly object gate = new();
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    private string AssignmentsFile => Path.Combine(RootDirectory, "OfflineAppearances.json");

    public string DirectoryFor(SkinTextureKind kind) => kind switch {
        SkinTextureKind.Skin => RootDirectory,
        SkinTextureKind.Cape => Path.Combine(RootDirectory, "Capes"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public IReadOnlyList<SkinLibraryEntry> List(SkinTextureKind kind = SkinTextureKind.Skin) {
        lock (gate) {
            string directory = CheckedDirectory(kind);
            if (!Directory.Exists(directory)) return [];
            var entries = new List<SkinLibraryEntry>();
            foreach (string path in Directory.EnumerateFiles(directory)) {
                if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) continue;
                try { entries.Add(Describe(path, kind)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public SkinImportResult Import(IEnumerable<string> files, SkinTextureKind kind = SkinTextureKind.Skin) {
        ArgumentNullException.ThrowIfNull(files);
        var entries = new List<SkinLibraryEntry>();
        var errors = new List<SkinImportError>();
        foreach (string path in files) {
            try { entries.Add(ImportTexture(Path.GetFileNameWithoutExtension(path), ReadBounded(path), kind)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
                errors.Add(new(Path.GetFileName(path), error.Message));
            }
        }
        return new(entries.AsReadOnly(), errors.AsReadOnly());
    }

    public SkinLibraryEntry ImportTexture(string name, byte[] png, SkinTextureKind kind = SkinTextureKind.Skin, SkinModel model = SkinModel.Classic) {
        name = ValidateName(name);
        ArgumentNullException.ThrowIfNull(png);
        if (!Enum.IsDefined(model) || kind == SkinTextureKind.Cape && model != SkinModel.Classic) throw new ArgumentOutOfRangeException(nameof(model));
        png = (byte[])png.Clone();
        CheckTexture(png, kind);
        lock (gate) {
            string folder = CheckedDirectory(kind);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + ".png");
            for (int suffix = 2; File.Exists(path) || File.Exists(ModelPath(path)); suffix++)
                path = Path.Combine(folder, $"{name} ({suffix}).png");
            string staged = StagingPath(folder);
            try {
                File.WriteAllBytes(staged, png);
                File.Move(staged, path);
                try { if (model == SkinModel.Slim) WriteModel(path, model); }
                catch { File.Delete(path); throw; }
                return Describe(path, kind);
            } finally { File.Delete(staged); }
        }
    }

    public SkinLibraryTexture ReadTexture(string id, SkinTextureKind kind = SkinTextureKind.Skin) {
        lock (gate) {
            string path = EntryPath(id, kind);
            byte[] png = ReadBounded(path);
            CheckTexture(png, kind);
            return new(png, kind == SkinTextureKind.Skin ? ReadModel(path) : SkinModel.Classic);
        }
    }

    public SkinLibraryEntry SetModel(string id, SkinModel model) {
        if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        lock (gate) {
            string path = EntryPath(id, SkinTextureKind.Skin);
            var entry = Describe(path, SkinTextureKind.Skin);
            WriteModel(path, model);
            return entry with { Model = model };
        }
    }

    public SkinLibraryEntry Rename(string id, string name, SkinTextureKind kind = SkinTextureKind.Skin) {
        name = ValidateName(name);
        lock (gate) {
            string source = EntryPath(id, kind);
            var entry = Describe(source, kind);
            string target = Path.Combine(DirectoryFor(kind), name + ".png");
            if (string.Equals(source, target, StringComparison.Ordinal)) return entry;
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || File.Exists(ModelPath(target))))
                throw new IOException("皮肤库中已有同名文件。");
            RequireOrdinary(ModelPath(source));
            RequireOrdinary(ModelPath(target));
            var assignments = ReadAssignments();
            bool changed = ReplaceAssignments(assignments, id, Path.GetFileName(target), kind);
            string staged = StagingPath(DirectoryFor(kind));
            File.Move(source, staged);
            bool textureMoved = false, modelMoved = false;
            try {
                File.Move(staged, target); textureMoved = true;
                if (File.Exists(ModelPath(source))) {
                    File.Move(ModelPath(source), ModelPath(target)); modelMoved = true;
                }
                if (changed) WriteAssignments(assignments);
            } catch {
                if (modelMoved) File.Move(ModelPath(target), ModelPath(source));
                if (textureMoved) File.Move(target, staged);
                File.Move(staged, source);
                throw;
            }
            return entry with { Id = Path.GetFileName(target), Name = name };
        }
    }

    public void Remove(string id, SkinTextureKind kind = SkinTextureKind.Skin) {
        lock (gate) {
            string entry = EntryPath(id, kind);
            RequireOrdinary(ModelPath(entry));
            var assignments = ReadAssignments();
            bool changed = ReplaceAssignments(assignments, id, null, kind);
            string pending = StagingPath(DirectoryFor(kind));
            File.Move(entry, pending);
            bool modelMoved = false;
            try {
                if (File.Exists(ModelPath(entry))) { File.Move(ModelPath(entry), ModelPath(pending)); modelMoved = true; }
                if (changed) WriteAssignments(assignments);
            } catch {
                if (modelMoved) File.Move(ModelPath(pending), ModelPath(entry));
                File.Move(pending, entry);
                throw;
            }
            File.Delete(pending);
            if (modelMoved) File.Delete(ModelPath(pending));
        }
    }

    public OfflineSkinAppearance GetOfflineAppearance(string player) {
        if (string.IsNullOrWhiteSpace(player)) return new();
        lock (gate) return ReadAssignments().GetValueOrDefault(player.Trim()) ?? new();
    }

    public void SetOfflineTexture(string player, SkinTextureKind kind, string? id) {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(player) || player.Length > 256) throw new ArgumentException("请先选择离线玩家。", nameof(player));
        player = player.Trim();
        lock (gate) {
            if (id is not null) ReadTexture(id, kind);
            var assignments = ReadAssignments();
            var previous = assignments.GetValueOrDefault(player) ?? new();
            var updated = kind == SkinTextureKind.Skin ? previous with { SkinId = id } : previous with { CapeId = id };
            if (updated.SkinId is null && updated.CapeId is null) assignments.Remove(player);
            else assignments[player] = updated;
            WriteAssignments(assignments);
        }
    }

    public OfflineSkinTextures? ReadOfflineTextures(string player) {
        lock (gate) {
            var appearance = GetOfflineAppearance(player);
            var skin = appearance.SkinId is { } skinId ? ReadIfPresent(skinId, SkinTextureKind.Skin) : null;
            var cape = appearance.CapeId is { } capeId ? ReadIfPresent(capeId, SkinTextureKind.Cape)?.Png : null;
            return skin is null && cape is null ? null : new(skin, cape);
        }
    }

    public string? FindOfflineSkinFile(string player) {
        lock (gate) {
            if (GetOfflineAppearance(player).SkinId is not { } id) return null;
            string path = EntryPath(id, SkinTextureKind.Skin);
            return File.Exists(path) ? path : null;
        }
    }

    private SkinLibraryTexture? ReadIfPresent(string id, SkinTextureKind kind) {
        try { return ReadTexture(id, kind); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private Dictionary<string, OfflineSkinAppearance> ReadAssignments() {
        RequireOrdinary(RootDirectory);
        RequireOrdinary(AssignmentsFile);
        if (!File.Exists(AssignmentsFile)) return new(StringComparer.Ordinal);
        try {
            var assignments = JsonSerializer.Deserialize<Dictionary<string, OfflineSkinAppearance>>(ReadBounded(AssignmentsFile));
            if (assignments is not null && assignments.Values.All(value => value is not null)) return assignments;
        } catch (System.Text.Json.JsonException error) {
            throw new IOException("离线皮肤库档案格式无效。", error);
        }
        throw new IOException("离线皮肤库档案格式无效。");
    }

    private void WriteAssignments(Dictionary<string, OfflineSkinAppearance> assignments) =>
        LocalGameDataStore.AtomicWrite(AssignmentsFile, JsonSerializer.SerializeToUtf8Bytes(assignments), null, false);

    private static bool ReplaceAssignments(Dictionary<string, OfflineSkinAppearance> assignments, string old, string? updated, SkinTextureKind kind) {
        string[] affected = assignments.Where(pair => string.Equals(kind == SkinTextureKind.Skin ? pair.Value.SkinId : pair.Value.CapeId,
            old, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
        foreach (string player in affected) {
            var previous = assignments[player];
            var next = kind == SkinTextureKind.Skin ? previous with { SkinId = updated } : previous with { CapeId = updated };
            if (next.SkinId is null && next.CapeId is null) assignments.Remove(player);
            else assignments[player] = next;
        }
        return affected.Length != 0;
    }

    private SkinLibraryEntry Describe(string path, SkinTextureKind kind) {
        RequireOrdinary(path);
        Span<byte> header = stackalloc byte[33];
        using var input = File.OpenRead(path);
        if (input.Length > MaximumTextureBytes) throw new IOException("纹理文件过大。");
        input.ReadExactly(header);
        var (width, height) = TextureSize(header, kind);
        return new(Path.GetFileName(path), Path.GetFileNameWithoutExtension(path), kind, width, height,
            kind == SkinTextureKind.Skin ? ReadModel(path) : SkinModel.Classic, File.GetLastWriteTimeUtc(path));
    }

    private string EntryPath(string id, SkinTextureKind kind) {
        if (string.IsNullOrEmpty(id) || id != Path.GetFileName(id) || !id.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("无效的皮肤库文件标识。", nameof(id));
        ValidateName(Path.GetFileNameWithoutExtension(id), limit: 240);
        string folder = CheckedDirectory(kind);
        string path = Path.Combine(folder, id);
        RequireOrdinary(path);
        return path;
    }

    private string CheckedDirectory(SkinTextureKind kind) {
        string folder = DirectoryFor(kind);
        RequireOrdinary(RootDirectory);
        if (kind == SkinTextureKind.Cape) RequireOrdinary(folder);
        return folder;
    }

    private static string ValidateName(string name, int limit = 40) {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length == 0 || name.Length > limit || name[^1] == '.' || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw new ArgumentException("皮肤名称包含无效字符或长度超出范围。", nameof(name));
        string device = name.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(device[3]))
            throw new ArgumentException("皮肤名称不能使用系统设备名。", nameof(name));
        return name;
    }

    private static string ModelPath(string entry) => entry + ".model";
    private static string StagingPath(string folder) => Path.Combine(folder, ".pcl-skin-" + Guid.NewGuid().ToString("N") + ".tmp");
    private static SkinModel ReadModel(string entry) {
        string path = ModelPath(entry);
        RequireOrdinary(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 64) return SkinModel.Classic;
        return string.Equals(File.ReadAllText(path).Trim(), "slim", StringComparison.OrdinalIgnoreCase) ? SkinModel.Slim : SkinModel.Classic;
    }
    private static void WriteModel(string entry, SkinModel model) {
        string path = ModelPath(entry);
        RequireOrdinary(path);
        if (model == SkinModel.Slim) LocalGameDataStore.AtomicWrite(path, "slim"u8.ToArray(), null, false);
        else File.Delete(path);
    }

    private static void RequireOrdinary(string path) {
        try {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("皮肤库中的链接文件或目录无法使用。");
        } catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static byte[] ReadBounded(string path) {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumTextureBytes) throw new IOException("皮肤库文件过大。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void CheckTexture(byte[] png, SkinTextureKind kind) {
        TextureSize(png, kind);
        decodePng(png);
    }

    public static (int Width, int Height) TextureSize(ReadOnlySpan<byte> png, SkinTextureKind kind) {
        if (png.Length is < 33 or > MaximumTextureBytes || !png[..8].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadInt32BigEndian(png[8..12]) != 13 || !png[12..16].SequenceEqual("IHDR"u8))
            throw new IOException("请选择有效的 PNG 纹理。");
        int width = BinaryPrimitives.ReadInt32BigEndian(png[16..20]);
        int height = BinaryPrimitives.ReadInt32BigEndian(png[20..24]);
        if (kind == SkinTextureKind.Skin && (width != 64 || height is not (32 or 64)))
            throw new IOException("皮肤必须是 64×32 或 64×64 的 PNG。");
        if (kind == SkinTextureKind.Cape && (width < 64 || height < 32 || width % 64 != 0 || height % 32 != 0 || (long)width * height > MaximumCapePixels))
            throw new IOException("披风尺寸需为 64×32 的整数倍，并且不超过 4194304 像素。");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return (width, height);
    }
}
