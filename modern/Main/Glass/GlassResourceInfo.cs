// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCLCS;

namespace PCL;

/// <summary>
/// Read-only project facts for the resource detail header: a label/value list (supported versions, loaders, source,
/// downloads, last update) with tags as small chips. Layout idea after BlockHelm's mod info card; independent
/// implementation over the original <see cref="ResourceProject"/> data.
/// </summary>
internal sealed class GlassResourceInfo : Grid
{
    private ResourceProject? _shown;

    internal GlassResourceInfo()
    {
        Margin = new Thickness(0, 4, 0, 14);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        ColumnDefinitions.Add(new ColumnDefinition());
    }

    internal void Show(ResourceProject project)
    {
        if (ReferenceEquals(_shown, project)) return;
        _shown = project;
        Children.Clear(); RowDefinitions.Clear();
        // The project's own summary, as a paragraph above the facts.
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var summary = new TextBlock { Text = project.Description!.Trim(), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = 20 };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
            Grid.SetColumnSpan(summary, 2); Children.Add(summary);
        }
        Add("支持版本", Text(VersionRange(project.KnownGameVersions)));
        string loaders = Loaders(project.ModLoaders);
        if (loaders.Length > 0) Add("加载器", Text(loaders));
        Add("来源", SourceLink(project));
        if (project.DownloadCount > 0) Add("下载次数", Text(Count(project.DownloadCount)));
        if (project.LastUpdate > DateTime.MinValue.AddYears(1)) Add("更新时间", Text(project.LastUpdate.ToLocalTime().ToString("yyyy-MM-dd")));
        if (project.Tags is { Count: > 0 } tags) Add("标签", Chips(tags));
    }

    private void Add(string label, FrameworkElement value)
    {
        int row = RowDefinitions.Count;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        Grid.SetRow(caption, row); Children.Add(caption);
        value.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(value, row); Grid.SetColumn(value, 1); Children.Add(value);
    }

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        return block;
    }

    // The source opens the project's page, like the original "转到 …" action.
    private static FrameworkElement SourceLink(ResourceProject project)
    {
        string name = project.Platform.ToString();
        var link = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextDecorations = TextDecorations.Underline };
        link.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        if (string.IsNullOrWhiteSpace(project.Website)) { link.TextDecorations = null; return link; }
        link.Cursor = Cursors.Hand; link.ToolTip = project.Website;
        link.MouseLeftButtonUp += (_, _) => ModBase.OpenWebsite(project.Website!);
        return link;
    }

    private static FrameworkElement Chips(IEnumerable<string> tags)
    {
        var panel = new WrapPanel();
        foreach (string tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Take(8))
        {
            var text = new TextBlock { Text = tag, FontSize = 12 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
            var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 4), Child = text };
            chip.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSelected");
            panel.Children.Add(chip);
        }
        return panel;
    }

    private static string Loaders(ModLoaders loaders)
    {
        var names = new List<string>();
        if (loaders.HasFlag(ModLoaders.Forge)) names.Add("Forge");
        if (loaders.HasFlag(ModLoaders.NeoForge)) names.Add("NeoForge");
        if (loaders.HasFlag(ModLoaders.Fabric)) names.Add("Fabric");
        if (loaders.HasFlag(ModLoaders.LiteLoader)) names.Add("LiteLoader");
        return string.Join(" / ", names);
    }

    // Release versions only (snapshots would make the range meaningless), oldest to newest.
    private static string VersionRange(IReadOnlyCollection<string>? versions)
    {
        var releases = (versions ?? Array.Empty<string>())
            .Select(value => (Text: value, Parsed: Version.TryParse(value, out Version? parsed) ? parsed : null))
            .Where(item => item.Parsed is not null).OrderBy(item => item.Parsed).ToList();
        if (releases.Count == 0) return versions is { Count: > 0 } ? string.Join(", ", versions.Take(3)) : "未知";
        if (releases.Count == 1) return releases[0].Text;
        return $"{releases[0].Text} – {releases[^1].Text}（{releases.Count} 个版本）";
    }

    private static string Count(int count) => count switch
    {
        >= 100_000_000 => $"{count / 100_000_000d:0.##} 亿",
        >= 10_000 => $"{count / 10_000d:0.#} 万",
        _ => count.ToString("N0"),
    };
}
