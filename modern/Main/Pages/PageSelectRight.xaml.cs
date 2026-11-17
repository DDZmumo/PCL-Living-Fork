using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
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
using CloneSelectionHost = global::PCLCS.InstanceCloneSelectionHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode>;
using CloneSelection = global::PCLCS.InstanceCloneSelectionPresenter<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>;
using SelectionCore = global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageSelectRight
    {
        private static readonly global::PCLCS.InstanceCloneSelectionPresenter<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet> ClonePresenter = new global::PCLCS.InstanceCloneSelectionPresenter<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>(global::PCL.InstanceCloneGroups.Service, global::PCL.PageSelectRight.CreateCloneHost());
        public global::System.Boolean ShowHidden;
        private global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet> Controller;
        private global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet> Service
        {
            get
            {
                if (this.Controller is null)
                    this.Controller = new global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>(global::PCL.PageSelectRight.CreateHost(this), global::PCL.PageSelectRight.ClonePresenter);
                return this.Controller;
            }
        }

        public PageSelectRight()
        {
            __LfAttachOwnHandles();
            this.ShowHidden = false;
            this.InitializeComponent();
        }

        private static global::PCLCS.InstanceSelectionPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType> CreateHost(global::PCL.PageSelectRight Owner)
        {
            return new global::PCLCS.InstanceSelectionPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType>()
            {
                View = new Func<global::PCLCS.InstanceSelectionPageView>(() => Owner.CreateView()),
                ShowHidden = new Func<global::System.Boolean>(() => Owner.ShowHidden),
                ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
                HiddenDownload = new Func<global::System.Boolean>(() => global::PCL.Settings.Get<bool>("UiHiddenPageDownload")),
                HiddenForceShow = new Func<global::System.Boolean>(() => global::PCL.PageSetupUI.HiddenForceShow),
                LoaderFailed = new Func<global::System.Boolean>(() => (global::PCL.ModMinecraft.McInstanceListLoader.State == global::PCL.ModBase.LoadState.Failed)),
                Shift = new Func<global::System.Boolean>(() => global::PCL.My.MyWpfExtension.Computer.Keyboard.ShiftKeyDown),
                InitializeLoader = new Action<global::System.Action>(Finished => Owner.PageLoaderInit(Owner.Load, Owner.PanLoad, Owner.PanAllBack, (global::System.Windows.FrameworkElement)null, global::PCL.ModMinecraft.McInstanceListLoader, _ => Finished(), AutoRun: false)),
                RefreshInstances = global::PCL.PageSelectRight.RefreshNativeInstances,
                Instances = new Func<global::System.Collections.Generic.IDictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>>>(() => global::PCL.ModMinecraft.McInstanceList),
                SetInstances = new Action<global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>>>(Value => global::PCL.ModMinecraft.McInstanceList = Value),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                SetSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.ModMinecraft.McInstanceSelected = Value),
                InstancePage = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance),
                SetInstancePage = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageInstanceLeft.Instance = Value),
                MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                NewInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Path => new global::PCL.ModMinecraft.McInstance(Path)),
                Locked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => global::PCL.ModMinecraft.McInstanceLock.IsLocked(Instance)),
                BlocksNavigation = Instance => global::PCL.ModMinecraft.McInstanceLock.BlocksNavigation(Instance),
                AllowsInstance = global::PCL.ModModpackMode.AllowsInstance,
                ForceRefresh = new Action(() => global::PCL.ModMinecraft.McInstanceListForceRefresh = true),
                PageBack = new Action(() => global::PCL.ModMain.FrmMain.PageBack()),
                OpenDownload = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download, global::PCL.FormMain.PageSubType.DownloadInstall)),
                OpenInstanceFolder = global::PCL.PageInstanceOverall.OpenInstanceFolder,
                OpenInstanceSetup = new Action<global::PCL.ModMinecraft.McInstance>(Instance =>
                {
                    global::PCL.PageInstanceLeft.Instance = Instance;
                    global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup, (global::PCL.FormMain.PageSubType)0);
                }),
                Hint = global::PCL.PageSelectRight.NativeHint,
                Message = new Func<global::PCLCS.SelectionPageMessage, global::System.Int32>(Request => global::PCL.ModMain.MyMsgBox(Request.Text, Request.Title, Request.Button1, Request.Button2, Request.Button3, Request.IsWarn)),
                Button = global::PCL.PageSelectRight.CreateNativeButton,
                SetButtons = new Action<global::System.Windows.FrameworkElement, global::System.Collections.Generic.IEnumerable<global::System.Windows.FrameworkElement>>((Row, Buttons) => ((global::PCL.MyListItem)Row).Buttons = Buttons.Cast<MyIconButton>().ToArray()),
                BindClick = new Action<global::System.Windows.FrameworkElement, global::System.Action<global::System.Windows.FrameworkElement, global::System.EventArgs>>((Row, Click) => ((global::PCL.MyListItem)Row).Click += (Sender, Args) => Click((global::System.Windows.FrameworkElement)Sender, Args)),
                BindRightClick = new Action<global::System.Windows.FrameworkElement, global::System.Action>((Row, Click) => Row.MouseRightButtonUp += (_, __) => Click()),
                WriteIni = new Action<global::System.String, global::System.String, global::System.Object>((File, Key, Value) => global::PCL.ModBase.WriteIni(File, Key, Conversions.ToString(Value))),
                DeleteConfirmed = DeleteInstanceInBackground,
                BeginManagedDelete = MmcDeleteUi.PromptDelete
            };
        }

        private global::PCLCS.InstanceSelectionPageView CreateView()
        {
            return new global::PCLCS.InstanceSelectionPageView()
            {
                Main = PanMain,
                Back = PanBack,
                Empty = PanEmpty,
                Download = BtnEmptyDownload,
                EmptyTitle = LabEmptyTitle,
                EmptyContent = LabEmptyContent,
                ScrollHome = new Action(() => this.PanBack.ScrollToHome()),
                ForceResize = new Action(() => this.PanLoad.TriggerForceResize())
            };
        }

        private static void RefreshNativeInstances(global::System.Boolean Force)
        {
            global::PCL.ModLoader.LoaderFolderRun(global::PCL.ModMinecraft.McInstanceListLoader, global::PCL.ModMinecraft.McFolderSelected, (Force) ? (global::PCL.ModLoader.LoaderFolderRunType.ForceRun) : (global::PCL.ModLoader.LoaderFolderRunType.RunOnUpdated), MaxDepth: 1, ExtraPath: @"versions\");
        }

        private static void NativeHint(global::System.String Text, global::PCLCS.SelectionPageHint Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.SelectionPageHint.Red:
                {
                    global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red);
                    break;
                }

                case global::PCLCS.SelectionPageHint.Blue:
                {
                    global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Blue);
                    break;
                }

                default:
                {
                    global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green);
                    break;
                }
            }
        }

        private static global::System.Windows.FrameworkElement CreateNativeButton(global::System.String Kind, global::System.String ToolTip, global::System.Double Scale, global::System.Windows.Visibility Visibility, global::System.Action Click)
        {
            var Button = new global::PCL.MyIconButton()
            {
                LogoScale = Scale,
                Visibility = Visibility,
                ToolTip = ToolTip
            };
            switch ((Kind ?? ""))
            {
                case "like-fill":
                {
                    Button.Logo = global::PCL.ModBase.Logo.IconButtonLikeFill;
                    break;
                }

                case "like-line":
                {
                    Button.Logo = global::PCL.ModBase.Logo.IconButtonLikeLine;
                    break;
                }

                case "delete":
                {
                    Button.Logo = global::PCL.ModBase.Logo.IconButtonDelete;
                    break;
                }

                case "setup":
                {
                    Button.Logo = global::PCL.ModBase.Logo.IconButtonSetup;
                    break;
                }

                case "open":
                {
                    Button.Logo = global::PCL.ModBase.Logo.IconButtonOpen;
                    break;
                }

                default:
                {
                    throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                }
            }

            Button.Click += (_, __) => Click();
            return Button;
        }

        private void PageSelectRight_Loaded(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            this.Service.Loaded();
        }

        private void LoaderInit()
        {
            this.Service.Initialize();
        }

        private void Load_Click(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Service.LoadClick();
        }

        private static global::PCLCS.InstanceCloneSelectionHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode> CreateCloneHost()
        {
            global::PCLCS.InstanceCloneSelectionHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode> @init = new global::PCLCS.InstanceCloneSelectionHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode>();
            return (@init.HiddenCategory = global::PCL.ModMinecraft.McInstanceCardType.Hidden, @init.StarCategory = global::PCL.ModMinecraft.McInstanceCardType.Star, @init.IsCollapsedCategory = new Func<global::PCL.ModMinecraft.McInstanceCardType, global::System.Boolean>(Kind => (((Kind == global::PCL.ModMinecraft.McInstanceCardType.Rubbish) || (Kind == global::PCL.ModMinecraft.McInstanceCardType.Error)) || (Kind == global::PCL.ModMinecraft.McInstanceCardType.Fool))), @init.IsLocked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => global::PCL.ModMinecraft.McInstanceLock.BlocksNavigation(Instance)), @init.AllowsInstance = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => global::PCL.ModModpackMode.AllowsInstance(Instance.PathVersion)), @init.VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion), @init.Name = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name), @init.Info = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Info), @init.SelectedPath = new Func<global::System.String>(() => global::PCL.ModMinecraft.McInstanceSelected?.PathVersion), @init.CreateRow = new Func<global::PCL.ModMinecraft.McInstance, global::System.Action<global::System.Object, global::System.EventArgs>, global::System.Windows.FrameworkElement>((Instance, Initialized) => Instance.ToListItem((Item, Args) => Initialized(Item, Args))), @init.InitializeRow = new Action<global::System.Object, global::System.EventArgs>((Item, Args) => global::PCL.PageSelectRight.McInstanceListContent((global::PCL.MyListItem)Item, Args)), @init.SetRowInfo = new Action<global::System.Object, global::System.String>((Item, Info) => ((global::PCL.MyListItem)Item).Info = Info), @init.CreateCard = new Func<global::System.String, global::System.Windows.Thickness, global::PCLCS.IInstanceCloneSelectionCard<global::PCL.InstanceCloneGroupNode>>((Title, Margin) => new global::PCL.PageSelectRight.CloneCard(new global::PCL.MyCard() { Title = Title, Margin = Margin })), @init.AdaptCard = new Func<global::System.Object, global::PCLCS.IInstanceCloneSelectionCard<global::PCL.InstanceCloneGroupNode>>(Element => new global::PCL.PageSelectRight.CloneCard((global::PCL.MyCard)Element)), @init.SwappedHeight = (global::System.Double)global::PCLLF.Presentation.Controls.MyCardView.SwapedHeight, @init).@init;
        }

        private sealed class CloneCard : global::PCLCS.IInstanceCloneSelectionCard<global::PCL.InstanceCloneGroupNode>
        {
            private readonly global::PCL.MyCard Card;
            public CloneCard(global::PCL.MyCard Card)
            {
                this.Card = Card;
            }

            public global::System.Windows.FrameworkElement Element
            {
                get
                {
                    return this.Card;
                }
            }

            public bool IsSwapped
            {
                get
                {
                    return this.Card.IsSwapped;
                }

                set
                {
                    this.Card.IsSwapped = value;
                }
            }

            public void SetContent(global::System.Windows.Controls.Panel Content)
            {
                this.Card.Children.Add(Content);
                this.Card.SwapControl = Content;
            }

            public void SetContentBuilder(global::System.Collections.Generic.IEnumerable<global::PCL.InstanceCloneGroupNode> Items, global::System.Func<global::PCL.InstanceCloneGroupNode, global::System.Windows.FrameworkElement> Builder)
            {
                this.Card.SetContentBuilder(Items, Node => (global::System.Windows.UIElement)Builder(Node));
            }

            public void BuildContent()
            {
                this.Card.BuildContent();
            }
        }

        private void McInstanceListUI(global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Int32> Loader)
        {
            this.Service.Render();
        }

        internal static global::System.Windows.FrameworkElement BuildCloneChoice(global::PCL.InstanceCloneGroupNode Node, global::System.Collections.Generic.Dictionary<global::System.String, global::PCL.ModMinecraft.McInstance> InstancesByPath, global::System.String SelectedPath)
        {
            return global::PCL.PageSelectRight.ClonePresenter.BuildCloneChoice(Node, InstancesByPath, SelectedPath);
        }

        public static void McInstanceListContent(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>.RowContent(sender, global::PCL.PageSelectRight.CreateHost((global::PCL.PageSelectRight)null));
        }

        public static void Item_Click(global::PCL.MyListItem sender, global::System.EventArgs e)
        {
            global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>.ItemClick(sender, global::PCL.PageSelectRight.CreateHost((global::PCL.PageSelectRight)null));
        }

        private void BtnDownload_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Service.OpenDownload();
        }


        public static void DeleteInstance(global::PCL.MyListItem Item, global::PCL.ModMinecraft.McInstance Instance)
        {
            global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>.Delete(Item, Instance, global::PCL.PageSelectRight.CreateHost((global::PCL.PageSelectRight)null));
        }

        internal static IWorker BeginInstanceDeletion(ModMinecraft.McInstance instance, bool recycle, Action completed)
        {
            string path = instance.PathVersion;
            string name = instance.Name;
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            return InstanceDeletionOperation.Start(
                () => InstanceFolderDeletion.Delete(path, recycle, RecycleInstanceFolder),
                worker => ModMinecraft.McInstanceLock.TryAcquire(worker, path),
                worker => ModMinecraft.McInstanceLock.Release(worker, path),
                work => dispatcher.InvokeAsync(work),
                () => {
                    try { completed(); }
                    catch (Exception error) { Logger.Error(error, "删除完成后刷新版本列表失败", LogBehavior.Alert); }
                },
                error => Logger.Error(error, "删除版本 " + name + " 失败", LogBehavior.Alert),
                () => ModMain.Hint("已取消删除版本 " + name + "。", ModMain.HintType.Blue));
        }
        private static void RecycleInstanceFolder(string path)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
            void Recycle()
            {
                try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException); }
                catch (Exception error) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            }
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) Recycle();
            else
            {
                var thread = new Thread(Recycle) { IsBackground = true, Name = "Recycle Minecraft Instance" };
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            }
            failure?.Throw();
        }
        internal static void DeleteInstanceInBackground(ModMinecraft.McInstance instance, bool recycle, Action completed)
        {
            if (BeginInstanceDeletion(instance, recycle, completed) is null)
                ModMain.Hint("该版本正在被修改，请稍后再删除！", ModMain.HintType.Red);
            else ModMain.Hint("正在删除版本 " + instance.Name + "……", ModMain.HintType.Blue);
        }

        internal static global::PCL.ModMinecraft.McInstance ResolveDeleteTarget(global::PCL.MyListItem Item, global::PCL.ModMinecraft.McInstance Expected)
        {
            return global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>.ResolveDeleteTarget(Item, Expected);
        }

        internal static global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>> WithoutDeletedInstance(global::System.Collections.Generic.IDictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>> Cards, global::System.String DeletedFolder)
        {
            return global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>.WithoutDeletedInstance(Cards, DeletedFolder);
        }

        internal static global::PCL.ModMinecraft.McInstance SelectionAfterDelete(global::PCL.ModMinecraft.McInstance Current, global::System.String DeletedFolder)
        {
            return global::PCLCS.InstanceSelectionPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.InstanceCloneGroupNode, global::PCL.InstanceCloneGroupSet>.SelectionAfterDelete(Current, DeletedFolder);
        }

        public void BtnEmptyDownload_Loaded()
        {
            this.Service.EmptyDownloadLoaded();
        }
    }
}
