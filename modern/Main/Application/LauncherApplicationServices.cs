// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Diagnostics;
using PCLCS.Applications;
using LaunchOwner = PCLCS.Applications.LaunchApplicationService<PCL.ModLaunch.McLaunchOptions, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<PCL.ModLaunch.McLaunchOptions, object>, PCL.ModLoader.LoaderCombo<object>, PCL.ModWatcher.Watcher>;
using AccountOwner = PCLCS.Applications.AccountApplicationService<PCL.ModLaunch.McLoginData, PCL.ModLoader.LoaderTask<PCL.ModLaunch.McLoginData, PCL.ModLaunch.McLoginResult>, PCL.ModLoader.LoaderTask<PCL.ModLaunch.McLoginMs, PCL.ModLaunch.McLoginResult>, PCL.ModLoader.LoaderTask<PCL.ModLaunch.McLoginLegacy, PCL.ModLaunch.McLoginResult>, PCL.ModLoader.LoaderTask<PCL.ModLaunch.McLoginServer, PCL.ModLaunch.McLoginResult>>;

namespace PCL;

/// <summary>The application's composition root. Services own state; legacy modules expose forwarding properties only.</summary>
public static class LauncherApplicationServices {
    internal static readonly TaskApplicationService<ModLoader.LoaderBase> TaskOwner = new(ReadTask, task => task.Cancel());
    internal static readonly LaunchOwner LaunchOwner = CreateLaunchService();
    internal static readonly AccountOwner AccountOwner = CreateAccountService();
    internal static readonly InstanceSelectionApplicationService<ModMinecraft.McInstance> SelectionOwner = CreateSelectionService();
    private static LaunchOwner CreateLaunchService() => new(new() {
        VerifyAccess = VerifyUiAccess,
        IsActive = IsLaunchActive,
        ReportBusy = () => ModMain.Hint("启动任务尚未结束，请稍后重试。", ModMain.HintType.Red),
        CanStart = CheckLaunchAccount,
        ResolveTarget = options => options?.Instance ?? SelectionOwner.Runtime.Instance,
        Start = options => ModLaunch.StartFromApplication(options),
        Cancel = CancelLaunch,
        Read = ReadLaunch
    });
    private static bool CheckLaunchAccount() {
        if (!GlassSkinSites.RoleSelectionPending) return true;
        ModMain.Hint("请先在账号页选择皮肤站角色。", ModMain.HintType.Red);
        return false;
    }
    private static AccountOwner CreateAccountService() => new(new() {
        VerifyAccess = VerifyUiAccess,
        Read = ReadAccount,
        Validate = input => ModLaunch.McLoginAble(input),
        CanSignIn = () => !IsLaunchActive() && !AccountTasks().Any(IsTaskActive),
        Start = input => AccountOwner.Runtime.Login.Start(input, IsForceRestart: true),
        Cancel = () => { AccountOwner.Runtime.Login.Cancel(); ProviderTask(CurrentProvider()).Cancel(); },
        SignOut = new() {
            CancelMicrosoft = () => AccountOwner.Runtime.Microsoft.Cancel(),
            CancelAuth = () => AccountOwner.Runtime.Auth.Cancel(),
            CancelNide = () => AccountOwner.Runtime.Nide.Cancel(),
            ResetAuthInput = () => AccountOwner.Runtime.Auth.Input = default!,
            ResetNideInput = () => AccountOwner.Runtime.Nide.Input = default!,
            Save = values => Settings.SaveAccountGroup(values)
        }
    });
    private static InstanceSelectionApplicationService<ModMinecraft.McInstance> CreateSelectionService() => new(new() {
        VerifyAccess = VerifyUiAccess,
        CanSelect = instance => !IsLaunchActive() && !ModMinecraft.McInstanceLock.BlocksNavigation(SelectionOwner.Runtime.Instance!) &&
            !ModMinecraft.McInstanceLock.BlocksNavigation(instance!) &&
            (instance is null || !ModModpackMode.Enabled || ModModpackMode.AllowsInstance(instance.PathVersion)),
        CanChangeFolder = () => !ModModpackMode.Enabled,
        ReadFolder = () => ModMinecraft.McFolderSelected,
        SetFolder = folder => ModMinecraft.McFolderSelected = folder,
        ApplySelection = instance => ModMinecraft.ApplySelectionFromApplication(instance)
    });

    public static ILaunchApplicationService<ModLaunch.McLaunchOptions> Launch {
        get { ModLaunch.EnsureApplicationServicesInitialized(); return LaunchOwner; }
    }
    public static IAccountApplicationService<ModLaunch.McLoginData> Accounts {
        get { ModLaunch.EnsureApplicationServicesInitialized(); return AccountOwner; }
    }
    public static SkinLibraryApplicationService SkinLibrary { get; } = new();
    public static IInstanceSelectionApplicationService<ModMinecraft.McInstance> Instances {
        get { ModMinecraft.EnsureApplicationServicesInitialized(); return SelectionOwner; }
    }
    public static ITaskApplicationService Tasks {
        get { ModLoader.EnsureApplicationServicesInitialized(); return TaskOwner; }
    }

    private static void VerifyUiAccess() {
        if (!ModBase.RunInUi()) throw new InvalidOperationException("应用命令必须在 UI 线程调用。");
    }
    private static bool IsTaskActive(ModLoader.LoaderBase? task) => task is not null &&
        (task.State == ModBase.LoadState.Loading || task.HasActiveWorkload == true);
    internal static bool IsLaunchActive() => IsTaskActive(LaunchOwner.Runtime.Task) || IsTaskActive(LaunchOwner.Runtime.Operation);
    internal static ModMinecraft.McInstance ReadLaunchInstance() =>
        IsLaunchActive() && LaunchOwner.Runtime.Target is not null ? LaunchOwner.Runtime.Target : ModMinecraft.McInstanceSelected;

    internal static ApplicationTaskSnapshot ReadTask(ModLoader.LoaderBase task) => new(task.Uuid, task.Name,
        task.State switch {
            ModBase.LoadState.Waiting => ApplicationTaskState.Waiting,
            ModBase.LoadState.Loading => ApplicationTaskState.Running,
            ModBase.LoadState.Finished => ApplicationTaskState.Succeeded,
            ModBase.LoadState.Failed => ApplicationTaskState.Failed,
            ModBase.LoadState.Canceled => ApplicationTaskState.CancellationRequested,
            _ => throw new InvalidOperationException("Unknown loader state.")
        }, task.Progress, task.HasActiveWorkload, task.Error?.GetType().FullName);

    private static LaunchApplicationSnapshot ReadLaunch() {
        int? processId = null;
        try { if (LaunchOwner.Runtime.Process is not null) processId = LaunchOwner.Runtime.Process.Id; }
        catch (InvalidOperationException) { }
        return new(ReadTask(LaunchOwner.Runtime.Task), LaunchOwner.Runtime.Target?.PathVersion, processId, ModWatcher.HasRunningMinecraft);
    }
    private static void CancelLaunch() => LaunchCancellationActions.Cancel(new() {
        HasOperation = () => LaunchOwner.Runtime.Operation is not null,
        CancelOperation = () => LaunchOwner.Runtime.Operation.Cancel(),
        LogCancellation = () => ModLaunch.McLaunchLog("已取消启动"),
        HasWatcher = () => LaunchOwner.Runtime.Watcher is not null,
        KillWatcher = () => LaunchOwner.Runtime.Watcher.Kill(),
        ReadProcess = () => LaunchOwner.Runtime.Process
    });
    private static AccountProvider CurrentProvider() => (AccountProvider)(int)Settings.Get<ModLaunch.McLoginType>("LoginType");
    private static ModLoader.LoaderBase ProviderTask(AccountProvider provider) => provider switch {
        AccountProvider.Microsoft => AccountOwner.Runtime.Microsoft,
        AccountProvider.Auth => AccountOwner.Runtime.Auth,
        AccountProvider.Nide => AccountOwner.Runtime.Nide,
        AccountProvider.Offline => AccountOwner.Runtime.Offline,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
    private static ModLoader.LoaderBase[] AccountTasks() => [AccountOwner.Runtime.Login, AccountOwner.Runtime.Microsoft,
        AccountOwner.Runtime.Offline, AccountOwner.Runtime.Nide, AccountOwner.Runtime.Auth];
    private static AccountApplicationSnapshot ReadAccount() {
        AccountProvider provider = CurrentProvider();
        ModLoader.LoaderBase task = IsTaskActive(AccountOwner.Runtime.Login) ? AccountOwner.Runtime.Login : ProviderTask(provider);
        if (provider == AccountProvider.Auth) {
            var character = GlassSkinSites.CurrentForLaunch();
            return new(provider, character?.Profile.Name, ReadTask(task), character is not null);
        }
        string? marker = provider switch {
            AccountProvider.Microsoft => "CacheMsV2OAuthRefresh",
            AccountProvider.Nide => "CacheNideAccess", _ => null
        };
        bool authenticated = marker is not null && !string.IsNullOrEmpty(Settings.Get<string>(marker));
        string nameKey = provider switch {
            AccountProvider.Microsoft => "CacheMsV2Name",
            AccountProvider.Nide => "CacheNideName", _ => "LoginLegacyName"
        };
        string? name = Settings.Get<string>(nameKey);
        if (provider == AccountProvider.Offline && name is not null) name = name.Split('¨')[0];
        return new(provider, name, ReadTask(task), authenticated);
    }
}
