using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCLCS;
using RowCore = PCLCS.ResourceItemController<PCL.ResourceProject, PCL.ResourceSearcher.SearchResult, PCL.ResourceSearcher.SearchRequest, PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.ModAnimation.AniData>;

namespace PCL
{
    public partial class MyResourceItem
    {
        public int Uuid;
        private RowCore Controller;
        private RowCore Service
        {
            get
            {
                if (Controller is null)
                    Controller = new RowCore(CreateHost(), CreateAnimations());
                return Controller;
            }
        }

        public MyResourceItem()
        {
            __LfAttachOwnHandles();
            this.Uuid = ModBase.GetUuid();
            this.InitializeComponent();
            ResourceFavorites.Observe(this, (_, _) => RefreshFavorite());
        }

        private ResourceProject FavoriteProject;
        internal ResourceTypes? ResourceTypeContext;
        internal void BindFavorite(ResourceProject project) { FavoriteProject = project; RefreshFavorite(); }
        internal void RefreshFavorite() {
            BtnFavorite.Visibility = ResourceFavorites.CanFavorite(FavoriteProject) ? Visibility.Visible : Visibility.Collapsed;
            bool saved = ResourceFavorites.IsFavorite(FavoriteProject);
            BtnFavorite.Logo = saved ? ModBase.Logo.IconButtonLikeFill : ModBase.Logo.IconButtonLikeLine;
            BtnFavorite.ToolTip = saved ? "取消收藏此项目" : "收藏此项目";
            BtnFavorite.IsEnabled = !ResourceFavorites.IsBusy(FavoriteProject);
        }
        // Let MyIconButton receive its native mouse event, but prevent the parent
        // resource row from interpreting the same gesture as an open-detail click.
        private void Favorite_MouseButton(object sender, MouseButtonEventArgs e) {
            Service.MouseLeave();
        }
        private void BtnFavorite_Click(object sender, EventArgs e) {
            ResourceFavorites.Toggle(FavoriteProject);
            RefreshFavorite();
        }

        private static (ResourceSearcher.SearchRequest Search, ModMinecraft.McInstance Instance) ReadCurrentDownloadContext() {
            var page = ((dynamic)ModMain.FrmDownloadLeft.PageGet(ModMain.FrmMain.PageCurrentSub)).FindName("Content") as PageResource;
            return (page?.Loader?.Input, page?.DownloadTargetInstance);
        }
        private ResourceItemHost<ResourceProject, ResourceSearcher.SearchRequest, ModMinecraft.McInstance> CreateHost()
        {
            return new ResourceItemHost<ResourceProject, ResourceSearcher.SearchRequest, ModMinecraft.McInstance>()
            {
                View = new Func<ResourceItemView>(() => new ResourceItemView() { Root = this, Title = LabTitle, RawTitle = LabTitleRaw, Info = LabInfo, InfoToolTip = ToolTipInfo, Tags = PanTags }),
                Uuid = new Func<int>(() => Uuid),
                CurrentPage = new Func<int>(() => (int)ModMain.FrmMain.PageCurrent.Page),
                DownloadPage = (int)FormMain.PageType.Download,
                ResourceDetailPage = (int)FormMain.PageType.ResourceDetail,
                InstanceSetupPage = (int)FormMain.PageType.InstanceSetup,
                DetailAvailable = new Func<bool>(() => ModMain.FrmDownloadResourceDetail is not null),
                DownloadAvailable = new Func<bool>(() => ModMain.FrmDownloadLeft is not null),
                ExpandedCards = new Func<IEnumerable<(string Title, bool IsSwapped)>>(() => ModMain.FrmDownloadResourceDetail.PanResults.Children.Cast<MyCard>().Select(Card => (Card.Title, Card.IsSwapped))),
                CurrentSearch = ReadCurrentSearch, CurrentDownloadContext = ReadCurrentDownloadContext,
                CurrentArguments = new Func<object[]>(() => ModMain.FrmMain.PageCurrent.Additional as object[]),
                CurrentInstance = new Func<ModMinecraft.McInstance>(() => PageInstanceLeft.Instance),
                OpenDetail = new Action<object[]>(Arguments => {
                    if (ModMain.FrmMain.PageCurrent.Page == FormMain.PageType.Favorites) {
                        ResourceTypes type = (ResourceTypes)Arguments[4];
                        if (ResourceTypeContext.HasValue && ((ResourceProject)Arguments[0]).Types.HasFlag(ResourceTypeContext.Value))
                            Arguments[4] = ResourceTypeContext.Value;
                        else if (type == ResourceTypes.ModOrDataPack) Arguments[4] = ResourceTypes.Mod;
                    }
                    ModMain.FrmMain.PageChange(new FormMain.PageStackData() { Page = FormMain.PageType.ResourceDetail, Additional = Arguments });
                }),
                RaiseClick = RaiseNativeClick,
                TextTrimmed = ModBase.IsTextTrimmed,
                Brush = new Func<string, Brush>(Color => new ModBase.MyColor(Color)),
                PixelSize = new Func<double, double>(Value => ModBase.GetWPFSize(Value))
            };
        }

        private ResourceSearcher.SearchRequest ReadCurrentSearch()
        {
            PageResource ResourcePage = ((dynamic)ModMain.FrmDownloadLeft.PageGet(ModMain.FrmMain.PageCurrentSub)).FindName("Content") as PageResource;
            return ResourcePage?.Loader?.Input;
        }

        private void RaiseNativeClick(object Sender, MouseButtonEventArgs Args)
        {
            Click?.Invoke(Sender, Args);
        }

        public string Title
        {
            get
            {
                return Service.Title;
            }

            set
            {
                Service.Title = value;
            }
        }

        public string SubTitle
        {
            get
            {
                return Service.SubTitle;
            }

            set
            {
                Service.SubTitle = value;
            }
        }

        public string Description
        {
            get
            {
                return Service.Description;
            }

            set
            {
                Service.Description = value;
            }
        }

        private void LabInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            Service.ShowDescriptionToolTip();
        }

        public List<string> Tags
        {
            set
            {
                Service.SetTags(value);
            }
        }

        public event ClickEventHandler Click;
        public delegate void ClickEventHandler(object sender, MouseButtonEventArgs e);
        public void PerformClick() => Service.Activate(this,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
        private void Button_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (BtnFavorite.IsMouseOver) { Service.MouseLeave(); return; }
            Service.MouseUp(sender, e);
        }

        private void MyResourceItem_Click(MyResourceItem sender, EventArgs e)
        {
            Service.Click(sender);
        }

        internal static object[] BuildDetailArguments(ResourceProject Project, FormMain.PageType Page, ResourceSearcher.SearchRequest Search, object[] Existing, ModMinecraft.McInstance Instance)
        {
            return RowCore.BuildDetailArguments(Project, (int)Page, Search, Existing, Instance, (int)FormMain.PageType.Download, (int)FormMain.PageType.ResourceDetail, (int)FormMain.PageType.InstanceSetup);
        }

        private void Button_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (BtnFavorite.IsMouseOver) return;
            Service.MouseDown();
        }

        private void Button_MouseLeave(object sender, object e)
        {
            Service.MouseLeave();
        }

        public Border RectBack
        {
            get
            {
                return Service.RectBack;
            }
        }

        public bool CanInteraction
        {
            get
            {
                return Service.CanInteraction;
            }

            set
            {
                Service.CanInteraction = value;
            }
        }

        public void RefreshColor(object sender, EventArgs e)
        {
            Service.RefreshColor();
        }

        private static DownloadPageAnimationHost<ModAnimation.AniData> CreateAnimations()
        {
            return new DownloadPageAnimationHost<ModAnimation.AniData>()
            {
                Disabled = new Func<int>(() => ModAnimation.AniControlEnabled),
                SetDisabled = new Action<int>(Value => ModAnimation.AniControlEnabled = Value),
                Opacity = new Func<FrameworkElement, double, double, int, DownloadPageEase, ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => ModAnimation.AaOpacity(Target, Delta, (int)Math.Round(Duration), Delay, ReadEase(Ease))),
                TranslateX = new Func<FrameworkElement, double, double, int, DownloadPageEase, ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => ModAnimation.AaTranslateX(Target, Delta, (int)Math.Round(Duration), Delay, ReadEase(Ease))),
                TranslateY = new Func<FrameworkElement, double, double, int, DownloadPageEase, ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease) => ModAnimation.AaTranslateY(Target, Delta, (int)Math.Round(Duration), Delay, ReadEase(Ease))),
                Scale = new Func<FrameworkElement, double, double, int, DownloadPageEase, bool, ModAnimation.AniData>((Target, Delta, Duration, Delay, Ease, After) => ModAnimation.AaScaleTransform(Target, Delta, (int)Math.Round(Duration), Delay, ReadEase(Ease), After)),
                Color = new Func<FrameworkElement, DependencyProperty, string, int, int, DownloadPageEase, ModAnimation.AniData>((Target, Property, Color, Duration, Delay, Ease) => ModAnimation.AaColor(Target, Property, Color, Duration, Delay, ReadEase(Ease))),
                Code = new Func<Action, int, bool, ModAnimation.AniData>((Work, Delay, After) => ModAnimation.AaCode(() => Work(), Delay, After)),
                Start = new Action<IEnumerable<ModAnimation.AniData>, string, bool>((Plan, Name, Refresh) => ModAnimation.AniStart(Plan.ToList(), Name, Refresh)),
                Stop = ModAnimation.AniStop
            };
        }

        private static ModAnimation.AniEase ReadEase(DownloadPageEase Value)
        {
            switch (Value)
            {
                case DownloadPageEase.OutFluent:
                {
                    return new ModAnimation.AniEaseOutFluent();
                }

                case DownloadPageEase.OutFluentExtraStrong:
                {
                    return new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong);
                }

                default:
                {
                    return null;
                }
            }
        }
    }
}
