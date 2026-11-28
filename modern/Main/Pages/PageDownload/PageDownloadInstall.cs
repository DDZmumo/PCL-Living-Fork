// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Wpf.Glass;
using static PCL.ModDownload;
using LoadState = PCL.ModBase.LoadState;
using InstallKind = PCLCS.DownloadInstallComponent;
using InstallCore = PCLCS.DownloadInstallPageController<PCL.ModDownload.DlOptiFineListEntry, PCL.ModDownload.DlForgelikeEntry, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ModDownload.DlLiteLoaderListEntry, PCL.ResourceVersion, PCL.ModDownloadLib.McInstallRequest, PCL.ModAnimation.AniData>;
using InstallHost = PCLCS.DownloadInstallPageHost<PCL.ModDownload.DlOptiFineListEntry, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ModDownload.DlLiteLoaderListEntry, PCL.ResourceVersion, PCL.ModDownloadLib.McInstallRequest>;
using InstallSelection = PCLCS.DownloadInstallSelection<PCL.ModDownload.DlOptiFineListEntry, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ModDownload.DlLiteLoaderListEntry, PCL.ResourceVersion>;
using ForgeLoader = PCL.ModLoader.LoaderTask<string, System.Collections.Generic.List<PCL.ModDownload.DlForgeVersionEntry>>;

namespace PCL;

public class PageDownloadInstall : GlassWorkspacePage, IRefreshable
{
    private const string SelectionAnimation = "FrmDownloadInstall SelectPageSwitch";
    private readonly int _animationId = ModBase.GetUuid();

    public bool IsInSelectPage;
    public string? VanillaName, VanillaIcon, SelectedFabric;
    public JObject? VanillaData;
    public JArray? MinecraftVersions;
    public DlOptiFineListEntry? SelectedOptiFine;
    public DlLiteLoaderListEntry? SelectedLiteLoader;
    public DlForgeVersionEntry? SelectedForge;
    public DlNeoForgeListEntry? SelectedNeoForge;
    public ResourceVersion? SelectedFabricApi, SelectedOptiFabric;
    public bool IsSelectNameEdited, AutoSelectedFabricApi, AutoSelectedOptiFabric;
    public static string? VersionWaitingSelect;

    private readonly Grid _viewer;
    private readonly GlassSurface _loaderCard;
    private readonly BlockCurveLoader _spinner;
    private readonly TextBlock _loaderLabel;
    private readonly Button _retry;
    private readonly Border _nameNotice;
    private readonly Border _hintFabricApi, _hintOptiFabric, _hintOptiFabricOld, _hintModOptiFine;
    private readonly Image _instanceIcon;

    private readonly Dictionary<InstallKind, GlassInstallComponentCard> _cards = new();
    private readonly Dictionary<InstallKind, ModLoader.LoaderBase> _componentLoaders;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly bool _modify;
    private ModLoader.LoaderBase? _primaryLoader;
    private ForgeLoader? _forge;
    private JObject? _renderedCatalog;
    private bool _presented, _initialized;

    private InstallCore Service { get; }
    private protected InstallSelection Selection { get; }
    private protected Grid PanAllBack { get; }
    private protected StackPanel PanMinecraft { get; }
    private protected StackPanel PanSelect { get; }
    private protected GlassValidatedTextBox TextSelectName { get; }
    private protected TextBlock LabMinecraftVersion { get; }
    private protected TextBlock? OriginalNameLabel { get; }
    private protected Button? OriginalNameRestore { get; }
    private protected Button BtnStart { get; }
    private protected Button BtnBack { get; }

    public PageDownloadInstall() : this(false) { }

    protected PageDownloadInstall(bool modify)
    {
        _modify = modify;
        NameScope.SetNameScope(this, new NameScope());
        PanAllBack = Part("PanAllBack", new Grid { Visibility = Visibility.Collapsed });
        _viewer = new Grid { Margin = new Thickness(24, 10, 24, 24) };
        PanMinecraft = Part("PanMinecraft", new StackPanel { RenderTransform = new TranslateTransform() });
        PanSelect = Part("PanSelect", new StackPanel {
            Visibility = Visibility.Collapsed, Opacity = 0, IsHitTestVisible = false, RenderTransform = new TranslateTransform(50, 0)
        });
        _viewer.Children.Add(PanMinecraft);
        _viewer.Children.Add(PanSelect);
        var scroll = GlassDialogControls.Scroll();
        scroll.Name = "PanBack";
        RegisterName(scroll.Name, scroll);
        scroll.Content = _viewer;
        PanAllBack.Children.Add(scroll);
        AttachScroll(scroll);

        BtnStart = Part("BtnStart", GlassPageLayout.FrostedAction(modify ? "开始修改" : "开始下载",
            modify ? ModBase.Logo.IconButtonEdit : ModBase.Logo.IconButtonSave));
        BtnStart.IsEnabled = false;
        BtnStart.Visibility = Visibility.Collapsed;
        BtnStart.HorizontalAlignment = HorizontalAlignment.Center;
        BtnStart.VerticalAlignment = VerticalAlignment.Bottom;
        BtnStart.Margin = new Thickness(0, 0, 0, 14);
        PanAllBack.Children.Add(BtnStart);

        BtnBack = Part("BtnBack", GlassLocalModRow.CreateButton("返回版本列表", "M12,2 L3,11 12,20 14,18 8,12 H22 V10 H8 L14,4 Z"));
        BtnBack.Margin = new Thickness(0, 0, 8, 0);
        BtnBack.Visibility = modify ? Visibility.Collapsed : Visibility.Visible;
        BtnBack.Click += (_, _) => ExitSelectPage();
        _instanceIcon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), Stretch = Stretch.Uniform };
        TextSelectName = Part("TextSelectName", new GlassValidatedTextBox {
            MaxLength = 70, Style = Theme.Style("GlassPresentationTextBox")
        });
        AutomationProperties.SetName(TextSelectName, "版本名称");
        var name = new DockPanel();
        DockPanel.SetDock(BtnBack, Dock.Left);
        DockPanel.SetDock(_instanceIcon, Dock.Left);
        name.Children.Add(BtnBack);
        name.Children.Add(_instanceIcon);
        if (modify) {
            OriginalNameLabel = Part("LabSelectName", Theme.Note("", 12));
            OriginalNameRestore = Part("BtnOriginalNameRestore", GlassLocalModRow.CreateButton("恢复原名", ModBase.Logo.IconButtonRefresh));
            OriginalNameRestore.Visibility = OriginalNameLabel.Visibility = Visibility.Collapsed;
            OriginalNameLabel.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(OriginalNameRestore, Dock.Right);
            DockPanel.SetDock(OriginalNameLabel, Dock.Right);
            name.Children.Add(OriginalNameRestore);
            name.Children.Add(OriginalNameLabel);
        }
        name.Children.Add(TextSelectName);
        var nameCard = GlassPageResources.Card(name, GlassPageLayout.CardRole.Content);
        PanSelect.Children.Add(nameCard);
        LabMinecraftVersion = Part("LabMinecraftVersion", Theme.Note("", 13));
        _nameNotice = AddNotice("", DialogTone.Error);
        _hintFabricApi = AddNotice("如果不安装 Fabric API，大多数 Mod 都会无法使用！", DialogTone.Error);
        _hintOptiFabric = AddNotice("必须安装 OptiFabric 才能正常使用 OptiFine！", DialogTone.Error);
        _hintOptiFabricOld = AddNotice("安装结束后，请在 Mod 下载中搜索 OptiFabric Origins 并下载，否则 OptiFine 会无法使用！", DialogTone.Warning);
        _hintModOptiFine = AddNotice("OptiFine 与一部分 Mod 的兼容性不佳，请谨慎安装。", DialogTone.Warning);
        AddComponent(InstallKind.Forge, "Forge", "Anvil.png", () => Service.ForgeClear());
        AddComponent(InstallKind.NeoForge, "NeoForge", "NeoForge.png", () => Service.NeoForgeClear());
        AddComponent(InstallKind.Fabric, "Fabric", "Fabric.png", () => Service.FabricClear());
        AddComponent(InstallKind.FabricApi, "Fabric API", "Fabric.png", () => Service.FabricApiClear());
        AddComponent(InstallKind.OptiFine, "OptiFine", "GrassPath.png", () => Service.OptiFineClear());
        AddComponent(InstallKind.OptiFabric, "OptiFabric", "OptiFabric.png", () => Service.OptiFabricClear());
        AddComponent(InstallKind.LiteLoader, "LiteLoader", "Egg.png", () => Service.LiteLoaderClear());

        _spinner = new BlockCurveLoader { Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Center };
        _spinner.SetResourceReference(BlockCurveLoader.ForegroundProperty, "GlassPresentationAccent");
        _loaderLabel = Theme.Note(modify ? "正在读取已安装组件和版本列表" : "正在读取 Minecraft 版本列表", 13);
        _loaderLabel.TextAlignment = TextAlignment.Center;
        _loaderLabel.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_loaderLabel, AutomationLiveSetting.Polite);
        _retry = GlassDialogControls.Button("重试");
        _retry.Visibility = Visibility.Collapsed;
        _retry.HorizontalAlignment = HorizontalAlignment.Center;
        _retry.Margin = new Thickness(0, 10, 0, 0);
        _retry.Click += (_, _) => RetryLoading();
        _loaderCard = Part("PanLoad", GlassPageResources.Plate(new StackPanel {
            MaxWidth = 450, Children = { _spinner, _loaderLabel, _retry }
        }, new Thickness(20)));
        _loaderCard.HorizontalAlignment = HorizontalAlignment.Center;
        _loaderCard.VerticalAlignment = VerticalAlignment.Center;
        var root = new Grid { Children = { PanAllBack, _loaderCard } };
        Child = root;

        _componentLoaders = new() {
            [InstallKind.OptiFine] = DlOptiFineListLoader, [InstallKind.LiteLoader] = DlLiteLoaderListLoader,
            [InstallKind.NeoForge] = DlNeoForgeListLoader, [InstallKind.Fabric] = DlFabricListLoader,
            [InstallKind.FabricApi] = DlFabricApiLoader, [InstallKind.OptiFabric] = DlOptiFabricLoader
        };
        Selection = new SelectionAdapter(this);
        Service = new InstallCore(CreateHost(), CreateAnimations());
        BtnStart.Click += (_, _) => { if (IsInSelectPage && VanillaData is not null && BtnStart.IsEnabled) Service.StartClick(); };
        TextSelectName.TextChanged += (_, _) => { Service.NameTextChanged(); UpdateNameState(); };
        TextSelectName.PreviewKeyDown += (_, args) => {
            if (args.Key == Key.Enter && VanillaData is not null && IsInSelectPage && BtnStart.IsEnabled) {
                args.Handled = true;
                Service.NameKeyDown(args.Key);
            }
        };
        Loaded += (_, _) => Open();
        Unloaded += (_, _) => Close();
        _progressTimer.Tick += (_, _) => RefreshLoadLabel();
    }

    private protected T Part<T>(string name, T element) where T : FrameworkElement
    {
        element.Name = name;
        RegisterName(name, element);
        return element;
    }

    private Border AddNotice(string text, DialogTone tone)
    {
        Border notice = GlassDialogControls.Notice(text, tone);
        notice.Margin = new Thickness(0, 0, 0, GlassPageLayout.CardGap(GlassPageLayout.CardRole.Expander));
        notice.Visibility = Visibility.Collapsed;
        PanSelect.Children.Add(notice);
        return notice;
    }

    private void AddComponent(InstallKind kind, string title, string icon, Action clear)
    {
        var card = new GlassInstallComponentCard(Theme, title, ModBase.PathImage + "Blocks/" + icon, _modify);
        Part("Card" + kind, card.Card);
        Part("Info" + kind, card.Selection.Info);
        _cards.Add(kind, card);
        PanSelect.Children.Add(card.Card);
        card.Clear.Click += (_, _) => clear();
    }

    private protected GlassInstallComponentCard GetCard(InstallKind kind) => _cards[kind];
    private protected ModLoader.LoaderBase? GetComponentLoader(InstallKind kind) => _componentLoaders.GetValueOrDefault(kind);
    private protected void ScrollHome() => Scroll?.ScrollToTop();
    private protected void SetInstanceIcon(string? address) => _instanceIcon.Source = GlassShellRuntime.LoadInstanceIcon(address);

    private InstallHost CreateHost()
    {
        var menus = ModDownloadLib.InstallRows;
        return new InstallHost {
            State = Selection,
            View = () => new() {
                Inner = _viewer, Back = Scroll!, Start = BtnStart, NameHint = _nameNotice,
                Select = PanSelect, Minecraft = PanMinecraft, Name = TextSelectName,
                MinecraftVersion = LabMinecraftVersion, HintFabricApi = _hintFabricApi,
                HintOptiFabric = _hintOptiFabric, HintOptiFabricOld = _hintOptiFabricOld, HintModOptiFine = _hintModOptiFine,
                Logo = SetInstanceIcon,
                NameHintText = text => GlassDialogControls.UpdateNotice(_nameNotice, text ?? "", DialogTone.Error),
                NameValidated = () => TextSelectName.IsValidated,
                NameValidationResult = () => TextSelectName.ValidateResult,
                ValidateName = ValidateName,
                ScrollHome = ScrollHome
            },
            Component = kind => _cards[kind].Selection,
            Loading = kind => LoaderView(GetComponentLoader(kind)),
            LoadingRun = (int)LoadState.Loading, LoadingError = (int)LoadState.Failed, LoadingUnloaded = (int)LoadState.Canceled,
            OptiFine = Source(DlOptiFineListLoader, () => DlOptiFineListLoader.Output.Value),
            LiteLoader = Source(DlLiteLoaderListLoader, () => DlLiteLoaderListLoader.Output.Value),
            NeoForge = Source(DlNeoForgeListLoader, () => DlNeoForgeListLoader.Output.Value),
            Fabric = Source(DlFabricListLoader, () => DlFabricListLoader.Output.Value),
            FabricApi = Source(DlFabricApiLoader, () => DlFabricApiLoader.Output),
            OptiFabric = Source(DlOptiFabricLoader, () => DlOptiFabricLoader.Output),
            Forge = () => new() { Input = () => _forge?.Input, Output = () => _forge?.Output ?? [], Finished = () => _forge?.State == LoadState.Finished },
            RefreshRecommended = ModDownloadLib.McDownloadForgeRecommendedRefresh,
            InitializeLoader = () => { ObservePrimary(DlClientListLoader); DlClientListLoader.Start(); },
            InitializeNameValidation = () => ConfigureNameValidation(),
            StartShow = show => BtnStart.Visibility = show ? Visibility.Visible : Visibility.Collapsed,
            AnimationControls = parent => parent is Panel panel ? panel.Children.OfType<FrameworkElement>() : [],
            StartForge = StartForge,
            ClientFinished = () => DlClientListLoader.State == LoadState.Finished,
            ClientList = () => DlClientListLoader.Output.Value,
            FoolName = value => ModMinecraft.GetMcFoolName((string)value!),
            ReloadSelected = ReloadSelected,
            ShowMinecraftList = ShowMinecraftVersionList,
            SelectLogo = GetSelectLogo,
            SelectMinecraft = (row, args) => MinecraftSelected(row, args),
            MinecraftRow = CreateMinecraftRow,
            OptiFineRow = (entry, click) => menus.OptiFineDownloadListItem(entry, row => click(row, EventArgs.Empty), false),
            LiteLoaderRow = (entry, click) => menus.LiteLoaderDownloadListItem(entry, row => click(row, EventArgs.Empty), false),
            ForgeRow = (entry, click) => menus.ForgeDownloadListItem(entry, row => click(row, EventArgs.Empty), false),
            NeoForgeRow = (entry, click) => menus.NeoForgeDownloadListItem(entry, row => click(row, EventArgs.Empty), false),
            FabricRow = (entry, click) => menus.FabricDownloadListItem(entry, row => click(row, EventArgs.Empty)),
            FabricApiRow = (entry, click) => menus.FabricApiDownloadListItem(entry, row => click(row, EventArgs.Empty)),
            OptiFabricRow = (entry, click) => menus.OptiFabricDownloadListItem(entry, row => click(row, EventArgs.Empty)),
            ForgePreload = (panel, entries, click) => menus.ForgeDownloadListItemPreload((StackPanel)panel, entries, row => click(row, EventArgs.Empty), false),
            NeoForgePreload = (panel, entries, click) => menus.NeoForgeDownloadListItemPreload((StackPanel)panel, entries, row => click(row, EventArgs.Empty), false),
            Materialize = row => row,
            RowTitle = row => ((GlassInstallRow)row).Title, RowLogo = row => ((GlassInstallRow)row).Logo,
            MinecraftCard = (title, _, entries, click, latest) => CreateMinecraftCard(title, entries, click, latest),
            Gray1 = () => (Brush)FindResource("GlassPresentationText"),
            Gray4 = () => (Brush)FindResource("GlassPresentationSecondary"),
            MinecraftFolder = () => ModMinecraft.McFolderSelected,
            IsolationSetting = () => Settings.Get<int>("LaunchArgumentIndieV2"),
            Message = request => ModMain.MyMsgBox(request.Text, request.Title, request.Button1, request.Button2),
            StartRequested = StartInstall, Install = ModDownloadLib.McInstallStart
        };
    }

    private static DownloadInstallSource<T> Source<T>(ModLoader.LoaderBase loader, Func<T?> output) where T : class =>
        new() { Finished = () => loader.State == LoadState.Finished, Output = output, Start = () => loader.Start() };

    private static DownloadInstallLoadingView? LoaderView(ModLoader.LoaderBase? loader) => loader is null ? null : new() {
        IsLoader = () => true,
        LoadingState = () => (int)(loader.State is LoadState.Waiting or LoadState.Canceled ? LoadState.Loading : loader.State),
        Error = () => loader.Error ?? new IOException("未能读取版本列表")
    };

    private protected static GlassInstallRow CreateMinecraftRow(JObject entry, Action<FrameworkElement, MouseButtonEventArgs?>? click = null) =>
        ModDownloadLib.InstallRows.McDownloadListItem(entry, click is null ? null : row => click(row, null), false);

    private FrameworkElement CreateMinecraftCard(string title, List<JObject> entries,
        Action<FrameworkElement, MouseButtonEventArgs?> click, bool latest)
    {
        if (latest) {
            // The newest releases lead the page as large tiles: the version is the headline, one click installs it.
            var heading = Theme.Text(title);
            heading.FontSize = 15;
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(2, 0, 0, 10);
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 0, -12, 0) };
            foreach (var entry in entries) tiles.Children.Add(LatestTile(CreateMinecraftRow(entry, click), click));
            return new StackPanel { Margin = new Thickness(0, 0, 0, 22), Children = { GlassSettingRows.HeadingPlate(heading), tiles } };
        }
        var expander = new Expander { Header = title, Style = Theme.Style("GlassPresentationExpander") };
        expander.Expanded += (_, args) => {
            if (!ReferenceEquals(args.OriginalSource, expander) || expander.Content is not null) return;
            expander.Content = new GlassInstallVersionList(entries.Count, entry => CreateMinecraftRow(entry, click)) { ItemsSource = entries };
        };
        return GlassPageResources.Card(expander, GlassPageLayout.CardRole.Expander);
    }

    private Button LatestTile(GlassInstallRow row, Action<FrameworkElement, MouseButtonEventArgs?> click)
    {
        var icon = new Image { Source = GlassInstallRow.Icon(row.Logo), Width = 44, Height = 44, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var version = Theme.Text(row.Title);
        version.FontSize = 24;
        version.FontWeight = FontWeights.SemiBold;
        version.TextWrapping = TextWrapping.NoWrap;
        version.TextTrimming = TextTrimming.CharacterEllipsis;
        var info = Theme.Note(row.Info, 12.5);
        info.Margin = new Thickness(0, 3, 0, 0);
        var install = Theme.Note("安装 →", 13);
        install.FontWeight = FontWeights.SemiBold;
        install.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationText");
        install.Margin = new Thickness(0, 10, 0, 0);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { version, info, install } };
        var content = new DockPanel { Children = { icon, labels } };
        DockPanel.SetDock(icon, Dock.Left);
        var tile = new Button { Style = Theme.Style("GlassPresentationButton"), MinHeight = 112, Margin = new Thickness(0, 0, 12, 0), ToolTip = row.Info };
        tile.SetResourceReference(BorderBrushProperty, "GlassPresentationEdge");
        // The tiles sit on the wallpaper without a card, so each carries its own frosted layer.
        GlassTiles.Frost(tile, content, new Thickness(20, 14, 20, 14));
        System.Windows.Automation.AutomationProperties.SetName(tile, "安装 " + row.Title);
        tile.Click += (_, _) => click(row, null);
        return tile;
    }

    private void Open()
    {
        if (_presented || Window.GetWindow(this) is FormMain { GlassPreviewState: not null }) return;
        _presented = true;
        foreach (var loader in _componentLoaders.Values) loader.OnStateChangedUi += ComponentStateChanged;
        if (!_initialized) {
            _initialized = true;
            LoaderInit();
        } else if (_primaryLoader is { } primary) primary.OnStateChangedUi += PrimaryStateChanged;
        Init();
        RefreshPrimary();
    }

    private void Close()
    {
        if (!_presented) return;
        _presented = false;
        ModAnimation.AniStop(AnimationName(SelectionAnimation));
        PanMinecraft.Opacity = IsInSelectPage ? 0 : 1;
        PanMinecraft.Visibility = IsInSelectPage ? Visibility.Collapsed : Visibility.Visible;
        PanMinecraft.RenderTransform = new TranslateTransform(IsInSelectPage ? -50 : 0, 0);
        PanSelect.Opacity = IsInSelectPage ? 1 : 0;
        PanSelect.Visibility = IsInSelectPage ? Visibility.Visible : Visibility.Collapsed;
        PanSelect.RenderTransform = new TranslateTransform(IsInSelectPage ? 0 : 50, 0);
        if (Scroll is { } scroll) scroll.IsHitTestVisible = true;
        _progressTimer.Stop();
        _spinner.IsActive = false;
        foreach (var loader in _componentLoaders.Values) loader.OnStateChangedUi -= ComponentStateChanged;
        if (_primaryLoader is { } primary) primary.OnStateChangedUi -= PrimaryStateChanged;
        _forge?.Cancel();
        OnClosed();
    }

    private protected void ObservePrimary(ModLoader.LoaderBase loader)
    {
        if (_primaryLoader is { } previous) previous.OnStateChangedUi -= PrimaryStateChanged;
        _primaryLoader = loader;
        if (_presented) loader.OnStateChangedUi += PrimaryStateChanged;
    }

    private void PrimaryStateChanged(ModLoader.LoaderBase sender, LoadState state, LoadState _)
    {
        if (_presented && ReferenceEquals(sender, _primaryLoader) && sender.State == state)
            RefreshPrimary();
    }

    private protected void RefreshPrimary()
    {
        if (!_presented || _primaryLoader is not { } loader) return;
        bool ready = loader.State == LoadState.Finished && PresentLoadedData();
        bool loading = loader.State is LoadState.Waiting or LoadState.Loading;
        PanAllBack.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        _loaderCard.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _spinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        _spinner.IsActive = loading;
        _retry.Visibility = !ready && !loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading) _progressTimer.Start(); else _progressTimer.Stop();
        RefreshLoadLabel();
    }

    private void RefreshLoadLabel()
    {
        if (_primaryLoader is not { } loader) return;
        string reading = _modify ? "正在读取已安装组件和版本列表" : "正在读取 Minecraft 版本列表";
        _loaderLabel.Text = loader.State switch {
            LoadState.Waiting or LoadState.Loading => reading + $" · {Math.Clamp(loader.Progress, 0, 1):P0}",
            LoadState.Failed => loader.Error?.Message ?? "读取失败",
            _ => "版本数据暂不可用"
        };
    }

    private protected virtual bool PresentLoadedData()
    {
        JObject? catalog = DlClientListLoader.Output.Value;
        if (catalog?["versions"] is not JArray { Count: > 0 }) return false;
        if (!ReferenceEquals(_renderedCatalog, catalog)) {
            _renderedCatalog = catalog;
            Service.LoadMinecraftOnFinish();
        } else TrySelectWaitingVersion();
        return IsInSelectPage || PanMinecraft.Children.Count > 0;
    }

    private protected virtual void RetryLoading() => DlClientListLoader.Start(IsForceRestart: true);
    private protected virtual void OnClosed() { }

    private void ComponentStateChanged(ModLoader.LoaderBase sender, LoadState state, LoadState _)
    {
        if (!_presented || !IsInSelectPage || VanillaName is null || _primaryLoader?.State != LoadState.Finished ||
            sender.State != state) return;
        var source = _componentLoaders.FirstOrDefault(pair => ReferenceEquals(pair.Value, sender));
        if (source.Value is null) return;
        if (state == LoadState.Finished) {
            switch (source.Key) {
                case InstallKind.OptiFine: Service.OptiFineLoaded(); break;
                case InstallKind.LiteLoader: Service.LiteLoaderLoaded(); break;
                case InstallKind.Forge: Service.ForgeLoaded(); break;
                case InstallKind.NeoForge: Service.NeoForgeLoaded(); break;
                case InstallKind.Fabric: Service.FabricLoaded(); break;
                case InstallKind.FabricApi: Service.FabricApiLoaded(); break;
                case InstallKind.OptiFabric: Service.OptiFabricLoaded(); break;
            }
        }
        ReloadSelected();
    }

    private protected void BindForge(ForgeLoader loader)
    {
        if (ReferenceEquals(_forge, loader)) return;
        if (_forge is { } previous) {
            previous.OnStateChangedUi -= ComponentStateChanged;
            previous.Cancel();
        }
        _forge = loader;
        _componentLoaders[InstallKind.Forge] = loader;
        if (_presented) loader.OnStateChangedUi += ComponentStateChanged;
    }

    private void StartForge(string version)
    {
        var loader = new ForgeLoader("DlForgeVersion " + version, DlForgeVersionMain);
        BindForge(loader);
        loader.Start(version);
    }

    private protected void ConfigureNameValidation(string? ignore = null, string? versionsFolder = null)
    {
        TextSelectName.ValidateRules.Clear();
        string? gameFolder = ModMinecraft.McFolderSelected;
        if (versionsFolder is null && string.IsNullOrWhiteSpace(gameFolder))
            TextSelectName.ValidateRules.Add(new ValidateFunc(_ => "请先选择游戏文件夹"));
        else TextSelectName.ValidateRules.Add(new ValidateFolderName(versionsFolder ?? Path.Combine(gameFolder!, "versions"),
            IgnoreList: ignore is null ? null : new List<string> { ignore }));
        ValidateName();
    }

    private void ValidateName()
    {
        TextSelectName.ValidateInput();
        UpdateNameState();
    }

    private protected virtual void UpdateNameState() => Service.NameValidateChanged();
    private protected string? ComponentError(InstallKind kind) => kind switch {
        InstallKind.Forge => Service.LoadForgeGetError(), InstallKind.NeoForge => Service.LoadNeoForgeGetError(),
        InstallKind.Fabric => Service.LoadFabricGetError(), InstallKind.FabricApi => Service.LoadFabricApiGetError(),
        InstallKind.OptiFine => Service.LoadOptiFineGetError(), InstallKind.OptiFabric => Service.LoadOptiFabricGetError(),
        _ => Service.LoadLiteLoaderGetError()
    };

    public void ControlInit() => Service.ControlInit();
    public virtual void LoaderInit() => Service.LoaderInit();
    public virtual void Init() => Service.Init();
    public void PrepareSelectPage() => Service.PrepareSelectPage();
    public void ClearSelected() => Service.ClearSelected();
    public void ReloadComponentLists() => Service.ReloadComponentLists();
    public virtual void Refresh()
    {
        if (_presented) RetryLoading();
    }
    public void ExitSelectPage() => Service.ExitSelectPage();
    public virtual void MinecraftSelected(FrameworkElement row, MouseButtonEventArgs? args)
    {
        if (!_modify) {
            if (IsInSelectPage) ExitSelectPage();
            IsSelectNameEdited = false;
            ClearVersionRows();
        }
        Service.MinecraftSelected(row, args);
    }
    private protected void ChangeNativeMinecraftInPlace(FrameworkElement row)
    {
        ClearVersionRows();
        Service.ChangeMinecraftInPlace(row);
    }
    private void ClearVersionRows()
    {
        foreach (var card in _cards.Values) card.Versions.Children.Clear();
    }
    public virtual void ReloadSelected() => Service.ReloadSelected();
    public virtual void ShowMinecraftVersionList() => Service.ShowMinecraftVersionList();
    public virtual string? GetSelectLogo() => Service.GetSelectLogo();
    public string? GetSelectName() => Service.GetSelectName();
    public McComponents GetSelectedComponents() => Service.GetSelectedComponents();
    public virtual void StartInstall(ModDownloadLib.McInstallRequest request) => Service.StartInstall(request);
    internal void TrySelectWaitingVersion()
    {
        if (!_modify && _presented && VersionWaitingSelect is not null) Service.TrySelectWaitingVersion();
    }

    private string AnimationName(string name) => $"{name} {_animationId}";

    private protected DownloadPageAnimationHost<ModAnimation.AniData> CreateAnimations() => new() {
        Disabled = () => ModAnimation.AniControlEnabled,
        SetDisabled = value => ModAnimation.AniControlEnabled = value,
        Opacity = (target, delta, duration, delay, ease) => ModAnimation.AaOpacity(target, delta, (int)Math.Round(duration), delay, ReadEase(ease)),
        TranslateX = (target, delta, duration, delay, ease) => ModAnimation.AaTranslateX(target, delta, (int)Math.Round(duration), delay, ReadEase(ease)),
        TranslateY = (target, delta, duration, delay, ease) => ModAnimation.AaTranslateY(target, delta, (int)Math.Round(duration), delay, ReadEase(ease)),
        Scale = (target, delta, duration, delay, ease, after) => ModAnimation.AaScaleTransform(target, delta, (int)Math.Round(duration), delay, ReadEase(ease), after),
        Color = (target, property, color, duration, delay, ease) => ModAnimation.AaColor(target, property, color, duration, delay, ReadEase(ease)),
        Code = (work, delay, after) => ModAnimation.AaCode(() => work(), delay, after),
        Start = (plan, name, refresh) => ModAnimation.AniStart(plan.ToList(), AnimationName(name), refresh),
        Stop = name => ModAnimation.AniStop(AnimationName(name))
    };

    private static ModAnimation.AniEase? ReadEase(DownloadPageEase ease) => ease switch {
        DownloadPageEase.OutFluent => new ModAnimation.AniEaseOutFluent(),
        DownloadPageEase.OutFluentExtraStrong => new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong),
        _ => null
    };

    private sealed class SelectionAdapter(PageDownloadInstall page) : InstallSelection
    {
        public override bool IsInSelectPage { get => page.IsInSelectPage; set => page.IsInSelectPage = value; }
        public override string? VanillaName { get => page.VanillaName; set => page.VanillaName = value; }
        public override JObject? VanillaData { get => page.VanillaData; set => page.VanillaData = value; }
        public override string? VanillaIcon { get => page.VanillaIcon; set => page.VanillaIcon = value; }
        public override DlOptiFineListEntry? SelectedOptiFine { get => page.SelectedOptiFine; set => page.SelectedOptiFine = value; }
        public override DlLiteLoaderListEntry? SelectedLiteLoader { get => page.SelectedLiteLoader; set => page.SelectedLiteLoader = value; }
        public override DlForgeVersionEntry? SelectedForge { get => page.SelectedForge; set => page.SelectedForge = value; }
        public override DlNeoForgeListEntry? SelectedNeoForge { get => page.SelectedNeoForge; set => page.SelectedNeoForge = value; }
        public override string? SelectedFabric { get => page.SelectedFabric; set => page.SelectedFabric = value; }
        public override ResourceVersion? SelectedFabricApi { get => page.SelectedFabricApi; set => page.SelectedFabricApi = value; }
        public override ResourceVersion? SelectedOptiFabric { get => page.SelectedOptiFabric; set => page.SelectedOptiFabric = value; }
        public override bool IsSelectNameEdited { get => page.IsSelectNameEdited; set => page.IsSelectNameEdited = value; }
        public override JArray? MinecraftVersions { get => page.MinecraftVersions; set => page.MinecraftVersions = value; }
        public override string? VersionWaitingSelect { get => PageDownloadInstall.VersionWaitingSelect; set => PageDownloadInstall.VersionWaitingSelect = value; }
        public override bool AutoSelectedFabricApi { get => page.AutoSelectedFabricApi; set => page.AutoSelectedFabricApi = value; }
        public override bool AutoSelectedOptiFabric { get => page.AutoSelectedOptiFabric; set => page.AutoSelectedOptiFabric = value; }
    }
}
