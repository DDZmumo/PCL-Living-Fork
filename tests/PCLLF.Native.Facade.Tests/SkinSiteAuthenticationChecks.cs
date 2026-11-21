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
        DelegatedCredentialConsent();
        BoundedHeadDecode();
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
            ["LoginPageType"] = 4
        };
        var previous = settings.Keys.ToDictionary(key => key, key => Settings.Entries[key].ValueCache.GetValueOrDefault(""));
        var selection = Native.State(typeof(ModMinecraft), "_McInstanceSelected");
        object? previousInstance = selection.GetValue(null);
        string instancePath = Path.Combine(environment.Root, "data", "skin-site-game", "versions", "skin-site-instance");
        Directory.CreateDirectory(instancePath);
        var instance = new ModMinecraft.McInstance(instancePath);
        var mode = Settings.Entries["VersionServerLogin"].ValueCache;
        var server = Settings.Entries["VersionServerAuthServer"].ValueCache;
        try {
            foreach (var (key, value) in settings) Native.Cache(key, value);
            selection.SetValue(null, instance);
            mode[instance.PathVersion] = 4;
            server[instance.PathVersion] = OtherSite;
            Native.Check(Settings.Get<int>("VersionServerLogin", instance) == 0 && Settings.Get<int>("LoginPageType") == 0,
                "A saved version login requirement was still effective.");
            var login = (ModLaunch.McLoginServer)Native.Call(typeof(ModLaunch), "ReadLoginInput", null, 3, true)!;
            Native.Check(login.BaseUrl == SavedSite + "/authserver" && login.UserName == "site-user" && login.Password is null,
                "The global skin-site launch input picked up the dedicated instance's endpoint or exposed a shared password.");
            Native.Check(Native.Static("PCL.GlassSkinSites", "CurrentForLaunch") is not null, "An old instance requirement hid the selected global skin-site account.");
            Native.Cache("LfSkinSiteActive", 0);
            var unselected = (ModLaunch.McLoginServer)Native.Call(typeof(ModLaunch), "ReadLoginInput", null, 3, true)!;
            Native.Check(Native.Get(unselected, "SkinSite") is null && string.IsNullOrEmpty(unselected.BaseUrl) && string.IsNullOrEmpty(unselected.Password),
                "A missing global role fell back to a previously configured instance server.");
        } finally {
            mode.TryRemove(instance.PathVersion, out _);
            server.TryRemove(instance.PathVersion, out _);
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

    private static void DelegatedCredentialConsent()
    {
        foreach ((bool confirm, bool remember) in new[] { (false, false), (true, false), (true, true) })
        {
            int confirmations = 0, posts = 0;
            using var client = new HttpClient(new ResponseHandler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    Native.Check(confirm && confirmations == 1 && request.RequestUri?.AbsoluteUri == OtherSite + "/authserver/authenticate",
                        "A login POST was sent before approval or to the wrong receiver.");
                    posts++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"accessToken":"synthetic-access","clientToken":"synthetic-client","availableProfiles":[{"id":"1","name":"Tester"}]}""")
                    };
                }
                Native.Check(request.Content is null, "Discovery must not send credentials.");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"meta":{}}""") };
                if (request.RequestUri?.AbsoluteUri == SavedSite) response.Headers.Add("X-Authlib-Injector-API-Location", OtherSite);
                return response;
            }));
            var task = (Task)Native.Static("PCL.GlassSkinSites", "SignIn", SavedSite, "mock", "fixture-password", remember,
                new Func<Uri, Uri, bool>((from, to) =>
                {
                    confirmations++;
                    Native.Check(from.AbsoluteUri == SavedSite && to.AbsoluteUri == OtherSite, "Consent did not include both endpoints.");
                    return confirm;
                }), client)!;
            VisualAssetChecks.Pump(() => task.IsCompleted, "Skin site consent did not complete.");
            task.GetAwaiter().GetResult();
            object? result = task.GetType().GetProperty("Result")!.GetValue(task);
            Native.Check(confirmations == 1 && posts == (confirm ? 1 : 0) && (result is not null) == confirm, "Declining delegated login did not prevent the credential request.");
            if (result is not null)
            {
                object account = result.GetType().GetField("Item4")!.GetValue(result)!;
                string password = (string)account.GetType().GetProperty("Password")!.GetValue(account)!;
                Native.Check(password == (remember ? "fixture-password" : ""), "Password retention did not respect the sign-in choice.");
            }
        }
    }

    private static void BoundedHeadDecode()
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray(), 64 * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] png = stream.ToArray();
        object? Decode(byte[] data) => Task.Run(() => Native.Static("PCL.GlassSkinSites", "DecodeHead", (object)data)).GetAwaiter().GetResult();
        Native.Check(Decode(png) is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true }, "A valid external head could not be decoded off the UI thread.");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), 8192);
        Native.Check(Decode(png) is null, "An oversized external texture reached the bitmap decoder.");
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
