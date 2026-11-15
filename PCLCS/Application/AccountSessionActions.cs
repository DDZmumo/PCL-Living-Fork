using System;
using System.Collections.Generic;

namespace PCLCS.Applications;

// Existing logout transaction extracted from presentation/Accounts/LegacyLoggedInPages.cs
// at 74031b6da49d6efa0cfe453f41285ee1b2b3b333. Retains the inherited root LICENCE.
public sealed class AccountSignOutHost {
    public Action CancelMicrosoft = null!, CancelAuth = null!, CancelNide = null!;
    public Action ResetAuthInput = null!, ResetNideInput = null!;
    public Action<Dictionary<string, object?>> Save = null!;
}

/// <summary>One logout policy for the application API and the existing logged-in page adapters.</summary>
public static class AccountSessionActions {
    public static void SignOut(AccountProvider provider, AccountSignOutHost host) {
        switch (provider) {
            case AccountProvider.Microsoft:
                host.CancelMicrosoft();
                host.Save(new Dictionary<string, object?> {
                    ["CacheMsV2OAuthRefresh"] = "", ["CacheMsV2Access"] = "", ["CacheMsV2ProfileJson"] = "",
                    ["CacheMsV2Uuid"] = "", ["CacheMsV2Name"] = "", ["CacheMsV2Expires"] = 0L, ["CacheMsV2Migrated"] = true
                });
                break;
            case AccountProvider.Auth:
                host.CancelAuth();
                host.Save(new Dictionary<string, object?> { ["CacheAuthAccess"] = "", ["CacheAuthUuid"] = "", ["CacheAuthName"] = "" });
                host.ResetAuthInput();
                break;
            case AccountProvider.Nide:
                host.CancelNide(); host.Save(new Dictionary<string, object?> { ["CacheNideAccess"] = "" });
                host.ResetNideInput();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }
}

