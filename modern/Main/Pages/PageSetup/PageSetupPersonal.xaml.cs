// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

public partial class PageSetupPersonal : MyPageRight {
    private bool refreshing;
    private readonly Dictionary<string, bool> collapsed = new(StringComparer.Ordinal);
    internal PersonalOptionsPreview CurrentPreview;
    private JObject globalValues = new();
    private string importedSourceName;
    internal bool IsInstanceContext;
    internal ModMinecraft.McInstance Target => IsInstanceContext ? PageInstanceLeft.Instance : ModMinecraft.McInstanceSelected;
    internal Func<string, string, bool> Confirm = (text, title) => ModMain.MyMsgBox(text, title, "继续", "取消", IsWarn: true) == 1;
    internal Action<string> Notify = text => ModMain.Hint(text);
    public PageSetupPersonal() {
        InitializeComponent();
        InitializeEditors();
        CheckEnabled.Change += (_, user) => {
            if (refreshing || !user) return;
            Execute(() => {
                StageSetting("LaunchPersonalOptionsSync", CheckEnabled.Checked); RefreshPage();
            });
        };
        ComboPolicy.SelectionChanged += (_, _) => {
            var instance = Target;
            if (refreshing || !IsInstanceContext || instance is null || ComboPolicy.SelectedIndex < 0) return;
            if (!ReferenceEquals(LabInstance.Tag, instance)) { RefreshPage(); return; }
            Settings.Set("VersionPersonalOptionsSync", ComboPolicy.SelectedIndex, Instance: instance); RefreshPage();
        };
        ComboCapture.SelectionChanged += (_, _) => {
            if (refreshing || IsInstanceContext || ComboCapture.SelectedIndex < 0) return;
            StageSetting("LaunchPersonalOptionsCapture", ComboCapture.SelectedIndex == 0); RefreshPage();
        };
        TextSearch.TextChanged += (_, _) => { if (!refreshing) RefreshPage(); };
        BtnImport.Click += (_, _) => Execute(ImportSelection);
        BtnSaveGlobal.Click += (_, _) => Execute(SaveGlobalSettings);
        BtnApply.Click += (_, _) => Execute(SaveInstanceSettings);
        BtnRefresh.Click += (_, _) => DiscardDraft();
        Loaded += (_, _) => {
            if (IsInstanceContext) ModMain.FrmInstanceLeft?.SyncSelection(FormMain.PageSubType.InstancePersonal);
            else ModMain.FrmSetupLeft?.ItemPersonal.SetChecked(true, false, false);
            PersonalGameSettings.Changed += ProfileChanged;
            ModMinecraft.InstanceSelectionChanged += InstanceSelectionChanged;
            AttachEditors();
            RefreshPage();
        };
        Unloaded += (_, _) => {
            PersonalGameSettings.Changed -= ProfileChanged;
            ModMinecraft.InstanceSelectionChanged -= InstanceSelectionChanged;
            DetachEditors();
        };
    }
    private void ProfileChanged() {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) RefreshPage(); })); return; }
        if (IsLoaded && !refreshing) RefreshPage();
    }
    private void InstanceSelectionChanged() {
        if (IsInstanceContext) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) { FlushPendingEdits(false); CancelProfileCapture(); RefreshPage(); } }));
    }
    internal void RefreshPage() {
        if (refreshing) return;
        SynchronizeDraftOwner();
        FlushPendingEdits(false);
        CancelProfileCapture();
        refreshing = true;
        try {
            var instance = Target;
            Visibility detail = IsInstanceContext ? Visibility.Visible : Visibility.Collapsed;
            CheckEnabled.Visibility = IsInstanceContext ? Visibility.Collapsed : Visibility.Visible;
            LabInstanceCaption.Text = IsInstanceContext ? "当前游戏版本" : "当前选择的版本";
            LabImportedInstanceCaption.Visibility = IsInstanceContext ? Visibility.Collapsed : Visibility.Visible;
            LabImportedInstance.Visibility = LabImportedInstanceCaption.Visibility;
            LabPolicyCaption.Visibility = detail; ComboPolicy.Visibility = detail;
            LabCaptureCaption.Visibility = IsInstanceContext ? Visibility.Collapsed : Visibility.Visible;
            ComboCapture.Visibility = LabCaptureCaption.Visibility;
            BtnApply.Visibility = detail; BtnRefresh.Visibility = Visibility.Visible;
            BtnSaveGlobal.Visibility = IsInstanceContext ? Visibility.Collapsed : Visibility.Visible;
            CardSearch.Visibility = Visibility.Visible; PanGroups.Visibility = Visibility.Visible; LabEditScope.Visibility = Visibility.Visible;
            CardSearch.Title = IsInstanceContext ? "本版本设置与全局设置对照" : "当前版本与全局设置对照";
            LabEditScope.Text = IsInstanceContext ? "跟随全局时不可编辑。选择不同步后，可修改左侧并点击“保存此版本个人设置”；右侧全局设置只读。"
                : "左侧显示当前版本的实际设置，仅供对照；右侧修改先暂存，点击“保存全局设置”后生效，来源会标记为自定义。";
            BtnImport.Visibility = IsInstanceContext ? Visibility.Collapsed : Visibility.Visible;
            PanGroups.IsEnabled = true;
            LabInstance.Text = instance?.Name ?? "尚未选择游戏版本";
            LabInstance.Tag = instance;
            LabInstance.ToolTip = instance?.PathVersion;
            CheckEnabled.Checked = PageSetting<bool>("LaunchPersonalOptionsSync");
            ComboCapture.SelectedIndex = PageSetting<bool>("LaunchPersonalOptionsCapture") ? 0 : 1;
            ComboPolicy.IsEnabled = instance is not null && !ModMinecraft.McInstanceLock.IsLocked(instance);
            ComboPolicy.SelectedIndex = Target is null ? -1 : Settings.Get<int>("VersionPersonalOptionsSync", Instance: Target);
            CurrentPreview = Target is null ? null : PersonalGameSettings.Preview(Target);
            JObject profile = PersonalGameSettings.Management.Profile();
            globalValues = (JObject)(profile["values"] as JObject)?.DeepClone() ?? new JObject();
            localValues = (JObject)CurrentPreview?.Current.DeepClone() ?? new JObject();
            OverlayDraft();
            bool custom = (string)profile["sourceKind"] == "custom";
            importedSourceName = !IsInstanceContext && stagedValues.Count > 0 ? "自定义（未保存）" : custom ? "自定义" : (string)(profile["importSource"] as JObject)?["name"];
            LabImportedInstance.Text = string.IsNullOrWhiteSpace(importedSourceName) ? "来源未记录，请重新导入" : importedSourceName;
            LabImportedInstance.ToolTip = custom || (!IsInstanceContext && stagedValues.Count > 0) ? "手动编辑的全局方案。" : (string)(profile["importSource"] as JObject)?["versionPath"];
            HashSet<string> excluded = PageExcluded();
            int selected = PersonalOptionsCatalog.Items.Count(item => PageSetting<bool>(item.Setting) && !excluded.Contains(item.Key));
            int saved = PersonalOptionsCatalog.Items.Count(item => PageSetting<bool>(item.Setting) && !excluded.Contains(item.Key) && CurrentPreview?.Saved[item.Key] is not null);
            int conflicts = (profile["conflicts"] as JObject)?.Properties().Count(item => PersonalOptionsCatalog.Contains(item.Name)) ?? 0;
            int applicable = CurrentPreview is null ? 0 : PersonalOptionsCatalog.Items.Count(item => PageSetting<bool>(item.Setting) && !excluded.Contains(item.Key) && CurrentPreview.Saved[item.Key] is not null && CurrentPreview.Current[item.Key] is not null);
            string state = !CheckEnabled.Checked ? "全局同步已关闭。导入并开启后，其他版本将在下次启动时应用。"
                : Target is null ? IsInstanceContext ? "请先打开一个版本的设置。" : "请先在主界面选择一个游戏版本。"
                : ComboPolicy.SelectedIndex == 1 ? "此版本已单独关闭同步，其他版本不受影响。"
                : !CurrentPreview.HasProfile ? "尚未建立个人方案，请先从一个已运行过的版本导入。"
                : !CurrentPreview.HasFile ? "此版本尚未生成游戏设置。首次启动前将写入所选基础设置，其余默认项目由游戏生成。"
                : selected == 0 ? "尚未选择同步项目。请勾选类别和具体项目。"
                : saved == 0 ? "所选项目没有已保存的值，请从来源版本重新导入。"
                : applicable == 0 ? "此版本未提供所选的基础项目，暂不应用。"
                : conflicts > 0 ? $"有 {conflicts} 项同步冲突，相关项目暂不自动应用。请在下方选择保留哪一份。"
                : ModWatcher.HasRunningMinecraft ? "游戏正在运行。关闭游戏后，可查看回写结果或立即应用。"
                : CurrentPreview.Changed.Count == 0 ? "此版本的所选设置已与个人方案一致。"
                : $"下次启动将应用 {CurrentPreview.Changed.Count} 项全局设置。";
            HintStatus.Text = state;
            if (!IsInstanceContext) HintStatus.Text = !PersonalGameSettings.HasProfile() ? "尚未建立全局设置，请从已运行过的版本导入。"
                : CheckEnabled.Checked ? "个人同步已开启，各版本将在启动前应用共用设置。" : "全局设置已保存，个人同步当前已关闭。";
            if (HasDraft) HintStatus.Text = IsInstanceContext
                ? ComboPolicy.SelectedIndex == 0 ? "有未保存修改。请选择不同步后保存，或放弃修改。" : "此版本有未保存修改，请点击“保存此版本个人设置”。"
                : "全局设置有未保存修改，请点击“保存全局设置”。";
            string updated = (string)profile["updatedUtc"];
            LabSummary.Text = $"已选择 {selected} 项，个人方案包含 {saved} 项对应值，本版本可用 {applicable} 项。" +
                (DateTime.TryParse(updated, out var time) ? "  最近更新：" + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "");
            if (!IsInstanceContext) LabSummary.Text = $"全局方案包含 {globalValues.Properties().Count(value => PersonalOptionsCatalog.Contains(value.Name))} 项基础设置。" +
                (DateTime.TryParse(updated, out var globalTime) ? "  最近更新：" + globalTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "");
            bool idle = !ModWatcher.HasRunningMinecraft && (instance is null || !ModMinecraft.McInstanceLock.IsLocked(instance));
            BtnImport.IsEnabled = idle && CurrentPreview?.HasFile == true && CurrentPreview.Current.Properties().Any(value => PersonalOptionsCatalog.Contains(value.Name));
            BtnSaveGlobal.IsEnabled = !IsInstanceContext && HasDraft && !ModWatcher.HasRunningMinecraft;
            BtnApply.IsEnabled = CanEditLocal && stagedValues.Count > 0;
            BtnRefresh.Text = HasDraft ? "放弃修改" : "刷新";
            BtnRefresh.IsEnabled = true;
            if (IsInstanceContext) { BuildGroups(excluded); CardConflicts.Visibility = Visibility.Collapsed; }
            else { BuildGlobalPreview(globalValues, excluded); BuildConflicts(profile["conflicts"] as JObject); }
        } catch (Exception error) {
            CurrentPreview = null; BtnImport.IsEnabled = false; BtnApply.IsEnabled = false; BtnSaveGlobal.IsEnabled = false;
            PanGroups.IsEnabled = false;
            HintStatus.Text = "无法读取同步资料：" + error.Message;
        } finally { refreshing = false; }
    }
    private static string ValueText(string key, JToken value) {
        if (value is null || value.Type == JTokenType.Null) return "未保存";
        string raw = value.ToString();
        if (key == "fov") return (PersonalOptionsRules.CanonicalValue(key, raw) ?? raw) + "°";
        if (raw == "true") return "开启";
        if (raw == "false") return "关闭";
        if (key.StartsWith("key_", StringComparison.Ordinal)) return InstanceKeybindingView.DisplayKey(PersonalOptionsRules.NormalizeBinding(raw));
        if (PersonalOptionsCatalog.Contains(key) && !PersonalOptionsCatalog.IsToggle(key)) {
            try { return PersonalOptionsCatalog.SliderText(key, PersonalOptionsCatalog.SliderValue(key, raw)); }
            catch { return raw; }
        }
        return raw;
    }
    private void BuildGroups(HashSet<string> excluded) {
        double offset = PanBack.VerticalOffset;
        foreach (MyCard card in PanGroups.Children) if (card.Tag is string category) collapsed[category] = card.IsSwapped;
        PanGroups.Children.Clear();
        profileKeyButtons.Clear();
        string query = (TextSearch.Text ?? "").Trim();
        foreach (var group in PersonalOptionsCatalog.Items.GroupBy(item => item.Category)) {
            var visible = group.Where(item => (item.Title + " " + item.Key + " " + item.Category).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (query == "视野" && item.Key.StartsWith("fov", StringComparison.Ordinal))).ToList();
            if (visible.Count == 0) continue;
            var panel = new StackPanel { Margin = new Thickness(25, 40, 25, 18) };
            bool Selected(PersonalOptionDefinition item) => PageSetting<bool>(item.Setting) && !excluded.Contains(item.Key);
            var category = new TextBlock { Text = group.All(Selected) ? "全局已启用" : "按全局选择同步",
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 11, 44, 0), Tag = "sync-state:" + group.Key };
            category.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
            var headings = EditorRow();
            AddEditorText(headings, "此版本", 1);
            AddEditorText(headings, "全局设置（只读）", 2);
            panel.Children.Add(headings);
            foreach (var definition in visible) {
                var row = EditorRow(); row.MinHeight = 44;
                var choice = new TextBlock { Text = definition.Title, Tag = definition.Key, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(choice);
                FrameworkElement editor = CreateValueEditor(definition, true);
                Grid.SetColumn(editor, 1); row.Children.Add(editor);
                AddReadOnlyValue(row, definition, (string)globalValues[definition.Key], 2, "global-value:", "未保存");
                panel.Children.Add(row);
            }
            var card = new MyCard { Title = group.Key, Tag = group.Key, CanSwap = true,
                IsSwapped = query.Length == 0 && (!collapsed.TryGetValue(group.Key, out bool swap) || swap), Margin = new Thickness(0, 0, 0, 15) };
            card.Children.Add(panel); card.Children.Add(category); _ = card.MainTextBlock; PanGroups.Children.Add(card);
        }
        PanBack.ScrollToVerticalOffset(offset);
    }
    private void BuildGlobalPreview(JObject values, HashSet<string> excluded) {
        double offset = PanBack.VerticalOffset;
        foreach (MyCard card in PanGroups.Children) if (card.Tag is string category) collapsed[category] = card.IsSwapped;
        PanGroups.Children.Clear(); profileKeyButtons.Clear();
        string query = (TextSearch.Text ?? "").Trim();
        foreach (var group in PersonalOptionsCatalog.Items.GroupBy(item => item.Category)) {
            var items = group.Where(item => (item.Title + " " + item.Key + " " + item.Category).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (query == "视野" && item.Key.StartsWith("fov", StringComparison.Ordinal))).ToArray();
            if (items.Length == 0) continue;
            var panel = new StackPanel { Margin = new Thickness(25, 40, 25, 18) };
            bool Selected(PersonalOptionDefinition item) => PageSetting<bool>(item.Setting) && !excluded.Contains(item.Key);
            var categoryToggle = new MyCheckBox { Text = "同步此分类", Checked = group.All(Selected), Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 9, 44, 0), Tag = "sync-category:" + group.Key };
            ProtectHeaderToggle(categoryToggle);
            categoryToggle.Change += (_, user) => { if (user && !refreshing) SelectOptions(group, categoryToggle.Checked); };
            var headings = EditorRow();
            AddEditorText(headings, "当前版本：" + (Target?.Name ?? "未选择"), 1);
            AddEditorText(headings, "全局方案：" + (string.IsNullOrWhiteSpace(importedSourceName) ? "来源未记录" : importedSourceName), 2);
            panel.Children.Add(headings);
            foreach (var definition in items) {
                var row = EditorRow(); row.MinHeight = 44;
                var label = new MyCheckBox { Text = definition.Title, Checked = Selected(definition), Tag = definition.Key, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                label.Change += (_, user) => { if (user && !refreshing) SelectOptions(new[] { definition }, label.Checked); };
                row.Children.Add(label);
                AddCurrentValue(row, definition, Target is null ? "未选择版本" : "此版本未提供");
                FrameworkElement editor = CreateValueEditor(definition, Selected(definition));
                Grid.SetColumn(editor, 2); row.Children.Add(editor);
                panel.Children.Add(row);
            }
            var preview = new MyCard { Title = group.Key, Tag = group.Key, CanSwap = true,
                IsSwapped = query.Length == 0 && (collapsed.TryGetValue(group.Key, out bool swap) ? swap : group.Key.Contains("按键绑定", StringComparison.Ordinal)),
                Margin = new Thickness(0, 0, 0, 15) };
            preview.Children.Add(panel); preview.Children.Add(categoryToggle); _ = preview.MainTextBlock; PanGroups.Children.Add(preview);
        }
        PanBack.ScrollToVerticalOffset(offset);
    }
    internal void SelectOptions(IEnumerable<PersonalOptionDefinition> definitions, bool enabled) {
        if (IsInstanceContext) throw new InvalidOperationException("请在全局设置页调整同步项目。");
        var chosen = definitions.ToArray();
        var excluded = PageExcluded();
        if (enabled) {
            foreach (string setting in chosen.Select(item => item.Setting).Distinct()) {
                if (PageSetting<bool>(setting)) continue;
                // Activating a former broad category must not enable unrelated menu items.
                foreach (var item in PersonalOptionsCatalog.Items.Where(item => item.Setting == setting)) excluded.Add(item.Key);
                StageSetting(setting, true);
            }
            foreach (var item in chosen) excluded.Remove(item.Key);
        } else foreach (var item in chosen) excluded.Add(item.Key);
        StageExcluded(excluded); RefreshPage();
    }
    private void BuildConflicts(JObject conflicts) {
        PanConflicts.Children.Clear();
        CardConflicts.Visibility = conflicts?.Properties().Any(entry => PersonalOptionsCatalog.Contains(entry.Name) && !stagedConflictChoices.ContainsKey(entry.Name)) == true ? Visibility.Visible : Visibility.Collapsed;
        if (conflicts is null) return;
        foreach (var entry in conflicts.Properties().Where(entry => PersonalOptionsCatalog.Contains(entry.Name))) {
            if (stagedConflictChoices.ContainsKey(entry.Name)) continue;
            if (entry.Value is not JObject record) continue;
            var expected = (JObject)record.DeepClone();
            var label = new TextBlock { Text = PersonalOptionsCatalog.Title(entry.Name) + "：个人方案 " + ValueText(entry.Name, record["profile"]) + "，游戏修改 " + ValueText(entry.Name, record["game"]), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            PanConflicts.Children.Add(label);
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
            foreach (bool game in new[] { false, true }) {
                var button = new MyButton { Text = game ? "采用游戏修改" : "保留个人方案", Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 12, 0), IsEnabled = !ModWatcher.HasRunningMinecraft };
                button.ToolTip = "选择后点击“保存全局设置”才会生效。";
                button.Click += (_, _) => Execute(() => StageConflict(entry.Name, game, expected));
                actions.Children.Add(button);
            }
            PanConflicts.Children.Add(actions);
        }
    }
    internal void ImportSelection() {
        if (IsInstanceContext) throw new InvalidOperationException("请在全局设置页导入共用方案。");
        FlushPendingEdits();
        var instance = Target;
        if (instance is null) throw new InvalidOperationException("请先在主界面选择一个已运行过的游戏版本。");
        if (!Confirm($"从 {instance.Name} 导入基础设置到全局方案，并开启个人同步？\r\n其他版本将在下次启动时应用它们已有的对应项目。" +
            (HasDraft ? "\r\n继续导入会替换尚未保存的修改。" : ""), "导入全局基础设置")) { RefreshPage(); return; }
        if (!ReferenceEquals(instance, Target)) { RefreshPage(); throw new InvalidOperationException("当前选择的游戏版本已变化，请重新操作。"); }
        PersonalGameSettings.ImportGlobalFrom(instance);
        Settings.Set("LaunchPersonalOptionsSync", true);
        stagedValues.Clear(); stagedSettings.Clear(); stagedConflictChoices.Clear();
        RefreshPage(); Notify("个人方案已导入，同步已开启。");
    }
    private void Execute(Action action) {
        try { action(); }
        catch (Exception error) { RefreshPage(); Logger.Error(error, "个人游戏设置同步操作失败", LogBehavior.Toast); }
    }
}
