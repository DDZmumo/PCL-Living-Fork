using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using MeloongCore;
using MeloongCore.Extensions;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Exercise the real Main startup/host bindings. Do not pre-register codecs or
// legacy encodings here: that would hide the deployment failures under test.
internal static class DesktopCompatibilityChecks
{
    // Generated with LF's pinned libwebp64: lossless 1x1 RGBA (12,34,56,255).
    private const string WebPPixel = "UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAdQkTIUp/+BiOh/AAA=";
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetACP();

    internal static void WebP(string root)
    {
        string folder = Path.Combine(root, "webp-production-host");
        Directory.CreateDirectory(folder);
        var purePath = Native.Field(typeof(ModBase), "PathPure");
        object? previous = purePath.GetValue(null);
        purePath.SetValue(null, new Lazy<string>(() => Path.Combine(folder, "native") + Path.DirectorySeparatorChar));
        try
        {
            foreach (string name in new[] { "pixel.webp", "Logo.png", "cached-mod-icon.png" })
            {
                string path = Path.Combine(folder, name);
                File.WriteAllBytes(path, Convert.FromBase64String(WebPPixel));
                MyBitmap image = new(path);
                try
                {
                    Native.Check(image.Pic is { Width: 1, Height: 1 }, "Real Main failed to decode WebP through its production host: " + name);
                    Native.Check(image.Pic!.GetPixel(0, 0).ToArgb() == System.Drawing.Color.FromArgb(255, 12, 34, 56).ToArgb(), "Production WebP pixel changed.");
                    BitmapSource source = image.ToImageSource();
                    Native.Check(source.IsFrozen && source.PixelWidth == 1, "WebP did not produce a frozen WPF image.");
                }
                finally { image.Pic?.Dispose(); }
                var thumbnail = LauncherMarkupImages.ReadCacheAsync(path, default).GetAwaiter().GetResult();
                Native.Check(thumbnail.Image is { PixelWidth: 1, IsFrozen: true }, "Actual cached image decoder lost its WebP image.");
                using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            string[] arguments = Environment.GetCommandLineArgs();
            int sample = Array.IndexOf(arguments, "--webp-fixture");
            if (sample >= 0)
            {
                if (sample + 1 >= arguments.Length) throw new ArgumentException("Missing read-only image fixture.");
                string path = Path.GetFullPath(arguments[sample + 1]);
                byte[] before = File.ReadAllBytes(path);
                MyBitmap actual = new(path);
                try { Native.Check(actual.Pic is { Width: > 0, Height: > 0 } && actual.ToImageSource().IsFrozen, "The reported WebP fixture still fails through Main."); }
                finally { actual.Pic?.Dispose(); }
                Native.Check(File.ReadAllBytes(path).SequenceEqual(before), "Reported image fixture changed during decoding.");
                Console.WriteLine("PASS reported WebP image copy through actual Main; source bytes unchanged.");
            }
            Console.WriteLine("PASS production WebP initialization: real Main bitmap host, WebP/PNG-named version and cached-mod icons, pixels and released handles; no test codec resolver.");
        }
        finally { purePath.SetValue(null, previous); }
    }

    internal static void SearchAndLegacyEncoding(string root)
    {
        List<Exception> failures = new();
        try { SearchText(); }
        catch (Exception error) { Console.Error.WriteLine("FAIL production search normalization: " + error); failures.Add(error); }
        try { LegacyEncoding(root); }
        catch (Exception error) { Console.Error.WriteLine("FAIL production legacy encoding initialization: " + error); failures.Add(error); }
        try { ArchiveIcons(root); }
        catch (Exception error) { Console.Error.WriteLine("FAIL production archive icon encoding: " + error); failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Desktop compatibility checks failed.", failures);
    }

    private static void SearchText()
    {
        object engine = Native.Field(typeof(ResourceSearcher), "Engine").GetValue(null)!;
        object host = Native.Field(engine.GetType(), "host").GetValue(engine)!;
        var simplify = (Func<string, string>)Native.Field(host.GetType(), "SimplifyChinese").GetValue(host)!;
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Native.Check(simplify("") == "", "Empty resource search failed.");
                Native.Check(simplify("JEI 1.21") == "JEI 1.21", "ASCII search changed.");
                Native.Check(simplify("繁體中文 模組資源") == "繁体中文 模组资源", "Traditional Chinese search did not normalize under " + culture);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Console.WriteLine("PASS actual Main search normalization: empty/ASCII/traditional Chinese under en-US, zh-CN and zh-TW; no global NLS switch.");
    }

    private static void LegacyEncoding(string root)
    {
        string oldAdvice = new DllNotFoundException("synthetic missing codec").GetDisplay(false);
        string newAdvice = (string)Native.Static("PCL.PclLogger", "ModernizeRuntimeMessage", oldAdvice)!;
        Native.Check(!newAdvice.Contains("Framework 4.8", StringComparison.Ordinal) && newAdvice.Contains("组件", StringComparison.Ordinal), "Modern product retained misleading CLR4 repair advice.");
        // The provider is used directly only to create independent fixture bytes.
        Encoding fixtureEncoding = CodePagesEncodingProvider.Instance.GetEncoding(54936)!;
        const string json = "{\"id\":\"中文版本\"}";
        Native.Check(fixtureEncoding.GetBytes(json).GetString() == json, "Actual Shared legacy JSON fallback is unavailable after Main initialization.");
        string archive = Path.Combine(root, "legacy-chinese.zip");
        using (ZipArchive output = ZipFile.Open(archive, ZipArchiveMode.Create, fixtureEncoding))
        using (StreamWriter writer = new(output.CreateEntry("中文目录/配置.txt").Open(), Encoding.UTF8))
            writer.Write("fixture");
        using (ZipArchive input = FileUtils.OpenZip(archive))
            Native.Check(input.Entries.Single().FullName == "中文目录/配置.txt", "Actual Shared legacy ZIP entry decoding changed.");
        using FileStream exclusive = File.Open(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("PASS Main startup legacy encodings: Shared GB18030 JSON and non-EFS Chinese ZIP, unchanged content and released handles.");
    }

    private static void ArchiveIcons(string root)
    {
        int codePage = checked((int)GetACP());
        Encoding acp = CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        string label = new[] { "中文", "café", "Ж", "あ", "Ω" }.FirstOrDefault(value => acp.GetString(acp.GetBytes(value)) == value) ?? "ascii";
        int sampleIndex = 0;
        foreach (var sample in new[] { (Name: label, Encoding: acp), (Name: "中文", Encoding: Encoding.UTF8) })
        {
            string path = Path.Combine(root, "mod-icon-" + sampleIndex++ + ".jar");
            string entryName = "assets/" + sample.Name + ".png";
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create, sample.Encoding))
            using (Stream entry = archive.CreateEntry(entryName).Open())
            using (System.Drawing.Bitmap bitmap = new(2, 2))
                bitmap.Save(entry, System.Drawing.Imaging.ImageFormat.Png);
            var icon = ModDependencyIconFiles.Read(path, entryName) as BitmapSource;
            Native.Check(icon is { IsFrozen: true, PixelWidth: 32, PixelHeight: 32 }, "Real dependency icon reader lost its 32px ACP/UTF8 icon: " + icon?.PixelWidth + "x" + icon?.PixelHeight);
            using FileStream exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Console.WriteLine("PASS real JAR icon reader: Windows ACP and EFS UTF8 names, eager pixels and released archives.");
    }
}
