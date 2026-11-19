// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS.Applications;

/// <summary>Transfers a loopback texture server from the launcher to the lifetime of its exact game process.</summary>
public sealed class LocalSkinServiceProcess : IDisposable {
    public const string Command = "--local-skin-service";
    private const int LaunchFrameSize = 12 * 1024 * 1024;
    private readonly Process worker;
    private bool attached, disposed;
    private sealed record GameProcess(int Id, long StartUtcTicks);
    public LocalSkinEndpoint Endpoint { get; }

    private LocalSkinServiceProcess(Process worker, LocalSkinEndpoint endpoint) {
        this.worker = worker;
        Endpoint = endpoint;
    }

    public static async Task<LocalSkinServiceProcess> StartAsync(string executable, LocalSkinLaunch appearance, CancellationToken token) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
        };
        start.ArgumentList.Add(Command);
        var child = Process.Start(start) ?? throw new IOException("无法启动本地皮肤服务。");
        try {
            await WriteAsync(child.StandardInput.BaseStream, appearance, LaunchFrameSize, deadline.Token).ConfigureAwait(false);
            var endpoint = await ReadAsync<LocalSkinEndpoint>(child.StandardOutput.BaseStream, 16384, deadline.Token).ConfigureAwait(false);
            if (endpoint.Address is not { IsAbsoluteUri: true, Scheme: "http", Host: "127.0.0.1" } || string.IsNullOrWhiteSpace(endpoint.Metadata))
                throw new IOException("本地皮肤服务返回了无效的端点。");
            return new(child, endpoint);
        } catch (Exception error) {
            Stop(child);
            child.Dispose();
            token.ThrowIfCancellationRequested();
            throw new IOException("本地皮肤服务启动失败。", error);
        }
    }

    public void Attach(Process process, CancellationToken token) => AttachAsync(process, token).GetAwaiter().GetResult();

    private async Task AttachAsync(Process process, CancellationToken token) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (attached) throw new InvalidOperationException("本地皮肤服务已关联游戏进程。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            await WriteAsync(worker.StandardInput.BaseStream, new GameProcess(process.Id, process.StartTime.ToUniversalTime().Ticks), 256, deadline.Token).ConfigureAwait(false);
            attached = await ReadAsync<bool>(worker.StandardOutput.BaseStream, 32, deadline.Token).ConfigureAwait(false);
            if (!attached) throw new IOException("游戏进程已退出或发生变化。");
        } catch (Exception error) {
            token.ThrowIfCancellationRequested();
            throw new IOException("无法将本地皮肤服务关联到本次游戏进程。", error);
        }
    }

    public static async Task<int> RunWorkerAsync(Stream input, Stream output) {
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var appearance = await ReadAsync<LocalSkinLaunch>(input, LaunchFrameSize, deadline.Token).ConfigureAwait(false);
            await using var server = new LocalYggdrasilServer(appearance);
            await WriteAsync(output, server.Endpoint, 16384, deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var target = await ReadAsync<GameProcess>(input, 256, deadline.Token).ConfigureAwait(false);
            using var game = Process.GetProcessById(target.Id);
            if (game.StartTime.ToUniversalTime().Ticks != target.StartUtcTicks || game.HasExited) return 1;
            await WriteAsync(output, true, 32, deadline.Token).ConfigureAwait(false);
            await game.WaitForExitAsync().ConfigureAwait(false);
            return 0;
        } catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            Win32Exception or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException or
            System.Text.Json.JsonException or OperationCanceledException) {
            return 1;
        }
    }

    private static async Task<T> ReadAsync<T>(Stream input, int limit, CancellationToken token) {
        byte[] header = new byte[4];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > limit) throw new IOException("本地皮肤服务消息大小无效。");
        byte[] payload = new byte[length];
        await input.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new IOException("本地皮肤服务消息无效。");
    }

    private static async Task WriteAsync<T>(Stream output, T value, int limit, CancellationToken token) {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > limit) throw new IOException("本地皮肤服务消息超过大小限制。");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, token).ConfigureAwait(false);
        await output.WriteAsync(payload, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static void Stop(Process child) {
        try { if (!child.HasExited) child.Kill(); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (!attached) Stop(worker);
        worker.StandardInput.Dispose();
        worker.StandardOutput.Dispose();
        worker.Dispose();
    }
}
