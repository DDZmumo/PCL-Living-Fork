using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

/// <summary>
/// LF Java initialization, legacy settings migration, selection and download orchestration.
/// Ported from LF ModJava.vb (PCL baseline 5ef81e4); uses the existing LF Java/JavaUtils cache.
/// </summary>
public sealed partial class LegacyJavaService<TInstance>(LegacyJavaHost<TInstance> host) where TInstance : class {
    private readonly object javaLock = new();
    private RedoableWorker<List<LegacyJavaRuntime>>? downloadListWorker;

    public void JavaInit() {
        try {
            if (Configs.JavaConfigVersion.Get() == 0) {
                try {
                    Logger.Warn("正在从老版本迁移 Java 列表");
                    List<Java> oldJavaList = host.ReadString("LaunchArgumentJavaAll", null).DeserializeJson<JArray>()!
                        .Select(javaObject => new Java(javaObject!["Path"]!.ToString())).ToList();
                    oldJavaList = JavaUtils.SortAsync(oldJavaList).Run();
                    Logger.Info($"已从老版本设置中发现 {oldJavaList.Count} 个 Java");
                    string selectedJavaSetting = host.ReadString("LaunchArgumentJavaSelect", null);
                    string selectedJava = selectedJavaSetting.StartsWithF("{")
                        ? selectedJavaSetting.DeserializeJson<JObject>()!["Path"]!.ToString() : "";
                    Java? match = oldJavaList.FirstOrDefault(j => PathUtils.IsSame(j.Folder, selectedJava));
                    if (match != null) {
                        oldJavaList.Remove(match);
                        oldJavaList.Insert(0, match);
                        Logger.Info($"已将老版本设置中强制选择的 Java 置顶：{match}");
                    }
                    lock (Configs.JavaList) Configs.JavaList.Set(new ConcurrentList<Java>(oldJavaList));
                    Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
                } catch (Exception ex) {
                    Logger.Error(ex, "从老版本 PCL 迁移 Java 列表失败");
                }
            }
            ConcurrentList<Java> list = Configs.JavaList.Get()!;
            if (list.Any()) {
                Logger.Info($"共有 {list.Count} 个可用的 Java：\r\n- {list.Select(j => j.ToString()).Join("\r\n- ")}");
                return;
            }
            Logger.Warn("初始化未找到可用的 Java，将自动触发搜索");
            host.RefreshWorker().Start();
        } catch (Exception ex) {
            Logger.Error(ex, "初始化 Java 列表失败");
            lock (Configs.JavaList) Configs.JavaList.Reset();
        }
    }

    public void ManuallyImportJava() {
        string? newJavaPath = host.SelectJavaExecutable();
        if (string.IsNullOrEmpty(newJavaPath)) return;
        Java newJava = new(PathUtils.RemoveLastPart(newJavaPath!));
        if (!newJava.CheckAsync().Run()) {
            host.Hint("该 Java 存在异常，无法使用", LegacyJavaHintKind.Red);
            return;
        }
        lock (Configs.JavaList) {
            Configs.JavaRemovedList.Edit((ref List<string>? list) =>
                list!.RemoveIf(f => PathUtils.IsSame(f, newJava.Folder)));
            Configs.JavaList.Edit((ref ConcurrentList<Java>? list) => {
                list!.Remove(newJava);
                list.Insert(0, newJava);
            });
        }
        host.Hint($"已将 Java 加入列表：{newJavaPath}", LegacyJavaHintKind.Green);
        host.UpdateJavaLists();
    }

    public void ManuallyRemoveJava(Java javaEntry) {
        lock (Configs.JavaList) {
            Configs.JavaRemovedList.Edit(list => MeloongCore.Extensions.EnumerableExtensions
                .DistinctBy(list!.Append(javaEntry.Folder), s => s.Lower()).ToList());
            Configs.JavaList.Edit((ref ConcurrentList<Java>? list) => { list!.Remove(javaEntry); });
        }
        host.Hint($"已将 Java 移除出列表：{javaEntry}", LegacyJavaHintKind.Green);
        host.UpdateJavaLists();
        host.ReloadCurrentJava();
    }

    public void ManuallySearchJava(CancellationToken cancellationToken = default) {
        host.RunInThread(() => {
            Configs.JavaList.Get()!.ForAll(java => java.InvalidateChecked());
            host.RefreshWorker().Start(cancellationToken: cancellationToken);
            host.RefreshWorker().WaitIfRunningAsync(cancellationToken: cancellationToken).Run();
            if (Configs.JavaList.Get()!.Any()) {
                host.Hint("已找到 " + Configs.JavaList.Get()!.Count + " 个 Java！", LegacyJavaHintKind.Green);
            } else {
                host.Hint("未找到可用的 Java！", LegacyJavaHintKind.Red);
            }
        });
    }

    private void MigrateInstanceJavaSettings(TInstance instance, CancellationToken c = default, ProgressProvider? p = null) {
        if (Configs.JavaMigrated.Get(host.Config(instance))) { p?.Skip(); return; }
        host.DisableJavaSettings();
        try {
            Logger.Info($"正在尝试迁移版本独立的 Java 设置（{host.InstanceName(instance)}）");
            string forcedJavaSetting = host.ReadString("VersionArgumentJavaSelect", instance);
            if (forcedJavaSetting.StartsWithF("{")) {
                Java forcedJava = new(forcedJavaSetting.DeserializeJson<JObject>()!["Path"]!.ToString());
                if (forcedJava.CheckAsync(c).Run()) {
                    Logger.Info($"将版本独立设置改为老版本中强制指定的 Java：{forcedJava}");
                    if (!Configs.JavaList.Get()!.Contains(forcedJava)) {
                        lock (Configs.JavaList) Configs.JavaList.Edit((ref ConcurrentList<Java>? list) => {
                            if (!list!.Contains(forcedJava)) list.Add(forcedJava);
                        });
                        Logger.Info($"已将强制指定的 Java 加入 Java 列表底部：{forcedJava}");
                    }
                    Configs.JavaForced.Set(forcedJava, host.Config(instance));
                    host.WriteInteger("VersionArgumentJavaV2", 3, instance);
                    host.UpdateJavaLists();
                    Configs.JavaMigrated.Set(true, host.Config(instance));
                    p?.Finish();
                    return;
                }
                host.Hint("该版本的设置中强制指定的 Java 存在异常，该设置已重置", LegacyJavaHintKind.Red);
                p?.Set(0.4);
            } else {
                p?.Set(0.4, true);
            }
            List<Java> javaFound = JavaUtils.SearchFoldersAsync(true, [host.VersionPath(instance)], c, p?.SplitTo(1)).Run();
            if (javaFound.Any()) {
                Logger.Info($"在版本文件夹中发现了 Java，将版本独立设置改为优先使用版本文件夹的 Java：{javaFound}");
                host.WriteInteger("VersionArgumentJavaV2", 2, instance);
            } else {
                Logger.Info("没有需要迁移的内容，自动选择 Java");
                host.WriteInteger("VersionArgumentJavaV2", 0, instance);
            }
            Configs.JavaMigrated.Set(true, host.Config(instance));
        } finally {
            host.RestoreJavaSettings();
        }
    }
}
