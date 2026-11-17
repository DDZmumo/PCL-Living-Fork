using System.Collections.Immutable;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
internal static class MmcInstancePackageUpdatePlanning<TNetFile> {
    internal static PackUpdateEngine<MmcPackageUpdateFile, MmcPackageUpdatePackage, MmcPackageUpdateChange, MmcPackageUpdatePlan, TNetFile> Engine(string stage) => new(new() {
        CreateFile = () => new(), CreatePackage = () => new(), CreateChange = () => new(), CreatePlan = () => new(),
        EnsureWithin = Within, AtomicWrite = (_, _) => throw new IOException("Managed package publication must use the MMC transaction."),
        Request = (_, _, _) => throw new IOException("Metadata transport belongs to the MMC source adapter."),
        ParseVersion = (_, _) => throw new IOException("Local MMC ZIP updates have no inferred platform version identity."),
        CreateDownload = (_, _, _) => throw new IOException("Author payloads already belong to the verified candidate."),
        Download = (_, _) => throw new IOException("Unexpected old update downloader dispatch."), TempFolder = () => stage,
        InstanceGameRoot = root => MmcInstancePaths.Resolve(root).GameRoot, Warn = (_, _) => { }
    });
    internal static void Within(string root, string target) {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); target = Path.GetFullPath(target);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update target escapes its fixed directory role.");
        MmcInstancePaths.EnsureNoLinks(root); MmcInstancePaths.EnsureNoLinks(target);
    }
    internal static bool SafeAuthorFile(string relative) {
        PackUpdateEngine<MmcPackageUpdateFile, MmcPackageUpdatePackage, MmcPackageUpdateChange, MmcPackageUpdatePlan, TNetFile>.RelativePath(relative);
        if (PackUpdateEngine<MmcPackageUpdateFile, MmcPackageUpdatePackage, MmcPackageUpdateChange, MmcPackageUpdatePlan, TNetFile>.IsPrivate(relative) ||
            PackUpdateEngine<MmcPackageUpdateFile, MmcPackageUpdatePackage, MmcPackageUpdateChange, MmcPackageUpdatePlan, TNetFile>.IsPersonal(relative)) return false;
        string lower = relative.ToLowerInvariant(), leaf = Path.GetFileName(lower);
        return !new[] { "accounts.json", "accounts.cfg", "launcher_accounts.json", "launcher_profiles.json", "multimc.cfg", "prismlauncher.cfg", "session.lock" }.Contains(leaf) &&
            !new[] { "logs/", "crash-reports/", "server-resource-packs/", "natives/" }.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal));
    }
    internal static MmcPackageUpdatePackage Package(MmcStoredPrimaryDefinition primary, MmcSavedInstanceSources saved,
        MmcCompiledProfile compiled, MmcInstancePaths paths, bool payloads) {
        var package = new MmcPackageUpdatePackage { Name = saved.PackageConfiguration.Name ?? primary.Definition.Origin.OriginalName,
            Version = primary.Definition.Origin.PackageSha256, Minecraft = compiled.MinecraftVersion,
            Source = new JObject { ["type"] = "MMC-local-zip", ["packageSha256"] = primary.Definition.Origin.PackageSha256 } };
        foreach (var component in saved.Resolved.Resolution.Profile!.Components.Where(value => value.Enabled && value.Version is not null)) {
            ModLoaders? loader = component.Uid switch { "net.minecraftforge" => ModLoaders.Forge, "net.neoforged" => ModLoaders.NeoForge,
                "net.fabricmc.fabric-loader" => ModLoaders.Fabric, "com.mumfrey.liteloader" => ModLoaders.LiteLoader, _ => null };
            if (loader is { } type) package.Loaders[type] = component.Version!;
        }
        string prefix = paths.GameRootLeaf + "/";
        foreach (var file in saved.Baseline.Baseline.Files.Where(value => value.Role == PCLLF.MMCC.Core.MmcPackageFileRole.Game && value.TargetRelativePath is not null)) {
            if (!file.TargetRelativePath!.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Author file belongs to another game-tree role.");
            string relative = file.TargetRelativePath[prefix.Length..]; if (!SafeAuthorFile(relative)) continue;
            if (!package.Files.TryAdd(relative, new() { Relative = relative, Hash = "sha1:" + file.Sha1,
                Payload = payloads ? paths.ResolveRelative(MmcPathRole.Instance, file.TargetRelativePath) : null }))
                throw new IOException("Author game paths collide in the update role.");
        }
        return package;
    }
    internal static JObject Baseline(MmcInstancePaths paths, MmcStoredPrimaryDefinition primary, MmcSavedInstanceSources saved,
        MmcCompiledProfile compiled, string stage) {
        var engine = Engine(stage);
        if (primary.Definition.PackageOwnershipReference is { } reference) {
            byte[] bytes = MmcInstancePackageOwnership.Read(paths, primary.Definition) ?? throw new IOException("MMC author ownership record is missing.");
            string recordRoot = Path.Combine(stage, "ownership", reference.Sha256, "versions", "record");
            string manifest = Path.Combine(recordRoot, "PCL", "ModpackOrigin.json"); MmcArtifactIo.Directory(Path.GetDirectoryName(manifest)!);
            string? old = MmcDurableFiles.Observe(manifest);
            if (old is null) MmcDurableFiles.WriteNew(manifest, bytes); else if (old != reference.Sha256) throw new IOException("Owned ownership-validation staging was changed.");
            var existing = engine.ReadManifest(recordRoot) ?? throw new IOException("MMC author ownership record is missing.");
            if (existing["source"]?["type"]?.Value<string>() != "MMC-local-zip" || existing["source"]?["packageSha256"]?.Value<string>() != primary.Definition.Origin.PackageSha256 ||
                PackUpdateEngine<MmcPackageUpdateFile, MmcPackageUpdatePackage, MmcPackageUpdateChange, MmcPackageUpdatePlan, TNetFile>.RequiresBaselineRepair(existing))
                throw new IOException("Existing author ownership does not bind this measured MMC source; explicit recovery is required.");
            return existing;
        }
        // Bootstrap ONLY from measured author bytes. Current files are observations, never authors.
        return PackUpdateEngine<MmcPackageUpdateFile, MmcPackageUpdatePackage, MmcPackageUpdateChange, MmcPackageUpdatePlan, TNetFile>.Manifest(Package(primary, saved, compiled, paths, false));
    }
    internal static ImmutableArray<MmcHistoryFile> LocalSnapshot(MmcInstancePaths paths, CancellationToken token) {
        var result = ImmutableArray.CreateBuilder<MmcHistoryFile>(); int nodes = 0; long total = 0;
        if (Directory.Exists(paths.GameRoot)) Walk(paths.GameRootLeaf, "", 0);
        string originMirror = paths.ResolveRelative(MmcPathRole.Instance, "PCL/ModpackOrigin.json");
        if (MmcInstancePaths.Attributes(originMirror) is not null) result.Add(MmcInstanceHistoryFiles.Measure(paths, "PCL/ModpackOrigin.json", token));
        return result.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToImmutableArray();
        void Walk(string instanceRelative, string relative, int depth) {
            token.ThrowIfCancellationRequested(); if (++nodes > 200000 || depth > 64) throw new IOException("Update local snapshot exceeds its path budget.");
            if (relative.Length != 0 && !SafeAuthorFile(relative + (Directory.Exists(paths.ResolveRelative(MmcPathRole.Instance, instanceRelative)) ? "/.snapshot-probe" : ""))) return;
            string target = paths.ResolveRelative(MmcPathRole.Instance, instanceRelative); var attributes = MmcInstancePaths.Attributes(target);
            if (attributes is null) return;
            if ((attributes.Value & FileAttributes.Directory) != 0) {
                foreach (string item in Directory.EnumerateFileSystemEntries(target)) Walk(instanceRelative + "/" + Path.GetFileName(item), relative.Length == 0 ? Path.GetFileName(item) : relative + "/" + Path.GetFileName(item), depth + 1);
            } else {
                var fact = MmcInstanceHistoryFiles.Measure(paths, instanceRelative, token); total = checked(total + fact.SizeBytes);
                if (total > MmcInstanceHistoryFiles.MaximumBytes || result.Count >= MmcInstanceHistoryFiles.MaximumFiles) throw new IOException("Update local snapshot exceeds its byte/count budget.");
                result.Add(fact);
            }
        }
    }
    internal static string ChangeId(MmcPackageUpdateChange change) => MmcDurableFiles.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {
        change.Relative, change.BaselineRelative, change.LocalRelative, change.Before, change.TargetBefore, change.Ownership, change.Operation,
        Incoming = change.Incoming is null ? null : new { change.Incoming.Relative, change.Incoming.Hash }, change.PreviewOnly
    }));
    internal static ImmutableArray<MmcPackageFileDifference> Describe(MmcPackageUpdatePlan plan) => plan.Changes.Concat(plan.PreviewChanges)
        .Select(change => new MmcPackageFileDifference(ChangeId(change), change.Relative ?? "", change.LocalRelative, change.Before,
            change.Incoming?.Hash, change.Ownership, change.Conflict, change.CanChoose || change.AllowAuthor && !change.DisplayOnly && change.Ownership == PackUpdateOwnership.Disabled, change.ApplyAuthor, change.Choice, change.KeepDisabled, change.Reason))
        .OrderBy(value => value.ChangeId, StringComparer.Ordinal).ToImmutableArray();
}
