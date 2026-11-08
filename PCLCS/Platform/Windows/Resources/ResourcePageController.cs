// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF resource-page behavior, ported from fixed LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983.
// Legacy custom controls, global services and typed backend contracts are supplied by the host.
public sealed partial class ResourcePageController<TInstance, TEntry, TOnline>
    where TInstance : class where TEntry : InstanceResourceEntryData where TOnline : class {
    private const string NewLine = "\r\n";
    private readonly int Kind;
    private readonly ResourcePageView<TInstance, TEntry, TOnline> View;
    private readonly ResourcePageHost<TInstance, TEntry, TOnline> Host;
    private readonly InstanceResourceTransactions Transactions;
    public TInstance? Owner;
    public string? CurrentRoot, CurrentFolder, RecycleRoot;
    public List<TEntry> Entries = new();
    public TEntry? Selected;
    public CancellationTokenSource? Operation, OnlineCancellation;
    public bool Busy, Checking, Rendering;
    public int Filter, OnlineRevision;
    public int SortMode { get; private set; }
    public readonly HashSet<string> SelectedPaths = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, TOnline> Online = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IResourcePageRow<TEntry, TOnline>> Rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IResourcePageThumbnail<TEntry>> ScreenshotRows = new(StringComparer.OrdinalIgnoreCase);
    private bool NeedsSchematicLoader => Kind == 4 && Owner is not null && Host.NeedsSchematicLoader(Owner);

    public sealed class Request {
        public TInstance Instance = null!;
        public string Root = null!, Folder = null!, GameFolder = null!, RecycleFolder = null!;
        public bool RecycleMode;
    }
    public sealed class Result {
        public Request Request = null!;
        public List<TEntry> Entries = null!;
        public List<JObject>? Backups;
        public string? BackupError;
    }
    public ResourcePageController(int kind, ResourcePageView<TInstance, TEntry, TOnline> view,
        ResourcePageHost<TInstance, TEntry, TOnline> host, InstanceResourceTransactions transactions) {
        Kind = kind; View = view; Host = host; Transactions = transactions;
        SortMode = kind == 0 ? 3 : 1;
    }
    private bool SamePath(string? left, string? right) => Host.SamePath(left!, right!);
    private string Title => InstanceResourceKindRules.Title(Kind);
    private void EnsureWithin(string root, string path, bool allowRoot = false) =>
        InstanceResourcePaths.EnsureWithin(root, path, allowRoot, Host.RemoveExtendedPrefix);
    private void ValidateEntry(string root, TEntry entry) =>
        InstanceResourceEntryValidator.ValidateEntry(root, entry, (parent, file) => EnsureWithin(parent, file));

    public void PageLoaded() {
        Host.BindExternalEvents(true, Refresh, MainKeyDown);
        ClearSelection(); View.ScrollHome(); Refresh();
    }
    public void ClearSelection() {
        Rendering = true; SelectedPaths.Clear(); ClearBackupSelection();
        foreach (var row in Rows.Values) row.Checked = false;
        foreach (var tile in ScreenshotRows.Values) tile.Checked = false;
        Rendering = false; SelectResource(null);
    }
    public void PageUnloaded() {
        Host.BindExternalEvents(false, Refresh, MainKeyDown);
        Host.CancelLoader();
        foreach (var tile in ScreenshotRows.Values) tile.ReleaseThumbnail();
        Operation?.Cancel(); CancelOnline(); View.StopSelectionBar();
        BottomBarShownCount = 0; View.CardSelect.Visibility = Visibility.Collapsed;
        View.CardSelect.Opacity = 0; View.TransSelect.Y = -10;
    }
    public void Refresh() {
        TInstance? instance = Host.CurrentInstance();
        if (instance is null || !Host.AllowsInstance(instance)) return;
        string gameFolder = Host.GameFolder(instance);
        string root = Path.Combine(gameFolder, InstanceResourceKindRules.FolderName(Kind));
        if (!ReferenceEquals(Owner, instance) || !SamePath(CurrentRoot, root)) {
            IsRecycleMode = false; CurrentFolder = root; SelectedPaths.Clear(); Online.Clear(); Filter = 0;
            WorldBackupPoints.Clear(); ResetWorldGroups();
        }
        CancelOnline(); Owner = instance; CurrentRoot = root;
        if (CurrentFolder is null || !Directory.Exists(CurrentFolder)) CurrentFolder = root;
        string minecraftRoot = Path.GetDirectoryName(Path.GetDirectoryName(Host.VersionPath(instance).TrimEnd('\\')))!;
        string[] kinds = { "Saves", "Screenshots", "ResourcePacks", "ShaderPacks", "Schematics" };
        RecycleRoot = Path.Combine(minecraftRoot, "PCL", "ResourceRecycle", kinds[Kind]);
        Host.StartLoader(new Request { Instance = instance, Root = root, Folder = CurrentFolder,
            GameFolder = gameFolder, RecycleMode = IsRecycleMode, RecycleFolder = RecycleRoot });
    }
    public Result? Read(Request request, CancellationToken token, Func<bool> isCanceled) {
        List<TEntry> items = request.RecycleMode ? Host.RecycleList(request.Root, request.RecycleFolder, Kind)
            : Host.Scan(request.Root, request.Folder, Kind, token);
        if (isCanceled()) return null;
        Result result = new() { Request = request, Entries = items };
        if (Kind == 0 && !request.RecycleMode) {
            try { result.Backups = Host.BackupPoints(request.GameFolder); }
            catch (Exception error) { Host.Warn(error, "读取此游戏目录的存档备份失败"); result.BackupError = "存档备份读取失败，请刷新后重试。"; }
        }
        return result;
    }
    public void Apply(Result? result) {
        if (result is not null && result.Request.RecycleMode != IsRecycleMode) return;
        if (result is null || !ReferenceEquals(Owner, result.Request.Instance) || !ReferenceEquals(Host.CurrentInstance(), Owner) ||
            !SamePath(CurrentFolder, result.Request.Folder)) return;
        if (Kind == 0 && !SamePath(Host.GameFolder(Owner!), result.Request.GameFolder)) return;
        WorldBackupPoints = result.Backups ?? new List<JObject>();
        View.LabBackupStatus.Text = result.BackupError;
        View.LabBackupStatus.Visibility = string.IsNullOrEmpty(result.BackupError) ? Visibility.Collapsed : Visibility.Visible;
        ShowResources(result.Request.Root, result.Request.Folder, result.Entries); CheckUpdates();
    }
    public void ShowResources(string root, string folder, IEnumerable<TEntry> items) {
        CurrentRoot = root; CurrentFolder = folder; Entries = items.ToList();
        View.BackupSettings.Visibility = Kind == 0 ? Visibility.Visible : Visibility.Collapsed;
        View.BtnBackupAll.Visibility = View.BackupSettings.Visibility;
        View.PanManageButtons.ColumnDefinitions[4].SharedSizeGroup = Kind == 0 ? "Button" : null;
        if (Kind == 0) View.BindBackupOwner(Owner);
        foreach (string key in Online.Keys.ToList()) {
            TEntry? current = Entries.FirstOrDefault(item => SamePath(item.FullPath, key));
            if (current is null) { Online.Remove(key); continue; }
            TEntry old = Host.OnlineEntry(Online[key]);
            if (current.ModifiedUtc != old.ModifiedUtc || current.Length != old.Length) Online.Remove(key);
        }
        View.BtnOpenFolder.ToolTip = folder;
        View.SetSearchHint("搜索" + Title + (Host.UpdatesSupported(Kind) ? "名称 / 描述 / 标签" : "名称 / 文件信息"));
        bool schematic = Kind == 4, needsLoader = NeedsSchematicLoader;
        View.BtnDownload.Text = schematic ? (needsLoader ? "先安装加载器" : "下载投影 Mod") : "下载新" + Title;
        View.BtnHintDownload.Text = needsLoader ? "先安装 Mod 加载器" : View.BtnDownload.Text;
        View.BtnDownload.ToolTip = schematic ? (needsLoader ? "当前版本尚未安装 Mod 加载器，点击前往此版本的修改页面。" : "前往 Mod 下载页面搜索 Litematica（投影），使用当前游戏版本及加载器筛选。") : null;
        View.BtnHintDownload.ToolTip = View.BtnDownload.ToolTip;
        View.BtnDownload.Visibility = needsLoader || ((Host.UpdatesSupported(Kind) || schematic) && !Host.ModpackEnabled()) ? Visibility.Visible : Visibility.Collapsed;
        View.PanManageButtons.ColumnDefinitions[2].SharedSizeGroup = View.BtnDownload.Visibility == Visibility.Visible ? "Button" : null;
        View.BtnUpdate.Visibility = Host.UpdatesSupported(Kind) ? Visibility.Visible : Visibility.Collapsed;
        View.BtnHintDownload.Visibility = View.BtnDownload.Visibility;
        View.LabEmptyTitle.Text = "暂无" + Title;
        View.LabEmptyHint.Text = (Host.UpdatesSupported(Kind) ? "你可以下载新的" + Title + "，或者从已经下载好的文件安装。" : "可以从本地文件安装，或将文件拖入此页面。") + NewLine + "如果资源已经存在，请在版本设置中检查版本隔离选项。";
        if (schematic) View.LabEmptyHint.Text = "投影/原理图文件需要配合投影 Mod（如 Litematica）在游戏内使用。" + NewLine +
            (needsLoader ? "当前版本还没有 Mod 加载器，请先前往修改页面安装加载器。" : "可以前往 Mod 下载页面获取投影 Mod，再安装或拖入原理图文件。");
        View.BtnUp.Visibility = schematic && !SamePath(root, folder) ? Visibility.Visible : Visibility.Collapsed;
        ApplyRecycleMode(); RenderList();
    }
    public void SearchChanged() => RenderList();
    public void SortChanged(int value) {
        if (value < 0 || value > 4 || value >= 3 && Kind != 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (SortMode == value) return;
        SortMode = value;
        RenderList();
    }
    private List<TEntry> SortVisible(List<TEntry> visible) {
        // Recycle entry creation is the deletion-record time, not installation or LastPlayed.
        if (SortMode == 0 || IsRecycleMode) return visible;
        bool newestFirst = SortMode == 1 || SortMode == 3;
        return LocalResourceSort.ByTime(visible, newestFirst,
            entry => SortMode >= 3 ? entry.LastPlayedUtc : entry.CreatedUtc > DateTime.FromFileTimeUtc(0) ? entry.CreatedUtc : (DateTime?)null,
            entry => entry.Name ?? "", entry => entry.FullPath ?? "").ToList();
    }
    private string OnlineSearchText(TEntry entry) => Online.TryGetValue(entry.FullPath!, out TOnline? info)
        ? Host.ProjectName(info) + " " + string.Join(" ", Host.Tags(info)) : "";
    private bool HasUpdate(TEntry entry) => Online.TryGetValue(entry.FullPath!, out TOnline? info) && Host.HasUpdate(info);
    private void RenderList() {
        if (View.PanList is null) return;
        Rendering = true;
        try {
            foreach (var button in View.Filters) button.Checked = button.Value == Filter;
            string query = View.GetSearch().Trim();
            List<TEntry> matches = Entries.Where(item => query == "" || (item.Name + " " + item.Description + " " + OnlineSearchText(item)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            List<TEntry> visible = matches.Where(item => Filter == 0 || (Filter == 1 && HasUpdate(item)) || (Filter == 2 && !item.IsDirectory) || (Filter == 3 && item.IsDirectory)).ToList();
            visible = SortVisible(visible);
            View.FilterAll.Text = (query == "" ? "全部" : "搜索结果") + $" ({matches.Count})";
            View.FilterUpdates.Text = $"可更新 ({matches.Count(HasUpdate)})";
            View.FilterUpdates.Visibility = Host.UpdatesSupported(Kind) && (Filter == 1 || matches.Any(HasUpdate)) ? Visibility.Visible : Visibility.Collapsed;
            View.FilterFiles.Text = $"文件 ({matches.Count(item => !item.IsDirectory)})";
            View.FilterFolders.Text = $"文件夹 ({matches.Count(item => item.IsDirectory)})";
            bool mixed = Entries.Any(item => item.IsDirectory) && Entries.Any(item => !item.IsDirectory);
            View.FilterFiles.Visibility = mixed || Filter == 2 ? Visibility.Visible : Visibility.Collapsed;
            View.FilterFolders.Visibility = mixed || Filter == 3 ? Visibility.Visible : Visibility.Collapsed;
            View.LabEmpty.Visibility = visible.Any() ? Visibility.Collapsed : Visibility.Visible;
            View.LabEmpty.Text = query == "" && Filter == 0 ? "暂无" + Title + "，可以从本地文件安装" + (Kind == 1 ? "或拖入图片。" : "或拖入文件夹。") :
                (Filter == 1 ? "未发现可更新的资源，可刷新列表重新检查。" : "没有找到匹配的资源。");
            SelectedPaths.IntersectWith(visible.Select(item => item.FullPath!));
            View.PanList.Visibility = !IsRecycleMode && (Kind == 1 || Kind == 0) ? Visibility.Collapsed : Visibility.Visible;
            View.PanWorlds.Visibility = Kind == 0 && !IsRecycleMode ? Visibility.Visible : Visibility.Collapsed;
            View.PanScreenshots.Visibility = Kind == 1 && !IsRecycleMode ? Visibility.Visible : Visibility.Collapsed;
            if (Kind == 1 && !IsRecycleMode) { RenderScreenshots(visible); SelectResource(Selection().FirstOrDefault()); return; }
            if (Kind == 0 && !IsRecycleMode) { RenderWorldGroups(visible, query); SelectResource(Selection().FirstOrDefault()); return; }
            foreach (string key in Rows.Keys.Where(value => !Entries.Any(item => string.Equals(item.FullPath, value, StringComparison.OrdinalIgnoreCase))).ToList()) Rows.Remove(key);
            List<IResourcePageRow<TEntry, TOnline>> desired = new();
            foreach (TEntry entry in visible) {
                Online.TryGetValue(entry.FullPath!, out TOnline? info);
                if (!Rows.TryGetValue(entry.FullPath!, out var row)) { row = CreateResourceRow(); Rows.Add(entry.FullPath!, row); }
                row.Bind(entry, info); row.Checked = SelectedPaths.Contains(entry.FullPath!); desired.Add(row);
            }
            foreach (UIElement element in View.PanList.Children.Cast<UIElement>().Where(element => !desired.Any(row => ReferenceEquals(row.Element, element))).ToList()) View.PanList.Children.Remove(element);
            for (int index = 0; index < desired.Count; index++) {
                var element = desired[index].Element;
                if (View.PanList.Children.IndexOf(element) == index) continue;
                View.PanList.Children.Remove(element); View.PanList.Children.Insert(index, element);
            }
            TEntry? selected = Selected;
            TEntry? next = selected is null ? null : visible.FirstOrDefault(item => SelectedPaths.Contains(item.FullPath!) && SamePath(item.FullPath, selected.FullPath));
            SelectResource(next ?? visible.FirstOrDefault(item => SelectedPaths.Contains(item.FullPath!)));
        } finally { Rendering = false; }
    }
    private void RenderScreenshots(List<TEntry> visible) {
        foreach (string key in ScreenshotRows.Keys.Where(value => !Entries.Any(item => string.Equals(item.FullPath, value, StringComparison.OrdinalIgnoreCase))).ToList()) {
            ScreenshotRows[key].ReleaseThumbnail(); ScreenshotRows.Remove(key);
        }
        List<IResourcePageThumbnail<TEntry>> desired = new();
        foreach (TEntry entry in visible) {
            if (!ScreenshotRows.TryGetValue(entry.FullPath!, out var tile)) {
                tile = View.CreateThumbnail(ScreenshotSelectionChanged, (selected, count) => ScreenshotClick(selected, count));
                tile.Element.Loaded += (_, _) => tile.UpdateViewport(View.Viewport);
                ScreenshotRows.Add(entry.FullPath!, tile);
            }
            tile.Bind(entry); tile.Checked = SelectedPaths.Contains(entry.FullPath!); desired.Add(tile);
        }
        foreach (UIElement element in View.PanScreenshots.Children.Cast<UIElement>().Where(element => !desired.Any(tile => ReferenceEquals(tile.Element, element))).ToList()) {
            var old = ScreenshotRows.Values.FirstOrDefault(tile => ReferenceEquals(tile.Element, element));
            old?.ReleaseThumbnail(); View.PanScreenshots.Children.Remove(element);
        }
        for (int index = 0; index < desired.Count; index++) {
            var element = desired[index].Element;
            if (View.PanScreenshots.Children.IndexOf(element) == index) continue;
            View.PanScreenshots.Children.Remove(element); View.PanScreenshots.Children.Insert(index, element);
        }
        RefreshScreenshotViewport();
    }
    public void RefreshScreenshotViewport() {
        if (Kind != 1) return;
        var tilesByElement = ScreenshotRows.Values.ToDictionary(tile => tile.Element);
        foreach (UIElement element in View.PanScreenshots.Children) tilesByElement[(FrameworkElement)element].UpdateViewport(View.Viewport);
    }
    private void ScreenshotSelectionChanged(IResourcePageThumbnail<TEntry> tile) {
        if (Rendering || Busy) return;
        if (tile.Checked) SelectedPaths.Add(tile.Entry.FullPath!); else SelectedPaths.Remove(tile.Entry.FullPath!);
        SelectResource(Selection().FirstOrDefault());
    }
    public void ScreenshotClick(TEntry? entry, int count, Action<ProcessStartInfo>? open = null) {
        if (Busy || Kind != 1 || entry is null || count < 1 || !Entries.Any(item => ReferenceEquals(item, entry))) return;
        Rendering = true;
        if (Keyboard.Modifiers != ModifierKeys.Control || count == 2) {
            SelectedPaths.Clear(); SelectedPaths.Add(entry.FullPath!);
            foreach (var tile in ScreenshotRows.Values) tile.Checked = ReferenceEquals(tile.Entry, entry);
        } else {
            var tile = ScreenshotRows[entry.FullPath!]; tile.Checked = !tile.Checked;
            if (tile.Checked) SelectedPaths.Add(entry.FullPath!); else SelectedPaths.Remove(entry.FullPath!);
        }
        Rendering = false; SelectResource(Selection().FirstOrDefault());
        if (count != 2) return;
        try { ValidateEntry(CurrentRoot!, entry); (open ?? Host.OpenProcess)(new ProcessStartInfo(entry.FullPath!) { UseShellExecute = true }); }
        catch (Exception error) { Host.Error(error, "使用默认图片软件打开截图失败"); }
    }
    private IResourcePageRow<TEntry, TOnline> CreateResourceRow() {
        ResourcePageRowActions<TEntry, TOnline> actions = new() {
            SelectionChanged = SelectionChanged, Click = row => row.Checked = !row.Checked,
            Details = ShowDetails, Open = OpenEntry, Rename = RenameEntry,
            Delete = entry => DeleteResources(new[] { entry }), Update = entry => UpdateResources(new[] { entry }),
            Play = entry => { if (CanChange()) Host.QuickPlay(Owner!, entry.Name!); },
            CanPlay = () => Kind == 0 && Host.QuickPlaySupported(Owner!), IsRecycle = () => IsRecycleMode,
            Restore = entry => RestoreRecycleSelection(new[] { entry }), Purge = entry => PurgeRecycleSelection(new[] { entry }),
            EnterFolder = entry => { if (IsRecycleMode || Busy || Kind != 4 || !entry.IsDirectory) return; CurrentFolder = entry.FullPath; SelectedPaths.Clear(); Filter = 0; Refresh(); }
        };
        return View.CreateRow(actions);
    }
    private List<TEntry> Selection() => Entries.Where(item => SelectedPaths.Contains(item.FullPath!)).ToList();
    private bool SelectionChanged(IResourcePageRow<TEntry, TOnline> row) {
        if (Rendering) return false;
        if (Busy) return true;
        ClearBackupSelection();
        if (row.Checked) SelectedPaths.Add(row.Entry.FullPath!); else SelectedPaths.Remove(row.Entry.FullPath!);
        SelectResource(row.Checked ? row.Entry : Selection().LastOrDefault()); return false;
    }
    public void ChangeAllSelected(bool value) {
        if (Busy) return;
        SelectedPaths.Clear(); ClearBackupSelection(); Rendering = true;
        foreach (var row in VisibleResourceRows()) { row.Checked = value; if (value) SelectedPaths.Add(row.Entry.FullPath!); }
        var tilesByElement = ScreenshotRows.Values.ToDictionary(tile => tile.Element);
        foreach (UIElement element in View.PanScreenshots.Children) {
            var tile = tilesByElement[(FrameworkElement)element];
            tile.Checked = value; if (value) SelectedPaths.Add(tile.Entry.FullPath!);
        }
        Rendering = false; SelectResource(Selection().FirstOrDefault());
    }
    public void SelectAllClick() => ChangeAllSelected(SelectedPaths.Count < (Kind == 1 && !IsRecycleMode ? View.PanScreenshots.Children.Count : VisibleResourceRows().Count()));
    public void DeselectClick() => ChangeAllSelected(false);
    public void FilterChanged(int value) { if (Rendering || View.PanList is null) return; Filter = value; RenderList(); }
    public void MainKeyDown(object sender, KeyEventArgs args) { if (Host.IsCurrentPage()) ResourceKeyDown(sender, args); }
    public void SearchKeyDown(object sender, KeyEventArgs args) { if (View.GetSearch() == "" && args.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) View.FocusViewport(); }
    public void ResourceKeyDown(object sender, KeyEventArgs args) {
        if (args.Key == Key.Escape && (SelectedPaths.Count > 0 || SelectedBackupIds().Count > 0)) { ChangeAllSelected(false); args.Handled = true; }
        if (args.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !View.SearchHasFocus()) { ChangeAllSelected(true); args.Handled = true; }
    }
    private void ShowDetails(TEntry entry) {
        if (IsRecycleMode) { Host.Message(entry.Name + NewLine + entry.Description, "回收站资源详情", "确定", "", false); return; }
        if (Kind == 0) {
            string root = CurrentRoot!;
            Host.Background(() => { try { ValidateEntry(root, entry); string content = Host.DescribeWorld(entry.FullPath!); Host.Dispatch(() => Host.Message(content, "存档详情", "确定", "", false)); } catch (Exception error) { Host.Error(error, "读取存档详情失败"); } }, "Local World Details"); return;
        }
        if (Kind == 1) return;
        Online.TryGetValue(entry.FullPath!, out TOnline? info);
        string content = entry.Name + NewLine + entry.Description + NewLine + entry.FullPath + (info is null ? "" : NewLine + Host.Status(info));
        int choice = Host.Message(content, "资源详情", "确定", info is null || string.IsNullOrEmpty(Host.Website(info)) ? "" : "项目主页", false);
        if (choice == 2 && info is not null && Host.Website(info) is not null) Host.OpenWebsite(Host.Website(info)!);
    }
    private void SelectResource(TEntry? entry) { Selected = entry; UpdateButtons(); }
    private void UpdateButtons() {
        View.BtnImportFile.IsEnabled = !Busy; View.BtnUp.IsEnabled = !Busy; View.BtnDownload.IsEnabled = !Busy;
        View.BtnSelectAll.IsEnabled = !Busy && (Kind == 1 && !IsRecycleMode ? View.PanScreenshots.Children.Count : VisibleResourceRows().Count()) > 0;
        View.BtnExport.IsEnabled = !Busy && SelectedPaths.Count > 0; View.BtnDelete.IsEnabled = View.BtnExport.IsEnabled;
        View.BtnDeselect.IsEnabled = !Busy; View.BtnUpdate.IsEnabled = !Busy && Selection().Any(HasUpdate);
        View.BtnRestore.IsEnabled = !Busy && CurrentRoot is not null; View.BtnHintRestore.IsEnabled = View.BtnRestore.IsEnabled;
        View.BtnRestore.Visibility = IsRecycleMode ? Visibility.Collapsed : Visibility.Visible;
        View.BtnRecycleBack.IsEnabled = View.BtnRestore.IsEnabled; View.BtnHintRestore.Visibility = Visibility.Collapsed;
        View.BtnCancel.Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
        View.PanOperations.Visibility = View.BtnUp.Visibility == Visibility.Visible || View.LabOperation.Visibility == Visibility.Visible || Busy ? Visibility.Visible : Visibility.Collapsed;
        View.PanEmpty.Visibility = Visibility.Collapsed;
        View.Viewport.Visibility = View.PanEmpty.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        View.PanList.IsHitTestVisible = !Busy; View.PanWorlds.IsHitTestVisible = !Busy; View.PanScreenshots.IsHitTestVisible = !Busy;
        RefreshBackupActions(); if (IsRecycleMode) ApplyRecycleMode();
    }
    private void CancelOnline() { Interlocked.Increment(ref OnlineRevision); OnlineCancellation?.Cancel(); OnlineCancellation = null; Checking = false; }
    public void CheckUpdates() {
        if (IsRecycleMode || !Host.UpdatesSupported(Kind) || Owner is null || Busy || Checking || !Entries.Any(item => !item.IsDirectory)) return;
        CancelOnline(); CancellationTokenSource cancellation = new(); OnlineCancellation = cancellation;
        int revision = OnlineRevision; TInstance target = Owner; string root = CurrentRoot!, folder = CurrentFolder!;
        var items = Entries.ToList(); string version = Host.GameVersion(Owner);
        Checking = true; View.FilterUpdates.ToolTip = "正在检查资源更新……"; UpdateButtons();
        Host.Background(() => {
            List<TOnline>? result = null; Exception? failure = null;
            try { result = Host.CheckOnline(root, items, Kind, version, cancellation.Token); } catch (Exception error) { failure = error; }
            Host.Dispatch(() => {
                if (revision != OnlineRevision || !ReferenceEquals(Owner, target) || !SamePath(CurrentFolder, folder)) { cancellation.Dispose(); return; }
                OnlineCancellation = null; Checking = false; cancellation.Dispose();
                if (failure is null) { ApplyOnlineInfo(result!); View.FilterUpdates.ToolTip = $"已找到 {result!.Count(Host.HasUpdate)} 个兼容更新。"; }
                else if (failure is OperationCanceledException) SetStatus("已取消检查更新。");
                else { SetStatus("检查资源更新失败，可通过左侧刷新入口重试。"); Host.Warn(failure, "检查资源更新失败"); }
                UpdateButtons();
            });
        }, "Resource Update Check");
    }
    public void ApplyOnlineInfo(IEnumerable<TOnline> infos) {
        foreach (TOnline info in infos) {
            TEntry source = Host.OnlineEntry(info); TEntry? current = Entries.FirstOrDefault(item => SamePath(item.FullPath, source.FullPath));
            if (current is not null && current.ModifiedUtc == source.ModifiedUtc && current.Length == source.Length) Online[current.FullPath!] = info;
        }
        RenderList();
    }
    private void SetStatus(string? text) { View.LabOperation.Text = text; View.LabOperation.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible; UpdateButtons(); }
    public void DownloadNew() {
        if (Busy || Owner is null) return;
        if (NeedsSchematicLoader) { Host.Navigate(0, Owner, ""); return; }
        if (Host.ModpackEnabled() || (!Host.UpdatesSupported(Kind) && Kind != 4)) return;
        Host.Navigate(Kind == 4 ? 1 : Kind == 2 ? 2 : 3, Owner, Kind == 4 ? "Litematica" : View.GetSearch());
    }
    public void UpdateSelected() => UpdateResources(Selection());
    private void UpdateResources(IEnumerable<TEntry> resources) {
        if (!CanChange()) return;
        if (Host.HasRunningGame()) { Host.Hint("请先关闭游戏，再更新资源包或光影包。", false); return; }
        List<TOnline> infos = resources.Where(HasUpdate).Select(item => Online[item.FullPath!]).ToList(); if (infos.Count == 0) return;
        if (Host.Message($"将更新 {infos.Count} 个{Title}。{NewLine}新版本可能改变游戏画面或与整合包不兼容，请确认后继续。旧文件会保留在本地回收区，可通过恢复按钮找回。", "资源更新", "更新", "取消", true) != 1) return;
        string root = CurrentRoot!, recycle = RecycleRoot!, version = Host.GameVersion(Owner!);
        RunOperation("更新资源", token => Host.InstallOnline(root, infos, recycle, Kind, version, token));
    }
    private bool CanChange(bool exporting = false) {
        if (Busy || Owner is null || CurrentRoot is null) return false;
        if (Host.IsLocked(Owner)) { Host.Hint("该版本正在被修改，请稍后再管理资源！", false); return false; }
        if (Host.HasRunningGame() && Kind == 0) { Host.Hint("请先关闭游戏，再修改或导出存档，避免存档数据不完整。", false); return false; }
        return true;
    }
    private bool ConfirmImport() => !Host.ModpackEnabled() || Host.Message("更改整合包的本地资源可能影响整合包运行。是否继续导入？", "整合包资源管理", "继续", "取消", true) == 1;
    private void RunOperation(string name, Action<CancellationToken> work) {
        CancelOnline(); Busy = true; Operation = new CancellationTokenSource(); CancellationToken token = Operation.Token; TInstance? target = Owner;
        SetStatus("正在" + name + "……"); UpdateButtons();
        Thread worker = new(() => {
            Exception? failure = null; try { work(token); } catch (Exception error) { failure = error; }
            Host.Dispatch(() => {
                Busy = false; Operation!.Dispose(); Operation = null; UpdateButtons();
                if (failure is null) {
                    if (name == "删除资源" || name == "还原资源" || name == "彻底删除资源") { SetStatus(null); Host.Hint(name == "删除资源" ? "已将资源移入回收站" : name == "还原资源" ? "已还原资源" : "已彻底删除资源", true); }
                    else SetStatus(name + "完成。");
                } else if (failure is OperationCanceledException) SetStatus("操作已取消，已完成的内容会保留。");
                else { SetStatus(failure.Message); Host.Error(failure, name + "失败"); }
                if (ReferenceEquals(Owner, target) && View.Root.IsLoaded) Refresh();
            });
        }) { Name = "Resource " + name, IsBackground = true };
        worker.Start();
    }
    public void OpenFolder() {
        if (IsRecycleMode) { if (RecycleRoot is not null && Directory.Exists(RecycleRoot)) Host.OpenExplorer(RecycleRoot.TrimEnd('\\', '/') + "\\"); return; }
        if (CurrentRoot is null) return;
        try { EnsureWithin(CurrentRoot, CurrentFolder!, true); Host.CreateDirectory(CurrentFolder!); Host.OpenExplorer(CurrentFolder!.TrimEnd('\\', '/') + "\\"); }
        catch (Exception error) { Host.Error(error, "打开资源文件夹失败"); }
    }
    private void OpenEntry(TEntry entry) { try { ValidateEntry(CurrentRoot!, entry); Host.OpenExplorer(entry.FullPath!); } catch (Exception error) { Host.Error(error, "打开资源位置失败"); } }
    public void RetryLoad() { if (Host.LoaderFailed()) Refresh(); }
    public void UpClick() { if (CurrentRoot is null || SamePath(CurrentRoot, CurrentFolder)) return; CurrentFolder = Path.GetDirectoryName(CurrentFolder!.TrimEnd('\\')); SelectedPaths.Clear(); Filter = 0; Refresh(); }
    public void ImportFiles() {
        if (IsRecycleMode) { RestoreRecycleSelection(); return; }
        if (!CanChange() || !ConfirmImport()) return;
        string[] extensions = Kind == 0 || Kind == 2 || Kind == 3 ? new[] { "zip" } : Kind == 1 ? new[] { "png", "jpg", "jpeg", "bmp" } : new[] { "litematic", "schem", "schematic", "nbt" };
        string[] files = Host.SelectFiles("导入" + Title, Kind != 0, extensions); if (!files.Any()) return;
        string? name = null;
        if (Kind == 0) { name = Host.Input("导入存档", "请输入导入后的存档文件夹名称。", Path.GetFileNameWithoutExtension(files[0]), CurrentRoot, false); if (string.IsNullOrWhiteSpace(name)) return; }
        string root = CurrentRoot!, folder = CurrentFolder!;
        RunOperation("导入资源", token => { foreach (string file in files) { if (Kind == 0) Transactions.ImportWorld(root, file, name!, token); else Transactions.ImportFile(root, folder, Kind, file, token); } });
    }
    public void ResourceDragOver(object sender, DragEventArgs args) { if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return; args.Effects = Busy || Owner is null ? DragDropEffects.None : DragDropEffects.Copy; args.Handled = true; }
    public void ResourceDrop(object sender, DragEventArgs args) {
        if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return; args.Handled = true;
        if (IsRecycleMode || !CanChange() || !ConfirmImport()) return;
        string[]? sources = args.Data.GetData(DataFormats.FileDrop) as string[]; if (sources is null || sources.Length == 0) return;
        string root = CurrentRoot!, folder = CurrentFolder!;
        RunOperation("安装资源", token => { foreach (string source in sources.Distinct(StringComparer.OrdinalIgnoreCase)) { token.ThrowIfCancellationRequested(); if (Directory.Exists(source)) Transactions.ImportFolder(root, folder, Kind, source, new DirectoryInfo(source).Name, token); else if (Kind == 0) Transactions.ImportWorld(root, source, Path.GetFileNameWithoutExtension(source), token); else Transactions.ImportFile(root, folder, Kind, source, token); } });
    }
    public void ExportSelected() {
        if (IsRecycleMode) { RestoreRecycleSelection(); return; }
        var items = Selection(); string root = CurrentRoot!; if (items.Count == 0 || !CanChange(true)) return;
        if (items.Count == 1) {
            TEntry entry = items[0]; string extension = entry.IsDirectory ? "zip" : Path.GetExtension(entry.Name!).TrimStart('.');
            string? destination = Host.SaveFile("导出" + Title, entry.IsDirectory ? entry.Name + ".zip" : entry.Name!, extension);
            if (string.IsNullOrEmpty(destination)) return; RunOperation("导出资源", token => Transactions.Export(root, entry, destination!, token));
        } else { string? folder = Host.SelectFolder("选择批量导出的目标文件夹", false).FirstOrDefault(); if (string.IsNullOrWhiteSpace(folder)) return; RunOperation("批量导出资源", token => Transactions.ExportMany(root, items, folder!, token)); }
    }
    private void RenameEntry(TEntry entry) {
        if (!CanChange()) return; string root = CurrentRoot!;
        string? name = Host.Input("重命名资源", (Kind == 0 ? "只修改存档文件夹名称，不修改游戏内的世界名称。" : "请输入新的文件或文件夹名称。") + (Host.ModpackEnabled() ? NewLine + "更改整合包资源可能影响运行，请确认后继续。" : ""), entry.Name!, null, true);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return; RunOperation("重命名资源", token => Transactions.Rename(root, entry, name!));
    }
    public void DeleteSelected() { var ids = SelectedBackupIds(); if (ids.Count > 0) { Host.StartBackup(Owner!, ids, null); return; } DeleteResources(Selection()); }
    private void DeleteResources(IEnumerable<TEntry> resources) {
        if (IsRecycleMode) { PurgeRecycleSelection(resources); return; } if (!CanChange()) return;
        var items = resources.ToList(); string root = CurrentRoot!, recycle = RecycleRoot!; if (items.Count == 0) return;
        string names = string.Join(NewLine, items.Take(6).Select(item => " · " + item.Name));
        if (Host.Message($"确定删除以下 {items.Count} 个资源吗？{NewLine}{names}{(items.Count > 6 ? NewLine + "……" : "")}{NewLine}资源会移入回收站，可从上方的回收站按钮还原。", "删除资源", "移入回收站", "取消", true) != 1) return;
        RunOperation("删除资源", token => Transactions.RecycleMany(root, items, recycle, token));
    }
    public void CancelClick() { Operation?.Cancel(); if (Checking) { CancelOnline(); SetStatus("已取消检查更新。"); UpdateButtons(); } }
}
