// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS.Applications;

public sealed record LocalSkinLaunch(string Player, Guid ProfileId, OfflineSkinTextures Textures);
public sealed record LocalSkinEndpoint(Uri Address, string Metadata);

/// <summary>Serves one offline player's signed texture profile and PNG files on a private loopback URL.</summary>
public sealed class LocalYggdrasilServer : IAsyncDisposable {
    private const string Json = "application/json; charset=utf-8";
    private const string Png = "image/png";
    private const string ProfileRoute = "sessionserver/session/minecraft/profile/";
    private const string LegacySkinRoute = "skins/MinecraftSkins/";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly string scope = "/" + Guid.NewGuid().ToString("N") + "/";
    private readonly LocalSkinLaunch appearance;
    private readonly string profileId;
    private readonly RSA key;
    private readonly Dictionary<string, byte[]> images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> textureProperties = new(StringComparer.Ordinal);
    private readonly Task serving;
    private int disposed;
    public LocalSkinEndpoint Endpoint { get; }

    public LocalYggdrasilServer(LocalSkinLaunch appearance) {
        if (appearance.Player is not { Length: > 0 and <= 256 } || appearance.Player.Any(char.IsControl) ||
            appearance.ProfileId == Guid.Empty || appearance.Textures is not { } textures || textures is { Skin: null, Cape: null })
            throw new ArgumentException("离线皮肤服务缺少有效的角色或纹理。", nameof(appearance));
        var skin = textures.Skin is { } texture ? texture with { Png = (byte[])texture.Png.Clone() } : null;
        byte[]? cape = textures.Cape is { } png ? (byte[])png.Clone() : null;
        if (skin is not null) SkinLibraryStore.TextureSize(skin.Png, SkinTextureKind.Skin);
        if (cape is not null) SkinLibraryStore.TextureSize(cape, SkinTextureKind.Cape);
        this.appearance = appearance with { Textures = new(skin, cape) };
        profileId = appearance.ProfileId.ToString("N");
        key = RSA.Create(2048);
        try {
            listener.Start(backlog: 8);
            var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{scope}");
            if (skin is not null) {
                string url = AddTexture(address, skin.Png);
                textureProperties["SKIN"] = skin.Model == SkinModel.Slim ? new { url, metadata = new { model = "slim" } } : new { url };
            }
            if (cape is not null) textureProperties["CAPE"] = new { url = AddTexture(address, cape) };
            Endpoint = new(address, JsonSerializer.Serialize(new {
                meta = new Dictionary<string, object> {
                    ["serverName"] = "PCL Living Fork", ["feature.legacy_skin_api"] = true, ["feature.no_mojang_namespace"] = true
                },
                skinDomains = new[] { "127.0.0.1" },
                signaturePublickey = key.ExportSubjectPublicKeyInfoPem()
            }));
            serving = ServeAsync(stopping.Token);
        } catch {
            listener.Stop(); key.Dispose(); stopping.Dispose(); throw;
        }
    }

    private string AddTexture(Uri address, byte[] png) {
        string path = "textures/" + Convert.ToHexStringLower(SHA256.HashData(png));
        images[path] = png;
        return new Uri(address, path).AbsoluteUri;
    }

    private async Task ServeAsync(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await RespondAsync(client.GetStream(), deadline.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task RespondAsync(NetworkStream stream, CancellationToken token) {
        byte[] header = new byte[8192];
        int used = 0;
        while (used < header.Length) {
            int received = await stream.ReadAsync(header.AsMemory(used), token).ConfigureAwait(false);
            if (received == 0) return;
            used += received;
            if (header.AsSpan(0, used).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        if (header.AsSpan(0, used).IndexOf("\r\n\r\n"u8) < 0) return;
        int end = header.AsSpan(0, used).IndexOf("\r\n"u8);
        string[] request = Encoding.ASCII.GetString(header, 0, end).Split(' ');
        if (request.Length != 3 || request[2] is not ("HTTP/1.0" or "HTTP/1.1")) return;
        bool head = request[0] == "HEAD";
        var response = request[0] is "GET" or "HEAD" ? Route(request[1]) : (Status: "405 Method Not Allowed", Type: Json, Body: Array.Empty<byte>());
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status}\r\nContent-Type: {response.Type}\r\n" +
            $"Content-Length: {response.Body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n");
        await stream.WriteAsync(headers, token).ConfigureAwait(false);
        if (!head && response.Body.Length > 0) await stream.WriteAsync(response.Body, token).ConfigureAwait(false);
    }

    private (string Status, string Type, byte[] Body) Route(string target) {
        if (!target.StartsWith(scope, StringComparison.Ordinal)) return ("404 Not Found", Json, []);
        string[] path = target[scope.Length..].Split('?', 2);
        string route = path[0];
        if (route.Length == 0) return ("200 OK", Json, Encoding.UTF8.GetBytes(Endpoint.Metadata));
        if (images.TryGetValue(route, out var image)) return ("200 OK", Png, image);
        if (route.StartsWith(ProfileRoute, StringComparison.Ordinal)) {
            if (!route[ProfileRoute.Length..].Equals(profileId, StringComparison.OrdinalIgnoreCase)) return ("204 No Content", Json, []);
            bool signed = path.Length == 2 && path[1].Split('&').Contains("unsigned=false", StringComparer.OrdinalIgnoreCase);
            return ("200 OK", Json, Profile(signed));
        }
        if (route.StartsWith(LegacySkinRoute, StringComparison.Ordinal) && appearance.Textures.Skin is { } skin &&
            string.Equals(Uri.UnescapeDataString(route[LegacySkinRoute.Length..]), appearance.Player + ".png", StringComparison.OrdinalIgnoreCase))
            return ("200 OK", Png, skin.Png);
        foreach (string prefix in new[] { "api/users/profiles/minecraft/", "minecraftservices/minecraft/profile/lookup/name/" }) {
            if (!route.StartsWith(prefix, StringComparison.Ordinal)) continue;
            return string.Equals(Uri.UnescapeDataString(route[prefix.Length..]), appearance.Player, StringComparison.OrdinalIgnoreCase)
                ? ("200 OK", Json, JsonSerializer.SerializeToUtf8Bytes(new { id = profileId, name = appearance.Player }))
                : ("204 No Content", Json, []);
        }
        return ("404 Not Found", Json, []);
    }

    private byte[] Profile(bool signed) {
        string value = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), profileId, profileName = appearance.Player, textures = textureProperties
        }));
        object properties = signed ? new[] { new { name = "textures", value, signature = Convert.ToBase64String(
            key.SignData(Encoding.ASCII.GetBytes(value), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)) } }
            : new[] { new { name = "textures", value } };
        return JsonSerializer.SerializeToUtf8Bytes(new { id = profileId, name = appearance.Player, properties });
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await stopping.CancelAsync().ConfigureAwait(false);
        try { await serving.ConfigureAwait(false); }
        finally { listener.Stop(); stopping.Dispose(); key.Dispose(); }
    }
}
