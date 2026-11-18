using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Mods;
using NativeItem = PCLLF.Presentation.Mods.LegacyLocalModItem<PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo, PCL.MyIconButton>;
using NativeHost = PCLLF.Presentation.Mods.LegacyLocalModItemHost<PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo, PCL.MyIconButton>;

namespace PCL
{
    public partial class MyLocalModItem
    {
        public int Uuid;
        internal InstanceResourceEntry ResourceEntry;
        private InstanceResourceOnlineInfo ResourceInfo;
        private ResourceProject FavoriteProject => RestorePoint is not null ? null : ResourceEntry is null ? Entry?.Project : ResourceInfo?.Project;
        private ResourceVersion FavoriteVersion => ResourceEntry is null ? Entry?.ProjectVersion : ResourceInfo?.CurrentVersion;
        internal JObject RestorePoint;
        private EnvironmentHistoryPagePointView RestorePointDisplay;
        internal Action ResourceUpdateRequested;
        internal Action ResourceDetailsRequested;
        public Action ResourceFolderRequested;
        internal FrameworkElement SelectionBar;
        public Action<MyLocalModItem, EventArgs> ButtonHandler;
        public FrameworkElement ButtonStack;
        internal readonly NativeItem Controller;
        public MyLocalModItem()
        {
            __LfAttachOwnHandles();
            this.Uuid = ModBase.GetUuid();
            PresentationRuntime.EnsureConfigured();
            Controller = new NativeItem(CreateView(), CreateHost(), ControlServices.Current.Animations);
            this.InitializeComponent();
            ResourceFavorites.Observe(this, (_, _) => Controller.RefreshFavorite());
            LazyLoadBehavior.OnFirstEnterScrollViewerViewport(this, this.FirstEnterScrollViewerViewport);
        }

        private LegacyLocalModItemView CreateView()
        {
            return new LegacyLocalModItemView()
            {
                LoaderWarningBorder = () => this.LoaderWarningBorder, Root = new Func<Grid>(() => this),
                Title = new Func<TextBlock>(() => this.LabTitle),
                Subtitle = new Func<TextBlock>(() => this.LabSubtitle),
                Description = new Func<TextBlock>(() => this.LabInfo),
                Tags = new Func<System.Windows.Controls.Panel>(() => this.PanTags),
                UpdateButton = new Func<FrameworkElement>(() => this.BtnUpdate),
                FavoriteButton = new Func<FrameworkElement>(() => this.BtnFavorite),
                SetFavorite = new Action<bool>(Value => {
                    this.BtnFavorite.Logo = Value ? ModBase.Logo.IconButtonLikeFill : ModBase.Logo.IconButtonLikeLine;
                    this.BtnFavorite.ToolTip = Value ? "已收藏，点击取消收藏" : ResourceEntry is null ? "收藏此模组" : "收藏此资源";
                    System.Windows.Automation.AutomationProperties.SetName(this.BtnFavorite, this.BtnFavorite.ToolTip.ToString());
                    this.BtnFavorite.IsEnabled = !ResourceFavorites.IsBusy(FavoriteProject);
                }),
                ResourceLogoElement = new Func<FrameworkElement>(() => this.ResourceLogo),
                PathLogoElement = new Func<FrameworkElement>(() => this.PathLogo),
                ResourceLogo = new Func<Image>(() => this.ResourceLogo),
                PaddingRight = new Func<ColumnDefinition>(() => this.ColumnPaddingRight),
                TitleColumn = new Func<ColumnDefinition>(() => this.ColumnTitle),
                SubtitleColumn = new Func<ColumnDefinition>(() => this.ColumnSubtitle),
                ExtendColumn = new Func<ColumnDefinition>(() => this.ColumnExtend),
                SetPathLogo = new Action<string>(Value => this.PathLogo.Source = Value),
                SetPathFallback = new Action<string>(Value => this.PathLogo.FallbackSource = Value)
            };
        }

        private NativeHost CreateHost()
        {
            NativeHost @init = new NativeHost();
            return (@init.Uuid = new Func<int>(() => Uuid), @init.NameStyle = new Func<int>(() => Settings.Get<int>("ToolModLocalNameStyle")), @init.Entry = new Func<LocalResourceFile>(() => Entry), @init.SetEntry = new Action<LocalResourceFile>(Value => Entry = Value), @init.Resource = new Func<InstanceResourceEntry>(() => ResourceEntry), @init.SetResource = new Action<InstanceResourceEntry>(Value => ResourceEntry = Value), @init.Point = new Func<JObject>(() => RestorePoint), @init.SetPoint = new Action<JObject>(Value => RestorePoint = Value), @init.PointView = RestorePointView, @init.Icon = new Func<InstanceResourceEntry, ImageSource>(Value => Value.Icon), @init.ImagePath = new Func<string>(() => ModBase.PathImage), @init.Bitmap = new Func<string, ImageSource>(Address => (ImageSource)new MyBitmap(Address)), @init.WpfSize = new Func<double, double>(Value => ModBase.GetWPFSize(Value)), @init.VisibleInForm = new Func<bool>(() => this.IsVisibleInForm()), @init.IsLocalItemDirectlyOver = new Func<bool>(() => Mouse.DirectlyOver is MyLocalModItem), @init.TitleTrimmed = new Func<bool>(() => this.LabTitle.IsTextTrimmed()), @init.SubtitleTrimmed = new Func<bool>(() => this.LabSubtitle.IsTextTrimmed()), @init.RaiseChanged = RaiseChanged, @init.RaiseCheck = RaiseCheck, @init.RaiseClick = RaiseClick, @init.ButtonStack = new Func<FrameworkElement>(() => ButtonStack), @init.SetButtonStack = new Action<FrameworkElement>(Value => ButtonStack = Value), @init.SelectionBar = new Func<FrameworkElement>(() => SelectionBar), @init.DefaultSelectionBar = new Func<FrameworkElement>(() => ModMain.FrmInstanceMod?.CardSelect), @init.HasButtonHandler = new Func<bool>(() => ButtonHandler is not null), @init.InvokeButtonHandler = new Action<object, EventArgs>((Sender, Args) => ButtonHandler((MyLocalModItem)Sender, Args)), @init.ClearButtonHandler = new Action(() => ButtonHandler = null), @init.ResourceDetails = new Action(() => ResourceDetailsRequested?.Invoke()), @init.ResourceUpdate = new Action(() => ResourceUpdateRequested?.Invoke()), @init.Controller = new Func<UIElement, NativeItem>(Element => ((MyLocalModItem)Element).Controller), @init.UpdateMods = new Action<IEnumerable<LocalResourceFile>>(Values => ModMain.FrmInstanceMod.UpdateMods(Values)), @init.Favorite = new Func<bool>(() => ResourceFavorites.IsFavorite(FavoriteProject)), @init.FavoriteProject = new Func<ResourceProject>(() => FavoriteProject), @init.ToggleFavorite = new Action<ResourceProject>(Project => ResourceFavorites.Toggle(Project, FavoriteVersion)), @init.Website = ModBase.OpenWebsite, @init.Message = new Func<string, string, string, string, string, int>((Text, Caption, Button1, Button2, Button3) => ModMain.MyMsgBox(Text, Caption, Button1, Button2, Button3)), @init.FormatTime = StringUtils.FormatTimeSpan, @init).@init;
        }

        private EnvironmentHistoryPagePointView RestorePointView()
        {
            if (RestorePointDisplay is null)
            {
                RestorePointDisplay = new EnvironmentHistoryPagePointView()
                {
                    Root = this,
                    UpdateButton = BtnUpdate,
                    ResourceLogo = ResourceLogo,
                    PathLogo = PathLogo,
                    Point = new Func<JObject>(() => RestorePoint),
                    SetTitle = new Action<string>(Value => Title = Value),
                    SetSubTitle = new Action<string>(Value => SubTitle = Value),
                    SetDescription = new Action<string>(Value => Description = Value),
                    SetTags = new Action<List<string>>(Value => Tags = Value),
                    Title = new Func<string>(() => Title),
                    SubTitle = new Func<string>(() => SubTitle),
                    Description = new Func<string>(() => Description),
                    SetPathLogo = new Action<string>(Value => this.PathLogo.Source = Value),
                    ImageRoot = new Func<string>(() => ModBase.PathImage)
                };
            }

            return RestorePointDisplay;
        }

        internal string LoaderWarning => Controller.LoaderWarning;
        internal void RefreshLoaderWarning(string Folder, ModDependencyInspector.InspectionContext Context) => Controller.RefreshLoaderWarning(() => ModDependencyInspector.LoaderWarningFor(Folder, Entry.File.FullName, Context));
        private void SetLoaderWarning(string Message) => Controller.SetLoaderWarning(Message);
        internal void BindRestorePoint(JObject Value)
        {
            ResourceInfo = null;
            Controller.BindRestorePoint(Value);
        }

        internal void BindResource(InstanceResourceEntry Value, InstanceResourceOnlineInfo Info)
        {
            ResourceInfo = Info;
            Controller.BindResource(Value, Info);
        }

        public string Title
        {
            get
            {
                return Controller.Title;
            }

            set
            {
                Controller.Title = value;
            }
        }

        public string SubTitle
        {
            get
            {
                return Controller.SubTitle;
            }

            set
            {
                Controller.SubTitle = value;
            }
        }

        public string Description
        {
            get
            {
                return Controller.Description;
            }

            set
            {
                Controller.Description = value;
            }
        }

        public List<string> Tags
        {
            set
            {
                Controller.SetTags(value);
            }
        }

        public LocalResourceFile Entry
        {
            get
            {
                return (LocalResourceFile)this.Tag;
            }

            set
            {
                this.Tag = value;
            }
        }

        public event ClickEventHandler Click;
        public delegate void ClickEventHandler(object sender, MouseButtonEventArgs e);
        public event CheckEventHandler Check;
        public delegate void CheckEventHandler(object sender, ModBase.RouteEventArgs e);
        public event ChangedEventHandler Changed;
        public delegate void ChangedEventHandler(object sender, ModBase.RouteEventArgs e);
        private void RaiseClick(object sender, MouseButtonEventArgs e)
        {
            Click?.Invoke(sender, e);
        }

        private bool RaiseCheck()
        {
            var Args = new ModBase.RouteEventArgs(false);
            Check?.Invoke(this, Args);
            return Args.Handled;
        }

        private bool RaiseChanged()
        {
            var Args = new ModBase.RouteEventArgs(false);
            Changed?.Invoke(this, Args);
            return Args.Handled;
        }

        private void Button_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Controller.MouseUp(sender, e);
        }
        private bool _initializingActions;
        /// <summary>Connects the original deferred row commands without requiring pointer entry.</summary>
        public void EnsureActions()
        {
            Dispatcher.VerifyAccess();
            if (_initializingActions) return;
            _initializingActions = true;
            try { Controller.InitLate(this, EventArgs.Empty); }
            finally { _initializingActions = false; }
        }
        public void PerformClick()
        {
            if (!IsEnabled) return;
            EnsureActions();
            Controller.Activate(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) {
                RoutedEvent = MouseLeftButtonUpEvent, Source = this
            });
        }
        public void PerformFolderOpen()
        {
            if (!IsEnabled) return;
            EnsureActions();
            ResourceFolderRequested?.Invoke();
        }

        private void Button_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Controller.MouseDown();
        }

        private void Button_MouseLeave(object sender, object e)
        {
            Controller.MouseLeave();
        }

        private void Button_MouseSwipeStart(object sender, object e)
        {
            Controller.SwipeStart();
        }

        private void Button_MouseSwipe(object sender, object e)
        {
            Controller.Swipe(e);
        }

        public bool Checked
        {
            get
            {
                return Controller.Checked;
            }

            set
            {
                Controller.Checked = value;
            }
        }

        public Border RectBack
        {
            get
            {
                return Controller.RectBack;
            }
        }

        public IEnumerable<MyIconButton> Buttons
        {
            get
            {
                return Controller.Buttons;
            }

            set
            {
                Controller.Buttons = value;
            }
        }

        public Border RectCheck
        {
            get
            {
                return Controller.RectCheck;
            }
        }

        private string GetUpdateCompareDescription()
        {
            return Controller.GetUpdateCompareDescription();
        }

        private void FirstEnterScrollViewerViewport()
        {
            Controller.FirstViewport();
        }

        public void Refresh()
        {
            Controller.Refresh();
        }

        public void RefreshColor(object sender, EventArgs e)
        {
            Controller.RefreshColor(sender, e);
        }

        private void InitLate(object sender, EventArgs e)
        {
            Controller.InitLate(sender, e);
        }

        private void BtnUpdate_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Controller.ShowUpdateLog();
        }

        private void BtnFavorite_Click(object sender, EventArgs e)
        {
            Controller.FavoriteClick();
        }

        private void ShowUpdateLog()
        {
            Controller.ShowUpdateLog();
        }

        private void BtnUpdate_Click(object sender, EventArgs e)
        {
            Controller.UpdateClick();
        }

        private void ReleaseResourceInteraction()
        {
            Controller.ReleaseResourceInteraction();
        }

        private void PanTitle_SizeChanged()
        {
            Controller.ResizeTitle();
        }
    }
}
