// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;
using JsonException = System.Text.Json.JsonException;

namespace PCL;

/// <summary>
/// Controller of the Terracotta lobby. It owns the helper connection and the room state; the visible page is drawn by
/// <see cref="GlassPresentationSession"/> (see GlassPresentation.Lobby.cs), which reads the state below on every refresh
/// and routes each action back through the methods here.
/// </summary>
internal sealed class TerracottaLobbyPage : MyPageRight, IDisposable
{
    internal enum LobbyStage { Setup, Lobby, Connecting, Room, Failed }
    internal enum LobbyFlow { Create, Join }

    internal const string Attribution = "Terracotta | 陶瓦联机 © Burning_TNT 与贡献者";
    internal const string HelpUrl = "https://docs.hmcl.net/multiplayer/help.html";
    internal static string LicenseUrl => TerracottaComponent.ProjectUrl + "#许可";

    private readonly FormMain _owner;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _componentDirectory = Path.Combine(ModBase.PathTemp, "Terracotta");
    private readonly DispatcherTimer _poll;
    private LobbyFlow _flow = LobbyFlow.Create;
    private TerracottaConnection? _connection;
    private TerracottaRoom? _room;
    private string? _activeLabel;
    private string _error = string.Empty;
    private int _pollFailures;
    private bool _polling, _disposed;

    /// <summary>Raised whenever the lobby state changes, so the presentation can refresh without waiting for its tick.</summary>
    internal event Action? Changed;

    internal TerracottaLobbyPage(FormMain owner)
    {
        _owner = owner;
        // The presentation replaces the whole page; the original tree only hosts the page lifecycle.
        Child = new Grid();
        _poll = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _poll.Tick += (_, _) => _ = PollAsync();
        PageEntering += BeginObserving;
        PageLeaving += EndObserving;
        owner.Closed += (_, _) => OnLauncherExit();
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

    internal IReadOnlyList<GlassSidebarItem> SidebarItems()
    {
        bool enabled = CanChooseFlow;
        return [
            new() {
                Label = "创建房间", IconData = "F0 M3,3 H21 V21 H3 Z M5,5 V19 H19 V5 Z M11,7 H13 V11 H17 V13 H13 V17 H11 V13 H7 V11 H11 Z",
                IsEnabled = enabled, IsSelected = enabled && _flow == LobbyFlow.Create, Invoked = () => ChooseFlow(LobbyFlow.Create)
            },
            new() {
                Label = "加入房间", IconData = "M11,3 H21 V21 H11 V19 H19 V5 H11 Z M2,11 H12 L8.5,7.5 L10,6 L16,12 L10,18 L8.5,16.5 L12,13 H2 Z",
                IsEnabled = enabled, IsSelected = enabled && _flow == LobbyFlow.Join, Invoked = () => ChooseFlow(LobbyFlow.Join)
            }
        ];
    }

    private bool CanChooseFlow => !_disposed && Stage == LobbyStage.Lobby && _activeLabel is null;

    internal void ChooseFlow(LobbyFlow flow)
    {
        if (!CanChooseFlow || _flow == flow) return;
        _flow = flow;
        _error = string.Empty;
        NotifyChanged();
        _owner.RefreshGlassShell();
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

    private static string? PlayerName() => LauncherApplicationServices.Accounts.ReadSnapshot().DisplayName;

    private async Task EnableAsync(CancellationToken token)
    {
        var connection = await TerracottaConnection.StartAsync(_componentDirectory, token, QueueDownloadAsync);
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
        Dispatcher.InvokeAsync(() => {
            task.Start(1);
            ModLoader.LoaderTaskbarAdd(task);
            _owner.BtnExtraDownload.ShowRefresh();
            _owner.BtnExtraDownload.Ribble();
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
    }

    private async Task RunAsync(string label, Func<CancellationToken, Task> action)
    {
        if (_disposed || _activeLabel is not null) return;
        _activeLabel = label;
        _error = string.Empty;
        NotifyChanged();
        try
        {
            await action(_lifetime.Token);
            if (!_disposed && _connection is { } connection)
                SetRoom(await connection.ReadAsync(_lifetime.Token));
        }
        catch (OperationCanceledException error) when (_disposed || error.InnerException is not TimeoutException)
        {
            if (!_disposed) _error = "操作已取消，可以重试。";
        }
        catch (Exception error)
        {
            if (!_disposed)
                _error = error is HttpRequestException or JsonException or OperationCanceledException ?
                    "陶瓦联机暂时没有响应，请稍后重试。" : error.Message;
        }
        finally
        {
            _activeLabel = null;
            if (!_disposed) NotifyChanged();
        }
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

    private void BeginObserving()
    {
        if (_disposed || _poll.IsEnabled) return;
        ModWatcher.GameActivityChanged += OnGameActivityChanged;
        _poll.Start();
        NotifyChanged();
        _ = PollAsync();
    }

    private void EndObserving()
    {
        ModWatcher.GameActivityChanged -= OnGameActivityChanged;
        _poll.Stop();
    }

    private void OnGameActivityChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
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
