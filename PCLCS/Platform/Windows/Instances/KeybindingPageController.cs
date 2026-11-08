// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PCLCS;

public enum KeybindingPageButtonState { Normal, Conflict, Capturing }

public sealed class KeybindingPageButton {
    public FrameworkElement Element { get; }
    private readonly Action<string> setText;
    private readonly Action<KeybindingPageButtonState> setState;
    public KeybindingPageButton(FrameworkElement element, Action<string> setText, Action<KeybindingPageButtonState> setState) {
        Element = element; this.setText = setText; this.setState = setState;
    }
    public string Text { set => setText(value); }
    public KeybindingPageButtonState State { set => setState(value); }
}

public sealed class KeybindingPageGroup {
    public FrameworkElement Element { get; }
    private readonly Action<Panel> initializeAndAdd;
    public KeybindingPageGroup(FrameworkElement element, Action<Panel> initializeAndAdd) {
        Element = element; this.initializeAndAdd = initializeAndAdd;
    }
    public void AddRows(Panel rows) => initializeAndAdd(rows);
}

/// <summary>仅此按键页的标准WPF与原自定义控件边界，不依赖其他页面契约。</summary>
public sealed class KeybindingPageView<TEntry> where TEntry : InstanceKeybindingData {
    public FrameworkElement Root = null!, CardBindings = null!, CancelButton = null!, ClearButton = null!;
    public Panel Bindings = null!;
    public TextBlock Empty = null!, Capture = null!;
    public Func<string> Search = null!;
    public Action<string> SetAllText = null!, SetConflictsText = null!;
    public Action ScrollHome = null!;
    public Func<TEntry, string, bool, string, Action, KeybindingPageButton> CreateButton = null!;
    public Func<string, string?, KeybindingPageGroup> CreateGroup = null!;
    public Func<int, int, bool, int> RefreshCaptureBar = null!;
}

public sealed class KeybindingPageHost<TInstance> where TInstance : class {
    public Func<TInstance?> CurrentInstance = null!;
    public Func<TInstance, string> GameFolder = null!, VersionPath = null!;
    public Func<TInstance, bool> AllowsInstance = null!, IsLocked = null!, VersionValid = null!;
    public Func<TInstance, int> VersionMajor = null!;
    public Func<bool> HasRunningGame = null!, IsCurrentPage = null!, AnimationEnabled = null!, LoaderFailed = null!;
    public Action<string> StartLoader = null!;
    public Action CancelLoader = null!;
    public Action<bool, KeyEventHandler, MouseButtonEventHandler> BindExternalEvents = null!;
    public Func<string?, bool, string?> BindingForVersion = null!;
    public Action<string, string, string?, string> Save = null!;
    public Action<TInstance, string, string> CaptureEditedBinding = null!;
    public Func<string, string, string, string, bool, int> Message = null!;
    public Action<string, bool> Hint = null!;
    public Action<Exception, string> Warn = null!, Error = null!;
    public Func<TInstance, IReadOnlyDictionary<string, string>>? PersonalBindingAnnotations;
    public bool CapturePersonalOnEdit = true;
}

/// <summary>LF 按键页的列表、分组、捕获与保存生命周期。</summary>
public sealed class KeybindingPageController<TInstance, TEntry>
        where TInstance : class where TEntry : InstanceKeybindingData {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 PageInstanceKeybindings.xaml.vb.
    // Keep actual entries and native cards/buttons; existing C# BuildView is reused.
    private readonly KeybindingPageView<TEntry> view;
    private readonly KeybindingPageHost<TInstance> host;
    public List<TEntry> Entries = new();
    public TInstance? Owner;
    public string? GameFolder;
    public TEntry? Captured;
    public bool ConflictsOnly;
    private int captureBarShownCount;
    public readonly Dictionary<string, KeybindingPageButton> KeyButtons = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> personalAnnotations = new Dictionary<string, string>();

    public KeybindingPageController(KeybindingPageView<TEntry> view, KeybindingPageHost<TInstance> host) {
        this.view = view; this.host = host;
    }

    private static bool SameText(string? left, string? right) =>
        string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);

    public void PageLoaded() {
        view.ScrollHome();
        RefreshBindings();
        host.BindExternalEvents(true, CaptureKey, HandleCapturedMouse);
    }

    public void PageUnloaded() {
        host.CancelLoader();
        CancelCapture();
        host.BindExternalEvents(false, CaptureKey, HandleCapturedMouse);
    }

    public void RefreshBindings() {
        CancelCapture();
        Owner = host.CurrentInstance();
        if (Owner is null || !host.AllowsInstance(Owner)) return;
        GameFolder = host.GameFolder(Owner);
        host.StartLoader(GameFolder);
    }

    public void BindingsLoaded(List<TEntry> entries) {
        Entries = entries;
        if (Owner is not null && host.VersionValid(Owner) && host.VersionMajor(Owner) < 13) {
            foreach (TEntry entry in Entries.Where(item => !item.Saved))
                entry.Value = host.BindingForVersion(entry.Value, false);
        }
        view.CardBindings.ToolTip = Entries.Any(item => !item.Saved)
            ? "尚未保存的原版按键显示默认键位。模组按键需在游戏内保存设置后才可识别。" : null;
        RenderList();
    }

    public void FilterChecked(bool conflictsOnly) {
        ConflictsOnly = conflictsOnly;
        RenderList();
    }

    public void RenderList() {
        if (view.Bindings is null) return;
        view.Bindings.Children.Clear();
        KeyButtons.Clear();
        personalAnnotations = Owner is null ? new Dictionary<string, string>() : host.PersonalBindingAnnotations?.Invoke(Owner) ?? new Dictionary<string, string>();
        string query = view.Search().Trim();
        InstanceKeybindingViewResult<TEntry> result = InstanceKeybindingView.BuildView(Entries, query, ConflictsOnly);
        view.SetAllText((query == "" ? "全部" : "搜索结果") + $" ({result.MatchingCount})");
        view.SetConflictsText($"同键占用 ({result.ConflictCount})");
        view.Empty.Visibility = result.VisibleCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        view.Empty.Text = Entries.Count == 0 ? "暂无按键记录。请先启动一次该版本的游戏并保存设置。" : "没有找到符合条件的按键。";
        foreach (InstanceKeybindingViewBlock<TEntry> block in result.Blocks) {
            if (!block.IsGroup) { view.Bindings.Children.Add(CreateBindingRow(block.Members[0])); continue; }
            KeybindingPageGroup group = view.CreateGroup(DisplayKey(block.Normalized) + $" 键位 · 与 {block.TotalCount - 1} 项共用", block.Normalized);
            StackPanel rows = new() { Margin = new Thickness(15, 36, 15, 12) };
            foreach (TEntry member in block.Members) rows.Children.Add(CreateBindingRow(member));
            group.AddRows(rows);
            view.Bindings.Children.Add(group.Element);
        }
    }

    public Grid CreateBindingRow(TEntry entry) {
        Grid row = new() { Height = 44, Margin = new Thickness(5, 2, 4, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel labels = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        labels.Children.Add(new TextBlock { Text = entry.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        string? detail = SameText(entry.Owners, "Minecraft") ? "" : entry.Owners;
        if (!entry.Saved) detail = SameText(detail, "") ? "原版默认值 · 此项尚未保存" : detail + " · 原版默认值 · 此项尚未保存";
        if (entry.Key is not null && personalAnnotations.TryGetValue(PersonalOptionsRules.CanonicalKey(entry.Key), out string? personal)) detail = SameText(detail, "") ? personal : detail + " · " + personal;
        if (!SameText(detail, "")) {
            TextBlock description = new() { Text = detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
            labels.Children.Add(description);
        }
        row.Children.Add(labels);
        KeybindingPageButton button = view.CreateButton(entry, DisplayKey(entry.Normalized), entry.SharedCount > 1,
            entry.Key + "\r\n" + entry.Owners + (!entry.Saved ? "\r\n原版默认键位" : ""), () => BeginCapture(entry));
        Grid.SetColumn(button.Element, 1);
        KeyButtons.Add(entry.Key!, button);
        row.Children.Add(button.Element);
        return row;
    }

    public static string DisplayKey(string? value) => InstanceKeybindingView.DisplayKey(value);

    public void BeginCapture(TEntry entry) {
        if (host.HasRunningGame() || Owner is null || host.IsLocked(Owner)) {
            host.Hint("请先关闭游戏并等待版本修改完成，再编辑按键。", false);
            return;
        }
        Captured = entry;
        view.Capture.Text = entry.Name + "：按下新按键，Esc 取消";
        UpdateCaptureUi();
    }

    public void CancelCapture() { Captured = null; UpdateCaptureUi(); }

    public void UpdateCaptureUi() {
        foreach (KeybindingPageButton button in KeyButtons.Values) {
            TEntry entry = (TEntry)button.Element.Tag;
            bool active = ReferenceEquals(entry, Captured);
            button.Text = active ? "请按键…" : DisplayKey(entry.Normalized);
            button.State = active ? KeybindingPageButtonState.Capturing : entry.SharedCount > 1
                ? KeybindingPageButtonState.Conflict : KeybindingPageButtonState.Normal;
        }
        captureBarShownCount = view.RefreshCaptureBar(Captured is null ? 0 : 1, captureBarShownCount,
            view.Root.IsLoaded && host.AnimationEnabled());
        view.CardBindings.Margin = new Thickness(0, 0, 0, Captured is null ? 15 : 95);
    }

    public void ClearBinding() { if (Captured is not null) SaveBinding("key.keyboard.unknown"); }

    public void CaptureKey(object sender, KeyEventArgs args) {
        if (Captured is null || !host.IsCurrentPage()) return;
        args.Handled = true;
        Key pressed = args.Key == Key.System ? args.SystemKey : args.Key == Key.ImeProcessed ? args.ImeProcessedKey : args.Key;
        if (pressed == Key.Escape) { CancelCapture(); return; }
        string? name = KeyboardName(pressed);
        if (name is not null) SaveBinding("key.keyboard." + name);
        else host.Hint("暂不支持这个按键，请选择其他键。", false);
    }

    public void HandleCapturedMouse(object sender, MouseButtonEventArgs args) {
        if (Captured is null || !host.IsCurrentPage()) return;
        // Keep cancel/clear buttons clickable inside the same main window.
        DependencyObject? source = args.OriginalSource as DependencyObject;
        if (source is not null && (view.CancelButton.IsMouseOver || view.ClearButton.IsMouseOver)) return;
        args.Handled = true;
        switch (args.ChangedButton) {
            case MouseButton.Left: SaveBinding("key.mouse.left"); break;
            case MouseButton.Right: SaveBinding("key.mouse.right"); break;
            case MouseButton.Middle: SaveBinding("key.mouse.middle"); break;
            case MouseButton.XButton1: SaveBinding("key.mouse.4"); break;
            case MouseButton.XButton2: SaveBinding("key.mouse.5"); break;
        }
    }

    public void SaveBinding(string value) {
        TEntry? entry = Captured;
        if (entry is null || Owner is null) return;
        // Stop preview capture BEFORE opening a main-window confirmation dialog.
        CancelCapture();
        try {
            if (host.HasRunningGame() || host.IsLocked(Owner)) throw new IOException("请先关闭游戏并等待版本修改完成。");
            if (!host.VersionValid(Owner)) throw new IOException("无法确定此版本的按键格式，请在游戏内修改。");
            int separator = entry.Value!.IndexOf(':');
            if (separator > 0 && !SameText(value, "key.keyboard.unknown")) value += entry.Value.Substring(separator);
            List<TEntry> existing = Entries.Where(item => !ReferenceEquals(item, entry) && SameText(item.Normalized, value) && !SameText(value, "key.keyboard.unknown")).ToList();
            if (existing.Count > 0 && host.Message("这个键已用于：\r\n" + string.Join("\r\n", existing.Take(8).Select(item => item.Name + " · " + item.Owners)) +
                    "\r\n仍然使用这个键吗？", "同键占用", "仍然绑定", "取消", true) != 1) return;
            // Deliberately reread Owner and GameFolder after the modal callback:
            // the original page allows that callback to switch/refetch the owner.
            string? encoded = host.BindingForVersion(value, host.VersionValid(Owner!) && host.VersionMajor(Owner!) >= 13);
            if (encoded is null) throw new IOException("此游戏版本无法使用这个按键。");
            host.Save(GameFolder!, entry.Key!, entry.Saved ? entry.Value : null, encoded);
            try { if (host.CapturePersonalOnEdit) host.CaptureEditedBinding(Owner!, entry.Key!, encoded); }
            catch (Exception error) { host.Warn(error, "按键已保存，但更新个人同步档案失败"); }
            CancelCapture();
            host.Hint("按键已保存。", true);
            RefreshBindings();
        } catch (Exception error) {
            CancelCapture();
            host.Error(error, "修改按键失败");
        }
    }

    public static string? KeyboardName(Key value) {
        if (value >= Key.A && value <= Key.Z) return value.ToString().ToLowerInvariant();
        if (value >= Key.D0 && value <= Key.D9) return ((int)value - (int)Key.D0).ToString();
        if (value >= Key.NumPad0 && value <= Key.NumPad9) return "keypad." + ((int)value - (int)Key.NumPad0);
        if (value >= Key.F1 && value <= Key.F24) return value.ToString().ToLowerInvariant();
        switch (value) {
            case Key.Space: return "space";
            case Key.Return: return "enter";
            case Key.Tab: return "tab";
            case Key.Back: return "backspace";
            case Key.LeftCtrl: return "left.control";
            case Key.RightCtrl: return "right.control";
            case Key.LeftShift: return "left.shift";
            case Key.RightShift: return "right.shift";
            case Key.LeftAlt: return "left.alt";
            case Key.RightAlt: return "right.alt";
            case Key.Left: case Key.Right: case Key.Up: case Key.Down:
            case Key.Home: case Key.End: case Key.Insert: case Key.Delete: return value.ToString().ToLowerInvariant();
            case Key.PageUp: return "page.up";
            case Key.PageDown: return "page.down";
            case Key.OemMinus: return "minus";
            case Key.OemPlus: return "equal";
            case Key.Oem1: return "semicolon";
            case Key.Oem2: return "slash";
            case Key.Oem3: return "grave.accent";
            case Key.Oem4: return "left.bracket";
            case Key.Oem5: return "backslash";
            case Key.Oem6: return "right.bracket";
            case Key.Oem7: return "apostrophe";
            case Key.OemComma: return "comma";
            case Key.OemPeriod: return "period";
            case Key.Capital: return "caps.lock";
            case Key.NumLock: return "num.lock";
            case Key.Scroll: return "scroll.lock";
            case Key.LWin: return "left.win";
            case Key.RWin: return "right.win";
            case Key.Apps: return "menu";
            case Key.Pause: return "pause";
            case Key.Snapshot: return "print.screen";
            case Key.Add: return "keypad.add";
            case Key.Subtract: return "keypad.subtract";
            case Key.Multiply: return "keypad.multiply";
            case Key.Divide: return "keypad.divide";
            case Key.Decimal: return "keypad.decimal";
        }
        return null;
    }

    public void RetryLoad() { if (host.LoaderFailed()) RefreshBindings(); }
}
