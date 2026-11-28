using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL;
using PCLCS;

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

    private static void LaunchToggles() {
        using var view = new PCLLF.Wpf.Glass.GlassLauncherView();
        Native.Check(view.FindName("LaunchButton") is Button, "The active launcher lost its native launch button.");
    }

    private static void LaunchDrainUi()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
