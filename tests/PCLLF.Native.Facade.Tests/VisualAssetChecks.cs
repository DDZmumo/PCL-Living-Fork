using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCL;
using Application = System.Windows.Application;
using PCLLF.Presentation.Foundation;

namespace PCLLF.Native.Facade.Tests;

// Native Main is the subject. Original VB/source-mode logo and MyBitmap oracles
// remain independently pinned in their existing jobs; this family does not replace them.
internal static class VisualAssetChecks
{
    private static readonly Dictionary<string, HashSet<int>> Seen = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Family,int Line),int> Executions = new();
    internal static void Run(FixtureEnvironment environment)
    {
        VerifyChild(environment);
        Native.Check(OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Application.Current.Dispatcher.CheckAccess(), "Actual Windows STA required for visual assets.");
        Seen.Clear(); Executions.Clear();
        DesktopCompatibilityChecks.WebP(environment.Root);
        using (Scope scope = new(environment))
        {
            VisualAssetBackgroundChecks.Run(scope);
            VisualAssetGalleryChecks.Run(scope);
            VisualAssetThumbnailChecks.Run(scope);
            VisualAssetIconChecks.Run(scope);
            foreach (var (family, minimum) in VisualAssetOriginalChecks.Minimum)
                foreach (var (line, count) in minimum)
                    Native.Check(Executions.TryGetValue((family,line), out int actualCount) && actualCount >= count, "Missing original visual loop execution: " + family + ":" + line + " required " + count);
            foreach (var (family, lines) in VisualAssetOriginalChecks.Expected)
                Native.Check(Seen.TryGetValue(family, out HashSet<int>? actual) && actual.SetEquals(lines), "Missing original visual asset assertion sites: " + family + " " + string.Join(',', lines.Except(actual ?? new())));
        }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS visual-assets: all original gallery/background/icon and real thumbnail-controller assertions; actual Main cards, image loader, settings page, URI repair and instance/cache paths. Independent original source oracle remains separate; no external viewer/game/profile used.");
    }
    internal static void Check(string family, int line, bool condition, string detail = "")
    {
        Native.Check(VisualAssetOriginalChecks.Expected.TryGetValue(family, out HashSet<int>? ids) && ids.Contains(line), "Unknown visual assertion ID: " + family + ':' + line);
        if (!Seen.TryGetValue(family, out HashSet<int>? seen)) Seen[family] = seen = new();
        seen.Add(line); Executions[(family,line)] = Executions.GetValueOrDefault((family,line)) + 1;
        Native.Check(condition, family + ':' + line + " " + detail);
    }
    internal static void VerifyChild(FixtureEnvironment environment)
    {
        string[] args = Environment.GetCommandLineArgs(); int option = Array.IndexOf(args, "--fixture");
        Native.Check(Assembly.GetEntryAssembly() == typeof(VisualAssetChecks).Assembly && typeof(VisualAssetChecks).Assembly.GetName().Name == "PCLLF.Native.Facade.Tests" && args.Contains("--isolated", StringComparer.Ordinal) && option >= 0 && option + 1 < args.Length, "Visual cleanup requires the genuine isolated Facade child.");
        Program.ValidateRoot(args[option + 1]);
        Native.Check(Native.Full(environment.Root) == Native.Full(args[option + 1]) && Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(environment.Root, "output")), "Visual child/output/fixture mismatch.");
    }
    internal static void FailClosed(FixtureEnvironment environment, string message, Exception? error = null)
    {
        VerifyChild(environment);
        Console.Error.WriteLine("FAIL visual-assets cleanup before state restoration: " + message + " " + error);
        Console.Error.Flush(); Console.Out.Flush();
        Environment.Exit(95); // Never restore caches/root while real image work can still run.
        throw new InvalidOperationException(message, error);
    }
    internal static void Pump(Func<bool> done, string reason, int timeout = 6000)
    {
        if (done()) return;
        bool complete = false, stopped = false; Exception? failure = null;
        object gate = new(); Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new(); DispatcherOperation? deadlineOperation = null;
        void Sample(bool deadline)
        {
            try { complete = done(); }
            catch (Exception error) { lock (gate) failure ??= error; }
            if (complete || failure is not null || deadline) frame.Continue = false;
        }
        DispatcherTimer observer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        EventHandler observed = (_, _) => Sample(false);
        observer.Tick += observed;
        // A ThreadPool timer posts directly at Send. Unlike a Background
        // DispatcherTimer, its deadline does not need the starving queue to
        // sample the clock or promote a WM_TIMER before requesting frame exit.
        using System.Threading.Timer deadlineTimer = new(_ =>
        {
            lock (gate)
            {
                if (stopped) return;
                try { deadlineOperation = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Sample(true))); }
                catch (Exception error) { failure ??= error; }
            }
        }, null, timeout, Timeout.Infinite);
        try { observer.Start(); Dispatcher.PushFrame(frame); }
        finally
        {
            DispatcherOperation? operation;
            lock (gate) { stopped = true; operation = deadlineOperation; }
            deadlineTimer.Dispose(); observer.Stop(); observer.Tick -= observed;
            if (operation?.Status == DispatcherOperationStatus.Pending) operation.Abort();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Native.Check(complete, "Bounded visual wait failed: " + reason);
    }
    internal static void Observe(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew(); Pump(() => timer.ElapsedMilliseconds >= milliseconds, "observation interval", milliseconds + 1500);
    }
    internal static void Drain()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int pass = 0; pass != 2; ++pass)
        {
            int remaining = 3000 - (int)elapsed.ElapsedMilliseconds;
            Native.Check(remaining > 0, "Dispatcher barriers exceeded their common deadline.");
            bool idle = false;
            DispatcherOperation operation = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => idle = true));
            try { Pump(() => idle, "Dispatcher completion barrier", remaining); }
            finally { if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort(); }
        }
    }
    internal static T Find<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Real Main XAML field missing: " + name);
    internal static void WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DrawingVisual visual = new(); using (DrawingContext drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 96, 64));
        RenderTargetBitmap bitmap = new(96, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); Save(bitmap, path);
    }
    internal static void Save(BitmapSource bitmap, string path)
    {
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using FileStream stream = File.Create(path); encoder.Save(stream);
    }
    internal static void Render(FrameworkElement element, string path, int width, int height)
    {
        element.Width = width; element.Height = height; element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element); Save(bitmap, path);
        Native.Check(new FileInfo(path).Length > 0, "Visual snapshot empty.");
    }
    internal sealed class Scope : IDisposable
    {
        internal readonly FixtureEnvironment Environment;
        internal readonly string Root;
        private readonly Stack<Action> restore = new();
        private readonly HashSet<MeloongCore.JsonConfigProvider> originalProviders = new();
        internal Scope(FixtureEnvironment environment, string dataDirectory = "visual-assets")
        {
            Environment = environment; Root = Path.Combine(environment.Data, dataDirectory);
            Native.Check(!Directory.Exists(Root), "Refusing to adopt existing visual data."); Directory.CreateDirectory(Root);
            try
            {
                foreach (var weak in MeloongCore.JsonConfigProvider.allProviders)
                    if (weak.TryGetTarget(out var provider)) originalProviders.Add(provider);
                Application app = Application.Current; ResourceDictionary original = app.Resources; ResourceDictionary isolated = new();
                foreach (DictionaryEntry entry in original) isolated[entry.Key] = entry.Value is Freezable value ? value.CloneCurrentValue() : entry.Value;
                foreach (ResourceDictionary merged in original.MergedDictionaries) isolated.MergedDictionaries.Add(merged);
                restore.Push(() => app.Resources = original); app.Resources = isolated;
                bool dark = LegacyThemePalette.IsDarkTheme;
                object palette = Native.Field(Native.Type("PCL.ModSecret"), "PaletteService").GetValue(null)!;
                foreach (FieldInfo field in Native.Type("PCL.ModSecret").GetFields(Native.Flags).Where(f => f.IsStatic && !f.IsInitOnly && !f.IsLiteral && f.Name.StartsWith("Color", StringComparison.Ordinal))) Preserve(field);
                restore.Push(() => Native.Call(palette.GetType(), "PrepareThemeTones", palette, dark));
                Preserve(typeof(ModAnimation).GetField("AniLastTick", Native.Flags)!);
                int suppression = ModAnimation.AniControlEnabled; restore.Push(() => ModAnimation.AniControlEnabled = suppression);
                Dictionary<string, ModAnimation.AniGroupEntry> groups = new(ModAnimation.AniGroups);
                restore.Push(() => { ModAnimation.AniGroups.Clear(); foreach (var entry in groups) ModAnimation.AniGroups.Add(entry.Key, entry.Value); });
                Window? main = app.MainWindow; ShutdownMode shutdown = app.ShutdownMode; restore.Push(() => { app.MainWindow = main; app.ShutdownMode = shutdown; }); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                foreach (DictionaryEntry pair in Native.Entries)
                {
                    IDictionary cache = (IDictionary)Native.Get(pair.Value!, "ValueCache")!;
                    List<DictionaryEntry> before = new(); foreach (DictionaryEntry item in cache) before.Add(item);
                    restore.Push(() => {
                        cache.Clear(); foreach (DictionaryEntry item in before) cache[item.Key] = item.Value;
                        Native.Check(cache.Count == before.Count && before.All(item => cache.Contains(item.Key) && ReferenceEquals(cache[item.Key], item.Value)), "Visual settings cache did not restore exact original keys/objects.");
                    });
                }
                IDictionary ini = (IDictionary)Native.Field(typeof(ModBase), "IniCache").GetValue(null)!;
                bool Owned(object key) => key is string path && Native.Full(path).StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                List<DictionaryEntry> iniBefore = new(); foreach (DictionaryEntry item in ini) if (Owned(item.Key)) iniBefore.Add(item);
                restore.Push(() => {
                    foreach (object key in ini.Keys.Cast<object>().ToArray()) if (Owned(key)) ini.Remove(key);
                    foreach (DictionaryEntry item in iniBefore) ini[item.Key] = item.Value;
                    Native.Check(iniBefore.All(item => ini.Contains(item.Key) && ReferenceEquals(ini[item.Key], item.Value)), "Visual INI cache did not restore exact preexisting owned objects.");
                });
                ModBase.RunInUi();
                // Reproduce the old light-mode fixture explicitly even when another selected group left a dark cache.
                Native.Cache("UiLauncherColorMode", 0); Native.Cache("UiBackgroundColorful", true); Native.Cache("UiBackgroundMode", 0);
                Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
            }
            catch { Restore(); throw; }
        }
        private void Preserve(FieldInfo field) { object? previous = field.GetValue(null); restore.Push(() => field.SetValue(null, previous)); }
        private void Restore()
        {
            List<Exception> errors = new();
            while (restore.Count > 0) { try { restore.Pop()(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count > 0) throw new AggregateException("Visual scope restoration failed.", errors);
        }
        private void FinishOwnedProviders()
        {
            PCLCS.InstanceConfigProviders.FlushAndDisposeWithin(Root);
            Stopwatch deadline = Stopwatch.StartNew();
            foreach (var weak in MeloongCore.JsonConfigProvider.allProviders)
            {
                if (!weak.TryGetTarget(out var provider) || originalProviders.Contains(provider)) continue;
                string file = (string)Native.Get(provider, "filePath")!;
                if (!Native.Full(file).StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                // The icon paths read only Config. Stop the actual debounce
                // producer and acquire the same monitor used for a real Save,
                // so no config IO can race root/cache restoration.
                int remaining = 3000 - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0 || !Monitor.TryEnter(provider, remaining)) throw new TimeoutException("Owned visual config saves exceeded their common monitor deadline.");
                try
                {
                    ((IDisposable)Native.Get(provider, "saveWorker")!).Dispose();
                    Native.Check(!(bool)Native.Get(provider, "isDirty")!, "Read-only visual instance fixture unexpectedly queued a config mutation.");
                }
                finally { Monitor.Exit(provider); }
            }
        }
        public void Dispose()
        {
            try { FinishOwnedProviders(); Drain(); Native.Check(Application.Current.Windows.Count == 0, "Visual tests left a real window open."); }
            catch (Exception error) { FailClosed(Environment, "Final UI barrier failed", error); }
            Restore();
        }
    }
}
