using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class PersonalOptionsPageChecks {
    internal static void Run(FixtureEnvironment environment) {
        using var visual = new VisualAssetChecks.Scope(environment, "personal-options-visual");
        using var scope = new LocalFeatureChecks.Scope(environment, "personal-options-page");
        using var settingsStore = new SettingsStore();
        ModAnimation.AniControlEnabled++;
        var source = LocalFeatureChecks.Instance(scope, "Source", "1.21.1");
        var target = LocalFeatureChecks.Instance(scope, "Target", "1.12.2");
        var empty = LocalFeatureChecks.Instance(scope, "FirstRun", "1.21.1");
        string sourceFile = Path.Combine(source.PathIndie, "options.txt"), targetFile = Path.Combine(target.PathIndie, "options.txt");
        LocalFeatureChecks.Put(sourceFile, "key_key.forward:key.keyboard.k\nmouseSensitivity:0.7\nfov:90\ninvertYMouse:false\nbobView:true\nsoundCategory_master:0.5\nrenderDistance:32\n");
        LocalFeatureChecks.Put(targetFile, "key_key.forward:17\nmouseSensitivity:0.2\nfov:0.0\ninvertYMouse:false\nbobView:false\nsoundCategory_master:1.0\nrenderDistance:8\n");
        scope.SetGlobal("LaunchPersonalOptionsSync", false); scope.SetGlobal("LaunchPersonalOptionsExcluded", "[]"); scope.SetGlobal("LaunchPersonalOptionsCapture", false);
        foreach (string category in new[] { "Keys", "Mouse", "View", "Accessibility", "Audio" }) scope.SetGlobal("LaunchPersonalOptions" + category, true);
        var previousPage = ModMain.FrmSetupPersonal; var previousLeft = ModMain.FrmInstanceLeft; var previousContext = PageInstanceLeft.Instance;
        var selected = Native.Field(typeof(ModMinecraft), "_McInstanceSelected"); object? oldSelected = selected.GetValue(null); selected.SetValue(null, source);
        var app = System.Windows.Application.Current; var oldMain = app.MainWindow;
        var window = new Window { Width = 870, Height = 740, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        try {
            var setupLeft = new PageSetupLeft();
            Native.Check(((MyListItem)setupLeft.FindName("ItemPersonal")).Logo != ((MyListItem)setupLeft.FindName("ItemUpdate")).Logo, "Personal settings still repeats the update icon.");
            var globalPage = (PageSetupPersonal)setupLeft.PageGet(FormMain.PageSubType.SetupPersonal);
            var versionLeft = new PageInstanceLeft(); ModMain.FrmInstanceLeft = versionLeft; PageInstanceLeft.Instance = target;
            var versionPage = (PageSetupPersonal)versionLeft.PageGet(FormMain.PageSubType.InstancePersonal);
            foreach (var view in new[] { globalPage, versionPage }) {
                Native.Set(view, "Confirm", new Func<string,string,bool>((_,_) => true)); Native.Set(view, "Notify", new Action<string>(_ => { }));
            }
            Native.Check(!ReferenceEquals(globalPage, versionPage) && versionLeft.FindName("ItemPersonal") is MyListItem { Title: "基础设置同步" }, "Version sync entry or its separate view is missing.");
            PageSetupPersonal page = globalPage;
            void Show(PageSetupPersonal view) { window.Content = view; page = view; window.UpdateLayout(); VisualAssetChecks.Pump(() => view.IsLoaded, "Sync page did not load."); }
            void Refresh() { Native.Call(typeof(PageSetupPersonal), "RefreshPage", page); window.UpdateLayout(); }
            void Search(string text) { ((MySearchBox)page.FindName("TextSearch")).Text = text; window.UpdateLayout(); }
            void ChangeSlider(string tag, int value) {
                var slider = Walk(page).OfType<MySlider>().Single(item => Equals(item.Tag, tag));
                VisualAssetChecks.Pump(() => slider.IsLoaded && slider.ActualWidth > 30, "Native numeric editor did not load.");
                int suppression = ModAnimation.AniControlEnabled; ModAnimation.AniControlEnabled = 0;
                try { slider.Value = value; } finally { ModAnimation.AniControlEnabled = suppression; }
                Native.Call(typeof(PageSetupPersonal), "FlushPendingEdits", page, true);
            }
            void Press(Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            JObject Profile() => (JObject)LocalFeatureChecks.Data("ReadJson", "personal-options.json")!;
            string profileFile = Path.Combine(scope.Root, "profile", "personal-options.json");
            window.Content = globalPage; window.SetResourceReference(Window.BackgroundProperty, "ColorBrushBackground"); window.Show(); window.UpdateLayout();
            VisualAssetChecks.Pump(() => globalPage.IsLoaded, "Global page did not load.");
            Native.Check(globalPage.FindName("ComboInstance") is null && globalPage.FindName("LabInstance") is TextBlock &&
                ((MyCheckBox)globalPage.FindName("CheckEnabled")).Visibility == Visibility.Visible && ((MyButton)globalPage.FindName("BtnApply")).Visibility == Visibility.Collapsed,
                "Global page does not retain its master/import role.");
            Native.Check(((TextBlock)page.FindName("LabImportedInstance")).Text.Contains("来源未记录", StringComparison.Ordinal), "A profile without provenance guessed its source.");
            Native.Call(typeof(PageSetupPersonal), "ImportSelection", globalPage);
            Native.Check(Settings.Get<bool>("LaunchPersonalOptionsSync") && (string?)Profile()["values"]!["fov"] == "90", "Global import did not establish the shared settings.");
            JObject origin = (JObject)Profile()["importSource"]!;
            Native.Check((string?)origin["name"] == source.Name && (string?)origin["versionPath"] == source.PathVersion, "Global import did not record the actual source.");
            selected.SetValue(null, target); Native.Call(typeof(PageSetupPersonal), "InstanceSelectionChanged", page);
            VisualAssetChecks.Pump(() => ReferenceEquals(((TextBlock)page.FindName("LabInstance")).Tag, target), "Current-version comparison did not follow the launcher selection.");
            Native.Check(((TextBlock)page.FindName("LabImportedInstance")).Text == source.Name, "Changing the current version replaced the recorded import source.");
            byte[] sourceBefore = File.ReadAllBytes(sourceFile), targetBefore = File.ReadAllBytes(targetFile);
            Search("视场角");
            var currentGlobalComparison = Walk(page).OfType<MySlider>().Single(item => Equals(item.Tag,"current-value:fov"));
            var globalEditor = Walk(page).OfType<MySlider>().Single(item => Equals(item.Tag,"profile-value:fov"));
            VisualAssetChecks.Pump(() => globalEditor.IsLoaded && globalEditor.ActualWidth > 30, "Global comparison did not load.");
            Native.Check(!currentGlobalComparison.IsEnabled && currentGlobalComparison.Value == 40 && globalEditor.Value == 60 &&
                Math.Abs(currentGlobalComparison.ActualWidth-globalEditor.ActualWidth)<1, "Global comparison does not show the actual selected version and equal-width editable global scheme.");
            ChangeSlider("profile-value:fov", 75);
            Native.Check((string?)Profile()["values"]!["fov"] == "90" && ((MyButton)page.FindName("BtnSaveGlobal")).IsEnabled &&
                ((TextBlock)page.FindName("LabImportedInstance")).Text == "自定义（未保存）", "A global slider wrote before Save or failed to mark its draft.");
            Search("反转鼠标");
            var globalToggle = Walk(page).OfType<MyCheckBox>().Single(item => Equals(item.Tag, "profile-value:invertYMouse"));
            VisualAssetChecks.Pump(() => globalToggle.IsLoaded, "Global native toggle did not load."); globalToggle.SetChecked(true, true);
            Search("前进");
            Native.Call(typeof(PageSetupPersonal), "BeginProfileCapture", page, "key_key.forward"); Press(Key.Escape);
            Native.Check((string?)Profile()["values"]!["key_key.forward"] == "key.keyboard.k", "Esc altered global key settings.");
            Native.Call(typeof(PageSetupPersonal), "BeginProfileCapture", page, "key_key.forward"); Press(Key.R);
            Native.Check((string?)Profile()["values"]!["key_key.forward"] == "key.keyboard.k" && (string?)Profile()["values"]!["invertYMouse"] == "false" &&
                File.ReadAllBytes(sourceFile).SequenceEqual(sourceBefore) && File.ReadAllBytes(targetFile).SequenceEqual(targetBefore), "Global key/toggle editors wrote before Save.");
            ((MyComboBox)page.FindName("ComboCapture")).SelectedIndex = 0;
            Native.Check(!Settings.Get<bool>("LaunchPersonalOptionsCapture"), "The global capture policy wrote before Save.");
            Native.Call(typeof(PageSetupPersonal), "SelectOptions", page, new[] { PersonalOptionsCatalog.Items.Single(item=>item.Key=="soundCategory_master") }, false);
            Native.Check(Settings.Get<string>("LaunchPersonalOptionsExcluded") == "[]", "Global participation wrote before Save.");
            Show(versionPage); Search("视场角");
            Native.Check(Walk(page).OfType<MySlider>().Single(item=>Equals(item.Tag,"global-value:fov")).Value==60, "An unsaved global draft leaked into the version page.");
            Show(globalPage); Search("视场角");
            Native.Check(Walk(page).OfType<MySlider>().Single(item=>Equals(item.Tag,"profile-value:fov")).Value==75 && (string?)Profile()["values"]!["fov"]=="90", "Navigating saved or discarded the global draft.");
            Native.Call(typeof(PageSetupPersonal), "SaveGlobalSettings", page);
            Native.Check((string?)Profile()["values"]!["fov"]=="105" && (string?)Profile()["values"]!["invertYMouse"]=="true" &&
                (string?)Profile()["values"]!["key_key.forward"]=="key.keyboard.r" && (string?)Profile()["sourceKind"]=="custom" &&
                ((TextBlock)page.FindName("LabImportedInstance")).Text=="自定义" && JToken.DeepEquals(origin,Profile()["importSource"]) &&
                Settings.Get<bool>("LaunchPersonalOptionsCapture") && Settings.Get<string>("LaunchPersonalOptionsExcluded").Contains("soundCategory_master",StringComparison.Ordinal),
                "Save did not commit the global draft or mark the source as custom.");
            ChangeSlider("profile-value:fov",70); Native.Call(typeof(PageSetupPersonal),"DiscardDraft",page);
            Native.Check((string?)Profile()["values"]!["fov"]=="105" && Walk(page).OfType<MySlider>().Single(item=>Equals(item.Tag,"profile-value:fov")).Value==75, "Discard changed stored settings or retained the draft.");
            byte[] globalBefore = File.ReadAllBytes(profileFile);
            Show(versionPage);
            Native.Check(ReferenceEquals(((TextBlock)page.FindName("LabInstance")).Tag, target) && ((MyCheckBox)page.FindName("CheckEnabled")).Visibility == Visibility.Collapsed &&
                ((MyButton)page.FindName("BtnImport")).Visibility == Visibility.Collapsed && ((MyComboBox)page.FindName("ComboCapture")).Visibility == Visibility.Collapsed,
                "Version page exposes global import/master/recapture controls or follows the wrong owner.");
            Search("视场角");
            var localSlider = Walk(page).OfType<MySlider>().Single(item => Equals(item.Tag, "instance-value:fov"));
            var readOnlyGlobal = Walk(page).OfType<MySlider>().Single(item => Equals(item.Tag, "global-value:fov"));
            VisualAssetChecks.Pump(() => localSlider.IsLoaded && localSlider.ActualWidth > 30, "Local slider did not load.");
            Native.Check(!localSlider.IsEnabled && !readOnlyGlobal.IsEnabled && !readOnlyGlobal.IsHitTestVisible && Math.Abs(localSlider.ActualWidth-readOnlyGlobal.ActualWidth) < 1 && readOnlyGlobal.Value == 75 &&
                ((MyButton)page.FindName("BtnApply")).Text=="保存此版本个人设置" && !((MyButton)page.FindName("BtnApply")).IsEnabled,
                "A following version is editable or has an incorrect comparison/save button.");
            Native.Check(LocalFeatureChecks.Rejected(()=>Native.Call(typeof(PageSetupPersonal),"SaveInstanceSettings",page)) is InvalidOperationException, "A following version allowed a manual settings write.");
            ((MyComboBox)page.FindName("ComboPolicy")).SelectedIndex=1;
            Native.Check(Walk(page).OfType<MySlider>().Single(item=>Equals(item.Tag,"instance-value:fov")).IsEnabled, "Disabling following did not unlock local settings.");
            ChangeSlider("instance-value:fov", 60);
            Search("反转鼠标");
            var localToggle = Walk(page).OfType<MyCheckBox>().Single(item => Equals(item.Tag, "instance-value:invertYMouse"));
            VisualAssetChecks.Pump(() => localToggle.IsLoaded, "Local native toggle did not load."); localToggle.SetChecked(true, true);
            Search("前进"); Native.Call(typeof(PageSetupPersonal), "BeginProfileCapture", page, "key_key.forward"); Press(Key.E);
            Native.Check(File.ReadAllBytes(targetFile).SequenceEqual(targetBefore) && File.ReadAllBytes(profileFile).SequenceEqual(globalBefore) && ((MyButton)page.FindName("BtnApply")).IsEnabled,
                "Version key/toggle/slider edits wrote before Save.");
            Native.Call(typeof(PageSetupPersonal), "SaveInstanceSettings", page);
            string local = File.ReadAllText(targetFile);
            Native.Check(local.Contains("fov:0.5", StringComparison.Ordinal) && local.Contains("key_key.forward:18", StringComparison.Ordinal) && local.Contains("invertYMouse:true", StringComparison.Ordinal) &&
                local.Contains("renderDistance:8", StringComparison.Ordinal) && File.Exists(targetFile+".pcl-backup") && File.ReadAllBytes(profileFile).SequenceEqual(globalBefore),
                "Version edits failed conversion/backup or modified global settings.");
            Native.Check(LocalFeatureChecks.Rejected(() => Native.Call(typeof(PageSetupPersonal), "SelectOptions", page, new[] { PersonalOptionsCatalog.Items.First() }, false)) is InvalidOperationException,
                "Version page can change global sync participation.");
            var keyPage = new PageInstanceKeybindings(); object keyController = Native.Get(keyPage, "Controller")!;
            object keyHost = Native.Get(keyController, "host")!;
            Native.Check(!(bool)Native.Get(keyHost,"CapturePersonalOnEdit")!, "Version key management still writes edits into the global scheme.");
            var annotations = (IReadOnlyDictionary<string,string>)Native.Static("PCL.PersonalGameSettings", "BindingAnnotations", target)!;
            Native.Check(annotations["key_key.forward"].Contains("个人方案：R",StringComparison.Ordinal) && !annotations.ContainsKey("key_mod.magic"), "Basic key annotations do not show the global values.");
            ((MyComboBox)page.FindName("ComboPolicy")).SelectedIndex=0; Search("视场角");
            Native.Check(!Walk(page).OfType<MySlider>().Single(item=>Equals(item.Tag,"instance-value:fov")).IsEnabled &&
                File.ReadAllBytes(profileFile).SequenceEqual(globalBefore), "Following global settings did not relock local editors.");
            string? proof = Environment.GetEnvironmentVariable("PCLLF_PERSONAL_OPTIONS_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(proof)) {
                Directory.CreateDirectory(proof);
                foreach (var scene in new[] { (View:globalPage,Query:"鼠标设置",Name:"global-settings"), (View:versionPage,Query:"鼠标设置",Name:"version-settings"), (View:versionPage,Query:"按键绑定",Name:"version-keys") }) {
                    Show(scene.View); Search(scene.Query);
                    foreach (int mode in new[] { 0,1 }) {
                        Native.Cache("UiLauncherColorMode",mode); Native.Static("PCL.ModSecret","ThemeRefresh",0); Refresh();
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(proof,scene.Name+(mode==0?"-light.png":"-dark.png"))); png.Save(output);
                    }
                }
            }
            Show(versionPage); PageInstanceLeft.Instance = empty; Refresh();
            Native.Check(!((MyButton)page.FindName("BtnApply")).IsEnabled, "A first-run version allows an invalid apply.");
            ((MyComboBox)page.FindName("ComboPolicy")).SelectedIndex=1;
            Native.Check(Settings.Get<int>("VersionPersonalOptionsSync",Instance:empty)==1, "Version policy did not stay per-version.");
            Show(globalPage); selected.SetValue(null,null); Refresh(); Search("视场角");
            Native.Check(Walk(page).OfType<MySlider>().Any(item=>Equals(item.Tag,"profile-value:fov") && item.IsEnabled && item.Value==75), "Global settings cannot be viewed/edited without a source version.");
            selected.SetValue(null, target); Refresh();
            byte[] beforeImport = File.ReadAllBytes(profileFile);
            Native.Set(page, "Confirm", new Func<string,string,bool>((_,_) => false));
            Native.Call(typeof(PageSetupPersonal), "ImportSelection", page);
            Native.Check(File.ReadAllBytes(profileFile).SequenceEqual(beforeImport), "Canceling an import changed the source or scheme.");
            Native.Set(page, "Confirm", new Func<string,string,bool>((_,_) => true));
            selected.SetValue(null, empty); Refresh();
            Native.Check(LocalFeatureChecks.Rejected(() => Native.Call(typeof(PageSetupPersonal), "ImportSelection", page)) is IOException &&
                File.ReadAllBytes(profileFile).SequenceEqual(beforeImport), "A failed import replaced source metadata.");
            selected.SetValue(null, target); Refresh(); Native.Call(typeof(PageSetupPersonal), "ImportSelection", page);
            Native.Check(((TextBlock)page.FindName("LabImportedInstance")).Text == target.Name && (string?)Profile()["importSource"]!["name"] == target.Name,
                "A successful re-import did not update the recorded source.");
            var conflict = new JObject(new JProperty("baseline","80"),new JProperty("profile",Profile()["values"]!["fov"]!.DeepClone()),new JProperty("game","100"));
            JObject withConflict = Profile(); withConflict["conflicts"] = new JObject(new JProperty("fov",conflict.DeepClone()));
            LocalFeatureChecks.Data("WriteJson","personal-options.json",withConflict); Refresh();
            byte[] conflictBefore = File.ReadAllBytes(profileFile);
            Native.Call(typeof(PageSetupPersonal),"StageConflict",page,"fov",true,conflict);
            Native.Check(File.ReadAllBytes(profileFile).SequenceEqual(conflictBefore), "Choosing a conflict value wrote before Save.");
            Native.Call(typeof(PageSetupPersonal),"SaveGlobalSettings",page);
            Native.Check((string?)Profile()["values"]!["fov"]=="100" && Profile()["conflicts"]!["fov"] is null &&
                ((TextBlock)page.FindName("LabImportedInstance")).Text=="自定义", "Saving the conflict choice did not commit the custom scheme.");
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS personal settings: explicit global/local Save, retained/discarded drafts, import/custom source, separate current/source comparison, following locks local editors, native controls, matching tracks, conflict drafts, stale guards, backup and key annotations.");
        } finally {
            window.Content=null; window.Close(); app.MainWindow=oldMain;
            selected.SetValue(null,oldSelected); ModMain.FrmSetupPersonal=previousPage; ModMain.FrmInstanceLeft=previousLeft; PageInstanceLeft.Instance=previousContext;
        }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root) {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    // Keep the genuine Settings engine/cache; isolate only the registry primitives
    // for this page's non-account keys. Every other access retains the host guard.
    private sealed class SettingsStore : IDisposable {
        private readonly object host;
        private readonly object read, write;
        private readonly Dictionary<(string Path, string Key), string?> values = new();
        private readonly HashSet<string> allowed = new(StringComparer.Ordinal) {
            "LaunchPersonalOptionsSync", "LaunchPersonalOptionsKeys", "LaunchPersonalOptionsMouse", "LaunchPersonalOptionsView",
            "LaunchPersonalOptionsAccessibility", "LaunchPersonalOptionsAudio", "LaunchPersonalOptionsExcluded", "LaunchPersonalOptionsCapture"
        };
        internal SettingsStore() {
            object backend = Native.Field(typeof(Settings), "Backend").GetValue(null)!;
            host = Native.Get(backend, "host")!;
            read = Native.Get(host, "ReadRegistry")!; write = Native.Get(host, "WriteRegistry")!;
            Native.Set(host, "ReadRegistry", new Func<string, string, string?, string?>((path, key, fallback) =>
                allowed.Contains(key) ? values.GetValueOrDefault((path, key), fallback) : ((Func<string, string, string?, string?>)read)(path, key, fallback)));
            Native.Set(host, "WriteRegistry", new Action<string, string, string?>((path, key, value) => {
                if (allowed.Contains(key)) values[(path, key)] = value;
                else ((Action<string, string, string?>)write)(path, key, value);
            }));
        }
        public void Dispose() { Native.Set(host, "ReadRegistry", read); Native.Set(host, "WriteRegistry", write); }
    }
}
