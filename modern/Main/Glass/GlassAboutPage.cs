// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeloongCore;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

internal sealed class GlassAboutPage : ContentControl
{
    private readonly GlassPageResources _resources;
    internal ScrollViewer Scroll { get; }

    internal GlassAboutPage()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        stack.Children.Add(Section("关于 PCL Living Fork", () => Credits(AboutContent.Authors), expanded: true, collapsible: false));
        stack.Children.Add(Section("本项目", () => TextSection(AboutContent.ProjectText, AboutContent.ProjectLinks), expanded: true, collapsible: false));
        stack.Children.Add(Section("原项目鸣谢", () => Credits(AboutContent.Credits)));
        stack.Children.Add(Section("法律信息", () => TextSection(AboutContent.LegalText, AboutContent.LegalLinks)));
        stack.Children.Add(Section("许可与版权声明", Licenses));
        stack.Children.Add(Section("离线许可与来源", () => {
            var offline = TextSection(AboutContent.OfflineText, AboutContent.OfflineLinks);
            offline.Children.Add(LicenseText("ThirdParty.txt"));
            return offline;
        }));
        Scroll = new ScrollViewer { Content = stack, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        Content = new ScrollEdgeFade(Scroll);
        Loaded += (_, _) => RefreshPalette();
    }

    internal void Open() { RefreshPalette(); Scroll.ScrollToTop(); }
    internal void RefreshPalette() => _resources.ApplyPalette();

    private FrameworkElement Section(string title, Func<UIElement> build, bool expanded = false, bool collapsible = true)
    {
        var heading = _resources.Text(title);
        heading.FontSize = 15;
        heading.FontWeight = FontWeights.SemiBold;
        if (!collapsible)
        {
            heading.Margin = new Thickness(0, 0, 0, 12);
            return GlassPageResources.Card(new StackPanel { Children = { heading, build() } }, GlassPageLayout.CardRole.Content);
        }
        var expander = new Expander { Header = heading, Style = _resources.Style("GlassPresentationExpander") };
        expander.Expanded += OnExpand;
        expander.IsExpanded = expanded;
        return GlassPageResources.Card(expander, GlassPageLayout.CardRole.Expander);

        void OnExpand(object? sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, expander)) return;
            expander.Expanded -= OnExpand;
            Dispatcher.InvokeAsync(() =>
            {
                try { expander.Content = build(); }
                catch (Exception error)
                {
                    Logger.Warn(error, "加载关于页内容失败");
                    expander.Content = _resources.Note("无法读取此内容。", 13);
                }
            }, DispatcherPriority.Background);
        }
    }

    private Grid Credits(IEnumerable<AboutContent.Credit> credits)
    {
        var list = new Grid();
        foreach (var credit in credits)
        {
            var row = new Grid();
            Grid.SetRow(row, list.RowDefinitions.Count);
            list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (credit.Image is { } address)
            {
                var image = new Image { Width = 34, Height = 34, Source = (ImageSource)new MyBitmap(address),
                    Clip = new EllipseGeometry(new Point(17, 17), 17, 17), Margin = new Thickness(0, 0, 12, 0) };
                row.Children.Add(image);
            }
            var title = _resources.Text(credit.Name);
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            var details = _resources.Note(credit.Info.Replace("%VERSION%", ModBase.VersionBaseName, StringComparison.Ordinal), 12);
            details.Margin = new Thickness(0, 2, 0, 0);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, details } };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (credit.Action is { } link)
            {
                Button button = Link(link);
                button.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            list.Children.Add(row);
        }
        return list;
    }

    private StackPanel TextSection(IEnumerable<string> paragraphs, IEnumerable<AboutContent.Link> links)
    {
        var stack = new StackPanel();
        foreach (string line in paragraphs)
        {
            var text = _resources.Text(line);
            text.FontSize = 13;
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(text);
        }
        var actions = new WrapPanel();
        foreach (var link in links) actions.Children.Add(Link(link));
        if (actions.Children.Count > 0) stack.Children.Add(actions);
        return stack;
    }

    private UIElement Licenses()
    {
        var stack = new StackPanel();
        foreach (var license in AboutContent.Licenses)
        {
            var text = TextSection([license.Name, license.Copyright], license.Actions);
            ((TextBlock)text.Children[0]).FontWeight = FontWeights.SemiBold;
            text.Margin = new Thickness(0, 6, 0, 11);
            string[] files = license.Name switch {
                "Java Launch Wrapper 1.4.4" => ["JavaLaunchWrapper/NOTICE.txt", "JavaLaunchWrapper/LICENSE.txt"],
                "LWJGL Unsafe Agent 2.0" => ["LwjglUnsafeAgent/NOTICE.txt", "MeloongCore.txt"],
                "Transformer Discovery Service 1.0" => ["HMCLTransformerDiscoveryService/NOTICE.txt",
                    "HMCLTransformerDiscoveryService/LICENSE", "HMCLTransformerDiscoveryService/ADDITIONAL_TERMS.md"],
                "Markdig" => ["Markdig.txt"], "MeloongCore" => ["MeloongCore.txt"], ".NET runtime / CoreCLR" => ["dotnet-runtime.txt"], _ => []
            };
            if (files.Length > 0) text.Children.Add(Section("随程序附带的许可", () => LicenseText(files)));
            stack.Children.Add(text);
        }
        return stack;
    }

    private TextBox LicenseText(params string[] files) => new()
    {
        Text = string.Join("\n\n", System.Linq.Enumerable.Select(files, AboutLicenses.Read)),
        TextWrapping = TextWrapping.Wrap, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 365,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = _resources.Style("GlassPresentationTextBox")
    };

    private Button Link(AboutContent.Link link)
    {
        // Supporting the original author is the page's primary action.
        string style = link.Caption == "支持原作者" ? "GlassPresentationAccentButton" : "GlassPresentationButton";
        var button = new Button { Content = link.Caption, Style = _resources.Style(style), Margin = new Thickness(0, 0, 8, 4) };
        button.Click += (_, _) =>
        {
            if (link.Address is { Length: > 0 } address) ModBase.OpenWebsite(address);
            else if (link.Caption == "保存对应源码") AboutLicenses.SaveTransformerSource();
            else if (link.Caption == "检查更新") ModSecret.UpdateCheckByButton();
        };
        return button;
    }
}
