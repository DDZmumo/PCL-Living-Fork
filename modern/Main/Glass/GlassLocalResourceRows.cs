// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using PCLCS;

namespace PCL;

internal sealed class GlassLocalResourceRow : Grid, IResourcePageRow<InstanceResourceEntry, InstanceResourceOnlineInfo>
{
    private readonly InstanceResourceKind _kind;
    private readonly ResourcePageRowActions<InstanceResourceEntry, InstanceResourceOnlineInfo> _actions;
    private readonly CheckBox _check;
    private readonly Image _icon;
    private readonly TextBlock _name, _description;
    private readonly Button _select;
    private readonly Action _queueArtwork;
    private readonly Action<InstanceResourceEntry>? _preview;
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private InstanceResourceOnlineInfo? _online;
    private string? _artworkAddress, _requestedArtwork;
    private ImageSource? _fallback;
    private Button? _update, _favorite;
    public FrameworkElement Element => this;
    public InstanceResourceEntry Entry { get; private set; } = null!;
    public bool Checked
    {
        get => _check.IsChecked == true;
        set
        {
            if (Checked == value) return;
            _check.IsChecked = value;
            if (Entry is not null && _actions.SelectionChanged(this)) _check.IsChecked = !value;
        }
    }

    internal GlassLocalResourceRow(InstanceResourceKind kind, GlassPageResources resources,
        ResourcePageRowActions<InstanceResourceEntry, InstanceResourceOnlineInfo> actions, Action queueArtwork,
        Action<InstanceResourceEntry>? preview = null)
    {
        _kind = kind;
        _actions = actions;
        _queueArtwork = queueArtwork;
        _preview = preview;
        ResourceFavorites.Observe(this, (_, _) => RefreshFavorite());
        // Same row as the mod list: selector, 32 px icon, title over a two-line description, hover band.
        MinHeight = 52;
        Background = Brushes.Transparent;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var band = new Border { IsHitTestVisible = false, Opacity = 0 };
        band.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHoverBand");
        SetColumnSpan(band, 3);
        Children.Add(band);
        var hover = new PCLLF.Wpf.Motion.SpringMotion(this, 0, value => band.Opacity = Math.Clamp(value, 0, 1), new PCLLF.Wpf.Motion.SpringParameters(.18, 1));
        MouseEnter += (_, _) => hover.Retarget(1);
        MouseLeave += (_, _) => hover.Retarget(0);
        _check = new CheckBox { Style = resources.Style("GlassPresentationRowSelector"), Width = 28, Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _check.Click += (_, _) => { if (_actions.SelectionChanged(this)) _check.IsChecked = !Checked; };
        Children.Add(_check);
        var body = new DockPanel();
        _icon = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _icon.Clip = new RectangleGeometry(new Rect(0, 0, 32, 32), 7, 7);
        DockPanel.SetDock(_icon, Dock.Left);
        body.Children.Add(_icon);
        _name = resources.Text("");
        _name.FontSize = 15;
        _name.FontWeight = FontWeights.SemiBold;
        _name.TextWrapping = TextWrapping.NoWrap;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _description = resources.Note("", 12);
        _description.Margin = new Thickness(0, 1, 0, 0);
        _description.TextWrapping = TextWrapping.Wrap;
        _description.TextTrimming = TextTrimming.CharacterEllipsis;
        _description.MaxHeight = 32;
        // Name with status tags beside it (version, available update), like the mod list.
        var titleLine = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        titleLine.ColumnDefinitions.Add(new ColumnDefinition());
        titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_chips, 1);
        titleLine.Children.Add(_name);
        titleLine.Children.Add(_chips);
        body.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { titleLine, _description } });
        _select = new Button { Content = body, Style = resources.Style("GlassPresentationBandRow"), MinHeight = 52, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _select.Click += (_, _) => _actions.Click(this);
        _select.MouseDoubleClick += (_, e) => {
            if (!Entry.IsDirectory || _kind != InstanceResourceKind.Schematics) return;
            e.Handled = true;
            _actions.EnterFolder(Entry);
        };
        _select.PreviewKeyDown += (_, e) => {
            if (e.Key != Key.Enter || !Entry.IsDirectory || _kind != InstanceResourceKind.Schematics) return;
            e.Handled = true;
            _actions.EnterFolder(Entry);
        };
        _select.MouseRightButtonUp += (_, e) => { e.Handled = true; _actions.Details(Entry); };
        Grid.SetColumn(_select, 1);
        Children.Add(_select);
        _tools.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_tools, 2);
        Children.Add(_tools);
        MouseEnter += (_, _) => ShowActions();
        MouseLeave += (_, _) => { if (!IsKeyboardFocusWithin) _tools.Visibility = Visibility.Collapsed; };
        IsKeyboardFocusWithinChanged += (_, _) => {
            if (IsKeyboardFocusWithin) ShowActions();
            else if (!IsMouseOver) _tools.Visibility = Visibility.Collapsed;
        };
        var line = new Border { Height = .7, Margin = new Thickness(76, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        Grid.SetColumnSpan(line, 3);
        Children.Add(line);
    }

    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    private void AddChip(string text, string fill, string foreground)
    {
        var label = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
        label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        var chip = new Border { Child = label, CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center };
        chip.SetResourceReference(Border.BackgroundProperty, fill);
        _chips.Children.Add(chip);
    }

    public void Bind(InstanceResourceEntry entry, InstanceResourceOnlineInfo? online)
    {
        Entry = entry;
        _online = online;
        string localName = entry.IsDirectory ? entry.Name : System.IO.Path.GetFileNameWithoutExtension(entry.Name);
        _name.Text = string.IsNullOrWhiteSpace(online?.ProjectName) ? localName : online.ProjectName;
        _chips.Children.Clear();
        if (online?.CurrentVersion is { } version && (version.Version ?? version.Display) is { Length: > 0 } versionText)
            AddChip(versionText, "GlassPresentationField", "GlassPresentationSecondary");
        if (online?.HasUpdate == true) AddChip("可更新", "GlassPresentationSelected", "GlassPresentationText");
        _description.Text = localName + ": " + entry.Description;
        if (!string.Equals(_artworkAddress, online?.IconUrl, StringComparison.Ordinal) || !ReferenceEquals(entry.Icon, _fallback) || _icon.Source is null)
        {
            _artworkAddress = online?.IconUrl;
            _requestedArtwork = null;
            _fallback = entry.Icon;
            _icon.Source = entry.Icon ?? GlassShellRuntime.LoadInstanceIcon(ModBase.PathImage + "Icons/NoIcon.png");
        }
        if (_artworkAddress is not null && _requestedArtwork is null) _queueArtwork();
        AutomationProperties.SetName(_check, "选择 " + entry.Name);
        AutomationProperties.SetName(_select, _name.Text);
        _select.ToolTip = entry.Name + "\n" + entry.Description + (string.IsNullOrWhiteSpace(online?.Status) ? "" : "\n" + online.Status);
        RefreshFavorite();
        if (_update is not null) _update.Visibility = online?.HasUpdate == true ? Visibility.Visible : Visibility.Collapsed;
    }

    internal async void LoadArtwork()
    {
        if (_artworkAddress is not { Length: > 0 } address || _requestedArtwork == address) return;
        _requestedArtwork = address;
        var image = await ResourceThumbnails.ReadLocalResourceAsync(address);
        if (_artworkAddress == address && image is not null) _icon.Source = image;
    }

    private void RefreshFavorite()
    {
        if (_favorite is not null) GlassLocalModRow.SetFavoriteState(_favorite, _online?.Project);
    }

    private void ShowActions()
    {
        if (Entry is null) return;
        _tools.Visibility = Visibility.Visible;
        if (_tools.Children.Count != 0) return;
        if (_actions.CanPlay()) Add("启动此版本并进入存档", LocalResourceUi.PlayLogo, _actions.Play);
        if (_preview is not null && !Entry.IsDirectory) Add("3D 预览", LocalResourceUi.PlayLogo, _preview);
        _update = Add("更新资源", ModBase.Logo.IconButtonArrowUp, _actions.Update);
        _update.Visibility = _online?.HasUpdate == true ? Visibility.Visible : Visibility.Collapsed;
        _favorite = Add("收藏", ModBase.Logo.IconButtonLikeLine, _ => {
            if (_online?.Project is { } project) ResourceFavorites.Toggle(project, _online.CurrentVersion);
        });
        RefreshFavorite();
        Add("详情", ModBase.Logo.IconButtonInfo, _actions.Details);
        Add("打开文件位置", ModBase.Logo.IconButtonOpen, _actions.Open);
        Add("重命名", ModBase.Logo.IconButtonEdit, _actions.Rename);
        Add("删除", ModBase.Logo.IconButtonDelete, _actions.Delete);
    }

    private Button Add(string text, string icon, Action<InstanceResourceEntry> handler)
    {
        var button = GlassLocalModRow.CreateButton(text, icon);
        button.Click += (_, _) => handler(Entry);
        _tools.Children.Add(button);
        return button;
    }
}

internal sealed class GlassResourceScreenshot : Grid, IResourcePageThumbnail<InstanceResourceEntry>
{
    private readonly CheckBox _check;
    private readonly ResourceThumbnailController _thumbnail;
    public FrameworkElement Element => this;
    public InstanceResourceEntry Entry { get; private set; } = null!;
    public bool Checked { get => _thumbnail.Checked; set => _thumbnail.Checked = value; }

    internal GlassResourceScreenshot(GlassPageResources resources, Action<IResourcePageThumbnail<InstanceResourceEntry>> changed,
        Action<InstanceResourceEntry, int> activate)
    {
        Margin = new Thickness(5, 5, 5, 10 + GlassPageLayout.CardGap(GlassPageLayout.CardRole.SettingsSection));
        Focusable = true;
        Background = Brushes.Transparent;
        var image = new Image { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var preview = new Border { CornerRadius = new CornerRadius(4), MinHeight = 160, ClipToBounds = true, Child = image };
        preview.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
        var name = resources.Text("");
        name.FontSize = 13.5;
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(3, 8, 3, 3);
        var description = resources.Note("", 12);
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        description.TextWrapping = TextWrapping.NoWrap;
        description.Margin = new Thickness(3, 0, 3, 0);
        var content = new StackPanel { Children = { preview, name, description } };
        var frame = GlassPageResources.Card(content, GlassPageLayout.CardRole.SettingsSection);
        frame.Margin = new Thickness();
        Children.Add(frame);
        var border = new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationAccent");
        Children.Add(border);
        _check = new CheckBox { Style = resources.Style("GlassPresentationSettingsCheckBox"), Margin = new Thickness(15),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Children.Add(_check);
        _thumbnail = new ResourceThumbnailController(new ResourceThumbnailController.View(this, image, name, description, border, _check,
            () => _check.IsChecked == true, value => _check.IsChecked = value, () => Entry, entry => Entry = (InstanceResourceEntry)entry),
            new ResourceThumbnailController.Host((path, width) => InstanceResourceStore.LoadImage(path, width),
                work => ModBase.RunInUi(work), (error, message) => Logger.Warn(error, message),
                (surface, hover, selected, pressed) => {
                    surface.Opacity = selected || hover || pressed ? 1 : 0;
                    surface.SetResourceReference(Border.BackgroundProperty, selected ? "GlassPresentationSelected" : "GlassPresentationHoverBand");
                },
                () => true, () => { }));
        _check.Click += (_, _) => _thumbnail.CheckChanged();
        _thumbnail.Changed += () => changed(this);
        _thumbnail.ClickRequested += count => activate(Entry, count);
        PreviewKeyDown += (_, e) => {
            if (e.Key != Key.Enter || e.OriginalSource is CheckBox) return;
            _thumbnail.Activate(2);
            e.Handled = true;
        };
    }
    public void Bind(InstanceResourceEntry entry)
    {
        _thumbnail.Bind(entry);
        AutomationProperties.SetName(_check, "选择截图 " + entry.Name);
    }
    public void UpdateViewport(FrameworkElement viewport) => _thumbnail.UpdateViewport(viewport);
    public void ReleaseThumbnail() => _thumbnail.ReleaseThumbnail();
}
