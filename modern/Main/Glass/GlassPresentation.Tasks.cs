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
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    private static readonly Geometry TaskDoneGlyph = TaskGlyph("M1,5 L4,8 10,1");
    private static readonly Geometry TaskFailedGlyph = TaskGlyph("M1,1 L9,9 M9,1 L1,9");
    private static Geometry TaskGlyph(string data) { Geometry geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    /// <summary>
    /// Task manager. Data comes from the original runtime: each card reads a live loader from
    /// <see cref="ModLoader.LoaderTaskbar"/> with its sub-tasks. Cancelling goes through the original card's own cancel
    /// button, so removal, cancellation and returning home keep their original behaviour.
    /// </summary>
    private Node ProjectTaskManager(PageSpeedRight source)
    {
        var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var empty = PlainText("当前没有进行中的任务"); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 24, 0, 0);
        empty.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
        root.Children.Add(empty);
        var list = new StackPanel();
        root.Children.Add(list);

        var cards = new Dictionary<ModLoader.LoaderBase, TaskCard>(ReferenceEqualityComparer.Instance);
        var node = new Node(root);
        node.Pull = () =>
        {
            List<ModLoader.LoaderBase> loaders = ModLoader.LoaderTaskbar.ToList().Where(loader => loader.Show).ToList();
            foreach (ModLoader.LoaderBase gone in cards.Keys.Where(loader => !loaders.Contains(loader)).ToList())
            {
                list.Children.Remove(cards[gone].View);
                cards.Remove(gone);
            }
            var added = new List<UIElement>();
            foreach (ModLoader.LoaderBase loader in loaders)
            {
                if (!cards.TryGetValue(loader, out TaskCard? card))
                {
                    card = new TaskCard(this, loader, source);
                    cards[loader] = card;
                    list.Children.Insert(0, card.View); // newest first, like the original
                    added.Add(card.View);
                }
                card.Update();
            }
            if (added.Count > 0) StaggeredEntrance.Play(added);
            empty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        return node;
    }

    private static GlassSurface Plate(UIElement child, Thickness margin)
    {
        var plate = new GlassSurface { Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 16, 20, 18), Margin = margin, BorderThickness = new Thickness(.7), BlurRadius = 24, TintOpacity = 1, HighlightOpacity = 0 };
        plate.SetResourceReference(Border.BackgroundProperty, "GlassPresentationFill");
        plate.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
        return plate;
    }

    /// <summary>One live task: title, state, aggregate progress, cancel and a row per sub-task.</summary>
    private sealed class TaskCard
    {
        private readonly GlassPresentationSession _owner;
        private readonly ModLoader.LoaderBase _loader;
        private readonly PageSpeedRight _page;
        private readonly TextBlock _state, _percent, _error;
        private readonly SlimBar _progress;
        private readonly StackPanel _rows = new() { Margin = new Thickness(0, 12, 0, 0) };
        private string _rowSignature = string.Empty;
        internal FrameworkElement View { get; }

        internal TaskCard(GlassPresentationSession owner, ModLoader.LoaderBase loader, PageSpeedRight page)
        {
            _owner = owner; _loader = loader; _page = page;
            var title = owner.PlainText(loader.Name); title.FontSize = 15; title.FontWeight = FontWeights.SemiBold; title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
            _state = owner.PlainText(string.Empty); _state.FontSize = 12; _state.Margin = new Thickness(10, 0, 0, 0); _state.VerticalAlignment = VerticalAlignment.Center;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _percent = owner.PlainText(string.Empty); _percent.FontSize = 13; _percent.FontWeight = FontWeights.SemiBold; _percent.VerticalAlignment = VerticalAlignment.Center; _percent.Margin = new Thickness(12, 0, 8, 0);
            var cancel = new Button { Content = "取消", Style = owner.Style("GlassPresentationButton"), Height = 28, MinHeight = 28, Padding = new Thickness(12, 0, 12, 0), FontSize = 12.5 };
            AutomationProperties.SetName(cancel, "取消任务：" + loader.Name);
            cancel.Click += (_, _) => { if (!owner.Live(cancel)) return; Cancel(); owner.Refresh(); };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(title);
            Grid.SetColumn(_state, 1); header.Children.Add(_state);
            Grid.SetColumn(_percent, 2); header.Children.Add(_percent);
            Grid.SetColumn(cancel, 3); header.Children.Add(cancel);

            _progress = new SlimBar { Margin = new Thickness(0, 12, 0, 0) };

            _error = owner.PlainText(string.Empty); _error.TextWrapping = TextWrapping.Wrap; _error.Margin = new Thickness(0, 12, 0, 0); _error.Cursor = System.Windows.Input.Cursors.Hand;
            _error.ToolTip = "单击复制错误详情";
            _error.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");
            _error.MouseLeftButtonDown += (_, _) => { ModBase.ClipboardSet(_error.Text, false); ModMain.Hint("已复制错误详情！", ModMain.HintType.Green); };

            var stack = new StackPanel();
            stack.Children.Add(header); stack.Children.Add(_progress); stack.Children.Add(_error); stack.Children.Add(_rows);
            View = Plate(stack, new Thickness(0, 0, 0, 12));
            AutomationProperties.SetName(View, loader.Name ?? string.Empty);
        }

        internal void Update()
        {
            ModBase.LoadState state = _loader.State;
            double progress = Math.Clamp(_loader.Progress, 0, 1);
            _progress.Value = progress;
            _percent.Text = state == ModBase.LoadState.Failed ? string.Empty : Math.Floor(progress * 100) + "%";
            _state.Text = state switch
            {
                ModBase.LoadState.Waiting => "等待中",
                ModBase.LoadState.Loading => "进行中",
                ModBase.LoadState.Failed => "失败",
                ModBase.LoadState.Finished => "已完成",
                ModBase.LoadState.Canceled => "已取消",
                _ => string.Empty,
            };
            bool failed = state == ModBase.LoadState.Failed;
            _progress.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
            _error.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            if (failed) _error.Text = _loader.Error?.GetDisplay(true) ?? "任务失败";
            UpdateRows(failed ? new List<ModLoader.LoaderBase>() : SubTasks());
        }

        private List<ModLoader.LoaderBase> SubTasks()
        {
            try
            {
                return Microsoft.VisualBasic.CompilerServices.NewLateBinding.LateGet(_loader, null, "GetLoaderList", Array.Empty<object>(), null, null, null) as List<ModLoader.LoaderBase> ?? new List<ModLoader.LoaderBase>();
            }
            catch (Exception) { return new List<ModLoader.LoaderBase>(); }
        }

        private void UpdateRows(List<ModLoader.LoaderBase> tasks)
        {
            string signature = string.Join("\n", tasks.Select(task => task.Name));
            if (signature != _rowSignature)
            {
                _rowSignature = signature;
                _rows.Children.Clear();
                foreach (ModLoader.LoaderBase _ in tasks) _rows.Children.Add(new TaskRow(_owner).View);
            }
            _rows.Visibility = tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            for (int index = 0; index < tasks.Count && index < _rows.Children.Count; index++)
                ((TaskRow)((FrameworkElement)_rows.Children[index]).Tag).Update(tasks[index]);
        }

        // The original card owns cancellation (removes it from the taskbar list, cancels on a worker, returns home).
        private void Cancel()
        {
            MyCard? original = _page.PanMain.Children.OfType<MyCard>().FirstOrDefault(card => card.Title == _loader.Name);
            if (original?.Children.OfType<MyIconButton>().FirstOrDefault(button => button.Name == "BtnCancel") is MyIconButton button)
            {
                button.PerformClick();
                return;
            }
            ModLoader.LoaderTaskbar.Remove(_loader);
            ModBase.RunInThread(() => _loader.Cancel());
        }
    }

    /// <summary>Sub-task row: a state mark (check, percentage, waiting dots or cross) and the step name.</summary>
    private sealed class TaskRow
    {
        private readonly Path _glyph;
        private readonly TextBlock _percent, _name;
        private readonly Grid _waiting;
        internal FrameworkElement View { get; }

        internal TaskRow(GlassPresentationSession owner)
        {
            var grid = new Grid { MinHeight = 26 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            _glyph = new Path { Width = 11, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _percent = owner.PlainText(string.Empty); _percent.FontSize = 12.5; _percent.FontWeight = FontWeights.SemiBold; _percent.HorizontalAlignment = HorizontalAlignment.Center; _percent.VerticalAlignment = VerticalAlignment.Center;
            _waiting = new Grid { Width = 18, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int dot = 0; dot < 3; dot++)
            {
                var ellipse = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(dot * 7, 0, 0, 0) };
                ellipse.SetResourceReference(Shape.FillProperty, "GlassPresentationSecondary");
                _waiting.Children.Add(ellipse);
            }
            _name = owner.PlainText(string.Empty); _name.FontSize = 13; _name.VerticalAlignment = VerticalAlignment.Center; _name.TextTrimming = TextTrimming.CharacterEllipsis;
            grid.Children.Add(_glyph); grid.Children.Add(_percent); grid.Children.Add(_waiting);
            Grid.SetColumn(_name, 1); grid.Children.Add(_name);
            grid.Tag = this;
            View = grid;
        }

        internal void Update(ModLoader.LoaderBase task)
        {
            _name.Text = task.Name;
            ModBase.LoadState state = task.State;
            _glyph.Visibility = state is ModBase.LoadState.Finished or ModBase.LoadState.Failed ? Visibility.Visible : Visibility.Collapsed;
            _percent.Visibility = state == ModBase.LoadState.Loading ? Visibility.Visible : Visibility.Collapsed;
            _waiting.Visibility = state == ModBase.LoadState.Waiting ? Visibility.Visible : Visibility.Collapsed;
            if (state == ModBase.LoadState.Loading) _percent.Text = Math.Floor(Math.Clamp(task.Progress, 0, 1) * 100) + "%";
            if (state == ModBase.LoadState.Finished) { _glyph.Data = TaskDoneGlyph; _glyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationText"); }
            if (state == ModBase.LoadState.Failed) { _glyph.Data = TaskFailedGlyph; _glyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationDanger"); }
            // Finished and waiting steps step back so the running one stands out.
            _name.SetResourceReference(TextBlock.ForegroundProperty, state == ModBase.LoadState.Loading ? "GlassPresentationText" : "GlassPresentationSecondary");
            _name.FontWeight = state == ModBase.LoadState.Loading ? FontWeights.SemiBold : FontWeights.Normal;
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

/// <summary>
/// Task manager sidebar: overall progress with a progress line and the transfer metrics, read from the values the
/// original task page computes every tick (总进度 / 下载速度 / 读写速度 / 剩余文件).
/// </summary>
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

    internal void Refresh()
    {
        if (ModMain.FrmSpeedLeft is not PageSpeedLeft left) return;
        _percent.Text = left.LabProgress.Text;
        _values["下载速度"].Text = left.LabSpeed.Text;
        _values["读取速度"].Text = left.LabReadSpeed.Text;
        _values["写入速度"].Text = left.LabWriteSpeed.Text;
        _values["剩余文件"].Text = left.LabFile.Text;
        string digits = new(left.LabProgress.Text.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
        double value = double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? Math.Clamp(parsed / 100, 0, 1) : 0;
        if (Math.Abs(value - _target) < .0005) return;
        _target = value;
        _motion.Retarget(value);
    }
}
