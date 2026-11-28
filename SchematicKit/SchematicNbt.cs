using System.IO;
using System.IO.Compression;
using fNbt;

namespace SchematicKit;

internal static class SchematicNbt
{
    private const int MaxBytes = 32 * 1024 * 1024;

    internal static NbtCompound Read(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxBytes) throw new InvalidDataException("投影文件过大。");
        int header = file.ReadByte();
        file.Position = 0;
        using Stream input = header switch {
            0x1f => new GZipStream(file, CompressionMode.Decompress),
            10 => file,
            _ when (header & 0x0f) == 8 => new ZLibStream(file, CompressionMode.Decompress),
            _ => throw new InvalidDataException("无法识别投影 NBT 格式。")
        };
        using var decoded = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer)) != 0) {
            token.ThrowIfCancellationRequested();
            if (decoded.Length + count > MaxBytes) throw new InvalidDataException("投影解压后的内容过大。");
            decoded.Write(buffer, 0, count);
        }
        decoded.Position = 0;
        // Verify structure before the NBT DOM allocates the declared arrays.
        var reader = new NbtReader(decoded);
        while (reader.ReadToFollowing()) {
            token.ThrowIfCancellationRequested();
            int width = reader.TagType switch { NbtTagType.IntArray => 4, NbtTagType.LongArray => 8, _ => 1 };
            if (reader.Depth > 64 || reader.TagsRead > 2_000_000 || reader.HasLength &&
                (reader.TagLength < 0 || (long)reader.TagLength * width > decoded.Length - decoded.Position))
                throw new InvalidDataException("投影的 NBT 结构超过读取上限。");
        }
        token.ThrowIfCancellationRequested();
        decoded.Position = 0;
        var nbt = new NbtFile();
        nbt.LoadFromStream(decoded, NbtCompression.None);
        return nbt.RootTag;
    }
}
