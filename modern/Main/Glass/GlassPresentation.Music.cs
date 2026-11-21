// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private Action BuildMusicSettings(PageSetupUI page, StackPanel body, Node node)
    {
        var volumeEditor = new Grid();
        volumeEditor.ColumnDefinitions.Add(new ColumnDefinition());
        volumeEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var volumeSlot = new Decorator();
        var percent = PlainText(string.Empty);
        percent.Name = "MusicVolumePercent";
        percent.MinWidth = 44;
        percent.Margin = new Thickness(10, 0, 0, 0);
        percent.TextAlignment = TextAlignment.Right;
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        Grid.SetColumn(percent, 1);
        volumeEditor.Children.Add(volumeSlot);
        volumeEditor.Children.Add(percent);

        var random = new Decorator();
        var autoplay = new Decorator();
        var gamePolicy = new ComboBox { Name = "ComboMusicGamePolicy", Style = Style("GlassPresentationComboBox"), MaxDropDownHeight = 200 };
        AutomationProperties.SetName(gamePolicy, "游戏启动与退出");
        foreach (string choice in new[] { "保持当前播放状态", "启动时播放 · 退出时暂停", "启动时暂停 · 退出时播放" })
            gamePolicy.Items.Add(new ComboBoxItem { Style = Style("GlassPresentationComboItem"), Content = choice });
        RouteDropDownWheel(gamePolicy, node);
        bool readingPolicy = false;
        gamePolicy.SelectionChanged += (_, args) =>
        {
            if (readingPolicy || !ReferenceEquals(args.OriginalSource, gamePolicy) || !Live(gamePolicy)) return;
            switch (gamePolicy.SelectedIndex)
            {
                case 0:
                    page.CheckMusicStart.SetChecked(false, true);
                    page.CheckMusicStop.SetChecked(false, true);
                    break;
                case 1:
                    page.CheckMusicStart.SetChecked(true, true);
                    if (page.CheckMusicStart.Checked) page.CheckMusicStop.SetChecked(false, true);
                    break;
                case 2:
                    page.CheckMusicStop.SetChecked(true, true);
                    if (page.CheckMusicStop.Checked) page.CheckMusicStart.SetChecked(false, true);
                    break;
            }
            Refresh();
        };

        var playback = new StackPanel { Name = "MusicPlaybackSettings" };
        Bind(playback, UIElement.VisibilityProperty, page.PanMusicDetail, nameof(UIElement.Visibility));
        playback.Children.Add(MusicSettingRow("音量", volumeEditor));
        playback.Children.Add(MusicSettingsDivider());
        playback.Children.Add(MusicSettingRow("随机播放", random));
        playback.Children.Add(MusicSettingRow("随启动器自动播放", autoplay));
        playback.Children.Add(MusicSettingRow("游戏启动与退出", gamePolicy));
        playback.Children.Add(MusicSettingsDivider());
        body.Children.Add(playback);

        var visualizerSource = (MyCheckBox)page.FindName("CheckMusicVisualizer");
        var styleSource = (MyComboBox)page.FindName("ComboMusicVisualizerStyle");
        var visualizer = new Decorator { ToolTip = "在主页播放背景音乐时显示频谱。" };
        var visualStyle = new Decorator();
        Bind(visualStyle, UIElement.IsEnabledProperty, visualizerSource, nameof(MyCheckBox.Checked));
        body.Children.Add(MusicSettingRow("音频可视化", visualizer));
        body.Children.Add(MusicSettingRow("可视化样式", visualStyle));
        body.Children.Add(MusicSettingsDivider());

        var actions = new WrapPanel { Name = "MusicFileActions", Margin = new Thickness(0, 2, 0, -4) };
        body.Children.Add(actions);

        return () =>
        {
            var volume = (Slider)Project(page.SliderMusicVolume, cardContent: true);
            AutomationProperties.SetName(volume, "背景音乐音量");
            if (!ReferenceEquals(volumeSlot.Child, volume)) volumeSlot.Child = volume;
            percent.Text = volume.ToolTip as string ?? $"{Math.Ceiling(100 * volume.Value / volume.Maximum):0}%";
            AttachMusicToggle(random, page.CheckMusicRandom, "随机播放");
            AttachMusicToggle(autoplay, page.CheckMusicAuto, "随启动器自动播放");
            AttachMusicToggle(visualizer, visualizerSource, "音频可视化");

            readingPolicy = true;
            try { gamePolicy.SelectedIndex = page.CheckMusicStop.Checked ? 2 : page.CheckMusicStart.Checked ? 1 : 0; }
            finally { readingPolicy = false; }

            var style = (ComboBox)Project(styleSource, cardContent: true);
            AutomationProperties.SetName(style, "可视化样式");
            if (!ReferenceEquals(visualStyle.Child, style)) visualStyle.Child = style;

            var buttons = new[] { page.BtnMusicOpen, page.BtnMusicRefresh, page.BtnMusicClear }
                .Select(button => (Button)Project(button, cardContent: true)).ToArray();
            foreach (Button button in buttons)
            {
                button.MinWidth = 0;
                button.Margin = new Thickness(0, 0, 8, 4);
            }
            Children(actions, buttons);
        };
    }

    private void AttachMusicToggle(Decorator slot, MyCheckBox source, string caption)
    {
        var toggle = (CheckBox)Project(source, cardContent: true);
        toggle.Content = null;
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(toggle, caption);
        if (!ReferenceEquals(slot.Child, toggle)) slot.Child = toggle;
    }

    private Grid MusicSettingRow(string title, FrameworkElement editor)
    {
        var row = new Grid { MinHeight = 42 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star), MaxWidth = 280 });
        var label = PlainText(title);
        label.Margin = new Thickness(0, 5, 12, 5);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(editor, 1);
        editor.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        row.Children.Add(editor);
        return row;
    }

    private static Border MusicSettingsDivider()
    {
        var divider = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), IsHitTestVisible = false };
        divider.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
        return divider;
    }
}
