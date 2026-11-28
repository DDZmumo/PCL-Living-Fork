// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;
using PCLCS.Applications;
using LoginType = PCL.ModLaunch.McLoginType;

namespace PCL;

/// <summary>Account commands and stored launch input for the glass workspace.</summary>
internal static class LauncherAccountSession {
    private sealed record ExternalCharacterSwitch(object Owner, Dispatcher Dispatcher);
    private static ExternalCharacterSwitch? _externalCharacterSwitch;
    private static LoginType _primary = LoginType.Legacy;
    internal static LoginType Current => Settings.Get<LoginType>("LoginType");
    internal static bool MicrosoftSignedIn => !string.IsNullOrEmpty(Settings.Get<string>("CacheMsV2OAuthRefresh"));
    internal static string OfflineName => (Settings.Get<string>("LoginLegacyName") ?? "").BeforeFirst("¨").Trim();
    internal static bool ExternalCharacterSwitchPending => Volatile.Read(ref _externalCharacterSwitch) is not null;

    internal static void Initialize() {
        _primary = Current == LoginType.Ms ? LoginType.Ms : LoginType.Legacy;
        ApplyExternalSelection();
    }

    internal static void ApplyExternalSelection() {
        LoginType provider = GlassSkinSites.CurrentForLaunch() is null ? _primary : LoginType.Auth;
        if (Current != provider) Settings.Set("LoginType", provider);
    }

    internal static async Task<bool> SelectExternalCharacterAsync(object owner, SkinSiteCharacter character, Dispatcher dispatcher,
        Func<bool> ownerClosed, HttpClient? transport = null) {
        dispatcher.VerifyAccess();
        if (ownerClosed() || !CanChangeAccount()) return false;
        var request = new ExternalCharacterSwitch(owner, dispatcher);
        Volatile.Write(ref _externalCharacterSwitch, request);
        try {
            await GlassSkinSites.UseCharacter(character, dispatcher,
                () => !OwnsRequest() || ownerClosed() || LauncherApplicationServices.IsLaunchActive() ||
                    LauncherApplicationServices.IsAccountLoaderActive(), transport).ConfigureAwait(false);
            return true;
        } catch (OperationCanceledException) { return false; }
        catch (Exception) when (!OwnsRequest()) { return false; }
        finally { Interlocked.CompareExchange(ref _externalCharacterSwitch, null, request); }

        bool OwnsRequest() => ReferenceEquals(Volatile.Read(ref _externalCharacterSwitch), request);
    }

    internal static void CancelExternalCharacterSwitch(object owner) {
        var request = Volatile.Read(ref _externalCharacterSwitch);
        if (request is null || !ReferenceEquals(request.Owner, owner)) return;
        request.Dispatcher.VerifyAccess();
        Interlocked.CompareExchange(ref _externalCharacterSwitch, null, request);
    }

    internal static void LoadActiveSkin(bool force = false) {
        switch (Current) {
            case LoginType.Legacy: LauncherSkinLoaders.SkinLegacy.Start(IsForceRestart: force); break;
            case LoginType.Ms when MicrosoftSignedIn: LauncherSkinLoaders.SkinMs.Start(IsForceRestart: force); break;
            case LoginType.Auth when GlassSkinSites.IsActive: LauncherSkinLoaders.SkinAuth.Start(IsForceRestart: force); break;
        }
    }

    internal static bool SelectPrimary(LoginType provider) {
        if (provider is not (LoginType.Legacy or LoginType.Ms)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (!CanChangeAccount()) return false;
        if (Current != provider) Settings.Set("LoginType", provider);
        _primary = provider;
        if (GlassSkinSites.IsActive) GlassSkinSites.Deactivate();
        LoadActiveSkin();
        return true;
    }

    internal static void SelectOffline(string name) {
        string player = name.Replace("¨", "").Trim();
        if (player.Length == 0 || Current != LoginType.Legacy || !CanChangeAccount()) return;
        Settings.UpdateLegacyNames(items => {
            items.RemoveAll(item => item.Equals(player, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, player);
        });
        LauncherSkinLoaders.SkinLegacy.Start();
    }

    internal static bool RemoveSavedAccount(string name, bool microsoft) {
        if (!CanChangeAccount()) return false;
        if (microsoft) Settings.UpdateMicrosoftAccounts(accounts => accounts.Property(name, StringComparison.OrdinalIgnoreCase)?.Remove());
        else {
            Settings.UpdateLegacyNames(items => items.RemoveAll(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)));
            LauncherSkinLoaders.SkinLegacy.Start();
        }
        return true;
    }

    internal static IReadOnlyList<string> SavedMicrosoftNames() =>
        ReadMicrosoftAccounts()?.Properties().Select(property => property.Name).Where(name => name.Length > 0).ToArray() ?? [];

    internal static ModLaunch.McLoginLegacy OfflineLaunchInput() => new() {
        UserName = OfflineName, SkinType = Settings.Get<int>("LaunchSkinType"), SkinName = Settings.Get<string>("LaunchSkinID")
    };

    internal static string ValidateOffline(ModLaunch.McLoginLegacy login) =>
        OfflineLoginPolicy.ValidateName(login.UserName, () =>
            LauncherApplicationServices.ReadLaunchInstance() is { } instance && instance.Version.Valid ? instance.Version.Vanilla : null);

    internal static ModLaunch.McLoginMs MicrosoftLaunchInput() {
        var cache = Settings.AccountSnapshot("CacheMsV2");
        var input = new ModLaunch.McLoginMs {
            UserName = cache.GetValueOrDefault("CacheMsV2Name") as string ?? "",
            OAuthRefreshToken = cache.GetValueOrDefault("CacheMsV2OAuthRefresh") as string ?? ""
        };
        if (ModLaunch.McLoginMsLoader.State == ModBase.LoadState.Finished) {
            input.AccessToken = cache.GetValueOrDefault("CacheMsV2Access") as string ?? "";
            input.Uuid = cache.GetValueOrDefault("CacheMsV2Uuid") as string ?? "";
            input.ProfileJson = cache.GetValueOrDefault("CacheMsV2ProfileJson") as string ?? "";
        }
        return input;
    }

    internal static string ValidateMicrosoft(ModLaunch.McLoginMs login) =>
        string.IsNullOrEmpty(login.OAuthRefreshToken) ? "请在登录账号后再启动游戏！" : "";

    internal static async Task SignInMicrosoftAsync(string? accountName) {
        if (Current != LoginType.Ms || !CanChangeAccount()) return;
        try {
            var input = new ModLaunch.McLoginMs();
            if (accountName is not null) {
                var saved = ReadMicrosoftAccounts()?.Property(accountName, StringComparison.OrdinalIgnoreCase);
                if (saved?.Value is not JValue { Type: JTokenType.String } token || string.IsNullOrWhiteSpace((string?)token)) {
                    ModMain.Hint("此账号的登录信息已失效，请重新登录。", ModMain.HintType.Red);
                    return;
                }
                input.UserName = saved.Name;
                input.OAuthRefreshToken = (string?)token;
            }
            if (MicrosoftSignedIn) LauncherApplicationServices.Accounts.SignOut(AccountProvider.Microsoft);
            var loader = ModLaunch.McLoginMsLoader;
            loader.Start(input, IsForceRestart: true);
            await Task.Run(() => loader.WaitForExit(StartIfNeeded: false));
            if (Current == LoginType.Ms && MicrosoftSignedIn) LoadActiveSkin(force: true);
        } catch (Exception error) {
            if (error.IsCanceled()) return;
            Exception cause = error.GetBaseException();
            if (cause.Message.StartsWith('$')) ModMain.Hint(cause.Message.TrimStart('$'), ModMain.HintType.Red);
            else if (cause is AuthenticationException && cause.Message.Contains("SSL/TLS", StringComparison.Ordinal))
                Logger.Error(cause, "正版登录验证失败，请考虑在 [设置 → 其他] 中关闭 [在正版登录时验证 SSL 证书]，然后再试。", LogBehavior.Alert);
            else Logger.Error(error, "正版登录尝试失败", LogBehavior.Alert);
        }
    }

    internal static void SignOutMicrosoft() {
        if (!CanChangeAccount()) return;
        try { LauncherApplicationServices.Accounts.SignOut(AccountProvider.Microsoft); }
        catch (Exception) { Logger.Error("退出登录未能保存，原有账号状态已保留。请稍后重试。", LogBehavior.Toast); }
    }

    internal static bool CanChangeAccount() {
        if (!LauncherApplicationServices.IsLaunchActive() && !LauncherApplicationServices.IsAccountOperationActive()) return true;
        ModMain.Hint("登录或启动任务尚未结束，请稍后再切换账号。", ModMain.HintType.Red);
        return false;
    }

    private static JObject? ReadMicrosoftAccounts() {
        try {
            string json = Settings.Get<string>("LoginMsJson");
            return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
        } catch (Newtonsoft.Json.JsonException) { return null; }
    }
}
