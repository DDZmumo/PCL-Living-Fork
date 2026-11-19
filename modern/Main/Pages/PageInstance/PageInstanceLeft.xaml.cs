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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using NavigationController = global::PCLCS.InstanceNavigationController<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources>;
using NavigationHost = global::PCLCS.InstanceNavigationHost<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources>;
using ThrottleDebounce;

namespace PCL
{
    public partial class PageInstanceLeft : global::PCL.IRefreshable
    {
        public static global::PCL.ModMinecraft.McInstance Instance = (global::PCL.ModMinecraft.McInstance)null;
        public global::PCL.FormMain.PageSubType PageID;
        private bool IsSyncingSelection;
        private readonly global::System.Collections.Generic.Dictionary<global::PCL.FormMain.PageSubType, global::PCL.PageInstanceResources> ResourcePages;
        private global::PCL.PageInstanceKeybindings KeybindingsPage;
        private global::PCL.PageInstanceRestorePoints RestorePointsPage;
        private global::PCL.PageInstanceClone ClonePage;
        private PageInstancePackUpdate PackUpdatePage;
        private PageSetupPersonal PersonalPage;
        private readonly global::System.Collections.Generic.Dictionary<(global::PCL.ModMinecraft.McInstance Owner, string PathVersion, int Route), MyPageRight> _mmcToolPages = new();
        private readonly string _ordinaryModifyLogo;
        private readonly double _ordinaryModifyLogoScale;
        private global::PCLCS.InstanceSidebarState _currentSidebarState = new(global::PCLCS.InstanceSidebarFamily.Legacy, null);
        private global::PCL.ModMinecraft.McInstance _contextOwner;
        private string _contextPathVersion = "";
        private MmcConfigurationContextStamp _contextStamp;
        private int _syncingContext;
        private readonly global::PCLCS.InstanceNavigationView<global::PCL.MyListItem> NavigationView;
        private global::PCLCS.InstanceNavigationController<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources> PageController;
        private global::PCLCS.InstanceNavigationController<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources> Controller
        {
            get
            {
                this.NavigationView.Items = this.PanItem;
                this.NavigationView.Overall = this.ItemOverall;
                this.NavigationView.Setup = this.ItemSetup;
                this.NavigationView.Modify = this.ItemModify;
                this.NavigationView.Export = this.ItemExport;
                this.NavigationView.Mod = this.ItemMod;
                this.NavigationView.ModDisabled = this.ItemModDisabled;
                this.NavigationView.Keybindings = this.ItemKeybindings;
                this.NavigationView.RestorePoints = this.ItemRestorePoints;
                this.NavigationView.Clone = this.ItemClone;
                this.NavigationView.Resources = new[]
                {
                    this.ItemSaves,
                    this.ItemScreenshots,
                    this.ItemResourcePacks,
                    this.ItemShaderPacks,
                    this.ItemSchematics
                };
                this.NavigationView.Refreshables = new[]
                {
                    this.ItemSaves,
                    this.ItemScreenshots,
                    this.ItemResourcePacks,
                    this.ItemShaderPacks,
                    this.ItemSchematics,
                    this.ItemKeybindings,
                    this.ItemRestorePoints,
                    this.ItemClone
                };
                if (this.PageController is null)
                    this.PageController = new global::PCLCS.InstanceNavigationController<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources>(this.NavigationView, this.CreateHost());
                return this.PageController;
            }
        }

        public PageInstanceLeft()
        {
            __LfAttachOwnHandles();
            this.PageID = global::PCL.FormMain.PageSubType.Default;
            this.ResourcePages = new global::System.Collections.Generic.Dictionary<global::PCL.FormMain.PageSubType, global::PCL.PageInstanceResources>();
            this.NavigationView = new global::PCLCS.InstanceNavigationView<global::PCL.MyListItem>();
            this.InitializeComponent();
            this._ordinaryModifyLogo = this.ItemModify.Logo;
            this._ordinaryModifyLogoScale = this.ItemModify.LogoScale;
            this.ItemPackUpdate.Logo = ModBase.Logo.IconButtonRefresh;
            this.ItemPackUpdate.Check += (sender, _) => this.Controller.Check((FrameworkElement)sender);
            this.ItemPersonal.Logo = ModBase.Logo.IconButtonPersonalSettings;
            this.ItemPersonal.Check += (sender, _) => this.Controller.Check((FrameworkElement)sender);
            this.ItemMmcMove.Logo = LegacyBaseIcons.IconButtonOpen;
            this.ItemMmcMove.Check += (sender, _) => this.Controller.Check((FrameworkElement)sender);
            this.SyncInstanceContext();
        }

        private global::PCLCS.InstanceNavigationHost<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources> CreateHost()
        {
            global::PCLCS.InstanceNavigationHost<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources> @init = new global::PCLCS.InstanceNavigationHost<global::PCL.ModMinecraft.McInstance, global::PCL.FormMain.PageSubType, global::PCL.MyListItem, global::PCL.MyIconButton, global::PCL.MyPageRight, global::PCL.PageInstanceResources>();
            var result = (@init.Instance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageInstanceLeft.Instance), @init.Locked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Target => global::PCL.ModMinecraft.McInstanceLock.IsLocked(Target)), @init.BlocksNavigation = Target => global::PCL.ModMinecraft.McInstanceLock.BlocksNavigation(Target), @init.IsSyncingSelection = () => this.IsSyncingSelection, @init.SetSyncingSelection = Value => this.IsSyncingSelection = Value, @init.Modable = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Target => Target.Modable), @init.PageId = new Func<global::PCL.FormMain.PageSubType>(() => this.PageID), @init.SetPageId = new Action<global::PCL.FormMain.PageSubType>(Value => this.PageID = Value), @init.CurrentSubPage = new Func<global::PCL.FormMain.PageSubType>(() => global::PCL.ModMain.FrmMain.PageCurrentSub), @init.Normalize = global::PCL.ModModpackMode.NormalizeInstancePage, @init.ReadPage = new global::System.Collections.Generic.Dictionary<global::System.Int32, global::System.Func<global::PCL.MyPageRight>>() { { 0, new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmInstanceOverall) }, { 1, new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmInstanceSetup) }, { 2, new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmDownloadModify) }, { 3, new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmInstanceMod) }, { 4, new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmInstanceModDisabled) }, { 5, new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmInstanceExport) }, { 11, new Func<global::PCL.MyPageRight>(() => this.KeybindingsPage) }, { 12, new Func<global::PCL.MyPageRight>(() => this.RestorePointsPage) }, { 14, new Func<global::PCL.MyPageRight>(() => this.ClonePage) } }, @init.WritePage = new global::System.Collections.Generic.Dictionary<global::System.Int32, global::System.Action<global::PCL.MyPageRight>>() { { 0, new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmInstanceOverall = (global::PCL.PageInstanceOverall)Value) }, { 1, new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmInstanceSetup = (global::PCL.PageInstanceSetup)Value) }, { 2, new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmDownloadModify = (global::PCL.PageDownloadModify)Value) }, { 3, new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmInstanceMod = (global::PCL.PageInstanceMod)Value) }, { 4, new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmInstanceModDisabled = (global::PCL.PageInstanceModDisabled)Value) }, { 5, new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmInstanceExport = (global::PCL.PageInstanceExport)Value) }, { 11, new Action<global::PCL.MyPageRight>(Value => this.KeybindingsPage = (global::PCL.PageInstanceKeybindings)Value) }, { 12, new Action<global::PCL.MyPageRight>(Value => this.RestorePointsPage = (global::PCL.PageInstanceRestorePoints)Value) }, { 14, new Action<global::PCL.MyPageRight>(Value => this.ClonePage = (global::PCL.PageInstanceClone)Value) } }, @init.CreatePage = new global::System.Collections.Generic.Dictionary<global::System.Int32, global::System.Func<global::PCL.MyPageRight>>() { { 0, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceOverall()) }, { 1, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceSetup()) }, { 2, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageDownloadModify()) }, { 3, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceMod()) }, { 4, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceModDisabled()) }, { 5, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceExport()) }, { 11, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceKeybindings()) }, { 12, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceRestorePoints()) }, { 14, new Func<global::PCL.MyPageRight>(() => new global::PCL.PageInstanceClone()) } }, @init.ResourcePages = ResourcePages, @init.CreateResource = new Func<global::System.Int32, global::PCL.PageInstanceResources>(Kind => new global::PCL.PageInstanceResources((global::PCL.InstanceResourceKind)(Kind))), @init.RefreshSpecial = new global::System.Collections.Generic.Dictionary<global::System.Int32, global::System.Action<global::PCL.MyPageRight>>() { { 6, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceResources)Page).RefreshResources()) }, { 7, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceResources)Page).RefreshResources()) }, { 8, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceResources)Page).RefreshResources()) }, { 9, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceResources)Page).RefreshResources()) }, { 10, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceResources)Page).RefreshResources()) }, { 11, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceKeybindings)Page).RefreshBindings()) }, { 12, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceRestorePoints)Page).Refresh()) }, { 14, new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceClone)Page).Refresh()) } }, @init.ResetSetup = new Action<global::PCL.MyPageRight>(Page => ((global::PCL.PageInstanceSetup)Page).Reset()), @init.RefreshMods = new Action(() => global::PCL.PageInstanceMod.Refresh()), @init.RefreshExport = new Action(() => global::PCL.ModMain.FrmInstanceExport?.RefreshAll()), @init.RestartModify = new Action(() => global::PCL.ModMain.FrmDownloadModify?.PageLoaderRestart()), @init.HintRed = new Action<global::System.String>(Message => global::PCL.ModMain.Hint(Message, global::PCL.ModMain.HintType.Red)), @init.Confirm = new Func<global::System.String, global::System.String, global::System.String, global::System.Boolean, global::System.Int32>((Caption, Title, Button2, Warn) => global::PCL.ModMain.MyMsgBox(Caption, Title, Button2: Button2, IsWarn: Warn)), @init.Error = new Action<global::System.Exception, global::System.String>((ErrorValue, Message) => global::MeloongCore.Logger.Error(ErrorValue, Message)), @init.AdjustAnimation = new Action<global::System.Int32>(Value => global::PCL.ModAnimation.AniControlEnabled += Value), @init.Transitions = global::PCL.PageInstanceLeft.CreateTransitions(), @init.HiddenRefresh = global::PCL.PageSetupUI.HiddenRefresh, @init.BindBeforeMinecraftChange = new Action(() => global::PCL.InstanceClonerUi.BeforeMinecraftChangeHandler = new Action<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.String, global::System.Threading.CancellationToken>((Target, Previous, NextVersion, Token) => global::PCL.WorldBackups.BeforeMinecraftChange(Target, Previous, NextVersion, Token))), @init.Logo = new Func<global::PCL.MyListItem, global::System.String>(Item => Item.Logo), @init.SetLogo = new Action<global::PCL.MyListItem, global::System.String>((Item, Value) => Item.Logo = Value), @init.LogoScale = new Func<global::PCL.MyListItem, global::System.Double>(Item => Item.LogoScale), @init.SetLogoScale = new Action<global::PCL.MyListItem, global::System.Double>((Item, Value) => Item.LogoScale = Value), @init.Buttons = new Func<global::PCL.MyListItem, global::System.Collections.Generic.IEnumerable<global::PCL.MyIconButton>>(Item => Item.Buttons), @init.SetButtons = new Action<global::PCL.MyListItem, global::System.Collections.Generic.IEnumerable<global::PCL.MyIconButton>>((Item, Value) => Item.Buttons = Value), @init.CreateButton = new Func<global::PCL.MyIconButton>(() => new global::PCL.MyIconButton()), @init.ButtonLogo = new Func<global::PCL.MyIconButton, global::System.String>(Button => Button.Logo), @init.SetButtonLogo = new Action<global::PCL.MyIconButton, global::System.String>((Button, Value) => Button.Logo = Value), @init.SetButtonScale = new Action<global::PCL.MyIconButton, global::System.Double>((Button, Value) => Button.LogoScale = Value), @init.OnClick = new Action<global::PCL.MyIconButton, global::System.Action>((Button, Work) => Button.Click += (_, __) => Work()), @init.SetChecked = new Action<global::PCL.MyListItem, global::System.Boolean>((Item, Value) => Item.Checked = Value), @init.SetCheckedState = (Item, Value, User, Anime) => Item.SetChecked(Value, User, Anime), @init.RestoreLogo = new Func<global::System.String>(() => global::PCL.ModBase.Logo.IconButtonRefresh), @init.ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled), @init).@init;
            result.ReadPage[15] = () => PackUpdatePage;
            result.WritePage[15] = page => PackUpdatePage = (PageInstancePackUpdate)page;
            result.CreatePage[15] = () => new PageInstancePackUpdate();
            result.RefreshSpecial[15] = page => ((PageInstancePackUpdate)page).Refresh();
            result.ReadPage[(int)FormMain.PageSubType.InstancePersonal] = () => PersonalPage;
            result.WritePage[(int)FormMain.PageSubType.InstancePersonal] = page => PersonalPage = (PageSetupPersonal)page;
            result.CreatePage[(int)FormMain.PageSubType.InstancePersonal] = () => new PageSetupPersonal { IsInstanceContext = true };
            result.RefreshSpecial[(int)FormMain.PageSubType.InstancePersonal] = page => ((PageSetupPersonal)page).RefreshPage();
            result.SyncContext = EnsureInstanceContext;
            result.IsMmcContext = () => { EnsureInstanceContext(); return _currentSidebarState.IsMmcFamily; };
            result.NormalizeForContext = code => { EnsureInstanceContext(); return global::PCLCS.InstanceSidebarContext.NormalizeRoute(code, _currentSidebarState.IsMmcFamily); };
            result.ResolveContextPage = code => {
                EnsureInstanceContext();
                if (!_currentSidebarState.IsMmcFamily || !global::PCLCS.InstanceSidebarContext.IsMmcToolRoute(code) || Instance is null)
                    return null;
                return GetOrCreateMmcToolPage((FormMain.PageSubType)code, Instance);
            };
            result.RefreshContextPage = code => {
                SyncInstanceContext();
                if (!_currentSidebarState.IsMmcFamily || !global::PCLCS.InstanceSidebarContext.IsMmcToolRoute(code) || Instance is null)
                    return false;
                var page = GetOrCreateMmcToolPage((FormMain.PageSubType)code, Instance);
                RefreshManagedPage(page);
                return true;
            };
            return result;
        }

        internal static void SyncInstanceContextIfLoaded() => ModMain.FrmInstanceLeft?.SyncInstanceContext();

        // A prior managed snapshot can only keep an unreadable instance protected;
        // no writable operation is authorized by this presentation hint.
        internal static global::PCLCS.InstanceSidebarState ObserveInstanceContext(ModMinecraft.McInstance owner, bool preserveMmcFamily = false) =>
            global::PCLCS.InstanceSidebarContext.Observe(owner?.PathVersion,
                preserveMmcFamily || owner?.UsesMmcBackend == true ||
                owner?.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed);

        internal static bool IsMmcNavigationContext
        {
            get
            {
                if (ModMain.FrmInstanceLeft is not { } left) return ObserveInstanceContext(Instance).IsMmcFamily;
                left.EnsureInstanceContext();
                return left._currentSidebarState.IsMmcFamily;
            }
        }

        // Reuse the display snapshot within a navigation dispatch. It grants no
        // write authority: tool pages and their backends re-read before actions.
        internal void EnsureInstanceContext()
        {
            if (!ReferenceEquals(_contextOwner, Instance) ||
                !PathUtils.IsSame(_contextPathVersion, Instance?.PathVersion ?? "") ||
                _contextStamp != Instance?.Config.ContextStamp)
                SyncInstanceContext();
        }

        internal global::PCLCS.InstanceSidebarState CurrentSidebarState
        {
            get
            {
                EnsureInstanceContext();
                return _currentSidebarState;
            }
        }

        private MyPageRight GetOrCreateMmcToolPage(FormMain.PageSubType route, ModMinecraft.McInstance owner)
        {
            route = (FormMain.PageSubType)global::PCLCS.InstanceSidebarContext.NormalizeRoute((int)route, true);
            string root = owner.PathVersion ?? "";
            var key = (owner, root, (int)route);
            Guid identity = owner.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed
                ? owner.Config.ContextStamp.InstanceId : _currentSidebarState.InstanceId;
            if (!_mmcToolPages.TryGetValue(key, out var page) || !ManagedPageMatches(page, owner, identity))
            {
                page = (int)route switch {
                    2 => new PageInstanceMmcComponents(owner, identity),
                    5 => new PageInstanceExport(owner, identity),
                    12 => new PageInstanceRestorePoints(owner, identity),
                    15 => new PageInstancePackUpdate(owner, identity),
                    _ => new PageInstanceMmcTool(route, owner)
                };
                _mmcToolPages[key] = page;
            }
            else if (page is PageInstanceMmcTool tool) tool.RefreshState(updateBaselineIdentity: false);
            return page;
        }

        private static bool ManagedPageMatches(MyPageRight page, ModMinecraft.McInstance owner, Guid identity) => page switch {
            PageInstanceMmcComponents components => components.IsManagedOwner(owner, identity),
            PageInstanceExport export => export.IsManagedOwner(owner, identity),
            PageInstanceRestorePoints history => history.IsMmcHistory && ReferenceEquals(history.CapturedInstance, owner),
            PageInstancePackUpdate update => update.IsManagedOwner(owner, identity),
            PageInstanceMmcTool tool => ReferenceEquals(tool.CapturedInstance, owner),
            _ => false
        };

        private static void RefreshManagedPage(MyPageRight page) {
            switch (page) {
                case PageInstanceMmcComponents components: components.Refresh(); break;
                case PageInstanceExport export: export.RefreshAll(); break;
                case PageInstanceRestorePoints history: history.Refresh(); break;
                case PageInstancePackUpdate update: update.Refresh(); break;
                case PageInstanceMmcTool tool: tool.Refresh(); break;
            }
        }

        internal void SyncInstanceContext()
        {
            if (Interlocked.Exchange(ref _syncingContext, 1) != 0) return;
            try
            {
                var owner = Instance;
                string root = owner?.PathVersion ?? "";
                bool sameOwner = ReferenceEquals(_contextOwner, owner) && PathUtils.IsSame(_contextPathVersion, root);
                var nextState = ObserveInstanceContext(owner, sameOwner && _currentSidebarState.Inspection is { IsLegacy: false });
                bool wasMmc = _currentSidebarState.IsMmcFamily;
                bool isMmc = nextState.IsMmcFamily;
                bool ownerChanged = !ReferenceEquals(_contextOwner, owner) || !PathUtils.IsSame(_contextPathVersion, root);
                bool familyChanged = wasMmc != isMmc || _currentSidebarState.Family != nextState.Family;
                bool displayedManaged = ModMain.FrmMain is { } existingMain && _mmcToolPages.Values.Contains(existingMain.PageRight);

                if (ownerChanged || wasMmc != isMmc)
                {
                    _mmcToolPages.Clear();
                    ModMain.FrmDownloadModify = null;
                    ModMain.FrmInstanceExport = null;
                    this.RestorePointsPage = null;
                    this.ClonePage = null;
                    this.PackUpdatePage = null;
                }

                _currentSidebarState = nextState;
                _contextOwner = owner;
                _contextPathVersion = root;
                _contextStamp = owner?.Config.ContextStamp;

                LabVersionConfiguration.Text = nextState.ConfigSectionTitle;
                LabVersionTools.Text = nextState.ToolsSectionTitle;
                ItemModify.Title = nextState.ModifyTitle;
                ItemRestorePoints.Title = nextState.HistoryTitle;
                ItemMmcMove.Visibility = isMmc ? Visibility.Visible : Visibility.Collapsed;

                if (isMmc)
                {
                    ItemModify.Logo = LegacyBaseIcons.IconButtonList;
                    ItemModify.LogoScale = 0.88;
                    ItemModify.Visibility = Visibility.Visible;
                    ItemExport.Visibility = Visibility.Visible;
                }
                else
                {
                    ItemModify.Logo = _ordinaryModifyLogo;
                    ItemModify.LogoScale = _ordinaryModifyLogoScale;
                    if (wasMmc)
                    {
                        bool hideModify = !PageSetupUI.HiddenForceShow && Settings.Get<bool>("UiHiddenFunctionInstanceModify");
                        ItemModify.Visibility = hideModify ? Visibility.Collapsed : Visibility.Visible;
                    }
                    ItemExport.Visibility = ModModpackMode.Enabled ? Visibility.Collapsed : Visibility.Visible;
                }

                if (!isMmc && global::PCLCS.InstanceSidebarContext.IsMmcOnlyRoute((int)PageID))
                    Controller.SyncSelection(FormMain.PageSubType.InstanceOverall);
                else if ((int)PageID == 17) Controller.SyncSelection(FormMain.PageSubType.InstancePackUpdate);

                if (ModMain.FrmMain is { } main && main.PageCurrent.Page == FormMain.PageType.InstanceSetup && ReferenceEquals(main.PageLeft, this))
                {
                    if (ownerChanged || familyChanged) main.PageNameRefresh();
                    Guid identity = owner?.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed
                        ? owner.Config.ContextStamp.InstanceId : nextState.InstanceId;
                    bool wrongRightPage =
                        (isMmc && global::PCLCS.InstanceSidebarContext.IsMmcToolRoute((int)PageID) &&
                            !ManagedPageMatches(main.PageRight, owner, identity)) ||
                        (!isMmc && (displayedManaged || main.PageRight is PageInstanceMmcTool)) ||
                        (ownerChanged && main.PageRight is PageInstanceExport or PageInstanceRestorePoints or PageInstancePackUpdate);
                    if (wrongRightPage)
                    {
                        var replacement = Controller.PageGet(PageID);
                        if (main.IsLoaded) Controller.PageChangeRun(replacement);
                        else
                        {
                            main.PageRight = replacement;
                            main.PanMainRight.Child = replacement;
                        }
                    }
                    else if (familyChanged && isMmc && global::PCLCS.InstanceSidebarContext.IsMmcToolRoute((int)PageID))
                        RefreshManagedPage(main.PageRight);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _syncingContext, 0);
            }
        }

        private static global::PCLCS.InstanceNavigationTransitionHost<global::PCL.MyPageRight> CreateTransitions()
        {
            return new global::PCLCS.InstanceNavigationTransitionHost<global::PCL.MyPageRight>()
            {
                StopAnimation = new Action<global::System.String>(Name => global::PCL.ModAnimation.AniStop(Name)),
                PageRight = new Func<global::PCL.MyPageRight>(() => global::PCL.ModMain.FrmMain.PageRight),
                SetPageRight = new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmMain.PageRight = Value),
                PanelChild = new Func<global::PCL.MyPageRight>(() => ((global::PCL.MyPageRight)(global::PCL.ModMain.FrmMain.PanMainRight.Child))),
                SetPanelChild = new Action<global::PCL.MyPageRight>(Value => global::PCL.ModMain.FrmMain.PanMainRight.Child = Value),
                PageExit = new Action<global::PCL.MyPageRight>(Page => Page.PageOnExit()),
                PageForceExit = new Action<global::PCL.MyPageRight>(Page => Page.PageOnForceExit()),
                PageEnter = new Action<global::PCL.MyPageRight>(Page => Page.PageOnEnter()),
                AnimatePage = new Action<global::System.Action, global::System.Int32, global::System.Action, global::System.Int32, global::System.Boolean, global::System.String>((First, FirstDelay, Second, SecondDelay, After, Name) =>
                {
                    if (global::PCL.ModMain.FrmMain?.GlassShellActive == true)
                    {
                        global::PCL.ModAnimation.AniStop(Name);
                        First();
                        Second();
                        return;
                    }
                    global::PCL.ModAnimation.AniStart(new[] { global::PCL.ModAnimation.AaCode(() => First(), FirstDelay), global::PCL.ModAnimation.AaCode(() => Second(), SecondDelay, After) }, Name);
                })
            };
        }

        private void PageInstanceLeft_Loaded(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            this.Controller.Loaded();
        }

        private void ResourceButtonsInit()
        {
            this.Controller.InitializeButtons();
        }

        public void RefreshModDisabled()
        {
            this.Controller.RefreshModDisabled();
        }

        private void PageCheck(global::System.Windows.FrameworkElement sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Controller.Check(sender);
        }

        internal global::PCL.MyListItem NavigationItem(global::PCL.FormMain.PageSubType ID)
        {
            return this.Controller.NavigationItem(ID);
        }
        internal void RefreshPackUpdatePage() { if (PackUpdatePage?.IsLoaded == true) PackUpdatePage.Refresh(); }

        public global::System.Object PageGet(global::PCL.FormMain.PageSubType ID = (FormMain.PageSubType)(-1))
        {
            return this.Controller.PageGet(ID);
        }

        public void PageChange(global::PCL.FormMain.PageSubType ID)
        {
            this.Controller.PageChange(ID);
        }

        private static void PageChangeRun(global::PCL.MyPageRight Target)
        {
            global::PCLCS.InstanceNavigationTransitions.Run(Target, global::PCL.PageInstanceLeft.CreateTransitions());
        }

        public void Refresh_Click(global::System.Object sender, global::System.EventArgs e)
        {
            if (sender is FrameworkElement { Tag: { } tag }) this.Controller.RefreshTag(tag);
        }

        public void Refresh()
        {
            this.Controller.Refresh();
        }

        public void Refresh(global::PCL.FormMain.PageSubType SubType)
        {
            this.Controller.Refresh(SubType);
        }

        public void Reset(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.Reset();
        }

        internal void SyncSelection(FormMain.PageSubType Id)
        {
            Controller.SyncSelection(Id);
        }
        internal static bool IsEditingPage(FormMain.PageSubType Id)
        {
            return global::PCLCS.InstanceNavigationRules.IsEditingPage((int)Id);
        }
        public static global::MeloongCore.RedoableWorker<global::PCLCS.Java> CurrentJavaWorker = MmcJavaDiscovery.CreateWorker(() => global::PCL.PageInstanceLeft.Instance);
        public static void ReloadCurrentJava()
        {
            if (global::PCL.ModAnimation.AniControlEnabled != 0 || global::PCL.PageInstanceLeft.Instance is null) return;
            MmcJavaDiscovery.Start(global::PCL.PageInstanceLeft.Instance, global::PCL.PageInstanceLeft.CurrentJavaWorker);
        }
    }

    // Same category-label style as the native sidebar. Binding the item's
    // Visibility (rather than IsVisible) also works before the page is loaded.
    public sealed class InstanceSidebarGroupVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            values.Any(value => value is Visibility visibility && visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public static class InstanceSidebarIcons
    {
        // iconmonstr, Alexander Kahlkopf; https://iconmonstr.com/license/ .
        // The native About page retains the original iconmonstr credit/license.
        // Exact original paths: https://iconmonstr.com/copy-2-svg/ and
        // https://iconmonstr.com/time-17-svg/ . Only app UI glyphs are included.
        // Pinned mirrors used to read those two originals (no icon-pack download):
        // github.com/adeojoemmanuel/gcwalletV2/blob/665790f43228983435b6715b41cde36a041dbea2/src/assets/img/iconmonstr-copy-2.svg
        // github.com/maximefondu/Pickmo/blob/c9a5ca0460158d0e2030c6d57223cc15de59ce0a/src/assets/images/iconmonstr-time-17.svg
        public const string Copy = "M22 6v16h-16v-16h16zm2-2h-20v20h20v-20zm-24 17v-21h21v2h-19v19h-2z";
        public const string History = "M24 12c0 6.6-5.4 12-12 12S0 18.6 0 12h2c0 5.5 4.5 10 10 10s10-4.5 10-10S17.5 2 12 2C9.2 2 6.7 3.1 4.9 5L7 7 0 8.4l1.3-7 2.2 2.2C5.7 1.4 8.7 0 12 0c6.6 0 12 5.4 12 12zM11 6v8h7v-2h-5V6h-2z";

        // Two supplemental Tabler icons, not claimed to be original PCL art.
        // Paweł Kuna, MIT; full notice is embedded as Resources/Tabler-Icons-LICENSE.txt.
        // https://github.com/tabler/tabler-icons/tree/ed177a085b652fe04ec4f37ced867c94621e7840/icons/outline
        // SVG paths and their 24px, stroke=2, round cap/join are unchanged.
        public static readonly string Keyboard = Outline(
            "M2 8a2 2 0 0 1 2 -2h16a2 2 0 0 1 2 2v8a2 2 0 0 1 -2 2h-16a2 2 0 0 1 -2 -2l0 -8",
            "M6 10l0 .01", "M10 10l0 .01", "M14 10l0 .01", "M18 10l0 .01", "M6 14l0 .01", "M18 14l0 .01", "M10 14l4 .01");
        public static readonly string Export = Outline(
            "M14 3v4a1 1 0 0 0 1 1h4",
            "M11.5 21h-4.5a2 2 0 0 1 -2 -2v-14a2 2 0 0 1 2 -2h7l5 5v5m-5 6h7m-3 -3l3 3l-3 3");

        private static string Outline(params string[] paths)
        {
            var pen = new Pen(Brushes.Black, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
            foreach (string path in paths) geometry.AddGeometry(Geometry.Parse(path).GetWidenedPathGeometry(pen));
            // MyListItem accepts filled Path geometry; standard WPF widening
            // preserves the stock SVG strokes without changing shared controls.
            return geometry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
