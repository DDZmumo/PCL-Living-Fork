using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLLF.Presentation.Windowing;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

internal static class LauncherUpdatePageChecks
{
    private const string AutoCheckKey = "ToolLauncherUpdateAutoCheck";

    internal static void Run(FixtureEnvironment environment)
    {
        StartupReleaseNotesChecks.Run();
        Native.Check(ModBase.ReleaseBaselineIsPrerelease == ModBase.ReleaseBaselineVersion.Contains('-', StringComparison.Ordinal), "Release baseline and channel label disagree.");
        string expectedBuildLabel = ModBase.BuildType.ToString() switch { "Debug" => "开发版", "Snapshot" => "快照版", _ => ModBase.VersionBaseName == "dev_edition" ? "开发版" : ModBase.ReleaseBaselineIsPrerelease ? "预发布版" : "正式版" };
        Native.Check(ModBase.BuildTypeDisplay == expectedBuildLabel, "Release/development build label does not match its actual version.");
        object stable = Native.Enum("PCL.ModLauncherUpdate+UpdateChannel", "Stable")!;
        object preview = Native.Enum("PCL.ModLauncherUpdate+UpdateChannel", "StableAndPrerelease")!;
        int futureMajor = Version.Parse(ModBase.ReleaseBaselineVersion.Split('-')[0]).Major + 1;
        string stableTag = $"v{futureMajor}.0.0", newestTag = $"v{futureMajor + 1}.0.0";
        string previewTag = stableTag + "-pre-10", olderPreviewTag = stableTag + "-pre-2";
        const string oldTag = "v0.0.0";
        bool Newer(string tag, object channel) => (bool)Native.Static("PCL.ModLauncherUpdate", "NewerReleaseForChannel", tag, "dev_edition", channel)!;
        Native.Check(!Newer(oldTag, stable) && !Newer("v" + ModBase.ReleaseBaselineVersion, preview) && Newer(stableTag, stable) && Newer(previewTag, preview) && !Newer(previewTag, stable), "dev_edition update baseline/channel compatibility changed.");
        Native.Check((int)Native.Static("PCL.ModLauncherUpdate", "CompareReleaseVersions", "dev_edition", ModBase.ReleaseBaselineVersion)! > 0 &&
            (int)Native.Static("PCL.ModLauncherUpdate", "CompareReleaseVersions", "dev_edition", "dev_edition")! == 0 && !Newer("dev_edition", preview), "Development editions leaked into public release ordering.");
        Native.Check(Native.Main.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion == ModBase.VersionBaseName, "Displayed and executable product versions disagree.");

        object? previousUpdate = Native.Field(typeof(ModMain), "FrmSetupUpdate").GetValue(null);
        object? previousLeft = Native.Field(typeof(ModMain), "FrmSetupLeft").GetValue(null);
        bool previousAutoCheck = Settings.Get<bool>(AutoCheckKey);
        Native.Check(bool.TryParse(Settings.GetDefault(AutoCheckKey), out bool enabledByDefault) && enabledByDefault,
            "An existing installation would silently stop checking for updates.");
        Native.Cache(AutoCheckKey, false);
        Native.Cache("ToolLauncherUpdateSource", 1);
        Native.Cache("ToolLauncherUpdateChannel", 0);
        using var allowRead = new ManualResetEventSlim(false);
        bool failRead = false;
        Window? window = null;
        try
        {
            var left = new PageSetupLeft();
            ModMain.FrmSetupLeft = left;
            Native.Check(left.FindName("ItemUpdate") is MyListItem { Title: "更新" }, "Settings sidebar has no native update entry.");
            var page = (PageSetupUpdate)left.PageGet(FormMain.PageSubType.SetupUpdate);
            Native.Check(ReferenceEquals(ModMain.FrmSetupUpdate, page), "Update sidebar did not reuse its actual cached page.");
            Native.Check(page.FindName("TextSearchVersion") is MySearchBox && page.FindName("ComboVersionType") is null && page.FindName("ComboVersionState") is null && page.FindName("BtnResetFilter") is null,
                "Update search still exposes removed filtering controls.");
            ((MyTextBox)((MySearchBox)page.FindName("TextSearchVersion")).FindName("TextBox")).Focusable = false;
            var system = new PageSetupSystem();
            Native.Check(system.FindName("ComboLauncherUpdateSource") is null && system.FindName("BtnSystemUpdate") is null, "Old settings still expose duplicate update controls.");
            JObject old = Release(oldTag), newest = Release(newestTag), prerelease = Release(previewTag);
            var entries = new List<JObject> { old, newest, prerelease, Release(stableTag), Release(olderPreviewTag) };
            int reads = 0;
            Func<List<JObject>> read = () =>
            {
                Interlocked.Increment(ref reads);
                if (!allowRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture release reader was not released.");
                if (Volatile.Read(ref failRead)) throw new IOException("Fixture catalogue unavailable");
                return entries;
            };
            FieldInfo reader = Native.Field(typeof(PageSetupUpdate), "ReadReleases");
            Type[] types = reader.FieldType.GetGenericArguments();
            reader.SetValue(page, Expression.Lambda(reader.FieldType, Expression.Invoke(Expression.Constant(read)), Expression.Parameter(types[0]), Expression.Parameter(types[1])).Compile());
            window = new Window { Width = 1000, Height = 700, Left = -24000, Top = -24000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = page, Background = Brushes.White };
            window.Show();
            Native.Check(!window.IsActive, "Offscreen update-page test window took desktop input focus.");
            var rows = (StackPanel)page.FindName("PanVersions");
            var loading = (FrameworkElement)page.FindName("Loading");
            var hint = (FrameworkElement)page.FindName("HintStatus");
            var count = (FrameworkElement)page.FindName("LabResultCount");
            Native.Check(loading.Visibility == Visibility.Collapsed && Volatile.Read(ref reads) == 0,
                "Opening the update page still fetched releases with automatic checks disabled.");
            ToggleAutoCheckAndManuallyRefresh(page);
            void CheckLoadingState()
            {
                Native.Check(loading.Visibility == Visibility.Visible && rows.Visibility == Visibility.Collapsed && hint.Visibility == Visibility.Collapsed &&
                    count.Visibility == Visibility.Collapsed && ((FrameworkElement)page.FindName("LabNoResults")).Visibility == Visibility.Collapsed,
                    "Checking updates left the current-version row, blue hint or old result state visible.");
            }
            CheckLoadingState();
            ((MySearchBox)page.FindName("TextSearchVersion")).Text = "dev";
            CheckLoadingState();
            ((MySearchBox)page.FindName("TextSearchVersion")).Text = "";
            allowRead.Set();
            VisualAssetChecks.Pump(() => loading.Visibility == Visibility.Collapsed, "Update-page async reader did not finish.");
            Native.Check(rows.Children.Count == 6, $"Update-page async result has {rows.Children.Count} rows after {reads} reads: {((MyHint)hint).Text}; query='{((MySearchBox)page.FindName("TextSearchVersion")).Text}'.");
            Native.Check(rows.Visibility == Visibility.Visible && hint.Visibility == Visibility.Visible && count.Visibility == Visibility.Visible,
                "Successful update check did not restore its list, blue hint and result count.");
            string[] Titles() => rows.Children.Cast<MyListItem>().Select(row => row.Title).ToArray();
            Native.Check(Titles().SequenceEqual(new[] { newestTag, stableTag, previewTag, olderPreviewTag, ModBase.VersionBaseName, oldTag }),
                "Current build was pinned instead of participating in the version order.");
            MyListItem CurrentRow() => rows.Children.Cast<MyListItem>().Single(row => row.Title == ModBase.VersionBaseName);
            Native.Check(rows.Children.Cast<MyListItem>().Single(row => row.Title == previewTag).Info.Contains("未启用"), "Browsing prereleases bypassed the stable subscription.");
            foreach (MyListItem row in rows.Children)
            {
                Native.Check(row.Logo.EndsWith("Branding/LivingPCL.ico", StringComparison.Ordinal) && row.PathLogo is Canvas { Background: ImageBrush brush } &&
                    ReferenceEquals(brush.ImageSource, ((Image)page.FindName("ImgLauncher")).Source), "A release row still uses the generic game icon or a low-resolution logo.");
                var badgeView = row.Inlines.OfType<InlineUIContainer>().Select(inline => (Border)inline.Child).Single(border => Equals(border.Tag, "release-channel"));
                string badge = ((TextBlock)badgeView.Child).Text;
                Native.Check(badge == (row.Title == "dev_edition" ? "开发版" : row.Title.Contains('-') ? "预发布版" : "正式版"), "Release row has the wrong visible channel badge.");
            }
            var search = (MySearchBox)page.FindName("TextSearchVersion");
            Native.Check(search.FindName("TextBox") is MyTextBox && search.FindName("BtnClear") is MyIconButton &&
                !((MyCard)page.FindName("CardVersions")).IsAncestorOf(search),
                "Search did not reuse the mod manager's independent MySearchBox card and clear control.");
            search.Text = " " + stableTag.ToUpperInvariant() + " ";
            Native.Check(Titles().SequenceEqual(new[] { stableTag, previewTag, olderPreviewTag }), "Version search omitted matching releases or left an unmatched current row pinned.");
            search.Text = stableTag.TrimStart('v') + "-pre";
            Native.Check(Titles().SequenceEqual(new[] { previewTag, olderPreviewTag }), "Search omitted a matching prerelease.");
            search.Text = "DEV_EDITION";
            Native.Check(Titles().SequenceEqual(ModBase.VersionBaseName == "dev_edition" ? new[] { "dev_edition" } : Array.Empty<string>()),
                "Development-edition search ignored the actual official/development build identity.");
            search.Text = "does-not-exist";
            Native.Check(rows.Children.Count == 0 && ((FrameworkElement)page.FindName("LabNoResults")).Visibility == Visibility.Visible &&
                ((TextBlock)page.FindName("LabResultCount")).Text.Contains("显示 0 个"), "Empty search has no honest result count/empty state.");
            var clear = (MyIconButton)search.FindName("BtnClear");
            Native.Call(typeof(MyIconButton), "OnClick", clear, clear, EventArgs.Empty);
            Native.Check(rows.Children.Count == 6 && reads == 1 && ((MyComboBox)page.FindName("ComboChannel")).SelectedIndex == 0,
                "Native search clearing/filtering made extra requests or changed the update subscription.");
            CheckProjectedLatestRelease(page, search, newestTag, stableTag, oldTag);
            Native.Check(!window.IsActive, "Native search clear activated the offscreen test window.");
            Native.Check(((TextBlock)page.FindName("LabCurrentVersion")).Text == ModBase.VersionBaseName, "Update page mislabeled the current build.");
            Native.Check(((TextBlock)page.FindName("LabCurrentVersion")).FontSize >= 24 && page.FindName("LabIconBrand") is null && ((Image)page.FindName("ImgLauncher")).Source is BitmapSource { PixelWidth: >= 256 }, "Launcher branding did not use the existing HD program icon and large version.");
            page.UpdateLayout();
            var currentBadge = CurrentRow().Inlines.OfType<InlineUIContainer>().Select(inline => (Border)inline.Child).Single(border => Equals(border.Tag, "release-channel"));
            Native.Check(currentBadge.TranslatePoint(new Point(), CurrentRow()).X < 300, "Channel badge is still detached from the version name.");
            var frame = CurrentRow().Children.OfType<Border>().Single(border => Equals(border.Tag, "current-version-frame"));
            var installedBadge = CurrentRow().Inlines.OfType<InlineUIContainer>().Select(inline => (Border)inline.Child).Single(border => Equals(border.Tag, "current-version-badge"));
            Native.Check(frame.BorderBrush is SolidColorBrush green && green.Color.G > green.Color.R && frame.BorderThickness.Left >= 1 &&
                ((TextBlock)installedBadge.Child).Text == "当前版本" && rows.Children.Cast<MyListItem>().Count(row => row.Children.OfType<Border>().Any(border => Equals(border.Tag, "current-version-frame"))) == 1,
                "Current version does not have its unique green frame and current-version label.");
            CurrentVersionNotesClick(CurrentRow());
            var currentIcon = (Canvas)CurrentRow().PathLogo!;
            Native.Check(Math.Abs(currentIcon.ActualWidth - currentIcon.ActualHeight) < 1 && currentIcon.ActualWidth >= 47 &&
                currentIcon.Background is ImageBrush { Stretch: Stretch.Uniform, ImageSource: BitmapSource { PixelWidth: >= 256 } }, "Current-version icon was squeezed or lost its HD source.");
            string[] args = Environment.GetCommandLineArgs(); int option = Array.IndexOf(args, "--preview-path");
            string? previewPath = option >= 0 && option + 1 < args.Length ? Path.GetFullPath(args[option + 1]) : null;
            void SavePreview(string suffix)
            {
                if (previewPath is null) return;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                string path = Path.Combine(Path.GetDirectoryName(previewPath)!, Path.GetFileNameWithoutExtension(previewPath) + suffix + ".png");
                using FileStream stream = File.Create(path); png.Save(stream);
            }
            SavePreview("");
            LauncherUpdateCardBackgroundChecks.Run(page, Path.Combine(environment.Data, "update-card"));
            var header = (Grid)page.FindName("HeaderGrid");
            var options = (Grid)page.FindName("PanUpdateOptions");
            var identity = (FrameworkElement)page.FindName("PanIdentity");
            var refresh = (FrameworkElement)page.FindName("BtnRefresh");
            var releasePage = (FrameworkElement)page.FindName("BtnReleasePage");
            Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), header), element.RenderSize);
            void CheckCompact(double width, string suffix, bool verticalButtons)
            {
                window.Width = width;
                VisualAssetChecks.Pump(() => Math.Abs(page.ActualWidth - width) < 1 && Grid.GetRow(options) == 1, "Compact update header did not move its selectors below the identity.");
                page.UpdateLayout();
                Rect name = Bounds(identity), check = Bounds(refresh), open = Bounds(releasePage), selectors = Bounds(options);
                Native.Check(check.Left >= name.Right - 1 && open.Left >= name.Right - 1 && check.Top < selectors.Top && open.Bottom <= selectors.Top &&
                    Math.Max(check.Right, open.Right) <= header.ActualWidth + 1 && selectors.Width <= header.ActualWidth + 1,
                    "Compact actions did not occupy the space beside the version, or overlapped the selectors/card.");
                Native.Check(verticalButtons ? open.Top >= check.Bottom + 5 : open.Left >= check.Right + 5 && Math.Abs(open.Top - check.Top) < 1,
                    "Compact action buttons overlapped or failed to adapt at the narrower width.");
                Native.Check(Math.Abs(currentIcon.ActualWidth - currentIcon.ActualHeight) < 1, "Resizing squeezed the current-version icon.");
                Point searchPosition = search.TranslatePoint(new Point(), page);
                Native.Check(searchPosition.X >= 0 && searchPosition.X + search.ActualWidth <= page.ActualWidth && search.ActualWidth >= page.ActualWidth - 60,
                    "Compact search overflowed or did not reclaim the removed filter's space.");
                SavePreview(suffix);
            }
            CheckCompact(750, "-compact", false);
            CheckCompact(650, "-narrow", true);
            window.Width = 1000;
            VisualAssetChecks.Pump(() => Grid.GetRow(options) == 0 && page.ActualWidth >= 999, "Wide update header did not restore its original layout.");
            page.UpdateLayout();
            Native.Check(Bounds(refresh).Top >= Bounds(options).Bottom + 5 && Bounds(options).Left >= Bounds(identity).Right,
                "Wide update header did not restore the controls to the right of the version.");
            CheckCompact(650, "-narrow", true);
            CheckCompact(550, "-minimum", true);
            var scroll = (ScrollViewer)page.FindName("PanBack");
            scroll.ScrollToEnd();
            var last = (FrameworkElement)rows.Children[rows.Children.Count - 1];
            VisualAssetChecks.Pump(() => scroll.VerticalOffset > 0 && last.TranslatePoint(new Point(), scroll).Y + last.ActualHeight <= scroll.ActualHeight + 1,
                "Last matching release cannot be reached by scrolling.");
            SavePreview("-bottom");
            allowRead.Reset(); Volatile.Write(ref failRead, true);
            var refreshButton = (MyButton)page.FindName("BtnRefresh");
            Native.Call(typeof(MyButton), "OnClick", refreshButton, refreshButton, null);
            CheckLoadingState();
            allowRead.Set();
            VisualAssetChecks.Pump(() => loading.Visibility == Visibility.Collapsed && ((MyHint)hint).Text.Contains("Fixture catalogue unavailable"),
                "Failed update check did not finish.");
            Native.Check(rows.Visibility == Visibility.Visible && rows.Children.Count == 1 && hint.Visibility == Visibility.Visible && count.Visibility == Visibility.Visible,
                "Failed update check did not restore the current version and its error hint.");
            CurrentVersionNotesClick(CurrentRow());
            if (!string.Equals(ModBase.VersionBaseName, "dev_edition", StringComparison.Ordinal))
            {
                Native.Call(typeof(PageSetupUpdate), "RenderReleases", page, new List<JObject> { Release("v" + ModBase.VersionBaseName) }, null);
                Native.Check(rows.Children.Count == 1 && CurrentRow().Children.OfType<Border>().Any(border => Equals(border.Tag, "current-version-frame")),
                    "The installed release duplicated or replaced the green current-version row.");
                CurrentVersionNotesClick(CurrentRow());
            }
            window.Close(); window = null;
            CheckProjectedScrollEdges(page);
            bool drained = false;
            Application.Current.Dispatcher.BeginInvoke((Action)(() => drained = true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            VisualAssetChecks.Pump(() => drained, "Update-page unload did not drain its callbacks.");
            environment.AssertNoForbiddenIo();
            ReleaseNotesChecks.Run();
            Console.WriteLine("PASS launcher update page: current row/hint/count hidden during checks and restored on success/failure, native search/clear, all matches, stable subscription, PCLLF icons/inline badges, compact resizing and scrolling; no HTTP or updater execution.");
        }
        finally
        {
            allowRead.Set();
            window?.Close();
            Native.Field(typeof(ModMain), "FrmSetupUpdate").SetValue(null, previousUpdate);
            Native.Field(typeof(ModMain), "FrmSetupLeft").SetValue(null, previousLeft);
            Settings.Set(AutoCheckKey, previousAutoCheck);
        }
    }

    private static void ToggleAutoCheckAndManuallyRefresh(PageSetupUpdate page)
    {
        using var session = GlassPresentation.Create(page);
        var controls = Descendants(session.View).ToArray();
        CheckBox toggle = controls.OfType<CheckBox>().Single(control => AutomationProperties.GetName(control) == "自动检查更新");
        Native.Check(toggle.IsChecked == false && controls.OfType<TextBlock>().Any(text => text.Text == "尚未检查更新"),
            "The update page did not reflect the disabled setting or reported its unchecked build as current.");
        toggle.SetCurrentValue(CheckBox.IsCheckedProperty, true);
        Native.Check(Settings.Get<bool>(AutoCheckKey), "The projected switch failed to turn automatic checks on.");
        toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
        Native.Check(!Settings.Get<bool>(AutoCheckKey) && bool.TryParse(ModBase.ReadIni("Setup", AutoCheckKey, ""), out bool saved) && !saved,
            "The projected switch did not persist the disabled preference.");
        Button manual = controls.OfType<Button>().Single(button => Equals(button.Content, "检查更新"));
        manual.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, manual));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void CheckProjectedLatestRelease(PageSetupUpdate page, MySearchBox search, string newestTag, string olderUpdate, string historicalTag)
    {
        FieldInfo request = Native.Field(typeof(PageSetupUpdate), "UpdateRelease");
        object? originalRequest = request.GetValue(page);
        string originalSearch = search.Text;
        List<string?> requested = new();
        request.SetValue(page, (Action<JObject>)(release => requested.Add((string?)release["tag_name"])));
        try
        {
            using var session = GlassPresentation.Create(page);
            var elements = Descendants(session.View).ToArray();
            Button install = elements.OfType<Button>().Single(button => Equals(button.Content, "下载并更新"));
            foreach (string query in new[] { olderUpdate, historicalTag, "no-matching-release" })
            {
                search.Text = query;
                session.Refresh();
                Native.Check(install.Visibility == Visibility.Visible && Equals(install.ToolTip, $"下载 {newestTag} 并更新启动器") &&
                    elements.OfType<TextBlock>().Any(text => text.Text == "发现新版本") &&
                    elements.OfType<TextBlock>().Any(text => text.Text == newestTag + " 更新内容"),
                    "Filtering history changed the update headline, download action or release notes.");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, install));
            }
            Native.Check(requested.SequenceEqual(Enumerable.Repeat(newestTag, 3)), "The update action downloaded a filtered history row instead of the newest eligible release.");
            Console.WriteLine("PASS projected updates retain the newest eligible headline, notes and download target across filtered and empty history.");
        }
        finally { search.Text = originalSearch; request.SetValue(page, originalRequest); }

    }

    private static void CheckProjectedScrollEdges(PageSetupUpdate page)
    {
        using var session = GlassPresentation.Create(page);
        var fade = ((ContentControl)session.View).Content as PCLLF.Wpf.Glass.ScrollEdgeFade;
        Native.Check(fade is not null, "The OTA page did not use the shared scroll-edge fade.");
        if (SystemParameters.HighContrast) return;
        var window = new Window { Content = session.View, Width = 750, Height = 310, Left = -24000, Top = -24000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try {
            window.Show(); window.UpdateLayout();
            var scroll = fade!.ScrollViewer;
            VisualAssetChecks.Pump(() => scroll.ScrollableHeight > 160 &&
                scroll.Template.FindName("PART_ScrollContentPresenter", scroll) is FrameworkElement { OpacityMask: LinearGradientBrush },
                "The projected update page has no scrollable content or mask.");
            var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            byte Leading() => ((LinearGradientBrush)viewport.OpacityMask).GradientStops[0].Color.A;
            byte Trailing() => ((LinearGradientBrush)viewport.OpacityMask).GradientStops[^1].Color.A;
            Native.Check(Leading() == 255 && Trailing() < 255, "The update page faded the wrong edge at the top.");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            VisualAssetChecks.Pump(() => Leading() < 255 && Trailing() < 255, "The update page did not fade both edges while scrolled.");
            scroll.ScrollToEnd();
            VisualAssetChecks.Pump(() => Leading() < 255 && Trailing() == 255, "The update page faded the wrong edge at the bottom.");
        } finally { window.Content = null; window.Close(); VisualAssetChecks.Drain(); }
    }

    private static void CurrentVersionNotesClick(MyListItem row)
    {
        Native.Check(row.Type == MyListItem.CheckType.Clickable, "The green current-version row has no native click entry for manually reopening update notes.");
        FormMain? previous = ModMain.FrmMain;
        List<Action> scheduled = new();
        List<bool> automatic = new();
        int preferenceReads = 0;
        var host = new LegacyMainWindowHost<FormMain.PageStackData, ModMinecraft.McInstance, ModAnimation.AniData>
        {
            NewThread = (work, _, _) => scheduled.Add(work),
            ReadString = _ => { preferenceReads++; return "dismissed"; },
            UpdateLogIdentity = () => "dismissed",
            ShowReleaseNotes = (isAutomatic, _) => { automatic.Add(isAutomatic); return true; }
        };
        var controller = new LegacyMainWindowController<FormMain.PageStackData, ModMinecraft.McInstance, ModAnimation.AniData>(new LegacyMainWindowView(), host);
        // Install only the real facade/controller field; never invoke FormMain's
        // startup constructor or send a release lookup/modal over the platform.
        FormMain shell = (FormMain)RuntimeHelpers.GetUninitializedObject(typeof(FormMain));
        Native.Field(typeof(FormMain), "Controller").SetValue(shell, controller);
        Native.Property(typeof(FormMain), "BtnExtraBack").SetValue(shell, new MyExtraButton());
        try
        {
            ModMain.FrmMain = shell;
            Native.Call(typeof(MyListItem), "OnClick", row, row, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
            Native.Check(scheduled.Count == 1 && automatic.Count == 0, "Current-version row did not reach the existing asynchronous Main update-note entry.");
            scheduled[0]();
            Native.Check(automatic.SequenceEqual(new[] { false }) && preferenceReads == 0,
                "Current-version row did not reopen the manual notice independently of the saved dismissal.");
        }
        finally { ModMain.FrmMain = previous; }
        Console.WriteLine("PASS current-version row: native clickable green row -> genuine FormMain facade -> controller background/manual notice, saved dismissal bypassed; no HTTP or modal wait.");
    }
    private static JObject Release(string tag)
    {
        string file = "PCL-Living-Fork-" + tag.TrimStart('v') + ".exe";
        return new JObject { ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = tag.Contains('-'), ["body"] = "Synthetic release notes", ["assets"] = new JArray(
            new JObject { ["name"] = file, ["size"] = 1, ["browser_download_url"] = "https://github.com/DDZmumo/PCL-Living-Fork/releases/download/" + tag + "/" + file },
            new JObject { ["name"] = "SHA256SUMS.txt", ["size"] = 1, ["browser_download_url"] = "https://github.com/DDZmumo/PCL-Living-Fork/releases/download/" + tag + "/SHA256SUMS.txt" }) };
    }
}
