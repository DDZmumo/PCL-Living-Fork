// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL;

internal sealed record TerracottaPlayer(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("kind")] string? Kind);

internal sealed record TerracottaRoom(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("index")] long Index,
    [property: JsonPropertyName("room")] string? Code,
    [property: JsonPropertyName("url")] string? Address,
    [property: JsonPropertyName("type")] int? Error,
    [property: JsonPropertyName("profiles")] TerracottaPlayer[]? Players);

// Owns a local upstream Terracotta session. The upstream helper owns the room protocol and port forwarding.
internal sealed class TerracottaConnection(HttpClient client) : IDisposable
{
    internal static async Task<TerracottaConnection> StartAsync(string directory, CancellationToken token,
        Func<ModLoader.LoaderCombo<int>, CancellationToken, Task>? queueDownload = null)
    {
        // The helper must report the release it was installed as; a newer release that does not is rejected.
        string expectedVersion = TerracottaComponent.Selected(directory).Version;
        string executable = await TerracottaComponent.EnsureAsync(directory, token, queueDownload).ConfigureAwait(false);
        string runtime = Path.Combine(directory, "data");
        Directory.CreateDirectory(runtime);
        string handshake = Path.Combine(runtime, Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        // Keep this launcher's helper separate from standalone Terracotta or another launcher's active room.
        start.Environment["TEMP"] = runtime;
        start.Environment["TMP"] = runtime;
        start.ArgumentList.Add("--hmcl");
        start.ArgumentList.Add(handshake);

        token.ThrowIfCancellationRequested();
        using Process bootstrap = Process.Start(start) ?? throw new IOException("无法启动陶瓦联机组件。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(14));
        TerracottaConnection? connection = null;
        try
        {
            while (!File.Exists(handshake))
            {
                if (bootstrap.HasExited && bootstrap.ExitCode != 0)
                    throw new IOException($"陶瓦联机组件启动失败（退出码 {bootstrap.ExitCode}）。");
                await Task.Delay(75, timeout.Token).ConfigureAwait(false);
            }
            using var portInfo = JsonDocument.Parse(await File.ReadAllTextAsync(handshake, timeout.Token).ConfigureAwait(false));
            if (!portInfo.RootElement.TryGetProperty("port", out JsonElement value) ||
                !value.TryGetInt32(out int port) || port is < 1 or > 65535)
                throw new InvalidDataException("陶瓦联机没有报告有效的本机接口端口。");
            var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(3),
                MaxResponseContentBufferSize = 1024 * 1024
            };
            connection = new TerracottaConnection(http);
            using JsonDocument meta = await http.GetFromJsonAsync<JsonDocument>("meta", timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("陶瓦联机组件没有报告版本。");
            if (!meta.RootElement.TryGetProperty("version", out JsonElement version) ||
                version.GetString()?.TrimStart('v') != expectedVersion)
                throw new InvalidDataException("本机陶瓦联机组件版本与接入接口不匹配。");
            return connection;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            AbortStartup();
            throw new TimeoutException("等待陶瓦联机启动超时，请重试。");
        }
        catch
        {
            AbortStartup();
            throw;
        }
        finally
        {
            try { File.Delete(handshake); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void AbortStartup()
        {
            connection?.Dispose();
            try { if (!bootstrap.HasExited) bootstrap.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    internal async Task<TerracottaRoom> ReadAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<TerracottaRoom>("state", token).ConfigureAwait(false)
        ?? throw new InvalidDataException("陶瓦联机返回了空状态。");

    internal Task CreateAsync(string? player, CancellationToken token) =>
        CommandAsync("state/scanning" + (string.IsNullOrWhiteSpace(player) ? "" : "?player=" + Uri.EscapeDataString(player)), token);

    internal Task JoinAsync(string code, string? player, CancellationToken token)
    {
        code = code.Trim();
        if (code.Length is 0 or > 512) throw new ArgumentException("请输入有效的联机房间码。", nameof(code));
        string query = "state/guesting?room=" + Uri.EscapeDataString(code);
        if (!string.IsNullOrWhiteSpace(player)) query += "&player=" + Uri.EscapeDataString(player);
        return CommandAsync(query, token);
    }

    internal Task LeaveAsync(CancellationToken token) => CommandAsync("state/ide", token);

    private async Task CommandAsync(string route, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(route, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new InvalidDataException("陶瓦无法接受该房间码，请核对邀请码和当前大厅状态。");
        if (!response.IsSuccessStatusCode)
            throw new IOException($"陶瓦联机操作失败（HTTP {(int)response.StatusCode}）。");
    }

    internal async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try { await CommandAsync("panic?peaceful=true", timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException) { }
        finally { Dispose(); }
    }

    public void Dispose() => client.Dispose();
}
