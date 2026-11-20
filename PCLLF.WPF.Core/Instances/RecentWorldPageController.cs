// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PCLCS;

public enum RecentWorldSlideEase { OutBackWeak, InFluentWeak }

/// <summary>原 LF 动画原语适配，TAnimation 保留原 AniData，不更换动画引擎。</summary>
public sealed class RecentWorldAnimationHost<TAnimation> where TAnimation : struct {
    public Func<FrameworkElement, double, int, TAnimation> Opacity = null!;
    public Func<FrameworkElement, double, int, RecentWorldSlideEase, TAnimation> TranslateX = null!;
    public Func<Action, int, bool, TAnimation> Code = null!;
    public Action<TAnimation[], string> Start = null!;
    public Action<string> Stop = null!;
    public Func<string, bool> IsRunning = null!;
}

public sealed class RecentWorldPageView {
    public FrameworkElement Root = null!, Card = null!;
    public Panel Item = null!;
    public TranslateTransform Transform = null!;
    public Func<string> PageId = null!;
    public Func<TextBlock> CardTitle = null!;
    public Action<string?> SetTitle = null!;
    public Action<string> SetInfo = null!;
}

public sealed class RecentWorldPageHost<TInstance, TWorld>
        where TInstance : class where TWorld : RecentWorldData {
    public Func<TInstance?> SelectedInstance = null!;
    public Action RecoverExitedSessions = null!;
    public Action<TInstance?> DiscoverExistingWorlds = null!;
    public Func<bool> HasRunningSessions = null!;
    public Func<List<TWorld>> Recent = null!;
    public Func<string?, string?, string> WorldPath = null!;
    public Func<string, int, ImageSource?> LoadImage = null!;
    public Func<ImageSource> GrassImage = null!;
    public Action<Action, string> Background = null!;
    public Action<Action> Dispatch = null!;
    public Action<Exception, string> Warn = null!, Error = null!;
    public Func<bool> HasRunningGame = null!, LaunchLoading = null!, AnimationEnabled = null!, Enabled = null!;
    public Action<bool, bool, bool> ApplyDependencyOptionsState = null!;
    public Func<string?, TInstance> CreateInstance = null!;
    public Func<TInstance, string> GameFolder = null!;
    public Func<string?, string?, bool> SamePath = null!;
    public Func<TInstance, string?, bool> StartWorld = null!;
    public Action<string> HintError = null!;
}

/// <summary>LF 近期存档卡片的异步读取、原控件显示、动画和启动交接。</summary>
// LF PageLaunchRight.xaml.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through 07dd3503b38b39095c8b92b40ae0454907387f9f. No CE code reused.
public sealed class RecentWorldPageController<TInstance, TWorld, TAnimation>
        where TInstance : class where TWorld : RecentWorldData where TAnimation : struct {
    private readonly RecentWorldPageView view;
    private readonly RecentWorldPageHost<TInstance, TWorld> host;
    private readonly RecentWorldAnimationHost<TAnimation> animation;
    private int revision;
    private Tuple<TWorld, ImageSource?>? latest;
    private bool visible;
    private bool sessionRunning;
    private bool launchPending;

    public RecentWorldPageController(RecentWorldPageView view, RecentWorldPageHost<TInstance, TWorld> host,
        RecentWorldAnimationHost<TAnimation> animation) {
        this.view = view; this.host = host; this.animation = animation;
    }

    public void InitializeCard() {
        TextBlock title = view.CardTitle();
        title.FontSize = 12;
        title.Margin = new Thickness(12, 7, 12, 0);
    }

    public void RefreshRecentWorlds() {
        int current = Interlocked.Increment(ref revision);
        TInstance? selectedInstance = host.SelectedInstance();
        host.Background(() => {
            Tuple<TWorld, ImageSource?>? item = null;
            bool running = false;
            try {
                host.RecoverExitedSessions();
                host.DiscoverExistingWorlds(selectedInstance);
                running = host.HasRunningSessions();
                TWorld? world = host.Recent().FirstOrDefault();
                if (world is not null) item = Tuple.Create(world, host.LoadImage(Path.Combine(host.WorldPath(world.GameFolder, world.Identifier), "icon.png"), 64));
            } catch (Exception error) {
                host.Warn(error, "读取最近存档失败");
            }
            host.Dispatch(() => {
                if (current != revision) return;
                sessionRunning = running;
                ShowRecentWorld(item);
            });
        }, "Load Recent Worlds");
    }

    public void ShowRecentWorld(Tuple<TWorld, ImageSource?>? item) {
        latest = item;
        if (item is null) {
            ApplyRecentWorldState(launchPending || host.HasRunningGame() || sessionRunning || host.LaunchLoading(),
                view.Root.IsLoaded && host.AnimationEnabled());
            return;
        }
        TWorld world = item.Item1;
        view.Item.Tag = world;
        view.SetTitle(world.Name);
        DateTime played = world.PlayedUtc.ToLocalTime();
        view.SetInfo((played.Date == DateTime.Today ? played.ToString("HH:mm") : played.ToString("MM/dd")) +
            " · " + new DirectoryInfo(world.VersionPath!).Name);
        ToolTip? tip = view.Item.ToolTip as ToolTip;
        if (tip is null) { tip = new ToolTip(); view.Item.ToolTip = tip; }
        tip.Content = world.Name + "\r\n" + new DirectoryInfo(world.VersionPath!).Name + " · " + played.ToString("yyyy-MM-dd HH:mm");
        Canvas? icon = view.Item.Children.OfType<Canvas>().FirstOrDefault(canvas => canvas.Background is ImageBrush);
        if (icon is not null) {
            ImageBrush picture = ((ImageBrush)icon.Background).CloneCurrentValue();
            picture.Stretch = Stretch.Uniform;
            picture.ImageSource = item.Item2 ?? host.GrassImage();
            icon.Background = picture;
            icon.Width = 32;
            icon.Height = 32;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Clip = new RectangleGeometry(new Rect(0, 0, 32, 32), 6, 6);
        }
        UpdateRecentWorldVisibility();
    }

    public void UpdateRecentWorldVisibility() {
        if (!view.Root.IsLoaded) return;
        ApplyRecentWorldState(launchPending || host.HasRunningGame() || sessionRunning || host.LaunchLoading(), host.AnimationEnabled());
    }

    public void ApplyRecentWorldState(bool gameActive, bool animate) {
        bool nextVisible = latest is not null && !gameActive && !launchPending && host.Enabled();
        host.ApplyDependencyOptionsState(gameActive || launchPending, animate, nextVisible);
        string name = "Recent World Card " + view.PageId();
        if (visible == nextVisible && (animation.IsRunning(name) || view.Card.Visibility == (nextVisible ? Visibility.Visible : Visibility.Collapsed))) return;
        visible = nextVisible;
        animation.Stop(name);
        ToolTipService.SetIsEnabled(view.Item, false);
        if (view.Item.ToolTip is ToolTip tip) tip.IsOpen = false;
        view.Card.IsHitTestVisible = nextVisible;
        if (!animate) {
            view.Card.Visibility = nextVisible ? Visibility.Visible : Visibility.Collapsed;
            view.Card.Opacity = nextVisible ? 1 : 0;
            view.Transform.X = nextVisible ? 0 : -24;
            ToolTipService.SetIsEnabled(view.Item, nextVisible);
        } else if (nextVisible) {
            if (view.Card.Visibility != Visibility.Visible) { view.Card.Opacity = 0; view.Transform.X = -24; }
            view.Card.Visibility = Visibility.Visible;
            animation.Start(new[] {
                animation.Opacity(view.Card, 1 - view.Card.Opacity, 180),
                animation.TranslateX(view.Card, -view.Transform.X, 280, RecentWorldSlideEase.OutBackWeak),
                animation.Code(() => ToolTipService.SetIsEnabled(view.Item, visible && !launchPending), 0, true)
            }, name);
        } else {
            animation.Start(new[] {
                animation.Opacity(view.Card, -view.Card.Opacity, 100),
                animation.TranslateX(view.Card, -24 - view.Transform.X, 150, RecentWorldSlideEase.InFluentWeak),
                animation.Code(() => view.Card.Visibility = Visibility.Collapsed, 0, true)
            }, name);
        }
    }

    public void RecentWorldClick(object sender, MouseButtonEventArgs args) {
        args.Handled = true;
        TWorld? world = view.Item.Tag as TWorld;
        if (world is null) return;
        BeginRecentWorldLaunch(() => {
            TInstance instance = host.CreateInstance(world.VersionPath);
            if (!host.SamePath(host.GameFolder(instance), world.GameFolder)) {
                host.HintError("此版本的游戏目录已经变化，请从存档管理页重新选择存档。");
                return false;
            }
            return host.StartWorld(instance, world.Identifier);
        }, host.AnimationEnabled());
    }

    public void BeginRecentWorldLaunch(Func<bool> start, bool animate = true) {
        if (launchPending || host.HasRunningGame() || host.LaunchLoading()) return;
        launchPending = true;
        ApplyRecentWorldState(true, animate);
        Action continueLaunch = () => {
            bool accepted = false;
            try {
                accepted = start();
            } catch (Exception error) {
                host.Error(error, "启动最近游玩的存档失败");
            } finally {
                launchPending = false;
                if (!accepted) ApplyRecentWorldState(false, animate);
            }
        };
        if (animate) animation.Start(new[] { animation.Code(() => continueLaunch(), 180, false) }, "Recent World Launch " + view.PageId());
        else continueLaunch();
    }

    public void ReleaseRecentWorlds() {
        Interlocked.Increment(ref revision);
        animation.Stop("Recent World Card " + view.PageId());
        animation.Stop("Recent World Launch " + view.PageId());
        launchPending = false;
        ToolTipService.SetIsEnabled(view.Item, false);
        if (view.Item.ToolTip is ToolTip tip) tip.IsOpen = false;
        visible = false;
        view.Card.Visibility = Visibility.Collapsed;
        view.Card.Opacity = 0;
        view.Transform.X = -24;
    }
}
