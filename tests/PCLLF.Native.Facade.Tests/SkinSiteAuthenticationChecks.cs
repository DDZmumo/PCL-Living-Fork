using System.Net;
using System.Net.Http;
using PCL;
using PCLLF.Presentation.Accounts;

namespace PCLLF.Native.Facade.Tests;

internal static class SkinSiteAuthenticationChecks {
    private const string SavedSite = "https://skins-a.invalid/api/yggdrasil";
    private const string OtherSite = "https://skins-b.invalid/api/yggdrasil";

    internal static void Run(FixtureEnvironment environment) {
        LegacyCredentials();
        GlobalSelection(environment);
        HttpsTransport();
        environment.AssertNoForbiddenIo();
    }

    private static void LegacyCredentials() {
        string instanceServer = OtherSite;
        var cache = new Dictionary<string, object?> {
            ["CacheAuthServerServer"] = SavedSite,
            ["CacheAuthCredentialServer"] = SavedSite + "/authserver",
            ["CacheAuthUsername"] = "fixture-user",
            ["CacheAuthPass"] = "fixture-password",
        };
        var host = new LegacyLoginHost<LegacyLoginInput, LegacyLoginInput, LegacyLoginInput> {
            AccountSnapshot = _ => cache,
            HasSelectedInstance = () => true,
            ReadInstanceString = _ => instanceServer,
            CreateServer = input => input
        };
        var page = new LegacyLoggedInPage<LegacyLoginInput, LegacyLoginInput, LegacyLoginInput>(host, new(), "Auth");
        var other = page.GetServerLoginData();
        Native.Check(other.BaseUrl == OtherSite + "/authserver" && string.IsNullOrEmpty(other.UserName) && string.IsNullOrEmpty(other.Password),
            "Dedicated instance login inherited the saved credentials of another skin site.");
        instanceServer = SavedSite + "/";
        var same = page.GetServerLoginData();
        Native.Check(same.BaseUrl == SavedSite + "/authserver" && same.UserName == "fixture-user" && same.Password == "fixture-password",
            "An account already bound to the instance's own endpoint lost its credentials.");
        cache.Remove("CacheAuthCredentialServer");
        Native.Check(string.IsNullOrEmpty(page.GetServerLoginData().Password), "Unattributed legacy credentials were reused.");
    }

    private static void GlobalSelection(FixtureEnvironment environment) {
        const string identity = "00000000000000000000000000000001";
        var settings = new Dictionary<string, object> {
            ["LfSkinSiteActive"] = 1,
            ["LfSkinSiteSelection"] = $$"""{"Server":"{{SavedSite}}","Username":"site-user","ProfileId":"{{identity}}"}""",
            ["LfSkinSites"] = $$"""[{"Server":"{{SavedSite}}","Accounts":[{"Username":"site-user","AccessToken":"site-token","SelectedId":"{{identity}}","Profiles":[{"Id":"{{identity}}","Name":"Site Player"}]}]}]""",
            ["CacheAuthServerServer"] = OtherSite,
            ["CacheAuthAccess"] = "instance-token",
            ["LoginPageType"] = 0
        };
        var previous = settings.Keys.ToDictionary(key => key, key => Settings.Entries[key].ValueCache.GetValueOrDefault(""));
        var selection = Native.State(typeof(ModMinecraft), "_McInstanceSelected");
        object? previousInstance = selection.GetValue(null);
        string instancePath = Path.Combine(environment.Root, "data", "skin-site-game", "versions", "skin-site-instance");
        Directory.CreateDirectory(instancePath);
        var instance = new ModMinecraft.McInstance(instancePath);
        var mode = Settings.Entries["VersionServerLogin"].ValueCache;
        try {
            foreach (var (key, value) in settings) Native.Cache(key, value);
            selection.SetValue(null, null);
            var login = (ModLaunch.McLoginServer)Native.Call(typeof(ModLaunch), "ReadLoginInput", null, 3, true)!;
            Native.Check(login.BaseUrl == SavedSite + "/authserver" && login.UserName == "site-user" && login.Password is null,
                "The global skin-site launch input picked up the dedicated instance's endpoint or exposed a shared password.");
            Settings.Set("VersionServerLogin", 4, instance);
            Native.Check(Native.Static("PCL.GlassSkinSites", "CurrentForLaunch", instance) is null, "A dedicated instance inherited global skin-site authentication.");
            Settings.Set("VersionServerLogin", 0, instance);
            Native.Check(Native.Static("PCL.GlassSkinSites", "CurrentForLaunch", instance) is not null, "Returning to a free-choice instance lost the selected global skin-site account.");
        } finally {
            mode.TryRemove(instance.PathVersion, out _);
            selection.SetValue(null, previousInstance);
            foreach (var (key, value) in previous) {
                if (value is null) Settings.Entries[key].ValueCache.TryRemove("", out _);
                else Native.Cache(key, value);
            }
        }
    }

    private static void HttpsTransport() {
        const string insecure = "http://skins-a.invalid/api/yggdrasil";
        using var untouched = new HttpClient(new ResponseHandler(_ => throw new InvalidOperationException("HTTP credentials reached the transport.")));
        Rejected((Task)Native.Static("PCL.GlassSkinSites", "Authenticate", insecure, "mock", "mock-password", untouched)!, "HTTPS");
        Rejected((Task)Native.Static("PCL.GlassSkinSites", "Resolve", insecure, untouched)!, "HTTPS");

        int requests = 0;
        using var discovery = new HttpClient(new ResponseHandler(request => {
            Native.Check(++requests == 1 && request.Method == HttpMethod.Get && request.Content is null, "Discovery sent credentials or followed an insecure API endpoint.");
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("X-Authlib-Injector-API-Location", insecure);
            return response;
        }));
        Rejected((Task)Native.Static("PCL.GlassSkinSites", "Resolve", SavedSite, discovery)!, "HTTPS");
        Native.Check(requests == 1, "Insecure API discovery was not rejected immediately.");

        requests = 0;
        using var redirect = new HttpClient(new ResponseHandler(request => {
            Native.Check(++requests == 1 && request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == SavedSite + "/authserver/authenticate",
                "The login request escaped its resolved HTTPS endpoint.");
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri(OtherSite);
            return response;
        }));
        Rejected((Task)Native.Static("PCL.GlassSkinSites", "Authenticate", SavedSite, "mock", "mock-password", redirect)!, "重定向");
        Native.Check(requests == 1, "A credential POST was repeated after the site redirected it.");
    }

    private static void Rejected(Task task, string expected) {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception error) when (error.GetType().Name == "SkinSiteException" && error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("The skin-site request did not fail closed with the expected reason.");
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
