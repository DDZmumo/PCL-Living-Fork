// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System.Net;
using System.Net.Http;
using PCL;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static class AccountRoleSwitchChecks {
    private const string Server = "https://role-switch.invalid/api";

    internal static void Run(AccountUiChecksScope scope) {
        Native.Cache("LfSkinSiteActive", 1);
        Native.Cache("LfSkinSiteSelection", $$"""{"Server":"{{Server}}","Username":"fixture","ProfileId":"one"}""");
        Native.Cache("LfSkinSites", $$"""
            [{"Server":"{{Server}}","Accounts":[{"Username":"fixture","AccessToken":"old-access","ClientToken":"client",
             "SelectedId":"one","Profiles":[{"Id":"one","Name":"First"},{"Id":"two","Name":"Second"}]}]}]
            """);
        Settings.Set("LoginType", ModLaunch.McLoginType.Auth);

        using var view = new GlassLauncherView();
        using var workspace = new GlassAccountWorkspace(view);
        using var nextView = new GlassLauncherView();
        using var nextWorkspace = new GlassAccountWorkspace(nextView);
        // Stop before the game loader while using the same account admission check as Home and Quick Play.
        var commands = Native.Get(LauncherApplicationServices.Launch, "commands")!;
        var start = Native.Get(commands, "Start");
        var launchedAccounts = new List<string?>();
        Native.Set(commands, "Start", new Func<ModLaunch.McLaunchOptions?, bool>(_ => {
            launchedAccounts.Add(LauncherApplicationServices.Accounts.ReadSnapshot().DisplayName);
            return false;
        }));

        var clients = new List<HttpClient>();
        var requests = new List<Task>();
        try {
            var reply = new DeferredReply("refresh");
            Task<bool> toSecond = Select(workspace, view, "two", reply);
            Native.Check(!GlassSkinSites.RoleSelectionPending, "The identity card path unexpectedly opened the role chooser.");
            AssertBlocked(0);
            Native.Check(!LauncherAccountSession.SelectPrimary(ModLaunch.McLoginType.Ms) && Active().Profile.Id == "one",
                "A competing account change replaced the active identity during verification.");
            reply.Respond(HttpStatusCode.OK, """{"accessToken":"second-access","selectedProfile":{"id":"two"}}""");
            Native.Check(Await(toSecond) && Active().Profile.Id == "two" && Active().Account.AccessToken == "second-access",
                "The accepted switch did not publish the new role and the bound session together.");
            ModLaunch.McLaunchStart();
            Native.Check(launchedAccounts.SequenceEqual(new[] { "Second" }), "Launch did not resume with the verified role.");

            var beforeFailure = scope.Store.Snapshot();
            var failed = new DeferredReply("refresh");
            Task<bool> rejected = Select(workspace, view, "one", failed);
            failed.Respond(HttpStatusCode.Forbidden, """{"errorMessage":"fixture rejection"}""");
            try {
                Await(rejected);
                throw new InvalidOperationException("A rejected role binding was accepted.");
            } catch (SkinSiteException error) when (error.Message == "fixture rejection") { }
            Native.Check(scope.Store.EqualsSnapshot(beforeFailure) && !LauncherApplicationServices.IsLaunchAccountPending,
                "A rejected binding changed the previous selection or kept its reservation.");

            var abandoned = new DeferredReply("refresh");
            Task<bool> fromClosedWindow = Select(workspace, view, "one", abandoned);
            workspace.Dispose();
            Native.Check(LauncherAccountSession.SelectPrimary(ModLaunch.McLoginType.Ms) && GlassSkinSites.CurrentForLaunch() is null,
                "Closing the account workspace kept the old switch reservation or prevented the next account change.");
            var next = new DeferredReply("validate");
            Task<bool> nextSwitch = Select(nextWorkspace, nextView, "two", next);
            var beforeLateReply = scope.Store.Snapshot();
            abandoned.Respond(HttpStatusCode.OK, """{"accessToken":"late-access","selectedProfile":{"id":"one"}}""");
            Native.Check(!Await(fromClosedWindow) && scope.Store.EqualsSnapshot(beforeLateReply) &&
                LauncherAccountSession.Current == ModLaunch.McLoginType.Ms && GlassSkinSites.CurrentForLaunch() is null,
                "An abandoned result wrote its session or activated a role after the account changed.");
            AssertBlocked(1);
            next.Respond(HttpStatusCode.NoContent);
            Native.Check(Await(nextSwitch) && LauncherAccountSession.Current == ModLaunch.McLoginType.Auth && Active().Profile.Id == "two" &&
                Active().Account.AccessToken == "second-access" && !LauncherApplicationServices.IsLaunchAccountPending,
                "Finishing the stale switch displaced the newer reservation or identity.");

            // Launch authentication may refresh the saved session while keeping the same active character.
            var selected = GlassSkinSites.Load();
            selected[0].Accounts[0].SelectedId = "one";
            GlassSkinSites.Save(selected);
            int beforeRefresh = scope.Store.Writes.Count;
            var refresh = new DeferredReply("refresh");
            var login = GlassSkinSites.LoginForLaunch(Active(), () => false, Connect(refresh));
            requests.Add(login);
            refresh.Respond(HttpStatusCode.OK, """{"accessToken":"launch-access","selectedProfile":{"id":"two"}}""");
            Native.Check(Await(login).AccessToken == "launch-access" && Active().Account.AccessToken == "launch-access" &&
                Active().Profile.Id == "two" && scope.Store.Writes.Skip(beforeRefresh).SequenceEqual(new[] { "LfSkinSites" }),
                "Launch authentication did not limit its write to the current role's saved session.");
            Console.WriteLine("PASS external role switching: shared launch admission, success, failure, stale completion after disposal, and launch-only cache refresh.");
        } finally {
            workspace.Dispose();
            nextWorkspace.Dispose();
            foreach (HttpClient client in clients) client.Dispose();
            VisualAssetChecks.Pump(() => requests.All(task => task.IsCompleted), "External role requests did not drain before fixture restoration.");
            Native.Set(commands, "Start", start);
        }

        Task<bool> Select(GlassAccountWorkspace owner, GlassLauncherView ownerView, string id, DeferredReply response) {
            var site = GlassSkinSites.Load().Single();
            var role = GlassSkinSites.Characters(site).Single(character => character.Profile.Id == id);
            var task = LauncherAccountSession.SelectExternalCharacterAsync(owner, role, ownerView.Dispatcher, () => ownerView.IsClosing, Connect(response));
            requests.Add(task);
            Native.Check(response.Started, "The role selection did not reach verification.");
            return task;
        }
        HttpClient Connect(DeferredReply reply) {
            var client = new HttpClient(reply);
            clients.Add(client);
            return client;
        }
        void AssertBlocked(int dispatched) => Native.Check(LauncherApplicationServices.IsLaunchAccountPending &&
            !ModLaunch.McLaunchStart() && !ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { QuickPlayWorld = "fixture-world" }) &&
            launchedAccounts.Count == dispatched, "A pending role switch reached the game launch pipeline.");
    }

    private static SkinSiteCharacter Active() => GlassSkinSites.CurrentForLaunch() ?? throw new InvalidOperationException("Missing active fixture role.");

    private static T Await<T>(Task<T> task) {
        VisualAssetChecks.Pump(() => task.IsCompleted, "The fixture role verification did not complete.");
        return task.GetAwaiter().GetResult();
    }

    private sealed class DeferredReply(string operation) : HttpMessageHandler {
        private readonly TaskCompletionSource<HttpResponseMessage> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Started { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Native.Check(!Started && request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == Server + "/authserver/" + operation,
                "Unexpected external verification endpoint.");
            Started = true;
            return result.Task.WaitAsync(cancellationToken);
        }
        internal void Respond(HttpStatusCode status, string json = "{}") => result.SetResult(new(status) { Content = new StringContent(json) });
    }
}
