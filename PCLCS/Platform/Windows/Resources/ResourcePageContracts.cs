// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// The wrappers map named LF controls directly; no reflection/dynamic or main assembly reference.
public sealed class ResourcePageButton {
    public FrameworkElement Element { get; }
    private readonly Func<string> getText;
    private readonly Action<string> setText;
    private readonly Action<string>? setLogo;
    public ResourcePageButton(FrameworkElement element, Func<string> getText, Action<string> setText, Action<string>? setLogo = null) {
        Element = element; this.getText = getText; this.setText = setText; this.setLogo = setLogo;
    }
    public string Text { get => getText(); set => setText(value); }
    public string Logo { set => setLogo!(value); }
    public bool IsEnabled { get => Element.IsEnabled; set => Element.IsEnabled = value; }
    public Visibility Visibility { get => Element.Visibility; set => Element.Visibility = value; }
    public object? ToolTip { get => Element.ToolTip; set => Element.ToolTip = value; }
    public double MinWidth { get => Element.MinWidth; set => Element.MinWidth = value; }
}

public sealed class ResourcePageFilter {
    public FrameworkElement Element { get; }
    private readonly Func<bool> getChecked;
    private readonly Action<bool> setChecked;
    private readonly Action<string> setText;
    public int Value { get; }
    public ResourcePageFilter(FrameworkElement element, int value, Func<bool> getChecked, Action<bool> setChecked, Action<string> setText) {
        Element = element; Value = value; this.getChecked = getChecked; this.setChecked = setChecked; this.setText = setText;
    }
    public bool Checked { get => getChecked(); set => setChecked(value); }
    public string Text { set => setText(value); }
    public Visibility Visibility { get => Element.Visibility; set => Element.Visibility = value; }
    public object? ToolTip { get => Element.ToolTip; set => Element.ToolTip = value; }
}

public interface IResourcePageRow<TEntry, TOnline> where TEntry : InstanceResourceEntryData where TOnline : class {
    FrameworkElement Element { get; }
    TEntry Entry { get; }
    bool Checked { get; set; }
    void Bind(TEntry entry, TOnline? online);
}
public interface IResourcePageThumbnail<TEntry> where TEntry : InstanceResourceEntryData {
    FrameworkElement Element { get; }
    TEntry Entry { get; }
    bool Checked { get; set; }
    void Bind(TEntry entry);
    void UpdateViewport(FrameworkElement viewport);
    void ReleaseThumbnail();
}
public interface IResourcePageBackupGroup<TInstance> where TInstance : class {
    FrameworkElement Element { get; }
    string FolderName { get; }
    IEnumerable<string> SelectedIds { get; }
    void ClearSelection(bool animate);
    void Bind(TInstance owner, string name, List<JObject> points);
    void PrefixTitle(string name);
}
public interface IResourcePageCard {
    FrameworkElement Element { get; }
    Panel Children { get; }
    string Title { get; set; }
    void InitializeHeader();
}

public sealed class ResourcePageRowActions<TEntry, TOnline> where TEntry : InstanceResourceEntryData where TOnline : class {
    public Func<IResourcePageRow<TEntry, TOnline>, bool> SelectionChanged = null!;
    public Action<IResourcePageRow<TEntry, TOnline>> Click = null!;
    public Action<TEntry> Details = null!, Open = null!, Rename = null!, Delete = null!, Update = null!, Play = null!, EnterFolder = null!, Restore = null!, Purge = null!;
    public Func<bool> CanPlay = null!, IsRecycle = null!;
}

public sealed class ResourcePageView<TInstance, TEntry, TOnline>
    where TInstance : class where TEntry : InstanceResourceEntryData where TOnline : class {
    public FrameworkElement Root = null!, Viewport = null!, PanFilter = null!, PanEmpty = null!, PanOperations = null!, PanResourceList = null!, BackupSettings = null!;
    // Optional for the original acceptance hosts, whose header uses absolute margins.
    public FrameworkElement? SortHeader, SortControl;
    public Panel PanList = null!, PanWorlds = null!;
    public UniformGrid PanScreenshots = null!;
    public Grid PanManageButtons = null!;
    public IResourcePageCard CardList = null!;
    public FrameworkElement CardSelect = null!;
    public TranslateTransform TransSelect = null!;
    public TextBlock LabOperation = null!, LabEmpty = null!, LabEmptyTitle = null!, LabEmptyHint = null!, LabBackupStatus = null!, LabSelect = null!;
    public Func<string> GetSearch = null!;
    public Action<string> SetSearch = null!, SetSearchHint = null!;
    public Func<bool> SearchHasFocus = null!;
    public Action FocusViewport = null!, ScrollHome = null!;
    public Action<TInstance?> BindBackupOwner = null!;
    public Func<bool> CommitBackupSettings = null!;
    public ResourcePageButton BtnOpenFolder = null!, BtnImportFile = null!, BtnDownload = null!, BtnSelectAll = null!, BtnBackupAll = null!, BtnRestore = null!, BtnUp = null!, BtnCancel = null!, BtnHintDownload = null!, BtnHintRestore = null!, BtnRecycleBack = null!, BtnUpdate = null!, BtnBackupRestore = null!, BtnExport = null!, BtnDelete = null!, BtnDeselect = null!;
    public ResourcePageFilter FilterAll = null!, FilterUpdates = null!, FilterFiles = null!, FilterFolders = null!;
    public IEnumerable<ResourcePageFilter> Filters = null!;
    public Func<ResourcePageRowActions<TEntry, TOnline>, IResourcePageRow<TEntry, TOnline>> CreateRow = null!;
    public Func<Action<IResourcePageThumbnail<TEntry>>, Action<TEntry, int>, IResourcePageThumbnail<TEntry>> CreateThumbnail = null!;
    public Func<Action, Func<bool>, IResourcePageBackupGroup<TInstance>> CreateBackupGroup = null!;
    public Func<Panel, IResourcePageCard> CreateBackupCard = null!;
    public Func<int, int, bool, int> RefreshSelectionBar = null!;
    public Action StopSelectionBar = null!;
    public string SaveLogo = null!, RestoreLogo = null!;
}

public sealed class ResourcePageHost<TInstance, TEntry, TOnline>
    where TInstance : class where TEntry : InstanceResourceEntryData where TOnline : class {
    public Func<TInstance?> CurrentInstance = null!;
    public Func<TInstance, string> GameFolder = null!, VersionPath = null!, GameVersion = null!;
    public Func<TInstance, bool> AllowsInstance = null!, IsLocked = null!, NeedsSchematicLoader = null!, QuickPlaySupported = null!;
    public Func<bool> HasRunningGame = null!, ModpackEnabled = null!, IsCurrentPage = null!, AnimationEnabled = null!, BackupBusy = null!;
    public Func<string, string, bool> SamePath = null!;
    public Func<string, string> RemoveExtendedPrefix = null!;
    public Action<Action> Dispatch = null!;
    public Action<Action, string> Background = null!;
    public Action<Exception, string> Warn = null!, Error = null!;
    public Action<string, bool> Hint = null!; // true = success/green, false = error/red
    public Func<string, string, string, string, bool, int> Message = null!;
    // folderRoot!=null selects the original ValidateFolderName; otherwise strictName chooses null/length validation.
    public Func<string, string, string, string?, bool, string?> Input = null!;
    public Func<string, bool, string[], string[]> SelectFiles = null!;
    public Func<string, string, string, string?> SaveFile = null!;
    public Func<string, bool, string[]> SelectFolder = null!;
    public Action<string> OpenExplorer = null!, OpenWebsite = null!, CreateDirectory = null!;
    public Action<ProcessStartInfo> OpenProcess = null!;
    public Action<int, TInstance, string> Navigate = null!; // 0=modify, 1=mod, 2=resource pack, 3=shader
    public Action<TInstance, string> QuickPlay = null!;
    public Func<string, string> DescribeWorld = null!;
    public Func<string, string, int, CancellationToken, List<TEntry>> Scan = null!;
    public Func<string, string, int, List<TEntry>> RecycleList = null!;
    public Action<string, string, IEnumerable<string>, CancellationToken> RestoreRecycle = null!, PurgeRecycle = null!;
    public Func<int, bool> UpdatesSupported = null!;
    public Func<string, IEnumerable<TEntry>, int, string, CancellationToken, List<TOnline>> CheckOnline = null!;
    public Action<string, IEnumerable<TOnline>, string, int, string, CancellationToken> InstallOnline = null!;
    public Func<TOnline, TEntry> OnlineEntry = null!;
    public Func<TOnline, bool> HasUpdate = null!;
    public Func<TOnline, string?> ProjectName = null!, Website = null!, Status = null!;
    public Func<TOnline, IEnumerable<string>> Tags = null!;
    public Func<string, List<JObject>> BackupPoints = null!;
    public Action<TInstance, IEnumerable<string>?, string?> StartBackup = null!;
    public Action<ResourcePageController<TInstance, TEntry, TOnline>.Request> StartLoader = null!;
    public Action CancelLoader = null!;
    public Action? ClearScanCache;
    public Func<bool> LoaderFailed = null!;
    public Action<bool, Action, KeyEventHandler> BindExternalEvents = null!;
}
