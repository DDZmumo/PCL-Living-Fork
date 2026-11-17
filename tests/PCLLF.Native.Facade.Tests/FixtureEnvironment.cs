using System.Collections;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;
// Only existing production platform delegates are replaced. Identity/Settings/Main stay genuine.
internal sealed class FixtureEnvironment : IDisposable
{
    private readonly Stack<Action> restore = new();
    private Application? application;
    private int forbiddenIo;
    private readonly HashSet<string> diagnosticSettingNames = new(StringComparer.Ordinal);
    private bool disposed;
    internal readonly string Root;
    internal string Data => Path.Combine(Root, "data");

    internal FixtureEnvironment(string root)
    {
        Root = root;
        Directory.CreateDirectory(Data);
        try
        {
            Native.Check(Application.Current is null, "A previous Application exists before fixture guards.");
            Native.Check(Assembly.GetEntryAssembly() == Assembly.GetExecutingAssembly() && Assembly.GetEntryAssembly() != Native.Main, "Facade host must retain its own EntryAssembly.");
            Native.Check(Native.Main.GetName().Name == "LivingPCL", "Reference is not the genuine LF Main assembly.");
            // WPF permits setting ResourceAssembly only when EntryAssembly is
            // null. This executable keeps its genuine host EntryAssembly;
            // Main's generated InitializeComponent uses its qualified component
            // URI instead. Never change WPF's static fields or fake EntryAssembly.
            InstallIdentityPrimitives(); // Before constructing the real Application, never retrospectively after an App constructor.
            InstallSettingsGuardsAndCache();
            application = (Application)(Activator.CreateInstance(Native.Type("PCL.Application"), true) ?? throw new InvalidOperationException("Real Application constructor failed."));
            AssertNoForbiddenIo();
            Native.Call(application.GetType(), "InitializeComponent", application);
            // Same real removal contract as Test-MigratedControls. The separate theme group may Show/pump; file-tasks may drain its real task callbacks after these hooks are detached.
            Native.Field(typeof(Application), "_startupUri").SetValue(application, null);
            Remove("Startup", "Application_Startup");
            Remove("DispatcherUnhandledException", "Application_DispatcherUnhandledException");
            Remove("SessionEnding", "Application_SessionEnding");
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Native.Check(ReferenceEquals(Application.ResourceAssembly, Assembly.GetEntryAssembly()) && application.StartupUri is null, "Hosted WPF resource identity or disabled StartupUri was lost.");
            VerifyMainBaml();
            object info = Native.Property(application.GetType(), "Info").GetValue(application)!;
            Native.Check((string)Native.Property(info.GetType(), "AssemblyName").GetValue(info)! == Native.Main.GetName().Name, "My.Application.Info used the TestHost entry identity.");
            string[] keys = ["Custom", "ForgeInstaller", "Help", "libwebp64", "Transformer"];
            var manager = (System.Resources.ResourceManager)Native.Property(Native.Type("PCL.My.Resources.Resources"), "ResourceManager").GetValue(null)!;
            foreach (string key in keys)
            {
                object? value = manager.GetObject(key, System.Globalization.CultureInfo.InvariantCulture);
                Native.Check(value is byte[] bytes && bytes.Length > 0, "Actual Main neutral resource missing: " + key);
            }

            Native.Check(manager.GetObject("MeloongCore.Wpf", System.Globalization.CultureInfo.InvariantCulture)is null, "Retired WPF assembly was rebound into modern resources.");
            foreach (string key in new[] { "CacheCow.Client", "CacheCow.Client.FileStore", "CacheCow.Common", "ThrottleDebounce", "Imazen.WebP", "MeloongCore", "Microsoft.Win32.Registry", "NAudio", "Newtonsoft.Json", "Ookii.Dialogs.Wpf", "PCLCS", "PCLLF.Presentation", "System.Net.Http.Formatting" })
                Native.Check(manager.GetObject(key, System.Globalization.CultureInfo.InvariantCulture) is null, "Duplicate managed payload is still embedded: " + key);
            AssertNoForbiddenIo();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void VerifyMainBaml()
    {
        Native.Check(application!.GetType().Assembly == Native.Main, "The fixture did not construct the genuine Main Application.");
        Uri component = new("/LivingPCL;component/application.xaml", UriKind.Relative);
        var resource = Application.GetResourceStream(component) ?? throw new InvalidOperationException("The actual Main component BAML is unavailable.");
        using Stream actual = resource.Stream;
        using Stream embedded = Native.Main.GetManifestResourceStream("LivingPCL.g.resources") ?? throw new InvalidOperationException("The actual Main compiled resource container is unavailable.");
        using ResourceReader reader = new(embedded);
        byte[]? expected = null;
        foreach (DictionaryEntry entry in reader)
        {
            if (!Equals(entry.Key, "application.baml")) continue;
            using Stream baml = (Stream)entry.Value!;
            using MemoryStream bytes = new();
            baml.CopyTo(bytes);
            expected = bytes.ToArray();
            break;
        }
        using MemoryStream resolved = new();
        actual.CopyTo(resolved);
        Native.Check(expected is { Length: > 0 } && resolved.ToArray().SequenceEqual(expected), "The qualified Main BAML differs from the actual Main embedded component.");
        Native.Check(application.Resources["ColorBrush1"] is SolidColorBrush role && role.Color == Color.FromRgb(52, 61, 74), "The real Main BAML role resources were not loaded.");
        Type menu = Native.Type("PCL.MyMenuItem");
        Native.Check(application.Resources[menu] is Style style && style.TargetType == menu, "The real Main BAML implicit control style was not loaded.");
    }

    private void Remove(string eventName, string methodName)
    {
        EventInfo evt = typeof(Application).GetEvent(eventName, Native.Flags) ?? throw new MissingMemberException(eventName);
        MethodInfo method = Native.Method(application!.GetType(), methodName);
        evt.RemoveEventHandler(application, Delegate.CreateDelegate(evt.EventHandlerType!, application, method));
    }

    private void RecordForbidden(string operation, string? key)
    {
        // Emit once at the attempted boundary: the real Settings engine may
        // catch this exception before the outer fixture checks the counter.
        // Only schema-known setting names are disclosed, never values, registry
        // folders, device inputs or the filename of a rejected profile access.
        if (Interlocked.Increment(ref forbiddenIo) != 1) return;
        string setting = key is not null && diagnosticSettingNames.Contains(key) ? key : "<not-a-schema-setting>";
        string stack = new System.Diagnostics.StackTrace(1, false).ToString();
        if (stack.Length > 8192) stack = stack[..8192] + "\n<truncated>";
        Console.Error.WriteLine("FORBIDDEN native facade first attempt: operation=" + operation + "; key=" + setting + "\n" + stack);
    }

    private T Forbidden<T>(string name, string? key = null)
    {
        RecordForbidden(name, key);
        throw new InvalidOperationException("Forbidden real platform access in facade fixture: " + name);
    }

    private void ForbiddenVoid(string name, string? key = null)
    {
        RecordForbidden(name, key);
        throw new InvalidOperationException("Forbidden real platform access in facade fixture: " + name);
    }

    private void Replace(object target, string name, object value)
    {
        FieldInfo field = Native.Field(target.GetType(), name);
        object? previous = field.GetValue(target);
        restore.Push(() => field.SetValue(target, previous));
        field.SetValue(target, value);
    }

    private void InstallIdentityPrimitives()
    {
        object service = Native.Field(Native.Type("PCL.ModSecret"), "IdentityService").GetValue(null) ?? throw new InvalidOperationException("Real identity service missing.");
        Native.Check(service.GetType().FullName == "PCLCS.LegacyDeviceIdentify", "Identity service was replaced with an unrelated implementation.");
        object host = Native.Field(service.GetType(), "host").GetValue(service)!;
        Replace(host, "ReadHardware", new Func<object>(() => "native-facade-synthetic-hardware"));
        Replace(host, "ReadSeed", new Func<string>(() => "native-facade-synthetic-seed"));
        Replace(host, "WriteSeed", new Action<string>(_ => ForbiddenVoid("identity seed persistence")));
        Replace(host, "AvailablePhysicalMemory", new Func<ulong>(() => Forbidden<ulong>("identity physical-memory fallback")));
    }

    private void InstallSettingsGuardsAndCache()
    {
        object backend = Native.Field(Native.Type("PCL.Settings"), "Backend").GetValue(null)!;
        object host = Native.Field(backend.GetType(), "host").GetValue(backend)!;
        foreach (string key in Native.Entries.Keys)
        {
            diagnosticSettingNames.Add(key);
            diagnosticSettingNames.Add(key + "_LF"); // Actual Microsoft-isolation registry name.
        }
        Replace(host, "ReadRegistry", new Func<string, string, string?, string?>((_, key, _) => Forbidden<string?>("Registry read", key)));
        Replace(host, "WriteRegistry", new Action<string, string, string?>((_, key, _) => ForbiddenVoid("Registry write", key)));
        Replace(host, "HasRegistry", new Func<string, string, bool>((_, key) => Forbidden<bool>("Registry Has", key)));
        Replace(host, "OpenRegistry", new Func<string, bool, bool, ILegacySettingsStore?>((_, _, _) => Forbidden<ILegacySettingsStore?>("Registry open")));
        Replace(host, "IdentifyCandidates", new Func<string, List<string>>(_ => Forbidden<List<string>>("Registry identity candidates")));
        Func<string, string, string?, string?> read = (Func<string, string, string?, string?>)Native.Get(host, "ReadIni")!;
        Action<string, string, string?> write = (Action<string, string, string?>)Native.Get(host, "WriteIni")!;
        Action<string, string> delete = (Action<string, string>)Native.Get(host, "DeleteIni")!;
        Func<string, string, bool> has = (Func<string, string, bool>)Native.Get(host, "HasIni")!;
        Replace(host, "ReadIni", new Func<string, string, string?, string?>((file, key, fallback) =>
        {
            CheckIni(file, key, "read");
            return read(file, key, fallback);
        }));
        Replace(host, "WriteIni", new Action<string, string, string?>((file, key, value) =>
        {
            CheckIni(file, key, "write");
            write(file, key, value);
        }));
        Replace(host, "DeleteIni", new Action<string, string>((file, key) =>
        {
            CheckIni(file, key, "delete");
            delete(file, key);
        }));
        Replace(host, "HasIni", new Func<string, string, bool>((file, key) =>
        {
            CheckIni(file, key, "has");
            return has(file, key);
        }));
        foreach (DictionaryEntry item in Native.Entries)
        {
            object entry = item.Value!;
            IDictionary cache = (IDictionary)Native.Get(entry, "ValueCache")!;
            List<DictionaryEntry> before = new();
            foreach (DictionaryEntry value in cache)
                before.Add(value);
            restore.Push(() =>
            {
                cache.Clear();
                foreach (var value in before)
                    cache[value.Key] = value.Value;
            });
            object? fallback = Native.Get(entry, "DefaultValue");
            Native.Check(fallback is not null, "Unexpected null real setting default.");
            cache[""] = fallback!;
        }

        Native.Cache("Identify", "native-facade-synthetic-seed");
        Native.Cache("SystemSystemCache", Path.Combine(Root, "cache") + Path.DirectorySeparatorChar);
        Native.Cache("LaunchFolderSelect", Path.Combine(Root, "minecraft") + Path.DirectorySeparatorChar);
    }

    private void CheckIni(string file, string key, string operation)
    {
        if (file == "Setup")
            return;
        string full = Native.Full(file);
        if (!full.StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ForbiddenVoid("INI " + operation + " outside GUID fixture", key);
    }

    internal void AssertNoForbiddenIo() => Native.Check(Volatile.Read(ref forbiddenIo) == 0, "A forbidden Registry/hardware-write/profile access was attempted, even if production caught the exception.");
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            InstanceConfigProviders.FlushAndDisposeWithin(Root);
            if (application is not null)
            {
                Native.Check(application.Windows.Count == 0, "Unexpected window before facade shutdown.");
                application.Shutdown();
            }
        }
        finally
        {
            // ResourceAssembly naturally belongs to the executable host. WPF
            // makes this process-static identity immutable; do not attempt a
            // setter/restore in this dedicated GUID child process.
            while (restore.Count > 0)
                restore.Pop()();
        }

        AssertNoForbiddenIo();
    }
}
