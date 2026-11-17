#nullable enable
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF Main integration; governed by the repository LICENCE.
/// <summary>Read-only settings discovery. Uses the launch selector; never downloads, logs in, adopts a variant or rewrites a preference.</summary>
internal static class MmcJavaDiscovery {
    private sealed class State {
        internal InstanceJavaPreview Value = new(InstanceJavaPreviewState.NotStarted, "正在按整合包组件声明查找 Java……");
        internal string Declaration = "";
        internal long Generation;
    }
    private static readonly ConditionalWeakTable<ModMinecraft.McInstance, State> States = new();
    private sealed class Request(ModMinecraft.McInstance owner, long generation) {
        internal readonly ModMinecraft.McInstance Owner = owner;
        internal readonly long Generation = generation;
        internal CancellationTokenRegistration Cancellation;
    }
    private sealed class Work { internal Request? Latest; }
    private static readonly ConditionalWeakTable<RedoableWorker<Java>, Work> Requests = new();
    private static readonly AsyncLocal<Request?> Executing = new();

    internal static RedoableWorker<Java> CreateWorker(Func<ModMinecraft.McInstance> currentInstance) {
        Work work = new();
        var worker = new RedoableWorker<Java>((token, progress) => {
            Request request;
            lock (work) { request = work.Latest ?? new(currentInstance(), 0); }
            // A superseding request may have canceled this old worker token before entry.
            // Do not publish that old token's cancellation into the newer request's state.
            (token ?? default).ThrowIfCancellationRequested();
            Executing.Value = request;
            try { return ModJava.SelectOrDownloadJava(request.Owner, false, token ?? default, progress); }
            finally { Executing.Value = null; request.Cancellation.Dispose(); }
        });
        Requests.Add(worker,work);
        return worker;
    }

    internal static void Start(ModMinecraft.McInstance instance, RedoableWorker<Java> worker, CancellationToken token = default) {
        var work = Requests.GetValue(worker,_ => new());
        lock (work) {
            work.Latest?.Cancellation.Dispose();
            var request = new Request(instance,Begin(instance)); work.Latest = request;
            // Bind cancellation before Start: a pre-canceled request never enters the workload.
            request.Cancellation = token.Register(() => Set(instance,request.Generation,
                new(InstanceJavaPreviewState.Canceled,"Java 识别已取消；修改设置或重新打开页面后可重试。")));
            worker.Start(token);
        }
    }
    internal static bool IsCurrentRequest(ModMinecraft.McInstance instance, RedoableWorker<Java> worker) {
        if(!Requests.TryGetValue(worker,out var work)) return false;
        lock(work) return ReferenceEquals(work.Latest?.Owner,instance);
    }

    internal static InstanceJavaPreview? Preview(ModMinecraft.McInstance instance, RedoableWorker<Java> worker) {
        try {
            var inspection = MmcInstanceInspector.Read(instance.PathVersion);
            if (inspection.Management.CanUseLegacyBackend) return null;
            if (!inspection.IsConsistent) return ManagementFailure(inspection);
            var state = States.GetValue(instance, _ => new());
            lock (state) {
                if (worker.Running) return new(InstanceJavaPreviewState.Searching, "正在按整合包组件声明和实际 Java 检查选择……");
                if (state.Value.State==InstanceJavaPreviewState.Searching)
                    return new(InstanceJavaPreviewState.Failed, "Java 识别未完成，请修改设置或重新打开页面后重试。", 1);
                return state.Value with { Message = state.Value.Message + (state.Declaration.Length==0 ? "" : "\r\n" + state.Declaration) };
            }
        } catch (Exception error) when (error is not OperationCanceledException) {
            return new(InstanceJavaPreviewState.Failed, "无法验证实例的 MMC 管理记录，Java 识别已停止。", 1);
        }
    }

    // False authorizes the unchanged legacy route only after a complete ordinary-instance inspection.
    internal static bool TrySelect(ModMinecraft.McInstance instance, CancellationToken token, ProgressProvider? progress, out Java? java) {
        java = null;
        long generation = Executing.Value is { } request && ReferenceEquals(request.Owner,instance) && request.Generation!=0
            ? request.Generation : Begin(instance);
        try {
            token.ThrowIfCancellationRequested();
            var inspection = MmcInstanceInspector.Read(instance.PathVersion);
            if (inspection.Management.CanUseLegacyBackend) return false;
            if (!inspection.IsConsistent) { Set(instance,generation,ManagementFailure(inspection)); return true; }
            var selection = SelectAsync(instance, inspection, generation, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            java = selection.Java;
            Set(instance,generation,selection.RequiresVariantAdoption
                ? new(InstanceJavaPreviewState.VariantRequired, $"兼容的 Java：{java}\r\n启动前需要确认采用对应的运行环境；当前未作更改。", 0)
                : new(InstanceJavaPreviewState.Selected, $"将会使用：{java}", 0));
            progress?.Finish();
            return true;
        } catch (OperationCanceledException) {
            Set(instance,generation,new(InstanceJavaPreviewState.Canceled, "Java 识别已取消；修改设置或重新打开页面后可重试。"));
            throw;
        } catch (MmcLaunchJavaException error) {
            Set(instance,generation,new(InstanceJavaPreviewState.Rejected, error.Message + "（" + error.Code + "）", 1));
            progress?.Skip(); return true;
        } catch (Exception error) {
            Set(instance,generation,new(InstanceJavaPreviewState.Failed, "MMC 保留来源或 Java 实际检查未通过，请检查实例与 Java 后重试。", 1));
            Logger.Error(error, "MMC 设置页 Java 识别失败");
            progress?.Skip(); return true;
        }
    }

    private static async Task<MmcLaunchJavaSelection> SelectAsync(ModMinecraft.McInstance instance, MmcInstanceInspection inspection, long generation, CancellationToken token) {
        var paths = inspection.Paths!;
        var primary = MmcSavedSourceStore.ReadPrimary(paths, token);
        var saved = await MmcSavedSourceStore.LoadAsync(paths, primary.Definition, token).ConfigureAwait(false);
        string declaration = Declaration(saved.Resolved.Resolution.Profile!);
        var state = States.GetValue(instance, _ => new()); lock (state) { if(state.Generation==generation) state.Declaration = declaration; }
        if (instance.Config.ContextStamp.Kind!=MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId!=primary.Definition.InstanceId)
            throw new MmcLaunchJavaException("MMC_JAVA_DEFINITION", "实例设置与已发布 MMC 身份不一致，请重新打开实例。");
        MmcLaunchJavaPreference preference;
        try { preference = MmcLaunchPreferences.CaptureJavaPreference(instance, saved.PackageConfiguration); }
        catch (Exception error) when (error is System.IO.InvalidDataException or ArgumentException) {
            throw new MmcLaunchJavaException("MMC_JAVA_PREFERENCE", error.Message);
        }
        var authentication = MmcLaunchAuthentication.JavaRequirementsForType(LoginType(instance));
        var result = await MmcLaunchJava.SelectAsync(instance, primary.Definition, saved.Resolved.Resolution.Profile!, preference,
            token, authentication.Majors, minimumVersion: authentication.MinimumVersion).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var current = MmcInstanceInspector.Read(instance.PathVersion);
        if (!current.IsConsistent || current.PrimarySha256!=primary.Sha256 || current.Definition!.InstanceId!=primary.Definition.InstanceId)
            throw new MmcLaunchJavaException("MMC_JAVA_DEFINITION", "Java 识别期间实例定义发生变化，请重试。");
        return result;
    }

    private static ModLaunch.McLoginType LoginType(ModMinecraft.McInstance instance) => Settings.Get<int>("VersionServerLogin", instance) switch {
        1 => ModLaunch.McLoginType.Ms,
        2 => ModLaunch.McLoginType.Legacy,
        3 => ModLaunch.McLoginType.Nide,
        4 => ModLaunch.McLoginType.Auth,
        // A settings page may inspect B while A remains selected globally. Ordinary/unknown
        // instance modes never inherit A's third-party account constraint.
        _ => ModLaunch.McLoginType.Legacy
    };
    private static string Declaration(MmcResolvedProfile resolved) {
        var majors = resolved.Components.Where(component => component.Enabled)
            .SelectMany(component => component.Patch.Fields.TryGetValue("compatibleJavaMajors", out var values)
                ? values.EnumerateArray().Select(value => value.GetInt32()) : Enumerable.Empty<int>()).Distinct().Order().ToArray();
        return majors.Length==0 ? "整合包未声明 Java 主版本候选；将按已发布运行视图及实际兼容检查选择。"
            : "整合包声明的候选主版本：" + string.Join("、", majors) + "。自定义范围仅进一步限制候选，实际选择仍需通过组件、账户类型与平台检查。";
    }
    private static InstanceJavaPreview ManagementFailure(MmcInstanceInspection inspection) => new(InstanceJavaPreviewState.Rejected,
        inspection.Management.State==MmcManagementState.PendingTransaction ? "MMC 实例有未完成的导入或事务，请先恢复后再识别 Java。"
        : inspection.Management.State==MmcManagementState.ManagedUnsupported ? "此 MMC 管理版本暂不受支持，无法识别 Java。"
        : "MMC 管理记录或兼容投影需要修复，无法识别 Java。", 1);
    private static long Begin(ModMinecraft.McInstance instance) {
        var state = States.GetValue(instance, _ => new()); lock (state) {
            state.Declaration = ""; state.Value = new(InstanceJavaPreviewState.Searching, "正在按整合包组件声明查找 Java……");
            return ++state.Generation;
        }
    }
    private static void Set(ModMinecraft.McInstance instance, long generation, InstanceJavaPreview value) {
        var state = States.GetValue(instance, _ => new()); lock (state) { if(state.Generation==generation) state.Value = value; }
    }
}
