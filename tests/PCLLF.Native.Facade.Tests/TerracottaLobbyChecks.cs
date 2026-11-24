// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class TerracottaLobbyChecks
{
    private const string RoomCode = "U/YNZE-U61D-2206-HXRG";

    internal static void Run(FixtureEnvironment environment)
    {
        Type type = Native.Type("PCL.TerracottaConnection");
        using var fake = new ApiFixture();
        using var http = new HttpClient(fake) { BaseAddress = new Uri("http://127.0.0.1:23456/") };
        using var connection = (IDisposable)type.GetConstructor(Native.Flags, null, [typeof(HttpClient)], null)!.Invoke([http]);
        const string player = "测试 A&B";
        Complete(Native.Call(type, "JoinAsync", connection, RoomCode, player, CancellationToken.None));
        object state = Result(Native.Call(type, "ReadAsync", connection, CancellationToken.None));
        Native.Check(State(state) == "host-ok" && Native.Property(state.GetType(), "Code").GetValue(state) as string == RoomCode,
            "The upstream room identifier was not preserved in lobby state.");
        Complete(Native.Call(type, "CreateAsync", connection, player, CancellationToken.None));
        Complete(Native.Call(type, "LeaveAsync", connection, CancellationToken.None));
        Native.Check(fake.Routes.SequenceEqual(new[] {
            "/state/guesting?room=" + Uri.EscapeDataString(RoomCode) + "&player=" + Uri.EscapeDataString(player),
            "/state", "/state/scanning?player=" + Uri.EscapeDataString(player), "/state/ide"
        }), "The launcher altered the upstream join code or protocol routes.");
        fake.RejectJoin = true;
        try {
            Complete(Native.Call(type, "JoinAsync", connection, "invalid", null, CancellationToken.None));
            throw new InvalidOperationException("The upstream rejection did not reach the launcher.");
        } catch (InvalidDataException) { }

        CheckUpdateCatalogue();
        CheckReleaseReceipt(Path.Combine(environment.Data, "terracotta-receipt"));
        string? components = Environment.GetEnvironmentVariable("PCLLF_TERRACOTTA_TEST_COMPONENTS");
        if (!string.IsNullOrWhiteSpace(components)) CheckUpstream(type, environment.Data, components);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS Terracotta lobby IPC contract: standard U/ room codes, escaped names, state mapping, host and exit commands; " +
            "update catalogue accepts only digested project assets and a tampered release receipt falls back to the bundled release.");
    }

    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string ReleaseJson(string tag = "v0.5.0", string? digest = Digest, string? url = null, long size = 9000000, string? name = null)
    {
        name ??= $"terracotta-{tag.TrimStart('v')}-windows-x86_64-pkg.tar.gz";
        url ??= $"https://github.com/burningtnt/Terracotta/releases/download/{tag}/{name}";
        var asset = new Dictionary<string, object?> { ["name"] = name, ["size"] = size, ["browser_download_url"] = url };
        if (digest is not null) asset["digest"] = digest;
        return JsonSerializer.Serialize(new Dictionary<string, object?> {
            ["tag_name"] = tag, ["published_at"] = "2026-10-01T08:00:00Z", ["body"] = "## 更新\n- 修复连接",
            ["html_url"] = "https://github.com/burningtnt/Terracotta/releases/tag/" + tag, ["assets"] = new[] { asset }
        });
    }

    private static object Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Native.Call(Native.Type("PCL.TerracottaUpdates"), "Parse", null, document.RootElement, "x86_64")!;
    }

    private static object? Get(object value, string name) => Native.Property(value.GetType(), name).GetValue(value);

    private static void CheckUpdateCatalogue()
    {
        object accepted = Parse(ReleaseJson());
        object? package = Get(accepted, "Package");
        Native.Check(package is not null && Get(package, "Version") as string == "0.5.0" &&
            Get(package, "ArchiveHash") as string == Digest[7..].ToUpperInvariant() && (long)Get(package, "Size")! == 9000000 &&
            Get(package, "ExecutableHash") is null && (bool)Native.Call(accepted.GetType(), "IsNewerThan", accepted, "0.4.2")!,
            "A digested upstream release for this machine was not offered for installation.");
        foreach ((string json, string reason) in new[] {
            (ReleaseJson(digest: null), "missing digest"),
            (ReleaseJson(digest: "sha1:0123"), "non-SHA-256 digest"),
            (ReleaseJson(url: "https://example.com/terracotta-0.5.0-windows-x86_64-pkg.tar.gz"), "foreign download host"),
            (ReleaseJson(name: "terracotta-0.5.0-windows-x86_64.exe"), "unexpected asset name"),
            (ReleaseJson(size: 512L * 1024 * 1024), "oversized archive")
        })
            Native.Check(Get(Parse(json), "Package") is null && Get(Parse(json), "Unavailable") is string { Length: > 0 },
                "An upstream release with a " + reason + " was offered for installation.");
        try {
            Parse(ReleaseJson(tag: "latest"));
            throw new InvalidOperationException("An unversioned upstream tag was accepted.");
        } catch (InvalidDataException) { }
        Native.Check(!(bool)Native.Call(accepted.GetType(), "IsNewerThan", accepted, "0.10.0")!,
            "Upstream versions were compared as text instead of numerically.");
    }

    private static void CheckReleaseReceipt(string root)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64) return;
        Type component = Native.Type("PCL.TerracottaComponent");
        string Selected() => (string)Get(Native.Call(component, "Selected", null, root)!, "Version")!;
        Directory.CreateDirectory(root);
        string receipt = Path.Combine(root, "selected.json");
        string hash = new('A', 64);
        object Receipt(string source) => new Dictionary<string, object> {
            ["version"] = "0.5.0", ["architecture"] = "x86_64", ["size"] = 9000000, ["source"] = source,
            ["archiveHash"] = hash, ["executableHash"] = hash, ["runtimeHash"] = hash
        };
        try {
            Native.Check(Selected() == "0.4.2", "Without a receipt the bundled release was not selected.");
            File.WriteAllText(receipt, JsonSerializer.Serialize(Receipt(
                "https://github.com/burningtnt/Terracotta/releases/download/v0.5.0/terracotta-0.5.0-windows-x86_64-pkg.tar.gz")));
            Native.Check(Selected() == "0.5.0", "A valid release receipt did not select the installed release.");
            File.WriteAllText(receipt, JsonSerializer.Serialize(Receipt("https://example.com/terracotta-0.5.0-windows-x86_64-pkg.tar.gz")));
            Native.Check(Selected() == "0.4.2", "A receipt pointing at a foreign host was trusted.");
            File.WriteAllText(receipt, "{ not json");
            Native.Check(Selected() == "0.4.2", "A malformed receipt did not fall back to the bundled release.");
        } finally {
            File.Delete(receipt);
        }
    }

    private static void CheckUpstream(Type type, string fixtureRoot, string components)
    {
        string root = Path.Combine(fixtureRoot, "terracotta");
        string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
        string installed = Path.Combine(root, "0.4.2-" + architecture);
        if (components != "download")
        {
            Directory.CreateDirectory(installed);
            foreach (string file in new[] { $"terracotta-0.4.2-windows-{architecture}.exe", "VCRUNTIME140.DLL" })
                File.Copy(Path.Combine(components, file), Path.Combine(installed, file));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(48));
        object connection = Result(Native.Call(type, "StartAsync", null, root, timeout.Token, null));
        try {
            Native.Check(State(Result(Native.Call(type, "ReadAsync", connection, timeout.Token))) == "waiting",
                "The real helper did not enter the lobby.");
            Complete(Native.Call(type, "CreateAsync", connection, "PCLLF fixture", timeout.Token));
            Native.Check(State(Result(Native.Call(type, "ReadAsync", connection, timeout.Token))) == "host-scanning",
                "The real helper did not start the LAN scan.");
            Complete(Native.Call(type, "LeaveAsync", connection, timeout.Token));
            Native.Check(State(Result(Native.Call(type, "ReadAsync", connection, timeout.Token))) == "waiting",
                "The real helper did not return to the lobby.");
            Native.Check(File.Exists(Path.Combine(root, "data", "terracotta", "terracotta.lock")),
                "The real helper shared the system Terracotta runtime directory.");
        } finally {
            Complete(Native.Call(type, "StopAsync", connection));
        }
        string unrelated = Path.Combine(installed, "keep.txt");
        File.WriteAllText(unrelated, "not a component file");
        Type component = Native.Type("PCL.TerracottaComponent");
        Complete(Native.Call(component, "UninstallAsync", null, root, timeout.Token));
        Native.Check(!(bool)Native.Call(component, "HasCachedComponent", null, root)! && File.Exists(unrelated) &&
            !File.Exists(Path.Combine(installed, "VCRUNTIME140.DLL")),
            "Uninstall did not remove exactly the installed component files.");
        CheckDownloadCancellation(component, Path.Combine(fixtureRoot, "terracotta-cancel"));
        Console.WriteLine("PASS real unmodified Terracotta v0.4.2: verified component, isolated process, host scan, task cancellation and uninstall.");
    }

    private static void CheckDownloadCancellation(Type component, string root)
    {
        ModLoader.LoaderCombo<int>? queued = null;
        Func<ModLoader.LoaderCombo<int>, CancellationToken, Task> register = (task, _) => {
            queued = task;
            task.Start(1);
            ModLoader.LoaderTaskbarAdd(task);
            task.Cancel();
            return Task.CompletedTask;
        };
        try
        {
            Complete(Native.Call(component, "EnsureAsync", null, root, CancellationToken.None, register));
            throw new InvalidOperationException("Canceling the component download completed the install.");
        }
        catch (OperationCanceledException)
        {
            Native.Check(queued is { State: ModBase.LoadState.Canceled } && ModLoader.LoaderTaskbar.Contains(queued) &&
                !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
                "The canceled Terracotta task lost its task-manager entry or left component files behind.");
        }
    }

    private static string? State(object room) => (string?)Native.Property(room.GetType(), "State").GetValue(room);
    private static void Complete(object? value) => ((Task)value!).GetAwaiter().GetResult();
    private static object Result(object? value) {
        Complete(value);
        return value!.GetType().GetProperty("Result")!.GetValue(value)!;
    }

    private sealed class ApiFixture : HttpMessageHandler
    {
        internal List<string> Routes { get; } = [];
        internal bool RejectJoin { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Native.Check(request.Method == HttpMethod.Get && uri.Host == "127.0.0.1", "Terracotta commands escaped the loopback IPC endpoint.");
            Routes.Add(uri.PathAndQuery);
            var response = new HttpResponseMessage(RejectJoin ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            if (uri.AbsolutePath == "/state")
                response.Content = new StringContent("{\"state\":\"host-ok\",\"index\":8,\"room\":\"" + RoomCode + "\",\"profiles\":[]}");
            return Task.FromResult(response);
        }
    }
}
