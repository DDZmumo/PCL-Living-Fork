// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using PCLCS.Applications;
using PCLLF.Wpf.Glass;
using PCLLF.Wpf.Motion;
using static PCL.GlassAccountWorkspace;

namespace PCL;

/// <summary>
/// Skin library page of the account workspace. Side card: the selected skin in 3D (drag to turn), its name, the
/// classic / slim switch and "apply to account". Scene: 皮肤 / 披风 tabs over a grid of full-body cards, with import,
/// folder, rename and delete. Applying follows the account: offline characters get the local texture service,
/// Microsoft accounts upload (capes: choose one the account owns), third-party logins open the site's wardrobe.
/// All files and account operations go through <see cref="SkinLibraryApplicationService"/>.
/// </summary>
internal sealed class GlassSkinLibrary : IDisposable
{
    private const string FolderGlyph = "M3,7 V18 A1,1 0 0 0 4,19 H20 A1,1 0 0 0 21,18 V9 A1,1 0 0 0 20,8 H12 L10,5 H4 A1,1 0 0 0 3,6 Z";
    private const string ImportGlyph = "M12,4 V15 M7,10 L12,15 L17,10 M5,19 H19";
    private const string RenameGlyph = "M4,20 H8 L19,9 15,5 4,16 Z";
    private const string TrashGlyph = "M5,7 H19 M10,7 V5 H14 V7 M7,7 L8,20 H16 L17,7";
    private const string RefreshGlyph = "M19,8 A8,8 0 1 0 20,13 M19,3 V8 H14";
    private const string AccountGlyph = "M12,12 A4,4 0 1 0 12,4 A4,4 0 1 0 12,12 M4,20 A8,6 0 0 1 20,20";

    private readonly SkinLibraryApplicationService _library;
    private readonly Func<SkinLibraryTarget, CancellationToken, Task<IReadOnlyList<MicrosoftCape>>> _readMicrosoftCapes;
    private readonly Func<bool> _darkText;
    private readonly Action _back;
    private readonly GlassSkinViewer _viewer = new() { Height = 250 };
    private readonly TextBlock _name = Text("", 19, FontWeights.SemiBold), _meta = Text("", 12.5, FontWeights.Normal, secondary: true);
    private readonly TextBlock _target = Text("", 12, FontWeights.Normal, secondary: true);
    private readonly StackPanel _models = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _apply, _resetOfflineSkin, _offlineRolePicker;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _grid = new() { Margin = new Thickness(0, 2, 0, 12) };
    private readonly StackPanel _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    // The empty-library message sits on a frosted card so it reads clearly over any wallpaper.
    private Border _emptyCard = null!;
    private readonly Dictionary<string, (uint[] Pixels, BitmapSource Texture, SkinModel Model)> _skins = new();
    private readonly Dictionary<string, BitmapSource> _capes = new();
    private SkinTextureKind _kind = SkinTextureKind.Skin;
    private string? _selectedSkin, _selectedCape;
    private IReadOnlyList<SkinLibraryEntry> _entries = Array.Empty<SkinLibraryEntry>();
    private IReadOnlyList<MicrosoftCape>? _microsoftCapes;
    private SkinLibraryAccount _account = new(null, null, false, new(), null);
    private string _accountKey = "";
    private string? _offlinePlayer;
    private CancellationTokenSource? _work;
    private bool _busy;

    internal FrameworkElement Side { get; }
    internal FrameworkElement Scene { get; }

    internal GlassSkinLibrary(Func<bool> darkText, Action back,
        Func<SkinLibraryTarget, CancellationToken, Task<IReadOnlyList<MicrosoftCape>>>? readMicrosoftCapes = null,
        SkinLibraryApplicationService? library = null)
    {
        _darkText = darkText; _back = back;
        _library = library ?? LauncherApplicationServices.SkinLibrary;
        _readMicrosoftCapes = readMicrosoftCapes ?? _library.GetMicrosoftCapesAsync;
        _apply = Pill("应用到账号", PillKind.Primary, () => _ = ApplyAsync());
        _offlineRolePicker = Pill("选择离线角色", PillKind.Plain, () => BuildOfflineRoleMenu().Show());
        _offlineRolePicker.Visibility = Visibility.Collapsed;
        _offlineRolePicker.Margin = new Thickness(0, 0, 0, 8);
        ((TextBlock)_offlineRolePicker.Content).TextTrimming = TextTrimming.CharacterEllipsis;
        _resetOfflineSkin = Pill("恢复默认皮肤", PillKind.Plain, ResetOfflineSkin);
        _resetOfflineSkin.Visibility = Visibility.Collapsed;
        _resetOfflineSkin.Margin = new Thickness(0, 6, 0, 0);
        Side = BuildSide();
        Scene = BuildScene();
    }

    // ---------- Layout ----------

    private FrameworkElement BuildSide()
    {
        var root = new Grid { Margin = new Thickness(16, 18, 16, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Top-left back button, then the title: the way out is where every page puts it.
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var back = IconButton("M14,6 L8,12 14,18", "返回游戏账号", () => _back(), 32);
        back.Margin = new Thickness(-4, 0, 10, 0); back.VerticalAlignment = VerticalAlignment.Center;
        var title = Text("皮肤库", 20, FontWeights.SemiBold); title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(back); header.Children.Add(title);
        root.Children.Add(header);

        var stage = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // A soft pool of light under the model.
        var floor = new Ellipse { Width = 150, Height = 26, Margin = new Thickness(0, -26, 0, 0), IsHitTestVisible = false,
            Fill = new RadialGradientBrush(Color.FromArgb(70, 0, 0, 0), Colors.Transparent) };
        stage.Children.Add(_viewer); stage.Children.Add(floor);
        _name.HorizontalAlignment = HorizontalAlignment.Center; _name.Margin = new Thickness(0, 8, 0, 0); _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _meta.HorizontalAlignment = HorizontalAlignment.Center; _meta.Margin = new Thickness(0, 3, 0, 0);
        stage.Children.Add(_name); stage.Children.Add(_meta); stage.Children.Add(_models);
        Grid.SetRow(stage, 1); root.Children.Add(stage);

        var footer = new StackPanel();
        _apply.HorizontalAlignment = HorizontalAlignment.Stretch; _apply.Height = 38;
        _target.HorizontalAlignment = HorizontalAlignment.Center; _target.Margin = new Thickness(0, 8, 0, 0); _target.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_offlineRolePicker);
        footer.Children.Add(_apply);
        footer.Children.Add(_resetOfflineSkin);
        footer.Children.Add(_target);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }

    private FrameworkElement BuildScene()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        // The right end stays clear of the window's minimise / close capsule, which sits over the scene's top-right corner.
        var header = new DockPanel { Margin = new Thickness(4, 6, 116, 14), LastChildFill = false };
        var title = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var frost = new GlassSurface();
        frost.SetResourceReference(FrameworkElement.StyleProperty, "GlassPageTitleFrost");
        title.Children.Add(frost);
        title.Children.Add(Text("皮肤库", 22, FontWeights.SemiBold));
        header.Children.Add(title);
        header.Children.Add(Frosted(_tabs));
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        foreach (Button tool in new[]
        {
            IconButton(ImportGlyph, "导入 PNG", Import, 34),
            IconButton(AccountGlyph, "导入当前账号的皮肤", () => ImportCurrent(), 34),
            IconButton(FolderGlyph, "打开文件夹", () => ModBase.OpenExplorer(_library.DirectoryFor(_kind)), 34),
            IconButton(RefreshGlyph, "刷新", () => Reload(refreshMicrosoftCapes: true), 34)
        }) { tool.Margin = new Thickness(tools.Children.Count == 0 ? 0 : 4, 0, 0, 0); tools.Children.Add(tool); }
        FrameworkElement toolBar = Frosted(tools);
        DockPanel.SetDock(toolBar, Dock.Right);
        header.Children.Add(toolBar);
        root.Children.Add(header);

        var scroller = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(4, 0, 4, 0) };
        SmoothScroll.SetIsEnabled(scroller, true);
        var body = new Grid();
        _emptyCard = Card(new Thickness(32, 26, 32, 26));
        _emptyCard.Child = _empty;
        _emptyCard.HorizontalAlignment = HorizontalAlignment.Center; _emptyCard.VerticalAlignment = VerticalAlignment.Center;
        _emptyCard.MinWidth = 320; _emptyCard.Visibility = Visibility.Collapsed;
        body.Children.Add(scroller); body.Children.Add(_emptyCard);
        Grid.SetRow(body, 1); root.Children.Add(body);
        // Files dropped on the page are imported.
        root.AllowDrop = true;
        root.Drop += (_, args) => { if (args.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files); };
        return root;
    }

    // A frosted capsule behind a row of pills or icon buttons, so they read over any wallpaper.
    private static FrameworkElement Frosted(Panel row)
    {
        var capsule = Card(new Thickness(4));
        capsule.CornerRadius = new CornerRadius(21);
        capsule.VerticalAlignment = VerticalAlignment.Center;
        if (row.Parent is Panel owner) owner.Children.Remove(row);
        capsule.Child = row;
        if (row is StackPanel { Children.Count: > 0 } && row.Children[^1] is FrameworkElement last) last.Margin = new Thickness(last.Margin.Left, 0, 0, 0);
        return capsule;
    }

    // ---------- State ----------

    /// <summary>Called when the page opens: re-reads the account and the library.</summary>
    internal void Open()
    {
        _offlinePlayer = null;
        _accountKey = "";
        RefreshAccount(updateContent: false);
        Reload();
    }

    /// <summary>Periodic refresh while the page is open: follows account switches.</summary>
    internal void Refresh() => RefreshAccount();

    private void RefreshAccount(bool updateContent = true)
    {
        var previous = _account.Target;
        try { _account = _library.ReadAccount(_offlinePlayer); }
        catch (Exception error) { Logger.Warn(error, "读取皮肤库账号失败"); return; }
        if (_account.Target is not { Provider: AccountProvider.Offline } role || role.Name != _offlinePlayer)
            _offlinePlayer = null;
        bool offlineChanged = previous != _account.Target &&
            (previous?.Provider == AccountProvider.Offline || _account.Target?.Provider == AccountProvider.Offline);
        if (offlineChanged)
        {
            _selectedSkin = _account.OfflineAppearance.SkinId;
            _selectedCape = _account.OfflineAppearance.CapeId;
        }
        string key = _account.Target is { } t ? t.Provider + "|" + t.Name + "|" + t.ProfileId : "";
        if (key == _accountKey) return;
        _accountKey = key;
        _microsoftCapes = null; _capesRequested = false;
        _work?.Cancel();
        if (updateContent)
        {
            if (offlineChanged || _kind == SkinTextureKind.Cape) Reload(); else RefreshSide();
        }
    }

    private GlassActionMenu BuildOfflineRoleMenu()
    {
        var menu = new GlassActionMenu(_offlineRolePicker, _darkText()) { Placement = PlacementMode.Bottom };
        foreach (string player in SkinLibraryApplicationService.ReadOfflinePlayers())
            menu.AddCommand("角色 " + player, () =>
            {
                _offlinePlayer = player;
                RefreshAccount();
            }, check: _account.Target?.Name == player);
        return menu;
    }

    private string ProviderLabel(AccountProvider provider) => provider switch
    {
        AccountProvider.Offline => "离线", AccountProvider.Microsoft => "正版", AccountProvider.Auth => "外置登录", AccountProvider.Nide => "统一通行证", _ => ""
    };

    private void RefreshTabs()
    {
        _tabs.Children.Clear();
        foreach ((string label, SkinTextureKind kind) in new[] { ("皮肤", SkinTextureKind.Skin), ("披风", SkinTextureKind.Cape) })
        {
            var tab = Pill(label, _kind == kind ? PillKind.Primary : PillKind.Plain, () => { if (_kind != kind) { _kind = kind; Reload(); } });
            tab.Margin = new Thickness(0, 0, 6, 0);
            _tabs.Children.Add(tab);
        }
    }

    private bool MicrosoftCapes => _kind == SkinTextureKind.Cape && _account.Target?.Provider == AccountProvider.Microsoft;

    private void Reload(bool refreshMicrosoftCapes = false)
    {
        RefreshTabs();
        _work?.Cancel();
        _work = new CancellationTokenSource();
        CancellationToken token = _work.Token;
        if (MicrosoftCapes) { _ = LoadMicrosoftCapes(token, refreshMicrosoftCapes); return; }
        try { _entries = _library.List(_kind); }
        catch (Exception error) { Logger.Warn(error, "读取皮肤库失败"); _entries = Array.Empty<SkinLibraryEntry>(); }
        if (_kind == SkinTextureKind.Skin && (_selectedSkin is null || _entries.All(entry => entry.Id != _selectedSkin)))
            _selectedSkin = _account.OfflineAppearance.SkinId is { } used && _entries.Any(entry => entry.Id == used) ? used : _entries.FirstOrDefault()?.Id;
        if (_kind == SkinTextureKind.Cape && _selectedCape is not null && _entries.All(entry => entry.Id != _selectedCape)) _selectedCape = null;
        BuildCards(token);
        RefreshSide();
    }

    // ---------- Cards ----------

    private void BuildCards(CancellationToken token)
    {
        _grid.Children.Clear();
        bool empty = _entries.Count == 0;
        _empty.Children.Clear();
        _emptyCard.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            var heading = Text(_kind == SkinTextureKind.Skin ? "皮肤库是空的" : "还没有披风", 18, FontWeights.SemiBold); heading.HorizontalAlignment = HorizontalAlignment.Center;
            var note = Text(_kind == SkinTextureKind.Skin ? "导入 64×64 或 64×32 的 PNG 皮肤，也可以直接拖到这里" : "导入 64×32（或其整数倍）的 PNG 披风", 12.5, FontWeights.Normal, secondary: true);
            note.HorizontalAlignment = HorizontalAlignment.Center; note.Margin = new Thickness(0, 6, 0, 16);
            var import = Pill("导入", PillKind.Primary, Import); import.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.Children.Add(heading); _empty.Children.Add(note); _empty.Children.Add(import);
            StaggeredEntrance.Play(new UIElement[] { _emptyCard });
            return;
        }
        if (_kind == SkinTextureKind.Cape && _account.Target?.Provider == AccountProvider.Offline)
            _grid.Children.Add(NoCapeCard(_account.OfflineAppearance.CapeId is null, () => ApplyOfflineCape(null)));
        foreach (SkinLibraryEntry entry in _entries) _grid.Children.Add(EntryCard(entry, token));
        StaggeredEntrance.Play(_grid.Children.OfType<UIElement>().Take(18));
    }

    private FrameworkElement EntryCard(SkinLibraryEntry entry, CancellationToken token)
    {
        bool skin = entry.Kind == SkinTextureKind.Skin;
        bool inUse = skin ? _account.OfflineAppearance.SkinId == entry.Id : _account.OfflineAppearance.CapeId == entry.Id;
        bool selected = skin ? _selectedSkin == entry.Id : _selectedCape == entry.Id;
        var picture = new Image { Width = skin ? 62 : 60, Height = skin ? 120 : 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(picture, skin ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        _ = LoadCardPicture(entry, picture, token);
        var name = Text(entry.Name, 13.5, FontWeights.SemiBold); name.HorizontalAlignment = HorizontalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Margin = new Thickness(0, 10, 0, 0);
        string detail = skin ? (entry.Model == SkinModel.Slim ? "纤细" : "经典") + " · " + entry.Width + "×" + entry.Height : entry.Width + "×" + entry.Height;
        var meta = Text(inUse ? "使用中 · " + detail : detail, 11.5, FontWeights.Normal, secondary: true); meta.HorizontalAlignment = HorizontalAlignment.Center; meta.Margin = new Thickness(0, 2, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(picture); stack.Children.Add(name); stack.Children.Add(meta);
        var card = CardShell(stack, selected, entry.Name, () => Select(entry));
        AttachTools(card, entry, name, meta);
        return card;
    }

    private async Task LoadCardPicture(SkinLibraryEntry entry, Image picture, CancellationToken token)
    {
        try
        {
            BitmapSource image = await Task.Run(() =>
            {
                SkinLibraryPreview preview = _library.LoadPreview(entry.Id, entry.Kind);
                if (entry.Kind == SkinTextureKind.Cape) return GlassSkinTexture.CapeFront(preview.Texture);
                uint[] pixels = GlassSkinTexture.Normalise(preview.Texture);
                lock (_skins) _skins[entry.Id] = (pixels, preview.Texture, preview.Model);
                return GlassSkinTexture.FrontViewLayered(pixels, preview.Model);
            }, token);
            if (token.IsCancellationRequested) return;
            if (entry.Kind == SkinTextureKind.Cape) _capes[entry.Id] = (await Task.Run(() => _library.LoadPreview(entry.Id, entry.Kind).Texture, token));
            picture.Source = image;
            if (entry.Id == _selectedSkin || entry.Id == _selectedCape) RefreshViewer();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Logger.Info(error, "读取皮肤预览失败：" + entry.Name); }
    }

    // Card chrome: frosted rounded tile, accent outline when selected, a small lift on hover and press feedback.
    private Border CardShell(UIElement content, bool selected, string name, Action click)
    {
        var card = Card(new Thickness(12, 14, 12, 12));
        card.Width = 132; card.Margin = new Thickness(0, 0, 12, 12); card.Cursor = Cursors.Hand;
        card.Child = content;
        if (selected) { card.BorderThickness = new Thickness(2); card.SetResourceReference(Border.BorderBrushProperty, "GlassAccentBrush"); }
        var lift = new TranslateTransform();
        card.RenderTransform = lift;
        var motion = new SpringMotion(card, 0, value => lift.Y = -3 * value, new SpringParameters(.3, .8));
        card.MouseEnter += (_, _) => motion.Retarget(1, !MotionPreferences.AllowsAnimation);
        card.MouseLeave += (_, _) => motion.Retarget(0, !MotionPreferences.AllowsAnimation);
        card.MouseLeftButtonUp += (_, args) => { if (args.OriginalSource is DependencyObject source && FindButton(source, card)) return; click(); };
        AutomationProperties.SetName(card, name);
        card.SetValue(PCLLF.Wpf.Glass.GlassLauncherView.PreventWindowDragProperty, true);
        return card;
    }

    private static bool FindButton(DependencyObject source, DependencyObject stop)
    {
        for (DependencyObject? item = source; item is not null && !ReferenceEquals(item, stop); item = VisualTreeHelper.GetParent(item))
            if (item is ButtonBase or TextBoxBase) return true;
        return false;
    }

    // Rename and delete appear on hover at the card's top corners; delete asks once more ("删除?") before removing.
    private void AttachTools(Border card, SkinLibraryEntry entry, TextBlock name, TextBlock meta)
    {
        var host = new Grid();
        var content = (UIElement)card.Child;
        card.Child = null;
        host.Children.Add(content);
        var rename = IconButton(RenameGlyph, "重命名", () => BeginRename(card, entry, name), 30);
        var delete = IconButton(TrashGlyph, "删除", () => { }, 30);
        rename.HorizontalAlignment = HorizontalAlignment.Left; delete.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (Button tool in new[] { rename, delete }) { tool.VerticalAlignment = VerticalAlignment.Top; tool.Margin = new Thickness(-4, -6, -4, 0); /* inside the card, so the pointer never leaves it while on them */ tool.Opacity = 0; tool.IsHitTestVisible = false; host.Children.Add(tool); }
        var reveal = new SpringMotion(host, 0, value => { foreach (Button tool in new[] { rename, delete }) { tool.Opacity = Math.Clamp(value, 0, 1); tool.IsHitTestVisible = value > .5; } }, new SpringParameters(.28, 1));
        bool armed = false;
        string original = meta.Text;
        var disarm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        void Disarm() { disarm.Stop(); armed = false; meta.Text = original; meta.SetResourceReference(TextBlock.ForegroundProperty, "GlassSecondaryTextBrush"); }
        disarm.Tick += (_, _) => Disarm();
        delete.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true; meta.Text = "再点一次删除";
                meta.Foreground = new SolidColorBrush(_darkText() ? Color.FromRgb(196, 43, 28) : Color.FromRgb(255, 128, 118));
                disarm.Start(); return;
            }
            Disarm();
            Remove(entry, card);
        };
        card.MouseEnter += (_, _) => reveal.Retarget(1, !MotionPreferences.AllowsAnimation);
        card.MouseLeave += (_, _) =>
        {
            if (card.IsMouseOver) return; // moving onto the card's own buttons is still "on the card"
            reveal.Retarget(0, !MotionPreferences.AllowsAnimation);
            if (armed) Disarm();
        };
        card.Child = host;
    }

    private void BeginRename(Border card, SkinLibraryEntry entry, TextBlock name)
    {
        if (name.Parent is not StackPanel stack) return;
        int index = stack.Children.IndexOf(name);
        var box = new TextBox
        {
            Text = entry.Name, FontSize = 13.5, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = name.Margin, Padding = new Thickness(4, 2, 4, 2),
            BorderThickness = new Thickness(1), Background = Brushes.Transparent
        };
        box.SetResourceReference(Control.ForegroundProperty, "GlassTextBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "GlassOutlineBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "GlassTextBrush");
        stack.Children.RemoveAt(index); stack.Children.Insert(index, box);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return; done = true;
            string value = box.Text.Trim();
            stack.Children.Remove(box); stack.Children.Insert(index, name);
            if (!commit || value.Length == 0 || value == entry.Name) return;
            try
            {
                SkinLibraryEntry renamed = _library.Rename(entry.Id, value, entry.Kind);
                if (_selectedSkin == entry.Id) _selectedSkin = renamed.Id;
                if (_selectedCape == entry.Id) _selectedCape = renamed.Id;
                RefreshAccount(); Reload();
            }
            catch (Exception error) { ModMain.Hint("重命名失败：" + error.Message, ModMain.HintType.Red); }
        }
        box.KeyDown += (_, args) => { if (args.Key == Key.Enter) Finish(true); else if (args.Key == Key.Escape) Finish(false); };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    private void Remove(SkinLibraryEntry entry, Border card)
    {
        try { _library.Remove(entry.Id, entry.Kind); }
        catch (Exception error) { ModMain.Hint("删除失败：" + error.Message, ModMain.HintType.Red); return; }
        if (_selectedSkin == entry.Id) _selectedSkin = null;
        if (_selectedCape == entry.Id) _selectedCape = null;
        // The card lifts away and fades, then the grid settles.
        var lift = card.RenderTransform as TranslateTransform ?? new TranslateTransform();
        card.RenderTransform = lift; card.IsHitTestVisible = false;
        var leave = new SpringMotion(card, 0, value => { lift.Y = -40 * value; card.Opacity = 1 - Math.Clamp(value, 0, 1); }, new SpringParameters(.32, 1));
        leave.Retarget(1, !MotionPreferences.AllowsAnimation);
        var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MotionPreferences.AllowsAnimation ? 260 : 0) };
        later.Tick += (_, _) => { later.Stop(); RefreshAccount(); Reload(); };
        later.Start();
    }

    private Border NoCapeCard(bool active, Action choose)
    {
        var slot = new Border { Width = 60, Height = 96, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.4), HorizontalAlignment = HorizontalAlignment.Center };
        slot.SetResourceReference(Border.BorderBrushProperty, "GlassOutlineBrush");
        var cross = Glyph("M7,7 L17,17 M17,7 L7,17", 18); cross.HorizontalAlignment = HorizontalAlignment.Center; cross.VerticalAlignment = VerticalAlignment.Center; cross.Opacity = .6;
        slot.Child = cross;
        var title = Text("不显示披风", 13.5, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 10, 0, 0);
        var meta = Text(active ? "使用中" : "隐藏披风", 11.5, FontWeights.Normal, secondary: true); meta.HorizontalAlignment = HorizontalAlignment.Center; meta.Margin = new Thickness(0, 2, 0, 0);
        var stack = new StackPanel(); stack.Children.Add(slot); stack.Children.Add(title); stack.Children.Add(meta);
        return CardShell(stack, active, "不显示披风", choose);
    }

    // Microsoft capes come from the account itself: one card per cape it owns, plus "不显示披风".
    private async Task LoadMicrosoftCapes(CancellationToken token, bool refresh)
    {
        if (_microsoftCapes is null) _grid.Children.Clear();
        _emptyCard.Visibility = Visibility.Collapsed;
        if (_account.Target is not { } target) return;
        try
        {
            var capes = _microsoftCapes;
            if (refresh || capes is null) capes = await _readMicrosoftCapes(target, token);
            if (token.IsCancellationRequested || _account.Target != target) return;
            _microsoftCapes = capes;
            ShowMicrosoftCapes(target, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && _account.Target == target)
                ModMain.Hint("读取正版披风失败：" + error.Message, ModMain.HintType.Red);
        }
    }

    private void ShowMicrosoftCapes(SkinLibraryTarget target, CancellationToken token)
    {
        _grid.Children.Clear();
        IReadOnlyList<MicrosoftCape> capes = _microsoftCapes ?? Array.Empty<MicrosoftCape>();
        _grid.Children.Add(NoCapeCard(capes.All(cape => !cape.Active), () => _ = ApplyMicrosoftCape(target, null)));
        foreach (MicrosoftCape cape in capes)
        {
            var picture = new Image { Width = 60, Height = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
            if (cape.Texture is { } uri) _ = LoadRemoteCape(uri, picture, token);
            var title = Text(cape.Name, 13.5, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis; title.Margin = new Thickness(0, 10, 0, 0);
            var meta = Text(cape.Active ? "使用中" : "正版披风", 11.5, FontWeights.Normal, secondary: true); meta.HorizontalAlignment = HorizontalAlignment.Center; meta.Margin = new Thickness(0, 2, 0, 0);
            var stack = new StackPanel(); stack.Children.Add(picture); stack.Children.Add(title); stack.Children.Add(meta);
            MicrosoftCape chosen = cape;
            _grid.Children.Add(CardShell(stack, cape.Active, cape.Name, () => _ = ApplyMicrosoftCape(target, chosen.Id)));
        }
        StaggeredEntrance.Play(_grid.Children.OfType<UIElement>().Take(18));
        RefreshSide();
    }

    private static readonly HttpClient CapeHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<Uri, BitmapSource> RemoteCapes = new();
    private async Task LoadRemoteCape(Uri uri, Image? picture, CancellationToken token)
    {
        try
        {
            if (!RemoteCapes.TryGetValue(uri, out BitmapSource? image))
            {
                byte[] bytes = await CapeHttp.GetByteArrayAsync(uri, token);
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new System.IO.MemoryStream(bytes); bitmap.EndInit(); bitmap.Freeze();
                RemoteCapes[uri] = image = bitmap;
            }
            if (picture is not null) picture.Source = GlassSkinTexture.CapeFront(image);
            RefreshViewer();
        }
        catch (Exception error) { Logger.Info(error, "读取正版披风图片失败"); }
    }

    // ---------- Side card ----------

    private void Select(SkinLibraryEntry entry)
    {
        if (entry.Kind == SkinTextureKind.Skin) _selectedSkin = entry.Id; else _selectedCape = entry.Id;
        foreach (Border card in _grid.Children.OfType<Border>())
        {
            bool on = AutomationProperties.GetName(card) == entry.Name;
            card.BorderThickness = new Thickness(on ? 2 : 1);
            card.SetResourceReference(Border.BorderBrushProperty, on ? "GlassAccentBrush" : "GlassOutlineBrush");
        }
        RefreshSide();
    }

    private SkinLibraryEntry? SelectedEntry =>
        _entries.FirstOrDefault(entry => entry.Id == (_kind == SkinTextureKind.Skin ? _selectedSkin : _selectedCape));

    private void RefreshSide()
    {
        SkinLibraryEntry? entry = SelectedEntry;
        RefreshViewer();
        _models.Children.Clear();
        if (_kind == SkinTextureKind.Skin && entry is not null)
        {
            _name.Text = entry.Name;
            _meta.Text = entry.Width + "×" + entry.Height + (_account.OfflineAppearance.SkinId == entry.Id ? " · 使用中" : "");
            foreach ((string label, SkinModel model) in new[] { ("经典", SkinModel.Classic), ("纤细", SkinModel.Slim) })
            {
                var pill = Pill(label, entry.Model == model ? PillKind.Primary : PillKind.Plain, () => SetModel(entry, model));
                pill.Height = 30; pill.Margin = new Thickness(3, 0, 3, 0);
                _models.Children.Add(pill);
            }
        }
        else if (_kind == SkinTextureKind.Cape && entry is not null) { _name.Text = entry.Name; _meta.Text = "披风 · " + entry.Width + "×" + entry.Height; }
        else if (MicrosoftCapes) { _name.Text = "正版披风"; _meta.Text = "在右侧选择要显示的披风"; }
        else { _name.Text = _entries.Count == 0 ? "还没有" + (_kind == SkinTextureKind.Skin ? "皮肤" : "披风") : "选择一个" + (_kind == SkinTextureKind.Skin ? "皮肤" : "披风"); _meta.Text = ""; }

        SkinLibraryTarget? target = _account.Target;
        bool offline = target?.Provider == AccountProvider.Offline;
        bool showRolePicker = offline || target is null && SkinLibraryApplicationService.ReadOfflinePlayers().Length > 0;
        _offlineRolePicker.Visibility = showRolePicker ? Visibility.Visible : Visibility.Collapsed;
        _offlineRolePicker.IsEnabled = !_busy;
        ((TextBlock)_offlineRolePicker.Content).Text = offline ? "离线角色 · " + target!.Name + " ▾" : "选择离线角色 ▾";
        _target.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        _target.Text = target is null ? "当前没有可以应用的角色" : "应用到：" + target.Name + " · " + ProviderLabel(target.Provider);
        string label2 = target?.Provider switch
        {
            AccountProvider.Auth => "在网站衣柜中上传",
            AccountProvider.Nide => "由统一通行证网站管理",
            AccountProvider.Microsoft when _kind == SkinTextureKind.Cape => "在右侧选择正版披风",
            AccountProvider.Microsoft => "上传到正版账号",
            AccountProvider.Offline => "应用到此角色",
            _ => "应用到账号"
        };
        ((TextBlock)_apply.Content).Text = _busy ? "正在应用…" : label2;
        _apply.IsEnabled = !_busy && target is not null && target.Provider != AccountProvider.Nide && !MicrosoftCapes &&
            (target.Provider == AccountProvider.Auth || entry is not null);
        _resetOfflineSkin.Visibility = _kind == SkinTextureKind.Skin && target?.Provider == AccountProvider.Offline &&
            _account.OfflineAppearance.SkinId is not null ? Visibility.Visible : Visibility.Collapsed;
        _resetOfflineSkin.IsEnabled = !_busy;
    }

    private void RefreshViewer()
    {
        string? skinId = _selectedSkin ?? _account.OfflineAppearance.SkinId;
        (uint[] Pixels, BitmapSource Texture, SkinModel Model) skin;
        bool has;
        lock (_skins) has = skinId is not null && _skins.TryGetValue(skinId, out skin);
        if (!has && skinId is not null) { _ = PreloadSkin(skinId); }
        lock (_skins)
        {
            if (skinId is not null && _skins.TryGetValue(skinId, out skin))
            {
                SkinModel model = _entries.FirstOrDefault(entry => entry.Id == skinId)?.Model ?? skin.Model;
                _viewer.Show(skin.Pixels, model, ViewerCape());
                return;
            }
        }
        _viewer.Show(null, SkinModel.Classic, null);
    }

    // The cape shown on the model: the one picked in the cape tab, else the cape the account currently wears.
    private BitmapSource? ViewerCape()
    {
        if (_kind == SkinTextureKind.Cape && _selectedCape is { } picked && _capes.TryGetValue(picked, out BitmapSource? chosen)) return chosen;
        if (_account.Target?.Provider == AccountProvider.Microsoft)
        {
            if (_microsoftCapes is null) {
                if (!MicrosoftCapes && _account.Target is { } target) _ = PreloadMicrosoftCapes(target);
                return null;
            }
            if (_microsoftCapes.FirstOrDefault(cape => cape.Active)?.Texture is not { } uri) return null;
            if (RemoteCapes.TryGetValue(uri, out BitmapSource? active)) return active;
            _ = LoadRemoteCape(uri, null, CancellationToken.None);
            return null;
        }
        if (_account.OfflineAppearance.CapeId is { } worn)
        {
            if (_capes.TryGetValue(worn, out BitmapSource? cape)) return cape;
            _ = PreloadCape(worn);
        }
        return null;
    }

    private bool _capesRequested;
    private async Task PreloadMicrosoftCapes(SkinLibraryTarget target)
    {
        if (_capesRequested) return;
        _capesRequested = true;
        try
        {
            var capes = await _readMicrosoftCapes(target, CancellationToken.None);
            if (_account.Target != target || _microsoftCapes is not null) return;
            _microsoftCapes = capes;
            RefreshViewer();
        }
        catch (Exception error) { Logger.Info(error, "读取正版披风失败"); }
    }

    private async Task PreloadCape(string id)
    {
        if (!_preloading.Add("cape:" + id)) return;
        try
        {
            _capes[id] = await Task.Run(() => _library.LoadPreview(id, SkinTextureKind.Cape).Texture);
            RefreshViewer();
        }
        catch (Exception) { }
        finally { _preloading.Remove("cape:" + id); }
    }

    private readonly HashSet<string> _preloading = new();
    private async Task PreloadSkin(string id)
    {
        if (!_preloading.Add(id)) return;
        try
        {
            await Task.Run(() =>
            {
                SkinLibraryPreview preview = _library.LoadPreview(id, SkinTextureKind.Skin);
                uint[] pixels = GlassSkinTexture.Normalise(preview.Texture);
                lock (_skins) _skins[id] = (pixels, preview.Texture, preview.Model);
            });
            RefreshViewer();
        }
        catch (Exception) { }
        finally { _preloading.Remove(id); }
    }

    private void SetModel(SkinLibraryEntry entry, SkinModel model)
    {
        if (entry.Model == model) return;
        try { _library.SetModel(entry.Id, model); }
        catch (Exception error) { ModMain.Hint("切换模型失败：" + error.Message, ModMain.HintType.Red); return; }
        Reload();
    }

    // ---------- Import ----------

    private void Import()
    {
        var picker = new OpenFileDialog { Title = _kind == SkinTextureKind.Skin ? "导入皮肤" : "导入披风", Filter = "PNG 图片|*.png", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(Scene)) == true) ImportFiles(picker.FileNames);
    }

    private void ImportFiles(IEnumerable<string> files)
    {
        try
        {
            SkinImportResult result = _library.Import(files, _kind);
            if (result.Entries.Count > 0)
            {
                if (_kind == SkinTextureKind.Skin) _selectedSkin = result.Entries[^1].Id; else _selectedCape = result.Entries[^1].Id;
                ModMain.Hint("已导入 " + result.Entries.Count + " 个" + (_kind == SkinTextureKind.Skin ? "皮肤" : "披风"), ModMain.HintType.Green);
            }
            foreach (SkinImportError error in result.Errors.Take(3))
                ModMain.Hint(System.IO.Path.GetFileName(error.File) + "：" + error.Message, ModMain.HintType.Red);
        }
        catch (Exception error) { ModMain.Hint("导入失败：" + error.Message, ModMain.HintType.Red); }
        Reload();
    }

    private void ImportCurrent()
    {
        if (_account.Target is not { } target || !_account.CanImportCurrent)
        {
            ModMain.Hint("当前账号的皮肤还没有加载，或者没有可以导入的皮肤", ModMain.HintType.Red);
            return;
        }
        try
        {
            SkinLibraryEntry entry = _library.ImportCurrent(target, target.Name);
            // The account's own skin keeps its arm width: slim (Alex) skins leave the outer arm columns empty.
            try
            {
                uint[] pixels = GlassSkinTexture.Normalise(_library.LoadPreview(entry.Id).Texture);
                if (GlassSkinTexture.LooksSlim(pixels) && entry.Model != SkinModel.Slim) entry = _library.SetModel(entry.Id, SkinModel.Slim);
            }
            catch (Exception error) { Logger.Info(error, "判断皮肤模型失败"); }
            _kind = SkinTextureKind.Skin; _selectedSkin = entry.Id;
            ModMain.Hint("已把 " + target.Name + " 的皮肤导入皮肤库", ModMain.HintType.Green);
        }
        catch (Exception error) { ModMain.Hint("导入失败：" + error.Message, ModMain.HintType.Red); }
        Reload();
    }

    // ---------- Apply ----------

    private async Task ApplyAsync()
    {
        if (_busy || _account.Target is not { } target) return;
        SkinLibraryEntry? entry = SelectedEntry;
        try
        {
            if (target.Provider == AccountProvider.Offline)
            {
                if (entry is null) return;
                _library.SetOfflineTexture(target, _kind, entry.Id);
                ModMain.Hint("已应用到 " + target.Name + "，启动游戏后生效", ModMain.HintType.Green);
            }
            else if (target.Provider == AccountProvider.Auth && _kind == SkinTextureKind.Cape)
            {
                if (_account.Wardrobe is { } wardrobe) { ModBase.OpenWebsite(wardrobe.AbsoluteUri); ModMain.Hint("请在网站的衣柜中更换披风"); }
            }
            else
            {
                if (entry is null && target.Provider != AccountProvider.Auth) return;
                // Uploading replaces the skin on the real Microsoft account: always ask first.
                if (target.Provider == AccountProvider.Microsoft && entry is not null &&
                    ModMain.MyMsgBox($"要把「{entry.Name}」上传为正版账号 {target.Name} 的皮肤吗？" + Environment.NewLine + "上传后会替换这个账号现在的皮肤。", "上传皮肤", "上传", "取消") != 1)
                    return;
                SetBusy(true);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                Uri? wardrobe = entry is null ? _account.Wardrobe : await _library.ApplySkinAsync(target, entry.Id, cancel.Token);
                if (wardrobe is not null)
                {
                    ModBase.OpenWebsite(wardrobe.AbsoluteUri);
                    if (entry is not null) ModBase.OpenExplorer(_library.DirectoryFor(SkinTextureKind.Skin));
                    ModMain.Hint("已打开网站衣柜，请在那里上传这个皮肤（皮肤文件夹也已打开）");
                }
                else { RefreshAccountSkin(target.Provider); ModMain.Hint("皮肤已上传到正版账号", ModMain.HintType.Green); }
            }
        }
        catch (OperationCanceledException) { ModMain.Hint("应用超时，请稍后再试", ModMain.HintType.Red); }
        catch (Exception error) { ModMain.Hint("应用失败：" + error.Message, ModMain.HintType.Red); }
        finally { SetBusy(false); }
        _accountKey = ""; RefreshAccount(updateContent: false); Reload();
    }

    private void ResetOfflineSkin()
    {
        if (_busy || _kind != SkinTextureKind.Skin || _account.Target is not { Provider: AccountProvider.Offline } target) return;
        try
        {
            _library.SetOfflineTexture(target, SkinTextureKind.Skin, null);
            _selectedSkin = null;
            ModMain.Hint("已恢复 " + target.Name + " 的默认皮肤", ModMain.HintType.Green);
        }
        catch (Exception error) { ModMain.Hint("恢复失败：" + error.Message, ModMain.HintType.Red); }
        _accountKey = "";
        RefreshAccount(updateContent: false);
        Reload();
    }

    private void ApplyOfflineCape(string? id)
    {
        if (_account.Target is not { Provider: AccountProvider.Offline } target) return;
        try
        {
            _library.SetOfflineTexture(target, SkinTextureKind.Cape, id);
            ModMain.Hint(id is null ? "已隐藏 " + target.Name + " 的披风" : "已应用披风，启动游戏后生效", ModMain.HintType.Green);
        }
        catch (Exception error) { ModMain.Hint("应用失败：" + error.Message, ModMain.HintType.Red); }
        _accountKey = ""; RefreshAccount(updateContent: false); Reload();
    }

    private async Task ApplyMicrosoftCape(SkinLibraryTarget target, string? capeId)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            _microsoftCapes = await _library.ApplyMicrosoftCapeAsync(target, capeId, cancel.Token);
            RefreshAccountSkin(target.Provider);
            ModMain.Hint(capeId is null ? "已隐藏正版披风" : "已更换正版披风", ModMain.HintType.Green);
            ShowMicrosoftCapes(target, CancellationToken.None);
        }
        catch (Exception error) { ModMain.Hint("更换披风失败：" + error.Message, ModMain.HintType.Red); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy) { _busy = busy; RefreshSide(); }

    // The account head (side card, rail avatar) reloads from the changed skin right away.
    private static void RefreshAccountSkin(AccountProvider provider)
    {
        try
        {
            switch (provider)
            {
                case AccountProvider.Offline: PageLaunchLeft.SkinLegacy.Start(IsForceRestart: true); break;
                case AccountProvider.Microsoft: PageLaunchLeft.SkinMs.Start(IsForceRestart: true); break;
            }
        }
        catch (Exception error) { Logger.Info(error, "刷新账号皮肤失败"); }
    }

    public void Dispose() { _work?.Cancel(); }
}
