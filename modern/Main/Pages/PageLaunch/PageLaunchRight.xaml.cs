using System;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeloongCore;
using PCLCS;
using RecentWorldAnimations = PCLCS.RecentWorldAnimationHost<PCL.ModAnimation.AniData>;
using RecentWorldController = PCLCS.RecentWorldPageController<PCL.ModMinecraft.McInstance, PCL.RecentWorld, PCL.ModAnimation.AniData>;
using RecentWorldHost = PCLCS.RecentWorldPageHost<PCL.ModMinecraft.McInstance, PCL.RecentWorld>;
using PCLLF.Presentation.Launch;

namespace PCL
{
    public partial class PageLaunchRight : IRefreshable, IDispatcherUnhandledException
    {
        private RecentWorldController RecentController;
        private readonly LegacyLaunchRight<ModMinecraft.McInstance, RecentWorld, ModAnimation.AniData> Native;
        // 保留真实下载任务与输入身份；读取/版本/缓存/内容处理在 C# 中。
        private ModLoader.LoaderTask<(string Address, bool ShouldRefresh), int> OnlineLoader;
        private bool? moreSettingsVisible;
        public PageLaunchRight()
        {
            __LfAttachOwnHandles();
            OnlineLoader = new ModLoader.LoaderTask<(string Address, bool ShouldRefresh), int>("下载主页", OnlineLoaderSub)
            {
                ReloadTimeout = 10 * 60 * 1000
            };
            Native = new LegacyLaunchRight<ModMinecraft.McInstance, RecentWorld, ModAnimation.AniData>(CreateView(), CreateHost(), LaunchPageRuntime.Animations(), () => RecentWorldService);
            this.InitializeComponent();
            DisabledPageAnimControls.Add(PanMoreSettings);
            RefreshMoreSettings(false);
        }

        private LegacyLaunchRightView CreateView()
        {
            return new LegacyLaunchRightView()
            {
                Root = new Func<FrameworkElement>(() => this),
                RecentCard = new Func<FrameworkElement>(() => this.CardRecentWorlds),
                DependencyCard = new Func<FrameworkElement>(() => this.CardDependencyChecks),
                DependencyAnchor = new Func<FrameworkElement>(() => this.PanDependencyOptionsAnchor),
                AutoFillCheck = new Func<FrameworkElement>(() => this.CheckAutoFillDependencies),
                LogPanel = new Func<FrameworkElement>(() => this.PanLog),
                DependencyPanel = new Func<FrameworkElement>(() => this.PanDependencyChecks),
                DependencyTransform = new Func<TranslateTransform>(() => this.TransDependencyChecks),
                CustomPanel = new Func<Panel>(() => this.PanCustom),
                DependencyCheck = new Func<bool>(() => this.CheckModDependencies.Checked),
                PageId = new Func<string>(() => this.PageUuid.ToString()),
                ScrollToHome = new Action(() => this.PanBack.ScrollToHome()),
                ResizeDependencyCard = new Action(() => this.CardDependencyChecks.TriggerForceResize())
            };
        }

        private LegacyLaunchRightHost CreateHost()
        {
            return new LegacyLaunchRightHost()
            {
                ReadString = new Func<string, string>(Key => Settings.Get<string>(Key)),
                ReadInteger = new Func<string, int>(Key => Settings.Get<int>(Key)),
                WriteSetting = new Action<string, object>((Key, Value) => Settings.Set(Key, Value)),
                Debug = new Func<bool>(() => ModBase.ModeDebug),
                IsLaunchPage = new Func<bool>(() => ModMain.FrmMain.PageCurrent.Page == FormMain.PageType.Launch),
                BasePath = new Func<string>(() => Paths.Base),
                TempPath = new Func<string>(() => ModBase.PathTemp),
                RunInThread = new Action<Action>(Work => ModBase.RunInThread(Work)),
                Dispatch = new Action<Action>(Work => ModBase.RunInUi(Work)),
                DispatchWait = new Action<Action>(Work => ModBase.RunInUiWait(Work)),
                DispatchWhenLoaded = new Action<Action>(Work => ModBase.RunInUi(Work, true)),
                ChangeAnimationControl = new Action<int>(Delta => ModAnimation.AniControlEnabled += Delta),
                DisablePageAnimation = new Action<FrameworkElement>(Control => this.DisabledPageAnimControls.Add(Control)),
                RefreshSettings = new Action<FrameworkElement>(Control => SettingService.RefreshSettings(Control)),
                Hint = new Action<string>(Text => ModMain.Hint(Text)),
                ChangeToLaunchPage = new Action(() => ModMain.FrmMain.PageChange((FormMain.PageStackData)FormMain.PageType.Launch)),
                HtmlReplace = new Func<string, string>(Content => ModMain.ArgumentReplace(Content, WebUtility.HtmlEncode)),
                XmlReplace = new Func<string, string>(Content => ModMain.ArgumentReplace(Content, StringUtils.XmlEscape)),
                Request = new Func<string, string>(Address => ModNet.NetRequestByClientRetry(Address)),
                ParseXaml = new Func<string, UIElement>(Content => (UIElement)ModBase.GetObjectFromXML(Content)),
                RetryMessage = new Func<string, string, int>((Text, Title) => ModMain.MyMsgBox(Text, Title, "重试", "取消")),
                StartOnline = new Action<(string Address, bool ShouldRefresh)>(Input => OnlineLoader.Start(Input)),
                ResetOnlineInput = new Action<(string Address, bool ShouldRefresh)>(Input => OnlineLoader.Input = Input)
            };
        }

        private RecentWorldController RecentWorldService
        {
            get
            {
                if (RecentController is null)
                {
                    var View = new RecentWorldPageView()
                    {
                        Root = this,
                        Card = CardRecentWorlds,
                        Item = ItemRecentWorld,
                        Transform = TransRecentWorld,
                        PageId = new Func<string>(() => this.PageUuid.ToString()),
                        CardTitle = new Func<TextBlock>(() => this.CardRecentWorlds.MainTextBlock),
                        SetTitle = new Action<string>(Value => this.ItemRecentWorld.Title = Value),
                        SetInfo = new Action<string>(Value => this.ItemRecentWorld.Info = Value)
                    };
                    RecentController = new RecentWorldController(View, CreateRecentWorldHost(), CreateRecentWorldAnimations());
                }

                return RecentController;
            }
        }

        private RecentWorldHost CreateRecentWorldHost()
        {
            return new RecentWorldHost()
            {
                SelectedInstance = new Func<ModMinecraft.McInstance>(() => ModMinecraft.McInstanceSelected),
                RecoverExitedSessions = QuickPlayWorlds.RecoverExitedSessions,
                DiscoverExistingWorlds = QuickPlayWorlds.DiscoverExistingWorlds,
                HasRunningSessions = QuickPlayWorlds.HasRunningSessions,
                Recent = QuickPlayWorlds.Recent,
                WorldPath = QuickPlayWorlds.WorldPath,
                LoadImage = new Func<string, int, ImageSource>((FileName, Width) => InstanceResourceStore.LoadImage(FileName, Width)),
                GrassImage = new Func<ImageSource>(() => (ImageSource)new MyBitmap(ModBase.PathImage + "Blocks/Grass.png")),
                Background = new Action<Action, string>((Work, Name) => ModBase.RunInNewThread(Work, Name)),
                Dispatch = new Action<Action>(Work => ModBase.RunInUi(Work)),
                Warn = new Action<Exception, string>((ErrorValue, Message) => Logger.Warn(ErrorValue, Message)),
                Error = new Action<Exception, string>((ErrorValue, Message) => Logger.Error(ErrorValue, Message, LogBehavior.Toast)),
                HasRunningGame = new Func<bool>(() => ModWatcher.HasRunningMinecraft),
                LaunchLoading = new Func<bool>(() => ModLaunch.McLaunchLoader.State == ModBase.LoadState.Loading),
                AnimationEnabled = new Func<bool>(() => ModAnimation.AniControlEnabled == 0),
                Enabled = new Func<bool>(() => Settings.Get<bool>("LaunchRecentWorlds")),
                ApplyDependencyOptionsState = ApplyDependencyOptionsState,
                CreateInstance = new Func<string, ModMinecraft.McInstance>(VersionPath => new ModMinecraft.McInstance(VersionPath)),
                GameFolder = new Func<ModMinecraft.McInstance, string>(Value => Value.PathIndie),
                SamePath = new Func<string, string, bool>((Left, Right) => PathUtils.IsSame(Left, Right)),
                StartWorld = QuickPlayWorlds.Start,
                HintError = new Action<string>(Message => ModMain.Hint(Message, ModMain.HintType.Red))
            };
        }

        private static RecentWorldAnimations CreateRecentWorldAnimations()
        {
            return new RecentWorldAnimations()
            {
                Opacity = new Func<FrameworkElement, double, int, ModAnimation.AniData>((Target, Value, Time) => ModAnimation.AaOpacity(Target, Value, Time)),
                TranslateX = RecentWorldTranslateX,
                Code = new Func<Action, int, bool, ModAnimation.AniData>((Work, Delay, After) => ModAnimation.AaCode(() => Work(), Delay, After)),
                Start = new Action<ModAnimation.AniData[], string>((Animations, Name) => ModAnimation.AniStart(Animations, Name)),
                Stop = new Action<string>(Name => ModAnimation.AniStop(Name)),
                IsRunning = ModAnimation.AniIsRun
            };
        }

        private static ModAnimation.AniData RecentWorldTranslateX(FrameworkElement Target, double Value, int Time, RecentWorldSlideEase Ease)
        {
            if (Ease == RecentWorldSlideEase.OutBackWeak)
                return ModAnimation.AaTranslateX(Target, Value, Time, Ease: new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak));
            return ModAnimation.AaTranslateX(Target, Value, Time, Ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak));
        }

        private void InitRecentWorldCard()
        {
            Native.InitializeRecentCard();
        }

        private void RecentPageEntered()
        {
            RefreshMoreSettings(false);
            Native.PageEntered();
        }

        private void Init()
        {
            RefreshMoreSettings(false);
            Native.Loaded();
        }

        internal void RefreshMoreSettings(bool animate = true)
        {
            bool visible = Settings.Get<bool>("UiLaunchMoreSettings");
            string name = "Launch More Settings " + PageUuid;
            if (animate && moreSettingsVisible == visible && (ModAnimation.AniIsRun(name) ||
                PanMoreSettings.Visibility == (visible ? Visibility.Visible : Visibility.Collapsed))) return;
            moreSettingsVisible = visible;
            ModAnimation.AniStop(name);
            PanMoreSettings.IsHitTestVisible = visible;
            double target = visible ? 0 : -300;
            if (!animate || !PanMoreSettings.IsLoaded || ModAnimation.AniControlEnabled != 0)
            {
                PanMoreSettings.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                PanMoreSettings.Opacity = visible ? 1 : 0;
                TransMoreSettings.X = target;
                return;
            }
            PanMoreSettings.Visibility = Visibility.Visible;
            ModAnimation.AniStart(new[] {
                ModAnimation.AaTranslateX(PanMoreSettings, target - TransMoreSettings.X, visible ? 280 : 150,
                    Ease: visible ? new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak) : new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaOpacity(PanMoreSettings, (visible ? 1 : 0) - PanMoreSettings.Opacity, visible ? 180 : 100),
                ModAnimation.AaCode(() => {
                    if (moreSettingsVisible == false) PanMoreSettings.Visibility = Visibility.Collapsed;
                }, After: true)
            }, name);
        }

        internal void RefreshRecentWorlds()
        {
            Native.RefreshRecentWorlds();
        }

        internal void ShowRecentWorld(Tuple<RecentWorld, ImageSource> Item)
        {
            Native.ShowRecentWorld(Item);
        }

        internal void UpdateRecentWorldVisibility()
        {
            Native.UpdateRecentWorldVisibility();
        }

        internal void ApplyRecentWorldState(bool GameActive, bool Animate)
        {
            Native.ApplyRecentWorldState(GameActive, Animate);
        }

        internal void RefreshDependencyOptions()
        {
            Native.RefreshDependencyOptions();
        }

        private void DependencyCheckChanged(object Sender, bool User)
        {
            Native.DependencyChanged();
        }

        internal void ApplyDependencyOptionsState(bool GameActive, bool Animate, bool RecentVisible)
        {
            Native.ApplyDependencyOptionsState(GameActive, Animate, RecentVisible);
        }

        private void RecentWorldClick(object Sender, MouseButtonEventArgs e)
        {
            Native.RecentWorldClick(Sender, e);
        }

        internal void BeginRecentWorldLaunch(Func<bool> Start, bool Animate = true)
        {
            Native.BeginRecentWorldLaunch(Start, Animate);
        }

        private void ReleaseRecentWorlds()
        {
            Native.Unloaded();
            RefreshMoreSettings(false);
        }

        private void Refresh()
        {
            Native.Refresh();
        }

        private void OnlineLoaderSub(ModLoader.LoaderTask<(string Address, bool ShouldRefresh), int> Task)
        {
            Native.OnlineLoad(() => Task.Input);
        }

        public void ForceRefresh()
        {
            Native.ForceRefresh();
        }

        void IRefreshable.Refresh() => ForceRefresh();
        private void DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Native.DispatcherUnhandledException(e);
        }

        void IDispatcherUnhandledException.DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) => DispatcherUnhandledException(sender, e);
    }
}
