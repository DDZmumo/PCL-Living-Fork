using System.Text.Json.Nodes;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation, under the repository LICENCE.
/// <summary>Rebuildable UI/interop projection; it never authorizes ordinary installation or launch.</summary>
public static class MmcVersionProjection {
    public static byte[] Render(string name, Guid instanceId, long generation, MmcCompiledProfile profile,
        IReadOnlyDictionary<string, string> artifactTargets, string? nativeDirectoryRelative,
        string assetsRootRelative) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(artifactTargets);
        MmcArtifactIo.Relative(name);
        if (name.Contains('/') || name.Contains('\\') || instanceId == Guid.Empty || generation < 1)
            throw new ArgumentException("Projection needs a valid instance name, identity and generation.");
        MmcArtifactIo.Relative(assetsRootRelative);
        if (nativeDirectoryRelative is not null) MmcArtifactIo.Relative(nativeDirectoryRelative);
        string Target(MmcArtifactReference artifact) {
            if (!artifactTargets.TryGetValue(artifact.Key, out string? target))
                throw new InvalidDataException("A compiled projection artifact has no materialized target: " + artifact.Key);
            MmcArtifactIo.Relative(target);
            return target.Replace('\\', '/');
        }
        JsonArray libraries = [];
        foreach (MmcArtifactReference artifact in profile.Classpath.Where(a => a.Role != MmcArtifactRole.MainJar)) {
            JsonObject library = new() {
                ["name"] = artifact.Coordinate?.Name ?? "pcllf.mmc:artifact:" + artifact.Key,
                ["pcllfArtifactKey"] = artifact.Key,
                ["pcllfTarget"] = Target(artifact)
            };
            libraries.Add(library);
        }
        JsonObject hint = ManagementHint(instanceId, generation, profile);
        JsonObject json = new() {
            ["id"] = name,
            ["type"] = profile.MinecraftVersionType ?? "release",
            ["mainClass"] = profile.MainClass,
            ["assets"] = profile.Assets.Id,
            ["assetIndex"] = AssetIndex(profile.Assets),
            ["libraries"] = libraries,
            ["arguments"] = new JsonObject {
                ["jvm"] = new JsonArray(profile.JvmArguments.Select(token => JsonValue.Create(token.Value)).ToArray()),
                ["game"] = new JsonArray(profile.GameArguments.Select(token => JsonValue.Create(token.Value)).ToArray())
            },
            ["pcllfManagement"] = hint,
            ["pcllfCompatibleJavaMajors"] = new JsonArray(profile.JavaPolicy.AllowedMajors.Select(value => JsonValue.Create(value)).ToArray()),
            ["pcllfRuntime"] = new JsonObject {
                ["formatVersion"] = 1,
                ["minecraftVersion"] = profile.MinecraftVersion,
                ["mainJar"] = Target(profile.MainJar),
                ["classpath"] = new JsonArray(profile.Classpath.Select(item => JsonValue.Create(Target(item))).ToArray()),
                ["nativeInputs"] = new JsonArray(profile.NativeArtifacts.Select(item => JsonValue.Create(Target(item))).ToArray()),
                ["downloadOnly"] = new JsonArray(profile.DownloadOnlyArtifacts.Select(item => JsonValue.Create(Target(item))).ToArray()),
                ["auxiliaryInputs"] = new JsonArray(profile.AuxiliaryArtifacts.Select(item => JsonValue.Create(Target(item))).ToArray()),
                ["nativesDirectory"] = nativeDirectoryRelative,
                ["assetsRoot"] = assetsRootRelative,
                ["profileFingerprint"] = profile.ProfileFingerprint,
                ["runtimePlanFingerprint"] = profile.RuntimePlanFingerprint
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public static JsonObject ManagementHint(Guid id, long generation, MmcCompiledProfile profile) => new() {
        ["schemaVersion"] = 1, ["managementKind"] = "Mmc", ["formatProfile"] = profile.FormatProfile,
        ["instanceId"] = id.ToString("D"), ["generation"] = generation,
        ["profileFingerprint"] = profile.ProfileFingerprint, ["sourceFingerprint"] = profile.SourceFingerprint
    };

    private static JsonObject AssetIndex(MmcAssetIndex index) {
        JsonObject result = new() { ["id"] = index.Id };
        if (index.Artifact is { } artifact) {
            if (artifact.DownloadUrls.Length != 0) result["url"] = artifact.DownloadUrls[0];
            if (artifact.Sha1 is not null) result["sha1"] = artifact.Sha1;
            if (artifact.Size is not null) result["size"] = artifact.Size.Value;
        }
        return result;
    }
}
