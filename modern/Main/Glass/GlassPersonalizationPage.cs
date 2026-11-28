// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;

namespace PCL;

/// <summary>Edits launcher personalization settings without constructing the classic settings page.</summary>
internal sealed class GlassPersonalizationPage : ContentControl, IDisposable {
    private readonly GlassPageResources _resources;
    private readonly List<Action> _readers = new();
    private readonly List<IDisposable> _lifetimes = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly StackPanel _sections = new() { Margin = new Thickness(0, 10, 0, 10) };
    private bool _active, _syncing;
    internal ScrollViewer Scroll { get; }

    internal GlassPersonalizationPage(Action chooseWallpaper, Action clearWallpaper, Func<GlassWallpaperStatus> readWallpaper) {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        Scroll = new ScrollViewer { Content = _sections, Style = _resources.Style("GlassPresentationScrollViewer"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.SetIsEnabled(Scroll, true);
        Content = new ScrollEdgeFade(Scroll);

        var basic = GlassSettingRows.Group(_sections, "基础");
        GlassSettingRows.Row(basic, "外观主题", "启动器使用浅色或深色界面。", Choice("UiLauncherColorMode", (0, "浅色"), (1, "深色")));
        GlassSettingRows.Row(basic, "面板不透明度", "侧栏和卡片底色的不透明度，文字和图标保持清晰。",
            Valued(SliderEditor("UiPanelOpacity", 1000, value => $"{value / 10:0.#}%"), value => $"{value / 10:0.#}%"));

        var wallpaper = GlassSettingRows.Group(_sections, "背景图片");
        var wallpaperRow = new GlassWallpaperSettingRow(chooseWallpaper, clearWallpaper, readWallpaper) { Margin = new Thickness(0, 12, 0, 12) };
        wallpaper.Children.Add(wallpaperRow);
        var wallpaperOptions = new[] {
            GlassSettingRows.Row(wallpaper, "自适应方式", "背景图片如何填充窗口。", Choice("UiBackgroundSuit", (0, "自动"), (4, "平铺"), (1, "居中"),
                (3, "拉伸"), (5, "左上 (保持长宽比)"), (9, "左中 (保持长宽比)"), (7, "左下 (保持长宽比)"),
                (11, "中上 (保持长宽比)"), (2, "居中 (保持长宽比)"), (12, "中下 (保持长宽比)"),
                (6, "右上 (保持长宽比)"), (10, "右中 (保持长宽比)"), (8, "右下 (保持长宽比)"))),
            GlassSettingRows.Row(wallpaper, "背景图片不透明度", "数值越低，背景图片越淡。",
                Valued(SliderEditor("UiBackgroundOpacity", 1000, value => $"{value / 10:0.#}%"), value => $"{value / 10:0.#}%")),
            GlassSettingRows.Row(wallpaper, "背景模糊", "模糊背景图片，让前景内容更易阅读。",
                Valued(SliderEditor("UiBackgroundBlur", 40, value => $"{value * 2.5:0.#}%"), value => $"{value * 2.5:0.#}%"))
        };
        _readers.Add(() => {
            wallpaperRow.Refresh();
            foreach (var row in wallpaperOptions) row.Visibility = Visible(readWallpaper().CanClear);
        });

        var music = GlassSettingRows.Group(_sections, "背景音乐", out var musicTitle);
        var policy = new ComboBox { Style = _resources.Style("GlassPresentationComboBox"),
            ItemsSource = new[] { "保持播放状态", "游戏启动后播放，退出后暂停", "游戏启动后暂停，退出后播放" } };
        policy.SelectionChanged += (_, _) => {
            if (!_active || _syncing || policy.SelectedIndex < 0) return;
            Settings.Set("UiMusicStart", policy.SelectedIndex == 1);
            Settings.Set("UiMusicStop", policy.SelectedIndex == 2);
        };
        _keys.Add("UiMusicStart");
        _keys.Add("UiMusicStop");
        _readers.Add(() => policy.SelectedIndex = Settings.Get<bool>("UiMusicStop") ? 2 : Settings.Get<bool>("UiMusicStart") ? 1 : 0);
        var playback = new[] {
            GlassSettingRows.Row(music, "音量", null, Valued(SliderEditor("UiMusicVolume", 1000, value => $"{value / 10:0.#}%"), value => $"{value / 10:0.#}%")),
            GlassSettingRows.Row(music, "随机播放", "按随机顺序播放音乐文件夹中的曲目。", Toggle("UiMusicRandom")),
            GlassSettingRows.Row(music, "随启动器自动播放", "打开启动器时开始播放。", Toggle("UiMusicAuto")),
            GlassSettingRows.Row(music, "游戏启动与退出", "游戏运行期间背景音乐的行为。", policy)
        };
        var visualizer = Toggle("UiMusicVisualizerEnabled");
        var style = Choice("UiMusicVisualizerStyle", (0, "柱状"), (1, "线式"));
        style.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = visualizer });
        GlassSettingRows.Row(music, "音频可视化", "播放时在界面上显示随音乐跳动的波形。", visualizer);
        GlassSettingRows.Row(music, "可视化样式", null, style);
        var musicActions = new WrapPanel();
        musicActions.Children.Add(ActionButton("打开文件夹", LauncherAppearanceSettings.OpenMusic));
        musicActions.Children.Add(ActionButton("刷新背景音乐", LauncherAppearanceSettings.RefreshMusic));
        Button clear = ActionButton("清空背景音乐", LauncherAppearanceSettings.ClearMusic);
        clear.SetResourceReference(Control.ForegroundProperty, "GlassPresentationDanger");
        musicActions.Children.Add(clear);
        foreach (Button action in musicActions.Children) action.Margin = new Thickness(0, 0, 8, 0);
        GlassSettingRows.Row(music, "音乐文件夹", "把音乐文件放进 PCL 文件夹下的 Musics 文件夹即可播放。", null).AddBelow(musicActions);
        _readers.Add(() => {
            int count = ModMusic.MusicCount;
            musicTitle.Text = count == 0 ? "背景音乐" : $"背景音乐（{count} 首）";
            foreach (var row in playback) row.Visibility = Visible(count > 0);
            clear.Visibility = Visible(count > 0);
        });

        BuildHomepageSettings();
        Loaded += (_, _) => Refresh();
    }

    private void BuildHomepageSettings()
    {
        var homepage = GlassSettingRows.Group(_sections, "主页");
        var homeModes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, name) in new[] { (0, "空白"), (3, "预设"), (1, "本地文件"), (2, "联网更新") }) {
            var radio = new RadioButton { Content = name, Style = _resources.Style("GlassPresentationRadioButton"), Margin = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetName(radio, name);
            radio.Checked += (_, _) => Write("UiCustomType", id);
            _readers.Add(() => radio.IsChecked = Settings.Get<int>("UiCustomType") == id);
            homeModes.Children.Add(radio);
        }
        _keys.Add("UiCustomType");
        var modeRow = GlassSettingRows.Row(homepage, "主页内容", "启动页右侧显示的内容。", homeModes);
        var warning = new GlassHint("请谨慎使用陌生人提供的主页，恶意代码可能会造成损害！", GlassHint.Tone.Warning, "HintCustomWarn");
        modeRow.AddBelow(warning);

        var presetChoice = Choice("UiCustomPreset", HomepagePresets);
        presetChoice.SelectionChanged += (_, _) => presetChoice.ToolTip = presetChoice.SelectedItem is ComboBoxItem { Content: string text }
            ? new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } }
            : null;
        var preset = GlassSettingRows.Row(homepage, "主页预设", "从社区作者制作的主页中选择一个。", presetChoice);

        var localActions = new WrapPanel();
        var refresh = ActionButton("刷新主页", LauncherAppearanceSettings.RefreshCustom);
        refresh.Style = _resources.Style("GlassPresentationAccentButton");
        localActions.Children.Add(refresh);
        localActions.Children.Add(ActionButton("生成教学文件", LauncherAppearanceSettings.CreateCustomFile));
        localActions.Children.Add(ActionButton("查看教程", LauncherAppearanceSettings.CustomTutorial));
        localActions.Children.Add(ActionButton("打开主页文件夹", LauncherAppearanceSettings.OpenCustomFolder));
        foreach (Button button in localActions.Children) button.Margin = new Thickness(0, 0, 8, 0);
        var local = GlassSettingRows.Row(homepage, "本地主页文件",
            "读取 PCL 文件夹下的 Custom.xaml。可以在其中添加文本、图片、常用网站和快捷启动。", null);
        local.AddBelow(localActions);

        const string addressHelp = "你可以用替换标记动态改变网址，以传入参数或变量。\n在 更多 → 帮助 中搜索“替换标记”以了解用法。";
        var address = new TextBox { Style = _resources.Style("GlassPresentationTextBox"), Height = 36, ToolTip = addressHelp };
        AutomationProperties.SetName(address, "下载地址");
        var addressError = _resources.Note(string.Empty, 11);
        addressError.Margin = new Thickness(3, 6, 3, 0);
        addressError.Visibility = Visibility.Collapsed;
        addressError.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
        var validate = new ValidateHttp();
        address.TextChanged += (_, _) => {
            string error = address.Text.Length == 0 ? string.Empty : validate.Validate(address.Text) ?? string.Empty;
            addressError.Text = error;
            addressError.Visibility = Visible(error.Length > 0);
            address.ToolTip = error.Length == 0 ? addressHelp : error;
            if (error.Length == 0) Write("UiCustomNet", address.Text);
        };
        _keys.Add("UiCustomNet");
        _readers.Add(() => { if (!address.IsKeyboardFocusWithin) address.Text = Settings.Get<string>("UiCustomNet"); });
        var onlineHelp = ActionButton("投稿联网主页", () => ModBase.OpenWebsite("https://github.com/Meloong-Git/PCL/discussions/2528"));
        onlineHelp.Margin = new Thickness();
        var online = GlassSettingRows.Row(homepage, "联网主页地址", "从这个网址获取主页内容，服主也可以用来发布公告。",
            new StackPanel { Width = GlassSettingRows.ControlWidth, Children = { address, addressError } });
        online.AddBelow(onlineHelp);

        GlassSettingRows.Row(homepage, "显示最近游玩的存档", "在主页列出最近进入的存档，需要支持 Quick Play 的 1.20 及以上版本。记录仅保存在本机。",
            Toggle("LaunchRecentWorlds"));
        _readers.Add(() => {
            int mode = Settings.Get<int>("UiCustomType");
            local.Visibility = Visible(mode == 1);
            online.Visibility = Visible(mode == 2);
            preset.Visibility = Visible(mode == 3);
            modeRow.Below.Visibility = warning.Visibility = Visible((mode is 1 or 2) && !Settings.Get<bool>("HintCustomWarn"));
        });
    }

    internal void Open() {
        _active = true;
        Scroll.ScrollToTop();
        Refresh();
    }

    internal void Leave() => _active = false;

    internal void Refresh() {
        if (!_active || _syncing) return;
        _syncing = true;
        try {
            RefreshPalette();
            foreach (Action read in _readers) read();
        } finally { _syncing = false; }
    }

    internal void RefreshPalette() => _resources.ApplyPalette();

    internal void Reset() {
        if (ModMain.MyMsgBox("是否要初始化个性化设置？背景图片、背景音乐和主页文件会保留。",
            "初始化确认", "初始化", "取消", IsWarn: true) != 1) return;
        foreach (string key in _keys) Settings.Reset(key);
        Settings.Reset("UiLauncherTheme");
        ModMain.Hint("已初始化个性化设置", ModMain.HintType.Green);
        Refresh();
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Write<T>(string key, T value) {
        if (!_active || _syncing || EqualityComparer<T>.Default.Equals(Settings.Get<T>(key), value)) return;
        Settings.Set(key, value);
        Refresh();
    }

    // A slider with its current value shown at its right end.
    private FrameworkElement Valued(Slider slider, Func<double, string> format)
    {
        var value = _resources.Text("");
        value.MinWidth = 52;
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        void Update() => value.Text = format(slider.Value);
        slider.ValueChanged += (_, _) => Update();
        Update();
        slider.VerticalAlignment = VerticalAlignment.Center;
        var host = new DockPanel { Width = 300, LastChildFill = true };
        DockPanel.SetDock(value, Dock.Right);
        host.Children.Add(value);
        host.Children.Add(slider);
        return host;
    }

    private Button ActionButton(string text, Action invoke) {
        var button = new Button { Content = text, Style = _resources.Style("GlassPresentationButton"),
            Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 5, 8, 3) };
        button.Click += (_, _) => { if (_active) { invoke(); Refresh(); } };
        return button;
    }

    private CheckBox Toggle(string key) {
        var control = new CheckBox { Style = _resources.Style("GlassPresentationSwitch") };
        control.Checked += (_, _) => Write(key, true);
        control.Unchecked += (_, _) => Write(key, false);
        _keys.Add(key);
        _readers.Add(() => control.IsChecked = Settings.Get<bool>(key));
        return control;
    }

    private ComboBox Choice(string key, params (int Id, string Label)[] options) {
        var control = new ComboBox { Style = _resources.Style("GlassPresentationComboBox"), MaxDropDownHeight = 260 };
        foreach (var option in options)
            control.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Id, Style = _resources.Style("GlassPresentationComboItem") });
        control.SelectionChanged += (_, _) => {
            if (control.SelectedItem is ComboBoxItem { Tag: int value }) Write(key, value);
        };
        _keys.Add(key);
        _readers.Add(() => {
            int value = Settings.Get<int>(key);
            int index = Array.FindIndex(options, option => option.Id == value);
            control.SelectedIndex = index < 0 ? 0 : index;
        });
        return control;
    }

    private Slider SliderEditor(string key, int max, Func<double, string> format) {
        var control = new TrackDragSlider { Style = _resources.Style("GlassPresentationSlider"), Maximum = max,
            SmallChange = max == 40 ? 1 : 10, LargeChange = max == 40 ? 5 : 50 };
        var hint = new GlassPageResources.SliderDragHint(control);
        _lifetimes.Add(hint);
        control.ValueChanged += (_, _) => {
            hint.SetText(format(control.Value));
            Write(key, (int)Math.Round(control.Value));
        };
        hint.SetText(format(control.Value));
        _keys.Add(key);
        _readers.Add(() => { if (!control.IsMouseCaptureWithin) control.Value = Settings.Get<int>(key); });
        return control;
    }

    public void Dispose() {
        Leave();
        foreach (IDisposable lifetime in _lifetimes) lifetime.Dispose();
    }

    private static readonly (int Id, string Label)[] HomepagePresets = [
        (0, "你知道吗？"),
        (1, "回声洞"),
        (2, "Minecraft 新闻（作者：最亮的信标）"),
        (4, "每日整合包推荐（作者：wkea）"),
        (5, "Minecraft 皮肤推荐（作者：wkea）"),
        (6, "OpenBMCLAPI 仪表盘 Lite（作者：Silverteal、Mxmilu666）"),
        (9, "PCL 新功能说明书（作者：WForst-Breeze）"),
        (11, "杂志主页（作者：CreeperIsASpy）"),
        (12, "PCL GitHub 仪表盘（作者：Deep-Dark-Forest）"),
        (13, "Minecraft 更新摘要（作者：pynickle）"),
        (14, "今日新闻热点（作者：EYicheng）"),
        (15, "Minecraft 芝士站（作者：awaEric233）"),
        (16, "整合包推荐引擎（作者：CHMYGZ）"),
        (17, "Bangumi 番剧主页（作者：wzyaeu）"),
        (18, "Bilibili 热门（作者：wzyaeu）"),
        (19, "Music 云音乐热门（作者：wzyaeu）"),
        (20, "模组推荐（作者：llsgllsg）"),
        (21, "每日游戏推荐（作者：llsgllsg）"),
        (22, "Wallpaper 每日壁纸（作者：wzyaeu）"),
        (23, "Weather 天气预报（作者：wzyaeu）"),
        (24, "新一代狗屁王大杂烩主页（作者：llsgllsg）")
    ];
}
