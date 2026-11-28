// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MeloongCore;
using PCLLF.Presentation.Mods;
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed class GlassLocalModRow : Grid
{
    private readonly PageInstanceMod _owner;
    private readonly CheckBox _check;
    private readonly Image _art = new() { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center,
        Clip = new RectangleGeometry(new Rect(0, 0, 32, 32), 7, 7) };
    private readonly Border _disabledBadge = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, -5, -3),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    // The description may use two lines; longer text is trimmed at the end of the second.
    private readonly TextBlock _subtitle = new() { FontSize = 12, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32 };
    private readonly StackPanel _labels = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly LinearGradientBrush _actionMask = new(Colors.Black, Colors.Transparent, new Point(), new Point(1, 0)) {
        MappingMode = BrushMappingMode.Absolute
    };
    private readonly Button _update;
    private readonly Button _favorite;
    private readonly Button _body;
    private ResourceProject? _displayedProject;
    private ResourceProject? _requestedArtwork;
    private string? _loaderWarning;
    private bool _actionsInitialized;
    public LocalResourceFile Entry { get; }
    public bool Checked { get => _check.IsChecked == true; set => _check.IsChecked = value; }
    internal event Action<GlassLocalModRow>? SelectionChanged;

    internal GlassLocalModRow(PageInstanceMod owner, LocalResourceFile entry, bool selected)
    {
        _owner = owner;
        Entry = entry;
        MinHeight = 52;
        Background = Brushes.Transparent; // the whole row, not just the text, tracks the pointer
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        // Edge-to-edge hover band behind the whole row; it fades in and out on a short spring.
        var band = new Border { IsHitTestVisible = false, Opacity = 0 };
        band.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHoverBand");
        SetColumnSpan(band, 2);
        Children.Add(band);
        var hover = new PCLLF.Wpf.Motion.SpringMotion(this, 0, value => band.Opacity = Math.Clamp(value, 0, 1), new PCLLF.Wpf.Motion.SpringParameters(.18, 1));
        MouseEnter += (_, _) => hover.Retarget(1);
        MouseLeave += (_, _) => hover.Retarget(0);
        _check = new CheckBox {
            Style = GlassDialogControls.Style("GlassPresentationRowSelector"),
            Width = 28, Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = selected
        };
        _check.Checked += (_, _) => SelectionChanged?.Invoke(this);
        _check.Unchecked += (_, _) => SelectionChanged?.Invoke(this);
        _check.PreviewMouseLeftButtonDown += (_, _) => _owner.BeginSelectionRange(this);
        _check.MouseEnter += (_, _) => _owner.ExtendSelectionRange(this);
        Children.Add(_check);

        // Keep the filename aligned with ordinary rows; the update glyph follows the title.
        _update = CreateButton("更新", GlassSelectionDock.UpdateIcon);
        ((Path)_update.Content).SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
        _update.Width = _update.Height = 21;
        _update.MinWidth = _update.MinHeight = 0;
        _update.Padding = new Thickness(3);
        _update.Margin = new Thickness(2, 0, 0, 0);
        _update.VerticalAlignment = VerticalAlignment.Center;
        _update.HorizontalContentAlignment = HorizontalAlignment.Center;
        _update.VerticalContentAlignment = VerticalAlignment.Center;
        _update.Opacity = .4;
        _update.Visibility = Visibility.Collapsed;
        ToolTipService.SetInitialShowDelay(_update, 100);
        _update.Click += (_, args) => { args.Handled = true; Update(); };
        _update.MouseRightButtonUp += (_, args) => { args.Handled = true; OpenChangelog(); };
        var titleLine = new Grid { HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 21 };
        titleLine.ColumnDefinitions.Add(new ColumnDefinition());
        titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleLine.Children.Add(_title);
        Grid.SetColumn(_update, 1);
        titleLine.Children.Add(_update);
        // Status at a glance beside the name: version, available update, disabled, loader problems.
        Grid.SetColumn(_chips, 2);
        titleLine.Children.Add(_chips);
        _labels.Children.Add(titleLine);
        _labels.Children.Add(_subtitle);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var content = new DockPanel();
        var artwork = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0), Children = { _art, _disabledBadge } };
        _disabledBadge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSecondary");
        _disabledBadge.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationPopup");
        var minus = new Border { Width = 10, Height = 2, CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        minus.SetResourceReference(Border.BackgroundProperty, "GlassPresentationPopup");
        _disabledBadge.Child = minus;
        DockPanel.SetDock(artwork, Dock.Left);
        content.Children.Add(artwork);
        content.Children.Add(_labels);
        _body = new Button { Content = content, Style = GlassDialogControls.Style("GlassPresentationBandRow"),
            MinHeight = 52, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _body.Click += (_, _) => Checked = !Checked;
        _body.PreviewMouseRightButtonUp += OpenDetails;
        _check.PreviewMouseRightButtonUp += OpenDetails;
        SetColumn(_body, 1);
        Children.Add(_body);

        _favorite = CreateButton("收藏", ModBase.Logo.IconButtonLikeLine);
        _favorite.Click += (_, _) => { if (Entry.Project is { } project) ResourceFavorites.Toggle(project, Entry.ProjectVersion); };
        ResourceFavorites.Observe(this, (_, _) => RefreshFavorite());
        // Row actions (update, favorite and the page's commands) appear while the row is hovered or focused.
        _actions.Children.Add(_favorite);
        // Overlay the actions within the same column so the wrapped text retains its measured size.
        SetColumn(_actions, 1);
        Children.Add(_actions);
        _actions.Visibility = Visibility.Collapsed;
        _actions.SizeChanged += (_, _) => UpdateActionMask();
        _labels.SizeChanged += (_, _) => UpdateActionMask();
        var separator = new Border { Height = .7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(76, 0, 8, 0), IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        SetColumnSpan(separator, 2);
        Children.Add(separator);
        MouseEnter += (_, _) => RefreshActionVisibility(true);
        MouseLeave += (_, _) => RefreshActionVisibility(IsKeyboardFocusWithin);
        IsKeyboardFocusWithinChanged += (_, _) => RefreshActionVisibility(IsMouseOver || IsKeyboardFocusWithin);
        Loaded += (_, _) => Refresh();
    }

    private void OpenDetails(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is Visual hit && (ReferenceEquals(hit, _update) || _update.IsAncestorOf(hit))) return;
        args.Handled = true;
        _owner.Info_Click(this, args);
    }

    internal void SetButtons(IEnumerable<Button> buttons)
    {
        while (_actions.Children.Count > 1) _actions.Children.RemoveAt(1);
        foreach (var button in buttons) _actions.Children.Add(button);
    }

    private void RefreshActionVisibility(bool show)
    {
        if (show && !_actionsInitialized) {
            _actionsInitialized = true;
            _owner.CreateRowActions(this);
        }
        _actions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionMask();
    }

    private void UpdateActionMask()
    {
        if (_actions.Visibility != Visibility.Visible || _actions.ActualWidth <= 0) {
            _labels.OpacityMask = null;
            return;
        }
        double fadeEnd = _actions.TranslatePoint(new Point(), _labels).X - 4;
        if (fadeEnd <= 0) {
            _labels.OpacityMask = Brushes.Transparent;
            return;
        }
        _actionMask.StartPoint = new Point(Math.Max(0, fadeEnd - 12), 0);
        _actionMask.EndPoint = new Point(fadeEnd, 0);
        _labels.OpacityMask = _actionMask;
    }

    internal void RefreshLoaderWarning(string folder, ModDependencyInspector.InspectionContext context)
    {
        _loaderWarning = Entry.IsEnabled ? ModDependencyInspector.LoaderWarningFor(folder, Entry.File.FullName, context) : null;
        if (IsLoaded) Refresh();
    }

    internal void RefreshFavorite() => SetFavoriteState(_favorite, Entry.Project);

    internal static void SetFavoriteState(Button button, ResourceProject? project)
    {
        bool available = project is not null && ResourceFavorites.CanFavorite(project);
        button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) return;
        button.IsEnabled = !ResourceFavorites.IsBusy(project);
        bool selected = ResourceFavorites.IsFavorite(project);
        button.Content = Glyph(selected ? ModBase.Logo.IconButtonLikeFill : ModBase.Logo.IconButtonLikeLine, 15);
        button.ToolTip = selected ? "取消收藏" : "收藏";
        AutomationProperties.SetName(button, (string)button.ToolTip);
    }

    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    private void RefreshChips(bool canUpdate)
    {
        _chips.Children.Clear();
        void Add(string text, string fill, string foreground)
        {
            var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
            label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
            var chip = new Border { Child = label, CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(Border.BackgroundProperty, fill);
            _chips.Children.Add(chip);
        }
        if (!string.IsNullOrEmpty(Entry.Version)) Add(Entry.Version, "GlassPresentationField", "GlassPresentationSecondary");
        if (canUpdate) Add("可更新", "GlassPresentationSelected", "GlassPresentationText");
        if (!Entry.IsEnabled) Add("已禁用", "GlassPresentationField", "GlassPresentationSecondary");
        if (!string.IsNullOrEmpty(_loaderWarning)) Add(_loaderWarning, "GlassPresentationCautionFill", "GlassPresentationCautionText");
    }

    public void Refresh()
    {
        string name = PathUtils.GetFileNameWithoutExtension(Entry.EnabledName ?? Entry.File.Name);
        var project = Entry.Project;
        string title = project?.GetControlTitle(false).Title ?? Entry.Display ?? name;
        string description = project?.Description ?? Entry.Description ?? "";
        string metadata = "";
        bool useFileName = Settings.Get<int>("ToolModLocalNameStyle") == 1;
        _title.Text = useFileName ? name : title;
        string tags = project is null ? "" : string.Join(" / ", project.Tags ?? []);
        _subtitle.Text = string.Join(" · ", new[] { metadata, useFileName ? title : name, tags, description.ReplaceLineEndings(" ") }.Where(value => value.Length > 0));
        AutomationProperties.SetName(_body, _title.Text);
        AutomationProperties.SetName(_check, "选择 " + _title.Text);
        _body.ToolTip = _title.Text + "\n" + _subtitle.Text;
        _title.TextDecorations = Entry.IsEnabled ? null : TextDecorations.Strikethrough;
        _title.SetResourceReference(TextBlock.ForegroundProperty, Entry.IsEnabled ? "GlassPresentationText" : "GlassPresentationSecondary");
        _disabledBadge.Visibility = Entry.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        bool canUpdate = LauncherInstanceContext.Instance is not null && Entry.HasUpdate;
        AutomationProperties.SetItemStatus(_body, string.Join(" · ", new[] {
            Entry.IsEnabled ? "" : "已禁用", canUpdate ? "可更新" : ""
        }.Where(value => value.Length > 0)));
        _update.Visibility = canUpdate ? Visibility.Visible : Visibility.Collapsed;
        RefreshChips(canUpdate);
        _update.ToolTip = canUpdate ? UpdateDescription() + "\n点击以更新，右键查看更新日志。" : null;
        RefreshFavorite();
        if (!ReferenceEquals(_displayedProject, project) || _art.Source is null) {
            _displayedProject = project;
            _art.Source = GlassShellRuntime.LoadInstanceIcon(ModBase.PathImage + "Icons/NoIcon.png");
        }
        if (project is not null && IsLoaded && !ReferenceEquals(_requestedArtwork, project)) {
            _requestedArtwork = project;
            _ = RefreshArtwork(project);
        }
    }

    private async System.Threading.Tasks.Task RefreshArtwork(ResourceProject project)
    {
        var image = await ResourceThumbnails.ReadAsync(project);
        if (ReferenceEquals(Entry.Project, project) && image is not null) _art.Source = image;
    }

    private string UpdateDescription() =>
        LegacyLocalModText.Compare<LocalResourceFile, ResourceProject, ResourceVersion>(Entry, StringUtils.FormatTimeSpan);

    private void Update()
    {
        if (LauncherInstanceContext.Instance is null || !Entry.HasUpdate) return;
        int action = ModMain.MyMsgBox($"是否要更新 {Entry.Display}？\r\n\r\n{UpdateDescription()}", "Mod 更新确认", "更新", "查看更新日志", "取消");
        if (action == 1) _owner.UpdateMods([Entry]);
        else if (action == 2) OpenChangelog();
    }

    private void OpenChangelog()
    {
        string? modrinth = Entry.ChangelogUrls.FirstOrDefault(url => url.Contains("modrinth.com", StringComparison.OrdinalIgnoreCase));
        string? curseforge = Entry.ChangelogUrls.FirstOrDefault(url => url.Contains("curseforge.com", StringComparison.OrdinalIgnoreCase));
        string? address;
        if (modrinth is null || curseforge is null) address = Entry.ChangelogUrls.FirstOrDefault();
        else address = ModMain.MyMsgBox("要在哪个网站上查看更新日志？", "查看更新日志", "Modrinth", "CurseForge", "取消") switch {
            1 => modrinth, 2 => curseforge, _ => null
        };
        if (address is not null) ModBase.OpenWebsite(address);
    }

    internal static Button CreateButton(string label, string icon, double scale = 1)
    {
        var button = new Button { Content = Glyph(icon, 15 * scale), Width = 29, Height = 32, Padding = new Thickness(5),
            Style = GlassDialogControls.Style("GlassPresentationRowButton"), ToolTip = label };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Path Glyph(string icon, double size)
    {
        var path = new Path { Data = Geometry.Parse(icon), Width = size, Height = size, Stretch = Stretch.Uniform };
        path.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
        return path;
    }
}
