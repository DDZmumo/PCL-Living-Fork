// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Media;

namespace PCL;

internal static class GlassJavaGraphics
{
    public const string Icon = "M3,7 H17 V9 H21 Q23,9 23,12 V13 Q23,16 20,16 H16 Q14,20 9,20 H8 Q3,20 3,15 Z M17,11 V14 H20 Q21,14 21,13 V12 Q21,11 20,11 Z M1,22 H19 V24 H1 Z M7,1 H9 V5 H7 Z M12,0 H14 V5 H12 Z";
    private const double MarkUnit = 40;
    internal sealed record JavaVendor(string Key, string Name, DrawingImage Logo, string Description);

    private static readonly JavaVendor GenericVendor = new("", "Java", DrawLogo(
        ("#FFFFFF", "F0 M8 15 H28 V17 H31 A5 5 0 0 1 31 27 H26 Q23 32 18 32 Q8 32 8 22 Z M11 18 V22 Q11 29 18 29 Q25 29 25 22 V18 Z M28 20 V24 H31 A2 2 0 0 0 31 20 Z M15 7 C11 10 14 13 17 13 L18 11 Q15 10 17 7 Z M23 5 C18 8 21 12 24 13 L25 11 Q22 9 25 6 Z M7 34 H32 V36 H7 Z")), "");
    internal static readonly JavaVendor AllVendors = new("", "全部可下载", DrawLogo(
        ("#FFFFFF", "F0 M7 5 H16 Q18 5 18 7 V16 Q18 18 16 18 H7 Q5 18 5 16 V7 Q5 5 7 5 Z M7.5 7.5 H15.5 V15.5 H7.5 Z M24 5 H33 Q35 5 35 7 V16 Q35 18 33 18 H24 Q22 18 22 16 V7 Q22 5 24 5 Z M24.5 7.5 H32.5 V15.5 H24.5 Z M7 22 H16 Q18 22 18 24 V33 Q18 35 16 35 H7 Q5 35 5 33 V24 Q5 22 7 22 Z M7.5 24.5 H15.5 V32.5 H7.5 Z M24 22 H33 Q35 22 35 24 V33 Q35 35 33 35 H24 Q22 35 22 33 V24 Q22 22 24 22 Z M24.5 24.5 H32.5 V32.5 H24.5 Z")), "列出启动器已接入的发行版");

    // Small, locally drawn versions of product marks and supplier emblems. All share a 40 DIP viewbox.
    private static readonly JavaVendor[] JavaVendors = {
        new("temurin", "Eclipse Temurin", DrawLogo(
            ("#9152A1", "M13 5 H28 C33 5 35 10 32 14 L19 33 Q17 35 13 35 H11 C6 35 4 30 7 26 L21 7 Z"),
            ("#BC377E", "M13 5 H28 C33 5 35 10 32 14 L26 22 L15 10 Z"),
            ("#EA2B74", "M12 5 Q16 5 18 8 L33 27 C36 31 33 35 29 35 Q26 35 24 32 L8 13 C5 9 8 5 12 5 Z")),
            "Eclipse Adoptium 项目构建，使用最广泛"),
        new("zulu", "Azul Zulu", DrawLogo(
            ("#377CBD", "M6 7 H34 V10 H6 Z M6 12 H29 L26 15 H6 Z M28 12 H34 L13 31 H6 V29 Z M15 29 H34 V32 H12 Z M9 34 H34 V37 H6 Z"),
            ("#55A9EE", "M28 12 H34 L13 31 H8 Z")), "Azul 构建，提供的版本与平台较多"),
        new("liberica", "BellSoft Liberica", DrawLogo(
            ("#218DAA", "M16 5 L5 11 V28 L16 35 L10 28 V12 Z M24 5 L35 11 V28 L24 35 L30 28 V12 Z"),
            ("#49B5CB", "M20 3 L28 11 V29 L20 37 L12 29 V11 Z")), "BellSoft 构建的 OpenJDK"),
        new("corretto", "Amazon Corretto", DrawLogo(
            ("#967452", "F0 M9 13 H28 V15 H31 C37 15 37 25 30 25 H27 Q24 29 19 29 Q9 29 9 21 Z M13 16 V21 Q13 26 19 26 Q25 26 25 21 V16 Z M28 18 V22 H31 A2 2 0 0 0 31 18 Z M16 5 Q12 9 17 11 L19 9 Q16 8 18 5 Z M23 4 Q19 8 24 11 L26 9 Q23 7 25 4 Z"),
            ("#E58C24", "M6 30 C12 34 23 35.5 33 30 L32 28.5 L37 28.5 L36 33.5 L34.5 32 C25 39 12 38 5 32 Z")),
            "Amazon 构建并长期维护"),
        new("microsoft", "Microsoft Build of OpenJDK", DrawLogo(
            ("#F25022", "M6 6 H19 V19 H6 Z"), ("#7FBA00", "M21 6 H34 V19 H21 Z"),
            ("#00A4EF", "M6 21 H19 V34 H6 Z"), ("#FFB900", "M21 21 H34 V34 H21 Z")), "微软构建的 OpenJDK"),
        new("semeru", "IBM Semeru", DrawLogo(
            ("#4587D4", "M3 10 H11 V12 H3 Z M3 14 H11 V16 H3 Z M6 18 H8 V20 H6 Z M6 22 H8 V24 H6 Z M3 26 H11 V28 H3 Z M3 30 H11 V32 H3 Z M13 10 H21 L24 12 H13 Z M14 14 H17 V16 H14 Z M20 14 H24 V16 H20 Z M14 18 H23 L21 20 H14 Z M14 22 H23 L25 24 H14 Z M14 26 H17 V28 H14 Z M21 26 H25 V28 H21 Z M13 30 H24 L21 32 H13 Z M26 10 H29 L31 12 H26 Z M35 10 H38 V12 H33 Z M26 14 H31 L32 16 H26 Z M33 14 H38 V16 H32 Z M26 18 H29 V20 H26 Z M30 18 H34 L32 20 Z M35 18 H38 V20 H35 Z M26 22 H29 V24 H26 Z M35 22 H38 V24 H35 Z M26 26 H29 V28 H26 Z M35 26 H38 V28 H35 Z M26 30 H29 V32 H26 Z M35 30 H38 V32 H35 Z")),
            "IBM 构建，使用 OpenJ9 虚拟机"),
        new("graalvm", "GraalVM", DrawLogo(
            ("#4B8FCA", "M34 24 L27 24 L23 17 L17 19 L13 26 L6 28 L8 32 H19 L24 29 L29 32 H37 L34 29 L30 27 Z"),
            ("#E5843E", "M23 19 L18 5 L23 8 L28 17 Z M26 19 L27 6 L31 10 L30 22 Z M30 22 L35 24 L32 27 L27 25 Z")), "Oracle GraalVM"),
        new("dragonwell", "Alibaba Dragonwell", DrawLogo(
            ("#D64D3F", "F0 M29 7 L30 4 L33 9 L37 12 L34 16 L29 16 L31 12 L25 14 L25 19 L30 22 C36 28 28 37 20 37 C11 37 5 31 5 23 C5 15 10 9 19 8 L15 12 C9 15 8 19 9 24 C10 30 16 33 21 32 C26 31 29 27 25 24 L20 20 L18 15 L21 10 Z M28 10 L30 11 L28 12 Z")),
            "阿里巴巴构建的 OpenJDK"),
        new("kona", "Tencent Kona", DrawLogo(
            ("#448CD0", "M22 5 C29 4 35 10 35 16 C35 25 29 33 21 36 C16 38 10 36 7 32 C12 30 14 25 15 20 C16 12 18 8 22 5 Z"),
            ("#2BA597", "M18 5 C13 9 12 14 11 19 C10 25 8 27 5 29 C3 24 5 17 8 12 Q13 5 18 5 Z")), "腾讯构建的 OpenJDK"),
        new("sapmachine", "SAP SapMachine", DrawLogo(
            ("#197CB7", "M3 10 H38 L24 30 H3 Z"),
            ("#FFFFFF", "F0 M12 14 Q8 14 8 17 Q8 19 11 20 Q13 21 13 22 Q13 24 9 22 L8 24 Q13 27 15 22 Q16 19 12 18 Q9 17 11 16 L14 17 L15 15 Z M17 25 H14 L18 14 H21 L24 25 H21 L20.4 23 H17.6 Z M18.3 21 H20 L19.1 17.5 Z M24 14 H27 Q30 14 30 18 Q30 22 26 22 V25 H24 Z M26 16 V20 Q28 20 28 18 Q28 16 26 16 Z")), "SAP 构建的 OpenJDK"),
        new("jetbrains", "JetBrains Runtime", DrawLogo(
            ("#F46462", "M5 7 L24 4 L37 17 L24 29 L5 19 Z"), ("#E75AA6", "M5 14 L31 19 L36 31 L22 36 L5 27 Z"),
            ("#65A4EB", "M31 7 L37 17 L36 31 L27 34 L22 19 Z"), ("#18181C", "M9 9 H31 V31 H9 Z"),
            ("#FFFFFF", "F0 M14 14 H17 V21 Q17 24 13 24 H12 V22 H13 Q14 22 14 21 Z M19 14 H24 Q27 14 27 17 Q27 19 25 19.5 Q28 20 28 22 Q28 24 24 24 H19 Z M21 16 V18 H24 Q25 18 25 17 Q25 16 24 16 Z M21 20 V22 H24 Q26 22 26 21 Q26 20 24 20 Z M12 27 H21 V28.5 H12 Z")), "JetBrains 构建的运行时"),
        new("red hat", "Red Hat OpenJDK", DrawLogo(
            ("#D83737", "M10 19 L13 12 Q14 9 17 11 Q20 14 24 10 Q27 9 29 13 L32 22 C27 26 15 25 9 22 Z M6 22 C11 26 24 28 32 24 C38 22 40 26 35 30 C28 36 10 34 5 28 Q1 24 6 22 Z")), "Red Hat 构建的 OpenJDK"),
        new("adoptopenjdk", "AdoptOpenJDK", DrawLogo(
            ("#4D82C4", "M5 10 H13 V18 H5 Z M16 10 H24 V18 H16 Z M27 10 H35 V18 H27 Z M5 22 H13 V30 H5 Z M16 22 H24 V30 H16 Z M27 22 H35 V30 H27 Z")), "Temurin 的前身"),
        new("oracle", "Oracle Java", DrawLogo(
            ("#E24A42", "F0 M13 10 H27 A10 10 0 0 1 27 30 H13 A10 10 0 0 1 13 10 Z M13 14 A6 6 0 0 0 13 26 H27 A6 6 0 0 0 27 14 Z")), "Oracle 官方 Java"),
        new("openlogic", "OpenLogic", DrawLogo(
            ("#F17837", "M5 15 L20 6 V13 L11 18 V25 L5 29 Z M20 27 L29 22 V15 L35 11 V25 L20 34 Z"),
            ("#DD521F", "M11 31 L17 28 L20 34 L14 37 Z M20 6 L26 3 L32 7 L26 11 Z")), ""),
        new("ojdkbuild", "OJDKBuild", DrawLogo(
            ("#386D9F", "F0 M5 11 L20 4 L35 11 V29 L20 36 L5 29 Z M10 14 V25 L20 30 L30 25 V14 L20 9 Z"),
            ("#62ADDD", "M11 16 L20 20 L29 16 V20 L20 24 L11 20 Z M18 24 H22 V31 H18 Z")), ""),
        new("bisheng", "BiSheng JDK", DrawLogo(
            ("#CF3A39", "M6 9 H19 V32 L6 36 Z M21 9 H34 V36 L21 32 Z M6 4 H34 V7 H6 Z"),
            ("#FFB070", "M9 14 H16 V16 H9 Z M9 21 H16 V23 H9 Z M24 14 H31 V16 H24 Z M24 21 H31 V23 H24 Z")), ""),
        new("trava", "Trava OpenJDK", DrawLogo(
            ("#379360", "M6 31 C5 18 9 10 24 4 C23 16 16 26 6 31 Z M12 33 C18 21 27 16 36 15 C35 28 25 35 12 33 Z"),
            ("#83C56A", "M6 35 C12 27 19 19 23 12 L26 13 C21 24 14 31 9 37 Z")), ""),
        new("mandrel", "Mandrel", DrawLogo(
            ("#DD4444", "M5 31 L8 9 H15 L20 20 L25 9 H32 L35 31 H29 L27 18 L20 29 L13 18 L11 31 Z"),
            ("#F78D6F", "M5 34 H35 V37 H5 Z")), ""),
        new("gluon", "Gluon GraalVM", DrawLogo(
            ("#3F88D5", "M6 12 L17 6 L27 12 V16 L17 10 L10 14 V24 L17 28 L23 24 V20 H17 V16 H27 V26 L17 32 L6 26 Z"),
            ("#46C9AE", "M29 20 L35 17 V28 L23 35 L19 33 L31 26 V22 Z")), ""),
        new("eliya", "Eliya JDK", DrawLogo(
            ("#6F63C8", "M8 7 H30 V12 H13 V18 H26 V23 H13 V29 H30 V34 H8 Z"),
            ("#AF9AF3", "M32 4 L34 8 L38 10 L34 12 L32 16 L30 12 L26 10 L30 8 Z")), ""),
        new("eclipse openj9", "Eclipse OpenJ9", DrawLogo(
            ("#E98732", "M19 4 A16 16 0 1 0 19 36 A14 14 0 1 1 19 4 Z"),
            ("#635080", "M14 12 H31 A12 12 0 0 1 31 16 H12 Z M11 18 H34 V22 H11 Z M12 24 H33 A12 12 0 0 1 30 28 H15 Z")), ""),
    };

    /// <summary>Resolves download catalogue keys and detected VM distribution names to the same vector marks.</summary>
    internal static JavaVendor VendorOf(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return GenericVendor;
        string text = identity.ToLowerInvariant();
        (string key, string? name) = text switch
        {
            "aoj" => ("adoptopenjdk", null),
            "aoj_openj9" => ("adoptopenjdk", "AdoptOpenJDK OpenJ9"),
            "semeru_certified" or "ibm semeru certified" => ("semeru", "IBM Semeru Certified"),
            "sap_machine" => ("sapmachine", null),
            "redhat" => ("red hat", null),
            "oracle_open_jdk" or "oracle openjdk" => ("oracle", "Oracle OpenJDK"),
            "ojdk_build" => ("ojdkbuild", null),
            "liberica_native" or "bellsoft liberica native image kit" => ("liberica", "BellSoft Liberica Native Image Kit"),
            "gluon_graalvm" => ("gluon", null),
            "gluon graalvm" => ("gluon", null),
            "graalvm_community" => ("graalvm", "GraalVM Community"),
            "zulu_prime" or "azul zulu prime" => ("zulu", "Azul Zulu Prime"),
            _ => (text, null)
        };
        foreach (JavaVendor vendor in JavaVendors)
            if (key == vendor.Key || text.Equals(vendor.Name, StringComparison.OrdinalIgnoreCase))
                return name is null ? vendor : vendor with { Name = name };
        foreach (JavaVendor vendor in JavaVendors)
            if (text.Contains(vendor.Key, StringComparison.Ordinal)) return vendor;
        if (text.Contains("adoptium", StringComparison.Ordinal)) return JavaVendors[0];
        if (text.Contains("azul", StringComparison.Ordinal)) return JavaVendors[1];
        if (text.Contains("bellsoft", StringComparison.Ordinal)) return JavaVendors[2];
        if (text.Contains("amazon", StringComparison.Ordinal)) return JavaVendors[3];
        return GenericVendor with { Name = identity };
    }

    private static DrawingImage DrawLogo(params (string Color, string Path)[] layers)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null,
            new RectangleGeometry(new Rect(0, 0, MarkUnit, MarkUnit))));
        foreach (var layer in layers)
            drawing.Children.Add(new GeometryDrawing(GlassCommandChrome.Brush(layer.Color), null, Geometry.Parse(layer.Path)));
        var logo = new DrawingImage(drawing);
        logo.Freeze();
        return logo;
    }

    internal static int? JavaMajor(Version? version) => version is null ? null : version.Major == 1 && version.Minor > 0 ? version.Minor : version.Major;

    internal static string MinecraftRange(int? major) => major switch
    {
        8 => "Minecraft 1.16.5 及更早",
        16 => "Minecraft 1.17",
        17 => "Minecraft 1.18 – 1.20.4",
        21 => "Minecraft 1.20.5 及以上",
        _ => ""
    };

    internal static string ArchitectureLabel(string? architecture) => architecture?.ToLowerInvariant() switch
    {
        "x86" or "i386" or "i686" or "x32" => "x86 · 32 位",
        "x64" or "amd64" or "x86_64" => "x64",
        "arm64" or "aarch64" => "ARM64",
        null or "" => "",
        _ => architecture!
    };

    internal static bool Is32Bit(string? architecture) => architecture?.ToLowerInvariant() is "x86" or "i386" or "i686" or "x32";

}
