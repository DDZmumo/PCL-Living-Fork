// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Security.Cryptography;
using System.Text;

namespace PCL
{

    // 发布凭据仅由构建生成的临时 partial class 提供；直接编译开源源码时为空。
    // 这是客户端字符串保护，不能替代服务器端保密或阻止运行时提取。
    internal sealed partial class BuildCredentials
    {
        private static readonly Func<string> CurseForgeFactory = null;
        private static readonly Func<string> MicrosoftFactory = null;

        private BuildCredentials()
        {
        }

        internal static string ReadCurseForgeKey()
        {
            return Resolve("PCL_CURSEFORGE_API_KEY", CurseForgeFactory);
        }

        internal static bool IsCurseForgeRequest(Uri Address)
        {
            return Address is not null && Address.IsAbsoluteUri && (Address.Scheme ?? "") == (Uri.UriSchemeHttps ?? "") && Address.Port == 443 && string.IsNullOrEmpty(Address.UserInfo) && string.Equals(Address.DnsSafeHost.TrimEnd('.'), "api.curseforge.com", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadMicrosoftClientId()
        {
            return Resolve("PCL_MS_CLIENT_ID", MicrosoftFactory);
        }

        private static string Resolve(string Variable, Func<string> Factory)
        {
            string OverrideValue = Environment.GetEnvironmentVariable(Variable);
            if (OverrideValue is not null)
                return OverrideValue;
            if (Factory is null)
                return "";
            return Factory.Invoke();
        }

        private static byte[] Assemble(string[] Parts, int[] Order)
        {
            if (Parts is null || Order is null || Parts.Length != Order.Length)
                throw new CryptographicException("Invalid embedded credential");
            var Seen = new bool[Parts.Length];
            var Encoded = new StringBuilder();
            foreach (var Index in Order)
            {
                if (Index < 0 || Index >= Parts.Length || Seen[Index])
                    throw new CryptographicException("Invalid embedded credential");
                Seen[Index] = true;
                Encoded.Append(Parts[Index]);
            }
            return Convert.FromBase64String(Encoded.ToString());
        }

        // v1: version(1) + IV(16) + AES-256-CBC/PKCS7 ciphertext + HMAC-SHA256(32)。
        // 独立的 AES/HMAC 密钥经两个随机份额重建；先认证后解密。
        internal static string Decode(byte[] Blob, byte[] ShareA, byte[] ShareB)
        {
            var Material = new byte[64];
            var EncryptionKey = new byte[32];
            var AuthenticationKey = new byte[32];
            byte[] Plain = null;
            try
            {
                if (Blob is null || Blob.Length < 65 || Blob[0] != 1 || (Blob.Length - 49) % 16 != 0 || ShareA is null || ShareA.Length != 64 || ShareB is null || ShareB.Length != 64)
                    throw new CryptographicException("Invalid embedded credential");
                for (int Index = 0; Index <= 63; Index++)
                    Material[Index] = checked((byte)(ShareA[Index] ^ ShareB[Index]));
                Buffer.BlockCopy(Material, 0, EncryptionKey, 0, 32);
                Buffer.BlockCopy(Material, 32, AuthenticationKey, 0, 32);
                using (var Authenticator = new HMACSHA256(AuthenticationKey))
                {
                    byte[] Tag = Authenticator.ComputeHash(Blob, 0, Blob.Length - 32);
                    int Difference = 0;
                    for (int Index = 0; Index <= 31; Index++)
                        Difference = Difference | Tag[Index] ^ Blob[Blob.Length - 32 + Index];
                    Array.Clear(Tag, 0, Tag.Length);
                    if (Difference != 0)
                        throw new CryptographicException("Invalid embedded credential");
                }
                var IV = new byte[16];
                Buffer.BlockCopy(Blob, 1, IV, 0, 16);
                using (var Cipher = Aes.Create())
                {
                    Cipher.KeySize = 256;
                    Cipher.Mode = CipherMode.CBC;
                    Cipher.Padding = PaddingMode.PKCS7;
                    Cipher.Key = EncryptionKey;
                    Cipher.IV = IV;
                    using (var Decryptor = Cipher.CreateDecryptor())
                    {
                        Plain = Decryptor.TransformFinalBlock(Blob, 17, Blob.Length - 49);
                        return new UTF8Encoding(false, true).GetString(Plain);
                    }
                }
            }
            finally
            {
                foreach (var Bytes in new byte[][] { Material, EncryptionKey, AuthenticationKey, Plain, ShareA, ShareB })
                {
                    if (Bytes is not null)
                        Array.Clear(Bytes, 0, Bytes.Length);
                }
            }
        }
    }
}