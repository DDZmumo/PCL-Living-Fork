// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private static readonly System.Windows.Media.FontFamily DocumentFont = new("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");

    private Node Document(FlowDocumentScrollViewer source)
    {
        var viewer = new FlowDocumentScrollViewer { IsToolBarVisible = false, VerticalScrollBarVisibility = source.VerticalScrollBarVisibility, HorizontalScrollBarVisibility = source.HorizontalScrollBarVisibility };
        viewer.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        FlowDocument previous = null; bool hasStamp = false; int previousStamp = 0; string previousText = null;
        var node = new Node(viewer);
        node.Pull = () =>
        {
            if (source.Document is null)
            {
                if (previous is null) return;
                previous = null; hasStamp = false; previousStamp = 0; previousText = string.Empty;
                viewer.Document = new FlowDocument();
                return;
            }
            if (TryComputeDocumentStamp(source.Document, out int stamp))
            {
                if (ReferenceEquals(source.Document, previous) && hasStamp && stamp == previousStamp) return;
                previous = source.Document; hasStamp = true; previousStamp = stamp; previousText = null;
                viewer.Document = CloneDocument(source.Document);
                return;
            }
            string text = new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Text;
            if (ReferenceEquals(source.Document, previous) && !hasStamp && text == previousText) return;
            previous = source.Document; hasStamp = false; previousStamp = 0; previousText = text; viewer.Document = CloneDocument(source.Document);
        };
        return node;
    }
    private Node RichDocument(RichTextBox source)
    {
        var input = new RichTextBox { IsReadOnly = source.IsReadOnly, BorderThickness = new Thickness(), Background = System.Windows.Media.Brushes.Transparent };
        input.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        FlowDocument previous = null; bool hasStamp = false; int previousStamp = 0; string previousText = null;
        bool pulling = false;
        input.TextChanged += (_, _) =>
        {
            if (Live(input) && !pulling && !source.IsReadOnly) source.Document = CloneDocument(input.Document);
        };
        var node = new Node(input);
        node.Pull = () =>
        {
            if (TryComputeDocumentStamp(source.Document, out int stamp))
            {
                if (ReferenceEquals(source.Document, previous) && hasStamp && stamp == previousStamp) return;
                pulling = true;
                try { previous = source.Document; hasStamp = true; previousStamp = stamp; previousText = null; input.Document = CloneDocument(source.Document); }
                finally { pulling = false; }
                return;
            }
            string text = new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Text;
            if (ReferenceEquals(source.Document, previous) && !hasStamp && text == previousText) return;
            pulling = true;
            try { previous = source.Document; hasStamp = false; previousStamp = 0; previousText = text; input.Document = CloneDocument(source.Document); }
            finally { pulling = false; }
        };
        return node;
    }
    private FlowDocument CloneDocument(FlowDocument original)
    {
        if (original is null) return new FlowDocument();
        if (TryCloneDocumentFast(original, out FlowDocument fastClone)) return fastClone;
        string xaml = XamlWriter.Save(original);
        var clone = (FlowDocument)XamlReader.Parse(xaml);
        clone.SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationText");
        clone.FontFamily = DocumentFont;
        Hyperlink[] oldLinks = Links(original).ToArray(), newLinks = Links(clone).ToArray();
        for (int index = 0; index < Math.Min(oldLinks.Length, newLinks.Length); index++)
        {
            BindHyperlink(oldLinks[index], newLinks[index]);
        }
        return clone;
    }

    private void BindHyperlink(Hyperlink source, Hyperlink target)
    {
        target.NavigateUri = null;
        target.Click += (_, _) =>
        {
            if (!Live(target)) return;
            AutomationPeer peer = ContentElementAutomationPeer.CreatePeerForElement(source) ?? new HyperlinkAutomationPeer(source);
            (peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
        };
    }

    private static bool TryComputeDocumentStamp(FlowDocument document, out int stamp)
    {
        var hash = new HashCode();
        hash.Add(document.Blocks.Count);
        if (!HashBlocks(document.Blocks, ref hash)) { stamp = 0; return false; }
        stamp = hash.ToHashCode();
        return true;
    }

    private static bool HashBlocks(BlockCollection blocks, ref HashCode hash)
    {
        foreach (Block block in blocks)
        {
            hash.Add(block.GetType());
            switch (block)
            {
                case Paragraph paragraph:
                    if (!HashInlines(paragraph.Inlines, ref hash)) return false;
                    break;
                case Section section:
                    if (!HashBlocks(section.Blocks, ref hash)) return false;
                    break;
                case List list:
                    hash.Add(list.MarkerStyle);
                    foreach (ListItem item in list.ListItems)
                        if (!HashBlocks(item.Blocks, ref hash)) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool HashInlines(InlineCollection inlines, ref HashCode hash)
    {
        foreach (Inline inline in inlines)
        {
            hash.Add(inline.GetType());
            switch (inline)
            {
                case Run run:
                    hash.Add(run.Text, StringComparer.Ordinal);
                    break;
                case LineBreak:
                    hash.Add(1);
                    break;
                case Span span:
                    if (!HashInlines(span.Inlines, ref hash)) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private bool TryCloneDocumentFast(FlowDocument original, out FlowDocument clone)
    {
        var result = new FlowDocument
        {
            PagePadding = original.PagePadding,
            LineHeight = original.LineHeight,
            TextAlignment = original.TextAlignment,
            FontSize = original.FontSize,
            FontFamily = DocumentFont
        };
        result.SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationText");
        if (!TryCloneBlocks(original.Blocks, result.Blocks)) { clone = null; return false; }
        clone = result;
        return true;
    }

    private bool TryCloneBlocks(BlockCollection source, BlockCollection target)
    {
        foreach (Block block in source)
        {
            Block copy;
            switch (block)
            {
                case Paragraph paragraph:
                    var p = new Paragraph { Margin = paragraph.Margin, Padding = paragraph.Padding, LineHeight = paragraph.LineHeight, TextIndent = paragraph.TextIndent, TextAlignment = paragraph.TextAlignment };
                    CopyTextFormatting(paragraph, p);
                    if (!TryCloneInlines(paragraph.Inlines, p.Inlines)) return false;
                    copy = p;
                    break;
                case Section section:
                    var s = new Section { Margin = section.Margin, Padding = section.Padding, TextAlignment = section.TextAlignment };
                    CopyTextFormatting(section, s);
                    if (!TryCloneBlocks(section.Blocks, s.Blocks)) return false;
                    copy = s;
                    break;
                case List list:
                    var l = new List { Margin = list.Margin, Padding = list.Padding, MarkerStyle = list.MarkerStyle, MarkerOffset = list.MarkerOffset, StartIndex = list.StartIndex };
                    CopyTextFormatting(list, l);
                    foreach (ListItem item in list.ListItems)
                    {
                        var li = new ListItem { Margin = item.Margin, Padding = item.Padding };
                        CopyTextFormatting(item, li);
                        if (!TryCloneBlocks(item.Blocks, li.Blocks)) return false;
                        l.ListItems.Add(li);
                    }
                    copy = l;
                    break;
                default:
                    return false;
            }
            target.Add(copy);
        }
        return true;
    }

    private bool TryCloneInlines(InlineCollection source, InlineCollection target)
    {
        foreach (Inline inline in source)
        {
            Inline copy;
            switch (inline)
            {
                case Run run:
                    var r = new Run(run.Text);
                    CopyTextFormatting(run, r);
                    copy = r;
                    break;
                case LineBreak:
                    copy = new LineBreak();
                    break;
                case Hyperlink link:
                    var hl = new Hyperlink { ToolTip = link.ToolTip };
                    CopyTextFormatting(link, hl);
                    if (!TryCloneInlines(link.Inlines, hl.Inlines)) return false;
                    BindHyperlink(link, hl);
                    copy = hl;
                    break;
                case Bold bold:
                    var b = new Bold();
                    CopyTextFormatting(bold, b);
                    if (!TryCloneInlines(bold.Inlines, b.Inlines)) return false;
                    copy = b;
                    break;
                case Italic italic:
                    var it = new Italic();
                    CopyTextFormatting(italic, it);
                    if (!TryCloneInlines(italic.Inlines, it.Inlines)) return false;
                    copy = it;
                    break;
                case Underline underline:
                    var u = new Underline();
                    CopyTextFormatting(underline, u);
                    if (!TryCloneInlines(underline.Inlines, u.Inlines)) return false;
                    copy = u;
                    break;
                case Span span when span.GetType() == typeof(Span):
                    var sp = new Span();
                    CopyTextFormatting(span, sp);
                    if (!TryCloneInlines(span.Inlines, sp.Inlines)) return false;
                    copy = sp;
                    break;
                default:
                    return false;
            }
            target.Add(copy);
        }
        return true;
    }

    private static void CopyTextFormatting(TextElement source, TextElement target)
    {
        if (source.ReadLocalValue(TextElement.FontSizeProperty) is double fontSize) target.FontSize = fontSize;
        if (source.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight fontWeight) target.FontWeight = fontWeight;
        if (source.ReadLocalValue(TextElement.FontStyleProperty) is FontStyle fontStyle) target.FontStyle = fontStyle;
        if (source.ReadLocalValue(TextElement.ForegroundProperty) is System.Windows.Media.Brush foreground) target.Foreground = foreground;
        if (source.ReadLocalValue(TextElement.BackgroundProperty) is System.Windows.Media.Brush background) target.Background = background;
        if (source is Inline sourceInline && target is Inline targetInline && sourceInline.ReadLocalValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations)
            targetInline.TextDecorations = decorations;
    }
    private static IEnumerable<Hyperlink> Links(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Hyperlink link) yield return link;
            if (child is DependencyObject dependency)
                foreach (Hyperlink nested in Links(dependency)) yield return nested;
        }
    }
}
