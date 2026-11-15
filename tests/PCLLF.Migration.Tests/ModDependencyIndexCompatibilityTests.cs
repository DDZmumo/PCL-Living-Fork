using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using Context = ModDependencyInspectorPipelineTests.Context;
using Report = ModDependencyInspectorPipelineTests.Report;
using Local = PCLCS.ModDependencyInspection<ModDependencyInspectorPipelineTests.Context, ModDependencyInspectorPipelineTests.Mod, ModDependencyInspectorPipelineTests.Dependency, ModDependencyInspectorPipelineTests.Edge, ModDependencyInspectorPipelineTests.Issue, ModDependencyInspectorPipelineTests.Impact, ModDependencyInspectorPipelineTests.Report, ModDependencyInspectorPipelineTests.OnlineProject, ModDependencyInspectorPipelineTests.OnlineDependency, ModDependencyInspectorPipelineTests.OnlineFile>;
using Online = PCLCS.ModDependencyOnlinePipeline<ModDependencyInspectorPipelineTests.Context, ModDependencyInspectorPipelineTests.Mod, ModDependencyInspectorPipelineTests.Dependency, ModDependencyInspectorPipelineTests.Edge, ModDependencyInspectorPipelineTests.Issue, ModDependencyInspectorPipelineTests.Impact, ModDependencyInspectorPipelineTests.Report, ModDependencyInspectorPipelineTests.OnlineProject, ModDependencyInspectorPipelineTests.OnlineDependency, ModDependencyInspectorPipelineTests.OnlineFile, ModDependencyOnlinePipelineTests.Options, ModDependencyOnlinePipelineTests.Cache>;

// Actual LF 50b028da5fcad2226d74d901a41d8c43a249bfb2 fixtures from
// scripts/Test-ModDependencies.ps1 and Test-ModDependencyOnline.ps1, LF LICENCE.
// Uses the real C# backend and real archives/index files; no game or JAR code executes.
internal static class ModDependencyIndexCompatibilityTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() => LocalCases().Concat(OnlineCases());
    internal static IEnumerable<(string Name, Action Run)> LocalCases() {
        yield return ("dependency index reuses unchanged archives after a disable rename", RenameReusesArchives);
        yield return ("dependency index reads only added or modified archives and drops deleted providers", IncrementalChanges);
        yield return ("dependency removal preview avoids index locks and preserves immutable cold and warm snapshots", ImpactWithoutIndexLocks);
        yield return ("dependency inventory reuse preserves nested providers and rejects ambiguous renames", NestedRenames);
        yield return ("dependency incremental snapshots recover old and malformed caches and honor forced rebuilds", SnapshotCompatibility);
        yield return ("dependency index persists graph identity and reuses locked archives while reevaluating Java", PersistAndReevaluate);
        yield return ("dependency index invalidates inventory and recovers corrupt or unavailable storage without publishing cancellation", InvalidationAndFailures);
        yield return ("dependency compatibility reads genuine legacy annotations and forces class-cache rebuilds without filename guesses", LegacyAndLoaderRules);
    }
    internal static IEnumerable<(string Name, Action Run)> OnlineCases() {
        yield return ("dependency online refresh covers disabled files and persists project identity separately from pinned versions", RefreshAndIdentity);
    }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static Context EnvironmentFor(string loader = "fabricloader", string version = "0.16.0", string minecraft = "1.20.1", string java = "17") {
        Context value = new() { MinecraftVersion = minecraft, JavaVersion = java, LoaderKnown = true }; value.LoaderVersions[loader] = version; return value;
    }
    private static JObject Fabric(string id, JObject? extras = null) {
        JObject value = new() { ["schemaVersion"] = 1, ["id"] = id, ["name"] = id, ["version"] = "1.0" };
        if (extras is not null) foreach (JProperty pair in extras.Properties()) value[pair.Name] = pair.Value.DeepClone(); return value;
    }
    private static byte[] Json(JToken value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
    private static string Jar(string folder, string name, params (string Path, byte[] Bytes)[] entries) {
        Directory.CreateDirectory(folder); string file = Path.Combine(folder, name); using ZipArchive zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var entry in entries) { using Stream payload = zip.CreateEntry(entry.Path, CompressionLevel.NoCompression).Open(); payload.Write(entry.Bytes); } return file;
    }
    private static string Mod(string folder, string name, string id, JObject? extras = null) => Jar(folder, name, ("fabric.mod.json", Json(Fabric(id, extras))));
    private sealed class Fixture : IDisposable {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pcllf-new-dependencies-" + Guid.NewGuid().ToString("N"));
        private readonly string previous = Local.Index.CacheDirectory;
        internal Fixture() { Directory.CreateDirectory(Root); Local.Index.CacheDirectory = Path.Combine(Root, "cache"); Local.Index.ClearMemory(); }
        internal string Folder(string name) { string path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
        public void Dispose() { Local.Index.ClearMemory(); Local.Index.CacheDirectory = previous; Directory.Delete(Root, true); }
    }
    private static void PersistAndReevaluate() {
        using Fixture fixture = new(); string folder = fixture.Folder("mods"); Context context = EnvironmentFor();
        Mod(folder, "core.jar", "core"); string user = Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["core"] = ">=1", ["java"] = ">=21" } });
        Report first = Local.Inspect(folder, context); Check(!first.IndexHit && first.IndexPersisted && first.HasErrors, "Initial tree was not persisted with its Java mismatch.");
        using (FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Local.Index.ClearMemory(); Report second = Local.Inspect(folder, context); Check(second.IndexHit && second.Mods.Count == 2, "Disk-tree reuse reopened an exclusively locked archive.");
            var edge = second.Edges.Single(value => value.Requirement.Id == "core"); var owner = second.Mods.Single(value => value.Id == "user");
            Check(ReferenceEquals(edge.Owner, owner) && ReferenceEquals(edge.Requirement, owner.Dependencies.Single(value => value.Id == "core")), "JSON restore copied graph nodes instead of preserving edge identity.");
            Check(second.ReverseDependencies.Values.SelectMany(value => value).Any(value => ReferenceEquals(value, edge)), "Reverse dependencies lost edge identity.");
            Report changed = Local.Inspect(folder, EnvironmentFor(java: "21.0.2")); Check(changed.IndexHit && changed.IndexReevaluated && !changed.HasErrors, "Java-only change reread archives or kept old errors.");
        }
        first.Mods[0].Name = "caller mutation"; Report fresh = Local.Inspect(folder, EnvironmentFor(java: "21.0.2")); Check(fresh.Mods.All(value => value.Name != "caller mutation"), "Mutable caller report leaked into cached JSON.");
    }
    private static void RenameReusesArchives() {
        using Fixture fixture = new(); string folder = fixture.Folder("rename"); Context context = EnvironmentFor();
        string core = Mod(folder, "core.jar", "core");
        string user = Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["core"] = "*" } });
        Local.Inspect(folder, context);
        Local.Inspect(folder, context);
        File.Move(core, core + ".disabled");
        // An unchanged consumer must not be reopened merely because its provider was renamed.
        using FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None);
        Report disabled = Local.Inspect(folder, context);
        Check(!disabled.IndexReadFailed && disabled.Mods.Count == 2,
            "A disable rename reopened an unchanged archive instead of reusing its declarations.");
        Check(disabled.IndexArchivesRead == 0 && disabled.IndexArchivesReused == 2, "A disable rename reread archive metadata.");
        Check(disabled.HasErrors && disabled.Mods.Single(value => value.Id == "core").RootPath == core + ".disabled",
            "Reused declarations did not update the disabled path and dependency graph.");
        File.Move(core + ".disabled", core);
        Report enabled = Local.Inspect(folder, context);
        Check(!enabled.HasErrors && enabled.IndexArchivesRead == 0 && enabled.IndexArchivesReused == 2,
            "Reenabling a cached provider failed to recompute dependencies without reopening archives.");
        Local.Index.ClearMemory();
        File.Move(core, core + ".old");
        Report disk = Local.Inspect(folder, context);
        Check(disk.HasErrors && disk.IndexArchivesRead == 0, "Disk snapshots lost the .old rename optimization.");
    }
    private static void IncrementalChanges() {
        using Fixture fixture = new(); string folder = fixture.Folder("incremental"); Context context = EnvironmentFor();
        string core = Mod(folder, "core.jar", "core");
        string user = Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["core"] = ">=2" } });
        Local.Inspect(folder, context);
        using FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None);
        File.Delete(core);
        JObject updated = Fabric("core"); updated["version"] = "2.0";
        Jar(folder, "core.jar", ("fabric.mod.json", Json(updated)));
        File.SetLastWriteTimeUtc(core, DateTime.UtcNow.AddMinutes(1));
        Report changed = Local.Inspect(folder, context);
        Check(changed.IndexArchivesRead == 1 && changed.IndexArchivesReused == 1 && !changed.IndexReadFailed && !changed.HasErrors,
            "Changing one provider reread an unchanged consumer or retained the old version.");
        File.Delete(core);
        Report deleted = Local.Inspect(folder, context);
        Check(deleted.IndexArchivesRead == 0 && deleted.IndexArchivesReused == 1 && deleted.HasErrors && deleted.Mods.Count == 1,
            "A deleted provider survived in the cached graph.");
        Jar(folder, "added.jar", ("fabric.mod.json", Json(updated)));
        Report added = Local.Inspect(folder, context);
        Check(added.IndexArchivesRead == 1 && added.IndexArchivesReused == 1 && !added.HasErrors,
            "Adding one provider rescanned the instance or lost its reverse dependency.");
        File.Move(Path.Combine(folder, "added.jar"), Path.Combine(folder, "renamed.jar"));
        Report renamed = Local.Inspect(folder, context);
        Check(renamed.IndexArchivesRead == 1 && renamed.IndexArchivesReused == 1 && !renamed.HasErrors,
            "An arbitrary rename must revalidate only the new archive, not guess its identity or rescan other files.");
    }
    private static void ImpactWithoutIndexLocks() {
        using Fixture fixture = new(); string folder = fixture.Folder("impact"); Context context = EnvironmentFor();
        string core = Mod(folder, "core.jar", "core");
        string user = Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["core"] = "*" } });
        Report cold = Local.InspectForImpact(folder, context);
        Check(!cold.IndexPersisted && !File.Exists(Local.Index.IndexPath(folder, context)) && Local.GetImpacts(cold, new[] { core }).Count == 1,
            "Cold removal preview wrote a disk index or skipped an ordinary instance's dependencies.");
        cold.Mods.Clear();
        using (FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None)) {
            File.Move(core, core + ".disabled");
            Report renamed = Local.InspectForImpact(folder, context);
            Check(renamed.IndexArchivesRead == 0 && renamed.Mods.Count == 2 && renamed.HasErrors,
                "Cold preview snapshots were mutable or lost when the provider was disabled.");
        }
        Report persisted = Local.Inspect(folder, context);
        string index = persisted.IndexFile!; string bytes = File.ReadAllText(index);
        var gates = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)typeof(Local.Index)
            .GetField("Gates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        SemaphoreSlim gate = gates[index];
        void PreviewWhileHeld() {
            Task<Report> work = Task.Run(() => Local.InspectForImpact(folder, context));
            Check(work.Wait(TimeSpan.FromSeconds(3)), "Removal preview queued behind an index writer.");
            Check(work.Result.HasErrors && File.ReadAllText(index) == bytes, "Lock-free preview changed the shared index or used a stale enabled graph.");
        }
        gate.Wait();
        try { PreviewWhileHeld(); } finally { gate.Release(); }
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.ToUpperInvariant()))).ToLowerInvariant();
        using var mutex = new Mutex(false, @"Local\PCLLF.DependencyIndex." + digest);
        mutex.WaitOne();
        try { PreviewWhileHeld(); } finally { mutex.ReleaseMutex(); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false;
        try { Local.InspectForImpact(folder, context, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && File.ReadAllText(index) == bytes, "Canceled removal inspection published a snapshot or changed the disk index.");
    }
    private static void NestedRenames() {
        using Fixture fixture = new(); string folder = fixture.Folder("nested"); Context context = EnvironmentFor();
        string inner = Mod(fixture.Folder("payload"), "inner.jar", "library");
        string outer = Jar(folder, "outer.jar", ("fabric.mod.json", Json(Fabric("outer", new JObject {
            ["jars"] = new JArray(new JObject { ["file"] = "META-INF/jars/library.jar" }) }))),
            ("META-INF/jars/library.jar", File.ReadAllBytes(inner)));
        string user = Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["library"] = "*" } });
        Check(!Local.Inspect(folder, context).HasErrors, "Nested fixture has no active provider.");
        using FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None);
        File.Move(outer, outer + ".disabled"); Report disabled = Local.Inspect(folder, context);
        Check(disabled.HasErrors && disabled.IndexArchivesRead == 0 && disabled.Mods.Where(value => value.Id != "user").All(value => !value.Enabled && value.RootPath == outer + ".disabled"),
            "Disabling an outer file left an active or stale-path nested provider.");
        File.Move(outer + ".disabled", outer); Report enabled = Local.Inspect(folder, context);
        Check(!enabled.HasErrors && enabled.IndexArchivesRead == 0, "Reenabling the outer file lost its selected nested provider.");
        File.Move(outer, outer + ".disabled"); File.Copy(outer + ".disabled", outer + ".old");
        Report ambiguous = Local.Inspect(folder, context);
        Check(ambiguous.IndexArchivesRead == 2 && ambiguous.IndexArchivesReused == 1 && ambiguous.HasErrors &&
            ambiguous.Mods.Where(value => value.Id == "library").Select(value => value.RootPath).Distinct().Count() == 2,
            "Ambiguous suffix names were treated as one cached archive or reread an unchanged consumer.");
    }
    private static void SnapshotCompatibility() {
        using Fixture fixture = new(); string folder = fixture.Folder("compatibility"); Context context = EnvironmentFor();
        string core = Mod(folder, "core.jar", "core"); Mod(folder, "user.jar", "user");
        Report first = Local.Inspect(folder, context); string index = first.IndexFile!;
        JObject old = JObject.Parse(File.ReadAllText(index)); old.Remove("Archives");
        File.WriteAllText(index, old.ToString()); Local.Index.ClearMemory();
        Check(Local.Inspect(folder, context).IndexHit, "An old tree without per-file snapshots became unreadable.");
        File.Move(core, core + ".disabled"); Report upgraded = Local.Inspect(folder, context);
        Check(upgraded.IndexArchivesRead == 2 && upgraded.Mods.Count == 2, "Old cache did not gain snapshots on the next inventory change.");
        JObject corrupt = JObject.Parse(File.ReadAllText(index)); corrupt["Archives"] = new JArray(JValue.CreateNull());
        File.WriteAllText(index, corrupt.ToString()); Local.Index.ClearMemory();
        File.Move(core + ".disabled", core); Report repaired = Local.Inspect(folder, context);
        Check(repaired.IndexArchivesRead == 2 && !repaired.IndexReadFailed, "Malformed per-file snapshots were reused or crashed the scan.");
        Report rebuilt = Local.RebuildIndex(folder, context);
        Check(rebuilt.IndexArchivesRead == 2 && rebuilt.IndexArchivesReused == 0, "Explicit rebuild reused metadata instead of forcing fresh declarations.");
        string unknown = Jar(folder, "unknown.jar", ("unrecognised.txt", Encoding.UTF8.GetBytes("no metadata")));
        Check(Local.Inspect(folder, context).MetadataIncomplete, "Unknown metadata did not stay conservative.");
        File.Move(unknown, unknown + ".disabled"); Report partial = Local.Inspect(folder, context);
        Check(partial.IndexArchivesRead == 1 && partial.IndexArchivesReused == 2 && !partial.MetadataIncomplete,
            "Changing the enabled state of incomplete metadata bypassed its fresh reliability check.");
        File.Move(unknown + ".disabled", unknown); Report active = Local.Inspect(folder, context);
        Check(active.IndexArchivesRead == 1 && active.MetadataIncomplete,
            "Reenabling an unknown file reused the disabled scan's suppressed diagnostics.");
    }
    private static void InvalidationAndFailures() {
        using Fixture fixture = new(); string folder = fixture.Folder("mods"); Context context = EnvironmentFor();
        string core = Mod(folder, "core.jar", "core"); Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["core"] = "*" } });
        Report initial = Local.Inspect(folder, context); File.Move(core, core + ".disabled"); Report disabled = Local.Inspect(folder, context);
        Check(!disabled.IndexHit && disabled.HasErrors, "Disabling an actual provider reused a stale tree."); File.Move(core + ".disabled", core);
        Report enabled = Local.Inspect(folder, context); Check(!enabled.IndexHit && !enabled.HasErrors, "Re-enable did not invalidate the tree.");
        string extra = Mod(folder, "extra.jar", "extra"); Check(Local.Inspect(folder, context).Mods.Count == 3, "Addition was not observed."); File.Delete(extra); Check(Local.Inspect(folder, context).Mods.Count == 2, "Deletion was not observed.");
        string index = Local.Index.IndexPath(folder, context); File.WriteAllText(index, "{broken index"); Local.Index.ClearMemory(); Check(!Local.Inspect(folder, context).IndexHit, "Corrupt JSON index was trusted.");
        JObject broken = JObject.Parse(File.ReadAllText(index)); broken["Tree"]!["Mods"] = JValue.CreateNull(); File.WriteAllText(index, broken.ToString()); Local.Index.ClearMemory();
        Report repaired = Local.Inspect(folder, context); Check(!repaired.IndexHit && repaired.Mods.Count == 2, "Structurally invalid graph was trusted.");
        byte[] committed = File.ReadAllBytes(index); using (CancellationTokenSource canceled = new()) { canceled.Cancel(); bool observed = false; try { Local.Inspect(folder, context, canceled.Token); } catch (OperationCanceledException) { observed = true; } Check(observed && File.ReadAllBytes(index).SequenceEqual(committed), "Canceled inspection published a snapshot."); }
        Check(!Directory.EnumerateFiles(Local.Index.CacheDirectory, "*.tmp").Any(), "Index transaction left temporary files.");
        string firstLocked = fixture.Folder("first-locked"); string lockedJar = Mod(firstLocked, "locked.jar", "locked");
        using (FileStream locked = new(lockedJar, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Report partial = Local.Inspect(firstLocked, context); Check(partial.IndexReadFailed && !partial.IndexPersisted, "A transient archive read failure was published as a complete index.");
        }
        Report recovered = Local.Inspect(firstLocked, context); Check(!recovered.IndexHit && recovered.IndexPersisted && recovered.Mods.Count == 1, "Releasing a previously locked archive did not recover the failed index.");
        string blocked = Path.Combine(fixture.Root, "cache-file"); File.WriteAllText(blocked, "fixture"); Local.Index.CacheDirectory = blocked;
        Report memoryOnly = Local.Inspect(folder, context); Check(!memoryOnly.IndexPersisted && !memoryOnly.HasErrors, "Unavailable disk cache blocked local checking or claimed persistence.");
        using FileStream lease = new(core, FileMode.Open, FileAccess.Read, FileShare.None); Report memoryHit = Local.Inspect(folder, context); Check(memoryHit.IndexHit && !memoryHit.IndexPersisted, "Memory fallback reopened archives or falsely claimed persistence.");
    }
    // Same genuine RuntimeVisibleAnnotations fixture as pinned LF's Windows regression.
    private static byte[] LegacyClass(params (string Name, string Value)[] values) {
        List<string> pool = ["Lnet/minecraftforge/fml/common/Mod;", "RuntimeVisibleAnnotations", "fixture/Mod", "java/lang/Object"];
        foreach (var value in values) { pool.Add(value.Name); pool.Add(value.Value); }
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream); void U2(int value) { writer.Write((byte)((value >> 8) & 255)); writer.Write((byte)(value & 255)); } void U4(long value) { U2((int)((value >> 16) & 65535)); U2((int)(value & 65535)); }
        U4(0xcafebabe); U2(0); U2(52); U2(pool.Count + 3); foreach (string text in pool) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write((byte)1); U2(bytes.Length); writer.Write(bytes); }
        writer.Write((byte)7); U2(3); writer.Write((byte)7); U2(4); U2(33); U2(pool.Count + 1); U2(pool.Count + 2); U2(0); U2(0); U2(0); U2(1); U2(2); U4(6 + 5 * values.Length); U2(1); U2(1); U2(values.Length);
        for (int i = 0; i < values.Length; i++) { U2(5 + 2 * i); writer.Write((byte)'s'); U2(6 + 2 * i); } writer.Flush(); return stream.ToArray();
    }
    private static void LegacyAndLoaderRules() {
        using Fixture fixture = new(); string folder = fixture.Folder("legacy"); Context context = EnvironmentFor("forge", "14.23.5.2855", "1.12.2");
        byte[] annotation = LegacyClass(("modid", "real_mod"), ("name", "Real Mod"), ("version", "1"), ("dependencies", "required-after:forge@[14.21.1.2443,)"));
        string jar = Jar(folder, "actual.jar", ("fixture/Mod.class", annotation), ("mcmod.info", Encoding.UTF8.GetBytes("[{\"modid\":\"stale_template\",\"version\":\"${version}\"}]")));
        Report first = Local.Inspect(folder, context); Check(first.Mods.Count == 1 && first.Mods[0].Id == "real_mod" && !first.HasErrors, "Legacy loader did not use the actual class annotation.");
        Report foreign = Local.Inspect(folder, EnvironmentFor()); Check(foreign.Issues.Any(value => value.KindValue == ModDependencyIssueKind.LoaderMismatch && value.IsError), "Enabled legacy archive was silently accepted by Fabric.");
        Check(Local.LoaderWarningFor(folder, jar, EnvironmentFor()) != "", "Inspected mismatch did not produce a current file marker.");
        DateTime modified = File.GetLastWriteTimeUtc(jar); File.SetLastWriteTimeUtc(jar, modified.AddSeconds(1)); Check(Local.LoaderWarningFor(folder, jar, EnvironmentFor()) == "", "A stale content marker remained after file state changed.");
        string swap = Jar(fixture.Folder("replacement"), "actual.jar", ("fixture/Mod.class", LegacyClass(("modid", "real_mod"), ("name", "Real Mod"), ("version", "2"), ("dependencies", "required-after:forge@[14.21.1.2443,)"))), ("mcmod.info", Encoding.UTF8.GetBytes("[{\"modid\":\"stale_template\",\"version\":\"${version}\"}]")));
        FileInfo info = new(jar); DateTime created = info.CreationTimeUtc, written = info.LastWriteTimeUtc; Check(info.Length == new FileInfo(swap).Length, "Force-refresh fixture must keep the same inventory size."); File.WriteAllBytes(jar, File.ReadAllBytes(swap)); File.SetCreationTimeUtc(jar, created); File.SetLastWriteTimeUtc(jar, written);
        Report forced = Local.RebuildIndex(folder, context); Check(forced.Mods.Single().Version == "2", "Manual rebuild retained the old legacy class cache.");
        string schema = fixture.Folder("schema0"); JObject oldSchema = new() { ["id"] = "schema_zero", ["version"] = "1", ["requires"] = new JObject { ["minecraft"] = "1.20.1" }, ["recommends"] = new JObject { ["absent_recommendation"] = "*" } }; Jar(schema, "schema0.jar", ("fabric.mod.json", Json(oldSchema)));
        Report old = Local.Inspect(schema, EnvironmentFor()); Check(!old.MetadataIncomplete && !old.HasErrors && old.Mods.Single().Dependencies.Count > 0, "Fabric schema 0 declarations were not parsed reliably.");
        Check(Local.MatchVersion("0.9.0", "^0.2.1") == ModDependencyMatchState.Satisfied && Local.MatchVersion("1.1.0", "~1") == ModDependencyMatchState.Unsatisfied, "Pinned LF Fabric caret/tilde semantics changed.");
    }
    private static void RefreshAndIdentity() {
        using Fixture fixture = new(); string folder = fixture.Folder("online"); Context context = EnvironmentFor(); string api = Mod(folder, "api.jar", "api");
        string user = Mod(folder, "user.jar", "user", new JObject { ["depends"] = new JObject { ["api"] = ">=1" } }); string disabled = Mod(folder, "disabled.jar.disabled", "disabled");
        Dictionary<string, string> projects = new() { [api] = "apiProject", [user] = "userProject", [disabled] = "disabledProject" }; int requests = 0; List<(int Current, int Total)> progress = new();
        JToken Request(string url, HttpMethod method, string? body, CancellationToken token) {
            requests++; token.ThrowIfCancellationRequested();
            if (url.EndsWith("/version_files", StringComparison.Ordinal)) { JObject response = new(); foreach (var pair in projects) { string sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(pair.Key))).ToLowerInvariant(); JArray deps = pair.Key == user ? new JArray(new JObject { ["project_id"] = "apiProject", ["version_id"] = "apiOther", ["dependency_type"] = "required" }) : new(); response[sha1] = new JObject { ["id"] = pair.Value + "Installed", ["project_id"] = pair.Value, ["version_number"] = "1.0", ["files"] = new JArray(new JObject { ["hashes"] = new JObject { ["sha1"] = sha1 }, ["size"] = new FileInfo(pair.Key).Length }), ["dependencies"] = deps }; } return response; }
            if (url.Contains("/fingerprints/", StringComparison.Ordinal)) return new JObject { ["data"] = new JObject { ["exactMatches"] = new JArray() } };
            if (url.Contains("/versions?ids=", StringComparison.Ordinal)) return new JArray(new JObject { ["id"] = "apiOther", ["project_id"] = "apiProject", ["version_number"] = "2.0" });
            if (url.Contains("/projects?ids=", StringComparison.Ordinal)) return new JArray(projects.Values.Select(id => new JObject { ["id"] = id, ["project_type"] = "mod", ["title"] = id + " name" }));
            throw new InvalidOperationException("Unexpected controlled request: " + url);
        }
        Online engine = new(new ModDependencyOnlinePipelineTests.Cache(), Request); ModDependencyOnlinePipelineTests.Options options = new() { Progress = (current, total) => progress.Add((current, total)) };
        Report report = engine.RebuildWithOnline(folder, context, default, options); Check(!report.OnlineIncomplete && report.OnlineFiles.Count == 3 && report.OnlineFiles.All(value => value.IsSupplementTarget), "Manual refresh failed to cover enabled and disabled archives.");
        Check(progress.Count == 3 && progress[^1] == (3, 3) && report.OnlineIndexScope == "all-files", "Manual refresh did not publish actual file progress and its full-file scope.");
        var requirement = report.OnlineFiles.Single(value => value.FullPath == user).Matches.Single().Dependencies.Single(); Check(requirement.ProjectPaths.SequenceEqual([api]) && requirement.MatchedPaths.Count == 0 && requirement.VersionName == "2.0", "Project identity was conflated with a different pinned platform version.");
        Check(Local.RuntimeProvidersForPlatformDependency(report, user, requirement).Single().Id == "api" && !report.HasErrors, "Reliable local provider was replaced by a guessed online version.");
        int before = requests; engine.ResetIndexLookups(); Local.Index.ClearMemory(); Report cached = Local.Inspect(folder, context); engine.Enrich(cached, folder, default, options);
        Check(cached.IndexHit && cached.OnlineRequests == 0 && requests == before && cached.OnlineFiles.Count == 3, "Persisted full-file online tree made unnecessary requests after cache reset.");
        var savedRequirement = cached.OnlineFiles.Single(value => value.FullPath == user).Matches.Single().Dependencies.Single();
        cached.OnlineFiles.Single(value => value.FullPath == api).Matches.Single().ProjectId = "UnrelatedProjectWithTheSameTitle";
        Online.MapRelations(cached); Check(savedRequirement.ProjectPaths.Count == 0 && savedRequirement.MatchedPaths.Count == 0, "The original remapping hook inferred project identity from a shared title.");
        using FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None);
        File.Move(api, api + ".disabled"); Report renamed = Local.Inspect(folder, context);
        Check(!renamed.IndexReadFailed && renamed.IndexArchivesRead == 0 && renamed.IndexArchivesReused == 3,
            "Saving online supplements broke per-file snapshot identity and caused a full archive rescan.");
    }
}
