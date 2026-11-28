// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MeloongCore;
using PCLCS;
using PCLLF.MMCC.Core;
using Instance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal sealed class GlassInstanceJavaPage : GlassJavaRuntimePage
{
    private static readonly (string Name, string Description)[] Modes = {
        ("自动选择", "根据游戏版本和组件要求选择兼容的 Java"),
        ("指定版本范围", "在版本区间内自动挑选，适合要求特定 Java 的 Mod"),
        ("实例文件夹中的 Java", "使用整合包自带、放在实例文件夹里的 Java"),
        ("指定 Java", "从本机已有的 Java 中手动选择一个")
    };
    private readonly Border[] _marks = new Border[Modes.Length];
    private readonly Button[] _modes = new Button[Modes.Length];
    private readonly TextBox _range;
    private readonly ComboBox _picker;
    private readonly Button _search;
    private readonly TextBlock _preview;
    private readonly Button _folder;
    private readonly StackPanel _rangeEditor = new(), _chooser = new();
    private readonly RedoableWorker<Java> _worker = LauncherInstanceContext.CurrentJavaWorker;
    private Instance? _instance;
    private MmcConfigurationContextStamp? _stamp;
    private Java[] _available = [];
    private int _mode;
    private bool _active, _connected, _syncing;
    private bool _rangeDirty;

    internal GlassInstanceJavaPage(Action manage)
    {
        var panel = new StackPanel();
        var tiles = new UniformGrid { Columns = 2 };
        for (int index = 0; index < Modes.Length; index++)
        {
            int mode = index;
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            var ring = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, "GlassPresentationAccent");
            ring.Child = dot;
            body.Children.Add(ring);
            var text = new StackPanel();
            var heading = Theme.Text(Modes[index].Name); heading.FontSize = 14; heading.FontWeight = FontWeights.SemiBold;
            TextBlock hint = Theme.Note(Modes[index].Description, 12); hint.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(heading); text.Children.Add(hint);
            Grid.SetColumn(text, 1); body.Children.Add(text);
            var button = Tile(body, Modes[index].Name, () => ChangeMode(mode));
            button.Margin = new Thickness(index % 2 == 0 ? 0 : 5, 0, index % 2 == 0 ? 5 : 0, 10);
            _marks[index] = ring;
            _modes[index] = button;
            tiles.Children.Add(button);
        }
        panel.Children.Add(tiles);
        _range = new TextBox { Style = Theme.Style("GlassPresentationTextBox") };
        AutomationProperties.SetName(_range, "Java 版本区间");
        _rangeEditor.Children.Add(Theme.Section("版本区间", "例如 [17, 22) 表示 Java 17 至 21。"));
        _rangeEditor.Children.Add(_range);
        panel.Children.Add(_rangeEditor);
        _range.TextChanged += (_, _) => { if (!_syncing && _active) _rangeDirty = true; };
        _range.LostKeyboardFocus += (_, _) => CommitRange();
        _range.PreviewKeyDown += (_, args) => { if (args.Key == Key.Enter) { CommitRange(); args.Handled = true; } };

        var editors = new Grid();
        editors.ColumnDefinitions.Add(new ColumnDefinition());
        editors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _picker = new ComboBox { Style = Theme.Style("GlassPresentationComboBox"), Margin = new Thickness(0, 0, 9, 0) };
        AutomationProperties.SetName(_picker, "指定 Java");
        _picker.SelectionChanged += (_, _) => SetForcedJava();
        editors.Children.Add(_picker);
        _search = Icon(ModBase.Logo.IconButtonRefresh, "搜索本机 Java", () => ModJava.ManuallySearchJava());
        Grid.SetColumn(_search, 1);
        editors.Children.Add(_search);
        _chooser.Children.Add(Theme.Section("使用的 Java", ""));
        _chooser.Children.Add(editors);
        var options = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        options.Children.Add(Command("导入已有 Java", ModJava.ManuallyImportJava));
        options.Children.Add(Command("打开所选 Java 文件夹", () =>
        {
            if (_picker.SelectedItem is Java java) ModBase.OpenExplorer(java.Folder);
        }));
        options.Children.Add(Command("从列表移除所选 Java", Forget));
        _chooser.Children.Add(options);
        panel.Children.Add(_chooser);
        var notice = GlassDialogControls.Notice("", DialogTone.Information);
        notice.Margin = new Thickness(0, 10, 0, 0);
        _preview = GlassDialogControls.NoticeText(notice);
        _preview.Visibility = Visibility.Collapsed;
        notice.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = _preview });
        panel.Children.Add(notice);
        _folder = Command("打开实例文件夹", () => { if (_instance is not null) PageInstanceOverall.OpenInstanceFolder(_instance); });
        panel.Children.Add(_folder);
        AddSection("这个实例使用哪个 Java", panel, "选择方式只影响当前实例；全局的启动优先级在“设置 → Java”中调整。");
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var managementLabel = Theme.Section("本机 Java", "查看已安装的 Java，导入或下载新的发行版。");
        managementLabel.Margin = new Thickness();
        managementLabel.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(managementLabel);
        var open = Command("管理本机 Java", manage);
        open.Margin = new Thickness();
        open.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(open, 1);
        actions.Children.Add(open);
        Sections.Children.Add(GlassPageResources.FormPlate(actions));
    }

    internal void Open(bool loadData)
    {
        _active = true;
        ApplyPalette();
        Scroll.ScrollToTop();
        if (!loadData) return;
        _instance = LauncherInstanceContext.Instance;
        _stamp = _instance?.Config.ContextStamp;
        if (!_connected)
        {
            _connected = true;
            _worker.Started += WorkerChanged;
            _worker.Stopped += WorkerChanged;
        }
        _rangeDirty = false;
        ReadSettings();
        BeginPreview();
    }

    internal void Leave()
    {
        CommitRange();
        _active = false;
        _instance = null;
        if (!_connected) return;
        _connected = false;
        _worker.Started -= WorkerChanged;
        _worker.Stopped -= WorkerChanged;
    }

    internal void Reload()
    {
        ReadSettings();
        BeginPreview();
    }

    internal void Refresh()
    {
        if (!_active || _instance is null) return;
        ApplyPalette();
        RefreshJavaList();
        UpdatePreview();
    }

    private void ReadSettings()
    {
        if (_instance is not { } instance) return;
        _mode = Math.Clamp(Settings.Get<int>("VersionArgumentJavaV2", instance), 0, 3);
        _syncing = true;
        try
        {
            if (!_rangeDirty) _range.Text = Settings.Get<string>("VersionArgumentJavaRange", instance);
            for (int index = 0; index < _modes.Length; index++)
            {
                var button = _modes[index];
                bool chosen = index == _mode;
                SetTileChosen(button, chosen);
                _marks[index].SetResourceReference(Border.BorderBrushProperty, chosen ? "GlassPresentationAccent" : "GlassPresentationEdge");
                _marks[index].Child.Visibility = chosen ? Visibility.Visible : Visibility.Hidden;
                AutomationProperties.SetItemStatus(button, index == _mode ? "已选中" : "");
            }
            _rangeEditor.Visibility = _mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            _chooser.Visibility = _mode == 3 ? Visibility.Visible : Visibility.Collapsed;
            RefreshJavaList(force: true);
        }
        finally { _syncing = false; }
    }

    private void RefreshJavaList(bool force = false)
    {
        if (_mode != 3 || _instance is not { } instance) return;
        bool busy = ModJava.JavaListRefreshWorker.Running;
        _picker.IsEnabled = _search.IsEnabled = !busy;
        var list = Configs.JavaList.Get()?.ToArray() ?? [];
        if (!force && list.SequenceEqual(_available, ReferenceEqualityComparer.Instance)) return;
        var selected = Configs.JavaForced.Get(instance.Config);
        bool suppress = _syncing;
        _syncing = true;
        try
        {
            _available = list;
            _picker.ItemsSource = list;
            _picker.SelectedItem = list.FirstOrDefault(entry => Equals(entry, selected));
        }
        finally { _syncing = suppress; }
    }

    private void ChangeMode(int mode)
    {
        if (!CanEdit() || _instance is not { } instance || _mode == mode) return;
        if (!CommitRange()) return;
        Settings.Set("VersionArgumentJavaV2", mode, instance);
        if (mode == 1 && !instance.UsesMmcBackend && !Settings.HasSaved("VersionArgumentJavaRange", instance))
        {
            var range = ModJava.GetJavaRequirement(instance).Range.ToString().Replace("-∞", "").Replace("+∞", "");
            Settings.Set("VersionArgumentJavaRange", range, instance);
        }
        _stamp = instance.Config.ContextStamp;
        ReadSettings();
        BeginPreview();
    }

    private bool CommitRange()
    {
        if (_syncing || !_rangeDirty || _mode != 1 || _instance is not { } instance) return true;
        string error = InstanceJavaRangeValidation.Validate(_range.Text, ModBase.vbLQ.ToString(), ModBase.vbRQ.ToString());
        if (!string.IsNullOrEmpty(error))
        {
            _preview.Text = "Java 版本范围错误：" + error;
            return false;
        }
        if (!CanEdit()) return false;
        Settings.Set("VersionArgumentJavaRange", _range.Text, instance);
        _stamp = instance.Config.ContextStamp;
        _rangeDirty = false;
        BeginPreview();
        return true;
    }

    private void SetForcedJava()
    {
        if (_syncing || _instance is not { } instance || _picker.SelectedItem is not Java java || !CanEdit()) return;
        var current = MmcInstanceInspector.Read(instance.PathVersion);
        if (_stamp is { Kind: MmcConfigurationContextKind.Managed } stamp
            ? !current.IsConsistent || current.Definition?.InstanceId != stamp.InstanceId
            : !current.Management.CanUseLegacyBackend)
        {
            ModMain.Hint("实例管理状态已变化，请重新打开实例设置后再选择 Java。", ModMain.HintType.Red);
            return;
        }
        MmcLaunchPreferences.SetForcedJava(instance, java);
        _stamp = instance.Config.ContextStamp;
        BeginPreview();
    }

    private void Forget()
    {
        if (!CanEdit() || _picker.SelectedItem is not Java java || PathUtils.IsParentOf($@"{Paths.AppData}.minecraft\runtime\", java.Folder)) return;
        ModJava.ManuallyRemoveJava(java);
        RefreshJavaList(force: true);
    }

    private bool CanEdit() => _active && _instance is { } instance && ReferenceEquals(instance, LauncherInstanceContext.Instance) &&
        instance.Config.ContextStamp == _stamp && !ModMinecraft.McInstanceLock.IsLocked(instance);

    private void WorkerChanged() => ModBase.RunInUi(UpdatePreview);

    private void BeginPreview()
    {
        if (!_active || _instance is not { } instance) return;
        if (_mode != 3 || instance.UsesMmcBackend) MmcJavaDiscovery.Start(instance, _worker);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (!_active || _instance is not { } instance || _rangeDirty) return;
        if (MmcJavaDiscovery.Preview(instance, _worker) is { } managed) _preview.Text = managed.Message;
        else if (_mode == 3) _preview.Text = _picker.SelectedItem is null ? "请选择要使用的 Java。" : "";
        else if (_worker.Running || !MmcJavaDiscovery.IsCurrentRequest(instance, _worker)) _preview.Text = "正在查找 Java……";
        else if (_worker.LastResult is { } selected) _preview.Text = $"将会使用：{selected}";
        else _preview.Text = _mode == 2 ? "在实例文件夹中未找到兼容的 Java。" : "未找到兼容的 Java。启动时将尝试获取兼容的运行时。";
        _preview.Visibility = _preview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _folder.Visibility = _mode == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
