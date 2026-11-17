using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeloongCore;
using PCL;
using PCLCS;
using PCLLF.Presentation.Foundation;
using NavigationController = PCLCS.InstanceNavigationController<PCL.ModMinecraft.McInstance, PCL.FormMain.PageSubType, PCL.MyListItem, PCL.MyIconButton, PCL.MyPageRight, PCL.PageInstanceResources>;
using NavigationHost = PCLCS.InstanceNavigationHost<PCL.ModMinecraft.McInstance, PCL.FormMain.PageSubType, PCL.MyListItem, PCL.MyIconButton, PCL.MyPageRight, PCL.PageInstanceResources>;
using SettingsController = PCLCS.InstanceSettingsController<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.MyListItem, PCL.MyIconButton>;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main BAML/controls and read-only page loaders, inside the existing owned
// file/UI fixture. Windows are placed offscreen; no product startup/account/game.
internal static class MmcReusedPageVisualChecks {
    internal static void RunLight(FixtureEnvironment environment) => Run(environment, 0);
    internal static void RunDark(FixtureEnvironment environment) => Run(environment, 1);

    private static void Run(FixtureEnvironment environment, int mode) {
        using FileTaskScope files = new(environment);
        using (var visuals = new VisualAssetChecks.Scope(environment, "mmc-reused-visual-" + mode))
        using (var scope = new Scope(files, mode)) scope.Render();
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-reused-page-visual-" + (mode == 0 ? "light" : "dark") +
            ": actual sidebar and reused export/update/history/component pages, genuine loaded records, fixed action bars and original Java controls; no product/account/network/game.");
    }

    private sealed class Scope : IDisposable {
        private static readonly FieldInfo AnimationTick = Native.Field(typeof(ModAnimation), "AniLastTick");
        private readonly FileTaskScope files;
        private readonly ModMinecraft.McInstance owner;
        private readonly Guid id;
        private readonly string output, tone;
        private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(48);
        private readonly List<FrameworkElement> pages = [];
        private readonly List<Window> windows = [];
        private readonly ModMinecraft.McInstance? previousOwner = PageInstanceLeft.Instance;
        private readonly int previousAnimation = ModAnimation.AniControlEnabled;

        internal Scope(FileTaskScope files, int mode) {
            this.files = files; tone = mode == 0 ? "light" : "dark";
            ModAnimation.AniControlEnabled++;
            Native.Cache("LivingModpackExported", false); Native.Cache("LivingModpackMode", false);
            Native.Cache("UiHiddenFunctionInstanceModify", false);
            Native.Cache("UiLauncherColorMode", mode); Native.Cache("UiBackgroundMode", 1);
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            string root = Path.Combine(files.Root, "reused-page-visual"), game = Path.Combine(root, "minecraft");
            Directory.CreateDirectory(game); files.SelectMinecraft(game);
            string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
            owner = new ModMinecraft.McInstance(source).Load(); FileTaskScope.CacheInstance(owner);
            PageInstanceLeft.Instance = owner;
            var primary = MmcInstanceInspector.Read(source);
            Native.Check(primary.IsConsistent && primary.Definition!.Origin.PlatformProjectIdentity is null,
                "Visual fixture must be a consistent unbound MMC source.");
            id = primary.Definition!.InstanceId;
            Put(source, "minecraft/mods/owned-example.jar", "inert visual fixture; never executed");
            Put(source, "minecraft/resourcepacks/owned-pack/readme.txt", "owned resource pack folder");
            Put(source, "minecraft/saves/owned-world/level.dat", "owned visual player data");
            Put(source, "minecraft/screenshots/readme.txt", "owned screenshot directory");
            var captured = new MmcInstanceHistoryBackend().CaptureAsync(new(source, id, primary.Definition.Generation,
                primary.PrimarySha256!, "界面检查：当前组件与配置")).GetAwaiter().GetResult();
            Native.Check(captured.Status == MmcInstanceBackendStatus.Succeeded, "Cannot create the genuine visual history row: " + captured.Failure);
            string? configured = Environment.GetEnvironmentVariable("PCLLF_MMC_UI_PREVIEW_DIR");
            output = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(files.Root, "previews") : configured);
            Directory.CreateDirectory(output);
        }

        private static void Put(string root, string relative, string value) {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value);
        }
        private static T Part<T>(FrameworkElement page, string name) where T : class => (T)(page.FindName(name) ?? throw new IOException("Missing original control: " + name));
        private static Task ReadTask(FrameworkElement page) => page switch {
            PageInstanceExport => (Task)Native.Property(page.GetType(), "MmcInventoryTask").GetValue(page)!,
            PageInstancePackUpdate => (Task)Native.Property(page.GetType(), "MmcReadCompletion").GetValue(page)!,
            PageInstanceMmcComponents => (Task)Native.Property(page.GetType(), "ModelTask").GetValue(page)!,
            _ => Task.CompletedTask
        };
        private void Wait(Func<bool> ready, string reason) {
            int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            Native.Check(remaining > 0, "Visual group exceeded its common deadline before " + reason);
            VisualAssetChecks.Pump(ready, reason, Math.Min(remaining, 10000));
            VisualAssetChecks.Drain();
        }
        private void Ready(FrameworkElement page) {
            if (page is PageInstanceRestorePoints) {
                object loader = Native.Get(page, "PointsLoader")!;
                Wait(() => (ModBase.LoadState)Native.Property(loader.GetType(), "State").GetValue(loader)! is ModBase.LoadState.Finished or ModBase.LoadState.Failed,
                    "actual MMC history loader");
                Native.Check((bool)Native.Get(page, "MmcReadReady")!, "The real history loader did not populate the original page.");
            } else Wait(() => ReadTask(page).IsCompleted, "actual " + page.GetType().Name + " model");
        }
        private void Stopped(FrameworkElement page) {
            if (page is PageInstanceRestorePoints) {
                object loader = Native.Get(page, "PointsLoader")!;
                Wait(() => Native.Get(loader, "LastRunningThread") is not Thread { IsAlive: true }, "history reader drain");
            } else Wait(() => ReadTask(page).IsCompleted, "page reader drain");
        }
        private T Managed<T>() where T : FrameworkElement {
            object? expected = typeof(T) == typeof(PageInstanceRestorePoints) ? (Guid?)id : id;
            var page = (T)Activator.CreateInstance(typeof(T), Native.Flags, null, [owner, expected], null)!;
            pages.Add(page); return page;
        }

        internal void Render() {
            var export = Managed<PageInstanceExport>();
            Composite(FormMain.PageSubType.InstanceExport, export, "mmc-export", () => {
                Native.Check(Part<MyExtraTextButton>(export, "BtnExport").IsEnabled, "Export screenshot contains a blocked/unloaded page.");
                VisibleAction(export, Part<MyExtraTextButton>(export, "BtnExport"));
            });
            var update = Managed<PageInstancePackUpdate>();
            Composite(FormMain.PageSubType.InstancePackUpdate, update, "mmc-pack-update-unbound", () => {
                Native.Check(!Part<MyButton>(update, "BtnOnline").IsEnabled && Part<MyButton>(update, "BtnLocal").IsEnabled &&
                    Part<TextBlock>(update, "LabSource").Text.Contains("未核实", StringComparison.Ordinal),
                    "Unbound update screenshot must use the genuine local-source state without an online catalog.");
                VisibleAction(update, Part<MyButton>(update, "BtnLocal"));
            });
            var history = Managed<PageInstanceRestorePoints>();
            Composite(FormMain.PageSubType.InstanceRestorePoints, history, "mmc-history", () => {
                var rows = Part<StackPanel>(history, "PanEntries");
                Native.Check(rows.Children.Count == 1, "History screenshot lacks the backend-created original row.");
                ((MyLocalModItem)rows.Children[0]).Checked = true;
                history.UpdateLayout(); VisualAssetChecks.Drain();
                VisibleAction(history, Part<MyCard>(history, "CardSelect"));
            });
            var components = Managed<PageInstanceMmcComponents>();
            Composite(FormMain.PageSubType.InstanceModify, components, "mmc-components", () => {
                Native.Check(components.FindName("MmcComponentEditor") is FrameworkElement, "Component screenshot contains no real editor.");
                VisibleAction(components, Part<MyExtraTextButton>(components, "BtnPreviewChanges"));
            }, () => {
                var editor = Part<FrameworkElement>(components, "MmcComponentEditor");
                Native.Call(editor.GetType(), "SelectComponent", editor, "net.minecraft");
                Type kind = editor.GetType().GetNestedType("EditKind", BindingFlags.NonPublic)!;
                Native.Set(editor, "kind", Enum.Parse(kind, "Patch")); Native.Call(editor.GetType(), "Show", editor, 1);
            }, "mmc-components-patch");
            JavaFragment();
            Console.WriteLine("MMC visual previews: " + output + " (" + tone + ")");
        }

        private void Composite(FormMain.PageSubType route, FrameworkElement right, string name, Action? validate = null,
            Action? alternate = null, string? alternateName = null) {
            PageInstanceLeft.Instance = owner;
            var left = new PageInstanceLeft(); ModMain.FrmInstanceLeft = left;
            var navigation = (NavigationController)Native.Property(typeof(PageInstanceLeft), "Controller").GetValue(left)!;
            var host = (NavigationHost)Native.Get(navigation, "host")!;
            host.HiddenRefresh = () => { }; host.Error = (error, text) => throw new IOException(text, error);
            navigation.SyncSelection(route);
            var sidebar = new Border { Child = left, Width = 230, Margin = new Thickness(8, 8, 0, 8), CornerRadius = new CornerRadius(6) };
            sidebar.SetResourceReference(Border.BackgroundProperty, "ColorBrushCard");
            var shell = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
            shell.SetResourceReference(Panel.BackgroundProperty, LegacyThemePalette.IsDarkTheme ? "ColorBrushBackground" : "ColorBrushWhite");
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(right, 1); shell.Children.Add(sidebar); shell.Children.Add(right);
            var window = new Window { Content = shell, Width = 1120, Height = 760, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            windows.Add(window);
            try {
                window.Show(); Wait(() => left.IsLoaded && right.IsLoaded, "offscreen native pages loaded");
                Ready(right);
                if (right is MyPageRight nativePage) {
                    if (nativePage.PageState == MyPageRight.PageStates.Empty) nativePage.PageOnEnter();
                    var extras = nativePage.GetAllAnimControls((FrameworkElement)nativePage.Child, true).OfType<MyExtraTextButton>().ToArray();
                    Wait(() => {
                        // The fixture intentionally does not start the product's foreground
                        // animation thread. Advance the genuine LF engine just as the native
                        // control checks do; never overwrite page state, opacity or transforms.
                        AnimationTick.SetValue(null, long.MaxValue); ModAnimation.AniTimer(16d);
                        return nativePage.PageState == MyPageRight.PageStates.ContentStay &&
                            !ModAnimation.AniIsRun("PageRight PageChange " + nativePage.PageUuid) &&
                            extras.All(extra => !ModAnimation.AniIsRun("MyExtraTextButton MainScale " + extra.Uuid));
                    }, "native page and independent action animations settled");
                }
                window.UpdateLayout(); VisualAssetChecks.Drain();
                // Keep the real pixels even when a geometric assertion fails, so an
                // offscreen-host or animation error cannot be mistaken for product clipping.
                Save(shell, name);
                validate?.Invoke();
                window.UpdateLayout(); Save(shell, name);
                if (alternate is not null) { alternate(); window.UpdateLayout(); VisualAssetChecks.Drain(); Save(shell, alternateName!); }
            } catch (Exception error) {
                Console.Error.WriteLine("MMC visual failure: " + name + "/" + tone + "; " + error);
                Console.Error.WriteLine("MMC visual page: " + ViewFacts(right));
                if (shell.ActualWidth > 0 && shell.ActualHeight > 0) {
                    try { window.UpdateLayout(); Save(shell, name + "-failed"); }
                    catch (Exception captureError) { Console.Error.WriteLine("MMC failure snapshot also failed: " + captureError); }
                }
                throw;
            } finally {
                window.Close(); window.Content = null;
                sidebar.Child = null; shell.Children.Clear(); VisualAssetChecks.Drain(); Stopped(right);
            }
        }

        private void JavaFragment() {
            // A declared but absent fixture Java path gives a real, bounded rejection
            // and the real long MMC declaration. No java.exe/process is supplied.
            var declared = new Java(Path.Combine(files.Root, "explicit-manual-java", "jdk-8-fixture", "bin"), new Version(8, 0, 491));
            Native.Static("PCL.MmcLaunchPreferences", "SetForcedJava", owner, declared);
            owner.Config.Save();
            PageInstanceLeft.Instance = owner;
            Native.Static("PCL.MmcJavaDiscovery", "TrySelect", owner, CancellationToken.None, null, null);
            var settings = new PageInstanceSetup(); ModMain.FrmInstanceSetup = settings;
            var controller = (SettingsController)Native.Property(typeof(PageInstanceSetup), "Controller").GetValue(settings)!;
            var select = Part<FrameworkElement>(settings, "PanArgumentJavaSelect");
            var grid = (Grid)select.Parent;
            // Preserve the original Java rows and controls. Hide only the preceding
            // unrelated launch-option rows when extracting this native settings fragment.
            foreach (UIElement child in grid.Children) if (Grid.GetRow(child) < 6) child.Visibility = Visibility.Collapsed;
            for (int row = 0; row < 6; row++) grid.RowDefinitions[row].Height = new GridLength(0);
            var card = Part<MyCard>(settings, "CardArgument"); ((Panel)card.Parent).Children.Remove(card);
            var content = new StackPanel { Margin = new Thickness(25) }; content.Children.Add(card);
            var fragment = new MyScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Part<ComboBox>(settings, "ComboArgumentJava").SelectedIndex = 3;
            controller.UpdateJavaUi();
            Composite(FormMain.PageSubType.InstanceSetup, fragment, "mmc-java-manual", () => {
                controller.UpdateJavaList(); controller.UpdateJavaUi(); fragment.UpdateLayout(); VisualAssetChecks.Drain();
                var hint = Part<MyHint>(settings, "HintArgumentJava");
                var selectionBounds = new Rect(select.TranslatePoint(new Point(), grid), select.RenderSize);
                var hintBounds = new Rect(hint.TranslatePoint(new Point(), grid), hint.RenderSize);
                Native.Check(select.Visibility == Visibility.Visible && hint.Visibility == Visibility.Visible && hint.ActualHeight > 28 &&
                    hint.Text.Contains("整合包声明的候选主版本", StringComparison.Ordinal) &&
                    !selectionBounds.IntersectsWith(hintBounds) && hintBounds.Top >= selectionBounds.Bottom,
                    "The actual manual Java selector overlaps the real multiline MMC declaration.");
            });
        }

        private static void VisibleAction(FrameworkElement page, FrameworkElement action) {
            var bounds = new Rect(action.TranslatePoint(new Point(), page), action.RenderSize);
            var transformed = action.TransformToAncestor(page).TransformBounds(new Rect(action.RenderSize));
            Console.WriteLine("MMC visual action bounds: page=" + page.GetType().Name + "; action=" + action.Name +
                "; origin-plus-unscaled-size=" + bounds + "; transformed-render-bounds=" + transformed +
                "; page-size=" + page.RenderSize + "; page-state=" + (page is MyPageRight right ? right.PageState.ToString() : "fragment") +
                "; active-animation-groups=" + string.Join(",", ModAnimation.AniGroups.Keys));
            for (DependencyObject? current = action; current is FrameworkElement element; current = VisualTreeHelper.GetParent(current)) {
                Console.WriteLine("MMC visual action ancestor: " + ViewFacts(element));
                if (ReferenceEquals(current, page)) break;
            }
            Native.Check(action.IsVisible && action.ActualHeight > 0 && bounds.Left >= 0 && bounds.Top >= 0 &&
                bounds.Right <= page.ActualWidth + 1 && bounds.Bottom <= page.ActualHeight + 1 && action.Opacity >= .99 &&
                transformed.Width > 0 && transformed.Height > 0 && transformed.Left >= 0 && transformed.Top >= 0 &&
                transformed.Right <= page.ActualWidth + 1 && transformed.Bottom <= page.ActualHeight + 1,
                "An original page action bar is clipped outside the visible page: action=" + bounds + "; transformed=" + transformed + "; page=" + page.RenderSize);
        }
        private static string ViewFacts(FrameworkElement element) => element.GetType().Name + "/" + element.Name +
            "; actual=" + element.RenderSize + "; desired=" + element.DesiredSize + "; margin=" + element.Margin +
            "; visibility=" + element.Visibility + "; isVisible=" + element.IsVisible + "; opacity=" + element.Opacity +
            "; clipToBounds=" + element.ClipToBounds + "; explicitClip=" + (element.Clip?.Bounds.ToString() ?? "none") +
            "; visualClip=" + (VisualTreeHelper.GetClip(element)?.Bounds.ToString() ?? "none") +
            "; layoutClip=" + (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element)?.Bounds.ToString() ?? "none") +
            "; layoutSlot=" + System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element) +
            "; renderTransform=" + (element.RenderTransform?.Value.ToString() ?? "none") +
            "; layoutTransform=" + (element.LayoutTransform?.Value.ToString() ?? "none");
        private void Save(FrameworkElement element, string name) {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            string file = Path.Combine(output, name + "-" + tone + ".png"); VisualAssetChecks.Save(bitmap, file);
            Native.Check(new FileInfo(file).Length > 1000, "Actual page snapshot is empty: " + file);
        }
        public void Dispose() {
            foreach (var window in windows) { window.Close(); window.Content = null; }
            foreach (var page in pages) { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Stopped(page); }
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
            InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
            PageInstanceLeft.Instance = previousOwner; ModAnimation.AniControlEnabled = previousAnimation;
            VisualAssetChecks.Drain();
        }
    }
}
