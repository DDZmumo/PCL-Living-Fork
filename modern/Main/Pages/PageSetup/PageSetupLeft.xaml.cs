using System;
using System.Windows;
using PCLCS;
using PCLLF.Presentation.Controls;

namespace PCL
{
    // Only LF original XAML/type/Handles/model identity adapters; all workflows are C#.
    public partial class PageSetupLeft
    {
        public PCL.FormMain.PageSubType PageID;
        private LegacyAuxiliaryNavigationController _controller;
        internal LegacyAuxiliaryNavigationController Controller
        {
            get
            {
                if (_controller is null)
                {
                    _controller = new LegacyAuxiliaryNavigationController(AuxiliaryPageRuntime.NavigationHost(() => (int)PageID, Value => PageID = (PCL.FormMain.PageSubType)Value), new LegacyAuxiliaryNavigationView() { Launch = new Func<MyListItemView>(() => this.ItemLaunch), UI = new Func<MyListItemView>(() => this.ItemUI), Link = new Func<MyListItemView>(() => this.ItemLink), System = new Func<MyListItemView>(() => this.ItemSystem) }, true);
                }

                return _controller;
            }
        }

        private void PageSetupLeft_Loaded(object sender, RoutedEventArgs e)
        {
            Controller.Loaded();
        }

        private void PageOtherLeft_Unloaded(object sender, RoutedEventArgs e)
        {
            Controller.Unloaded();
        }

        public PageSetupLeft()
        {
            __LfAttachOwnHandles();
            this.InitializeComponent();
            ItemUpdate.Logo = ModBase.Logo.IconButtonRefresh;
            ItemUpdate.Check += (sender, args) => PageCheck((MyListItem)sender, args);
            ItemPersonal.Logo = ModBase.Logo.IconButtonPersonalSettings;
            ItemPersonal.Check += (sender, args) => PageCheck((MyListItem)sender, args);
            Controller.InitializePageId();
        }

        private void PageCheck(PCL.MyListItem sender, EventArgs e)
        {
            Controller.PageCheck(sender);
        }

        public object PageGet(PCL.FormMain.PageSubType ID = (PCL.FormMain.PageSubType)(-1))
        {
            return (PCL.MyPageRight)Controller.PageGet((int)ID);
        }

        public void PageChange(PCL.FormMain.PageSubType ID, bool Force = false)
        {
            Controller.PageChange((int)ID, Force);
        }

        private static void PageChangeRun(PCL.MyPageRight Target)
        {
            LegacyAuxiliaryNavigationController.PageChangeRun(AuxiliaryPageRuntime.NavigationHost(null, null), Target);
        }

        public void Reset(object sender, EventArgs e)
        {
            Controller.Reset(sender);
        }
    }
}
