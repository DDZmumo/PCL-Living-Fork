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
    private Node Document(FlowDocumentScrollViewer source)
    {
        var viewer = new FlowDocumentScrollViewer { IsToolBarVisible = false, VerticalScrollBarVisibility = source.VerticalScrollBarVisibility, HorizontalScrollBarVisibility = source.HorizontalScrollBarVisibility };
        viewer.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        FlowDocument previous = null; string previousText = null;
        var node = new Node(viewer);
        node.Pull = () =>
        {
            string text = source.Document is null ? string.Empty : new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Text;
            if (ReferenceEquals(source.Document, previous) && text == previousText) return;
            previous = source.Document; previousText = text; viewer.Document = CloneDocument(source.Document);
        };
        return node;
    }
    private Node RichDocument(RichTextBox source)
    {
        var input = new RichTextBox { IsReadOnly = source.IsReadOnly, BorderThickness = new Thickness(), Background = System.Windows.Media.Brushes.Transparent };
        input.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        FlowDocument previous = null; string previousText = null;
        bool pulling = false;
        input.TextChanged += (_, _) =>
        {
            if (Live(input) && !pulling && !source.IsReadOnly) source.Document = CloneDocument(input.Document);
        };
        var node = new Node(input);
        node.Pull = () =>
        {
            string text = new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Text;
            if (ReferenceEquals(source.Document, previous) && text == previousText) return;
            pulling = true;
            try { previous = source.Document; previousText = text; input.Document = CloneDocument(source.Document); }
            finally { pulling = false; }
        };
        return node;
    }
    private FlowDocument CloneDocument(FlowDocument original)
    {
        if (original is null) return new FlowDocument();
        string xaml = XamlWriter.Save(original);
        var clone = (FlowDocument)XamlReader.Parse(xaml);
        clone.SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationText");
        clone.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        Hyperlink[] oldLinks = Links(original).ToArray(), newLinks = Links(clone).ToArray();
        for (int index = 0; index < Math.Min(oldLinks.Length, newLinks.Length); index++)
        {
            Hyperlink source = oldLinks[index], target = newLinks[index];
            target.NavigateUri = null;
            target.Click += (_, _) =>
            {
                if (!Live(target)) return;
                AutomationPeer peer = ContentElementAutomationPeer.CreatePeerForElement(source) ?? new HyperlinkAutomationPeer(source);
                (peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
            };
        }
        return clone;
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
