using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
// The existing component editor itself is the page body. Only the actual backend
// difference/commit confirmation is modal; the editable component choice stays here.
public sealed class PageInstanceMmcComponents : GlassWorkspacePage, IRefreshable {
    private readonly ModMinecraft.McInstance _owner;
    private readonly string _root;
    private readonly Guid _instanceId;
    private readonly Border _status;
    private readonly Border _editorHost;
    private readonly Button _preview;
    private readonly Grid _footer = new() { Margin = new Thickness(24, 0, 24, 0) };
    private long _generation;
    private string _primarySha256 = "";
    private int _revision;
    private CancellationTokenSource _loadCancellation;
    private MmcComponentEditorModel _model;
    private MmcComponentEditorView _editor;
    private ModLoader.LoaderCombo<string> _operation;
    internal Task ModelTask { get; private set; } = Task.CompletedTask;

    public PageInstanceMmcComponents() : this(LauncherInstanceContext.Instance) { }
    internal PageInstanceMmcComponents(ModMinecraft.McInstance owner, Guid expectedInstanceId = default) {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _root = owner.PathVersion;
        var attached = owner.Config.ContextStamp;
        _instanceId = expectedInstanceId != Guid.Empty ? expectedInstanceId :
            attached.Kind == MmcConfigurationContextKind.Managed ? attached.InstanceId : LauncherInstanceContext.ObserveInstanceContext(owner).InstanceId;
        NameScope.SetNameScope(this, new NameScope());
        var all = new Grid { Name = "PanAllBack" };
        RegisterName(all.Name, all);
        var body = new Grid { Margin = new Thickness(24) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _status = GlassDialogControls.Notice("", DialogTone.Information);
        _status.Margin = new Thickness(0, 0, 0, 12);
        _status.Name = "HintStatus";
        _editorHost = new Border { Name = "PanEditor" };
        Grid.SetRow(_editorHost, 1);
        body.Children.Add(_status); body.Children.Add(_editorHost); all.Children.Add(body);
        _preview = GlassPageResources.Pill("预览组件修改", primary: true, readableWhenDisabled: true);
        _preview.Name = "BtnPreviewChanges";
        _preview.IsEnabled = false;
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.VerticalAlignment = VerticalAlignment.Bottom;
        _preview.Margin = new Thickness(0, 0, 0, 12);
        _preview.Click += (_, _) => StartChange();
        _footer.Children.Add(_preview);
        all.Children.Add(_footer); Child = all;
        RegisterName(_status.Name, _status); RegisterName(_editorHost.Name, _editorHost); RegisterName(_preview.Name, _preview);
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { Interlocked.Increment(ref _revision); _loadCancellation?.Cancel(); _preview.IsEnabled = false; };
        Refresh();
    }

    internal bool IsManagedOwner(ModMinecraft.McInstance owner, Guid instanceId) => ReferenceEquals(owner, _owner) &&
        owner is not null && PathUtils.IsSame(owner.PathVersion, _root) && instanceId == _instanceId;

    private void Block(string message) {
        GlassDialogControls.UpdateNotice(_status, message, DialogTone.Error);
        _preview.IsEnabled = false;
        if (_editor is not null) _editor.IsEnabled = false;
    }

    private bool VerifyOwner(bool updateBaseline, out InstanceSidebarState state) {
        state = null;
        var active = LauncherInstanceContext.Instance;
        if (!ReferenceEquals(active, _owner) || active is null || !PathUtils.IsSame(active.PathVersion, _root)) {
            Block("当前组件页对应的实例已切换，请重新进入对应实例设置。"); return false;
        }
        state = LauncherInstanceContext.ObserveInstanceContext(_owner);
        if (_instanceId == Guid.Empty || state.InstanceId != _instanceId || _owner.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed ||
            _owner.Config.ContextStamp.InstanceId != _instanceId) {
            Block("实例身份已变化或尚未确认，请重新选择实例。刷新不会接管另一个实例。"); return false;
        }
        if (!state.IsConsistent || state.Inspection?.Paths is null || state.Inspection.Definition is null || string.IsNullOrEmpty(state.Inspection.PrimarySha256)) {
            Block(state.IsPendingTransaction ? "实例有未完成的事务，恢复完成前不能修改组件。" :
                "MMC 管理信息不可用或需要修复，当前组件页处于只读保护状态。"); return false;
        }
        if (!updateBaseline && (state.Generation != _generation || !string.Equals(state.Inspection.PrimarySha256, _primarySha256, StringComparison.OrdinalIgnoreCase))) {
            Block("实例的组件配置已变化，请刷新页面后重新选择本次修改。"); return false;
        }
        if (updateBaseline) { _generation = state.Generation; _primarySha256 = state.Inspection.PrimarySha256; }
        return true;
    }

    public void Refresh() {
        int revision = Interlocked.Increment(ref _revision);
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = new CancellationTokenSource();
        _preview.IsEnabled = false;
        if (_editor is not null) _editor.IsEnabled = false;
        if (!VerifyOwner(updateBaseline: true, out var state)) return;
        GlassDialogControls.UpdateNotice(_status, "正在读取当前组件、作者声明和个人覆盖……", DialogTone.Information);
        var selection = new MmcInstanceRepairSelection(state.Inspection.Paths.InstanceRoot, _instanceId, _generation, _primarySha256);
        ModelTask = LoadEditorAsync(selection, revision, _loadCancellation.Token);
    }

    private async Task LoadEditorAsync(MmcInstanceRepairSelection selection, int revision, CancellationToken token) {
        try {
            var model = await MmcComponentEditorUi.LoadModelAsync(_owner, selection, token).ConfigureAwait(false);
            ModBase.RunInUi(() => {
                if (revision != _revision || token.IsCancellationRequested || !VerifyOwner(updateBaseline: false, out _)) return;
                var editor = new MmcComponentEditorView(model, null, instancePage: true);
                editor.FitToPage(_status, _footer);
                editor.ConfirmationChanged += (_, _) => {
                    if (ReferenceEquals(_editor, editor) && revision == _revision && _operation is null && VerifyOwner(updateBaseline: false, out _))
                        _preview.IsEnabled = editor.IsEnabled && editor.CanConfirm;
                };
                if (_editor is not null) { UnregisterName("MmcComponentEditor"); UnregisterName("PanBack"); }
                _model = model; _editor = editor;
                RegisterName("MmcComponentEditor", editor); RegisterName("PanBack", editor.PageScroller); _editorHost.Child = editor;
                GlassDialogControls.UpdateNotice(_status, "在下方选择已有组件并编辑本次修改，再预览实际差异。Java 统一使用“实例设置 → Java”中的选择。", DialogTone.Information);
                _preview.IsEnabled = editor.CanConfirm && _operation is null;
                AttachScroll(editor.PageScroller, fadeEdges: false);
            });
        } catch (OperationCanceledException) { }
        catch (Exception error) {
            ModBase.RunInUi(() => {
                if (revision != _revision || token.IsCancellationRequested || !VerifyOwner(updateBaseline: false, out _)) return;
                Block("无法读取 MMC 组件：" + error.Message + "。请检查实例后刷新。");
            });
        }
    }

    private void StartChange() {
        if (_editor is null || _model is null || !_editor.CanConfirm || _operation is not null) return;
        try {
            if (!VerifyOwner(updateBaseline: false, out _)) return;
            var choice = _editor.ReadChoice();
            int revision = _revision;
            _editor.IsEnabled = false; _preview.IsEnabled = false;
            var operation = MmcComponentEditorUi.StartWithChoice(_owner, _model, choice);
            _operation = operation;
            void Finished() {
                if (!ReferenceEquals(_operation, operation)) return;
                _operation = null;
                if (revision != _revision) return;
                if (operation.State == ModBase.LoadState.Finished) Refresh();
                else {
                    if (!VerifyOwner(updateBaseline: false, out _)) return;
                    _editor.IsEnabled = true; _preview.IsEnabled = _editor.CanConfirm;
                    GlassDialogControls.UpdateNotice(_status, "本次尚未提交，可在当前页面继续修改后重新预览。需要更换 Java 时请进入“实例设置 → Java”。",
                        operation.State == ModBase.LoadState.Failed ? DialogTone.Warning : DialogTone.Information);
                }
            }
            operation.OnStateChangedUi += (_, state, _) => {
                if (state is ModBase.LoadState.Finished or ModBase.LoadState.Canceled or ModBase.LoadState.Failed) Finished();
            };
            if (operation.State is ModBase.LoadState.Finished or ModBase.LoadState.Canceled or ModBase.LoadState.Failed) Finished();
        } catch (Exception error) {
            if (_editor is not null && VerifyOwner(updateBaseline: false, out _)) { _editor.IsEnabled = true; _preview.IsEnabled = _editor.CanConfirm; }
            MmcComponentEditorUi.Report(error, "无法开始预览 MMC 组件修改");
        }
    }
}
