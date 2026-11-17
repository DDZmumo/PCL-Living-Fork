// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.IO.Compression;
using Expression = System.Linq.Expressions.Expression;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Network;

namespace PCLLF.Native.Facade.Tests;

internal static class ModDownloadDependencyChecks {
    private static ResourceProject Project(string id, ResourcePlatforms platform = ResourcePlatforms.Modrinth) => new(new JObject {
        ["Platform"] = (int)platform, ["Types"] = (int)ResourceTypes.Mod, ["Id"] = id, ["Slug"] = id,
        ["RawName"] = "Fixture " + id, ["Description"] = "fixture", ["Website"] = "https://example.invalid/" + id,
        ["LastUpdate"] = DateTime.UtcNow, ["DownloadCount"] = 0, ["Tags"] = new JArray(),
        ["LogoUrl"] = ModBase.PathImage + (id == "root" ? "Blocks/Fabric.png" : "Blocks/Grass.png")
    });
    private static ResourceVersion Version(string project, string id, params ModDownloadDependencyReference[] dependencies) => new() {
        Id = id, ProjectId = project, Display = id, ResourceType = ResourceTypes.Mod, ModLoaders = ModLoaders.Fabric,
        GameVersions = new() { "1.20.1" }, ExactGameVersions = new() { "1.20.1" }, FileName = project + ".jar",
        DownloadUrls = new() { "https://example.invalid/" + id }, ReleaseType = ResourceVersion.ReleaseTypes.Release,
        ReleaseDate = DateTime.UnixEpoch, RequiredDependencyReferences = dependencies.ToList()
    };
    internal static void Run(FixtureEnvironment environment) {
        Parsing(); Planning(); NativeFlow(environment); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS download dependencies: real platform declarations, exact/compatible versions, transitive/cycle/conflict/cancel rules, native modal, unchanged cancel, enabled dependency installation and switch-off primary-only download.");
    }
    private static void Parsing() {
        var json = new JObject { ["id"] = "root-v", ["project_id"] = "root", ["name"] = "root", ["version_number"] = "1",
            ["date_published"] = "2026-01-01T00:00:00Z", ["version_type"] = "release", ["downloads"] = 1,
            ["game_versions"] = new JArray("1.20.1"), ["loaders"] = new JArray("fabric"),
            ["files"] = new JArray(new JObject { ["filename"] = "extra.jar", ["url"] = "https://example.invalid/extra", ["size"] = 1, ["hashes"] = new JObject() },
                new JObject { ["primary"] = true, ["filename"] = "primary.jar", ["url"] = "https://example.invalid/primary", ["size"] = 2, ["hashes"] = new JObject { ["sha1"] = new string('a', 40) } }),
            ["dependencies"] = new JArray(new JObject { ["project_id"] = "P7dR8mSH", ["dependency_type"] = "required" },
                new JObject { ["project_id"] = null, ["version_id"] = "exact", ["dependency_type"] = "required" },
                new JObject { ["project_id"] = "optional", ["dependency_type"] = "optional" },
                new JObject { ["project_id"] = "embedded", ["dependency_type"] = "embedded" }) };
        var version = ResourceVersion.FromPlatformJson(json, ResourceTypes.Mod);
        Native.Check(version.FileName == "primary.jar" && version.RequiredDependencyReferences!.Count == 2 &&
            version.RequiredDependencyReferences[0].ProjectId == "P7dR8mSH" && version.RequiredDependencyReferences[1].VersionId == "exact",
            "Platform parser dropped Fabric API, version-only references, primary file selection or required-only filtering.");
        var cached = ResourceVersion.FromCacheJson(version.ToCacheJson());
        Native.Check(cached.RequiredDependencyReferences is null, "An old/cache model falsely claims complete dependency declarations.");
    }
    private static void Planning() {
        var root = Version("root", "root-v", new ModDownloadDependencyReference("a", null), new ModDownloadDependencyReference("b", null));
        var a = Version("a", "a-v", new ModDownloadDependencyReference("shared", null));
        var b = Version("b", "b-v", new ModDownloadDependencyReference("shared", null));
        var shared = Version("shared", "shared-v", new ModDownloadDependencyReference("a", null));
        var available = new Dictionary<string, ResourceVersion[]> { ["a"] = new[] { a }, ["b"] = new[] { b }, ["shared"] = new[] { shared } };
        var resolver = new ModDownloadDependencyResolver<ResourceVersion> {
            Project = (_, id, _) => Project(id), Versions = (_, id, _) => available[id],
            ExactVersion = (_, _, id, _) => available.Values.SelectMany(value => value).Single(value => value.Id == id)
        };
        var plan = resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default);
        Native.Check(plan.CanInstall && plan.Dependencies.Select(value => value.ProjectId).SequenceEqual(new[] { "shared", "a", "b" }),
            "Transitive/shared/cyclic requirements did not produce one dependency-first proposal.");
        var wrongGame = Version("a", "wrong-game"); wrongGame.GameVersions = wrongGame.ExactGameVersions = new() { "1.21.1" };
        var wrongLoader = Version("a", "wrong-loader"); wrongLoader.ModLoaders = ModLoaders.Forge;
        var beta = Version("a", "new-beta"); beta.ReleaseType = ResourceVersion.ReleaseTypes.Beta; beta.ReleaseDate = DateTime.UtcNow;
        available["a"] = new[] { wrongGame, wrongLoader, beta, a };
        root.RequiredDependencyReferences = new() { new ModDownloadDependencyReference("a", null) };
        plan = resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default);
        Native.Check(plan.CanInstall && plan.Dependencies.Single(value => value.ProjectId == "a").Version == a, "Resolver selected an incompatible or beta version ahead of a compatible release.");
        resolver.Installed = (version, _) => version == beta;
        plan = resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default);
        Native.Check(plan.Dependencies.Single(value => value.ProjectId == "a").Installed && plan.Dependencies.Single(value => value.ProjectId == "a").Version == beta,
            "A byte-verified installed compatible version was replaced merely because it was not the preferred release.");
        resolver.Installed = (_, _) => false;
        root.RequiredDependencyReferences = new() { new(null, "a-v") };
        Native.Check(resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default).CanInstall, "Version-only pinned dependency was not resolved to its project.");
        root.RequiredDependencyReferences = new() { new("a", "wrong-game") };
        Native.Check(!resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default).CanInstall, "An incompatible pinned version fell back to a different file.");
        root.RequiredDependencyReferences = new() { new("a", "a-v"), new("a", "new-beta") };
        Native.Check(!resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default).CanInstall, "Conflicting exact versions were silently combined.");
        var laterPin = Version("b", "b-pinned", new ModDownloadDependencyReference("a", "a-v"));
        available["b"] = new[] { laterPin }; available["a"] = new[] { a, beta };
        a.RequiredDependencyReferences = new();
        root.RequiredDependencyReferences = new() { new("a", null), new("b", null) };
        a.ReleaseDate = DateTime.UnixEpoch; beta.ReleaseType = ResourceVersion.ReleaseTypes.Release;
        plan = resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default);
        Native.Check(plan.CanInstall && plan.Dependencies.Single(value => value.ProjectId == "a").Version == a,
            "A later exact requirement did not refine the earlier project-level selection.");
        var stale = Version("a", "stale"); stale.RequiredDependencyReferences = null;
        var refreshed = Version("a", "stale"); refreshed.GameVersions = refreshed.ExactGameVersions = new() { "1.21.1" };
        available["a"] = new[] { stale }; root.RequiredDependencyReferences = new() { new("a", null) };
        resolver.ExactVersion = (_, _, _, _) => refreshed;
        Native.Check(!resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, default).CanInstall,
            "Refreshing old cache metadata bypassed the final compatibility check.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { resolver.Resolve(Project("root"), root, "1.20.1", ModLoaders.Fabric, canceled.Token); throw new Exception("Pre-canceled planning succeeded."); }
        catch (OperationCanceledException) { }
    }
    private static void NativeFlow(FixtureEnvironment environment) {
        string root = Path.Combine(environment.Data, "download-dependencies"), mods = Path.Combine(root, "mods"), cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(mods);
        byte[] Jar(string id) {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                writer.Write(new JObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = "1.0.0", ["name"] = id }.ToString());
            return bytes.ToArray();
        }
        using var server = new Server(new() { ["/root.jar"] = Jar("fixture_root"), ["/lib.jar"] = Jar("fixture_lib") });
        // The dedicated child retains this complete transport context while the
        // three genuine foreground manager loops await their parent-owned end.
        var transport = (LegacyNetworkHost<ModNet.NetFile, ModNet.LoaderDownload, ModNet.NetManagerClass, ModLoader.LoaderBase>)Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
        transport.TempFolder = () => Path.Combine(root, "network-temp") + Path.DirectorySeparatorChar;
        transport.RequestTempFolder = () => Path.Combine(root, "network-work-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
        transport.AppData = () => Path.Combine(root, "app-data") + Path.DirectorySeparatorChar;
        transport.MinecraftFolders = () => new[] { Path.Combine(root, "minecraft") + Path.DirectorySeparatorChar };
        transport.SignCdn = url => url.StartsWith(server.Url + "/", StringComparison.Ordinal) ? url : throw new IOException("Dependency transport escaped its owned HTTP server.");
        var sign = transport.SignHeaders;
        transport.SignHeaders = (string url, ref HttpRequestMessage request, bool browser, bool credentials) => {
            Native.Check(url.StartsWith(server.Url + "/", StringComparison.Ordinal), "Dependency request escaped owned HTTP.");
            sign(url, ref request, browser, credentials);
            Native.Check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("x-api-key"), "Dependency loopback carried credentials.");
        };
        var primary = Version("root", "root-v", new ModDownloadDependencyReference("lib", null)); var dependency = Version("lib", "lib-v");
        void Download(ResourceVersion version, string path) { version.DownloadUrls = new() { server.Url + path }; version.Hash = Convert.ToHexString(SHA1.HashData(server.Files[path])); version.Size = server.Files[path].Length; }
        Download(primary, "/root.jar"); Download(dependency, "/lib.jar");
        Type host = Native.Type("PCL.ModDownloadDependencies"), installer = Native.Type("PCL.ModDownloadInstaller");
        var fields = new[] { "ReadProject", "ReadVersions", "ReadVersion", "Select" }.Select(name => Native.Field(host, name)).Append(Native.Field(installer, "DownloadCache")).ToArray();
        var originals = fields.Select(field => field.GetValue(null)).ToArray();
        object? oldSwitch = Settings.Get("ToolDownloadModDependencies");
        int reads = 0;
        try {
            fields[0].SetValue(null, new Func<ResourcePlatforms, string, CancellationToken, ResourceProjectModelData>((_, id, _) => { reads++; return Project(id); }));
            fields[1].SetValue(null, new Func<ResourcePlatforms, string, CancellationToken, IReadOnlyList<ResourceVersion>>((_, _, _) => new[] { dependency }));
            fields[2].SetValue(null, new Func<ResourcePlatforms, string, string, CancellationToken, ResourceVersion>((_, _, _, _) => throw new Exception("Unexpected exact metadata request.")));
            fields[4].SetValue(null, new Func<string>(() => cache));
            var plan = (ModDownloadDependencyPlan<ResourceVersion>)Native.Call(host, "Prepare", null, Project("root"), primary, Path.Combine(mods, "root.jar"), null, CancellationToken.None)!;
            Native.Check(plan.CanInstall && plan.Dependencies.Single().Version == dependency, "Actual Main preparation did not resolve the required version.");
            var panel = (StackPanel)Native.Call(host, "CreateView", null, plan)!;
            var cards = ((StackPanel)panel.Children.OfType<MyScrollViewer>().Single().Content).Children.OfType<Border>().ToArray();
            Native.Check(cards.Length == 2 && cards.All(card => card.BorderThickness.Left == 1 && card.CornerRadius.TopLeft > 0),
                "The selected mod and required dependency are not displayed in bordered cards.");
            Native.Check(((Grid)cards[0].Child).Children.OfType<MyImage>().Single().Source == plan.PrimaryProject!.LogoUrl &&
                ((Grid)cards[1].Child).Children.OfType<MyImage>().Single().Source == plan.Dependencies.Single().Project!.LogoUrl,
                "Download cards do not use each mod's own project icon.");
            var prompt = panel.Children.OfType<MyCheckBox>().Single();
            prompt.Checked = false; Native.Call(typeof(MyCheckBox), "OnChange", prompt, true);
            Native.Check(!Settings.Get<bool>("ToolDownloadModDependencies"), "Native modal prompt switch did not persist.");
            prompt.Checked = true; Native.Call(typeof(MyCheckBox), "OnChange", prompt, true);
            Native.Check(Settings.Get<bool>("ToolDownloadModDependencies"), "Native modal prompt switch could not be re-enabled.");
            if (Environment.GetEnvironmentVariable("PCLLF_DEPENDENCY_PREVIEW_DIR") is { Length: > 0 } preview) {
                Directory.CreateDirectory(preview); panel.Measure(new Size(490, 500)); panel.Arrange(new Rect(0, 0, 490, panel.DesiredSize.Height)); panel.UpdateLayout();
                var image = new RenderTargetBitmap(490, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(panel);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(preview, "dependency-content.png")); png.Save(file);
                var modal = new MyMsgText(new ModMain.MyMsgBoxConverter { Title = "下载 Mod 与必需前置", Text = "此模组需要以下前置。确认后会补齐缺失项，已安装的前置会保留。",
                    Button1 = "安装并补全", Button2 = "取消", Button3 = "仅下载此模组", ContentFactory = () => (FrameworkElement)Native.Call(host, "CreateView", null, plan)! });
                Native.Check(modal.Width == 590, "Dependency modal did not retain its bounded native width.");
                modal.Opacity = 1; modal.RenderTransform = Transform.Identity;
                var backdrop = new Border { Background = Brushes.WhiteSmoke, Child = modal };
                backdrop.Measure(new Size(800, 600)); backdrop.Arrange(new Rect(new Point(), backdrop.DesiredSize)); backdrop.UpdateLayout();
                var complete = new RenderTargetBitmap((int)Math.Ceiling(backdrop.ActualWidth), (int)Math.Ceiling(backdrop.ActualHeight), 96, 96, PixelFormats.Pbgra32); complete.Render(backdrop);
                var completePng = new PngBitmapEncoder(); completePng.Frames.Add(BitmapFrame.Create(complete));
                using var completeFile = File.Create(Path.Combine(preview, "dependency-dialog.png")); completePng.Save(completeFile);
            }
            var invoke = fields[3].FieldType.GetMethod("Invoke")!;
            var parameter = Expression.Parameter(typeof(ModDownloadDependencyPlan<ResourceVersion>));
            int confirmations = 0;
            Action<ModDownloadDependencyPlan<ResourceVersion>> observe = _ => confirmations++;
            fields[3].SetValue(null, Expression.Lambda(fields[3].FieldType, Expression.Block(
                Expression.Invoke(Expression.Constant(observe), parameter),
                Expression.Constant(Enum.Parse(invoke.ReturnType, "Cancel"), invoke.ReturnType)), parameter).Compile());
            Task canceled = Task.Run(() => Native.Call(installer, "PrepareAndStart", null, Project("root"), primary, Path.Combine(mods, "root.jar"), ResourceDownloadReason.Standalone, null));
            VisualAssetChecks.Pump(() => canceled.IsCompleted, "Canceled download proposal did not return.", 10000); canceled.GetAwaiter().GetResult();
            Native.Check(!Directory.EnumerateFileSystemEntries(mods).Any() && !Directory.Exists(cache) && server.Requests == 0, "Canceling the actual proposal downloaded or installed files.");
            File.WriteAllBytes(Path.Combine(mods, "lib.jar"), server.Files["/lib.jar"]);
            canceled = Task.Run(() => Native.Call(installer, "PrepareAndStart", null, Project("root"), primary, Path.Combine(mods, "root.jar"), ResourceDownloadReason.Standalone, null));
            VisualAssetChecks.Pump(() => canceled.IsCompleted, "Installed dependency confirmation did not return.", 10000); canceled.GetAwaiter().GetResult();
            Native.Check(confirmations == 2 && server.Requests == 0 && !Directory.Exists(cache), "A download with all required dependencies installed bypassed confirmation.");
            File.Delete(Path.Combine(mods, "lib.jar"));
            var independent = Version("root", "independent-v");
            canceled = Task.Run(() => Native.Call(installer, "PrepareAndStart", null, Project("root"), independent, Path.Combine(mods, "root.jar"), ResourceDownloadReason.Standalone, null));
            VisualAssetChecks.Pump(() => canceled.IsCompleted, "No-dependency confirmation did not return.", 10000); canceled.GetAwaiter().GetResult();
            Native.Check(confirmations == 3 && server.Requests == 0 && !Directory.Exists(cache), "A version with no required dependencies bypassed the download summary.");
            File.WriteAllBytes(Path.Combine(mods, "lib.jar.disabled"), server.Files["/lib.jar"]);
            File.WriteAllBytes(Path.Combine(mods, "unrelated.jar"), Jar("fixture_other"));
            var loader = (ModLoader.LoaderBase)Native.Call(installer, "StartPrepared", null, Project("root"), primary, Path.Combine(mods, "root.jar"), ResourceDownloadReason.Standalone, null, plan)!;
            VisualAssetChecks.Pump(() => loader.State.ToString() is "Finished" or "Failed" or "Aborted", "Actual dependency download did not terminate.", 25000);
            Native.Check(loader.State.ToString() == "Finished", "Actual dependency download failed: " + loader.Error);
            VisualAssetChecks.Pump(() => Directory.Exists(cache) && !Directory.EnumerateFileSystemEntries(cache).Any(), "Download staging did not drain.", 5000);
            Native.Check(File.ReadAllBytes(Path.Combine(mods, "root.jar")).SequenceEqual(server.Files["/root.jar"]) &&
                File.ReadAllBytes(Path.Combine(mods, "lib.jar")).SequenceEqual(server.Files["/lib.jar"]) && !File.Exists(Path.Combine(mods, "lib.jar.disabled")) &&
                File.Exists(Path.Combine(mods, "unrelated.jar")), "Confirmed dependency installation lost bytes, kept the required library disabled or altered unrelated mods.");
            plan = (ModDownloadDependencyPlan<ResourceVersion>)Native.Call(host, "Prepare", null, Project("root"), primary, Path.Combine(mods, "root.jar"), null, CancellationToken.None)!;
            Native.Check(plan.Dependencies.Single().Installed, "A verified enabled renamed/installed dependency was not recognized.");
            string off = Path.Combine(root, "off"); Directory.CreateDirectory(off); int before = reads;
            Settings.Set("ToolDownloadModDependencies", false);
            Native.Call(installer, "Start", null, Project("root"), primary, Path.Combine(off, "root.jar"), ResourceDownloadReason.Standalone, null);
            VisualAssetChecks.Pump(() => File.Exists(Path.Combine(off, "root.jar")) && !Directory.EnumerateFileSystemEntries(cache).Any(), "Switch-off primary download did not complete.", 20000);
            Native.Check(reads == before && !File.Exists(Path.Combine(off, "lib.jar")), "Disabling download dependency checks still resolved or installed dependencies.");
        } finally {
            foreach (var pending in ModLoader.LoaderTaskbar.ToArray())
                if (pending.State.ToString() is "Waiting" or "Loading") pending.Cancel();
            VisualAssetChecks.Pump(() => !Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any(), "Owned download cleanup did not drain.", 10000);
            for (int index = 0; index < fields.Length; index++) fields[index].SetValue(null, originals[index]);
            Settings.Set("ToolDownloadModDependencies", oldSwitch);
        }
    }
    private sealed class Server : IDisposable {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task worker;
        internal readonly Dictionary<string, byte[]> Files;
        internal readonly string Url;
        internal int Requests;
        internal Server(Dictionary<string, byte[]> files) {
            Files = files; listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = Task.Run(async () => {
                try { while (!stopping.IsCancellationRequested) {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    using var stream = client.GetStream(); var request = new StringBuilder(); byte[] buffer = new byte[1024];
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) {
                        int read = await stream.ReadAsync(buffer, stopping.Token); if (read == 0) break;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    string path = request.ToString().Split(' ')[1].Split('?')[0]; Interlocked.Increment(ref Requests);
                    byte[] bytes = Files[path];
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"), stopping.Token);
                    await stream.WriteAsync(bytes, stopping.Token);
                } } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
            });
        }
        public void Dispose() { stopping.Cancel(); listener.Stop(); Native.Check(worker.Wait(5000), "Fixture HTTP server did not drain."); stopping.Dispose(); }
    }
}
