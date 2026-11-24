// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    /// <summary>
    /// Settings → Java. Installed runtimes are grouped by major version (newest first, with the Minecraft versions each
    /// major serves). Every row leads with its distribution's mark, then the distribution, the exact version and tags for
    /// package type, architecture (32-bit stands out), VM and launcher-managed installs. Rows, filter, status and the
    /// refresh/import buttons are read from the original page; 下载 Java opens the catalogue as its own detail page.
    /// </summary>
    private Node ProjectJavaManagement(PageSetupLaunch page)
    {
        var view = new JavaManagementView(this, page);
        var node = new Node(new ScrollEdgeFade(view.Root));
        TrackBackToTop(view.Root, node);
        node.Pull = view.Pull;
        node.Cleanup.Add(view.Dispose);
        return node;
    }

    /// <summary>
    /// Settings → Java → 下载 Java (a detail page that keeps the settings sidebar and the back entry). The original
    /// selectors become version tiles, distribution tiles and tag groups; the catalogue is searched again shortly after
    /// any of them changes. Searching, results and installing go through the original panel.
    /// </summary>
    private Node ProjectJavaDownload(PageSetupLaunch page)
    {
        var view = new JavaDownloadView(this, page);
        var node = new Node(new ScrollEdgeFade(view.Root));
        TrackBackToTop(view.Root, node);
        node.Pull = view.Pull;
        node.Cleanup.Add(view.Dispose);
        return node;
    }

    /// <summary>Instance → Java: the four selection modes as option tiles, then the editor the chosen mode needs.</summary>
    private Node ProjectInstanceJava(PageInstanceSetup page)
    {
        var view = new InstanceJavaView(this, page);
        var node = new Node(new ScrollEdgeFade(view.Root));
        node.Pull = view.Pull;
        return node;
    }

    // ---------- Distribution marks ----------

    private const double MarkUnit = 40;
    private sealed record JavaVendor(string Key, string Name, DrawingImage Logo, string Description);

    private static readonly JavaVendor GenericVendor = new("", "Java", DrawLogo(
        ("#FFFFFF", "F0 M8 15 H28 V17 H31 A5 5 0 0 1 31 27 H26 Q23 32 18 32 Q8 32 8 22 Z M11 18 V22 Q11 29 18 29 Q25 29 25 22 V18 Z M28 20 V24 H31 A2 2 0 0 0 31 20 Z M15 7 C11 10 14 13 17 13 L18 11 Q15 10 17 7 Z M23 5 C18 8 21 12 24 13 L25 11 Q22 9 25 6 Z M7 34 H32 V36 H7 Z")), "");
    private static readonly JavaVendor AllVendors = new("", "全部可下载", DrawLogo(
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
    private static JavaVendor VendorOf(string? identity)
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

    private FrameworkElement VendorMark(JavaVendor vendor, double size)
    {
        FrameworkElement mark;
        if (vendor.Key.Length == 0 || SystemParameters.HighContrast)
        {
            var glyph = new Border { OpacityMask = new ImageBrush(vendor.Logo) { Stretch = Stretch.Uniform } };
            glyph.SetResourceReference(Border.BackgroundProperty, "GlassPresentationText");
            mark = glyph;
        }
        else mark = new Image { Source = vendor.Logo, Stretch = Stretch.Uniform };
        mark.Width = size;
        mark.Height = size;
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.IsHitTestVisible = false;
        AutomationProperties.SetName(mark, vendor.Name);
        return mark;
    }

    /// <summary>The slot a distribution mark would take, holding a plus: for the "import an existing Java" command.</summary>
    private FrameworkElement JavaImportMark(double size)
    {
        var plus = new Path { Data = Geometry.Parse("M12,6 V18 M6,12 H18"), Stretch = Stretch.Uniform, Width = size * .42, Height = size * .42,
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        plus.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        var frame = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .28), BorderThickness = new Thickness(1.2),
            Child = plus, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        frame.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return frame;
    }

    // ---------- Shared pieces ----------

    private static int? JavaMajor(Version? version) => version is null ? null : version.Major == 1 && version.Minor > 0 ? version.Minor : version.Major;

    private static string MinecraftRange(int? major) => major switch
    {
        8 => "Minecraft 1.16.5 及更早",
        16 => "Minecraft 1.17",
        17 => "Minecraft 1.18 – 1.20.4",
        21 => "Minecraft 1.20.5 及以上",
        _ => ""
    };

    private static string ArchitectureLabel(string? architecture) => architecture?.ToLowerInvariant() switch
    {
        "x86" or "i386" or "i686" or "x32" => "x86 · 32 位",
        "x64" or "amd64" or "x86_64" => "x64",
        "arm64" or "aarch64" => "ARM64",
        null or "" => "",
        _ => architecture!
    };

    private static bool Is32Bit(string? architecture) => architecture?.ToLowerInvariant() is "x86" or "i386" or "i686" or "x32";

    private enum TagTone { Neutral, Accent, Warn }

    private FrameworkElement JavaTag(string text, TagTone tone = TagTone.Neutral)
    {
        var tag = new Grid { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { CornerRadius = new CornerRadius(6), Opacity = tone == TagTone.Neutral ? .08 : .15 };
        fill.SetResourceReference(Border.BackgroundProperty, tone switch { TagTone.Accent => "GlassPresentationAccent", TagTone.Warn => "GlassPresentationWarnText", _ => "GlassPresentationText" });
        var label = PlainText(text); label.FontSize = 11.5; label.Margin = new Thickness(7, 1, 7, 2);
        label.SetResourceReference(TextBlock.ForegroundProperty, tone switch { TagTone.Accent => "GlassPresentationAccent", TagTone.Warn => "GlassPresentationWarnText", _ => "GlassPresentationSecondary" });
        tag.Children.Add(fill); tag.Children.Add(label);
        return tag;
    }

    private static GlassSurface JavaPlate(UIElement child)
    {
        GlassSurface plate = Plate(child, new Thickness(0, 0, 0, 14));
        plate.CornerRadius = new CornerRadius(16);
        plate.Padding = new Thickness(20, 16, 20, 16);
        return plate;
    }

    private TextBlock JavaHeading(string text)
    {
        var heading = PlainText(text); heading.FontSize = 16; heading.FontWeight = FontWeights.SemiBold; heading.VerticalAlignment = VerticalAlignment.Center;
        return heading;
    }

    private TextBlock JavaNote(string text, double size = 12.5)
    {
        var note = PlainText(text); note.FontSize = size; note.TextWrapping = TextWrapping.Wrap;
        note.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        return note;
    }

    /// <summary>A section title with a one-line explanation under it.</summary>
    private StackPanel JavaSection(string title, string note)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(JavaHeading(title));
        if (note.Length > 0)
        {
            TextBlock text = JavaNote(note, 12); text.Margin = new Thickness(0, 3, 0, 0);
            header.Children.Add(text);
        }
        return header;
    }

    private Button JavaIconAction(string data, string tip, Action invoke)
    {
        var icon = new Path { Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = 14, Height = 14 };
        icon.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
        var button = new Button { Content = icon, Style = Style("GlassPresentationButton"), Width = 34, Height = 32, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => { if (Live(button)) { invoke(); Refresh(); } };
        return button;
    }

    private Button JavaButton(string text, Action invoke, bool accent = false)
    {
        var button = new Button { Content = text, Style = Style(accent ? "GlassPresentationAccentButton" : "GlassPresentationButton"), MinWidth = 72, Height = 32,
            Padding = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => { if (Live(button)) { invoke(); Refresh(); } };
        return button;
    }

    /// <summary>A selectable tag: accent-filled when chosen, field-coloured otherwise.</summary>
    private Button JavaChoice(string text, Action invoke)
    {
        var surface = new FrameworkElementFactory(typeof(Border), "Surface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        surface.SetValue(Border.PaddingProperty, new Thickness(13, 4, 13, 5));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(FrameworkElement.StyleProperty, null);
        label.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        surface.AppendChild(label);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .86, "Surface"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45));
        template.Triggers.Add(hover); template.Triggers.Add(disabled);
        var button = new Button { Content = text, Template = template, FontSize = 13, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 8) };
        button.SetResourceReference(Control.FontFamilyProperty, "GlassPresentationFont");
        SetChoiceChosen(button, false);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => { if (Live(button)) { invoke(); Refresh(); } };
        return button;
    }

    private static void SetChoiceChosen(Button button, bool chosen)
    {
        if (Equals(button.Tag, chosen)) return;
        button.Tag = chosen;
        button.SetResourceReference(Control.BackgroundProperty, chosen ? "GlassPresentationAccent" : "GlassPresentationField");
        button.SetResourceReference(Control.BorderBrushProperty, chosen ? "GlassPresentationAccentStroke" : "GlassPresentationEdge");
        button.SetResourceReference(Control.ForegroundProperty, chosen ? "GlassPresentationOnAccent" : "GlassPresentationText");
    }

    /// <summary>A selectable card (version, distribution, selection mode): accent rim and tint when chosen.</summary>
    private Button JavaTile(UIElement content, string name, Action invoke)
    {
        var tile = new Button { Content = content, Template = (ControlTemplate)_root.FindResource("GlassPresentationPlainButtonTemplate"), Padding = new Thickness(14, 12, 14, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1.2), Cursor = Cursors.Hand };
        tile.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        SetTileChosen(tile, false);
        AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) => { if (Live(tile)) { invoke(); Refresh(); } };
        return tile;
    }

    private static void SetTileChosen(Button tile, bool chosen)
    {
        if (Equals(tile.Tag, chosen)) return;
        tile.Tag = chosen;
        tile.SetResourceReference(Control.BackgroundProperty, chosen ? "GlassPresentationSelected" : "GlassPresentationField");
        tile.SetResourceReference(Control.BorderBrushProperty, chosen ? "GlassPresentationAccent" : "GlassPresentationEdge");
    }

    private ContentControl JavaSlot(Thickness margin) => new() { Focusable = false, IsTabStop = false, Margin = margin, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private void FillSlot(ContentControl slot, DependencyObject source)
    {
        UIElement? projected = Project(source);
        if (projected is FrameworkElement element) element.Margin = new Thickness(0);
        if (!ReferenceEquals(slot.Content, projected)) slot.Content = projected;
    }

    private static Border JavaDivider(double indent = 56)
    {
        var line = new Border { Height = 1, Margin = new Thickness(indent, 0, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        return line;
    }

    private static Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A runtime row: mark | name + version, tags, path | actions.</summary>
    private FrameworkElement JavaRow(JavaVendor vendor, string version, IEnumerable<FrameworkElement> tags, string path, IEnumerable<Button> actions)
    {
        var row = new Grid { MinHeight = 72, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement mark = VendorMark(vendor, 40);
        mark.Margin = new Thickness(0, 0, 16, 0);
        row.Children.Add(mark);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        TextStyle(title);
        title.Inlines.Add(new System.Windows.Documents.Run(vendor.Name) { FontSize = 14.5, FontWeight = FontWeights.SemiBold });
        var versionRun = new System.Windows.Documents.Run("   " + version) { FontSize = 13 };
        versionRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "GlassPresentationSecondary");
        title.Inlines.Add(versionRun);
        text.Children.Add(title);
        var tagRow = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (FrameworkElement tag in tags) tagRow.Children.Add(tag);
        if (tagRow.Children.Count > 0) text.Children.Add(tagRow);
        TextBlock pathText = JavaNote(path, 11.5); pathText.TextWrapping = TextWrapping.NoWrap; pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        pathText.Margin = new Thickness(0, 5, 0, 0); pathText.Opacity = .85;
        text.Children.Add(pathText);
        Grid.SetColumn(text, 1); row.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (Button action in actions) buttons.Children.Add(action);
        Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
        return row;
    }

    private ScrollViewer JavaScroll(StackPanel stack)
    {
        var root = new ScrollViewer { Content = stack, Style = Style("GlassPresentationScrollViewer"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.SetIsEnabled(root, true);
        bool primed = false;
        root.Loaded += (_, _) =>
        {
            if (primed) return;
            primed = true;
            StaggeredEntrance.Play(stack.Children.Cast<UIElement>());
        };
        return root;
    }

    /// <summary>The launcher's block-curve loader with a caption, for work that has no measurable progress.</summary>
    /// <remarks>
    /// While shown it checks <c>running</c> on its own every 200 ms and hides as soon as the work ends: the shell pauses
    /// page refreshes during scrolling and transitions, and the loader must not outlive the work it stands for.
    /// </remarks>
    private sealed class JavaLoading : IDisposable
    {
        internal FrameworkElement View { get; }
        private readonly GlassPresentationSession _owner;
        private readonly Func<bool> _running;
        private readonly BlockCurveLoader _loader;
        private readonly TextBlock _caption;
        private readonly DispatcherTimer _watch;

        internal JavaLoading(GlassPresentationSession owner, double size, Func<bool> running)
        {
            _owner = owner; _running = running;
            _watch = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
            _watch.Tick += (_, _) =>
            {
                if (_running()) return;
                Show(false, _caption.Text);
                // The work just ended: bring its results in now rather than at the shell's next refresh.
                _owner.QueueRefresh();
            };
            _loader = new BlockCurveLoader { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
            _loader.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
            _caption = owner.JavaNote("", 12.5); _caption.TextAlignment = TextAlignment.Center; _caption.Margin = new Thickness(0, 12, 0, 0);
            View = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 10), Visibility = Visibility.Collapsed,
                Children = { _loader, _caption } };
        }

        internal void Show(bool running, string caption)
        {
            _caption.Text = caption;
            if (View.Visibility == Shown(running)) return;
            View.Visibility = Shown(running);
            _loader.IsActive = running;
            if (running) { _watch.Start(); StaggeredEntrance.Play(new[] { View }); }
            else _watch.Stop();
        }

        public void Dispose()
        {
            _watch.Stop();
            _loader.IsActive = false;
        }
    }

    // ---------- Settings → Java ----------

    private sealed class JavaManagementView : IDisposable
    {
        private readonly GlassPresentationSession _owner;
        private readonly PageSetupLaunch _page;
        internal ScrollViewer Root { get; }
        private readonly StackPanel _stack = new() { Margin = new Thickness(0, 6, 0, 18) };
        private readonly ContentControl _priority, _refresh, _import, _filter;
        private readonly TextBlock _count, _status;
        private readonly StackPanel _groups = new();
        private readonly JavaLoading _searching;
        private readonly Dictionary<string, bool> _jdk = new(StringComparer.OrdinalIgnoreCase);
        private List<object> _sources = new();

        internal JavaManagementView(GlassPresentationSession owner, PageSetupLaunch page)
        {
            _owner = owner; _page = page;
            Root = owner.JavaScroll(_stack);

            // ---------- Launch priority ----------
            var priority = new Grid();
            priority.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            priority.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            StackPanel priorityText = owner.JavaSection("启动优先级", "自动选择优先用兼容的 64 位 Java，同位数的运行时可在下拉列表中调整顺序。");
            priorityText.Margin = new Thickness(0, 0, 16, 0); priorityText.VerticalAlignment = VerticalAlignment.Center;
            _priority = owner.JavaSlot(new Thickness(0)); _priority.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_priority, 1);
            priority.Children.Add(priorityText); priority.Children.Add(_priority);
            _stack.Children.Add(JavaPlate(priority));

            // ---------- Installed: header, filter, status ----------
            var installed = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(owner.JavaHeading("本机 Java"));
            _count = owner.JavaNote("", 12); _count.VerticalAlignment = VerticalAlignment.Center; _count.Margin = new Thickness(10, 2, 0, 0);
            Grid.SetColumn(_count, 1); header.Children.Add(_count);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _refresh = owner.JavaSlot(new Thickness(0, 0, 8, 0)); _import = owner.JavaSlot(new Thickness(0, 0, 8, 0));
            actions.Children.Add(_refresh); actions.Children.Add(_import);
            actions.Children.Add(owner.JavaButton("下载 Java", page.ShowJavaDownloadPage, accent: true));
            Grid.SetColumn(actions, 2); header.Children.Add(actions);
            installed.Children.Add(header);
            _filter = owner.JavaSlot(new Thickness(0));
            installed.Children.Add(_filter);
            _status = owner.JavaNote(""); _status.Margin = new Thickness(2, 10, 0, 0);
            installed.Children.Add(_status);
            _searching = new JavaLoading(owner, 40, () => ModJava.JavaListRefreshWorker.Running);
            installed.Children.Add(_searching.View);
            _stack.Children.Add(JavaPlate(installed));

            // ---------- Installed: one plate per major version ----------
            _stack.Children.Add(_groups);
        }

        internal void Pull()
        {
            PageSetupLaunch page = _page;
            _owner.FillSlot(_priority, page.PanAdvanceJava);
            _owner.FillSlot(_refresh, page.BtnRefreshJavaManagement);
            _owner.FillSlot(_import, page.BtnImportJavaManagement);
            _owner.FillSlot(_filter, page.TextJavaFilter);

            // The original rows carry each Java and the filter result.
            var sources = page.PanJavaRows.Children.OfType<MyListItem>().Where(row => row.Visibility == Visibility.Visible && row.Tag is Java).Select(row => row.Tag!).ToList();
            if (!sources.SequenceEqual(_sources))
            {
                bool primed = _sources.Count > 0;
                _sources = sources;
                _groups.Children.Clear();
                foreach (var group in sources.Cast<Java>().GroupBy(java => JavaMajor(java.Version)).OrderByDescending(group => group.Key ?? -1))
                    _groups.Children.Add(GroupPlate(group.Key, group.ToList()));
                if (primed) StaggeredEntrance.Play(_groups.Children.Cast<UIElement>());
            }
            int total = page.PanJavaRows.Children.Count;
            _count.Text = total == 0 ? "" : sources.Count == total ? $"{total} 个" : $"{sources.Count} / {total} 个";
            bool searching = ModJava.JavaListRefreshWorker.Running;
            _searching.Show(searching, "正在搜索本机 Java…");
            _status.Text = page.LabJavaManagementStatus.Text;
            // The loader carries the searching caption; the status line keeps empty and no-match notices.
            _status.Visibility = searching ? Visibility.Collapsed : page.LabJavaManagementStatus.Visibility;
            _groups.Opacity = searching ? .55 : 1;
            _groups.IsHitTestVisible = !searching;
        }

        public void Dispose() => _searching.Dispose();

        private FrameworkElement GroupPlate(int? major, IReadOnlyList<Java> javas)
        {
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var title = _owner.JavaHeading(major is { } value ? "Java " + value.ToString(CultureInfo.InvariantCulture) : "版本待检测");
            header.Children.Add(title);
            string range = MinecraftRange(major);
            TextBlock note = _owner.JavaNote($"{javas.Count} 个" + (range.Length > 0 ? " · 适用于 " + range : ""), 12);
            note.VerticalAlignment = VerticalAlignment.Center; note.Margin = new Thickness(10, 2, 0, 0);
            header.Children.Add(note);
            stack.Children.Add(header);
            for (int index = 0; index < javas.Count; index++)
            {
                if (index > 0) stack.Children.Add(JavaDivider());
                stack.Children.Add(InstalledRow(javas[index]));
            }
            return JavaPlate(stack);
        }

        private FrameworkElement InstalledRow(Java java)
        {
            bool managed = JavaRuntimeDownloads.IsManaged(java);
            var tags = new List<FrameworkElement>();
            if (java.PackageType is { } packageType) tags.Add(_owner.JavaTag(packageType.ToUpperInvariant()));
            else if (IsJdk(java) is { } jdk) tags.Add(_owner.JavaTag(jdk ? "JDK" : "JRE"));
            if (ArchitectureLabel(java.Architecture) is { Length: > 0 } architecture)
                tags.Add(_owner.JavaTag(architecture, Is32Bit(java.Architecture) ? TagTone.Warn : TagTone.Neutral));
            if (!string.IsNullOrWhiteSpace(java.VirtualMachine)) tags.Add(_owner.JavaTag(java.VirtualMachine!));
            if (managed) tags.Add(_owner.JavaTag("启动器安装", TagTone.Accent));

            var actions = new List<Button> { _owner.JavaIconAction(ModBase.Logo.IconButtonOpen, "打开 Java 所在文件夹", () => ModBase.OpenExplorer(java.Folder)) };
            if (managed) actions.Add(_owner.JavaIconAction(ModBase.Logo.IconButtonDelete, "卸载此 Java", () => JavaRuntimeDownloads.StartUninstall(java)));
            FrameworkElement row = _owner.JavaRow(VendorOf(java.Distribution), java.RuntimeVersion ?? java.Version?.ToString() ?? "版本待检测", tags, java.Folder.TrimEnd('\\'), actions);
            row.ToolTip = Is32Bit(java.Architecture)
                ? java.JavaExePath + $"\n32 位 Java 的启动堆内存上限为 {Java.X86MaxHeapMiB} MiB，适用于支持 x86 的游戏版本。"
                : java.JavaExePath;
            return row;
        }

        // A JDK ships the compiler next to java.exe; cached per folder so refreshes do not touch the disk again.
        private bool? IsJdk(Java java)
        {
            if (_jdk.TryGetValue(java.Folder, out bool known)) return known;
            try { return _jdk[java.Folder] = File.Exists(System.IO.Path.Combine(java.Folder, "javac.exe")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
    }

    // ---------- Settings → Java → 下载 Java ----------

    private sealed class JavaDownloadView : IDisposable
    {
        private static readonly (string Major, string Note)[] Majors = {
            ("8", "Minecraft 1.16.5 及更早"),
            ("17", "Minecraft 1.18 – 1.20.4"),
            ("21", "Minecraft 1.20.5 及以上"),
            ("25", "最新的长期支持版")
        };
        // Prefer general-purpose runtimes common in Minecraft, followed by specialized and legacy builds.
        private static readonly string[] VendorOrder = [
            "temurin", "microsoft", "zulu", "corretto", "oracle", "liberica", "oracle_open_jdk", "redhat", "semeru",
            "graalvm_community", "graalvm", "jetbrains", "dragonwell", "kona", "sap_machine", "liberica_native", "mandrel",
            "aoj", "aoj_openj9", "ojdk_build"
        ];

        private readonly GlassPresentationSession _owner;
        private readonly PageSetupLaunch _page;
        internal ScrollViewer Root { get; }
        private readonly StackPanel _stack = new() { Margin = new Thickness(0, 6, 0, 18) };
        private readonly List<(Button Tile, string Major)> _majorTiles = new();
        private readonly UniformGrid _vendors = new() { Columns = 3, Margin = new Thickness(0, 10, 0, 0), IsEnabled = false };
        private readonly Expander _vendorPicker;
        private readonly Button _allVendors, _chooseVendor;
        private readonly TextBlock _selectedVendor;
        private readonly RotateTransform _vendorChevron = new();
        private readonly WrapPanel _architectures = new(), _types = new();
        private readonly ContentControl _major, _search, _cancel;
        private readonly TextBlock _status;
        private readonly StackPanel _results = new();
        private readonly JavaLoading _searching;
        private readonly DispatcherTimer _debounce;
        private List<object> _resultSources = new();
        private Java[] _localRuntimes = [];
        private string? _searched, _pending;
        private bool _disposed;

        internal JavaDownloadView(GlassPresentationSession owner, PageSetupLaunch page)
        {
            _owner = owner; _page = page;
            Root = owner.JavaScroll(_stack);

            // ---------- Version ----------
            var version = new StackPanel();
            version.Children.Add(owner.JavaSection("Java 版本", "不同版本的 Minecraft 需要不同的 Java，选择与游戏对应的主版本。"));
            var majors = new UniformGrid { Columns = Majors.Length };
            for (int index = 0; index < Majors.Length; index++)
            {
                (string major, string note) = Majors[index];
                var body = new StackPanel();
                var number = owner.PlainText(major); number.FontSize = 24; number.FontWeight = FontWeights.Bold;
                number.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
                var caption = owner.PlainText("Java " + major); caption.FontSize = 12.5; caption.Margin = new Thickness(0, 0, 0, 4);
                caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                TextBlock hint = owner.JavaNote(note, 12);
                body.Children.Add(number); body.Children.Add(caption); body.Children.Add(hint);
                Button tile = owner.JavaTile(body, "Java " + major, () => _page.TextJavaDownloadMajor.Text = major);
                tile.Margin = new Thickness(index == 0 ? 0 : 5, 0, index == Majors.Length - 1 ? 0 : 5, 0);
                _majorTiles.Add((tile, major));
                majors.Children.Add(tile);
            }
            version.Children.Add(majors);
            var custom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            TextBlock customLabel = owner.JavaNote("其他主版本", 13); customLabel.VerticalAlignment = VerticalAlignment.Center; customLabel.Margin = new Thickness(0, 0, 12, 0);
            custom.Children.Add(customLabel);
            _major = owner.JavaSlot(new Thickness(0));
            Grid.SetColumn(_major, 1); custom.Children.Add(_major);
            version.Children.Add(custom);
            _stack.Children.Add(JavaPlate(version));

            // ---------- Distribution ----------
            var distribution = new StackPanel();
            distribution.Children.Add(owner.JavaSection("发行版", "都是基于 OpenJDK 的构建，运行 Minecraft 没有明显差别，任选其一即可。"));
            _vendorPicker = new Expander { Name = "JavaVendorPicker", Content = _vendors, Style = owner.Style("GlassJavaVendorExpander") };
            _allVendors = owner.JavaTile(VendorTileBody(AllVendors, AllVendors.Name, owner.JavaNote(AllVendors.Description, 11.5)),
                AllVendors.Name, () => {
                    if (_page.ComboJavaDownloadDistribution.HasItems) _page.ComboJavaDownloadDistribution.SelectedIndex = 0;
                    SetVendorPickerOpen(false);
                });
            _allVendors.Margin = new Thickness(0, 0, 4, 0);
            _selectedVendor = owner.JavaNote("", 11.5);
            var chooser = VendorTileBody(GenericVendor, "选择发行版", _selectedVendor);
            chooser.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chevron = new Path { Data = Geometry.Parse("M1,1 L5,5 9,1"), Stretch = Stretch.Uniform, Width = 12, Height = 7,
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = _vendorChevron };
            chevron.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
            Grid.SetColumn(chevron, 2); chooser.Children.Add(chevron);
            _chooseVendor = owner.JavaTile(chooser, "选择发行版", () => SetVendorPickerOpen(!_vendorPicker.IsExpanded));
            _chooseVendor.Margin = new Thickness(4, 0, 0, 0);
            AutomationProperties.SetHelpText(_chooseVendor, "展开发行版列表");
            distribution.Children.Add(new UniformGrid { Columns = 2, Children = { _allVendors, _chooseVendor } });
            distribution.Children.Add(_vendorPicker);
            distribution.PreviewKeyDown += (_, args) => {
                if (args.Key != Key.Escape || !_vendorPicker.IsExpanded) return;
                args.Handled = true;
                SetVendorPickerOpen(false);
            };
            _stack.Children.Add(JavaPlate(distribution));

            // ---------- Architecture and package ----------
            var package = new StackPanel();
            package.Children.Add(owner.JavaSection("架构与包类型", "运行游戏只需要 JRE；JDK 额外包含开发工具，体积更大。"));
            package.Children.Add(FilterRow("架构", _architectures));
            package.Children.Add(FilterRow("包类型", _types));
            _stack.Children.Add(JavaPlate(package));

            // ---------- Results ----------
            var results = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel();
            heading.Children.Add(owner.JavaHeading("可下载的安装包"));
            _status = owner.JavaNote("", 12); _status.Margin = new Thickness(0, 3, 0, 0);
            heading.Children.Add(_status);
            header.Children.Add(heading);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _search = owner.JavaSlot(new Thickness(0)); _cancel = owner.JavaSlot(new Thickness(8, 0, 0, 0));
            buttons.Children.Add(_search); buttons.Children.Add(_cancel);
            Grid.SetColumn(buttons, 1); header.Children.Add(buttons);
            results.Children.Add(header);
            _searching = new JavaLoading(owner, 48, () => DownloadSearching(page));
            results.Children.Add(_searching.View);
            results.Children.Add(_results);
            _stack.Children.Add(JavaPlate(results));

            // Search again a moment after the filters settle, so several quick choices make one request.
            _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(380) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                if (_disposed || _pending is null || !_owner.Live(Root)) return;
                _searched = _pending;
                _ = _page.SearchJavaDownloadCatalogAsync(JavaRuntimeDownloads.SearchAsync);
            };
        }

        private FrameworkElement FilterRow(string label, UIElement content)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var text = _owner.PlainText(label); text.FontSize = 13; text.Margin = new Thickness(0, 5, 0, 0); text.VerticalAlignment = VerticalAlignment.Top;
            text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            row.Children.Add(text);
            Grid.SetColumn(content, 1); row.Children.Add(content);
            return row;
        }

        internal void Pull()
        {
            if (_disposed) return;
            PageSetupLaunch page = _page;
            _owner.FillSlot(_major, page.TextJavaDownloadMajor);
            _owner.FillSlot(_search, page.BtnSearchJavaDownload);
            _owner.FillSlot(_cancel, page.BtnCancelJavaDownloadSearch);
            string major = page.TextJavaDownloadMajor.Text.Trim();
            foreach ((Button tile, string value) in _majorTiles) SetTileChosen(tile, value == major);
            SyncVendors();
            SyncChoices(_architectures, page.ComboJavaDownloadArchitecture);
            SyncChoices(_types, page.ComboJavaDownloadType);
            // The cancel button exists only while a catalogue request is in flight.
            bool searching = DownloadSearching(page);
            _searching.Show(searching, "正在搜索可下载的 Java…");
            _status.Text = searching ? "" : page.LabJavaDownloadStatus.Text;
            _status.Visibility = Shown(_status.Text.Length > 0);

            var results = page.PanJavaDownloadResults.Children.OfType<MyListItem>().Where(row => row.Tag is JavaRuntimePackage).Select(row => row.Tag!).ToList();
            bool resultsChanged = !results.SequenceEqual(_resultSources);
            Java[] runtimes = results.Count == 0 ? [] : Configs.JavaList.Get()?.ToArray() ?? [];
            if (resultsChanged || !runtimes.SequenceEqual(_localRuntimes, ReferenceEqualityComparer.Instance))
            {
                _resultSources = results;
                _localRuntimes = runtimes;
                var installations = runtimes.Select(java => (Runtime: java,
                    Package: ManagedJavaRuntimeStore.GetInstalledPackage(ManagedJavaRuntimeStore.DefaultRoot, java))).ToArray();
                _results.Children.Clear();
                foreach (MyListItem source in page.PanJavaDownloadResults.Children.OfType<MyListItem>())
                {
                    if (source.Tag is not JavaRuntimePackage package) continue;
                    _results.Children.Add(_results.Children.Count == 0 ? new Border { Height = 4 } : JavaDivider());
                    string? installed = installations.Any(entry => entry.Package?.IsSameArchive(package) == true) ? "本机已有" :
                        installations.Any(entry => entry.Package is null && JavaRuntimeCatalog.MatchesLocalRuntime(package, entry.Runtime)) ? "本机同版本" : null;
                    _results.Children.Add(PackageRow(package, source, installed));
                }
                if (resultsChanged && results.Count > 0) StaggeredEntrance.Play(_results.Children.Cast<UIElement>());
            }

            // Filters changed since the last search: search again once they settle.
            bool open = page.PanJavaDownload.Visibility == Visibility.Visible && page.BtnSearchJavaDownload.IsEnabled;
            string filters = string.Join("|", major, page.ComboJavaDownloadDistribution.SelectedIndex, page.ComboJavaDownloadArchitecture.SelectedIndex, page.ComboJavaDownloadType.SelectedIndex);
            if (open && filters != _searched && filters != _pending)
            {
                _pending = filters;
                _debounce.Stop(); _debounce.Start();
            }
        }

        private void SyncVendors()
        {
            var selected = _page.ComboJavaDownloadDistribution.SelectedItem as ComboBoxItem;
            bool all = selected?.Tag is not string;
            _selectedVendor.Text = all ? "" : selected?.Content?.ToString() ?? "";
            _selectedVendor.Visibility = Shown(_selectedVendor.Text.Length > 0);
            AutomationProperties.SetName(_chooseVendor, all ? "选择发行版" : $"选择发行版，当前为 {_selectedVendor.Text}");
            SetTileChosen(_allVendors, all);
            SetTileChosen(_chooseVendor, !all);
            if (_vendorPicker.IsExpanded) SyncVendorTiles();
        }

        private void SetVendorPickerOpen(bool expanded)
        {
            if (_vendorPicker.IsExpanded == expanded) return;
            if (expanded) SyncVendorTiles();
            else if (_vendors.IsKeyboardFocusWithin) _chooseVendor.Focus();
            _vendors.IsEnabled = expanded;
            _vendorPicker.IsExpanded = expanded;
            _vendorChevron.Angle = expanded ? 180 : 0;
            AutomationProperties.SetHelpText(_chooseVendor, expanded ? "收起发行版列表" : "展开发行版列表");
        }

        private void SyncVendorTiles()
        {
            ComboBox combo = _page.ComboJavaDownloadDistribution;
            var items = combo.Items.OfType<ComboBoxItem>().Where(item => item.Tag is string).ToArray();
            if (_vendors.Tag is not ComboBoxItem[] previous || !items.SequenceEqual(previous))
            {
                _vendors.Tag = items;
                _vendors.Children.Clear();
                foreach (ComboBoxItem item in items.OrderBy(item => {
                    int position = Array.IndexOf(VendorOrder, (string)item.Tag);
                    return position < 0 ? int.MaxValue : position;
                }).ThenBy(item => item.Content?.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    JavaVendor vendor = VendorOf((string)item.Tag);
                    string name = item.Content?.ToString() ?? vendor.Name;
                    var body = VendorTileBody(vendor, name, _owner.JavaNote(vendor.Description, 11.5));
                    Button tile = _owner.JavaTile(body, name, () => combo.SelectedItem = item);
                    tile.DataContext = item;
                    tile.Margin = new Thickness(0, 0, 8, 8);
                    _vendors.Children.Add(tile);
                }
            }
            foreach (Button tile in _vendors.Children) SetTileChosen(tile, ReferenceEquals(tile.DataContext, combo.SelectedItem));
        }

        private Grid VendorTileBody(JavaVendor vendor, string name, TextBlock detail)
        {
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement mark = _owner.VendorMark(vendor, 34);
            mark.Margin = new Thickness(0, 0, 12, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            var title = _owner.PlainText(name);
            title.FontSize = 13.5;
            title.FontWeight = FontWeights.SemiBold;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.Margin = new Thickness(0, 2, 0, 0);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } };
            Grid.SetColumn(text, 1);
            body.Children.Add(mark);
            body.Children.Add(text);
            return body;
        }

        private void SyncChoices(WrapPanel panel, ComboBox combo)
        {
            string[] labels = combo.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString() ?? "").ToArray();
            string signature = string.Join("|", labels);
            if (panel.Tag as string != signature)
            {
                panel.Tag = signature;
                panel.Children.Clear();
                for (int index = 0; index < labels.Length; index++)
                {
                    int target = index;
                    string label = labels[index] is "x64" or "arm64" or "x86" ? ArchitectureLabel(labels[index]) : labels[index];
                    panel.Children.Add(_owner.JavaChoice(label, () => { if (target < combo.Items.Count) combo.SelectedIndex = target; }));
                }
                if (labels.Length == 0) panel.Children.Add(_owner.JavaNote("当前系统暂无可选项", 12.5));
            }
            for (int index = 0; index < panel.Children.Count; index++)
                if (panel.Children[index] is Button button) SetChoiceChosen(button, combo.SelectedIndex == index);
        }

        private FrameworkElement PackageRow(JavaRuntimePackage package, MyListItem source, string? installed)
        {
            JavaVendor vendor = VendorOf(package.Distribution);
            if (JavaRuntimeCatalog.Distributions.TryGetValue(package.Distribution, out string? name)) vendor = vendor with { Name = name };
            var tags = new List<FrameworkElement> { _owner.JavaTag(package.PackageType.ToUpperInvariant()) };
            if (ArchitectureLabel(package.Architecture) is { Length: > 0 } architecture)
                tags.Add(_owner.JavaTag(architecture, Is32Bit(package.Architecture) ? TagTone.Warn : TagTone.Neutral));
            tags.Add(_owner.JavaTag(package.Size > 0 ? StringUtils.FormatByteSize(package.Size) : "大小未知"));
            if (installed is not null) tags.Add(_owner.JavaTag(installed, TagTone.Accent));
            Button install = _owner.JavaButton("下载", () =>
            {
                // Same guard as the original row: only a result still on the open panel installs.
                if (ReferenceEquals(source.Parent, _page.PanJavaDownloadResults) && _page.PanJavaDownload.Visibility == Visibility.Visible)
                    JavaRuntimeDownloads.StartInstall(package);
            }, accent: true);
            install.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(install, $"下载 {vendor.Name} Java {package.Version}");
            FrameworkElement row = _owner.JavaRow(vendor, "Java " + package.Version, tags, package.FileName, new[] { install });
            row.ToolTip = package.FileName;
            return row;
        }

        // The cancel button exists only while a request is in flight. Results are added just before it is hidden, so a
        // panel that already lists packages counts as finished.
        private static bool DownloadSearching(PageSetupLaunch page) =>
            page.BtnCancelJavaDownloadSearch.Visibility == Visibility.Visible && page.PanJavaDownloadResults.Children.Count == 0;

        public void Dispose()
        {
            _disposed = true;
            _debounce.Stop();
            _searching.Dispose();
        }
    }

    // ---------- Instance → Java ----------

    private sealed class InstanceJavaView
    {
        private static readonly (string Title, string Note)[] Modes = {
            ("自动选择", "根据游戏版本和组件要求选择兼容的 Java"),
            ("指定版本范围", "在版本区间内自动挑选，适合要求特定 Java 的 Mod"),
            ("实例文件夹中的 Java", "使用整合包自带、放在实例文件夹里的 Java"),
            ("指定 Java", "从本机已有的 Java 中手动选择一个")
        };

        private readonly GlassPresentationSession _owner;
        private readonly PageInstanceSetup _page;
        internal ScrollViewer Root { get; }
        private readonly StackPanel _stack = new() { Margin = new Thickness(0, 6, 0, 18) };
        private readonly Button[] _tiles;
        private readonly Border[] _marks;
        private readonly FrameworkElement _rangeRow, _selectRow;
        private readonly ContentControl _range, _select, _hint, _manage;

        internal InstanceJavaView(GlassPresentationSession owner, PageInstanceSetup page)
        {
            _owner = owner; _page = page;
            Root = owner.JavaScroll(_stack);

            var mode = new StackPanel();
            mode.Children.Add(owner.JavaSection("这个实例使用哪个 Java", "选择方式只影响当前实例；全局的启动优先级在“设置 → Java”中调整。"));
            var grid = new UniformGrid { Columns = 2 };
            _tiles = new Button[Modes.Length];
            _marks = new Border[Modes.Length];
            for (int index = 0; index < Modes.Length; index++)
            {
                int target = index;
                var body = new Grid();
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                body.ColumnDefinitions.Add(new ColumnDefinition());
                var ring = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, "GlassPresentationAccent");
                ring.Child = dot;
                body.Children.Add(ring);
                var text = new StackPanel();
                var heading = owner.PlainText(Modes[index].Title); heading.FontSize = 14; heading.FontWeight = FontWeights.SemiBold;
                TextBlock hint = owner.JavaNote(Modes[index].Note, 12); hint.Margin = new Thickness(0, 3, 0, 0);
                text.Children.Add(heading); text.Children.Add(hint);
                Grid.SetColumn(text, 1); body.Children.Add(text);
                Button tile = owner.JavaTile(body, Modes[index].Title, () =>
                {
                    if (target < _page.ComboArgumentJava.Items.Count) _page.ComboArgumentJava.SelectedIndex = target;
                });
                tile.Margin = new Thickness(index % 2 == 0 ? 0 : 5, 0, index % 2 == 0 ? 5 : 0, 10);
                _tiles[index] = tile; _marks[index] = ring;
                grid.Children.Add(tile);
            }
            mode.Children.Add(grid);

            _range = owner.JavaSlot(new Thickness(0));
            _rangeRow = EditorRow("版本区间", "例如 [17, 22) 表示 Java 17 至 21；方括号包含、圆括号不包含", _range);
            _select = owner.JavaSlot(new Thickness(0));
            _selectRow = EditorRow("使用的 Java", "列表中显示本机已有的 Java 与其版本", _select);
            mode.Children.Add(_rangeRow); mode.Children.Add(_selectRow);
            _hint = owner.JavaSlot(new Thickness(0, 10, 0, 0));
            mode.Children.Add(_hint);
            _stack.Children.Add(JavaPlate(mode));

            _manage = owner.JavaSlot(new Thickness(0)); _manage.HorizontalAlignment = HorizontalAlignment.Left; _manage.VerticalAlignment = VerticalAlignment.Center;
            var manage = new Grid();
            manage.ColumnDefinitions.Add(new ColumnDefinition());
            manage.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel manageText = owner.JavaSection("本机 Java", "查看已安装的 Java，导入或下载新的发行版。");
            manageText.Margin = new Thickness(0); manageText.VerticalAlignment = VerticalAlignment.Center;
            manage.Children.Add(manageText);
            Grid.SetColumn(_manage, 1); manage.Children.Add(_manage);
            _stack.Children.Add(JavaPlate(manage));
        }

        private FrameworkElement EditorRow(string label, string hint, UIElement editor)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var title = _owner.PlainText(label); title.FontSize = 14;
            TextBlock note = _owner.JavaNote(hint, 12); note.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(title); text.Children.Add(note);
            row.Children.Add(text);
            Grid.SetColumn(editor, 1); row.Children.Add(editor);
            return row;
        }

        internal void Pull()
        {
            int selected = _page.ComboArgumentJava.SelectedIndex;
            for (int index = 0; index < _tiles.Length; index++)
            {
                bool chosen = index == selected;
                SetTileChosen(_tiles[index], chosen);
                _marks[index].SetResourceReference(Border.BorderBrushProperty, chosen ? "GlassPresentationAccent" : "GlassPresentationTrack");
                ((Border)_marks[index].Child).Visibility = Shown(chosen);
                _tiles[index].IsEnabled = _page.ComboArgumentJava.IsEnabled;
            }
            _rangeRow.Visibility = _page.TextArgumentJavaRange.Visibility;
            if (_rangeRow.Visibility == Visibility.Visible) _owner.FillSlot(_range, _page.TextArgumentJavaRange);
            _selectRow.Visibility = _page.PanArgumentJavaSelect.Visibility;
            if (_selectRow.Visibility == Visibility.Visible) _owner.FillSlot(_select, _page.PanArgumentJavaSelect);
            _hint.Visibility = _page.HintArgumentJava.Visibility;
            if (_hint.Visibility == Visibility.Visible) _owner.FillSlot(_hint, _page.HintArgumentJava);
            if (ManageButton() is { } manage) _owner.FillSlot(_manage, manage);
        }

        private MyButton? ManageButton() =>
            Descend(_page.CardInstanceJava).OfType<MyButton>().FirstOrDefault(button => button.Text == "管理本机 Java");

        private static IEnumerable<DependencyObject> Descend(DependencyObject root)
        {
            yield return root;
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (DependencyObject item in Descend(child)) yield return item;
        }
    }
}
