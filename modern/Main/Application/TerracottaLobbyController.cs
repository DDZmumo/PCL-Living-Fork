// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using JsonException = System.Text.Json.JsonException;

namespace PCL;

/// <summary>Owns the helper connection, active room, and component operations.</summary>
internal sealed class TerracottaLobbyController : IDisposable
{
    internal enum LobbyStage { Setup, Lobby, Connecting, Room, Failed }
    internal enum LobbyFlow { Create, Join }
    internal enum LobbySection { Lobby, Updates }

    internal const string Attribution = "Terracotta | 陶瓦联机 © Burning_TNT 与贡献者";
    internal const string HelpUrl = "https://docs.hmcl.net/multiplayer/help.html";
    internal static string LicenseUrl => TerracottaComponent.ProjectUrl + "#许可";

    private readonly FormMain _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _componentDirectory = Path.Combine(ModBase.PathTemp, "Terracotta");
    private readonly DispatcherTimer _poll;
    private LobbyFlow _flow = LobbyFlow.Create;
    private LobbySection _section = LobbySection.Lobby;
    private TerracottaRelease? _selected;
    private TerracottaUpstream? _upstream;
    private TerracottaHealth? _health;
    private DateTime? _checkedAt;
    private string _updateError = string.Empty, _updateNotice = string.Empty;
    private TerracottaConnection? _connection;
    private TerracottaRoom? _room;
    private string? _activeLabel;
    private string _error = string.Empty;
    private int _pollFailures;
    private bool _polling, _disposed;

    /// <summary>Raised whenever the lobby state changes, so the presentation can refresh without waiting for its tick.</summary>
    internal event Action? Changed;

    internal TerracottaLobbyController(FormMain owner)
    {
        _owner = owner;
        _poll = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _poll.Tick += (_, _) => _ = PollAsync();
    }

    // ---------- State read by the presentation ----------

    internal LobbyStage Stage => _connection is null || _room is null ? LobbyStage.Setup : _room.State switch
    {
        "waiting" => LobbyStage.Lobby,
        "host-ok" or "guest-ok" => LobbyStage.Room,
        "exception" => LobbyStage.Failed,
        _ => LobbyStage.Connecting
    };

    internal LobbyFlow Flow => _flow;
    internal bool IsHost => _room?.State is "host-ok" or "host-scanning" or "host-starting";
    internal string? ActivityLabel => _activeLabel;
    internal bool IsIdle => !_disposed && _activeLabel is null;
    internal string ErrorText => _error;
    internal bool ComponentCached => TerracottaComponent.HasCachedComponent(_componentDirectory);
    internal IReadOnlyList<TerracottaPlayer> Players => _room?.Players ?? [];
    internal string? ShareValue => _room?.State switch { "host-ok" => _room.Code, "guest-ok" => _room.Address, _ => null };
    internal string Code { get; set; } = string.Empty;

    // ---------- State of the component page ----------

    internal LobbySection Section => _section;
    /// <summary>The release the lobby starts (a newer installed one, or the bundled one).</summary>
    internal TerracottaRelease SelectedRelease => _selected ??= TerracottaComponent.Selected(_componentDirectory);
    internal TerracottaUpstream? Upstream => _upstream;
    internal TerracottaHealth? Health => _health;
    internal DateTime? CheckedAt => _checkedAt;
    internal string UpdateError => _updateError;
    internal string UpdateNotice => _updateNotice;
    /// <summary>Changing the component stops the helper, so it is not offered while a room is open or forming.</summary>
    internal bool CanChangeComponent => IsIdle && Stage is LobbyStage.Setup or LobbyStage.Lobby or LobbyStage.Failed;

    internal string? InstanceName => LauncherApplicationServices.Instances.ReadSnapshot().Instance?.Name;
    internal bool GameRunning => ModWatcher.IsGameActive;
    internal bool CanChooseInstance => !LauncherApplicationServices.IsLaunchActive();
    internal bool CanLaunch => InstanceName is not null && CanChooseInstance;

    /// <summary>One-line description of the current state, or of the operation in progress.</summary>
    internal string StatusText => _activeLabel ?? (_room?.State switch
    {
        "waiting" => "陶瓦联机已就绪",
        "host-scanning" => "正在查找已对局域网开放的 Minecraft 存档…",
        "host-starting" => "已找到存档，正在创建房间…",
        "host-ok" => "把房间码发给好友，对方在陶瓦、HMCL 或 PCL CE 中输入即可加入。",
        "guest-connecting" or "guest-starting" => "正在连接房主…",
        "guest-ok" => "在 Minecraft 的多人游戏中添加下面的地址，或直接从列表进入。",
        "exception" => _room.Error switch
        {
            0 => "无法连接房主，请确认房间还在开放。",
            1 => "与房主的连接已中断。",
            2 or 3 => "联机组网进程已退出。",
            4 => "房主的 Minecraft 存档已关闭。",
            5 => "房间未能完成 Scaffolding 协议协商。",
            _ => "陶瓦联机会话遇到异常。"
        },
        null => ComponentCached ? "组件已在本机，启用后即可创建或加入房间。" : "陶瓦联机为可选组件，下载启用后即可创建或加入房间。",
        _ => "正在等待陶瓦联机返回状态…"
    });

    // ---------- Navigation ----------

    internal bool CanChooseFlow => !_disposed && Stage == LobbyStage.Lobby && _activeLabel is null;

    internal void ChooseFlow(LobbyFlow flow)
    {
        if (!CanChooseFlow || _flow == flow && _section == LobbySection.Lobby) return;
        _flow = flow;
        _section = LobbySection.Lobby;
        _error = string.Empty;
        NotifyChanged();
        _owner.RefreshGlassShell();
    }

    internal void ShowSection(LobbySection section)
    {
        if (_disposed || _section == section) return;
        _section = section;
        NotifyChanged();
        _owner.RefreshGlassShell();
        // The first visit checks the installed files right away; upstream is only asked when the user checks.
        if (section == LobbySection.Updates && _health is null) _ = VerifyComponentAsync();
    }

    // ---------- Actions ----------

    internal Task EnableAsync() => RunAsync("正在准备陶瓦联机组件…", EnableAsync);
    internal Task CreateAsync() => RunAsync("正在发起建房…", token => _connection!.CreateAsync(PlayerName(), token));
    internal Task JoinAsync() => RunAsync("正在加入房间…", token => _connection!.JoinAsync(Code, PlayerName(), token));
    internal Task LeaveAsync() => RunAsync(Stage == LobbyStage.Room ? "正在退出房间…" : "正在返回大厅…", token => _connection!.LeaveAsync(token));

    internal void PasteCode()
    {
        if (ModBase.ClipboardGetText() is { Length: > 0 } value) Code = value.Trim();
        NotifyChanged();
    }

    internal bool CopyShare()
    {
        if (ShareValue is not { } value) return false;
        ModBase.ClipboardSet(value);
        return true;
    }

    internal void ChooseInstance() => _owner.PageChange(FormMain.PageType.InstanceSelect);

    internal void LaunchGame()
    {
        if (!CanLaunch) return;
        LauncherApplicationServices.Launch.Start(new ModLaunch.McLaunchOptions { KeepLauncherVisible = true });
    }

    internal static void OpenHelp() => ModBase.OpenWebsite(HelpUrl);
    internal static void OpenLicense() => ModBase.OpenWebsite(LicenseUrl);

    internal Task RequestUninstallAsync()
    {
        if (_disposed || _activeLabel is not null) return Task.CompletedTask;
        if (Stage == LobbyStage.Room && ModMain.MyMsgBox("卸载组件会退出当前联机房间。", "卸载陶瓦组件", "卸载", "取消", IsWarn: true) != 1)
            return Task.CompletedTask;
        return RunAsync("正在卸载陶瓦联机组件…", UninstallAsync);
    }

    // ---------- Component page actions ----------

    internal const string CheckLabel = "正在检查陶瓦联机更新…";
    internal bool CheckingUpdates => _activeLabel == CheckLabel;

    internal Task CheckUpdatesAsync() => RunAsync(CheckLabel, async token =>
    {
        _upstream = null;
        _upstream = await TerracottaUpdates.CheckAsync(token);
        _checkedAt = DateTime.Now;
    }, updates: true);

    internal Task VerifyComponentAsync() => RunAsync("正在校验组件文件…", async token =>
        _health = await TerracottaComponent.VerifyAsync(_componentDirectory, token), updates: true);

    internal Task RepairComponentAsync()
    {
        if (!CanChangeComponent) return Task.CompletedTask;
        return RunAsync("正在修复陶瓦联机组件…", async token =>
        {
            await StopConnectionAsync();
            await TerracottaComponent.EnsureAsync(_componentDirectory, token, QueueDownloadAsync);
            _health = await TerracottaComponent.VerifyAsync(_componentDirectory, token);
            _updateNotice = _health == TerracottaHealth.Intact ? "组件文件已修复。" : "";
        }, updates: true);
    }

    internal Task InstallUpstreamAsync()
    {
        if (!CanChangeComponent || _upstream?.Package is not { } package) return Task.CompletedTask;
        if (ModMain.MyMsgBox(
                $"陶瓦联机 {package.Version} 尚未由启动器适配。安装包会按上游提供的 SHA-256 校验，但新版本的功能或接口可能有变化。" +
                $"\n\n安装后会停用当前的陶瓦联机；如果新版本无法正常启动，会自动改回 {TerracottaComponent.Version}。",
                $"安装陶瓦联机 {package.Version}", "安装并使用", "取消") != 1)
            return Task.CompletedTask;
        return RunAsync($"正在安装陶瓦联机 {package.Version}…", async token =>
        {
            await StopConnectionAsync();
            TerracottaRelease installed = await TerracottaComponent.InstallReleaseAsync(_componentDirectory, package, token, QueueDownloadAsync);
            _selected = installed;
            _health = await TerracottaComponent.VerifyAsync(_componentDirectory, token);
            _updateNotice = $"已安装陶瓦联机 {installed.Version}，回到联机大厅启用即可使用。";
        }, updates: true);
    }

    internal Task UseBundledAsync()
    {
        if (!CanChangeComponent || SelectedRelease.IsBundled) return Task.CompletedTask;
        return RunAsync($"正在改回陶瓦联机 {TerracottaComponent.Version}…", async token =>
        {
            await StopConnectionAsync();
            await TerracottaComponent.UseBundledAsync(_componentDirectory, removeSelected: true, token);
            _selected = null;
            _health = await TerracottaComponent.VerifyAsync(_componentDirectory, token);
            _updateNotice = $"已改回启动器适配的 {TerracottaComponent.Version}。";
        }, updates: true);
    }

    private async Task StopConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        _room = null;
        _pollFailures = 0;
        if (connection is not null) await connection.StopAsync();
    }

    private static string? PlayerName() => LauncherApplicationServices.Accounts.ReadSnapshot().DisplayName;

    private async Task EnableAsync(CancellationToken token)
    {
        TerracottaConnection connection;
        try { connection = await TerracottaConnection.StartAsync(_componentDirectory, token, QueueDownloadAsync); }
        catch (Exception error) when (error is not OperationCanceledException && !SelectedRelease.IsBundled)
        {
            // A newer release that cannot start (or no longer speaks the expected interface) falls back to the bundled one.
            string failed = SelectedRelease.Version;
            await TerracottaComponent.UseBundledAsync(_componentDirectory, removeSelected: false, CancellationToken.None);
            _selected = null;
            _health = null;
            throw new IOException($"陶瓦联机 {failed} 未能正常启动，已改回启动器适配的 {TerracottaComponent.Version}，请重新启用。", error);
        }
        if (_disposed)
        {
            await connection.StopAsync();
            return;
        }
        _connection = connection;
        _room = null;
        _pollFailures = 0;
    }

    private Task QueueDownloadAsync(ModLoader.LoaderCombo<int> task, CancellationToken token) =>
        _owner.Dispatcher.InvokeAsync(() => {
            task.Start(1);
            ModLoader.LoaderTaskbarAdd(task);
            _owner.RefreshTaskButton(pulse: true);
            _owner.PageChange(FormMain.PageType.TaskManager);
        }, DispatcherPriority.Normal, token).Task;

    private async Task UninstallAsync(CancellationToken token)
    {
        var connection = _connection;
        _connection = null;
        _room = null;
        _pollFailures = 0;
        if (connection is not null) await connection.StopAsync();
        await TerracottaComponent.UninstallAsync(_componentDirectory, token);
        _selected = null;
        _health = null;
    }

    private async Task RunAsync(string label, Func<CancellationToken, Task> action, bool updates = false)
    {
        if (_disposed || _activeLabel is not null) return;
        _activeLabel = label;
        if (updates) { _updateError = string.Empty; _updateNotice = string.Empty; }
        else _error = string.Empty;
        NotifyChanged();
        try
        {
            await action(_lifetime.Token);
            if (!_disposed && _connection is { } connection)
                SetRoom(await connection.ReadAsync(_lifetime.Token));
        }
        catch (OperationCanceledException error) when (_disposed || error.InnerException is not TimeoutException)
        {
            if (!_disposed) Report("操作已取消，可以重试。");
        }
        catch (Exception error)
        {
            if (!_disposed) Report(Describe(error));
        }
        finally
        {
            _activeLabel = null;
            if (!_disposed) NotifyChanged();
        }

        void Report(string message)
        {
            if (updates) _updateError = message;
            else _error = message;
        }

        // Network failures read as one plain sentence; the update check's own messages (rate limit) pass through.
        string Describe(Exception error) => error switch
        {
            HttpRequestException { StatusCode: null, InnerException: null } own when updates => own.Message,
            HttpRequestException or JsonException or OperationCanceledException =>
                updates ? "无法连接 GitHub，请检查网络后重试。" : "陶瓦联机暂时没有响应，请稍后重试。",
            _ => error.Message
        };
    }

    private async Task PollAsync()
    {
        if (_disposed || _polling || _activeLabel is not null) return;
        if (_connection is not { } connection) return;
        _polling = true;
        try
        {
            TerracottaRoom room = await connection.ReadAsync(_lifetime.Token);
            if (_disposed || _connection != connection) return;
            _pollFailures = 0;
            SetRoom(room);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or JsonException or OperationCanceledException ||
            _disposed && error is ObjectDisposedException)
        {
            if (!_disposed && _connection == connection && ++_pollFailures >= 5)
            {
                connection.Dispose();
                _connection = null;
                _room = null;
                _error = "与陶瓦联机组件的连接已中断，请重新启用。";
                NotifyChanged();
            }
        }
        finally { _polling = false; }
    }

    private void SetRoom(TerracottaRoom room)
    {
        if (_disposed || _room is { } prior && room.Index <= prior.Index) return;
        _room = room;
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    internal void BeginObserving()
    {
        if (_disposed || _poll.IsEnabled) return;
        ModWatcher.GameActivityChanged += OnGameActivityChanged;
        _poll.Start();
        NotifyChanged();
        _ = PollAsync();
    }

    internal void EndObserving()
    {
        ModWatcher.GameActivityChanged -= OnGameActivityChanged;
        _poll.Stop();
    }

    private void OnGameActivityChanged()
    {
        if (_owner.Dispatcher.HasShutdownStarted) return;
        _ = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
            if (_disposed || !_poll.IsEnabled) return;
            NotifyChanged();
            _ = PollAsync();
        }));
    }

    // ---------- Lifetime ----------

    internal void OnLauncherExit()
    {
        bool keepRoom = ModWatcher.HasRunningMinecraft && _room?.State is "host-scanning" or "host-starting" or "host-ok" or
            "guest-connecting" or "guest-starting" or "guest-ok";
        Release(stopHelper: !keepRoom);
    }

    public void Dispose() => Release(stopHelper: true);

    private void Release(bool stopHelper)
    {
        if (_disposed) return;
        _disposed = true;
        EndObserving();
        _lifetime.Cancel();
        if (_connection is { } connection)
        {
            if (stopHelper) connection.StopAsync().GetAwaiter().GetResult();
            else connection.Dispose();
        }
        _connection = null;
        _lifetime.Dispose();
        Changed = null;
    }
}
