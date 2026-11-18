// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

public static class MmcAssetIndexReader {
    public static MmcAssetIndexReadResult Read(MmcAssetIndexInput input, string expectedSha256, MmcReadLimits? limits = null) {
        ArgumentNullException.ThrowIfNull(input);
        limits ??= new();
        List<MmcDiagnostic> diagnostics = [];
        try {
            MmcDefinitionCodec.CheckHash(input.ArtifactKey); MmcDefinitionCodec.CheckHash(expectedSha256);
            if (input.Content.Length > limits.MaxMetadataBytes) throw new MmcInputException("MMC_ASSET_INDEX_BUDGET", "asset-index", "Raw asset index exceeds the metadata budget.");
            byte[] content = input.Content.ToArray(); // hash and parse one stable bounded snapshot
            string digest = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!MmcDefinitionCodec.SameHash(digest, expectedSha256)) throw new MmcInputException("MMC_ASSET_INDEX_DIGEST", "asset-index", "Asset index raw bytes differ from the adopted parent.");
            string text = new UTF8Encoding(false, true).GetString(content);
            using var document = MmcJson.Read(text, "asset-index", limits.MaxDepth);
            JsonElement root = document.RootElement;
            foreach (var field in root.EnumerateObject()) if (field.Name is not ("objects" or "virtual" or "map_to_resources"))
                throw new MmcInputException("MMC_UNSUPPORTED_REQUIRED", "asset-index", "Unknown asset-index behavior field.");
            if (!root.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object) MmcJson.Fail("asset-index", "Asset index objects map is required.");
            var result = ImmutableArray.CreateBuilder<MmcAssetIndexObject>();
            List<MmcArchiveEntry> entries = [];
            long total = 0, pathCharacters = 0;
            foreach (var item in objects.EnumerateObject()) {
                if (result.Count >= limits.MaxArchiveEntries) throw new MmcInputException("MMC_ASSET_INDEX_BUDGET", "asset-index", "Asset object count exceeded.");
                pathCharacters += item.Name.Length;
                if (pathCharacters > limits.MaxTotalPathCharacters) throw new MmcInputException("MMC_PATH_BUDGET", "asset-index", "Asset-name character budget exceeded.");
                if (item.Value.ValueKind != JsonValueKind.Object) MmcJson.Fail("asset-index", "Asset entry must be an object.");
                foreach (var field in item.Value.EnumerateObject()) if (field.Name is not ("hash" or "size")) MmcJson.Fail("asset-index", "Unknown asset object field.");
                string hash = MmcJson.String(item.Value, "hash", "asset-index", true)!;
                MmcDefinitionCodec.CheckHash(hash, 40);
                long size = -1;
                if (!item.Value.TryGetProperty("size", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out size) || size < 0 || size > limits.MaxEntryBytes || total > limits.MaxArchiveBytes - size)
                    throw new MmcInputException("MMC_ASSET_INDEX_BUDGET", "asset-index", "Asset object size/total exceeded.");
                total += size;
                entries.Add(new(item.Name, size));
                result.Add(new(item.Name, hash.ToLowerInvariant(), size));
            }
            diagnostics.AddRange(MmcPackageReader.InspectPaths(entries, limits));
            bool virtualAssets = MmcJson.Boolean(root, "virtual", "asset-index"), mapped = MmcJson.Boolean(root, "map_to_resources", "asset-index");
            if (virtualAssets && mapped) diagnostics.Add(new("MMC_ASSET_LAYOUT_PRECEDENCE", MmcDiagnosticSeverity.Information, "asset-index",
                "prism-v1 uses the virtual layout first; the preserved map_to_resources flag is inactive for this index."));
            return new(new(input.ArtifactKey, digest, virtualAssets, mapped, result.ToImmutable()), diagnostics.ToImmutableArray());
        } catch (MmcInputException error) { diagnostics.Add(new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)); }
        catch (DecoderFallbackException) { diagnostics.Add(new("MMC_ASSET_INDEX_ENCODING", MmcDiagnosticSeverity.Error, "asset-index", "Invalid asset-index UTF-8.")); }
        return new(null, diagnostics.ToImmutableArray());
    }
}
