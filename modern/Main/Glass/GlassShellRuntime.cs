// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore;
using PCLCS.Applications;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using McInstance = PCL.ModMinecraft.McInstance;

namespace PCL;

/// <summary>
/// Adapts the existing application services to the glass view. Cached fields are presentation metadata;
/// selection, authentication, launch admission, cancellation and Quick Play remain owned by their services.
/// FormMain owns the refresh cadence, page hosts, account portal, wallpaper and native window controls.
/// </summary>
internal sealed class GlassShellRuntime : IDisposable {
    private readonly FormMain _owner;
    private readonly GlassLauncherView _view;
    private readonly Action _requestWorkspaceRefresh;
    private McInstance? _observedInstance;
    private string _instanceName = "选择实例", _instanceDescription = string.Empty;
    private string? _instanceLogo, _gameFolder;
    private McInstance? _loadRequested;
    private readonly HashSet<McInstance> _loadingInstances = new(ReferenceEqualityComparer.Instance);
    private McInstance? _pendingInstanceSettings;
    private ImageSource? _instanceIcon;
    private double _nextMetadataRefresh, _nextRecentRefresh;
    private RecentWorld? _recentWorld;
    private ImageSource? _recentIcon;
    private string _recentName = string.Empty, _recentDetail = string.Empty;
    private string? _recentImagePath;
    private DateTime _recentImageStamp;
    private bool _recentRefreshPending, _discoverWorlds = true, _recentWorldSessionActive, _disposed;
    private int _recentRevision;

    internal GlassShellRuntime(FormMain owner, GlassLauncherView view, Action requestWorkspaceRefresh) {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(requestWorkspaceRefresh);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _view = view;
        _requestWorkspaceRefresh = requestWorkspaceRefresh;
        view.StartRequested += OnStartRequested;
        view.CancelRequested += OnCancelRequested;
        view.SelectInstanceRequested += OnSelectInstanceRequested;
        view.InstanceSettingsRequested += OnInstanceSettingsRequested;
        view.AccountRequested += OnAccountRequested;
        view.RecentWorldRequested += OnRecentWorldRequested;
        view.BackRequested += OnBackRequested;
        view.NavigationRequested += OnNavigationRequested;
        view.DependencyCheckChanged += OnDependencyCheckChanged;
        view.AutoFillChanged += OnAutoFillChanged;
    }

    internal event EventHandler? AccountRequested;
    internal bool UseDarkText { get; set; }

    /// <summary>Reads current application/UI state. Call on the window's Dispatcher, including after page changes.</summary>
    public void Refresh() {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        double now = Timestamp();
        McInstance? instance = LauncherApplicationServices.Instances.ReadSnapshot().Instance;
        bool changed = !ReferenceEquals(instance, _observedInstance);
        if (changed) {
            if (!ReferenceEquals(_pendingInstanceSettings, instance)) CancelPendingInstanceSettings();
            _observedInstance = instance;
            _instanceLogo = null;
            _instanceIcon = null;
            _gameFolder = null;
            _nextMetadataRefresh = 0;
            InvalidateRecent();
        }
        // The glass home replaces the original launch page, which used to load the selected instance's metadata on display.
        // Without that, the game folder (and so the recent world) stays unknown until the first launch. Load it once in the background.
        if (instance is { IsLoaded: false } && !ReferenceEquals(_loadRequested, instance)) {
            _loadRequested = instance;
            BeginInstanceLoad(instance);
        }
        if (now >= _nextMetadataRefresh) {
            _nextMetadataRefresh = now + 1;
            RefreshInstanceMetadata(instance);
        }
        bool waitingForRole = GlassSkinSites.RoleSelectionPending;
        bool verifyingCharacter = LauncherAccountSession.ExternalCharacterSwitchPending;
        var (accountName, accountAvatar) = ReadAccountAppearance(waitingForRole);

        // Read the real current session every refresh, rather than keeping a second launch state machine.
        LaunchApplicationSnapshot launch = LauncherApplicationServices.Launch.ReadSnapshot();
        bool launching = LauncherApplicationServices.IsLaunchActive();
        bool hasInstance = instance is not null && ModModpackMode.AllowsInstance(instance.PathVersion);
        bool loadingInstances = ModMinecraft.McInstanceListLoader.State == ModBase.LoadState.Loading ||
                                ModMinecraft.McFolderListLoader.State == ModBase.LoadState.Loading;
        bool downloadRequired = !hasInstance && CanOfferInstall();
        bool canLaunch = !loadingInstances && !waitingForRole && !verifyingCharacter &&
            (downloadRequired || (hasInstance && !ModMinecraft.McInstanceLock.BlocksNavigation(instance!)));
        int page = _owner.GlassPageCode;
        string title = _owner.GlassPageTitle ?? string.Empty;
        string? resourceTitle = page == (int)FormMain.PageType.Download ? ResourceBrowserCatalog.Find(_owner.GlassDownloadsSubPage)?.Title : null;
        bool recentEnabled = Settings.Get<bool>("LaunchRecentWorlds");
        if (page == (int)FormMain.PageType.Launch && recentEnabled && instance is not null && !launching && !launch.HasRunningGame &&
            !_recentRefreshPending && now >= _nextRecentRefresh && !string.IsNullOrEmpty(_gameFolder))
            BeginRecentRefresh(instance, _gameFolder);

        var operation = ModLaunch.McLaunchLoaderReal;
        double? progress = null;
        string launchStatus = string.Empty;
        if (launching) {
            double actualProgress = operation?.Progress ?? launch.Task.Progress;
            if (double.IsFinite(actualProgress)) progress = Math.Clamp(actualProgress, 0, 1);
            // Task names are UI metadata. Never project Error, raw login inputs or credentials into the view.
            launchStatus = OneLine(operation?.GetLoaderList(false)
                .FirstOrDefault(task => task.State == ModBase.LoadState.Loading)?.Name ?? launch.Task.Name, 160);
        }
        bool recentAvailable = recentEnabled && !_recentWorldSessionActive && _recentWorld is not null && instance is not null &&
            SamePath(_recentWorld.VersionPath, instance.PathVersion) && SamePath(_recentWorld.GameFolder, _gameFolder);
        _view.SetState(new GlassLauncherViewState {
            IsHome = page == (int)FormMain.PageType.Launch,
            // Detail pages keep the rail selection and secondary title of the page they were opened from.
            SelectedNavigationId = _owner.GlassContextOrigin is { } origin ? (int)origin : page,
            PageTitle = resourceTitle ?? title,
            IsResourceList = resourceTitle is not null,
            CanGoBack = !string.IsNullOrEmpty(title),
            AccountName = accountName,
            AccountAvatar = accountAvatar,
            HasInstance = hasInstance,
            InstanceName = _instanceName,
            InstanceDescription = _instanceDescription,
            InstanceIcon = _instanceIcon,
            CanLaunch = canLaunch,
            IsLaunching = launching,
            LaunchText = verifyingCharacter ? "正在验证角色" : waitingForRole ? "请先选择角色" : loadingInstances ? "正在加载" : downloadRequired ? "下载游戏" : "启动游戏",
            LaunchStatus = launchStatus,
            LaunchProgress = progress,
            DependencyCheckEnabled = Settings.Get<bool>("LaunchModDependencyCheck"),
            AutoFillEnabled = Settings.Get<bool>("LaunchModDependencyAutoFill"),
            LaunchOptionsAvailable = !launching && !launch.HasRunningGame,
            RecentWorldAvailable = recentAvailable,
            RecentWorldName = recentAvailable ? _recentName : string.Empty,
            RecentWorldDetail = recentAvailable ? _recentDetail : string.Empty,
            RecentWorldIcon = recentAvailable ? _recentIcon : null,
            UseDarkText = UseDarkText,
            ReduceMotion = !MotionPreferences.AllowsAnimation
        });
    }


    public void Dispose() {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _recentRevision++;
        _view.StartRequested -= OnStartRequested;
        _view.CancelRequested -= OnCancelRequested;
        _view.SelectInstanceRequested -= OnSelectInstanceRequested;
        _view.InstanceSettingsRequested -= OnInstanceSettingsRequested;
        _view.AccountRequested -= OnAccountRequested;
        _view.RecentWorldRequested -= OnRecentWorldRequested;
        _view.BackRequested -= OnBackRequested;
        _view.NavigationRequested -= OnNavigationRequested;
        _view.DependencyCheckChanged -= OnDependencyCheckChanged;
        _view.AutoFillChanged -= OnAutoFillChanged;
        AccountRequested = null;
    }

    private void RefreshInstanceMetadata(McInstance? instance) {
        _instanceName = instance is null ? "选择实例" : OneLine(instance.Name, 160);
        // An invalid instance's Info can contain a parser exception. Keep errors in their existing UI.
        _instanceDescription = instance is null || !instance.IsLoaded || instance.State == McInstance.McInstanceState.Error
            ? string.Empty : OneLine(instance.Info, 200);
        string? gameFolder = instance?.IsLoaded == true && instance.State != McInstance.McInstanceState.Error ? instance.PathIndie : null;
        if (!SamePathOrEmpty(_gameFolder, gameFolder)) { _gameFolder = gameFolder; InvalidateRecent(); }
        if (_recentWorld is not null) _recentDetail = RecentDetail(_recentWorld, _instanceName);
        string? logo = instance?.Logo;
        if (string.Equals(logo, _instanceLogo, StringComparison.Ordinal)) return;
        _instanceLogo = logo;
        _instanceIcon = LoadInstanceIcon(logo, instance?.PathVersion);
    }

    private static (string Name, ImageSource? Avatar) ReadAccountAppearance(bool waitingForRole) {
        if (waitingForRole) return ("等待选择角色", null);
        AccountApplicationSnapshot account = LauncherApplicationServices.Accounts.ReadSnapshot();
        string kind = account.Provider switch {
            AccountProvider.Microsoft => "Ms", AccountProvider.Nide => "Nide", AccountProvider.Auth => "Auth", _ => "Legacy"
        };
        var request = LauncherSkinSessions.Skins.Input(kind);
        string name = kind == "Legacy" ? LauncherSkinSessions.Skins.OfflineAccountName() : account.Authenticated ? request.Name : "";
        return string.IsNullOrWhiteSpace(name) ? ("未登录", null) : (OneLine(name, 100), LauncherSkinSessions.Avatar(kind, request));
    }

    internal static DrawingImage ComposeAccountAvatar(ImageSource back, ImageSource? fore) {
        var drawing = new DrawingGroup();
        // Both Minecraft layers share the same 8x8-to-32 grid. An inset face would
        // introduce a fractional pixel scale and misalign transparent hat pixels.
        drawing.Children.Add(new ImageDrawing(back, new Rect(0, 0, 32, 32)));
        if (fore is not null) drawing.Children.Add(new ImageDrawing(fore, new Rect(0, 0, 32, 32)));
        var image = new DrawingImage(drawing);
        if (image.CanFreeze) image.Freeze();
        return image;
    }

    internal void RequestRecentWorldRefresh() {
        _recentRevision++;
        _discoverWorlds = true;
        _nextRecentRefresh = 0;
        _requestWorkspaceRefresh();
    }

    private void BeginRecentRefresh(McInstance instance, string gameFolder) {
        _nextRecentRefresh = Timestamp() + 5;
        _recentRefreshPending = true;
        bool discover = _discoverWorlds;
        _discoverWorlds = false;
        int revision = _recentRevision;
        string versionPath = instance.PathVersion;
        string? previousImagePath = _recentImagePath;
        DateTime previousImageStamp = _recentImageStamp;
        ImageSource? previousIcon = _recentIcon;
        _ = Task.Run(() => {
            RecentWorld? world = null;
            ImageSource? icon = null;
            string? imagePath = null;
            DateTime imageStamp = default;
            bool running = false;
            try {
                if (discover) {
                    QuickPlayWorlds.RecoverExitedSessions();
                    QuickPlayWorlds.DiscoverExistingWorlds(instance);
                }
                running = QuickPlayWorlds.HasRunningSessions();
                world = QuickPlayWorlds.Recent().FirstOrDefault(item =>
                    SamePath(item.VersionPath, versionPath) && SamePath(item.GameFolder, gameFolder));
                if (world is not null) {
                    imagePath = Path.Combine(QuickPlayWorlds.WorldPath(world.GameFolder!, world.Identifier!), "icon.png");
                    imageStamp = File.GetLastWriteTimeUtc(imagePath);
                    icon = string.Equals(imagePath, previousImagePath, StringComparison.OrdinalIgnoreCase) && imageStamp == previousImageStamp
                        ? previousIcon : InstanceResourceStore.LoadImage(imagePath, 64);
                }
            } catch (Exception error) {
                Logger.Warn(error, "读取玻璃界面的最近存档失败");
            }
            if (_owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished) return;
            try {
                _owner.Dispatcher.BeginInvoke(new Action(() => {
                    _recentRefreshPending = false;
                    if (_disposed || revision != _recentRevision) return;
                    _recentWorldSessionActive = running;
                    SetRecentPresentation(world, icon);
                    _recentImagePath = imagePath;
                    _recentImageStamp = imageStamp;
                    _requestWorkspaceRefresh();
                }));
            } catch (InvalidOperationException) when (_owner.Dispatcher.HasShutdownStarted) {
                // Shutdown can race the dispatch check. No work remains for a closed view.
            }
        });
    }

    private void SetRecentPresentation(RecentWorld? world, ImageSource? icon) {
        _recentWorld = world;
        _recentIcon = icon;
        _recentName = world is null ? string.Empty : OneLine(world.Name ?? world.Identifier, 160);
        _recentDetail = world is null ? string.Empty : RecentDetail(world, _instanceName);
    }

    private void InvalidateRecent() {
        _recentWorld = null;
        _recentIcon = null;
        _recentWorldSessionActive = false;
        _recentName = _recentDetail = string.Empty;
        _recentImagePath = null;
        _recentImageStamp = default;
        RequestRecentWorldRefresh();
    }

    private static bool CanOfferInstall() => !ModModpackMode.Enabled &&
        (!Settings.Get<bool>("UiHiddenPageDownload") || LauncherAppearanceSettings.HiddenForceShow);

    internal void StartFromHome() => OnStartRequested(this, EventArgs.Empty);

    private void OnStartRequested(object? sender, EventArgs arguments) {
        VerifyUsable();
        CancelPendingInstanceSettings();
        if (!LauncherApplicationServices.IsLaunchActive() && !LauncherApplicationServices.IsLaunchAccountPending &&
            (ModMinecraft.McInstanceSelected is not { } selected || !ModModpackMode.AllowsInstance(selected.PathVersion)) &&
            CanOfferInstall() && ModMinecraft.McInstanceListLoader.State != ModBase.LoadState.Loading &&
            ModMinecraft.McFolderListLoader.State != ModBase.LoadState.Loading)
        {
            _owner.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall);
            _requestWorkspaceRefresh();
            return;
        }
        LauncherApplicationServices.Launch.Start();
        _requestWorkspaceRefresh();
    }
    private void OnCancelRequested(object? sender, EventArgs arguments) {
        VerifyUsable();
        LauncherApplicationServices.Launch.RequestCancellation();
        _requestWorkspaceRefresh();
    }
    private void OnSelectInstanceRequested(object? sender, EventArgs arguments) {
        VerifyUsable();
        if (LauncherApplicationServices.IsLaunchActive()) return;
        _owner.PageChange(FormMain.PageType.InstanceSelect);
        _requestWorkspaceRefresh();
    }
    private void OnInstanceSettingsRequested(object? sender, EventArgs arguments) {
        VerifyUsable();
        McInstance? instance = LauncherApplicationServices.Instances.ReadSnapshot().Instance;
        if (instance is null || LauncherApplicationServices.IsLaunchActive()) return;
        OpenInstanceSettings(instance);
    }

    internal void CancelPendingInstanceSettings() => _pendingInstanceSettings = null;

    private void BeginInstanceLoad(McInstance instance) {
        if (!_loadingInstances.Add(instance)) return;
        _ = Task.Run(() => {
            try { instance.Load(); }
            catch (Exception error) { Logger.Warn(error, "预加载玻璃界面的实例信息失败"); }
            if (_owner.Dispatcher.HasShutdownStarted) return;
            _owner.Dispatcher.BeginInvoke(new Action(() => {
                _loadingInstances.Remove(instance);
                if (_disposed) return;
                _nextMetadataRefresh = 0;
                if (ReferenceEquals(_pendingInstanceSettings, instance)) {
                    CancelPendingInstanceSettings();
                    if (instance.IsLoaded && _owner.GlassPageCode == (int)FormMain.PageType.Launch &&
                        !_view.IsAccountWorkspaceOpen && !LauncherApplicationServices.IsLaunchActive() &&
                        ReferenceEquals(LauncherApplicationServices.Instances.ReadSnapshot().Instance, instance)) {
                        OpenInstanceSettings(instance);
                    }
                }
                _requestWorkspaceRefresh();
            }));
        });
    }

    private void OpenInstanceSettings(McInstance instance) {
        if (ModMinecraft.McInstanceLock.BlocksNavigation(instance)) {
            ModMain.Hint("该版本正在被修改，暂时无法进入版本设置！", ModMain.HintType.Red);
            return;
        }
        if (!instance.IsLoaded) {
            _pendingInstanceSettings = instance;
            BeginInstanceLoad(instance);
            return;
        }
        CancelPendingInstanceSettings();
        LauncherInstanceContext.Instance = instance;
        _owner.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceOverall);
        _requestWorkspaceRefresh();
    }
    private void OnAccountRequested(object? sender, EventArgs arguments) {
        VerifyUsable();
        CancelPendingInstanceSettings();
        AccountRequested?.Invoke(this, EventArgs.Empty);
        _requestWorkspaceRefresh();
    }
    private void OnRecentWorldRequested(object? sender, EventArgs arguments) {
        VerifyUsable();
        McInstance? instance = LauncherApplicationServices.Instances.ReadSnapshot().Instance;
        RecentWorld? world = _recentWorld;
        if (instance is null || world is null || _recentWorldSessionActive || !Settings.Get<bool>("LaunchRecentWorlds") ||
            LauncherApplicationServices.IsLaunchActive() || LauncherApplicationServices.Launch.ReadSnapshot().HasRunningGame) return;
        if (!SamePath(instance.PathVersion, world.VersionPath) || !SamePath(instance.PathIndie, world.GameFolder)) {
            InvalidateRecent();
            ModMain.Hint("此版本的游戏目录已经变化，请从存档管理页重新选择存档。", ModMain.HintType.Red);
        } else {
            QuickPlayWorlds.Start(instance, world.Identifier!);
        }
        _requestWorkspaceRefresh();
    }
    private void OnBackRequested(object? sender, EventArgs arguments) { VerifyUsable(); _owner.PageBack(); _requestWorkspaceRefresh(); }
    private void OnNavigationRequested(int page) {
        VerifyUsable();
        if (page is not (0 or 1 or 10 or 2 or 3 or 4)) return;
        _owner.PageChange((FormMain.PageType)page);
        _requestWorkspaceRefresh();
    }
    private void OnDependencyCheckChanged(bool value) => SetDependencySetting("LaunchModDependencyCheck", value);
    private void OnAutoFillChanged(bool value) => SetDependencySetting("LaunchModDependencyAutoFill", value);
    private void SetDependencySetting(string name, bool value) {
        VerifyUsable();
        // Settings.Set invokes the original LaunchDependencyCheck.SettingsChanged callback exactly once.
        if (Settings.Get<bool>(name) != value) Settings.Set(name, value);
        _requestWorkspaceRefresh();
    }

    private void VerifyUsable() {
        _owner.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private static double Timestamp() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    private static bool SamePath(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) && PathUtils.IsSame(first, second);
    private static bool SamePathOrEmpty(string? first, string? second) =>
        string.IsNullOrEmpty(first) && string.IsNullOrEmpty(second) || SamePath(first, second);
    private static string OneLine(string? text, int maximum) {
        string result = (text ?? string.Empty).ReplaceLineEndings(" ").Replace('\t', ' ').Trim();
        return result.Length <= maximum ? result : result[..maximum];
    }
    private static string RecentDetail(RecentWorld world, string instanceName) {
        DateTime played = world.PlayedUtc.ToLocalTime();
        return (played.Date == DateTime.Today ? played.ToString("HH:mm") : played.ToString("MM/dd")) + " · " + instanceName;
    }
    internal static ImageSource? LoadInstanceIcon(string? address, string? versionFolder = null) {
        if (string.IsNullOrWhiteSpace(address)) return null;
        try {
            // A modpack's own icon or a custom one is stored relative to the instance folder (e.g. "PCL\Logo.png").
            if (!string.IsNullOrEmpty(versionFolder) && !Path.IsPathRooted(address) && !address.Contains("://", StringComparison.Ordinal) &&
                !address.StartsWith("/", StringComparison.Ordinal) && File.Exists(Path.Combine(versionFolder, address)))
                return InstanceResourceStore.LoadImage(Path.Combine(versionFolder, address), 96);
            if (address.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("/LivingPCL;component/", StringComparison.OrdinalIgnoreCase)) {
                ImageSource image = new MyBitmap(address);
                if (image.CanFreeze) image.Freeze();
                return image;
            }
            // The instance already owns this local icon path; do not start remote image requests for the shell.
            if (Path.IsPathFullyQualified(address) && !address.StartsWith(@"\\", StringComparison.Ordinal) &&
                !address.StartsWith("//", StringComparison.Ordinal)) return InstanceResourceStore.LoadImage(address, 96);
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || !uri.IsFile || uri.IsUnc) return null;
            return InstanceResourceStore.LoadImage(uri.LocalPath, 96);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException) {
            return null;
        }
    }
}
