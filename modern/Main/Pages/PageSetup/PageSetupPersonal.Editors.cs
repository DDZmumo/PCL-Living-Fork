// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MeloongCore;
using PCLCS;

namespace PCL;

public partial class PageSetupPersonal {
    private readonly Dictionary<string, KeybindingPageButton> profileKeyButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersonalOptionEdit> pendingEdits = new(StringComparer.Ordinal);
    private readonly DispatcherTimer editTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ModMinecraft.McInstance pendingOwner, captureOwner;
    private Window editorWindow;
    private string captureKey, captureExpected;
    private int captureBarCount;
    private bool flushing;

    private void InitializeEditors() {
        DisabledPageAnimControls.Add(CardCapture);
        BtnClearBinding.Logo = ModBase.Logo.IconButtonDelete;
        BtnCancelBinding.Logo = ModBase.Logo.IconButtonCross;
        BtnClearBinding.Click += (_, _) => SaveCapturedBinding("key.keyboard.unknown");
        BtnCancelBinding.Click += (_, _) => CancelProfileCapture();
        editTimer.Tick += (_, _) => {
            if (Mouse.LeftButton == MouseButtonState.Pressed && ModMain.DragControl is MySlider) return;
            FlushPendingEdits();
        };
    }
    private void AttachEditors() {
        if (editorWindow is not null) return;
        editorWindow = Window.GetWindow(this);
        if (editorWindow is null) return;
        editorWindow.PreviewKeyDown += CaptureProfileKeyboard;
        editorWindow.PreviewMouseDown += CaptureProfileMouse;
    }
    private void DetachEditors() {
        FlushPendingEdits(false); CancelProfileCapture(); editTimer.Stop();
        if (editorWindow is null) return;
        editorWindow.PreviewKeyDown -= CaptureProfileKeyboard;
        editorWindow.PreviewMouseDown -= CaptureProfileMouse;
        editorWindow = null;
    }
    private void CancelEditorState() { editTimer.Stop(); pendingEdits.Clear(); pendingOwner = null; CancelProfileCapture(); }
    private static Grid EditorRow() {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 100 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 132 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 132 });
        return row;
    }
    private static void AddEditorText(Grid row, string text, int column) {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        Grid.SetColumn(label, column); row.Children.Add(label);
    }
    private static void ProtectHeaderToggle(MyCheckBox toggle) {
        // Keep the checkbox's own input handlers; stop the header click before
        // it also reaches the card's expand/collapse handlers.
        toggle.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, args) => args.Handled = true), true);
        toggle.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, args) => args.Handled = true), true);
    }
    private void AddCurrentValue(Grid row, PersonalOptionDefinition definition, string text) {
        AddReadOnlyValue(row, definition, (string)CurrentPreview?.Current[definition.Key], 1, "current-value:", text);
    }
    private void AddReadOnlyValue(Grid row, PersonalOptionDefinition definition, string raw, int column, string tagPrefix, string missingText) {
        string text = raw is null ? missingText : ValueText(definition.Key, new Newtonsoft.Json.Linq.JValue(raw));
        if (raw is null) { AddEditorText(row, text, column); return; }
        if (PersonalOptionsCatalog.IsToggle(definition.Key)) {
            if (!bool.TryParse(raw, out bool value)) { AddEditorText(row, text, column); return; }
            var toggle = new MyCheckBox { Text = "启用", Checked = value, IsEnabled = false, IsHitTestVisible = false,
                Focusable = false, Height = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Tag = tagPrefix + definition.Key };
            Grid.SetColumn(toggle, column); row.Children.Add(toggle); return;
        }
        if (!definition.Key.StartsWith("key_", StringComparison.Ordinal)) {
            int value;
            try { value = PersonalOptionsCatalog.SliderValue(definition.Key, raw); }
            catch { AddEditorText(row, text, column); return; }
            var panel = new Grid { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            var slider = new MySlider { MaxValue = PersonalOptionsCatalog.SliderMaximum(definition.Key), Value = value,
                IsEnabled = false, IsHitTestVisible = false, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
                Tag = tagPrefix + definition.Key };
            var label = new TextBlock { Text = PersonalOptionsCatalog.SliderText(definition.Key, value), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
            Grid.SetColumn(label, 1); panel.Children.Add(slider); panel.Children.Add(label);
            Grid.SetColumn(panel, column); row.Children.Add(panel); return;
        }
        var entry = new InstanceKeybinding { Key = definition.Key, Name = definition.Title, Owners = "Minecraft" };
        var button = PageInstanceKeybindings.CreateNativeButton(entry, text, false, tagPrefix == "global-value:" ? "已保存的全局键位，只读预览。" : "当前版本的键位，仅用于对照。", () => { });
        button.Element.IsEnabled = false; button.Element.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(button.Element, column); row.Children.Add(button.Element);
    }
    private FrameworkElement CreateValueEditor(PersonalOptionDefinition definition, bool selected) {
        string key = definition.Key;
        string saved = (string)EditorValues[key];
        string current = (string)CurrentPreview?.Current[key];
        string valuePrefix = IsInstanceContext ? "instance-value:" : "profile-value:";
        bool enabled = selected && !ModWatcher.HasRunningMinecraft && (!IsInstanceContext ||
            (CanEditLocal && CurrentPreview?.HasFile == true && current is not null));
        if (key.StartsWith("key_", StringComparison.Ordinal)) {
            string normalized = saved is null ? null : PersonalOptionsRules.NormalizeBinding(saved);
            var bindingValues = EditorValues;
            bool shared = normalized is not null && normalized != "key.keyboard.unknown" && bindingValues?.Properties().Any(pair => pair.Name != key && PersonalOptionsCatalog.Contains(pair.Name) && pair.Name.StartsWith("key_", StringComparison.Ordinal) && PersonalOptionsRules.NormalizeBinding(pair.Value.ToString()) == normalized) == true;
            var entry = new InstanceKeybinding { Key = key, Name = definition.Title, Normalized = normalized, Owners = "个人方案", Value = saved, Saved = saved is not null, SharedCount = shared ? 2 : 1 };
            var button = PageInstanceKeybindings.CreateNativeButton(entry, saved is null ? "设置按键" : InstanceKeybindingView.DisplayKey(normalized), shared,
                IsInstanceContext ? "暂存本版本按键，点击保存后生效。Esc 取消。" : "暂存全局按键，点击保存后生效。Esc 取消。", () => BeginProfileCapture(key));
            button.Element.IsEnabled = enabled;
            button.Element.HorizontalAlignment = HorizontalAlignment.Center;
            profileKeyButtons[key] = button;
            return button.Element;
        }
        if (PersonalOptionsCatalog.IsToggle(key)) {
            bool valid = bool.TryParse(saved ?? current, out bool on);
            var toggle = new MyCheckBox { Text = saved is not null && !valid ? "重新设置" : "启用", Checked = on, IsEnabled = enabled, Height = 24,
                Tag = valuePrefix + key, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "点击保存后生效。" };
            toggle.Change += (_, user) => { if (user && !refreshing) SaveSchemeValue(key, toggle.Checked ? "true" : "false", saved); };
            return toggle;
        }
        string fallback = key == "fov" ? "70" : key == "mouseSensitivity" ? "0.5" : "1";
        int initial; bool invalid = false;
        try { initial = PersonalOptionsCatalog.SliderValue(key, saved ?? current ?? fallback); }
        catch { initial = PersonalOptionsCatalog.SliderValue(key, fallback); invalid = true; }
        var panel = new Grid { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
        var slider = new MySlider { MaxValue = PersonalOptionsCatalog.SliderMaximum(key), Value = initial, ValueByKey = 1,
            IsEnabled = enabled, Tag = valuePrefix + key, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "调整后点击保存才会生效。" };
        slider.GetHintText = new Func<int, string>(value => PersonalOptionsCatalog.SliderText(key, value));
        var valueLabel = new TextBlock { Text = invalid ? "无效值" : saved is null ? "未保存" : PersonalOptionsCatalog.SliderText(key, initial),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        valueLabel.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        Grid.SetColumn(valueLabel, 1); panel.Children.Add(slider); panel.Children.Add(valueLabel);
        // LF's native slider reports false for both drag and keyboard changes;
        // initialization happens before this subscription and never writes a value.
        slider.Change += (_, _) => {
            if (refreshing || !slider.IsLoaded || !slider.IsEnabled) return;
            valueLabel.Text = PersonalOptionsCatalog.SliderText(key, slider.Value);
            if (pendingEdits.Count == 0) pendingOwner = Target;
            pendingEdits[key] = new PersonalOptionEdit(key, PersonalOptionsCatalog.FromSlider(key, slider.Value),
                pendingEdits.TryGetValue(key, out var previous) ? previous.Expected : saved);
            editTimer.Stop(); editTimer.Start();
        };
        return panel;
    }
    internal void FlushPendingEdits(bool refresh = true) {
        editTimer.Stop();
        if (flushing || pendingEdits.Count == 0) return;
        flushing = true;
        var edits = pendingEdits.Values.ToArray(); pendingEdits.Clear();
        try {
            if (IsInstanceContext && !ReferenceEquals(pendingOwner, Target)) throw new InvalidOperationException("当前版本已变化，请重新编辑个人方案。");
            PersistEditorValues(edits);
        } catch (Exception error) {
            if (IsLoaded) Logger.Error(error, "保存个人方案失败", LogBehavior.Toast);
            else Logger.Warn(error, "保存个人方案失败");
        }
        finally { pendingOwner = null; flushing = false; }
        if (refresh && IsLoaded) RefreshPage();
    }
    private void SaveSchemeValue(string key, string value, string expected) {
        Execute(() => { FlushPendingEdits(false); PersistEditorValues(new[] { new PersonalOptionEdit(key, value, expected) }); RefreshPage(); });
    }
    private void PersistEditorValues(IEnumerable<PersonalOptionEdit> edits) {
        StageEditorValues(edits);
    }
    internal void BeginProfileCapture(string key) {
        FlushPendingEdits();
        if (!profileKeyButtons.TryGetValue(key, out var button) || !button.Element.IsEnabled) return;
        CancelProfileCapture();
        captureKey = key; captureExpected = (string)EditorValues[key]; captureOwner = Target;
        button.Text = "请按键…"; button.State = KeybindingPageButtonState.Capturing;
        LabCapture.Text = PersonalOptionsCatalog.Title(key) + "：按下新按键，Esc 取消";
        LocalResourceUi.RefreshSelectionBar(CardCapture, TransCapture, 1, ref captureBarCount, "Personal key capture", IsLoaded && ModAnimation.AniControlEnabled == 0);
    }
    private bool CanCapture => captureKey is not null && IsLoaded && (ModMain.FrmMain is null || ReferenceEquals(ModMain.FrmMain.PageRight, this));
    private void CaptureProfileKeyboard(object sender, KeyEventArgs args) {
        if (!CanCapture) return;
        args.Handled = true;
        Key key = args.Key == Key.System ? args.SystemKey : args.Key == Key.ImeProcessed ? args.ImeProcessedKey : args.Key;
        if (key == Key.Escape) { CancelProfileCapture(); return; }
        string name = KeybindingPageController<ModMinecraft.McInstance, InstanceKeybinding>.KeyboardName(key);
        if (name is not null) SaveCapturedBinding("key.keyboard." + name);
    }
    private void CaptureProfileMouse(object sender, MouseButtonEventArgs args) {
        if (!CanCapture || BtnClearBinding.IsMouseOver || BtnCancelBinding.IsMouseOver) return;
        string value = args.ChangedButton switch { MouseButton.Left => "key.mouse.left", MouseButton.Right => "key.mouse.right",
            MouseButton.Middle => "key.mouse.middle", MouseButton.XButton1 => "key.mouse.4", MouseButton.XButton2 => "key.mouse.5", _ => null };
        if (value is null) return;
        args.Handled = true; SaveCapturedBinding(value);
    }
    internal void SaveCapturedBinding(string value) {
        string key = captureKey, expected = captureExpected; var owner = captureOwner;
        if (key is null) return;
        CancelProfileCapture();
        Execute(() => {
            string normalized = PersonalOptionsCatalog.ValidateValue(key, value);
            var occupied = EditorValues;
            string[] others = occupied?.Properties().Where(pair => pair.Name != key && PersonalOptionsCatalog.Contains(pair.Name) && pair.Name.StartsWith("key_", StringComparison.Ordinal) &&
                normalized != "key.keyboard.unknown" && PersonalOptionsRules.NormalizeBinding(pair.Value.ToString()) == normalized).Select(pair => PersonalOptionsCatalog.Title(pair.Name)).ToArray() ?? Array.Empty<string>();
            if (others.Length > 0 && !Confirm("这个键已用于：\r\n" + string.Join("\r\n", others) + "\r\n仍然绑定吗？", "同键占用")) { RefreshPage(); return; }
            if (IsInstanceContext && !ReferenceEquals(owner, Target)) throw new InvalidOperationException("当前版本已变化，请重新绑定。");
            PersistEditorValues(new[] { new PersonalOptionEdit(key, normalized, expected) }); RefreshPage();
        });
    }
    private void CancelProfileCapture() {
        if (captureKey is not null && profileKeyButtons.TryGetValue(captureKey, out var button)) {
            button.Text = captureExpected is null ? "设置按键" : InstanceKeybindingView.DisplayKey(captureExpected);
            button.State = button.Element.Tag is InstanceKeybindingData entry && entry.SharedCount > 1 ? KeybindingPageButtonState.Conflict : KeybindingPageButtonState.Normal;
        }
        captureKey = null; captureExpected = null; captureOwner = null;
        if (captureBarCount != 0) LocalResourceUi.RefreshSelectionBar(CardCapture, TransCapture, 0, ref captureBarCount, "Personal key capture", IsLoaded && ModAnimation.AniControlEnabled == 0);
    }
}
