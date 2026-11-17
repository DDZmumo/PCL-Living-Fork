// SPDX-License-Identifier: MPL-2.0
using System.Security.Cryptography;
using System.Text;

namespace PCLLF.MMCC.Core;

internal static class MmcFingerprint {
    // Length-prefixed UTF-8 prevents ambiguous concatenations without serializing
    // whole domain objects or depending on dictionary enumeration order.
    internal static string Of(IEnumerable<string?> values) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[4];
        foreach (string? value in values) {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
            hash.AppendData(size);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
