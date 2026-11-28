using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PCLLF.Native.Facade.Tests;
// Directly preserves Test-WorldDetails' real Main adapter, field/caption and file-handle assertions.
internal static class WorldChecks
{
    internal static void Run(FixtureEnvironment fixture)
    {
        string folder = Path.Combine(fixture.Data, "world");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "level.dat");
        using (var file = File.Create(path))
        using (var zip = new GZipStream(file, CompressionMode.Compress))
        {
            Header(zip, 10, "");
            Header(zip, 10, "Data");
            Text(zip, "LevelName", "Fixture world");
            Number(zip, 4, "LastPlayed", 1730000000000);
            Number(zip, 3, "GameType", 1);
            Number(zip, 1, "Difficulty", 3);
            Number(zip, 1, "hardcore", 1);
            Number(zip, 1, "allowCommands", 0);
            Number(zip, 1, "WasModded", 1);
            Number(zip, 3, "DataVersion", 3465);
            Number(zip, 3, "SpawnX", -32);
            Number(zip, 3, "SpawnY", 70);
            Number(zip, 3, "SpawnZ", 256);
            Number(zip, 4, "DayTime", 48000);
            Number(zip, 4, "Time", 92000);
            Header(zip, 10, "Version");
            Text(zip, "Name", "1.20.1");
            Number(zip, 1, "Snapshot", 0);
            zip.WriteByte(0);
            Header(zip, 10, "Unknown");
            Number(zip, 3, "Ignored", 42);
            zip.WriteByte(0);
            Header(zip, 10, "WorldGenSettings");
            Number(zip, 4, "seed", -9876543210123);
            zip.WriteByte(0);
            zip.WriteByte(0);
            zip.WriteByte(0);
        }

        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        object details = Read("ReadDetails", folder)!;
        DateTime played = new(2024, 10, 27, 3, 33, 20, DateTimeKind.Utc);
        (string Name, object Value)[] fields = [("Name", "Fixture world"), ("LastPlayedUtc", played), ("VersionName", "1.20.1"), ("DataVersion", 3465), ("GameMode", 1), ("Difficulty", 3), ("Hardcore", true), ("AllowCommands", false), ("WasModded", true), ("Snapshot", false), ("Seed", -9876543210123L), ("SpawnX", -32), ("SpawnY", 70), ("SpawnZ", 256), ("DayTime", 48000L), ("Time", 92000L)];
        foreach (var field in fields)
            Native.Check(Equals(Native.Get(details, field.Name), field.Value), "Real world detail mismatch: " + field.Name);
        string expected = string.Join("\r\n", new[] { "世界名称：Fixture world", "存档文件夹：" + Path.GetFileName(folder), "游戏版本：1.20.1", "数据版本：3465", "最后游玩：" + played.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "游戏模式：创造", "难度：困难", "极限模式：开启", "允许作弊：关闭", "模组环境记录：使用过", "世界种子：-9876543210123", "世界出生点：-32, 70, 256", "游戏日：第 2 天", "世界累计刻数：92000", "", "位置：" + folder });
        Native.Check(Describe(folder) == expected, "World caption changed exact CRLF, field order, spacing or wording.");
        object basic = Read("Read", folder)!;
        Native.Check((string)Native.Get(basic, "Name")! == "Fixture world", "Basic world facade lost the real name.");
        foreach (string name in fields.Skip(2).Select(value => value.Name))
            Native.Check(Native.Get(basic, name)is null, "Basic facade unexpectedly read detail field: " + name);
        Released(path);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            Native.Check(Read("ReadDetails", folder)is not null, "Main facade refused a game writer handle.");
        FileAttributes attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            Native.Check(Describe(folder) == expected, "Read-only world changed its caption.");
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }

        Released(path);
        Native.Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "World detail reads modified bytes.");
        string legacy = Path.Combine(fixture.Data, "legacy-world");
        Directory.CreateDirectory(legacy);
        using (var file = File.Create(Path.Combine(legacy, "level.dat")))
        using (var zip = new GZipStream(file, CompressionMode.Compress))
        {
            Header(zip, 10, "");
            Header(zip, 10, "Data");
            Text(zip, "LevelName", "Old world");
            Number(zip, 4, "RandomSeed", -42);
            zip.WriteByte(0);
            zip.WriteByte(0);
        }

        string old = Describe(legacy);
        Native.Check(old.Contains("Old world", StringComparison.Ordinal) && old.Contains("-42", StringComparison.Ordinal) && !old.Contains("游戏模式：", StringComparison.Ordinal), "Legacy/absent fields were not preserved.");
        Native.Check(Read("Read", legacy)is null, "Legacy world without LastPlayed became a recent entry.");
        CheckFileLink(fixture, path);
        Native.Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Link validation modified its target.");
        File.WriteAllText(path, "invalid gzip");
        bool corrupt = false;
        try
        {
            Read("ReadDetails", folder);
        }
        catch
        {
            corrupt = true;
        }

        Native.Check(corrupt, "ReadDetails swallowed corrupt input.");
        string bad = Describe(folder);
        Native.Check(bad.Contains("无法读取", StringComparison.Ordinal) && bad.Contains(folder, StringComparison.Ordinal), "Corrupt facade lost folder/error text.");
        Released(path);
        Native.Check(Describe(Path.Combine(fixture.Data, "missing-world")).Contains("未找到", StringComparison.Ordinal), "Missing metadata was not explained.");
    }

    private static void CheckFileLink(FixtureEnvironment fixture, string path)
    {
        string linked = Directory.CreateDirectory(Path.Combine(fixture.Data, "linked-world")).FullName;
        string link = Path.Combine(linked, "level.dat");
        const int privilegeNotHeld = unchecked((int)0x80070522);
        try { File.CreateSymbolicLink(link, path); }
        catch (IOException error) when (error.HResult == privilegeNotHeld)
        {
            Console.WriteLine("SKIP world-details symbolic-link assertion: Windows runner lacks symbolic-link permission.");
            return;
        }

        try
        {
            bool rejected = false;
            try { Read("ReadDetails", linked); }
            catch (IOException error) when (error.Message.Contains("链接文件", StringComparison.Ordinal)) { rejected = true; }
            Native.Check(rejected && Describe(linked).Contains("无法读取", StringComparison.Ordinal), "Real Main file-link guard was bypassed.");
        }
        finally { File.Delete(link); }
    }

    private static object? Read(string name, string folder) => Native.Static("PCL.WorldMetadata", name, folder);
    private static string Describe(string folder) => (string)Read("Describe", folder)!;
    private static void Released(string path)
    {
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void Name(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    internal static void Header(Stream stream, byte kind, string name)
    {
        stream.WriteByte(kind);
        Name(stream, name);
    }

    internal static void Text(Stream stream, string name, string text)
    {
        Header(stream, 8, name);
        Name(stream, text);
    }

    internal static void Number(Stream stream, byte kind, string name, long number)
    {
        Header(stream, kind, name);
        Span<byte> bytes = stackalloc byte[8];
        if (kind == 1)
            stream.WriteByte(checked((byte)number));
        else if (kind == 3)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)number));
            stream.Write(bytes[..4]);
        }
        else
        {
            BinaryPrimitives.WriteInt64BigEndian(bytes, number);
            stream.Write(bytes);
        }
    }
}
