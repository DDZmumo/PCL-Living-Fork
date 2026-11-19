// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>
/// Full account page opened from the rail avatar. The side card shows the game account (login method switch, avatar,
/// name, method) with skin and sign-out actions; the scene shows the signed-in account and a swipeable stack of saved
/// accounts ending with an "add account" card. Every action runs through the original login pages
/// (<see cref="PageLaunchLeft"/>, <see cref="PageLoginMs"/>, <see cref="PageLoginMsSkin"/>, <see cref="PageLoginLegacy"/>),
/// which keep ownership of sessions, saved accounts and skins.
/// </summary>
internal sealed class GlassAccountWorkspace : IDisposable
{
    private const string GridGlyph = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z";
    private const string ExitGlyph = "M14,4 H19 A1,1 0 0 1 20,5 V19 A1,1 0 0 1 19,20 H14 M10,8 L6,12 10,16 M6,12 H15";
    private const string EditGlyph = "M4,20 H8 L19,9 15,5 4,16 Z M13,7 L17,11";
    private const string PlusGlyph = "M12,5 V19 M5,12 H19";
    private const string ChevronLeft = "M14,6 L8,12 14,18";
    private const string ChevronRight = "M10,6 L16,12 10,18";

    private readonly PageLaunchLeft _source;
    private readonly PCLLF.Wpf.Glass.GlassLauncherView _view;
    private readonly StackPanel _types = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _typesScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Focusable = false };
    private readonly ContentControl _plusSlot = new() { Focusable = false, IsTabStop = false };
    private Button _typesBack = null!, _typesNext = null!;
    private SpringMotion _typesScrollMotion = null!;
    // The side card's middle is a stage: the account in use, the skin-site sign-in form, or the site's characters.
    // Changing what it shows swaps the whole panel in depth (see SwapStage).
    private readonly Grid _stage = new() { ClipToBounds = false };
    private FrameworkElement? _stageCurrent;
    private LayeredHead _sideAvatar = new(84);
    private TextBlock _sideName = Text(string.Empty, 19, FontWeights.SemiBold);
    private TextBlock _sideMethod = Text(string.Empty, 12.5, FontWeights.Normal, secondary: true);
    private string? _identityKey;
    private string? _pendingIdentity;
    private long _pendingIdentitySince;
    private enum SideMode { Identity, Servers, Form, Characters }
    private SideMode _sideMode = SideMode.Identity;
    private FrameworkElement _actions = null!;
    private TextBlock _deckTitle = null!;
    // After signing in on a skin site: the account whose character is still to be chosen (shown on the right deck).
    private (SkinSite Site, SkinSiteAccount? Account)? _choosing;
    private SpringMotion _actionsMotion = null!;
    private readonly Button _skin, _edit, _exit, _libraryButton;
    private const string WardrobeGlyph = "M9,4 L12,6 L15,4 L20,7 L18,11 L16,10 V20 H8 V10 L6,11 L4,7 Z";
    private readonly Grid _deck = new() { ClipToBounds = false };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly List<FrameworkElement> _cards = new();
    private readonly SpringMotion _deckMotion;
    private string _typeSignature = string.Empty, _deckSignature = string.Empty;
    private int _front;
    private double _deckPosition;
    private Point? _dragStart;
    private double _dragOrigin;
    private bool _disposed;

    internal FrameworkElement Side { get; }
    private readonly FrameworkElement _accountSide, _accountScene;
    private readonly GlassSkinLibrary _library;
    private bool _libraryOpen;

    // Account ↔ skin library: the outgoing page recedes into the card while the incoming one settles from in front of
    // it (going back mirrors this), on the same springs as the side card's own depth swaps.
    private void ShowLibrary(bool open, bool instant = false)
    {
        if (_libraryOpen == open && !instant) return;
        _libraryOpen = open;
        if (open) _library.Open();
        DepthSwap(open ? _accountSide : _library.Side, open ? _library.Side : _accountSide, open, instant);
        DepthSwap(open ? _accountScene : _library.Scene, open ? _library.Scene : _accountScene, open, instant);
        if (!open) Refresh();
    }

    private static void DepthSwap(FrameworkElement from, FrameworkElement to, bool forward, bool instant)
    {
        instant |= !MotionPreferences.AllowsAnimation || !from.IsLoaded;
        to.Visibility = Visibility.Visible; to.IsHitTestVisible = true;
        from.IsHitTestVisible = false;
        if (instant) { from.Visibility = Visibility.Collapsed; from.Opacity = to.Opacity = 1; from.RenderTransform = to.RenderTransform = null; return; }
        var outScale = new ScaleTransform(1, 1); from.RenderTransformOrigin = new Point(.5, .5); from.RenderTransform = outScale;
        var inScale = new ScaleTransform(1, 1); to.RenderTransformOrigin = new Point(.5, .5); to.RenderTransform = inScale; to.Opacity = 0;
        SpringMotion? leave = null, enter = null;
        leave = new SpringMotion(from, 0, value =>
        {
            double p = Math.Clamp(value, 0, 1);
            outScale.ScaleX = outScale.ScaleY = 1 + (forward ? -.08 : .06) * p;
            from.Opacity = 1 - p;
            if (value >= .995) { from.Visibility = Visibility.Collapsed; from.Opacity = 1; from.RenderTransform = null; leave?.Dispose(); }
        }, new SpringParameters(.32, 1));
        enter = new SpringMotion(to, 0, value =>
        {
            inScale.ScaleX = inScale.ScaleY = (forward ? 1.06 : .93) + (forward ? -.06 : .07) * value;
            to.Opacity = Math.Clamp(value * 1.3 - .2, 0, 1);
            if (Math.Abs(value - 1) < .002 && enter is { IsRunning: false }) { to.RenderTransform = null; enter.Dispose(); }
        }, new SpringParameters(.46, .84));
        leave.Retarget(1);
        var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        delay.Tick += (_, _) => { delay.Stop(); enter.Retarget(1); };
        delay.Start();
    }

    private GlassTideProgress _loginTide = null!;
    internal FrameworkElement Scene { get; }

    internal GlassAccountWorkspace(PageLaunchLeft source, PCLLF.Wpf.Glass.GlassLauncherView view)
    {
        _source = source; _view = view;
        _skin = IconButton(GridGlyph, "皮肤与披风", OpenSkinMenu);
        // The skin library has its own button beside the account's skin menu (which keeps all its original options).
        _libraryButton = IconButton(WardrobeGlyph, "皮肤库", () => ShowLibrary(true));
        _edit = IconButton(EditGlyph, "修改信息", OpenEditMenu);
        _exit = IconButton(ExitGlyph, "切换账号", SignOut);
        // The workspace has two pages: the account (default) and the skin library; each swaps side card and scene together.
        _accountSide = BuildSide();
        _accountScene = BuildScene();
        _library = new GlassSkinLibrary(() => DarkText, () => ShowLibrary(false));
        _library.Side.Visibility = _library.Scene.Visibility = Visibility.Collapsed;
        Side = new Grid { Children = { _accountSide, _library.Side } };
        Scene = new Grid { Children = { _accountScene, _library.Scene } };
        _deckMotion = new SpringMotion(_deck, 0, value => { _deckPosition = value; LayoutDeck(); }, new SpringParameters(.42, .82));
    }

    // ---------- Layout ----------

    private FrameworkElement BuildSide()
    {
        var root = new Grid { Margin = new Thickness(16, 18, 16, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(Text("游戏账号", 20, FontWeights.SemiBold));
        header.Children.Add(BuildTypeStrip());
        root.Children.Add(header);

        _stageCurrent = BuildIdentityPanel();
        _stage.Children.Add(_stageCurrent);
        Grid.SetRow(_stage, 1); root.Children.Add(_stage);
        // Login progress: the shared tide over the whole card (see GlassTideProgress).
        _loginTide = new GlassTideProgress(11) { Margin = new Thickness(-16, -18, -16, -14), Covers = _stage };
        Grid.SetRowSpan(_loginTide, 3); root.Children.Insert(0, _loginTide);

        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(_skin);
        var middle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _libraryButton.Margin = new Thickness(0, 0, 10, 0);
        middle.Children.Add(_libraryButton);
        middle.Children.Add(_edit);
        Grid.SetColumn(middle, 1); actions.Children.Add(middle);
        Grid.SetColumn(_exit, 2); actions.Children.Add(_exit);
        Grid.SetRow(actions, 2); root.Children.Add(actions);
        // The account actions step aside (sink and fade) while the form or the character list is on stage.
        _actions = actions;
        var sink = new TranslateTransform();
        actions.RenderTransform = sink;
        _actionsMotion = new SpringMotion(actions, 1, value =>
        {
            actions.Opacity = Math.Clamp(value, 0, 1);
            sink.Y = 16 * (1 - value);
            actions.IsHitTestVisible = value > .5;
        }, new SpringParameters(.36, 1));
        return root;
    }

    // [‹] pills (scrolling) [›] [+]: wheel or the chevrons glide the row on a spring; each chevron fades in only when
    // there is more in its direction, and the row's edges fade out under them.
    private FrameworkElement BuildTypeStrip()
    {
        var strip = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _typesBack = IconButton(ChevronLeft, "向左", () => ScrollTypes(-1), 26);
        _typesNext = IconButton(ChevronRight, "向右", () => ScrollTypes(1), 26);
        foreach (Button chevron in new[] { _typesBack, _typesNext }) { chevron.Opacity = 0; chevron.IsHitTestVisible = false; chevron.Width = 0; chevron.VerticalAlignment = VerticalAlignment.Center; }
        _typesBack.Margin = new Thickness(0, 0, 4, 0); _typesNext.Margin = new Thickness(4, 0, 6, 0);
        _typesScroll.Content = _types;
        _typesScroll.PreviewMouseWheel += (_, args) => { ScrollTypes(args.Delta > 0 ? -1 : 1); args.Handled = true; };
        _typesScroll.ScrollChanged += (_, _) => RefreshTypeChevrons();
        _typesScroll.SizeChanged += (_, _) => RefreshTypeChevrons();
        _typesScrollMotion = new SpringMotion(_typesScroll, 0, value => _typesScroll.ScrollToHorizontalOffset(value), new SpringParameters(.42, 1));
        Grid.SetColumn(_typesScroll, 1); Grid.SetColumn(_typesNext, 2); Grid.SetColumn(_plusSlot, 3);
        strip.Children.Add(_typesBack); strip.Children.Add(_typesScroll); strip.Children.Add(_typesNext); strip.Children.Add(_plusSlot);
        return strip;
    }

    private void ScrollTypes(int direction)
    {
        double max = Math.Max(0, _typesScroll.ExtentWidth - _typesScroll.ViewportWidth);
        double target = Math.Clamp(_typesScrollMotion.Target + direction * Math.Max(80, _typesScroll.ViewportWidth * .6), 0, max);
        _typesScrollMotion.Retarget(target, !MotionPreferences.AllowsAnimation);
    }

    private readonly Dictionary<Button, SpringMotion> _chevronMotions = new(ReferenceEqualityComparer.Instance);

    private void RefreshTypeChevrons()
    {
        // Three pills always fit: no chevrons, no edge fades.
        ShowChevron(_typesBack, false); ShowChevron(_typesNext, false); _typesScroll.OpacityMask = null;
        return;
        double max = Math.Max(0, _typesScroll.ExtentWidth - _typesScroll.ViewportWidth);
        ShowChevron(_typesBack, _typesScroll.HorizontalOffset > .5);
        ShowChevron(_typesNext, _typesScroll.HorizontalOffset < max - .5);
        // Edge fades where more pills continue.
        bool left = _typesScroll.HorizontalOffset > .5, right = _typesScroll.HorizontalOffset < max - .5;
        if (!left && !right) { _typesScroll.OpacityMask = null; return; }
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, .1));
        mask.GradientStops.Add(new GradientStop(Colors.Black, .9));
        mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
        _typesScroll.OpacityMask = mask;
    }

    // A chevron grows in from zero width (pushing the row aside) and fades, on a spring.
    private void ShowChevron(Button chevron, bool show)
    {
        if (!_chevronMotions.TryGetValue(chevron, out SpringMotion? motion))
        {
            motion = new SpringMotion(chevron, 0, value =>
            {
                double p = Math.Clamp(value, 0, 1);
                chevron.Width = 26 * p; chevron.Opacity = p;
                chevron.IsHitTestVisible = p > .5;
            }, new SpringParameters(.34, .9));
            _chevronMotions[chevron] = motion;
        }
        if (Math.Abs(motion.Target - (show ? 1 : 0)) > .01) motion.Retarget(show ? 1 : 0, !MotionPreferences.AllowsAnimation);
    }

    // The account in use: head, name and sign-in method, centred.
    private FrameworkElement BuildIdentityPanel()
    {
        _sideAvatar = new LayeredHead(84);
        _sideName = Text(string.Empty, 19, FontWeights.SemiBold);
        _sideMethod = Text(string.Empty, 12.5, FontWeights.Normal, secondary: true);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        identity.Children.Add(_sideAvatar);
        _sideName.HorizontalAlignment = HorizontalAlignment.Center; _sideName.Margin = new Thickness(0, 14, 0, 0); _sideName.TextTrimming = TextTrimming.CharacterEllipsis;
        _sideMethod.HorizontalAlignment = HorizontalAlignment.Center; _sideMethod.Margin = new Thickness(0, 4, 0, 0);
        identity.Children.Add(_sideName); identity.Children.Add(_sideMethod);
        return identity;
    }

    // Depth swap: going forward the old panel recedes (shrinks and fades) while the new one arrives from in front of
    // the card (slightly larger, settling to size); going back the motion is mirrored. Both ride iOS-like springs.
    private void SwapStage(FrameworkElement next, bool forward)
    {
        FrameworkElement? previous = _stageCurrent;
        _stageCurrent = next;
        _stage.Children.Add(next);
        bool animate = MotionPreferences.AllowsAnimation && _stage.IsLoaded;
        if (previous is not null)
        {
            previous.IsHitTestVisible = false;
            if (!animate) _stage.Children.Remove(previous);
            else
            {
                var outScale = new ScaleTransform(1, 1);
                previous.RenderTransformOrigin = new Point(.5, .5); previous.RenderTransform = outScale;
                SpringMotion? leave = null;
                leave = new SpringMotion(previous, 0, value =>
                {
                    double p = Math.Clamp(value, 0, 1);
                    outScale.ScaleX = outScale.ScaleY = 1 + (forward ? -.14 : .12) * p;
                    previous.Opacity = 1 - p;
                    if (value >= .995) { _stage.Children.Remove(previous); leave?.Dispose(); }
                }, new SpringParameters(.34, 1));
                leave.Retarget(1);
            }
        }
        if (!animate) return;
        var inScale = new ScaleTransform(1, 1);
        next.RenderTransformOrigin = new Point(.5, .5); next.RenderTransform = inScale;
        next.Opacity = 0;
        SpringMotion? enter = null;
        enter = new SpringMotion(next, 0, value =>
        {
            inScale.ScaleX = inScale.ScaleY = (forward ? 1.1 : .88) + ((forward ? -.1 : .12) * value);
            next.Opacity = Math.Clamp(value * 1.3 - .15, 0, 1);
            if (Math.Abs(value - 1) < .002 && enter is { IsRunning: false }) { next.RenderTransform = null; enter.Dispose(); }
        }, new SpringParameters(.46, .84));
        // The new panel starts as the old one is half gone, so the two never read as one blurred layer.
        var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(previous is null ? 0 : 90) };
        delay.Tick += (_, _) => { delay.Stop(); if (!_disposed) enter.Retarget(1); };
        delay.Start();
    }

    private void SetSideMode(SideMode mode, FrameworkElement panel, bool forward)
    {
        _sideMode = mode;
        if (mode == SideMode.Identity) _identityKey = null; // re-read the account in use on the next refresh
        SwapStage(panel, forward);
        _actionsMotion.Retarget(mode == SideMode.Identity ? 1 : 0, !MotionPreferences.AllowsAnimation);
        _typeSignature = string.Empty;
        RefreshTypes();
    }

    private FrameworkElement BuildScene()
    {
        // Saved accounts: a stacked deck, swiped with the wheel, a drag or the arrows.
        var deckColumn = new Grid();
        deckColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        deckColumn.RowDefinitions.Add(new RowDefinition());
        deckColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var deckHeader = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 6, 0, 18) };
        deckHeader.Children.Add(new GlassSurface { Style = (Style)_view.FindResource("GlassPageTitleFrost") });
        _deckTitle = Text("切换账号", 22, FontWeights.SemiBold);
        deckHeader.Children.Add(_deckTitle);
        deckColumn.Children.Add(deckHeader);
        _deck.Background = Brushes.Transparent; // the whole deck area takes wheel and drag input
        _deck.PreviewMouseWheel += (_, args) => { Swipe(args.Delta > 0 ? -1 : 1); args.Handled = true; };
        _deck.PreviewMouseLeftButtonDown += OnDeckPress;
        _deck.PreviewMouseMove += OnDeckDrag;
        _deck.PreviewMouseLeftButtonUp += OnDeckRelease;
        _deck.LostMouseCapture += (_, _) => { if (_dragStart is not null) { _dragStart = null; SetFront((int)Math.Round(_deckPosition)); } };
        _deck.SizeChanged += (_, _) => LayoutDeck();
        Grid.SetRow(_deck, 1); deckColumn.Children.Add(_deck);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        pager.Children.Add(IconButton(ChevronLeft, "上一个", () => Swipe(-1), 30));
        _dots.Margin = new Thickness(12, 0, 12, 0); _dots.VerticalAlignment = VerticalAlignment.Center;
        pager.Children.Add(_dots);
        pager.Children.Add(IconButton(ChevronRight, "下一个", () => Swipe(1), 30));
        Grid.SetRow(pager, 2); deckColumn.Children.Add(pager);
        return deckColumn;
    }

    // ---------- State ----------

    /// <summary>Called when the page opens: rebuild and bring the account in use to the front.</summary>
    internal void Open() { ShowLibrary(false, instant: true); _deckSignature = string.Empty; _front = -1; _rebuildInstantly = true; Refresh(); }

    private GlassPresentationSession? _customLoginSession;

    internal void Refresh()
    {
        if (_disposed) return;
        List<SkinSite> sites = GlassSkinSites.Load();
        SkinSiteCharacter? siteCharacter = AuthSelected && _source.PanType.Visibility == Visibility.Visible ? GlassSkinSites.Current(sites) : null;
        bool siteMode = siteCharacter is not null;
        bool custom = CustomProviderSelected;
        bool customSignedIn = AuthSignedIn || NideSignedIn;
        bool microsoft = MicrosoftSelected;
        string kind = AuthSelected ? "Auth" : NideSelected ? "Nide" : microsoft ? "Ms" : "Legacy";
        var skinRequest = siteCharacter is null ? LaunchPageRuntime.Skins.Input(kind) : LaunchPageRuntime.SkinSiteRequest(siteCharacter);
        string name = kind == "Legacy" ? LaunchPageRuntime.Skins.OfflineAccountName() :
            siteMode || customSignedIn || microsoft && MicrosoftSignedIn ? skinRequest.Name : "";
        ImageSource? avatar = string.IsNullOrWhiteSpace(name) ? null : LaunchPageRuntime.Avatar(kind, skinRequest);
        string customLabel = !string.IsNullOrWhiteSpace(_source.LabTypeOne.Text)
            ? _source.LabTypeOne.Text
            : AuthSelected ? "Authlib-Injector" : "统一通行证";
        string siteName = siteCharacter?.Site.Name ?? "";
        string method = siteMode
            ? (siteName.Length > 0 ? siteName + " · 外置登录" : "外置登录账号")
            : custom
            ? (customSignedIn ? customLabel : customLabel + " · 未登录")
            : microsoft ? (MicrosoftSignedIn ? "正版账号 · Microsoft" : "正版账号 · 未登录") : "离线账号";
        string shownName = string.IsNullOrWhiteSpace(name) ? "未登录" : name;
        // Another account (or method) in use: the identity panel swaps in depth instead of changing in place.
        string key = siteCharacter?.Key ?? string.Join("|", kind, shownName, method, skinRequest.Server, skinRequest.Id);
        bool settled = true;
        if (_sideMode == SideMode.Identity && _identityKey is not null && _identityKey != key)
        {
            // Switching updates the original pages in steps; swap once the new account has held for a moment.
            long now = Environment.TickCount64;
            if (_pendingIdentity != key) { _pendingIdentity = key; _pendingIdentitySince = now; settled = false; ScheduleSettle(); }
            else if (now - _pendingIdentitySince < 160) { settled = false; ScheduleSettle(); }
            else { _pendingIdentity = null; SwapStage(BuildIdentityPanel(), forward: true); }
        }
        if (_sideMode == SideMode.Identity && settled) _identityKey = key;
        if (settled)
        {
            _sideName.Text = shownName;
            _sideMethod.Text = method;
            _sideAvatar.Show(avatar);
        }
        _skin.Visibility = SkinMenu() is null ? Visibility.Hidden : Visibility.Visible;
        if (_libraryOpen) _library.Refresh();
        _edit.Visibility = !siteMode && ((microsoft && MicrosoftSignedIn) || customSignedIn) ? Visibility.Visible : Visibility.Collapsed;
        _exit.Visibility = siteMode || (microsoft && MicrosoftSignedIn) || customSignedIn ? Visibility.Visible : Visibility.Hidden;
        string exitTip = siteMode ? "切换外置登录服务器" : "切换账号";
        if (!Equals(_exit.ToolTip, exitTip)) { _exit.ToolTip = exitTip; AutomationProperties.SetName(_exit, exitTip); }
        RefreshTypes();
        RefreshDeck(microsoft, custom, customLabel, siteCharacter, name, avatar);
        RefreshLoginProgress();
    }

    private bool AuthSelected => _source.PanLogin.Children.OfType<UIElement>().Any(child => child is PageLoginAuth or PageLoginAuthSkin);
    private bool NideSelected => _source.PanLogin.Children.OfType<UIElement>().Any(child => child is PageLoginNide or PageLoginNideSkin);
    // A skin-site character in use on an instance that leaves the login method free (not locked to one server).
    private bool SiteMode => AuthSelected && _source.PanType.Visibility == Visibility.Visible && GlassSkinSites.CurrentForLaunch(ModMinecraft.McInstanceSelected) is not null;
    // The original third-party page is shown only when the instance itself locks the login method to that server; on a
    // free-choice instance an Authlib page that is still on screen is just the skin site being switched away from.
    private bool CustomProviderSelected => (AuthSelected || NideSelected) && _source.PanType.Visibility != Visibility.Visible;
    private bool AuthSignedIn => ModMain.FrmLoginAuthSkin is { } skin && _source.PanLogin.Children.Contains(skin);
    private bool NideSignedIn => ModMain.FrmLoginNideSkin is { } skin && _source.PanLogin.Children.Contains(skin);
    private bool MicrosoftSelected => !SiteMode && !CustomProviderSelected && (_source.RadioLoginType5.Checked ||
        (_source.PanType.Visibility != Visibility.Visible && _source.PanLogin.Children.OfType<UIElement>().Any(child => child is PageLoginMs or PageLoginMsSkin)));
    private bool MicrosoftSignedIn => ModMain.FrmLoginMsSkin is { } skin && _source.PanLogin.Children.Contains(skin);

    private void RefreshTypes()
    {
        if (_source.PanType.Visibility != Visibility.Visible)
        {
            string lockedLabel = _source.LabTypeOne.Text ?? string.Empty;
            string lockedSig = "locked:" + lockedLabel;
            if (lockedSig == _typeSignature) return;
            _typeSignature = lockedSig;
            _types.Children.Clear();
            if (!string.IsNullOrWhiteSpace(lockedLabel))
            {
                var pill = Pill(lockedLabel, PillKind.Selected, static () => { });
                pill.IsHitTestVisible = false;
                _types.Children.Add(pill);
            }
            return;
        }
        var radios = _source.PanType.Children.OfType<MyRadioButton>().Where(radio => radio.Visibility == Visibility.Visible).ToList();
        bool siteMode = SiteMode;
        bool external = siteMode || _sideMode != SideMode.Identity;
        string signature = string.Join("|", radios.Select(radio => radio.Text + ":" + (radio.Checked && !external))) + "#" + external;
        if (signature == _typeSignature) return;
        _typeSignature = signature;
        _types.Children.Clear();
        foreach (MyRadioButton radio in radios)
        {
            MyRadioButton target = radio;
            var segment = Pill(radio.Text, radio.Checked && !external ? PillKind.Primary : PillKind.Plain, () => UseMethod(target));
            segment.Margin = new Thickness(0, 0, 6, 0);
            _types.Children.Add(segment);
        }
        // One bubble for every third-party (Yggdrasil) server: it opens the server list on the side card.
        var externalPill = Pill("外置登录", external ? PillKind.Primary : PillKind.Plain, () => { if (!_siteBusy && _sideMode != SideMode.Servers) OpenServers(); });
        externalPill.Margin = new Thickness(0, 0, 6, 0);
        externalPill.ToolTip = "Yggdrasil 外置登录（如 LittleSkin）";
        _types.Children.Add(externalPill);
        _plusSlot.Content = null;
        _typesScroll.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(RefreshTypeChevrons));
    }

    // Side card: the saved servers as a vertical list (the one in use ticked), and a + bubble that opens the form.
    private void OpenServers(bool forward = true)
    {
        bool cancelRoleSelection = _choosing is not null;
        if (cancelRoleSelection) { _choosing = null; _deckSignature = string.Empty; }
        List<SkinSite> sites = GlassSkinSites.Load();
        string activeServer = SiteMode ? GlassSkinSites.Current(sites)?.Site.Server ?? "" : "";
        var panel = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(FlowHeading("外置登录", sites.Count == 0 ? "还没有添加验证服务器" : "选择验证服务器"));
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (SkinSite site in sites) list.Children.Add(ServerRow(site, GlassSkinSites.SameServer(site.Server, activeServer)));
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1); panel.Children.Add(scroller);
        // + bubble with its caption, centred under the list.
        var add = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2) };
        var plus = IconButton(PlusGlyph, "添加验证服务器", () => { if (!_siteBusy) OpenSiteForm(null); }, 42);
        plus.HorizontalAlignment = HorizontalAlignment.Center;
        var caption = Text("添加验证服务器", 12, FontWeights.Normal, secondary: true); caption.HorizontalAlignment = HorizontalAlignment.Center; caption.Margin = new Thickness(0, 6, 0, 0);
        add.Children.Add(plus); add.Children.Add(caption);
        Grid.SetRow(add, 2); panel.Children.Add(add);
        SetSideMode(SideMode.Servers, panel, forward);
        if (cancelRoleSelection) {
            GlassSkinSites.EndRoleSelection(this);
            Refresh();
        }
        Stagger(list);
    }

    private FrameworkElement FlowHeading(string title, string note, Action? back = null)
    {
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        if (back is not null && GlassSkinSites.RoleSelectionPending) {
            var backButton = IconButton(ChevronLeft, "返回", () => { if (!_siteBusy) back(); }, 30);
            backButton.Margin = new Thickness(0, 0, 8, 0);
            heading.Children.Add(backButton);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(title, 17, FontWeights.SemiBold));
        var line = Text(note, 12, FontWeights.Normal, secondary: true); line.TextTrimming = TextTrimming.CharacterEllipsis; line.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(line);
        Grid.SetColumn(texts, 1); heading.Children.Add(texts);
        return heading;
    }

    // A server row: initial disc, name and host, a tick when in use; hovering shows a dark × disc to delete (click
    // twice: the first turns the host line into a red confirmation).
    private FrameworkElement ServerRow(SkinSite site, bool active)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var disc = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18) };
        disc.SetResourceReference(Border.BackgroundProperty, active ? "GlassAccentBrush" : "GlassListSelectedBrush");
        string name = site.Name.Length > 0 ? site.Name : "外置登录";
        var initial = Text(name[..1].ToUpperInvariant(), 15, FontWeights.SemiBold);
        if (active) initial.SetResourceReference(TextBlock.ForegroundProperty, "GlassAccentTextBrush");
        initial.HorizontalAlignment = HorizontalAlignment.Center; initial.VerticalAlignment = VerticalAlignment.Center;
        disc.Child = initial;
        row.Children.Add(disc);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        var title = Text(name, 14.5, FontWeights.SemiBold); title.TextTrimming = TextTrimming.CharacterEllipsis;
        string hostText = Uri.TryCreate(site.Server, UriKind.Absolute, out Uri? uri) ? uri.Host : site.Server;
        int count = GlassSkinSites.Characters(site).Count();
        var detail = Text(hostText + (count > 0 ? " · " + count + " 个角色" : ""), 11.5, FontWeights.Normal, secondary: true); detail.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title); texts.Children.Add(detail);
        Grid.SetColumn(texts, 1); row.Children.Add(texts);
        var tail = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        if (active) { var tick = Glyph("M5,12 L10,17 L19,7", 14); tick.HorizontalAlignment = HorizontalAlignment.Center; tick.VerticalAlignment = VerticalAlignment.Center; tail.Children.Add(tick); }
        Grid.SetColumn(tail, 2); row.Children.Add(tail);

        var button = ChromeButton(row, new CornerRadius(14), new Thickness(10, 9, 10, 9), double.NaN, "GlassSelectedBrush");
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.SizeChanged += (_, args) => row.Width = Math.Max(0, args.NewSize.Width - 20);
        button.Click += (_, _) => { if (!_siteBusy) UseSite(site); };
        AutomationProperties.SetName(button, name);

        var host = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        host.Children.Add(button);
        // Delete badge: dark disc with a white ×, popping in at the row's right end on hover.
        var cross = Glyph("M7,7 L17,17 M17,7 L7,17", 8);
        cross.StrokeThickness = 2.4; cross.Stroke = Brushes.White;
        cross.HorizontalAlignment = HorizontalAlignment.Center; cross.VerticalAlignment = VerticalAlignment.Center;
        var badgeDisc = new Grid { Width = 22, Height = 22 };
        badgeDisc.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x18, 0x1C)), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 });
        badgeDisc.Children.Add(cross);
        badgeDisc.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 270, Opacity = .35, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        var badge = ChromeButton(badgeDisc, new CornerRadius(11), new Thickness(), 22, "GlassBadgeNoFill");
        badge.Width = 22; badge.HorizontalAlignment = HorizontalAlignment.Right; badge.VerticalAlignment = VerticalAlignment.Center; badge.Margin = new Thickness(0, 0, 10, 0);
        badge.ToolTip = "删除"; AutomationProperties.SetName(badge, "删除 " + name);
        var pop = new ScaleTransform(.4, .4);
        badge.RenderTransformOrigin = new Point(.5, .5); badge.RenderTransform = pop; badge.Opacity = 0; badge.IsHitTestVisible = false;
        host.Children.Add(badge);
        var motion = new SpringMotion(badge, 0, value =>
        {
            pop.ScaleX = pop.ScaleY = .4 + .6 * Math.Clamp(value, 0, 1.2);
            badge.Opacity = Math.Clamp(value, 0, 1);
            badge.IsHitTestVisible = value > .5;
            tail.Opacity = 1 - Math.Clamp(value, 0, 1);
        }, new SpringParameters(.3, .7));
        bool armed = false;
        string original = detail.Text;
        var disarm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        void Disarm() { disarm.Stop(); armed = false; detail.Text = original; detail.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush"); }
        disarm.Tick += (_, _) => Disarm();
        host.MouseEnter += (_, _) => { if (!_siteBusy) motion.Retarget(1, !MotionPreferences.AllowsAnimation); };
        host.MouseLeave += (_, _) => { if (!armed) motion.Retarget(0, !MotionPreferences.AllowsAnimation); };
        badge.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                detail.Text = "再次点击 × 删除这个服务器";
                detail.Foreground = new SolidColorBrush(DarkText ? Color.FromRgb(196, 43, 28) : Color.FromRgb(255, 128, 118));
                disarm.Start();
                return;
            }
            Disarm();
            DeleteSite(site);
            OpenServers(forward: false);
        };
        return host;
    }

    private void DeleteSite(SkinSite site)
    {
        List<SkinSite> sites = GlassSkinSites.Load();
        bool inUse = GlassSkinSites.Current(sites) is { } current && GlassSkinSites.SameServer(current.Site.Server, site.Server);
        sites.RemoveAll(item => GlassSkinSites.SameServer(item.Server, site.Server));
        GlassSkinSites.Save(sites);
        if (inUse)
        {
            GlassSkinSites.Deactivate();
            _source.RefreshPage(true, false);
        }
        _typeSignature = string.Empty;
        _deckSignature = string.Empty;
        Refresh();
    }

    // Microsoft / offline: leaving a skin-site character first, then the original radio as before.
    private void UseMethod(MyRadioButton target)
    {
        if (_siteBusy) return;
        if (GlassSkinSites.IsActive)
        {
            GlassSkinSites.Deactivate();
            // Switch the original page at once (no animated swap), so the account page never sees the old page.
            if (!target.Checked) target.Checked = true;
            _source.RefreshPage(true, false);
        }
        else if (!target.Checked) target.PerformClick();
        if (_sideMode != SideMode.Identity) CloseSideFlow();
        else Refresh();
    }

    private sealed record DeckAccount(string Key, string Name, Action Use, SkinSiteCharacter? Character = null);

    private void RefreshDeck(bool microsoft, bool custom, string customLabel, SkinSiteCharacter? selected, string currentName, ImageSource? avatar)
    {
        SkinSite? site = selected?.Site;
        SetDeckTitle(_choosing is null && site is null ? "切换账号" : "切换角色");
        if (_choosing is { } choosing) { RefreshChoosingDeck(choosing.Site, choosing.Account); return; }
        if (custom)
        {
            string customSig = "custom|" + AuthSelected + "|" + NideSelected + "|" + (AuthSignedIn || NideSignedIn);
            if (customSig == _deckSignature)
            {
                _customLoginSession?.Refresh();
                return;
            }
            _pendingSignature = null;
            _deckSignature = customSig;
            foreach (FrameworkElement leaving in _presence.Where(pair => pair.Value.Leaving).Select(pair => pair.Key).ToList())
                RemoveCard(leaving);
            foreach (FrameworkElement old in _cards.ToList())
                RemoveCard(old);
            _cards.Clear();
            _dots.Children.Clear();
            _customLoginSession ??= GlassPresentation.Create(_source.PanLogin);
            _customLoginSession.Refresh();
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _customLoginSession.View
            };
            FrameworkElement hostCard = DeckCard(scroller, string.IsNullOrWhiteSpace(customLabel) ? "外置账号" : customLabel);
            _cards.Add(hostCard);
            _deck.Children.Add(hostCard);
            _front = 0;
            _deckMotion.Snap(0);
            Enter(hostCard, 0);
            LayoutDeck();
            return;
        }
        var accounts = new List<DeckAccount>();
        if (site is not null)
        {
            foreach (SkinSiteCharacter character in GlassSkinSites.Characters(site))
            {
                accounts.Add(new(character.Key, character.Profile.Name, () => UseCharacter(character), character));
            }
        }
        else if (microsoft)
        {
            foreach (string saved in SavedMicrosoftAccounts())
            {
                string chosen = saved;
                accounts.Add(new(saved, saved, () => UseMicrosoft(chosen)));
            }
        }
        else if (!microsoft && ModMain.FrmLoginLegacy is { } offline)
        {
            IEnumerable names = offline.ComboName.ItemsSource ?? offline.ComboName.Items;
            foreach (string value in names.OfType<object>().Select(item => item?.ToString() ?? string.Empty).Where(text => text.Length > 0).Distinct())
            {
                string chosen = value;
                accounts.Add(new(value, value, () => UseOffline(chosen)));
            }
        }
        string current = selected?.Key ?? currentName;
        bool currentSignedIn = current.Length > 0 && (site is not null || !microsoft || MicrosoftSignedIn);
        // The account in use always has its card, even before the original page has saved it to its list.
        if (currentSignedIn && !accounts.Any(account => account.Key == current))
            accounts.Insert(0, new(current, currentName, () => { }));
        string signature = (site is not null ? "site:" + site.Server : microsoft.ToString()) + "|" + string.Join("|", accounts.Select(account => account.Key + "=" + account.Name));
        if (signature == _deckSignature)
        {
            _pendingSignature = null;
            foreach (FrameworkElement card in _cards)
                if (_inUse.TryGetValue(card, out var entry))
                    entry.Update(entry.Key == current && currentSignedIn, avatar);
            // When the account in use changes (e.g. after signing in), bring it forward once; swipes are left alone.
            if (currentSignedIn && current != _followedCurrent)
            {
                _followedCurrent = current;
                int signedIn = _cards.FindIndex(card => _inUse.TryGetValue(card, out var entry) && entry.Key == current);
                if (signedIn >= 0 && signedIn != _front) SetFront(signedIn);
            }
            return;
        }
        // Switching the login method updates the original pages in several steps (method first, then the account in
        // use). Wait until the list has been stable for a moment so the stack animates once, from the final state.
        if (!_rebuildInstantly && !_skipNextRebuildIfSame)
        {
            long now = Environment.TickCount64;
            if (_pendingSignature != signature) { _pendingSignature = signature; _pendingSince = now; ScheduleSettle(); return; }
            if (now - _pendingSince < 180) { ScheduleSettle(); return; }
        }
        _pendingSignature = null;
        _deckSignature = signature;
        _followedCurrent = currentSignedIn ? current : null;
        if (_skipNextRebuildIfSame)
        {
            _skipNextRebuildIfSame = false;
            var remaining = _cards.Take(Math.Max(0, _cards.Count - 1)).Select(card => AutomationProperties.GetName(card)).ToList();
            if (remaining.SequenceEqual(accounts.Select(account => account.Name))) { LayoutDeck(); return; }
        }

        // Outgoing cards slide down and out (when the page opens they are simply cleared).
        bool animate = MotionPreferences.AllowsAnimation && !_rebuildInstantly && _deck.IsLoaded;
        _rebuildInstantly = false;
        // Cards still leaving from an earlier switch go at once, so two stacks never overlap.
        foreach (FrameworkElement leaving in _presence.Where(pair => pair.Value.Leaving).Select(pair => pair.Key).ToList())
            RemoveCard(leaving);
        bool hadCards = _cards.Count > 0;
        foreach (FrameworkElement old in _cards.ToList())
        {
            if (animate) Leave(old, below: true);
            else RemoveCard(old);
        }
        _cards.Clear(); _dots.Children.Clear();
        foreach (DeckAccount account in accounts)
        {
            bool isCurrent = account.Key == current && currentSignedIn;
            string chosen = account.Name;
            if (site is not null && account.Character is { } character)
            {
                _cards.Add(AccountCard(account.Key, chosen, string.IsNullOrEmpty(site.Name) ? "外置登录角色" : site.Name, isCurrent, avatar, account.Use, () => { }, true,
                    () => GlassSkinSites.Head(character.Site.Server, character.Profile.Id), deletable: false));
            }
            else _cards.Add(AccountCard(account.Key, chosen, microsoft ? "正版账号" : "离线账号", isCurrent, avatar, account.Use, () => Delete(chosen, microsoft), microsoft));
        }
        if (site is null) _cards.Add(microsoft ? AddMicrosoftCard() : AddOfflineCard());
        foreach (FrameworkElement card in _cards) _deck.Children.Add(card);
        // Incoming cards rise from below, front first, then the stack around it.
        for (int index = 0; index < _cards.Count; index++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0) };
            dot.SetResourceReference(Shape.FillProperty, "GlassTextBrush");
            _dots.Children.Add(dot);
        }
        // Open on the account in use.
        int inUse = accounts.FindIndex(account => account.Key == current && currentSignedIn);
        _front = inUse >= 0 ? inUse : Math.Clamp(_front < 0 ? 0 : _front, 0, _cards.Count - 1);
        _deckMotion.Snap(_front);
        var order = _cards.OrderBy(card => Math.Abs(_cards.IndexOf(card) - _front)).ToList();
        // New cards rise once the old stack has mostly cleared.
        double start = animate && hadCards ? .16 : 0;
        for (int index = 0; index < order.Count; index++) Enter(order[index], MotionPreferences.AllowsAnimation ? start + index * .05 : 0);
    }

    // ---------- Card presence (enter from below, leave below, delete upwards) ----------

    private sealed class Presence
    {
        internal SpringMotion Motion = null!;
        internal double Value;
        internal double Direction = 1;   // +1: offset below, −1: offset above
        internal double BaseOpacity = 1; // for leaving cards, which no longer take part in the layout
        internal bool Leaving;
    }

    private readonly Dictionary<FrameworkElement, Presence> _presence = new(ReferenceEqualityComparer.Instance);
    private bool _rebuildInstantly;

    private Presence StateOf(FrameworkElement card)
    {
        if (_presence.TryGetValue(card, out Presence? state)) return state;
        state = new Presence();
        Presence captured = state;
        state.Motion = new SpringMotion(_deck, 0, value => { captured.Value = value; ApplyPresence(card, captured); }, new SpringParameters(.48, .82)) { MaxFrameSeconds = 1 / 40d };
        _presence[card] = state;
        return state;
    }

    private void Enter(FrameworkElement card, double delaySeconds)
    {
        Presence state = StateOf(card);
        state.Direction = 1; state.Leaving = false;
        if (!MotionPreferences.AllowsAnimation) { state.Motion.Snap(1); return; }
        state.Motion.Snap(0);
        if (delaySeconds <= 0) { state.Motion.Retarget(1); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) => { timer.Stop(); if (!_disposed && !state.Leaving && _presence.ContainsKey(card)) state.Motion.Retarget(1); };
        timer.Start();
    }

    private void Leave(FrameworkElement card, bool below)
    {
        Presence state = StateOf(card);
        state.Leaving = true; state.Direction = below ? 1 : -1;
        state.BaseOpacity = card.Opacity;
        card.IsHitTestVisible = false;
        if (below && card.Tag is Grid content) content.Tag = true;
        if (!MotionPreferences.AllowsAnimation) { state.Motion.Snap(0); return; }
        state.Motion.Retarget(0);
    }

    private void ApplyPresence(FrameworkElement card, Presence state)
    {
        double p = state.Value;
        var lift = (TranslateTransform)((TransformGroup)card.RenderTransform).Children[2];
        lift.Y = state.Direction * (state.Direction > 0 ? 120 : 150) * (1 - p);
        if (state.Leaving)
        {
            card.Opacity = state.BaseOpacity * Math.Clamp(p, 0, 1);
            if (p <= MotionTokens.VisibilityThreshold) RemoveCard(card);
        }
        else LayoutDeck();
    }

    private void RemoveCard(FrameworkElement card)
    {
        _deck.Children.Remove(card);
        _inUse.Remove(card);
        if (_presence.Remove(card, out Presence? state)) { state.Leaving = true; state.Motion.Dispose(); }
    }

    private string? _pendingSignature;
    private string? _followedCurrent;

    private long _pendingSince;
    private System.Windows.Threading.DispatcherTimer? _settle;

    private void ScheduleSettle()
    {
        _settle ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => { _settle!.Stop(); if (!_disposed) Refresh(); }, _deck.Dispatcher) { IsEnabled = false };
        _settle.Stop(); _settle.Start();
    }

    private double PresenceOf(FrameworkElement card) => _presence.TryGetValue(card, out Presence? state) ? Math.Clamp(state.Value, 0, 1) : 1;

    // ---------- Delete ----------

    // Removes a saved account the same way the original account list's delete button does, then lifts its card away
    // and lets the stack close the gap. Deleting the account in use signs out of it first.
    private void Delete(string name, bool microsoft)
    {
        FrameworkElement? card = _cards.FirstOrDefault(item => AutomationProperties.GetName(item) == name);
        try
        {
            if (microsoft)
            {
                Settings.UpdateMicrosoftAccounts(accounts => accounts.Property(name, StringComparison.OrdinalIgnoreCase)?.Remove());
                if (ModMain.FrmLoginMs is PageLoginMs page &&
                    page.ComboAccounts.Items.OfType<MyListItem>().FirstOrDefault(item => string.Equals(item.Title, name, StringComparison.OrdinalIgnoreCase)) is MyListItem row)
                {
                    if (ReferenceEquals(page.ComboAccounts.SelectedItem, row)) page.ComboAccounts.SelectedIndex = 0;
                    page.ComboAccounts.Items.Remove(row);
                }
            }
            else if (ModMain.FrmLoginLegacy is PageLoginLegacy offline)
            {
                var names = Settings.UpdateLegacyNames(items => items.RemoveAll(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)));
                offline.ComboName.ItemsSource = names;
                if (string.Equals(offline.ComboName.Text, name, StringComparison.OrdinalIgnoreCase)) offline.ComboName.Text = names.Any() ? names[0] : string.Empty;
            }
        }
        catch (Exception error)
        {
            Logger.Error(error, "删除账号失败", LogBehavior.Toast);
            return;
        }
        if (card is null) { Refresh(); return; }
        int index = _cards.IndexOf(card);
        _cards.Remove(card);
        if (_dots.Children.Count > 0) _dots.Children.RemoveAt(_dots.Children.Count - 1);
        Leave(card, below: false);
        // The stack closes the gap on its own spring; the next refresh only rebuilds if more than this card changed.
        _front = Math.Clamp(index <= _front ? _front - (index < _front ? 1 : 0) : _front, 0, Math.Max(0, _cards.Count - 1));
        _deckMotion.Retarget(_front);
        _deckSignature = string.Empty;
        _skipNextRebuildIfSame = true;
        Refresh();
    }

    private bool _skipNextRebuildIfSame;

    // ---------- Login progress ----------

    // Microsoft sign-in reports one overall progress value (no per-step names), so the caption stays general.
    private void RefreshLoginProgress()
    {
        if (_siteBusy) { _loginTide.Update(true, _siteProgress, _siteCaption); return; }
        var loader = ModLaunch.McLoginMsLoader;
        bool active = loader is not null && loader.State == ModBase.LoadState.Loading;
        double progress = active ? Math.Clamp(loader!.Progress, 0, 1) : 0;
        _loginTide.Update(active, progress, progress < .9 ? "正在登录 Microsoft 账号" : "即将完成");
    }

    // ---------- Deck ----------

    private void Swipe(int direction) => SetFront(_front + direction);

    private void SetFront(int index)
    {
        if (_cards.Count == 0) return;
        _front = Math.Clamp(index, 0, _cards.Count - 1);
        _deckMotion.Retarget(_front, !MotionPreferences.AllowsAnimation);
    }

    // Two-sided stack: the front card is centred; earlier cards tuck in behind it on the left and later ones on the
    // right, each step back moving 44 px outwards, shrinking 7 % towards its outer edge and dimming. Positions follow the
    // continuous deck spring, so swiping slides every card smoothly between the two sides.
    private void LayoutDeck()
    {
        const double step = 44;
        double width = Math.Min(340, Math.Max(220, _deck.ActualWidth - 2 * step * 2 - 20));
        double left = Math.Max(0, (_deck.ActualWidth - width) / 2);
        int foregroundIndex = (int)Math.Clamp(Math.Round(_deckPosition, MidpointRounding.AwayFromZero), 0, Math.Max(0, _cards.Count - 1));
        for (int index = 0; index < _cards.Count; index++)
        {
            FrameworkElement card = _cards[index];
            double rank = index - _deckPosition, distance = Math.Abs(rank);
            card.Width = width;
            card.RenderTransformOrigin = new Point(rank >= 0 ? 1 : 0, .5); // shrink towards the outer, visible edge
            var group = (TransformGroup)card.RenderTransform;
            var scale = (ScaleTransform)group.Children[0];
            var shift = (TranslateTransform)group.Children[1];
            scale.ScaleX = scale.ScaleY = 1 - .07 * Math.Min(distance, 3);
            shift.X = left + Math.Sign(rank) * step * Math.Min(distance, 3);
            shift.Y = 0;
            double presence = PresenceOf(card);
            ((TranslateTransform)group.Children[2]).Y = 120 * (1 - presence);
            card.Opacity = Math.Clamp(1 - .22 * distance, 0, 1) * (distance > 3 ? Math.Clamp(4 - distance, 0, 1) : 1) * presence;
            Panel.SetZIndex(card, 1000 - (int)Math.Round(distance * 10));
            card.IsHitTestVisible = distance < 3.5;
            if (card.Tag is Grid body) {
                body.IsHitTestVisible = index == foregroundIndex;
                // Desktop acrylic samples behind the window, so blur overlapping WPF content separately.
                bool behind = index != foregroundIndex && card.Opacity > .01;
                if (body.Tag is not bool current || current != behind) body.Tag = behind;
            }
            card.Visibility = card.Opacity <= .01 ? Visibility.Hidden : Visibility.Visible;
        }
        for (int index = 0; index < _dots.Children.Count; index++)
            ((Ellipse)_dots.Children[index]).Opacity = index == _front ? .95 : .3;
    }

    private void OnDeckPress(object sender, MouseButtonEventArgs args)
    {
        // A card at the side brings itself to the front instead of acting; the topmost card under the pointer wins.
        Point at = args.GetPosition(_deck);
        FrameworkElement? hitCard = _cards
            .Where(card => card.Visibility == Visibility.Visible && card.Opacity > .05 && CardBounds(card).Contains(at))
            .OrderByDescending(Panel.GetZIndex).FirstOrDefault();
        if (hitCard is null) return;
        int index = _cards.IndexOf(hitCard);
        if (index != _front)
        {
            SetFront(index); args.Handled = true; return;
        }
        _dragStart = at; _dragOrigin = _deckPosition;
        // A card's glass can be hit-transparent where there is no foreground content.
        if (ReferenceEquals(args.OriginalSource, _deck)) args.Handled = true;
    }

    private Rect CardBounds(FrameworkElement card)
    {
        try { return card.TransformToVisual(_deck).TransformBounds(new Rect(card.RenderSize)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    private void OnDeckDrag(object sender, MouseEventArgs args)
    {
        if (_dragStart is not Point start || args.LeftButton != MouseButtonState.Pressed) return;
        double dx = args.GetPosition(_deck).X - start.X;
        if (Math.Abs(dx) < 8) return;
        if (!_deck.IsMouseCaptured) _deck.CaptureMouse();
        _deckMotion.Snap(Math.Clamp(_dragOrigin - dx / 160, -.4, _cards.Count - .6));
    }

    private void OnDeckRelease(object sender, MouseButtonEventArgs args)
    {
        if (_deck.IsMouseCaptured) { _deck.ReleaseMouseCapture(); SetFront((int)Math.Round(_deckPosition)); args.Handled = true; }
        _dragStart = null;
    }

    // ---------- Cards ----------

    private FrameworkElement AccountCard(string key, string name, string method, bool current, ImageSource? avatar, Action use, Action delete, bool microsoft, Func<System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapSource?>>? head = null, bool deletable = true)
    {
        FrameworkElement card = DeckCard(AccountBody(name, method, current, use, delete, microsoft, head, deletable, out var inUseHead), name);
        inUseHead?.Show(avatar);
        _inUse[card] = (key, (isCurrent, currentAvatar) =>
        {
            if (current != isCurrent) {
                current = isCurrent;
                FrameworkElement body = AccountBody(name, method, isCurrent, use, delete, microsoft, head, deletable, out inUseHead);
                var layer = (Grid)card.Tag;
                layer.Children.Clear(); layer.Children.Add(body);
                LayoutDeck();
            }
            inUseHead?.Show(currentAvatar);
        });
        return card;
    }

    private readonly Dictionary<FrameworkElement, (string Key, Action<bool, ImageSource?> Update)> _inUse = new(ReferenceEqualityComparer.Instance);

    private FrameworkElement AccountBody(string name, string method, bool current, Action use, Action delete, bool microsoft, Func<System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapSource?>>? head, bool deletable, out LayeredHead? inUseHead)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        inUseHead = null;
        if (current)
        {
            inUseHead = new LayeredHead(84);
            body.Children.Add(inUseHead);
        }
        else
        {
            var initial = AvatarPlaceholder(84, name.Length > 0 ? name[..1].ToUpperInvariant() : "?");
            body.Children.Add(initial);
            // Skin-site characters: the site's own head replaces the initial once it has loaded (fades in).
            if (head is not null) _ = ShowFetchedHead(initial, head);
        }
        var title = Text(name, 19, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 16, 0, 0); title.TextTrimming = TextTrimming.CharacterEllipsis;
        var subtitle = Text(method, 12.5, FontWeights.Normal, secondary: true); subtitle.HorizontalAlignment = HorizontalAlignment.Center; subtitle.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(title); body.Children.Add(subtitle);
        var action = Pill(current ? "正在使用" : "使用此账号", current ? PillKind.Selected : PillKind.Primary, () => { if (!current) { use(); Refresh(); } });
        action.HorizontalAlignment = HorizontalAlignment.Center; action.Margin = new Thickness(0, 22, 0, 0); action.IsEnabled = !current;
        body.Children.Add(action);
        var layered = new Grid();
        layered.Children.Add(body);
        if (deletable && !(current && microsoft)) layered.Children.Add(DeleteButton(delete));
        return layered;
    }

    private static async System.Threading.Tasks.Task ShowFetchedHead(Border slot, Func<System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapSource?>> load)
    {
        System.Windows.Media.Imaging.BitmapSource? image = await load();
        if (image is null) return;
        var picture = new Image { Source = image, Stretch = Stretch.Uniform, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        slot.Background = Brushes.Transparent;
        slot.BorderThickness = default;
        slot.Child = picture;
        if (!MotionPreferences.AllowsAnimation) { picture.Opacity = 1; return; }
        _ = new SpringMotion(picture, 0, value => picture.Opacity = Math.Clamp(value, 0, 1), new SpringParameters(.3, 1)) is var fade ? fade : null;
        fade.Retarget(1);
    }

    private const string TrashGlyph = "M5,7 H19 M10,7 V5 H14 V7 M7,7 L8,20 H16 L17,7 M10.5,11 V16 M13.5,11 V16";

    private FrameworkElement DeleteButton(Action delete)
    {
        var icon = IconButton(TrashGlyph, "删除账号", () => { }, 32);
        var confirm = Pill("确认删除", PillKind.Plain, () => { });
        ((TextBlock)confirm.Content).Foreground = new SolidColorBrush(DarkText ? Color.FromRgb(196, 43, 28) : Color.FromRgb(255, 128, 118));
        confirm.Height = 30; confirm.Opacity = 0; confirm.IsHitTestVisible = false;
        var stretch = new ScaleTransform(.3, 1);
        confirm.RenderTransformOrigin = new Point(1, .5); confirm.RenderTransform = stretch;
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -10, -8, 0) };
        icon.HorizontalAlignment = HorizontalAlignment.Right; confirm.HorizontalAlignment = HorizontalAlignment.Right;
        host.Children.Add(confirm); host.Children.Add(icon);
        // 0 = trash icon, 1 = expanded confirmation pill growing leftwards from the icon.
        var motion = new SpringMotion(host, 0, value =>
        {
            double p = Math.Clamp(value, 0, 1.1);
            stretch.ScaleX = .3 + .7 * p;
            confirm.Opacity = Math.Clamp(value * 1.6 - .2, 0, 1);
            icon.Opacity = Math.Clamp(1 - value * 2, 0, 1);
        }, new SpringParameters(.32, .78));
        var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        void Collapse() { reset.Stop(); confirm.IsHitTestVisible = false; icon.IsHitTestVisible = true; motion.Retarget(0, !MotionPreferences.AllowsAnimation); }
        reset.Tick += (_, _) => Collapse();
        icon.Click += (_, _) => { icon.IsHitTestVisible = false; confirm.IsHitTestVisible = true; motion.Retarget(1, !MotionPreferences.AllowsAnimation); reset.Start(); };
        confirm.Click += (_, _) => { Collapse(); delete(); };
        return host;
    }

    private FrameworkElement AddMicrosoftCard()
    {
        var body = AddCardHead("添加正版账号", "使用 Microsoft 账号登录，登录完成后会出现在这里。");
        var login = Pill("登录 Microsoft", PillKind.Primary, () => { AddMicrosoft(); Refresh(); });
        login.HorizontalAlignment = HorizontalAlignment.Center; login.Margin = new Thickness(0, 22, 0, 0);
        body.Children.Add(login);
        return DeckCard(body, "添加账号");
    }

    private FrameworkElement AddOfflineCard()
    {
        var body = AddCardHead("添加离线账号", "输入游戏用户名，建议只使用英文、数字和下划线。");
        var field = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 0, 12, 0), Height = 38, Margin = new Thickness(0, 18, 0, 0) };
        field.SetResourceReference(Border.BackgroundProperty, "GlassSelectedBrush");
        field.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        var input = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14, MaxLength = 16 };
        input.SetResourceReference(Control.ForegroundProperty, "GlassTextBrush");
        input.SetResourceReference(TextBoxBase.CaretBrushProperty, "GlassTextBrush");
        AutomationProperties.SetName(input, "离线用户名");
        field.Child = input;
        body.Children.Add(field);
        var login = Pill("登录", PillKind.Primary, () => { if (input.Text.Trim().Length == 0) return; UseOffline(input.Text.Trim()); input.Clear(); Refresh(); });
        login.HorizontalAlignment = HorizontalAlignment.Center; login.Margin = new Thickness(0, 16, 0, 0);
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter) login.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, login)); };
        body.Children.Add(login);
        return DeckCard(body, "添加账号");
    }

    private static StackPanel AddCardHead(string title, string hint)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var plus = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), BorderThickness = new Thickness(1.4), HorizontalAlignment = HorizontalAlignment.Center };
        plus.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        var glyph = Glyph(PlusGlyph, 24); glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        plus.Child = glyph;
        body.Children.Add(plus);
        var heading = Text(title, 18, FontWeights.SemiBold); heading.HorizontalAlignment = HorizontalAlignment.Center; heading.Margin = new Thickness(0, 16, 0, 0);
        var note = Text(hint, 12.5, FontWeights.Normal, secondary: true); note.HorizontalAlignment = HorizontalAlignment.Center; note.TextAlignment = TextAlignment.Center; note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(8, 6, 8, 0);
        body.Children.Add(heading); body.Children.Add(note);
        return body;
    }

    private FrameworkElement DeckCard(FrameworkElement body, string name)
    {
        var card = Card(new Thickness(22, 26, 22, 24));
        card.SetValue(GlassLauncherView.PreventWindowDragProperty, true);
        var content = new Grid { Style = (Style)_view.FindResource("GlassAccountDeckContent"), Children = { body } };
        card.Child = content;
        card.Tag = content;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.MaxHeight = 440;
        card.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform(), new TranslateTransform() } };
        card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = .22, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        AutomationProperties.SetName(card, name);
        return card;
    }

    // ---------- Skin sites ----------

    private bool _siteBusy;
    private double _siteProgress;
    private string _siteCaption = "";

    private void SetSiteBusy(bool busy, double progress = 0, string caption = "")
    {
        _siteBusy = busy; _siteProgress = progress; _siteCaption = caption;
        RefreshLoginProgress();
    }

    private void CloseSideFlow()
    {
        if (_sideMode == SideMode.Identity) return;
        GlassSkinSites.EndRoleSelection(this);
        if (_choosing is not null) { _choosing = null; _deckSignature = string.Empty; }
        SetSideMode(SideMode.Identity, BuildIdentityPanel(), forward: false);
        Refresh();
    }

    // Sign-in form: skin site name, Yggdrasil address, account, password and the sign-in button, laid out on the side
    // card where the head was; the fields arrive one after another.
    private void OpenSiteForm(SkinSite? site)
    {
        var form = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        form.Children.Add(FlowHeading(site is null ? "添加验证服务器" : "登录 " + site.Name, "Yggdrasil 外置登录", () => OpenServers(forward: false)));
        (Border nameField, TextBox nameBox) = FormField("名称（可选）", site?.Name ?? "");
        (Border serverField, TextBox serverBox) = FormField("Yggdrasil 地址", site?.Server ?? "");
        (Border userField, TextBox userBox) = FormField("账号 / 邮箱", "");
        (Border passField, PasswordBox passBox) = PasswordField("密码");
        foreach (Border field in new[] { nameField, serverField, userField, passField }) form.Children.Add(field);
        var error = Text("", 12, FontWeights.Normal);
        error.Foreground = new SolidColorBrush(DarkText ? Color.FromRgb(196, 43, 28) : Color.FromRgb(255, 128, 118));
        error.TextWrapping = TextWrapping.Wrap; error.Margin = new Thickness(2, 2, 2, 0); error.Visibility = Visibility.Collapsed;
        form.Children.Add(error);
        var login = Pill("登录", PillKind.Primary, () => { });
        login.HorizontalAlignment = HorizontalAlignment.Stretch; login.Height = 38; login.Margin = new Thickness(0, 12, 0, 0);
        form.Children.Add(login);
        async void Submit()
        {
            if (_siteBusy) return;
            error.Visibility = Visibility.Collapsed;
            string user = userBox.Text.Trim(), password = passBox.Password;
            if (serverBox.Text.Trim().Length == 0) { ShowError(error, "请填写 Yggdrasil 地址。"); return; }
            if (user.Length == 0 || password.Length == 0) { ShowError(error, "请填写账号和密码。"); return; }
            login.IsEnabled = false;
            try
            {
                SetSiteBusy(true, .25, "正在连接验证服务器");
                (string server, string metaName, string register) = await GlassSkinSites.Resolve(serverBox.Text);
                SetSiteBusy(true, .6, "正在登录");
                SkinSiteAccount account = await GlassSkinSites.Authenticate(server, user, password);
                List<SkinSite> sites = GlassSkinSites.Load();
                SkinSite? saved = sites.FirstOrDefault(item => GlassSkinSites.SameServer(item.Server, server));
                if (saved is null) { saved = new SkinSite { Server = server }; sites.Add(saved); }
                string typedName = nameBox.Text.Trim();
                saved.Name = typedName.Length > 0 ? typedName : saved.Name.Length > 0 ? saved.Name : metaName.Length > 0 ? metaName : new Uri(server).Host;
                if (register.Length > 0) saved.Register = register;
                saved.Accounts.RemoveAll(item => string.Equals(item.Username, user, StringComparison.OrdinalIgnoreCase));
                saved.Accounts.Add(account);
                GlassSkinSites.Save(sites);
                SetSiteBusy(false);
                BeginChoosing(saved, account);
            }
            catch (SkinSiteException failure) { SetSiteBusy(false); ShowError(error, failure.Message); }
            catch (Exception failure) { SetSiteBusy(false); Logger.Warn(failure, "外置登录登录失败"); ShowError(error, "登录失败：" + failure.Message); }
            finally { login.IsEnabled = true; }
        }
        login.Click += (_, _) => Submit();
        foreach (Control box in new Control[] { nameBox, serverBox, userBox, passBox })
            box.KeyDown += (_, args) => { if (args.Key == Key.Enter) Submit(); };
        SetSideMode(SideMode.Form, form, forward: true);
        Stagger(form);
        (site is null ? serverBox : userBox).Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => (site is null ? (Control)nameBox : userBox).Focus()));
    }

    private static void ShowError(TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
        if (!MotionPreferences.AllowsAnimation) return;
        var nudge = new TranslateTransform();
        error.RenderTransform = nudge;
        var motion = new SpringMotion(error, 0, value => { nudge.X = 8 * Math.Sin(value * Math.PI * 3) * (1 - value); error.Opacity = Math.Clamp(value * 2, 0, 1); }, new SpringParameters(.4, 1));
        motion.Retarget(1);
    }

    // Children rise and fade in one after another (40 ms apart) on a soft spring.
    private static void Stagger(Panel panel)
    {
        if (!MotionPreferences.AllowsAnimation) return;
        int index = 0;
        foreach (FrameworkElement child in panel.Children.OfType<FrameworkElement>())
        {
            var lift = new TranslateTransform(0, 12);
            child.RenderTransform = lift; child.Opacity = 0;
            FrameworkElement target = child;
            var motion = new SpringMotion(target, 0, value => { lift.Y = 12 * (1 - value); target.Opacity = Math.Clamp(value, 0, 1); }, new SpringParameters(.42, .86));
            var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70 + 40 * index++) };
            delay.Tick += (_, _) => { delay.Stop(); motion.Retarget(1); };
            delay.Start();
        }
    }

    // ---------- Choosing a character ----------

    private void BeginChoosing(SkinSite site, SkinSiteAccount? account)
    {
        GlassSkinSites.BeginRoleSelection(this);
        _choosing = (site, account);
        _deckSignature = string.Empty;
        SetSideMode(SideMode.Characters, WaitingPanel(site, account), forward: true);
        Refresh();
    }

    // Left card while the character is being chosen: an empty head slot breathing softly, "等待选择角色", and the
    // site and account it belongs to.
    private FrameworkElement WaitingPanel(SkinSite site, SkinSiteAccount? account)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var slot = AvatarPlaceholder(84);
        if (MotionPreferences.AllowsAnimation)
            slot.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, .45, TimeSpan.FromSeconds(1.1))
            {
                AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            });
        panel.Children.Add(slot);
        var title = Text("等待选择角色", 19, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 14, 0, 0);
        var note = Text(account is null ? site.Name : (site.Name.Length > 0 ? site.Name + " · " : "") + account.Username, 12.5, FontWeights.Normal, secondary: true);
        note.HorizontalAlignment = HorizontalAlignment.Center; note.Margin = new Thickness(0, 4, 0, 0); note.TextTrimming = TextTrimming.CharacterEllipsis; note.MaxWidth = 220;
        var hint = Text("在右侧选择要使用的角色", 12, FontWeights.Normal, secondary: true);
        hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 18, 0, 0); hint.Opacity = .8;
        panel.Children.Add(title); panel.Children.Add(note); panel.Children.Add(hint);

        var back = IconButton(ChevronLeft, "返回外置登录", () => { if (!_siteBusy) OpenServers(forward: false); }, 30);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.VerticalAlignment = VerticalAlignment.Top;
        back.Margin = new Thickness(4, 26, 0, 0);
        return new Grid { Children = { panel, back } };
    }

    // The account's characters as the deck: the usual stack, each card with the site's head and "选择此角色".
    private void RefreshChoosingDeck(SkinSite site, SkinSiteAccount? account)
    {
        List<SkinSiteCharacter> candidates = (account is null ? GlassSkinSites.Characters(site) :
            account.Profiles.Select(profile => new SkinSiteCharacter(site, account, profile))).ToList();
        string signature = "choose|" + site.Server + "|" + string.Join("|", candidates.Select(character => character.Key));
        if (signature == _deckSignature) return;
        _pendingSignature = null;
        _deckSignature = signature;
        bool animate = MotionPreferences.AllowsAnimation && _deck.IsLoaded;
        foreach (FrameworkElement leaving in _presence.Where(pair => pair.Value.Leaving).Select(pair => pair.Key).ToList()) RemoveCard(leaving);
        bool hadCards = _cards.Count > 0;
        foreach (FrameworkElement old in _cards.ToList()) { if (animate) Leave(old, below: true); else RemoveCard(old); }
        _cards.Clear(); _dots.Children.Clear();
        foreach (SkinSiteCharacter character in candidates)
            _cards.Add(ChoiceCard(character));
        foreach (FrameworkElement card in _cards) _deck.Children.Add(card);
        for (int index = 0; index < _cards.Count; index++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0) };
            dot.SetResourceReference(Shape.FillProperty, "GlassTextBrush");
            _dots.Children.Add(dot);
        }
        _front = 0;
        _deckMotion.Snap(0);
        double start = animate && hadCards ? .16 : 0;
        for (int index = 0; index < _cards.Count; index++) Enter(_cards[index], MotionPreferences.AllowsAnimation ? start + index * .05 : 0);
    }

    private FrameworkElement ChoiceCard(SkinSiteCharacter character)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var slot = AvatarPlaceholder(84);
        _ = ShowFetchedHead(slot, () => GlassSkinSites.Head(character.Site.Server, character.Profile.Id));
        body.Children.Add(slot);
        var title = Text(character.Profile.Name, 19, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 16, 0, 0); title.TextTrimming = TextTrimming.CharacterEllipsis;
        var subtitle = Text(character.Site.Name.Length > 0 ? character.Site.Name : "外置登录角色", 12.5, FontWeights.Normal, secondary: true); subtitle.HorizontalAlignment = HorizontalAlignment.Center; subtitle.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(title); body.Children.Add(subtitle);
        var choose = Pill("选择此角色", PillKind.Primary, () =>
        {
            if (_siteBusy) return;
            _ = ChooseAsync(character);
        });
        choose.HorizontalAlignment = HorizontalAlignment.Center; choose.Margin = new Thickness(0, 22, 0, 0);
        body.Children.Add(choose);
        return DeckCard(body, character.Profile.Name);
    }

    private async System.Threading.Tasks.Task ChooseAsync(SkinSiteCharacter character)
    {
        if (!await UseCharacterAsync(character, fromSideFlow: false)) return;
        _rebuildInstantly = false;
        CloseSideFlow();
    }

    // The deck title changes with a short fade (切换账号 ↔ 切换角色).
    private void SetDeckTitle(string text)
    {
        if (_deckTitle.Text == text) return;
        if (!MotionPreferences.AllowsAnimation || !_deckTitle.IsLoaded) { _deckTitle.Text = text; return; }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            _deckTitle.Text = text;
            _deckTitle.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        _deckTitle.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void UseSite(SkinSite site)
    {
        if (_siteBusy) return;
        List<SkinSite> sites = GlassSkinSites.Load();
        SkinSite? saved = sites.FirstOrDefault(item => GlassSkinSites.SameServer(item.Server, site.Server));
        if (saved is null) return;
        BeginChoosing(saved, null);
    }

    private void UseCharacter(SkinSiteCharacter character) => _ = UseCharacterAsync(character, fromSideFlow: _sideMode != SideMode.Identity);

    // Binds the account's token to the character, saves it and makes it the account in use through the original
    // Authlib-Injector login page (which then loads its skin like any other account).
    private async System.Threading.Tasks.Task<bool> UseCharacterAsync(SkinSiteCharacter character, bool fromSideFlow)
    {
        if (_siteBusy) return false;
        try
        {
            SetSiteBusy(true, .4, "正在切换到 " + character.Profile.Name);
            await GlassSkinSites.UseCharacter(character);
            SetSiteBusy(true, .85, "即将完成");
            _source.RefreshPage(false, false);
            ModMain.FrmLoginAuthSkin?.Reload(false);
            PageLaunchLeft.SkinAuth.Start(IsForceRestart: true);
            SetSiteBusy(false);
            if (fromSideFlow) CloseSideFlow();
            else Refresh();
            return true;
        }
        catch (SkinSiteException failure) { SetSiteBusy(false); ModMain.Hint("切换外置登录角色失败：" + failure.Message, ModMain.HintType.Red); }
        catch (Exception failure) { SetSiteBusy(false); Logger.Error(failure, "切换外置登录角色失败", LogBehavior.Toast); }
        return false;
    }

    private (Border Field, TextBox Box) FormField(string placeholder, string value)
    {
        var box = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14, Text = value, Template = BareFieldTemplate(typeof(TextBox)), FocusVisualStyle = null };
        box.SetResourceReference(Control.ForegroundProperty, "GlassTextBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "GlassTextBrush");
        AutomationProperties.SetName(box, placeholder);
        Border field = FieldShell(box, placeholder, () => box.Text.Length == 0);
        box.TextChanged += (_, _) => field.Tag = box.Text.Length == 0;
        return (field, box);
    }

    private (Border Field, PasswordBox Box) PasswordField(string placeholder)
    {
        var box = new PasswordBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14, Template = BareFieldTemplate(typeof(PasswordBox)), FocusVisualStyle = null };
        box.SetResourceReference(Control.ForegroundProperty, "GlassTextBrush");
        box.SetResourceReference(PasswordBox.CaretBrushProperty, "GlassTextBrush");
        AutomationProperties.SetName(box, placeholder);
        Border field = FieldShell(box, placeholder, () => box.Password.Length == 0);
        box.PasswordChanged += (_, _) => field.Tag = box.Password.Length == 0;
        return (field, box);
    }

    // Just the text host: no border, no focus rectangle (the glass field shows focus with its own outline).
    private static ControlTemplate BareFieldTemplate(Type type)
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(Control.PaddingProperty, new Thickness(0));
        return new ControlTemplate(type) { VisualTree = host };
    }

    // Rounded glass field with an in-field placeholder that fades as soon as there is text.
    private static Border FieldShell(Control box, string placeholder, Func<bool> empty)
    {
        var field = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(12, 0, 12, 0), Height = 38, Margin = new Thickness(0, 0, 0, 8) };
        field.SetResourceReference(Border.BackgroundProperty, "GlassSelectedBrush");
        field.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        var hint = Text(placeholder, 13.5, FontWeights.Normal, secondary: true);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.IsHitTestVisible = false; hint.Margin = new Thickness(2, 0, 0, 0);
        var layers = new Grid();
        layers.Children.Add(hint); layers.Children.Add(box);
        field.Child = layers;
        void Sync() => hint.Opacity = empty() ? 1 : 0;
        Sync();
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(Border));
        descriptor.AddValueChanged(field, (_, _) => Sync());
        box.GotKeyboardFocus += (_, _) => field.SetResourceReference(Border.BorderBrushProperty, "GlassTextBrush");
        box.LostKeyboardFocus += (_, _) => field.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        return field;
    }

    // ---------- Actions (all through the original pages) ----------

    private static IReadOnlyList<string> SavedMicrosoftAccounts()
    {
        try
        {
            string json = Settings.Get<string>("LoginMsJson");
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            return Newtonsoft.Json.Linq.JObject.Parse(json).Properties().Select(property => property.Name).Where(name => name.Length > 0).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    // Switching to another saved Microsoft account signs out of the current one (it stays saved, and the original
    // login page reloads its list), then selects that account in the original list and logs in.
    private void UseMicrosoft(string name) => ViaLoginPage(page =>
    {
        int index = page.ComboAccounts.Items.Cast<object>().ToList().FindIndex(item => item is MyListItem row && row.Title == name);
        if (index < 0) return;
        page.ComboAccounts.SelectedIndex = index;
        page.BtnLogin.PerformClick();
    });

    private void AddMicrosoft() => ViaLoginPage(page =>
    {
        int index = page.ComboAccounts.Items.Cast<object>().ToList().FindIndex(item => item is MyComboBoxItem);
        if (index >= 0) page.ComboAccounts.SelectedIndex = index;
        page.BtnLogin.PerformClick();
    });

    private void ViaLoginPage(Action<PageLoginMs> act)
    {
        if (MicrosoftSignedIn && ModMain.FrmLoginMsSkin is { } skin)
        {
            skin.BtnExit.PerformClick();
            skin.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { if (ModMain.FrmLoginMs is PageLoginMs page) act(page); Refresh(); }));
        }
        else if (ModMain.FrmLoginMs is PageLoginMs page) act(page);
    }

    // The offline account in use is the first entry of the original saved name list (LoginLegacyName); the original
    // page only writes it on launch. Switching moves the name to the front of that list and updates the page.
    private void UseOffline(string name)
    {
        name = name.Replace("¨", string.Empty).Trim();
        if (name.Length == 0) return;
        try
        {
            var names = Settings.UpdateLegacyNames(items =>
            {
                items.RemoveAll(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
                items.Insert(0, name);
            });
            if (ModMain.FrmLoginLegacy is PageLoginLegacy page)
            {
                page.ComboName.ItemsSource = names;
                page.ComboName.Text = name;
            }
            _source.RefreshPage(false, false);
        }
        catch (Exception error) { Logger.Error(error, "切换离线账号失败", LogBehavior.Toast); }
    }

    private ContextMenu? SkinMenu() => AuthSelected
        ? (AuthSignedIn ? ModMain.FrmLoginAuthSkin?.Skin.ContextMenu : null)
        : NideSelected
            ? (NideSignedIn ? ModMain.FrmLoginNideSkin?.Skin.ContextMenu : null)
            : MicrosoftSelected
                ? (MicrosoftSignedIn ? ModMain.FrmLoginMsSkin?.BtnSkin.ContextMenu : null)
                : null; // offline: the skin library replaces the original offline-skin menu

    // Changing the skin or cape lives in the skin library now; the account menu keeps the rest (save, refresh, …).
    private static readonly HashSet<string> LibraryHandled = new(StringComparer.Ordinal) { "修改皮肤", "修改披风", "刷新披风列表", "更换披风", "更换皮肤", "保存皮肤文件", "另存为" };

    private void OpenSkinMenu()
    {
        if (SkinMenu() is not ContextMenu menu) return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            if (item.Header is string header && LibraryHandled.Contains(header)) item.Visibility = Visibility.Collapsed;
        // A separator left with nothing after it (or two in a row) goes too.
        var entries = menu.Items.OfType<Control>().ToList();
        for (int index = 0; index < entries.Count; index++)
            if (entries[index] is Separator separator)
            {
                bool before = entries.Take(index).Any(entry => entry is MenuItem { Visibility: Visibility.Visible });
                bool after = entries.Skip(index + 1).TakeWhile(entry => entry is not Separator).Any(entry => entry.Visibility == Visibility.Visible);
                separator.Visibility = before && after ? Visibility.Visible : Visibility.Collapsed;
            }
        GlassActionMenu.FromContextMenu(menu, _skin, DarkText).IsOpen = true;
    }

    private void OpenEditMenu()
    {
        if (AuthSignedIn) { ModMain.FrmLoginAuthSkin?.BtnEdit.PerformClick(); Refresh(); return; }
        if (NideSignedIn) { ModMain.FrmLoginNideSkin?.BtnEdit.PerformClick(); Refresh(); return; }
        if (ModMain.FrmLoginMsSkin?.BtnEdit.ContextMenu is ContextMenu menu) GlassActionMenu.FromContextMenu(menu, _edit, DarkText).IsOpen = true;
    }

    private void SignOut()
    {
        if (SiteMode) { if (!_siteBusy) OpenServers(); return; }
        if (AuthSignedIn) ModMain.FrmLoginAuthSkin?.BtnExit.PerformClick();
        else if (NideSignedIn) ModMain.FrmLoginNideSkin?.BtnExit.PerformClick();
        else if (MicrosoftSignedIn) ModMain.FrmLoginMsSkin?.BtnExit.PerformClick();
        Refresh();
    }

    private bool DarkText => (_view.TryFindResource("GlassTextBrush") as SolidColorBrush)?.Color is { } color && color.R + color.G + color.B < 300;

    // ---------- Building blocks ----------

    private static Border AvatarPlaceholder(double size, string glyph = "?") {
        var slot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center };
        slot.SetResourceReference(Border.BackgroundProperty, "GlassSelectedBrush");
        slot.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        var mark = Text(glyph, 30, FontWeights.SemiBold, secondary: true);
        mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Center;
        slot.Child = mark;
        return slot;
    }

    /// <summary>Skin head tile: the shared 2D head with its raised outer layer (<see cref="GlassSkinHead"/>).</summary>
    private sealed class LayeredHead : Grid
    {
        private readonly Image _head = new() { Stretch = Stretch.Uniform };
        private readonly Border _placeholder;

        internal LayeredHead(double size)
        {
            Width = Height = size;
            HorizontalAlignment = HorizontalAlignment.Center;
            _placeholder = AvatarPlaceholder(size);
            Children.Add(_placeholder);
            RenderOptions.SetBitmapScalingMode(_head, BitmapScalingMode.NearestNeighbor);
            Children.Add(_head);
            _head.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Direction = 270, Opacity = .28, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        }

        internal void Show(ImageSource? head)
        {
            if (!ReferenceEquals(_head.Source, head)) _head.Source = head;
            _placeholder.Visibility = head is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal static Border Card(Thickness padding)
    {
        var card = new GlassSurface { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Padding = padding, BlurRadius = 36, TintOpacity = .72, HighlightOpacity = 0 };
        card.SetResourceReference(Border.BackgroundProperty, "GlassCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        return card;
    }

    private static FrameworkElement AvatarFrame(Image image, double size, double radius)
    {
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        image.Width = image.Height = size;
        var frame = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Child = image };
        frame.SetResourceReference(Border.BackgroundProperty, "GlassSelectedBrush");
        image.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        return frame;
    }

    internal static TextBlock Text(string value, double size, FontWeight weight, bool secondary = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight, FontFamily = new FontFamily("Segoe UI Variable Display, Microsoft YaHei UI, Segoe UI") };
        text.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "GlassSecondaryTextBrush" : "GlassTextBrush");
        return text;
    }

    internal static Path Glyph(string data, double size)
    {
        var path = new Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush");
        return path;
    }

    internal enum PillKind { Plain, Selected, Primary }

    internal static Button Pill(string text, PillKind kind, Action click)
    {
        var label = Text(text, 13.5, kind == PillKind.Primary ? FontWeights.SemiBold : FontWeights.Normal);
        if (kind == PillKind.Primary) label.SetResourceReference(TextBlock.ForegroundProperty, "GlassAccentTextBrush");
        var button = ChromeButton(label, new CornerRadius(17), new Thickness(16, 0, 16, 0), 34,
            kind switch { PillKind.Primary => "GlassAccentBrush", PillKind.Selected => "GlassListSelectedBrush", _ => "GlassSelectedBrush" });
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, text);
        return button;
    }

    internal static Button IconButton(string glyph, string tip, Action click, double size = 38)
    {
        var button = ChromeButton(Glyph(glyph, size * .45), new CornerRadius(size / 2), new Thickness(), size, "GlassSelectedBrush");
        button.Width = size; button.ToolTip = tip;
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, tip);
        return button;
    }

    // Plain glass button: tinted fill, light hover lift and a small press scale.
    internal static Button ChromeButton(UIElement content, CornerRadius radius, Thickness padding, double height, string fillKey)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, radius);
        chrome.SetResourceReference(Border.BackgroundProperty, fillKey);
        chrome.SetValue(Border.PaddingProperty, padding);
        chrome.SetValue(UIElement.RenderTransformOriginProperty, new Point(.5, .5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .86, "Chrome"));
        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(UIElement.RenderTransformProperty, new ScaleTransform(.96, .96), "Chrome"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .7, "Chrome"));
        template.Triggers.Add(hover); template.Triggers.Add(press); template.Triggers.Add(disabled);
        return new Button { Template = template, Content = content, Height = height, Cursor = Cursors.Hand, Focusable = true };
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? item = source; item is not null; item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (ReferenceEquals(item, ancestor)) return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GlassSkinSites.EndRoleSelection(this);
        foreach (Presence state in _presence.Values) state.Motion.Dispose();
        _presence.Clear();
        _customLoginSession?.Dispose();
        _customLoginSession = null;
        _deckMotion.Dispose();
        _library.Dispose();
        _actionsMotion.Dispose();
        _typesScrollMotion.Dispose();
        foreach (SpringMotion motion in _chevronMotions.Values) motion.Dispose();
        _settle?.Stop();
    }
}
