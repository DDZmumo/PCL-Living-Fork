// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using PCLCS.Applications;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>Native glass view for the loader tasks and their transfer metrics.</summary>
internal sealed class GlassTaskManagerPage : ContentControl
{
    private readonly TaskManagerApplicationService _service;
    private readonly GlassPageResources _resources;
    private readonly StackPanel _cardsPanel = new();
    private readonly TextBlock _empty;
    private readonly Dictionary<int, TaskCard> _cards = new();
    private bool _primed;
    internal GlassTaskSidebar Sidebar { get; } = new();
    internal ScrollViewer Scroll { get; }
    internal TaskTransferMetrics Metrics { get; private set; }
    internal bool HasItems => _cards.Count > 0;

    internal GlassTaskManagerPage(TaskManagerApplicationService service)
    {
        _service = service;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
        _resources = new GlassPageResources(this);
        _empty = PlainText("当前没有进行中的任务");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 24, 0, 0);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        var stack = new StackPanel();
        stack.Children.Add(_empty);
        stack.Children.Add(_cardsPanel);
        Scroll = new ScrollViewer
        {
            Content = stack,
            Style = StyleOf("GlassPresentationScrollViewer"),
            Margin = new Thickness(0, 6, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Content = Scroll;
        Loaded += (_, _) => _resources.ApplyPalette();
    }

    internal void Open()
    {
        _primed = false;
        Scroll.ScrollToTop();
        _service.ResetRates();
        Refresh();
    }

    internal void RefreshPalette() => _resources.ApplyPalette();

    internal void Refresh()
    {
        RefreshPalette();
        IReadOnlyList<TaskManagerItem> items = _service.ReadItems();
        var present = items.Select(item => item.Task.Id).ToHashSet();
        foreach (int id in _cards.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _cardsPanel.Children.Remove(_cards[id].View);
            _cards.Remove(id);
        }

        var added = new List<UIElement>();
        for (int index = 0; index < items.Count; index++)
        {
            TaskManagerItem item = items[index];
            if (!_cards.TryGetValue(item.Task.Id, out TaskCard? card))
            {
                card = new TaskCard(this, item.Task);
                _cards.Add(item.Task.Id, card);
                _cardsPanel.Children.Insert(index, card.View);
                added.Add(card.View);
            }
            card.Update(item, this);
        }

        if (_primed && added.Count > 0) StaggeredEntrance.Play(added);
        _primed = true;
        _empty.Visibility = HasItems ? Visibility.Collapsed : Visibility.Visible;
        Metrics = _service.SampleTransfers(items);
        Sidebar.Refresh(Metrics);
    }

    private Style StyleOf(string key) => _resources.Style(key);
    private TextBlock PlainText(string? text) => _resources.Text(text);

    private static readonly Geometry TaskDoneGlyph = TaskGlyph("M1,5 L4,8 10,1");
    private static readonly Geometry TaskFailedGlyph = TaskGlyph("M1,1 L9,9 M9,1 L1,9");
    private static Geometry TaskGlyph(string data) { Geometry geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    /// <summary>One live task: title, state, aggregate progress, cancel and a row per sub-task.</summary>
    private sealed class TaskCard
    {
        private readonly int _taskId;
        private readonly TextBlock _title, _state, _percent, _error;
        private readonly Button _cancel;
        private readonly SlimBar _progress;
        private readonly StackPanel _rows = new() { Margin = new Thickness(0, 12, 0, 0) };
        private readonly List<TaskRow> _stages = new();
        internal FrameworkElement View { get; }

        internal TaskCard(GlassTaskManagerPage owner, ApplicationTaskSnapshot task)
        {
            _taskId = task.Id;
            _title = owner.PlainText(task.Name); _title.FontSize = 15; _title.FontWeight = FontWeights.SemiBold; _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.VerticalAlignment = VerticalAlignment.Center;
            _title.TextWrapping = TextWrapping.NoWrap;
            _title.ToolTip = task.Name;
            _state = owner.PlainText(string.Empty); _state.FontSize = 12; _state.Margin = new Thickness(10, 0, 0, 0); _state.VerticalAlignment = VerticalAlignment.Center; _state.TextWrapping = TextWrapping.NoWrap;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _percent = owner.PlainText(string.Empty); _percent.FontSize = 13; _percent.FontWeight = FontWeights.SemiBold; _percent.VerticalAlignment = VerticalAlignment.Center; _percent.Margin = new Thickness(12, 0, 8, 0);
            _cancel = new Button { Content = "取消", Style = owner.StyleOf("GlassPresentationButton"), Height = 28, MinHeight = 28, Padding = new Thickness(12, 0, 12, 0), FontSize = 12.5 };
            AutomationProperties.SetName(_cancel, "取消任务：" + task.Name);
            _cancel.Click += (_, _) => { owner._service.CloseOrCancel(_taskId); owner.Refresh(); };

            var heading = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(_state, Dock.Right);
            heading.Children.Add(_state);
            heading.Children.Add(_title);

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(heading);
            Grid.SetColumn(_percent, 1); header.Children.Add(_percent);
            Grid.SetColumn(_cancel, 2); header.Children.Add(_cancel);

            _progress = new SlimBar { Margin = new Thickness(0, 12, 0, 0) };

            _error = owner.PlainText(string.Empty); _error.TextWrapping = TextWrapping.Wrap; _error.Margin = new Thickness(0, 12, 0, 0); _error.Cursor = System.Windows.Input.Cursors.Hand;
            _error.ToolTip = "单击复制错误详情";
            _error.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
            _error.MouseLeftButtonDown += (_, _) => { ModBase.ClipboardSet(_error.Text, false); ModMain.Hint("已复制错误详情！", ModMain.HintType.Green); };

            var stack = new StackPanel();
            stack.Children.Add(header); stack.Children.Add(_progress); stack.Children.Add(_error); stack.Children.Add(_rows);
            View = GlassPageResources.Plate(stack, new Thickness(0, 0, 0, 12));
            AutomationProperties.SetName(View, task.Name ?? string.Empty);
        }

        internal void Update(TaskManagerItem item, GlassTaskManagerPage owner)
        {
            ApplicationTaskSnapshot task = item.Task;
            if (_title.Text != task.Name)
            {
                _title.Text = task.Name;
                _title.ToolTip = task.Name;
                AutomationProperties.SetName(View, task.Name);
                AutomationProperties.SetName(_cancel, "管理任务：" + task.Name);
            }
            double progress = Math.Clamp(task.Progress, 0, 1);
            _progress.Value = progress;
            bool failed = task.State == ApplicationTaskState.Failed;
            _percent.Text = failed ? string.Empty : Math.Floor(progress * 100) + "%";
            _state.Text = item.Cancelling ? "取消中" : task.State switch
            {
                ApplicationTaskState.Waiting => "等待中",
                ApplicationTaskState.Running => "进行中",
                ApplicationTaskState.Failed => "失败",
                ApplicationTaskState.Succeeded => "已完成",
                _ => "已取消"
            };
            _progress.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
            _error.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            _error.Text = item.Error ?? string.Empty;
            _cancel.Content = failed ? "关闭" : "取消";
            _cancel.IsEnabled = item.CanClose;
            _cancel.ToolTip = item.ActionHint;

            while (_stages.Count > item.Stages.Count)
            {
                _stages.RemoveAt(_stages.Count - 1);
                _rows.Children.RemoveAt(_rows.Children.Count - 1);
            }
            for (int index = 0; index < item.Stages.Count; index++)
            {
                if (index == _stages.Count)
                {
                    var stage = new TaskRow(owner);
                    _stages.Add(stage);
                    _rows.Children.Add(stage.View);
                }
                _stages[index].Update(item.Stages[index]);
            }
            _rows.Visibility = _stages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Sub-task row: a state mark (check, percentage, waiting dots or cross) and the step name.</summary>
    private sealed class TaskRow
    {
        private readonly Path _glyph;
        private readonly TextBlock _percent, _name, _detail;
        private readonly Grid _waiting;
        internal FrameworkElement View { get; }

        private TaskStage? _current;

        internal TaskRow(GlassTaskManagerPage owner)
        {
            var grid = new Grid { MinHeight = 26 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            _glyph = new Path { Width = 11, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _percent = owner.PlainText(string.Empty); _percent.FontSize = 12; _percent.FontWeight = FontWeights.SemiBold; _percent.HorizontalAlignment = HorizontalAlignment.Center; _percent.VerticalAlignment = VerticalAlignment.Center; _percent.TextWrapping = TextWrapping.NoWrap;
            _waiting = new Grid { Width = 18, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int dot = 0; dot < 3; dot++)
            {
                var ellipse = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(dot * 7, 0, 0, 0) };
                ellipse.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary");
                _waiting.Children.Add(ellipse);
            }
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
            _name = owner.PlainText(string.Empty); _name.FontSize = 13; _name.VerticalAlignment = VerticalAlignment.Center; _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _detail = owner.PlainText(string.Empty); _detail.FontSize = 11.5; _detail.Margin = new Thickness(0, 2, 0, 0); _detail.Visibility = Visibility.Collapsed;
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            content.Children.Add(_name); content.Children.Add(_detail);
            grid.Children.Add(_glyph); grid.Children.Add(_percent); grid.Children.Add(_waiting);
            Grid.SetColumn(content, 1); grid.Children.Add(content);
            View = grid;
        }

        internal void Update(TaskStage stage)
        {
            if (stage == _current) return;
            _current = stage;
            _name.Text = stage.Name;
            bool active = stage.State is TaskStageState.Running or TaskStageState.Cancelling;
            _name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            _name.SetResourceReference(TextBlock.ForegroundProperty, active ? "GlassPresentationText" : "GlassPresentationSecondary");
            string? badge = stage.State switch
            {
                TaskStageState.Running when stage.Progress is double progress => Math.Floor(Math.Clamp(progress, 0, 1) * 100) + "%",
                TaskStageState.Running => "进行中",
                TaskStageState.AwaitingConfirmation => "待确认",
                TaskStageState.Cancelling => "取消中",
                TaskStageState.Canceled => "已取消",
                TaskStageState.Skipped => "跳过",
                TaskStageState.NotRun => "未执行",
                _ => null
            };
            bool done = stage.State == TaskStageState.Succeeded;
            bool failed = stage.State == TaskStageState.Failed;
            _glyph.Visibility = done || failed ? Visibility.Visible : Visibility.Collapsed;
            if (done || failed)
            {
                _glyph.Data = done ? TaskDoneGlyph : TaskFailedGlyph;
                _glyph.SetResourceReference(Shape.StrokeProperty, done ? "GlassPresentationText" : "GlassPresentationDanger");
            }
            _percent.Text = badge ?? string.Empty;
            _percent.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
            _waiting.Visibility = stage.State == TaskStageState.Waiting ? Visibility.Visible : Visibility.Collapsed;
            _detail.Text = stage.Detail?.Trim() ?? string.Empty;
            _detail.Visibility = _detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Rounded 4 px progress line: neutral track, accent fill that glides to each new value.</summary>
    private sealed class SlimBar : Grid
    {
        private readonly ScaleTransform _fill = new(0, 1);
        private readonly SpringMotion? _motion;
        private double _value;

        internal SlimBar()
        {
            Height = 4;
            var track = new Border { CornerRadius = new CornerRadius(2) };
            track.SetResourceReference(Border.BackgroundProperty, "GlassPresentationTrack");
            var fill = new Border { CornerRadius = new CornerRadius(2), RenderTransform = _fill, RenderTransformOrigin = new Point(0, .5) };
            fill.SetResourceReference(Border.BackgroundProperty, "GlassPresentationAccent");
            Children.Add(track); Children.Add(fill);
            _motion = new SpringMotion(this, 0, value => _fill.ScaleX = Math.Clamp(value, 0, 1), new SpringParameters(.4, 1));
        }

        internal double Value
        {
            get => _value;
            set { if (Math.Abs(_value - value) < .0005) return; _value = value; _motion?.Retarget(value); }
        }
    }
}

/// <summary>Transfer metrics and progress for the task manager sidebar.</summary>
internal sealed class GlassTaskSidebar : Grid
{
    private readonly TextBlock _percent;
    private readonly Border _fill;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly SpringMotion _motion;
    private readonly Dictionary<string, TextBlock> _values = new();
    private double _target = -1;

    internal GlassTaskSidebar()
    {
        Margin = new Thickness(12, 4, 12, 0);
        // Top: overall progress at its own size. Below: the metrics share all remaining height equally, as tiles.
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var head = new StackPanel();
        head.Children.Add(Caption("总进度"));
        _percent = Value(28); _percent.Margin = new Thickness(0, 2, 0, 0);
        head.Children.Add(_percent);
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 10, 0, 14) };
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "GlassOutlineBrush");
        _fill = new Border { CornerRadius = new CornerRadius(2), RenderTransform = _fillScale, RenderTransformOrigin = new Point(0, .5) };
        _fill.SetResourceReference(Border.BackgroundProperty, "GlassAccentBrush");
        bar.Children.Add(track); bar.Children.Add(_fill);
        head.Children.Add(bar);
        Children.Add(head);

        var tiles = new Grid();
        Grid.SetRow(tiles, 1);
        string[] names = { "下载速度", "读取速度", "写入速度", "剩余文件" };
        for (int index = 0; index < names.Length; index++)
        {
            tiles.RowDefinitions.Add(new RowDefinition { MinHeight = 52 });
            var tile = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, index == names.Length - 1 ? 12 : 8) };
            tile.SetResourceReference(Border.BackgroundProperty, "GlassSelectedBrush");
            var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(Caption(names[index]));
            TextBlock value = Value(20); value.Margin = new Thickness(0, 2, 0, 0); value.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(value, 1); content.Children.Add(value);
            tile.Child = content;
            Grid.SetRow(tile, index); tiles.Children.Add(tile);
            _values[names[index]] = value;
        }
        Children.Add(tiles);
        _motion = new SpringMotion(this, 0, value => _fillScale.ScaleX = Math.Clamp(value, 0, 1), new SpringParameters(.4, 1));
    }

    private static TextBlock Caption(string text)
    {
        var caption = new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush");
        return caption;
    }

    private static TextBlock Value(double size)
    {
        var value = new TextBlock { Text = "-", FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "GlassTextBrush");
        return value;
    }

    internal void Refresh(TaskTransferMetrics metrics)
    {
        double value = metrics.Progress;
        _percent.Text = value >= 1 ? "100 %" : (value * 100).ToString("F2") + " %";
        _values["下载速度"].Text = StringUtils.FormatByteSize(metrics.DownloadRate) + "/s";
        _values["读取速度"].Text = StringUtils.FormatByteSize(metrics.ReadRate) + "/s";
        _values["写入速度"].Text = StringUtils.FormatByteSize(metrics.WriteRate) + "/s";
        _values["剩余文件"].Text = metrics.RemainingFiles + (metrics.DownloadCountEstimated ? "*" : "");
        if (Math.Abs(value - _target) < .0005) return;
        _target = value;
        _motion.Retarget(value);
    }
}
