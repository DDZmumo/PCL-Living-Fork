using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static partial class MmcImportRecoveryUiChecks {
    internal static void RunStageDetails(FixtureEnvironment environment) {
        using var scope = new Scope(environment, "mmc-import-stage-details");
        scope.RealStageDetails(); scope.Finish();
        Console.WriteLine("PASS mmc-import-stage-details: actual Main tiny ZIP/native ZIP/cached asset import displays genuine runtime substeps and sticky asset-copy details; real inventory, manifest, placement and verification counts stay distinct from bytes, including a zero-byte file. Held producer boundaries keep future stages waiting and total progress monotonic; unknown totals show no invented percentage. No game launch, network transfer or desktop interaction.");
    }
    private static string VisibleDetail(MyCard card, string stage) => Rows(card).Children.OfType<StackPanel>()
        .Single(value => value.Tag?.ToString() == stage).Children.OfType<TextBlock>().Single(value => Equals(value.Tag, "mmc-import-detail")).Text;

    private sealed partial class Scope {
        private sealed record DetailEvent(MmcInstanceBackendProgress Event, double Fraction, StageView[] Stages);
        internal void RealStageDetails() {
            AddDetailSources(); var (left, right) = TaskPages();
            using ManualResetEventSlim native = new(), nativeRelease = new(), plan = new(), planRelease = new(),
                manifest = new(), manifestRelease = new(), placement = new(), placementRelease = new();
            var events = new ConcurrentQueue<DetailEvent>();
            var task = StartProgressImport("actual-stage-details", left, value => {
                events.Enqueue(new(value, current!.Progress, Stages(current!)));
                if (value.Phase == "native-extract" && !native.IsSet) Hold(native, nativeRelease, "native extraction");
                if (value.Phase == "private-payload-plan" && value.CompletedFiles == 1) Hold(plan, planRelease, "inventory");
                if (value.Phase == "private-payload-manifest") Hold(manifest, manifestRelease, "manifest");
                if (value.Phase == "private-payload-install" && value.CompletedFiles == 1) Hold(placement, placementRelease, "placement");
            });
            try {
                Until(() => native.IsSet, "Actual local native ZIP did not reach its real extraction callback."); left.TaskRefresh(task);
                var runtimeStage = Stage(task, "PrepareRuntime");
                Native.Check(runtimeStage is { State: "Running", Progress: null, TotalBytes: 0, CompletedBytes: > 0, Detail: "解压本地运行库" } &&
                    VisibleDetail(Cards(right).Single(), "PrepareRuntime").StartsWith("解压本地运行库", StringComparison.Ordinal),
                    "Native extraction remained hidden in a tooltip/generic runtime title or showed an invented total percentage.");
                nativeRelease.Set(); Until(() => plan.IsSet, "Actual import did not reach its first real inventory member."); left.TaskRefresh(task);
                var planning = Stage(task, "InstallPackage");
                Native.Check(planning is { State: "Running", CompletedFiles: 1, TotalFiles: 3, CompletedBytes: 0, TotalBytes: 0, Progress: > 0 and < .35 } &&
                    Stage(task, "PublishInstance").State == "Waiting" && VisibleDetail(Cards(right).Single(), "InstallPackage").Contains("核对文件清单 1/3", StringComparison.Ordinal),
                    "Real preflight counts did not advance visibly, became bytes, or completed the install/publication prematurely.");
                planRelease.Set(); Until(() => manifest.IsSet, "Actual import did not reach manifest persistence."); left.TaskRefresh(task);
                Native.Check(Stage(task, "InstallPackage") is { State: "Running", Progress: null, Detail: "保存文件清单", TotalBytes: > 0 } &&
                    VisibleDetail(Cards(right).Single(), "InstallPackage").StartsWith("保存文件清单", StringComparison.Ordinal),
                    "Manifest boundary reused the finished plan percentage or hid its current work.");
                manifestRelease.Set(); Until(() => placement.IsSet, "Actual import did not place its first real payload file."); left.TaskRefresh(task);
                var installing = Stage(task, "InstallPackage"); string placementText = VisibleDetail(Cards(right).Single(), "InstallPackage");
                Native.Check(installing is { State: "Running", CompletedFiles: 1, TotalFiles: 3, CompletedBytes: > 0, TotalBytes: > 0, Progress: > .40 and < 1 } &&
                    Stage(task, "PublishInstance").State == "Waiting" && placementText.Contains("放置文件 1/3", StringComparison.Ordinal) && placementText.Contains(" / ", StringComparison.Ordinal),
                    "Actual placement did not show independent file counts and byte progress before publication.");
            } finally { nativeRelease.Set(); planRelease.Set(); manifestRelease.Set(); placementRelease.Set(); }
            Returned(task, ModBase.LoadState.Finished);
            var recorded = events.ToArray();
            var expectedDetails = new Dictionary<string, string> {
                ["preparing-runtime"] = "检查游戏运行文件与缓存", ["materializing-runtime"] = "整理游戏运行文件",
                ["asset-index-preparation"] = "读取并校验资源索引", ["asset-object-preparation"] = "核对并整理资源文件",
                ["runtime-view-materialization"] = "生成游戏运行目录", ["asset-view-materialization"] = "生成游戏资源目录", ["native-extract"] = "解压本地运行库"
            };
            foreach (var expected in expectedDetails) Native.Check(recorded.Any(value => value.Event.Phase == expected.Key &&
                value.Stages.Single(stage => stage.Stage == "PrepareRuntime") is { State: "Running", Progress: null } currentStage && currentStage.Detail == expected.Value),
                "Actual runtime phase was missing, hidden or prematurely finished: " + expected.Key);
            Native.Check(recorded.Any(value => value.Event.Phase == "copy" && value.Stages.Single(stage => stage.Stage == "PrepareRuntime").Detail == "生成游戏资源目录"),
                "Actual asset-view copy replaced its explicit asset phase with a generic library-copy label.");
            var inventory = recorded.Where(value => value.Event.Phase == "private-payload-plan").ToArray();
            Native.Check(inventory.Select(value => value.Event.CompletedFiles).SequenceEqual(new int?[] { 0, 1, 2, 3 }) &&
                inventory.All(value => value.Event.CompletedBytes == 0 && value.Event.TotalBytes == 0), "Actual inventory count events were lost or interpreted as bytes.");
            var placed = recorded.Where(value => value.Event.Phase == "private-payload-install" && value.Event.CompletedFiles > 0).ToArray();
            Native.Check(placed.Length == 3 && placed[1].Event.CompletedBytes == placed[0].Event.CompletedBytes && placed[1].Event.CompletedFiles == placed[0].Event.CompletedFiles + 1,
                "Zero-byte payload did not advance its genuine file count independently from bytes.");
            Native.Check(recorded.Select(value => value.Fraction).SequenceEqual(recorded.Select(value => value.Fraction).Order()) &&
                recorded.Any(value => value.Event.Phase == "private-payload-verify" && value.Event.CompletedFiles == 3 && value.Event.TotalFiles == 3) &&
                Stages(task).All(value => value.State == "Finished"), "Real plan/manifest/place/verify transitions regressed total progress or lost completed file counts.");
            string finalRoot = Path.Combine(game, "versions", "actual-stage-details");
            string dll = Directory.EnumerateFiles(Path.Combine(finalRoot, "PCL", "MmcRuntime"), "fixture.dll", SearchOption.AllDirectories).Single();
            Native.Check(File.ReadAllBytes(dll).SequenceEqual(new byte[] { 1, 2, 3 }) && MmcInstanceInspector.Read(finalRoot).IsConsistent,
                "Actual native ZIP fixture was not extracted and published through the genuine backend.");
            left.TaskRefresh(task); ModLoader.LoaderTaskbar.Remove(task);

            static void Hold(ManualResetEventSlim entered, ManualResetEventSlim release, string stage) {
                entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("Owned " + stage + " detail barrier was not released.");
            }
        }

        private void AddDetailSources() {
            byte[] asset = Encoding.UTF8.GetBytes("owned cached asset"); string assetHash = Convert.ToHexStringLower(SHA1.HashData(asset));
            byte[] index = Encoding.UTF8.GetBytes(new JObject { ["virtual"] = true, ["objects"] = new JObject {
                ["sound/fixture.ogg"] = new JObject { ["hash"] = assetHash, ["size"] = asset.Length }
            } }.ToString());
            string objectPath = Path.Combine(game, "assets", "objects", assetHash[..2], assetHash); Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!); File.WriteAllBytes(objectPath, asset);
            File.WriteAllBytes(Path.Combine(game, "assets", "indexes", "initial-recovery.json"), index);
            using var nativeBytes = new MemoryStream();
            using (var nativeZip = new ZipArchive(nativeBytes, ZipArchiveMode.Create, leaveOpen: true)) {
                using var dll = nativeZip.CreateEntry("fixture.dll", CompressionLevel.NoCompression).Open(); dll.Write(new byte[] { 1, 2, 3 });
            }
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            var originalPatch = zip.GetEntry("patches/net.minecraft.json")!; JObject patch;
            using (var reader = new StreamReader(originalPatch.Open(), Encoding.UTF8)) patch = JObject.Parse(reader.ReadToEnd());
            originalPatch.Delete();
            patch["assetIndex"]!["sha1"] = Convert.ToHexStringLower(SHA1.HashData(index)); patch["assetIndex"]!["size"] = index.Length;
            patch["libraries"] = new JArray(new JObject { ["name"] = "fixture:native:1", ["MMC-hint"] = "local", ["MMC-filename"] = "fixture-native.jar",
                ["natives"] = new JObject { ["windows"] = "natives-windows-64" } });
            using (var writer = new StreamWriter(zip.CreateEntry("patches/net.minecraft.json").Open(), new UTF8Encoding(false))) writer.Write(patch.ToString());
            using (var output = zip.CreateEntry("libraries/fixture-native.jar", CompressionLevel.NoCompression).Open()) output.Write(nativeBytes.ToArray());
            using (var writer = new StreamWriter(zip.CreateEntry("minecraft/config/counting.cfg").Open(), new UTF8Encoding(false))) writer.Write("owned counted file");
            zip.CreateEntry("minecraft/config/empty.cfg", CompressionLevel.NoCompression).Open().Dispose();
        }
    }
}
