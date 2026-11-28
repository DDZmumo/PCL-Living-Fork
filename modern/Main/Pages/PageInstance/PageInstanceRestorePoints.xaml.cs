// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using PCLCS;
using HistoryController = PCLCS.EnvironmentHistoryPageController<PCL.ModMinecraft.McInstance>;
using HistoryHost = PCLCS.EnvironmentHistoryPageHost<PCL.ModMinecraft.McInstance>;
using HistoryView = PCLCS.EnvironmentHistoryPageView<PCL.ModMinecraft.McInstance>;

namespace PCL
{
    public partial class PageInstanceRestorePoints : IRefreshable
    {
        private readonly Button BtnRestore, BtnDelete, BtnDeselect;
        private readonly HistoryController Controller;
        private readonly ModLoader.LoaderTask<HistoryPointsRequest, HistoryPointsResult> PointsLoader;
        private readonly EnvironmentHistoryUi.ChangedEventHandler HistoryChangedHandler;
        public PageInstanceRestorePoints() : this(null, null)
        {
        }

        internal PageInstanceRestorePoints(ModMinecraft.McInstance owner, Guid? expectedInstanceId = null)
        {
            MmcOwner = owner;
            IsMmcHistory = owner is not null;
            MmcRoot = owner is null ? "" : System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(owner.PathVersion));
            MmcInstanceId = owner is null ? Guid.Empty : CaptureMmcInstanceId(owner, expectedInstanceId);
            HistoryChangedHandler = Refresh;
            this.InitializeComponent();
            CardSelect.SetBackdropSource(PanBack);
            RegisterName("LabSelect", CardSelect.Status);
            BtnRestore = CardSelect.AddNamedCommand(this, "BtnRestore", "恢复", ModBase.Logo.IconButtonRefresh);
            BtnDelete = CardSelect.AddNamedCommand(this, "BtnDelete", "删除", GlassSelectionDock.DeleteIcon, danger: true);
            BtnDeselect = CardSelect.AddNamedCommand(this, "BtnDeselect", "取消选择", GlassSelectionDock.CancelIcon, dismiss: true);
            foreach (var editor in new[] { ComboRetentionMode, ComboHistoryRetentionPolicy })
                if (editor.Parent is Grid row) GlassPageLayout.SizeRowToEditor(row);
            AttachScroll(PanBack);
            AttachPlaceholder(SearchBox, IsMmcHistory ? "搜索环境记录 / 版本 / 时间" : "搜索还原点 / 版本 / 时间");
            Controller = new HistoryController(CreateView(), CreateHost());
            PointsLoader = new ModLoader.LoaderTask<HistoryPointsRequest, HistoryPointsResult>(IsMmcHistory ? "读取 MMC 环境记录" : "读取还原点", Task =>
            {
                HistoryPointsRequest request = Task.Input;
                Task.Output = new HistoryPointsResult(request, IsMmcHistory
                    ? ReadMmcPoints(request, Task.CreateCancellationToken())
                    : EnvironmentHistory.Points(request.Root));
            });
            Init();
            if (IsMmcHistory) InitMmcHistory();
        }

        private HistoryView CreateView()
        {
            return new HistoryView()
            {
                Root = this,
                CardRetention = CardRetention,
                PanIndependent = PanIndependentRetention,
                PreferenceMode = () => ComboRetentionMode.SelectedIndex,
                SetPreferenceMode = value => ComboRetentionMode.SelectedIndex = value,
                PanRetention = PanRetention,
                CardList = CardList,
                RestoreButton = BtnRestore,
                Entries = PanEntries,
                Empty = LabEmpty,
                Select = CardSelect.Status,
                Search = new Func<string>(() => this.SearchBox.Text),
                SetSearch = new Action<string>(Value => this.SearchBox.Text = Value),
                RetentionText = new Func<string>(() => this.TextRetention.Text),
                SetRetentionText = new Action<string>(Value => this.TextRetention.Text = Value),
                SetAllText = new Action<string>(Value => this.FilterAll.Content = Value),
                SetHealthyText = new Action<string>(Value => this.FilterHealthy.Content = Value),
                AutoCleanup = new Func<bool>(() => this.ComboHistoryRetentionPolicy.SelectedIndex == 1),
                SetAutoCleanup = new Action<bool>(Value => this.ComboHistoryRetentionPolicy.SelectedIndex = Value ? 1 : 0),
                AutoCreate = () => CheckHistoryAutoCreate.IsChecked == true,
                SetAutoCreate = value => CheckHistoryAutoCreate.IsChecked = value,
                RetentionHasFocus = new Func<bool>(() => TextRetention.IsKeyboardFocusWithin),
                SearchHasFocus = new Func<bool>(() => this.SearchBox.IsKeyboardFocusWithin),
                CheckAll = new Action(() => this.FilterAll.IsChecked = true),
                ForceRetentionResize = new Action(() => this.CardRetention.InvalidateMeasure()),
                ScrollHome = new Action(() => this.PanBack.ScrollToTop()),
                CreateRow = new Func<IEnvironmentHistoryPageRow>(() => new GlassHistoryRow()),
                AdaptRow = new Func<object, IEnvironmentHistoryPageRow>(Sender => (GlassHistoryRow)Sender),
                CreateIcon = CreateIcon,
                RestoreLogo = ModBase.Logo.IconButtonRefresh,
                DetailsLogo = ModBase.Logo.IconButtonInfo,
                OpenLogo = ModBase.Logo.IconButtonOpen,
                DeleteLogo = ModBase.Logo.IconButtonDelete,
                RefreshSelectionBar = new Func<int, int, bool, int>((Count, Previous, Animate) =>
                {
                    CardSelect.ShowSelection(Count, Animate);
                    return Count;
                })
            };
        }

        private HistoryHost CreateHost()
        {
            if (IsMmcHistory) return CreateMmcHost();
            return new HistoryHost()
            {
                CurrentInstance = new Func<ModMinecraft.McInstance>(() => LauncherInstanceContext.Instance),
                VersionPath = new Func<ModMinecraft.McInstance, string>(Instance => Instance.PathVersion),
                AutoCleanup = new Func<ModMinecraft.McInstance, bool>(Instance => Settings.Get<bool>("VersionEnvironmentHistoryAutoCleanup", Instance: Instance)),
                Retention = new Func<ModMinecraft.McInstance, int>(Instance => Settings.Get<int>("VersionEnvironmentHistoryRetention", Instance: Instance)),
                SetAutoCleanup = new Action<ModMinecraft.McInstance, bool>((Instance, Value) => Settings.Set("VersionEnvironmentHistoryAutoCleanup", Value, Instance: Instance)),
                SetRetention = new Action<ModMinecraft.McInstance, int>((Instance, Value) => Settings.Set("VersionEnvironmentHistoryRetention", Value, Instance: Instance)),
                PreferenceMode = BackupPreferences.HistoryMode,
                SetPreferenceMode = BackupPreferences.SetHistoryMode,
                AutoCreate = instance => Settings.Get<bool>("VersionEnvironmentHistoryAutoEnabled", instance),
                SetAutoCreate = (instance, value) => Settings.Set("VersionEnvironmentHistoryAutoEnabled", value, instance),
                StartLoader = new Action<string>(VersionFolder => PointsLoader.Start(new HistoryPointsRequest(VersionFolder), IsForceRestart: true)),
                CancelLoader = new Action(() => PointsLoader.Cancel()),
                LoaderFailed = new Func<bool>(() => PointsLoader.State == ModBase.LoadState.Failed),
                AnimationEnabled = new Func<bool>(() => ModAnimation.AniControlEnabled == 0),
                BindChanged = BindChanged,
                Start = new Action<ModMinecraft.McInstance, bool, string, IEnumerable<string>>((Instance, Restore, PointId, DeleteIds) => EnvironmentHistoryUi.Start(Instance, Restore, PointId, DeleteIds)),
                HintError = new Action<string>(Message => ModMain.Hint(Message, ModMain.HintType.Red)),
                OpenFolder = new Action<string>(VersionFolder => EnvironmentHistory.OpenFolder(VersionFolder)),
                ShowDetails = new Action<string, string>((Text, Title) => ModMain.MyMsgBox(Text, Title))
            };
        }

        private void BindChanged(bool Attach)
        {
            if (IsMmcHistory)
            {
                BindMmcChanged(Attach);
                return;
            }
            if (Attach)
            {
                EnvironmentHistoryUi.Changed += HistoryChangedHandler;
            }
            else
            {
                EnvironmentHistoryUi.Changed -= HistoryChangedHandler;
            }
        }

        private static EnvironmentHistoryPageIcon CreateIcon(string Logo, double? Scale, string Tip)
        {
            double size = 16 * (Scale ?? 1);
            var image = new System.Windows.Shapes.Path { Data = Geometry.Parse(Logo), Width = size, Height = size, Stretch = Stretch.Uniform };
            image.SetResourceReference(Shape.FillProperty, "GlassPresentationText");
            var button = new Button { Content = image, Style = GlassDialogControls.Style("GlassPresentationRowButton"), ToolTip = Tip, Width = 30, Height = 32, Padding = new Thickness(5) };
            AutomationProperties.SetName(button, Tip);
            return new EnvironmentHistoryPageIcon(button, click => button.Click += (_, _) => click());
        }

        // 同名状态入口仅转发到控制器，原条目与选择集合没有页面副本。
        private List<JObject> Entries
        {
            get
            {
                return Controller.Entries;
            }

            set
            {
                Controller.Entries = value;
            }
        }

        private HashSet<string> SelectedIds
        {
            get
            {
                return Controller.SelectedIds;
            }
        }

        private bool Rendering
        {
            get
            {
                return Controller.Rendering;
            }

            set
            {
                Controller.Rendering = value;
            }
        }

        private int BarCount
        {
            get
            {
                return Controller.BarCount;
            }

            set
            {
                Controller.BarCount = value;
            }
        }

        private bool HealthyOnly
        {
            get
            {
                return Controller.HealthyOnly;
            }

            set
            {
                Controller.HealthyOnly = value;
            }
        }

        private ModMinecraft.McInstance Owner
        {
            get
            {
                return Controller.Owner;
            }

            set
            {
                Controller.Owner = value;
            }
        }

        private string PendingFocusId
        {
            get
            {
                return Controller.PendingFocusId;
            }

            set
            {
                Controller.PendingFocusId = value;
            }
        }

        private bool BindingRetention
        {
            get
            {
                return Controller.BindingRetention;
            }

            set
            {
                Controller.BindingRetention = value;
            }
        }

        private bool RetentionDirty
        {
            get
            {
                return Controller.RetentionDirty;
            }

            set
            {
                Controller.RetentionDirty = value;
            }
        }

        internal void FocusPoint(string Id)
        {
            Controller.FocusPoint(Id);
        }

        private void Init()
        {
            Loaded += (_, _) => PageLoaded();
            Unloaded += (_, _) => { PageUnloaded(); Load.IsActive = false; };
            PreviewKeyDown += SelectionKeys;
            SearchBox.TextChanged += (_, _) => SearchChanged();
            TextRetention.TextChanged += (_, _) => RetentionEdited();
            TextRetention.LostKeyboardFocus += (_, _) => RetentionLostFocus();
            TextRetention.PreviewKeyDown += RetentionKey;
            FilterAll.Checked += (_, _) => FilterChanged(FilterAll, true);
            FilterHealthy.Checked += (_, _) => FilterChanged(FilterHealthy, true);
            CheckHistoryAutoCreate.Checked += (_, _) => HistoryAutoCreateChanged(CheckHistoryAutoCreate, true);
            CheckHistoryAutoCreate.Unchecked += (_, _) => HistoryAutoCreateChanged(CheckHistoryAutoCreate, true);
            BtnSelectAll.Click += (_, _) => SelectAll();
            BtnDeselect.Click += (_, _) => Deselect();
            BtnDelete.Click += (_, _) => DeleteSelected();
            BtnCreate.Click += (_, _) => CreatePoint();
            BtnRestore.Click += (_, _) => RestorePoint();
            BtnFolder.Click += (_, _) => OpenFolder();
            BtnRetry.Click += (_, _) => Retry();
            PointsLoader.PreviewFinish += _ => ModBase.RunInUiWait(LoadedPoints);
            PointsLoader.OnStateChangedUi += (_, state, _) => {
                if (state != PointsLoader.State) return;
                UpdateLoading();
            };
        }

        private void PageLoaded()
        {
            if (IsMmcHistory) RefreshMmcState();
            Controller.PageLoaded();
            UpdateLoading();
        }

        private void PageUnloaded()
        {
            if (IsMmcHistory) InvalidateMmcRead();
            Controller.PageUnloaded();
        }

        public void Refresh()
        {
            if (IsMmcHistory && !RefreshMmcState())
            {
                InvalidateMmcRead();
                Controller.Entries = new List<JObject>();
                Controller.SelectedIds.Clear();
                Controller.RenderList();
                return;
            }
            Controller.Refresh();
            UpdateLoading();
        }

        private void UpdateLoading()
        {
            bool reading = PointsLoader.State == ModBase.LoadState.Loading;
            bool failed = PointsLoader.State == ModBase.LoadState.Failed;
            PanBack.Visibility = reading || failed ? Visibility.Collapsed : Visibility.Visible;
            PanLoad.Visibility = reading || failed ? Visibility.Visible : Visibility.Collapsed;
            Load.IsActive = reading && IsLoaded;
            Load.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
            BtnRetry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            LabLoad.Text = failed ? "读取记录失败，请重试。" : IsMmcHistory ? "正在读取环境记录…" : "正在读取还原点…";
        }

        private void BindRetention()
        {
            Controller.BindRetention();
        }

        private void AutoCleanupChanged(object Sender, bool User)
        {
            Controller?.AutoCleanupChanged(User);
        }
        private void RetentionModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs args) => Controller?.PreferenceModeChanged();
        private void HistoryRetentionPolicyChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs args) => Controller?.AutoCleanupChanged(true);
        private void HistoryAutoCreateChanged(object sender, bool user) => Controller?.AutoCreateChanged(user);

        private void RetentionEdited()
        {
            Controller?.RetentionEdited();
        }

        private void RetentionLostFocus()
        {
            Controller.CommitRetention();
        }

        private void RetentionKey(object Sender, KeyEventArgs e)
        {
            Controller.RetentionKey(Sender, e);
        }

        internal bool CommitRetention()
        {
            return Controller.CommitRetention();
        }

        private void LoadedPoints()
        {
            if (IsMmcHistory && !AcceptMmcPoints(PointsLoader.Output)) return;
            Controller.LoadedPoints(() => PointsLoader.Output.Points);
        }

        private void SearchChanged()
        {
            Controller?.RenderList();
        }

        private void FilterChanged(RadioButton Sender, bool User)
        {
            Controller?.FilterChanged(object.ReferenceEquals(Sender, this.FilterHealthy));
        }

        internal void RenderList()
        {
            Controller.RenderList();
        }

        private List<Button> PointButtons(JObject Point)
        {
            return Controller.PointButtons(Point).Select(Button => (Button)Button.Element).ToList();
        }

        private void SelectionChanged(object Sender, ModBase.RouteEventArgs e)
        {
            Controller.SelectionChanged(Sender);
        }

        private void RefreshSelection()
        {
            Controller.RefreshSelection();
        }

        internal void ChangeAllSelected(bool Value)
        {
            Controller.ChangeAllSelected(Value);
        }

        private void SelectAll()
        {
            Controller.SelectAll();
        }

        private void Deselect()
        {
            Controller.Deselect();
        }

        private void SelectionKeys(object Sender, KeyEventArgs e)
        {
            Controller.SelectionKeys(Sender, e);
        }

        private void DeleteSelected()
        {
            Controller.DeleteSelected();
        }

        private void CreatePoint()
        {
            Controller.CreatePoint();
        }

        private void RestorePoint()
        {
            Controller.RestorePoint();
        }

        private void OpenFolder()
        {
            Controller.OpenFolder();
        }

        private void Retry()
        {
            Controller.Retry();
        }
    }
}
