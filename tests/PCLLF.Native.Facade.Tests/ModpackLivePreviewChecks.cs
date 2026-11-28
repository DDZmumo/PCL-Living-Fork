using System.Collections;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using static PCLLF.Native.Facade.Tests.ModpackUpdateChecks;
using PreviewPackage = PCLCS.PackUpdatePackageData<PCLCS.PackUpdateFileData>;
using PreviewChange = PCLCS.PackUpdateChangeData<PCLCS.PackUpdateFileData>;
using PreviewPlan = PCLCS.PackUpdatePlanData<PCLCS.PackUpdatePackageData<PCLCS.PackUpdateFileData>, PCLCS.PackUpdateChangeData<PCLCS.PackUpdateFileData>>;

namespace PCLLF.Native.Facade.Tests;

internal static class ModpackLivePreviewChecks
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        DrainPendingReader(root);
        string game = Path.Combine(root, "game"), stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(Path.Combine(game, "mods")); Directory.CreateDirectory(stage);
        object Package(string name, string minecraft)
        {
            object package = New("PackUpdatePackage"); Set(package, "Name", "Preview compatibility"); Set(package, "Version", name); Set(package, "Minecraft", minecraft);
            ((IDictionary)Get(package, "Loaders")!).Add(ModLoaders.Fabric, "0.20.1"); return package;
        }
        int next = 0;
        object Jar(object package, string relative, string id, string version, JObject? requirements = null)
        {
            string file = Path.Combine(stage, "input-" + (++next) + ".jar");
            using (ZipArchive zip = ZipFile.Open(file, ZipArchiveMode.Create)) {
                ZipText(zip, "fabric.mod.json", new JObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = version,
                    ["icon"] = "icon.png", ["depends"] = requirements ?? new JObject { ["minecraft"] = "*", ["fabricloader"] = ">=0.16.0" } }.ToString());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 160, 64, 255, 255, 160, 64, 255 }, 8)));
                using var bytes = new MemoryStream(); encoder.Save(bytes); bytes.Position = 0;
                using var icon = zip.CreateEntry("icon.png").Open(); bytes.CopyTo(icon);
            }
            object data = New("PackUpdateFile"); Set(data, "Relative", relative); Set(data, "Hash", Digest(file)); Set(data, "Payload", file); Set(data, "Identity", "modrinth:" + id);
            ((IDictionary)Get(package, "Files")!).Add(relative, data); return data;
        }
        object original = Package("1", "1.21.1"), incoming = Package("2", "26.2");
        object legacy = Jar(original, "mods/legacy-adapter.jar", "legacy_adapter", "1", new JObject { ["minecraft"] = "1.21.1" });
        Jar(original, "mods/replaceable.jar", "replaceable", "1");
        object provider = Jar(original, "mods/provider-old.jar", "provider", "1");
        foreach (var pair in new[] { (legacy, "mods/legacy-adapter.jar"), (provider, "mods/provider-old.jar.disabled") })
            File.Copy(Text(pair.Item1, "Payload")!, Path.Combine(game, pair.Item2.Replace('/', Path.DirectorySeparatorChar)));
        ((IDictionary)Get(incoming, "Files")!).Add("mods/legacy-adapter.jar", legacy);
        Jar(incoming, "mods/provider-new.jar", "provider", "2");
        Jar(incoming, "mods/consumer.jar", "consumer", "1", new JObject { ["minecraft"] = "*", ["provider"] = ">=2" });
        object localOnly = Package("personal", "26.2");
        object localProvider = Jar(localOnly, "mods/personal-provider.jar", "personal_provider", "2");
        File.Copy(Text(localProvider, "Payload")!, Path.Combine(game, "mods", "personal-provider.jar.disabled"));
        Jar(incoming, "mods/personal-consumer.jar", "personal_consumer", "1", new JObject { ["minecraft"] = "*", ["personal_provider"] = ">=2" });
        object modified = Jar(localOnly, "mods/replaceable.jar", "replaceable", "1", new JObject { ["minecraft"] = "1.21.1" });
        File.Copy(Text(modified, "Payload")!, Path.Combine(game, "mods", "replaceable.jar"));
        Jar(incoming, "mods/replaceable.jar", "replaceable", "2");
        object plan = Update("Plan", game, Update("Manifest", original), incoming)!;
        object replaceableChange = ((IEnumerable)Get(plan, "Changes")!).Cast<object>().Single(change => Text(change, "Relative") == "mods/replaceable.jar");
        Set(replaceableChange, "Choice", PackUpdateChoice.UseAuthor);
        Update("DownloadChanges", plan, stage, CancellationToken.None, null);
        var ctor = Native.Type("PCL.ModpackUpdatePreviewView").GetConstructors(Native.Flags).Single(info => info.GetParameters().Length == 4);
        FrameworkElement preview = (FrameworkElement)ctor.Invoke(new[] { plan, "1.21.1", new Dictionary<ModLoaders, string> { [ModLoaders.Fabric] = "0.20.1" }, true });
        bool CanConfirm() => (bool)Native.Property(preview.GetType(), "CanConfirm").GetValue(preview)!;
        IEnumerable<DependencyObject> Walk(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (DependencyObject nested in Walk(child)) yield return nested;
            }
        }
        CheckBox Option(string kind, string relative) {
            if (Convert.ToInt32(Native.Property(preview.GetType(), "ActiveKind").GetValue(preview)) != 4)
                Native.Call(preview.GetType(), "ShowCompatibility", preview);
            preview.UpdateLayout();
            var option = Walk(preview).OfType<CheckBox>().Single(box => Equals(box.Tag, "preview-repair:" + kind + ":" + relative));
            VisualAssetChecks.Pump(() => option.IsLoaded, "Repair option did not load after switching pages.", 2000);
            return option;
        }
        Border ModifiedCard() {
            Type kinds = Native.Type("PCL.ModpackUpdatePreviewView+ChangeKind");
            Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(kinds, "Updated"));
            preview.UpdateLayout();
            return Walk(preview).OfType<Border>().Single(border => border.ToolTip is string tip && tip.StartsWith("mods/replaceable.jar\r\n", StringComparison.Ordinal));
        }
        var status = ((StackPanel)Get(preview, "Header")!).Children.OfType<Border>().Single();
        bool Checking() => GlassDialogControls.NoticeText(status).Text.Contains("正在检查", StringComparison.Ordinal);
        int cancelInvocations = 0;
        var converter = new ModMain.MyMsgBoxConverter { Title = "整合包更新预览", Text = "Live compatibility checks", Button1 = "按以上选择更新", Button2 = "取消", IsWarn = false, ContentFactory = () => preview, Button2Action = () => cancelInvocations++ };
        using var modal = Native.Dialog(converter);
        var messagePanel = new Grid(); messagePanel.Children.Add(modal);
        var confirm = (Button)((Button[])Native.Get(modal, "_buttons")!)[0];
        var app = System.Windows.Application.Current; Window? previousMain = app.MainWindow; ShutdownMode previousMode = app.ShutdownMode;
        int previousAnimation = ModAnimation.AniControlEnabled;
        var window = new Window { Content = messagePanel, Width = 850, Height = 650, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown; ModAnimation.AniControlEnabled++;
            window.Show(); window.UpdateLayout(); modal.Opacity = 1;
            VisualAssetChecks.Pump(() => !Checking(), "Initial live compatibility check did not finish.", 12000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "Incompatible preview allowed confirmation.");
            var cancel = (Button)((Button[])Native.Get(modal, "_buttons")!)[1];
            Native.Check(cancel.IsEnabled, "Compatibility errors disabled the native cancel button.");
            ClickDialog(modal, 1);
            Native.Check(cancelInvocations == 1, "The native cancel action was blocked by compatibility errors.");
            ClickDialog(modal, 0); Native.Check(converter.Result is null, "A disabled confirmation was accepted through the message controller.");
            Native.Check(Convert.ToInt32(Native.Property(preview.GetType(), "ActiveKind").GetValue(preview)) == 4, "An actionable update did not default to update checks.");
            Native.Call(preview.GetType(), "ShowKind", preview, Enum.Parse(Native.Type("PCL.ModpackUpdatePreviewView+ChangeKind"), "Preserved"));
            preview.UpdateLayout();
            VisualAssetChecks.Pump(() => Walk(preview).OfType<Image>().Any(img => Equals(img.Tag, "preview-file-icon") && img.Source is not null), "Embedded mod artwork did not load.", 8000);
            Native.Check(Walk(preview).OfType<Image>().Where(img => Equals(img.Tag, "preview-file-icon")).All(img => img.Stretch == Stretch.Uniform), "Artwork was stretched out of proportion.");
            Native.Check(Walk(preview).OfType<Image>().Where(img => Equals(img.Tag, "preview-file-icon")).All(img => {
                if (img.Width != 34 || img.Height != 34 || img.Clip is not RectangleGeometry clip || clip.RadiusX <= 0 || clip.RadiusY <= 0) return false;
                if (img.Source is not ImageSource source) return clip.Rect == new Rect(0, 0, 34, 34);
                double scale = 34 / Math.Max(source.Width, source.Height);
                return Math.Abs(clip.Rect.Width - source.Width * scale) < 0.01 && Math.Abs(clip.Rect.Height - source.Height * scale) < 0.01;
            }), "Square or wide Uniform artwork lacks a rounded clip around its visible content.");
            Save(modal, "live-preview-files.png");
            Native.Check(Walk(preview).OfType<Border>().Any(card => AutomationProperties.GetName(card) == "文件变更") && !Walk(preview).OfType<Border>().Any(card => AutomationProperties.GetName(card) == "更新检查"), "File page mixed in compatibility decisions.");
            Native.Check(Option("EnableRequired", "mods/provider-new.jar").IsChecked == false && Option("DisableIncompatible", "mods/legacy-adapter.jar").IsChecked == false,
                "Required-provider/unchanged-author decisions were missing or silently applied.");
            CheckBox personalRequired = Option("EnableRequired", "mods/personal-provider.jar");
            Native.Check(personalRequired.IsChecked == false && personalRequired.Content?.ToString() == "启用本地的必需前置",
                "A disabled personal provider was presented as an author package option or silently enabled.");
            Native.Check(Option("EnableRequired", "mods/provider-new.jar").Content?.ToString() == "启用整包版本的必需前置",
                "The packaged provider lost its distinct author-version label.");
            Save(modal, "live-preview-needs-decisions.png");
            var sections = Walk(preview).OfType<Border>().ToArray();
            Native.Check(sections.Count(card => AutomationProperties.GetName(card) == "更新检查") == 1 && !sections.Any(card => AutomationProperties.GetName(card) == "文件变更"), "Compatibility page mixed in file changes.");
            Native.Check(Walk(preview).OfType<Expander>().All(details => !details.IsExpanded), "Action details were repeated as expanded error walls.");
            // Resize the attached native dialog through the real WPF layout.
            window.Width = 560; window.UpdateLayout();
            var scroll = Walk(preview).OfType<ScrollViewer>().Single();
            Native.Check(scroll.ViewportHeight >= 80 && scroll.ScrollableHeight > 0, "Narrow preview lost its scrolling viewport.");
            foreach (var details in Walk(preview).OfType<Expander>().ToArray()) details.IsExpanded = true;
            window.UpdateLayout(); scroll.ScrollToBottom(); window.UpdateLayout();
            var filters = (FrameworkElement)Get(preview, "Filters")!;
            Native.Check(filters.TransformToAncestor(preview).TransformBounds(new Rect(filters.RenderSize)).Bottom <= scroll.TransformToAncestor(preview).TransformBounds(new Rect(scroll.RenderSize)).Top + 1, "Page navigation scrolled with the content.");
            Native.Check(scroll.VerticalOffset > 0 && cancel.IsVisible, "Expanded narrow preview lost scrolling or cancel access.");
            Rect bounds = modal.TransformToAncestor(window).TransformBounds(new Rect(modal.RenderSize));
            Native.Check(bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1, "Narrow preview escaped the available dialog width.");
            Save(modal, "live-preview-narrow.png");
            foreach (var details in Walk(preview).OfType<Expander>().ToArray()) details.IsExpanded = false;
            window.Width = 850; scroll.ScrollToTop(); window.UpdateLayout();
            Option("DisableIncompatible", "mods/legacy-adapter.jar").IsChecked = true;
            VisualAssetChecks.Pump(() => !Checking(), "Disable decision check did not finish.", 12000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "Disabling one mod hid the remaining missing provider.");
            Option("EnableRequired", "mods/provider-new.jar").IsChecked = true;
            VisualAssetChecks.Pump(() => !Checking(), "Author-provider decision check did not finish.", 12000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "The remaining disabled personal provider did not block confirmation.");
            Option("EnableRequired", "mods/personal-provider.jar").IsChecked = true;
            VisualAssetChecks.Pump(CanConfirm, "Enabling the local personal provider did not satisfy its consumer.", 12000);
            Native.Check(confirm.IsEnabled, "Live success did not reach the real native modal button.");
            // Explicitly return to an incompatible personal modification through its genuine radio control.
            void PreserveModified() {
                Border card = ModifiedCard();
                Walk(card).OfType<RadioButton>().Single(radio => Equals(radio.Tag, PackUpdateChoice.PreservePersonal)).IsChecked = true;
            }
            PreserveModified();
            VisualAssetChecks.Pump(() => !Checking(), "Personal modification recheck did not finish.", 12000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "An incompatible preserved personal modification was accepted.");
            CheckBox restore = Option("UseAuthorVersion", "mods/replaceable.jar");
            Native.Check(restore.IsChecked == false && restore.Content?.ToString() == "使用整包新版", "The author restore decision was not explicit.");
            Native.Check(!Walk(preview).OfType<CheckBox>().Any(box => Equals(box.Tag, "preview-repair:DisableIncompatible:mods/replaceable.jar")),
                "A healthy author replacement was offered for disabling to accommodate the personal modification.");
            restore.IsChecked = true;
            VisualAssetChecks.Pump(CanConfirm, "Restoring the healthy author version did not enable confirmation.", 12000);
            Native.Check(confirm.IsEnabled && Option("UseAuthorVersion", "mods/replaceable.jar").IsChecked == true, "The selected author restore decision disappeared or failed to reach the native confirm button.");
            Border restoredCard = ModifiedCard();
            Native.Check(Walk(restoredCard).OfType<RadioButton>().Single(radio => Equals(radio.Tag, PackUpdateChoice.UseAuthor)).IsChecked == true,
                "The ordinary author-version radio did not reflect the repair decision.");
            Option("UseAuthorVersion", "mods/replaceable.jar").IsChecked = false;
            VisualAssetChecks.Pump(() => !Checking(), "Reverting the author restore decision did not finish.", 12000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "Reverting to the personal modification bypassed compatibility checks.");
            Option("UseAuthorVersion", "mods/replaceable.jar").IsChecked = true;
            VisualAssetChecks.Pump(CanConfirm, "Restoring the author version a second time did not recover confirmation.", 12000);
            scroll.ScrollToTop(); window.UpdateLayout();
            Save(modal, "live-preview-valid.png");
            var rapid = Option("DisableIncompatible", "mods/legacy-adapter.jar");
            rapid.IsChecked = false; rapid.IsChecked = true; rapid.IsChecked = false;
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "A changed selection retained stale permission while checking.");
            VisualAssetChecks.Pump(() => !Checking(), "Restored incompatible selection check did not finish.", 12000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "Keeping an incompatible mod was not blocked inside the preview.");
            ClickDialog(modal, 0); Native.Check(converter.Result is null, "Invalid selected combination closed the modal or started the update.");
            // Unload while a valid review is pending: late completions must never reopen confirmation.
            Option("DisableIncompatible", "mods/legacy-adapter.jar").IsChecked = true;
            ClickDialog(modal, 1);
            Native.Check(cancel.IsEnabled && cancelInvocations == 2, "A pending review blocked the native cancel action.");
            window.Content = null;
            window.UpdateLayout();
            VisualAssetChecks.Pump(() => !preview.IsLoaded, "Preview did not unload.", 2000);
            Native.Check(!CanConfirm() && !confirm.IsEnabled, "Unloading a checking preview retained confirmation permission.");
            Native.Check(!Directory.Exists(Path.Combine(game, "PCL", "PackUpdateBackups")) && File.Exists(Path.Combine(game, "mods", "legacy-adapter.jar")) &&
                File.Exists(Path.Combine(game, "mods", "provider-old.jar.disabled")) && File.Exists(Path.Combine(game, "mods", "personal-provider.jar.disabled")) &&
                !File.Exists(Path.Combine(game, "mods", "personal-provider.jar")), "Preview decisions wrote into the real commit path.");
        } finally { window.Content = null; window.Close(); app.MainWindow = previousMain; app.ShutdownMode = previousMode; ModAnimation.AniControlEnabled = previousAnimation; }
        Console.WriteLine("PASS live modpack preview: genuine native required-provider/unchanged-author controls, async recheck, original modal confirmation disabled until valid, restored conflicts blocked and no commit.");
    }

    private static void DrainPendingReader(string root)
    {
        string file = Path.Combine(root, "reader-seam.jar"); File.WriteAllText(file, "owned preview input");
        using ManualResetEventSlim entered = new(), release = new(), cancelled = new();
        var view = new ModpackDialogController<Button, ScrollViewer>("Reader lifetime", "", 4, new() {
            AnimationName = () => "reader-lifetime", CreateButton = () => new(), SetButtonText = (button, text) => button.Content = text,
            HighlightButton = (_, _) => { }, OnClick = (button, action) => button.Click += (_, _) => action(), CreateScroll = () => new(),
            CreateRadio = (text, selected, action) => new RadioButton { Content = text, IsChecked = selected },
            CreateCheck = (text, selected, action) => new CheckBox { Content = text, IsChecked = selected },
            AnimationControl = () => 1, StopAnimation = _ => { }, AnimateOpacity = (_, _, _, _) => { }
        });
        var plan = new PreviewPlan { Package = new PreviewPackage { Minecraft = "1.21.1" } };
        var controller = new ModpackUpdatePreviewController<PackUpdateFileData, PreviewPackage, PreviewChange, PreviewPlan, DayOfWeek, Button, ScrollViewer>(
            view, plan, "1.21.1", new(), new(), _ => { }, text => new TextBlock { Text = text }, value => value, (_, token) => {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                using var registration = token.Register(() => cancelled.Set());
                entered.Set(); release.Wait();
                return new PackUpdatePreviewReview();
            });
        try {
            Native.Check(entered.Wait(5000), "Pending preview reader did not enter its file-read seam.");
            Task drain = controller.CloseAndDrainAsync();
            Native.Check(cancelled.IsSet && !drain.IsCompleted, "Closing preview failed to cancel and retain its active reader.");
            bool held = false;
            try { using var conflicting = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { held = true; }
            Native.Check(held, "Reader lifetime seam did not hold its input until released.");
            release.Set();
            Native.Check(drain.Wait(5000), "Preview drain waited for a Dispatcher callback instead of only its reader.");
            using var released = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Native.Check(!view.CanConfirm, "A closed preview enabled confirmation after its read completed.");
        } finally { release.Set(); controller.CloseAndDrainAsync().GetAwaiter().GetResult(); }
        int selectedPage = -1;
        var cleanView = new ModpackDialogController<Button, ScrollViewer>("No decisions", "", 4, new() {
            AnimationName = () => "clean-preview", CreateButton = () => new(), SetButtonText = (button, text) => button.Content = text,
            HighlightButton = (_, _) => { }, OnClick = (_, _) => { }, CreateScroll = () => new(), AnimationControl = () => 1, StopAnimation = _ => { }, AnimateOpacity = (_, _, _, _) => { }
        });
        var cleanController = new ModpackUpdatePreviewController<PackUpdateFileData, PreviewPackage, PreviewChange, PreviewPlan, DayOfWeek, Button, ScrollViewer>(cleanView, plan, "1.21.1", new(), new(), value => selectedPage = value, text => new TextBlock { Text = text }, value => value, (_, _) => new());
        VisualAssetChecks.Pump(() => cleanView.CanConfirm, "Empty review did not finish.", 5000);
        Native.Check(selectedPage == 3, "Empty update checks did not default to preserve/custom, including an empty file category.");
        cleanController.CloseAndDrainAsync().GetAwaiter().GetResult();
    }

    private static void Save(FrameworkElement element, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("PCLLF_MODPACK_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder); element.UpdateLayout();
        var surface = Window.GetWindow(element) ?? element;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
    }

    private static void ClickDialog(GlassMessageDialog dialog, int index)
    {
        var button = ((Button[])Native.Get(dialog, "_buttons")!)[index];
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
}
