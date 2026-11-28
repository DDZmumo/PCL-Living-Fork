// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Wpf.Glass;
using static PCL.ModDownload;
using Instance = PCL.ModMinecraft.McInstance;
using LoadState = PCL.ModBase.LoadState;
using InstallKind = PCLCS.DownloadInstallComponent;
using ForgeLoader = PCL.ModLoader.LoaderTask<string, System.Collections.Generic.List<PCL.ModDownload.DlForgeVersionEntry>>;
using ModifyLoader = PCL.ModLoader.LoaderTask<PCL.ModMinecraft.McInstance, object>;
using ModifyCore = PCLCS.DownloadModifyPageController<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.ModDownload.DlOptiFineListEntry, PCL.ModDownload.DlForgelikeEntry, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ModDownload.DlLiteLoaderListEntry, PCL.ResourceVersion, PCL.ModDownloadLib.McInstallRequest, PCL.ModLoader.LoaderBase, PCLCS.DownloadInstallComponent, PCL.ModAnimation.AniData>;
using ModifyHost = PCLCS.DownloadModifyPageHost<PCL.ModDownload.DlOptiFineListEntry, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ModDownload.DlLiteLoaderListEntry, PCL.ResourceVersion, PCL.ModDownloadLib.McInstallRequest, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderBase, PCLCS.DownloadInstallComponent>;

namespace PCL;

public sealed class PageDownloadModify : PageDownloadInstall
{
    private readonly Instance? _owner;
    private readonly ModifyLoader InitLoader;
    private readonly Dictionary<int, DownloadModifyComparison> _comparisons = new();
    private readonly Grid _minecraftChange;
    private readonly Expander _minecraftPicker;
    private readonly TextBox _minecraftSearch;
    private readonly ListBox _minecraftOptions;
    private readonly TextBlock _noVersions;
    private readonly GlassSurface _packCard;
    private readonly TextBlock _packInfo;
    private JArray? _displayedCatalog;
    private MinecraftChoice[] _choices = [];
    private bool _applied;
    private readonly ModifyState _state = new();
    private McComponents? InitialTarget { get => _state.InitialTarget; set => _state.InitialTarget = value; }
    private ModifyCore ModifyService { get; }

    public PageDownloadModify() : this(null) { }

    internal PageDownloadModify(Instance? owner) : base(true)
    {
        _owner = owner;
        InitLoader = new ModifyLoader("初始化版本修改", task => ModifyService.LoadInstance(WrapTask(task)));

        _packInfo = Theme.Note("", 13);
        _packCard = GlassPageResources.Plate(_packInfo, new Thickness(0, 0, 0, 12));
        _packCard.Visibility = Visibility.Collapsed;
        PanSelect.Children.Insert(1, _packCard);

        LabMinecraftVersion.FontSize = 12.5;
        LabMinecraftVersion.VerticalAlignment = VerticalAlignment.Center;
        LabMinecraftVersion.TextTrimming = TextTrimming.CharacterEllipsis;
        LabMinecraftVersion.TextWrapping = TextWrapping.NoWrap;
        var current = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { LabMinecraftVersion } };
        var details = new Grid { Children = { current } };
        var badge = new Border { Child = details, MaxWidth = 260, Margin = new Thickness(10, 0, 12, 0),
            Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        badge.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSelected");
        var restore = Part("BtnMinecraftRestore", GlassLocalModRow.CreateButton("恢复原有 Minecraft 版本", ModBase.Logo.IconButtonRefresh));
        restore.Visibility = Visibility.Collapsed;
        var title = Theme.Text("Minecraft");
        title.FontWeight = FontWeights.SemiBold;
        title.FontSize = 15;
        title.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        Grid.SetColumn(restore, 2);
        header.Children.Add(restore);

        _minecraftSearch = Part("MinecraftVersionSearch", GlassDialogControls.TextBox());
        AutomationProperties.SetName(_minecraftSearch, "搜索 Minecraft 版本");
        var search = GlassDialogControls.WithPlaceholder(_minecraftSearch, "搜索版本");
        search.Margin = new Thickness(0, 0, 0, 8);
        _minecraftOptions = Part("MinecraftVersionOptions", new ListBox {
            Style = Theme.Style("GlassPresentationPickerList"), MaxHeight = 270,
            BorderThickness = new Thickness(), Background = Brushes.Transparent
        });
        ScrollViewer.SetCanContentScroll(_minecraftOptions, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_minecraftOptions, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_minecraftOptions, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_minecraftOptions, true);
        VirtualizingPanel.SetVirtualizationMode(_minecraftOptions, VirtualizationMode.Recycling);
        AutomationProperties.SetName(_minecraftOptions, "可选 Minecraft 版本");
        var option = new FrameworkElementFactory(typeof(Button));
        option.SetValue(FrameworkElement.StyleProperty, Theme.Style("GlassPresentationRowButton"));
        option.SetValue(FrameworkElement.MinHeightProperty, 34d);
        option.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        option.SetValue(Control.PaddingProperty, new Thickness(10, 0, 10, 0));
        option.SetBinding(ContentControl.ContentProperty, new Binding(nameof(MinecraftChoice.Name)));
        option.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) => {
            if (sender is Button { DataContext: MinecraftChoice choice }) ChooseVersion(choice);
        }));
        _minecraftOptions.ItemTemplate = new DataTemplate { VisualTree = option };
        _minecraftOptions.PreviewKeyDown += (_, args) => {
            if (args.Key != Key.Enter || args.OriginalSource is Button || _minecraftOptions.SelectedItem is not MinecraftChoice choice) return;
            args.Handled = true;
            ChooseVersion(choice);
        };

        _noVersions = Theme.Note("", 13);
        _noVersions.Margin = new Thickness(6, 6, 0, 6);
        _minecraftPicker = Part("MinecraftVersionPicker", new Expander {
            Header = header, Style = Theme.Style("GlassPresentationExpander"),
            Content = new StackPanel { Children = { search, _minecraftOptions, _noVersions } }
        });
        AutomationProperties.SetName(_minecraftPicker, "更换 Minecraft 版本");
        _minecraftPicker.Expanded += (_, args) => {
            if (ReferenceEquals(args.OriginalSource, _minecraftPicker)) FillVersionChoices();
        };
        _minecraftSearch.TextChanged += (_, _) => { if (_minecraftPicker.IsExpanded) FillVersionChoices(); };
        _minecraftChange = Part("PanMinecraftChange", new Grid());
        var searchBody = (UIElement)_minecraftPicker.Content;
        _minecraftPicker.Content = null;
        _minecraftChange.Children.Add(searchBody);
        _minecraftPicker.Content = _minecraftChange;
        var card = GlassPageResources.Plate(_minecraftPicker, new Thickness(0, 0, 0, 16));
        card.Padding = new Thickness(18, 10, 18, 10);
        Part("CardMinecraft", card);
        PanSelect.Children.Insert(2, card);
        var minecraft = GlassInstallComponentCard.CreateComparison(Theme, ModBase.PathImage + "Blocks/Grass.png",
            card, details, current, LabMinecraftVersion, restore, () => _minecraftChange, value => _minecraftPicker.IsExpanded = !value);
        _comparisons.Add(-1, minecraft);
        foreach (InstallKind kind in Enum.GetValues<InstallKind>())
            _comparisons.Add((int)kind, GetCard(kind).Comparison!);
        foreach (var comparison in _comparisons.Values)
            ((Button)comparison.Restore).Click += (_, _) => ModifyService.RestoreComponent(comparison.Restore);

        ModifyService = new ModifyCore(CreateModifyHost(), CreateAnimations());
        OriginalNameRestore!.Click += (_, _) => ModifyService.RestoreOriginalName();
    }

    private void FillVersionChoices()
    {
        if (!ReferenceEquals(_displayedCatalog, MinecraftVersions)) {
            _displayedCatalog = MinecraftVersions;
            _choices = MinecraftVersions?.OfType<JObject>()
                .Where(entry => !string.IsNullOrWhiteSpace((string?)entry["id"]) && !string.IsNullOrWhiteSpace((string?)entry["url"]))
                .Select(entry => new MinecraftChoice((string)entry["id"]!, entry)).ToArray() ?? [];
        }
        string query = _minecraftSearch.Text.Trim();
        MinecraftChoice[] visible = query.Length == 0 ? _choices :
            _choices.Where(choice => choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _minecraftOptions.ItemsSource = visible;
        _minecraftOptions.Visibility = visible.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _noVersions.Text = _choices.Length == 0 ? "版本列表暂不可用" : "没有符合条件的版本";
        _noVersions.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseVersion(MinecraftChoice choice)
    {
        if (!_minecraftPicker.IsExpanded || _minecraftChange.Visibility != Visibility.Visible ||
            !ReferenceEquals(_displayedCatalog, MinecraftVersions)) return;
        if (ChooseMinecraftInPlace(choice.Entry)) _minecraftPicker.IsExpanded = false;
    }

    internal bool ChooseMinecraftInPlace(JObject version) => ModifyService.ChooseMinecraftInPlace(version);

    private ModifyHost CreateModifyHost() => new() {
        Selection = Selection, State = _state,
        View = () => new() {
            Select = PanSelect, AllBack = PanAllBack, Back = Scroll!,
            Minecraft = PanMinecraft, BackButton = BtnBack, ChangeMinecraft = _minecraftChange,
            Start = BtnStart, NameRestore = OriginalNameRestore!, Name = TextSelectName,
            NameLabel = OriginalNameLabel!, MinecraftVersion = LabMinecraftVersion,
            ScrollHome = ScrollHome,
            MinecraftOldLogo = address => SetCompareIcon(0, address),
            MinecraftNewLogo = address => SetCompareIcon(2, address),
            NameValidated = () => TextSelectName.IsValidated,
            ValidateName = original => ConfigureNameValidation(original,
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(InitLoader.Input.PathVersion))),
            StartText = text => GlassPageLayout.Frost(BtnStart, text, ModBase.Logo.IconButtonEdit)
        },
        Comparison = kind => _comparisons[kind],
        ComparisonCard = panel => _comparisons.Values.Single(part => ReferenceEquals(part.Compare, panel)),
        ShowPackInfo = ShowPackInfo,
        BaseControlInit = ControlInit,
        InitializeLoader = () => { ObservePrimary(InitLoader); ReloadInstance(false); },
        Restart = () => ReloadInstance(true), Cancel = InitLoader.Cancel,
        BaseClearSelected = ClearSelected,
        BaseReloadSelected = () => base.ReloadSelected(),
        BaseShowMinecraftList = () => base.ShowMinecraftVersionList(),
        ChangeMinecraftInPlace = ChangeNativeMinecraftInPlace,
        BasePrepareSelection = PrepareSelectPage,
        BaseReloadLists = ReloadComponentLists,
        BaseLogo = () => base.GetSelectLogo(),
        SelectName = GetSelectName,
        Components = GetSelectedComponents,
        SelectMinecraft = version => MinecraftSelected(CreateMinecraftRow(version), null),
        MinecraftRow = version => CreateMinecraftRow(version),
        RowTitle = row => ((GlassInstallRow)row).Title, RowLogo = row => ((GlassInstallRow)row).Logo,
        InitInput = () => InitLoader.Input,
        UiWait = work => ModBase.RunInUiWait(work),
        DetectPack = ModpackUpdate.Detect,
        NewInstance = path => new Instance(path),
        CatalogLoaders = () => new() {
            ["Minecraft"] = DlClientListLoader, ["OptiFine"] = DlOptiFineListLoader,
            ["LiteLoader"] = DlLiteLoaderListLoader, ["NeoForge"] = DlNeoForgeListLoader,
            ["Fabric"] = DlFabricListLoader, ["Fabric API"] = DlFabricApiLoader, ["OptiFabric"] = DlOptiFabricLoader
        },
        CreateForgeLoader = version => new ForgeLoader("DlForgeVersion " + version, DlForgeVersionMain),
        States = new() {
            Waiting = (int)LoadState.Waiting, Loading = (int)LoadState.Loading, Finished = (int)LoadState.Finished,
            Failed = (int)LoadState.Failed, Canceled = (int)LoadState.Canceled
        },
        StartLoader = (loader, input, force) => loader.Start(input, force),
        LoaderState = loader => (int)loader.State,
        LoaderError = loader => loader.Error,
        LoaderProgress = loader => loader.Progress,
        ForgeInput = loader => ((ForgeLoader)loader).Input,
        ForgeOutput = loader => ((ForgeLoader)loader).Output,
        BindForge = loader => BindForge((ForgeLoader)loader),
        SwapRestoreParent = (button, swapped) => _comparisons.Values.Single(part => ReferenceEquals(part.Restore, button)).SetSwapped(swapped),
        LoadingKey = kind => kind,
        IsLoader = kind => GetComponentLoader(kind) is not null,
        ReadLoader = kind => GetComponentLoader(kind)!,
        ComponentError = ComponentError,
        ClientOutput = () => DlClientListLoader.Output.Value,
        FabricOutput = () => DlFabricListLoader.Output.Value,
        OptiFineHashes = () => (JObject)FileUtils.ReadAsJson(@"Minecraft\OptiFineHashes.json", type: typeof(Resource)),
        OptiFineOutput = () => DlOptiFineListLoader.Output.Value,
        NeoForgeOutput = () => DlNeoForgeListLoader.Output.Value,
        LiteLoaderOutput = () => DlLiteLoaderListLoader.Output.Value,
        FabricApiOutput = () => DlFabricApiLoader.Output,
        OptiFabricOutput = () => DlOptiFabricLoader.Output,
        OptiFineMirrorFinished = () => DlOptiFineListBmclapiLoader.State == LoadState.Finished,
        WaitOptiFineMirror = () => DlOptiFineListBmclapiLoader.WaitForExit(),
        CopyOptiFineMirror = () => DlOptiFineListLoader.Output = DlOptiFineListBmclapiLoader.Output,
        LocalSha1 = file => new LocalResourceFile(file).ModrinthHash,
        ChooseMod = (names, title) => ModMain.SelectChoice(names.Select(name => new DialogChoice(name)), title),
        ReadIniBoolean = (file, key) => Conversions.ToBoolean(ModBase.ReadIni(file, key, "False")),
        ModpackEnabled = () => ModModpackMode.Enabled, CanChangeMinecraft = ModModpackMode.CanChangeMinecraft,
        MinecraftFolder = () => ModMinecraft.McFolderSelected,
        Gray4 = () => (Brush)FindResource("GlassPresentationSecondary"),
        Hint = (message, kind) => ModMain.Hint(message, kind switch {
            SelectionPageHint.Red => ModMain.HintType.Red,
            SelectionPageHint.Blue => ModMain.HintType.Blue,
            _ => ModMain.HintType.Green
        }),
        Message = request => ModMain.MyMsgBox(request.Text, request.Title, request.Button1, request.Button2, request.Button3, request.IsWarn),
        Modify = ModDownloadLib.McModificationStart,
        InstancePage = () => LauncherInstanceContext.Instance, SetInstancePage = value => LauncherInstanceContext.Instance = value,
        SelectedInstance = () => ModMinecraft.McInstanceSelected, SetSelectedInstance = value => ModMinecraft.McInstanceSelected = value,
        ReloadJava = LauncherInstanceContext.ReloadCurrentJava,
        RefreshInstances = () => ModLoader.LoaderFolderRun(ModMinecraft.McInstanceListLoader, ModMinecraft.McFolderSelected,
            ModLoader.LoaderFolderRunType.ForceRun, MaxDepth: 1, ExtraPath: @"versions\"),
        PageBack = () => ModMain.FrmMain?.PageBack()
    };

    private void SetCompareIcon(int position, string? address)
    {
        var side = (DockPanel)_comparisons[-1].Compare.Children[position];
        ((Image)side.Children[0]).Source = GlassInstallRow.Icon(address);
    }

    private void ShowPackInfo(JObject? data)
    {
        bool recognized = data is not null && data["error"] is null;
        _packCard.Visibility = recognized ? Visibility.Visible : Visibility.Collapsed;
        if (!recognized) return;
        string name = (string?)data!["name"] ?? "整合包";
        string? version = (string?)data["version"];
        string source = (string?)data["source"]?["platform"] ?? "本地整合包";
        _packInfo.Text = string.IsNullOrWhiteSpace(version) ? $"{name} · {source}" : $"{name} {version} · {source}";
    }

    private static DownloadModifyTask<Instance> WrapTask(ModifyLoader task) => new() {
        Input = () => task.Input, Output = () => task.Output, SetOutput = value => task.Output = value,
        Canceled = () => task.IsCanceled, ForceRestart = () => task.IsForceRestarting, Progress = value => task.Progress = value
    };

    private void ReloadInstance(bool force)
    {
        var target = _owner ?? LauncherInstanceContext.Instance;
        if (target is null || !ReferenceEquals(target, LauncherInstanceContext.Instance)) return;
        _applied = false;
        _minecraftPicker.IsExpanded = false;
        BtnStart.IsEnabled = false;
        InitLoader.Start(target, force);
        RefreshPrimary();
    }

    private protected override bool PresentLoadedData()
    {
        if (InitLoader.Input is null || !ReferenceEquals(InitLoader.Input, LauncherInstanceContext.Instance)) return false;
        if (!_applied) {
            _applied = true;
            ModifyService.LoadInstanceFinished(WrapTask(InitLoader));
        }
        return true;
    }

    public override void LoaderInit() => ModifyService.LoaderInit();
    public override void Init() => ModifyService.Init();
    private protected override void OnClosed() => ModifyService.Unloaded();
    private protected override void RetryLoading() => ReloadInstance(true);

    public override string? GetSelectLogo() => ModifyService.GetSelectLogo();
    public override void ReloadSelected() => ModifyService.ReloadSelected();
    public override void ShowMinecraftVersionList() => ModifyService.ShowMinecraftVersionList();

    private protected override void UpdateNameState()
    {
        base.UpdateNameState();
        ModifyService.UpdateOriginalNameVisibility();
        ModifyService.UpdateStartButton();
    }

    public override void StartInstall(ModDownloadLib.McInstallRequest request)
    {
        if (InitLoader.State == LoadState.Finished && InitLoader.Input is { } target && ReferenceEquals(target, LauncherInstanceContext.Instance))
            ModifyService.StartInstall(request);
    }

    internal void RefreshModpackRestrictions() => ModifyService.RefreshModpackRestrictions();

    private sealed class ModifyState : DownloadModifyPageState<ModLoader.LoaderBase, InstallKind>
    {
        public override ModLoader.LoaderBase? ForgeVersionLoader { get; set; }
        public override McComponents? InitialTarget { get; set; }
        public override bool IsVanillaVersionReliable { get; set; }
        public override string? OriginalName { get; set; }
        public override string? CustomLogo { get; set; }
        public override Dictionary<InstallKind, string> PendingComponents { get; } = new();
        public override bool IsRestoringComponents { get; set; }
    }

    private sealed record MinecraftChoice(string Name, JObject Entry);
}
