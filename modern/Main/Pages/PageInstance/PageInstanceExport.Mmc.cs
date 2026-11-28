using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
// Managed export shares the original page and controls, but never its ordinary
// version-reconstruction pipeline. Only the existing MMC export options are offered.
public partial class PageInstanceExport {
    private ModMinecraft.McInstance ExportOwner;
    private string _exportOwnerRoot = "";
    private Guid _mmcExportInstanceId;
    private long _mmcExportGeneration;
    private string _mmcExportPrimarySha256 = "";
    private int _mmcExportRevision;
    private bool IsMmcExport;
    private CancellationTokenSource _mmcFileListCancellation;
    private CheckBox _mmcOtherGameFiles;
    private StackPanel _mmcRequiredFiles, _mmcScreenshotFiles, _mmcServerFiles, _mmcOtherFiles;
    internal Task MmcInventoryTask { get; private set; } = Task.CompletedTask;

    private void CaptureExportOwner(ModMinecraft.McInstance owner, bool managed, Guid expectedInstanceId) {
        ExportOwner = owner;
        _exportOwnerRoot = owner?.PathVersion ?? "";
        var observed = owner is null ? null : LauncherInstanceContext.ObserveInstanceContext(owner);
        IsMmcExport = managed || observed?.IsMmcFamily == true;
        if (!IsMmcExport || owner is null) return;
        var attached = owner.Config.ContextStamp;
        _mmcExportInstanceId = expectedInstanceId != Guid.Empty ? expectedInstanceId :
            attached.Kind == MmcConfigurationContextKind.Managed ? attached.InstanceId : observed.InstanceId;
    }

    internal bool IsManagedOwner(ModMinecraft.McInstance owner, Guid instanceId) => IsMmcExport &&
        IsExportOwner(owner) &&
        _mmcExportInstanceId == instanceId;

    internal bool IsExportOwner(ModMinecraft.McInstance owner) => ReferenceEquals(ExportOwner, owner) &&
        owner is not null && PathUtils.IsSame(owner.PathVersion, _exportOwnerRoot);

    private bool CanUseOrdinaryExport() {
        var active = LauncherInstanceContext.Instance;
        bool valid = !IsMmcExport && (ExportOwner is null ? active is null :
            ReferenceEquals(active, ExportOwner) && PathUtils.IsSame(active.PathVersion, _exportOwnerRoot) &&
            !LauncherInstanceContext.ObserveInstanceContext(ExportOwner).IsMmcFamily);
        if (!valid && BtnExport is not null) BtnExport.IsEnabled = false;
        return valid;
    }

    private void InitializeMmcExport() {
        HintOptiFine.Visibility = Visibility.Collapsed;
        HintModrinthFormat.Visibility = Visibility.Collapsed;
        BtnOverrideCancel.Visibility = Visibility.Collapsed;
        PanAdvancedStandard.Visibility = Visibility.Collapsed;
        PanMmcPortability.Visibility = Visibility.Visible;
        MmcInactiveNotice.Tag = DialogTone.Warning;
        GlassDialogControls.Callout(MmcJavaNotice);
        GlassDialogControls.Callout(MmcInactiveNotice);
        CardAdvanced.Header = "可移植设置";
        CardAdvanced.IsExpanded = true;
        LabOptionsTitle.Text = "导出内容列表";
        LabExportVersion.Text = "文件名版本";
        _versionHint.Text = "可留空";
        TextExportVersion.ToolTip = "仅作为导出 ZIP 文件名的版本标记；组件版本与补丁按当前配置保留。";
        GlassPageLayout.Frost(BtnExport, "预览并导出", InstanceSidebarIcons.Export);

        // Reparent the actual original checkboxes and file-list panels. The ordinary
        // page's complete layout stays in the same BAML for ordinary instances.
        PanOptions.Children.Clear();
        PanOptionsResourcePacks.Columns = PanOptionsSaves.Columns = 1;
        PanOptionsResourcePacks.Children.Clear();
        PanOptionsSaves.Children.Clear();
        BindingOperations.ClearBinding(PanOptionsSaves, VisibilityProperty);
        PanOptionsSaves.Visibility = Visibility.Visible;
        ConfigureMmcCheck(CheckOptionsBasic, "组件、补丁与运行库", true, fixedChoice: true);
        ConfigureMmcCheck(CheckOptionsMod, "模组、配置与整合包文件", true, fixedChoice: true);
        ConfigureMmcCheck(CheckOptionsResourcePacks, "资源包与光影包", true, fixedChoice: true);
        ConfigureMmcCheck(CheckOptionsSaves, "存档与地图个人数据", false);
        ConfigureMmcCheck(CheckOptionsScreenshots, "截图", false);
        ConfigureMmcCheck(CheckOptionsServers, "服务器列表及自动加入设置", false);
        _mmcOtherGameFiles = GlassDialogControls.Check("允许携带未识别的游戏文件（可能含个人信息）", false);
        _mmcOtherGameFiles.Name = "CheckMmcOtherGameFiles";
        RegisterName(_mmcOtherGameFiles.Name, _mmcOtherGameFiles);
        _mmcRequiredFiles = MmcFilePanel();
        _mmcScreenshotFiles = MmcFilePanel();
        _mmcServerFiles = MmcFilePanel();
        _mmcOtherFiles = MmcFilePanel();
        foreach (UIElement control in new UIElement[] {
            CheckOptionsBasic,
            GlassDialogControls.Notice("必需的组件、补丁、运行库和当前整合包环境会完整保留。下方按类别选择玩家数据；文件清单用于核对，导出前还会核对实际内容。", DialogTone.Information),
            CheckOptionsMod, MmcFileScroll(_mmcRequiredFiles), CheckOptionsResourcePacks, MmcFileScroll(PanOptionsResourcePacks),
            CheckOptionsSaves, MmcFileScroll(PanOptionsSaves), CheckOptionsScreenshots, MmcFileScroll(_mmcScreenshotFiles),
            CheckOptionsServers, MmcFileScroll(_mmcServerFiles), _mmcOtherGameFiles, MmcFileScroll(_mmcOtherFiles),
            GlassDialogControls.Notice("存档、截图和服务器默认不包含。以上选择作用于整个类别；发现未识别文件时导出会停止，请核对后明确允许携带。启动器账号文件、日志、缓存及本机 Java 文件不会打包。", DialogTone.Warning)
        }) {
            if (control is FrameworkElement option) {
                option.Margin = new Thickness();
                if (option is CheckBox check) {
                    check.MinHeight = 36;
                    check.FontSize = 14;
                }
            }
            PanOptions.Children.Add(control);
        }
        Unloaded += (_, _) => { Interlocked.Increment(ref _mmcExportRevision); _mmcFileListCancellation?.Cancel(); };
        RefreshMmcExport();
    }

    private static StackPanel MmcFilePanel() => new() { Margin = new Thickness(30, 0, 0, 8) };
    private static Grid MmcFileScroll(Panel panel)
    {
        var scroll = GlassDialogControls.Scroll();
        var inset = panel.Margin;
        panel.Margin = new Thickness(Math.Min(inset.Left, 24), inset.Top, inset.Right, inset.Bottom);
        scroll.Content = panel;
        scroll.MaxHeight = 150;
        scroll.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { MinHeight = 36, Children = { scroll } };
        GlassPageLayout.PreserveRowSize(row);
        return row;
    }
    private static void ConfigureMmcCheck(CheckBox check, string text, bool selected, bool fixedChoice = false) {
        check.Visibility = Visibility.Visible;
        check.Content = text;
        check.IsChecked = selected;
        check.IsEnabled = !fixedChoice;
        check.Margin = new Thickness(0);
    }

    private void BlockMmcExport(string message) {
        GlassDialogControls.UpdateNotice(HintJava, message, DialogTone.Error);
        HintJava.Visibility = Visibility.Visible;
        PanOptions.IsEnabled = false;
        PanMmcPortability.IsEnabled = false;
        BtnExport.IsEnabled = false;
    }

    private bool VerifyMmcExportOwner(bool updateBaseline, out InstanceSidebarState state) {
        state = null;
        var active = LauncherInstanceContext.Instance;
        if (ExportOwner is null || !ReferenceEquals(active, ExportOwner) || !PathUtils.IsSame(active.PathVersion, _exportOwnerRoot)) {
            BlockMmcExport("当前页面对应的实例已切换，请重新进入对应实例的导出页。");
            return false;
        }
        state = LauncherInstanceContext.ObserveInstanceContext(ExportOwner);
        if (_mmcExportInstanceId == Guid.Empty || state.InstanceId != _mmcExportInstanceId ||
            ExportOwner.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed ||
            ExportOwner.Config.ContextStamp.InstanceId != _mmcExportInstanceId) {
            BlockMmcExport("实例身份已变化或尚未确认，请重新选择实例。刷新不会接管此目录中的另一个实例。");
            return false;
        }
        if (!state.IsConsistent || state.Inspection?.Paths is null || state.Inspection.Definition is null || string.IsNullOrEmpty(state.Inspection.PrimarySha256)) {
            BlockMmcExport(state.IsPendingTransaction ? "实例有未完成的事务，恢复完成前暂不能导出。" :
                "MMC 管理信息不可用或需要修复，当前导出已停用。请先检查实例状态。");
            return false;
        }
        if (!updateBaseline && (state.Generation != _mmcExportGeneration || !string.Equals(state.Inspection.PrimarySha256, _mmcExportPrimarySha256, StringComparison.OrdinalIgnoreCase))) {
            BlockMmcExport("实例配置已发生变化，请刷新并重新核对导出内容。");
            return false;
        }
        if (updateBaseline) {
            _mmcExportGeneration = state.Generation;
            _mmcExportPrimarySha256 = state.Inspection.PrimarySha256;
        }
        return true;
    }

    private void RefreshMmcExport() {
        int revision = Interlocked.Increment(ref _mmcExportRevision);
        _mmcFileListCancellation?.Cancel();
        _mmcFileListCancellation?.Dispose();
        _mmcFileListCancellation = new CancellationTokenSource();
        if (!VerifyMmcExportOwner(updateBaseline: true, out var state)) return;
        _nameHint.Text = ExportOwner.Name;
        GlassDialogControls.UpdateNotice(HintJava, "以 MMC / Prism 格式导出当前环境。现有同名 ZIP 不会被覆盖；文件名版本只用于 ZIP 名称。", DialogTone.Information);
        HintJava.Visibility = Visibility.Visible;
        PanOptions.IsEnabled = true;
        PanMmcPortability.IsEnabled = true;
        BtnExport.IsEnabled = true;
        var summary = InstalledComponentSummary.Describe(ExportOwner.Version, state.Inspection.Manifest?.Components);
        CheckOptionsBasic.Content = "组件、补丁与运行库" + (summary.Count == 0 ? "" : "   " + string.Join(" · ", summary.Select(item => item.Name + " " + item.Version)));
        foreach (var panel in MmcFilePanels()) SetMmcFileLines(panel, ["正在读取文件清单……"]);
        MmcInventoryTask = LoadMmcFileListAsync(state, revision, _mmcFileListCancellation.Token);
    }

    private IEnumerable<Panel> MmcFilePanels() => [_mmcRequiredFiles, PanOptionsResourcePacks, PanOptionsSaves, _mmcScreenshotFiles, _mmcServerFiles, _mmcOtherFiles];

    private async Task LoadMmcFileListAsync(InstanceSidebarState state, int revision, CancellationToken token) {
        try {
            var listing = await Task.Run(() => ReadMmcFileList(state, token), token).ConfigureAwait(false);
            ModBase.RunInUi(() => {
                if (revision != _mmcExportRevision || token.IsCancellationRequested || !VerifyMmcExportOwner(updateBaseline: false, out _)) return;
                var panels = MmcFilePanels().ToArray();
                for (int index = 0; index < panels.Length; index++) SetMmcFileLines(panels[index], listing[index]);
            });
        } catch (OperationCanceledException) { }
        catch (Exception error) {
            ModBase.RunInUi(() => {
                if (revision != _mmcExportRevision || token.IsCancellationRequested || !VerifyMmcExportOwner(updateBaseline: false, out _)) return;
                BlockMmcExport("无法读取导出文件清单：" + error.Message + "。请检查文件后刷新。");
            });
        }
    }

    private static List<string>[] ReadMmcFileList(InstanceSidebarState state, CancellationToken token) {
        var paths = state.Inspection.Paths;
        var definition = state.Inspection.Definition;
        var saved = MmcSavedSourceStore.LoadAsync(paths, definition, token).GetAwaiter().GetResult();
        string gameLeaf = definition.Layout.GameRootLeaf;
        var expected = saved.Baseline.Baseline.Files.Where(file => file.TargetRelativePath is not null).Select(file => file.TargetRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = Enumerable.Range(0, 6).Select(_ => new List<string>()).ToArray();
        var counts = new int[6];
        int visited = 0;
        long pathCharacters = 0;
        var transient = new HashSet<string>(["PCL", "logs", "crash-reports", "cache", ".cache", "natives", "bin", "java", "runtime", ".jre", ".java", "skins"], StringComparer.OrdinalIgnoreCase);
        var accounts = new HashSet<string>(["accounts.json", "accounts.cfg", "launcher_accounts.json", "launcher_profiles.json", "multimc.cfg", "prismlauncher.cfg", "usercache.json", "usernamecache.json", "launcher_accounts_microsoft_store.json"], StringComparer.OrdinalIgnoreCase);
        var standard = new HashSet<string>(["mods", "coremods", "config", "scripts", "resources", "libraries", "defaultconfigs", "kubejs", "patchouli_books"], StringComparer.OrdinalIgnoreCase);
        Walk(Path.Combine(paths.InstanceRoot, gameLeaf), gameLeaf, true, 0);
        Walk(Path.Combine(paths.InstanceRoot, "libraries"), "libraries", false, 0);
        for (int index = 0; index < lines.Length; index++) {
            if (counts[index] == 0) lines[index].Add("未发现此类文件");
            else if (counts[index] > lines[index].Count) lines[index].Add($"……共 {counts[index]} 个文件（显示前 {lines[index].Count} 个，导出前会核对完整内容）");
        }
        return lines;

        void Walk(string path, string relative, bool game, int depth) {
            token.ThrowIfCancellationRequested();
            if (depth > 128) throw new IOException("文件目录层级过深");
            pathCharacters = checked(pathCharacters + relative.Length);
            if (++visited > 200000 || relative.Length > 32767 || pathCharacters > 32 * 1024 * 1024)
                throw new IOException("文件清单数量或路径总长超过可安全显示的范围");
            var parts = relative.Split('/');
            string leaf = parts[^1];
            if (accounts.Contains(leaf) || accounts.Any(name => leaf.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))) return;
            if (game && parts.Length > 1 && transient.Contains(parts[1])) return;
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("清单中存在链接目录或文件，请先检查：" + relative);
            if ((attributes & FileAttributes.Directory) != 0) {
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    Walk(child, relative + "/" + Path.GetFileName(child), game, depth + 1);
                return;
            }
            int group = 0;
            if (game && parts.Length > 1) {
                string top = parts[1];
                if (new[] { "saves", "journeymap", "XaeroWaypoints", "XaeroWorldMap" }.Contains(top, StringComparer.OrdinalIgnoreCase)) group = 2;
                else if (top.Equals("screenshots", StringComparison.OrdinalIgnoreCase)) group = 3;
                else if (new[] { "servers.dat", "servers.dat_old", "realms_persistence.json" }.Contains(leaf, StringComparer.OrdinalIgnoreCase)) group = 4;
                else if (new[] { "resourcepacks", "texturepacks", "shaderpacks" }.Contains(top, StringComparer.OrdinalIgnoreCase)) group = 1;
                else if (!standard.Contains(top) && !expected.Contains(relative) && !(parts.Length == 2 && (leaf.StartsWith("options", StringComparison.OrdinalIgnoreCase) || leaf is "eula.txt" or "pack.png"))) group = 5;
            }
            counts[group]++;
            if (lines[group].Count < 100) lines[group].Add(relative);
        }
    }

    private static void SetMmcFileLines(Panel panel, IEnumerable<string> lines) {
        panel.Children.Clear();
        foreach (string line in lines) panel.Children.Add(new TextBlock {
            Text = line, FontSize = 13, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, Margin = new Thickness(0, 1, 0, 1)
        });
    }

    private MmcInstanceExportOptions ReadMmcExportOptions() {
        int? minimum = null, maximum = null;
        if (CheckMmcFixedMemory.IsChecked == true) {
            if (!int.TryParse(TextMmcMinimumMemory.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int min) ||
                !int.TryParse(TextMmcMaximumMemory.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int max) || min < 0 || max < 16 || max > 1048576 || min > max)
                throw new IOException("请填写有效的内存范围：最大值至少 16 MiB，最小值不能大于最大值。");
            minimum = min; maximum = max;
        }
        return new(CheckOptionsSaves.IsChecked == true, CheckOptionsScreenshots.IsChecked == true, CheckOptionsServers.IsChecked == true) {
            IncludeUnrecognizedGameFiles = _mmcOtherGameFiles.IsChecked == true,
            InactiveGameTree = CheckMmcExcludeInactive.IsChecked == true ? MmcExportInactiveTree.Exclude : MmcExportInactiveTree.RejectNonempty,
            DiscardMachineJavaSelection = CheckMmcPortableJava.IsChecked == true,
            PortableMinimumMemoryMiB = minimum, PortableMaximumMemoryMiB = maximum
        };
    }

    private void StartMmcExport() {
        try {
            if (!VerifyMmcExportOwner(updateBaseline: false, out var state)) return;
            ExportOwner.Config.AssertAttached();
            var options = ReadMmcExportOptions();
            string name = string.IsNullOrWhiteSpace(TextExportName.Text) ? ExportOwner.Name : TextExportName.Text.Trim();
            string version = TextExportVersion.Text.Trim();
            if (name.Any(char.IsControl) || version.Any(char.IsControl) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("名称或文件名版本包含无效字符。");
            string outputName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + (version.Length == 0 ? "" : "-" + version);
            string output = MmcExportUi.ChooseOutput(outputName);
            if (string.IsNullOrWhiteSpace(output)) return;
            // A native file dialog may pump navigation or allow another operation to replace
            // the instance. Recheck the same immutable owner before starting the real worker.
            if (!VerifyMmcExportOwner(updateBaseline: false, out state)) return;
            MmcExportUi.Start(new(state.Inspection.Paths.InstanceRoot, output, name, _mmcExportInstanceId,
                _mmcExportGeneration, _mmcExportPrimarySha256, options));
        } catch (Exception error) { MmcExportUi.Report(error, "无法开始导出 MMC 整合包"); }
    }
}
