using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class LegacyJavaService<TInstance> where TInstance : class {
    /// <summary>Select from the original LF list, then search/download under the original shared selection lock.</summary>
    public Java? SelectOrDownloadJava(OneOf<ValueRange<Version>, TInstance> target, bool tryFixOrHint,
                                     CancellationToken c = default, ProgressProvider? p = null) {
        try {
            if (target.Is<TInstance>()) MigrateInstanceJavaSettings(target.As<TInstance>(), c, p?.SplitTo(0.2));
            TInstance? instance = target.Is<TInstance>() ? target.As<TInstance>() : null;
            bool? x86Supported = null;
            bool SupportsArchitecture(Java java) => !java.Is32Bit || instance is null ||
                (x86Supported ??= LegacyWindowsNatives.SupportsX86(host.JsonObject(instance)));
            switch (instance == null ? 0 : host.ReadInteger("VersionArgumentJavaV2", instance)) {
                case 2:
                    Logger.Info($"版本设置要求使用版本文件夹中的 Java：{host.VersionPath(instance!)}");
                    Java? javaFound = JavaUtils.SearchFoldersAsync(true, [host.VersionPath(instance!)], c, p?.SplitTo(1))
                        .Run().Where(SupportsArchitecture).OrderBy(java => java.Is32Bit).FirstOrDefault();
                    if (javaFound != null) {
                        Logger.Info($"已发现版本文件夹中的 Java：{javaFound}");
                        return javaFound;
                    }
                    if (tryFixOrHint) {
                        host.SwitchToInstanceSetup(instance!);
                        host.Message("该版本的版本设置选择了 “使用版本文件夹中的 Java”，但版本文件夹里没能找到 Java。\r\n请先修改实例管理中 Java 页的选择，或在版本文件夹中放一个 Java，然后再试。",
                            "未找到 Java", true, true);
                    }
                    return null;

                case 3:
                    Java? chosenJava = Configs.JavaForced.Get(host.Config(instance!));
                    Logger.Info($"版本设置中强制指定的 Java：{host.VersionPath(instance!)} → {chosenJava}");
                    if (chosenJava == null) {
                        if (tryFixOrHint) {
                            host.SwitchToInstanceSetup(instance!);
                            host.Message("该版本的版本设置选择了 “使用指定的 Java”，但还没有指定任何 Java。\r\n请先选择你想使用的 Java，然后再试。",
                                "未选择 Java", true, true);
                        }
                        p?.Skip();
                        return null;
                    }
                    if (!Configs.JavaList.Get()!.Contains(chosenJava)) {
                        Configs.JavaForced.Reset(host.Config(instance!));
                        host.UpdateJavaLists();
                        if (tryFixOrHint) {
                            host.SwitchToInstanceSetup(instance!);
                            Logger.Error("版本设置中指定的 Java 不在 Java 列表中，请重新选择一个 Java！", LogBehavior.Alert);
                        }
                        p?.Skip();
                        return null;
                    }
                    if (!chosenJava.CheckAsync(c).Run()) {
                        Configs.JavaForced.Reset(host.Config(instance!));
                        host.UpdateJavaLists();
                        if (tryFixOrHint) {
                            host.SwitchToInstanceSetup(instance!);
                            Logger.Error("版本设置中指定的 Java 已无法使用，请重新选择一个 Java，然后再试。", LogBehavior.Alert);
                        } else {
                            Logger.Error("版本设置中指定的 Java 已无法使用，请重新选择一个 Java！", LogBehavior.Toast);
                        }
                        p?.Finish();
                        return null;
                    }
                    if (!SupportsArchitecture(chosenJava)) {
                        if (tryFixOrHint) {
                            host.SwitchToInstanceSetup(instance!);
                            host.Message("此实例的支持库需要 64 位 Java。请在实例的 Java 设置中选择 64 位版本。",
                                "需要 64 位 Java", true, true);
                        }
                        p?.Skip();
                        return null;
                    }
                    p?.Finish();
                    return chosenJava;

                default:
                    // The legacy implementation intentionally evaluates the requirement twice.
                    ValueRange<Version> range = target.Is<TInstance>() ? GetJavaRequirement(instance!).Range : target.As<ValueRange<Version>>();
                    string? recommendedComponent = target.Is<TInstance>() ? GetJavaRequirement(instance!).RecommendedComponent : null;
                    Logger.Info($"自动选择以下范围的 Java：{range} {recommendedComponent}");
                    if (range.IsEmpty()) throw new Exception("Java 版本需求存在冲突，导致没有任何可能适配的 Java，请查看 PCL 的日志了解详细信息！");
                    int gcOverride = instance is null ? 0 : host.ReadInteger("VersionAdvanceGC", instance);
                    bool preferHotSpot = instance is not null &&
                        (gcOverride > 0 ? gcOverride != 4 : host.ReadInteger("LaunchAdvanceGC", instance) != 3);
                    Java? SelectJavaFromJavaList() {
                        // Preserve the original un-tokened worker wait. Each candidate check receives the caller's token.
                        host.RefreshWorker().WaitIfRunning();
                        Java? openJ9Fallback = null, x86Fallback = null, x86OpenJ9Fallback = null;
                        foreach (Java candidate in Configs.JavaList.Get()!) {
                            if (!candidate.CheckAsync(c).Run()) {
                                Logger.Warn($"Java 检查失败，将尝试列表中的下一个 Java：{candidate}");
                                continue;
                            }
                            if (candidate.Version == null || !range.Contains(candidate.Version) || !SupportsArchitecture(candidate)) continue;
                            if (candidate.Is32Bit) {
                                if (preferHotSpot && candidate.VirtualMachine == "OpenJ9") x86OpenJ9Fallback ??= candidate;
                                else x86Fallback ??= candidate;
                                continue;
                            }
                            if (preferHotSpot && candidate.VirtualMachine == "OpenJ9") {
                                openJ9Fallback ??= candidate;
                                continue;
                            }
                            Logger.Info($"已选定 Java：{candidate}");
                            return candidate;
                        }
                        Java? fallback = openJ9Fallback ?? x86Fallback ?? x86OpenJ9Fallback;
                        if (fallback is not null) Logger.Info($"已选定 Java：{fallback}");
                        return fallback;
                    }
                    p?.Set(0.25);
                    Java? result = SelectJavaFromJavaList();
                    if (result != null) { p?.Skip(); return result; }

                    lock (javaLock) {
                        Logger.Warn("既有列表中没有合适的 Java，重新触发搜索");
                        host.RefreshWorker().Start(c, p?.SplitTo(0.3));
                        result = SelectJavaFromJavaList();
                        if (result != null) { p?.Skip(); return result; }
                        if (!tryFixOrHint) return null;

                        Logger.Warn("电脑中没有合适的 Java，开始获取 Mojang 提供的 Java 下载列表");
                        downloadListWorker ??= new RedoableWorker<List<LegacyJavaRuntime>>(DownloadRuntimeIndex);
                        if (!downloadListWorker.HasSucceeded) downloadListWorker.Start(c, p?.SplitTo(0.4));
                        downloadListWorker.WaitIfRunning(cancellationToken: c);
                        p?.Set(0.4, skiped: true);
                        List<LegacyJavaRuntime> components = downloadListWorker.LastResult;
                        // LF prioritizes version.name here, even though the old comment called it a component.
                        LegacyJavaRuntime? javaToDownload = MeloongCore.Extensions.EnumerableExtensions.FirstOrDefault(components,
                            comp => Operators.CompareString(comp.Java["version"]!["name"]!.ToString(), recommendedComponent, false) == 0,
                            comp => range.Contains(comp.JavaVersion));
                        if (javaToDownload == null) {
                            Logger.Warn("Mojang 提供的 Java 列表中没有合适的 Java，无法自动下载");
                            host.Message($"PCL 没能找到合适的 Java。\r\n请在【设置 → Java】中下载或导入{DescribeRequirement(range)}。",
                                "需要手动安装 Java", false, false);
                            return null;
                        }
                        LegacyJavaDownloadOperation javaLoader = host.CreateDownload(CreateDownloadPlan(javaToDownload));
                        try {
                            javaLoader.Wait(c, p?.SplitTo(0.95));
                        } finally {
                            javaLoader.Cancel();
                        }
                        result = SelectJavaFromJavaList();
                        if (result != null) { p?.Finish(); return result; }
                        throw new Exception($"下载 Java {javaToDownload.ComponentName} 后仍然无法找到合适的 Java");
                    }
            }
        } catch (Exception ex) {
            ex.ThrowIfCanceled();
            Logger.Error(ex, "选择 Java 时出现异常");
            p?.Skip();
            return null;
        }
    }

    private List<LegacyJavaRuntime> DownloadRuntimeIndex(CancellationToken? cancellationToken) {
        string indexFileStr = host.RequestJson(host.VersionListOrder(
            ["https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json"],
            ["https://bmclapi2.bangbang93.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json"]));
        cancellationToken?.ThrowIfCancellationRequested();
        return ParseRuntimeIndex(indexFileStr, cancellationToken);
    }

    /// <summary>Read the original windows-x64 list; do not add platforms, components or version rules.</summary>
    public static List<LegacyJavaRuntime> ParseRuntimeIndex(string indexFileStr, CancellationToken? cancellationToken = null) {
        List<LegacyJavaRuntime> results = [];
        foreach (JProperty prop in indexFileStr.DeserializeJson<JObject>()!["windows-x64"]!) {
            cancellationToken?.ThrowIfCancellationRequested();
            if (prop.Name == "minecraft-java-exe") continue;
            try {
                JObject? entry = (JObject?)prop.Value.FirstOrDefault();
                if (entry == null) continue;
                string javaName = entry["version"]!["name"]!.ToString();
                Version javaVersion;
                if (javaName.Contains(".")) {
                    string[] parts = javaName.Split('.');
                    javaVersion = new Version(ValInteger(parts[0]), ValInteger(parts[1]),
                        parts.Length >= 3 ? ValInteger(parts[2]) : 0, parts.Length >= 4 ? ValInteger(parts[3]) : 0);
                } else if (javaName.StartsWithF("8u51-")) {
                    javaVersion = new Version(8, 0, 51, 0);
                } else {
                    javaVersion = new Version(ValInteger(javaName.BeforeFirstOfAny(["u", "."])), 0, 0, 0);
                }
                results.Add(new LegacyJavaRuntime { ComponentName = prop.Name, Java = entry, JavaVersion = javaVersion });
            } catch (Exception ex) {
                Logger.Warn(ex, $"解析 Mojang 提供的 Java 列表项失败，已跳过（{prop.Name}）");
            }
        }
        return results;
    }

    private static int ValInteger(string value) => Conversions.ToInteger(Conversion.Val(value));

    private static string DescribeRequirement(ValueRange<Version> range) {
        bool hasPureLower = range.HasLower && range.IsLowerInclusive &&
            range.Lower!.Minor <= 0 && range.Lower.Build <= 0 && range.Lower.Revision <= 0;
        bool hasPureUpper = range.HasUpper && !range.IsUpperInclusive &&
            range.Upper!.Minor <= 0 && range.Upper.Build <= 0 && range.Upper.Revision <= 0;
        if (range.HasLower && range.HasUpper) {
            if (hasPureLower && hasPureUpper) {
                return range.Lower!.Major == range.Upper!.Major - 1
                    ? $" Java {range.Lower.Major}" : $" Java {range.Lower.Major} ~ {range.Upper.Major - 1}";
            }
            return $" Java {range}";
        }
        if (range.HasLower) {
            if (hasPureLower) return $" Java {range.Lower!.Major} 或更高版本的 Java";
            return range.IsLowerInclusive ? $" Java {range.Lower} 或更高版本的 Java" : $"版本高于 {range.Lower} 的 Java";
        }
        if (hasPureUpper) return $" Java {range.Upper!.Major - 1} 或更低版本的 Java";
        return range.IsUpperInclusive ? $" Java {range.Upper} 或更低版本的 Java" : $"版本低于 {range.Upper} 的 Java";
    }

    public LegacyJavaDownloadPlan CreateDownloadPlan(LegacyJavaRuntime target) {
        string address = (string)target.Java["manifest"]!["url"]!;
        Logger.Info($"准备下载 Java {target.Java["version"]!["name"]}（{target.ComponentName}）：{address}");
        string listFileStr = host.RequestJson(host.SourceOrder([address], [address.Replace("piston-meta.mojang.com", "bmclapi2.bangbang93.com")]));
        LegacyJavaDownloadPlan plan = new() { BaseDirectory = $"{Paths.AppData}.minecraft\\runtime\\{target.ComponentName}\\" };
        foreach (JProperty file in ((JObject)listFileStr.DeserializeJson()!)["files"]!) {
            JObject fileValue = (JObject)file.Value;
            if (fileValue["downloads"]?["raw"] == null) continue;
            JObject info = (JObject)fileValue["downloads"]!["raw"]!;
            long size = (long)info["size"]!;
            string hash = (string)info["sha1"]!;
            if (hash is "12976a6c2b227cbac58969c1455444596c894656" or "c80e4bab46e34d02826eab226a4441d0970f2aba" or "84d2102ad171863db04e7ee22a259d1f6c5de4a5") continue;
            string url = (string)info["url"]!;
            plan.Files.Add(new LegacyJavaDownloadFile {
                Sources = host.SourceOrder([url], [url.Replace("piston-data.mojang.com", "bmclapi2.bangbang93.com")]),
                Destination = plan.BaseDirectory + file.Name,
                ActualSize = size,
                Hash = hash
            });
        }
        Logger.Info($"该 Java 需要下载 {plan.Files.Count} 个文件，目标文件夹：{plan.BaseDirectory}");
        return plan;
    }
}
