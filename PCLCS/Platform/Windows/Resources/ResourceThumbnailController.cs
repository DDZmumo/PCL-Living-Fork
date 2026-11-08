// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PCLCS;

/// <summary>LF 截图卡片的实际选择、视口和缩略图生命周期；视觉控件由原 XAML 提供。</summary>
// Adapted from LF MyScreenshotItem.xaml.vb at
// f4aa0fae62c3ee8af24c81e61844c8fdd13c8983. CE is architecture reference only;
// its resource page has no equivalent LF screenshot-gallery controller.
public sealed class ResourceThumbnailController {
    private static readonly SemaphoreSlim DecodeGate = new(2);
    private readonly View view;
    private readonly Host host;
    private int revision;
    private bool inViewport;
    private bool ready;
    private bool loading;
    private bool pressed;

    /// <summary>Only standard WPF references and the original entry/check adapters cross this boundary.</summary>
    public sealed class View {
        internal readonly FrameworkElement Owner;
        internal readonly Image Thumbnail;
        internal readonly TextBlock Name;
        internal readonly TextBlock Info;
        internal readonly Border Selection;
        internal readonly FrameworkElement Check;
        internal readonly Func<bool> GetChecked;
        internal readonly Action<bool> SetChecked;
        internal readonly Func<InstanceResourceEntryData?> GetEntry;
        internal readonly Action<InstanceResourceEntryData> SetEntry;

        public View(FrameworkElement owner, Image thumbnail, TextBlock name, TextBlock info,
            Border selection, FrameworkElement check, Func<bool> getChecked, Action<bool> setChecked,
            Func<InstanceResourceEntryData?> getEntry, Action<InstanceResourceEntryData> setEntry) {
            Owner = owner;
            Thumbnail = thumbnail;
            Name = name;
            Info = info;
            Selection = selection;
            Check = check;
            GetChecked = getChecked;
            SetChecked = setChecked;
            GetEntry = getEntry;
            SetEntry = setEntry;
        }
    }

    /// <summary>Original image decode, dispatch, logging and animation remain supplied by the LF host.</summary>
    public sealed class Host {
        internal readonly Func<string, int, ImageSource?> LoadImage;
        internal readonly Action<Action> Dispatch;
        internal readonly Action<Exception, string> Warn;
        internal readonly Action<Border, bool, bool, bool> AnimateSelection;
        internal readonly Func<bool> AnimationEnabled;
        internal readonly Action StopAnimation;

        public Host(Func<string, int, ImageSource?> loadImage, Action<Action> dispatch,
            Action<Exception, string> warn, Action<Border, bool, bool, bool> animateSelection,
            Func<bool> animationEnabled, Action stopAnimation) {
            LoadImage = loadImage;
            Dispatch = dispatch;
            Warn = warn;
            AnimateSelection = animateSelection;
            AnimationEnabled = animationEnabled;
            StopAnimation = stopAnimation;
        }
    }

    public ResourceThumbnailController(View view, Host host) {
        this.view = view;
        this.host = host;
        view.Owner.MouseEnter += RefreshSelection;
        view.Owner.MouseLeftButtonDown += MouseClick;
        view.Owner.MouseLeftButtonUp += ReleasePointer;
        view.Owner.MouseLeave += ReleasePointer;
        view.Owner.Unloaded += Unloaded;
    }

    public event Action? Changed;
    public event Action<int>? ClickRequested;

    public bool Checked {
        get => view.GetChecked();
        set => view.SetChecked(value);
    }

    public void Bind(InstanceResourceEntryData value) {
        InstanceResourceEntryData? previous = view.GetEntry();
        bool changed = previous is null ||
            !string.Equals(previous.FullPath ?? string.Empty, value.FullPath ?? string.Empty, StringComparison.Ordinal) ||
            previous.ModifiedUtc != value.ModifiedUtc || previous.Length != value.Length;
        view.SetEntry(value);
        view.Name.Text = value.Name;
        view.Info.Text = value.Description;
        view.Owner.ToolTip = value.Name;
        if (changed) ReleaseThumbnail();
    }

    // The only custom-control event is forwarded by the original VB checkbox.
    public void CheckChanged() {
        RefreshSelection();
        Changed?.Invoke();
    }

    private void RefreshSelection(object sender, MouseEventArgs args) => RefreshSelection();

    private void RefreshSelection() {
        double target = Checked || view.Owner.IsMouseOver ? 1.0 : 0.0;
        if (view.Owner.IsLoaded && host.AnimationEnabled()) {
            host.AnimateSelection(view.Selection, view.Owner.IsMouseOver, Checked, pressed);
        } else {
            view.Selection.Opacity = target;
            view.Selection.RenderTransform = new ScaleTransform(target == 1 ? 1 : 0.8, target == 1 ? 1 : 0.8);
        }
    }

    private void MouseClick(object sender, MouseButtonEventArgs args) {
        if (view.Check.IsMouseOver) return;
        pressed = true;
        RefreshSelection();
        args.Handled = true;
        ClickRequested?.Invoke(args.ClickCount);
    }

    private void ReleasePointer(object sender, MouseEventArgs args) {
        pressed = false;
        RefreshSelection();
    }

    public void UpdateViewport(FrameworkElement viewport) {
        if (view.GetEntry() is null || !view.Owner.IsLoaded) return;
        Point position = view.Owner.TranslatePoint(new Point(), viewport);
        bool visible = position.Y + view.Owner.ActualHeight >= -100 && position.Y <= viewport.ActualHeight + 100;
        inViewport = visible;
        if (!visible) {
            if (ready || loading) ReleaseThumbnail();
            return;
        }
        if (!ready && !loading) LoadThumbnail();
    }

    private async void LoadThumbnail() {
        if (view.GetEntry() is null) return;
        loading = true;
        InstanceResourceEntryData current = view.GetEntry()!;
        int request = revision;
        try {
            await DecodeGate.WaitAsync().ConfigureAwait(false);
            ImageSource? bitmap;
            try {
                bitmap = await Task.Run(() => host.LoadImage(current.FullPath!, 640)).ConfigureAwait(false);
            } finally {
                DecodeGate.Release();
            }
            host.Dispatch(() => {
                if (request != revision || !inViewport) return;
                view.Thumbnail.Source = bitmap;
                loading = false;
                ready = true;
            });
        } catch (Exception error) {
            host.Dispatch(() => {
                if (request != revision) return;
                loading = false;
                ready = true;
            });
            host.Warn(error, "加载截图缩略图失败");
        }
    }

    private void Unloaded(object sender, RoutedEventArgs args) => ReleaseThumbnail();

    public void ReleaseThumbnail() {
        Interlocked.Increment(ref revision);
        host.StopAnimation();
        view.Thumbnail.Source = null;
        ready = false;
        loading = false;
    }
}
