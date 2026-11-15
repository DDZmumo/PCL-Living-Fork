using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class LaunchMoreSettingsChecks {
    private const string Preference = "UiLaunchMoreSettings";
    internal static void Run(FixtureEnvironment fixture) {
        using var scope = new LocalFeatureChecks.Scope(fixture, "launch-more-settings");
        using var store = new PreferenceStore();
        Native.Check(Settings.Entries[Preference].DefaultValue is false &&
            Settings.Entries["LaunchModDependencyCheck"].DefaultValue is true && Settings.Entries["LaunchModDependencyAutoFill"].DefaultValue is true,
            "More Settings must default collapsed while its dependency features remain enabled.");
        scope.SetGlobal(Preference, true);
        var previousRight = ModMain.FrmLaunchRight;
        var app = System.Windows.Application.Current; var previousWindow = app.MainWindow;
        int previousAnimationControl = ModAnimation.AniControlEnabled;
        var tick = Native.Method(typeof(ModAnimation), "AniTimer"); var lastTick = Native.Field(typeof(ModAnimation), "AniLastTick"); object? previousTick = lastTick.GetValue(null);
        PageLaunchRight? right = null; Window? window = null;
        try {
            ModAnimation.AniControlEnabled = 0;
            Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            var left = new PageLaunchLeft(); right = new PageLaunchRight(); ModMain.FrmLaunchRight = right;
            var choice = (MyCheckBox)left.FindName("CheckMoreSettings");
            var launch = (MyButton)left.FindName("BtnLaunch");
            left.Measure(new Size(300,620)); left.Arrange(new Rect(0,0,300,620)); left.UpdateLayout();
            Point choicePosition = choice.TranslatePoint(new Point(), left), launchPosition = launch.TranslatePoint(new Point(), left);
            Native.Check(choice.Text == "更多设置" && choice.Checked && choicePosition.X > 150 &&
                choicePosition.Y + choice.ActualHeight <= launchPosition.Y - 8 && choicePosition.X + choice.ActualWidth <= 280.1,
                "The native More Settings checkbox is not above the launch button on the right.");
            AccountActionsFit(left);
            string? preview = Environment.GetEnvironmentVariable("PCLLF_PERSONAL_OPTIONS_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(preview)) SaveImage(left,300,620,preview,"launch-more-placement-light.png");
            var content = (Grid)right.FindName("PanMoreSettings");
            var transform = (TranslateTransform)right.FindName("TransMoreSettings");
            var recent = (MyCard)right.FindName("CardRecentWorlds");
            var dependencies = (MyCard)right.FindName("CardDependencyChecks");
            Native.Call(typeof(PageLaunchRight),"ApplyRecentWorldState",right,true,false);
            Native.Check(dependencies.Visibility == Visibility.Visible,
                "A fresh launch page hid dependency settings when discovering an already-running game.");
            object world = LocalFeatureChecks.New("PCL.RecentWorld");
            Native.Set(world,"Name","测试存档"); Native.Set(world,"Identifier","World"); Native.Set(world,"VersionPath",Path.Combine(scope.Root,"versions","Example"));
            Native.Set(world,"GameFolder",scope.Root); Native.Set(world,"PlayedUtc",DateTime.UtcNow);
            object record = Activator.CreateInstance(typeof(Tuple<,>).MakeGenericType(world.GetType(),typeof(ImageSource)),world,null)!;
            Native.Call(typeof(PageLaunchRight),"ShowRecentWorld",right,record);
            Native.Call(typeof(PageLaunchRight),"ApplyRecentWorldState",right,false,false);
            // Host only the real checkbox/cards; the login page's Loaded workflow never runs.
            ((Panel)choice.Parent).Children.Remove(choice); ((Panel)content.Parent).Children.Remove(content);
            var host = new Grid { ClipToBounds = true }; host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) }); host.ColumnDefinitions.Add(new ColumnDefinition());
            choice.VerticalAlignment = VerticalAlignment.Bottom; choice.Margin = new Thickness(20,0,20,35);
            Grid.SetColumn(choice,0); Grid.SetColumnSpan(choice,1); Grid.SetRow(choice,0); Grid.SetColumn(content,1); host.Children.Add(choice); host.Children.Add(content);
            window = new Window { Content = host, Width = 670, Height = 430, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.UpdateLayout();
            VisualAssetChecks.Pump(() => choice.IsLoaded && content.IsLoaded,"More Settings fixture did not load.");
            void Step(int count) { for (int index=0;index<count;index++) { lastTick.SetValue(null,long.MaxValue); Native.Call(tick.DeclaringType!,tick.Name,null,16d); } }
            Native.Check(recent.IsVisible && dependencies.IsVisible,"Default-on More Settings hid an existing card.");
            bool checkBefore = Settings.Get<bool>("LaunchModDependencyCheck"), fillBefore = Settings.Get<bool>("LaunchModDependencyAutoFill"), recentBefore = Settings.Get<bool>("LaunchRecentWorlds");
            choice.SetChecked(false,true); Step(4);
            Native.Check(transform.X < 0 && transform.X > -300 && !content.IsHitTestVisible,"Unchecking did not start a leftward slide or block hidden input.");
            Step(35);
            Native.Check(content.Visibility == Visibility.Collapsed && !recent.IsVisible && !dependencies.IsVisible && Math.Abs(transform.X+300)<0.05 && !Settings.Get<bool>(Preference),
                "Both cards were not hidden together or the preference did not save.");
            Native.Call(typeof(PageLaunchRight),"ApplyRecentWorldState",right,true,false);
            choice.SetChecked(true,true); Step(35);
            Native.Check(dependencies.IsVisible && !recent.IsVisible,
                "More Settings was empty while a game was running, or Quick Play lost its running-game protection.");
            choice.SetChecked(false,true); Step(35);
            Native.Call(typeof(PageLaunchRight),"ApplyRecentWorldState",right,false,false);
            Native.Check(!content.IsVisible,"A game-state refresh reopened hidden cards.");
            ((System.Collections.IDictionary)Native.Get(Settings.Entries[Preference],"ValueCache")!).Remove("");
            var reopenedLeft = new PageLaunchLeft(); var reopenedRight = new PageLaunchRight();
            Native.Check(!((MyCheckBox)reopenedLeft.FindName("CheckMoreSettings")).Checked && ((Grid)reopenedRight.FindName("PanMoreSettings")).Visibility == Visibility.Collapsed,
                "Fresh pages did not reload the stored hidden preference.");
            Native.Call(typeof(PageLaunchRight),"ReleaseRecentWorlds",reopenedRight);
            choice.SetChecked(true,true); Step(4);
            Native.Check(transform.X > -300 && transform.X < 0,"Checking did not slide the cards rightward.");
            double furthest = transform.X;
            for(int frame=0;frame<35;frame++) { Step(1); furthest=Math.Max(furthest,transform.X); }
            Native.Check(furthest>0.1,"Showing the panels did not have PCL's overshoot and rebound.");
            Native.Check(recent.IsVisible && dependencies.IsVisible && content.IsHitTestVisible && Math.Abs(transform.X)<0.05,"The two cards did not return together.");
            choice.SetChecked(false,true); Step(4); choice.SetChecked(true,true); Step(35);
            Native.Check(content.IsVisible && Math.Abs(transform.X)<0.05,"Reversing a slide left a stale hide callback.");
            Native.Check(checkBefore==Settings.Get<bool>("LaunchModDependencyCheck") && fillBefore==Settings.Get<bool>("LaunchModDependencyAutoFill") && recentBefore==Settings.Get<bool>("LaunchRecentWorlds"),
                "Hiding the cards changed dependency checks or Quick Play behavior.");
            if (!string.IsNullOrWhiteSpace(preview)) {
                foreach (int mode in new[] { 0,1 }) {
                    Native.Cache("UiLauncherColorMode",mode); Native.Static("PCL.ModSecret","ThemeRefresh",0); window.UpdateLayout();
                    SaveImage(host,(int)host.ActualWidth,(int)host.ActualHeight,preview,"launch-more-shown"+(mode==0?"-light.png":"-dark.png"));
                    choice.SetChecked(false,true); Step(35); window.UpdateLayout();
                    SaveImage(host,(int)host.ActualWidth,(int)host.ActualHeight,preview,"launch-more-hidden"+(mode==0?"-light.png":"-dark.png"));
                    choice.SetChecked(true,true); Step(35);
                }
                Native.Cache("UiLauncherColorMode",0); Native.Static("PCL.ModSecret","ThemeRefresh",0);
            }
            fixture.AssertNoForbiddenIo();
            Console.WriteLine("PASS More Settings: actual checkbox placement/persistence, both cards slide left to hide/right to show, reverse/reopen/game-state checks and unchanged dependency/Quick Play preferences.");
        } finally {
            if (right is not null) Native.Call(typeof(PageLaunchRight),"ReleaseRecentWorlds",right);
            if (window is not null) { window.Content=null; window.Close(); }
            app.MainWindow=previousWindow; ModMain.FrmLaunchRight=previousRight;
            ModAnimation.AniControlEnabled=previousAnimationControl; lastTick.SetValue(null,previousTick);
        }
    }
    private static void AccountActionsFit(PageLaunchLeft left) {
        var panel = (Grid)left.FindName("PanLogin");
        var account = new PageLoginMsSkin(); panel.Children.Add(account);
        ((Border)account.FindName("PanButtons")).Opacity = 1;
        ((TextBlock)account.FindName("TextName")).Text = "示例账号";
        try {
            foreach (double height in new[] { 360d, 400d, 470d, 620d }) {
                left.Measure(new Size(300,height)); left.Arrange(new Rect(0,0,300,height)); left.UpdateLayout();
                foreach (string name in new[] { "BtnSkin", "BtnEdit", "BtnExit" }) {
                    var button = (FrameworkElement)account.FindName(name);
                    foreach (double fraction in new[] { 0.25,0.5,0.75 }) {
                        Point point = button.TranslatePoint(new Point(button.ActualWidth*fraction,button.ActualHeight*fraction),left);
                        DependencyObject? hit = VisualTreeHelper.HitTest(left,point)?.VisualHit;
                        while (hit is not null && !ReferenceEquals(hit,button)) hit = VisualTreeHelper.GetParent(hit);
                        Native.Check(ReferenceEquals(hit,button), $"Account action {name} was clipped or covered at launch-page height {height} (point {point}).");
                    }
                }
                string? preview = Environment.GetEnvironmentVariable("PCLLF_PERSONAL_OPTIONS_PREVIEW_DIR");
                if (!string.IsNullOrWhiteSpace(preview)) SaveImage(left,300,(int)height,preview,$"account-actions-{height}.png");
            }
        } finally { panel.Children.Remove(account); }
        left.Measure(new Size(300,620)); left.Arrange(new Rect(0,0,300,620)); left.UpdateLayout();
    }
    private static void SaveImage(FrameworkElement view,int width,int height,string directory,string name) {
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory,name)); png.Save(file);
    }
    private sealed class PreferenceStore : IDisposable {
        private readonly object host;
        private readonly Func<string,string,string?,string?> read;
        private readonly Action<string,string,string?> write;
        private readonly Dictionary<(string Path,string Key),string?> values = new();
        internal PreferenceStore() {
            object backend = Native.Field(typeof(Settings),"Backend").GetValue(null)!;
            host=Native.Get(backend,"host")!;
            read=(Func<string,string,string?,string?>)Native.Get(host,"ReadRegistry")!; write=(Action<string,string,string?>)Native.Get(host,"WriteRegistry")!;
            Native.Set(host,"ReadRegistry",new Func<string,string,string?,string?>((path,key,fallback)=>key==Preference?values.GetValueOrDefault((path,key),fallback):read(path,key,fallback)));
            Native.Set(host,"WriteRegistry",new Action<string,string,string?>((path,key,value)=> { if(key==Preference)values[(path,key)]=value;else write(path,key,value); }));
        }
        public void Dispose() { Native.Set(host,"ReadRegistry",read); Native.Set(host,"WriteRegistry",write); }
    }
}
