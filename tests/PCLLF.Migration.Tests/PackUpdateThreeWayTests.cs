using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;

// Real packages, loader metadata and transaction engine; the host substitutes only
// network transport and owned test paths. Every fixture stays below its own root.
internal static class PackUpdateThreeWayTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("pack update shared NeoForge dependencies do not block AE2 or Applied Flux upgrades", SharedDependencies);
        yield return ("pack update Lithium pure rename follows the author hash algorithm and removes the actual old path", PureRename);
        yield return ("pack update default preservation and explicit author selection handle a player replacement", ModifiedVersionAndChoice);
        yield return ("pack update merges configuration keys and keeps the original author baseline for the next update", ConfigurationAcrossUpdates);
        yield return ("pack update can recover a verified old configuration and refuses stale choices", ConfigurationBaselineRecovery);
        yield return ("pack update preserves TOML and properties layout and rejects ambiguous configuration syntax", ConfigurationFormats);
        yield return ("pack update personal additions are visible, retained and excluded from author ownership", PersonalAdditions);
        yield return ("pack update disabled and deleted author files stay disabled or deleted", DisabledAndDeleted);
        yield return ("pack update duplicate author hash candidates and copied files need confirmation", AmbiguousCopies);
        yield return ("pack update one physical jar cannot satisfy two old author declarations with the same hash", SharedAuthorHash);
        yield return ("pack update two changed disabled variants are ambiguous and cannot be chosen automatically", AmbiguousDisabledVariants);
        yield return ("pack update explicit author selection upgrades disabled bytes without enabling the mod", UpgradeDisabledSelection);
        yield return ("pack update asks disabled author state independently and protects occupied enable destinations", DisabledEnableChoice);
        yield return ("pack update recognizes tracked local content already equal to the new author bytes", ConvergedAuthorContent);
        yield return ("pack update personal own-id conflicts block while alias ambiguity needs explicit confirmation without deleting additions", PersonalModAndAliasCollisions);
        yield return ("pack update no-project overrides use old and new loader contexts when associating an upgrade", LoaderContextTransition);
        yield return ("pack update multiple old or new files from one platform project are not paired arbitrarily", AmbiguousProjectVersions);
        yield return ("pack update author removals delete unchanged files and retain personal modifications", AuthorRemoval);
        yield return ("pack update absent optional selections survive recording and two updates", OptionalSelections);
        yield return ("pack update first recording preserves missing, modified and disabled author declarations", RecordAuthorDeclarations);
        yield return ("pack update hashes retain their algorithm and project identity is distinct from file version", HashAndIdentityContracts);
        yield return ("pack update retains project identities for historical Modrinth version-name URLs", LegacyVersionLinks);
        yield return ("pack update incomplete schema one records require a warning or the correct old package", LegacyBaseline);
        yield return ("pack update unknown metadata cannot alone establish a duplicate mod", UnknownMetadata);
        yield return ("pack update download failure, cancellation and corrupted payloads leave the instance intact", DownloadFailures);
        yield return ("pack update commit failure and cancellation roll back renamed files and the manifest", CommitRollback);
        yield return ("pack update rollback preserves external writes and leaves incomplete recovery pending", RollbackExternalWrites);
        yield return ("pack update rejects changed renamed files and new mods after preview", StalePreview);
        yield return ("pack update personal additions occupying an author target retain their provenance across updates", PersonalTargetAcrossUpdates);
        yield return ("pack update stages only changed selectable author files and skips unavailable unchanged or optional URLs", SelectiveDownloads);
        yield return ("pack update incoming conflicts and cascading retained-mod conflicts are visible before preview", IncomingAndCascadingConflicts);
        yield return ("pack update canceled planning and hashing preserve the instance and throw cancellation", PlanningCancellation);
    }

    private static void ConfigurationAcrossUpdates() {
        using Context f = new();
        const string path = "config/example.json";
        Spec old = new(path, Encoding.UTF8.GetBytes("""{"hud":{"scale":1,"show":true},"cap":3,"stale":1,"rate":1e-120}"""));
        var original = f.Package([old], overrides: true);
        f.Install(old);
        f.Engine.RecordInstalled(f.LastArchive!, f.Game);
        JObject baseline = f.Engine.ReadManifest(f.Game)!;
        Require(baseline["files"]![path]!["originalConfiguration"]?.Value<string>() == Convert.ToBase64String(old.Bytes), "Archive author bytes were not stored at install.");
        f.Text(path, """{"hud":{"scale":2,"show":true},"cap":4,"stale":1,"rate":1e-120,"playerOnly":true}""");
        Spec next = old with { Bytes = Encoding.UTF8.GetBytes("""{"hud":{"scale":1,"show":false},"cap":5,"rate":1e-120,"new":null}""") };
        Plan plan = f.Engine.Plan(f.Game, baseline, f.Package([next], overrides: true));
        f.Stage(plan);
        Change change = Incoming(plan, path);
        Require(change.ApplyAuthor && change.ConfigurationMerge?.Conflicts.Select(value => value.Key).SequenceEqual(["/cap"]) == true,
            "Only the same-key edit should need a choice.");
        TweakChoice();
        f.Commit(plan);
        var current = JObject.Parse(File.ReadAllText(f.Path(path)));
        Require(current["hud"]!["scale"]!.Value<int>() == 2 && !current["hud"]!["show"]!.Value<bool>() && current["cap"]!.Value<int>() == 5 &&
            current["stale"] is null && current["new"]?.Type == JTokenType.Null && current["playerOnly"]!.Value<bool>() &&
            File.ReadAllText(f.Path(path)).Contains("1e-120"), "The file lost a personal edit, an author change, a deletion or numeric precision.");
        JObject manifest = f.Engine.ReadManifest(f.Game)!;
        Require(manifest["files"]![path]!["originalConfiguration"]?.Value<string>() == Convert.ToBase64String(next.Bytes) &&
            manifest["files"]![path]!["local"]!["hash"]?.Value<string>() == Digest(f.Path(path)),
            "Update confused the author configuration and the installed merge result.");
        Spec third = next with { Bytes = Encoding.UTF8.GetBytes("""{"hud":{"scale":3,"show":true},"cap":5,"rate":1e-120,"new":null}""") };
        Plan second = f.Engine.Plan(f.Game, manifest, f.Package([third], overrides: true));
        f.Stage(second);
        Require(Incoming(second, path).ConfigurationMerge?.Conflicts.Single().Key == "/hud/scale", "The second update did not use the last author version as its base.");
        f.Commit(second);
        current = JObject.Parse(File.ReadAllText(f.Path(path)));
        Require(current["hud"]!["scale"]!.Value<int>() == 2 && current["hud"]!["show"]!.Value<bool>(), "Default selection lost a personal value on a subsequent update.");

        void TweakChoice() {
            change.AuthorConfigurationKeys!.Add("/cap");
            var snapshot = f.Engine.SnapshotForPreview(plan);
            Incoming(snapshot, path).AuthorConfigurationKeys!.Clear();
            Require(change.AuthorConfigurationKeys.Contains("/cap"), "Background preview mutated live configuration choices.");
        }
    }

    private static void ConfigurationBaselineRecovery() {
        using Context f = new();
        const string path = "config/example.properties";
        Spec old = new(path, Encoding.UTF8.GetBytes("speed=1\nmode=easy\n"));
        Spec next = old with { Bytes = Encoding.UTF8.GetBytes("speed=1\nmode=hard\n") };
        var original = f.Package([old], overrides: true);
        JObject baseline = Manifest(original);
        baseline["files"]![path]!["originalConfiguration"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("speed=2\nmode=easy\n"));
        f.Text(path, "speed=2\nmode=easy\n");
        var plan = f.Engine.Plan(f.Game, baseline, f.Package([next], overrides: true));
        f.Stage(plan);
        Require(Incoming(plan, path).ConfigurationMerge is null && f.Engine.NeedsOriginalConfigurations(plan, CancellationToken.None),
            "An invalid cached original was treated as author evidence.");
        PackUpdateEngine<PackFile, Package, Change, Plan, Download>.RecoverOriginalConfigurations(plan, original, CancellationToken.None);
        f.Engine.PrepareConfigurationMerges(plan, f.StagePath(), CancellationToken.None);
        Require(Incoming(plan, path).ConfigurationMerge?.Conflicts.Count == 0, "The verified archive original was not used.");
        f.Text(path, "speed=6\nmode=easy\n");
        Throws<IOException>(() => f.Commit(plan));
        Require(File.ReadAllText(f.Path(path)) == "speed=6\nmode=easy\n", "A stale configuration preview overwrote an external edit.");
    }

    private static void ConfigurationFormats() {
        byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
        var toml = PackConfigMerge.TryCreate("config/settings.toml",
            Text("[client]\r\n\"显示 HUD\" = true\r\nrange = [1, 2]\r\n"),
            Text("[client]\r\n\"显示 HUD\" = false\r\nrange = [1, 2]\r\n[player]\r\nlimit = 8\r\n"),
            Text("# incoming\r\n[client]\r\n\"显示 HUD\" = true # keep comment\r\nrange = [3, 4]\r\n"));
        Require(toml?.Conflicts.Count == 0, "The valid UTF-8 TOML configuration was not merged.");
        string result = Encoding.UTF8.GetString(toml!.Render(new()));
        Require(result == "# incoming\r\n[client]\r\n\"显示 HUD\" = false # keep comment\r\nrange = [3, 4]\r\n[player]\r\nlimit = 8\r\n",
            "A TOML value, table, newline or author comment was lost.");
        var properties = PackConfigMerge.TryCreate("defaultconfigs/mod.properties", Text("color=blue\nn=4\n"),
            Text("color=green\nn=4\n"), Text("# incoming\ncolor=red\nn:7\n"));
        Require(properties?.Conflicts.Single().Key == "/color" &&
            Encoding.UTF8.GetString(properties.Render(new())) == "# incoming\ncolor=green\nn:7\n", "Properties merge did not retain the author layout and personal value.");
        Require(PackConfigMerge.TryCreate("config/invalid.json", Text("""{"n":1}"""), Text("""{"n":1,"n":2}"""), Text("""{"n":3}""")) is null &&
            PackConfigMerge.TryCreate("config/invalid.toml", Text("foo = 1"), Text("foo = 2"), Text("[foo]\nbar = 3")) is null,
            "An ambiguous configuration should stay in the file-level preview.");
    }

    private static void RollbackExternalWrites() {
        foreach (bool existed in new[] { false, true }) {
            using Context f = new();
            string first = f.Path("config/first.txt"), second = f.Path("config/second.txt");
            if (existed) f.Text("config/first.txt", "OLD_AUTHOR");
            f.Text("config/second.txt", "SECOND_OLD");
            string stage = f.StagePath(), firstPayload = System.IO.Path.Combine(stage, "first"), secondPayload = System.IO.Path.Combine(stage, "second");
            File.WriteAllText(firstPayload, "NEW_AUTHOR"); File.WriteAllText(secondPayload, "SECOND_NEW");
            var operations = new List<Tuple<string, string?, string?>> {
                Tuple.Create(first, (string?)firstPayload, (string?)Digest(first)),
                Tuple.Create(second, (string?)secondPayload, (string?)Digest(second))
            };
            IOException? error = null;
            try { f.Engine.ApplyTransaction(f.Game, f.Game, operations, CancellationToken.None, index => {
                if (index == 1) { File.WriteAllText(first, "EXTERNAL_PERSONAL_EDIT"); throw new IOException("injected"); }
            }); } catch (IOException failure) { error = failure; }
            Require(error?.Message.Contains("回滚未完成", StringComparison.Ordinal) == true, "Incomplete rollback was reported as ordinary failure.");
            Require(File.ReadAllText(first) == "EXTERNAL_PERSONAL_EDIT" && File.ReadAllText(second) == "SECOND_OLD", "Rollback overwrote external data or changed an unapplied file.");
            string journal = f.Engine.Pending(f.Game).Single(); JObject log = JObject.Parse(File.ReadAllText(journal));
            Require(log["complete"]!.Value<bool>() == false && log["rolledBack"]!.Value<bool>() == false && log["rollbackIncomplete"]!.Value<bool>(), "Incomplete rollback was falsely marked complete.");
            if (existed) Require(File.ReadAllText(log["files"]![0]!["backup"]!.ToString()) == "OLD_AUTHOR", "Original backup was not retained.");
            Throws<IOException>(() => f.Engine.Recover(f.Game, f.Game));
            Require(File.ReadAllText(first) == "EXTERNAL_PERSONAL_EDIT", "Recovery overwrote unresolved external content.");
        }
    }
    private static void DisabledEnableChoice() {
        foreach (bool occupied in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/old.jar", Fabric("example", "1"), "project");
            Spec next = old with { Relative = "mods/new.jar", Bytes = Fabric("example", "2") };
            var baseline = f.Package([old]); f.Install(old);
            File.Move(f.Path(old.Relative), f.Path(old.Relative + ".disabled"));
            if (occupied) f.Install(next with { Bytes = Fabric("foreign", "personal") });
            Plan plan = f.Plan(baseline, f.Package([next])); f.Stage(plan);
            Change change = Incoming(plan, next.Relative);
            Require(change.ApplyAuthor && change.KeepDisabled, "Disabled author version should update with disabled selected by default.");
            change.KeepDisabled = false;
            if (occupied) {
                Throws<IOException>(() => f.Commit(plan));
                Require(File.Exists(f.Path(old.Relative + ".disabled")), "Rejected enable removed the previous disabled version.");
            } else {
                f.Commit(plan); f.Has(next); f.Absent(old.Relative + ".disabled"); f.Absent(next.Relative + ".disabled");
                Require(f.Engine.ReadManifest(f.Game)!["files"]![next.Relative]!["state"]!.ToString() == "installed", "Explicit enable did not record the actual enabled state.");
            }
        }
    }
    private static void ConvergedAuthorContent() {
        using Context f = new();
        Spec old = new("mods/same.jar", Fabric("same", "1"), "project");
        Spec incoming = old with { Bytes = Fabric("same", "2") };
        var baseline = f.Package([old]); f.Install(incoming);
        Plan plan = f.Plan(baseline, f.Package([incoming])); f.Stage(plan);
        Require(!plan.Changes.Any(change => change.Relative == incoming.Relative), "Tracked local bytes already matching N were called a conflict or unnecessarily rewritten.");
        f.Commit(plan); f.Has(incoming);
        Require(f.Engine.ReadManifest(f.Game)!["files"]![incoming.Relative]!["state"]!.ToString() == "installed", "Converged content was not recorded as the author version.");
    }
    private static void LegacyVersionLinks() {
        using Context f = new();
        Spec mod = new("mods/legacy.jar", Fabric("legacy", "1"), "oldproject");
        foreach (string segment in new[] { "1.1.1+1.17", "1.1.1%2B1.17", "OldId123" }) {
            f.Package([mod]);
            using (ZipArchive zip = ZipFile.Open(f.LastArchive!, ZipArchiveMode.Update)) {
                ZipArchiveEntry entry = zip.GetEntry("modrinth.index.json")!;
                JObject json;
                using (StreamReader input = new(entry.Open())) json = JObject.Parse(input.ReadToEnd());
                json["files"]![0]!["downloads"] = new JArray("https://cdn.modrinth.com/data/oldproject/versions/" + segment + "/legacy.jar");
                entry.Delete();
                using StreamWriter output = new(zip.CreateEntry("modrinth.index.json").Open()); output.Write(json.ToString());
            }
            var declared = f.Engine.ReadPackage(f.LastArchive!, f.StagePath(), CancellationToken.None).Files[mod.Relative];
            Require(declared.Identity == "modrinth:oldproject", "A historical version-name URL lost its independent project identity.");
            Require(declared.FileIdentity == (segment == "OldId123" ? "modrinth:OldId123" : null), "A version-name URL fabricated an exact platform file identity.");
        }
    }
    private static void SharedDependencies() {
        using Context f = new();
        Spec ae2 = new("mods/appliedenergistics2-19.2.17.jar", Neo("ae2", "19.2.17"), "ae2project");
        Spec flux = new("mods/AppliedFlux-1.21-1.5-neoforge.jar", Neo("appliedflux", "1.5"), "fluxproject");
        Spec decay = new("mods/accelerated-decay-neoforge-21.0.0.jar", Neo("accelerated_decay", "21.0.0"), "decayproject");
        var original = f.Package([ae2, flux, decay], ModLoaders.NeoForge, "21.1.264", "1.21.1"); f.Install(ae2, flux, decay);
        Spec newAe2 = ae2 with { Relative = "mods/appliedenergistics2-19.2.18.jar", Bytes = Neo("ae2", "19.2.18") };
        Spec newFlux = flux with { Relative = "mods/AppliedFlux-1.21-1.7-neoforge.jar", Bytes = Neo("appliedflux", "1.7") };
        var incoming = f.Package([newAe2, newFlux, decay], ModLoaders.NeoForge, "21.1.264", "1.21.1");
        Plan plan = f.Plan(original, incoming); f.Stage(plan);
        foreach (string path in new[] { newAe2.Relative, newFlux.Relative }) {
            Change change = Incoming(plan, path);
            Require(!change.Conflict && change.ApplyAuthor, "Shared minecraft/neoforge dependency was treated as a mod collision: " + change.Reason);
        }
        Require(!plan.Changes.Any(c => c.Relative == decay.Relative && c.Ownership == PackUpdateOwnership.PersonalAdded), "An unchanged author jar was described as a personal addition.");
        var graph = f.Engine.CheckDependencies(plan);
        Require(!graph.HasErrors && graph.HasUnknown && graph.Warnings.Any(text => text.Contains("javafml")), "NeoForge language-loader uncertainty must be explicit, not a duplicate-mod error or assumed compatibility.");
        Throws<IOException>(() => f.Commit(plan));
        f.Has(ae2); f.Has(flux); f.Has(decay); f.Absent(newAe2.Relative); f.Absent(newFlux.Relative);
        plan.AllowUnverifiedDependencies = true;
        f.Commit(plan);
        f.Has(newAe2); f.Has(newFlux); f.Has(decay); f.Absent(ae2.Relative); f.Absent(flux.Relative);
        Require(Directory.GetFiles(f.Path("mods"), "*.jar").Length == 3, "Upgrade left duplicate old versions.");
    }

    private static void PureRename() {
        foreach (string algorithm in new[] { "sha1", "md5", "sha256", "sha512" }) {
            using Context f = new();
            Spec old = new("mods/lithium-fabric-0.21.4+mc1.21.11.jar", Fabric("lithium", "0.21.4"), "lithiumproject");
            Spec next = old with { Relative = "mods/lithium-fabric-0.24.2+mc26.1.2.jar", Bytes = Fabric("lithium", "0.24.2") };
            var original = f.Package([old]); f.Install(old);
            original.Files[old.Relative].Hash = Digest(f.Path(old.Relative), algorithm);
            string renamed = "mods/[锂] lithium-fabric-0.21.4+mc1.21.11.jar";
            File.Move(f.Path(old.Relative), f.Path(renamed));
            Require(!File.Exists(f.Path(old.Relative)), "The pure-rename fixture accidentally contains two old jars.");
            var incoming = f.Package([next], minecraft: "26.1.2");
            Plan plan = f.Plan(original, incoming); f.Stage(plan);
            Change update = Incoming(plan, next.Relative);
            Require(update.LocalRelative == renamed && update.BaselineRelative == old.Relative, "The author origin was not linked to the actual renamed file using " + algorithm + ".");
            Require(!update.Conflict && update.ApplyAuthor, "A byte-identical pure rename was treated as a player version replacement.");
            f.Commit(plan); f.Has(next); f.Absent(old.Relative); f.Absent(renamed);
        }
    }

    private static void ModifiedVersionAndChoice() {
        foreach (bool useAuthor in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/lithium-old.jar", Fabric("lithium", "1.0"), "lithiumproject");
            Spec personal = old with { Bytes = Fabric("lithium", "player-1.1") };
            Spec next = old with { Relative = "mods/lithium-new.jar", Bytes = Fabric("lithium", "2.0") };
            var original = f.Package([old]); f.Install(personal);
            var package = f.Package([next]); Plan plan = f.Plan(original, package); f.Stage(plan);
            Change change = Incoming(plan, next.Relative);
            Require(change.PersonalCompatible && !change.Conflict && change.CanChoose, "A usable personal modification must be retained with an independent author-version choice.");
            Require(!change.ApplyAuthor, "Personal replacement was not preserved by default.");
            if (useAuthor) change.Choice = PackUpdateChoice.UseAuthor;
            f.Commit(plan);
            JObject manifest = f.Engine.ReadManifest(f.Game) ?? throw new Exception("Committed author record missing.");
            Require(manifest["files"]?[next.Relative]?["hash"]?.ToString() == package.Files[next.Relative].Hash, "Expected author content was replaced with a player's local hash.");
            if (useAuthor) { f.Has(next); f.Absent(old.Relative); }
            else {
                f.Has(personal); f.Absent(next.Relative);
                Spec third = next with { Relative = "mods/lithium-third.jar", Bytes = Fabric("lithium", "3.0") };
                Plan repeated = f.Engine.Plan(f.Game, manifest, f.Package([third])); f.Stage(repeated);
                Require(Incoming(repeated, third.Relative).PersonalCompatible && !Incoming(repeated, third.Relative).ApplyAuthor, "The second update forgot a usable retained personal replacement.");
                f.Commit(repeated); f.Has(personal); f.Absent(third.Relative);
            }
        }
    }

    private static void PersonalAdditions() {
        using Context f = new();
        Spec stable = new("mods/stable.jar", Fabric("stable", "1.0"), "stableproject");
        Spec added = new("mods/player-extra.jar", Fabric("player_extra", "1.0"));
        var old = f.Package([stable]); f.Install(stable, added); f.Text("config/generated.toml", "runtime = true");
        Plan plan = f.Plan(old, f.Package([stable])); f.Stage(plan);
        Change row = plan.Changes.Single(c => c.Relative == added.Relative || c.LocalRelative == added.Relative);
        Require(row.Ownership == PackUpdateOwnership.PersonalAdded && row.DisplayOnly && !row.ApplyAuthor, "Player addition must have a retained display row, not a delete operation.");
        Require(!plan.Changes.Any(c => c.Relative == stable.Relative && c.Ownership == PackUpdateOwnership.PersonalAdded), "An unchanged author jar was called personal.");
        f.Commit(plan); f.Has(added); f.Has(stable);
        JObject manifest = f.Engine.ReadManifest(f.Game)!;
        Require(manifest["files"]?[added.Relative] is null && manifest["files"]?["config/generated.toml"] is null, "Local additions or generated configuration became author-owned.");
        Plan repeated = f.Engine.Plan(f.Game, manifest, f.Package([stable])); f.Stage(repeated); f.Commit(repeated);
        f.Has(added);
        Require(f.Engine.ReadManifest(f.Game)!["files"]?[added.Relative] is null, "Second update promoted a personal addition into the author baseline.");
    }

    private static void DisabledAndDeleted() {
        foreach (bool disabled in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/example-old.jar", Fabric("example", "1.0"), "exampleproject");
            Spec next = old with { Relative = "mods/example-new.jar", Bytes = Fabric("example", "2.0") };
            var original = f.Package([old]); f.Install(old);
            if (disabled) File.Move(f.Path(old.Relative), f.Path(old.Relative + ".disabled")); else File.Delete(f.Path(old.Relative));
            Plan plan = f.Plan(original, f.Package([next])); f.Stage(plan);
            Change update = Incoming(plan, next.Relative);
            Require(disabled ? !update.Conflict && update.ApplyAuthor && update.KeepDisabled : update.Conflict && !update.ApplyAuthor,
                "Disabled author content must update without enabling; personal deletion must stay respected.");
            Require(update.Ownership == (disabled ? PackUpdateOwnership.Disabled : PackUpdateOwnership.Deleted), "Disabled/deleted states were conflated with a normal rename.");
            f.Commit(plan); f.Absent(old.Relative); f.Absent(next.Relative);
            if (disabled) {
                Require(!File.Exists(f.Path(old.Relative + ".disabled")) && File.ReadAllBytes(f.Path(next.Relative + ".disabled")).SequenceEqual(next.Bytes), "Disabled author content was not updated to the newer bytes.");
            }
        }
    }

    private static void AmbiguousCopies() {
        foreach (bool originalStillPresent in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/example-old.jar", Fabric("example", "1.0"), "exampleproject");
            Spec next = old with { Relative = "mods/example-new.jar", Bytes = Fabric("example", "2.0") };
            var original = f.Package([old]); f.Install(old);
            string copy = "mods/copy-of-example.jar"; File.Copy(f.Path(old.Relative), f.Path(copy));
            if (!originalStillPresent) File.Move(f.Path(old.Relative), f.Path("mods/[renamed] example.jar"));
            Plan plan = f.Plan(original, f.Package([next])); f.Stage(plan);
            Change update = Incoming(plan, next.Relative);
            Require(update.Conflict && !update.AllowAuthor && !update.ApplyAuthor, "Multiple matching copies must not choose or delete a candidate automatically.");
            Require(plan.Changes.Any(c => c.Ownership == PackUpdateOwnership.Ambiguous), "Duplicate copies were not explicitly reported as ambiguous.");
            f.WriteManifest(Manifest(original));
            byte[] manifestBefore = File.ReadAllBytes(f.ManifestPath);
            var bytesBefore = Directory.GetFiles(f.Path("mods"), "*.jar").ToDictionary(path => path, File.ReadAllBytes);
            Require(f.Engine.CheckDependencies(plan).HasErrors, "Real duplicate active copies were reduced to unknown-only metadata.");
            plan.AllowUnverifiedDependencies = true;
            Throws<IOException>(() => f.Commit(plan));
            Require(File.ReadAllBytes(f.ManifestPath).SequenceEqual(manifestBefore), "Rejecting duplicate active copies changed the author manifest.");
            foreach (var before in bytesBefore) Require(File.ReadAllBytes(before.Key).SequenceEqual(before.Value), "Rejecting duplicate active copies changed local bytes: " + before.Key);
            f.Absent(next.Relative);
            Require(Directory.GetFiles(f.Path("mods"), "*.jar").Length == 2, "An ambiguous hash match deleted one of the user's copies.");
        }
    }

    private static void AuthorRemoval() {
        foreach (bool modified in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/removed.jar", Fabric("removed", "1.0"), "removedproject");
            var original = f.Package([old]); Spec local = modified ? old with { Bytes = Fabric("removed", "personal") } : old;
            f.Install(local); Plan plan = f.Plan(original, f.Package([])); f.Stage(plan); f.Commit(plan);
            if (modified) f.Has(local); else f.Absent(old.Relative);
        }
    }

    private static void SharedAuthorHash() {
        using Context f = new();
        byte[] identical = Fabric("shared_content", "1.0");
        Spec oldA = new("mods/old-a.jar", identical, "projectA");
        Spec oldB = new("mods/old-b.jar", identical, "projectB");
        Spec nextA = oldA with { Relative = "mods/new-a.jar", Bytes = Fabric("module_a", "2.0") };
        Spec nextB = oldB with { Relative = "mods/new-b.jar", Bytes = Fabric("module_b", "2.0") };
        var original = f.Package([oldA, oldB]); f.Install(oldA, oldB);
        File.Delete(f.Path(oldA.Relative));
        Plan plan = f.Plan(original, f.Package([nextA, nextB])); f.Stage(plan);
        Require(plan.Changes.Count(c => c.LocalRelative == oldB.Relative) <= 1, "One physical file was assigned to two distinct old author declarations.");
        foreach (Spec next in new[] { nextA, nextB }) {
            Change change = Incoming(plan, next.Relative);
            Require(change.Ownership == PackUpdateOwnership.Ambiguous && change.Conflict && !change.AllowAuthor && !change.ApplyAuthor, "A shared old author hash selected a physical file automatically.");
            change.Choice = PackUpdateChoice.UseAuthor;
            Throws<IOException>(() => f.Engine.ValidateChoices(plan));
            change.Choice = PackUpdateChoice.PreservePersonal;
        }
        f.Commit(plan); f.Has(oldB); f.Absent(oldA.Relative); f.Absent(nextA.Relative); f.Absent(nextB.Relative);
    }

    private static void AmbiguousDisabledVariants() {
        using Context f = new();
        Spec old = new("mods/example-old.jar", Fabric("example", "1.0"), "exampleproject");
        Spec next = old with { Relative = "mods/example-new.jar", Bytes = Fabric("example", "2.0") };
        var original = f.Package([old]);
        Spec disabled = new(old.Relative + ".disabled", Fabric("example", "player-disabled"));
        Spec oldVariant = new(old.Relative + ".old", Fabric("example", "player-old"));
        f.Install(disabled, oldVariant);
        Plan plan = f.Plan(original, f.Package([next])); f.Stage(plan);
        Change change = Incoming(plan, next.Relative);
        Require(change.Ownership == PackUpdateOwnership.Ambiguous && change.Conflict && !change.AllowAuthor && !change.ApplyAuthor, "Two independently modified disabled variants were collapsed to the first suffix.");
        Require(change.Candidates.Contains(disabled.Relative) && change.Candidates.Contains(oldVariant.Relative), "The preview omitted a disabled ambiguity candidate.");
        change.Choice = PackUpdateChoice.UseAuthor; Throws<IOException>(() => f.Engine.ValidateChoices(plan));
        change.Choice = PackUpdateChoice.PreservePersonal; f.Commit(plan);
        f.Has(disabled); f.Has(oldVariant); f.Absent(old.Relative); f.Absent(next.Relative); f.Absent(next.Relative + ".disabled");
    }

    private static void UpgradeDisabledSelection() {
        using Context f = new();
        Spec old = new("mods/example-old.jar", Fabric("example", "1.0"), "exampleproject");
        Spec next = old with { Relative = "mods/example-new.jar", Bytes = Fabric("example", "2.0") };
        var original = f.Package([old]); f.Install(old);
        File.Move(f.Path(old.Relative), f.Path(old.Relative + ".disabled"));
        Plan plan = f.Plan(original, f.Package([next])); f.Stage(plan);
        Change change = Incoming(plan, next.Relative);
        Require(change.Ownership == PackUpdateOwnership.Disabled && change.AllowAuthor && change.ApplyAuthor && change.KeepDisabled, "The disabled author file did not default to updating while keeping its disabled state.");
        change.Choice = PackUpdateChoice.UseAuthor; f.Commit(plan);
        f.Has(next with { Relative = next.Relative + ".disabled" });
        f.Absent(old.Relative); f.Absent(old.Relative + ".disabled"); f.Absent(next.Relative);
        JObject manifest = f.Engine.ReadManifest(f.Game)!;
        Require(manifest["files"]?[next.Relative]?["state"]?.ToString() == "disabled" &&
            manifest["files"]?[next.Relative]?["local"]?["relative"]?.ToString() == next.Relative + ".disabled", "The author record lost the disabled destination after choosing the author's new bytes.");
    }

    private static void PersonalModAndAliasCollisions() {
        foreach (int kind in new[] { 0, 1, 2 }) {
            using Context f = new();
            string playerId = kind == 1 ? "personal_provider" : "shared_id";
            string authorId = kind == 2 ? "author_provider" : "shared_id";
            Spec extra = new("mods/personal-extra.jar", kind == 1 ? FabricAlias(playerId, "1.0", "shared_id") : Fabric(playerId, "1.0"));
            Spec incoming = new("mods/author-new.jar", kind == 2 ? FabricAlias(authorId, "2.0", "shared_id") : Fabric(authorId, "2.0"), "authorproject");
            var empty = f.Package([]); f.Install(extra);
            Plan plan = f.Plan(empty, f.Package([incoming])); f.Stage(plan);
            Change change = Incoming(plan, incoming.Relative);
            if (kind == 0) {
                Require(!change.Conflict && change.ApplyAuthor && change.LocalRelative == extra.Relative && change.Before is not null &&
                    change.CanChoose && plan.Changes.Count == 1 && change.Operation == PackUpdateOperation.AuthorChange,
                    "A uniquely matched local mod and new author version must be one selectable upgrade, not addition plus disabling.");
                f.Commit(plan); f.Has(incoming); f.Absent(extra.Relative); f.Absent(extra.Relative + ".disabled");
            } else {
                Require(!plan.Changes.Any(c => c.ApplyAuthor && c.LocalRelative == extra.Relative), "An unconfirmed alias provider was automatically disabled.");
                Require(!change.Conflict && change.ApplyAuthor, "A valid provided alias was flattened into a hard duplicate-own-ID conflict.");
                var graph = f.Engine.CheckDependencies(plan);
                Require(!graph.HasErrors && graph.HasUnknown && graph.Warnings.Any(text => text.Contains("shared_id") && text.Contains("提供")), "Alias provider ambiguity was not visible as unverified loader behavior.");
                Throws<IOException>(() => f.Commit(plan)); f.Has(extra); f.Absent(incoming.Relative);
                plan.AllowUnverifiedDependencies = true;
                f.Commit(plan); f.Has(extra); f.Has(incoming);
            }
            JObject manifest = f.Engine.ReadManifest(f.Game)!;
            Require(manifest["files"]?[extra.Relative] is null, "A preserved personal addition became an author-owned file.");
            Require(manifest["files"]?[incoming.Relative]?["state"]?.ToString() == "installed", "The author record did not describe the actual installed curated file.");
        }
    }

    private static void LoaderContextTransition() {
        foreach (bool neoOldMetadata in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/override-old.jar", Forge("same_mod", "1.0", neoOldMetadata ? "neoforge" : "forge"));
            Spec next = new("mods/override-new.jar", Neo("same_mod", "2.0"));
            var original = f.Package([old], neoOldMetadata ? ModLoaders.NeoForge : ModLoaders.Forge, neoOldMetadata ? "20.4.237" : "47.4.0", neoOldMetadata ? "1.20.4" : "1.20.1");
            f.Install(old);
            var package = f.Package([next], ModLoaders.NeoForge, "21.1.264", "1.21.1");
            Require(original.Files[old.Relative].Identity is null && package.Files[next.Relative].Identity is null, "The loader-transition fixture accidentally acquired a platform project identity.");
            Plan plan = f.Plan(original, package); f.Stage(plan);
            Change change = Incoming(plan, next.Relative);
            Require(change.BaselineRelative == old.Relative && change.LocalRelative == old.Relative && !change.Conflict && change.ApplyAuthor, "A proven old author override was not associated using the old loader context.");
            Require(!plan.Changes.Any(c => c.BaselineRelative == old.Relative && c.Incoming is null && !c.DisplayOnly), "Loader transition split the related upgrade into a separate removal and addition.");
            var graph = f.Engine.CheckDependencies(plan);
            Require(!graph.HasErrors && graph.HasUnknown && graph.Warnings.Any(text => text.Contains("javafml")), "NeoForge language-loader uncertainty must be explicit after correctly associating old and new metadata contexts.");
            Throws<IOException>(() => f.Commit(plan)); f.Has(old); f.Absent(next.Relative);
            plan.AllowUnverifiedDependencies = true;
            f.Commit(plan); f.Has(next); f.Absent(old.Relative);
        }
    }

    private static void AmbiguousProjectVersions() {
        foreach (bool multipleOld in new[] { false, true }) {
            using Context f = new();
            Spec oldA = new("mods/old-a.jar", Fabric("old_module_a", "1.0"), "multiproject");
            Spec oldB = new("mods/old-b.jar", Fabric("old_module_b", "1.0"), "multiproject");
            Spec nextA = new("mods/new-a.jar", Fabric("new_module_a", "2.0"), "multiproject");
            Spec nextB = new("mods/new-b.jar", Fabric("new_module_b", "2.0"), "multiproject");
            Spec[] old = multipleOld ? [oldA, oldB] : [oldA];
            Spec[] incoming = multipleOld ? [nextA] : [nextA, nextB];
            var original = f.Package(old); f.Install(old);
            Plan plan = f.Plan(original, f.Package(incoming)); f.Stage(plan);
            Require(plan.Changes.Any(c => c.Ownership == PackUpdateOwnership.Ambiguous), "Multiple files from one project were not reported as ambiguous.");
            Require(plan.Changes.Where(c => c.Incoming is not null).All(c => c.Conflict && !c.AllowAuthor && !c.ApplyAuthor), "A new candidate escaped the ambiguous project association and became an automatic addition.");
            Require(!plan.Changes.Any(c => c.Incoming is null && !c.DisplayOnly && c.ApplyAuthor), "An ambiguous old project candidate became an automatic deletion.");
            foreach (Spec item in incoming) {
                Change change = Incoming(plan, item.Relative); change.Choice = PackUpdateChoice.UseAuthor;
                Throws<IOException>(() => f.Engine.ValidateChoices(plan)); change.Choice = PackUpdateChoice.PreservePersonal;
            }
            f.Commit(plan); foreach (Spec item in old) f.Has(item); foreach (Spec item in incoming) f.Absent(item.Relative);
        }
    }

    private static void OptionalSelections() {
        using Context f = new();
        Spec optional = new("mods/optional-old.jar", Fabric("optional", "1.0"), "optionalproject", true);
        var original = f.Package([optional]);
        string archive = f.LastArchive!;
        f.Engine.RecordInstalled(archive, f.Game);
        JObject baseline = f.Engine.ReadManifest(f.Game)!;
        Require(baseline["files"]?[optional.Relative]?["hash"]?.ToString() == original.Files[optional.Relative].Hash, "Recording dropped a declared but unselected optional file.");
        for (int version = 2; version <= 3; version++) {
            Spec next = optional with { Relative = "mods/optional-" + version + ".jar", Bytes = Fabric("optional", version + ".0") };
            Plan plan = f.Engine.Plan(f.Game, baseline, f.Package([next])); f.Stage(plan);
            Require(!Incoming(plan, next.Relative).ApplyAuthor, "An unselected optional component became enabled during an update.");
            f.Commit(plan); f.Absent(next.Relative); baseline = f.Engine.ReadManifest(f.Game)!;
        }
        Require(!Directory.Exists(f.Path("mods")) || !Directory.EnumerateFiles(f.Path("mods")).Any(), "Optional planning installed a file.");
    }

    private static void LegacyBaseline() {
        using Context f = new();
        Spec old = new("mods/old.jar", Fabric("example", "1.0"), "exampleproject");
        Spec next = old with { Relative = "mods/new.jar", Bytes = Fabric("example", "2.0") };
        var original = f.Package([old]); f.Install(old);
        JObject legacy = Manifest(original); legacy["schema"] = 1; legacy.Remove("complete");
        ((JObject)legacy["files"]!).Remove(old.Relative); // What the old RecordInstalled actually lost.
        bool notified = false;
        try { Plan incomplete = f.Engine.Plan(f.Game, legacy, f.Package([next])); notified = incomplete.Warnings.Count > 0; }
        catch (IOException) { notified = true; }
        Require(notified, "Incomplete schema one baseline was silently assumed to be a complete author declaration.");
        f.Has(old); f.Absent(next.Relative);
        Plan repaired = f.Engine.Plan(f.Game, Manifest(original), f.Package([next])); f.Stage(repaired); f.Commit(repaired);
        f.Has(next); f.Absent(old.Relative);
    }

    private static void RecordAuthorDeclarations() {
        using Context f = new();
        Spec missing = new("mods/missing.jar", Fabric("missing", "1.0"), "missingproject");
        Spec modified = new("mods/modified.jar", Fabric("modified", "1.0"), "modifiedproject");
        Spec disabled = new("mods/disabled.jar", Fabric("disabled", "1.0"), "disabledproject");
        Spec optional = new("mods/optional.jar", Fabric("optional", "1.0"), "optionalproject", true);
        var package = f.Package([missing, modified, disabled, optional]);
        string archive = f.LastArchive!;
        f.Install(modified with { Bytes = Fabric("modified", "player") }, disabled);
        File.Move(f.Path(disabled.Relative), f.Path(disabled.Relative + ".disabled"));
        f.Install(new Spec("mods/player.jar", Fabric("player", "1.0")));
        f.Engine.RecordInstalled(archive, f.Game);
        JObject manifest = f.Engine.ReadManifest(f.Game)!;
        Require(manifest["schema"]?.Value<int>() == 2 && manifest["complete"]?.Value<bool>() == true, "A complete package declaration was not recorded as schema two.");
        foreach (Spec item in new[] { missing, modified, disabled, optional })
            Require(manifest["files"]?[item.Relative]?["hash"]?.ToString() == package.Files[item.Relative].Hash, "Original author hash was dropped or replaced: " + item.Relative);
        Require(manifest["files"]?[missing.Relative]?["state"]?.ToString() == "missing", "Missing required author file was not distinguished from optional absence.");
        Require(manifest["files"]?[modified.Relative]?["state"]?.ToString() == "modified", "A locally replaced author file was recorded as installed author content.");
        Require(manifest["files"]?[disabled.Relative]?["state"]?.ToString() == "disabled", "Disabled state was lost in the first record.");
        Require(manifest["files"]?[optional.Relative]?["state"]?.ToString() == "optional-absent", "An unselected optional component was not recorded separately.");
        Require(manifest["files"]?["mods/player.jar"] is null, "A player's extra jar was promoted into author ownership during recording.");
    }

    private static void HashAndIdentityContracts() {
        using Context f = new();
        Spec item = new("mods/example.jar", Fabric("example", "1.0"), "exampleproject"); f.Install(item);
        var package = f.Package([item], hashAlgorithm: "sha512");
        PackFile file = package.Files[item.Relative];
        Require(file.Hash == Digest(f.Path(item.Relative), "sha512"), "A sha512-only Modrinth declaration was not retained with its algorithm.");
        Require(file.Identity == "modrinth:exampleproject" && !string.IsNullOrEmpty(file.FileIdentity) && file.FileIdentity != file.Identity, "Platform project and platform file-version identity were conflated.");
        Require(PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Matches(f.Path(item.Relative), file.Hash!.ToUpperInvariant()), "Valid case-insensitive recorded hashes failed normalization.");
        Throws<IOException>(() => Digest(f.Path(item.Relative), "crc32"));
        Throws<IOException>(() => PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Matches(f.Path(item.Relative), "unknown:" + new string('a', 40)));
        JObject corrupt = Manifest(package); corrupt["files"]![item.Relative]!["hash"] = "sha512:abcd"; f.WriteManifest(corrupt);
        Throws<IOException>(() => f.Engine.ReadManifest(f.Game));
        f.Has(item);
    }

    private static void UnknownMetadata() {
        using Context f = new();
        Spec extra = new("mods/unknown-player.jar", Jar(("README.txt", "no supported loader metadata")));
        Spec next = new("mods/known-author.jar", Fabric("known_author", "1.0"), "knownproject");
        var empty = f.Package([]); f.Install(extra);
        Plan plan = f.Plan(empty, f.Package([next])); f.Stage(plan);
        Require(!Incoming(plan, next.Relative).Conflict, "Unknown metadata was used as positive duplicate evidence.");
        Require(plan.Warnings.Count > 0 || plan.RuntimeWarnings.Count > 0 || plan.Changes.Any(c => c.Relative == extra.Relative && (c.Ownership == PackUpdateOwnership.Unknown || c.Reason!.Contains("无法"))), "Unknown metadata was not made visible.");
        var graph = f.Engine.CheckDependencies(plan);
        Require(graph.HasUnknown && !graph.HasErrors, "Unrecognized metadata became either a proven duplicate or an assumed compatible file.");
        Throws<IOException>(() => f.Commit(plan)); f.Has(extra); f.Absent(next.Relative);
        plan.AllowUnverifiedDependencies = true;
        f.Commit(plan); f.Has(extra); f.Has(next);
    }

    private static void DownloadFailures() {
        foreach (string failure in new[] { "download", "cancel", "hash" }) {
            using Context f = new();
            Spec old = new("mods/old.jar", Fabric("example", "1.0"), "exampleproject");
            Spec next = old with { Relative = "mods/new.jar", Bytes = Fabric("example", "2.0") };
            var original = f.Package([old]); f.Install(old); Plan plan = f.Plan(original, f.Package([next]));
            using CancellationTokenSource canceled = new();
            f.DownloadOverride = (files, token) => {
                if (failure == "download") throw new IOException("injected download failure");
                if (failure == "cancel") { canceled.Cancel(); token.ThrowIfCancellationRequested(); }
                foreach (Download file in files) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path)!); File.WriteAllText(file.Path, "corrupted payload"); }
            };
            if (failure == "cancel") Throws<OperationCanceledException>(() => f.Engine.DownloadChanges(plan, f.StagePath(), canceled.Token));
            else Throws<IOException>(() => f.Engine.DownloadChanges(plan, f.StagePath(), canceled.Token));
            f.Has(old); f.Absent(next.Relative);
            Require(!File.Exists(f.ManifestPath), "A failed staged download wrote the author baseline.");
        }
    }

    private static void CommitRollback() {
        foreach (bool canceled in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/old.jar", Fabric("example", "1.0"), "exampleproject");
            Spec next = old with { Relative = "mods/new.jar", Bytes = Fabric("example", "2.0") };
            var original = f.Package([old]); f.Install(old); f.WriteManifest(Manifest(original));
            byte[] baselineBefore = File.ReadAllBytes(f.ManifestPath);
            Plan plan = f.Plan(original, f.Package([next])); f.Stage(plan); f.Engine.ValidateChoices(plan);
            using CancellationTokenSource cancel = new();
            Action commit = () => f.Engine.Commit(plan, f.Game, cancel.Token, beforeApply: index => {
                if (index != 1) return;
                if (canceled) { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
                throw new IOException("injected second operation failure");
            });
            if (canceled) Throws<OperationCanceledException>(commit); else Throws<IOException>(commit);
            f.Has(old); f.Absent(next.Relative);
            Require(File.ReadAllBytes(f.ManifestPath).SequenceEqual(baselineBefore), "Rollback failed to restore the previous author record.");
            Require(!Directory.EnumerateFiles(f.Game, "*.pcl-update-*", SearchOption.AllDirectories).Any(), "Failed commit left replacement files behind.");
            Require(f.Engine.Pending(f.Game).Count == 0, "Rolled-back transaction still appears pending.");
        }
    }

    private static void StalePreview() {
        foreach (bool newAddition in new[] { false, true }) {
            using Context f = new();
            Spec old = new("mods/old.jar", Fabric("example", "1.0"), "exampleproject");
            Spec next = old with { Relative = "mods/new.jar", Bytes = Fabric("example", "2.0") };
            var original = f.Package([old]); f.Install(old);
            string renamed = "mods/[renamed] old.jar"; File.Move(f.Path(old.Relative), f.Path(renamed));
            Plan plan = f.Plan(original, f.Package([next])); f.Stage(plan); f.Engine.ValidateChoices(plan);
            if (newAddition) f.Install(new Spec("mods/newly-added-copy.jar", Fabric("example", "player")));
            else File.WriteAllBytes(f.Path(renamed), Fabric("example", "edited-after-preview"));
            string before = Digest(f.Path(renamed));
            Throws<IOException>(() => f.Engine.Commit(plan, f.Game, CancellationToken.None));
            Require(Digest(f.Path(renamed)) == before, "A stale plan overwrote a post-preview local edit.");
            f.Absent(next.Relative);
            if (newAddition) Require(File.Exists(f.Path("mods/newly-added-copy.jar")), "Stale-plan rejection deleted a newly added personal file.");
        }
    }

    private static void PersonalTargetAcrossUpdates() {
        foreach (string secondKind in new[] { "removed", "upgraded", "same-bytes" }) {
            using Context f = new();
            Spec personal = new("mods/occupied.jar", Fabric("occupied", "personal"));
            Spec firstAuthor = new(personal.Relative, Fabric("occupied", "author-1"), "occupiedproject");
            var empty = f.Package([]); f.Install(personal);
            Plan first = f.Plan(empty, f.Package([firstAuthor])); f.Stage(first);
            Change occupied = Incoming(first, firstAuthor.Relative);
            Require(occupied.Ownership == PackUpdateOwnership.PersonalAdded && !occupied.AllowAuthor && !occupied.ApplyAuthor, "An existing personal file at a new author target was automatically adopted.");
            f.Commit(first); f.Has(personal);
            JObject baseline = f.Engine.ReadManifest(f.Game)!;
            Require(baseline["files"]?[personal.Relative]?["local"]?["ownership"]?.ToString() == "personal-added", "The first update lost the personal origin of an occupied author target.");
            Spec secondAuthor = firstAuthor with { Bytes = secondKind == "same-bytes" ? personal.Bytes : Fabric("occupied", "author-2") };
            Plan second = f.Engine.Plan(f.Game, baseline, f.Package(secondKind == "removed" ? [] : [secondAuthor])); f.Stage(second);
            Change retained = second.Changes.Single(c => c.LocalRelative == personal.Relative);
            Require(retained.Ownership == PackUpdateOwnership.PersonalAdded && !retained.AllowAuthor && !retained.ApplyAuthor, "The second update converted a personal addition into an author-owned replacement or deletion.");
            retained.Choice = PackUpdateChoice.UseAuthor; Throws<IOException>(() => f.Engine.ValidateChoices(second));
            retained.Choice = PackUpdateChoice.PreservePersonal; f.Commit(second); f.Has(personal);
            JObject after = f.Engine.ReadManifest(f.Game)!;
            if (secondKind == "removed") Require(after["files"]?[personal.Relative] is null, "A removed author declaration retained ownership of a personal addition.");
            else Require(after["files"]?[personal.Relative]?["local"]?["ownership"]?.ToString() == "personal-added" && after["files"]?[personal.Relative]?["state"]?.ToString() != "installed", "Coincidentally equal bytes erased a preserved personal addition's provenance.");
        }
    }

    private static void SelectiveDownloads() {
        foreach (bool changed in new[] { false, true }) {
            using Context f = new();
            Spec stable = new("mods/stable.jar", Fabric("stable", "1.0"), "stableproject");
            Spec optional = new("mods/optional-old.jar", Fabric("optional", "1.0"), "optionalproject", true);
            Spec old = new("mods/changed-old.jar", Fabric("changed", "1.0"), "changedproject");
            var original = f.Package(changed ? [stable, optional, old] : [stable, optional]);
            string originalArchive = f.LastArchive!;
            if (changed) f.Install(stable, old); else f.Install(stable);
            f.Engine.RecordInstalled(originalArchive, f.Game);
            JObject baseline = f.Engine.ReadManifest(f.Game)!;
            Spec nextOptional = optional with { Relative = "mods/optional-new.jar", Bytes = Fabric("optional", "2.0") };
            Spec next = old with { Relative = "mods/changed-new.jar", Bytes = Fabric("changed", "2.0") };
            var package = f.Package(changed ? [stable, nextOptional, next] : [stable, nextOptional]);
            // Valid HTTPS declarations with unavailable endpoints: transport would
            // fail if an unchanged file or an unselected component were requested.
            package.Files[stable.Relative].Urls = ["https://unavailable.invalid/unchanged.jar"];
            package.Files[nextOptional.Relative].Urls = ["https://unavailable.invalid/unselected-optional.jar"];
            int transports = 0;
            f.DownloadOverride = (files, token) => {
                transports++;
                Require(changed && files.Count == 1 && files[0].Urls.SequenceEqual(package.Files[next.Relative].Urls), "Unchanged or unselected optional files reached transport.");
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(files[0].Path)!); File.WriteAllBytes(files[0].Path, next.Bytes);
            };
            Plan plan = f.Engine.Plan(f.Game, baseline, package); f.Stage(plan);
            Require(transports == (changed ? 1 : 0), "Selective staging did not preserve the unchanged-file download contract.");
            f.Commit(plan); f.Has(stable); f.Absent(optional.Relative); f.Absent(nextOptional.Relative);
            if (changed) { f.Has(next); f.Absent(old.Relative); }
        }
    }

    private static void IncomingAndCascadingConflicts() {
        foreach (bool alias in new[] { false, true }) {
            using Context f = new();
            Spec first = new("mods/first-new.jar", Fabric("shared_id", "1.0"), "firstproject");
            Spec second = new("mods/second-new.jar", alias ? FabricAlias("other_mod", "1.0", "shared_id") : Fabric("shared_id", "2.0"), "secondproject");
            var empty = f.Package([]); Plan plan = f.Plan(empty, f.Package([first, second])); f.Stage(plan);
            Change a = Incoming(plan, first.Relative), b = Incoming(plan, second.Relative);
            if (!alias) {
                Require(a.Conflict && b.Conflict && !a.ApplyAuthor && !b.ApplyAuthor, "Conflicting new author own IDs were not both visible as conflicts before preview.");
                a.Choice = b.Choice = PackUpdateChoice.UseAuthor; Throws<IOException>(() => f.Engine.ValidateChoices(plan));
                b.Choice = PackUpdateChoice.PreservePersonal; f.Commit(plan); f.Has(first); f.Absent(second.Relative);
            } else {
                Require(!a.Conflict && !b.Conflict && a.ApplyAuthor && b.ApplyAuthor, "Provided aliases were treated as duplicate own IDs instead of separately evaluated loader behavior.");
                var graph = f.Engine.CheckDependencies(plan);
                Require(!graph.HasErrors && graph.HasUnknown && graph.Warnings.Any(text => text.Contains("shared_id") && text.Contains("提供")), "The incoming alias-provider ambiguity was not visible before preview.");
                Throws<IOException>(() => f.Commit(plan)); f.Absent(first.Relative); f.Absent(second.Relative);
                plan.AllowUnverifiedDependencies = true;
                f.Commit(plan); f.Has(first); f.Has(second);
            }
        }
        using (Context f = new()) {
            Spec oldA = new("mods/a-old.jar", Fabric("a_old", "1.0"), "projectA");
            Spec oldB = new("mods/b-old.jar", Fabric("b_old", "1.0"), "projectB");
            Spec personal = new("mods/player.jar", Fabric("personal", "1.0"));
            Spec nextA = new("mods/a-new.jar", Fabric("personal", "2.0"), "projectA");
            Spec nextB = new("mods/b-new.jar", Fabric("a_old", "2.0"), "projectB");
            Spec nextC = new("mods/c-new.jar", Fabric("b_old", "2.0"), "projectC");
            var original = f.Package([oldA, oldB]); f.Install(oldA, oldB, personal);
            Plan plan = f.Plan(original, f.Package([nextA, nextB, nextC])); f.Stage(plan);
            foreach (Spec next in new[] { nextA, nextB, nextC }) {
                Change change = Incoming(plan, next.Relative);
                Require(!change.Conflict && change.AllowAuthor && change.ApplyAuthor, "A personal duplicate caused unrelated curated upgrades to be retained: " + next.Relative);
            }
            Change disabled = plan.Changes.Single(change => change.LocalRelative == personal.Relative);
            Require(disabled.Operation == PackUpdateOperation.DisablePersonal && disabled.ApplyAuthor, "Personal duplicate was not prepared for reversible disabling.");
            f.Commit(plan); f.Absent(oldA.Relative); f.Absent(oldB.Relative); f.Absent(personal.Relative);
            f.Has(nextA); f.Has(nextB); f.Has(nextC); f.Has(personal with { Relative = disabled.DestinationRelative });
        }
    }

    private static void PlanningCancellation() {
        using Context f = new();
        Spec old = new("mods/old.jar", Fabric("example", "1.0"), "exampleproject");
        Spec next = old with { Relative = "mods/new.jar", Bytes = Fabric("example", "2.0") };
        var original = f.Package([old]); var package = f.Package([next]); f.Install(old);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Throws<OperationCanceledException>(() => f.Engine.Plan(f.Game, Manifest(original), package, canceled.Token));
        Throws<OperationCanceledException>(() => PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Digest(f.Path(old.Relative), token: canceled.Token));
        Throws<OperationCanceledException>(() => PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Digest(f.Path("mods/missing.jar"), token: canceled.Token));
        Throws<OperationCanceledException>(() => PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Matches(f.Path(old.Relative), original.Files[old.Relative].Hash, canceled.Token));
        f.Has(old); f.Absent(next.Relative);
        Require(!File.Exists(f.ManifestPath), "Canceled planning or hashing wrote a new author baseline.");
    }

    private static Change Incoming(Plan plan, string relative) => plan.Changes.Single(c => c.Incoming?.Relative == relative);
    private static string Digest(string path, string algorithm = "sha1") => PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Digest(path, algorithm)!;
    private static JObject Manifest(Package pack) => PackUpdateEngine<PackFile, Package, Change, Plan, Download>.Manifest(pack);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + "."); }
    private static byte[] Fabric(string id, string version) => Jar(("fabric.mod.json", new JObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = version, ["name"] = id, ["environment"] = "*", ["depends"] = new JObject { ["minecraft"] = "*", ["fabricloader"] = ">=0.16.0" } }.ToString()));
    private static byte[] FabricAlias(string id, string version, string alias) => Jar(("fabric.mod.json", new JObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = version, ["name"] = id, ["environment"] = "*", ["provides"] = new JArray(alias), ["depends"] = new JObject { ["minecraft"] = "*", ["fabricloader"] = ">=0.16.0" } }.ToString()));
    private static byte[] Forge(string id, string version, string platform) => Jar(("META-INF/mods.toml", $"modLoader=\"javafml\"\nloaderVersion=\"[1,)\"\nlicense=\"MIT\"\n[[mods]]\nmodId=\"{id}\"\nversion=\"{version}\"\ndisplayName=\"{id}\"\n[[dependencies.{id}]]\nmodId=\"minecraft\"\nmandatory=true\nversionRange=\"[1.20,1.21)\"\nside=\"BOTH\"\n[[dependencies.{id}]]\nmodId=\"{platform}\"\nmandatory=true\nversionRange=\"[1,)\"\nside=\"BOTH\"\n"));
    private static byte[] Neo(string id, string version) => Jar(("META-INF/neoforge.mods.toml", $"modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\nlicense=\"MIT\"\n[[mods]]\nmodId=\"{id}\"\nversion=\"{version}\"\ndisplayName=\"{id}\"\n[[dependencies.{id}]]\nmodId=\"minecraft\"\ntype=\"required\"\nversionRange=\"[1.21.1,1.22)\"\nside=\"BOTH\"\n[[dependencies.{id}]]\nmodId=\"neoforge\"\ntype=\"required\"\nversionRange=\"[21.1,)\"\nside=\"BOTH\"\n"));
    private static byte[] Jar(params (string Path, string Text)[] entries) {
        using MemoryStream memory = new();
        using (ZipArchive archive = new(memory, ZipArchiveMode.Create, true)) {
            foreach (var item in entries) using (StreamWriter writer = new(archive.CreateEntry(item.Path).Open(), new UTF8Encoding(false))) writer.Write(item.Text);
        }
        return memory.ToArray();
    }
    private sealed record Spec(string Relative, byte[] Bytes, string? Project = null, bool Optional = false);
    private sealed class PackFile : PackUpdateFileData { }
    private sealed class Package : PackUpdatePackageData<PackFile> { }
    private sealed class Change : PackUpdateChangeData<PackFile> { }
    private sealed class Plan : PackUpdatePlanData<Package, Change> { }
    private sealed record Download(List<string> Urls, string Path);
    private sealed class Context : IDisposable {
        internal readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PclPackThreeWay-" + Guid.NewGuid().ToString("N"));
        internal string Game => System.IO.Path.Combine(Root, "game");
        internal string ManifestPath => Engine.Target(Game, PackUpdateEngine<PackFile, Package, Change, Plan, Download>.ManifestName);
        internal string? LastArchive;
        private readonly Dictionary<string, byte[]> downloads = new(StringComparer.Ordinal);
        internal Action<List<Download>, CancellationToken>? DownloadOverride;
        internal readonly PackUpdateEngine<PackFile, Package, Change, Plan, Download> Engine;
        private int counter;
        internal Context() {
            Directory.CreateDirectory(Game);
            Engine = new(new PackUpdateHost<PackFile, Package, Change, Plan, Download> {
                CreateFile = () => new(), CreatePackage = () => new(), CreateChange = () => new(), CreatePlan = () => new(),
                EnsureWithin = EnsureWithin,
                AtomicWrite = (path, bytes) => LocalGameDataStore.AtomicWrite(path, bytes, null, false),
                CreateDownload = (urls, path, _) => new Download(urls, path), Download = Download,
                TempFolder = StagePath, InstanceGameRoot = _ => Game,
                Warn = (error, message) => throw new Exception(message, error),
                Request = (_, _, _) => throw new Exception("Fixture unexpectedly attempted a real metadata request.")
            });
        }
        internal string Path(string relative) => System.IO.Path.Combine(Game, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        internal void Text(string relative, string text) { string target = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); File.WriteAllText(target, text, new UTF8Encoding(false)); }
        internal void Install(params Spec[] specs) { foreach (Spec spec in specs) { string target = Path(spec.Relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); File.WriteAllBytes(target, spec.Bytes); } }
        internal string StagePath() { string target = System.IO.Path.Combine(Root, "stage-" + (++counter)); Directory.CreateDirectory(target); return target; }
        internal Package Package(Spec[] specs, ModLoaders loader = ModLoaders.Fabric, string loaderVersion = "0.18.4", string minecraft = "1.21.11", string hashAlgorithm = "sha1", bool overrides = false) {
            string folder = StagePath(); JArray files = new();
            foreach (Spec spec in overrides ? Array.Empty<Spec>() : specs) {
                string payload = System.IO.Path.Combine(folder, "payload-" + files.Count); File.WriteAllBytes(payload, spec.Bytes);
                string project = spec.Project ?? "unmapped";
                string url = "https://cdn.modrinth.com/data/" + project + "/versions/fixture" + counter + "/" + files.Count + ".jar";
                downloads[url] = spec.Bytes;
                files.Add(new JObject { ["path"] = spec.Relative, ["hashes"] = new JObject { [hashAlgorithm] = Digest(payload, hashAlgorithm).Split(':')[1] }, ["downloads"] = new JArray(url), ["env"] = new JObject { ["client"] = spec.Optional ? "optional" : "required" } });
            }
            string loaderKey = loader == ModLoaders.NeoForge ? "neoforge" : loader == ModLoaders.Forge ? "forge" : "fabric-loader";
            JObject json = new() { ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = "Author fixture", ["versionId"] = "fixture-" + counter, ["dependencies"] = new JObject { ["minecraft"] = minecraft, [loaderKey] = loaderVersion }, ["files"] = files };
            string archivePath = System.IO.Path.Combine(folder, "package.mrpack");
            var entries = new[] { ("modrinth.index.json", json.ToString()) };
            if (overrides) entries = entries.Concat(specs.Select(spec => ("overrides/" + spec.Relative, Encoding.UTF8.GetString(spec.Bytes)))).ToArray();
            File.WriteAllBytes(archivePath, Jar(entries));
            LastArchive = archivePath;
            Package result = Engine.ReadPackage(archivePath, StagePath(), CancellationToken.None);
            foreach (Spec spec in specs.Where(s => s.Project is null)) result.Files[spec.Relative].Identity = null;
            return result;
        }
        internal Plan Plan(Package old, Package incoming) => Engine.Plan(Game, Manifest(old), incoming);
        internal void Stage(Plan plan) => Engine.DownloadChanges(plan, StagePath(), CancellationToken.None);
        internal void Commit(Plan plan) { Engine.ValidateChoices(plan); Engine.Commit(plan, Game, CancellationToken.None); }
        internal void WriteManifest(JObject manifest) => LocalGameDataStore.AtomicWrite(ManifestPath, Encoding.UTF8.GetBytes(manifest.ToString()), null, false);
        internal void Has(Spec spec) => Require(File.Exists(Path(spec.Relative)) && File.ReadAllBytes(Path(spec.Relative)).SequenceEqual(spec.Bytes), "Expected local bytes missing or changed: " + spec.Relative);
        internal void Absent(string relative) => Require(!File.Exists(Path(relative)), "Unexpected local file: " + relative);
        private void Download(List<Download> files, CancellationToken token) {
            if (DownloadOverride is not null) { DownloadOverride(files, token); return; }
            foreach (Download file in files) { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file.Path)!); File.WriteAllBytes(file.Path, downloads[file.Urls[0]]); }
        }
        private static void EnsureWithin(string root, string target) {
            string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture path escaped its owned root.");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
