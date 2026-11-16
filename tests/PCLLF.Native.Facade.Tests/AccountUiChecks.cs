using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;

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
