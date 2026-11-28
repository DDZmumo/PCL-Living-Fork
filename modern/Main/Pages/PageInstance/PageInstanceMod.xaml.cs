using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.CompilerServices;
using System.Linq;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Mods;
using NativePage = PCLLF.Presentation.Mods.LegacyModPage<PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion, PCL.GlassLocalModRow, PCL.ModMinecraft.McInstance, PCL.ModNet.NetFile, System.Windows.Controls.Button, PCL.ModDependencyImpactView.Item>;

namespace PCL
{
    public partial class PageInstanceMod : IRefreshable
    {
        public ConcurrentDictionary<string, GlassLocalModRow> ModItems;
        public List<string> SelectedMods;
        public static List<string> UpdatingInstanceModFolders = new List<string>();
        private readonly NativePage Native;
        private ScrollViewer _rowsScroll;
        private GlassLocalModRow _selectionTail;
        private FormMain _keyOwner;
        private string _displayedFolder;
        private double _previousListOffset;
        private string _loadingCaption = "正在读取模组…";
        private bool _showLoadingProgress;
        private readonly DispatcherTimer _loadingProgress = new() { Interval = TimeSpan.FromMilliseconds(160) };
        private int _rangeStart = -1;
        private bool _rangeSelected;

        private readonly Button BtnSelectUpdate, BtnSelectEnable, BtnSelectDisable, BtnSelectDelete, BtnSelectCancel;

        public PageInstanceMod()
        {
            this.ModItems = new ConcurrentDictionary<string, GlassLocalModRow>();
            this.SelectedMods = new List<string>();
            Native = new NativePage(CreateView(), LocalModPageRuntime.CreateHost(this)) { SortOrder = LocalResourceSortOrder.AddedNewest };
            InitializeComponent();
            CardSelect.SetBackdropSource(PanAllBack);
            BtnSelectUpdate = CardSelect.AddCommand(new Button { Content = "更新", Name = "BtnSelectUpdate" }, GlassSelectionDock.UpdateIcon);
            BtnSelectEnable = CardSelect.AddCommand(new Button { Content = "启用", Name = "BtnSelectEnable" }, GlassSelectionDock.EnableIcon);
            BtnSelectDisable = CardSelect.AddCommand(new Button { Content = "禁用", Name = "BtnSelectDisable" }, GlassSelectionDock.DisableIcon);
            BtnSelectDelete = CardSelect.AddCommand(new Button { Content = "删除", Name = "BtnSelectDelete" }, GlassSelectionDock.DeleteIcon, danger: true);
            BtnSelectCancel = CardSelect.AddCommand(new Button { Content = "取消选择", Name = "BtnSelectCancel" }, GlassSelectionDock.CancelIcon, dismiss: true);
            AttachPlaceholder(SearchBox, "搜索 Mod 名称 / 描述 / 标签");
            // Available updates get their own call to action at the top of the list, not just a filter count.
            _updateBanner = new GlassUpdateBanner(UpdateAllMods, () => BtnFilterCanUpdate.IsChecked = true);
            PanMain.Children.Add(_updateBanner);
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ContentControl.ContentProperty, typeof(RadioButton))
                .AddValueChanged(BtnFilterCanUpdate, (_, _) => RefreshUpdateBanner());
            BtnFilterCanUpdate.Checked += (_, _) => RefreshUpdateBanner();
            BtnFilterCanUpdate.Unchecked += (_, _) => RefreshUpdateBanner();
            _loadingProgress.Tick += (_, _) => RefreshLoadingCaption();
            UpdateSortLabel();
            BtnSort.Click += (_, _) => ToggleSort();
            BtnManageOpen.Click += BtnManageOpen_Click;
            BtnHintOpen.Click += BtnManageOpen_Click;
            BtnManageInstall.Click += BtnManageInstall_Click;
            BtnHintInstall.Click += BtnManageInstall_Click;
            BtnManageDownload.Click += BtnManageDownload_Click;
            BtnHintDownload.Click += BtnManageDownload_Click;
            BtnManageSelectAll.Click += BtnManageSelectAll_Click;
            BtnManageIndex.Click += (_, _) => RebuildDependencyIndex();
            BtnSelectEnable.Click += (_, _) => Native.SelectED(true);
            BtnSelectDisable.Click += (_, _) => Native.SelectED(false);
            BtnSelectUpdate.Click += (_, _) => BtnSelectUpdate_Click();
            BtnSelectDelete.Click += (_, _) => BtnSelectDelete_Click();
            BtnSelectCancel.Click += (_, _) => BtnSelectCancel_Click();
            BtnRetry.Click += (_, _) => Native.LoadClick();
            SearchBox.TextChanged += (_, _) => SearchRun();
            SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
            foreach (RadioButton filter in PanFilter.Children)
                filter.Checked += (_, _) => ChangeFilter(filter, true);
            CardSelect.SizeChanged += (_, _) => RefreshSelectionTail();
            PanList.SizeChanged += (_, _) => RefreshSelectionTail();
            Loaded += (_, _) => PageOther_Loaded();
            Unloaded += (_, _) => LeavePage();
            PreviewMouseLeftButtonUp += (_, _) => _rangeStart = -1;
        }

        private void ToggleSort()
        {
            if (!this.BtnSort.IsEnabled) return;
            LocalResourceUi.ShowSortMenu(BtnSort, SortMode(Native.SortOrder), false, mode =>
            {
                Native.SortOrder = mode switch
                {
                    2 => LocalResourceSortOrder.AddedOldest,
                    5 => LocalResourceSortOrder.NameAscending,
                    6 => LocalResourceSortOrder.NameDescending,
                    _ => LocalResourceSortOrder.AddedNewest
                };
                UpdateSortLabel();
            });
        }

        // The shared sort-button modes (see LocalResourceUi) for the mod page's order.
        private static int SortMode(LocalResourceSortOrder order) => order switch
        {
            LocalResourceSortOrder.AddedOldest => 2,
            LocalResourceSortOrder.NameAscending => 5,
            LocalResourceSortOrder.NameDescending => 6,
            _ => 1
        };

        private void UpdateSortLabel() => LocalResourceUi.SetSortButton(BtnSort, SortMode(Native.SortOrder));

        private LegacyModPageView CreateView()
        {
            return new LegacyModPageView()
            {
                Root = new Func<FrameworkElement>(() => this),
                Back = new Func<FrameworkElement>(() => this.PanBack),
                Empty = new Func<FrameworkElement>(() => this.PanEmpty),
                List = () => PanList,
                Search = new Func<string>(() => this.SearchBox.Text),
                SetSearch = new Action<string>(Value => this.SearchBox.Text = Value),
                SetSelectionText = new Action<string>(Value => this.CardSelect.Status.Text = Value),
                ScrollHome = new Action(() => _rowsScroll?.ScrollToTop()),
                FocusBack = new Action(() => this.PanBack.Focus()),
                Filters = ReadFilterViews,
                EnableDisable = new Action<bool>(Value => this.BtnSelectDisable.IsEnabled = Value),
                EnableEnable = new Action<bool>(Value => this.BtnSelectEnable.IsEnabled = Value),
                EnableUpdate = new Action<bool>(Value => this.BtnSelectUpdate.IsEnabled = Value),
                EnableIndex = Value => this.BtnManageIndex.IsEnabled = Value, IndexText = Value => this.BtnManageIndex.Content = Value,
                SetEmptyTitle = Value => this.LabEmptyTitle.Text = Value, SetEmptyHint = Value => this.LabEmptyHint.Text = Value,
                SelectionBar = RefreshNativeSelectionBar
            };
        }

        internal void ApplyLoaderWarnings() => Native.ApplyLoaderWarnings();
        private void RebuildDependencyIndex() => Native.RebuildDependencyIndex();
        private ResourcePageFilter[] ReadFilterViews()
        {
            return new[]
            {
                new ResourcePageFilter(this.BtnFilterAll, 0, () => this.BtnFilterAll.IsChecked == true, Value => this.BtnFilterAll.IsChecked = Value, Value => this.BtnFilterAll.Content = Value),
                new ResourcePageFilter(this.BtnFilterEnabled, 1, () => this.BtnFilterEnabled.IsChecked == true, Value => this.BtnFilterEnabled.IsChecked = Value, Value => this.BtnFilterEnabled.Content = Value),
                new ResourcePageFilter(this.BtnFilterDisabled, 2, () => this.BtnFilterDisabled.IsChecked == true, Value => this.BtnFilterDisabled.IsChecked = Value, Value => this.BtnFilterDisabled.Content = Value),
                new ResourcePageFilter(this.BtnFilterCanUpdate, 3, () => this.BtnFilterCanUpdate.IsChecked == true, Value => this.BtnFilterCanUpdate.IsChecked = Value, Value => this.BtnFilterCanUpdate.Content = Value),
                new ResourcePageFilter(this.BtnFilterDuplicate, 4, () => this.BtnFilterDuplicate.IsChecked == true, Value => this.BtnFilterDuplicate.IsChecked = Value, Value => this.BtnFilterDuplicate.Content = Value)
            };
        }

        private void RefreshNativeSelectionBar(int Count, ref int Previous, bool Animate)
        {
            CardSelect.ShowSelection(Count, Animate);
            RefreshSelectionTail();
            Previous = Count;
        }

        private void RefreshSelectionTail()
        {
            // Keep the card and its virtualized viewport in place. Scroll past the dock at the final row.
            var last = CardSelect.IsHitTestVisible && PanList.Items.Count > 0 ? PanList.Items[^1] as GlassLocalModRow : null;
            if (!ReferenceEquals(_selectionTail, last)) {
                if (_selectionTail is not null) _selectionTail.Margin = new Thickness();
                _selectionTail = last;
            }
            if (last is not null && _rowsScroll is { } scroll)
                last.Margin = new Thickness(0, 0, 0, CardSelect.ScrollClearance(scroll));
        }

        internal void InitializeNativeLoader()
        {
            PanList.ApplyTemplate();
            var scroll = (ScrollViewer)PanList.Template.FindName("RowsScroll", PanList);
            if (!ReferenceEquals(_rowsScroll, scroll)) {
                _rowsScroll = scroll;
                AttachScroll(scroll, fadeEdges: false);
                RefreshSelectionTail();
            }
        }

        internal void BindMainKey()
        {
            if (_keyOwner is not null || ModMain.FrmMain is not { } window) return;
            _keyOwner = window;
            window.KeyDown += FrmMain_KeyDown;
        }

        internal void AdjustFilterMargins() { }

        internal GlassLocalModRow CreateItem(LocalResourceFile entry, bool selected) => new(this, entry, selected);

        internal void BindRowEvents(GlassLocalModRow item) => item.SelectionChanged += row => Native.CheckChanged(row);
        internal void CreateRowActions(GlassLocalModRow item) => Native.ItemContent(item);

        internal void BeginSelectionRange(GlassLocalModRow item)
        {
            _rangeStart = PanList.Items.IndexOf(item);
            _rangeSelected = !item.Checked;
        }

        internal void ExtendSelectionRange(GlassLocalModRow item)
        {
            if (_rangeStart < 0 || Mouse.LeftButton != MouseButtonState.Pressed) { _rangeStart = -1; return; }
            int end = PanList.Items.IndexOf(item);
            if (end < 0 || _rangeStart >= PanList.Items.Count || end == _rangeStart) return;
            for (int index = Math.Min(end, _rangeStart); index <= Math.Max(end, _rangeStart); index++)
                ((GlassLocalModRow)PanList.Items[index]).Checked = _rangeSelected;
        }

        public void PageOther_Loaded()
        {
            if (LauncherInstanceContext.Instance is null) return;
            bool returnFromDetails = ModMain.FrmMain?.PageLast.Page == FormMain.PageType.ResourceDetail;
            Native.Initialize();
            BindMainKey();
            LocalResourceLoaders.LocalResourceLoader.OnStateChangedUi -= LoaderStateChanged;
            LocalResourceLoaders.LocalResourceLoader.OnStateChangedUi += LoaderStateChanged;
            Native.Loaded();
            UpdateLoaderState(forceRefresh: false);
            foreach (var item in ModItems.Values) item.RefreshFavorite();
            if (returnFromDetails && _previousListOffset > 0) {
                double offset = _previousListOffset;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                    if (IsLoaded) _rowsScroll?.ScrollToVerticalOffset(offset);
                }));
            }
        }

        private void LeavePage()
        {
            _previousListOffset = _rowsScroll?.VerticalOffset ?? 0;
            LocalResourceLoaders.LocalResourceLoader.OnStateChangedUi -= LoaderStateChanged;
            if (_keyOwner is { } window) window.KeyDown -= FrmMain_KeyDown;
            _keyOwner = null;
            Load.IsActive = false;
            _loadingProgress.Stop();
            _rangeStart = -1;
        }

        private void LoaderStateChanged(ModLoader.LoaderBase sender, ModBase.LoadState state, ModBase.LoadState previous)
        {
            if (state == sender.State && IsLoaded) UpdateLoaderState(forceRefresh: true);
        }

        private void UpdateLoaderState(bool forceRefresh)
        {
            if (LauncherInstanceContext.Instance is not { } instance) return;
            var loader = LocalResourceLoaders.LocalResourceLoader;
            string folder = System.IO.Path.Combine(instance.PathIndie, "mods");
            bool belongs = !string.IsNullOrWhiteSpace(loader.Input) && PathUtils.IsSame(loader.Input, folder);
            bool reading = !belongs || loader.State == ModBase.LoadState.Loading;
            bool failed = belongs && loader.State == ModBase.LoadState.Failed;
            PanAllBack.Visibility = reading || failed ? Visibility.Collapsed : Visibility.Visible;
            PanLoad.Visibility = reading || failed ? Visibility.Visible : Visibility.Collapsed;
            Load.IsActive = reading && IsLoaded;
            Load.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
            BtnRetry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            if (reading && IsLoaded) {
                RefreshLoadingCaption();
                _loadingProgress.Start();
            } else {
                _loadingProgress.Stop();
                LabLoad.Text = failed ? "读取模组失败，请重试。" : _loadingCaption;
            }
            if (!belongs || loader.State != ModBase.LoadState.Finished) return;
            if (forceRefresh || !string.Equals(_displayedFolder, folder, StringComparison.OrdinalIgnoreCase)) {
                _displayedFolder = folder;
                LoadUIFromLoaderOutput();
            } else Native.RefreshUI();
        }

        internal void SetLoadingCaption(string caption) {
            _loadingCaption = caption;
            if (Load.IsActive) RefreshLoadingCaption();
        }

        internal void SetLoadingProgress(bool show) {
            _showLoadingProgress = show;
            if (Load.IsActive) RefreshLoadingCaption();
        }

        private void RefreshLoadingCaption() {
            double progress = LocalResourceLoaders.LocalResourceLoader.Progress;
            LabLoad.Text = _showLoadingProgress && double.IsFinite(progress) ? $"{_loadingCaption} {progress:P0}" : _loadingCaption;
        }

        public void ReloadModList(bool ForceReload = false)
        {
            Native.ReloadModList(ForceReload);
        }

        private void RefreshSelf()
        {
            Refresh();
        }

        void IRefreshable.Refresh() => RefreshSelf();
        public static void Refresh()
        {
            NativePage.Refresh(LocalModPageRuntime.CreateHost(null));
        }

        private void LoaderInit()
        {
            Native.Initialize();
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            Native.LoadClick();
        }

        public bool LoaderRun(ModLoader.LoaderFolderRunType Type)
        {
            return Native.RunLoader((int)Type);
        }

        private void LoadUIFromLoaderOutput()
        {
            Native.LoadUI();
        }

        private void McModContent(GlassLocalModRow sender, EventArgs e)
        {
            Native.ItemContent(sender);
        }

        public void RefreshUI()
        {
            Native.RefreshUI();
            RefreshUpdateBanner();
        }

        private GlassUpdateBanner _updateBanner;

        private System.Collections.Generic.List<LocalResourceFile> UpdatableMods() =>
            (global::PCL.LocalResourceLoaders.LocalResourceLoader.Output ?? new System.Collections.Generic.List<LocalResourceFile>())
                .Where(mod => mod.HasUpdate).ToList();

        private void RefreshUpdateBanner()
        {
            if (_updateBanner is null) return;
            bool reviewing = BtnFilterCanUpdate.IsChecked == true;
            _updateBanner.Show(reviewing || LauncherInstanceContext.Instance is null ? 0 : UpdatableMods().Count, " Mod ");
        }

        private void UpdateAllMods()
        {
            var mods = UpdatableMods();
            if (mods.Count == 0) return;
            if (ModMain.MyMsgBox($"要更新全部 {mods.Count} 个可更新的 Mod 吗？", "全部更新", "更新", "取消") != 1) return;
            UpdateMods(mods);
        }

        public void RefreshBars()
        {
            Native.RefreshBars();
        }

        private void BtnManageOpen_Click(object sender, EventArgs e)
        {
            Native.OpenFolder();
        }

        private void BtnManageSelectAll_Click(object sender, RoutedEventArgs e)
        {
            Native.SelectAll();
        }

        private void BtnManageInstall_Click(object sender, RoutedEventArgs e)
        {
            Native.InstallClick();
        }

        public static bool InstallMods(IEnumerable<string> FilePathList)
        {
            return NativePage.Install(LocalModPageRuntime.CreateHost(null), FilePathList, false);
        }

        private void BtnManageDownload_Click(object sender, RoutedEventArgs e)
        {
            Native.Download();
        }

        public void CheckChanged(GlassLocalModRow sender, ModBase.RouteEventArgs e)
        {
            Native.CheckChanged(sender);
        }

        private void UnselectedAllWithAnimation()
        {
            Native.UnselectAnimated();
        }

        private void FrmMain_KeyDown(object sender, KeyEventArgs e)
        {
            if (_keyOwner?.HasOpenGlassDialog != true) Native.MainKey(e);
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Native.SearchKey(e);
        }

        public FilterType Filter
        {
            get
            {
                return (FilterType)(int)Native.Filter;
            }

            set
            {
                Native.Filter = (LegacyModFilter)(int)value;
            }
        }

        public enum FilterType : int
        {
            All = 0,
            Enabled = 1,
            Disabled = 2,
            CanUpdate = 3,
            Duplicate = 4
        }


        private void ChangeFilter(RadioButton sender, bool raiseByMouse)
        {
            Native.FilterChanged(Conversions.ToInteger(sender.Tag));
        }

        private void BtnSelectUpdate_Click()
        {
            Native.UpdateSelected();
        }

        public void UpdateMods(IEnumerable<LocalResourceFile> ModList)
        {
            Native.UpdateMods(ModList, false);
        }

        private void BtnSelectDelete_Click()
        {
            Native.DeleteSelected();
        }

        private void BtnSelectCancel_Click()
        {
            Native.CancelSelection();
        }

        public void Info_Click(object sender, EventArgs e)
        {
            Native.Info(sender);
        }

        public void Open_Click(Button sender, EventArgs e)
        {
            Native.OpenSender(sender);
        }

        public void Delete_Click(Button sender, EventArgs e)
        {
            Native.DeleteItem((GlassLocalModRow)sender.Tag);
        }

        public void ED_Click(Button sender, EventArgs e)
        {
            Native.ToggleItem((GlassLocalModRow)sender.Tag);
        }

        public bool IsSearching
        {
            get
            {
                return Native.IsSearching;
            }
        }

        public void SearchRun()
        {
            Native.SearchRun();
        }
    }
}
