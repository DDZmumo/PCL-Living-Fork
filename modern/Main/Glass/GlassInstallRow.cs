// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Wpf.Motion;
using InstallMenus = PCLCS.MinecraftInstallMenuController<PCL.GlassInstallRow, PCL.GlassInstallRow, System.Action<PCL.GlassInstallRow>?, PCL.ModDownload.DlOptiFineListEntry, PCL.ModDownload.DlForgelikeEntry, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ModDownload.DlLiteLoaderListEntry, PCL.ResourceVersion>;

namespace PCL;

internal sealed class GlassInstallRow : Grid
{
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _subtitle;
    private List<MinecraftInstallMenuButton>? _actions;
    private Action<object, EventArgs>? _buildActions;
    private FrameworkElement? _menuTarget;
    internal string Title { get; }
    internal string? Logo { get; }
    internal string Info { get => _subtitle.Text; private set => _subtitle.Text = value; }

    private GlassInstallRow(MinecraftInstallListItem item, Action<GlassInstallRow>? select, Action<object, EventArgs>? buildActions)
    {
        Title = item.Title ?? "";
        Logo = item.Logo;
        Tag = item.Tag;
        _buildActions = buildActions;
        MinHeight = 52;
        Background = Brushes.Transparent;
        var band = new Border { IsHitTestVisible = false, Opacity = 0 };
        band.SetResourceReference(Border.BackgroundProperty, "GlassPresentationHoverBand");
        Children.Add(band);
        var hover = new SpringMotion(this, 0, value => band.Opacity = Math.Clamp(value, 0, 1), new SpringParameters(.18, 1));
        MouseEnter += (_, _) => hover.Retarget(1);
        MouseLeave += (_, _) => hover.Retarget(0);

        var icon = new Image {
            Source = Icon(Logo), Width = 32, Height = 32, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
        };
        var title = new TextBlock { Text = Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle = new TextBlock { Text = item.Info ?? "", Margin = new Thickness(0, 2, 0, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var details = new StackPanel { Children = { title, _subtitle }, VerticalAlignment = VerticalAlignment.Center };
        var content = new DockPanel { Children = { icon, details } };
        DockPanel.SetDock(icon, Dock.Left);
        var button = new Button {
            Content = content, Height = 52, Style = GlassDialogControls.Style("GlassPresentationBandRow")
        };
        AutomationProperties.SetName(button, Title);
        button.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = _subtitle });
        button.SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(TextBlock.Text)) { Source = _subtitle });
        button.Click += (_, _) => select?.Invoke(this);
        Children.Add(button);
        var separator = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(52, 0, 8, 0), IsHitTestVisible = false };
        separator.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        Children.Add(separator);

        if (buildActions is not null) {
            SetContextMenuTarget(this);
            Unloaded += (_, _) => CloseContextMenu();
        }
    }

    internal static ImageSource? Icon(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        if (!Icons.TryGetValue(address, out var icon))
            Icons[address] = icon = GlassShellRuntime.LoadInstanceIcon(address);
        return icon;
    }

    private void BuildContextMenu(object sender, ContextMenuEventArgs args)
    {
        if (_buildActions is { } build) {
            _buildActions = null;
            build(this, EventArgs.Empty);
        }
        if (_actions is not { Count: > 0 } actions) { args.Handled = true; return; }
        var target = (FrameworkElement)sender;
        var menu = new GlassActionMenu(target, !ModTheme.IsDarkTheme);
        menu.CanActivateOwner = () => ReferenceEquals(target.ContextMenu, menu) && IsLoaded && IsVisible && IsHitTestVisible;
        foreach (MinecraftInstallMenuButton action in actions) {
            string icon = action.Icon switch {
                MinecraftInstallMenuIcon.Save => ModBase.Logo.IconButtonSave,
                MinecraftInstallMenuIcon.Info => ModBase.Logo.IconButtonInfo,
                MinecraftInstallMenuIcon.Server => ModBase.Logo.IconButtonServer,
                _ => ModBase.Logo.IconButtonList
            };
            menu.AddCommand(action.ToolTip, () => action.Click(this, new RoutedEventArgs(MenuItem.ClickEvent, this)), iconData: icon,
                allowServerAction: action.Icon == MinecraftInstallMenuIcon.Server);
        }
        target.ContextMenu = menu;
    }

    private void CloseContextMenu()
    {
        if (_menuTarget?.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
    }

    internal void SetContextMenuTarget(FrameworkElement? target)
    {
        if (_menuTarget is { } previous) {
            CloseContextMenu();
            previous.ContextMenuOpening -= BuildContextMenu;
            previous.ClearValue(ContextMenuProperty);
        }
        _menuTarget = target;
        if (target is null) return;
        // WPF uses the placeholder for both mouse and keyboard openings, including ListBoxItem focus.
        target.ContextMenu = new ContextMenu();
        target.ContextMenuOpening += BuildContextMenu;
    }

    internal static InstallMenus CreateMenus(Action<ModDownload.DlOptiFineListEntry> saveOptiFine,
        Action<ModDownload.DlForgelikeEntry> saveForgelike, Action<ModDownload.DlLiteLoaderListEntry> saveLiteLoader,
        Action<Func<(string Title, JToken Json)>, bool> saveMinecraft) => new(new() {
            Virtualize = (create, _) => create(),
            CreateItem = (item, select, actions) => new GlassInstallRow(item, select, actions),
            Initialize = item => item,
            Title = item => item.Title,
            Info = item => item.Info,
            SetInfo = (item, info) => item.Info = info,
            SetButtons = (item, actions) => ((GlassInstallRow)item)._actions = actions,
            ImageRoot = () => ModBase.PathImage,
            DebugMode = () => ModBase.ModeDebug,
            OpenWebsite = ModBase.OpenWebsite,
            SaveOptiFine = saveOptiFine,
            SaveForgelike = saveForgelike,
            SaveLiteLoader = saveLiteLoader,
            SaveMinecraft = saveMinecraft
        });
}

/// <summary>A bounded native viewport for expanding long Minecraft version categories.</summary>
internal sealed class GlassInstallVersionList : ListBox
{
    private readonly Func<JObject, GlassInstallRow> _createRow;

    internal GlassInstallVersionList(int count, Func<JObject, GlassInstallRow> createRow)
    {
        _createRow = createRow;
        Style = GlassDialogControls.Style("GlassPresentationPickerList");
        ItemContainerStyle = GlassDialogControls.Style("GlassInstallVersionListItem");
        BorderThickness = new Thickness();
        Background = Brushes.Transparent;
        MaxHeight = Math.Min(440, Math.Max(52, count * 52));
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        var container = (ListBoxItem)element;
        GlassInstallRow row = _createRow((JObject)item);
        container.Content = row;
        if (row.ContextMenu is not null) row.SetContextMenuTarget(container);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (((ListBoxItem)element).Content is GlassInstallRow row) row.SetContextMenuTarget(null);
        element.ClearValue(ContentControl.ContentProperty);
        base.ClearContainerForItemOverride(element, item);
    }
}
