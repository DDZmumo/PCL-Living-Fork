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

    internal static void Run(FixtureEnvironment environment, bool glassPortal = false)
    {
        VisualAssetChecks.VerifyChild(environment);
        Native.Check(OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && System.Windows.Application.Current.Dispatcher.CheckAccess(), "Account UI requires the real isolated Windows STA child.");
        if (glassPortal) Native.Cache("UiShellStyle", 1);
        using AccountUiChecksScope scope = new(environment);
        using GlassAccountPanel? projectedAccount = glassPortal ? new GlassAccountPanel(scope.Launch) : null;
        try {
        scope.At(AccountUiChecksScope.Checkpoint.Navigation);
        PageLaunchLeft launch = scope.Launch;
        if (glassPortal) {
            Panel providerPanel = Part<Panel>(launch, "PanType");
            Native.Check(projectedAccount!.Children.Count == 3 && !projectedAccount.Children.Contains(providerPanel) &&
                Part<FrameworkElement>(launch, "PanLogin").Parent is Viewbox &&
                ReferenceEquals(Part<MyRadioButton>(launch, "RadioLoginType5").Parent, providerPanel) &&
                ReferenceEquals(Part<MyRadioButton>(launch, "RadioLoginType0").Parent, providerPanel),
                "New account elements must not reparent or display the original provider controls.");
        }
        scope.SetCache("LoginPageType", 1);
        launch.RefreshPage(false, false);
        PageLoginMs page = scope.Current<PageLoginMs>();
        scope.VerifyController(page);
        MyComboBox accounts = Part<MyComboBox>(page, "ComboAccounts");
        MyButton login = Part<MyButton>(page, "BtnLogin");
        void SelectAccount(int index) {
            if (projectedAccount is null) accounts.SelectedIndex = index;
            else { projectedAccount.Refresh(); NewPart<ComboBox>(projectedAccount, "ComboAccounts").SelectedIndex = index; }
        }
        void Activate(FrameworkElement source) {
            if (projectedAccount is null) {
                if (source is MyButton button) Click(button);
                else Click((MyIconButton)source);
            } else {
                projectedAccount.Refresh();
                Button button = NewPart<Button>(projectedAccount, source.Name);
                Native.Check(button.IsEnabled && !ReferenceEquals(button, source), "Projected account action is disabled or reused its old visual.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        accounts.ApplyTemplate();
        Native.Check(accounts.ContentPresenter is not null, "The genuine Microsoft account BAML template did not resolve its presenter.");
        Native.Check(accounts.Items.Count == 3 && accounts.SelectedIndex == 0 && accounts.Items[0] is MyComboBoxItem, "Actual account selector lost add-new plus the two preserved fixture accounts.");
        Native.Check(PageLoginMs.GetLoginData().OAuthRefreshToken == "" && page.IsVaild() == "请在登录账号后再启动游戏！", "Add-new input must remain unauthenticated before starting actual login.");
        scope.At(AccountUiChecksScope.Checkpoint.AccountSelection);
        SelectAccount(1);
        ModLaunch.McLoginMs first = PageLoginMs.GetLoginData();
        Native.Check(first.UserName == "FixtureA" && first.OAuthRefreshToken == "fixture-refresh-a" && Equals(accounts.ContentPresenter!.Content, "FixtureA"), "First real account selection did not reach the original DTO/presenter.");
        SelectAccount(2);
        ModLaunch.McLoginMs second = PageLoginMs.GetLoginData();
        Native.Check(second.UserName == "FixtureB" && second.OAuthRefreshToken == "fixture-refresh-b" && page.IsVaild() == "", "Switching accounts did not replace the real login input.");
        page.Reload(true);
        Native.Check(accounts.SelectedIndex == 2 && PageLoginMs.GetLoginData().UserName == "FixtureB", "Reload(KeepInput) lost the selected account.");

        scope.BeginScenario(AccountUiChecksScope.Scenario.Success);
        Activate(login);
        Native.Check(!accounts.IsEnabled && !login.IsEnabled && login.Text == "0%", "Actual login event did not disable both controls before its producer starts.");
        scope.AwaitRequest();
        Native.Check(scope.Loader.State == ModBase.LoadState.Loading && scope.Loader.Input.UserName == "FixtureB" && scope.Loader.Input.OAuthRefreshToken == "fixture-refresh-b", "Actual Main loader did not receive the selected account.");
        scope.ReleaseRequest();
        scope.AwaitCompletion(ModBase.LoadState.Finished, () => accounts.IsEnabled && login.IsEnabled && login.Text == "登录" && scope.IsCurrent<PageLoginMsSkin>());
        Native.Check(scope.RequestCount == 6 && scope.Refreshes == 1 && scope.Hints.Count == 0, "Synthetic six-stage login did not complete exactly once through the real account host.");
        scope.AssertPurchaseHintSaved();
        scope.At(AccountUiChecksScope.Checkpoint.SessionChecks);
        PageLoginMsSkin skin = scope.Current<PageLoginMsSkin>();
        Native.Check(Part<TextBlock>(skin, "TextName").Text == "FixtureBUpdated", "Actual logged-in page did not display the atomically saved profile.");
        ModLaunch.McLoginMs saved = PageLoginMsSkin.GetLoginData();
        Native.Check(saved.UserName == "FixtureBUpdated" && saved.OAuthRefreshToken == "fixture-refresh-new" && saved.AccessToken == "fixture-minecraft-access" && saved.Uuid == AccountUiChecksScope.ProfileId && JToken.DeepEquals(JObject.Parse(saved.ProfileJson), JObject.Parse(AccountUiChecksScope.Profile)), "Actual logged-in DTO lost the complete saved session/profile, including unknown profile fields.");
        JObject history = JObject.Parse(Settings.Get<string>("LoginMsJson"));
        Native.Check(history.Count == 2 && (string?)history["FixtureA"] == "fixture-refresh-a" && history["FixtureB"] is null && (string?)history["FixtureBUpdated"] == "fixture-refresh-new", "Actual session commit did not preserve the other account and rename only the selected account.");
        Native.Check(Settings.Get<long>("CacheMsV2Expires") > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && Settings.Get<bool>("CacheMsV2Migrated") && Settings.Get<ModLaunch.McLoginType>("LoginType") == ModLaunch.McLoginType.Ms, "Actual account commit lost Int64/bool/enum setting types.");
        string[] sessionKeys = scope.Store.Transactions.Skip(scope.SeedTransactionCount).Single(transaction => transaction.Contains("LoginMsJson_LF", StringComparer.Ordinal));
        var applicationAccount = LauncherApplicationServices.Accounts.ReadSnapshot();
        string accountMetadata = Newtonsoft.Json.JsonConvert.SerializeObject(applicationAccount);
        Native.Check(applicationAccount.Provider == PCLCS.Applications.AccountProvider.Microsoft && applicationAccount.Authenticated &&
            applicationAccount.DisplayName == saved.UserName && applicationAccount.Task.Id == scope.Loader.Uuid &&
            !accountMetadata.Contains(saved.AccessToken, StringComparison.Ordinal) &&
            !accountMetadata.Contains(saved.OAuthRefreshToken, StringComparison.Ordinal), "Application account metadata diverged from the actual session or exposed credentials.");
        Native.Check(sessionKeys.Length == 8 && sessionKeys[^1] == "CacheMsV2Migrated_LF" && scope.Store.GetValue("CacheMsV2OAuthRefresh_LF") is string cipher && cipher != "fixture-refresh-new" && !cipher.Contains("fixture-refresh-new", StringComparison.Ordinal), "Actual Microsoft session transaction lost marker-last LF mapping or stored its token as plaintext.");
        if (glassPortal) CheckAvatars(environment, launch, Part<MySkin>(skin, "Skin"), saved.UserName, saved.Uuid);

        scope.At(AccountUiChecksScope.Checkpoint.Logout);
        Activate(Part<MyIconButton>(skin, "BtnExit"));
        scope.AwaitUi(() => scope.IsCurrent<PageLoginMs>() && scope.Refreshes == 2);
        scope.At(AccountUiChecksScope.Checkpoint.PostLogout);
        Native.Check(!LauncherApplicationServices.Accounts.ReadSnapshot().Authenticated, "Application account metadata retained a logged-out session.");
        Native.Check(ReferenceEquals(page, scope.Current<PageLoginMs>()), "Logout/navigation replaced the existing actual account page rather than using Main's page cache.");
        Native.Check(Settings.Get<string>("CacheMsV2OAuthRefresh") == "" && Settings.Get<string>("CacheMsV2Access") == "" && Settings.Get<string>("CacheMsV2Name") == "" && Settings.Get<string>("CacheMsV2Uuid") == "" && Settings.Get<string>("CacheMsV2ProfileJson") == "" && Settings.Get<long>("CacheMsV2Expires") == 0L && Settings.Get<bool>("CacheMsV2Migrated"), "Actual logout did not clear all active session values with the original types.");
        Native.Check(JToken.DeepEquals(history, JObject.Parse(Settings.Get<string>("LoginMsJson"))) && accounts.SelectedIndex == 0 && PageLoginMs.GetLoginData().OAuthRefreshToken == "", "Logout deleted saved accounts or retained an authenticated selected input.");

        foreach (AccountUiChecksScope.Scenario scenario in new[] { AccountUiChecksScope.Scenario.Failure, AccountUiChecksScope.Scenario.Cancel })
        {
            SelectAccount(1);
            Native.Check(PageLoginMs.GetLoginData().UserName == "FixtureA", "Failure/cancel must exercise a real saved-account input.");
            var before = scope.Store.Snapshot();
            int refreshes = scope.Refreshes, hints = scope.Hints.Count;
            scope.BeginScenario(scenario);
            Activate(login);
            scope.AwaitRequest();
            Native.Check(!accounts.IsEnabled && !login.IsEnabled && scope.Loader.State == ModBase.LoadState.Loading, "Actual in-flight login controls/state were not observable.");
            if (scenario == AccountUiChecksScope.Scenario.Cancel) scope.Loader.Cancel();
            else scope.ReleaseRequest();
            scope.AwaitCompletion(scenario == AccountUiChecksScope.Scenario.Cancel ? ModBase.LoadState.Canceled : ModBase.LoadState.Failed, () => accounts.IsEnabled && login.IsEnabled && login.Text == "登录");
            Native.Check(scope.RequestCount == 1 && scope.Refreshes == refreshes && ReferenceEquals(page, scope.Current<PageLoginMs>()), "Rejected/canceled actual login advanced navigation or sent extra requests.");
            Native.Check(scope.Store.EqualsSnapshot(before) && JToken.DeepEquals(history, JObject.Parse(Settings.Get<string>("LoginMsJson"))) && Settings.Get<long>("CacheMsV2Expires") == 0L, "Rejected/canceled login changed the previously committed account store.");
            if (scenario == AccountUiChecksScope.Scenario.Failure)
                Native.Check(scope.Loader.Error?.Message == "$native account fixture denied" && scope.Hints.Count == hints + 1 && scope.Hints[^1] == ("native account fixture denied", PCLLF.Presentation.Accounts.LegacyLoginHint.Red), "Actual failure did not retain loader error and publish the original red UI hint.");
            else Native.Check(scope.Hints.Count == hints, "Actual cancellation incorrectly published a login failure hint.");
        }
        scope.AssertPurchaseHintSaved();
        scope.At(AccountUiChecksScope.Checkpoint.FinalChecks);
        scope.AssertIdle();
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS account-ui: actual Main BAML navigation/account selection/click -> genuine Microsoft loader with six bounded synthetic HTTP responses -> genuine LF encrypted atomic account store -> logged-in page/logout; actual error and cancel preserve history and restore UI. No real account, authorization, entitlement, external HTTP, skin upload, or product cold-entry completion claimed.");
        } catch (Exception error) { scope.ReportPrimary(error); throw; }
    }

    private static void CheckAvatars(FixtureEnvironment environment, PageLaunchLeft launch, MySkin skin, string name, string uuid) {
        var oldAvatar = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
        oldAvatar.Freeze();
        var view = new GlassLauncherView();
        view.SetState(new GlassLauncherViewState { AccountName = "FixtureA", AccountAvatar = oldAvatar });
        Type workspaceType = Native.Type("PCL.GlassAccountWorkspace");
        var constructor = workspaceType.GetConstructor(Native.Flags, null, [typeof(PageLaunchLeft), typeof(GlassLauncherView)], null)!;
        using var workspace = (IDisposable)constructor.Invoke([launch, view]);
        Native.Call(workspaceType, "Open", workspace);
        var cards = (List<FrameworkElement>)Native.Get(workspace, "_cards")!;
        var card = cards.Single(item => AutomationProperties.GetName(item) == name);
        Native.Check(!ContainsAvatar(card, oldAvatar), "The new Microsoft account card adopted the preceding account's rail avatar while its own skin was loading.");
        Native.Check(ContainsQuestionMark(card), "A current card awaiting its avatar did not show the question mark.");
        var request = new LegacySkinRequest(name, uuid);
        var preceding = new LegacySkinRequest("FixtureA", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var previousInput = skin.Loader.Input;
        string skinPath = Path.Combine(environment.Root, "account-preview-skin.png");
        BitmapSource pixels = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(new byte[] { 24, 190, 44, 255 }, 64 * 64).SelectMany(pixel => pixel).ToArray(), 64 * 4);
        using (var output = File.Create(skinPath)) {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            encoder.Save(output);
        }
        var load = typeof(MySkin).GetMethod("Load", Native.Flags, null, [typeof(string), typeof(LegacySkinRequest), typeof(string), typeof(bool)], null)!;
        try {
            skin.Loader.Input = request;
            load.Invoke(skin, ["Ms", preceding, skinPath, true]);
            Native.Call(workspaceType, "Refresh", workspace);
            Native.Check(ContainsQuestionMark(card), "A delayed skin for the preceding account reached the current card.");
            load.Invoke(skin, ["Ms", request, skinPath, true]);
            var confirmed = (BitmapSource)Native.Static("PCL.LaunchPageRuntime", "Avatar", "Ms", request)!;
            Native.Call(workspaceType, "Refresh", workspace);
            Native.Check(cards.Contains(card) && ContainsAvatar(card, confirmed), "The existing current card did not update when its own avatar arrived.");
            load.Invoke(skin, ["Ms", request, skinPath, false]);
            Native.Call(workspaceType, "Refresh", workspace);
            Native.Check(ContainsAvatar(card, confirmed), "The download fallback replaced the current account's last confirmed avatar.");

            string stored = (string)Native.Static("PCL.GlassSkinHead", "HeadPath", "Ms", request)!;
            VisualAssetChecks.Pump(() => File.Exists(stored), "last confirmed account head is persisted");
            var cache = Native.Field(Native.Type("PCL.GlassSkinHead"), "LastGoodHeads").GetValue(null)!;
            Native.Call(cache.GetType(), "Clear", cache);
            var restored = Native.Static("PCL.LaunchPageRuntime", "Avatar", "Ms", request) as BitmapSource;
            Native.Check(restored is not null && !ReferenceEquals(restored, confirmed) && Pixel(restored).SequenceEqual(Pixel(confirmed)),
                "The last confirmed avatar did not survive a fresh in-memory cache.");
            view.SetState(new GlassLauncherViewState());
            Native.Check(Part<Image>(view, "AccountAvatarImage").Source is null && Part<TextBlock>(view, "AccountFallbackGlyph").Text == "?",
                "The signed-out rail displayed a Minecraft avatar.");
            SavedSiteWaitsForChoice(view, workspace, workspaceType, confirmed);
        } finally {
            skin.Loader.Input = previousInput;
            skin.Clear();
        }
        QueuedSkinResults();

        static byte[] Pixel(BitmapSource source) {
            var bytes = new byte[4];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(new Int32Rect(70, 70, 1, 1), bytes, 4, 0);
            return bytes;
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
            Dispatch = action => queue.Add(action),
            ClearSkin = _ => {}, PageExists = _ => true,
            RenderSkin = (_, request, file, _) => shown.Add((request, file)),
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

    private static void Click(MyButton button) => Native.Call(typeof(MyButton), "OnClick", button, button, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
    private static void Click(MyIconButton button) => Native.Call(typeof(MyIconButton), "OnClick", button, button, EventArgs.Empty);
    private static T NewPart<T>(DependencyObject root, string name) where T : FrameworkElement {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0) {
            var current = pending.Pop(); if (!seen.Add(current)) continue;
            if (current is T element && element.Name == name) return element;
            foreach (object child in LogicalTreeHelper.GetChildren(current)) if (child is DependencyObject node) pending.Push(node);
            if (current is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
        throw new InvalidOperationException("New account presentation is missing " + typeof(T).Name + ": " + name);
    }
}
