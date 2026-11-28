// SPDX-License-Identifier: MPL-2.0
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MeloongCore;
using PCLCS;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Instance = PCL.ModMinecraft.McInstance;
using Page = PCL.FormMain.PageType;
using Route = PCL.FormMain.PageSubType;

namespace PCL;

internal sealed class GlassLaunchOptionsPage : GlassWorkspacePage
{
    private readonly FormMain _window;
    private readonly Instance? _instance;
    private readonly StackPanel _sections = new() { Margin = new Thickness(25, 16, 25, 20) };
    private readonly TrackDragSlider _memory = new() { Maximum = 49, SmallChange = 1, LargeChange = 5 };
    private readonly GlassMemoryMeter _memoryMeter = new();
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ComboBox? _instanceIsolation;
    private readonly string _prefix;
    private bool _reading;

    internal GlassLaunchOptionsPage(FormMain window, Instance? instance = null)
    {
        _window = window;
        _instance = instance;
        _prefix = instance is null ? "Launch" : "Version";
        var scroll = GlassDialogControls.Scroll();
        scroll.Content = _sections;
        Child = scroll;
        AttachScroll(scroll);

        if (_instance is not null) {
            var hint = new GlassHint("这些设置只对该游戏版本生效，不影响其他版本。", GlassHint.Tone.Information, "HintIndieSetup") {
                Margin = new Thickness(0, 0, 0, 15) };
            _sections.Children.Add(hint);
            var personal = GlassSettingRows.Group(_sections, "个人游戏设置");
            var sync = Choice("VersionPersonalOptionsSync", "跟随全局设置", "不同步，保留此版本的个人设置");
            AssignValues(sync, "0", "1");
            GlassSettingRows.Row(personal, "个人设置同步", "关闭后，此版本保留自己的按键、鼠标、视野和字幕等设置。", sync);
        }
        var arguments = GlassSettingRows.Group(_sections, "启动选项");
        if (_instance is null) BuildGlobalArguments(arguments);
        else {
            _instanceIsolation = Choice(null, "开启", "关闭");
            _instanceIsolation.SelectionChanged += InstanceIsolationChanged;
            GlassSettingRows.Row(arguments, "版本隔离", "开启后，此版本使用独立的存档、Mod 和配置文件夹。", _instanceIsolation);
            AddWindowTextFields(arguments);
        }

        // Memory: the allocation mode as a segmented choice, the custom amount with its value, then the usage meter.
        var memory = GlassSettingRows.Group(_sections, "内存分配");
        var memoryModes = new List<RadioButton>();
        var modeBar = new StackPanel { Orientation = Orientation.Horizontal };
        string ramGroup = "LaunchMemory_" + Guid.NewGuid();
        void AddMode(string value, string label) {
            var mode = Option(_prefix + "RamType", value, label, ramGroup);
            mode.Style = Theme.Style("GlassPresentationRadioButton");
            mode.Margin = new Thickness(4, 0, 0, 0);
            modeBar.Children.Add(mode);
            memoryModes.Add(mode);
        }
        if (_instance is not null) AddMode("2", "跟随全局");
        AddMode("0", "自动");
        AddMode("1", "自定义");
        GlassSettingRows.Row(memory, "分配方式", "自动配置会根据系统剩余内存和游戏需要分配。", modeBar);
        _memory.Style = Theme.Style("GlassPresentationSlider");
        _memory.VerticalAlignment = VerticalAlignment.Center;
        SettingService.SetKey(_memory, _prefix + "RamCustom");
        AutomationProperties.SetName(_memory, "游戏分配内存");
        var amount = Theme.Text("");
        amount.FontWeight = FontWeights.SemiBold;
        amount.MinWidth = 64;
        amount.TextAlignment = TextAlignment.Right;
        amount.VerticalAlignment = VerticalAlignment.Center;
        var customSlider = new DockPanel { Width = 300, LastChildFill = true };
        DockPanel.SetDock(amount, Dock.Right);
        customSlider.Children.Add(amount);
        customSlider.Children.Add(_memory);
        GlassSettingRows.Row(memory, "自定义内存", "仅在分配方式为“自定义”时生效。", customSlider);
        var ramHint = new GlassPageResources.SliderDragHint(_memory);
        void RefreshAmount() {
            string text = $"{ManualMemory((int)_memory.Value):0.##} GB";
            ramHint.SetText(text);
            amount.Text = text;
        }
        RefreshAmount();
        _memory.ValueChanged += (_, _) => RefreshAmount();
        if (_instance is null) {
            var optimize = GlassSettingRows.Switch("LaunchArgumentRam");
            optimize.Checked += (_, _) => {
                if (_reading) return;
                string permission = WindowsUtils.HasAdminRole() ? "" : "\r\n启动游戏时会申请管理员权限。";
                if (ModMain.MyMsgBox("内存优化会延长启动时间，建议仅在内存不足时开启。" + permission,
                    "内存优化", "启用", "取消") != 1) optimize.IsChecked = false;
            };
            GlassSettingRows.Row(memory, "启动前优化内存", "启动前整理系统内存，会延长启动时间，建议仅在内存不足时开启。", optimize);
        } else GlassSettingRows.Row(memory, "启动前优化内存", "整理系统内存后再启动游戏。", Choice("VersionRamOptimize", "跟随全局设置", "开启", "关闭"));
        var usage = GlassSettingRows.Row(memory, "内存使用", null, null);
        _memoryMeter.Margin = new Thickness();
        usage.AddBelow(_memoryMeter);

        // The global offline-skin card is retired: offline skins are managed with the account.
        if (_instance is not null) {
            var server = GlassSettingRows.Group(_sections, "服务器");
            var address = TextField("VersionServerEnter");
            address.TextChanged += (_, _) => {
                string normalized = address.Text.Replace('：', ':').Replace('。', '.');
                if (normalized == address.Text) return;
                int caret = address.CaretIndex;
                address.Text = normalized;
                address.CaretIndex = Math.Min(caret, normalized.Length);
            };
            GlassSettingRows.Row(server, "自动进入服务器", "启动后直接连接这个服务器地址，留空则不进入。", address);
        }
        BuildAdvanced();

        var switchPage = GlassPageLayout.FrostedAction(_instance is null ? "版本独立设置" : "全局设置", SwitchGlyph);
        switchPage.HorizontalAlignment = HorizontalAlignment.Center;
        switchPage.Margin = new Thickness(0, 4, 0, 6);
        switchPage.Click += (_, _) => {
            if (_instance is not null) _window.PageChange(Page.Setup, Route.SetupLaunch);
            else if (ModMinecraft.McInstanceSelected is { } selected) {
                LauncherInstanceContext.Instance = selected.Load();
                _window.PageChange(Page.InstanceSetup, Route.InstanceSetup);
            }
        };
        _sections.Children.Add(switchPage);

        SettingService.BindNativeControls(this, () => !_reading && (_instance is null || ReferenceEquals(_instance, LauncherInstanceContext.Instance)));
        foreach (RadioButton mode in memoryModes) mode.Checked += (_, _) => RefreshMemory();
        _memory.ValueChanged += (_, _) => RefreshMemory();
        _memoryTimer.Tick += (_, _) => RefreshMemory();
        Loaded += (_, _) => {
            switchPage.Visibility = _instance is not null || ModMinecraft.McInstanceSelected is not null ? Visibility.Visible : Visibility.Collapsed;
            Reload();
            scroll.ScrollToTop();
            _memoryTimer.Start();
        };
        Unloaded += (_, _) => _memoryTimer.Stop();
    }

    internal void Reload()
    {
        _reading = true;
        try {
            if (_instanceIsolation is { } isolation && _instance is not null) {
                _ = _instance.PathIndie;
                var permission = MmcInstanceCapabilities.LegacyOperation(_instance.PathVersion, "切换版本隔离");
                isolation.IsEnabled = permission.Allowed;
                isolation.ToolTip = permission.RefusalReason;
                ToolTipService.SetShowOnDisabled(isolation, !permission.Allowed);
                isolation.SelectedIndex = Settings.Get<bool>("VersionArgumentIndieV2", Instance: _instance) ? 0 : 1;
                if (Settings.Get<int>("VersionAdvanceAssets", Instance: _instance) == 2) {
                    Settings.ResetInstanceUserPreference("VersionAdvanceAssets", Instance: _instance);
                    Settings.SetInstanceUserPreference("VersionAdvanceAssetsV2", true, Instance: _instance);
                }
            }
            SettingService.RefreshSettings(this);
        }
        catch (Exception error) { Logger.Error(error, "重载启动设置时出错", LogBehavior.Toast); }
        finally { _reading = false; }
        RefreshMemory();
    }

    internal void Reset()
    {
        try {
            if (_instance is not null) {
                if (!ReferenceEquals(_instance, LauncherInstanceContext.Instance)) return;
                var permission = MmcInstanceCapabilities.LegacyOperation(_instance.PathVersion, "初始化启动设置");
                if (!permission.Allowed) throw new InvalidOperationException(permission.RefusalReason);
                Settings.ResetInstanceUserPreference("VersionArgumentIndieV2", Instance: _instance);
                Settings.ResetInstanceUserPreference("VersionAdvanceAssets", Instance: _instance);
            }
            SettingService.ResetSettings(this);
            ModMain.Hint("已初始化启动设置！", ModMain.HintType.Green);
        }
        catch (Exception error) { Logger.Error(error, "初始化启动设置失败", LogBehavior.Alert); }
        Reload();
    }

    private void AddWindowTextFields(Panel arguments)
    {
        var title = TextField(_prefix + "ArgumentTitle", 100);
        GlassSettingRows.Row(arguments, "游戏窗口标题", "留空则不修改。可以使用替换标记，详见“更多 → 帮助”。",
            GlassDialogControls.WithPlaceholder(title, _instance is null ? "默认" : "跟随全局设置"));
        var information = TextField(_prefix + "ArgumentInfo");
        information.ValidateRules.Add(new ValidateExcept(new[] { '"', '“', '”' }));
        GlassSettingRows.Row(arguments, "自定义信息", "显示在游戏主界面左下角和 F3 界面。Minecraft 26.1 起不再支持。",
            _instance is null ? information : GlassDialogControls.WithPlaceholder(information, "跟随全局设置"));
    }

    private void BuildGlobalArguments(Panel panel)
    {
        var isolation = Choice("LaunchArgumentIndieV2", "关闭", "隔离可安装 Mod 的版本", "隔离非正式版",
            "隔离可安装 Mod 的版本与非正式版", "隔离所有版本");
        isolation.SelectionChanged += (_, _) => {
            if (!_reading && IsLoaded)
                ModMain.MyMsgBox("此设置应用于之后新安装的版本。已有版本可在版本独立设置里调整隔离方式。");
        };
        GlassSettingRows.Row(panel, "默认版本隔离", "新安装的版本据此决定存档、Mod 等是否与其他版本分开存放。", isolation);
        AddWindowTextFields(panel);
        var visibility = Choice("LaunchArgumentVisible", "游戏启动后立即关闭", "游戏启动后隐藏，游戏退出后自动关闭",
            "游戏启动后隐藏，游戏退出后重新打开", "游戏启动后最小化", "游戏启动后仍保持不变");
        AssignValues(visibility, "0", "2", "3", "4", "5");
        visibility.SelectionChanged += (_, args) => {
            if (_reading || visibility.SelectedIndex != 0 || args.RemovedItems.Count == 0) return;
            if (ModMain.MyMsgBox("游戏启动后立即关闭启动器会关闭崩溃检测和更改游戏窗口标题等功能。",
                "启动器可见性", "继续", "取消") != 1) visibility.SelectedItem = args.RemovedItems[0];
        };
        GlassSettingRows.Row(panel, "启动器可见性", "游戏运行期间启动器窗口如何处理。", visibility);
        GlassSettingRows.Row(panel, "进程优先级", "游戏进程占用处理器的优先程度。",
            Choice("LaunchArgumentPriority", "高（优先保证游戏运行）", "中（平衡）", "低（优先保证其他程序运行）"));
        var size = Choice("LaunchArgumentWindowType", "全屏", "默认", "与启动器尺寸一致", "自定义尺寸", "最大化");
        var width = TextField("LaunchArgumentWindowWidth", 5);
        var height = TextField("LaunchArgumentWindowHeight", 5);
        width.Width = height.Width = 70;
        width.ValidateRules.Add(new ValidateInteger { Min = 1, Max = 99999 });
        height.ValidateRules.Add(new ValidateInteger { Min = 1, Max = 99999 });
        var times = Theme.Text("×");
        times.Margin = new Thickness(8, 0, 8, 0);
        times.VerticalAlignment = VerticalAlignment.Center;
        var dimensions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { width, times, height } };
        void UpdateSize() => dimensions.Visibility = size.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        size.SelectionChanged += (_, _) => UpdateSize();
        UpdateSize();
        // Custom width and height open under the row only when "自定义尺寸" is chosen.
        var window = GlassSettingRows.Row(panel, "窗口大小", "游戏窗口打开时的尺寸。", size);
        window.AddBelow(dimensions);
        void SyncBelow() => window.Below.Visibility = dimensions.Visibility;
        size.SelectionChanged += (_, _) => SyncBelow();
        SyncBelow();
    }

    private void InstanceIsolationChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_reading || _instance is null || !ReferenceEquals(_instance, LauncherInstanceContext.Instance) ||
            _instanceIsolation is not { SelectedIndex: >= 0 } combo || args.RemovedItems.Count == 0) return;
        var permission = MmcInstanceCapabilities.LegacyOperation(_instance.PathVersion, "切换版本隔离");
        if (!permission.Allowed || ModMain.MyMsgBox(
            "调整版本隔离后，存档和 Mod 可能需要手动迁移。切回原设置可以回到原来的游戏文件夹。",
            "调整版本隔离", "继续", "取消", IsWarn: true) != 1) {
            Reload();
            return;
        }
        Settings.SetInstanceUserPreference("VersionArgumentIndieV2", combo.SelectedIndex == 0, Instance: _instance);
        RefreshMemory();
    }

    private void BuildAdvanced()
    {
        var fields = GlassSettingRows.Expander(_sections, "高级选项", Theme.Style("GlassPresentationExpander"));
        var jvm = TextField(_prefix + "AdvanceJvm", 4000);
        jvm.AcceptsReturn = true;
        jvm.TextWrapping = TextWrapping.Wrap;
        jvm.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        jvm.MinHeight = 92;
        const string jvmNote = "追加到 Java 虚拟机的启动参数，支持版本 JSON 和启动器的替换标记。";
        if (_instance is null) {
            var reset = GlassDialogControls.Button("恢复默认");
            reset.Click += (_, _) => { Settings.Reset("LaunchAdvanceJvm"); Reload(); };
            GlassSettingRows.Row(fields, "Java 虚拟机参数", jvmNote, reset).AddBelow(jvm);
        } else GlassSettingRows.Row(fields, "Java 虚拟机参数", jvmNote, GlassDialogControls.WithPlaceholder(jvm, "跟随全局设置"), wide: true);
        GlassSettingRows.Row(fields, "游戏参数", "追加到 Minecraft 的启动参数。", TextField(_prefix + "AdvanceGame"), wide: true);
        var run = TextField(_prefix + "AdvanceRun");
        var wait = Check(_prefix + "AdvanceRunWait", "等待命令执行完成后再继续启动");
        wait.Margin = new Thickness(0, 10, 0, 0);
        void UpdateWait() => wait.Visibility = run.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        run.TextChanged += (_, _) => UpdateWait();
        UpdateWait();
        var command = GlassSettingRows.Row(fields, "启动前执行命令", "在 .minecraft 文件夹中执行，路径可以使用替换标记。", null);
        command.AddBelow(run);
        command.AddBelow(wait);
        string[] garbageCollectors = ["尽量使用 ZGC", "尽量使用分代 ZGC", "标准 G1GC", "调优 G1GC", "不指定（可自定义）"];
        if (_instance is not null) garbageCollectors = ["跟随全局设置", .. garbageCollectors];
        var gc = Choice(_prefix + "AdvanceGC", garbageCollectors);
        AssignValues(gc, _instance is null ? ["0", "1", "2", "4", "3"] : ["0", "1", "2", "3", "5", "4"]);
        GlassSettingRows.Row(fields, "内存管理", "Java 使用的垃圾回收器。", gc);
        if (_instance is not null) {
            GlassSettingRows.Row(fields, "禁止更新 Mod", "检查更新时跳过此版本的 Mod。", GlassSettingRows.Switch("VersionAdvanceDisableModUpdate"));
            GlassSettingRows.Row(fields, "关闭文件校验", "启动时不再校验游戏资源文件，可能导致缺失文件不被发现。", GlassSettingRows.Switch("VersionAdvanceAssetsV2"));
        }
        GlassSettingRows.Row(fields, "禁用 Java Launch Wrapper", "遇到启动兼容性问题时再开启。", GlassSettingRows.Switch(_prefix + "AdvanceDisableJLW"));
        GlassSettingRows.Row(fields, "禁用 LWJGL Unsafe Agent", "遇到启动兼容性问题时再开启。", GlassSettingRows.Switch(_prefix + "AdvanceDisableLUA"));
        if (_instance is null) {
            GlassSettingRows.Row(fields, "使用高性能显卡", "让游戏优先使用独立显卡。", GlassSettingRows.Switch("LaunchAdvanceGraphicCard"));
            GlassSettingRows.Row(fields, "正版登录时验证 SSL 证书", "网络环境存在证书问题时可以关闭。", GlassSettingRows.Switch("ToolDownloadCert"));
        }
    }

    private void RefreshMemory()
    {
        if (_reading || !IsLoaded || (_instance is not null && !ReferenceEquals(_instance, LauncherInstanceContext.Instance))) return;
        _memory.IsEnabled = Settings.Get<int>(_prefix + "RamType", Instance: _instance) == 1;
        var system = My.MyWpfExtension.Computer.Info;
        const double units = 1024d * 1024 * 1024;
        double available = system.AvailablePhysicalMemory / units;
        double total = system.TotalPhysicalMemory / units;
        double game = _instance is null ? LauncherMemory.ForGlobal(ModMinecraft.McInstanceSelected, false) : LauncherMemory.ForInstance(_instance);
        double used = Math.Max(0, total - available);
        _memoryMeter.Show(used, game, total);
    }

    private static double ManualMemory(int value) => value switch {
        <= 12 => value * .1 + .3,
        <= 25 => (value - 12) * .5 + 1.5,
        <= 33 => value - 25 + 8,
        _ => (value - 33) * 2 + 16
    };

    private const string SwitchGlyph = "M4,11 H17.2 L12.6,6.4 L14,5 L21,12 L14,19 L12.6,17.6 L17.2,13 H4 Z";

    private ComboBox Choice(string? key, params string[] labels)
    {
        var box = new ComboBox { Style = Theme.Style("GlassPresentationComboBox") };
        if (key is not null) SettingService.SetKey(box, key);
        foreach (string label in labels) box.Items.Add(new ComboBoxItem { Content = label });
        return box;
    }

    private static void AssignValues(ComboBox box, params string[] values)
    {
        for (int index = 0; index < values.Length; index++) SettingService.SetValue((ComboBoxItem)box.Items[index], values[index]);
    }

    private GlassValidatedTextBox TextField(string key, int limit = 0)
    {
        var field = new GlassValidatedTextBox { Style = Theme.Style("GlassPresentationTextBox"), MaxLength = limit };
        SettingService.SetKey(field, key);
        return field;
    }

    private CheckBox Check(string key, string label)
    {
        var check = GlassDialogControls.Check(label, false);
        check.FontSize = 14;
        check.Margin = new Thickness(0, 4, 0, 7);
        SettingService.SetKey(check, key);
        return check;
    }

    private RadioButton Option(string key, string value, string label, string group)
    {
        var choice = new RadioButton { Content = Theme.Text(label), GroupName = group, FontSize = 14, Style = Theme.Style("GlassPresentationRadioBox"),
            Margin = new Thickness(0, 4, 16, 4) };
        AutomationProperties.SetName(choice, label);
        SettingService.SetKey(choice, key);
        SettingService.SetValue(choice, value);
        return choice;
    }
}
