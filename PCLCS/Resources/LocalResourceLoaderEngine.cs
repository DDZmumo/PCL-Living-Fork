using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF LocalResourceLoaders.vb at 50b028da5fcad2226d74d901a41d8c43a249bfb2; base matches
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8, LF LICENCE. Fixed CE stable
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModLocalComp.cs reviewed only:
// retain LF's two worker threads, Loader thread-identity cancellation and cache schema.
public sealed partial class LocalResourceLoaderEngine<TLocal, TProject, TVersion>
    where TLocal : LocalResourceFileData<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    private readonly LocalResourceLoaderHost<TLocal, TProject, TVersion> host;
    public LocalResourceLoaderEngine(LocalResourceLoaderHost<TLocal, TProject, TVersion> host) { this.host = host; }
    public void LoadFiles(LocalResourceFileTask<TLocal> task) {
        try {
            host.SetShowProgress(false);
            if (host.IsUpdatingFolder(task.Input())) {
                host.Info($"等待 Mod 更新完成后才能继续加载 Mod 列表：{task.Input()}");
                try {
                    host.SetLoadText("正在更新 Mod");
                    while (host.IsUpdatingFolder(task.Input())) {
                        if (task.IsCanceled()) return;
                        Thread.Sleep(100);
                    }
                } finally { host.SetLoadText("正在加载 Mod 列表"); }
                host.ReloadFolder();
            }
            List<string> modFiles = new();
            if (DirectoryUtils.Exists(task.Input())) {
                foreach (string file in DirectoryUtils.EnumerateFiles(task.Input(), true)) {
                    if (!PathUtils.IsSame(PathUtils.RemoveLastPart(file), task.Input())) {
                        // Read the actual selected version only when testing a nested file, as LF did.
                        McVersion? version = host.CurrentVersion();
                        if (!(version is not null && version.ModLoaders.HasFlagF(ModLoaders.Forge) && version.Vanilla.Major < 13 &&
                            PathUtils.GetLastPart(PathUtils.RemoveLastPart(file)) == $"1.{version.Vanilla.Major}.{version.Vanilla.Build}")) continue;
                    }
                    if (IsModFile(file)) modFiles.Add(file);
                }
            }
            string cachePath = host.TempPath() + @"Cache\LocalMod.json";
            JObject cache = new();
            try {
                string? content = FileUtils.TryReadAsString(cachePath);
                if (!string.IsNullOrWhiteSpace(content)) {
                    cache = (JObject)content!.DeserializeJson()!;
                    if (!cache.ContainsKey("version") || cache["version"]!.ToObject<int>() != host.CacheVersion()) {
                        host.Warning("本地 Mod 信息缓存版本已过期，将弃用这些缓存信息"); cache = new JObject();
                    }
                }
            } catch (Exception ex) { host.Warn(ex, "读取本地 Mod 信息缓存失败，已重置"); cache = new JObject(); }
            cache["version"] = host.CacheVersion();
            List<TLocal> mods = new();
            foreach (string file in modFiles) {
                if (task.IsCanceled()) return;
                TLocal entry = host.CreateLocal(file);
                TLocal? duplicate = mods.FirstOrDefault(m => m.EnabledName == entry.EnabledName);
                if (duplicate is not null) {
                    TLocal disabled = duplicate.IsEnabled ? entry : duplicate;
                    host.Warning($"重复的 Mod 文件：{duplicate.File.Name} 与 {entry.File.Name}，已忽略 {disabled.File.Name}");
                    if (ReferenceEquals(disabled, entry)) continue;
                    mods.Remove(disabled);
                }
                mods.Add(entry);
            }
            host.Info($"共发现 {mods.Count} 个 Mod");
            mods = mods.OrderBy(m => m.File.Name).ToList();
            LocalResourceCompatibilityContext? compatibility = host.ReadCompatibility();
            if (compatibility is not null && PathUtils.IsSame(task.Input(), compatibility.Folder)) {
                try { compatibility.Inspect(task.Input(), task.CreateCancellationToken()); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { host.Warn(ex, "检查 Mod 列表的加载器兼容性失败，仍显示本地文件"); }
            }
            if (task.IsCanceled()) return;
            task.SetOutput(mods);
            host.StartOnline(mods, cache, host.CurrentVersion() ?? throw new NullReferenceException());
        } catch (Exception ex) { host.Warn(ex, "Mod 列表加载失败"); throw; }
    }
    private static bool IsModFile(string? fullName) {
        if (fullName is null) return false;
        if (new[] { "disabled", "old" }.Contains(PathUtils.GetExtension(fullName))) fullName = PathUtils.GetFileNameWithoutExtension(fullName);
        return new[] { "jar", "zip", "litemod" }.Contains(PathUtils.GetExtension(fullName));
    }
    public void LoadOnline(LocalResourceOnlineTask<TLocal> task) {
        List<TLocal> mods = new();
        JObject cache = task.Input().Cache;
        ModLoaders loaders = task.Input().Target.ModLoaders;
        foreach (TLocal entry in task.Input().ModList) {
            if (task.IsCanceled()) return;
            string prefix = entry.ModrinthHash + task.Input().Target.VanillaName;
            string key = prefix + ((int)loaders).ToString(CultureInfo.InvariantCulture);
            JToken? cached = cache[key] ?? cache[prefix + loaders];
            if (cached is not null) {
                // VB '& enum' persisted its numeric value. Also preserve caches
                // written by the early C# migration using enum names.
                if (cache[key] is null) cache[key] = cached.DeepClone();
                entry.FromJson((JObject)cached);
                if (entry.OnlineDataLoaded && DateTime.Now - cached["Project"]!["CacheTime"]!.ToObject<DateTime>() < new TimeSpan(6, 0, 0)) continue;
            }
            mods.Add(entry);
        }
        host.Info($"有 {mods.Count(m => m.Project is null)} 个 Mod 需要联网获取信息，{mods.Count(m => m.Project is not null)} 个 Mod 需要更新信息");
        if (!mods.Any()) return;
        McVersion target = task.Input().Target;
        string vanillaVersion = target.VanillaName;
        host.Info($"正在检查 Mod 信息的版本：{vanillaVersion}，加载器 {loaders}");
        int endedThreadCount = 0;
        bool failed = false;
        Thread currentTaskThread = Thread.CurrentThread;
        host.StartThread(() => {
            try { LoadModrinth(mods, vanillaVersion, loaders, task, currentTaskThread); }
            catch (Exception ex) { host.Warn(ex, "从 Modrinth 获取本地 Mod 信息失败"); failed = true; }
            finally { endedThreadCount += 1; }
        }, "Mod List Detail Loader Modrinth");
        host.StartThread(() => {
            try { LoadCurseForge(mods, vanillaVersion, loaders, task, currentTaskThread); }
            catch (Exception ex) { host.Warn(ex, "从 CurseForge 获取本地 Mod 信息失败"); failed = true; }
            finally { endedThreadCount += 1; }
        }, "Mod List Detail Loader CurseForge");
        // Preserve LF's counter/update publication and return-with-workers cancellation behavior.
        while (endedThreadCount != 2) { Thread.Sleep(10); if (task.IsCanceled()) return; }
        mods = mods.Where(m => m.Project is not null).ToList();
        host.Info($"联网获取本地 Mod 信息完成，为 {mods.Count} 个 Mod 更新缓存");
        if (!mods.Any()) return;
        if (task.IsCanceled()) return;
        foreach (TLocal entry in mods) {
            entry.OnlineDataLoaded = !failed;
            cache[entry.ModrinthHash + vanillaVersion + ((int)loaders).ToString(CultureInfo.InvariantCulture)] = entry.ToJson();
        }
        FileUtils.Write(host.TempPath() + @"Cache\LocalMod.json", cache.ToString(host.DebugMode() ? Formatting.Indented : Formatting.None));
        if (task.IsCanceled()) return;
        if (host.IsUpdateFilter()) host.RefreshUi(); else host.RefreshBars();
    }
    private static ModLoaders UpdateLoaders(ModLoaders target,ModLoaders installed) => target==ModLoaders.None ? installed : target & ModLoaders.All;
    private static bool SupportsUpdateLoader(ModLoaders candidate,ModLoaders target,ModLoaders installed) {
        ModLoaders expected=UpdateLoaders(target,installed);
        return expected==ModLoaders.None ? candidate==ModLoaders.None : (candidate & expected)!=0;
    }
}
