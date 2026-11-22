// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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

        string? components = Environment.GetEnvironmentVariable("PCLLF_TERRACOTTA_TEST_COMPONENTS");
        if (!string.IsNullOrWhiteSpace(components)) CheckUpstream(type, environment.Data, components);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS Terracotta lobby IPC contract: standard U/ room codes, escaped names, state mapping, host and exit commands.");
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
