// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;
using MeloongCore;
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
    private CancellationTokenSource? _translation;

    // Material "translate" glyph (文A).
    private const string TranslateGlyph = "M12.87,15.07 L10.33,12.56 L10.36,12.53 C12.1,10.59 13.34,8.36 14.07,6 H17 V4 H10 V2 H8 V4 H1 V5.99 H12.17 " +
        "C11.5,7.92 10.44,9.75 9,11.35 C8.07,10.32 7.3,9.19 6.69,8 H4.69 C5.42,9.63 6.42,11.17 7.67,12.56 L2.58,17.58 L4,19 L9,14 " +
        "L12.11,17.11 Z M18.5,10 H16.5 L12,22 H14 L15.12,19 H19.87 L21,22 H23 Z M15.88,17 L17.5,12.67 L19.12,17 Z";

    /// <summary>Fetches the summary's Chinese translation; when set, a translate action sits under the summary.</summary>
    internal Func<CancellationToken, Task<ResourceDescriptionTranslationResult?>>? Translate { get; set; }

    internal GlassResourceInfo()
    {
        Margin = new Thickness(0, 4, 0, 14);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Unloaded += (_, _) => _translation?.Cancel();
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
            if (Translate is not null)
            {
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var translation = TranslationBlock(project);
                Grid.SetRow(translation, RowDefinitions.Count - 1); Grid.SetColumnSpan(translation, 2); Children.Add(translation);
            }
        }
        Add("支持版本", Text(VersionRange(project.KnownGameVersions)));
        string loaders = Loaders(project.ModLoaders);
        if (loaders.Length > 0) Add("加载器", Text(loaders));
        Add("来源", SourceLink(project));
        if (project.DownloadCount > 0) Add("下载次数", Text(Count(project.DownloadCount)));
        if (project.LastUpdate > DateTime.MinValue.AddYears(1)) Add("更新时间", Text(project.LastUpdate.ToLocalTime().ToString("yyyy-MM-dd")));
        if (project.Tags is { Count: > 0 } tags) Add("标签", Chips(tags));
    }

    // Under the summary: a small translate action; the translation then appears right below, no dialog. It can be folded
    // away and shown again without asking the service twice.
    private FrameworkElement TranslationBlock(ResourceProject project)
    {
        var glyph = new Path { Data = Geometry.Parse(TranslateGlyph), Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
        var label = new TextBlock { Text = "翻译简介", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var button = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, label } },
            Height = 28, MinHeight = 28, MinWidth = 0, Padding = new Thickness(10, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "获取 MCIM 提供的中文简介，显示在原文下方" };
        button.SetResourceReference(StyleProperty, "GlassPresentationButton");
        System.Windows.Automation.AutomationProperties.SetName(button, "翻译简介");

        var source = new TextBlock { Text = "中文译文 · MCIM", FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) };
        source.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var translated = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        translated.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        var outdated = new TextBlock { Text = "译文基于较早的简介，请以当前原文为准。", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed };
        outdated.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationCautionText");
        var result = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed, Child = new StackPanel { Children = { source, translated, outdated } } };
        result.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationAccent");

        bool fetched = false;
        button.Click += async (_, _) =>
        {
            if (fetched)
            {
                bool show = result.Visibility != Visibility.Visible;
                result.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                label.Text = show ? "收起译文" : "显示译文";
                return;
            }
            if (Translate is not { } fetch) return;
            _translation?.Cancel();
            using var cancellation = _translation = new CancellationTokenSource();
            button.IsEnabled = false;
            label.Text = "正在翻译…";
            try
            {
                var answer = await fetch(cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                if (answer is null)
                {
                    label.Text = "翻译简介";
                    ModMain.Hint("此项目暂时没有可用的中文简介。", ModMain.HintType.Blue);
                    return;
                }
                translated.Text = answer.Translated.Trim();
                outdated.Visibility = answer.IsCurrent ? Visibility.Collapsed : Visibility.Visible;
                result.Visibility = Visibility.Visible;
                fetched = true;
                label.Text = "收起译文";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { label.Text = "翻译简介"; }
            catch (Exception error)
            {
                label.Text = "翻译简介";
                Logger.Warn(error, "翻译资源简介失败");
                ModMain.Hint("简介翻译暂不可用，请稍后重试。", ModMain.HintType.Red);
            }
            finally
            {
                if (ReferenceEquals(_translation, cancellation)) _translation = null;
                button.IsEnabled = true;
            }
        };
        return new StackPanel { Margin = new Thickness(0, -2, 0, 12), Children = { button, result } };
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
