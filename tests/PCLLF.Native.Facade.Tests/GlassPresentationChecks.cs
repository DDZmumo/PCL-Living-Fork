// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Presentation.Foundation;
using PCLLF.Presentation.Controls;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static class GlassPresentationChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        HomepageCards();
        ModalFocusEffects();
        NativeInstallStart(environment);
        ResourceSearchFirstSource();
        ModNoticeColors();
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native pages and authored homepage content.");
    }

    private static void HomepageCards()
    {
        const string markup = """
            <StackPanel>
                <StackPanel.Resources>
                    <SolidColorBrush x:Key="AuthoredInk" Color="#FF128080"/>
                    <Style x:Key="NewsCommand" TargetType="local:MyIconTextButton"><Setter Property="Text" Value="查看"/></Style>
                    <ControlTemplate x:Key="AuthoredTemplate" TargetType="ContentControl">
                        <Border x:Name="TemplateFace" Background="{StaticResource AuthoredInk}" Padding="4">
                            <TextBlock x:Name="TemplateLabel" Text="{TemplateBinding Content}"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="TemplateFace" Property="Opacity" Value="0.25"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </StackPanel.Resources>
                <Button x:Name="AuthoredAction" Content="初始名称"/>
                <ContentControl x:Name="AuthoredSection" Content="分组" Template="{StaticResource AuthoredTemplate}"/>
                <Border MaxHeight="140" HorizontalAlignment="Center">
                    <Image x:Name="FlexibleArtwork" Stretch="UniformToFill"/>
                </Border>
                <Border x:Name="AuthoredBadge" Width="170" Height="30" Margin="0,-20,0,0" Background="{DynamicResource ColorBrush3}">
                    <TextBlock x:Name="AuthoredBadgeLabel" Text="版本标签" HorizontalAlignment="Center" Foreground="{DynamicResource ColorBrush7}"/>
                </Border>
                <Border Width="90" Height="58" ClipToBounds="True" HorizontalAlignment="Left">
                    <Image x:Name="AuthoredCrop" Stretch="UniformToFill"/>
                </Border>
                <local:MyIconTextButton x:Name="AuthoredIconAction" Text="查看" Logo="M2,2 H18 V18 H2 Z"/>
                <local:MyIconTextButton x:Name="StyledNewsAction" Style="{StaticResource NewsCommand}" LogoScale="1.1" Logo="M2,2 H18 V18 H2 Z"/>
                <TextBlock x:Name="StaticThemeText" Foreground="{StaticResource ColorBrush3}" Text="测试"/>
                <StackPanel><StackPanel.Resources><SolidColorBrush x:Key="ColorBrushTestInk" Color="#FF126655"/></StackPanel.Resources>
                    <TextBlock x:Name="StaticAuthorText" Foreground="{StaticResource ColorBrushTestInk}" Text="测试"/>
                </StackPanel>
                <local:MyLoading x:Name="NewsLoading" Text="翻译施工中"/>
                <local:MyCard x:Name="AuthoredCard" Title="推荐">
                    <StackPanel Margin="12,32,12,12">
                        <Viewbox x:Name="AuthoredGallery">
                            <StackPanel Orientation="Horizontal">
                                <Border Width="200" Height="40" Background="Green"/>
                                <Border Width="200" Height="40" Background="Orange"/>
                                <Border Width="200" Height="40" Background="Purple"/>
                                <Border Width="200" Height="40" Background="Blue"/>
                            </StackPanel>
                        </Viewbox>
                    </StackPanel>
                </local:MyCard>
            </StackPanel>
            """;
        var page = (ContentControl)Activator.CreateInstance(Native.Main.GetType("PCL.GlassHomePage", throwOnError: true)!, nonPublic: true)!;
        using var lifetime = (IDisposable)page;
        var outlet = (StackPanel)Native.Property(page.GetType(), "Homepage").GetValue(page)!;
        object loader = Native.Property(page.GetType(), "Loader").GetValue(page)!;
        void Load(string xaml) => Native.Call(loader.GetType(), "LoadContent", loader, xaml);

        Load(markup);
        var authored = (FrameworkElement)outlet.Children[0];
        var button = (Button)authored.FindName("AuthoredAction");
        var section = (ContentControl)authored.FindName("AuthoredSection");
        var gallery = (Viewbox)authored.FindName("AuthoredGallery");
        var artwork = (Image)authored.FindName("FlexibleArtwork");
        var crop = (Image)authored.FindName("AuthoredCrop");
        var badge = (Border)authored.FindName("AuthoredBadge");
        var badgeLabel = (TextBlock)authored.FindName("AuthoredBadgeLabel");
        var iconAction = (LauncherMarkupButton)authored.FindName("AuthoredIconAction");
        var fixtureArtwork = new DrawingImage(new GeometryDrawing(Brushes.Tan, null, new RectangleGeometry(new Rect(0, 0, 640, 360))));
        int clicks = 0;
        button.Click += (_, _) => clicks++;

        int theme = Settings.Get<int>("UiLauncherColorMode");
        var window = new Window { Content = page, Width = 850, Height = 610, Left = -30000, Top = -30000,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            artwork.Source = crop.Source = fixtureArtwork;
            window.UpdateLayout();
            var styledNewsAction = (LauncherMarkupButton)authored.FindName("StyledNewsAction");
            var newsLoader = (LauncherMarkupLoading)authored.FindName("NewsLoading");
            var spinner = newsLoader.Children.OfType<BlockCurveLoader>().Single();
            Native.Check(spinner.IsActive && spinner.HorizontalAlignment == HorizontalAlignment.Center &&
                newsLoader.Children.OfType<TextBlock>().Single().Text == "翻译施工中",
                "MyLoading did not use the shared Glass loading control and the supplied caption.");
            Native.Check(iconAction.ActualHeight == 27 && styledNewsAction.ActualHeight == 27 &&
                Math.Abs(styledNewsAction.ActualWidth - iconAction.ActualWidth) < .01 &&
                ReferenceEquals(styledNewsAction.Template, iconAction.Template),
                "An authored command style or logo scale changed the news button's footprint or replaced its chrome.");
            Native.Check(ReferenceEquals(outlet.Children[0], authored) && authored.IsVisible,
                "The new shell did not present the original parsed homepage tree.");
            var face = (Border)section.Template.FindName("TemplateFace", section);
            section.IsEnabled = false;
            section.Content = "更新名称";
            VisualAssetChecks.Drain();
            var label = (TextBlock)section.Template.FindName("TemplateLabel", section);
            Native.Check(label.Text == "更新名称" && Math.Abs(face.Opacity - .25) < .01 &&
                face.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(18, 128, 128),
                "An authored template trigger, static resource or template binding was lost.");
            Invoke(button);
            Native.Check(clicks == 1, "Hosting the authored homepage lost its original action.");
            foreach (int mode in new[] { 0, 1 }) {
                Native.Cache("UiLauncherColorMode", mode);
                Native.Static("PCL.ModSecret", "ThemeRefresh", -1);
                VisualAssetChecks.Drain();
                Native.Check(ReferenceEquals(badge.Background, page.FindResource("GlassPresentationAccent")) &&
                    ReferenceEquals(badgeLabel.Foreground, page.FindResource("GlassPresentationOnAccent")),
                    "The authored news badge did not use the same accent and label brushes as the current Glass page.");
                Native.Check(ReferenceEquals(spinner.Foreground, badge.Background) &&
                    ReferenceEquals(((TextBlock)authored.FindName("StaticThemeText")).Foreground, badge.Background) &&
                    ((TextBlock)authored.FindName("StaticAuthorText")).Foreground is SolidColorBrush { Color: { R: 18, G: 102, B: 85 } },
                    "The loader, legacy brush reference or fragment-local brush did not retain its intended color.");
                Color badgeBackground = ThemeTransition.PaletteTarget(badge.Background)!.Value;
                Color badgeForeground = ThemeTransition.PaletteTarget(badgeLabel.Foreground)!.Value;
                static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
                static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
                double background = Luminance(badgeBackground), foreground = Luminance(badgeForeground);
                double contrast = (Math.Max(background, foreground) + .05) / (Math.Min(background, foreground) + .05);
                Native.Check(contrast >= 4.5 && badgeForeground.A == 255 && badge.Margin.Top == -20,
                    $"The native homepage's badge lost contrast or its authored placement. Theme {mode}, {badgeBackground}/{badgeForeground}, contrast {contrast:F2}.");
                var hover = (SolidColorBrush)iconAction.FindResource("ColorBrushBg1");
                iconAction.SetCurrentValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)));
                var animation = ModAnimation.AaColor(iconAction, Border.BackgroundProperty, "ColorBrushBg1", 100);
                Color animated = ((ModBase.MyColor)animation.Value).ToMediaColor();
                Native.Check(ReferenceEquals(hover, page.FindResource("GlassPresentationHover")) &&
                    animated == (Color)iconAction.FindResource("ColorObjectBg1") &&
                    ThemeTransition.PaletteTarget(hover) == animated,
                    "An authored button animation did not resolve the Glass hover brush and its matching color alias.");
            }

            foreach (double width in new[] { 850d, 475d }) {
                window.Width = width; window.UpdateLayout(); VisualAssetChecks.Drain();
                var cards = ((StackPanel)gallery.Child).Children.Cast<FrameworkElement>().ToArray();
                Native.Check(cards.All(card => {
                    Rect bounds = card.TransformToVisual(gallery).TransformBounds(new Rect(card.RenderSize));
                    return bounds.Width > 20 && bounds.Left >= -1 && bounds.Right <= gallery.ActualWidth + 1;
                }), "A Viewbox authored in the original homepage clipped its gallery at " + width + " DIP.");
                Native.Check(artwork.Stretch == Stretch.UniformToFill && ((Border)artwork.Parent).ActualHeight <= 140.5 &&
                    artwork.ActualWidth > 0 && crop.Stretch == Stretch.UniformToFill &&
                    crop.Parent is Border { ClipToBounds: true },
                    "The homepage changed the authored proportional fill and explicit crop.");
            }

            Load(markup);
            Native.Check(ReferenceEquals(outlet.Children[0], authored), "Refresh rebuilt an unchanged original homepage.");
            window.Content = null;
            VisualAssetChecks.Drain();
            window.Content = page;
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(ReferenceEquals(outlet.Children[0], authored) &&
                section.Template.FindName("TemplateLabel", section) is TextBlock { Text: "更新名称" } &&
                button.IsEnabled && artwork.Stretch == Stretch.UniformToFill,
                "Reopening Home discarded the active page, bindings or image fit.");
            Load("""<TextBlock x:Name="ReplacementHome" Text="替换后的主页"/>""");
            window.UpdateLayout(); VisualAssetChecks.Drain();
            Native.Check(outlet.Children.Count == 1 &&
                ((FrameworkElement)outlet.Children[0]).FindName("ReplacementHome") is TextBlock { Text: "替换后的主页", IsVisible: true },
                "A live homepage refresh did not update the visible outlet.");
        } finally {
            window.Content = null; window.Close();
            Native.Cache("UiLauncherColorMode", theme);
            Native.Static("PCL.ModSecret", "ThemeRefresh", -1);
            VisualAssetChecks.Drain();
        }
    }

    private static void ModalFocusEffects()
    {
        var root = new Grid();
        var background = new Grid { Background = Brushes.SteelBlue };
        background.Children.Add(new TextBlock { Text = "背景界面保持独立", FontSize = 24 });
        var scrim = new Border { Background = Brushes.Black };
        var dialog = new Border { Child = new TextBlock { Text = "弹窗文字保持清晰" }, Width = 280, Height = 160 };
        root.Children.Add(background); root.Children.Add(scrim); root.Children.Add(dialog);
        var window = new Window { Content = root, Width = 640, Height = 440, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        object? effects = null;
        try
        {
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            Type type = Native.Type("PCL.GlassModalEffects");
            effects = Activator.CreateInstance(type, Native.Flags, null, [root, background, scrim, dialog], null)!;
            Native.Call(type, "SetMessage", effects, new object());
            VisualAssetChecks.Pump(() => (double)Native.Property(type, "Progress").GetValue(effects)! > .999, "modal blur open endpoint");
            Native.Check(background.Effect is System.Windows.Media.Effects.BlurEffect { Radius: > 5.9 } && dialog.Effect is null &&
                PCLLF.Wpf.Glass.GlassMaterial.GetWallpaperSource(dialog) is BitmapSource { IsFrozen: true } && !background.IsHitTestVisible && dialog.IsHitTestVisible,
                "Modal focus did not blur its background, retain sharp foreground or supply an owned backdrop snapshot.");
            Native.Call(type, "SetMessage", effects, new object?[] { null });
            Native.Check(background.IsHitTestVisible && !dialog.IsHitTestVisible, "Modal dismissal delayed input ownership until the blur finished.");
            VisualAssetChecks.Pump(() => background.Effect is null && scrim.Visibility == Visibility.Collapsed, "modal blur close endpoint");
        }
        finally { (effects as IDisposable)?.Dispose(); window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    internal static object InstallHost(PageDownloadInstall page) =>
        Native.Get(Native.Property(typeof(PageDownloadInstall), "Service").GetValue(page)!, "host")!;

    internal static void FreezeInstallFeeds(object host)
    {
        Native.Set(host, "RefreshRecommended", (Action)(() => { }));
        Native.Set(host, "StartForge", (Action<string>)(_ => { }));
        Native.Set(host, "Loading", (Func<DownloadInstallComponent, DownloadInstallLoadingView?>)(_ => null));
        foreach (string feed in new[] { "OptiFine", "LiteLoader", "NeoForge", "Fabric", "FabricApi", "OptiFabric" }) {
            object loader = Native.Get(host, feed)!;
            Native.Set(loader, "Finished", (Func<bool>)(() => false));
            Native.Set(loader, "Start", (Action)(() => { }));
        }
    }

    internal static void LatestDownloadTiles(FixtureEnvironment environment)
    {
        using var scope = new VisualAssetChecks.Scope(environment, "latest-download-tiles");
        var page = new PageDownloadInstall();
        var entries = new List<JObject> {
            new() { ["id"] = "26.3", ["type"] = "release", ["url"] = "https://example.invalid/release.json",
                ["lore"] = "最新正式版，发布于 2026/09/15 19:23" },
            new() { ["id"] = "26.4-pre-2", ["type"] = "snapshot", ["url"] = "https://example.invalid/snapshot.json",
                ["lore"] = "最新预览版，发布于 2026/11/27 18:00" }
        };
        var selected = new List<string>();
        Action<FrameworkElement, System.Windows.Input.MouseButtonEventArgs?> click = (row, _) => selected.Add(((GlassInstallRow)row).Title);
        var section = (FrameworkElement)Native.Call(typeof(PageDownloadInstall), "CreateMinecraftCard", page, "最新版本", entries, click, true)!;
        foreach (double width in new[] { 536d, 430d, 780d }) {
            section.Measure(new Size(width, double.PositiveInfinity));
            section.Arrange(new Rect(0, 0, width, section.DesiredSize.Height));
            section.UpdateLayout();
            var cards = Descendants(section).OfType<Button>().ToArray();
            Native.Check(cards.Length == entries.Count && Math.Abs(cards[0].ActualHeight - cards[1].ActualHeight) < 1,
                "The latest release and snapshot tiles have different heights.");
            foreach (var card in cards) {
                var action = Descendants(card).OfType<TextBlock>().Single(label => label.Text == "安装 →");
                double bottom = action.TranslatePoint(new Point(0, action.ActualHeight), card).Y;
                Native.Check(action.ActualHeight >= action.FontSize && bottom <= card.ActualHeight - 13,
                    $"The install action lost its bottom inset at width {width}.");
            }
        }
        Invoke(Descendants(section).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "安装 26.4-pre-2"));
        Native.Check(selected.SequenceEqual(new[] { "26.4-pre-2" }), "The preview tile did not select its own version.");
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS latest download tiles: wrapped release dates, visible install actions, equal height and version selection.");
    }

    private static void NativeInstallStart(FixtureEnvironment environment)
    {
        var originalClient = ModDownload.DlClientListLoader;
        string? pendingVersion = PageDownloadInstall.VersionWaitingSelect;
        int unexpectedLoads = 0;
        var cached = new ModLoader.LoaderTask<string, ModDownload.DlClientListResult>("Cached client list", _ => {
            unexpectedLoads++;
            throw new InvalidOperationException("The installer used the network in a cached fixture.");
        });
        var release = new JObject {
            ["id"] = "1.21.1", ["type"] = "release", ["releaseTime"] = new DateTime(2024, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            ["url"] = "https://invalid.example/release.json"
        };
        cached.Output = new ModDownload.DlClientListResult { Value = new JObject { ["versions"] = new JArray(release) } };
        cached.State = ModBase.LoadState.Finished;
        Window? window = null;
        try {
            ModDownload.DlClientListLoader = cached;
            PageDownloadInstall.VersionWaitingSelect = null;
            var page = new PageDownloadInstall();
            Native.Check(unexpectedLoads == 0 && cached.LastRunningThread is null && page is GlassWorkspacePage,
                "Constructing the native installer activated its loader or used an original page base.");
            var host = InstallHost(page);
            FreezeInstallFeeds(host);
            var input = (GlassValidatedTextBox)page.FindName("TextSelectName");
            Native.Set(host, "InitializeNameValidation", (Action)(() => {
                input.ValidateRules.Clear();
                input.ValidateRules.Add(new ValidateFolderName(Path.Combine(environment.Data, "versions")));
                input.ValidateInput();
            }));
            Native.Set(host, "MinecraftFolder", (Func<string>)(() => environment.Data + Path.DirectorySeparatorChar));
            Native.Set(host, "IsolationSetting", (Func<int>)(() => 4));
            Native.Set(host, "Loading", (Func<DownloadInstallComponent, DownloadInstallLoadingView?>)(kind =>
                kind is DownloadInstallComponent.Fabric or DownloadInstallComponent.FabricApi ? new() {
                    IsLoader = () => true, LoadingState = () => (int)ModBase.LoadState.Finished
                } : null));
            var fabric = Native.Get(host, "Fabric")!;
            Native.Set(fabric, "Finished", (Func<bool>)(() => true));
            Native.Set(fabric, "Output", (Func<JObject>)(() => new JObject {
                ["game"] = new JArray(new JObject { ["version"] = "1.21.1" }),
                ["loader"] = new JArray(new JObject { ["version"] = "0.18.4", ["stable"] = true })
            }));
            var api = new ResourceVersion { Display = "[1.21.1] Fabric API 0.1", Id = "fixture-api", ReleaseDate = DateTime.UtcNow };
            var fabricApi = Native.Get(host, "FabricApi")!;
            Native.Set(fabricApi, "Finished", (Func<bool>)(() => true));
            Native.Set(fabricApi, "Output", (Func<List<ResourceVersion>>)(() => [api]));
            var requests = new List<ModDownloadLib.McInstallRequest>();
            Native.Set(host, "Install", (Func<ModDownloadLib.McInstallRequest, bool>)(request => { requests.Add(request); return true; }));

            window = new Window { Content = page, Width = 820, Height = 660, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            var catalog = (StackPanel)page.FindName("PanMinecraft");
            VisualAssetChecks.Pump(() => catalog.Children.Count > 0, "the cached release-only catalog");
            window.UpdateLayout();
            var releaseRow = Descendants(catalog).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "安装 1.21.1");
            Invoke(releaseRow);
            Native.Check(page.IsInSelectPage && page.VanillaName == "1.21.1", "Clicking a native version row did not enter selection.");
            page.ReloadComponentLists();
            var picker = Descendants((Border)page.FindName("CardFabric")).OfType<Expander>().Single();
            picker.IsExpanded = true;
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var loader = Descendants((StackPanel)picker.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "0.18.4");
            Invoke(loader);
            Native.Check(page.SelectedFabric == "0.18.4" && ReferenceEquals(page.SelectedFabricApi, api) && !picker.IsExpanded,
                "The native Fabric action lost component selection, default Fabric API, or closing behavior.");

            var start = (Button)page.FindName("BtnStart");
            input.Text = "bad/name";
            Native.Check(!start.IsEnabled, "The native Install button accepted an invalid version name.");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Native.Check(requests.Count == 0, "The native event path bypassed the name validator.");
            input.Text = "NativeInstallFixture";
            window.UpdateLayout();
            Invoke(start);
            Native.Check(requests.Count == 1 && requests[0].NewInstanceName == "NativeInstallFixture" &&
                requests[0].MinecraftJson == "https://invalid.example/release.json" &&
                requests[0].Components.ModLoaderVersions[ModLoaders.Fabric] == "0.18.4" && ReferenceEquals(requests[0].FabricApi, api)
                && !page.IsInSelectPage && start.Visibility == Visibility.Collapsed,
                "The native Install button did not submit exactly one complete request or return to the catalog.");
            Native.Check(unexpectedLoads == 0 && cached.LastRunningThread is null, "The cached installer started unexpected work.");
            environment.AssertNoForbiddenIo();
        }
        finally {
            if (window is not null) { window.Content = null; window.Close(); }
            VisualAssetChecks.Drain();
            ModDownload.DlClientListLoader = originalClient;
            PageDownloadInstall.VersionWaitingSelect = pendingVersion;
        }
    }

    private static void ResourceSearchFirstSource()
    {
        using var slow = new ManualResetEventSlim();
        var available = new TaskCompletionSource<IReadOnlyList<ResourceProject>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new ResourceSearcher.SearchResult();
        var request = new ResourceSearcher.SearchRequest(ResourceTypes.Mod, storage, 15) { PresentationPageSize = 15 };
        var engine = new ResourceSearchEngine<ResourceProject, ResourceSearcher.SearchResult>(new ResourceSearchHost<ResourceProject> {
            CreateProject = json => new ResourceProject(json), SimplifyChinese = text => text,
            WikiEntries = () => [], Info = _ => { }, Warning = _ => { }, Warn = (_, _) => { },
            StartThread = (action, name) => { var thread = new Thread(() => action()) { IsBackground = true, Name = name }; thread.Start(); return thread; },
            Request = url => {
                if (url.Contains("curseforge.com", StringComparison.Ordinal)) {
                    Native.Check(url.Contains("pageSize=8&", StringComparison.Ordinal), "CurseForge exceeded its share of the fifteen-result batch.");
                    if (!slow.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Timed out waiting for fixture to release CurseForge.");
                    return new JObject { ["data"] = new JArray(Project(ResourcePlatforms.CurseForge, "1")), ["pagination"] = new JObject { ["totalCount"] = 1 } };
                }
                Native.Check(url.Contains("limit=7&", StringComparison.Ordinal), "Modrinth exceeded its share of the fifteen-result batch.");
                return new JObject { ["hits"] = new JArray(Project(ResourcePlatforms.Modrinth, "fixture1")), ["total_hits"] = 1 };
            }
        });
        var run = Task.Run(() => engine.Run(request, new ResourceSearchTask {
            IsCanceled = () => false, Progress = () => 0, SetProgress = _ => { }
        }, results => available.TrySetResult(results)));
        try
        {
            Native.Check(available.Task.Wait(TimeSpan.FromSeconds(5)) && available.Task.Result.Single().Platform == ResourcePlatforms.Modrinth && !run.IsCompleted,
                "The ready source was not published before the slower one responded.");
            Native.Check(storage.Results.Count == 0, "Preliminary search rows changed the completed result store.");
        }
        finally { slow.Set(); run.GetAwaiter().GetResult(); }
        Native.Check(storage.Results.Select(project => project.Platform).ToHashSet().SetEquals([ResourcePlatforms.CurseForge, ResourcePlatforms.Modrinth]),
            "The final resource search lost one of the selected sources.");

        static JObject Project(ResourcePlatforms platform, string id) => new() {
            ["Platform"] = (int)platform, ["Types"] = (int)ResourceTypes.Mod, ["Id"] = id, ["Slug"] = "test-" + id,
            ["RawName"] = "Fixture " + id, ["Description"] = "Fixture resource " + id, ["LastUpdate"] = new DateTime(2024, 1, 1),
            ["DownloadCount"] = 42, ["Tags"] = new JArray(), ["Drops"] = new JArray(210), ["ModLoaders"] = (int)ModLoaders.Fabric,
            ["Website"] = "https://example.invalid/" + id
        };
    }

    private static void ModNoticeColors()
    {
        var view = new PageInstanceModDisabled();
        var size = new Size(900, 480);
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var line = Descendants(view).OfType<System.Windows.Shapes.Rectangle>().Single();
        var label = Descendants(view).OfType<TextBlock>().Single(text => text.Text == "该版本不可使用 Mod");
        var grid = (Grid)label.Parent;
        foreach (Brush palette in new[] { Brushes.DarkSlateGray, Brushes.LightGray }) {
            grid.Resources["GlassPresentationSeparator"] = palette;
            Native.Check(ReferenceEquals(line.Fill, palette), "The mod notice divider retained its old fill after the palette changed.");
        }
    }

    internal static void DirectResourceSearch()
    {
        var projects = Enumerable.Range(1, 30).Select(index => new ResourceProject(new JObject {
            ["Platform"] = (int)ResourcePlatforms.Modrinth, ["Types"] = (int)ResourceTypes.Mod,
            ["Id"] = "NativeFixture" + index, ["RawName"] = "样本 " + index, ["Description"] = "查询结果",
            ["LastUpdate"] = new DateTime(2025, 1, 1), ["DownloadCount"] = 5, ["Tags"] = new JArray(), ["Drops"] = new JArray(211)
        })).ToArray();
        using var releaseFirstPage = new ManualResetEventSlim(false);
        using var releaseOldPage = new ManualResetEventSlim(false);
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var jobs = new System.Collections.Concurrent.ConcurrentBag<Thread>();
        var oldJob = new TaskCompletionSource<Thread>();
        var newFailureJob = new TaskCompletionSource<Thread>();
        int secondPageAttempts = 0;
        var engine = new ResourceSearchEngine<ResourceProject, ResourceSearcher.SearchResult>(new() {
            CreateProject = json => projects[(int)json["fixtureIndex"]!],
            SimplifyChinese = value => value,
            WikiEntries = () => [new() { Id = 1, ChineseName = "生存 (Survival Fixture)", Popularity = 1,
                Slugs = new() { [ResourcePlatforms.Modrinth] = "survival-fixture" } }],
            StartThread = (work, name) => { var thread = new Thread(() => work()) { IsBackground = true, Name = name }; thread.Start(); return thread; },
            Info = _ => { }, Warning = _ => { }, Warn = (_, _) => { },
            Request = url => {
                if (url.Contains("/v2/projects?", StringComparison.Ordinal)) throw new IOException("The direct Modrinth lookup failed.");
                Native.Check(url.Contains("/v2/search?", StringComparison.Ordinal) && url.Contains("&offset=15&", StringComparison.Ordinal),
                    "The next page did not resume at the committed provider offset.");
                return new JObject { ["total_hits"] = projects.Length,
                    ["hits"] = new JArray(Enumerable.Range(15, 3).Select(index => new JObject { ["fixtureIndex"] = index })) };
            }
        });
        using var session = new LauncherResourceBrowser(ResourceTypes.Mod, work => dispatcher.BeginInvoke(work), publish =>
            new("Native search fixture", task => {
                Thread worker = Thread.CurrentThread;
                jobs.Add(worker);
                var input = task.Input;
                if (input.SearchText == "New failed search")
                {
                    newFailureJob.SetResult(worker);
                    throw new InvalidOperationException("The active provider failed.");
                }
                int count = input.TargetResultCount;
                if (count == 15 && input.Storage.Results.Count == 0)
                {
                    publish(input, projects.Take(7).ToArray());
                    if (!releaseFirstPage.Wait(8000)) throw new TimeoutException("The fixture's first batch was held too long.");
                }
                if (count == 30)
                {
                    int attempt = Interlocked.Increment(ref secondPageAttempts);
                    if (attempt == 1)
                    {
                        oldJob.SetResult(worker);
                        publish(input, projects.Skip(15).Take(3).ToArray());
                        try { releaseOldPage.Wait(8000); }
                        catch (ThreadInterruptedException) { releaseOldPage.Wait(8000); }
                        publish(input, projects.Skip(15).ToArray());
                        throw new InvalidOperationException("The replaced provider failed late.");
                    }
                    if (attempt == 2)
                    {
                        engine.Run(input, new() {
                            IsCanceled = () => task.IsCanceledWithThread(worker), Progress = () => task.Progress, SetProgress = value => task.Progress = value
                        }, results => publish(input, results));
                        return;
                    }
                    input.Storage.ModrinthDirectOffset = input.Storage.ModrinthDirectTotal;
                }
                input.Storage.Results.AddRange(projects.Skip(input.Storage.Results.Count).Take(count - input.Storage.Results.Count));
                input.Storage.ModrinthOffset = input.Storage.Results.Count;
                input.Storage.ModrinthTotal = projects.Length;
                input.Storage.CurseForgeTotal = 0;
                input.Storage.ErrorMessage = "只显示一处来源的测试警告";
            }));
        var options = new ResourceSearchOptions("生存", "1.21.1", "/optimization", ModLoaders.Fabric, ResourcePlatforms.Modrinth);
        int Count() => session.Snapshot.Results.Count;
        try
        {
            session.Start(options, 15);
            VisualAssetChecks.Pump(() => Count() == 7, "first page appears before both sources finish");
            session.Navigate(1);
            Native.Check(session.Snapshot.Page == 0 && !session.Snapshot.CanNext,
                "A partially filled native results page accepted a request for the next page.");
            releaseFirstPage.Set();
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "the native first page is complete");
            Native.Check(Count() == 15 && session.Snapshot.CanNext && session.Snapshot.Warning == "只显示一处来源的测试警告",
                "The native first page dropped results or a partial-source warning.");
            session.Navigate(1);
            VisualAssetChecks.Pump(() => session.Snapshot.Page == 1 && Count() == 3, "partial preview of the second page");
            session.Navigate(2);
            Native.Check(session.Snapshot.Page == 1 && !session.Snapshot.CanNext, "The underfilled second page accepted a forward request.");
            session.Navigate(0);
            Native.Check(session.Snapshot.Page == 0 && session.Snapshot.Results.SequenceEqual(projects.Take(15)),
                "Backward navigation while page two is loading did not restore the cached first page.");
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "cached first page is ready");

            session.Start(options with { Name = "New failed search" }, 15);
            Native.Check(newFailureJob.Task.Wait(5000) && newFailureJob.Task.Result.Join(5000), "The active failed search did not finish.");
            releaseOldPage.Set();
            Native.Check(oldJob.Task.IsCompletedSuccessfully && oldJob.Task.Result.Join(5000), "The replaced search did not finish.");
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "active provider failure is displayed");
            Native.Check(session.Snapshot.Page == 0 && Count() == 0 && session.Snapshot.Error?.Message == "The active provider failed.",
                "A late callback from the replaced search overwrote the active page's rows or error.");

            session.Start(options, 15);
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "the first page is ready again");
            session.Navigate(1);
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "the underfilled second page is complete");
            Native.Check(session.Snapshot.Page == 1 && Count() == 3 && !session.Snapshot.CanNext &&
                session.Snapshot.Warning?.StartsWith("Modrinth 搜索未完成", StringComparison.Ordinal) == true,
                "A provider claiming more results reopened the next button on a completed underfilled page.");
            var committedSecondPage = session.Snapshot.Results;
            session.Navigate(2);
            Native.Check(session.Snapshot.Page == 1, "The completed underfilled page accepted a forward request.");
            session.Navigate(0);
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "the cached first page is ready again");
            session.Navigate(1);
            VisualAssetChecks.Pump(() => !session.Snapshot.Loading, "the full second page is complete");
            var input = session.Snapshot.Request;
            Native.Check(input?.SearchText == "生存" && input.GameVersion == "1.21.1" && input.Sources == ResourcePlatforms.Modrinth &&
                session.Snapshot.Results.Take(3).SequenceEqual(committedSecondPage) &&
                session.Snapshot.Results.Skip(3).SequenceEqual(projects.Skip(18)) && !session.Snapshot.CanNext,
                "The final full page lost its search, displayed incorrect rows, or offered a page after provider exhaustion.");
        }
        finally
        {
            releaseFirstPage.Set();
            releaseOldPage.Set();
            foreach (var job in jobs) Native.Check(job.Join(8000), "Native resource fixture left an unfinished search worker.");
        }
    }

    private static void RenderSettings(FrameworkElement presentation, string fixtureRoot, int width)
    {
        string evidence = Environment.GetEnvironmentVariable("PCLLF_GLASS_EVIDENCE_DIR") ?? fixtureRoot;
        if (evidence != fixtureRoot)
        {
            string full = Path.GetFullPath(evidence);
            DirectoryInfo? directory = new(full); bool allowed = false;
            for (; directory != null; directory = directory.Parent)
                if (directory.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent &&
                    (File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")))) { allowed = true; break; }
            Native.Check(allowed, "Presentation evidence must stay under a Git workspace's artifacts directory.");
            evidence = full;
        }
        Directory.CreateDirectory(evidence);
        var wallpaper = new DrawingImage(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(158, 179, 182), Color.FromRgb(217, 222, 209), 35), null, new RectangleGeometry(new Rect(0, 0, width, 720)))); wallpaper.Freeze();
        var host = new Grid { Width = width, Height = 720 };
        var background = new Image { Source = wallpaper, Stretch = Stretch.Fill, Clip = new RectangleGeometry(new Rect(24, 24, width - 48, 672), 44, 44) };
        host.Children.Add(background);
        var surface = new Border { Margin = new Thickness(24), Padding = new Thickness(24), CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(223, 245, 246, 248)), Child = presentation };
        host.Children.Add(surface);
        presentation.Width = double.NaN; presentation.Height = double.NaN;
        host.Measure(new Size(width, 720)); host.Arrange(new Rect(0, 0, width, 720)); host.UpdateLayout();
        VisualAssetChecks.Drain(); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
        string path = Path.Combine(evidence, $"settings-new-{width}.png"); VisualAssetChecks.Save(bitmap, path);
        Console.WriteLine("Actual settings presentation render (synthetic wallpaper; not native backdrop proof): " + path);
        surface.Child = null;
    }

    private static Window Host(FrameworkElement source, FrameworkElement presentation)
    {
        var root = new Grid();
        root.Children.Add(new ContentControl { Content = source, Visibility = Visibility.Hidden, IsHitTestVisible = false });
        root.Children.Add(presentation);
        var window = new Window { Content = root, Width = 850, Height = 650, Left = -30000, Top = -30000, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
        Native.Check(source.IsLoaded && !source.IsVisible && presentation.IsLoaded && presentation.IsVisible, "The semantic backend and new presentation did not have the intended independent lifecycles.");
        return window;
    }
    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement => Descendants(root).OfType<T>().Single(item => item.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke(); VisualAssetChecks.Drain();
    }
    private static void Toggle(CheckBox checkbox)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(checkbox) ?? new CheckBoxAutomationPeer(checkbox);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
        VisualAssetChecks.Drain();
    }
}
