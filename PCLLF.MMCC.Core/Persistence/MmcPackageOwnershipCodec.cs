// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCLLF.MMCC.Core;

public static class MmcPackageOwnershipCodec {
    public static ImmutableArray<MmcDiagnostic> Validate(ReadOnlyMemory<byte> content, MmcObjectReference reference, string packageSha256) {
        try {
            MmcDefinitionCodec.CheckHash(reference.Sha256); MmcDefinitionCodec.CheckHash(packageSha256);
            ValidateReference(reference);
            if (content.Length > 16 * 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(content.Span)) != reference.Sha256)
                throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_DIGEST", "ownership", "Immutable ownership bytes exceed budget or differ from the fixed object digest.");
            using var document = MmcJson.Read(new UTF8Encoding(false, true).GetString(content.Span), "ownership", 128);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 2 ||
                !root.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("associationPolicyVersion", out var policy) || policy.ValueKind != JsonValueKind.Number || !policy.TryGetInt32(out int association) || association != 2 ||
                root.TryGetProperty("baselineNeedsRepair", out var repair) && repair.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_SCHEMA", "ownership", "A complete supported schema-2 ownership record is required.");
            if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object || MmcJson.String(source, "type", "ownership", true) != "MMC-local-zip" ||
                MmcJson.String(source, "packageSha256", "ownership", true) != packageSha256)
                throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_ORIGIN", "ownership", "Ownership does not belong to the selected measured author package.");
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object || files.EnumerateObject().Take(100001).Count() > 100000)
                throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_SCHEMA", "ownership", "Author ownership file inventory is invalid.");
            foreach (var file in files.EnumerateObject()) {
                MmcArtifactRules.Relative(file.Name, "ownership");
                if (file.Value.ValueKind != JsonValueKind.Object) throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_SCHEMA", "ownership", "An author file requires its hash record.");
                string value = MmcJson.String(file.Value, "hash", "ownership", true)!;
                int split = value.IndexOf(':');
                int length = split > 0 ? value[..split] switch { "sha1" => 40, "sha256" => 64, "sha512" => 128, "md5" => 32, _ => 0 } : 0;
                if (length == 0) throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_SCHEMA", "ownership", "Author ownership requires an explicit supported hash algorithm.");
                MmcDefinitionCodec.CheckHash(value[(split + 1)..], length);
            }
            return [];
        } catch (MmcInputException error) { return [new(error.Code, MmcDiagnosticSeverity.Error, error.SourcePath, error.Message)]; }
        catch (DecoderFallbackException) { return [new("MMC_PACKAGE_OWNERSHIP_ENCODING", MmcDiagnosticSeverity.Error, "ownership", "Ownership is not UTF-8.")]; }
    }
    internal static void ValidateReference(MmcObjectReference reference) {
        MmcDefinitionCodec.CheckHash(reference.Sha256); MmcArtifactRules.Relative(reference.ObjectRelativePath, "ownership");
        string[] path = reference.ObjectRelativePath.Split('/');
        if (path.Length != 5 || path[0] != "PCL" || path[1] != "MmcSource" || path[3] != "ownership" || path[4] != reference.Sha256 + ".json")
            throw new MmcInputException("MMC_PACKAGE_OWNERSHIP_PATH", "ownership", "Ownership must reference its own content-addressed MmcSource object.");
        MmcDefinitionCodec.CheckHash(path[2]);
    }
}
