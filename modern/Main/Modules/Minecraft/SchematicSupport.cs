// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeloongCore;
using SchematicKit;

namespace PCL
{
    /// <summary>
    ///     把 SchematicKit（投影解析与 3D 预览）接到启动器：实例来源、客户端 JAR、日志与配色。
    ///     投影页与实例资源页共用同一个 <see cref="Service" />，避免重复扫描实例。
    /// </summary>
    internal static class SchematicSupport
    {
        private static readonly object Sync = new();
        private static SchematicService ServiceCache;
        private static bool Initialized;

        /// <summary>共享的投影业务入口。</summary>
        internal static SchematicService Service
        {
            get
            {
                EnsureInitialized();
                lock (Sync)
                    return ServiceCache ??= new SchematicService(new LauncherInstanceProvider());
            }
        }

        /// <summary>首次使用时把日志、文件夹打开和主题接进启动器。</summary>
        internal static void EnsureInitialized()
        {
            if (Initialized) return;
            lock (Sync)
            {
                if (Initialized) return;
                try
                {
                    SchematicOptions.ErrorLog = (error, message) => Logger.Error(error, message);
                    SchematicOptions.OpenFolder = path =>
                    {
                        try { ModBase.OpenExplorer(path); }
                        catch (Exception error) { Logger.Warn(error, "打开投影文件夹失败"); }
                    };
                    SchematicOptions.OpenUrl = url =>
                    {
                        try { ModBase.StartProcess(url); }
                        catch (Exception error) { Logger.Warn(error, "打开投影链接失败"); }
                    };
                    // 玻璃新界面用的是深色材质，但主题本身可能仍是浅色；按实际观感选配色。
                    ApplyTheme();
                }
                catch (Exception error)
                {
                    Logger.Warn(error, "初始化投影组件失败");
                }
                finally
                {
                    Initialized = true;
                }
            }
        }

        /// <summary>按启动器当前的明暗与界面风格刷新投影控件配色。</summary>
        internal static void ApplyTheme()
        {
            bool Dark = ModTheme.IsDarkTheme || ModMain.FrmMain?.GlassShellActive == true;
            SchematicTheme.Apply(Dark ? SchematicTheme.Dark : SchematicTheme.Light);
        }

        /// <summary>启动器当前实例的客户端 JAR；找不到时为 null（预览退回近似纯色）。</summary>
        internal static string ResolveJarPath(SchematicInstance instance) => instance?.JarPath;

        internal static string ResolveJarPath(ModMinecraft.McInstance instance)
        {
            if (instance is null) return null;
            try
            {
                string folder = instance.PathVersion;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
                string named = Path.Combine(folder, instance.Name + ".jar");
                if (File.Exists(named)) return named;
                return Directory.EnumerateFiles(folder, "*.jar", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
            catch (Exception error)
            {
                Logger.Warn(error, "查找实例客户端 JAR 失败");
                return null;
            }
        }

        /// <summary>把 PCL 的实例列表映射成投影组件需要的路径信息。</summary>
        private sealed class LauncherInstanceProvider : ISchematicInstanceProvider
        {
            public string SelectedInstanceName => ModMinecraft.McInstanceSelected?.Name;

            public IReadOnlyList<SchematicInstance> GetInstances()
            {
                var result = new List<SchematicInstance>();
                try
                {
                    var map = ModMinecraft.McInstanceList;
                    if (map is not null)
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var instance in map.Values.Where(list => list is not null).SelectMany(list => list))
                        {
                            if (instance is null || string.IsNullOrEmpty(instance.PathVersion)) continue;
                            if (!seen.Add(instance.PathVersion)) continue;
                            result.Add(Convert(instance));
                        }
                    }
                }
                catch (Exception error)
                {
                    Logger.Warn(error, "枚举实例用于投影页面失败");
                }

                // 主界面的版本列表是懒加载的；用户直接从「更多」进来时它可能还是空的，退回目录扫描。
                if (result.Count == 0) result = ScanVersionsFolder();
                return result;
            }

            private static List<SchematicInstance> ScanVersionsFolder()
            {
                try
                {
                    string folder = ModMinecraft.McFolderSelected;
                    if (string.IsNullOrEmpty(folder)) return [];
                    return new FolderInstanceProvider(folder, ModMinecraft.McInstanceSelected?.Name)
                        .GetInstances().ToList();
                }
                catch (Exception error)
                {
                    Logger.Warn(error, "扫描 versions 目录用于投影页面失败");
                    return [];
                }
            }

            private static SchematicInstance Convert(ModMinecraft.McInstance instance)
            {
                string game = instance.PathVersion;
                try
                {
                    string indie = instance.PathIndie;
                    if (!string.IsNullOrEmpty(indie)) game = indie;
                }
                catch (Exception error)
                {
                    Logger.Warn(error, $"推导实例游戏目录失败（{instance.Name}），使用版本目录");
                }

                string logo = Path.Combine(instance.PathVersion ?? string.Empty, "PCL", "Logo.png");
                return new SchematicInstance
                {
                    Name = instance.Name,
                    InstanceDirectory = instance.PathVersion,
                    GameDirectory = game,
                    JarPath = ResolveJarPath(instance),
                    LogoPath = File.Exists(logo) ? logo : null
                };
            }
        }
    }
}
