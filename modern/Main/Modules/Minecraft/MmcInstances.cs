using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host integration; governed by the repository LICENCE.
/// <summary>Small host seams for owned facade fixtures; production still uses the genuine Loader/NetFile backend.</summary>
public sealed class MmcImportServices {
    public MmcMetadataStore Metadata { get; init; }
    public MmcArtifactCacheRoots CacheRoots { get; init; }
    public Func<string, string> SelectName { get; init; }
    public Action<ModLoader.LoaderCombo<string>> RegisterTask { get; init; }
    public Action<MmcInstanceBackendResult> Published { get; init; }
    public Action<ModNet.LoaderDownload> DownloadCreated { get; init; }
    public Action<MmcTransactionCheckpoint, int> TransactionObserved { get; init; }
    public Action<MmcInstanceBackendProgress> ProgressObserved { get; init; }
}

public static class MmcInstances {
    // Only transport/UI boundaries are replaceable. Source parsing, Java probing/filtering, file
    // publication, cancellation and the Loader workload are always the production implementations.
    internal static Func<ModpackMmcInstallRequest, MmcImportServices> ServicesFactory { get; set; }

    public static ModLoader.LoaderCombo<string> StartImport(ModpackMmcInstallRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        var services = ServicesFactory?.Invoke(request) ?? CreateServices(request);
        if (services.Metadata is null || services.CacheRoots is null) throw new InvalidOperationException("MMC 导入缺少下载和元数据服务。");
        var cancellation = new MmcOperationCancellationFence();
        var progress = new MmcImportProgress();
        ModLoader.LoaderCombo<string> combo = null;
        var worker = new MmcOperationWorker<string, MmcInstanceBackendResult>(cancellation, "读取 MMC 整合包", task => {
            progress.Attach(task); progress.Begin(MmcImportStage.ReadArchive);
            task.Output = ImportAsync(task, request, services, name => combo.Name = "MMC 整合包安装：" + name, cancellation, progress).GetAwaiter().GetResult();
        }) { InterruptOnCancel = false };
        combo = new MmcImportOperationCombo<string>(cancellation, progress, "MMC 整合包安装：" + Path.GetFileNameWithoutExtension(request.ArchivePath), [worker]);
        // Deliberately does not attach McInstallState: its legacy one-second failure cleanup does
        // not own MMC import reservations or durable recovery journals.
        try {
            if (services.RegisterTask is not null) services.RegisterTask(combo);
            else {
                ModLoader.LoaderTaskbarAdd(combo);
                ModBase.RunInUi(() => ModMain.FrmMain.BtnExtraDownload.ShowRefresh());
            }
            combo.Start(request.ArchivePath);
            return combo;
        } catch { cancellation.AbandonUnstarted();throw; }
    }

    private static async Task<MmcInstanceBackendResult> ImportAsync(ModLoader.LoaderTask<string, MmcInstanceBackendResult> task,
        ModpackMmcInstallRequest request, MmcImportServices services, Action<string> named, MmcOperationCancellationFence cancellation, MmcImportProgress progress) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), cancellation.Token);
        CancellationToken token = linked.Token;
        using var source = await MmcArchiveSourceReader.ReadAsync(request.ArchivePath, cancellationToken: token,
            progress: new SyncProgress<MmcArchiveProgress>(value => {
                progress.Archive(value);
                services.ProgressObserved?.Invoke(new(value.Phase, value.CompletedBytes, value.TotalBytes));
            })).ConfigureAwait(false);
        progress.SourceRead(source);
        token.ThrowIfCancellationRequested();
        string candidate = request.InstanceName ?? source.Package.Configuration.Name;
        string name = services.SelectName is not null ? services.SelectName(candidate) : SelectName(request.MinecraftFolder, candidate);
        if (string.IsNullOrWhiteSpace(name)) throw new OperationCanceledException(token);
        var validator = new ValidateFolderName(Path.Combine(request.MinecraftFolder, "versions"));
        string validation = validator.Validate(name);
        if (!string.IsNullOrEmpty(validation)) throw new InvalidDataException("整合包实例名称无效：" + validation);
        named(name); progress.Begin(MmcImportStage.ResolveComponents);
        var resolved = await MmcMetadataResolution.ResolveAsync(source.Package, services.Metadata, cancellationToken: token).ConfigureAwait(false);
        if (!resolved.Resolution.IsComplete || resolved.Resolution.Profile is null)
            throw new InvalidDataException("无法解析 MMC 组件：" + string.Join("；", resolved.Resolution.Diagnostics.Where(value => value.Severity == MmcDiagnosticSeverity.Error).Select(value => value.Message)));
        progress.Begin(MmcImportStage.SelectJava);
        var environment = await SelectJavaAsync(source.Package, resolved.Resolution.Profile, token).ConfigureAwait(false);
        string target = Path.Combine(request.MinecraftFolder, "versions", name);
        var backend = new MmcInstanceBackend<ModNet.NetFile>(services.Metadata, services.CacheRoots, MmcDownloads.BuildFile,
            (files, cancellation) => progress.DownloadAsync(files, cancellation, services.DownloadCreated));
        var result = await backend.ImportPreparedAsync(source, new(request.ArchivePath, target, name, environment) {
            TransactionCheckpoint = (phase, index) => {
                if (phase == MmcTransactionCheckpoint.BeforeDecision) { cancellation.EnterDecision(token); progress.EnteredDecision(); }
                services.TransactionObserved?.Invoke(phase, index);
            }
        }, token,
            new SyncProgress<MmcInstanceBackendProgress>(value => {
                progress.Backend(value);
                services.ProgressObserved?.Invoke(value);
            }), resolved).ConfigureAwait(false);
        if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(result.Failure, token);
        if (result.Status != MmcInstanceBackendStatus.Succeeded || result.Definition is null || result.Paths is null)
            throw new IOException("MMC 整合包未完成安装：" + result.Failure + (result.WorkingRoot is null ? "" :
                "\n导入现场：" + result.WorkingRoot + "\n可在版本选择页的“MMC 导入恢复”中查看记录。"));
        ModMinecraft.McInstanceListForceRefresh = true;
        ModMinecraft.McInstanceListLoader.Start(request.MinecraftFolder, IsForceRestart: true);
        if (services.Published is not null) services.Published(result);
        else {
            ModBase.RunInUi(() => ModMain.Hint("MMC 整合包安装完成：" + name));
        }
        // A retained-but-inert package requirement is only a difference notice until the player is told about
        // it. Reporting it beside the completion hint keeps the requirement visible without failing an import
        // that is otherwise correct, and never turns it into a capability this launcher does not have.
        if (result.Diagnostics.Length != 0) {
            string[] differences = result.Diagnostics
                .Where(value => value.Code is MmcDisplayDifference.ConsoleCode or MmcDisplayDifference.PermanentGenerationCode)
                .Select(value => value.Message).ToArray();
            if (differences.Length != 0) ModBase.RunInUi(() => ModMain.Hint(string.Join("\n", differences), ModMain.HintType.Blue));
        }
        return result;
    }

    private static string SelectName(string minecraftFolder, string candidate) => ModBase.RunInUiWait(() => {
        var rule = new ValidateFolderName(Path.Combine(minecraftFolder, "versions"));
        if (!string.IsNullOrWhiteSpace(candidate) && rule.Validate(candidate) == "") return candidate;
        return ModMain.MyMsgBoxInput("输入版本名称", "", "", new Collection<Validate> { rule });
    });

    internal static async Task<MmcRuntimeEnvironment> SelectJavaAsync(MmcPackage package, MmcResolvedProfile resolved, CancellationToken token) {
        var provisional = MmcProfileCompiler.Compile(resolved, new("windows", "x86_64", 0));
        if (provisional.HasErrors || provisional.Profile is null) throw new InvalidDataException("整合包启动配置无法解析。" + string.Join("；", provisional.Diagnostics.Select(value => value.Message)));
        var policy = provisional.Profile.JavaPolicy;
        if (policy.Kind == MmcJavaPolicyKind.Unsatisfiable) throw new InvalidDataException("整合包声明的 Java 要求无法同时满足。");
        // Current PCL global Java preferences are the ordered catalogue. The retired
        // LaunchArgumentJavaSelect value is used only by JavaInit's one-time migration;
        // it must not reintroduce a forced path after the user changes the current list.
        string recommended = package.Configuration.Values.TryGetValue("OverrideJavaLocation", out string flag) && bool.TryParse(flag, out bool enabled) && enabled
            ? package.Configuration.Values.GetValueOrDefault("JavaPath") : null;
        for (int attempt = 0; attempt < 2; attempt++) {
            var candidates = Configs.JavaList.Get()!.ToArray();
            if (!string.IsNullOrWhiteSpace(recommended)) candidates = candidates.OrderByDescending(java => SameJavaPath(java, recommended)).ToArray();
            foreach (Java candidate in candidates) {
                token.ThrowIfCancellationRequested();
                if (!await candidate.CheckAsync(token).ConfigureAwait(false) || candidate.Version is null || !policy.Allows(candidate.Version.Major)) continue;
                string architecture = JavaArchitecture(candidate.JavaExePath);
                var actual = MmcProfileCompiler.Compile(resolved, new("windows", architecture, candidate.Version.Major, 64));
                if (!actual.HasErrors && actual.Profile is not null && actual.Profile.JavaPolicy.Allows(candidate.Version.Major)) return actual.Profile.Environment;
            }
            if (attempt == 0) {
                ModJava.JavaListRefreshWorker.Start(cancellationToken: token);
                ModJava.JavaListRefreshWorker.WaitIfRunning(cancellationToken: token);
            }
        }
        string versions = policy.Kind == MmcJavaPolicyKind.DeclaredCandidates ? string.Join("、", policy.AllowedMajors) : "符合整合包要求的版本";
        throw new InvalidOperationException("当前 Java 列表中没有通过检查且符合整合包声明的 Java（" + versions + "）。请在设置 → 启动 → Java 列表中下载或导入兼容版本后重试。");
    }
    private static bool SameJavaPath(Java candidate, string path) {
        try { return PathUtils.IsSame(candidate.Folder, path) || PathUtils.IsSame(candidate.JavaExePath, path); } catch (ArgumentException) { return false; }
    }
    private static string JavaArchitecture(string executable) {
        using FileStream input = File.OpenRead(executable); using BinaryReader reader = new(input);
        if (input.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Java 入口不是有效的 Windows 程序。");
        input.Position = 0x3c; int offset = reader.ReadInt32();
        if (offset < 64 || offset > input.Length - 6) throw new InvalidDataException("Java 程序头损坏。");
        input.Position = offset; if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Java 程序头无效。");
        return reader.ReadUInt16() switch { 0x8664 => "x86_64", 0xaa64 => "arm64", _ => throw new InvalidDataException("MMC 当前需要受支持的 64 位 Java。") };
    }
    private static MmcImportServices CreateServices(ModpackMmcInstallRequest request) {
        string cache = Path.Combine(request.MinecraftFolder, "PCL", "MmcCache");
        return new() {
            Metadata = MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")),
            CacheRoots = new(Path.Combine(request.MinecraftFolder, "libraries"), Path.Combine(request.MinecraftFolder, "assets"), Path.Combine(cache, "sources"), Path.Combine(request.MinecraftFolder, "versions"))
        };
    }
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
