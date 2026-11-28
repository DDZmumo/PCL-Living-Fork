using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL;
using PCLCS;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static void LaunchLoginPort()
    {
        Type input = Native.Type("PCL.ModLaunch+McLoginLegacy"), result = Native.Type("PCL.ModLaunch+McLoginResult");
        Type loaderType = typeof(ModLoader.LoaderTask<,>).MakeGenericType(input, result);
        object loader = Activator.CreateInstance(loaderType)!;
        MethodInfo factory = Native.Method(Native.Type("PCL.ModLaunch"), "LoginPort").MakeGenericMethod(input);
        object port = factory.Invoke(null, [loader])!;
        object live = Native.Property(port.GetType(), "Output").GetValue(port)!;
        LD(39, ReferenceEquals(Native.Get(port, "Source"), loader));
        string[] names = ["Name", "Uuid", "AccessToken", "Type", "ClientToken", "ProfileJson"];
        foreach (string name in names) Native.Property(live.GetType(), name).SetValue(live, "port-" + name);
        FieldInfo output = Native.Field(loaderType, "Output"); object actual = output.GetValue(loader)!;
        foreach (string name in names) LD(43, (string)Native.Get(actual, name)! == "port-" + name, name);
        object direct = Activator.CreateInstance(result)!;
        foreach (string name in names) Native.Set(direct, name, "direct-" + name);
        output.SetValue(loader, direct);
        foreach (string name in names) LD(47, (string)Native.Property(live.GetType(), name).GetValue(live)! == "direct-" + name, name);
        LegacyLaunchLoginResult replacement = new();
        foreach (string name in names) Native.Property(replacement.GetType(), name).SetValue(replacement, "whole-" + name);
        Native.Property(port.GetType(), "Output").SetValue(port, replacement); actual = output.GetValue(loader)!;
        foreach (string name in names) LD(52, (string)Native.Get(actual, name)! == "whole-" + name, name);
        LD(53, ReferenceEquals(live, Native.Property(port.GetType(), "Output").GetValue(port)));
        object actualInput = Activator.CreateInstance(input)!; Native.Set(loader, "Input", actualInput);
        LD(55, ReferenceEquals(actualInput, Native.Property(port.GetType(), "Input").GetValue(port)));
        Native.Property(port.GetType(), "State").SetValue(port, LegacyLaunchState.Loading);
        Native.Property(port.GetType(), "Progress").SetValue(port, 0.375);
        LD(60, (double)Native.Property(loaderType, "Progress").GetValue(loader)! == 0.375 && Native.Property(loaderType, "State").GetValue(loader)!.ToString() == "Loading" && Native.Get(loader, "LastRunningThread") is null);
        Native.Property(port.GetType(), "State").SetValue(port, LegacyLaunchState.Waiting);
        LD(62, Native.Property(loaderType, "State").GetValue(loader)!.ToString() == "Waiting" && (double)Native.Property(port.GetType(), "Progress").GetValue(port)! == 0 && Native.Get(loader, "LastRunningThread") is null);
    }

    private static T LaunchPart<T>(FrameworkElement view, string name) where T : class
        => view.FindName(name) as T ?? throw new InvalidOperationException("Actual native BAML part missing: " + view.GetType().Name + "." + name);

    private static void ShowDependencyOptions(GlassLauncherView view, bool recentWorld = false) =>
        view.SetState(new GlassLauncherViewState {
            HasInstance = true, InstanceName = "1.21.1", CanLaunch = true, ReduceMotion = true,
            DependencyCheckEnabled = Settings.Get<bool>("LaunchModDependencyCheck"),
            AutoFillEnabled = Settings.Get<bool>("LaunchModDependencyAutoFill"),
            RecentWorldAvailable = recentWorld, RecentWorldName = "新的世界", RecentWorldDetail = "1.21.1 · 测试版本"
        });

    private static void LaunchToggles()
    {
        var checkEntry = Settings.Entries["LaunchModDependencyCheck"];
        var autoEntry = Settings.Entries["LaunchModDependencyAutoFill"];
        object previousCheck = checkEntry.ValueCache[""], previousAuto = autoEntry.ValueCache[""];
        var owner = FileTaskShell.Install();
        using var view = new GlassLauncherView();
        int refreshRequests = 0;
        using var runtime = new GlassShellRuntime(owner, view, () => refreshRequests++);
        var window = new Window { Content = view, Width = 1100, Height = 720, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            Native.Cache("LaunchModDependencyCheck", true); Native.Cache("LaunchModDependencyAutoFill", true);
            ShowDependencyOptions(view);
            var master = LaunchPart<CheckBox>(view, "DependencyCheckBox");
            var auto = LaunchPart<CheckBox>(view, "AutoFillCheckBox");
            TD(20, master.IsChecked == true && auto.IsChecked == true && auto.Visibility == Visibility.Visible);
            ShowDependencyOptions(view, recentWorld: true);
            window.Show();
            var expand = LaunchPart<Button>(view, "ExpandButton");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, expand));
            window.UpdateLayout(); LaunchDrainUi();
            TD(27, master.IsLoaded && master.IsVisible);
            var card = LaunchPart<FrameworkElement>(view, "DependencyCard");
            var recent = LaunchPart<FrameworkElement>(view, "RecentWorldCard");
            foreach (bool value in new[] { false, true, false, true })
            {
                // Deliberate no-op settings writes still have to update the dependency option immediately.
                Native.Cache("LaunchModDependencyCheck", value);
                master.IsChecked = value;
                window.UpdateLayout(); LaunchDrainUi();
                TD(33, auto.Visibility == (value ? Visibility.Visible : Visibility.Collapsed));
                Rect checkBounds = card.TransformToAncestor(view).TransformBounds(new Rect(card.RenderSize));
                Rect worldBounds = recent.TransformToAncestor(view).TransformBounds(new Rect(recent.RenderSize));
                TD(35, recent.IsVisible && checkBounds.Bottom <= worldBounds.Top + 1);
                if (value) TD(36, auto.TranslatePoint(new Point(), card).Y >= master.TranslatePoint(new Point(), card).Y + master.ActualHeight);
            }
            Native.Check(refreshRequests == 4, "Live dependency toggles were not forwarded to the launcher runtime.");
            Native.Cache("LaunchModDependencyAutoFill", false); auto.IsChecked = false;
            ShowDependencyOptions(view);
            TD(40, auto.IsChecked == false && master.IsChecked == true && auto.Visibility == Visibility.Visible);
            Native.Cache("LaunchModDependencyCheck", false); master.IsChecked = false;
            ShowDependencyOptions(view); LaunchDrainUi();
            TD(43, master.IsChecked == false && auto.Visibility == Visibility.Collapsed);
            using var reopened = new GlassLauncherView();
            ShowDependencyOptions(reopened); LaunchDrainUi();
            TD(46, LaunchPart<CheckBox>(reopened, "DependencyCheckBox").IsChecked == false &&
                LaunchPart<CheckBox>(reopened, "AutoFillCheckBox") is { IsChecked: false, Visibility: Visibility.Collapsed });
            TD(47, checkEntry.DefaultValue is true && autoEntry.DefaultValue is true);
        }
        finally
        {
            window.Content = null;
            window.Close();
            Native.Cache("LaunchModDependencyCheck", previousCheck); Native.Cache("LaunchModDependencyAutoFill", previousAuto);
            LaunchDrainUi();
        }
        Console.WriteLine("PASS toggles original 20..47: actual loaded startup options, recent-world clearance and current settings through the launcher runtime.");
    }

    private static void LaunchDrainUi()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
