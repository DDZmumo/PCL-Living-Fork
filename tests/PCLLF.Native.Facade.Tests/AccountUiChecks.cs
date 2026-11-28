using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLLF.Presentation.Launch;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

// LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 and LF main50
// 50b028da5fcad2226d74d901a41d8c43a249bfb2 Pages/PageLaunch/PageLoginMs*,
// PageLaunchLeft and Modules/Minecraft/ModLaunch are the behavior sources.
// LF LICENCE. Synthetic transport/store are existing platform seams, never a
// second account/login implementation. No CE policy or real authorization.
internal static class AccountUiChecks
{
    internal static T Part<T>(FrameworkElement owner, string name) where T : class =>
        owner.FindName(name) as T ?? throw new MissingMemberException(owner.GetType().FullName, name);

    internal static void Run(FixtureEnvironment environment, bool glassPortal = true) => RunGlass(environment);

    private static void RunGlass(FixtureEnvironment environment) {
        Native.Cache("UiShellStyle", 1);
        using var scope = new AccountUiChecksScope(environment, createLegacyPage: false);
        using var view = new GlassLauncherView();
        Type type = Native.Type("PCL.GlassAccountWorkspace");
        using var workspace = (IDisposable)Activator.CreateInstance(type, Native.Flags, null, [view], null)!;
        try {
            Native.Call(type, "UseMethod", workspace, ModLaunch.McLoginType.Ms);
            Native.Call(type, "Open", workspace);
            CheckNoLegacyLoginControls();

            scope.BeginScenario(AccountUiChecksScope.Scenario.Success);
            Task login = SignIn("FixtureB");
            scope.AwaitRequest();
            Native.Check(scope.Loader.Input.UserName == "FixtureB" && scope.Loader.Input.OAuthRefreshToken == "fixture-refresh-b",
                "The native workspace did not send the saved account to the real provider loader.");
            scope.ReleaseRequest();
            scope.AwaitCompletion(ModBase.LoadState.Finished, () => login.IsCompleted);
            var saved = (ModLaunch.McLoginMs)Native.Static("PCL.LauncherAccountSession", "MicrosoftLaunchInput")!;
            Native.Check(scope.RequestCount == 6 && saved.UserName == "FixtureBUpdated" && saved.OAuthRefreshToken == "fixture-refresh-new" &&
                saved.AccessToken == "fixture-minecraft-access" && saved.Uuid == AccountUiChecksScope.ProfileId &&
                JToken.DeepEquals(JObject.Parse(saved.ProfileJson), JObject.Parse(AccountUiChecksScope.Profile)),
                "The native launch input did not read the completed provider session.");
            CheckNoLegacyLoginControls();
            scope.AssertPurchaseHintSaved();
            CheckAvatars(environment, saved.UserName, saved.Uuid);

            JObject history = JObject.Parse(Settings.Get<string>("LoginMsJson"));
            Native.Call(type, "SignOut", workspace);
            Native.Check(!LauncherApplicationServices.Accounts.ReadSnapshot().Authenticated &&
                JToken.DeepEquals(history, JObject.Parse(Settings.Get<string>("LoginMsJson"))),
                "Native sign-out retained the active session or removed saved accounts.");

            foreach (var scenario in new[] { AccountUiChecksScope.Scenario.Failure, AccountUiChecksScope.Scenario.Cancel }) {
                var before = scope.Store.Snapshot();
                scope.BeginScenario(scenario);
                login = SignIn("FixtureA");
                scope.AwaitRequest();
                if (scenario == AccountUiChecksScope.Scenario.Cancel) scope.Loader.Cancel();
                else scope.ReleaseRequest();
                var terminal = scenario == AccountUiChecksScope.Scenario.Cancel ? ModBase.LoadState.Canceled : ModBase.LoadState.Failed;
                scope.AwaitCompletion(terminal, () => login.IsCompleted);
                Native.Check(scope.RequestCount == 1 && scope.Store.EqualsSnapshot(before),
                    "A failed or canceled native sign-in changed saved sessions.");
                CheckNoLegacyLoginControls();
            }
            AccountRoleSwitchChecks.Run(scope);
            scope.AssertIdle();
            Console.WriteLine("PASS glass-account-ui: saved account, real provider loader, encrypted session, avatar identity, sign-out, error and cancellation without the old login controls.");
        } catch (Exception error) { scope.ReportPrimary(error); throw; }

        Task SignIn(string name) => (Task)Native.Call(type, "SignInMicrosoft", workspace, name)!;
        static void CheckNoLegacyLoginControls() => Native.Check(typeof(ModMain).GetField("FrmLoginMs") is null,
            "Removed login UI fields remain on the application.");
    }

    private static void CheckAvatars(FixtureEnvironment environment, string name, string uuid) {
        var oldAvatar = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
        oldAvatar.Freeze();
        using var view = new GlassLauncherView();
        view.SetState(new GlassLauncherViewState { AccountName = "FixtureA", AccountAvatar = oldAvatar });
        Type workspaceType = Native.Type("PCL.GlassAccountWorkspace");
        var constructor = workspaceType.GetConstructor(Native.Flags, null, [typeof(GlassLauncherView)], null)!;
        using var workspace = (IDisposable)constructor.Invoke([view]);
        Native.Call(workspaceType, "Open", workspace);
        var cards = (List<FrameworkElement>)Native.Get(workspace, "_cards")!;
        var card = cards.Single(item => AutomationProperties.GetName(item) == name);
        Native.Check(!ContainsAvatar(card, oldAvatar), "The new Microsoft account card adopted the preceding account's rail avatar while its own skin was loading.");
        Native.Check(ContainsQuestionMark(card), "A current card awaiting its avatar did not show the question mark.");
        var request = new LegacySkinRequest(name, uuid);
        var preceding = new LegacySkinRequest("FixtureA", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        string skinPath = Path.Combine(environment.Root, "account-preview-skin.png");
        BitmapSource pixels = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(new byte[] { 24, 190, 44, 255 }, 64 * 64).SelectMany(pixel => pixel).ToArray(), 64 * 4);
        using (var output = File.Create(skinPath)) {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            encoder.Save(output);
        }
        const string renderer = "PCL.LauncherSkinAvatars";
        void Publish(LegacySkinRequest identity, bool fetched, bool current = true) =>
            Native.Static(renderer, "Render", "Ms", identity, skinPath, fetched, (Func<bool>)(() => current));
        try {
            Publish(preceding, fetched: true);
            Publish(request, fetched: true, current: false);
            Native.Call(workspaceType, "Refresh", workspace);
            Native.Check(ContainsQuestionMark(card) && Native.Static("PCL.LauncherSkinSessions", "TextureFile", "Ms", request) is null,
                "A delayed skin for the preceding account reached the current card or became importable.");
            Publish(request, fetched: true);
            var confirmed = (BitmapSource)Native.Static("PCL.LauncherSkinSessions", "Avatar", "Ms", request)!;
            Native.Call(workspaceType, "Refresh", workspace);
            Native.Check(cards.Contains(card) && ContainsAvatar(card, confirmed) &&
                Equals(Native.Static("PCL.LauncherSkinSessions", "TextureFile", "Ms", request), skinPath),
                "The existing current card did not update with its avatar and a usable texture.");
            Publish(request, fetched: false);
            Native.Call(workspaceType, "Refresh", workspace);
            Native.Check(ContainsAvatar(card, confirmed) && Native.Static("PCL.LauncherSkinSessions", "TextureFile", "Ms", request) is null,
                "The fallback replaced the last confirmed avatar or exposed a fallback texture for import.");

            string stored = (string)Native.Static("PCL.GlassSkinHead", "HeadPath", "Ms", request)!;
            VisualAssetChecks.Pump(() => File.Exists(stored), "last confirmed account head is persisted");
            var cache = Native.Field(Native.Type("PCL.GlassSkinHead"), "LastGoodHeads").GetValue(null)!;
            Native.Call(cache.GetType(), "Clear", cache);
            var displayed = Native.Field(Native.Type(renderer), "heads").GetValue(null)!;
            Native.Call(displayed.GetType(), "Clear", displayed);
            var restored = Native.Static("PCL.LauncherSkinSessions", "Avatar", "Ms", request) as BitmapSource;
            Native.Check(restored is not null && !ReferenceEquals(restored, confirmed) && Pixel(restored).SequenceEqual(Pixel(confirmed)),
                "The last confirmed avatar did not survive a fresh in-memory cache.");
            view.SetState(new GlassLauncherViewState());
            Native.Check(Part<Image>(view, "AccountAvatarImage").Source is null && Part<TextBlock>(view, "AccountFallbackGlyph").Text == "?",
                "The signed-out rail displayed a Minecraft avatar.");
            SavedSiteWaitsForChoice(view, workspace, workspaceType, confirmed);
            var types = (Panel)Native.Get(workspace, "_types")!;
            Native.Check(types.Children.OfType<Button>().Count() == 3 &&
                types.Children.OfType<Button>().Any(button => AutomationProperties.GetName(button) == "外置登录"),
                "The global account workspace did not offer all three login methods.");
            Native.Call(workspaceType, "OpenSiteForm", workspace, new object?[] { null });
            var form = (Panel)Native.Get(workspace, "_stageCurrent")!;
            var remember = form.Children.OfType<CheckBox>().Single();
            Native.Check(Equals(remember.Content, "记住密码") && remember.IsChecked == false &&
                ReferenceEquals(remember.Style, view.FindResource("GlassCheckBox")),
                "The external-login checkbox lost the workspace style or its opt-in default.");
            SiteFormTransitions(view, workspaceType, workspace);
        } finally {
            var displayed = Native.Field(Native.Type(renderer), "heads").GetValue(null)!;
            Native.Call(displayed.GetType(), "Clear", displayed);
        }
        QueuedSkinResults();

        static byte[] Pixel(BitmapSource source) {
            var bytes = new byte[4];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(new Int32Rect(70, 70, 1, 1), bytes, 4, 0);
            return bytes;
        }
    }

    private static void SiteFormTransitions(GlassLauncherView view, Type workspaceType, object workspace) {
        var window = new System.Windows.Window { Content = view, Width = 1080, Height = 680,
            Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        string savedSites = Settings.Get<string>("LfSkinSites");
        try {
            Native.Cache("LfSkinSites", "");
            view.SetAccountWorkspace((FrameworkElement)Native.Property(workspaceType, "Side").GetValue(workspace)!,
                (FrameworkElement)Native.Property(workspaceType, "Scene").GetValue(workspace)!);
            window.Show(); window.UpdateLayout(); VisualAssetChecks.Drain();
            view.SetAccountWorkspaceOpen(true, immediate: true);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var stage = (Panel)Native.Get(workspace, "_stage")!;

            // Close while the outgoing server list is still a visual child. Hiding the workspace snaps the spring.
            Native.Call(workspaceType, "OpenServers", workspace, true);
            var emptyServers = (Grid)Native.Get(workspace, "_stageCurrent")!;
            var empty = emptyServers.Children.OfType<StackPanel>()
                .SingleOrDefault(panel => Grid.GetRow(panel) == 1 && panel.Children.OfType<GlassStatusGlyph>().Any());
            Native.Check(empty is not null && empty.Children.OfType<TextBlock>().Single().Text == "还没有添加验证服务器",
                "The empty external server list did not show the shared status glyph and caption.");
            window.UpdateLayout();
            var glyph = empty!.Children.OfType<GlassStatusGlyph>().Single();
            var outline = ((Canvas)glyph.Child).Children.OfType<System.Windows.Shapes.Path>().First();
            Native.Check(glyph.IsVisible && glyph.ActualWidth == 52 &&
                ReferenceEquals(outline.Stroke, view.FindResource("GlassSecondaryTextBrush")),
                "The shared status glyph was clipped or did not use the account workspace palette.");
            var addServer = emptyServers.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Button>())
                .Single(button => AutomationProperties.GetName(button) == "添加验证服务器");
            addServer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation)
                Native.Check(stage.Children.Count > 1, "No outgoing form was mounted when the account workspace was closed.");
            view.SetAccountWorkspaceOpen(false, immediate: true);
            VisualAssetChecks.Drain();
            Native.Check(stage.Children.Count == 1 && ReferenceEquals(stage.Children[0], Native.Get(workspace, "_stageCurrent")),
                "Closing during a side panel swap did not settle to the current panel.");

            view.SetAccountWorkspaceOpen(true, immediate: true);
            window.UpdateLayout(); VisualAssetChecks.Drain();
            var form = (Panel)Native.Get(workspace, "_stageCurrent")!;
            var back = ((Grid)form.Children[0]).Children.OfType<Button>().Single();
            Native.Check(AutomationProperties.GetName(back) == "返回外置登录服务器列表", "The add-server form lacks an accessible server-list back action.");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var servers = (Panel)Native.Get(workspace, "_stageCurrent")!;
            Native.Check(Native.Get(workspace, "_sideMode")?.ToString() == "Servers" &&
                ((Grid)servers.Children[0]).Children.OfType<Button>().Count() == 0,
                "The form back button did not return to the external server list.");

            Native.Cache("LfSkinSites", savedSites);
            Native.Call(workspaceType, "OpenServers", workspace, true);
            var saved = (Grid)Native.Get(workspace, "_stageCurrent")!;
            var list = (StackPanel)saved.Children.OfType<ScrollViewer>().Single().Content;
            Native.Check(list.Children.Count == 1 && !saved.Children.OfType<StackPanel>()
                .SelectMany(panel => panel.Children.OfType<GlassStatusGlyph>()).Any(),
                "A populated external server list must show its saved server without the empty icon.");
        } finally {
            Native.Cache("LfSkinSites", savedSites);
            view.SetAccountWorkspace(null!, null!);
            window.Content = null; window.Close(); VisualAssetChecks.Drain();
        }
    }

    private static void SavedSiteWaitsForChoice(GlassLauncherView view, IDisposable workspace, Type workspaceType, BitmapSource head) {
        const string server = "https://saved-skin-site.invalid/api";
        var roles = new JArray();
        var files = new List<string>();
        foreach (string id in new[] { "profile-one", "profile-two" }) {
            var request = new LegacySkinRequest("Player", id, server, UsesSkinSite: true);
            Native.Static("PCL.GlassSkinHead", "Remember", "Auth", request, head);
            files.Add((string)Native.Static("PCL.GlassSkinHead", "HeadPath", "Auth", request)!);
            roles.Add(new JObject { ["Id"] = id, ["Name"] = "Player" });
        }
        var site = new JObject { ["Name"] = "Fixture Site", ["Server"] = server,
            ["Accounts"] = new JArray(new JObject { ["Username"] = "Fixture", ["SelectedId"] = "profile-one", ["Profiles"] = roles }) };
        Native.Cache("LfSkinSiteActive", 0);
        Native.Cache("LfSkinSites", new JArray(site).ToString(Newtonsoft.Json.Formatting.None));
        var sites = (System.Collections.IList)Native.Static("PCL.GlassSkinSites", "Load")!;
        Native.Call(workspaceType, "UseSite", workspace, sites[0]);
        var cards = (List<FrameworkElement>)Native.Get(workspace, "_cards")!;
        Native.Check(Native.Get(workspace, "_choosing") is not null && ContainsQuestionMark((FrameworkElement)Native.Get(workspace, "_stageCurrent")!) &&
            cards.Count == 2 && Settings.Get<int>("LfSkinSiteActive") == 0 && Settings.Get<ModLaunch.McLoginType>("LoginType") == ModLaunch.McLoginType.Ms,
            "Opening a saved site assigned a stored role or hid a role with the same display name.");
        VisualAssetChecks.Pump(() => files.All(File.Exists), "distinct role avatars are stored before teardown");
        foreach (FrameworkElement card in cards) {
            var content = (Grid)card.Tag;
            var body = (StackPanel)content.Children[0];
            var slot = (Border)body.Children[0];
            Native.Check(slot.Child is Image { Source: not null } && slot.BorderThickness == new Thickness(0),
                "The loaded role avatar retained the question-mark placeholder outline.");
        }

        // Side hosts the account page first and the skin library page after it.
        var side = (Grid)((Grid)Native.Property(workspaceType, "Side").GetValue(workspace)!).Children[0];
        var header = side.Children.OfType<StackPanel>().Single();
        var roleBack = RoleBackButton();
        Native.Check(header.Children[0] is TextBlock { Text: "游戏账号" } &&
            roleBack.HorizontalAlignment == HorizontalAlignment.Left && roleBack.VerticalAlignment == VerticalAlignment.Top,
            "The role chooser did not place its return button below the provider tabs.");

        var pendingProperty = Native.Property(Native.Type("PCL.GlassSkinSites"), "RoleSelectionPending");
        var pending = (bool)pendingProperty.GetValue(null)!;
        var appearance = ((string Name, ImageSource? Avatar))Native.Static("PCL.GlassShellRuntime", "ReadAccountAppearance", pending)!;
        Native.Check(pending && appearance.Name == "等待选择角色" && appearance.Avatar is null,
            "A role selection in progress displayed the previously active account in the rail.");
        view.SetState(new GlassLauncherViewState { AccountName = appearance.Name, AccountAvatar = appearance.Avatar });
        Native.Check(Part<TextBlock>(view, "AccountFallbackGlyph").Visibility == Visibility.Visible && Part<Image>(view, "AccountAvatarImage").Source is null,
            "The pending role did not show the question-mark avatar in the rail.");

        view.SetAccountWorkspace(side,
            (FrameworkElement)Native.Property(workspaceType, "Scene").GetValue(workspace)!);
        view.SetAccountWorkspaceOpen(true, immediate: true);
        view.SetAccountWorkspaceOpen(false, immediate: true);
        var launchCommands = Native.Get(LauncherApplicationServices.Launch, "commands")!;
        var originalStart = Native.Get(launchCommands, "Start");
        int dispatches = 0;
        Native.Set(launchCommands, "Start", new Func<ModLaunch.McLaunchOptions?, bool>(_ => { dispatches++; return false; }));
        try {
            AssertLaunchBlocked();
            roleBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Native.Check(Native.Get(workspace, "_sideMode")?.ToString() == "Servers" && Native.Get(workspace, "_choosing") is null,
                "The role chooser back button did not return to external login selection.");
            var servers = (Grid)Native.Get(workspace, "_stageCurrent")!;
            var serverHeading = (Grid)servers.Children[0];
            Native.Check(!serverHeading.Children.OfType<Button>().Any(), "The external login list still showed a heading back button.");
            pending = (bool)pendingProperty.GetValue(null)!;
            Native.Check(!pending && Settings.Get<ModLaunch.McLoginType>("LoginType") == ModLaunch.McLoginType.Ms,
                "Returning to the skin-site list left role selection pending or changed the previous login method.");
            var restored = ((string Name, ImageSource? Avatar))Native.Static("PCL.GlassShellRuntime", "ReadAccountAppearance", pending)!;
            Native.Check(restored.Name == "FixtureBUpdated" && restored.Avatar is not null, "Returning to the site list did not restore the previous account avatar.");
            ModLaunch.McLaunchStart();
            Native.Check(dispatches == 1, "Returning to the site list kept blocking launch with the previously selected account.");
            dispatches = 0;

            // Selecting the previously active site still requires an explicit role choice.
            Native.Cache("LfSkinSiteActive", 1);
            string savedSelection = new JObject { ["Server"] = server, ["Username"] = "Fixture", ["ProfileId"] = "profile-one" }
                .ToString(Newtonsoft.Json.Formatting.None);
            Native.Cache("LfSkinSiteSelection", savedSelection);
            Native.Call(workspaceType, "UseSite", workspace, sites[0]);
            Native.Check(Native.Get(workspace, "_choosing") is not null, "Reselecting the active site skipped the role chooser.");
            AssertLaunchBlocked();
            RoleBackButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Native.Check((bool)pendingProperty.GetValue(null)! == false && Native.Get(workspace, "_sideMode")?.ToString() == "Servers" &&
                Settings.Get<string>("LfSkinSiteSelection") == savedSelection && Settings.Get<int>("LfSkinSiteActive") == 1,
                "Canceling role selection changed the earlier active role.");
        } finally {
            Native.Cache("LfSkinSiteActive", 0);
            Native.Set(launchCommands, "Start", originalStart);
            view.SetAccountWorkspace(null!, null!);
        }

        void AssertLaunchBlocked() {
            Native.Check(!ModLaunch.McLaunchStart() && !ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { QuickPlayWorld = "fixture-world" }) && dispatches == 0,
                "A pending skin-site selection reached the game launch pipeline with the previous account.");
        }

        Button RoleBackButton() => ((Grid)Native.Get(workspace, "_stageCurrent")!).Children.OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "返回外置登录");
    }

    private static void QueuedSkinResults() {
        var original = new LegacySkinRequest("Player", "same-id", "https://first.invalid/api");
        var next = original with { Server = "https://second.invalid/api" };
        LegacySkinRequest input = original;
        var queue = new List<Action>();
        var shown = new List<(LegacySkinRequest Request, string Path)>();
        object task = new();
        var handler = new LegacyLaunchSkins<object>(new LegacyLaunchSkinHost<object> {
            Input = _ => input, Canceled = (_, _) => false,
            PublishAvatar = (_, request, file, _, canPublish) => {
                queue.Add(() => { if (canPublish()) shown.Add((request, file)); });
                return true;
            },
            ImagePath = () => "", SkinAddress = (_, request) => request.Server,
            DownloadSkin = address => address, Output = (_, _) => { }
        });
        handler.Official(task, "Auth");
        input = next;
        handler.Official(task, "Auth");
        foreach (Action callback in queue.AsEnumerable().Reverse()) callback();
        Native.Check(shown.SequenceEqual(new[] { (next, next.Server) }), "An old queued response for the same role ID at another site entered the active view.");
    }

    private static bool ContainsQuestionMark(DependencyObject root) {
        if (root is TextBlock { Text: "?", Visibility: Visibility.Visible }) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsQuestionMark(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    private static bool ContainsAvatar(DependencyObject root, ImageSource avatar) {
        if (root is Image image && ReferenceEquals(image.Source, avatar)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsAvatar(VisualTreeHelper.GetChild(root, i), avatar)) return true;
        return false;
    }

}
