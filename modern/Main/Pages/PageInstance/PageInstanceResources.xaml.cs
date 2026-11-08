// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using global::System.IO;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using global::System.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using ResourceController = global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>;
using ResourceRequest = global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Request;
using ResourceResult = global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Result;
using ResourceHost = global::PCLCS.ResourcePageHost<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>;
using ResourceView = global::PCLCS.ResourcePageView<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageInstanceResources : global::PCL.IRefreshable
    {
        private readonly global::PCL.InstanceResourceKind Kind;
        private readonly global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo> Controller;
        private readonly global::PCL.ModLoader.LoaderTask<global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Request, global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Result> ResourceLoader;
        private global::PCL.FormMain KeyboardOwner;
        private global::System.Windows.Input.KeyEventHandler KeyboardHandler;
        private global::PCL.WorldBackups.ChangedEventHandler BackupRefreshHandler;
        public PageInstanceResources() : this(global::PCL.InstanceResourceKind.Saves)
        {
        }

        internal PageInstanceResources(global::PCL.InstanceResourceKind Kind)
        {
            __LfAttachOwnHandles();
            this.Kind = Kind;
            this.InitializeComponent();
            global::PCL.LocalResourceUi.ApplySelectionIcons(this.BtnUpdate, this.BtnDelete, this.BtnDeselect);
            this.BtnExport.Logo = global::PCL.ModBase.Logo.IconButtonSave;
            this.BtnBackupRestore.Logo = global::PCL.ModBase.Logo.IconButtonRefresh;
            foreach (global::PCL.MyRadioButton Button in this.PanFilter.Children)
                Button.LabText.Margin = new global::System.Windows.Thickness((global::System.Double)(-(2)), 0d, 8d, 0d);
            var View = this.CreateView();
            var Host = this.CreateHost();
            this.Controller = new global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>((global::System.Int32)Kind, View, Host, new global::PCLCS.InstanceResourceTransactions(global::MeloongCore.PathUtils.RemoveExtendedPrefix));
            LocalResourceUi.InitializeSortButton(this.BtnSort, ToggleSort, this.PanSortHeader, this.PanFilter);
            LocalResourceUi.UpdateSortButton(this.BtnSort, this.Controller.SortMode);
            this.ResourceLoader = new global::PCL.ModLoader.LoaderTask<global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Request, global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Result>("读取游戏资源", this.ReadResources);
            this.PageLoaderInit(this.Load, this.PanLoad, this.PanAllBack, (global::System.Windows.FrameworkElement)null, this.ResourceLoader, this.ResourcesLoaded, AutoRun: false);
        }

        private global::PCLCS.ResourcePageView<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo> CreateView()
        {
            var View = new global::PCLCS.ResourcePageView<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>()
            {
                Root = this,
                Viewport = PanBack,
                PanEmpty = PanEmpty,
                PanOperations = PanOperations,
                PanResourceList = PanResourceList,
                PanFilter = PanFilter,
                SortHeader = PanSortHeader,
                SortControl = BtnSort,
                BackupSettings = BackupSettings,
                PanList = PanList,
                PanWorlds = PanWorlds,
                PanScreenshots = PanScreenshots,
                PanManageButtons = PanManageButtons,
                CardList = new global::PCL.PageInstanceResources.CardAdapter(this.CardList),
                CardSelect = CardSelect,
                TransSelect = TransSelect,
                LabOperation = LabOperation,
                LabEmpty = LabEmpty,
                LabEmptyTitle = LabEmptyTitle,
                LabEmptyHint = LabEmptyHint,
                LabBackupStatus = LabBackupStatus,
                LabSelect = LabSelect,
                GetSearch = new Func<global::System.String>(() => this.SearchBox.Text),
                SetSearch = new Action<global::System.String>(Value => this.SearchBox.Text = Value),
                SetSearchHint = new Action<global::System.String>(Value => this.SearchBox.HintText = Value),
                SearchHasFocus = new Func<global::System.Boolean>(() => this.SearchBox.IsKeyboardFocusWithin),
                FocusViewport = new Action(() => this.PanBack.Focus()),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                BindBackupOwner = new Action<global::PCL.ModMinecraft.McInstance>(Value => this.BackupSettings.BindOwner(Value)),
                CommitBackupSettings = new Func<global::System.Boolean>(() => this.BackupSettings.CommitPending()),
                SaveLogo = global::PCL.ModBase.Logo.IconButtonSave,
                RestoreLogo = global::PCL.ModBase.Logo.IconButtonRefresh
            };
            View.BtnOpenFolder = global::PCL.PageInstanceResources.NativeButton(this.BtnOpenFolder);
            View.BtnImportFile = global::PCL.PageInstanceResources.NativeButton(this.BtnImportFile);
            View.BtnDownload = global::PCL.PageInstanceResources.NativeButton(this.BtnDownload);
            View.BtnSelectAll = global::PCL.PageInstanceResources.NativeButton(this.BtnSelectAll);
            View.BtnBackupAll = global::PCL.PageInstanceResources.NativeButton(this.BtnBackupAll);
            View.BtnRestore = global::PCL.PageInstanceResources.NativeButton(this.BtnRestore);
            View.BtnUp = global::PCL.PageInstanceResources.NativeButton(this.BtnUp);
            View.BtnCancel = global::PCL.PageInstanceResources.NativeButton(this.BtnCancel);
            View.BtnHintDownload = global::PCL.PageInstanceResources.NativeButton(this.BtnHintDownload);
            View.BtnHintRestore = global::PCL.PageInstanceResources.NativeButton(this.BtnHintRestore);
            View.BtnRecycleBack = global::PCL.PageInstanceResources.NativeButton(this.BtnRecycleBack);
            View.BtnUpdate = global::PCL.PageInstanceResources.NativeButton(this.BtnUpdate);
            View.BtnBackupRestore = global::PCL.PageInstanceResources.NativeButton(this.BtnBackupRestore);
            View.BtnExport = global::PCL.PageInstanceResources.NativeButton(this.BtnExport);
            View.BtnDelete = global::PCL.PageInstanceResources.NativeButton(this.BtnDelete);
            View.BtnDeselect = global::PCL.PageInstanceResources.NativeButton(this.BtnDeselect);
            View.FilterAll = global::PCL.PageInstanceResources.NativeFilter(this.BtnFilterAll);
            View.FilterUpdates = global::PCL.PageInstanceResources.NativeFilter(this.BtnFilterUpdates);
            View.FilterFiles = global::PCL.PageInstanceResources.NativeFilter(this.BtnFilterFiles);
            View.FilterFolders = global::PCL.PageInstanceResources.NativeFilter(this.BtnFilterFolders);
            View.Filters = new[]
            {
                View.FilterAll,
                View.FilterUpdates,
                View.FilterFiles,
                View.FilterFolders
            };
            View.CreateRow = new Func<global::PCLCS.ResourcePageRowActions<global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>, global::PCLCS.IResourcePageRow<global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>>(Actions => new global::PCL.PageInstanceResources.RowAdapter(this, Actions));
            View.CreateThumbnail = new Func<global::System.Action<global::PCLCS.IResourcePageThumbnail<global::PCL.InstanceResourceEntry>>, global::System.Action<global::PCL.InstanceResourceEntry, global::System.Int32>, global::PCLCS.IResourcePageThumbnail<global::PCL.InstanceResourceEntry>>((Changed, Clicked) => new global::PCL.PageInstanceResources.ThumbnailAdapter(Changed, Clicked, this.PanBack));
            View.CreateBackupGroup = new Func<global::System.Action, global::System.Func<global::System.Boolean>, global::PCLCS.IResourcePageBackupGroup<global::PCL.ModMinecraft.McInstance>>((Changed, BeforeCreate) => new global::PCL.PageInstanceResources.BackupGroupAdapter(this.CardSelect, Changed, BeforeCreate));
            View.CreateBackupCard = global::PCL.PageInstanceResources.CreateBackupCard;
            View.RefreshSelectionBar = new Func<global::System.Int32, global::System.Int32, global::System.Boolean, global::System.Int32>((Count, Previous, Animate) =>
            {
                global::PCL.LocalResourceUi.RefreshSelectionBar(this.CardSelect, this.TransSelect, Count, ref Previous, "Resource Sidebar " + this.PageUuid, Animate);
                return Previous;
            });
            View.StopSelectionBar = new Action(() => global::PCL.ModAnimation.AniStop("Resource Sidebar " + this.PageUuid));
            return View;
        }

        private static global::PCLCS.ResourcePageButton NativeButton(global::PCL.MyButton Button)
        {
            return new global::PCLCS.ResourcePageButton(Button, () => Button.Text, Value => Button.Text = Value);
        }

        private static global::PCLCS.ResourcePageButton NativeButton(global::PCL.MyTextButton Button)
        {
            return new global::PCLCS.ResourcePageButton(Button, () => Button.Text, Value => Button.Text = Value);
        }

        private static global::PCLCS.ResourcePageButton NativeButton(global::PCL.MyIconTextButton Button)
        {
            return new global::PCLCS.ResourcePageButton(Button, () => Button.Text, Value => Button.Text = Value, Value => Button.Logo = Value);
        }

        private static global::PCLCS.ResourcePageFilter NativeFilter(global::PCL.MyRadioButton Button)
        {
            return new global::PCLCS.ResourcePageFilter(Button, Conversions.ToInteger(Button.Tag), () => Button.Checked, Value => Button.Checked = Value, Value => Button.Text = Value);
        }

        private global::PCLCS.ResourcePageHost<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo> CreateHost()
        {
            global::PCLCS.ResourcePageHost<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo> @init = new global::PCLCS.ResourcePageHost<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>();
            return (@init.CurrentInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance), @init.GameFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathIndie), @init.VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.PathVersion), @init.GameVersion = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Value => Value.Version.VanillaName), @init.AllowsInstance = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.ModModpackMode.AllowsInstance(Value.PathVersion)), @init.IsLocked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.ModMinecraft.McInstanceLock.IsLocked(Value)), @init.NeedsSchematicLoader = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => (Value.Version.ModLoaders == global::PCLCS.ModLoaders.None)), @init.QuickPlaySupported = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Value => global::PCL.QuickPlayWorlds.Supported(Value)), @init.HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft), @init.ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled), @init.IsCurrentPage = new Func<global::System.Boolean>(() => Object.ReferenceEquals((global::PCL.ModMain.FrmMain?.PageRight), this)), @init.AnimationEnabled = new Func<global::System.Boolean>(() => ((global::PCL.ModAnimation.AniControlEnabled) == (0))), @init.BackupBusy = new Func<global::System.Boolean>(() => global::PCL.WorldBackups.IsBusy), @init.SamePath = new Func<global::System.String, global::System.String, global::System.Boolean>((Left, Right) => global::MeloongCore.PathUtils.IsSame(Left, Right)), @init.RemoveExtendedPrefix = global::MeloongCore.PathUtils.RemoveExtendedPrefix, @init.Dispatch = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)), @init.Background = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)), @init.Warn = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Warn(ErrorValue, Message)), @init.Error = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message, global::MeloongCore.LogBehavior.Toast)), @init.Hint = new Action<global::System.String, global::System.Boolean>((Message, Success) => global::PCL.ModMain.Hint(Message, (Success) ? (global::PCL.ModMain.HintType.Green) : (global::PCL.ModMain.HintType.Red))), @init.Message = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Text, Title, First, Second, Warning) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, IsWarn: Warning)), @init.Input = global::PCL.PageInstanceResources.InputResourceName, @init.SelectFiles = new Func<global::System.String, global::System.Boolean, global::System.String[], global::System.String[]>((Title, Multiple, Extensions) => global::MeloongCore.Wpf.Dialogs.SelectFile(Title, Multiple, filter: new[] { (Extensions, "资源文件") }).ToArray()), @init.SaveFile = new Func<global::System.String, global::System.String, global::System.String, global::System.String>((Title, Name, Extension) => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, Name, filter: new[] { (Extension, "资源文件") })), @init.SelectFolder = new Func<global::System.String, global::System.Boolean, global::System.String[]>((Title, Multiple) => global::MeloongCore.Wpf.Dialogs.SelectFolder(Title, Multiple).ToArray()), @init.OpenExplorer = new Action<global::System.String>(Value => global::PCL.ModBase.OpenExplorer(Value)), @init.OpenWebsite = new Action<global::System.String>(Value => global::PCL.ModBase.OpenWebsite(Value)), @init.CreateDirectory = new Action<global::System.String>(Value => global::MeloongCore.DirectoryUtils.Create(Value)), @init.OpenProcess = new Action<global::System.Diagnostics.ProcessStartInfo>(Value => global::System.Diagnostics.Process.Start(Value)), @init.Navigate = global::PCL.PageInstanceResources.NavigateResource, @init.QuickPlay = new Action<global::PCL.ModMinecraft.McInstance, global::System.String>((Value, Name) => global::PCL.QuickPlayWorlds.Start(Value, Name)), @init.DescribeWorld = new Func<global::System.String, global::System.String>(Value => global::PCL.WorldMetadata.Describe(Value)), @init.Scan = new Func<global::System.String, global::System.String, global::System.Int32, global::System.Threading.CancellationToken, global::System.Collections.Generic.List<global::PCL.InstanceResourceEntry>>((Root, Folder, Value, Token) => global::PCL.InstanceResourceStore.Scan(Root, Folder, (global::PCL.InstanceResourceKind)(Value), Token)), @init.RecycleList = new Func<global::System.String, global::System.String, global::System.Int32, global::System.Collections.Generic.List<global::PCL.InstanceResourceEntry>>((Root, Recycle, Value) => global::PCL.ResourceRecycleStore.List(Root, Recycle, (global::PCL.InstanceResourceKind)(Value))), @init.RestoreRecycle = new Action<global::System.String, global::System.String, global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Threading.CancellationToken>((Root, Recycle, Ids, Token) => global::PCL.ResourceRecycleStore.Restore(Root, Recycle, Ids, Token)), @init.PurgeRecycle = new Action<global::System.String, global::System.String, global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Threading.CancellationToken>((Root, Recycle, Ids, Token) => global::PCL.ResourceRecycleStore.Purge(Root, Recycle, Ids, Token)), @init.UpdatesSupported = new Func<global::System.Int32, global::System.Boolean>(Value => global::PCL.InstanceResourceUpdates.Supported((global::PCL.InstanceResourceKind)(Value))), @init.CheckOnline = new Func<global::System.String, global::System.Collections.Generic.IEnumerable<global::PCL.InstanceResourceEntry>, global::System.Int32, global::System.String, global::System.Threading.CancellationToken, global::System.Collections.Generic.List<global::PCL.InstanceResourceOnlineInfo>>((Root, Items, Value, Version, Token) => global::PCL.InstanceResourceUpdates.Check(Root, Items, (global::PCL.InstanceResourceKind)(Value), Version, Token)), @init.InstallOnline = new Action<global::System.String, global::System.Collections.Generic.IEnumerable<global::PCL.InstanceResourceOnlineInfo>, global::System.String, global::System.Int32, global::System.String, global::System.Threading.CancellationToken>((Root, Items, Recycle, Value, Version, Token) => global::PCL.InstanceResourceUpdates.Install(Root, Items, Recycle, (global::PCL.InstanceResourceKind)(Value), Version, Token)), @init.OnlineEntry = new Func<global::PCL.InstanceResourceOnlineInfo, global::PCL.InstanceResourceEntry>(Value => Value.Entry), @init.HasUpdate = new Func<global::PCL.InstanceResourceOnlineInfo, global::System.Boolean>(Value => Value.HasUpdate), @init.ProjectName = new Func<global::PCL.InstanceResourceOnlineInfo, global::System.String>(Value => Value.ProjectName), @init.Website = new Func<global::PCL.InstanceResourceOnlineInfo, global::System.String>(Value => Value.Website), @init.Status = new Func<global::PCL.InstanceResourceOnlineInfo, global::System.String>(Value => Value.Status), @init.Tags = new Func<global::PCL.InstanceResourceOnlineInfo, global::System.Collections.Generic.IEnumerable<global::System.String>>(Value => Value.Tags), @init.BackupPoints = new Func<global::System.String, global::System.Collections.Generic.List<global::Newtonsoft.Json.Linq.JObject>>(Value => global::PCL.WorldBackups.Points(Value)), @init.StartBackup = new Action<global::PCL.ModMinecraft.McInstance, global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.String>((Value, Ids, RestoreId) => global::PCL.WorldBackupsUi.Start(Value, RestoreId: RestoreId, DeleteIds: Ids)), @init.StartLoader = new Action<global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Request>(Request => this.ResourceLoader.Start(Request, IsForceRestart: true)), @init.CancelLoader = new Action(() => this.ResourceLoader.Cancel()), @init.LoaderFailed = new Func<global::System.Boolean>(() => (this.ResourceLoader.State == global::PCL.ModBase.LoadState.Failed)), @init.BindExternalEvents = this.BindExternalEvents, @init).@init;
        }

        private static global::System.String InputResourceName(global::System.String Title, global::System.String Text, global::System.String Initial, global::System.String FolderRoot, global::System.Boolean StrictName)
        {
            if (FolderRoot is not null)
            {
                return global::PCL.ModMain.MyMsgBoxInput(Title, Text, Initial, new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { new global::PCL.ValidateFolderName(FolderRoot) });
            }

            if (StrictName)
            {
                return global::PCL.ModMain.MyMsgBoxInput(Title, Text, Initial, new global::System.Collections.ObjectModel.Collection<global::PCL.Validate>() { new global::PCL.ValidateNullOrWhiteSpace(), new global::PCL.ValidateLength(1, 120) });
            }

            return global::PCL.ModMain.MyMsgBoxInput(Title, Text, Initial);
        }

        private static void NavigateResource(global::System.Int32 Target, global::PCL.ModMinecraft.McInstance Instance, global::System.String Query)
        {
            if (((Target) == (0)))
            {
                global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup, global::PCL.FormMain.PageSubType.InstanceModify);
                return;
            }

            global::PCL.PageResource.TargetInstance = Instance;
            global::PCL.PageResource.TargetName = Query;
            var Page = ((Target) == (1)) ? (global::PCL.FormMain.PageSubType.DownloadMod) : ((((Target) == (2)) ? (global::PCL.FormMain.PageSubType.DownloadResourcePack) : (global::PCL.FormMain.PageSubType.DownloadShader)));
            global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download, Page);
        }

        private void BindExternalEvents(global::System.Boolean Attach, global::System.Action Refresh, global::System.Windows.Input.KeyEventHandler Handler)
        {
            if (Attach)
            {
                if ((this.Kind == global::PCL.InstanceResourceKind.Saves))
                {
                    this.BackupRefreshHandler = new global::PCL.WorldBackups.ChangedEventHandler(() => Refresh());
                    global::PCL.WorldBackups.Changed += this.BackupRefreshHandler;
                }

                if ((this.KeyboardOwner is null && global::PCL.ModMain.FrmMain is not null))
                {
                    this.KeyboardOwner = global::PCL.ModMain.FrmMain;
                    this.KeyboardHandler = Handler;
                    this.KeyboardOwner.KeyDown += this.KeyboardHandler;
                }
            }
            else
            {
                if (((this.Kind == global::PCL.InstanceResourceKind.Saves) && this.BackupRefreshHandler is not null))
                {
                    global::PCL.WorldBackups.Changed -= this.BackupRefreshHandler;
                    this.BackupRefreshHandler = (global::PCL.WorldBackups.ChangedEventHandler)null;
                }

                if (this.KeyboardOwner is not null)
                {
                    this.KeyboardOwner.KeyDown -= this.KeyboardHandler;
                    this.KeyboardOwner = (global::PCL.FormMain)null;
                    this.KeyboardHandler = (global::System.Windows.Input.KeyEventHandler)null;
                }
            }
        }

        private void ReadResources(global::PCL.ModLoader.LoaderTask<global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Request, global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Result> Loader)
        {
            var Request = Loader.Input;
            var Result = this.Controller.Read(Request, (Request.RecycleMode) ? (global::System.Threading.CancellationToken.None) : (Loader.CreateCancellationToken()), () => Loader.IsCanceled);
            if (Result is not null)
                Loader.Output = Result;
        }

        private void ResourcesLoaded(global::PCL.ModLoader.LoaderTask<global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Request, global::PCLCS.ResourcePageController<global::PCL.ModMinecraft.McInstance, global::PCL.InstanceResourceEntry, global::PCL.InstanceResourceOnlineInfo>.Result> Loader)
        {
            this.Controller.Apply(Loader.Output);
        }

        public void RefreshResources()
        {
            this.Controller.Refresh();
        }

        void global::PCL.IRefreshable.Refresh() => this.RefreshResources();
        internal void ShowResources(global::System.String Root, global::System.String Folder, global::System.Collections.Generic.IEnumerable<global::PCL.InstanceResourceEntry> Items)
        {
            this.Controller.ShowResources(Root, Folder, Items);
        }

        internal void ApplyOnlineInfo(global::System.Collections.Generic.IEnumerable<global::PCL.InstanceResourceOnlineInfo> Infos)
        {
            this.Controller.ApplyOnlineInfo(Infos);
        }

        internal void ScreenshotClick(global::PCL.InstanceResourceEntry Entry, global::System.Int32 Count, global::System.Action<global::System.Diagnostics.ProcessStartInfo> Open = null)
        {
            this.Controller.ScreenshotClick(Entry, Count, Open);
        }

        internal void ChangeAllSelected(global::System.Boolean Value)
        {
            this.Controller.ChangeAllSelected(Value);
        }

        private void PageLoaded()
        {
            this.Controller.PageLoaded();
        }

        private void PageUnloaded()
        {
            this.Controller.PageUnloaded();
        }

        private void ClearSelection()
        {
            this.Controller.ClearSelection();
        }

        private void SearchChanged()
        {
            this.Controller?.SearchChanged();
        }

        private void SortClick(object sender, ModBase.RouteEventArgs args) => ToggleSort();
        private void ToggleSort()
        {
            if (this.Controller is null || this.Controller.IsRecycleMode || !this.BtnSort.IsEnabled) return;
            int next = this.Kind == global::PCL.InstanceResourceKind.Saves
                ? (this.Controller.SortMode == 3 ? 4 : 3) : (this.Controller.SortMode == 1 ? 2 : 1);
            this.Controller.SortChanged(next);
            LocalResourceUi.UpdateSortButton(this.BtnSort, this.Controller.SortMode);
        }

        private void FilterChanged(global::PCL.MyRadioButton Sender, global::System.Boolean User)
        {
            this.Controller?.FilterChanged(Conversions.ToInteger(Sender.Tag));
        }

        private void SelectAllClick()
        {
            this.Controller.SelectAllClick();
        }

        private void DeselectClick()
        {
            this.Controller.DeselectClick();
        }

        private void RefreshScreenshotViewport()
        {
            this.Controller?.RefreshScreenshotViewport();
        }

        private void SearchKeyDown(global::System.Object Sender, global::System.Windows.Input.KeyEventArgs e)
        {
            this.Controller.SearchKeyDown(Sender, e);
        }

        private void ResourceKeyDown(global::System.Object Sender, global::System.Windows.Input.KeyEventArgs e)
        {
            this.Controller.ResourceKeyDown(Sender, e);
        }

        private void OpenFolder()
        {
            this.Controller.OpenFolder();
        }

        private void UpClick()
        {
            this.Controller.UpClick();
        }

        private void ImportFiles()
        {
            this.Controller.ImportFiles();
        }

        private void ResourceDragOver(global::System.Object Sender, global::System.Windows.DragEventArgs e)
        {
            this.Controller.ResourceDragOver(Sender, e);
        }

        private void ResourceDrop(global::System.Object Sender, global::System.Windows.DragEventArgs e)
        {
            this.Controller.ResourceDrop(Sender, e);
        }

        private void ExportSelected()
        {
            this.Controller.ExportSelected();
        }

        private void DeleteSelected()
        {
            this.Controller.DeleteSelected();
        }

        private void DownloadNew()
        {
            this.Controller.DownloadNew();
        }

        private void UpdateSelected()
        {
            this.Controller.UpdateSelected();
        }

        private void CancelClick()
        {
            this.Controller.CancelClick();
        }

        private void RetryLoad()
        {
            this.Controller.RetryLoad();
        }

        private void CheckUpdates()
        {
            this.Controller.CheckUpdates();
        }

        private global::PCL.ModMinecraft.McInstance Owner
        {
            get
            {
                return this.Controller.Owner;
            }

            set
            {
                this.Controller.Owner = value;
            }
        }

        private string CurrentRoot
        {
            get
            {
                return this.Controller.CurrentRoot;
            }

            set
            {
                this.Controller.CurrentRoot = value;
            }
        }

        private string CurrentFolder
        {
            get
            {
                return this.Controller.CurrentFolder;
            }

            set
            {
                this.Controller.CurrentFolder = value;
            }
        }

        private string RecycleRoot
        {
            get
            {
                return this.Controller.RecycleRoot;
            }

            set
            {
                this.Controller.RecycleRoot = value;
            }
        }

        private global::System.Collections.Generic.List<global::PCL.InstanceResourceEntry> Entries
        {
            get
            {
                return this.Controller.Entries;
            }

            set
            {
                this.Controller.Entries = value;
            }
        }

        private bool Busy
        {
            get
            {
                return this.Controller.Busy;
            }

            set
            {
                this.Controller.Busy = value;
            }
        }

        private bool Checking
        {
            get
            {
                return this.Controller.Checking;
            }

            set
            {
                this.Controller.Checking = value;
            }
        }

        private global::System.Threading.CancellationTokenSource OnlineCancellation
        {
            get
            {
                return this.Controller.OnlineCancellation;
            }

            set
            {
                this.Controller.OnlineCancellation = value;
            }
        }
    }
}
