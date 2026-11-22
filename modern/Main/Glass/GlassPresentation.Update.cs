// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Newtonsoft.Json.Linq;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    /// <summary>
    /// Launcher update page, laid out like a phone's system update screen. The hero carries the result as its headline
    /// (正在检查更新 / 已是最新版本 / 发现新版本) over the launcher mark in a halo, with the main action right under it;
    /// a new version brings its notes directly below. Update settings and the version history follow, quieter.
    /// Everything is read from, and every action goes through, the original page and its controls: the check runs when
    /// the page opens (as before), the selectors are the projected originals (channel confirmation included), and
    /// the headline, update notes and install action use the newest eligible release from the original catalog.
    /// </summary>
    private Node ProjectLauncherUpdate(PageSetupUpdate page)
    {
        var view = new LauncherUpdateView(this, page);
        var node = new Node(new ScrollEdgeFade(view.Root));
        node.Pull = view.Pull;
        node.Cleanup.Add(view.Dispose);
        return node;
    }

    private sealed class LauncherUpdateView : IDisposable
    {
        private const string CheckGlyph = "M6.5,12.5 L10.5,16.5 L17.5,8";
        private const string ArrowGlyph = "M12,17.5 V6.5 M7,11.5 L12,6.5 L17,11.5";
        private const string AlertGlyph = "M12,6.5 V13 M12,17 V17.1";
        private const double Halo = 176, Ring = Halo / 2 - 14, Logo = 104;

        private readonly GlassPresentationSession _owner;
        private readonly PageSetupUpdate _page;
        internal ScrollViewer Root { get; }
        private readonly StackPanel _stack = new() { Margin = new Thickness(0, 6, 0, 18) };

        // ---- Hero ----
        private readonly Grid _halo = new() { Width = Halo, Height = Halo, HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        private readonly Ellipse _glow = new() { IsHitTestVisible = false };
        private readonly ScaleTransform _glowScale = new(1, 1);
        private readonly Ellipse[] _ripples = { new() { IsHitTestVisible = false }, new() { IsHitTestVisible = false } };
        private readonly Path _arc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = 0 };
        private readonly RotateTransform _arcTurn = new();
        private readonly ContentControl _logo = new() { Width = Logo, Height = Logo, Focusable = false, IsTabStop = false, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5) };
        private readonly ScaleTransform _logoScale = new(1, 1);
        private readonly Grid _badge = new() { Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 26, 26), IsHitTestVisible = false };
        private readonly Ellipse _badgeFace = new() { StrokeThickness = 3 };
        private readonly Path _badgeGlyph = new() { Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        private readonly ScaleTransform _badgeScale = new(0, 0);
        private readonly Grid _headline = new() { ClipToBounds = false, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 14, 0, 0) };
        private readonly TextBlock[] _titles = new TextBlock[2];
        private readonly TranslateTransform[] _titleShift = { new(), new() };
        private readonly ScaleTransform[] _titleScale = { new(1, 1), new(1, 1) };
        private readonly ScaleTransform _heroScale = new(1, 1);
        private readonly TranslateTransform _heroShift = new();
        private readonly ScaleTransform _notesScale = new(1, 1);
        private int _titleFront;
        private readonly TextBlock _product, _subtitle, _detail, _checkedAt;
        private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        private readonly Button _check, _install;

        // ---- Notes ----
        private readonly FrameworkElement _notesCard;
        private readonly TranslateTransform _notesRise = new();
        private readonly TextBlock _notesTitle, _notes;
        private readonly Button _fullNotes;

        // ---- Settings and history ----
        private readonly StackPanel _settings = new();
        private readonly TextBlock _count, _empty, _listLoading;
        private readonly ContentControl _search = new() { Focusable = false, IsTabStop = false, Margin = new Thickness(0, 0, 0, 12) };
        private readonly ContentControl _rows = new() { Focusable = false, IsTabStop = false };

        // ---- Motion ----
        private readonly SpringMotion _sweep, _arcPresence, _badgeMotion, _titleMotion, _notesMotion, _logoMotion, _installMotion, _heroMotion;
        private readonly ScaleTransform _installScale = new(1, 1);
        private double _spinSpeed, _breath, _breathAmount;
        private TimeSpan? _lastFrame;
        private bool _hooked, _settling;

        private enum State { Checking, Latest, Available, Failed, Development }
        private State? _state;
        private bool _wasLoading, _primed, _disposed;
        private DateTime? _checkedTime;
        private JObject? _newest;
        private string _markVersion = "";

        internal LauncherUpdateView(GlassPresentationSession owner, PageSetupUpdate page)
        {
            _owner = owner; _page = page;
            Root = new ScrollViewer { Content = _stack, Style = owner.Style("GlassPresentationScrollViewer"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            SmoothScroll.SetIsEnabled(Root, true);

            // ---------- Hero ----------
            // Soft glow: the accent colour fading out from the centre, breathing while the check runs.
            _glow.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
            _glow.OpacityMask = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(150, 0, 0, 0), 0), new GradientStop(Color.FromArgb(60, 0, 0, 0), .55), new GradientStop(Colors.Transparent, 1) } };
            _glow.RenderTransformOrigin = new Point(.5, .5); _glow.RenderTransform = _glowScale;
            _halo.Children.Add(_glow);
            foreach (Ellipse ripple in _ripples)
            {
                ripple.Width = ripple.Height = Ring * 2; ripple.StrokeThickness = 2; ripple.Opacity = 0;
                ripple.SetResourceReference(Shape.StrokeProperty, "GlassPresentationAccent");
                ripple.RenderTransformOrigin = new Point(.5, .5); ripple.RenderTransform = new ScaleTransform(1, 1);
                _halo.Children.Add(ripple);
            }
            var track = new Ellipse { Width = Ring * 2, Height = Ring * 2, StrokeThickness = 4, Opacity = .55, IsHitTestVisible = false };
            track.SetResourceReference(Shape.StrokeProperty, "GlassPresentationTrack");
            _halo.Children.Add(track);
            _arc.SetResourceReference(Shape.StrokeProperty, "GlassPresentationAccent");
            _arc.RenderTransformOrigin = new Point(.5, .5); _arc.RenderTransform = _arcTurn;
            _halo.Children.Add(_arc);
            _logo.RenderTransform = _logoScale;
            _halo.Children.Add(_logo);
            _badgeFace.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
            _badgeFace.SetResourceReference(Shape.StrokeProperty, "GlassPresentationFill");
            _badgeGlyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationOnAccent");
            _badge.Children.Add(_badgeFace); _badge.Children.Add(_badgeGlyph);
            _badge.RenderTransformOrigin = new Point(.5, .5); _badge.RenderTransform = _badgeScale;
            _halo.Children.Add(_badge);
            _halo.ToolTip = "查看当前版本的更新说明";
            _halo.MouseLeftButtonUp += (_, _) => { if (owner.Live(_halo)) page.ShowCurrentUpdateLog(); };
            AutomationProperties.SetName(_halo, "当前版本更新说明");

            for (int index = 0; index < 2; index++)
            {
                TextBlock title = owner.PlainText("");
                title.FontSize = 16.5; title.FontWeight = FontWeights.SemiBold; title.TextAlignment = TextAlignment.Center; title.TextWrapping = TextWrapping.Wrap;
                title.RenderTransformOrigin = new Point(.5, .5);
                title.RenderTransform = new TransformGroup { Children = { _titleScale[index], _titleShift[index] } }; title.Opacity = index == 0 ? 1 : 0;
                _titles[index] = title; _headline.Children.Add(title);
            }
            _product = owner.PlainText("PCL Living Fork"); _product.FontSize = 30; _product.FontWeight = FontWeights.Bold; _product.HorizontalAlignment = HorizontalAlignment.Center;
            _product.Margin = new Thickness(0, 18, 0, 0); _product.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            _subtitle = owner.PlainText(""); _subtitle.FontSize = 20; _subtitle.FontWeight = FontWeights.SemiBold; _subtitle.HorizontalAlignment = HorizontalAlignment.Center; _subtitle.Margin = new Thickness(0, 2, 0, 0);
            _subtitle.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _detail = owner.PlainText(""); _detail.FontSize = 13; _detail.HorizontalAlignment = HorizontalAlignment.Center; _detail.TextAlignment = TextAlignment.Center; _detail.TextWrapping = TextWrapping.Wrap;
            _detail.Margin = new Thickness(32, 4, 32, 0); _detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");

            _install = Pill("下载并更新", primary: true);
            _install.RenderTransform = new TransformGroup { Children = { _installScale, (Transform)_install.RenderTransform } };
            _install.Visibility = Visibility.Collapsed; _install.Margin = new Thickness(0, 0, 12, 0);
            _install.Click += (_, _) =>
            {
                if (!owner.Live(_install) || _newest is null || !ReferenceEquals(_newest, page.LatestUpdateRelease)) return;
                page.UpdateRelease(_newest);
                owner.Refresh();
            };
            _check = Pill("检查更新", primary: false);
            _check.Click += (_, _) => { if (!owner.Live(_check)) return; page.BtnRefresh.PerformClick(); owner.Refresh(); };
            _actions.Children.Add(_install); _actions.Children.Add(_check);
            _checkedAt = owner.PlainText(""); _checkedAt.FontSize = 11.5; _checkedAt.HorizontalAlignment = HorizontalAlignment.Center; _checkedAt.Margin = new Thickness(0, 12, 0, 0);
            _checkedAt.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary"); _checkedAt.Opacity = .8;

            var hero = new StackPanel { Margin = new Thickness(0, 22, 0, 26) };
            hero.Children.Add(_halo); hero.Children.Add(_product); hero.Children.Add(_subtitle); hero.Children.Add(_headline); hero.Children.Add(_detail); hero.Children.Add(_actions); hero.Children.Add(_checkedAt);
            hero.RenderTransformOrigin = new Point(.5, .3);
            hero.RenderTransform = new TransformGroup { Children = { _heroScale, _heroShift } };
            FrameworkElement heroPlate = Plate(hero, new Thickness(0, 0, 0, 14));

            // ---------- Notes of the new version ----------
            var notes = new StackPanel();
            _notesTitle = owner.PlainText("更新内容"); _notesTitle.FontSize = 16; _notesTitle.FontWeight = FontWeights.SemiBold;
            notes.Children.Add(_notesTitle);
            _notes = owner.PlainText(""); _notes.FontSize = 14; _notes.TextWrapping = TextWrapping.Wrap; _notes.LineHeight = 24; _notes.MaxHeight = 24 * 10;
            _notes.TextTrimming = TextTrimming.CharacterEllipsis; _notes.Margin = new Thickness(0, 12, 0, 0);
            notes.Children.Add(_notes);
            _fullNotes = new Button { Content = "查看完整更新说明", Style = owner.Style("GlassPresentationButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(14, 0, 14, 0) };
            _fullNotes.Click += (_, _) => { if (owner.Live(_fullNotes)) ShowNotes(); };
            notes.Children.Add(_fullNotes);
            var notesPlate = Plate(notes, new Thickness(0, 0, 0, 14));
            notesPlate.RenderTransformOrigin = new Point(.5, 0);
            notesPlate.RenderTransform = new TransformGroup { Children = { _notesScale, _notesRise } }; notesPlate.Visibility = Visibility.Collapsed;
            _notesCard = notesPlate;

            // ---------- Settings ----------
            var settingsPlate = (GlassSurface)Plate(_settings, new Thickness(0, 0, 0, 14));
            settingsPlate.Padding = new Thickness(22, 6, 22, 6);

            // ---------- History ----------
            var history = new StackPanel();
            var header = new Grid { Margin = new Thickness(2, 0, 2, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var historyTitle = owner.PlainText("版本历史"); historyTitle.FontSize = 16; historyTitle.FontWeight = FontWeights.SemiBold;
            _count = owner.PlainText(""); _count.FontSize = 12; _count.VerticalAlignment = VerticalAlignment.Center;
            _count.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            header.Children.Add(historyTitle); Grid.SetColumn(_count, 1); header.Children.Add(_count);
            _listLoading = owner.PlainText("正在读取版本列表…"); _listLoading.FontSize = 12.5; _listLoading.Margin = new Thickness(4, 6, 4, 10);
            _listLoading.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _empty = owner.PlainText(""); _empty.FontSize = 12.5; _empty.TextWrapping = TextWrapping.Wrap; _empty.Margin = new Thickness(4, 6, 4, 10);
            _empty.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            history.Children.Add(header); history.Children.Add(_search); history.Children.Add(_listLoading); history.Children.Add(_rows); history.Children.Add(_empty);
            FrameworkElement historyPlate = Plate(history, new Thickness(0));

            _stack.Children.Add(heroPlate); _stack.Children.Add(_notesCard); _stack.Children.Add(settingsPlate); _stack.Children.Add(historyPlate);

            // ---------- Motion ----------
            bool snap() => !MotionPreferences.AllowsAnimation;
            // The arc's length: short while it orbits, closing into a full ring when the result lands.
            _sweep = new SpringMotion(_arc, .08, value =>
            {
                _arc.Data = Arc(Math.Clamp(value, .02, .9999));
                if (_settling && value > .985)
                {
                    _settling = false;
                    _arcPresence!.Retarget(0, snap());
                    Celebrate();
                }
            }, new SpringParameters(.55, .86));
            _arcPresence = new SpringMotion(_arc, 0, value => _arc.Opacity = Math.Clamp(value, 0, 1), new SpringParameters(.4, 1));
            _badgeMotion = new SpringMotion(_badge, 0, value => _badgeScale.ScaleX = _badgeScale.ScaleY = Math.Max(0, value), new SpringParameters(.46, .52));
            _titleMotion = new SpringMotion(_headline, 1, value =>
            {
                double v = Math.Clamp(value, 0, 1.2), p = Math.Clamp(value, 0, 1);
                int f = _titleFront, b = 1 - _titleFront;
                _titles[f].Opacity = p;
                _titleShift[f].Y = 18 * (1 - v);
                _titleScale[f].ScaleX = _titleScale[f].ScaleY = .82 + .18 * v;
                _titles[b].Opacity = Math.Clamp(1 - value * 1.6, 0, 1);
                _titleShift[b].Y = -14 * p;
                _titleScale[b].ScaleX = _titleScale[b].ScaleY = 1 + .1 * p;
            }, new SpringParameters(.48, .74));
            _notesMotion = new SpringMotion(notesPlate, 0, value =>
            {
                double v = Math.Clamp(value, 0, 1);
                notesPlate.Opacity = v;
                _notesRise.Y = 36 * (1 - value);
                _notesScale.ScaleX = _notesScale.ScaleY = .95 + .05 * Math.Clamp(value, 0, 1.05);
                notesPlate.Visibility = v < .01 && _state != State.Available ? Visibility.Collapsed : Visibility.Visible;
            }, new SpringParameters(.5, .78));
            _heroMotion = new SpringMotion(hero, 1, value =>
            {
                _heroScale.ScaleX = _heroScale.ScaleY = value;
                _heroShift.Y = 40 * (1 - value);
            }, new SpringParameters(.5, .6));
            _logoMotion = new SpringMotion(_logo, .55, value => _logoScale.ScaleX = _logoScale.ScaleY = value, new SpringParameters(.6, .62));
            _installMotion = new SpringMotion(_install, 0, value =>
            {
                double v = Math.Clamp(value, 0, 1);
                _install.Opacity = v;
                _installScale.ScaleX = _installScale.ScaleY = .7 + .3 * Math.Clamp(value, 0, 1.1);
                _install.Visibility = v < .01 && _state != State.Available ? Visibility.Collapsed : Visibility.Visible;
            }, new SpringParameters(.42, .66));

            Root.Loaded += (_, _) =>
            {
                if (_primed) return;
                _primed = true;
                // Entrance: the mark drops into its halo, the blocks rise in turn.
                _logoMotion.Snap(.55); _logoMotion.Retarget(1, snap());
                _heroMotion.Snap(.94); _heroMotion.Retarget(1, snap());
                StaggeredEntrance.Play(_stack.Children.Cast<UIElement>().Concat(hero.Children.Cast<UIElement>().Skip(1)));
                UpdateFrames();
            };
            Root.IsVisibleChanged += (_, _) => UpdateFrames();
        }

        // Reads the original page every refresh.
        internal void Pull()
        {
            if (_disposed) return;
            string version = _page.LabCurrentVersion.Text;

            FillSettings();
            _search.Content = _owner.Project(_page.TextSearchVersion);
            if (_search.Content is FrameworkElement search) search.Margin = new Thickness(0);
            _rows.Content = _owner.Project(_page.PanVersions);
            if (_rows.Content is FrameworkElement rows) rows.Margin = new Thickness(0);

            bool loading = _page.Loading.Visibility == Visibility.Visible;
            if (_wasLoading && !loading) _checkedTime = DateTime.Now;
            _wasLoading = loading;
            JObject? newest = _page.LatestUpdateRelease;
            string tag = newest?["tag_name"]?.ToString() ?? "";
            if (!ReferenceEquals(_newest, newest))
            {
                _newest = newest;
                if (newest is not null) ShowRelease(newest, tag);
            }
            bool failed = !loading && _page.HintStatus.Text.StartsWith("读取版本列表失败", StringComparison.Ordinal);
            State state = loading ? State.Checking : failed ? State.Failed : _newest is not null ? State.Available
                : version == "dev_edition" ? State.Development : State.Latest;

            _check.IsEnabled = _page.BtnRefresh.IsEnabled;
            _check.Content = loading ? "正在检查…" : state == State.Available ? "重新检查" : "检查更新";
            _checkedAt.Text = _checkedTime is { } at ? "上次检查 · 今天 " + at.ToString("HH:mm") : "";
            _count.Text = loading ? "" : _page.LabResultCount.Text;
            _listLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = _page.LabNoResults.Text;
            _empty.Visibility = _page.LabNoResults.Visibility;

            // The halo shows the mark of the version about to arrive, otherwise the installed one.
            string markFor = state == State.Available ? tag : version;
            if (markFor != _markVersion) { _markVersion = markFor; _logo.Content = Mark(markFor, Logo); }
            ApplyState(state, version);
        }

        private void ApplyState(State state, string version)
        {
            _subtitle.Text = version;
            _detail.Text = state switch
            {
                State.Checking => "正在连接更新源",
                State.Available => _newestMeta,
                State.Failed => _page.HintStatus.Text,
                State.Development => "开发构建不会自动更新，可在版本历史中选择正式发布",
                _ => "当前更新通道没有更新的版本",
            };
            if (_state == state) return;
            bool first = _state is null;
            State? previous = _state;
            _state = state;
            bool snap = !MotionPreferences.AllowsAnimation;

            string headline = state switch
            {
                State.Checking => "正在检查更新",
                State.Available => "发现新版本",
                State.Failed => "检查更新未完成",
                State.Development => "本机开发构建",
                _ => "已是最新版本",
            };
            // Headline: the old one lifts away, the new one springs up from below.
            if (first) { _titles[_titleFront].Text = headline; _titleMotion.Snap(1); }
            else
            {
                _titleFront = 1 - _titleFront;
                _titles[_titleFront].Text = headline;
                _titleMotion.Snap(0); _titleMotion.Retarget(1, snap);
            }
            _titles[_titleFront].SetResourceReference(TextBlock.ForegroundProperty, state == State.Failed ? "GlassPresentationDanger" : "GlassPresentationText");

            if (state == State.Checking)
            {
                // An arc orbits the mark and the glow breathes until the result lands.
                _settling = false;
                _badgeMotion.Retarget(0, snap);
                _sweep.Snap(.04); _sweep.Retarget(.28, snap);
                _arcPresence.Retarget(1, snap);
                _breathAmount = 1;
            }
            else
            {
                _breathAmount = 0;
                SetBadge(state);
                if (previous == State.Checking && !snap)
                {
                    // The orbiting arc closes into a full ring, then hands over to the badge and a ripple.
                    _settling = true;
                    _sweep.Retarget(1);
                }
                else
                {
                    _arcPresence.Retarget(0, snap);
                    if (HasBadge(state)) { _badgeMotion.Snap(snap ? 1 : 0); _badgeMotion.Retarget(1, snap); }
                }
            }
            bool available = state == State.Available;
            if (available) _install.Visibility = Visibility.Visible;
            _installMotion.Retarget(available ? 1 : 0, snap);
            if (available) _notesCard.Visibility = Visibility.Visible;
            _notesMotion.Retarget(available ? 1 : 0, snap);
            UpdateFrames();
        }

        private static bool HasBadge(State state) => state is State.Latest or State.Available or State.Failed;

        private void SetBadge(State state)
        {
            string? glyph = state switch { State.Latest => CheckGlyph, State.Available => ArrowGlyph, State.Failed => AlertGlyph, _ => null };
            if (glyph is null) { _badgeMotion.Retarget(0, !MotionPreferences.AllowsAnimation); return; }
            _badgeGlyph.Data = Geometry.Parse(glyph);
            _badgeFace.SetResourceReference(Shape.FillProperty, state == State.Failed ? "GlassPresentationDanger" : "GlassPresentationAccent");
        }

        // The ring is complete: the badge pops, two ripples spread and the glow blooms once.
        private void Celebrate()
        {
            if (_state is not { } state || !HasBadge(state)) return;
            _badgeMotion.Snap(0); _badgeMotion.Retarget(1);
            for (int index = 0; index < _ripples.Length; index++)
            {
                Ellipse ripple = _ripples[index];
                var delay = TimeSpan.FromMilliseconds(index * 180);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var grow = new DoubleAnimation(1, 1.32, TimeSpan.FromMilliseconds(900)) { BeginTime = delay, EasingFunction = ease };
                var fade = new DoubleAnimation(.55, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = delay, EasingFunction = ease };
                var scale = (ScaleTransform)ripple.RenderTransform;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                ripple.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            _logoMotion.Snap(.94); _logoMotion.Retarget(1);
            _heroMotion.Snap(.975); _heroMotion.Retarget(1);
        }

        // ---------- Frame loop: orbit and breathing ----------

        private void UpdateFrames()
        {
            bool want = !_disposed && Root.IsVisible && MotionPreferences.AllowsAnimation && (_state == State.Checking || _spinSpeed > 1 || _breath > .002);
            if (want == _hooked) return;
            _hooked = want;
            _lastFrame = null;
            if (want) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs args)
        {
            if (args is not RenderingEventArgs frame) return;
            if (_lastFrame == frame.RenderingTime) return;
            double dt = _lastFrame is { } last ? Math.Clamp((frame.RenderingTime - last).TotalSeconds, 0, .05) : 1 / 60d;
            _lastFrame = frame.RenderingTime;
            bool checking = _state == State.Checking;
            // The orbit eases up to speed and, when the check ends, glides to a stop as the ring closes.
            double targetSpeed = checking ? 320 : 0;
            _spinSpeed += (targetSpeed - _spinSpeed) * (1 - Math.Exp(-dt / (checking ? .35 : .45)));
            _arcTurn.Angle = (_arcTurn.Angle + _spinSpeed * dt) % 360;
            _breath += (_breathAmount - _breath) * (1 - Math.Exp(-dt / .4));
            double pulse = Math.Sin(frame.RenderingTime.TotalSeconds * Math.PI / 1.3);
            _glowScale.ScaleX = _glowScale.ScaleY = 1 + .06 * _breath * pulse;
            _glow.Opacity = .55 + .25 * _breath * (.5 + .5 * pulse);
            if (!checking && _spinSpeed < 1 && _breath < .002) { _spinSpeed = 0; _breath = 0; _glowScale.ScaleX = _glowScale.ScaleY = 1; _glow.Opacity = .55; UpdateFrames(); }
        }

        // ---------- Content ----------

        private string _newestMeta = "";

        private void ShowRelease(JObject release, string tag)
        {
            string body = release["body"]?.ToString() ?? "";
            long size = (release["assets"] as JArray)?.OfType<JObject>()
                .Where(asset => asset["name"]?.ToString().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                .Select(asset => (long?)asset["size"] ?? 0).DefaultIfEmpty(0).Max() ?? 0;
            bool preview = (bool?)release["prerelease"] == true;
            string date = release["published_at"]?.ToString() is { Length: >= 10 } published ? " · " + published.Substring(0, 10) : "";
            _newestMeta = "新版本 " + tag + " · " + (preview ? "预发布版" : "正式版") + (size > 0 ? " · " + Size(size) : "") + date;
            _notesTitle.Text = tag + " 更新内容";
            _notes.Text = Preview(body);
            _install.ToolTip = "下载 " + tag + " 并更新启动器";
        }

        private void ShowNotes()
        {
            if (_newest is not JObject release || !ReferenceEquals(release, _page.LatestUpdateRelease)) return;
            ModMain.MyMsgBoxContent("点击“下载并更新”即可下载此版本并更新启动器。", release["tag_name"] + " 更新说明", () => new ReleaseNotesView(release["body"]?.ToString()));
        }

        private void FillSettings()
        {
            UIElement? source = _owner.Project(_page.ComboSource), channel = _owner.Project(_page.ComboChannel);
            if (_settings.Children.Count > 0) return;
            _settings.Children.Add(SettingRow("更新源", "选择从哪里下载启动器更新", source));
            _settings.Children.Add(Divider());
            _settings.Children.Add(SettingRow("更新通道", "预发布版可能包含尚未完成验证的改动", channel));
            _settings.Children.Add(Divider());
            var open = new Button { Content = "打开", Style = _owner.Style("GlassPresentationButton"), MinWidth = 96, Padding = new Thickness(14, 0, 14, 0) };
            open.Click += (_, _) => { if (_owner.Live(open)) _page.BtnReleasePage.PerformClick(); };
            AutomationProperties.SetName(open, "打开发布页");
            _settings.Children.Add(SettingRow("发布页", "在浏览器中查看全部发布", open));
        }

        private FrameworkElement SettingRow(string title, string hint, UIElement? editor)
        {
            var row = new Grid { MinHeight = 60 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
            var label = _owner.PlainText(title); label.FontSize = 14;
            var note = _owner.PlainText(hint); note.FontSize = 12; note.Margin = new Thickness(0, 2, 0, 0); note.TextWrapping = TextWrapping.Wrap;
            note.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            text.Children.Add(label); text.Children.Add(note);
            row.Children.Add(text);
            if (editor is FrameworkElement element)
            {
                if (element.Parent is Panel previous) previous.Children.Remove(element);
                else if (element.Parent is ContentControl holder) holder.Content = null;
                element.Margin = new Thickness(0);
                element.VerticalAlignment = VerticalAlignment.Center;
                if (element is ComboBox) element.Width = 220;
                Grid.SetColumn(element, 1); row.Children.Add(element);
            }
            return row;
        }

        private static Border Divider()
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
            return line;
        }

        public void Dispose()
        {
            _disposed = true;
            UpdateFrames();
            foreach (SpringMotion motion in new[] { _sweep, _arcPresence, _badgeMotion, _titleMotion, _notesMotion, _logoMotion, _installMotion, _heroMotion }) motion.Dispose();
        }

        // ---------- Building blocks ----------

        private static FrameworkElement Plate(UIElement child, Thickness margin)
        {
            GlassSurface plate = GlassPresentationSession.Plate(child, margin);
            plate.CornerRadius = new CornerRadius(20);
            plate.Padding = new Thickness(24, 20, 24, 22);
            return plate;
        }

        // The launcher's mark for a version: the WPF edition's mark in the theme's text colour, or the classic icon.
        private FrameworkElement Mark(string version, double size)
        {
            if (LauncherEdition.IsWpf(version) && LauncherEdition.WpfMark is { } mark)
            {
                var path = new Path { Data = mark, Stretch = Stretch.Uniform, Width = size, Height = size };
                path.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
                return path;
            }
            var image = new Image { Source = _page.ImgLauncher.Source, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        // Rounded pill button that gives a little under the finger (spring).
        internal static Button Pill(string text, bool primary, bool readableWhenDisabled = false)
        {
            var surface = new FrameworkElementFactory(typeof(Border), "Surface");
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
            surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var label = new FrameworkElementFactory(typeof(TextBlock), "Label");
            label.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            label.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            label.SetValue(TextBlock.FontFamilyProperty, new TemplateBindingExtension(Control.FontFamilyProperty));
            label.SetValue(TextBlock.FontSizeProperty, new TemplateBindingExtension(Control.FontSizeProperty));
            label.SetValue(TextBlock.FontWeightProperty, new TemplateBindingExtension(Control.FontWeightProperty));
            label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(label);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, .88, "Surface"));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            if (readableWhenDisabled)
            {
                disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("GlassPresentationField"), "Surface"));
                disabled.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("GlassPresentationControlStroke"), "Surface"));
                disabled.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                    new DynamicResourceExtension("GlassPresentationSecondary"), "Label"));
                disabled.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Arrow));
            }
            else disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .55));
            template.Triggers.Add(hover); template.Triggers.Add(disabled);
            var scale = new ScaleTransform(1, 1);
            var button = new Button { Template = template, Content = text, Height = 44, MinWidth = primary ? 196 : 150, Padding = new Thickness(24, 0, 24, 0), FontSize = 15,
                FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale };
            button.SetResourceReference(Control.FontFamilyProperty, "GlassPresentationFont");
            button.SetResourceReference(Control.BackgroundProperty, primary ? "GlassPresentationAccent" : "GlassPresentationPrimaryFill");
            button.SetResourceReference(Control.ForegroundProperty, primary ? "GlassPresentationOnAccent" : "GlassPresentationText");
            button.SetResourceReference(Control.BorderBrushProperty, primary ? "GlassPresentationAccentStroke" : "GlassPresentationPrimaryEdge");
            var press = new SpringMotion(button, 1, value => scale.ScaleX = scale.ScaleY = value, new SpringParameters(.3, .58));
            button.PreviewMouseLeftButtonDown += (_, _) => press.Retarget(.94, !MotionPreferences.AllowsAnimation);
            button.PreviewMouseLeftButtonUp += (_, _) => press.Retarget(1, !MotionPreferences.AllowsAnimation);
            button.MouseLeave += (_, _) => press.Retarget(1, !MotionPreferences.AllowsAnimation);
            AutomationProperties.SetName(button, text);
            return button;
        }

        // An arc of the ring, starting at 12 o'clock and running clockwise for `share` of the circle.
        private static Geometry Arc(double share)
        {
            double angle = share * 2 * Math.PI, centre = Halo / 2;
            var figure = new PathFigure { StartPoint = new Point(centre, centre - Ring) };
            figure.Segments.Add(new ArcSegment(new Point(centre + Ring * Math.Sin(angle), centre - Ring * Math.Cos(angle)), new Size(Ring, Ring), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            // Keep the full halo square as the bounds so the arc sits on the ring.
            var group = new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(0, 0)), new LineGeometry(new Point(Halo, Halo), new Point(Halo, Halo)), new PathGeometry(new[] { figure }) } };
            group.Freeze();
            return group;
        }

        private static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576d:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        // A plain-text preview of the Markdown notes: headings and list marks removed, the first lines kept.
        private static string Preview(string markdown)
        {
            var lines = new List<string>();
            foreach (string raw in markdown.Replace("\r", "").Split('\n'))
            {
                string line = Regex.Replace(raw.Trim(), @"^(#{1,6}\s*|[-*+]\s+|\d+\.\s+|>\s*)", "");
                line = Regex.Replace(line, @"\[(.*?)\]\(.*?\)", "$1");
                line = Regex.Replace(line, @"\*\*|__|`", "");
                if (line.Length == 0) continue;
                string start = raw.TrimStart();
                lines.Add(start.StartsWith("-") || start.StartsWith("*") || start.StartsWith("+") ? "·  " + line : line);
                if (lines.Count >= 10) break;
            }
            return lines.Count == 0 ? "此版本没有附带更新说明。" : string.Join("\n", lines);
        }
    }
}

/// <summary>Which launcher edition a release belongs to, and the WPF edition's mark.</summary>
internal static class LauncherEdition
{
    private static Geometry? _mark;
    private static bool _loaded;

    /// <summary>The WPF edition: development builds and releases from 1.6.0 on (earlier ones are the classic edition).</summary>
    internal static bool IsWpf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        string text = version.Trim();
        if (text.Equals("dev_edition", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
        int dash = text.IndexOf('-');
        if (dash >= 0) text = text.Substring(0, dash);
        return Version.TryParse(text.Split('.').Length == 1 ? text + ".0" : text, out Version? parsed) && parsed >= new Version(1, 6);
    }

    /// <summary>The WPF edition's mark (from PCLLF.Logo.xaml), to be filled with the theme's text colour.</summary>
    internal static Geometry? WpfMark
    {
        get
        {
            if (_loaded) return _mark;
            _loaded = true;
            try
            {
                var drawing = (DrawingImage)Application.LoadComponent(new Uri("/LivingPCL;component/Images/Branding/PCLLF.Logo.xaml", UriKind.Relative));
                var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
                // Keep the full 1254 square (the transparent frame) so the mark keeps its own margins when stretched.
                foreach (GeometryDrawing part in ((DrawingGroup)drawing.Drawing).Children.OfType<GeometryDrawing>())
                    group.Children.Add(part.Brush is SolidColorBrush { Color.A: 0 } ? Frame(part.Geometry.Bounds) : part.Geometry.Clone());
                group.Freeze();
                _mark = group;
            }
            catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or NotSupportedException or InvalidCastException) { _mark = null; }
            return _mark;
        }
    }

    // A frame that sets the bounds without painting: two zero-area points at opposite corners.
    private static Geometry Frame(Rect bounds) => new GeometryGroup { Children = {
        new LineGeometry(bounds.TopLeft, bounds.TopLeft), new LineGeometry(bounds.BottomRight, bounds.BottomRight) } };
}
