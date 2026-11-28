// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using MeloongCore;
using PCLCS;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

internal sealed class GlassResourceProjectRow : Grid
{
    internal const double RowHeight = 52;
    private static readonly Geometry Heart = Geometry.Parse(ModBase.Logo.IconButtonLikeLine);
    private static readonly Geometry FilledHeart = Geometry.Parse(ModBase.Logo.IconButtonLikeFill);
    private readonly Image _image;
    private readonly Button _favorite;
    private readonly Path _heart;
    private bool _loadingIcon;

    internal ResourceProject Project { get; }

    internal GlassResourceProjectRow(ResourceProject project, GlassPageResources resources, bool showVersion, bool showLoader, Action? open)
    {
        Project = project;
        MinHeight = RowHeight;
        Background = Brushes.Transparent;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var band = new Border { IsHitTestVisible = false, Opacity = 0 };
        band.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHoverBand");
        SetColumnSpan(band, 2);
        Children.Add(band);
        var hover = new SpringMotion(this, 0, value => band.Opacity = Math.Clamp(value, 0, 1), new SpringParameters(.18, 1));
        MouseEnter += (_, _) => hover.Retarget(1);
        MouseLeave += (_, _) => hover.Retarget(0);

        var contents = new Grid();
        contents.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contents.ColumnDefinitions.Add(new ColumnDefinition());
        contents.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _image = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0),
            Clip = new RectangleGeometry(new Rect(0, 0, 32, 32), 7, 7),
            Stretch = Stretch.Uniform, Source = (ImageSource)new MyBitmap(ModBase.PathImage + "Icons/NoIcon.png") };
        contents.Children.Add(_image);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        SetColumn(labels, 1);
        contents.Children.Add(labels);
        var (name, subtitle) = project.GetControlTitle(true);
        string title = name ?? project.RawName;
        var text = resources.Text("");
        text.FontSize = 15;
        text.FontWeight = FontWeights.SemiBold;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Inlines.Add(new Run(title));
        foreach (string tag in project.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.Ordinal))
        {
            var caption = resources.Note(tag, 10);
            caption.TextWrapping = TextWrapping.NoWrap;
            caption.FontWeight = FontWeights.Normal;
            var badge = new Border { Child = caption, CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(7, 0, 0, 0) };
            badge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
            text.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
        }
        labels.Children.Add(text);
        var description = ResourcePresentation.ProjectContext(project, showVersion, showLoader);
        string version = (showVersion, showLoader) switch {
            (true, true) => string.Join(" ", new[] { description.LoaderPart, description.GameVersion }.Where(part => part.Length > 0)),
            (true, false) => description.GameVersion,
            (false, true) => description.LoaderFull,
            _ => ""
        };
        var metadata = resources.Note(string.IsNullOrEmpty(version) ? project.Platform.ToString() : version + " · " + project.Platform, 12);
        metadata.TextWrapping = TextWrapping.NoWrap;
        metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        metadata.Margin = new Thickness(0, 2, 0, 0);
        labels.Children.Add(metadata);
        // Trailing download count, as in the classic resource list.
        if (open is not null && project.DownloadCount > 0)
        {
            // Popularity and freshness as a right-aligned figure: the count stands out, its caption and age below.
            var count = resources.Text(DownloadText(project.DownloadCount));
            count.FontSize = 14;
            count.FontWeight = FontWeights.SemiBold;
            count.TextAlignment = TextAlignment.Right;
            count.TextWrapping = TextWrapping.NoWrap;
            string age = project.LastUpdate == default ? "" : " · " + StringUtils.FormatTimeSpan(project.LastUpdate - DateTime.Now, true);
            var caption = resources.Note("下载" + age, 11);
            caption.TextAlignment = TextAlignment.Right;
            caption.TextWrapping = TextWrapping.NoWrap;
            var downloads = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0), MinWidth = 64, Children = { count, caption } };
            SetColumn(downloads, 2);
            contents.Children.Add(downloads);
        }
        var details = new Button { Content = contents, Height = RowHeight, Style = resources.Style("GlassPresentationBandRow"),
            ToolTip = string.Join("\n", new[] { title, subtitle, project.Description }.Where(line => !string.IsNullOrWhiteSpace(line))),
            Focusable = open is not null, IsHitTestVisible = open is not null };
        AutomationProperties.SetName(details, title);
        if (open is not null) details.Click += (_, _) => open();
        Children.Add(details);

        _heart = new Path { Data = Heart, Width = 17, Height = 17, Stretch = Stretch.Uniform };
        _heart.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
        _favorite = new Button { Content = _heart, Width = 42, Height = 36, VerticalAlignment = VerticalAlignment.Center,
            Style = resources.Style("GlassPresentationRowButton"), Visibility = ResourceFavorites.CanFavorite(project) ? Visibility.Visible : Visibility.Collapsed };
        SetColumn(_favorite, 1);
        _favorite.Click += (_, _) => ResourceFavorites.Toggle(project);
        Children.Add(_favorite);
        var separator = new Border { Height = .7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(52, 0, 0, 0),
            IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        SetColumnSpan(separator, 2);
        Children.Add(separator);
        ResourceFavorites.Observe(this, (_, _) => RefreshFavorite());
    }

    private static string DownloadText(long count) =>
        count > 100000000 ? Math.Round(count / 100000000d, 2) + " 亿" :
        count > 100000 ? Math.Floor(count / 10000d) + " 万" : count.ToString();

    private void RefreshFavorite()
    {
        bool saved = ResourceFavorites.IsFavorite(Project);
        _favorite.IsEnabled = !ResourceFavorites.IsBusy(Project);
        _heart.Data = saved ? FilledHeart : Heart;
        string action = saved ? "取消收藏" : "收藏";
        _favorite.ToolTip = action;
        AutomationProperties.SetName(_favorite, action + " " + Project.TranslatedName);
    }

    internal async void LoadIcon()
    {
        if (_loadingIcon) return;
        _loadingIcon = true;
        try
        {
            if (await ResourceThumbnails.ReadAsync(Project) is { } source) _image.Source = source;
        }
        catch (Exception error)
        {
            Logger.Warn(error, "读取资源图标失败");
        }
    }
}
