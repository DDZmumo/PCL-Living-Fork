// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using DetailCore = PCLCS.ResourceDetailPageController<PCL.ResourceProject, PCL.ResourceVersion, PCL.GlassResourceVersionCard,
    PCL.GlassResourceProjectRow, PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState,
    PCL.ModMinecraft.McInstanceCardType, PCL.ModLoader.LoaderBase, PCL.ModNet.NetFile, PCL.ModAnimation.AniData>;

namespace PCL;

internal sealed class GlassResourceDetailPage : ContentControl, IDisposable
{
    private readonly FormMain _window;
    private readonly object[] _arguments;
    private readonly ResourceProject _project;
    private readonly GlassPageResources _resources;
    private readonly DetailCore _detail;
    private readonly ModLoader.LoaderTask<int, List<ResourceVersion>> _loader;
    private readonly StackPanel _summary = new(), _groups = new();
    private readonly WrapPanel _filtration = new();
    private readonly GlassSurface _filterCard;
    private readonly Button _alternate, _website, _wiki, _noteAction;
    private readonly GlassSurface _noteCard;
    private readonly StackPanel _noteContent = new();
    private bool _editingNote;
    private readonly string _filterGroup = Guid.NewGuid().ToString("N");
    private readonly BlockCurveLoader _loading = new() { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly GlassStatusGlyph _failed = new(GlassStatusGlyph.Kind.Failed, 48) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status;
    private readonly Button _retry;
    private readonly FrameworkElement _statusCard;
    private int _revision, _applied = -1;
    private bool _created, _active, _disposed, _retriedInvalidReply;
    private bool _loadData;

    internal ScrollViewer Scroll { get; }

    internal GlassResourceDetailPage(FormMain window, object[] arguments)
    {
        _window = window;
        _arguments = arguments;
        _project = (ResourceProject)arguments[0];
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        var body = new StackPanel { Margin = new Thickness(24, 24, 24, 10) };
        Scroll = new ScrollViewer { Content = body, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly };
        SmoothScroll.SetIsEnabled(Scroll, true);
        Content = new ScrollEdgeFade(Scroll);
        body.Children.Add(GlassPageResources.Card(_summary, GlassPageLayout.CardRole.Content));
        var actions = new WrapPanel();
        _website = Button("转到 " + _project.Platform, () => _detail!.IntroWeb());
        _wiki = Button("转到 MC 百科", () => _detail!.IntroWiki());
        _noteAction = Button("添加备注", () => EditNote(true));
        _noteAction.ToolTip = "给这个资源写一段只有自己看得到的备注，比如兼容情况或使用心得";
        var buttons = new List<Button> { _website, _wiki, Button("复制名称", () => _detail!.IntroCopy()) };
        if (ResourceNotes.CanNote(_project)) buttons.Add(_noteAction);
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 10, 8);
            actions.Children.Add(button);
        }
        _summary.Children.Add(actions);
        var info = new GlassResourceInfo {
            Translate = token => Task.Run(() => ResourceDownloadOperations.Descriptions.Get(_project.Platform, _project.Id, _project.Description, token), token)
        };
        info.Show(_project);
        _summary.Children.Add(info);
        // The note sits right under the header, and only once there is one (or while writing it).
        _noteCard = GlassPageResources.Card(_noteContent, GlassPageLayout.CardRole.Content);
        body.Children.Add(_noteCard);
        RenderNote();

        _alternate = Button("", () => _detail!.AlternativeClick());
        _alternate.Margin = new Thickness(0, 0, 0, 12);
        _alternate.Visibility = Visibility.Collapsed;
        body.Children.Add(_alternate);
        _filterCard = GlassPageResources.Card(_filtration, GlassPageLayout.CardRole.Content);
        _filterCard.Visibility = Visibility.Collapsed;
        body.Children.Add(_filterCard);
        body.Children.Add(_groups);

        _status = _resources.Note("", 14);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextAlignment = TextAlignment.Center;
        _status.MaxWidth = 480;
        _retry = Button("重试", Reload);
        _retry.HorizontalAlignment = HorizontalAlignment.Center;
        _retry.Margin = new Thickness(0, 10, 0, 0);
        var status = new StackPanel { Children = { _loading, _failed, _status, _retry } };
        _loading.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        var plate = GlassPageResources.Plate(status, new Thickness(0, 24, 0, 14));
        plate.HorizontalAlignment = HorizontalAlignment.Center;
        _statusCard = plate;
        body.Children.Add(_statusCard);

        _loader = new("下载资源版本列表", task => {
            Thread worker = Thread.CurrentThread;
            var files = ResourceVersion.FromProjectId(_project.Id, _project.Platform);
            if (!task.IsCanceledWithThread(worker)) task.Output = files;
        });
        _loader.OnStateChangedUi += OnLoaderState;
        _detail = new DetailCore(CreateHost(), ResourceDownloadOperations.CreateHost(), new() {
            Disabled = () => ModAnimation.AniControlEnabled,
            SetDisabled = value => ModAnimation.AniControlEnabled = value
        });
        RenderStatus();
    }

    internal void Open(bool loadData)
    {
        _active = true;
        _loadData = loadData;
        if (!_created)
        {
            _created = true;
            _detail.LoadPageArguments();
            _detail.Init();
            if (loadData) Restart();
        }
        Refresh();
    }

    internal void Refresh()
    {
        if (!_active || _disposed) return;
        _resources.ApplyPalette();
        if (_loader.State == ModBase.LoadState.Finished && _applied != _revision)
        {
            _applied = _revision;
            _detail.LoadOnFinish();
        }
        RenderStatus();
    }

    internal void Leave()
    {
        _active = false;
        _loading.IsActive = false;
    }

    internal void Reload() { _retriedInvalidReply = false; Restart(); }

    private void Restart()
    {
        if (!_loadData || _disposed) return;
        _detail.LoadPageArguments();
        _groups.Children.Clear();
        _filterCard.Visibility = Visibility.Collapsed;
        _alternate.Visibility = Visibility.Collapsed;
        _loader.Start(++_revision, IsForceRestart: true);
        RenderStatus();
    }

    private void OnLoaderState(ModLoader.LoaderBase _, ModBase.LoadState state, ModBase.LoadState oldState)
    {
        if (!_active || _disposed || _loader.State != state || _loader.Input != _revision) return;
        if (state == ModBase.LoadState.Failed && !_retriedInvalidReply &&
            _loader.Error?.Message.Contains("不是有效的 json 文件", StringComparison.Ordinal) == true)
        {
            _retriedInvalidReply = true;
            Restart();
        }
        else Refresh();
    }

    private void RenderStatus()
    {
        bool busy = _active && _loader.State == ModBase.LoadState.Loading;
        bool failed = _loader.State == ModBase.LoadState.Failed;
        bool empty = _loader.State == ModBase.LoadState.Finished && _groups.Children.Count == 0;
        _loading.IsActive = busy;
        _loading.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _failed.Visibility = _retry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = busy ? "正在获取版本列表" : failed ? _loader.Error?.Message ?? "加载失败" : empty ? "没有符合条件的版本" : "";
        _statusCard.Visibility = busy || failed || empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private ResourceDetailPageHost<ResourceProject, ResourceVersion, GlassResourceVersionCard, GlassResourceProjectRow> CreateHost() => new()
    {
        View = () => new ResourceDetailPageView {
            Filter = _filtration, Results = _groups, Intro = _summary, FilterCard = _filterCard, Alternative = _alternate, Wiki = _wiki,
            AlternativeText = text => _alternate.Content = text, WebText = text => _website.Content = text, ScrollHome = Scroll.ScrollToTop
        },
        Argument = index => _arguments[index], Arguments = () => _arguments, SetArgument = (index, value) => _arguments[index] = value,
        HasArguments = () => true,
        Failed = () => _loader.State == ModBase.LoadState.Failed, Error = () => _loader.Error, Files = () => _loader.Output,
        Restart = _ => Restart(),
        FilterButton = (text, change) => {
            var button = new RadioButton { Content = text, GroupName = _filterGroup, Style = _resources.Style("GlassPresentationRadioButton"),
                Margin = new Thickness(0, 0, 10, 0), MinWidth = 54 };
            button.Click += (_, _) => { change(text); RenderStatus(); };
            return button;
        },
        FilterText = button => (string)((RadioButton)button).Content,
        FilterChecked = (button, value) => {
            var item = (RadioButton)button;
            item.IsChecked = value;
            if (value) _detail.VersionFilter = Equals(item.Content, "全部") ? null : (string)item.Content;
        },
        SwappedHeight = () => 4,
        Card = (title, rows) => new GlassResourceVersionCard(title, rows, _resources),
        SetBuilder = (card, build) => card.Builder = build,
        CardTitle = card => card.Title,
        BuildCard = card => card.SetExpanded(true),
        SwapCard = (card, swapped) => card.SetExpanded(!swapped),
        HasProject = id => ResourceProject.Cache.ContainsKey(ResourceProjectKey.Get(_project.Platform, id)),
        CachedProject = id => ResourceProject.Cache[ResourceProjectKey.Get(_project.Platform, id)],
        ProjectRow = project => CreateProjectRow(project, false, false, () => OpenDependency(project)),
        YellowHint = (text, margin) => {
            var notice = _resources.Note(text, 13);
            notice.Margin = margin;
            return notice;
        },
        IsVersionRow = item => item is GlassResourceVersionRow,
        VersionRow = CreateVersionRow,
        Header = project => CreateProjectRow(project, true, true, null),
        HeaderInteractive = (_, _) => { },
        HeaderLogo = row => ResourceThumbnails.CachedPath(row.Project) ?? "",
        HeaderName = row => {
            var (name, raw) = row.Project.GetControlTitle(true);
            return (name ?? row.Project.TranslatedName, raw);
        },
        Website = address => ModBase.OpenWebsite(address), Clipboard = value => ModBase.ClipboardSet(value)
    };

    private GlassResourceProjectRow CreateProjectRow(ResourceProject project, bool showVersion, bool showLoader, Action? open)
    {
        var row = new GlassResourceProjectRow(project, _resources, showVersion, showLoader, open);
        if (_loadData) row.Loaded += (_, _) => row.LoadIcon();
        return row;
    }

    private void OpenDependency(ResourceProject project)
    {
        _arguments[1] = _groups.Children.OfType<GlassResourceVersionCard>().Where(card => card.IsExpanded).Select(card => card.Title).ToList();
        var args = new object[] { project, new List<string>(), _detail.TargetVersion ?? "", _detail.TargetLoaders,
            ResourceTypes.Any, _detail.DownloadTargetInstance! };
        _window.PageChange(new FormMain.PageStackData { Page = FormMain.PageType.ResourceDetail, Additional = args });
    }

    private FrameworkElement CreateVersionRow(ResourceVersion version, Action<FrameworkElement, EventArgs> open,
        Action<object, EventArgs>? save, bool duplicate)
    {
        return ResourcePresentation.VersionRow(version, open, save, duplicate,
            new ResourceVersionListHost<GlassResourceVersionRow, Action<FrameworkElement, EventArgs>, Action<object, EventArgs>> {
                Create = () => new GlassResourceVersionRow(_resources),
                Configure = (row, data, title, logo) => row.Configure(data, title, logo),
                AttachClick = (row, click) => row.Handle = click,
                SetInfo = (row, info) => row.Describe(info, version.ReleaseDate),
                Title = row => row.Title,
                AttachSave = (row, action) => row.ShowSave(action)
            });
    }

    // The note card: hidden without a note; otherwise the quoted note with edit / delete, or an editor while writing.
    private void RenderNote()
    {
        string? note = ResourceNotes.Get(_project);
        _noteAction.Content = note is null ? "添加备注" : "编辑备注";
        _noteContent.Children.Clear();
        _noteCard.Visibility = note is null && !_editingNote ? Visibility.Collapsed : Visibility.Visible;
        if (_noteCard.Visibility != Visibility.Visible) return;

        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = _resources.Text("备注");
        title.FontWeight = FontWeights.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        if (_editingNote)
        {
            heading.Children.Add(title);
            _noteContent.Children.Add(heading);
            var editor = new TextBox { Text = note ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 84, MaxHeight = 240,
                MaxLength = ResourceNotes.MaxLength, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = _resources.Style("GlassPresentationTextBox"),
                VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6) };
            System.Windows.Automation.AutomationProperties.SetName(editor, "备注内容");
            _noteContent.Children.Add(editor);
            var hint = _resources.Note("备注只保存在本机，会显示在详情页和收藏列表里。", 12);
            hint.Margin = new Thickness(0, 8, 0, 0);
            var save = Button("保存", () => { if (ResourceNotes.Set(_project, editor.Text)) { _editingNote = false; RenderNote(); } });
            save.Style = _resources.Style("GlassPresentationAccentButton");
            var cancel = Button("取消", () => { _editingNote = false; RenderNote(); });
            cancel.Margin = new Thickness(8, 0, 0, 0);
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { save, cancel } };
            DockPanel.SetDock(commands, Dock.Right);
            _noteContent.Children.Add(new DockPanel { Children = { commands, hint } });
            editor.Loaded += (_, _) => { editor.Focus(); editor.CaretIndex = editor.Text.Length; };
            return;
        }
        var edit = Button("编辑", () => EditNote(true));
        var remove = Button("删除", () => { if (ResourceNotes.Set(_project, null)) RenderNote(); });
        edit.Margin = new Thickness(8, 0, 0, 0);
        remove.Margin = new Thickness(8, 0, 0, 0);
        foreach (var command in new[] { edit, remove }) { command.Height = command.MinHeight = 28; command.MinWidth = 0; command.Padding = new Thickness(12, 0, 12, 0); }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, remove } };
        DockPanel.SetDock(tools, Dock.Right);
        heading.Children.Add(tools);
        heading.Children.Add(title);
        _noteContent.Children.Add(heading);
        var text = _resources.Text(ResourceNotes.Quote(note!));
        text.TextWrapping = TextWrapping.Wrap;
        text.LineHeight = 21;
        _noteContent.Children.Add(text);
    }

    private void EditNote(bool editing)
    {
        _editingNote = editing;
        RenderNote();
        if (editing) _noteCard.BringIntoView();
    }

    private Button Button(string caption, Action action)
    {
        var button = new Button { Content = caption, Style = _resources.Style("GlassPresentationButton") };
        button.Click += (_, _) => action();
        return button;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Leave();
        _loader.OnStateChangedUi -= OnLoaderState;
        _loader.Cancel();
    }
}

internal sealed class GlassResourceVersionCard : ContentControl
{
    private readonly StackPanel _rows;
    private readonly Expander _expand;
    internal string Title { get; }
    internal Action<StackPanel>? Builder { get; set; }
    internal bool IsExpanded => _expand.IsExpanded;

    internal GlassResourceVersionCard(string title, StackPanel rows, GlassPageResources resources)
    {
        Title = title;
        _rows = rows;
        var caption = resources.Text(title);
        caption.FontSize = 15;
        caption.FontWeight = FontWeights.SemiBold;
        _expand = new Expander { Header = caption, Content = rows, Style = resources.Style("GlassPresentationExpander") };
        _expand.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, _expand)) Build(); };
        var plate = GlassPageResources.Card(_expand, GlassPageLayout.CardRole.Expander);
        Content = plate;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Focusable = false;
    }

    internal void SetExpanded(bool expanded)
    {
        if (expanded) Build();
        _expand.IsExpanded = expanded;
    }

    private void Build()
    {
        Action<StackPanel>? build = Builder;
        Builder = null;
        build?.Invoke(_rows);
    }
}

internal sealed class GlassResourceVersionRow : Grid
{
    private readonly GlassPageResources _resources;
    private readonly Image _logo = new() { Width = 26, Height = 26, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _title, _info;
    private readonly Button _open;
    internal string Title => _title.Text;
    internal Action<FrameworkElement, EventArgs>? Handle { get; set; }

    internal GlassResourceVersionRow(GlassPageResources resources)
    {
        _resources = resources;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var body = new DockPanel();
        DockPanel.SetDock(_logo, Dock.Left);
        body.Children.Add(_logo);
        _title = resources.Text("");
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _info = resources.Note("", 12);
        _info.TextWrapping = TextWrapping.NoWrap;
        _info.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(new StackPanel { Children = { _title, _info }, VerticalAlignment = VerticalAlignment.Center });
        _open = new Button { Content = body, Style = resources.Style("GlassPresentationBandRow"), Height = 49 };
        _open.Click += (_, e) => Handle?.Invoke(this, e);
        Children.Add(_open);
    }

    internal void Configure(object data, string title, string logo)
    {
        Tag = data;
        _title.Text = title;
        _logo.Source = (ImageSource)new MyBitmap(logo);
        AutomationProperties.SetName(_open, title);
    }

    internal void Describe(string info, DateTime releaseDate)
    {
        _info.Text = info;
        _open.ToolTip = Title + "\n" + info + "\n发布时间 " + releaseDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    internal void ShowSave(Action<object, EventArgs> save)
    {
        var button = new Button { Content = "另存为", Style = _resources.Style("GlassPresentationButton"), Margin = new Thickness(6, 0, 0, 0) };
        SetColumn(button, 1);
        button.Click += (_, e) => save(button, e);
        Children.Add(button);
    }
}
