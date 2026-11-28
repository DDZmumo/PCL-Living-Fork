// SPDX-License-Identifier: MPL-2.0
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PCLLF.Presentation.Foundation;
using System.Windows.Media;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

internal sealed class GlassHomePage : ContentControl, IDisposable
{
    private readonly TextBox _launchLog;
    private readonly GlassSurface _logCard;
    private readonly GlassPageResources _resources;
    internal ScrollViewer Scroll { get; }
    internal GlassHomepageViewport Homepage { get; } = new() { Name = "PanCustom" };
    internal LauncherHomepage Loader { get; }

    internal GlassHomePage()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this, paletteChanged: Homepage.ApplyPalette);
        LegacyThemePalette.Watch(this, RefreshAppearance);
        SetResourceReference(FontFamilyProperty, "GlassPresentationFont");
        var sections = new StackPanel { Margin = new Thickness(25, 25, 25, 10) };
        Grid.SetIsSharedSizeScope(sections, true);
        Scroll = new ScrollViewer { Content = sections, Style = (Style)FindResource("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        Content = new ScrollEdgeFade(Scroll);

        sections.Children.Add(Homepage);
        Loader = new LauncherHomepage(Homepage, Scroll.ScrollToTop);
        var log = new StackPanel { Margin = new Thickness(20, 16, 20, 14) };
        var title = new TextBlock { Text = "启动日志", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 9) };
        title.SetResourceReference(TextElement.ForegroundProperty, "GlassPresentationText");
        log.Children.Add(title);
        _launchLog = new TextBox { IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _launchLog.SetResourceReference(ForegroundProperty, "GlassPresentationText");
        log.Children.Add(_launchLog);
        _logCard = GlassPageResources.Plate(log, new Thickness(0, 0, 0, 12));
        _logCard.Visibility = Visibility.Collapsed;
        sections.Children.Add(_logCard);
        _resources.ApplyPalette();
    }

    internal void Open()
    {
        Scroll.ScrollToTop();
        RefreshAppearance();
        Loader.Refresh();
    }

    internal void RefreshAppearance()
    {
        _resources.ApplyPalette();
        _logCard.Visibility = ModBase.ModeDebug ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void ClearLaunchLog() => _launchLog.Clear();
    internal void AppendLaunchLog(string text) => _launchLog.AppendText(text);

    public void Dispose() => Loader.Dispose();
}
