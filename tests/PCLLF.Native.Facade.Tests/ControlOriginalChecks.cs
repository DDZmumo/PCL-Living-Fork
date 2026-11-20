using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Windows;
using PCL;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Foundation;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main's original three control identities; this is never a Modern-view
// substitute or a claim that a sidecar-backed Facade is a native cold process.
internal static class ControlOriginalChecks
{
    private static readonly HashSet<int> Expected = new() { 60, 69, 79, 81, 86, 89, 102, 104, 108, 109, 112, 119, 127, 135, 144, 147, 150, 157, 167, 169, 172, 175, 177, 193, 195, 197, 199, 201, 204, 208, 223, 226, 229, 231, 233, 236, 238, 239, 241, 242, 244, 249, 269 };
    private static readonly HashSet<int> Seen = new();
    private static readonly Dictionary<int,int> Counts = new();
    private static readonly Dictionary<int,int> RepeatedMinimum = new() { [60] = 20, [79] = 3, [81] = 3, [86] = 3, [89] = 14, [102] = 5, [104] = 5, [108] = 10, [109] = 3, [249] = 7 };
    private static readonly HashSet<int> Groups = new();
    private static int executions;
    internal static void Run(FixtureEnvironment environment)
    {
        VisualAssetChecks.VerifyChild(environment);
        Native.Check(OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Application.Current.Dispatcher.CheckAccess(), "Actual Main control acceptance requires Windows STA.");
        Seen.Clear(); Counts.Clear(); Groups.Clear(); executions = 0;
        using (Scope scope = new(environment))
        {
            VerifyActualBindings();
            ControlMigratedChecks.Run();
            Native.Check(Seen.SetEquals(Expected), "Missing original Main-control assertions: " + string.Join(',', Expected.Except(Seen)));
            foreach (var (line, count) in RepeatedMinimum) Native.Check(Counts.GetValueOrDefault(line) >= count, "Original repeated control assertions missing: " + line + " minimum " + count);
            Native.Check(Groups.SetEquals(new[] { 1, 2, 3, 4, 5, 6 }), "Incomplete original Main-control groups.");
            environment.AssertNoForbiddenIo();
        }
        Console.WriteLine("PASS migrated-controls: all43 original actual Main assertion sites across six groups (" + executions + " executions), real BAML/DP/layout/settings/preview/cancel/events/AniTimer/color behavior. Native single-file cold/embedded identity gates remain separate; no human hover/focus/visual-feel completion inferred.");
    }
    internal static void Check(int line, bool value, string message)
    {
        Native.Check(Expected.Contains(line), "Unknown original control assertion ID: " + line); Seen.Add(line); Counts[line] = Counts.GetValueOrDefault(line) + 1; executions++;
        Native.Check(value, "Test-MigratedControls.ps1:" + line + " " + message);
    }
    internal static void Group(int group) => Native.Check(Groups.Add(group), "Original control group executed twice: " + group);
    private static void VerifyActualBindings()
    {
        Native.Check(Native.Main.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName == ".NETCoreApp,Version=v10.0", "Control subject must be the genuine modern NET10 Main, never the former Framework product.");
        foreach (Type control in new[] { typeof(MyButton), typeof(MyRadioButton), typeof(MyCheckBox) })
            Native.Check(control.Assembly == Native.Main, "Original control bound away from actual Main: " + control.FullName);
        FieldInfo bound = (FieldInfo)Native.Field(typeof(ControlMigratedChecks), "LastTick").GetValue(null)!;
        Native.Check(bound.DeclaringType!.Assembly == Native.Main, "Compiled control verifier did not bind original AniLastTick to actual Main.");
        VerifyDependency("PCLLF.Presentation", typeof(MyButtonView).Assembly);
        VerifyDependency("PCLLF.WPF.Core", typeof(CustomEventCollection).BaseType!.Assembly);
        string mainReference = Path.Combine(AppContext.BaseDirectory, "LivingPCL.dll");
        VerifyMetadata(File.ReadAllBytes(mainReference), Native.Main, "actual Main compiler/staged reference");
        Native.Check(Native.Full(Native.Main.Location) == Native.Full(mainReference), "Actual Main was rebound outside its own isolated staged reference.");
    }
    private static void VerifyDependency(string key, Assembly loaded)
    {
        Native.Check(loaded.GetName().Name == key, "Unexpected actual control dependency: " + loaded.FullName);
        string reference = Path.Combine(AppContext.BaseDirectory, key + ".dll");
        byte[] bytes = File.ReadAllBytes(reference);
        Native.Check(bytes.Length > 0 && Native.Full(loaded.Location) == Native.Full(reference), "Actual Main dependency did not use the staged build reference: " + key);
        VerifyMetadata(bytes, loaded, key);
        Console.WriteLine("PASS actual Main control binding " + key + ": dependency SHA256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ", runtime MVID=" + loaded.ManifestModule.ModuleVersionId + "; facade runtime Location=" + loaded.Location + ". Native cold/memory-only loading is validated by its separate genuine EXE gate.");
    }
    private static void VerifyMetadata(byte[] bytes, Assembly loaded, string label)
    {
        using MemoryStream stream = new(bytes, false); using PEReader pe = new(stream); MetadataReader reader = pe.GetMetadataReader();
        AssemblyDefinition definition = reader.GetAssemblyDefinition(); AssemblyName runtime = loaded.GetName();
        byte[] key = definition.PublicKey.IsNil ? Array.Empty<byte>() : reader.GetBlobBytes(definition.PublicKey);
        Native.Check(reader.GetString(definition.Name) == runtime.Name && definition.Version == runtime.Version && reader.GetString(definition.Culture) == (runtime.CultureName ?? "") && key.SequenceEqual(runtime.GetPublicKey() ?? Array.Empty<byte>()) && reader.GetGuid(reader.GetModuleDefinition().Mvid) == loaded.ManifestModule.ModuleVersionId, "Runtime identity/MVID differs from genuine embedded/compiler metadata: " + label);
    }
    private sealed class Scope : IDisposable
    {
        private readonly FixtureEnvironment environment;
        private readonly Stack<Action> restore = new();
        private readonly string root;
        internal Scope(FixtureEnvironment environment)
        {
            this.environment = environment; root = Path.Combine(environment.Data, "migrated-controls");
            Native.Check(!Directory.Exists(root), "Refusing to adopt an existing control fixture."); Directory.CreateDirectory(root);
            try
            {
                Native.Check(!ModAnimation.AniRunning, "Manual control ticks cannot adopt a live LF animation driver.");
                Dictionary<string, ModAnimation.AniGroupEntry> groups = new(ModAnimation.AniGroups);
                restore.Push(() => { ModAnimation.AniGroups.Clear(); foreach (var pair in groups) ModAnimation.AniGroups.Add(pair.Key, pair.Value); Native.Check(groups.Count == ModAnimation.AniGroups.Count && groups.All(pair => ReferenceEquals(ModAnimation.AniGroups[pair.Key], pair.Value)), "Original animation groups did not restore exact identity."); });
                ModAnimation.AniGroups.Clear(); // Manual ticks must not execute callbacks owned by another selected group.
                Preserve(typeof(ModAnimation), "AniLastTick");
                int suppression = ModAnimation.AniControlEnabled; restore.Push(() => ModAnimation.AniControlEnabled = suppression);
                foreach (DictionaryEntry pair in Native.Entries)
                {
                    IDictionary cache = (IDictionary)Native.Get(pair.Value!, "ValueCache")!; List<DictionaryEntry> before = new();
                    foreach (DictionaryEntry entry in cache) before.Add(entry);
                    restore.Push(() => { cache.Clear(); foreach (DictionaryEntry entry in before) cache[entry.Key] = entry.Value; Native.Check(cache.Count == before.Count && before.All(entry => cache.Contains(entry.Key) && ReferenceEquals(cache[entry.Key], entry.Value)), "Original control setting cache did not restore exact keys/objects."); });
                }
                PropertyInfo dataRoot = Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot"); object? previousRoot = dataRoot.GetValue(null);
                restore.Push(() => dataRoot.SetValue(null, previousRoot)); dataRoot.SetValue(null, Path.Combine(root, "profile"));
                Application app = Application.Current; ResourceDictionary beforeResources = app.Resources; ResourceDictionary isolated = new();
                foreach (DictionaryEntry entry in beforeResources) isolated[entry.Key] = entry.Value is Freezable value ? value.CloneCurrentValue() : entry.Value;
                foreach (ResourceDictionary merged in beforeResources.MergedDictionaries) isolated.MergedDictionaries.Add(merged);
                restore.Push(() => app.Resources = beforeResources); app.Resources = isolated;
                Window? main = app.MainWindow; ShutdownMode shutdown = app.ShutdownMode;
                restore.Push(() => { app.MainWindow = main; app.ShutdownMode = shutdown; }); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Type theme = Native.Type("PCL.ModSecret"); object palette = Native.Field(theme, "PaletteService").GetValue(null)!;
                bool dark = LegacyThemePalette.IsDarkTheme;
                foreach (FieldInfo field in theme.GetFields(Native.Flags).Where(f => f.IsStatic && !f.IsInitOnly && !f.IsLiteral && f.Name.StartsWith("Color", StringComparison.Ordinal))) Preserve(field.DeclaringType!, field.Name);
                restore.Push(() => Native.Call(palette.GetType(), "PrepareThemeTones", palette, dark));
                Native.Cache("UiLauncherColorMode", 0); Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
                ModBase.RunInUi(); // Initialize the genuine Main UI-thread identity before real manual animation ticks.
            }
            catch { Restore(); throw; }
        }
        private void Preserve(Type type, string name) { FieldInfo field = Native.Field(type, name); object? previous = field.GetValue(null); restore.Push(() => field.SetValue(null, previous)); }
        private void Restore()
        {
            List<Exception> errors = new(); while (restore.Count > 0) { try { restore.Pop()(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count > 0) throw new AggregateException("Main control scope restoration failed.", errors);
        }
        public void Dispose()
        {
            try
            {
                // Original Run closes the real Window and removes its dependency-
                // property observer. Drain deferred Loaded/Unloaded UI work before
                // canceling any last group and restoring roots/resources/cache.
                VisualAssetChecks.Drain();
                Native.Check(Application.Current.Windows.Count == 0 && !ModAnimation.AniRunning, "Main control fixture left a live Window/animation driver.");
                ModAnimation.AniGroups.Clear();
                object gate = Native.Field(Native.Type("PCL.LocalGameData"), "Gate").GetValue(null)!;
                if (!Monitor.TryEnter(gate, 3000)) throw new TimeoutException("Actual control personal-data IO did not stop.");
                Monitor.Exit(gate);
                environment.AssertNoForbiddenIo();
            }
            catch (Exception error) { VisualAssetChecks.FailClosed(environment, "Control producer/UI cleanup failed before state restore", error); }
            Restore();
        }
    }
}
