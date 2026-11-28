using System.Collections;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using PCL;
using PCLCS;
using System.Windows;
using System.Windows.Controls;

namespace PCLLF.Native.Facade.Tests;
// Test-InstalledComponentLoading.ps1 through the genuine Main/BAML/controller,
// actual Fabric metadata ZIP reader and actual Main LoaderTask. No product startup.
internal static class ToolboxComponentsChecks
{
    private const string Script = "Test-InstalledComponentLoading.ps1";
    private static void C(int line, bool value, string message) => ToolboxChecks.Check(Script, line, value, message);
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Data, "toolbox-components-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, ".minecraft", "versions", "fabric"), mods = Path.Combine(folder, "mods");
        Directory.CreateDirectory(Path.Combine(folder, "PCL")); Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(folder, "PCL", "Setup.ini"), "VersionArgumentIndieV2:True\r\n");
        using (ZipArchive zip = ZipFile.Open(Path.Combine(mods, "fabric-api.jar"), ZipArchiveMode.Create))
        using (StreamWriter writer = new(zip.CreateEntry("fabric.mod.json").Open())) writer.Write("{\"id\":\"fabric-api\",\"version\":\"0.155.2\"}");
        ModMinecraft.McInstance instance = new(folder) { Version = new McVersion("26.2", true) };
        instance.Version.ModLoaderVersions[ModLoaders.Fabric] = "0.19.3";
        ModMinecraft.McInstance previous = LauncherInstanceContext.Instance;
        List<Action> restore = new();
        PageInstanceOverall? page = null;
        ModLoader.LoaderBase? loader = null;
        FieldInfo? loadField = null; Delegate? originalLoad = null;
        ConcurrentBag<Thread> threads = new();
        try
        {
            foreach (string name in new[] { "LivingModpackExported", "LivingModpackMode" })
            {
                IDictionary cache = (IDictionary)Native.Get(Native.Entries[name]!, "ValueCache")!;
                bool had = cache.Contains(""); object? value = cache[""];
                restore.Add(() => { if (had) cache[""] = value; else cache.Remove(""); }); cache[""] = true;
            }
            foreach (Settings.Setting setting in Settings.Entries.Values)
                if (setting.Source == Settings.Sources.Instance)
                {
                    IDictionary cache = (IDictionary)setting.ValueCache; string path = instance.PathVersion;
                    bool had = cache.Contains(path); object? value = cache[path];
                    restore.Add(() => { if (had) cache[path] = value; else cache.Remove(path); });
                    cache[path] = setting.DefaultValue;
                }
            Settings.Entries["VersionArgumentIndieV2"].ValueCache[instance.PathVersion] = true;
            LauncherInstanceContext.Instance = instance;
            page = new PageInstanceOverall();
            Native.Check(page.GetType().Assembly == Native.Main, "Installed overview is not the real Main page.");
            StackPanel panel = (StackPanel)page.FindName("PanInstalledComponents");
            FrameworkElement content = (FrameworkElement)page.FindName("PanBack");
            loader = (ModLoader.LoaderBase)Native.Get(page, "ComponentsLoader")!;
            loadField = Native.Field(loader.GetType(), "LoadDelegate"); originalLoad = (Delegate)loadField.GetValue(loader)!;
            Delegate observe = (Delegate)typeof(ToolboxComponentsChecks).GetMethod(nameof(ObserveRead), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(loadField.FieldType.GetGenericArguments()[0]).Invoke(null, new object[] { originalLoad, threads })!;
            Native.Check(loadField.FieldType.IsInstanceOfType(observe), "Installed component observer changed the actual loader delegate type.");
            loadField.SetValue(loader, observe);
            Native.Call(page.GetType(), "RefreshInstalledComponents", page);
            int initial = panel.Children.Count; Visibility initialVisibility = content.Visibility;
            bool partial = initial > 0 && initial < 3 && content.Visibility == Visibility.Visible;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (panel.Children.Count != 3 && DateTime.UtcNow < deadline)
            {
                ToolboxChecks.Drain(deadline);
                if (panel.Children.Count > 0 && panel.Children.Count < 3 && content.Visibility == Visibility.Visible) partial = true;
                Thread.Sleep(5);
            }
            C(60, panel.Children.Count == 3, "Complete installed components were not delivered: " + loader.Error);
            C(61, !partial, "Overview exposed a partial component list: initial=" + initial + ", final=" + panel.Children.Count + ", content=" + initialVisibility);
            C(62, initial == 3 || initialVisibility == Visibility.Collapsed, "Overview was shown before its complete component snapshot was ready.");
            C(63, string.Join(',', panel.Children.Cast<Grid>().Select(card => (string)card.Tag)) == "Minecraft,Fabric,Fabric API", "Completed overview omitted installed Fabric API.");
            FieldInfo revision = Native.Field(page.GetType(), "ComponentRefreshId"); int current = (int)revision.GetValue(page)!;
            panel.Children.Clear(); revision.SetValue(page, current + 1);
            Native.Call(page.GetType(), "InstalledComponentsLoaded", page, loader);
            C(71, panel.Children.Count == 0, "A result from a previous refresh was published.");
            revision.SetValue(page, current); LauncherInstanceContext.Instance = null!;
            Native.Call(page.GetType(), "InstalledComponentsLoaded", page, loader);
            C(75, panel.Children.Count == 0, "A previous instance result was published.");
            LauncherInstanceContext.Instance = instance;
            Native.Call(page.GetType(), "InstalledComponentsLoaded", page, loader);
            C(78, panel.Children.Count == 3, "Current complete result was not published.");
            Native.Cache("LivingModpackMode", false);
            Native.Call(page.GetType(), "RefreshInstalledComponents", page);
            Native.Call(page.GetType(), "RefreshModpackVisibility", page);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            C(83, content.Visibility == Visibility.Visible && panel.Visibility == Visibility.Collapsed, "Ordinary overview remained behind the component loader.");
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
        finally
        {
            try
            {
                page?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                if (loader is not null)
                {
                    FieldInfo last = Native.Field(loader.GetType(), "LastRunningThread");
                    if (last.GetValue(loader) is Thread active) threads.Add(active);
                    if (loader.State == ModBase.LoadState.Loading) loader.Cancel();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (true)
                    {
                        if (last.GetValue(loader) is Thread thread) threads.Add(thread);
                        ToolboxChecks.Drain(deadline);
                        if (loader.State != ModBase.LoadState.Loading && threads.All(thread => !thread.IsAlive))
                        {
                            ToolboxChecks.Drain(deadline);
                            if (loader.State != ModBase.LoadState.Loading && threads.All(thread => !thread.IsAlive) && (last.GetValue(loader) as Thread)?.IsAlive != true) break;
                        }
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Actual installed-component loader/thread/UI posts did not stop.");
                        Thread.Sleep(2);
                    }
                    loadField?.SetValue(loader, originalLoad);
                }
                // Restore only after actual IO and terminal UI callbacks end.
                LauncherInstanceContext.Instance = previous;
                for (int index = restore.Count - 1; index >= 0; index--) restore[index]();
                environment.AssertNoForbiddenIo();
            }
            catch (Exception error) { ToolboxChecks.FailClosed(error); }
        }
        Console.WriteLine("PASS native toolbox installed-components: original complete-before-display, real local JAR scan, stale revision/owner gates and ordinary-mode entry.");
    }
    private static Action<TTask> ObserveRead<TTask>(Delegate original, ConcurrentBag<Thread> threads) => task =>
    {
        threads.Add(Thread.CurrentThread); ((Action<TTask>)original)(task);
    };
}
