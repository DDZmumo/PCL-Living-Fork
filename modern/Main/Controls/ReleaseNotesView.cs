// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Md = Markdig.Syntax;
using Mi = Markdig.Syntax.Inlines;
using Wpf = System.Windows.Documents;

namespace PCL;

internal sealed class ReleaseNotesView : FlowDocumentScrollViewer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().UseTaskLists().DisableHtml().Build();
    private readonly Action<string> openLink;
    internal double PreferredWidth => 720;

    internal ReleaseNotesView(string markdown, Action<string> openLink = null)
    {
        this.openLink = openLink ?? (url => ModBase.OpenWebsite(url));
        IsToolBarVisible = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = Brushes.Transparent;
        Height = Math.Clamp((ModMain.FrmMain?.ActualHeight ?? 640) - 220, 180, 440);
        Document = new FlowDocument { FontSize = 14, FontFamily = new FontFamily("Microsoft YaHei UI"), PagePadding = new Thickness(0, 0, 10, 0),
            ColumnWidth = double.PositiveInfinity, Background = Brushes.Transparent };
        Document.SetResourceReference(TextElement.ForegroundProperty, "ColorBrush1");
        AppendBlocks(Document.Blocks, Markdown.Parse(string.IsNullOrWhiteSpace(markdown) ? "该版本暂无更新说明。" : markdown, Pipeline));
    }

    private void AppendBlocks(BlockCollection target, Md.ContainerBlock container)
    {
        foreach (Md.Block block in container)
        {
            switch (block)
            {
                case Md.HeadingBlock heading:
                    var title = Paragraph(heading.Inline);
                    title.FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
                    title.FontWeight = FontWeights.SemiBold;
                    title.Margin = new Thickness(0, 12, 0, 8);
                    target.Add(title);
                    break;
                case Md.ParagraphBlock paragraph:
                    target.Add(Paragraph(paragraph.Inline));
                    break;
                case Md.ListBlock list:
                    var items = new Wpf.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(24, 0, 0, 0) };
                    if (list.IsOrdered && int.TryParse(Convert.ToString(list.OrderedStart, CultureInfo.InvariantCulture), out int start) && start > 0) items.StartIndex = start;
                    foreach (Md.Block item in list)
                    {
                        var entry = new ListItem();
                        if (item is Md.ContainerBlock content) AppendBlocks(entry.Blocks, content);
                        items.ListItems.Add(entry);
                    }
                    target.Add(items);
                    break;
                case Md.QuoteBlock quote:
                    var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 3, 0, 3), Margin = new Thickness(0, 4, 0, 12) };
                    section.SetResourceReference(Block.BorderBrushProperty, "ColorBrush5");
                    AppendBlocks(section.Blocks, quote); target.Add(section);
                    break;
                case Md.CodeBlock code:
                    var codeText = new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 13,
                        Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 12) };
                    codeText.SetResourceReference(TextElement.BackgroundProperty, "ColorBrushGray8");
                    target.Add(codeText);
                    break;
                case Md.ThematicBreakBlock:
                    var line = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 12) };
                    line.SetResourceReference(Block.BorderBrushProperty, "ColorBrushGray5"); target.Add(line);
                    break;
                case Markdig.Extensions.Tables.Table table:
                    var grid = new Wpf.Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
                    var group = new TableRowGroup(); grid.RowGroups.Add(group);
                    foreach (Markdig.Extensions.Tables.TableRow row in table)
                    {
                        var renderedRow = new Wpf.TableRow();
                        if (row.IsHeader) { renderedRow.FontWeight = FontWeights.SemiBold; renderedRow.SetResourceReference(TextElement.BackgroundProperty, "ColorBrush8"); }
                        foreach (Markdig.Extensions.Tables.TableCell cell in row)
                        {
                            var renderedCell = new Wpf.TableCell { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0.5) };
                            renderedCell.SetResourceReference(TableCell.BorderBrushProperty, "ColorBrushGray5");
                            AppendBlocks(renderedCell.Blocks, cell); renderedRow.Cells.Add(renderedCell);
                        }
                        group.Rows.Add(renderedRow);
                    }
                    target.Add(grid);
                    break;
                case Md.ContainerBlock nested:
                    var nestedSection = new Section(); AppendBlocks(nestedSection.Blocks, nested); target.Add(nestedSection);
                    break;
                case Md.LeafBlock leaf when leaf.Inline is not null:
                    target.Add(Paragraph(leaf.Inline));
                    break;
            }
        }
    }

    private Paragraph Paragraph(Mi.ContainerInline content)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 23 };
        AppendInlines(paragraph.Inlines, content);
        return paragraph;
    }

    private void AppendInlines(InlineCollection target, Mi.ContainerInline content)
    {
        for (Mi.Inline inline = content?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case Mi.LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case Mi.CodeInline code:
                    var codeText = new Run(code.Content) { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
                    codeText.SetResourceReference(TextElement.BackgroundProperty, "ColorBrushGray8"); target.Add(codeText);
                    break;
                case Mi.LineBreakInline line: target.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case Mi.EmphasisInline emphasis:
                    var styled = new Span();
                    if (emphasis.DelimiterChar == '~') styled.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) styled.FontWeight = FontWeights.Bold;
                    else styled.FontStyle = FontStyles.Italic;
                    AppendInlines(styled.Inlines, emphasis); target.Add(styled);
                    break;
                case Mi.LinkInline link:
                    Span linked = Link(link.Url);
                    if (link.IsImage) linked.Inlines.Add(new Run("图片："));
                    AppendInlines(linked.Inlines, link); target.Add(linked);
                    break;
                case Mi.AutolinkInline link:
                    Span automatic = Link(link.IsEmail ? null : link.Url);
                    automatic.Inlines.Add(new Run(link.Url)); target.Add(automatic);
                    break;
                case Markdig.Extensions.TaskLists.TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case Mi.ContainerInline nested:
                    var span = new Span(); AppendInlines(span.Inlines, nested); target.Add(span);
                    break;
            }
        }
    }

    private Span Link(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return new Span();
        if (!Uri.TryCreate(new Uri(ModBase.LauncherRepositoryUrl + "/"), address, out Uri uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return new Span();
        var link = new Hyperlink { NavigateUri = uri };
        link.SetResourceReference(TextElement.ForegroundProperty, "ColorBrush3");
        link.RequestNavigate += (_, args) => { args.Handled = true; openLink(uri.AbsoluteUri); };
        return link;
    }
}
