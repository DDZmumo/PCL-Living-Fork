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
using System.Windows.Input;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed class EnvironmentHistoryPageIcon {
    public FrameworkElement Element { get; }
    private readonly Action<Action> subscribe;
    public EnvironmentHistoryPageIcon(FrameworkElement element, Action<Action> subscribe) { Element = element; this.subscribe = subscribe; }
    public void Subscribe(Action click) => subscribe(click);
}

public interface IEnvironmentHistoryPageRow {
    FrameworkElement Element { get; }
    JObject? Point { get; }
    bool Checked { get; set; }
    void Bind(JObject point);
    void SetButtons(List<EnvironmentHistoryPageIcon> buttons);
    void Subscribe(Action click, Action<object> changed);
}

public sealed class EnvironmentHistoryPageView<TInstance> where TInstance : class {
    public FrameworkElement Root = null!, CardRetention = null!, PanRetention = null!, CardList = null!, RestoreButton = null!;
    public Panel Entries = null!;
    public TextBlock Empty = null!, Select = null!;
    public Func<string> Search = null!, RetentionText = null!;
    public Action<string> SetSearch = null!, SetRetentionText = null!, SetAllText = null!, SetHealthyText = null!;
    public Func<bool> AutoCleanup = null!, RetentionHasFocus = null!, SearchHasFocus = null!;
    public Action<bool> SetAutoCleanup = null!;
    public Action CheckAll = null!, ForceRetentionResize = null!, ScrollHome = null!;
    public Func<IEnvironmentHistoryPageRow> CreateRow = null!;
    public Func<object, IEnvironmentHistoryPageRow> AdaptRow = null!;
    public Func<string, double?, string, EnvironmentHistoryPageIcon> CreateIcon = null!;
    public string RestoreLogo = null!, DetailsLogo = null!, OpenLogo = null!, DeleteLogo = null!;
    public Func<int, int, bool, int> RefreshSelectionBar = null!;
}

public sealed class EnvironmentHistoryPageHost<TInstance> where TInstance : class {
    public Func<TInstance?> CurrentInstance = null!;
    public Func<TInstance, string> VersionPath = null!;
    public Func<TInstance, bool> AutoCleanup = null!;
    public Func<TInstance, int> Retention = null!;
    public Action<TInstance, bool> SetAutoCleanup = null!;
    public Action<TInstance, int> SetRetention = null!;
    public Action<string> StartLoader = null!, HintError = null!, OpenFolder = null!;
    public Action CancelLoader = null!;
    public Func<bool> LoaderFailed = null!, AnimationEnabled = null!;
    public Action<bool> BindChanged = null!;
    public Action<TInstance?, bool, string?, IEnumerable<string>?> Start = null!;
    public Action<string, string> ShowDetails = null!;
}

/// <summary>LF 还原点页面的加载、保留设置、搜索、选择、键盘及命令编排。</summary>
// LF PageInstanceRestorePoints.xaml.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through 07dd3503b38b39095c8b92b40ae0454907387f9f. No CE code reused.
public sealed class EnvironmentHistoryPageController<TInstance> where TInstance : class {
    private readonly EnvironmentHistoryPageView<TInstance> view;
    private readonly EnvironmentHistoryPageHost<TInstance> host;
    public List<JObject> Entries = new();
    public readonly HashSet<string> SelectedIds = new(StringComparer.Ordinal);
    public bool Rendering, HealthyOnly, BindingRetention, RetentionDirty;
    public int BarCount;
    public TInstance? Owner;
    public string? PendingFocusId;
    public EnvironmentHistoryPageController(EnvironmentHistoryPageView<TInstance> view, EnvironmentHistoryPageHost<TInstance> host) {
        this.view = view; this.host = host;
    }
    private static bool SameText(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);

    public void FocusPoint(string? id) {
        PendingFocusId = id;
        view.SetSearch("");
        view.CheckAll();
        if (Entries.Any(item => SameText(item["id"]!.ToString(), id))) {
            SelectedIds.Clear();
            SelectedIds.Add(id!);
            RenderList();
        }
    }
    public void PageLoaded() { host.BindChanged(true); view.ScrollHome(); Refresh(); }
    public void PageUnloaded() { CommitRetention(); host.BindChanged(false); host.CancelLoader(); }
    public void Refresh() {
        if (!ReferenceEquals(Owner, host.CurrentInstance())) CommitRetention();
        Owner = host.CurrentInstance();
        BindRetention();
        if (Owner is null) return;
        SelectedIds.Clear();
        view.RestoreButton.IsEnabled = false;
        host.StartLoader(host.VersionPath(Owner));
    }
    public void BindRetention() {
        BindingRetention = true;
        try {
            view.CardRetention.IsEnabled = Owner is not null;
            view.SetAutoCleanup(Owner is not null && host.AutoCleanup(Owner));
            view.PanRetention.Visibility = view.AutoCleanup() ? Visibility.Visible : Visibility.Collapsed;
            if (!RetentionDirty || !view.RetentionHasFocus()) {
                view.SetRetentionText((Owner is null ? 10 : host.Retention(Owner)).ToString());
                RetentionDirty = false;
            }
        } finally { BindingRetention = false; }
    }
    public void AutoCleanupChanged(bool user) {
        if (BindingRetention) return;
        view.PanRetention.Visibility = view.AutoCleanup() ? Visibility.Visible : Visibility.Collapsed;
        view.ForceRetentionResize();
        if (user && Owner is not null) host.SetAutoCleanup(Owner, view.AutoCleanup());
    }
    public void RetentionEdited() {
        if (!BindingRetention && Owner is not null && view.RetentionHasFocus()) RetentionDirty = true;
    }
    public void RetentionKey(object sender, KeyEventArgs args) {
        if (args.Key != Key.Enter) return;
        CommitRetention(); args.Handled = true;
    }
    public bool CommitRetention() {
        if (BindingRetention || Owner is null || !RetentionDirty) return true;
        RetentionDirty = false;
        bool valid = int.TryParse(view.RetentionText(), out int count) && count >= 1 && count <= 100;
        int previous = host.Retention(Owner);
        if (valid && count != previous) host.SetRetention(Owner, count);
        BindingRetention = true;
        try { view.SetRetentionText((valid ? count : previous).ToString()); }
        finally { BindingRetention = false; }
        if (!valid) host.HintError("自动还原点保留数量必须为 1 到 100。");
        return valid;
    }
    public void LoadedPoints(Func<List<JObject>> output) {
        if (Owner is null || !ReferenceEquals(Owner, host.CurrentInstance())) return;
        Entries = output();
        if (PendingFocusId is not null && Entries.Any(item => SameText(item["id"]!.ToString(), PendingFocusId))) {
            SelectedIds.Add(PendingFocusId);
            PendingFocusId = null;
        }
        RenderList();
    }
    public void FilterChanged(bool healthyOnly) { HealthyOnly = healthyOnly; RenderList(); }
    public void RenderList() {
        if (view.Entries is null) return;
        view.Entries.Children.Clear();
        Rendering = true;
        view.RestoreButton.IsEnabled = false;
        string query = view.Search().Trim();
        List<JObject> matching = Entries.Where(item => (item["label"]!.ToString() + " " + item["description"]?.ToString() + " " +
            ((DateTime)item["created"]!).ToLocalTime().ToString("yyyy-MM-dd HH:mm")).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        view.SetAllText($"全部 ({matching.Count})");
        view.SetHealthyText($"正常运行 ({matching.Where(item => (bool)item["healthy"]!).Count()})");
        List<JObject> visible = matching.Where(item => !HealthyOnly || (bool)item["healthy"]!).ToList();
        SelectedIds.IntersectWith(visible.Select(item => item["id"]!.ToString()));
        view.Empty.Visibility = visible.Any() ? Visibility.Collapsed : Visibility.Visible;
        view.Empty.Text = Entries.Any() ? "没有找到符合条件的还原点。" : "暂无还原点。更新前会自动保存，也可以手动创建。";
        foreach (JObject point in visible) {
            IEnvironmentHistoryPageRow item = view.CreateRow();
            item.Bind(point);
            item.Checked = SelectedIds.Contains(point["id"]!.ToString());
            item.SetButtons(PointButtons(point));
            item.Subscribe(() => item.Checked = !item.Checked, SelectionChanged);
            view.Entries.Children.Add(item.Element);
        }
        Rendering = false;
        RefreshSelection();
    }
    public List<EnvironmentHistoryPageIcon> PointButtons(JObject point) {
        EnvironmentHistoryPageIcon restore = view.CreateIcon(view.RestoreLogo, 0.85, "恢复此还原点");
        EnvironmentHistoryPageIcon details = view.CreateIcon(view.DetailsLogo, null, "详情");
        EnvironmentHistoryPageIcon open = view.CreateIcon(view.OpenLogo, 1.05, "打开还原点文件夹");
        EnvironmentHistoryPageIcon delete = view.CreateIcon(view.DeleteLogo, null, "删除此还原点");
        restore.Subscribe(() => host.Start(Owner, true, point["id"]!.ToString(), null));
        details.Subscribe(() => host.ShowDetails(point["label"]!.ToString() + "\r\n" + ((DateTime)point["created"]!).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
            point["description"]?.ToString() + "\r\n" + ((bool)point["healthy"]! ? "正常运行记录" : "尚未标记正常运行") + "\r\n" +
            $"包含 {(point["files"] as JObject)?.Count ?? 0} 个环境文件", "还原点详情"));
        open.Subscribe(OpenFolder);
        delete.Subscribe(() => host.Start(Owner, false, null, new[] { point["id"]!.ToString() }));
        return new List<EnvironmentHistoryPageIcon> { restore, details, open, delete };
    }
    public void SelectionChanged(object sender) {
        if (Rendering) return;
        IEnvironmentHistoryPageRow row = view.AdaptRow(sender);
        if (row.Checked) SelectedIds.Add(row.Point!["id"]!.ToString());
        else SelectedIds.Remove(row.Point!["id"]!.ToString());
        RefreshSelection();
    }
    public void RefreshSelection() {
        view.RestoreButton.IsEnabled = SelectedIds.Count == 1;
        view.Select.Text = $"已选择 {SelectedIds.Count} 个还原点";
        view.CardList.Margin = new Thickness(0, 0, 0, SelectedIds.Count > 0 ? 95 : 14);
        BarCount = view.RefreshSelectionBar(SelectedIds.Count, BarCount, view.Root.IsLoaded && host.AnimationEnabled());
    }
    public void ChangeAllSelected(bool value) {
        Rendering = true;
        SelectedIds.Clear();
        foreach (UIElement child in view.Entries.Children) {
            IEnvironmentHistoryPageRow row = view.AdaptRow(child);
            row.Checked = value;
            if (value) SelectedIds.Add(row.Point!["id"]!.ToString());
        }
        Rendering = false;
        RefreshSelection();
    }
    public void SelectAll() => ChangeAllSelected(SelectedIds.Count < view.Entries.Children.Count);
    public void Deselect() => ChangeAllSelected(false);
    public void SelectionKeys(object sender, KeyEventArgs args) {
        if (args.Key == Key.Escape && SelectedIds.Count > 0) { ChangeAllSelected(false); args.Handled = true; }
        if (args.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !view.SearchHasFocus()) { ChangeAllSelected(true); args.Handled = true; }
    }
    public void DeleteSelected() { if (SelectedIds.Count > 0) host.Start(Owner, false, null, SelectedIds.ToArray()); }
    public void CreatePoint() => host.Start(Owner, false, null, null);
    public void RestorePoint() { if (SelectedIds.Count == 1) host.Start(Owner, true, SelectedIds.Single(), null); }
    public void OpenFolder() { if (Owner is not null) host.OpenFolder(host.VersionPath(Owner)); }
    public void Retry() { if (host.LoaderFailed()) Refresh(); }
}
