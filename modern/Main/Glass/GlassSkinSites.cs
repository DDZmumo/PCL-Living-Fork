// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Launch;

namespace PCL;

/// <summary>A character (game profile) on a skin site.</summary>
internal sealed class SkinSiteProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>One account signed in on a skin site, with the characters it owns.</summary>
internal sealed class SkinSiteAccount
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public string SelectedId { get; set; } = "";
    public List<SkinSiteProfile> Profiles { get; set; } = new();
}

/// <summary>A third-party skin site (Yggdrasil API, used through Authlib-Injector).</summary>
internal sealed class SkinSite
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Register { get; set; } = "";
    public List<SkinSiteAccount> Accounts { get; set; } = new();
}

/// <summary>A character together with the account and site it belongs to.</summary>
internal sealed record SkinSiteCharacter(SkinSite Site, SkinSiteAccount Account, SkinSiteProfile Profile)
{
    internal string Key => Site.Server + "|" + Account.Username + "|" + Profile.Id;
}

/// <summary>Thrown with a message meant for the user (server error text, unreachable address…).</summary>
internal sealed class SkinSiteException : Exception
{
    internal SkinSiteException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Third-party skin site accounts for the account page. Sites, accounts and characters are saved in
/// <c>LfSkinSites</c>. The active character refers to that store and has no dependency on instance-login credentials.
/// </summary>
internal static class GlassSkinSites
{
    private sealed record Selection(string Server, string Username, string ProfileId);
    private static readonly object StoreGate = new();
    private static readonly HttpClient Http = CreateClient(followRedirects: false);
    private static readonly HttpClient Skins = CreateClient(followRedirects: true);
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Heads = new();
    private static object? _roleSelectionOwner;

    // The selection is an in-session UI operation. It stays pending even if the account page is hidden.
    internal static bool RoleSelectionPending => _roleSelectionOwner is not null;
    internal static void BeginRoleSelection(object owner) => _roleSelectionOwner = owner;
    internal static void EndRoleSelection(object owner) {
        if (ReferenceEquals(_roleSelectionOwner, owner)) _roleSelectionOwner = null;
    }

    private static HttpClient CreateClient(bool followRedirects)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Living-Fork");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");
        return client;
    }

    // ---------- Store ----------

    internal static List<SkinSite> Load()
    {
        try
        {
            string json = Settings.Get<string>("LfSkinSites");
            return string.IsNullOrWhiteSpace(json) ? new() : JsonConvert.DeserializeObject<List<SkinSite>>(json) ?? new();
        }
        catch (Exception error) { Logger.Warn(error, "读取外置登录账号失败"); return new(); }
    }

    internal static void Save(List<SkinSite> sites)
    {
        lock (StoreGate) {
            sites.RemoveAll(site => site.Accounts.Count == 0);
            Settings.Set("LfSkinSites", sites.Count == 0 ? "" : JsonConvert.SerializeObject(sites, Formatting.None));
        }
    }

    internal static IEnumerable<SkinSiteCharacter> Characters(SkinSite site) =>
        site.Accounts.SelectMany(account => account.Profiles.Select(profile => new SkinSiteCharacter(site, account, profile)));

    /// <summary>True while a skin-site character is the account in use.</summary>
    internal static bool IsActive => Settings.Get<int>("LfSkinSiteActive") == 1;

    /// <summary>The character in use, matched in the saved sites (null when none is in use).</summary>
    internal static SkinSiteCharacter? Current(List<SkinSite> sites)
    {
        if (!IsActive) return null;
        MigrateSelection(sites);
        try {
            string saved = Settings.Get<string>("LfSkinSiteSelection");
            if (!IsActive || saved.Length == 0 || JsonConvert.DeserializeObject<Selection>(saved) is not { } selected) return null;
            return Find(sites, selected);
        } catch (JsonException) { return null; }
    }

    internal static SkinSiteCharacter? CurrentForLaunch(ModMinecraft.McInstance? instance) =>
        IsActive && (instance is null ? Settings.Get<int>("LoginPageType") : Settings.Get<int>("VersionServerLogin", instance)) == 0 ? Current(Load()) : null;

    internal static bool SameServer(string a, string b) => AuthlibEndpoint.Same(a, b);
    internal static bool SameCharacter(SkinSiteCharacter a, SkinSiteCharacter b) =>
        SameServer(a.Site.Server, b.Site.Server) && string.Equals(a.Account.Username, b.Account.Username, StringComparison.OrdinalIgnoreCase) && a.Profile.Id == b.Profile.Id;
    private static Selection Identity(SkinSiteCharacter character) => new(character.Site.Server, character.Account.Username, character.Profile.Id);
    private static SkinSiteCharacter? Find(List<SkinSite> sites, Selection selection) =>
        sites.Where(site => IsSecure(site.Server) && SameServer(site.Server, selection.Server)).SelectMany(Characters)
            .FirstOrDefault(character => string.Equals(character.Account.Username, selection.Username, StringComparison.OrdinalIgnoreCase) && character.Profile.Id == selection.ProfileId);

    // The older build saved the active identity in CacheAuth*. Recover it from the matching saved account
    // before an instance can change the shared cache. Its credentials are read only from LfSkinSites.
    internal static void MigrateSelection(List<SkinSite>? sites = null) {
        if (!IsActive || Settings.Get<string>("LfSkinSiteSelection").Length != 0) return;
        lock (StoreGate) {
            if (!IsActive || Settings.Get<string>("LfSkinSiteSelection").Length != 0) return;
            var cached = Settings.AccountSnapshot("CacheAuth");
            var selected = new Selection(cached.GetValueOrDefault("CacheAuthServerServer") as string ?? "",
                cached.GetValueOrDefault("CacheAuthUsername") as string ?? "", cached.GetValueOrDefault("CacheAuthUuid") as string ?? "");
            if (Find(sites ?? Load(), selected) is { } character) Settings.Set("LfSkinSiteSelection", JsonConvert.SerializeObject(Identity(character)));
            else Deactivate();
        }
    }

    /// <summary>Selects a saved character independently of the instance-specific login cache.</summary>
    internal static void Activate(SkinSiteCharacter character)
    {
        RequireSecureUri(character.Site.Server);
        lock (StoreGate) {
            Settings.Set("LfSkinSiteSelection", JsonConvert.SerializeObject(Identity(character)));
            Settings.Set("LfSkinSiteActive", 1);
        }
    }

    internal static void Deactivate() { lock (StoreGate) Settings.Set("LfSkinSiteActive", 0); }

    internal static async Task<ModLaunch.McLoginResult> LoginForLaunch(SkinSiteCharacter requested, Func<bool> canceled) {
        var character = CurrentForLaunch(ModMinecraft.McInstanceSelected);
        if (character is null || !SameCharacter(character, requested)) throw new SkinSiteException("外置登录角色已改变，请重新启动。");
        if (canceled()) throw new OperationCanceledException();
        string previousAccess = character.Account.AccessToken, previousClient = character.Account.ClientToken;
        await Select(character.Site, character.Account, character.Profile).ConfigureAwait(false);
        CommitCharacter(character, previousAccess, previousClient, activate: false, canceled);
        return new ModLaunch.McLoginResult { Type = "Auth", Name = character.Profile.Name, Uuid = character.Profile.Id,
            AccessToken = character.Account.AccessToken, ClientToken = character.Account.ClientToken };
    }

    internal static async Task UseCharacter(SkinSiteCharacter requested) {
        RequireSecureUri(requested.Site.Server);
        var character = Find(Load(), Identity(requested)) ?? throw new SkinSiteException("找不到该站点的角色，请重新登录。");
        string previousAccess = character.Account.AccessToken, previousClient = character.Account.ClientToken;
        await Select(character.Site, character.Account, character.Profile).ConfigureAwait(false);
        CommitCharacter(character, previousAccess, previousClient, activate: true);
    }

    private static void CommitCharacter(SkinSiteCharacter character, string previousAccess, string previousClient, bool activate, Func<bool>? canceled = null) {
        lock (StoreGate) {
            if (canceled?.Invoke() == true) throw new OperationCanceledException();
            var sites = Load();
            var saved = activate ? Find(sites, Identity(character)) : Current(sites);
            if (saved is null || !SameCharacter(saved, character)) throw new SkinSiteException("外置登录角色已改变，请重试。");
            if (saved.Account.AccessToken != previousAccess || saved.Account.ClientToken != previousClient || saved.Account.Password != character.Account.Password)
                throw new SkinSiteException("外置登录会话已更新，请重试。");
            if (saved.Account.AccessToken != character.Account.AccessToken || saved.Account.ClientToken != character.Account.ClientToken || saved.Account.SelectedId != character.Account.SelectedId) {
                saved.Account.AccessToken = character.Account.AccessToken;
                saved.Account.ClientToken = character.Account.ClientToken;
                saved.Account.SelectedId = character.Account.SelectedId;
                saved.Account.Profiles = character.Account.Profiles;
                Save(sites);
            }
            if (activate) Activate(saved);
        }
    }

    // ---------- Yggdrasil ----------

    /// <summary>The API root for what the user typed (API Location Indication followed), with the site's own name.</summary>
    private static bool IsSecure(string address) => AuthlibEndpoint.TryParse(address, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static Uri RequireSecureUri(string address) {
        if (AuthlibEndpoint.TryParse(address, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps) return uri;
        throw new SkinSiteException("外置登录需要有效的 HTTPS 验证地址。");
    }

    internal static async Task<(string Server, string Name, string Register)> Resolve(string input, HttpClient? transport = null)
    {
        string address = input.Trim();
        if (address.Length == 0) throw new SkinSiteException("请填写 Yggdrasil 地址。");
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        Uri uri = RequireSecureUri(address);
        transport ??= Http;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            for (int hop = 0; hop < 6; hop++) {
                using HttpResponseMessage response = await transport.GetAsync(uri, deadline.Token).ConfigureAwait(false);
                string? location = (int)response.StatusCode is >= 300 and < 400 ? response.Headers.Location?.OriginalString :
                    response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var locations) ? locations.FirstOrDefault() : null;
                if (!string.IsNullOrWhiteSpace(location)) {
                    if (!Uri.TryCreate(uri, location, out Uri? next)) throw new SkinSiteException("验证服务器返回的跳转地址无效。");
                    next = RequireSecureUri(next.AbsoluteUri);
                    if (next != uri) { uri = next; continue; }
                }
                response.EnsureSuccessStatusCode();
                JObject root = JObject.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                if (root["meta"] is not JObject meta) throw new SkinSiteException("这个地址不是 Yggdrasil 验证服务器。");
                return (uri.AbsoluteUri.TrimEnd('/'), meta["serverName"]?.ToString() ?? "", meta["links"]?["register"]?.ToString() ?? "");
            }
            throw new SkinSiteException("验证服务器跳转次数过多。");
        }
        catch (SkinSiteException) { throw; }
        catch (JsonException error) { throw new SkinSiteException("这个地址不是 Yggdrasil 验证服务器。", error); }
        catch (Exception error) { throw new SkinSiteException("无法连接到验证服务器：" + error.Message, error); }
    }

    /// <summary>Signs in with the account's credentials; the result lists its characters.</summary>
    internal static async Task<SkinSiteAccount> Authenticate(string server, string username, string password, HttpClient? transport = null)
    {
        var request = new JObject
        {
            ["agent"] = new JObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username, ["password"] = password, ["requestUser"] = true,
            ["clientToken"] = Guid.NewGuid().ToString("N")
        };
        JObject response = await Post(server, "authenticate", request, transport).ConfigureAwait(false);
        var account = new SkinSiteAccount
        {
            Username = username, Password = password,
            AccessToken = response["accessToken"]?.ToString() ?? "", ClientToken = response["clientToken"]?.ToString() ?? "",
            SelectedId = response["selectedProfile"]?["id"]?.ToString() ?? "",
            Profiles = (response["availableProfiles"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(profile => new SkinSiteProfile { Id = profile["id"]?.ToString() ?? "", Name = profile["name"]?.ToString() ?? "" })
                .Where(profile => profile.Id.Length > 0).ToList()
        };
        if (account.AccessToken.Length == 0 || account.ClientToken.Length == 0) throw new SkinSiteException("验证服务器未返回完整的登录会话。");
        if (account.Profiles.Count == 0) throw new SkinSiteException("这个账号还没有角色，请先在网站上创建角色。");
        return account;
    }

    /// <summary>Binds the account's token to <paramref name="profile"/> (refresh with a selected profile); signs in again when the token has expired.</summary>
    private static async Task Select(SkinSite site, SkinSiteAccount account, SkinSiteProfile profile)
    {
        RequireSecureUri(site.Server);
        if (account.SelectedId == profile.Id && account.AccessToken.Length > 0 && await Validate(site.Server, account).ConfigureAwait(false)) return;
        try
        {
            await Refresh(site.Server, account, profile).ConfigureAwait(false);
        }
        catch (SkinSiteException) when (account.Password.Length > 0)
        {
            SkinSiteAccount fresh = await Authenticate(site.Server, account.Username, account.Password).ConfigureAwait(false);
            account.AccessToken = fresh.AccessToken; account.ClientToken = fresh.ClientToken; account.SelectedId = fresh.SelectedId;
            account.Profiles = fresh.Profiles;
            if (account.SelectedId != profile.Id) await Refresh(site.Server, account, profile).ConfigureAwait(false);
        }
    }

    private static async Task Refresh(string server, SkinSiteAccount account, SkinSiteProfile profile)
    {
        var request = new JObject
        {
            ["accessToken"] = account.AccessToken, ["clientToken"] = account.ClientToken, ["requestUser"] = true,
            ["selectedProfile"] = new JObject { ["id"] = profile.Id, ["name"] = profile.Name }
        };
        JObject response = await Post(server, "refresh", request).ConfigureAwait(false);
        string? selected = response["selectedProfile"]?["id"]?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != profile.Id) throw new SkinSiteException("验证服务器没有绑定所选角色。");
        account.AccessToken = response["accessToken"]?.ToString() ?? account.AccessToken;
        account.ClientToken = response["clientToken"]?.ToString() ?? account.ClientToken;
        account.SelectedId = profile.Id;
    }

    private static async Task<bool> Validate(string server, SkinSiteAccount account)
    {
        try
        {
            using var content = new StringContent(new JObject { ["accessToken"] = account.AccessToken, ["clientToken"] = account.ClientToken }.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.PostAsync(CredentialUri(server, "validate"), content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    private static Uri CredentialUri(string server, string operation) => new(AuthlibEndpoint.ForServer(RequireSecureUri(server).AbsoluteUri) + "/" + operation);

    private static async Task<JObject> Post(string server, string operation, JObject body, HttpClient? transport = null)
    {
        Uri url = CredentialUri(server, operation);
        try
        {
            using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await (transport ?? Http).PostAsync(url, content).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) throw new SkinSiteException("验证服务器重定向了登录请求，请填写最终的 HTTPS API 地址。");
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JObject? json = null;
            try { json = JObject.Parse(text); } catch (JsonException) { }
            if (!response.IsSuccessStatusCode || json is null)
            {
                string message = json?["errorMessage"]?.ToString() ?? json?["error"]?.ToString() ?? $"验证服务器返回了错误（{(int)response.StatusCode}）";
                if (message.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) || message.Contains("Incorrect username or password", StringComparison.OrdinalIgnoreCase))
                    message = "账号或密码错误。";
                throw new SkinSiteException(message);
            }
            return json;
        }
        catch (SkinSiteException) { throw; }
        catch (Exception error) { throw new SkinSiteException("无法连接到验证服务器：" + error.Message, error); }
    }

    // ---------- Heads ----------

    /// <summary>The character's head with its hat layer (cached per site and character); null when it has no skin.</summary>
    internal static Task<BitmapSource?> Head(string server, string uuid) {
        var request = new LegacySkinRequest("", uuid, server, UsesSkinSite: true);
        if (GlassSkinHead.LastGood("Auth", request) is { } known) return Task.FromResult<BitmapSource?>(known);
        string key = server + "|" + uuid;
        if (Heads.TryGetValue(key, out var previous) && previous.IsCompletedSuccessfully && previous.Result is null)
            Heads.TryRemove(new KeyValuePair<string, Task<BitmapSource?>>(key, previous));
        return Heads.GetOrAdd(key, _ => LoadHead(request));
    }

    private static async Task<BitmapSource?> LoadHead(LegacySkinRequest request)
    {
        try
        {
            string? skinUrl = await SkinAddress(request.Server, request.Id);
            if (string.IsNullOrEmpty(skinUrl)) return GlassSkinHead.LastGood("Auth", request);
            byte[] bytes = await Skins.GetByteArrayAsync(skinUrl);
            var head = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new System.IO.MemoryStream(bytes); image.EndInit(); image.Freeze();
                double unit = image.PixelWidth / 64d;
                BitmapSource Crop(int x, int y) { var crop = new CroppedBitmap(image, new Int32Rect((int)(x * unit), (int)(y * unit), (int)(8 * unit), (int)(8 * unit))); crop.Freeze(); return crop; }
                return GlassSkinHead.Render(Crop(8, 8), Crop(40, 8));
            });
            if (head is not null) GlassSkinHead.Remember("Auth", request, head);
            return head ?? GlassSkinHead.LastGood("Auth", request);
        }
        catch (Exception error) { Logger.Info(error, "读取外置登录角色头像失败"); return GlassSkinHead.LastGood("Auth", request); }
    }

    internal static async Task<string?> SkinAddress(string server, string uuid) {
        Uri root = RequireSecureUri(server);
        JObject profile = JObject.Parse(await Skins.GetStringAsync(root.AbsoluteUri.TrimEnd('/') + "/sessionserver/session/minecraft/profile/" + Uri.EscapeDataString(uuid)).ConfigureAwait(false));
        string? encoded = (profile["properties"] as JArray)?.OfType<JObject>().FirstOrDefault(item => item["name"]?.ToString() == "textures")?["value"]?.ToString();
        if (encoded is null) return null;
        return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))["textures"]?["SKIN"]?["url"]?.ToString();
    }
}
