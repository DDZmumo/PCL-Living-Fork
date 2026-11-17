using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcExportSnapshot(MmcInstancePaths Paths, MmcStoredPrimaryDefinition Primary,
    MmcSavedInstanceSources Saved, MmcCompiledProfile Compiled, MmcExportPreferences Preferences,
    string PreferenceFingerprint, ImmutableArray<MmcExportMeasuredFile> SourceFiles,
    ImmutableArray<string> Directories, MmcExportMetadata Metadata, MmcInstanceExportPreview Preview);

internal static class MmcInstanceExportSnapshot {
    internal static readonly MmcReadLimits Limits = new();
    private static readonly HashSet<string> AccountNames = new(["accounts.json","accounts.cfg","launcher_accounts.json","launcher_profiles.json",
        "multimc.cfg","prismlauncher.cfg","usercache.json","usernamecache.json","launcher_accounts_microsoft_store.json"],StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TransientRoots = new(["PCL","logs","crash-reports","cache",".cache","natives","bin","java","runtime",".jre",".java","skins"],StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GameRoots = new(["mods","coremods","config","scripts","resources","resourcepacks","texturepacks","shaderpacks","libraries","defaultconfigs","kubejs","patchouli_books","screenshots","saves","servers.dat","servers.dat_old","realms_persistence.json"],StringComparer.OrdinalIgnoreCase);
    internal static MmcExportSnapshot Capture(MmcInstanceExportRequest request, MmcConfigurationQuiescence quiet, MmcAdmissionLease lease, CancellationToken token) {
        AssertSource(request,lease);
        var primary=MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(lease.VersionRoot));
        var paths=MmcInstancePaths.Resolve(lease.VersionRoot,primary.Definition.Layout.GameRootLeaf);
        var saved=MmcSavedSourceStore.LoadAsync(paths,primary.Definition,token).GetAwaiter().GetResult();
        var compiled=MmcInstanceCloneBackend.CompileEnvironment(saved.Resolved.Resolution,primary.Definition,request.LegacyEnvironmentCandidates);
        var prefSource=MmcInstanceCloneSnapshot.Preferences(quiet,primary.Definition.InstanceId);
        var preferences=MmcInstanceExportPreferences.Capture(saved.PackageConfiguration,prefSource,request);
        var sources=new Dictionary<string,MmcExportMeasuredFile>(StringComparer.OrdinalIgnoreCase);
        var zipped=new Dictionary<string,MmcExportMeasuredFile>(StringComparer.OrdinalIgnoreCase);
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pins=new Dictionary<string,MmcExportMeasuredFile>(StringComparer.Ordinal);
        var locksByKey=primary.Definition.ArtifactLocks.ToDictionary(value=>value.ArtifactKey,StringComparer.Ordinal);
        long measuredBytes=0,traversalCharacters=0;
        foreach(var record in primary.Definition.SourceBundle.MetadataRecords) Authority(record.ObjectRelativePath,record.ContentSha256);
        if(primary.Definition.AuthorFileBaseline is { } baseline) Authority(baseline.ObjectRelativePath,baseline.ContentSha256);
        foreach(var change in primary.Definition.ComponentOverrides) if(change.PatchReference is { } patch) Authority(patch.ObjectRelativePath,patch.Sha256);
        foreach(var artifact in MmcInstanceExportMetadata.Active(compiled)) {
            if(!locksByKey.TryGetValue(artifact.Key,out var locked))throw new InvalidDataException("An active export input has no adopted lock.");
            pins.Add(artifact.Key,Authority(locked.RecoveryObjectRelativePath,locked.AdoptedSha256,locked.SizeBytes));
        }
        var activeLocal=MmcInstanceExportMetadata.Active(compiled).Where(a=>a.Scope==MmcArtifactScope.InstanceLocal).ToDictionary(a=>"libraries/"+a.RelativePath,StringComparer.OrdinalIgnoreCase);
        var inventory=primary.Definition.ArtifactInventory.ToDictionary(a=>a.ArtifactKey,StringComparer.Ordinal);
        var known=new Dictionary<string,List<(string Sha,long Size,string Original)>>(StringComparer.OrdinalIgnoreCase);
        foreach(var locked in primary.Definition.ArtifactLocks) if(inventory.TryGetValue(locked.ArtifactKey,out var item)&&item.Scope==MmcArtifactScope.InstanceLocal)
            Known(locked.TargetRelativePath,locked.AdoptedSha256,locked.SizeBytes,locked.RecoveryObjectRelativePath);
        foreach(var locked in primary.Definition.PackageFileLocks) Known(locked.TargetRelativePath,locked.AdoptedSha256,locked.SizeBytes,locked.RecoveryObjectRelativePath);
        var expectedGame=saved.Baseline.Baseline.Files.Where(file=>file.TargetRelativePath is not null&&file.TargetRelativePath.StartsWith(paths.GameRootLeaf+"/",StringComparison.OrdinalIgnoreCase))
            .Select(file=>file.TargetRelativePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string other=paths.GameRootLeaf=="minecraft"?".minecraft":"minecraft";
        string otherPath=paths.ResolveRelative(MmcPathRole.Instance,other);
        if(Directory.Exists(otherPath)&&Directory.EnumerateFileSystemEntries(otherPath).Any()&&request.Options.InactiveGameTree!=MmcExportInactiveTree.Exclude)
            throw new InvalidDataException("An inactive game tree contains data; explicitly exclude it instead of merging or silently losing its role.");
        Walk(paths.GameRootLeaf,true,0); Walk("libraries",false,0);
        foreach(var path in activeLocal.Keys) if(!zipped.ContainsKey(path)) throw new InvalidDataException("An active private library is missing; repair it before export.");
        var metadata=MmcInstanceExportMetadata.Create(saved,compiled,preferences,pins);
        long bytes=metadata.Files.Sum(pair=>(long)pair.Value.Length)+zipped.Values.Sum(file=>file.SizeBytes);
        if(bytes>Limits.MaxArchiveBytes-16L*1024*1024) throw new IOException("Export exceeds the MMC archive byte budget.");
        var entries=metadata.Files.Select(pair=>new MmcArchiveEntry(pair.Key,pair.Value.Length)).Concat(zipped.Select(pair=>new MmcArchiveEntry(pair.Key,pair.Value.SizeBytes)))
            .Concat(directories.Select(path=>new MmcArchiveEntry(path+"/",0,true))).ToArray();
        var checkedIndex=MmcPackageReader.InspectArchive(entries,Limits);
        if(checkedIndex.HasErrors) throw new InvalidDataException("Export target index is not portable MMC: "+string.Join("; ",checkedIndex.Diagnostics.Select(d=>d.Code)));
        long central=entries.Sum(entry=>78L+Encoding.UTF8.GetByteCount(entry.Path));
        if(central>16L*1024*1024) throw new IOException("Export central-directory budget would be exceeded.");
        var all=sources.Values.OrderBy(file=>file.SourceRelativePath,StringComparer.Ordinal).ToImmutableArray();
        string fingerprint=Hash(JsonSerializer.SerializeToUtf8Bytes(new {Protocol="PCLLF.MmcExportPreview/v1",request.SourceRoot,request.OutputZipPath,request.DisplayName,request.Options,
            primary.Sha256,Preferences=prefSource.Fingerprint,Files=all,Directories=directories.Order(StringComparer.Ordinal),Metadata=metadata.Files.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>new{pair.Key,Hash=Hash(pair.Value)})}));
        var transformations=metadata.Transformations;
        if(Directory.Exists(otherPath)&&Directory.EnumerateFileSystemEntries(otherPath).Any()) transformations=transformations.Add("Inactive game tree explicitly excluded; the active role is unchanged.");
        var preview=new MmcInstanceExportPreview(primary.Definition.InstanceId,primary.Definition.Generation,primary.Sha256,fingerprint,paths.GameRootLeaf,entries.Length,bytes,transformations);
        return new(paths,primary,saved,compiled,preferences,prefSource.Fingerprint,all,directories.Order(StringComparer.Ordinal).ToImmutableArray(),metadata,preview);

        void Known(string relative,string sha,long size,string original) {
            if(!relative.StartsWith("libraries/",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A private-library lock has an unknown directory role.");
            if(!known.TryGetValue(relative,out var list)) known.Add(relative,list=[]); list.Add((sha,size,original));
        }
        MmcExportMeasuredFile Authority(string relative,string sha,long? size=null) {
            if(sources.TryGetValue(relative,out var previous)) {if(previous.Sha256!=sha||size is not null&&previous.SizeBytes!=size)throw new InvalidDataException("Source authority has conflicting bytes.");return previous;}
            var measured=Measure(paths.ResolveRelative(MmcPathRole.Instance,relative),relative,token);
            measuredBytes=checked(measuredBytes+measured.SizeBytes);if(measuredBytes>Limits.MaxArchiveBytes)throw new IOException("Export source measurement budget exceeded.");
            if(measured.Sha256!=sha||size is not null&&measured.SizeBytes!=size) throw new InvalidDataException("An immutable export source differs from its adopted digest.");
            sources.Add(relative,measured);return measured;
        }
        void Walk(string relative,bool game,int depth) {
            token.ThrowIfCancellationRequested(); if(depth>128)throw new IOException("Export tree depth budget exceeded.");
            traversalCharacters=checked(traversalCharacters+relative.Length);if(relative.Length>32767||traversalCharacters>Limits.MaxTotalPathCharacters)throw new IOException("Export traversal path budget exceeded.");
            string[] parts=relative.Split('/');string leaf=parts[^1];
            if(AccountNames.Contains(leaf)||AccountNames.Any(name=>leaf.StartsWith(name+".",StringComparison.OrdinalIgnoreCase)))return;
            if(game&&parts.Length>1) {
                string child=parts[1];
                if(TransientRoots.Contains(child)||AccountNames.Contains(leaf)||AccountNames.Any(name=>leaf.StartsWith(name+".",StringComparison.OrdinalIgnoreCase)))return;
                if(!request.Options.IncludeSaves&&(child.Equals("saves",StringComparison.OrdinalIgnoreCase)||child.Equals("journeymap",StringComparison.OrdinalIgnoreCase)||child.Equals("XaeroWaypoints",StringComparison.OrdinalIgnoreCase)||child.Equals("XaeroWorldMap",StringComparison.OrdinalIgnoreCase)))return;
                if(!request.Options.IncludeScreenshots&&child.Equals("screenshots",StringComparison.OrdinalIgnoreCase))return;
                if(!request.Options.IncludeServers&&(leaf.Equals("servers.dat",StringComparison.OrdinalIgnoreCase)||leaf.Equals("servers.dat_old",StringComparison.OrdinalIgnoreCase)||leaf.Equals("realms_persistence.json",StringComparison.OrdinalIgnoreCase)))return;
            }
            string path=paths.ResolveRelative(MmcPathRole.Instance,relative);var attr=MmcInstancePaths.Attributes(path);if(attr is null)return;
            if((attr.Value&FileAttributes.Directory)!=0) {
                if(!directories.Add(relative)||directories.Count>Limits.MaxArchiveEntries)throw new IOException("Duplicate export directory or entry budget exceeded.");
                foreach(string input in Directory.EnumerateFileSystemEntries(path)) Walk(relative+"/"+Path.GetFileName(input),game,depth+1); // Preserve the logical root; DirectoryInfo.FullName may carry a device prefix.
            } else {
                if(game&&!request.Options.IncludeUnrecognizedGameFiles&&!GameRoots.Contains(parts[1])&&!expectedGame.Contains(relative)&&
                    !(parts.Length==2&&(leaf.StartsWith("options",StringComparison.OrdinalIgnoreCase)||leaf is "eula.txt" or "pack.png")))
                    throw new InvalidDataException("Unrecognized game data needs explicit inclusion to avoid exporting personal files: "+relative);
                var measured=Measure(path,relative,token) with {ZipPath=relative};
                measuredBytes=checked(measuredBytes+measured.SizeBytes);if(measuredBytes>Limits.MaxArchiveBytes)throw new IOException("Export measurement budget exceeded.");
                if(!game) {
                    if(!known.TryGetValue(relative,out var candidates))throw new InvalidDataException("An unadopted private-library file has no export authority: "+relative);
                    var match=candidates.FirstOrDefault(candidate=>candidate.Sha==measured.Sha256&&candidate.Size==measured.SizeBytes);
                    if(match.Original is null)throw new InvalidDataException("A private-library file changed outside an adopted definition.");
                    if(activeLocal.TryGetValue(relative,out var active)&&locksByKey[active.Key].AdoptedSha256!=measured.Sha256)throw new InvalidDataException("An active private library differs from its current lock.");
                    Authority(match.Original,match.Sha,match.Size);
                }
                if(!zipped.TryAdd(relative,measured)||zipped.Count>Limits.MaxArchiveEntries)throw new IOException("Duplicate export target or file budget exceeded.");
                if(sources.TryGetValue(relative,out var existing)&&existing.Sha256!=measured.Sha256)throw new IOException("Export roles conflict.");sources[relative]=measured;
            }
        }
    }
    internal static MmcExportMeasuredFile Measure(string path,string relative,CancellationToken token) {
        MmcInstancePaths.EnsureNoLinks(path);using FileStream input=new(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan);
        if(input.Length>Limits.MaxEntryBytes)throw new IOException("Export input exceeds MMC per-entry budget.");
        using var sha256=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var sha1=IncrementalHash.CreateHash(HashAlgorithmName.SHA1);byte[] buffer=new byte[131072];long size=0;int count;
        while((count=input.Read(buffer,0,buffer.Length))!=0){token.ThrowIfCancellationRequested();size=checked(size+count);if(size>Limits.MaxEntryBytes)throw new IOException("Export source grew beyond its entry budget.");sha256.AppendData(buffer,0,count);sha1.AppendData(buffer,0,count);}
        token.ThrowIfCancellationRequested();return new(relative,Convert.ToHexStringLower(sha256.GetHashAndReset()),Convert.ToHexStringLower(sha1.GetHashAndReset()),size);
    }
    internal static void AssertSource(MmcInstanceExportRequest request,MmcAdmissionLease lease) {
        lease.AssertHeld();MmcFileTransactions.AssertNoPending(lease);MmcSessions.AssertNoActive(lease);
        var inspected=MmcInstanceInspector.Read(lease.VersionRoot);
        if(!inspected.IsConsistent||inspected.Definition!.InstanceId!=request.ExpectedSourceInstanceId||inspected.Definition.Generation!=request.ExpectedSourceGeneration||inspected.PrimarySha256!=request.ExpectedSourcePrimarySha256)
            throw new IOException("Export source ID/generation/primary/projection changed, is active, or needs recovery.");
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
