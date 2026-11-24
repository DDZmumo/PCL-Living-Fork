using System;
using System.Windows;
using System.Windows.Controls;

namespace PCL;

/// <summary>
/// Settings → Java → 下载 Java, shown as a detail page: it keeps the settings sidebar and the back entry. The controls stay
/// on <see cref="PageSetupLaunch"/>; this page only owns the panel's open state while it is displayed.
/// </summary>
public sealed class PageSetupJavaDownload : MyPageRight
{
    internal PageSetupLaunch Host { get; }

    internal PageSetupJavaDownload(PageSetupLaunch host)
    {
        Host = host;
        Child = new Grid();
        PageEntering += host.OpenJavaDownloadPanel;
        PageLeaving += host.CloseJavaDownloadPanel;
    }
}

// Both routes use the settings shell and their existing Java control owners.
public sealed class PageSetupJava : MyPageRight
{
    public const string Icon = "M3,7 H17 V9 H21 Q23,9 23,12 V13 Q23,16 20,16 H16 Q14,20 9,20 H8 Q3,20 3,15 Z M17,11 V14 H20 Q21,14 21,13 V12 Q21,11 20,11 Z M1,22 H19 V24 H1 Z M7,1 H9 V5 H7 Z M12,0 H14 V5 H12 Z";

    /// <summary>The settings page that owns the card and its controls (global launch settings or an instance).</summary>
    internal FrameworkElement Host { get; }

    internal PageSetupJava(FrameworkElement host, MyCard card, Action refresh, Action close = null)
    {
        Host = host;
        ((Panel)card.Parent).Children.Remove(card);
        var content = new StackPanel { Margin = new Thickness(25) };
        content.Children.Add(card);
        var scroll = new MyScrollViewer {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Child = scroll;
        PanScroll = "PanJavaScroll";
        NameScope.SetNameScope(this, new NameScope());
        RegisterName(PanScroll, scroll);
        Loaded += (_, _) => refresh();
        if (close is not null)
        {
            PageExit += () => close();
            Unloaded += (_, _) => close();
        }
    }
}
