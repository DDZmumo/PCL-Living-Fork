// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.Windows;
using System.Windows.Controls;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>A page mounted directly in the launcher, with the shared theme and scrolling behavior.</summary>
public class GlassWorkspacePage : Border
{
    internal GlassPageResources Theme { get; }
    internal ScrollViewer? Scroll { get; private set; }

    protected GlassWorkspacePage()
    {
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        Theme = new GlassPageResources(this);
        Resources[typeof(TextBlock)] = Theme.Style("GlassPresentationTextStyle");
        Theme.ApplyPalette();
        Loaded += (_, _) => RefreshPalette();
    }

    internal void RefreshPalette() => Theme.ApplyPalette();

    internal static TextBlock? AttachPlaceholder(TextBox text, string placeholder)
    {
        if (text.Parent is not Panel parent) return null;
        int index = parent.Children.IndexOf(text);
        parent.Children.RemoveAt(index);
        Grid host = GlassDialogControls.WithPlaceholder(text, placeholder);
        Grid.SetRow(host, Grid.GetRow(text));
        Grid.SetColumn(host, Grid.GetColumn(text));
        Grid.SetRowSpan(host, Grid.GetRowSpan(text));
        Grid.SetColumnSpan(host, Grid.GetColumnSpan(text));
        parent.Children.Insert(index, host);
        return (TextBlock)host.Children[1];
    }

    internal void AttachScroll(ScrollViewer scroll, bool fadeEdges = true)
    {
        Scroll = scroll;
        SmoothScroll.SetIsEnabled(scroll, true);
        if (fadeEdges && ReferenceEquals(Child, scroll))
        {
            Child = null;
            Child = new ScrollEdgeFade(scroll);
        }
        else if (fadeEdges && scroll.Parent is Grid grid)
        {
            int index = grid.Children.IndexOf(scroll);
            grid.Children.RemoveAt(index);
            grid.Children.Insert(index, new ScrollEdgeFade(scroll));
        }
        scroll.ScrollChanged += (_, args) => { if (args.VerticalChange != 0) ModMain.FrmMain?.RefreshBackToTop(); };
    }
}
