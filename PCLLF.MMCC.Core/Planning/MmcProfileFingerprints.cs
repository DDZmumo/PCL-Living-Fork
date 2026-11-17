// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

public sealed record MmcProfileFingerprintValues(string ProfileFingerprint, string RuntimePlanFingerprint);

/// <summary>One canonical protocol for compilation, cold-plan verification, binding and authority. Labels never hash themselves.</summary>
public static class MmcProfileFingerprints {
    public static MmcProfileFingerprintValues Compute(MmcCompiledProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);
        List<string?> values = [];
        void Text(string? value) { values.Add(value is null ? "null" : "text"); values.Add(value); }
        void Integer(long value) => Text(value.ToString(CultureInfo.InvariantCulture));
        void Strings(IEnumerable<string> items) { string[] array = items.ToArray(); Integer(array.Length); foreach (string item in array) Text(item); }
        void Source(MmcFieldSource source) { Text(source.ComponentUid); Text(source.ComponentVersion); Text(source.Source); Text(source.SourceSha256); Text(source.Field); Integer(source.Position); }
        void Artifact(MmcArtifactReference? artifact) {
            if (artifact is null) { Text(null); return; }
            Text("artifact"); Text(artifact.Key); Text(artifact.ComponentUid); Text(artifact.SourceRevision); Text(artifact.Role.ToString()); Text(artifact.Scope.ToString());
            if (artifact.Coordinate is { } coordinate) { Text("coordinate"); Text(coordinate.Group); Text(coordinate.Artifact); Text(coordinate.Version); Text(coordinate.Classifier); Text(coordinate.Extension); }
            else Text(null);
            Text(artifact.RelativePath); Strings(artifact.DownloadUrls); Text(artifact.Sha1); Text(artifact.Sha256);
            if (artifact.PackageFileOrigin is { } origin) { Text("package-file-origin"); Text(origin.PackageSha256); Text(origin.BaselineSha256); Text(origin.SourceRelativePath); }
            else Text(null);
            Text(artifact.Size?.ToString(CultureInfo.InvariantCulture)); Text(artifact.NativeClassifier); Strings(artifact.ExtractExcludes); Source(artifact.Provenance);
        }
        void Artifacts(ImmutableArray<MmcArtifactReference> artifacts) { if (artifacts.IsDefault) throw new ArgumentException("Absent artifact array."); Integer(artifacts.Length); foreach (var artifact in artifacts) Artifact(artifact); }
        void Tokens(ImmutableArray<MmcArgumentToken> tokens) { if (tokens.IsDefault) throw new ArgumentException("Absent argument array."); Integer(tokens.Length); foreach (var token in tokens) { Text(token.Value); Source(token.Provenance); } }
        void Json(JsonElement element) {
            Text(element.ValueKind.ToString());
            switch (element.ValueKind) {
                case JsonValueKind.Object:
                    var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(); Integer(properties.Length);
                    foreach (var property in properties) { Text(property.Name); Json(property.Value); }
                    break;
                case JsonValueKind.Array: Integer(element.GetArrayLength()); foreach (var item in element.EnumerateArray()) Json(item); break;
                case JsonValueKind.String: Text(element.GetString()); break;
                case JsonValueKind.Number: Text(element.GetRawText()); break;
            }
        }
        string structural = MmcFingerprint.Of(["mmc-prism-v1-profile-v2", profile.FormatProfile, profile.SourceFingerprint]);
        Text("mmc-prism-v1-runtime-plan-v2"); Text(structural); Text(profile.FormatProfile); Text(profile.SourceFingerprint);
        Text(profile.Environment.OperatingSystem); Text(profile.Environment.Architecture); Integer(profile.Environment.JavaMajor); Integer(profile.Environment.JavaBitness); Text(profile.Environment.OsVersion);
        Text(profile.MinecraftVersion); Text(profile.MinecraftVersionType); Text(profile.MainClass); Artifact(profile.MainJar);
        Text(profile.Assets.Id); Artifact(profile.Assets.Artifact); Source(profile.Assets.Provenance);
        Tokens(profile.JvmArguments); Tokens(profile.GameArguments); Strings(profile.Tweakers); Strings(profile.Traits);
        Artifacts(profile.Classpath); Artifacts(profile.NativeArtifacts); Artifacts(profile.DownloadOnlyArtifacts); Artifacts(profile.AuxiliaryArtifacts);
        Text(profile.JavaPolicy.Kind.ToString()); Strings(profile.JavaPolicy.AllowedMajors.Select(v => v.ToString(CultureInfo.InvariantCulture))); Text(profile.JavaPolicy.RecommendedRuntimeName);
        Integer(profile.JavaPolicy.HardConstraints.Length);
        foreach (var constraint in profile.JavaPolicy.HardConstraints) {
            Text(constraint.Source); Text(constraint.MinimumMajor?.ToString(CultureInfo.InvariantCulture)); Text(constraint.MaximumMajor?.ToString(CultureInfo.InvariantCulture));
            Text(constraint.AllowedMajors.IsDefault ? "unspecified" : "declared");
            if (!constraint.AllowedMajors.IsDefault) Strings(constraint.AllowedMajors.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }
        Integer(profile.FieldProvenance.Length); foreach (var source in profile.FieldProvenance) Source(source);
        Integer(profile.OverrideEffects.Length); foreach (var effect in profile.OverrideEffects) { Text(effect.ComponentUid); Strings(effect.Fields); Text(effect.ReplacesBuiltinLaunchDefinition.ToString()); }
        Integer(profile.ArtifactInventory.Length);
        foreach (var declaration in profile.ArtifactInventory) { Text(declaration.ComponentUid); Text(declaration.Enabled.ToString()); Text(declaration.Role.ToString()); Source(declaration.Provenance); Json(declaration.Declaration); }
        return new(structural, MmcFingerprint.Of(values));
    }

    public static bool Matches(MmcCompiledProfile profile) {
        if (profile is null || profile.Environment is null || profile.Assets is null || profile.MainJar is null || profile.JavaPolicy is null ||
            profile.JvmArguments.IsDefault || profile.GameArguments.IsDefault || profile.Classpath.IsDefault || profile.NativeArtifacts.IsDefault || profile.DownloadOnlyArtifacts.IsDefault || profile.AuxiliaryArtifacts.IsDefault ||
            profile.Tweakers.IsDefault || profile.Traits.IsDefault || profile.FieldProvenance.IsDefault || profile.OverrideEffects.IsDefault || profile.ArtifactInventory.IsDefault || profile.JavaPolicy.AllowedMajors.IsDefault || profile.JavaPolicy.HardConstraints.IsDefault)
            return false;
        if (profile.JvmArguments.Concat(profile.GameArguments).Any(t => t is null || t.Value is null || t.Provenance is null) || profile.FieldProvenance.Any(s => s is null) || profile.Assets.Provenance is null ||
            profile.OverrideEffects.Any(e => e is null || e.Fields.IsDefault) || profile.JavaPolicy.HardConstraints.Any(c => c is null) || profile.ArtifactInventory.Any(d => d is null || d.Provenance is null || d.Declaration.ValueKind == JsonValueKind.Undefined))
            return false;
        if (profile.Classpath.Concat(profile.NativeArtifacts).Concat(profile.DownloadOnlyArtifacts).Concat(profile.AuxiliaryArtifacts).Append(profile.MainJar).Concat(profile.Assets.Artifact is { } assets ? [assets] : [])
            .Any(a => a is null || a.Provenance is null || a.DownloadUrls.IsDefault || a.ExtractExcludes.IsDefault)) return false;
        var computed = Compute(profile);
        return MmcDefinitionCodec.SameHash(computed.ProfileFingerprint, profile.ProfileFingerprint) &&
            MmcDefinitionCodec.SameHash(computed.RuntimePlanFingerprint, profile.RuntimePlanFingerprint);
    }
}
