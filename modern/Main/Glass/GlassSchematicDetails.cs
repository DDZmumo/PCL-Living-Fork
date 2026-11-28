// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using SchematicKit;

namespace PCL;

/// <summary>
/// "投影详情": the file at the top, the author's description with its links pulled out, the headline figures as tiles,
/// then every remaining property as a labelled row. Fields the file does not carry are left out.
/// </summary>
internal sealed partial class GlassSchematicDetails : StackPanel, IGlassDialogContent
{
    public double PreferredWidth => 600;

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    internal GlassSchematicDetails(SchematicSession session, InstanceResourceEntry entry)
    {
        SchematicMetadata metadata = session.Metadata;
        string fileName = entry.Name ?? "";
        string title = System.IO.Path.GetFileNameWithoutExtension(fileName);
        Children.Add(Header(title, System.IO.Path.GetExtension(fileName).TrimStart('.'), FormatName(metadata.Format), entry.Length));

        if (Description(metadata.Description) is { } description) Children.Add(description);

        var figures = new List<(string Value, string Caption)>();
        if (metadata.EnclosingSize is { Length: > 0 } size) figures.Add((size, "尺寸"));
        if (metadata.TotalBlocks is { } blocks) figures.Add((blocks.ToString("N0", CultureInfo.CurrentCulture), "方块"));
        if (metadata.TotalVolume is { } volume) figures.Add((volume.ToString("N0", CultureInfo.CurrentCulture), "体积"));
        if (metadata.RegionCount is { } regions) figures.Add((regions.ToString(CultureInfo.CurrentCulture), "区域"));
        if (figures.Count > 0) Children.Add(Figures(figures));

        var rows = new List<(string Label, string Value)>();
        if (metadata.Author is { Length: > 0 } author) rows.Add(("作者", author));
        if (metadata.Name is { Length: > 0 } name && !string.Equals(name.Trim(), title, StringComparison.OrdinalIgnoreCase)) rows.Add(("原始名称", name.Trim()));
        if (metadata.Version is { } version) rows.Add(("原理图版本", version.ToString(CultureInfo.InvariantCulture)));
        if (metadata.DataVersion is { } dataVersion) rows.Add(("数据版本", dataVersion.ToString(CultureInfo.InvariantCulture)));
        if (metadata.CreatedTime is { } created) rows.Add(("创建时间", created.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)));
        if (metadata.ModifiedTime is { } modified) rows.Add(("修改时间", modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)));
        if (entry.FullPath is { Length: > 0 } path) rows.Add(("文件位置", path));
        if (rows.Count > 0) Children.Add(Properties(rows));
    }

    private static string FormatName(SchematicFormat format) => format switch
    {
        SchematicFormat.Litematic => "Litematica",
        SchematicFormat.Sponge => "Sponge Schematic",
        SchematicFormat.Structure => "原版结构",
        SchematicFormat.Mcedit => "MCEdit Schematic",
        _ => "未知格式"
    };

    private static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / 1024d / 1024d:0.##} MB"
    };

    // A format badge, the file name as the headline and "format · size" under it.
    private static FrameworkElement Header(string title, string extension, string format, long length)
    {
        var badgeText = Text(extension.Length > 0 ? extension.ToUpperInvariant() : "?", 11.5, "GlassPresentationAccent", FontWeights.Bold);
        badgeText.HorizontalAlignment = HorizontalAlignment.Center;
        badgeText.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Child = badgeText, Margin = new Thickness(0, 0, 14, 0),
            BorderThickness = new Thickness(1) };
        badge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        badge.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        DockPanel.SetDock(badge, Dock.Left);

        var name = Text(title, 18, "GlassPresentationText", FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = title;
        string detail = length > 0 ? $"{format} · {FileSize(length)}" : format;
        var sub = Text(detail, 12.5, "GlassPresentationSecondary");
        sub.Margin = new Thickness(0, 3, 0, 0);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, sub } };
        return new DockPanel { Children = { badge, labels }, Margin = new Thickness(0, 0, 0, 16) };
    }

    // The author's words, with any links taken out of the prose and shown as tidy, clickable lines below it.
    private static FrameworkElement? Description(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] links = UrlPattern().Matches(raw).Select(match => match.Value.TrimEnd('.', ',', ')', '，', '。')).Distinct().ToArray();
        string prose = UrlPattern().Replace(raw, "");
        prose = Regex.Replace(prose, @"[ \t]+\n", "\n");
        prose = Regex.Replace(prose, @"\n{3,}", "\n\n").Trim();
        var panel = new StackPanel();
        if (prose.Length > 0) {
            var text = Text(prose, 13.5, "GlassPresentationText");
            text.TextWrapping = TextWrapping.Wrap;
            text.LineHeight = 21;
            panel.Children.Add(text);
        }
        foreach (string link in links.Take(4)) panel.Children.Add(Link(link, panel.Children.Count > 0));
        if (panel.Children.Count == 0) return null;
        var card = new Border { Child = panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 12), Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(.7) };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return card;
    }

    private static TextBlock Link(string url, bool spaced)
    {
        string shown = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) {
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            string path = uri.AbsolutePath.TrimEnd('/');
            shown = path.Length > 34 ? host + path[..34] + "…" : host + path;
        }
        var hyperlink = new Hyperlink(new Run("↗ " + shown)) { TextDecorations = null, ToolTip = url };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationAccent");
        hyperlink.Click += (_, _) => ModBase.OpenWebsite(url);
        AutomationProperties.SetName(hyperlink, "打开链接 " + url);
        var line = new TextBlock(hyperlink) { FontSize = 13, Margin = new Thickness(0, spaced ? 8 : 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        return line;
    }

    // The figures people look for first, as a row of tiles.
    private static FrameworkElement Figures(IReadOnlyList<(string Value, string Caption)> figures)
    {
        var grid = new UniformGrid { Rows = 1, Columns = figures.Count, Margin = new Thickness(0, 0, -8, 12) };
        foreach (var (value, caption) in figures) {
            var number = Text(value, 18, "GlassPresentationText", FontWeights.SemiBold);
            number.TextTrimming = TextTrimming.CharacterEllipsis;
            number.ToolTip = value;
            var label = Text(caption, 12, "GlassPresentationSecondary");
            label.Margin = new Thickness(0, 2, 0, 0);
            var tile = new Border { Child = new StackPanel { Children = { number, label } }, CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 12, 10), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(.7) };
            tile.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
            tile.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
            grid.Children.Add(tile);
        }
        return grid;
    }

    // Everything else as label / value rows divided by hairlines.
    private static FrameworkElement Properties(IReadOnlyList<(string Label, string Value)> rows)
    {
        var list = new StackPanel();
        for (int index = 0; index < rows.Count; index++) {
            var (label, value) = rows[index];
            var key = Text(label, 13, "GlassPresentationSecondary");
            key.Width = 92;
            key.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(key, Dock.Left);
            var shown = Text(value, 13, "GlassPresentationText");
            shown.TextWrapping = TextWrapping.Wrap;
            shown.ToolTip = value;
            var row = new DockPanel { Children = { key, shown }, Margin = new Thickness(0, 8, 0, 8) };
            if (index > 0) {
                var line = new Border { Height = 1, Opacity = .6 };
                line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
                list.Children.Add(line);
            }
            list.Children.Add(row);
        }
        var card = new Border { Child = list, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 2, 14, 2), BorderThickness = new Thickness(.7) };
        card.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return card;
    }

    private static TextBlock Text(string value, double size, string brush, FontWeight? weight = null)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return text;
    }
}
