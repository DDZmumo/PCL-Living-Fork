using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

// Functional resource pipeline from Test-InstanceResources: no live API, viewer or game.
internal static class BackupResourceStoreChecks
{
    private static void Zip(string file, params (string Name, string Text)[] items)
    {
        using ZipArchive archive = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var item in items) { using StreamWriter writer = new(archive.CreateEntry(item.Name).Open()); writer.Write(item.Text); }
    }
    private static void Png(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        DrawingVisual visual = new(); using (DrawingContext drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 96, 64));
        RenderTargetBitmap bitmap = new(96, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using FileStream output = File.Create(file); encoder.Save(output);
    }
    internal static void Run(Scope scope)
    {
        string root = Path.Combine(scope.Root, "resources"); Directory.CreateDirectory(root);
        BackupResourceContractChecks.Run(root);
        string missing = Path.Combine(root, "absent");
        Native.Check(Scan(missing, "Saves").Count == 0 && !Directory.Exists(missing), "Viewing resources created a missing directory.");
        string game = Path.Combine(root, "minecraft"), saves = (string)Store("Root", game, Kind("Saves"))!;
        Put(Path.Combine(saves, "Test World/level.dat"), "synthetic-world-data"); Put(Path.Combine(saves, "Test World/region/r.0.0.mca"), "synthetic-region"); Png(Path.Combine(saves, "Test World/icon.png"));
        IList worlds = Scan(saves, "Saves");
        Native.Check(worlds.Count == 1 && (string)Native.Get(worlds[0]!, "Name")! == "Test World", "Actual world folders were not listed.");
        Type entryType = worlds[0]!.GetType();
        Native.Check(entryType.Assembly == Native.Main && entryType.FullName == "PCL.InstanceResourceEntry" && entryType.IsSealed && entryType.BaseType!.FullName == "PCLCS.InstanceResourceEntryData", "Resource facade lost its genuine Main type and single production DTO base.");
        foreach (var field in new (string Name, Type Type)[] { ("Name", typeof(string)), ("FullPath", typeof(string)), ("Description", typeof(string)), ("IsDirectory", typeof(bool)), ("ModifiedUtc", typeof(DateTime)), ("CreatedUtc", typeof(DateTime)), ("Length", typeof(long)) }) {
            var actual = Native.Field(entryType, field.Name); Native.Check(actual.FieldType == field.Type && actual.DeclaringType == entryType.BaseType, "Original resource field ABI changed: " + field.Name);
        }
        var icon = Native.Field(entryType, "Icon");
        Native.Check(icon.DeclaringType == entryType && icon.FieldType == typeof(ImageSource) && icon.GetValue(worlds[0]) is ImageSource { IsFrozen: true }, "World icon did not retain original field/type/frozen pixels.");
        string export = Path.Combine(root, "world.zip"); Store("Export", saves, worlds[0], export, None);
        using (ZipArchive archive = ZipFile.OpenRead(export)) Native.Check(archive.GetEntry("level.dat") is not null && archive.GetEntry("region/r.0.0.mca") is not null, "World ZIP export omitted files.");
        Reject(() => Store("Export", saves, worlds[0], Path.Combine((string)Native.Get(worlds[0]!, "FullPath")!, "self.zip"), None), "recursive self export");
        Store("ImportWorld", saves, export, "Imported", None); Text(Path.Combine(saves, "Imported/level.dat"), "synthetic-world-data");
        Reject(() => Store("ImportWorld", saves, export, "Imported", None), "import collision");
        Store("ImportFolder", saves, saves, Kind("Saves"), Path.Combine(saves, "Imported"), "Folder Copy", None);
        Native.Check(File.Exists(Path.Combine(saves, "Folder Copy/region/r.0.0.mca")), "World folder import omitted files.");
        string traversal = Path.Combine(root, "traversal.zip"); Zip(traversal, ("../outside.txt", "must-not-write"), ("level.dat", "bad"));
        Reject(() => Store("ImportWorld", saves, traversal, "Bad", None), "ZIP traversal");
        Native.Check(!File.Exists(Path.Combine(root, "outside.txt")) && !Directory.Exists(Path.Combine(saves, "Bad")), "Failed ZIP import escaped staging or published a partial world.");
        using (CancellationTokenSource cancel = new()) { cancel.Cancel(); Native.Check(Reject(() => Store("ImportWorld", saves, export, "Canceled", cancel.Token), "canceled import") is OperationCanceledException, "World import cancellation category changed."); }
        Native.Check(!Directory.EnumerateDirectories(saves, ".pcl-import-*").Any(), "Canceled import retained staging directories.");
        string packs = (string)Store("Root", game, Kind("ResourcePacks"))!; Directory.CreateDirectory(packs);
        string pack = Path.Combine(root, "Native Pack.zip"); Zip(pack, ("pack.mcmeta", "{\"pack\":{\"pack_format\":46,\"description\":\"Synthetic native pack\"}}"));
        PackageGuards(root, pack);
        Store("ImportFile", packs, packs, Kind("ResourcePacks"), pack, None);
        IList entries = Scan(packs, "ResourcePacks"); Native.Check(entries.Count == 1 && ((string)Native.Get(entries[0]!, "Description")!).Contains("Synthetic native pack", StringComparison.Ordinal), "Resource-pack metadata was not read.");
        Reject(() => Store("ImportFile", packs, packs, Kind("ResourcePacks"), pack, None), "same-name resource import");
        string renamed = (string)Store("Rename", packs, entries[0], "Renamed.zip")!; Native.Check(File.Exists(renamed), "Resource rename failed or retained archive handle.");
        string trash = Path.Combine(game, "PCL/ResourceRecycle/ResourcePacks");
        Store("DeleteMany", packs, Scan(packs, "ResourcePacks"), None);
        Native.Check(!File.Exists(renamed) && !Directory.Exists(trash), "Direct deletion retained the resource or a recycle copy.");
        Store("ImportFile", packs, packs, Kind("ResourcePacks"), pack, None);
        IList stale = Scan(packs, "ResourcePacks"); File.AppendAllText((string)Native.Get(stale[0]!, "FullPath")!, "changed-after-listing");
        Reject(() => Store("DeleteMany", packs, stale, None), "stale selection deletion");
        string shots = (string)Store("Root", game, Kind("Screenshots"))!, png = Path.Combine(shots, "screenshot.png"); Png(png);
        IList images = Scan(shots, "Screenshots");
        Native.Check(Store("LoadImage", Native.Get(images[0]!, "FullPath"), 1200) is BitmapSource { IsFrozen: true }, "Screenshot preview was not fully decoded/frozen.");
        File.Move(png, Path.Combine(shots, "renamed.png")); File.Move(Path.Combine(shots, "renamed.png"), png);
        string shaders = (string)Store("Root", game, Kind("ShaderPacks"))!, schematics = (string)Store("Root", game, Kind("Schematics"))!; Directory.CreateDirectory(shaders);
        Store("ImportFile", shaders, shaders, Kind("ShaderPacks"), pack, None);
        Put(Path.Combine(schematics, "house.litematic"), "schematic-fixture"); Put(Path.Combine(schematics, "group/tree.schem"), "schematic-folder");
        Native.Check(Scan(shaders, "ShaderPacks").Count == 1 && Scan(schematics, "Schematics").Count == 2, "Shader/schematic resources were not listed.");
        Reject(() => Store("EnsureWithin", schematics, Path.Combine(root, "foreign.txt"), false), "foreign resource scope");
        foreach (string name in new[] { "../escape", "CON", "file.", "a/b" }) Reject(() => Store("SafeName", name), "unsafe resource name " + name);
        Console.WriteLine("PASS backup-resources store: actual world ZIP/folder roundtrips, collision/cancel/path/stale guards, frozen previews/handle release, package validation, native resource page and batch export/direct deletion.");
    }

    private static void PackageGuards(string root, string valid)
    {
        object packs = Kind("ResourcePacks"), shaders = Kind("ShaderPacks"), unknown = Enum.ToObject(Native.Type("PCL.InstanceResourceKind"), 5);
        string empty = Path.Combine(root, "empty.zip"), wrongCase = Path.Combine(root, "wrong-case.zip"), unparsed = Path.Combine(root, "unparsed.zip"), shader = Path.Combine(root, "shader.zip"), directory = Path.Combine(root, "shader-directory.zip"), wrongShader = Path.Combine(root, "wrong-shader.zip"), broken = Path.Combine(root, "broken.zip"), lazy = Path.Combine(root, "lazy-central.zip");
        Zip(empty); Zip(wrongCase, ("Pack.mcmeta", "not JSON")); Zip(unparsed, ("pack.mcmeta", "not JSON")); Zip(shader, ("SHADERS/only.vert", "not inspected")); Zip(directory, ("shaders/", "")); Zip(wrongShader, ("nested/shaders/file.vert", "not root"), ("shaders\\file.vert", "wrong separator")); Put(broken, "not ZIP");
        foreach (string path in new[] { valid, unparsed }) Store("ValidatePackage", path, packs);
        foreach (string path in new[] { shader, directory }) Store("ValidatePackage", path, shaders);
        foreach (string path in new[] { empty, wrongCase }) { Exception ex = Reject(() => Store("ValidatePackage", path, packs), "missing pack metadata"); Native.Check(ex.GetType() == typeof(IOException) && ex.Message == "资源包缺少 pack.mcmeta，未替换原文件。", "Resource-pack validation exception/message changed."); }
        foreach (string path in new[] { empty, wrongShader }) { Exception ex = Reject(() => Store("ValidatePackage", path, shaders), "missing root shaders"); Native.Check(ex.GetType() == typeof(IOException) && ex.Message == "光影包缺少 shaders 目录，未替换原文件。", "Shader validation exception/message changed."); }
        Store("ValidatePackage", empty, unknown);
        Native.Check(Reject(() => Store("ValidatePackage", Path.Combine(root, "missing.zip"), unknown), "missing ZIP") is FileNotFoundException, "Missing archive exception changed.");
        Native.Check(Reject(() => Store("ValidatePackage", broken, unknown), "invalid ZIP") is InvalidDataException, "Invalid archive exception changed.");
        Zip(lazy, ("file.txt", "owned")); byte[] bytes = File.ReadAllBytes(lazy); List<int> positions = [];
        for (int i = 0; i < bytes.Length - 3; i++) if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) positions.Add(i);
        Native.Check(positions.Count == 1, "Synthetic central directory is ambiguous."); bytes[positions[0]] = 0; File.WriteAllBytes(lazy, bytes);
        Store("ValidatePackage", lazy, unknown);
        foreach (object kind in new[] { packs, shaders }) Native.Check(Reject(() => Store("ValidatePackage", lazy, kind), "corrupt ZIP central directory") is InvalidDataException, "Known-kind package validation did not enumerate its real central directory.");
        foreach (string path in new[] { valid, empty, wrongCase, unparsed, shader, directory, wrongShader, broken, lazy }) { using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.None); }
    }
}
