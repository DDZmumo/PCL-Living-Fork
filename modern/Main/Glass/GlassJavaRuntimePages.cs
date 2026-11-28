// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MeloongCore;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Graphics = PCL.GlassJavaGraphics;
using Path = System.Windows.Shapes.Path;

namespace PCL;

internal abstract class GlassJavaRuntimePage : ContentControl
{
    protected GlassPageResources Theme { get; }
    protected StackPanel Sections { get; } = new() { Margin = new Thickness(0, 6, 0, 13) };
    internal ScrollViewer Scroll { get; }

    protected GlassJavaRuntimePage()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        Theme = new GlassPageResources(this);
        Scroll = new ScrollViewer { Content = Sections, Style = Theme.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        Content = new ScrollEdgeFade(Scroll);
        Loaded += (_, _) => ApplyPalette();
    }

    internal void ApplyPalette() => Theme.ApplyPalette();

    protected void AddSection(string title, UIElement content, string note = "")
    {
        var section = new StackPanel { Children = { Theme.Section(title, note), content } };
        Sections.Children.Add(GlassPageResources.FormPlate(section));
    }

    protected TextBlock Heading(string text)
    {
        var heading = Theme.Text(text);
        heading.FontSize = 16;
        heading.FontWeight = FontWeights.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.TextWrapping = TextWrapping.NoWrap;
        return heading;
    }

    protected static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(56, 0, 0, 0), IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        return line;
    }

    protected Button Tile(UIElement content, string name, Action invoke)
    {
        var tile = new Button { Content = content, Template = (ControlTemplate)FindResource("GlassPresentationPlainButtonTemplate"), Padding = new Thickness(14, 12, 14, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1.2), Cursor = Cursors.Hand };
        tile.SetResourceReference(Control.ForegroundProperty, "GlassPresentationText");
        SetTileChosen(tile, false);
        AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) => { invoke(); };
        return tile;
    }

    protected static void SetTileChosen(Button tile, bool chosen)
    {
        if (Equals(tile.Tag, chosen)) return;
        tile.Tag = chosen;
        tile.SetResourceReference(Control.BackgroundProperty, chosen ? "GlassPresentationSelected" : "GlassPresentationField");
        tile.SetResourceReference(Control.BorderBrushProperty, chosen ? "GlassPresentationAccent" : "GlassPresentationEdge");
    }

    protected Button Command(string title, Action run, bool primary = false)
    {
        var button = new Button { Content = title, Style = Theme.Style(primary ? "GlassPresentationAccentButton" : "GlassPresentationButton"),
            Margin = new Thickness(0, 0, 7, 5) };
        button.Click += (_, _) => run();
        return button;
    }

    protected Button Icon(string data, string title, Action run)
    {
        var glyph = new Path { Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = 16, Height = 16 };
        glyph.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
        var button = new Button { Content = glyph, Style = Theme.Style("GlassPresentationButton"), ToolTip = title,
            Width = 36, Height = 35, MinHeight = 35, Padding = new Thickness(0), Margin = new Thickness(5, 0, 0, 0) };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => run();
        return button;
    }

    private enum TagTone { Neutral, Accent, Warn }

    private FrameworkElement JavaTag(string text, TagTone tone)
    {
        var tag = new Grid { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { CornerRadius = new CornerRadius(6), Opacity = tone == TagTone.Neutral ? .08 : .15 };
        fill.SetResourceReference(Border.BackgroundProperty, tone switch { TagTone.Accent => "GlassPresentationAccent", TagTone.Warn => "GlassPresentationWarnText", _ => "GlassPresentationText" });
        var label = Theme.Text(text); label.FontSize = 11.5; label.Margin = new Thickness(7, 1, 7, 2);
        label.SetResourceReference(TextBlock.ForegroundProperty, tone switch { TagTone.Accent => "GlassPresentationAccent", TagTone.Warn => "GlassPresentationWarnText", _ => "GlassPresentationSecondary" });
        tag.Children.Add(fill); tag.Children.Add(label);
        return tag;
    }

    protected FrameworkElement Row(string? vendorKey, string version, string path, IEnumerable<string> tags, IEnumerable<Button> actions, bool x86 = false)
    {
        var vendor = Graphics.VendorOf(vendorKey);
        var row = new Grid { MinHeight = 72, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new Image { Source = vendor.Logo, Width = 40, Height = 40 };
        AutomationProperties.SetName(mark, vendor.Name);
        mark.Margin = new Thickness(0, 0, 16, 0);
        row.Children.Add(mark);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        title.Style = Theme.Style("GlassPresentationTextStyle");
        title.Inlines.Add(new System.Windows.Documents.Run(vendor.Name) { FontSize = 14.5, FontWeight = FontWeights.SemiBold });
        var versionRun = new System.Windows.Documents.Run("   " + version) { FontSize = 13 };
        versionRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "GlassPresentationSecondary");
        title.Inlines.Add(versionRun);
        text.Children.Add(title);
        var tagRow = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (string tagText in tags.Where(tag => !string.IsNullOrWhiteSpace(tag))) {
            bool warn = x86 && tagText is "x86" or "32 位";
            var tag = JavaTag(tagText, warn ? TagTone.Warn : tagText is "启动器安装" or "本机已有" or "本机同版本" ? TagTone.Accent : TagTone.Neutral);
            if (warn) tag.ToolTip = $"32 位 Java 的启动堆内存上限为 {Java.X86MaxHeapMiB} MiB。";
            tagRow.Children.Add(tag);
        }
        if (tagRow.Children.Count > 0) text.Children.Add(tagRow);
        TextBlock pathText = Theme.Note(path, 11.5); pathText.TextWrapping = TextWrapping.NoWrap; pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        pathText.Margin = new Thickness(0, 5, 0, 0); pathText.Opacity = .85; pathText.ToolTip = path;
        text.Children.Add(pathText);
        Grid.SetColumn(text, 1); row.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (Button action in actions) buttons.Children.Add(action);
        Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
        return row;
    }
}

internal sealed class GlassJavaManagementPage : GlassJavaRuntimePage
{
    private readonly TextBox _filter;
    private readonly TextBlock _status, _count;
    private readonly TextBlock _heroTitle;
    private readonly WrapPanel _heroVersions = new();
    private readonly StackPanel _groups = new();
    private readonly ComboBox _priority;
    private readonly TextBlock _prioritySummary;
    private readonly Button _priorityRefresh;
    private readonly Button _refresh, _import;
    private readonly Dictionary<string, string> _detectedPackages = new(StringComparer.OrdinalIgnoreCase);
    private Java[] _entries = [];
    private bool _loadData, _primed, _busy;
    private string _query = "";

    internal GlassJavaManagementPage(Action download)
    {
        // Lead card: how many runtimes were found and which major versions they cover, newest highlighted.
        _heroTitle = new TextBlock { Text = "本机 Java" };
        Sections.Children.Add(new GlassHeroCard { Icon = GlassJavaGraphics.Icon, TitleContent = _heroTitle, Status = _heroVersions }.Built());
        // Backup GlassPresentation.Java.cs 507–516 and PageSetupLaunch.xaml 288–300.
        var priority = new Grid();
        priority.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        priority.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        var description = Theme.Section("启动优先级", "自动选择优先用兼容的 64 位 Java，同位数的运行时可在下拉列表中调整顺序。");
        description.Margin = new Thickness(0, 0, 16, 0);
        description.VerticalAlignment = VerticalAlignment.Center;
        priority.Children.Add(description);
        var controls = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        _priorityRefresh = PriorityAction(ModBase.Logo.IconButtonRefresh, "搜索 Java", () => ModJava.ManuallySearchJava());
        _priorityRefresh.Margin = new Thickness(5, 0, 0, 0);
        DockPanel.SetDock(_priorityRefresh, Dock.Right);
        controls.Children.Add(_priorityRefresh);
        _priority = new PriorityComboBox { Style = Theme.Style("GlassPresentationComboBox"), MaxDropDownHeight = 260, IsTextSearchEnabled = false };
        _prioritySummary = Theme.Text("正在加载……");
        _prioritySummary.Margin = new Thickness(13, 0, 36, 0);
        _prioritySummary.FontSize = 14;
        _prioritySummary.VerticalAlignment = VerticalAlignment.Center;
        _prioritySummary.TextTrimming = TextTrimming.CharacterEllipsis;
        _prioritySummary.IsHitTestVisible = false;
        _priority.PreviewKeyDown += (_, args) => {
            if (_priority.IsDropDownOpen || args.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space)) return;
            _priority.IsDropDownOpen = true;
            args.Handled = true;
        };
        _priority.SelectionChanged += (_, _) => {
            if (_priority.SelectedItem is not ComboBoxItem choice) return;
            _priority.SelectedIndex = -1;
            _priority.IsDropDownOpen = false;
            if (choice.Tag is Java) ModMain.Hint("点击选项右侧的箭头可以进行排序，以控制 PCL 优先选择哪个 Java！", ModMain.HintType.Blue);
            else ModJava.ManuallyImportJava();
        };
        _priority.DropDownOpened += (_, _) => RenderPriority();
        _priority.DropDownClosed += (_, _) => _priority.SelectedIndex = -1;
        AutomationProperties.SetName(_priority, "启动优先级");
        controls.Children.Add(new Grid { Children = { _priority, _prioritySummary } });
        Grid.SetColumn(controls, 1);
        priority.Children.Add(controls);
        Sections.Children.Add(GlassPageResources.FormPlate(priority));
        // Header row: title, count and the list's commands on one line, the filter underneath.
        var body = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Heading("本机 Java"));
        _count = Theme.Note("", 12);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(10, 2, 0, 0);
        Grid.SetColumn(_count, 1);
        header.Children.Add(_count);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _refresh = Command("刷新", () => ModJava.ManuallySearchJava());
        _import = Command("导入", ModJava.ManuallyImportJava);
        actions.Children.Add(_refresh);
        actions.Children.Add(_import);
        actions.Children.Add(Command("下载 Java", download, primary: true));
        foreach (Button command in actions.Children) { command.MinWidth = 88; command.Margin = new Thickness(8, 0, 0, 0); }
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        body.Children.Add(header);
        var input = new Grid();
        _filter = new TextBox { Height = 36, Style = Theme.Style("GlassPresentationTextBox") };
        AutomationProperties.SetName(_filter, "筛选本机 Java");
        var prompt = Theme.Note("筛选本机 Java 的版本、发行版、架构或路径", 13);
        prompt.IsHitTestVisible = false;
        prompt.VerticalAlignment = VerticalAlignment.Center;
        prompt.Margin = new Thickness(12, 0, 0, 0);
        input.Children.Add(_filter);
        input.Children.Add(prompt);
        GlassPageResources.BindTextPlaceholder(_filter, prompt);
        _filter.TextChanged += (_, _) => Refresh();
        body.Children.Add(input);
        _status = Theme.Note("", 13);
        _status.Margin = new Thickness(2, 10, 0, 0);
        body.Children.Add(_status);
        Sections.Children.Add(GlassPageResources.FormPlate(body));
        Sections.Children.Add(_groups);
    }

    internal void Open(bool loadData)
    {
        _loadData = loadData;
        Refresh();
    }

    internal void Refresh()
    {
        ApplyPalette();
        if (!_loadData) return;
        var next = Configs.JavaList.Get()?.ToArray() ?? [];
        bool busy = ModJava.JavaListRefreshWorker.Running;
        _refresh.IsEnabled = _import.IsEnabled = _priorityRefresh.IsEnabled = !busy;
        string query = _filter.Text.Trim();
        if (_primed && busy == _busy && next.SequenceEqual(_entries, ReferenceEqualityComparer.Instance) && query == _query) return;
        _entries = next;
        _busy = busy;
        _query = query;
        _primed = true;
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rows = next.Where(java => words.All(word => (java.Folder + " " + java.Version + " " + java.Distribution + " " + java.Architecture)
            .Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        _count.Text = next.Length == 0 ? "" : rows.Length == next.Length ? $"{next.Length} 个" : $"{rows.Length} / {next.Length} 个";
        _status.Text = busy ? "正在搜索本机 Java……" : next.Length == 0 ? "尚未找到 Java，可以刷新搜索或手动导入。" :
            rows.Length == 0 ? "没有符合筛选条件的 Java。" : "";
        _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _heroTitle.Text = busy && next.Length == 0 ? "正在搜索本机 Java" : next.Length == 0 ? "尚未找到 Java" : $"已找到 {next.Length} 个 Java";
        _heroVersions.Children.Clear();
        bool newest = true;
        foreach (var major in next.GroupBy(java => Graphics.JavaMajor(java.Version)).Where(group => group.Key is not null).OrderByDescending(group => group.Key))
        {
            _heroVersions.Children.Add(GlassHeroCard.Chip($"Java {major.Key} × {major.Count()}", accent: newest));
            newest = false;
        }
        _groups.Children.Clear();
        foreach (var group in rows.GroupBy(java => Graphics.JavaMajor(java.Version)).OrderByDescending(group => group.Key))
        {
            string range = Graphics.MinecraftRange(group.Key);
            // Group title with its count inline, rows separated by hairlines.
            var pane = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            title.Children.Add(Heading(group.Key is { } major ? "Java " + major : "版本待检测"));
            var note = Theme.Note(range.Length == 0 ? $"{group.Count()} 个" : $"{group.Count()} 个 · 适用于 {range}", 12);
            note.VerticalAlignment = VerticalAlignment.Center;
            note.Margin = new Thickness(10, 2, 0, 0);
            title.Children.Add(note);
            pane.Children.Add(title);
            bool first = true;
            foreach (Java java in group) {
                if (!first) pane.Children.Add(Divider());
                first = false;
                pane.Children.Add(Installed(java));
            }
            _groups.Children.Add(GlassPageResources.FormPlate(pane));
        }
        _priority.IsEnabled = !busy;
        _prioritySummary.Text = busy ? "搜索中 …" : next.Length == 0 ? "未找到 Java，点击以导入已有的 Java" : $"共有 {next.Length} 个 Java …";
        if (_priority.IsDropDownOpen) RenderPriority();
    }

    private FrameworkElement Installed(Java java)
    {
        bool managed = JavaRuntimeDownloads.IsManaged(java);
        var actions = new List<Button> { Icon(ModBase.Logo.IconButtonOpen, "打开 Java 所在文件夹", () => ModBase.OpenExplorer(java.Folder)) };
        if (managed) actions.Add(Icon(ModBase.Logo.IconButtonDelete, "卸载此 Java", () => JavaRuntimeDownloads.StartUninstall(java)));
        string architecture = Graphics.ArchitectureLabel(java.Architecture);
        return Row(java.Distribution, java.RuntimeVersion ?? java.Version?.ToString() ?? "版本待检测", java.Folder.TrimEnd('\\'),
            [ReadPackageType(java), architecture, java.VirtualMachine ?? "", managed ? "启动器安装" : ""],
            actions, Graphics.Is32Bit(java.Architecture));
    }

    private string ReadPackageType(Java java)
    {
        if (!string.IsNullOrWhiteSpace(java.PackageType)) return java.PackageType.ToUpperInvariant();
        if (_detectedPackages.TryGetValue(java.Folder, out string? value)) return value;
        try { return _detectedPackages[java.Folder] = File.Exists(System.IO.Path.Combine(java.Folder, "javac.exe")) ? "JDK" : "JRE"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    private void RenderPriority()
    {
        _priority.Items.Clear();
        for (int index = 0; index < _entries.Length; index++)
        {
            Java java = _entries[index];
            var up = PriorityAction(ModBase.Logo.IconButtonArrowUp, "提高优先级", () => Move(java, -1));
            var down = PriorityAction(ModBase.Logo.IconButtonArrowDown, "降低优先级", () => Move(java, 1));
            up.IsEnabled = index > 0 && !_busy;
            down.IsEnabled = index + 1 < _entries.Length && !_busy;
            var folder = PriorityAction(ModBase.Logo.IconButtonOpen, "打开文件夹", () => ModBase.OpenExplorer(java.Folder));
            var remove = PriorityAction(ModBase.Logo.IconButtonDelete, "从列表中移除", () => { ModJava.ManuallyRemoveJava(java); Refresh(); });
            remove.IsEnabled = !_busy && !PathUtils.IsParentOf($@"{Paths.AppData}.minecraft\runtime\", java.Folder);
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var vendor = Graphics.VendorOf(java.Distribution);
            var mark = new Image { Source = vendor.Logo, Width = 22, Height = 22, Margin = new Thickness(0, 3, 10, 3) };
            AutomationProperties.SetName(mark, vendor.Name);
            line.Children.Add(mark);
            var name = new TextBlock { Text = java.ToString(), ToolTip = java.ToString(), VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Children = { up, down, remove, folder } };
            Grid.SetColumn(actions, 2);
            line.Children.Add(actions);
            var choice = new ComboBoxItem { Tag = java, Content = line, Style = Theme.Style("GlassPresentationComboItem"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(choice, java.ToString());
            _priority.Items.Add(choice);
        }
        var import = new ComboBoxItem { Content = "＋   导入电脑中已有的 Java…", Style = Theme.Style("GlassPresentationComboItem") };
        AutomationProperties.SetName(import, "导入电脑中已有的 Java");
        _priority.Items.Add(import);
    }

    private static Button PriorityAction(string icon, string title, Action action)
    {
        var button = GlassLocalModRow.CreateButton(title, icon);
        button.Width = 38;
        button.Height = 36;
        button.Click += (_, _) => action();
        return button;
    }

    // GlassPresentation.Controls.cs: JavaActionComboBox kept its popup aligned to the field.
    private sealed class PriorityComboBox : ComboBox
    {
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("DropDownSurface") is GlassSurface surface) {
                surface.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = this });
                surface.SetResourceReference(Border.BackgroundProperty, "GlassPresentationDialogFill");
            }
        }
        protected override void OnMouseWheel(MouseWheelEventArgs args)
        {
            if (IsDropDownOpen) base.OnMouseWheel(args);
        }
    }

    private void Move(Java java, int amount)
    {
        JavaRuntimePriority.Move(java, amount);
        ModJava.UpdateJavaLists();
        Refresh();
    }
}

internal sealed class GlassJavaDownloadPage : GlassJavaRuntimePage, IDisposable
{
    private static readonly (string Major, string Note)[] Majors = {
        ("8", "Minecraft 1.16.5 及更早"),
        ("17", "Minecraft 1.18 – 1.20.4"),
        ("21", "Minecraft 1.20.5 及以上"),
        ("25", "最新的长期支持版")
    };
    // Prefer general-purpose runtimes common in Minecraft, followed by specialized and legacy builds.
    private static readonly string[] VendorOrder = [
        "temurin", "microsoft", "zulu", "corretto", "oracle", "liberica", "oracle_open_jdk", "redhat", "semeru",
        "graalvm_community", "graalvm", "jetbrains", "dragonwell", "kona", "sap_machine", "liberica_native", "mandrel",
        "aoj", "aoj_openj9", "ojdk_build"
    ];

    private readonly DispatcherTimer _debounce;
    private readonly StackPanel _results = new();
    private readonly TextBlock _status;
    private readonly Button _cancel, _search;
    private readonly BlockCurveLoader _loader = new() { Width = 48, Height = 48, IsActive = false };
    private readonly StackPanel _loading = new() { HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 18, 0, 10), Visibility = Visibility.Collapsed };
    private readonly TextBox _major;
    private readonly Expander _vendors;
    private readonly Button _allVendors, _chooseVendor;
    private readonly TextBlock _selectedVendor;
    private readonly RotateTransform _vendorChevron = new();
    private readonly Dictionary<string, Button> _majorTiles = new(), _vendorTiles = new();
    private CancellationTokenSource? _request;
    private string? _vendor, _architecture, _package;
    private bool _active, _loadData, _disposed;

    internal GlassJavaDownloadPage()
    {
        _major = new TextBox { Text = "21", Width = 120, Height = 36, Style = Theme.Style("GlassPresentationTextBox") };
        var versions = new StackPanel();
        var majors = new UniformGrid { Columns = Majors.Length };
        for (int index = 0; index < Majors.Length; index++)
        {
            var (major, note) = Majors[index];
            var number = Theme.Text(major);
            number.FontSize = 24;
            number.FontWeight = FontWeights.Bold;
            number.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            var caption = Theme.Note("Java " + major, 12.5);
            caption.Margin = new Thickness(0, 0, 0, 4);
            var body = new StackPanel { Children = { number, caption, Theme.Note(note, 12) } };
            var tile = Tile(body, "Java " + major, () => _major.Text = major);
            tile.Margin = new Thickness(index == 0 ? 0 : 5, 0, index == Majors.Length - 1 ? 0 : 5, 0);
            _majorTiles.Add(major, tile);
            majors.Children.Add(tile);
        }
        versions.Children.Add(majors);
        AutomationProperties.SetName(_major, "Java 主版本");
        var customLabel = Theme.Note("其他主版本", 13);
        customLabel.VerticalAlignment = VerticalAlignment.Center;
        customLabel.Margin = new Thickness(0, 0, 12, 0);
        versions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0),
            Children = { customLabel, _major } });
        AddSection("Java 版本", versions, "不同版本的 Minecraft 需要不同的 Java，选择与游戏对应的主版本。");

        var vendorChoice = new StackPanel();
        var vendorList = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0), IsEnabled = false };
        _vendors = new Expander { Style = Theme.Style("GlassJavaVendorExpander"), Content = vendorList };
        var all = Graphics.AllVendors;
        _allVendors = Tile(VendorTileBody(all, all.Name, Theme.Note(all.Description, 11.5)), all.Name, () => { SetVendor(null); ShowVendors(false); });
        _allVendors.Margin = new Thickness(0, 0, 4, 0);
        _selectedVendor = Theme.Note("", 11.5);
        var chooser = VendorTileBody(Graphics.VendorOf(null), "选择发行版", _selectedVendor);
        chooser.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var arrow = new Path { Data = Geometry.Parse("M1,1 L5,5 9,1"), Stretch = Stretch.Uniform, Width = 12, Height = 7,
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = _vendorChevron };
        arrow.SetResourceReference(Shape.StrokeProperty, "GlassPresentationSecondary");
        Grid.SetColumn(arrow, 2);
        chooser.Children.Add(arrow);
        _chooseVendor = Tile(chooser, "选择发行版", () => ShowVendors(!_vendors.IsExpanded));
        _chooseVendor.Margin = new Thickness(4, 0, 0, 0);
        var order = VendorOrder.Select((key, index) => (key, index)).ToDictionary(entry => entry.key, entry => entry.index);
        foreach (var vendor in JavaRuntimeCatalog.Distributions.OrderBy(entry => order.GetValueOrDefault(entry.Key, int.MaxValue)).ThenBy(entry => entry.Value))
        {
            var style = Graphics.VendorOf(vendor.Key);
            var choice = Tile(VendorTileBody(style, vendor.Value, Theme.Note(style.Description, 11.5)), vendor.Value, () => SetVendor(vendor.Key));
            choice.Margin = new Thickness(0, 0, 8, 8);
            _vendorTiles.Add(vendor.Key, choice);
            vendorList.Children.Add(choice);
        }
        vendorChoice.Children.Add(new UniformGrid { Columns = 2, Children = { _allVendors, _chooseVendor } });
        vendorChoice.Children.Add(_vendors);
        vendorChoice.PreviewKeyDown += (_, args) => {
            if (args.Key == Key.Escape && _vendors.IsExpanded) { args.Handled = true; ShowVendors(false); }
        };
        AddSection("发行版", vendorChoice, "都是基于 OpenJDK 的构建，运行 Minecraft 没有明显差别，任选其一即可。");
        RefreshTiles();

        var options = new StackPanel();
        string[] architectures = RuntimeInformation.OSArchitecture switch {
            Architecture.X64 => ["x64", "x86"], Architecture.Arm64 => ["arm64", "x86"], Architecture.X86 => ["x86"], _ => []
        };
        _architecture = architectures.FirstOrDefault();
        options.Children.Add(Choices("架构", architectures, value => { _architecture = value; Schedule(); }));
        options.Children.Add(Choices("包类型", ["全部", "JDK", "JRE"], value => {
            _package = value == "全部" ? null : value.ToLowerInvariant();
            Schedule();
        }));
        AddSection("架构与包类型", options, "运行游戏只需要 JRE；JDK 额外包含开发工具，体积更大。");
        var catalog = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = Theme.Note("选择下载条件后点击搜索。", 12);
        _status.Margin = new Thickness(0, 3, 0, 0);
        header.Children.Add(new StackPanel { Children = { Heading("可下载的安装包"), _status } });
        var commands = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _search = Command("搜索", StartSearch);
        _search.Margin = new Thickness();
        _search.MinWidth = 88;
        _cancel = Command("取消", Cancel);
        _cancel.Margin = new Thickness(8, 0, 0, 0);
        _cancel.MinWidth = 88;
        _cancel.Visibility = Visibility.Collapsed;
        commands.Children.Add(_search);
        commands.Children.Add(_cancel);
        Grid.SetColumn(commands, 1);
        header.Children.Add(commands);
        catalog.Children.Add(header);
        _loader.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        var loadingCaption = Theme.Note("正在搜索可下载的 Java…", 12.5);
        loadingCaption.Margin = new Thickness(0, 12, 0, 0);
        loadingCaption.TextAlignment = TextAlignment.Center;
        _loading.Children.Add(_loader);
        _loading.Children.Add(loadingCaption);
        catalog.Children.Add(_loading);
        catalog.Children.Add(_results);
        Sections.Children.Add(GlassPageResources.FormPlate(catalog));
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(240) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Search(); };
        _major.TextChanged += (_, _) => { RefreshTiles(); Schedule(); };
        _major.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { StartSearch(); e.Handled = true; } };
    }

    internal void Open(bool loadData)
    {
        _active = true;
        _loadData = loadData;
        SetSearching(false);
        ApplyPalette();
        Scroll.ScrollToTop();
        Schedule();
    }

    internal void Leave()
    {
        _active = false;
        _debounce.Stop();
        _request?.Cancel();
        _request = null;
        SetSearching(false);
    }

    private Button JavaChoice(string text, Action invoke)
    {
        var surface = new FrameworkElementFactory(typeof(Border), "Surface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        surface.SetValue(Border.PaddingProperty, new Thickness(13, 4, 13, 5));
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(FrameworkElement.StyleProperty, null);
        label.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        surface.AppendChild(label);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .86, "Surface"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .45));
        template.Triggers.Add(hover); template.Triggers.Add(disabled);
        var button = new Button { Content = text, Template = template, FontSize = 13, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 8) };
        button.SetResourceReference(Control.FontFamilyProperty, "GlassPresentationFont");
        SetChoiceChosen(button, false);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => { invoke(); };
        return button;
    }

    private static void SetChoiceChosen(Button button, bool chosen)
    {
        if (Equals(button.Tag, chosen)) return;
        button.Tag = chosen;
        button.SetResourceReference(Control.BackgroundProperty, chosen ? "GlassPresentationAccent" : "GlassPresentationField");
        button.SetResourceReference(Control.BorderBrushProperty, chosen ? "GlassPresentationAccentStroke" : "GlassPresentationEdge");
        button.SetResourceReference(Control.ForegroundProperty, chosen ? "GlassPresentationOnAccent" : "GlassPresentationText");
    }

    private FrameworkElement Choices(string title, string[] options, Action<string> choose)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = Theme.Note(title, 13);
        caption.Margin = new Thickness(0, 5, 0, 0);
        caption.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(caption);
        var choices = new WrapPanel();
        foreach (string option in options)
        {
            string captionText = title == "架构" ? Graphics.ArchitectureLabel(option) : option;
            var button = JavaChoice(captionText, () => {
                foreach (Button item in choices.Children) SetChoiceChosen(item, Equals(item.Content, captionText));
                choose(option);
            });
            SetChoiceChosen(button, choices.Children.Count == 0);
            AutomationProperties.SetName(button, title + " " + option);
            choices.Children.Add(button);
        }
        Grid.SetColumn(choices, 1);
        row.Children.Add(choices);
        return row;
    }

    private Grid VendorTileBody(Graphics.JavaVendor vendor, string name, TextBlock detail)
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var mark = new Image { Source = vendor.Logo, Width = 34, Height = 34 };
        mark.Margin = new Thickness(0, 0, 12, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        var title = Theme.Text(name);
        title.FontSize = 13.5;
        title.FontWeight = FontWeights.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 2, 0, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } };
        Grid.SetColumn(text, 1);
        body.Children.Add(mark);
        body.Children.Add(text);
        return body;
    }

    private void SetVendor(string? id)
    {
        _vendor = id;
        RefreshTiles();
        Schedule();
    }

    private void RefreshTiles()
    {
        foreach (var (value, tile) in _majorTiles) SetTileChosen(tile, value == _major.Text.Trim());
        foreach (var (key, tile) in _vendorTiles) SetTileChosen(tile, key == _vendor);
        SetTileChosen(_allVendors, _vendor is null);
        SetTileChosen(_chooseVendor, _vendor is not null);
        _selectedVendor.Text = _vendor is null ? "" : JavaRuntimeCatalog.Distributions[_vendor];
        _selectedVendor.Visibility = _vendor is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowVendors(bool show)
    {
        var list = (Panel)_vendors.Content;
        if (!show && list.IsKeyboardFocusWithin) _chooseVendor.Focus();
        list.IsEnabled = show;
        _vendors.IsExpanded = show;
        _vendorChevron.Angle = show ? 180 : 0;
        AutomationProperties.SetHelpText(_chooseVendor, show ? "收起发行版列表" : "展开发行版列表");
    }

    private void StartSearch()
    {
        _debounce.Stop();
        Search();
    }

    private void SetSearching(bool busy)
    {
        _search.IsEnabled = !busy && _architecture is not null;
        _cancel.Visibility = _loading.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _loader.IsActive = busy;
    }

    private void Schedule()
    {
        if (!_active || !_loadData || _disposed) return;
        _request?.Cancel();
        _request = null;
        SetSearching(false);
        _debounce.Stop();
        _debounce.Start();
    }

    private async void Search()
    {
        if (!_active || !_loadData || _disposed) return;
        if (!int.TryParse(_major.Text, out int major) || major is < JavaRuntimeCatalog.MinimumMajor or > JavaRuntimeCatalog.MaximumMajor)
        {
            _results.Children.Clear();
            _status.Text = $"请输入 {JavaRuntimeCatalog.MinimumMajor} 至 {JavaRuntimeCatalog.MaximumMajor} 的 Java 主版本。";
            return;
        }
        if (_architecture is not { } architecture)
        {
            _status.Text = "当前系统架构暂无 Java 下载选项。";
            return;
        }
        _request?.Cancel();
        using var next = _request = new CancellationTokenSource();
        _results.Children.Clear();
        SetSearching(true);
        try
        {
            var items = await JavaRuntimeDownloads.SearchAsync(major, architecture, _vendor, _package, next.Token);
            if (!ReferenceEquals(next, _request)) return;
            var installations = items.Count == 0 ? [] : (Configs.JavaList.Get()?.ToArray() ?? [])
                .Select(java => (Runtime: java, Package: ManagedJavaRuntimeStore.GetInstalledPackage(ManagedJavaRuntimeStore.DefaultRoot, java))).ToArray();
            foreach (JavaRuntimePackage item in items)
            {
                var installed = installations.Any(entry => entry.Package?.IsSameArchive(item) == true) ? "本机已有" :
                    installations.Any(entry => entry.Package is null && JavaRuntimeCatalog.MatchesLocalRuntime(item, entry.Runtime)) ? "本机同版本" : "";
                var size = item.Size <= 0 ? "大小未知" : StringUtils.FormatByteSize(item.Size);
                var download = Command("下载", () => JavaRuntimeDownloads.StartInstall(item), primary: true);
                download.Margin = new Thickness(8, 0, 0, 0);
                download.MinWidth = 72;
                download.Height = 32;
                _results.Children.Add(_results.Children.Count == 0 ? new Border { Height = 4 } : Divider());
                _results.Children.Add(Row(item.Distribution, "Java " + item.Version, item.FileName,
                    [item.PackageType.ToUpperInvariant(), Graphics.ArchitectureLabel(item.Architecture), size, installed],
                    [download], Graphics.Is32Bit(item.Architecture)));
            }
            _status.Text = items.Count > 0 ? $"找到 {items.Count} 个安装包。" : "没有符合条件的 Java 安装包。";
            StaggeredEntrance.Play(_results.Children.OfType<UIElement>().Take(15));
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (ReferenceEquals(next, _request))
            {
                Logger.Warn(error, "搜索 Java 下载目录失败");
                _status.Text = "无法获取 Java 下载列表，请检查网络后重试。";
            }
        }
        finally
        {
            if (ReferenceEquals(next, _request))
            {
                _request = null;
                SetSearching(false);
            }
        }
    }

    private void Cancel()
    {
        _debounce.Stop();
        _request?.Cancel();
        _request = null;
        SetSearching(false);
        _status.Text = "已取消搜索。";
    }

    public void Dispose()
    {
        _disposed = true;
        Leave();
    }
}
