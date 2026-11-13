using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
internal static class MmcExportUi {
    private static int busy;
    internal static bool HasActiveOperation => Volatile.Read(ref busy) != 0;
    internal static Action<ModLoader.LoaderCombo<string>> RegisterTask { get; set; }
    internal static Func<MmcInstanceExportOptions> SelectOptions { get; set; }
    internal static Func<string, string> SelectOutput { get; set; }
    internal static Func<MmcInstanceExportPreview, bool> ConfirmPreview { get; set; }
    internal static Action<MmcInstanceExportResult> Published { get; set; }
    internal static Action<Exception, string> ErrorObserved { get; set; }
    internal static Action<MmcInstanceExportCheckpoint, int> CheckpointObserved { get; set; }

    internal static void Prompt(ModMinecraft.McInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);
        ModBase.RunInNewThread(() => {
            try {
                // Fix identity before any user dialog; an object left open while another
                // operation replaces this path must never export the replacement instance.
                instance.Config.AssertAttached();
                var source = MmcInstanceInspector.Read(instance.PathVersion);
                if (!source.IsConsistent || source.Paths is null || source.Definition is null || source.PrimarySha256 is null ||
                    instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed || instance.Config.ContextStamp.InstanceId != source.Definition.InstanceId)
                    throw new IOException(source.Error ?? "实例状态已变化，请重新打开版本页面。");
                ModBase.RunInUi(() => {
                    try {
                        MmcInstanceExportOptions options;
                        if (SelectOptions is not null) options = SelectOptions();
                        else {
                            MmcExportOptionsView view = null;
                            if (ModMain.MyMsgBoxConfirmContent("导出当前组件、模组和配置。存档、截图及服务器默认不包含；模组自己的配置仍可能含有个人信息。",
                                "导出 MMC 整合包", "预览导出", () => view = new MmcExportOptionsView(), IsWarn: false) != 1) return;
                            options = view.ReadOptions();
                        }
                        if (options is null) return;
                        string output = SelectOutput is not null ? SelectOutput(instance.Name) :
                            MeloongCore.Wpf.Dialogs.SaveFile("导出 MMC 整合包（已有文件不会覆盖）", instance.Name + ".zip", filter: new[] { ("zip", "MMC / Prism 整合包") });
                        if (string.IsNullOrWhiteSpace(output)) return;
                        Start(new(source.Paths.InstanceRoot, output, instance.Name, source.Definition.InstanceId,
                            source.Definition.Generation, source.PrimarySha256, options));
                    } catch (Exception error) { Report(error, "无法开始导出 MMC 整合包"); }
                });
            } catch (Exception error) { Report(error, "无法读取 MMC 导出来源"); }
        }, "Preview MMC export");
    }

    internal static ModLoader.LoaderCombo<string> Start(MmcInstanceExportRequest request) {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new IOException("另一项 MMC 导出任务尚未完成。");
        var fence = new MmcOperationCancellationFence(() => Interlocked.Exchange(ref busy, 0));
        var worker = new MmcOperationWorker<string, MmcInstanceExportResult>(fence, "核对导出内容", task => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.CreateCancellationToken(), fence.Token);
            var backend = new MmcInstanceExportBackend();
            var preview = backend.PreviewAsync(request, linked.Token).GetAwaiter().GetResult();
            linked.Token.ThrowIfCancellationRequested();
            // PreviewAsync has released every file/configuration/admission lease before
            // asking the user. ExportAsync measures the same preview again, under new leases.
            bool accepted = ModBase.RunInUiWait(() => ConfirmPreview is not null ? ConfirmPreview(preview) :
                ModMain.MyMsgBox(PreviewText(preview), "确认 MMC 导出内容", "导出", "取消", IsWarn: false) == 1);
            if (!accepted) { task.Cancel(); throw new OperationCanceledException("已取消 MMC 导出预览。"); }
            linked.Token.ThrowIfCancellationRequested();
            task.Name = "写入 MMC 整合包";
            var result = backend.ExportAsync(request with {
                ExpectedPreviewFingerprint = preview.Fingerprint,
                Checkpoint = (phase, index) => {
                    CheckpointObserved?.Invoke(phase, index);
                    request.Checkpoint?.Invoke(phase, index);
                    if (phase == MmcInstanceExportCheckpoint.BeforePublication) fence.EnterDecision(linked.Token);
                }
            }, linked.Token, new Progress(task)).GetAwaiter().GetResult();
            task.Output = result;
            if (result.Status == MmcInstanceExportStatus.Cancelled) throw new OperationCanceledException(linked.Token);
            if (result.Status != MmcInstanceExportStatus.Completed)
                throw new IOException("导出未完成：" + result.Failure + (result.RetainedPartialFile is null ? "" : "\n未完成的临时文件保留在：" + result.RetainedPartialFile));
            if (Published is not null) Published(result);
            else ModBase.RunInUi(() => ModMain.Hint("MMC 整合包已导出：" + result.OutputZipPath, ModMain.HintType.Green));
        }) { InterruptOnCancel = false };
        var combo = new MmcOperationCombo<string>(fence, "导出 MMC 整合包：" + request.DisplayName, [worker]);
        try {
            if (RegisterTask is not null) RegisterTask(combo);
            else { ModLoader.LoaderTaskbarAdd(combo); ModBase.RunInUi(() => ModMain.FrmMain?.BtnExtraDownload.ShowRefresh()); }
            combo.Start(request.SourceRoot); return combo;
        } catch { fence.AbandonUnstarted(); throw; }
    }

    private static string PreviewText(MmcInstanceExportPreview preview) {
        string text = "将导出 " + preview.EntryCount.ToString(CultureInfo.InvariantCulture) + " 个文件，未压缩大小 " +
            (preview.UncompressedBytes / 1048576d).ToString("0.##", CultureInfo.InvariantCulture) + " MiB。\n现有同名文件不会被覆盖。";
        if (!preview.Transformations.IsEmpty) text += "\n\n导出时的调整：\n" + string.Join("\n", preview.Transformations.Select(Describe));
        return text;
    }
    private static string Describe(string value) => value switch {
        "Machine-specific Java selection omitted; component compatibleJavaMajors remains authoritative." => "由接收方选择符合整合包要求的 Java，不携带本机 Java 路径。",
        "Hardware-dependent memory policy frozen to the explicitly selected portable MiB values." => "自动内存改为本次明确填写的固定内存值。",
        "Server auto-join data omitted by the explicit default player-data exclusion policy." => "按本次选择移除服务器自动加入信息。",
        "Instance JVM override becomes the new package override, preserving an explicit empty value." => "保留当前实例的 JVM 参数设置。",
        "Inactive game tree explicitly excluded; the active role is unchanged." => "排除未启用的另一游戏目录。",
        "An active package console preference is not portable and was omitted; it is not a launcher-visibility setting." => "整合包的独立控制台偏好无法随包导出，已移除；它不等同于启动器可见性设置。",
        "A declared PermGen size is not portable and was omitted; it was not converted into a Metaspace option." => "整合包声明的 PermGen 大小无法随包导出，已移除；未换算为 Metaspace 参数。",
        _ => value
    };
    private static void Report(Exception error, string text) {
        if (ErrorObserved is not null) ErrorObserved(error, text);
        else MeloongCore.Logger.Error(error, text, MeloongCore.LogBehavior.Alert);
    }
    private sealed class Progress(ModLoader.LoaderTask<string, MmcInstanceExportResult> task) : IProgress<MmcInstanceExportProgress> {
        public void Report(MmcInstanceExportProgress value) {
            if (value.TotalBytes > 0) task.Progress = Math.Min(0.99, (double)value.CompletedBytes / value.TotalBytes);
        }
    }
}

internal sealed class MmcExportOptionsView : StackPanel {
    internal double PreferredWidth => 660;
    internal readonly MyCheckBox Saves = Choice("包含存档"), Screenshots = Choice("包含截图"), Servers = Choice("包含服务器列表及自动加入设置");
    internal readonly MyCheckBox OtherGameFiles = Choice("包含未识别的游戏文件（可能含个人数据）"), ExcludeInactive = Choice("不导出未启用的另一游戏目录");
    internal readonly MyCheckBox PortableJava = Choice("移除本机 Java 选择，保留整合包的 Java 要求"), FixedMemory = Choice("将自动内存改为下方固定值（MiB）");
    internal readonly MyTextBox Minimum = new() { Text = "0", Width = 90, Margin = new Thickness(8, 0, 20, 0) };
    internal readonly MyTextBox Maximum = new() { Text = "4096", Width = 90, Margin = new Thickness(8, 0, 0, 0) };
    internal MmcExportOptionsView() {
        foreach (var check in new[] { Saves, Screenshots, Servers, OtherGameFiles, ExcludeInactive, PortableJava, FixedMemory }) Children.Add(check);
        var memory = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        memory.Children.Add(new TextBlock { Text = "最小", VerticalAlignment = VerticalAlignment.Center }); memory.Children.Add(Minimum);
        memory.Children.Add(new TextBlock { Text = "最大", VerticalAlignment = VerticalAlignment.Center }); memory.Children.Add(Maximum); Children.Add(memory);
        Minimum.IsEnabled = Maximum.IsEnabled = false;
        FixedMemory.Change += (_, _) => Minimum.IsEnabled = Maximum.IsEnabled = FixedMemory.Checked;
    }
    internal MmcInstanceExportOptions ReadOptions() {
        int? minimum = null, maximum = null;
        if (FixedMemory.Checked) {
            if (!int.TryParse(Minimum.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int min) ||
                !int.TryParse(Maximum.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int max) || min < 0 || max < 16 || max > 1048576 || min > max)
                throw new IOException("请填写有效的内存范围：最大值至少 16 MiB，最小值不能大于最大值。");
            minimum = min; maximum = max;
        }
        return new(Saves.Checked, Screenshots.Checked, Servers.Checked) {
            IncludeUnrecognizedGameFiles = OtherGameFiles.Checked,
            InactiveGameTree = ExcludeInactive.Checked ? MmcExportInactiveTree.Exclude : MmcExportInactiveTree.RejectNonempty,
            DiscardMachineJavaSelection = PortableJava.Checked, PortableMinimumMemoryMiB = minimum, PortableMaximumMemoryMiB = maximum
        };
    }
    private static MyCheckBox Choice(string text) => new() { Text = text, Checked = false, Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
}
