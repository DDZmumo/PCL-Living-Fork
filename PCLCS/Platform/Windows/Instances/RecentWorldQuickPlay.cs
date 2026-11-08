// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;

namespace PCLCS;

/// <summary>原实例、版本加载及最终 ModLaunch 交接；不访问账号或改写启动流水线。</summary>
public sealed class RecentWorldQuickPlayHost<TInstance> where TInstance : class {
    public Func<TInstance, string> VersionPath = null!, GameFolder = null!;
    public Func<string, bool> AllowsInstance = null!;
    public Action<TInstance> Load = null!;
    public Func<TInstance, bool> Supported = null!;
    public Func<string, string?, string> WorldPath = null!;
    public Func<bool> HasRunningGame = null!;
    public Action NavigateLaunch = null!;
    public Func<TInstance, string?, bool> Launch = null!;
    public Action<string> HintError = null!;
}

/// <summary>原 QuickPlayWorlds.Start 的校验与启动顺序，既有 Flow/History 不变。</summary>
// LF QuickPlayWorlds.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983;
// platform bridge after 257e98ea14e2cd93d2c9a906c69c62d9709ad2d3. No CE code reused.
public sealed class RecentWorldQuickPlay<TInstance> where TInstance : class {
    private readonly RecentWorldQuickPlayHost<TInstance> host;
    public RecentWorldQuickPlay(RecentWorldQuickPlayHost<TInstance> host) { this.host = host; }

    public bool Start(TInstance? instance, string? identifier) {
        try {
            if (instance is null || !host.AllowsInstance(host.VersionPath(instance))) throw new IOException("请使用当前整合包目录中的游戏版本。");
            host.Load(instance);
            if (!host.Supported(instance)) throw new IOException("直接进入存档需要支持 Quick Play 的 Minecraft Java 1.20 或更新版本。");
            host.WorldPath(host.GameFolder(instance), identifier);
            if (host.HasRunningGame()) throw new IOException("请先关闭正在运行的游戏，再直接进入存档。");
            host.NavigateLaunch();
            return host.Launch(instance, identifier);
        } catch (Exception error) {
            host.HintError(error.Message);
            return false;
        }
    }
}
