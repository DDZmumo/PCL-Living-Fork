// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System.Net.Http.Headers;
using PCLCS.Applications;
using PCLLF.Presentation.Launch;

namespace PCL;

public sealed record SkinLibraryTarget(AccountProvider Provider, string Name, string ProfileId, string Server);
public sealed record SkinLibraryAccount(SkinLibraryTarget? Target, ImageSource? Avatar, bool CanImportCurrent,
    OfflineSkinAppearance OfflineAppearance, Uri? Wardrobe);
public sealed record SkinLibraryPreview(BitmapSource Texture, BitmapSource? Avatar, SkinModel Model);
public sealed record MicrosoftCape(string Id, string Name, bool Active, Uri? Texture);

/// <summary>File, preview and account operations for the WPF skin library.</summary>
public sealed class SkinLibraryApplicationService {
    private readonly Lazy<SkinLibraryStore> library;
    private static readonly HttpClient Microsoft = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
        BaseAddress = new("https://api.minecraftservices.com/minecraft/profile/"), Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 256 * 1024
    };
    private readonly SemaphoreSlim microsoftOperations = new(1, 1);
    internal SkinLibraryStore Store => library.Value;
    internal SkinLibraryApplicationService(SkinLibraryStore? store = null) {
        library = new(() => store ?? new(Path.Combine(Paths.AppDataThenName, "SkinLibrary"), png => Decode(png)));
    }

    public string DirectoryFor(SkinTextureKind kind) => Store.DirectoryFor(kind);
    public IReadOnlyList<SkinLibraryEntry> List(SkinTextureKind kind = SkinTextureKind.Skin) => Store.List(kind);
    public SkinImportResult Import(IEnumerable<string> files, SkinTextureKind kind = SkinTextureKind.Skin) => Store.Import(files, kind);
    public SkinLibraryEntry SetModel(string id, SkinModel model) => Store.SetModel(id, model);

    public SkinLibraryEntry Rename(string id, string name, SkinTextureKind kind = SkinTextureKind.Skin) {
        VerifyAccess();
        var entry = Store.Rename(id, name, kind);
        if (kind == SkinTextureKind.Skin) RefreshOfflineAvatar();
        return entry;
    }

    public void Remove(string id, SkinTextureKind kind = SkinTextureKind.Skin) {
        VerifyAccess();
        Store.Remove(id, kind);
        if (kind == SkinTextureKind.Skin) RefreshOfflineAvatar();
    }

    public SkinLibraryPreview LoadPreview(string id, SkinTextureKind kind = SkinTextureKind.Skin) => Preview(Store.ReadTexture(id, kind), kind);

    internal static SkinLibraryPreview Preview(SkinLibraryTexture texture, SkinTextureKind kind) {
        var bitmap = Decode(texture.Png);
        BitmapSource? avatar = kind == SkinTextureKind.Skin ? RenderAvatar(bitmap) : null;
        return new(bitmap, avatar, texture.Model);
    }

    private static BitmapSource? RenderAvatar(BitmapSource bitmap) => GlassSkinHead.Render(
        new CroppedBitmap(bitmap, new Int32Rect(8, 8, 8, 8)), new CroppedBitmap(bitmap, new Int32Rect(40, 8, 8, 8)));

    internal async Task<BitmapSource?> ReadOfflineAvatarAsync(string player) {
        var avatar = await Task.Run(() => {
            try {
                return Store.GetOfflineAppearance(player).SkinId is { } id ? LoadPreview(id).Avatar : null;
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
                return null;
            }
        });
        if (avatar is not null) return avatar;
        try {
            var path = new Uri(LauncherSkinSessions.Skins.OfflineDefaultSkin(player));
            using var stream = System.Windows.Application.GetResourceStream(path)?.Stream;
            return stream is null ? null : RenderAvatar(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
        } catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException) {
            return null;
        }
    }

    public SkinLibraryAccount ReadAccount() => ReadAccount(null);

    internal SkinLibraryAccount ReadAccount(string? offlinePlayer) {
        VerifyAccess();
        var active = ReadTarget();
        var selected = offlinePlayer is not null && ReadOfflinePlayers().Contains(offlinePlayer, StringComparer.Ordinal)
            ? OfflineTarget(offlinePlayer) : active;
        if (selected is null) return new(null, null, false, new(), null);
        OfflineSkinAppearance appearance = selected.Provider == AccountProvider.Offline ? Store.GetOfflineAppearance(selected.Name) : new();
        if (selected != active)
            return new(selected, null, Store.FindOfflineSkinFile(selected.Name) is not null, appearance, null);
        string kind = LoaderKind(selected.Provider);
        var request = LauncherSkinSessions.Skins.Input(kind);
        return new(selected, LauncherSkinSessions.Avatar(kind, request), LauncherSkinSessions.TextureFile(kind, request) is not null,
            appearance, WardrobeFor(selected));
    }

    internal static string[] ReadOfflinePlayers() {
        VerifyAccess();
        if (Settings.Get<ModLaunch.McLoginType>("LoginType") != ModLaunch.McLoginType.Legacy) return [];
        return (Settings.Get<string>("LoginLegacyName") ?? "")
            .Split('¨', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(LauncherSkinSessions.Skins.OfflineAccountName())
            .Where(player => player.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SkinLibraryEntry ImportCurrent(SkinLibraryTarget target, string name, SkinModel? model = null) {
        RequireTarget(target);
        if (target.Provider == AccountProvider.Offline) {
            if (Store.ReadOfflineTextures(target.Name)?.Skin is { } local)
                return Store.ImportTexture(name, local.Png, model: model ?? local.Model);
            if (ReadTarget() != target) throw new InvalidOperationException("这个离线角色还没有设置自定义皮肤。");
        }
        string kind = LoaderKind(target.Provider);
        string path = LauncherSkinSessions.TextureFile(kind, LauncherSkinSessions.Skins.Input(kind))
            ?? throw new InvalidOperationException("当前角色的原始皮肤还没有加载完成。");
        using var input = path.StartsWith("pack://", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Application.GetResourceStream(new Uri(path))?.Stream ?? throw new FileNotFoundException("当前皮肤资源未找到。")
            : File.OpenRead(path);
        if (input.Length > SkinLibraryStore.MaximumTextureBytes) throw new IOException("当前皮肤文件过大。");
        byte[] png = new byte[(int)input.Length];
        input.ReadExactly(png);
        SkinModel variant = target.Provider == AccountProvider.Microsoft &&
            ReadCachedProfile(target.ProfileId)?["skins"]?.OfType<JObject>().Any(skin =>
                (string?)skin["state"] == "ACTIVE" && string.Equals((string?)skin["variant"], "SLIM", StringComparison.OrdinalIgnoreCase)) == true
            ? SkinModel.Slim : SkinModel.Classic;
        return Store.ImportTexture(name, png, model: model ?? variant);
    }

    /// <returns>A wardrobe URL when the chosen skin must be uploaded through a skin site.</returns>
    public async Task<Uri?> ApplySkinAsync(SkinLibraryTarget target, string id, CancellationToken cancellationToken = default) {
        RequireTarget(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Provider == AccountProvider.Offline) { SetOfflineTexture(target, SkinTextureKind.Skin, id); return null; }
        if (target.Provider == AccountProvider.Auth) return WardrobeFor(target) ?? throw new InvalidOperationException("该外置登录站点未配置皮肤衣柜。");
        var texture = Store.ReadTexture(id);
        await WithMicrosoftAsync(target, async token => {
            using var request = new HttpRequestMessage(HttpMethod.Post, "skins");
            var png = new ByteArrayContent(texture.Png);
            png.Headers.ContentType = new("image/png");
            request.Content = new MultipartFormDataContent {
                { new StringContent(texture.Model == SkinModel.Slim ? "slim" : "classic"), "variant" }, { png, "file", "skin.png" }
            };
            var profile = await ReadProfileAsync(request, token, target.ProfileId, cancellationToken);
            PublishMicrosoftProfile(target, token, profile);
            var active = (profile["skins"] as JArray)?.OfType<JObject>().FirstOrDefault(skin => (string?)skin["state"] == "ACTIVE");
            if (SafeHttps((string?)active?["url"]) is { } url)
                ModBase.WriteIni(Path.Combine(ModBase.PathTemp, "Cache", "Skin", "IndexMs.ini"), target.ProfileId, url.AbsoluteUri);
            LauncherSkinLoaders.SkinMs.Start(IsForceRestart: true);
            return true;
        }, cancellationToken);
        return null;
    }

    public void SetOfflineTexture(SkinLibraryTarget target, SkinTextureKind kind, string? id) {
        RequireTarget(target);
        if (target.Provider != AccountProvider.Offline) throw new InvalidOperationException("此操作仅适用于离线角色。");
        Store.SetOfflineTexture(target.Name, kind, id);
        if (kind == SkinTextureKind.Skin && ReadTarget() == target) RefreshOfflineAvatar();
    }

    public Task<IReadOnlyList<MicrosoftCape>> GetMicrosoftCapesAsync(SkinLibraryTarget target, CancellationToken cancellationToken = default) =>
        WithMicrosoftAsync<IReadOnlyList<MicrosoftCape>>(target, async token => {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/minecraft/profile");
            var profile = await ReadProfileAsync(request, token, target.ProfileId, cancellationToken);
            PublishMicrosoftProfile(target, token, profile);
            return Capes(profile);
        }, cancellationToken);

    public Task<IReadOnlyList<MicrosoftCape>> ApplyMicrosoftCapeAsync(SkinLibraryTarget target, string? capeId, CancellationToken cancellationToken = default) =>
        WithMicrosoftAsync<IReadOnlyList<MicrosoftCape>>(target, async token => {
            var profile = ReadCachedProfile(target.ProfileId);
            if (profile is null || capeId is not null && !Capes(profile).Any(cape => cape.Id == capeId))
                throw new InvalidOperationException("请先刷新此正版账号的披风列表。");
            using var request = new HttpRequestMessage(capeId is null ? HttpMethod.Delete : HttpMethod.Put, "capes/active");
            if (capeId is not null) request.Content = new StringContent(new JObject { ["capeId"] = capeId }.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await SendMicrosoftAsync(request, token, cancellationToken);
            foreach (var cape in (profile["capes"] as JArray ?? []).OfType<JObject>())
                cape["state"] = (string?)cape["id"] == capeId && capeId is not null ? "ACTIVE" : "INACTIVE";
            PublishMicrosoftProfile(target, token, profile);
            return Capes(profile);
        }, cancellationToken);

    private static SkinLibraryTarget? ReadTarget() {
        var account = LauncherApplicationServices.Accounts.ReadSnapshot();
        if (account.Provider == AccountProvider.Offline) {
            string player = LauncherSkinSessions.Skins.OfflineAccountName();
            return player.Length > 0 ? OfflineTarget(player) : null;
        }
        if (!account.Authenticated || account.Provider == AccountProvider.Auth && GlassSkinSites.RoleSelectionPending) return null;
        var request = LauncherSkinSessions.Skins.Input(LoaderKind(account.Provider));
        return request.Name is { Length: > 0 } && Guid.TryParse(request.Id, out Guid profile) && profile != Guid.Empty
            ? new(account.Provider, request.Name, request.Id, request.Server) : null;
    }

    private static void VerifyAccess() => System.Windows.Application.Current?.Dispatcher.VerifyAccess();
    private static SkinLibraryTarget OfflineTarget(string player) => new(AccountProvider.Offline, player, "", "");

    private static void RequireTarget(SkinLibraryTarget target) {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(target);
        if (target == OfflineTarget(target.Name) && ReadOfflinePlayers().Contains(target.Name, StringComparer.Ordinal)) return;
        if (ReadTarget() != target) throw new InvalidOperationException("游戏账号或角色已切换，请重新选择。");
    }

    private static string LoaderKind(AccountProvider provider) => provider switch {
        AccountProvider.Offline => "Legacy", AccountProvider.Microsoft => "Ms", AccountProvider.Auth => "Auth", AccountProvider.Nide => "Nide",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    internal static string? FindOfflineSkinFile(string name) {
        if (name.Length == 0) return null;
        try { return LauncherApplicationServices.SkinLibrary.Store.FindOfflineSkinFile(name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static void RefreshOfflineAvatar() {
        if (Settings.Get<ModLaunch.McLoginType>("LoginType") == ModLaunch.McLoginType.Legacy)
            LauncherSkinLoaders.SkinLegacy.Start(IsForceRestart: true);
    }

    private static BitmapSource Decode(byte[] png) {
        try {
            using var input = new MemoryStream(png, writable: false);
            var frame = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        } catch (Exception error) when (error is FileFormatException or NotSupportedException or ArgumentException) {
            throw new IOException("无法读取此 PNG 纹理。", error);
        }
    }

    private static Uri? WardrobeFor(SkinLibraryTarget target) {
        if (target.Provider != AccountProvider.Auth || SafeHttps(target.Server) is not { } server) return null;
        int index = server.AbsolutePath.IndexOf("/api/yggdrasil", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        return new UriBuilder(server) { Path = server.AbsolutePath[..index] + "/user/closet", Query = "", Fragment = "" }.Uri;
    }

    // Mojang serves texture addresses as http://textures.minecraft.net; that host also answers on https, so use it.
    private static Uri? SafeHttps(string? value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.UserInfo.Length != 0) return null;
        if (address.Scheme == Uri.UriSchemeHttp && address.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
            address = new UriBuilder(address) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        return address.Scheme == Uri.UriSchemeHttps ? address : null;
    }

    private static T? Cache<T>(Dictionary<string, object> fields, string key) where T : class => fields.GetValueOrDefault("CacheMsV2" + key) as T;
    private static bool SameProfile(string? first, string? second) => Guid.TryParse(first, out Guid a) && Guid.TryParse(second, out Guid b) && a == b;

    private async Task<T> WithMicrosoftAsync<T>(SkinLibraryTarget target, Func<string, Task<T>> action, CancellationToken cancellationToken) {
        RequireTarget(target);
        if (target.Provider != AccountProvider.Microsoft) throw new InvalidOperationException("此操作仅适用于 Microsoft 正版账号。");
        await microsoftOperations.WaitAsync(cancellationToken);
        try {
            RequireTarget(target);
            var fields = Settings.AccountSnapshot("CacheMsV2");
            if (!SameProfile(target.ProfileId, Cache<string>(fields, "Uuid")) || Cache<string>(fields, "Access") is not { Length: > 0 } token)
                throw new InvalidOperationException("请重新登录此 Microsoft 账号后重试。");
            return await action(token);
        } finally { microsoftOperations.Release(); }
    }

    private static JObject? ReadCachedProfile(string id) {
        var fields = Settings.AccountSnapshot("CacheMsV2");
        if (!SameProfile(id, Cache<string>(fields, "Uuid")) || Cache<string>(fields, "ProfileJson") is not { Length: > 0 } text) return null;
        try {
            var profile = JObject.Parse(text);
            return SameProfile(id, (string?)profile["id"]) ? profile : null;
        } catch (Newtonsoft.Json.JsonException) { return null; }
    }

    private static IReadOnlyList<MicrosoftCape> Capes(JObject profile) => (profile["capes"] as JArray ?? []).OfType<JObject>()
        .Where(cape => (string?)cape["id"] is { Length: > 0 })
        .Select(cape => new MicrosoftCape((string)cape["id"]!, (string?)cape["alias"] ?? "披风", (string?)cape["state"] == "ACTIVE", SafeHttps((string?)cape["url"]))).ToArray();

    private static void PublishMicrosoftProfile(SkinLibraryTarget target, string token, JObject profile) {
        RequireTarget(target);
        Settings.SaveAccountGroup(new Dictionary<string, object> { ["CacheMsV2ProfileJson"] = profile.ToString(Formatting.None) }, () => {
            var fields = Settings.AccountSnapshot("CacheMsV2");
            return !SameProfile(Cache<string>(fields, "Uuid"), target.ProfileId) || Cache<string>(fields, "Access") != token;
        });
    }

    private static async Task<JObject> ReadProfileAsync(HttpRequestMessage request, string token, string id, CancellationToken cancellationToken) {
        using var response = await SendMicrosoftAsync(request, token, cancellationToken);
        var profile = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!SameProfile(id, (string?)profile["id"])) throw new IOException("Minecraft 服务返回了其他账号的档案。");
        return profile;
    }

    private static async Task<HttpResponseMessage> SendMicrosoftAsync(HttpRequestMessage request, string token, CancellationToken cancellationToken) {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Microsoft.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.Unauthorized) throw new InvalidOperationException("请重新登录此 Microsoft 账号后重试。");
        throw new HttpRequestException($"Minecraft 外观服务请求失败（HTTP {(int)status}）。", null, status);
    }
}
