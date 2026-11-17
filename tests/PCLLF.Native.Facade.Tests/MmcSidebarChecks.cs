using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using PCLLF.Presentation.Windowing;
using Controller = PCLCS.InstanceNavigationController<PCL.ModMinecraft.McInstance, PCL.FormMain.PageSubType, PCL.MyListItem, PCL.MyIconButton, PCL.MyPageRight, PCL.PageInstanceResources>;
using Host = PCLCS.InstanceNavigationHost<PCL.ModMinecraft.McInstance, PCL.FormMain.PageSubType, PCL.MyListItem, PCL.MyIconButton, PCL.MyPageRight, PCL.PageInstanceResources>;

namespace PCLLF.Native.Facade.Tests;

// Actual Main BAML navigation, Presentation title host, ModModpackMode and Mmc*Ui fences.
// No game launch, network IO or real user folders.
internal static class MmcSidebarChecks
{
    internal static void Run(FixtureEnvironment environment) => RunPart(environment, "routes");
    internal static void RunStates(FixtureEnvironment environment) => RunPart(environment, "states");
    internal static void RunVisuals(FixtureEnvironment environment) => RunPart(environment, "visuals");

    private static void RunPart(FixtureEnvironment environment, string part)
    {
        Console.WriteLine("START mmc-sidebar fixture scope");
        using FileTaskScope files = new(environment);
        using var scope = new Scope(files);
        scope.Run(environment, part);
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-sidebar " + part + ": actual Main BAML and MMC routes; original60-second child deadline retained.");
    }

    internal static void RunAdmission(FixtureEnvironment environment)
    {
        using FileTaskScope files = new(environment);
        using (var scope = new Scope(files)) scope.RunAdmission();
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-sidebar-admission: real Main host switches same-family owner/current right pane; displayed and cached repair/unavailable states stay protected; refresh and clone/update/repair cannot adopt a replaced identity.");
    }

    private sealed class Scope : IDisposable
    {
        private readonly FileTaskScope files;
        private readonly Stack<Action> restore = new();
        private readonly List<Window> windows = [];
        private readonly List<ModLoader.LoaderCombo<string>> tasks = [];

        internal Scope(FileTaskScope files)
        {
            this.files = files;
            foreach (string name in new[] { "FrmMain", "FrmInstanceLeft", "FrmInstanceOverall", "FrmDownloadModify", "FrmInstanceExport", "FrmInstanceMod", "FrmInstanceModDisabled" })
            {
                FieldInfo field = Native.Field(typeof(ModMain), name);
                object? previous = field.GetValue(null);
                restore.Push(() => field.SetValue(null, previous));
                field.SetValue(null, null);
            }
            var oldInstance = PageInstanceLeft.Instance;
            restore.Push(() => PageInstanceLeft.Instance = oldInstance);
            PageInstanceLeft.Instance = null!;

            PropertyInfo backup = Native.Property(Native.Type("PCL.InstanceClonerUi"), "BeforeMinecraftChangeHandler");
            object? oldBackup = backup.GetValue(null);
            restore.Push(() => backup.SetValue(null, oldBackup));

            var scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            Action oldRefresh = scheduler.RefreshExtraDownload;
            restore.Push(() => scheduler.RefreshExtraDownload = oldRefresh);
            scheduler.RefreshExtraDownload = () => { };

            bool oldModpackExported = Settings.Get<bool>("LivingModpackExported");
            bool oldModpackMode = Settings.Get<bool>("LivingModpackMode");
            bool oldHiddenModify = Settings.Get<bool>("UiHiddenFunctionInstanceModify");
            var uiRuntime = (LegacyPageSetupUIRuntime)Native.Field(Native.Type("PCL.SetupPageRuntime"), "UI").GetValue(null)!;
            bool oldHiddenForce = uiRuntime.HiddenForceShow;
            int oldColorMode = Settings.Get<int>("UiLauncherColorMode");
            restore.Push(() =>
            {
                Native.Cache("LivingModpackExported", oldModpackExported);
                Native.Cache("LivingModpackMode", oldModpackMode);
                Native.Cache("UiHiddenFunctionInstanceModify", oldHiddenModify);
                uiRuntime.Host.WriteHiddenForceShow(oldHiddenForce);
                Native.Cache("UiLauncherColorMode", oldColorMode);
                Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            });

            Native.Cache("LivingModpackExported", false);
            Native.Cache("LivingModpackMode", false);
            Native.Cache("UiHiddenFunctionInstanceModify", false);
            uiRuntime.Host.WriteHiddenForceShow(false);
            int previousAnimation = ModAnimation.AniControlEnabled;
            restore.Push(() => ModAnimation.AniControlEnabled = previousAnimation);
            ModAnimation.AniControlEnabled++;
        }

        internal void RunAdmission()
        {
            string root = Path.Combine(files.Root, "sidebar-admission");
            ModMinecraft.McInstance Import(string leaf) {
                string source = Path.Combine(root, leaf);
                string game = Path.Combine(source, "minecraft");
                Directory.CreateDirectory(game);
                string imported = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, source, game)!;
                return new ModMinecraft.McInstance(imported).Load();
            }
            var first = Import("first");
            var second = Import("second");
            Native.Check(first.Config.ContextStamp.InstanceId != second.Config.ContextStamp.InstanceId,
                "Identity replacement fixture must use two independently imported identities.");
            files.SelectMinecraft(Path.GetDirectoryName(Path.GetDirectoryName(first.PathVersion))!);
            PageInstanceLeft.Instance = first;
            var left = new PageInstanceLeft();
            ModMain.FrmInstanceLeft = left;
            var exportItem = (MyListItem)left.FindName("ItemExport");
            Native.Check(exportItem.Buttons.Single() is not null, "Export refresh button missing.");
            var controller = (Controller)Native.Property(typeof(PageInstanceLeft), "Controller").GetValue(left)!;
            var host = (Host)Native.Get(controller, "host")!;
            host.HiddenRefresh = () => { };
            host.Error = (error, text) => throw new InvalidOperationException(text, error);
            var main = FileTaskShell.Install(); // Actual Main private host/view factories; no product startup.
            var firstTool = (PageInstanceMmcTool)left.PageGet(FormMain.PageSubType.InstanceClone);
            controller.SyncSelection(FormMain.PageSubType.InstanceClone);
            FileTaskShell.Set(main, "PanMainLeft", new Border { Child = left });
            FileTaskShell.Set(main, "PanMainRight", new Border { Child = firstTool });
            main.PageLeft = left;
            main.PageRight = firstTool;
            main.PageCurrent = FormMain.PageType.InstanceSetup;
            PageInstanceLeft.Instance = second;
            SyncContext(left);
            var secondTool = (PageInstanceMmcTool)main.PageRight;
            Native.Check(!ReferenceEquals(firstTool, secondTool) && ReferenceEquals(GetCapturedInstance(secondTool), second) &&
                ReferenceEquals(((Border)Native.Field(typeof(FormMain), "PanMainRight").GetValue(main)!).Child, secondTool) && left.PageID == FormMain.PageSubType.InstanceClone &&
                ReferenceEquals(left.PageGet(FormMain.PageSubType.InstanceClone), secondTool),
                "Actual Main right pane retained another MMC owner on the same selected route.");
            Native.Check(((TextBlock)Native.Field(typeof(FormMain), "LabTitleInner").GetValue(main)!).Text.StartsWith("实例设置 - ", StringComparison.Ordinal),
                "Genuine Main title host did not use MMC instance wording.");

            string secondRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second.PathVersion));
            string projection = Path.Combine(secondRoot, "managed.json");
            byte[] originalProjection = File.ReadAllBytes(projection);
            File.AppendAllText(projection, " ");
            SyncContext(left);
            Native.Check(!Part<MyButton>(secondTool, "BtnPrimaryAction").IsEnabled &&
                Part<MyButton>(secondTool, "BtnRepairInstance").Visibility == Visibility.Visible,
                "Displayed MMC page did not immediately enter repair state.");
            File.WriteAllBytes(projection, originalProjection);
            SyncContext(left);
            Native.Check(Part<MyButton>(secondTool, "BtnPrimaryAction").IsEnabled, "Displayed page did not recover its same-identity view.");

            var componentTool = (PageInstanceMmcComponents)left.PageGet(FormMain.PageSubType.InstanceModify);
            string config = Path.Combine(secondRoot, "PCL"), heldConfig = Path.Combine(secondRoot, "PCL-retained");
            Directory.Move(config, heldConfig);
            File.Move(projection, projection + ".retained");
            try {
                SyncContext(left);
                Native.Check(main.PageRight is PageInstanceMmcTool && !Part<MyButton>(secondTool, "BtnPrimaryAction").IsEnabled &&
                    ReferenceEquals(left.PageGet(FormMain.PageSubType.InstanceModify), componentTool) &&
                    !Part<MyExtraTextButton>(componentTool, "BtnPreviewChanges").IsEnabled,
                    "Missing management records downgraded a known MMC owner or left cached actions enabled.");
                Native.Check(Part<MyListItem>(left, "ItemMmcMove").Visibility == Visibility.Visible &&
                    !Part<MyExtraTextButton>(componentTool, "BtnPreviewChanges").IsEnabled, "Unavailable MMC owner escaped protected navigation.");
            } finally { Directory.Move(heldConfig, config); File.Move(projection + ".retained", projection); }
            SyncContext(left);

            string retainedRoot = secondRoot + "-retained";
            Directory.Move(secondRoot, retainedRoot);
            try {
                CopyDirectory(first.PathVersion, secondRoot);
                Native.Check(MmcInstanceInspector.Read(secondRoot).IsConsistent, "Replacement must be another valid MMC environment.");
                int cloneCalls = 0, archiveCalls = 0;
                var register = Native.Property(Native.Type("PCL.MmcCloneUi"), "RegisterTask");
                var picker = Native.Property(Native.Type("PCL.MmcPackageUpdateUi"), "SelectArchive");
                object? oldRegister = register.GetValue(null), oldPicker = picker.GetValue(null);
                register.SetValue(null, new Action<ModLoader.LoaderCombo<string>>(task => { cloneCalls++; task.Cancel(); }));
                picker.SetValue(null, new Func<string>(() => { archiveCalls++; return ""; }));
                try {
                    secondTool.Refresh();
                    Native.Check(!Part<MyButton>(secondTool, "BtnPrimaryAction").IsEnabled && !ExecutePrimary(secondTool) && cloneCalls == 0,
                        "Refresh authorized cloning a different identity now occupying the old directory.");
                    var update = (PageInstancePackUpdate)left.PageGet(FormMain.PageSubType.InstancePackUpdate);
                    update.Refresh();
                    Native.Check(!Part<MyButton>(update, "BtnLocal").IsEnabled && archiveCalls == 0,
                        "Original update page adopted a replacement identity.");
                    File.AppendAllText(projection, " ");
                    secondTool.Refresh();
                    Native.Check(!ExecuteRepair(secondTool), "Old page repaired a replacement instance after refresh.");
                } finally { register.SetValue(null, oldRegister); picker.SetValue(null, oldPicker); }
            } finally {
                // Keep both exact fixture trees for normal fixture-owned cleanup.
                Directory.Move(secondRoot, secondRoot + "-replacement");
                Directory.Move(retainedRoot, secondRoot);
            }
        }

        internal void Run(FixtureEnvironment environment, string part)
        {
            Console.WriteLine("START mmc-sidebar fixture files");
            string root = Path.Combine(files.Root, "mmc-sidebar");
            string game = Path.Combine(root, "minecraft");
            string ordinaryRoot = Path.Combine(game, "versions", "ordinary-1.20.1");
            Directory.CreateDirectory(ordinaryRoot);
            Directory.CreateDirectory(Path.Combine(ordinaryRoot, "mods"));
            File.WriteAllText(Path.Combine(ordinaryRoot, "ordinary-1.20.1.json"),
                """{"id":"ordinary-1.20.1","mainClass":"net.minecraft.client.main.Main","libraries":[]}""", new UTF8Encoding(false));
            files.SelectMinecraft(game);

            Console.WriteLine("START mmc-sidebar fixture import");
            string managedRoot = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            Console.WriteLine("START mmc-sidebar fixture load");
            var mmcInstance = new ModMinecraft.McInstance(managedRoot).Load();
            var ordinaryInstance = new ModMinecraft.McInstance(ordinaryRoot);
            FileTaskScope.CacheInstance(ordinaryInstance);
            Native.Check(MmcInstanceInspector.Read(managedRoot).IsConsistent, "Fixture MMC instance is not consistent.");
            Native.Check(MmcInstanceInspector.Read(ordinaryRoot).IsLegacy, "Ordinary fixture instance was misclassified.");

            void Step(string name, Action action) {
                Console.WriteLine("START mmc-sidebar " + name);
                action();
                Console.WriteLine("DONE mmc-sidebar " + name);
            }
            Step("overview", () => CheckOverviewButtonsRemoved(mmcInstance, ordinaryInstance));
            Console.WriteLine("START mmc-sidebar native window");
            var (left, controller, host, window, hostGrid) = CreateSidebarWindow(mmcInstance);
            if (part == "routes") {
                Step("title", () => CheckTitleRouting(mmcInstance, ordinaryInstance));
                Step("structure", () => CheckManagedSidebarStructure(left, controller));
                Step("visibility", () => CheckHiddenModifyDoesNotHideMmcComponents(left));
                Step("routes", () => CheckAllMmcRoutesAndRefresh(left, controller, host, mmcInstance));
                Step("dispatch", () => CheckActionDispatchAndValidation(left, mmcInstance));
                Step("modpack mode", () => CheckModpackModeCoexistence(left, controller, mmcInstance, ordinaryInstance));
            }
            if (part == "states") {
                Step("family isolation", () => CheckCrossFamilyIsolationAndStaleOwner(left, controller, mmcInstance, ordinaryInstance, root, game));
                Step("protected states", () => CheckNonConsistentMmcStates(left, controller, root, game));
            }

            string? previewDir = Environment.GetEnvironmentVariable("PCLLF_MMC_SIDEBAR_PREVIEW_DIR");
            if (part == "visuals")
            {
                previewDir = string.IsNullOrWhiteSpace(previewDir) ? Path.Combine(root, "previews") : previewDir;
                PageInstanceLeft.Instance = mmcInstance;
                SyncContext(left);
                controller.SyncSelection(FormMain.PageSubType.InstanceModify);
                RenderPreviews(left, mmcInstance, root, game, previewDir, window, hostGrid);
            }
        }

        private static void CheckOverviewButtonsRemoved(ModMinecraft.McInstance mmcInstance, ModMinecraft.McInstance ordinaryInstance)
        {
            string[] removedNames = ["BtnMmcExport", "BtnMmcHistory", "BtnMmcMove", "BtnMmcPackageUpdate", "BtnMmcComponents", "BtnMmcPlatformOrigin"];
            PageInstanceLeft.Instance = mmcInstance;
            var mmcOverview = new PageInstanceOverall();
            Native.Call(mmcOverview.GetType(), "RefreshMmcActions", mmcOverview);
            foreach (string name in removedNames)
                Native.Check(mmcOverview.FindName(name) is null, "Duplicate MMC overview button still exists on managed overview: " + name);
            Native.Check(((FrameworkElement)mmcOverview.FindName("BtnMmcLegacyAdoption")).Visibility == Visibility.Collapsed,
                "Managed MMC instance exposed legacy adoption button.");

            PageInstanceLeft.Instance = ordinaryInstance;
            var ordinaryOverview = new PageInstanceOverall();
            Native.Call(ordinaryOverview.GetType(), "RefreshMmcActions", ordinaryOverview);
            foreach (string name in removedNames)
                Native.Check(ordinaryOverview.FindName(name) is null, "Duplicate MMC overview button still exists on ordinary overview: " + name);
            Native.Check(((FrameworkElement)ordinaryOverview.FindName("BtnMmcLegacyAdoption")).Visibility == Visibility.Visible,
                "Ordinary instance lost explicit legacy adoption entry.");
        }

        private (PageInstanceLeft Left, Controller Controller, Host Host, Window Window, Grid HostGrid) CreateSidebarWindow(ModMinecraft.McInstance initial)
        {
            PageInstanceLeft.Instance = initial;
            var left = new PageInstanceLeft();
            ModMain.FrmInstanceLeft = left;
            var controller = (Controller)Native.Property(left.GetType(), "Controller").GetValue(left)!;
            var host = (Host)Native.Get(controller, "host")!;
            host.HiddenRefresh = () => { };
            host.Error = (error, text) => throw new InvalidOperationException(text, error);
            var grid = new Grid();
            grid.SetResourceReference(Panel.BackgroundProperty, "ColorBrushWhite");
            grid.Children.Add(left);
            var window = new Window
            {
                Width = 240,
                Height = 680,
                SizeToContent = SizeToContent.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = grid
            };
            windows.Add(window);
            window.Show();
            VisualAssetChecks.Pump(() => left.IsLoaded, "Real MMC PageInstanceLeft did not load.");
            return (left, controller, host, window, grid);
        }

        private static void CheckTitleRouting(ModMinecraft.McInstance mmcInstance, ModMinecraft.McInstance ordinaryInstance)
        {
            ModMinecraft.McInstance? current = mmcInstance;
            string observedTitle = "";
            var mainView = new LegacyMainWindowView
            {
                LabTitleInner = () => new TextBlock { Text = observedTitle }
            };
            var windowHost = new LegacyMainWindowHost<FormMain.PageStackData, ModMinecraft.McInstance, ModAnimation.AniData>
            {
                SettingsInstance = () => current,
                InstanceName = value => value.Name,
                InstanceSettingsTitlePrefix = value => InstanceSidebarContext.Observe(value?.PathVersion).SettingsTitlePrefix,
                HelpTitle = _ => "help"
            };
            var windowController = new LegacyMainWindowController<FormMain.PageStackData, ModMinecraft.McInstance, ModAnimation.AniData>(mainView, windowHost);
            var stack = new FormMain.PageStackData { Page = FormMain.PageType.InstanceSetup };

            Native.Check(windowController.PageNameGet(stack) == "实例设置 - " + mmcInstance.Name,
                "MMC window title prefix mismatch: " + windowController.PageNameGet(stack));
            current = ordinaryInstance;
            Native.Check(windowController.PageNameGet(stack) == "版本设置 - " + ordinaryInstance.Name,
                "Ordinary window title prefix mismatch: " + windowController.PageNameGet(stack));
            current = null;
            Native.Check(windowController.PageNameGet(stack) == "版本设置 - 未知版本",
                "Null window title fallback mismatch: " + windowController.PageNameGet(stack));
        }

        private static void CheckManagedSidebarStructure(PageInstanceLeft left, Controller controller)
        {
            Native.Check(Part<TextBlock>(left, "LabVersionConfiguration").Text == "实例配置", "MMC config header mismatch.");
            Native.Check(Part<TextBlock>(left, "LabVersionTools").Text == "MMC 实例工具", "MMC tools header mismatch.");
            Native.Check(Part<MyListItem>(left, "ItemModify").Title == "组件与补丁", "MMC tag 2 title mismatch.");
            Native.Check(Part<MyListItem>(left, "ItemModify").Logo == LegacyBaseIcons.IconButtonList, "MMC tag 2 icon mismatch.");
            Native.Check(Part<MyListItem>(left, "ItemModify").Visibility == Visibility.Visible, "MMC components item hidden.");
            Native.Check(Part<MyListItem>(left, "ItemExport").Visibility == Visibility.Visible, "MMC export item hidden.");
            Native.Check(Part<MyListItem>(left, "ItemRestorePoints").Title == "环境记录", "MMC tag 12 title mismatch.");
            Native.Check(Part<MyListItem>(left, "ItemPackUpdate").Title == "整合包更新", "MMC tag 15 title mismatch.");
            Native.Check(Part<MyListItem>(left, "ItemClone").Title == "克隆", "MMC tag 14 title mismatch.");

            var move = Part<MyListItem>(left, "ItemMmcMove");
            Native.Check(move.Title == "移动实例" && move.Visibility == Visibility.Visible && move.Logo == LegacyBaseIcons.IconButtonOpen,
                "MMC move sidebar entry mismatch.");
            Native.Check(left.FindName("ItemMmcPlatformOrigin") is null, "Merged platform source left a duplicate sidebar entry.");

            var panel = Part<StackPanel>(left, "PanItem");
            string[] expectedOrder = ["0", "1", "2", "11", "16", "15", "5", "12", "14", "18", "6", "7", "3", "4", "8", "9", "10"];
            Native.Check(panel.Children.OfType<MyListItem>().Select(item => item.Tag.ToString()).SequenceEqual(expectedOrder),
                "MMC sidebar flat PanItem order changed.");
            Native.Check(ReferenceEquals(controller.NavigationItem(FormMain.PageSubType.InstanceMmcMove), move), "Route 18 did not resolve ItemMmcMove.");
            Native.Check(ReferenceEquals(controller.NavigationItem(FormMain.PageSubType.InstanceMmcPlatformOrigin), Part<MyListItem>(left, "ItemPackUpdate")), "Route 17 did not resolve ItemMmcPlatformOrigin.");
        }

        private static void CheckHiddenModifyDoesNotHideMmcComponents(PageInstanceLeft left)
        {
            Native.Cache("UiHiddenFunctionInstanceModify", true);
            ((LegacyPageSetupUIRuntime)Native.Field(Native.Type("PCL.SetupPageRuntime"), "UI").GetValue(null)!).Host.WriteHiddenForceShow(false);
            try
            {
                // Replicating what HiddenRefresh does: ordinary flag hides ItemModify, then ModModpackMode/Sync restores MMC.
                Part<MyListItem>(left, "ItemModify").Visibility = Visibility.Collapsed;
                Native.Static("PCL.ModModpackMode", "ApplyInstanceVisibility");
                Native.Check(Part<MyListItem>(left, "ItemModify").Visibility == Visibility.Visible,
                    "UiHiddenFunctionInstanceModify hid MMC 组件与补丁.");
            }
            finally
            {
                Native.Cache("UiHiddenFunctionInstanceModify", false);
                SyncContext(left);
            }
        }

        private static void CheckAllMmcRoutesAndRefresh(PageInstanceLeft left, Controller controller, Host host, ModMinecraft.McInstance mmcInstance)
        {
            foreach (int code in new[] { 2, 5, 12, 14, 15, 18, 17 }) {
                var route = (FormMain.PageSubType)code;
                var page = (MyPageRight)left.PageGet(route);
                Type expected = code switch {
                    2 => typeof(PageInstanceMmcComponents), 5 => typeof(PageInstanceExport),
                    12 => typeof(PageInstanceRestorePoints), 15 or 17 => typeof(PageInstancePackUpdate),
                    _ => typeof(PageInstanceMmcTool)
                };
                Native.Check(page.GetType() == expected, "MMC route did not reuse its actual page: " + code);
                controller.SyncSelection(route);
                controller.Refresh(route);
                Native.Check(ReferenceEquals(left.PageGet(route), page), "Refresh replaced its owner-bound page: " + code);
            }
            Native.Check(ReferenceEquals(left.PageGet(FormMain.PageSubType.InstanceMmcPlatformOrigin), left.PageGet(FormMain.PageSubType.InstancePackUpdate)),
                "Legacy platform route did not converge on the existing update page.");
            Native.Check(ModMain.FrmDownloadModify is null, "MMC navigation instantiated ordinary Forge/Fabric modification.");
        }

        private void CheckActionDispatchAndValidation(PageInstanceLeft left, ModMinecraft.McInstance mmcInstance)
        {
            // 2. Tag 14 inline clone validation rejects blank name and valid input starts + cancels cleanly
            var cloneTool = (PageInstanceMmcTool)left.PageGet(FormMain.PageSubType.InstanceClone);
            var textName = (MyTextBox)cloneTool.FindName("TextCloneName");
            var validation = (TextBlock)cloneTool.FindName("LabCloneValidation");
            var propRegister = Native.Property(Native.Type("PCL.MmcCloneUi"), "RegisterTask");
            object? oldRegister = propRegister.GetValue(null);
            ModLoader.LoaderCombo<string>? cloneTask = null;
            propRegister.SetValue(null, new Action<ModLoader.LoaderCombo<string>>(task =>
            {
                cloneTask = task;
                tasks.Add(task);
                ModLoader.LoaderTaskbar.Add(task);
                task.Cancel();
            }));
            try
            {
                textName.Text = "   ";
                Native.Check(!ExecutePrimary(cloneTool) && validation.Visibility == Visibility.Visible && cloneTask is null,
                    "Empty clone name was not rejected before MmcCloneUi.");

                textName.Text = "mmc-sidebar-clone";
                ((MyCheckBox)cloneTool.FindName("CheckCopySaves")).Checked = true;
                Native.Check(ExecutePrimary(cloneTool) && cloneTask is not null, "Valid MMC clone did not start MmcCloneUi.");
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (cloneTask!.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading ||
                    (bool)Native.Property(Native.Type("PCL.MmcCloneUi"), "HasActiveOperation").GetValue(null)!)
                {
                    Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Cancelled MMC clone task did not drain.");
                    Thread.Sleep(5);
                }
            }
            finally
            {
                propRegister.SetValue(null, oldRegister);
                foreach (var t in tasks) ModLoader.LoaderTaskbar.Remove(t);
            }
        }

        private static void CheckModpackModeCoexistence(PageInstanceLeft left, Controller controller,
            ModMinecraft.McInstance mmcInstance, ModMinecraft.McInstance ordinaryInstance)
        {
            Native.Cache("LivingModpackExported", true);
            Native.Cache("LivingModpackMode", true);
            try
            {
                PageInstanceLeft.Instance = mmcInstance;
                SyncContext(left);
                Native.Static("PCL.ModModpackMode", "ApplyInstanceVisibility");
                Native.Check(Part<MyListItem>(left, "ItemExport").Visibility == Visibility.Visible,
                    "ModpackMode hid MMC Export entry.");
                Native.Check(NormalizeModpackPage(FormMain.PageSubType.InstanceExport) == FormMain.PageSubType.InstanceExport,
                    "ModpackMode normalized MMC Export back to Overview.");
                Native.Check(ReferenceEquals(controller.NavigationItem(FormMain.PageSubType.InstanceExport), Part<MyListItem>(left, "ItemExport")),
                    "ModpackMode broke NavigationItem resolution on MMC Export.");

                PageInstanceLeft.Instance = ordinaryInstance;
                SyncContext(left);
                Native.Static("PCL.ModModpackMode", "ApplyInstanceVisibility");
                Native.Check(Part<MyListItem>(left, "ItemExport").Visibility == Visibility.Collapsed,
                    "ModpackMode stopped hiding ordinary Export entry.");
                Native.Check(NormalizeModpackPage(FormMain.PageSubType.InstanceExport) == FormMain.PageSubType.InstanceOverall,
                    "ModpackMode stopped normalizing ordinary Export back to Overview.");
                Native.Check(Part<MyListItem>(left, "ItemMmcMove").Visibility == Visibility.Collapsed &&
                    left.FindName("ItemMmcPlatformOrigin") is null,
                    "MMC-only entries became visible on ordinary instance under ModpackMode.");
            }
            finally
            {
                Native.Cache("LivingModpackMode", false);
                Native.Cache("LivingModpackExported", false);
                PageInstanceLeft.Instance = mmcInstance;
                SyncContext(left);
            }
        }

        private static void CheckCrossFamilyIsolationAndStaleOwner(PageInstanceLeft left, Controller controller,
            ModMinecraft.McInstance mmcInstance, ModMinecraft.McInstance ordinaryInstance, string root, string game)
        {
            // 1. Capture MMC tool page, switch to MMC route 18, then switch to Ordinary instance.
            PageInstanceLeft.Instance = mmcInstance;
            SyncContext(left);
            var capturedTool15 = (PageInstanceMmcTool)left.PageGet(FormMain.PageSubType.InstanceMmcMove);
            controller.SyncSelection(FormMain.PageSubType.InstanceMmcMove);
            Native.Check(left.PageID == FormMain.PageSubType.InstanceMmcMove, "Failed to select route 18 on MMC.");

            PageInstanceLeft.Instance = ordinaryInstance;
            SyncContext(left);
            Native.Check(left.PageID == FormMain.PageSubType.InstanceOverall,
                "Switching from MMC route 18 to ordinary did not normalize selection to Overview.");
            Native.Check(Part<MyListItem>(left, "ItemMmcMove").Visibility == Visibility.Collapsed &&
                left.FindName("ItemMmcPlatformOrigin") is null &&
                Part<MyListItem>(left, "ItemModify").Title == "修改" &&
                Part<MyListItem>(left, "ItemRestorePoints").Title == "还原点" &&
                Part<TextBlock>(left, "LabVersionConfiguration").Text == "版本配置" &&
                Part<TextBlock>(left, "LabVersionTools").Text == "版本工具",
                "Switching back to ordinary left MMC labels/rows behind.");

            // Ordinary PageGet must return ordinary page types, never stale PageInstanceMmcTool.
            Native.Check(left.PageGet(FormMain.PageSubType.InstanceModify) is PageDownloadModify, "Ordinary tag 2 did not restore PageDownloadModify.");
            Native.Check(left.PageGet(FormMain.PageSubType.InstanceExport) is PageInstanceExport, "Ordinary tag 5 did not restore PageInstanceExport.");
            Native.Check(left.PageGet(FormMain.PageSubType.InstanceRestorePoints) is PageInstanceRestorePoints, "Ordinary tag 12 did not restore PageInstanceRestorePoints.");
            Native.Check(left.PageGet(FormMain.PageSubType.InstanceClone) is PageInstanceClone, "Ordinary tag 14 did not restore PageInstanceClone.");
            Native.Check(left.PageGet(FormMain.PageSubType.InstancePackUpdate) is PageInstancePackUpdate, "Ordinary tag 15 did not restore PageInstancePackUpdate.");
            Native.Check(left.PageGet(FormMain.PageSubType.InstanceMmcMove) is PageInstanceOverall, "Ordinary tag 18 leaked out of Overview fallback.");
            Native.Check(left.PageGet(FormMain.PageSubType.InstanceMmcPlatformOrigin) is PageInstanceOverall, "Ordinary tag 17 leaked out of Overview fallback.");

            // Stale captured MMC page MUST refuse to act when owner has changed to Ordinary (or another MMC instance).
            var propArchive = Native.Property(Native.Type("PCL.MmcPackageUpdateUi"), "SelectArchive");
            object? oldArchive = propArchive.GetValue(null);
            propArchive.SetValue(null, new Func<string>(() => ""));
            try
            {
                Native.Check(!ExecutePrimary(capturedTool15), "Stale captured MMC page executed after owner changed to ordinary.");
            }
            finally
            {
                propArchive.SetValue(null, oldArchive);
            }

            // Switch back MMC -> stale ordinary mutator singletons MUST NOT survive; new MMC page opens cleanly.
            PageInstanceLeft.Instance = mmcInstance;
            SyncContext(left);
            var newTool2 = left.PageGet(FormMain.PageSubType.InstanceModify);
            Native.Check(newTool2 is PageInstanceMmcComponents && ModMain.FrmDownloadModify is null && ModMain.FrmInstanceExport is null,
                "Switching Ordinary -> MMC did not clear ordinary mutator caches or leaked wrong page.");

            // Clone a second MMC copy on disk so we can test tampered definition hash without breaking primary fixture.
            string secondRoot = Path.Combine(root, "second-game", "versions", "managed");
            CopyDirectory(mmcInstance.PathVersion, secondRoot);
            Native.Check(MmcInstanceInspector.Read(secondRoot).IsConsistent, "Hash-refusal fixture must be consistent before tampering.");
            var secondInstance = new ModMinecraft.McInstance(secondRoot).Load();
            PageInstanceLeft.Instance = secondInstance;
            SyncContext(left);
            var secondTool15 = (PageInstanceMmcTool)left.PageGet(FormMain.PageSubType.InstanceClone);
            Native.Check(Part<MyButton>(secondTool15, "BtnPrimaryAction").IsEnabled, "Hash-refusal fixture was already blocked before tampering.");
            string defFile = Path.Combine(secondRoot, "PCL", "InstanceDefinition.json");
            byte[] originalBytes = File.ReadAllBytes(defFile);
            propArchive.SetValue(null, new Func<string>(() => ""));
            try
            {
                File.WriteAllText(defFile, File.ReadAllText(defFile, Encoding.UTF8) + " ", new UTF8Encoding(false));
                Native.Check(!ExecutePrimary(secondTool15),
                    "Action executed despite primary definition hash changing after page capture.");
            }
            finally
            {
                propArchive.SetValue(null, oldArchive);
                File.WriteAllBytes(defFile, originalBytes);
            }
        }

        private static void CheckNonConsistentMmcStates(PageInstanceLeft left, Controller controller, string root, string game)
        {
            string sourceManaged = Path.Combine(game, "versions", "managed");
            foreach (string state in new[] { "damaged", "pending", "unsupported" }) {
                string target = Path.Combine(root, state, "versions", "managed");
                CopyDirectory(sourceManaged, target);
                if (state == "damaged") File.AppendAllText(Path.Combine(target, "managed.json"), " ");
                if (state == "pending") Directory.CreateDirectory(Path.Combine(target, "PCL", "MmcTransactions", "unrecognized-journal"));
                if (state == "unsupported") File.WriteAllText(Path.Combine(target, "PCL", "InstanceDefinition.json"),
                    """{"schemaVersion":99,"managementKind":"Mmc","formatProfile":"prism-v1"}""");
                PageInstanceLeft.Instance = new ModMinecraft.McInstance(target).Load();
                SyncContext(left);
                Native.Check(Part<MyListItem>(left, "ItemMmcMove").Visibility == Visibility.Visible, "Invalid MMC fell back to ordinary navigation.");
                foreach (int code in new[] { 2, 5, 12, 15 }) {
                    var page = (FrameworkElement)left.PageGet((FormMain.PageSubType)code);
                    string action = code switch { 2 => "BtnPreviewChanges", 5 => "BtnExport", 12 => "BtnCreate", _ => "BtnLocal" };
                    Native.Check(page.FindName(action) is FrameworkElement button && !button.IsEnabled,
                        "Restricted MMC page enabled its actual action: " + state + "/" + code);
                }
            }
        }

        private static void RenderPreviews(PageInstanceLeft left, ModMinecraft.McInstance mmcInstance, string root, string game,
            string previewDir, Window sidebarWindow, Grid sidebarHost)
        {
            Directory.CreateDirectory(previewDir);

            // Render MMC sidebar light & dark
            foreach (int mode in new[] { 0, 1 })
            {
                Native.Cache("UiLauncherColorMode", mode);
                Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
                sidebarWindow.Height = 680;
                Part<MyScrollViewer>(left, "ScrollSidebar").ScrollToTop();
                sidebarWindow.UpdateLayout();
                VisualAssetChecks.Drain();
                SaveVisual(sidebarHost, Path.Combine(previewDir, mode == 0 ? "mmc-sidebar-light.png" : "mmc-sidebar-dark.png"));
                sidebarWindow.Height = 360;
                sidebarWindow.UpdateLayout();
                var scroll = Part<MyScrollViewer>(left, "ScrollSidebar");
                scroll.ScrollToEnd();
                VisualAssetChecks.Pump(() => scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                    "MMC sidebar could not scroll to its final resource item.");
                var last = Part<MyListItem>(left, "ItemSchematics");
                Rect bounds = new(last.TranslatePoint(new Point(), left), last.RenderSize);
                Native.Check(bounds.Top >= 0 && bounds.Bottom <= left.ActualHeight + 1 && bounds.Right <= left.ActualWidth + 1,
                    "Small MMC sidebar clipped its final item.");
                SaveVisual(sidebarHost, Path.Combine(previewDir, mode == 0 ? "mmc-sidebar-small-light-bottom.png" : "mmc-sidebar-small-dark-bottom.png"));
            }

            // Composite preview window: Header ("实例设置 - <Name>") + MMC Sidebar + PageInstanceMmcTool right pane
            RenderShellComposite(mmcInstance, FormMain.PageSubType.InstanceModify, 0, Path.Combine(previewDir, "mmc-shell-components-light.png"));
            RenderShellComposite(mmcInstance, FormMain.PageSubType.InstanceClone, 0, Path.Combine(previewDir, "mmc-shell-clone-light.png"));
            RenderShellComposite(mmcInstance, FormMain.PageSubType.InstanceModify, 1, Path.Combine(previewDir, "mmc-shell-components-dark.png"));

            // Also render damaged repair state composite
            string damagedRoot = Path.Combine(game, "versions", "managed-damaged");
            CopyDirectory(mmcInstance.PathVersion, damagedRoot);
            File.Move(Path.Combine(damagedRoot, "managed.json"), Path.Combine(damagedRoot, "managed-damaged.json"));
            File.AppendAllText(Path.Combine(damagedRoot, "managed-damaged.json"), " ");
            var damaged = new ModMinecraft.McInstance(damagedRoot).Load();
            RenderShellComposite(damaged, FormMain.PageSubType.InstanceModify, 1, Path.Combine(previewDir, "mmc-shell-repair-dark.png"));

            // Restore consistent mmcInstance
            Native.Cache("UiLauncherColorMode", 0);
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            PageInstanceLeft.Instance = mmcInstance;
            SyncContext(left);
        }

        private static void RenderShellComposite(ModMinecraft.McInstance instance, FormMain.PageSubType route, int colorMode, string outputPath)
        {
            Native.Cache("UiLauncherColorMode", colorMode);
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            PageInstanceLeft.Instance = instance;

            var left = new PageInstanceLeft();
            var controller = (Controller)Native.Property(left.GetType(), "Controller").GetValue(left)!;
            var host = (Host)Native.Get(controller, "host")!;
            host.HiddenRefresh = () => { };
            var right = (MyPageRight)left.PageGet(route);
            controller.SyncSelection(route);

            string titlePrefix = InstanceSidebarContext.Observe(instance.PathVersion).SettingsTitlePrefix;
            var titleBar = new Border
            {
                Height = 46,
                Padding = new Thickness(18, 0, 18, 0),
                Child = new TextBlock
                {
                    Text = titlePrefix + " - " + instance.Name,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ((TextBlock)titleBar.Child).SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
            titleBar.SetResourceReference(Border.BackgroundProperty, "ColorBrushWhite");

            var leftBorder = new Border { Width = 230, Child = left };
            leftBorder.SetResourceReference(Border.BackgroundProperty, "ColorBrushCard");

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(leftBorder, 0);
            Grid.SetColumn(right, 1);
            body.Children.Add(leftBorder);
            body.Children.Add(right);

            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.SetResourceReference(Panel.BackgroundProperty, "ColorBrushWhite");
            Grid.SetRow(titleBar, 0);
            Grid.SetRow(body, 1);
            shell.Children.Add(titleBar);
            shell.Children.Add(body);

            var win = new Window
            {
                Width = 980,
                Height = 680,
                SizeToContent = SizeToContent.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Content = shell
            };
            try
            {
                win.Show();
                VisualAssetChecks.Pump(() => left.IsLoaded && right.IsLoaded, "Shell composite did not load.");
                win.UpdateLayout();
                VisualAssetChecks.Drain();
                SaveVisual(shell, outputPath);
            }
            finally
            {
                win.Close();
                win.Content = null;
                VisualAssetChecks.Drain();
            }
        }

        private static void SaveVisual(FrameworkElement view, string path)
        {
            var pixels = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            pixels.Render(view);
            VisualAssetChecks.Save(pixels, path);
        }

        private static T Part<T>(FrameworkElement page, string name) where T : class =>
            page.FindName(name) as T ?? throw new MissingMemberException(page.GetType().Name, name);

        private static void SyncContext(PageInstanceLeft left) =>
            Native.Call(typeof(PageInstanceLeft), "SyncInstanceContext", left);

        private static FormMain.PageSubType GetSubRoute(PageInstanceMmcTool tool) =>
            (FormMain.PageSubType)Native.Property(typeof(PageInstanceMmcTool), "SubRoute").GetValue(tool)!;

        private static ModMinecraft.McInstance? GetCapturedInstance(PageInstanceMmcTool tool) =>
            (ModMinecraft.McInstance?)Native.Property(typeof(PageInstanceMmcTool), "CapturedInstance").GetValue(tool);

        private static bool ExecutePrimary(PageInstanceMmcTool tool) =>
            (bool)Native.Call(typeof(PageInstanceMmcTool), "ExecutePrimaryAction", tool)!;

        private static bool ExecuteRepair(PageInstanceMmcTool tool) =>
            (bool)Native.Call(typeof(PageInstanceMmcTool), "ExecuteRepairAction", tool)!;

        private static FormMain.PageSubType NormalizeModpackPage(FormMain.PageSubType page) =>
            (FormMain.PageSubType)Native.Static("PCL.ModModpackMode", "NormalizeInstancePage", page)!;

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            foreach (string dir in Directory.EnumerateDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        public void Dispose()
        {
            foreach (var win in windows)
            {
                win.Close();
                win.Content = null;
            }
            VisualAssetChecks.Drain();
            while (restore.Count > 0) restore.Pop()();
        }
    }
}
