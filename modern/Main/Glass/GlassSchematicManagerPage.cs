// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeloongCore;
using Instance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal sealed class GlassSchematicManagerPage : Grid, IDisposable
{
    private readonly GlassPageResources _resources;
    private readonly ComboBox _instances;
    private readonly TextBlock _path;
    private readonly GlassLocalResourcesPage _files;
    private readonly ModLoader.LoaderBase _loader = ModMinecraft.McInstanceListLoader;
    private Instance? _selected;
    private string? _preferredPath;
    private bool _active, _rebinding;

    internal GlassSchematicManagerPage(FormMain window)
    {
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        // Lead: what this page manages and where, with the instance picker as its main control.
        _path = _resources.Note("", 12.5);
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _path.TextWrapping = TextWrapping.NoWrap;
        _instances = new ComboBox { Style = _resources.Style("GlassPresentationComboBox"), Width = 240, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_instances, "选择投影所属实例");
        _instances.SelectionChanged += (_, _) => { if (!_rebinding) ApplySelection((_instances.SelectedItem as ComboBoxItem)?.Tag as Instance); };
        var refresh = new Button { Content = "刷新", Style = _resources.Style("GlassPresentationButton"), MinWidth = 62 };
        refresh.Click += (_, _) => Refresh();
        var picker = new StackPanel { Orientation = Orientation.Horizontal, Children = { _instances, refresh } };
        string icon = ResourceBrowserCatalog.Find(FormMain.PageSubType.DownloadDataPack)!.Icon;
        var header = new GlassHeroCard { Icon = icon, Title = "投影管理", Status = _path, Actions = picker }.Built();
        header.Margin = new Thickness(0, 10, 0, 4);
        Children.Add(header);

        _files = new GlassLocalResourcesPage(window, InstanceResourceKind.Schematics, () => _selected);
        SetRow(_files, 1);
        Children.Add(_files);
    }

    internal ScrollViewer? Scroll => _files.ActiveScroll;
    internal bool ClosePreview() => _files.CloseSchematicPreview();

    internal void Open(bool loadData, Instance? preferred)
    {
        _active = true;
        if (preferred is not null) _preferredPath = preferred.PathVersion;
        _resources.ApplyPalette();
        _loader.OnStateChangedUi += LoaderChanged;
        Rebind();
        _files.Open(loadData);
        if (loadData && _loader.State != ModBase.LoadState.Finished) LauncherGameFolders.RefreshInstances(force: false);
    }

    internal void Refresh()
    {
        if (!_active) return;
        LauncherGameFolders.RefreshInstances(force: true);
        _files.Refresh();
    }

    private void LoaderChanged(ModLoader.LoaderBase _, ModBase.LoadState state, ModBase.LoadState former)
    {
        // Rebind once the scan has finished; mid-scan states would empty the picker and make instances flicker.
        if (_active && _loader.State == state && state == ModBase.LoadState.Finished) Rebind();
    }

    private void Rebind()
    {
        Instance[] instances = LauncherSchematicApplication.Instances();
        string? preferred = _preferredPath ?? ModMinecraft.McInstanceSelected?.PathVersion;
        var selected = instances.FirstOrDefault(instance => preferred is not null && PathUtils.IsSame(instance.PathVersion, preferred)) ?? instances.FirstOrDefault();
        _rebinding = true;
        // Explicit items: the instance's display name, with the instance itself as the tag.
        _instances.Items.Clear();
        foreach (var instance in instances) {
            var item = new ComboBoxItem { Content = instance.Name, Tag = instance, ToolTip = instance.PathVersion };
            _instances.Items.Add(item);
            if (ReferenceEquals(instance, selected)) _instances.SelectedItem = item;
        }
        _instances.IsEnabled = instances.Length > 0;
        _rebinding = false;
        ApplySelection(selected);
    }

    private void ApplySelection(Instance? instance)
    {
        bool changed = !ReferenceEquals(_selected, instance);
        _selected = instance;
        if (instance is not null) _preferredPath = instance.PathVersion;
        _files.Visibility = instance is null ? Visibility.Collapsed : Visibility.Visible;
        _path.Text = instance is null ? (_loader.State == ModBase.LoadState.Loading ? "正在读取实例……" : "当前没有可用的游戏实例。") :
            SchematicFolder(instance);
        _path.ToolTip = instance is null ? null : _path.Text;
        if (changed) { _files.CloseSchematicPreview(); _files.Scroll.ScrollToTop(); }
        _files.Refresh();
    }

    // Reading the game folder loads the instance; an unconfirmed managed instance reports why instead of failing the page.
    private static string SchematicFolder(Instance instance)
    {
        try { return System.IO.Path.Combine(instance.PathIndie, "schematics"); }
        catch (Exception error) { return "无法确定游戏文件夹：" + error.Message; }
    }

    internal void RefreshPalette() { _resources.ApplyPalette(); _files.PaletteChanged(); }

    internal void Leave()
    {
        if (!_active) return;
        _active = false;
        _loader.OnStateChangedUi -= LoaderChanged;
        _files.Leave();
    }

    public void Dispose() { Leave(); _files.Dispose(); }
}
