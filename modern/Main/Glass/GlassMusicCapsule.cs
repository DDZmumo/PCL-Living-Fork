// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using Path = System.Windows.Shapes.Path;

namespace PCL;

/// <summary>The shared body sample and chrome for the dock and its playlist.</summary>
public sealed class GlassMusicSurface : Grid
{
    private readonly GlassSceneFrost _frost = new();

    internal GlassSurface Chrome { get; } = new() {
        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(5),
        TintOpacity = .78, HighlightOpacity = 0, PaintsWallpaper = false
    };
    internal ContentControl Host { get; } = new() { Focusable = false, IsTabStop = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    public GlassMusicSurface()
    {
        Background = Brushes.Transparent;
        Chrome.SetResourceReference(Border.BackgroundProperty, "GlassCardBrush");
        Chrome.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        Chrome.Child = Host;
        Children.Add(_frost);
        Children.Add(Chrome);
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearBackdrop(); };
        Unloaded += (_, _) => ClearBackdrop();
    }

    internal void UpdateBackdrop(Border frame, FrameworkElement scene)
    {
        if (Chrome.IsTransparencyActive) _frost.UpdateBackdrop(this, frame, scene, Chrome.CornerRadius.TopLeft);
        else ClearBackdrop();
    }

    internal void ClearBackdrop() => _frost.ClearBackdrop();
}

/// <summary>
/// Background music player, docked in the window's bottom-right corner — the window controls' twin (same chrome, same
/// notch, standing 10 px proud of the body). Three rows:
/// <list type="bullet">
/// <item>the disc and progress ring beside the track name and its place in the list;</item>
/// <item>a progress bar with elapsed and total time, dragged to seek;</item>
/// <item>shuffle · previous · play / pause (filled) · next · track list · minimise.</item>
/// </list>
/// The disc or the list button opens a card above the dock for choosing a track (playlist and volume). Minimising
/// folds the dock back into the corner and brings back the original round music button, which restores it.
/// All playback goes through the original music runtime (<see cref="ModMusic"/>).
/// </summary>
internal sealed class GlassMusicPlayer
{
    private const string NoteGlyph = "M9,16 V6 L19,4 V14 M9,9 L19,7 M9,16 A2.5,2.5 0 1 1 4,16 A2.5,2.5 0 1 1 9,16 Z M19,14 A2.5,2.5 0 1 1 14,14 A2.5,2.5 0 1 1 19,14 Z";
    private const string PlayGlyph = "M8.5,5.5 L18.5,12 L8.5,18.5 Z";
    private const string PauseGlyph = "M8.5,5.5 V18.5 M15.5,5.5 V18.5";
    private const string NextGlyph = "M6.5,5.5 L15,12 L6.5,18.5 Z M17.5,5.5 V18.5";
    private const string PreviousGlyph = "M17.5,5.5 L9,12 L17.5,18.5 Z M6.5,5.5 V18.5";
    private const string ShuffleGlyph = "M4,7 H7 C11,7 13,17 17,17 H20 M17,14 L20,17 L17,20 M4,17 H7 C8.5,17 9.6,15.6 10.6,14 M13.4,10 C14.4,8.4 15.5,7 17,7 H20 M17,4 L20,7 L17,10";
    private const string ListGlyph = "M5,7 H19 M5,12 H19 M5,17 H13";
    private const string MinimiseGlyph = "M7,10 L12,15 L17,10";
    private const string SpeakerGlyph = "M5,9.5 H8 L12,6 V18 L8,14.5 H5 Z M15.5,9 C16.5,10 16.5,14 15.5,15 M18,7 C20.3,9.3 20.3,14.7 18,17";
    private const double Width = 252, Disc = 40;

    // On the home page, expanding / minimising is kept (for the session). Elsewhere the player rests as the round button
    // and opens only until the next click outside it. The track list is always one-shot.
    private static bool _homeMinimised;
    private bool _onHome = true, _awayExpanded, _cardOpen;
    private bool _minimised => _onHome ? _homeMinimised : !_awayExpanded;

    private readonly GlassLauncherView _view;
    private readonly StackPanel _bar = new() { Width = Width, Margin = new Thickness(4, 4, 4, 2) };
    private readonly Path _ring = new() { StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    private readonly TextBlock _title = Label(13, FontWeights.SemiBold), _place = Label(11, FontWeights.Normal, secondary: true);
    private readonly TextBlock _elapsed = Label(10.5, FontWeights.Normal, secondary: true), _total = Label(10.5, FontWeights.Normal, secondary: true);
    private readonly Grid _seek = new() { Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Border _seekFill = new() { Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _seekKnob = new() { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5) };
    private readonly ScaleTransform _knobScale = new(.6, .6);
    private readonly Path _play = Glyph(PlayGlyph, 15, 1.7, fill: true);
    private readonly Button _shuffle;

    private readonly GlassMusicSurface _card;
    private readonly TextBlock _cardHeading = Label(12, FontWeights.SemiBold, secondary: true);
    private readonly Grid _volume = new() { Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _volumeFill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new();
    private readonly SpringMotion _cardMotion, _knobMotion;
    private readonly ScaleTransform _cardScale = new(1, 1);
    private readonly TranslateTransform _cardRise = new();
    private string _listSignature = "";
    private bool _shown, _playing, _seeking;
    private double _lastRingProgress = double.NaN;

    /// <summary>True while there is music.</summary>
    internal bool IsShown => _shown;
    /// <summary>True once the shell's loading is over (the player never shows over the splash).</summary>
    internal bool Ready
    {
        get => _ready;
        set { if (_ready == value) return; _ready = value; MinimisedChanged?.Invoke(this, EventArgs.Empty); }
    }
    private bool _ready;
    internal bool IsReady => Ready && _shown;
    internal bool IsMinimised => _minimised;
    /// <summary>True while the dock itself is out (music, loading over, not minimised).</summary>
    internal bool IsDocked => _shown && Ready && !_minimised;

    /// <summary>Whether the home page is in front (expanding is kept there; elsewhere it lasts until a click outside).</summary>
    internal bool OnHome
    {
        get => _onHome;
        set
        {
            if (_onHome == value) return;
            _onHome = value;
            _awayExpanded = false;
            SetCard(false);
            MinimisedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A click landed outside the dock, the card and the round button: close the one-shot parts.</summary>
    internal void Dismiss()
    {
        SetCard(false);
        if (!_onHome && _awayExpanded) SetMinimised(true);
    }

    /// <summary>True while the track list card is open (or opening).</summary>
    internal bool IsCardOpen => _cardOpen;
    /// <summary>Raised when the track list card opens or closes.</summary>
    internal event EventHandler? CardChanged;

    /// <summary>Closes the track list (e.g. when a dialog takes over).</summary>
    internal void CloseCard(bool immediate = false) => SetCard(false, immediate);
    /// <summary>Raised when the player is minimised to (or restored from) the round button.</summary>
    internal event EventHandler? MinimisedChanged;
    /// <summary>The bar for the dock's chrome.</summary>
    internal FrameworkElement Bar => _bar;
    /// <summary>The track list card (placed right above the dock, matching its width).</summary>
    internal FrameworkElement Card => _card;

    internal GlassMusicPlayer(GlassLauncherView view)
    {
        _view = view;
        _bar.Children.Add(BuildHeader());
        _bar.Children.Add(BuildSeek());
        _shuffle = Circle(Glyph(ShuffleGlyph, 15, 1.6), "随机播放", ToggleShuffle);
        _bar.Children.Add(BuildControls());

        _card = new GlassMusicSurface {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(1, 1) };
        _card.Chrome.Padding = new Thickness(12, 12, 12, 10);
        _card.RenderTransform = new TransformGroup { Children = { _cardScale, _cardRise } };
        _card.SetValue(GlassLauncherView.PreventWindowDragProperty, true);
        _card.Host.Content = BuildCard();
        _view.AddMusicDockCompanion(_card);

        _cardMotion = new SpringMotion(_card, 0, value =>
        {
            double v = Math.Clamp(value, 0, 1);
            _card.Opacity = v;
            _cardScale.ScaleX = _cardScale.ScaleY = .92 + .08 * Math.Clamp(value, 0, 1.1);
            _cardRise.Y = 14 * (1 - Math.Min(value, 1));
            _card.IsHitTestVisible = _cardOpen && v > .5;
            if (v < .01 && !_cardOpen)
                _card.Visibility = Visibility.Collapsed;
            _view.RefreshMusicDockRegions();
        }, new SpringParameters(.44, .8));
        _knobMotion = new SpringMotion(_seekKnob, .6, value => _knobScale.ScaleX = _knobScale.ScaleY = value, new SpringParameters(.28, .7));
    }

    // ---------- Building ----------

    // Disc with a progress ring, beside the name and the place in the list.
    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var disc = new Grid { Width = Disc, Height = Disc, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "选择曲目" };
        var face = new Ellipse { Margin = new Thickness(4) };
        face.SetResourceReference(Shape.FillProperty, "GlassSelectedBrush");
        var track = new Ellipse { StrokeThickness = 2.2, Opacity = .2, Margin = new Thickness(1.1) };
        track.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush");
        _ring.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush");
        var note = Glyph(NoteGlyph, 17, 1.5);
        note.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush");
        disc.Children.Add(face); disc.Children.Add(track); disc.Children.Add(_ring); disc.Children.Add(note);
        disc.MouseLeftButtonUp += (_, _) => SetCard(!_cardOpen);
        AutomationProperties.SetName(disc, "选择曲目");
        header.Children.Add(disc);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        _title.TextTrimming = TextTrimming.CharacterEllipsis; _place.Margin = new Thickness(0, 2, 0, 0);
        names.Children.Add(_title); names.Children.Add(_place);
        Grid.SetColumn(names, 1); header.Children.Add(names);
        return header;
    }

    // Progress bar with elapsed and total time at its ends; the knob grows while the pointer is over it.
    private FrameworkElement BuildSeek()
    {
        var row = new Grid { Margin = new Thickness(2, 0, 2, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        _elapsed.HorizontalAlignment = HorizontalAlignment.Left;
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(_elapsed);
        _seekFill.SetResourceReference(Border.BackgroundProperty, "GlassTextBrush");
        _seekKnob.SetResourceReference(Shape.FillProperty, "GlassTextBrush");
        _seekKnob.RenderTransform = _knobScale;
        _seek.Children.Add(Track(3, .22)); _seek.Children.Add(_seekFill); _seek.Children.Add(_seekKnob);
        AutomationProperties.SetName(_seek, "播放进度");
        _seek.MouseEnter += (_, _) => _knobMotion.Retarget(1.15, !MotionPreferences.AllowsAnimation);
        _seek.MouseLeave += (_, _) => { if (!_seek.IsMouseCaptured) _knobMotion.Retarget(.6, !MotionPreferences.AllowsAnimation); };
        _seek.MouseLeftButtonDown += (_, args) => { _seeking = true; _seek.CaptureMouse(); ShowSeek(Fraction(args)); args.Handled = true; };
        _seek.MouseMove += (_, args) => { if (_seek.IsMouseCaptured) ShowSeek(Fraction(args)); };
        _seek.MouseLeftButtonUp += (_, args) =>
        {
            if (!_seek.IsMouseCaptured) return;
            _seek.ReleaseMouseCapture(); _seeking = false;
            if (!_seek.IsMouseOver) _knobMotion.Retarget(.6, !MotionPreferences.AllowsAnimation);
            ModMusic.MusicSeek(Fraction(args));
            Refresh();
        };
        Grid.SetColumn(_seek, 1); row.Children.Add(_seek);
        Grid.SetColumn(_total, 2); row.Children.Add(_total);
        return row;
    }

    private double Fraction(MouseEventArgs args) => Math.Clamp(args.GetPosition(_seek).X / Math.Max(1, _seek.ActualWidth), 0, 1);

    // shuffle · previous · play (filled) · next · list · minimise, evenly spaced.
    private FrameworkElement BuildControls()
    {
        var row = new Grid();
        Button play = Circle(_play, "播放 / 暂停", () => { ModMusic.MusicControlPause(); Refresh(); });
        play.SetResourceReference(Control.BackgroundProperty, "GlassSelectedBrush");
        Button[] buttons =
        {
            _shuffle,
            Circle(Glyph(PreviousGlyph, 14, 1.6, fill: true), "上一首", () => { ModMusic.MusicControlPrevious(); Refresh(); }),
            play,
            Circle(Glyph(NextGlyph, 14, 1.6, fill: true), "下一首", () => { ModMusic.MusicControlNext(); Refresh(); }),
            Circle(Glyph(ListGlyph, 15, 1.7), "播放列表", () => SetCard(!_cardOpen)),
            Circle(Glyph(MinimiseGlyph, 14, 1.8), "收起", () => SetMinimised(true))
        };
        for (int index = 0; index < buttons.Length; index++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(buttons[index], index);
            buttons[index].HorizontalAlignment = HorizontalAlignment.Center;
            row.Children.Add(buttons[index]);
        }
        return row;
    }

    private FrameworkElement BuildCard()
    {
        var root = new StackPanel();
        _cardHeading.Margin = new Thickness(4, 0, 0, 8);
        root.Children.Add(_cardHeading);
        var volumeRow = new Grid { Margin = new Thickness(4, 0, 4, 8) };
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition());
        var speaker = Glyph(SpeakerGlyph, 15, 1.5); speaker.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush");
        speaker.Margin = new Thickness(0, 0, 10, 0); speaker.Opacity = .8;
        volumeRow.Children.Add(speaker);
        _volumeFill.SetResourceReference(Border.BackgroundProperty, "GlassTextBrush");
        _volume.Children.Add(Track(4, .22)); _volume.Children.Add(_volumeFill);
        AutomationProperties.SetName(_volume, "音量");
        _volume.MouseLeftButtonDown += (_, args) => { _volume.CaptureMouse(); SetVolume(args.GetPosition(_volume).X / Math.Max(1, _volume.ActualWidth)); args.Handled = true; };
        _volume.MouseMove += (_, args) => { if (_volume.IsMouseCaptured) SetVolume(args.GetPosition(_volume).X / Math.Max(1, _volume.ActualWidth)); };
        _volume.MouseLeftButtonUp += (_, _) => _volume.ReleaseMouseCapture();
        Grid.SetColumn(_volume, 1); volumeRow.Children.Add(_volume);
        root.Children.Add(volumeRow);
        var scroller = new ScrollViewer { Content = _list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SmoothScroll.SetIsEnabled(scroller, true);
        root.Children.Add(scroller);
        return root;
    }

    // ---------- State ----------

    /// <summary>Re-reads the music runtime (called from the shell's periodic refresh).</summary>
    internal void Refresh(bool updateDetails = true)
    {
        bool available = ModMusic.MusicCount > 0;
        if (available != _shown) { _shown = available; MinimisedChanged?.Invoke(this, EventArgs.Empty); }
        bool docked = available && Ready && !_minimised;
        _view.SetMusicDockVisible(docked);
        if (!docked)
        {
            SetCard(false);
            return;
        }
        if (!updateDetails) return;
        bool playing = ModMusic.MusicState == ModMusic.MusicStates.Play;
        if (playing != _playing) { _playing = playing; _play.Data = Geometry.Parse(playing ? PauseGlyph : PlayGlyph); }
        string name = ModMusic.MusicCurrentName;
        string shown = string.IsNullOrEmpty(name) ? "背景音乐" : name;
        if (_title.Text != shown) _title.Text = shown;
        IReadOnlyList<string> tracks = ModMusic.MusicTracks;
        string current = ModMusic.MusicCurrentPath;
        int index = 0;
        while (index < tracks.Count && tracks[index] != current) index++;
        _place.Text = (playing ? "正在播放" : "已暂停") + (index < tracks.Count ? $" · 第 {index + 1} / {tracks.Count} 首" : $" · 共 {tracks.Count} 首");
        double progress = Math.Clamp(ModMusic.MusicProgress, 0, 1);
        if (!_seeking) ShowSeekPosition(progress, ModMusic.MusicPosition);
        DrawRing(progress);
        _total.Text = Time(ModMusic.MusicDuration);
        _shuffle.Opacity = Settings.Get<bool>("UiMusicRandom") ? 1 : .45;
        if (_cardOpen) RefreshCard(tracks);
    }

    private void RefreshCard(IReadOnlyList<string> tracks)
    {
        string current = ModMusic.MusicCurrentPath;
        _cardHeading.Text = $"播放列表 · 共 {tracks.Count} 首";
        _volumeFill.Width = Math.Max(0, _volume.ActualWidth * Math.Clamp(Settings.Get<int>("UiMusicVolume") / 1000d, 0, 1));
        string signature = string.Join("|", tracks) + "#" + current + "#" + _playing;
        if (signature == _listSignature) return;
        _listSignature = signature;
        _list.Children.Clear();
        foreach (string track in tracks)
        {
            bool active = track == current;
            var name = Label(12.5, active ? FontWeights.SemiBold : FontWeights.Normal, secondary: !active);
            name.Text = System.IO.Path.GetFileNameWithoutExtension(track); name.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            if (active) { Path mark = Glyph(_playing ? NoteGlyph : PauseGlyph, 11, 1.4); mark.SetResourceReference(Shape.StrokeProperty, "GlassTextBrush"); row.Children.Add(mark); }
            Grid.SetColumn(name, 1); row.Children.Add(name);
            string chosen = track;
            _list.Children.Add(Row(row, name.Text, () => { ModMusic.MusicPlayTrack(chosen); Refresh(); }, active));
        }
    }

    private void ShowSeek(double fraction) => ShowSeekPosition(fraction, TimeSpan.FromMilliseconds(ModMusic.MusicDuration.TotalMilliseconds * fraction));

    private void ShowSeekPosition(double fraction, TimeSpan position)
    {
        double width = Math.Max(0, _seek.ActualWidth);
        _seekFill.Width = width * fraction;
        _seekKnob.Margin = new Thickness(Math.Clamp(width * fraction - 4.5, 0, Math.Max(0, width - 9)), 0, 0, 0);
        _elapsed.Text = Time(position);
    }

    private void DrawRing(double progress)
    {
        if (Math.Abs(progress - _lastRingProgress) < .0005) return;
        _lastRingProgress = progress;
        const double radius = Disc / 2 - 1.1;
        var center = new Point(Disc / 2, Disc / 2);
        if (progress <= .001) { _ring.Data = null; return; }
        double angle = Math.Min(progress, .9999) * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false };
        figure.Segments.Add(new ArcSegment(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        _ring.Data = new PathGeometry(new[] { figure });
    }

    private static string Time(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void ToggleShuffle()
    {
        Settings.Set("UiMusicRandom", !Settings.Get<bool>("UiMusicRandom"));
        ModMusic.MusicReshuffle();
        ModMain.Hint(Settings.Get<bool>("UiMusicRandom") ? "已开启随机播放" : "已改为按顺序播放");
        Refresh();
    }

    private static void SetVolume(double fraction)
    {
        int value = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 1000);
        if (Settings.Get<int>("UiMusicVolume") != value) Settings.Set("UiMusicVolume", value);
    }

    /// <summary>Minimises to the original round button (or restores the dock from it).</summary>
    internal void SetMinimised(bool minimised)
    {
        if (_minimised == minimised) return;
        if (_onHome) _homeMinimised = minimised; else _awayExpanded = !minimised;
        if (minimised) SetCard(false);
        Refresh();
        MinimisedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetCard(bool open, bool immediate = false)
    {
        bool next = open && !_minimised && _shown && Ready;
        if (next == _cardOpen && (!immediate || _card.Visibility != Visibility.Visible)) return;
        _cardOpen = next;
        if (_cardOpen)
        {
            _card.Visibility = Visibility.Visible;
            _listSignature = "";
            RefreshCard(ModMusic.MusicTracks);
        }
        _cardMotion.Retarget(_cardOpen ? 1 : 0, immediate || !MotionPreferences.AllowsAnimation);
        CardChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- Building blocks ----------

    private static Border Track(double height, double opacity)
    {
        var bar = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Opacity = opacity, VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "GlassTextBrush");
        return bar;
    }

    private static TextBlock Label(double size, FontWeight weight, bool secondary = false)
    {
        var text = new TextBlock { FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI") };
        text.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "GlassSecondaryTextBrush" : "GlassTextBrush");
        return text;
    }

    // Glyph drawn in its button's foreground (stroked, or filled for the play / skip shapes).
    private static Path Glyph(string data, double size, double thickness, bool fill = false)
    {
        var path = new Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var foreground = new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) };
        path.SetBinding(Shape.StrokeProperty, foreground);
        if (fill) path.SetBinding(Shape.FillProperty, foreground);
        return path;
    }

    // The window controls' own round button style (36 px, hover fill, press feedback).
    private static Button Circle(UIElement content, string name, Action click)
    {
        var button = new Button { Content = content, ToolTip = name };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GlassCircleButton");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    // Track row: full width, highlighted when it is the track in use.
    private static Button Row(UIElement content, string name, Action click, bool active)
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        chrome.SetValue(Border.PaddingProperty, new Thickness(6, 7, 8, 7));
        chrome.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .8, "Chrome"));
        template.Triggers.Add(hover);
        var button = new Button { Template = template, Content = content, Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (active) button.SetResourceReference(Control.BackgroundProperty, "GlassSelectedBrush");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
