// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

public sealed partial class GlassPresentationSession
{
    /// <summary>
    /// Terracotta lobby. One hero carries the whole session: a network mark in a halo (an arc orbits it while the helper
    /// works, a badge lands when a room is up or something fails), a status chip, the headline, and right below it the
    /// one thing to do next — the enable action, the room-code field, or the code to share with a copy button.
    /// A guide follows the hero: the three steps to host (instance, LAN world, room) or to join, each marking itself done
    /// from live launcher state; inside a room it becomes the member list. Attribution and links close the page.
    /// All state is read from <see cref="TerracottaLobbyPage"/>, and every action goes back through it.
    /// </summary>
    private Node ProjectTerracottaLobby(TerracottaLobbyPage page)
    {
        var view = new TerracottaLobbyView(this, page);
        var node = new Node(new ScrollEdgeFade(view.Root));
        node.Pull = view.Pull;
        node.Cleanup.Add(view.Dispose);
        return node;
    }

    private sealed class TerracottaLobbyView : IDisposable
    {
        private const double Halo = 136, Ring = Halo / 2 - 12;
        private const string CheckGlyph = "M6.5,12.5 L10.5,16.5 L17.5,8";
        private const string AlertGlyph = "M12,6.5 V13 M12,17 V17.1";
        private static readonly Geometry NetworkGlyph = BuildNetworkGlyph();
        private static readonly Geometry OrbitArc = Arc(.26);

        private readonly GlassPresentationSession _owner;
        private readonly TerracottaLobbyPage _page;
        internal ScrollViewer Root { get; }
        private readonly StackPanel _stack = new() { Margin = new Thickness(0, 6, 0, 18) };

        // ---- Hero ----
        private readonly StackPanel _hero = new() { Margin = new Thickness(0, 14, 0, 6) };
        private readonly Grid _halo = new() { Width = Halo, Height = Halo, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        private readonly Ellipse _glow = new();
        private readonly ScaleTransform _glowScale = new(1, 1);
        private readonly Path _arc = new() { StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0, Data = OrbitArc };
        private readonly RotateTransform _arcTurn = new();
        private readonly Path _mark = new() { Data = NetworkGlyph, Width = 54, Height = 54, Stretch = Stretch.Uniform, StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransformOrigin = new Point(.5, .5) };
        private readonly ScaleTransform _markScale = new(1, 1);
        private readonly Grid _badge = new() { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 18), RenderTransformOrigin = new Point(.5, .5) };
        private readonly Ellipse _badgeFace = new() { StrokeThickness = 3 };
        private readonly Path _badgeGlyph = new() { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        private readonly ScaleTransform _badgeScale = new(0, 0);
        private readonly Border _chip = new() { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 12, 4), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1) };
        private readonly Ellipse _chipDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _chipText, _title, _detail, _error;
        private readonly TranslateTransform _titleShift = new();

        // ---- Hero: share ticket (in a room) and code field (join) ----
        private readonly Border _ticket = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 12, 12, 12), Margin = new Thickness(0, 20, 0, 0), MaxWidth = 500, BorderThickness = new Thickness(1) };
        private readonly TextBlock _shareLabel, _shareText;
        private readonly Button _copy;
        private DateTime _copiedAt = DateTime.MinValue;
        private readonly Grid _codeRow = new() { Margin = new Thickness(0, 20, 0, 0), MaxWidth = 500 };
        private readonly TextBox _codeBox;
        private readonly Button _paste;

        // ---- Hero: actions ----
        private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        private readonly Button _enable, _create, _join, _cancel, _back, _launchGuest, _leave;

        // ---- Guide and members ----
        private readonly FrameworkElement _hostGuide, _joinGuide, _membersPlate;
        private readonly StepRow[] _hostSteps, _joinSteps;
        private readonly TextBlock _membersCount;
        private readonly StackPanel _members = new();
        private string _membersSignature = "";

        // ---- Footer ----
        private readonly Button _uninstall;
        private readonly TextBlock _uninstallDot;

        // ---- Motion ----
        private readonly SpringMotion _arcPresence, _badgeMotion, _titleMotion, _markMotion, _flowMotion;
        private readonly ScaleTransform _flowScale = new(1, 1);
        private readonly TranslateTransform _flowShift = new();
        private TerracottaLobbyPage.LobbyFlow _shownFlow;
        private double _flowTravel, _flowStartScale = 1;
        private bool _showingLobby, _flowExiting, _swappingFlow;
        private DispatcherOperation? _flowSwap;
        private double _spinSpeed, _breath;
        private TimeSpan? _lastFrame;
        private bool _busy, _hooked, _primed, _disposed;
        private string? _sceneKey, _headline, _badgeKind;

        internal TerracottaLobbyView(GlassPresentationSession owner, TerracottaLobbyPage page)
        {
            _owner = owner; _page = page;
            Root = new ScrollViewer { Content = _stack, Style = owner.Style("GlassPresentationScrollViewer"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            SmoothScroll.SetIsEnabled(Root, true);

            // ---------- Halo ----------
            _glow.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
            _glow.OpacityMask = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(120, 0, 0, 0), 0), new GradientStop(Color.FromArgb(44, 0, 0, 0), .55), new GradientStop(Colors.Transparent, 1) } };
            _glow.RenderTransformOrigin = new Point(.5, .5); _glow.RenderTransform = _glowScale; _glow.Opacity = .55;
            _halo.Children.Add(_glow);
            var track = new Ellipse { Width = Ring * 2, Height = Ring * 2, StrokeThickness = 3.5, Opacity = .5 };
            track.SetResourceReference(Shape.StrokeProperty, "GlassPresentationTrack");
            _halo.Children.Add(track);
            _arc.SetResourceReference(Shape.StrokeProperty, "GlassPresentationAccent");
            _arc.RenderTransformOrigin = new Point(.5, .5); _arc.RenderTransform = _arcTurn;
            _halo.Children.Add(_arc);
            _mark.SetResourceReference(Shape.StrokeProperty, "GlassPresentationText");
            _mark.RenderTransform = _markScale;
            _halo.Children.Add(_mark);
            _badgeFace.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
            _badgeFace.SetResourceReference(Shape.StrokeProperty, "GlassPresentationFill");
            _badgeGlyph.SetResourceReference(Shape.StrokeProperty, "GlassPresentationOnAccent");
            _badge.Children.Add(_badgeFace); _badge.Children.Add(_badgeGlyph);
            _badge.RenderTransform = _badgeScale;
            _halo.Children.Add(_badge);

            // ---------- Chip, headline, detail ----------
            _chip.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
            _chip.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
            _chipText = owner.PlainText(""); _chipText.FontSize = 12;
            _chipText.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _chip.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _chipDot, _chipText } };

            _title = owner.PlainText(""); _title.FontSize = 22; _title.FontWeight = FontWeights.SemiBold; _title.TextAlignment = TextAlignment.Center;
            _title.TextWrapping = TextWrapping.Wrap; _title.Margin = new Thickness(0, 12, 0, 0); _title.RenderTransform = _titleShift;
            _title.SetResourceReference(TextBlock.FontFamilyProperty, "GlassPresentationDisplayFont");
            _detail = owner.PlainText(""); _detail.FontSize = 13.5; _detail.TextAlignment = TextAlignment.Center; _detail.TextWrapping = TextWrapping.Wrap;
            _detail.Margin = new Thickness(24, 6, 24, 0); _detail.MaxWidth = 520; _detail.HorizontalAlignment = HorizontalAlignment.Center;
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _error = owner.PlainText(""); _error.FontSize = 13; _error.TextAlignment = TextAlignment.Center; _error.TextWrapping = TextWrapping.Wrap;
            _error.Margin = new Thickness(24, 8, 24, 0); _error.MaxWidth = 520; _error.HorizontalAlignment = HorizontalAlignment.Center;
            _error.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationDanger");

            // ---------- Share ticket ----------
            _ticket.SetResourceReference(Border.BackgroundProperty, "GlassPresentationField");
            _ticket.SetResourceReference(Border.BorderBrushProperty, "GlassPresentationEdge");
            var ticket = new Grid();
            ticket.ColumnDefinitions.Add(new ColumnDefinition());
            ticket.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _shareLabel = owner.PlainText(""); _shareLabel.FontSize = 12;
            _shareLabel.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _shareText = owner.PlainText(""); _shareText.FontSize = 21; _shareText.FontWeight = FontWeights.SemiBold; _shareText.TextWrapping = TextWrapping.Wrap;
            _shareText.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"); _shareText.Margin = new Thickness(0, 2, 0, 0);
            var shareStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _shareLabel, _shareText } };
            ticket.Children.Add(shareStack);
            _copy = Action("复制", () => { if (_page.CopyShare()) _copiedAt = DateTime.UtcNow; });
            _copy.MinWidth = 80; _copy.Height = 36; _copy.Margin = new Thickness(16, 0, 0, 0); _copy.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_copy, 1); ticket.Children.Add(_copy);
            _ticket.Child = ticket;

            // ---------- Code field ----------
            _codeRow.ColumnDefinitions.Add(new ColumnDefinition());
            _codeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _codeBox = new TextBox { Style = owner.Style("GlassPresentationTextBox"), Height = 44, FontSize = 15, MaxLength = 512 };
            AutomationProperties.SetName(_codeBox, "房间码");
            var placeholder = new TextBlock { Text = "粘贴房主分享的 U/ 开头房间码", Margin = new Thickness(15, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, FontSize = 14 };
            owner.TextStyle(placeholder); placeholder.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            BindTextPlaceholder(_codeBox, placeholder);
            _codeBox.TextChanged += (_, _) =>
            {
                if (_page.Code == _codeBox.Text) return;
                _page.Code = _codeBox.Text;
                _owner.QueueRefresh();
            };
            _codeBox.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter || !_join.IsEnabled) return;
                args.Handled = true;
                _ = _page.JoinAsync();
            };
            var field = new Grid { Children = { _codeBox, placeholder } };
            _codeRow.Children.Add(field);
            _paste = Action("粘贴", _page.PasteCode);
            _paste.MinWidth = 80; _paste.Height = 44; _paste.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(_paste, 1); _codeRow.Children.Add(_paste);

            // ---------- Actions ----------
            _enable = Primary("下载并启用", () => _ = _page.EnableAsync());
            _create = Primary("创建房间", () => _ = _page.CreateAsync());
            _join = Primary("加入房间", () => _ = _page.JoinAsync());
            _back = Primary("返回大厅", () => _ = _page.LeaveAsync());
            _launchGuest = Primary("启动游戏", _page.LaunchGame);
            _cancel = Secondary("取消连接", () => _ = _page.LeaveAsync());
            _leave = Secondary("退出房间", () => _ = _page.LeaveAsync());
            foreach (Button action in new[] { _enable, _create, _join, _back, _launchGuest, _cancel, _leave }) _actions.Children.Add(action);

            foreach (UIElement part in new UIElement[] { _halo, _chip, _title, _detail, _error, _ticket, _codeRow, _actions }) _hero.Children.Add(part);
            _stack.Children.Add(HeroPlate(_hero));

            // ---------- Host guide ----------
            _hostSteps = new[] {
                new StepRow(owner, 1, "选择游戏实例", Action("选择实例", _page.ChooseInstance)),
                new StepRow(owner, 2, "进入存档并对局域网开放", Action("启动游戏", _page.LaunchGame)),
                new StepRow(owner, 3, "创建房间并分享房间码", null)
            };
            _hostGuide = GuidePlate("开房准备", "按顺序完成，陶瓦会自动找到你对局域网开放的世界。", _hostSteps);

            // ---------- Join guide ----------
            _joinSteps = new[] {
                new StepRow(owner, 1, "获取房间码", null),
                new StepRow(owner, 2, "粘贴并加入", null),
                new StepRow(owner, 3, "进入多人游戏", Action("启动游戏", _page.LaunchGame))
            };
            _joinGuide = GuidePlate("加入好友的房间", "支持独立陶瓦、HMCL 与 PCL CE 创建的标准 U/ 房间码。", _joinSteps);

            // ---------- Members ----------
            var members = new StackPanel();
            var membersHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            membersHeader.ColumnDefinitions.Add(new ColumnDefinition());
            membersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var membersTitle = owner.PlainText("房间成员"); membersTitle.FontSize = 16; membersTitle.FontWeight = FontWeights.SemiBold;
            _membersCount = owner.PlainText(""); _membersCount.FontSize = 12; _membersCount.VerticalAlignment = VerticalAlignment.Center;
            _membersCount.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            Grid.SetColumn(_membersCount, 1);
            membersHeader.Children.Add(membersTitle); membersHeader.Children.Add(_membersCount);
            members.Children.Add(membersHeader); members.Children.Add(_members);
            _membersPlate = SectionPlate(members);

            _stack.Children.Add(_hostGuide); _stack.Children.Add(_joinGuide); _stack.Children.Add(_membersPlate);

            // ---------- Footer ----------
            var attribution = owner.PlainText(TerracottaLobbyPage.Attribution); attribution.FontSize = 12; attribution.HorizontalAlignment = HorizontalAlignment.Center;
            attribution.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            _uninstall = Link("卸载组件", () => _ = _page.RequestUninstallAsync());
            _uninstallDot = Dot();
            var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
                Children = { Link("联机帮助", TerracottaLobbyPage.OpenHelp), Dot(), Link("项目与许可", TerracottaLobbyPage.OpenLicense), _uninstallDot, _uninstall } };
            _stack.Children.Add(new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children = { attribution, links } });

            // ---------- Motion ----------
            bool snap() => !MotionPreferences.AllowsAnimation;
            _arcPresence = new SpringMotion(_arc, 0, value => { _arc.Opacity = Math.Clamp(value, 0, 1); UpdateFrames(); }, new SpringParameters(.4, 1));
            _badgeMotion = new SpringMotion(_badge, 0, value => _badgeScale.ScaleX = _badgeScale.ScaleY = Math.Max(0, value), new SpringParameters(.46, .52));
            _titleMotion = new SpringMotion(_title, 1, value =>
            {
                _title.Opacity = Math.Clamp(value, 0, 1);
                _titleShift.Y = 14 * (1 - value);
            }, new SpringParameters(.48, .74));
            _markMotion = new SpringMotion(_mark, 1, value => _markScale.ScaleX = _markScale.ScaleY = value, new SpringParameters(.6, .62));
            _stack.RenderTransformOrigin = new Point(.5, .42);
            _stack.RenderTransform = new TransformGroup { Children = { _flowScale, _flowShift } };
            _flowMotion = new SpringMotion(_stack, 1, ApplyFlowPose, GlassLauncherView.PageSpring) { MaxFrameSeconds = GlassLauncherView.TransitionFrameCap };

            _page.Changed += OnPageChanged;
            Root.Loaded += (_, _) =>
            {
                if (_primed) return;
                _primed = true;
                _markMotion.Snap(.6); _markMotion.Retarget(1, snap());
                StaggeredEntrance.Play(_stack.Children.Cast<UIElement>().Concat(_hero.Children.Cast<UIElement>().Skip(1)));
                UpdateFrames();
            };
            Root.IsVisibleChanged += (_, _) => UpdateFrames();
        }

        private void OnPageChanged()
        {
            if (!_disposed) _owner.QueueRefresh();
        }

        // ---------- Reading the controller ----------

        internal void Pull()
        {
            if (_disposed) return;
            TerracottaLobbyPage.LobbyStage stage = _page.Stage;
            bool lobby = stage == TerracottaLobbyPage.LobbyStage.Lobby;
            if (!PrepareFlow(lobby)) return;
            TerracottaLobbyPage.LobbyFlow flow = _shownFlow;
            bool idle = _page.IsIdle, host = _page.IsHost;
            string? activity = _page.ActivityLabel, error = _page.ErrorText, share = _page.ShareValue;
            bool room = stage == TerracottaLobbyPage.LobbyStage.Room;
            bool joinFlow = lobby && flow == TerracottaLobbyPage.LobbyFlow.Join, createFlow = lobby && flow == TerracottaLobbyPage.LobbyFlow.Create;
            bool connecting = stage == TerracottaLobbyPage.LobbyStage.Connecting;
            bool cached = _page.ComponentCached;
            int players = _page.Players.Count;

            // Headline and detail.
            string headline = activity is not null && stage is TerracottaLobbyPage.LobbyStage.Setup ? "正在准备陶瓦联机" : stage switch
            {
                TerracottaLobbyPage.LobbyStage.Setup => "和好友一起联机",
                TerracottaLobbyPage.LobbyStage.Lobby => joinFlow ? "加入好友的世界" : "邀请好友来你的世界",
                TerracottaLobbyPage.LobbyStage.Connecting => host ? "正在开设房间" : "正在连接房主",
                TerracottaLobbyPage.LobbyStage.Room => host ? "房间已就绪" : "已加入房间",
                _ => "联机已中断"
            };
            string detail = activity ?? (lobby
                ? joinFlow ? "粘贴房主分享的房间码，即可加入对方的世界。" : "在游戏中对局域网开放存档后创建房间，再把房间码发给好友。"
                : _page.StatusText);
            SetHeadline(headline, stage == TerracottaLobbyPage.LobbyStage.Failed);
            _detail.Text = detail;
            _error.Text = error ?? "";
            _error.Visibility = Visible(!string.IsNullOrEmpty(error));

            // Status chip.
            _chip.Visibility = Visible(stage != TerracottaLobbyPage.LobbyStage.Setup);
            _chipText.Text = stage switch
            {
                TerracottaLobbyPage.LobbyStage.Lobby => "陶瓦联机已就绪",
                TerracottaLobbyPage.LobbyStage.Connecting => "连接中",
                TerracottaLobbyPage.LobbyStage.Room => players > 0 ? $"在线 · {players} 人" : "在线",
                _ => "已中断"
            };
            _chipDot.SetResourceReference(Shape.FillProperty, stage == TerracottaLobbyPage.LobbyStage.Failed ? "GlassPresentationDanger" :
                connecting ? "GlassPresentationSecondary" : "GlassPresentationAccent");

            // Halo: orbit while the helper works, badge for the outcome.
            SetBusy(activity is not null || connecting);
            SetBadge(room ? "check" : stage == TerracottaLobbyPage.LobbyStage.Failed || !string.IsNullOrEmpty(error) ? "alert" : null);

            // Share ticket.
            _ticket.Visibility = Visible(room && share is not null);
            _shareLabel.Text = host ? "房间码" : "直连地址";
            _shareText.Text = share ?? "";
            _copy.Content = DateTime.UtcNow - _copiedAt < TimeSpan.FromSeconds(1.6) ? "已复制" : "复制";
            _copy.IsEnabled = idle && share is not null;

            // Code field.
            _codeRow.Visibility = Visible(joinFlow);
            if (_codeBox.Text != _page.Code) { _codeBox.Text = _page.Code; _codeBox.CaretIndex = _codeBox.Text.Length; }
            _codeBox.IsEnabled = _paste.IsEnabled = joinFlow && idle;

            // Actions.
            _enable.Visibility = Visible(stage == TerracottaLobbyPage.LobbyStage.Setup);
            _enable.Content = cached ? "启用陶瓦联机" : "下载并启用";
            _enable.IsEnabled = idle;
            _create.Visibility = Visible(createFlow);
            _create.IsEnabled = idle;
            _join.Visibility = Visible(joinFlow);
            _join.IsEnabled = idle && !string.IsNullOrWhiteSpace(_page.Code);
            _cancel.Visibility = Visible(connecting);
            _cancel.IsEnabled = idle;
            _back.Visibility = Visible(stage == TerracottaLobbyPage.LobbyStage.Failed);
            _back.IsEnabled = idle;
            _launchGuest.Visibility = Visible(room && !host && !_page.GameRunning);
            _launchGuest.IsEnabled = idle && _page.CanLaunch;
            _leave.Visibility = Visible(room);
            _leave.IsEnabled = idle;
            _actions.Visibility = Visible(_actions.Children.Cast<UIElement>().Any(action => action.Visibility == Visibility.Visible));

            // Guides.
            bool hostGuide = createFlow || connecting && host;
            _hostGuide.Visibility = Visible(hostGuide);
            if (hostGuide) UpdateHostSteps(idle, connecting);
            bool joinGuide = joinFlow || connecting && !host;
            _joinGuide.Visibility = Visible(joinGuide);
            if (joinGuide) UpdateJoinSteps(idle, connecting);
            _membersPlate.Visibility = Visible(room && players > 0);
            if (room) UpdateMembers();

            // Footer.
            _uninstall.Visibility = _uninstallDot.Visibility = Visible(cached);
            _uninstall.IsEnabled = idle;

            // A new scene enters like a fresh page: the hero contents and the plates below rise in turn.
            string scene = $"{stage}:{(lobby ? flow : default)}:{host}";
            if (_sceneKey != scene)
            {
                bool first = _sceneKey is null;
                _sceneKey = scene;
                if (!first && _primed && !_swappingFlow)
                    StaggeredEntrance.Play(_hero.Children.Cast<UIElement>().Skip(1).Concat(_stack.Children.Cast<UIElement>().Skip(1)));
            }
        }

        private bool CanAnimateFlow => _primed && Root.IsVisible && _owner.Live(Root) && MotionPreferences.AllowsAnimation &&
            Window.GetWindow(Root) is FormMain { GlassView.AnimateInnerPageChanges: true };

        private bool PrepareFlow(bool lobby)
        {
            bool samePageStage = _showingLobby && lobby;
            _showingLobby = lobby;
            if (!samePageStage || !CanAnimateFlow)
            {
                _shownFlow = _page.Flow;
                if (_flowExiting || _flowMotion.IsRunning)
                {
                    CancelFlowExit();
                    _flowMotion.Snap(1);
                }
                return true;
            }
            if (_shownFlow == _page.Flow)
            {
                if (_flowExiting)
                {
                    CancelFlowExit();
                    _flowMotion.Retarget(1);
                }
                return true;
            }
            if (!_flowExiting)
            {
                if (_flowMotion.Value >= .999)
                {
                    _flowTravel = -Direction(_page.Flow) * GlassLauncherView.PageExitTravel;
                    _flowStartScale = GlassLauncherView.PageExitScale;
                }
                _flowExiting = true;
                _stack.IsHitTestVisible = false;
                if (_stack.IsKeyboardFocusWithin) Keyboard.ClearFocus();
                _flowMotion.Retarget(0);
            }
            return false;
        }

        private static int Direction(TerracottaLobbyPage.LobbyFlow flow) => flow == TerracottaLobbyPage.LobbyFlow.Join ? 1 : -1;

        private void ApplyFlowPose(double value)
        {
            double p = Math.Clamp(value, 0, 1);
            _stack.Opacity = p * p * (3 - 2 * p);
            _flowScale.ScaleX = _flowScale.ScaleY = _flowStartScale + (1 - _flowStartScale) * p;
            _flowShift.Y = _flowTravel * (1 - p);
            if (_flowExiting && p <= MotionTokens.VisibilityThreshold && _flowSwap is null)
                _flowSwap = Root.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(FinishFlowExit));
        }

        private void CancelFlowExit()
        {
            _flowSwap?.Abort();
            _flowSwap = null;
            _flowExiting = false;
            _stack.IsHitTestVisible = true;
        }

        private void FinishFlowExit()
        {
            _flowSwap = null;
            if (_disposed || !_flowExiting) return;
            CancelFlowExit();
            bool animate = CanAnimateFlow && _page.Stage == TerracottaLobbyPage.LobbyStage.Lobby && _shownFlow != _page.Flow;
            if (!animate)
            {
                _owner.Refresh();
                if (!_disposed) _flowMotion.Retarget(1, !CanAnimateFlow);
                return;
            }
            _shownFlow = _page.Flow;
            _flowTravel = Direction(_shownFlow) * GlassLauncherView.PageRise;
            _flowStartScale = MotionTokens.RevealScale;
            _flowMotion.Snap(0);
            _swappingFlow = true;
            try { _owner.Refresh(); }
            finally { _swappingFlow = false; }
            if (!_disposed) _flowMotion.Retarget(1, !CanAnimateFlow);
        }

        private void UpdateHostSteps(bool idle, bool scanning)
        {
            string? instance = _page.InstanceName;
            bool running = _page.GameRunning, chosen = instance is not null;
            int current = scanning ? 3 : !chosen ? 1 : !running ? 2 : 3;
            _hostSteps[0].Show(chosen ? StepState.Done : StepState.Current, chosen ? "当前实例 · " + instance : "尚未选择游戏实例", true,
                idle && _page.CanChooseInstance);
            _hostSteps[1].Show(running ? StepState.Done : current == 2 ? StepState.Current : StepState.Pending,
                running ? "游戏运行中 · 进入存档后按 Esc，选择「对局域网开放」" : "启动游戏，进入存档后按 Esc，选择「对局域网开放」",
                !running, idle && _page.CanLaunch);
            _hostSteps[2].Show(current == 3 ? StepState.Current : StepState.Pending,
                scanning ? _page.StatusText : "点击上方“创建房间”，陶瓦会生成房间码", false, false);
        }

        private void UpdateJoinSteps(bool idle, bool connecting)
        {
            bool hasCode = !string.IsNullOrWhiteSpace(_page.Code), running = _page.GameRunning;
            _joinSteps[0].Show(hasCode || connecting ? StepState.Done : StepState.Current, "向房主索要以 U/ 开头的房间码", false, false);
            _joinSteps[1].Show(connecting ? StepState.Current : hasCode ? StepState.Current : StepState.Pending,
                connecting ? _page.StatusText : "粘贴到上方输入框，点击“加入房间”", false, false);
            _joinSteps[2].Show(running ? StepState.Done : StepState.Pending,
                running ? "游戏运行中 · 加入后在多人游戏列表中进入房间" : "加入后启动游戏，在多人游戏列表中进入房间", !running, idle && _page.CanLaunch);
        }

        private void UpdateMembers()
        {
            IReadOnlyList<TerracottaPlayer> players = _page.Players;
            _membersCount.Text = players.Count + " 人";
            string signature = string.Join("\n", players.Select(player => player.Name + "\t" + player.Kind + "\t" + player.Vendor));
            if (signature == _membersSignature) return;
            _membersSignature = signature;
            _members.Children.Clear();
            for (int index = 0; index < players.Count; index++)
            {
                if (index > 0) _members.Children.Add(Divider());
                _members.Children.Add(MemberRow(players[index]));
            }
        }

        private FrameworkElement MemberRow(TerracottaPlayer player)
        {
            string name = string.IsNullOrWhiteSpace(player.Name) ? "玩家" : player.Name.Trim();
            bool hostRow = player.Kind == "HOST";
            string role = hostRow ? "房主" : player.Kind == "LOCAL" ? "本机" : "成员";
            var row = new Grid { MinHeight = 54 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var disc = new Ellipse { Opacity = hostRow ? .22 : .12 };
            disc.SetResourceReference(Shape.FillProperty, hostRow ? "GlassPresentationAccent" : "GlassPresentationText");
            var initial = _owner.PlainText(StringInfo.GetNextTextElement(name).ToUpperInvariant());
            initial.FontSize = 14; initial.FontWeight = FontWeights.SemiBold; initial.HorizontalAlignment = HorizontalAlignment.Center; initial.VerticalAlignment = VerticalAlignment.Center;
            if (hostRow) initial.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationAccent");
            avatar.Children.Add(disc); avatar.Children.Add(initial);
            row.Children.Add(avatar);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
            var label = _owner.PlainText(name); label.FontSize = 14; label.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(label);
            if (!string.IsNullOrWhiteSpace(player.Vendor))
            {
                var vendor = _owner.PlainText(player.Vendor); vendor.FontSize = 12; vendor.TextTrimming = TextTrimming.CharacterEllipsis; vendor.Margin = new Thickness(0, 1, 0, 0);
                vendor.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
                text.Children.Add(vendor);
            }
            Grid.SetColumn(text, 1); row.Children.Add(text);

            var tag = new Grid { VerticalAlignment = VerticalAlignment.Center };
            var tagFill = new Border { CornerRadius = new CornerRadius(10), Opacity = hostRow ? .16 : .08 };
            tagFill.SetResourceReference(Border.BackgroundProperty, hostRow ? "GlassPresentationAccent" : "GlassPresentationText");
            var tagText = _owner.PlainText(role); tagText.FontSize = 12; tagText.Margin = new Thickness(10, 2, 10, 3);
            tagText.SetResourceReference(TextBlock.ForegroundProperty, hostRow ? "GlassPresentationAccent" : "GlassPresentationSecondary");
            tag.Children.Add(tagFill); tag.Children.Add(tagText);
            Grid.SetColumn(tag, 2); row.Children.Add(tag);
            return row;
        }

        // ---------- Hero state ----------

        private void SetHeadline(string text, bool danger)
        {
            _title.SetResourceReference(TextBlock.ForegroundProperty, danger ? "GlassPresentationDanger" : "GlassPresentationText");
            if (_headline == text) return;
            bool first = _headline is null;
            _headline = text;
            _title.Text = text;
            if (first || _swappingFlow) { _titleMotion.Snap(1); return; }
            _titleMotion.Snap(0); _titleMotion.Retarget(1, !MotionPreferences.AllowsAnimation);
        }

        private void SetBusy(bool busy)
        {
            if (_busy == busy) return;
            _busy = busy;
            _arcPresence.Retarget(busy ? 1 : 0, !MotionPreferences.AllowsAnimation);
            UpdateFrames();
        }

        private void SetBadge(string? kind)
        {
            if (_badgeKind == kind) return;
            _badgeKind = kind;
            bool snap = !MotionPreferences.AllowsAnimation;
            if (kind is null) { _badgeMotion.Retarget(0, snap); return; }
            _badgeGlyph.Data = Geometry.Parse(kind == "check" ? CheckGlyph : AlertGlyph);
            _badgeFace.SetResourceReference(Shape.FillProperty, kind == "check" ? "GlassPresentationAccent" : "GlassPresentationDanger");
            // The badge pops in and the mark answers with a small bounce.
            _badgeMotion.Snap(snap ? 1 : 0); _badgeMotion.Retarget(1, snap);
            if (!snap) { _markMotion.Snap(.9); _markMotion.Retarget(1); }
        }

        // ---------- Frame loop: orbit and breathing ----------

        private void UpdateFrames()
        {
            bool want = !_disposed && Root.IsVisible && MotionPreferences.AllowsAnimation && (_busy || _spinSpeed > 1 || _breath > .002);
            if (want == _hooked) return;
            _hooked = want;
            _lastFrame = null;
            if (want) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs args)
        {
            if (args is not RenderingEventArgs frame || _lastFrame == frame.RenderingTime) return;
            double dt = _lastFrame is { } last ? Math.Clamp((frame.RenderingTime - last).TotalSeconds, 0, .05) : 1 / 60d;
            _lastFrame = frame.RenderingTime;
            double targetSpeed = _busy ? 300 : 0;
            _spinSpeed += (targetSpeed - _spinSpeed) * (1 - Math.Exp(-dt / (_busy ? .35 : .5)));
            _arcTurn.Angle = (_arcTurn.Angle + _spinSpeed * dt) % 360;
            _breath += ((_busy ? 1 : 0) - _breath) * (1 - Math.Exp(-dt / .4));
            double pulse = Math.Sin(frame.RenderingTime.TotalSeconds * Math.PI / 1.3);
            _glowScale.ScaleX = _glowScale.ScaleY = 1 + .07 * _breath * pulse;
            _glow.Opacity = .55 + .25 * _breath * (.5 + .5 * pulse);
            if (!_busy && _spinSpeed < 1 && _breath < .002)
            {
                _spinSpeed = 0; _breath = 0; _glowScale.ScaleX = _glowScale.ScaleY = 1; _glow.Opacity = .55;
                UpdateFrames();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _flowSwap?.Abort();
            _page.Changed -= OnPageChanged;
            UpdateFrames();
            foreach (SpringMotion motion in new[] { _arcPresence, _badgeMotion, _titleMotion, _markMotion, _flowMotion }) motion.Dispose();
            foreach (StepRow step in _hostSteps.Concat(_joinSteps)) step.Dispose();
        }

        // ---------- Building blocks ----------

        private static FrameworkElement HeroPlate(UIElement child)
        {
            GlassSurface plate = Plate(child, new Thickness(0, 0, 0, 14));
            plate.CornerRadius = new CornerRadius(20);
            plate.Padding = new Thickness(24, 20, 24, 26);
            return plate;
        }

        private static FrameworkElement SectionPlate(UIElement child)
        {
            GlassSurface plate = Plate(child, new Thickness(0, 0, 0, 14));
            plate.CornerRadius = new CornerRadius(20);
            plate.Padding = new Thickness(22, 18, 22, 12);
            return plate;
        }

        private FrameworkElement GuidePlate(string title, string note, StepRow[] steps)
        {
            var stack = new StackPanel();
            var heading = _owner.PlainText(title); heading.FontSize = 16; heading.FontWeight = FontWeights.SemiBold;
            var hint = _owner.PlainText(note); hint.FontSize = 12.5; hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 3, 0, 8);
            hint.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            stack.Children.Add(heading); stack.Children.Add(hint);
            for (int index = 0; index < steps.Length; index++)
            {
                if (index > 0) stack.Children.Add(Divider());
                stack.Children.Add(steps[index].View);
            }
            return SectionPlate(stack);
        }

        private Button Primary(string text, System.Action invoke) => Pill(text, primary: true, invoke);
        private Button Secondary(string text, System.Action invoke) => Pill(text, primary: false, invoke);

        private Button Pill(string text, bool primary, System.Action invoke)
        {
            Button button = LauncherUpdateView.Pill(text, primary, readableWhenDisabled: true);
            button.MinWidth = primary ? 172 : 136;
            button.Margin = new Thickness(6, 0, 6, 0);
            button.Visibility = Visibility.Collapsed;
            button.Click += (_, _) => { if (_owner.Live(button)) { invoke(); _owner.Refresh(); } };
            return button;
        }

        private Button Action(string text, System.Action invoke)
        {
            var button = new Button { Content = text, Style = _owner.Style("GlassPresentationButton"), MinWidth = 88, Padding = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, text);
            button.Click += (_, _) => { if (_owner.Live(button)) { invoke(); _owner.Refresh(); } };
            return button;
        }

        private Button Link(string text, System.Action invoke)
        {
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            factory.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
            factory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var button = new Button { Content = text, Template = new ControlTemplate(typeof(Button)) { VisualTree = factory }, Cursor = Cursors.Hand, FontSize = 12.5, Focusable = true };
            button.SetResourceReference(Control.FontFamilyProperty, "GlassPresentationFont");
            button.SetResourceReference(Control.ForegroundProperty, "GlassPresentationSecondary");
            button.MouseEnter += (_, _) => button.SetResourceReference(Control.ForegroundProperty, "GlassPresentationAccent");
            button.MouseLeave += (_, _) => button.SetResourceReference(Control.ForegroundProperty, "GlassPresentationSecondary");
            AutomationProperties.SetName(button, text);
            button.Click += (_, _) => { if (_owner.Live(button)) { invoke(); _owner.Refresh(); } };
            return button;
        }

        private TextBlock Dot()
        {
            var dot = _owner.PlainText("·"); dot.FontSize = 12.5; dot.VerticalAlignment = VerticalAlignment.Center;
            dot.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            return dot;
        }

        private static Border Divider()
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "GlassPresentationSeparator");
            return line;
        }

        private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        // Three peers joined in a triangle, drawn in a 24-unit box; the links stop short of each node.
        private static Geometry BuildNetworkGlyph()
        {
            Point[] nodes = { new(12, 5.5), new(5, 17.5), new(19, 17.5) };
            const double radius = 2.9, gap = radius + 1.6;
            var group = new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(0, 0)), new LineGeometry(new Point(24, 24), new Point(24, 24)) } };
            foreach (Point node in nodes) group.Children.Add(new EllipseGeometry(node, radius, radius));
            for (int index = 0; index < nodes.Length; index++)
            {
                Point from = nodes[index], to = nodes[(index + 1) % nodes.Length];
                Vector direction = to - from; direction.Normalize();
                group.Children.Add(new LineGeometry(from + direction * gap, to - direction * gap));
            }
            group.Freeze();
            return group;
        }

        // An arc of the ring, starting at 12 o'clock and running clockwise for `share` of the circle.
        private static Geometry Arc(double share)
        {
            double angle = share * 2 * Math.PI, centre = Halo / 2;
            var figure = new PathFigure { StartPoint = new Point(centre, centre - Ring) };
            figure.Segments.Add(new ArcSegment(new Point(centre + Ring * Math.Sin(angle), centre - Ring * Math.Cos(angle)), new Size(Ring, Ring), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            var group = new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(0, 0)), new LineGeometry(new Point(Halo, Halo), new Point(Halo, Halo)), new PathGeometry(new[] { figure }) } };
            group.Freeze();
            return group;
        }
    }

    private enum StepState { Pending, Current, Done }

    /// <summary>A numbered guide step: marker (number, or a check once done), title and note, and an optional action.</summary>
    private sealed class StepRow : IDisposable
    {
        private const string CheckGlyph = "M6.5,12.5 L10.5,16.5 L17.5,8";
        internal FrameworkElement View { get; }
        private readonly Ellipse _ring = new() { StrokeThickness = 1.6 };
        private readonly Ellipse _fill = new() { Opacity = 0 };
        private readonly TextBlock _number, _title, _note;
        private readonly Path _check = new() { Data = Geometry.Parse(CheckGlyph), Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Opacity = 0 };
        private readonly Button? _action;
        private readonly ScaleTransform _checkScale = new(1, 1);
        private readonly SpringMotion _doneMotion;
        private StepState? _state;

        internal StepRow(GlassPresentationSession owner, int number, string title, Button? action)
        {
            var row = new Grid { MinHeight = 62 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var marker = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            _fill.SetResourceReference(Shape.FillProperty, "GlassPresentationAccent");
            _number = owner.PlainText(number.ToString(CultureInfo.InvariantCulture));
            _number.FontSize = 13; _number.FontWeight = FontWeights.SemiBold; _number.HorizontalAlignment = HorizontalAlignment.Center; _number.VerticalAlignment = VerticalAlignment.Center;
            _check.SetResourceReference(Shape.StrokeProperty, "GlassPresentationOnAccent");
            _check.RenderTransformOrigin = new Point(.5, .5); _check.RenderTransform = _checkScale;
            marker.Children.Add(_fill); marker.Children.Add(_ring); marker.Children.Add(_number); marker.Children.Add(_check);
            row.Children.Add(marker);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
            _title = owner.PlainText(title); _title.FontSize = 14;
            _note = owner.PlainText(""); _note.FontSize = 12; _note.TextWrapping = TextWrapping.Wrap; _note.Margin = new Thickness(0, 2, 0, 0);
            _note.SetResourceReference(TextBlock.ForegroundProperty, "GlassPresentationSecondary");
            text.Children.Add(_title); text.Children.Add(_note);
            Grid.SetColumn(text, 1); row.Children.Add(text);

            if (action is not null)
            {
                _action = action;
                Grid.SetColumn(action, 2); row.Children.Add(action);
            }
            View = row;
            _doneMotion = new SpringMotion(marker, 0, value =>
            {
                double v = Math.Clamp(value, 0, 1);
                _fill.Opacity = v;
                _check.Opacity = v;
                _number.Opacity = 1 - v;
                _checkScale.ScaleX = _checkScale.ScaleY = .5 + .5 * Math.Clamp(value, 0, 1.15);
            }, new SpringParameters(.42, .6));
        }

        internal void Show(StepState state, string note, bool actionVisible, bool actionEnabled)
        {
            _note.Text = note;
            if (_action is not null)
            {
                _action.Visibility = actionVisible ? Visibility.Visible : Visibility.Collapsed;
                _action.IsEnabled = actionEnabled;
            }
            if (_state == state) return;
            bool first = _state is null;
            _state = state;
            _ring.SetResourceReference(Shape.StrokeProperty, state == StepState.Pending ? "GlassPresentationTrack" : "GlassPresentationAccent");
            _number.SetResourceReference(TextBlock.ForegroundProperty, state == StepState.Current ? "GlassPresentationAccent" : "GlassPresentationSecondary");
            _title.SetResourceReference(TextBlock.ForegroundProperty, state == StepState.Pending ? "GlassPresentationSecondary" : "GlassPresentationText");
            _title.FontWeight = state == StepState.Current ? FontWeights.SemiBold : FontWeights.Normal;
            _doneMotion.Retarget(state == StepState.Done ? 1 : 0, first || !MotionPreferences.AllowsAnimation);
        }

        public void Dispose() => _doneMotion.Dispose();
    }
}
