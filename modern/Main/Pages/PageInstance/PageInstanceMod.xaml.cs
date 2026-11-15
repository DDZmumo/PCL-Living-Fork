using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Mods;
using NativePage = PCLLF.Presentation.Mods.LegacyModPage<PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion, PCL.MyLocalModItem, PCL.ModMinecraft.McInstance, PCL.ModNet.NetFile, PCL.MyIconButton, PCL.ModDependencyImpactView.Item>;

namespace PCL
{
    public partial class PageInstanceMod : IRefreshable
    {
        public ConcurrentDictionary<string, MyLocalModItem> ModItems;
        public List<string> SelectedMods;
        public static List<string> UpdatingInstanceModFolders = new List<string>();
        private readonly NativePage Native;
        public PageInstanceMod()
        {
            __LfAttachOwnHandles();
            this.ModItems = new ConcurrentDictionary<string, MyLocalModItem>();
            this.SelectedMods = new List<string>();
            Native = new NativePage(CreateView(), LocalModPageRuntime.CreateHost(this)) { SortOrder = LocalResourceSortOrder.AddedNewest };
            this.InitializeComponent();
            BtnFilterDuplicate.Check += (sender, user) => ChangeFilter((MyRadioButton)sender,user);
            LocalResourceUi.InitializeSortButton(this.BtnSort, ToggleSort, this.PanSortHeader, this.PanFilter);
            LocalResourceUi.UpdateSortButton(this.BtnSort, (int)Native.SortOrder);
        }

        private void SortClick(object sender, ModBase.RouteEventArgs e) => ToggleSort();
        private void ToggleSort()
        {
            if (!this.BtnSort.IsEnabled) return;
            Native.SortChanged(Native.SortOrder == LocalResourceSortOrder.AddedNewest ? 2 : 1);
            LocalResourceUi.UpdateSortButton(this.BtnSort, (int)Native.SortOrder);
        }

        private LegacyModPageView CreateView()
        {
            return new LegacyModPageView()
            {
                Root = new Func<FrameworkElement>(() => this),
                Back = new Func<FrameworkElement>(() => this.PanBack),
                Empty = new Func<FrameworkElement>(() => this.PanEmpty),
                ListBack = new Func<FrameworkElement>(() => this.PanListBack),
                List = new Func<Panel>(() => this.PanList),
                Search = new Func<string>(() => this.SearchBox.Text),
                SetSearch = new Action<string>(Value => this.SearchBox.Text = Value),
                SetSelectionText = new Action<string>(Value => this.LabSelect.Text = Value),
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                FocusBack = new Action(() => this.PanBack.Focus()),
                Filters = ReadFilterViews,
                EnableDisable = new Action<bool>(Value => this.BtnSelectDisable.IsEnabled = Value),
                EnableEnable = new Action<bool>(Value => this.BtnSelectEnable.IsEnabled = Value),
                EnableUpdate = new Action<bool>(Value => this.BtnSelectUpdate.IsEnabled = Value),
                EnableIndex = Value => this.BtnManageIndex.IsEnabled = Value, IndexText = Value => this.BtnManageIndex.Text = Value,
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
                new ResourcePageFilter(this.BtnFilterAll, 0, () => this.BtnFilterAll.Checked, Value => this.BtnFilterAll.Checked = Value, Value => this.BtnFilterAll.Text = Value),
                new ResourcePageFilter(this.BtnFilterEnabled, 1, () => this.BtnFilterEnabled.Checked, Value => this.BtnFilterEnabled.Checked = Value, Value => this.BtnFilterEnabled.Text = Value),
                new ResourcePageFilter(this.BtnFilterDisabled, 2, () => this.BtnFilterDisabled.Checked, Value => this.BtnFilterDisabled.Checked = Value, Value => this.BtnFilterDisabled.Text = Value),
                new ResourcePageFilter(this.BtnFilterCanUpdate, 3, () => this.BtnFilterCanUpdate.Checked, Value => this.BtnFilterCanUpdate.Checked = Value, Value => this.BtnFilterCanUpdate.Text = Value),
                new ResourcePageFilter(this.BtnFilterDuplicate, 4, () => this.BtnFilterDuplicate.Checked, Value => this.BtnFilterDuplicate.Checked = Value, Value => this.BtnFilterDuplicate.Text = Value)
            };
        }

        private void RefreshNativeSelectionBar(int Count, ref int Previous, bool Animate)
        {
            LocalResourceUi.RefreshSelectionBar(this.CardSelect, this.TransSelect, Count, ref Previous, "Mod Sidebar", Animate);
        }

        internal void InitializeNativeLoader()
        {
            LocalResourceUi.ApplySelectionIcons(this.BtnSelectUpdate, this.BtnSelectDelete, this.BtnSelectCancel);
            this.PageLoaderInit(this.Load, this.PanLoad, this.PanAllBack, (FrameworkElement)null, LocalResourceLoaders.LocalResourceLoader, (_) => LoadUIFromLoaderOutput(), AutoRun: false);
        }

        internal void BindMainKey()
        {
            global::PCL.ModMain.FrmMain.KeyDown += this.FrmMain_KeyDown;
        }

        internal void AdjustFilterMargins()
        {
            foreach (MyRadioButton Button in this.PanFilter.Children)
                Button.LabText.Margin = new Thickness(-2, 0d, 8d, 0d);
        }

        internal MyLocalModItem CreateItem(LocalResourceFile Entry, bool Selected)
        {
            return new MyLocalModItem()
            {
                SnapsToDevicePixels = true,
                Entry = Entry,
                ButtonHandler = McModContent,
                Checked = Selected
            };
        }

        internal void BindRowEvents(MyLocalModItem Item)
        {
            Item.Changed += (Sender, Args) => this.CheckChanged((MyLocalModItem)Sender, Args);
            Item.Click += (Sender, Args) =>
            {
                var Row = (MyLocalModItem)Sender;
                Row.Checked = !Row.Checked;
            };
            Item.MouseRightButtonUp += this.Info_Click;
        }

        public void PageOther_Loaded()
        {
            Native.Loaded();
            foreach (MyLocalModItem item in ModItems.Values) item.Controller.RefreshFavorite();
            Native.RefreshUI();
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

        private void Load_Click(object sender, MouseButtonEventArgs e)
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

        private void McModContent(MyLocalModItem sender, EventArgs e)
        {
            Native.ItemContent(sender);
        }

        public void RefreshUI()
        {
            Native.RefreshUI();
        }

        public void RefreshBars()
        {
            Native.RefreshBars();
        }

        private void BtnManageOpen_Click(object sender, EventArgs e)
        {
            Native.OpenFolder();
        }

        private void BtnManageSelectAll_Click(object sender, MouseButtonEventArgs e)
        {
            Native.SelectAll();
        }

        private void BtnManageInstall_Click(object sender, MouseButtonEventArgs e)
        {
            Native.InstallClick();
        }

        public static bool InstallMods(IEnumerable<string> FilePathList)
        {
            return NativePage.Install(LocalModPageRuntime.CreateHost(null), FilePathList, false);
        }

        private void BtnManageDownload_Click(object sender, MouseButtonEventArgs e)
        {
            Native.Download();
        }

        public void CheckChanged(MyLocalModItem sender, ModBase.RouteEventArgs e)
        {
            Native.CheckChanged(sender);
        }

        private void UnselectedAllWithAnimation()
        {
            Native.UnselectAnimated();
        }

        private void FrmMain_KeyDown(object sender, KeyEventArgs e)
        {
            Native.MainKey(e);
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


        private void ChangeFilter(MyRadioButton sender, bool raiseByMouse)
        {
            Native.FilterChanged(Conversions.ToInteger(sender.Tag));
        }

        private void BtnSelectED_Click(MyIconTextButton sender, ModBase.RouteEventArgs e)
        {
            Native.SelectED(!sender.Equals(this.BtnSelectDisable));
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

        public void Open_Click(MyIconButton sender, EventArgs e)
        {
            Native.OpenSender(sender);
        }

        public void Delete_Click(MyIconButton sender, EventArgs e)
        {
            Native.DeleteItem((MyLocalModItem)sender.Tag);
        }

        public void ED_Click(MyIconButton sender, EventArgs e)
        {
            Native.ToggleItem((MyLocalModItem)sender.Tag);
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
