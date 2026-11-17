using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcExportMeasuredFile(string SourceRelativePath, string Sha256, string Sha1, long SizeBytes, string? ZipPath = null);
internal sealed record MmcExportMetadata(ImmutableDictionary<string,byte[]> Files, ImmutableArray<string> Transformations,
    MmcCompiledProfile PortableProfile);

internal static class MmcInstanceExportMetadata {
    internal static MmcExportMetadata Create(MmcSavedInstanceSources saved, MmcCompiledProfile current,
        MmcExportPreferences prefs, IReadOnlyDictionary<string,MmcExportMeasuredFile> originals) {
        var profile=saved.Resolved.Resolution.Profile??throw new InvalidDataException("Export needs complete fixed source resolution.");
        var patches=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
        var components=new JsonArray(); var transformations=prefs.Transformations.ToBuilder();
        foreach(var component in profile.Components) {
            var patch=new JsonObject();
            foreach(var field in component.Patch.Fields.OrderBy(pair=>pair.Key,StringComparer.Ordinal)) patch[field.Key]=JsonNode.Parse(field.Value.GetRawText());
            patch["uid"]=component.Uid; patch["formatVersion"]=1; patch["volatile"]=false;
            if(component.Version is not null) patch["version"]=component.Version;
            patches.Add(component.Uid,patch);
            var declaration=new JsonObject { ["uid"]=component.Uid, ["disabled"]=!component.Enabled };
            if(component.Version is not null) declaration["version"]=component.Version;
            if(component.OriginalDeclaration?.Important==true) declaration["important"]=true;
            if(component.OriginalDeclaration?.DependencyOnly==true) declaration["dependencyOnly"]=true;
            // Exact local patches freeze adopted dependency versions; original important/dependencyOnly
            // roles and requires edges remain, without selecting recommendation metadata later.
            components.Add(declaration);
        }
        var declaredPositions=current.ArtifactInventory.GroupBy(item=>(item.ComponentUid,item.Provenance.Field)).ToDictionary(group=>group.Key,group=>group.Select(item=>item.Provenance.Position).ToHashSet());
        var declarationIndices=current.FieldProvenance.GroupBy(source=>(source.ComponentUid,source.Field)).ToDictionary(group=>group.Key,group=>group.Where(source=>!declaredPositions.TryGetValue(group.Key,out var positions)||!positions.Contains(source.Position)).Select(source=>source.Position).Distinct().Order().Select((position,index)=>(position,index)).ToDictionary(pair=>pair.position,pair=>pair.index));
        foreach(var artifact in Active(current).Where(a=>a.Scope==MmcArtifactScope.Shared)) {
            if(!originals.TryGetValue(artifact.Key,out var bytes)) throw new InvalidDataException("An adopted shared original is unavailable for export pinning.");
            JsonObject patch=patches[artifact.ComponentUid]; JsonObject declaration=Declaration(declarationIndices,artifact,patch);
            JsonObject download;
            if(artifact.Role==MmcArtifactRole.AssetIndex || artifact.Role==MmcArtifactRole.LoggingConfiguration) download=declaration;
            else {
                var downloads=declaration["downloads"] as JsonObject; if(downloads is null) { downloads=new JsonObject(); declaration["downloads"]=downloads; }
                if(!string.IsNullOrEmpty(artifact.NativeClassifier)) {
                    var classifiers=downloads["classifiers"] as JsonObject; if(classifiers is null) { classifiers=new JsonObject(); downloads["classifiers"]=classifiers; }
                    if(classifiers[artifact.NativeClassifier] is JsonObject existing) download=existing; else { download=new JsonObject(); classifiers[artifact.NativeClassifier]=download; }
                } else { if(downloads["artifact"] is JsonObject existing) download=existing; else { download=new JsonObject(); downloads["artifact"]=download; } }
                download["path"]=artifact.RelativePath;
            }
            if(download["url"] is null) download["url"]=artifact.DownloadUrls.FirstOrDefault()??throw new InvalidDataException("A portable shared input needs a declared download URL.");
            download["sha1"]=bytes.Sha1; download["size"]=bytes.SizeBytes;
        }
        ValidateNativeGameContract(profile,current);
        MmcInstanceExportArgumentPolicy.Declared(current.GameArguments.Select(argument=>argument.Value).ToArray(),prefs.IncludeServers);
        bool overlay=!prefs.ExtraGame.IsEmpty||prefs.Fullscreen;
        if(overlay) {
            string uid="org.pcllf.portable-launch-preferences";
            for(int suffix=1;patches.ContainsKey(uid);suffix++) {
                if(suffix>4096)throw new InvalidDataException("No bounded portable-preference component UID is available.");
                uid="org.pcllf.portable-launch-preferences."+suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var game=current.GameArguments.Select(a=>a.Value).Concat(prefs.ExtraGame).ToList();
            if(prefs.Fullscreen&&!game.Contains("--fullscreen",StringComparer.Ordinal)) game.Add("--fullscreen");
            MmcInstanceExportArgumentPolicy.Declared(game,prefs.IncludeServers);
            if(game.Any(token=>token.Length==0||token.Any(char.IsWhiteSpace))) throw new InvalidDataException("Prism legacy minecraftArguments cannot encode a literal empty/whitespace argument token; refusing an inexact export.");
            if(!current.Tweakers.IsEmpty) {
                // Complete argv already contains these flags. Leaving +tweakers would append
                // them a second time after the preference tail. The shared Core requirements
                // collector and Authority now prove raw --tweakClass as well as structured ones.
                foreach(JsonObject patch in patches.Values)patch.Remove("+tweakers");
                transformations.Add("Structured tweaks encoded as the identical ordered raw --tweakClass tokens; shared launch-class proof remains mandatory.");
            }
            patches.Add(uid,new JsonObject { ["formatVersion"]=1,["uid"]=uid,["version"]="1",["order"]=int.MaxValue,["minecraftArguments"]=string.Join(' ',game) });
            components.Add(new JsonObject { ["uid"]=uid,["version"]="1" });
            transformations.Add("Portable game/fullscreen preferences encoded as a final ordinary MMC component.");
        }
        var files=ImmutableDictionary.CreateBuilder<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        files.Add("mmc-pack.json",Encoding.UTF8.GetBytes(new JsonObject { ["formatVersion"]=1,["components"]=components }.ToJsonString()));
        files.Add("instance.cfg",Encoding.UTF8.GetBytes(prefs.Encode()));
        foreach(var patch in patches) {
            CheckPortableMetadata(patch.Value);
            files.Add("patches/"+patch.Key+".json",Encoding.UTF8.GetBytes(patch.Value.ToJsonString()));
        }
        var parsed=MmcPackageReader.Parse(new(files.Select(f=>new MmcArchiveEntry(f.Key,f.Value.Length)).Append(new MmcArchiveEntry(saved.Package.GameRootLeaf+"/",0,true)).ToArray(),
            files.ToDictionary(f=>f.Key,f=>new UTF8Encoding(false,true).GetString(f.Value),StringComparer.OrdinalIgnoreCase)));
        if(parsed.HasErrors||parsed.Package is null) throw new InvalidDataException("Portable metadata is not valid MMC: "+string.Join("; ",parsed.Diagnostics.Select(d=>d.Code)));
        var resolved=MmcComponentResolver.Resolve(parsed.Package,[]);
        if(!resolved.IsComplete) throw new InvalidDataException("Frozen MMC export still needs unfixed remote component metadata.");
        var portable=MmcProfileCompiler.Compile(resolved.Profile!,current.Environment);
        if(portable.HasErrors||portable.Profile is null) throw new InvalidDataException("Portable MMC launch compilation failed.");
        MmcInstanceExportArgumentPolicy.Declared(portable.Profile.GameArguments.Select(argument=>argument.Value).ToArray(),prefs.IncludeServers);
        Equivalent(current,portable.Profile,prefs);
        transformations.Add("Effective local/user/remote component metadata frozen into exact local patches; current shared downloads pinned by adopted original SHA1/size.");
        return new(files.ToImmutable(),transformations.ToImmutable(),portable.Profile);
    }
    private static JsonObject Declaration(IReadOnlyDictionary<(string ComponentUid,string Field),Dictionary<int,int>> indices,MmcArtifactReference artifact,JsonObject patch) {
        string field=artifact.Provenance.Field;
        if(field=="logging") return patch["logging"]?["file"] as JsonObject??throw new InvalidDataException("Logging source provenance is incomplete.");
        if(field is "mainJar" or "assetIndex") return patch[field] as JsonObject??throw new InvalidDataException("Artifact source declaration is incomplete.");
        var array=patch[field] as JsonArray??throw new InvalidDataException("Artifact source array is incomplete.");
        if(!indices.TryGetValue((artifact.ComponentUid,field),out var positions)||positions.Count!=array.Count||!positions.TryGetValue(artifact.Provenance.Position,out int index))
            throw new InvalidDataException("Artifact declaration provenance cannot be mapped uniquely for export pinning.");
        return array[index] as JsonObject??throw new InvalidDataException("Artifact declaration is not an object.");
    }
    private static void ValidateNativeGameContract(MmcResolvedProfile profile,MmcCompiledProfile compiled) {
        string? raw=null;
        foreach(var component in profile.Components.Where(c=>c.Enabled)) if(component.Patch.Fields.TryGetValue("minecraftArguments",out var value)&&value.ValueKind==JsonValueKind.String&&!string.IsNullOrEmpty(value.GetString())) raw=value.GetString();
        var native=(raw??"").Split(' ',StringSplitOptions.RemoveEmptyEntries).Concat(compiled.Tweakers.SelectMany(t=>new[]{"--tweakClass",t}));
        if(!native.SequenceEqual(compiled.GameArguments.Select(a=>a.Value))) throw new InvalidDataException("Legacy game argument interpretation differs from native Prism; update/revalidate the source instead of exporting the launcher's old quote semantics.");
    }
    internal static ImmutableArray<MmcArtifactReference> Active(MmcCompiledProfile profile) => profile.Classpath.Concat(profile.NativeArtifacts).Concat(profile.DownloadOnlyArtifacts).Concat(profile.AuxiliaryArtifacts).GroupBy(a=>a.Key,StringComparer.Ordinal).Select(g=>g.First()).ToImmutableArray();
    internal static void Equivalent(MmcCompiledProfile expected,MmcCompiledProfile actual,MmcExportPreferences prefs) {
        string Artifact(MmcArtifactReference a)=>JsonSerializer.Serialize(new{a.ComponentUid,a.Role,a.Scope,Name=a.Coordinate?.Name,a.RelativePath,a.NativeClassifier,Excludes=a.ExtractExcludes});
        string Policy(MmcJavaPolicy p)=>JsonSerializer.Serialize(new{p.Kind,p.AllowedMajors,p.RecommendedRuntimeName,Hard=p.HardConstraints.Select(c=>new{c.MinimumMajor,c.MaximumMajor,c.AllowedMajors})});
        var game=expected.GameArguments.Select(a=>a.Value).Concat(prefs.ExtraGame).ToList(); if(prefs.Fullscreen&&!game.Contains("--fullscreen",StringComparer.Ordinal))game.Add("--fullscreen");
        if(expected.MainClass!=actual.MainClass||expected.MinecraftVersion!=actual.MinecraftVersion||expected.MinecraftVersionType!=actual.MinecraftVersionType||expected.Assets.Id!=actual.Assets.Id||
            !expected.JvmArguments.Select(a=>a.Value).SequenceEqual(actual.JvmArguments.Select(a=>a.Value))||!game.SequenceEqual(actual.GameArguments.Select(a=>a.Value))||
            !MmcLaunchClassRequirements.Collect(expected.MainClass,expected.JvmArguments.Select(a=>a.Value),game,expected.Tweakers)
                .SequenceEqual(MmcLaunchClassRequirements.Collect(actual.MainClass,actual.JvmArguments.Select(a=>a.Value),actual.GameArguments.Select(a=>a.Value),actual.Tweakers))||
            !expected.Traits.SequenceEqual(actual.Traits)||Policy(expected.JavaPolicy)!=Policy(actual.JavaPolicy)||
            !expected.Classpath.Select(Artifact).SequenceEqual(actual.Classpath.Select(Artifact))||!expected.NativeArtifacts.Select(Artifact).SequenceEqual(actual.NativeArtifacts.Select(Artifact))||
            !expected.DownloadOnlyArtifacts.Select(Artifact).SequenceEqual(actual.DownloadOnlyArtifacts.Select(Artifact))||!expected.AuxiliaryArtifacts.Select(Artifact).SequenceEqual(actual.AuxiliaryArtifacts.Select(Artifact)))
            throw new InvalidDataException("Portable MMC projection changed the effective launch contract; refusing export.");
    }
    private static bool CredentialQuery(string query) {
        var privateNames=new HashSet<string>(new[]{"token","access_token","auth","key","api_key","apikey","authorization","password","signature","sig","X-Amz-Credential","X-Amz-Signature","X-Goog-Credential","X-Goog-Signature"},StringComparer.OrdinalIgnoreCase);
        foreach(string part in query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)) if(privateNames.Contains(Uri.UnescapeDataString(part.Split('=')[0])))return true;
        return false;
    }
    private static void CheckPortableMetadata(JsonNode? node,string? key=null) {
        if(node is JsonObject obj) foreach(var pair in obj) CheckPortableMetadata(pair.Value,pair.Key);
        else if(node is JsonArray array) foreach(var item in array) CheckPortableMetadata(item,key);
        else if(node is JsonValue scalar&&scalar.TryGetValue<string>(out string? text)) {
            if(key is "url" or "absoluteUrl" or "MMC-absoluteUrl") {
                if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.UserInfo.Length!=0||CredentialQuery(uri.Query))
                    throw new InvalidDataException("A metadata URL is machine-specific or carries a query credential; it is not portable.");
            } else if(key is "minecraftArguments" or "+jvmArgs") MmcInstanceExportPreferences.PortableArguments([text]);
        }
    }
}
