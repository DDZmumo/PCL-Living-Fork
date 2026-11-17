using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
public sealed class MmcComponentEditorServices {
    public MmcMetadataStore Metadata { get; init; }
    public MmcArtifactCacheRoots CacheRoots { get; init; }
    public Action<ModLoader.LoaderCombo<string>> RegisterTask { get; init; }
    public Action<MmcInstanceDefinitionChangeResult> Published { get; init; }
    public Action<ModNet.LoaderDownload> DownloadCreated { get; init; }
    public Action<MmcTransactionCheckpoint, int> TransactionObserved { get; init; }
    public Action DecisionReady { get; init; }
}
public sealed record MmcComponentEditorLocalFile(string FileName, string AbsolutePath);
public sealed record MmcComponentEditorFileFact(string FileName, string AbsolutePath, string Sha256, long SizeBytes);
public sealed record MmcComponentEditorChoice(MmcInstanceComponentEdit Edit) {
    public ImmutableArray<MmcComponentEditorLocalFile> LocalFiles { get; init; } = [];
    public string JavaPath { get; init; }
    public MmcComponentEditorFileFact PatchFile { get; init; }
}
public sealed record MmcComponentEditorModel(MmcInstanceInspection Source, MmcSavedInstanceSources Saved,
    MmcRuntimeEnvironment PublishedEnvironment, ImmutableDictionary<string, string> PatchTexts);
public sealed record MmcComponentEditorConfirmation(MmcInstanceDefinitionChangePreviewResult Result,
    ImmutableArray<MmcComponentEditorFileFact> Inputs, MmcComponentEditorFileFact JavaImage = null) {
    public bool CanCommit => Result.Status == MmcInstanceBackendStatus.Succeeded && Result.Preview is not null &&
        !Result.Diagnostics.Any(value => value.Severity == MmcDiagnosticSeverity.Error) &&
        !Result.Preview.Diagnostics.Any(value => value.Severity == MmcDiagnosticSeverity.Error);
}

internal static class MmcComponentEditorUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Func<ModMinecraft.McInstance, MmcComponentEditorServices> ServicesFactory { get; set; }
    internal static Func<MmcComponentEditorModel, MmcComponentEditorChoice, MmcComponentEditorChoice> SelectEdit { get; set; }
    // 1 commits the fixed preview, 2 cancels, 3 returns to editing.
    internal static Func<MmcComponentEditorConfirmation, int> ConfirmPreview { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }
    internal static void Prompt(ModMinecraft.McInstance instance) {
        try { Start(instance); }
        catch (Exception error) { Report(error, "无法开始修改 MMC 组件"); }
    }
    internal static Task<MmcComponentEditorModel> LoadModelAsync(ModMinecraft.McInstance instance,
        MmcInstanceRepairSelection selection, CancellationToken token = default) => Task.Run(async () => {
        token.ThrowIfCancellationRequested(); AssertSelected(instance, selection);
        var source = ReadSource(instance);
        var environment = await MmcInstanceRepairSelections.ResolvePublishedEnvironmentAsync(selection, token).ConfigureAwait(false);
        var saved = await MmcSavedSourceStore.LoadAsync(source.Paths, source.Definition, token).ConfigureAwait(false);
        var model = new MmcComponentEditorModel(source, saved, environment, PatchTexts(source, saved, token));
        token.ThrowIfCancellationRequested(); AssertSelected(instance, selection);
        return model;
    }, token);

    internal static ModLoader.LoaderCombo<string> Start(ModMinecraft.McInstance instance) => StartCore(instance, null, null, null);

    internal static ModLoader.LoaderCombo<string> StartWithChoice(ModMinecraft.McInstance instance,
        MmcComponentEditorModel model, MmcComponentEditorChoice choice) {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(choice);
        AssertSelected(instance, Selection(model.Source));
        // Capture before any backend settings pause. There is one Java selector in
        // the product: instance settings. Legacy Start/choice.JavaPath remain usable
        // for the existing controlled API checks, but this page cannot override it.
        var preference = MmcLaunchPreferences.CaptureJavaPreference(instance, model.Saved.PackageConfiguration);
        return StartCore(instance, model, choice with { JavaPath = null }, preference);
    }

    private static MmcInstanceRepairSelection Selection(MmcInstanceInspection source) => new(source.Paths.InstanceRoot,
        source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256);

    private static ModLoader.LoaderCombo<string> StartCore(ModMinecraft.McInstance instance, MmcComponentEditorModel suppliedModel,
        MmcComponentEditorChoice suppliedChoice, MmcLaunchJavaPreference pagePreference) {
        var source = ReadSource(instance);
        if (ModMinecraft.McInstanceLock.IsLocked(instance)) throw new IOException("该实例正在被修改，请稍后再编辑组件。");
        var selected = Selection(suppliedModel?.Source ?? source);
        AssertSelected(instance, selected);
        var services = ServicesFactory?.Invoke(instance) ?? CreateServices(selected.VersionRoot);
        if (services.Metadata is null || services.CacheRoots is null) throw new IOException("MMC 组件编辑缺少下载和元数据服务。");
        // Read before the backend quiesces settings. The callback uses only its
        // supplied game root and this captured value, never a settings provider.
        int retention = BackupPreferences.WorldRetention(instance);
        bool autoBackupWorlds = WorldBackups.AutoEnabled(instance);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 组件修改尚未完成。");
        var fence = new MmcOperationCancellationFence(() => Interlocked.Exchange(ref busy, 0));
        var worker = new MmcOperationWorker<string, MmcInstanceDefinitionChangeResult>(fence, "读取 MMC 组件与个人覆盖", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            var token = linked.Token;
            var model = suppliedModel ?? LoadModelAsync(instance, selected, token).GetAwaiter().GetResult();
            var backend = new MmcInstanceDefinitionChangeBackend<ModNet.NetFile>(services.Metadata, services.CacheRoots, MmcDownloads.BuildFile,
                (files, cancellation) => MmcDownloads.RunBatchAsync(files, cancellation, onLoaderCreated: services.DownloadCreated),
                backupWorlds: (gameRoot, cancellation) => WorldBackups.CaptureFolder(gameRoot, "修改 Minecraft 组件前", retention, cancellation),
                autoBackupWorlds: autoBackupWorlds);
            MmcComponentEditorChoice previous = null;
            for (;;) {
                token.ThrowIfCancellationRequested(); AssertSelected(instance, selected);
                task.Name = "等待选择 MMC 组件修改";
                var choice = suppliedModel is not null ? suppliedChoice : ModBase.RunInUiWait(() => {
                    token.ThrowIfCancellationRequested();
                    return SelectEdit is not null ? SelectEdit(model, previous) : Choose(model, previous);
                });
                if (choice is null) { task.Cancel(); throw new OperationCanceledException(token); }
                token.ThrowIfCancellationRequested(); AssertSelected(instance, selected);
                if (pagePreference is not null) choice = ApplySettingsJava(instance, model, choice, pagePreference, token);
                task.Name = "固定所选组件、补丁和本地文件";
                var frozen = Freeze(model, choice, token);
                task.Name = "计算 MMC 组件实际运行差异";
                var result = backend.PreviewAsync(frozen.Request, token).GetAwaiter().GetResult();
                if (result.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                var confirmation = new MmcComponentEditorConfirmation(result, frozen.Inputs, frozen.JavaImage);
                task.Name = "等待确认 MMC 组件预览";
                // Preview has returned. No admission lease or configuration pause
                // is held while either native dialog waits for the user's choice.
                int decision = ModBase.RunInUiWait(() => {
                    token.ThrowIfCancellationRequested();
                    return ConfirmPreview is not null ? ConfirmPreview(confirmation) : Confirm(confirmation);
                });
                token.ThrowIfCancellationRequested();
                if (decision == 3) {
                    if (suppliedModel is not null) { task.Cancel(); throw new OperationCanceledException(token); }
                    previous = choice; continue;
                }
                if (decision != 1) { task.Cancel(); throw new OperationCanceledException(token); }
                if (!confirmation.CanCommit) throw new IOException("组件预览仍有错误，无法提交：" + result.Failure);
                if (pagePreference is not null && MmcLaunchPreferences.CaptureJavaPreference(instance, model.Saved.PackageConfiguration) != pagePreference)
                    throw new IOException("实例设置中的 Java 选择已变化，请回到组件页重新预览。");
                AssertSelected(instance, selected); VerifyInputs(frozen.Inputs, token);
                if (frozen.JavaImage is not null) VerifyInputs([frozen.JavaImage], token);
                var preview = result.Preview;
                task.Name = preview.Differences.Any(value => value.Field == "minecraft") ? "备份存档并修改 MMC 组件" : "修改 MMC 组件";
                task.Output = backend.CommitAsync(new(preview, preview.CandidateSha256) {
                    BeforeTransaction = () => {
                        VerifyInputs(frozen.Inputs, token);
                        if (frozen.JavaImage is not null) VerifyInputs([frozen.JavaImage], token);
                    },
                    TransactionCheckpoint = (phase, index) => {
                        services.TransactionObserved?.Invoke(phase, index);
                    },
                    BeforeCommitDecision = () => {
                        // The backend has finished its final world/source scan.
                        // Cancellation remains effective throughout that scan.
                        services.DecisionReady?.Invoke();
                        VerifyInputs(frozen.Inputs, token);
                        if (frozen.JavaImage is not null) VerifyInputs([frozen.JavaImage], token);
                        fence.EnterDecision(token);
                    }
                }, token).GetAwaiter().GetResult();
                if (task.Output.Status == MmcInstanceBackendStatus.Cancelled) throw new OperationCanceledException(token);
                if (task.Output.Status != MmcInstanceBackendStatus.Succeeded)
                    throw new IOException("MMC 组件修改未完成，原配置及恢复现场已保留：" + task.Output.Failure);
                break;
            }
            if (services.Published is not null) services.Published(task.Output);
            else ModBase.RunInUi(() => {
                // Refresh the list without navigating a page opened meanwhile.
                ModMinecraft.McInstanceListForceRefresh = true;
                ModMinecraft.McInstanceListLoader.Start(ModMinecraft.McFolderSelected, IsForceRestart: true);
                ModMain.Hint("MMC 组件已修改，可在环境记录中撤回。", ModMain.HintType.Green);
            });
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "修改 MMC 组件：" + instance.Name, [worker]);
        try {
            if (services.RegisterTask is not null) services.RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(selected.VersionRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }

    private static MmcComponentEditorChoice ApplySettingsJava(ModMinecraft.McInstance instance, MmcComponentEditorModel model,
        MmcComponentEditorChoice choice, MmcLaunchJavaPreference preference, CancellationToken token) {
        if (preference.Force) {
            if (string.IsNullOrWhiteSpace(preference.Path)) throw new IOException("实例设置缺少强制 Java 路径，请在“实例设置 → Java”中重新选择。");
            string path = Path.GetFullPath(preference.Path);
            if (!Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
                path = File.Exists(Path.Combine(path, "bin", "java.exe")) ? Path.Combine(path, "bin", "java.exe") : Path.Combine(path, "java.exe");
            // Freeze performs the actual PE/version/bitness/hash probe and the
            // target component preview validates that measured environment.
            return choice with { JavaPath = path };
        }
        if (preference.VersionRange is not null || preference.InstanceFolderOnly) {
            var selected = MmcLaunchJava.SelectAsync(instance, model.Source.Definition, model.Saved.Resolved.Resolution.Profile,
                preference, token).GetAwaiter().GetResult();
            return choice with { JavaPath = selected.Java.JavaExePath };
        }
        // Automatic/inherited selection retains the declared published environment;
        // target component compatibility still has to pass the real backend preview.
        return choice with { JavaPath = null };
    }

    private sealed record Frozen(MmcInstanceDefinitionChangeRequest Request, ImmutableArray<MmcComponentEditorFileFact> Inputs,
        MmcComponentEditorFileFact JavaImage);
    private static Frozen Freeze(MmcComponentEditorModel model, MmcComponentEditorChoice choice, CancellationToken token) {
        if (choice.Edit is null || !model.Saved.Package.Components.Any(value => value.Uid == choice.Edit.Uid))
            throw new IOException("只能修改原包已声明的组件；新增组件请使用整合包来源更新。");
        if (choice.Edit.Reset && (choice.Edit.Enabled is not null || choice.Edit.ExactVersion is not null || choice.Edit.PatchContentUtf8 is not null))
            throw new IOException("恢复作者设置不能同时指定其他修改。");
        var facts = ImmutableArray.CreateBuilder<MmcComponentEditorFileFact>();
        if (choice.PatchFile is { } patch) {
            VerifyInputs([patch], token);
            if (choice.Edit.PatchContentUtf8 is null || Hash(new UTF8Encoding(false, true).GetBytes(choice.Edit.PatchContentUtf8)) != patch.Sha256)
                throw new IOException("所选补丁文件与本次编辑内容不一致，请重新选择文件或使用文本编辑。");
            facts.Add(patch);
        }
        var locals = choice.LocalFiles.IsDefault ? [] : choice.LocalFiles;
        if (locals.Length > 128 || locals.Any(value => value is null) || locals.Select(value => value.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != locals.Length)
            throw new IOException("本地运行库的数量或目标文件名无效。");
        var inputs = ImmutableArray.CreateBuilder<MmcInstanceLocalLibraryInput>();
        foreach (var local in locals) {
            if (string.IsNullOrWhiteSpace(local.FileName) || local.FileName.Contains('/') || local.FileName.Contains('\\')) throw new IOException("本地运行库必须指定单个目标文件名。");
            model.Source.Paths.ResolveRelative(MmcPathRole.LocalLibraries, local.FileName);
            var fact = Observe(local.FileName, local.AbsolutePath, 4L * 1024 * 1024 * 1024, token);
            string live = Path.Combine(model.Source.Paths.InstanceRoot, "libraries") + Path.DirectorySeparatorChar;
            if (fact.AbsolutePath.StartsWith(live, StringComparison.OrdinalIgnoreCase) && !model.Source.Definition.ArtifactLocks.Any(value =>
                value.TargetRelativePath.Equals("libraries/" + Path.GetRelativePath(live, fact.AbsolutePath).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) && value.AdoptedSha256 == fact.Sha256 && value.SizeBytes == fact.SizeBytes))
                throw new IOException("所选实例运行库不是已采用的原始字节，请选择实例目录以外的明确来源文件。");
            facts.Add(fact); inputs.Add(new(local.FileName, fact.AbsolutePath, fact.Sha256));
        }
        var environment = model.PublishedEnvironment; MmcComponentEditorFileFact javaImage = null;
        if (!string.IsNullOrWhiteSpace(choice.JavaPath)) (environment, javaImage) = ProbeJava(choice.JavaPath, token);
        var source = model.Source;
        return new(new(source.Paths.InstanceRoot, source.Definition.InstanceId, source.Definition.Generation, source.PrimarySha256,
            environment, [choice.Edit]) { LocalInputs = inputs.ToImmutable() }, facts.ToImmutable(), javaImage);
    }
    internal static MmcComponentEditorFileFact Observe(string name, string path, long maximum, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path)) throw new IOException("请明确选择本地文件的完整路径。");
        path = Path.GetFullPath(path); AssertFilePath(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = input.Length; if (size > maximum) throw new IOException("所选文件超过允许的大小。");
        string sha = Convert.ToHexStringLower(SHA256.HashData(input)); token.ThrowIfCancellationRequested();
        if (input.Length != size) throw new IOException("所选文件在读取期间发生变化。");
        return new(name, path, sha, size);
    }
    private static void VerifyInputs(ImmutableArray<MmcComponentEditorFileFact> inputs, CancellationToken token) {
        foreach (var fact in inputs) {
            var current = Observe(fact.FileName, fact.AbsolutePath, 4L * 1024 * 1024 * 1024, token);
            if (current != fact) throw new IOException("所选补丁、Java 或本地运行库在预览后发生变化：" + fact.FileName);
        }
    }
    internal static (string Text, MmcComponentEditorFileFact Fact) ReadPatchFile(string path) {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("请明确选择补丁文件的完整路径。");
        path = Path.GetFullPath(path); AssertFilePath(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 16 * 1024 * 1024) throw new IOException("所选补丁超过允许的大小。");
        using MemoryStream content = new(); byte[] buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
            if (content.Length + count > 16 * 1024 * 1024) throw new IOException("所选补丁超过允许的大小。");
            content.Write(buffer, 0, count);
        }
        byte[] bytes = content.ToArray();
        return (new UTF8Encoding(false, true).GetString(bytes), new(Path.GetFileName(path), path, Hash(bytes), bytes.LongLength));
    }
    private static void AssertFilePath(string path) {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) throw new IOException("所选文件不能使用设备命名空间路径。");
        for (string current = path; current is not null; current = Path.GetDirectoryName(current)) {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || current != path && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("所选文件的路径包含链接或无效的父目录。");
        }
    }
    private static ImmutableDictionary<string, string> PatchTexts(MmcInstanceInspection source, MmcSavedInstanceSources saved, CancellationToken token) {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var component in saved.Resolved.Resolution.Profile.Components) {
            token.ThrowIfCancellationRequested();
            var personal = source.Definition.ComponentOverrides.FirstOrDefault(value => value.Uid == component.Uid)?.PatchReference;
            string relative = personal?.Sha256 == component.Patch.ContentSha256 ? personal.ObjectRelativePath :
                source.Definition.SourceBundle.MetadataRecords.FirstOrDefault(value => value.Kind == MmcSourceRecordKind.Component && value.ContentSha256 == component.Patch.ContentSha256)?.ObjectRelativePath;
            if (relative is null) throw new IOException("当前组件缺少已固定的原始补丁：" + component.Uid);
            var read = ReadPatchFile(source.Paths.ResolveRelative(MmcPathRole.Instance, relative));
            if (read.Fact.Sha256 != component.Patch.ContentSha256) throw new IOException("组件原始补丁发生变化：" + component.Uid);
            result.Add(component.Uid, read.Text);
        }
        return result.ToImmutable();
    }
    private static (MmcRuntimeEnvironment Environment, MmcComponentEditorFileFact Image) ProbeJava(string path, CancellationToken token) {
        if (!Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择实际的 java.exe 文件。");
        var image = Observe("java.exe", path, 64 * 1024 * 1024, token);
        string architecture;
        using (var file = File.OpenRead(image.AbsolutePath)) using (var reader = new BinaryReader(file)) {
            if (file.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new IOException("所选 Java 不是有效的 Windows 程序。");
            file.Position = 0x3c; int offset = reader.ReadInt32();
            if (offset < 64 || offset > file.Length - 26) throw new IOException("所选 Java 的程序头损坏。");
            file.Position = offset; if (reader.ReadUInt32() != 0x4550) throw new IOException("所选 Java 的程序头无效。");
            architecture = reader.ReadUInt16() switch { 0x8664 => "x86_64", 0xaa64 => "arm64", _ => throw new IOException("请选择受支持的 64 位 Java。") };
        }
        string output = null; var java = new Java(Path.GetDirectoryName(image.AbsolutePath));
        bool usable = java.CheckAsync(token, async (file, cancellation) => output = await CleanJavaProbe(file, cancellation).ConfigureAwait(false)).GetAwaiter().GetResult();
        if (!usable || java.Version is null || output is null) throw new IOException("所选 Java 未通过实际运行检查。");
        string Property(string name) => output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith(name + " = ", StringComparison.Ordinal))?[(name.Length + 3)..].Trim();
        string arch = Property("os.arch");
        if (Property("sun.arch.data.model") != "64" || (arch switch { "amd64" or "x86_64" => "x86_64", "aarch64" or "arm64" => "arm64", _ => arch }) != architecture)
            throw new IOException("Java 实际报告的架构或位数与其程序不一致。");
        VerifyInputs([image], token); return (new("windows", architecture, java.Version.Major, 64), image);
    }
    private static async Task<string> CleanJavaProbe(string file, CancellationToken token) {
        ProcessStartInfo start = new(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(file) };
        foreach (string argument in new[] { "-Dsun.stdout.encoding=UTF-8", "-Dsun.stderr.encoding=UTF-8", "-XshowSettings:properties", "-version" }) start.ArgumentList.Add(argument);
        foreach (string key in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new IOException("Java 检查进程未能启动。");
        var stdout = ReadOutput(process.StandardOutput, token); var stderr = ReadOutput(process.StandardError, token);
        try {
            await Task.WhenAll(process.WaitForExitAsync(token), stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Java 检查进程未成功退出。");
            return stdout.Result + "\n" + stderr.Result;
        } catch {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }
    private static async Task<string> ReadOutput(StreamReader reader, CancellationToken token) {
        StringBuilder result = new(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) {
            if (result.Length + count > 1024 * 1024) throw new IOException("Java 检查输出超过允许的大小。");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
    private static MmcComponentEditorChoice Choose(MmcComponentEditorModel model, MmcComponentEditorChoice previous) {
        var view = new MmcComponentEditorView(model, previous);
        return ModMain.MyMsgBoxConfirmContent("选择一个已有组件，并设置本次修改。下一步会解析真实运行配置。", "修改 MMC 组件", "计算实际预览", () => view, IsWarn: false) == 1
            ? view.ReadChoice() : null;
    }
    private static int Confirm(MmcComponentEditorConfirmation confirmation) {
        var view = new MmcComponentEditorPreviewView(confirmation);
        if (!confirmation.CanCommit) {
            view.SetCanConfirm(true); // This button can only return to editing.
            return ModMain.MyMsgBoxConfirmContent("组件或 Java 要求尚不能满足，请查看诊断并修改选择。需要更换 Java 时，请在“实例设置 → Java”中调整。", "MMC 组件尚不能提交", "修改选择", () => view, IsWarn: false) == 1 ? 3 : 2;
        }
        return ModMain.MyMsgBoxConfirmContent("请确认实际解析后的入口、参数、Java 和运行库差异。提交前会保存可撤回的环境记录；Minecraft 版本变化时先备份当前存档。",
            "确认 MMC 组件修改", "按以上结果修改", () => view, ThirdText: "修改选择", IsWarn: true);
    }
    private static void AssertSelected(ModMinecraft.McInstance instance, MmcInstanceRepairSelection selected) {
        var current = ReadSource(instance);
        if (!MeloongCore.PathUtils.IsSame(current.Paths.InstanceRoot, selected.VersionRoot) || current.Definition.InstanceId != selected.InstanceId ||
            current.Definition.Generation != selected.Generation || current.PrimarySha256 != selected.PrimarySha256)
            throw new IOException("所选实例的身份或组件来源已变化，请重新打开组件编辑。");
    }
    private static MmcInstanceInspection ReadSource(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance); instance.Config.AssertAttached(); var source = MmcInstanceInspector.Read(instance.PathVersion);
        if (!source.IsConsistent || source.Paths is null || source.Definition is null || source.PrimarySha256 is null ||
            instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != source.Definition.InstanceId)
            throw new IOException(source.Error ?? "实例管理状态已变化，请重新打开版本页面。");
        return source;
    }
    private static MmcComponentEditorServices CreateServices(string root) {
        string game = Path.GetDirectoryName(Path.GetDirectoryName(root)), cache = Path.Combine(game, "PCL", "MmcCache");
        return new() { Metadata = MmcDownloads.CreateMetadataStore(Path.Combine(cache, "metadata")),
            CacheRoots = new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(cache, "sources"), Path.Combine(game, "versions")) };
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void Report(Exception error, string text) {
        if (ErrorObserved is not null) ErrorObserved(error, text);
        else MeloongCore.Logger.Error(error, text, MeloongCore.LogBehavior.Alert);
    }
}
