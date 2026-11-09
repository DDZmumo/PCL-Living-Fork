using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using Newtonsoft.Json.Linq;
using PCL;

namespace PCLLF.Native.Facade.Tests;

// LF f4/main50 save/backup fixtures adapted to the genuine Native Main family.
// CE is not a source for these LF-only backup formats or UI assertions.
internal static class BackupResourceChecks
{
    internal static readonly CancellationToken None = CancellationToken.None;
    internal static void Run(FixtureEnvironment environment)
    {
        using Scope scope = new(environment);
        BackupStorageChecks.Run(scope);
        BackupResourceWorldChecks.Run(scope);
        BackupResourceGroupChecks.Run(scope);
        BackupResourcePageChecks.Run(scope);
        BackupResourceStoreChecks.Run(scope);
        environment.AssertNoForbiddenIo();
    }

    // Program calls this before ReadGroups/FixtureEnvironment. This is an idle
    // process identity, not another hosted launcher or a second WPF Application.
    internal static bool TryRunIdleChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 1 || args[0] != "--backup-resources-idle") return false;
        exitCode = Task.Run(Console.ReadLine).Wait(TimeSpan.FromSeconds(15)) ? 0 : 2;
        return true;
    }

    internal sealed class IdleProcess : IDisposable
    {
        internal readonly Process Value;
        internal IdleProcess()
        {
            string executable = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe");
            Native.Check(File.Exists(executable), "Actual facade apphost is required for the backup process fixture.");
            ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--backup-resources-idle");
            Value = Process.Start(start) ?? throw new IOException("Could not start the controlled process identity.");
        }
        internal void Finish()
        {
            Value.StandardInput.Close();
            Native.Check(Value.WaitForExit(5000) && Value.ExitCode == 0, "Controlled backup process did not exit normally after stdin closed.");
        }
        public void Dispose()
        {
            try { Value.StandardInput.Close(); if (!Value.WaitForExit(5000)) { Value.Kill(true); Native.Check(Value.WaitForExit(5000), "Controlled backup process cleanup timed out."); } }
            finally { Value.Dispose(); }
        }
    }

    internal sealed class Scope : IDisposable
    {
        internal readonly string Root;
        internal readonly string Profile;
        internal readonly string Backups;
        private readonly object? oldProfile;
        private readonly object? oldBackups;
        internal Scope(FixtureEnvironment fixture)
        {
            Root = Path.Combine(fixture.Data, "backup-resources"); Profile = Path.Combine(Root, "profile"); Backups = Path.Combine(Root, "backups");
            Native.Check(!Directory.Exists(Root), "Backup fixture already exists; refusing to adopt unowned files.");
            Directory.CreateDirectory(Root);
            oldProfile = Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").GetValue(null);
            oldBackups = Native.Property(Native.Type("PCL.WorldBackups"), "TestRoot").GetValue(null);
            Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").SetValue(null, Profile);
            Native.Property(Native.Type("PCL.WorldBackups"), "TestRoot").SetValue(null, Backups);
        }
        public void Dispose()
        {
            try { Native.Check(!(bool)Native.Property(Native.Type("PCL.WorldBackups"), "IsBusy").GetValue(null)!, "Backup fixture left its genuine engine busy."); }
            finally {
                Native.Property(Native.Type("PCL.WorldBackups"), "TestRoot").SetValue(null, oldBackups);
                Native.Property(Native.Type("PCL.LocalGameData"), "TestRoot").SetValue(null, oldProfile);
            }
        }
    }

    internal static object? World(string name, params object?[] args) => Native.Static("PCL.WorldBackups", name, args);
    internal static object? Store(string name, params object?[] args) => Native.Static("PCL.InstanceResourceStore", name, args);
    internal static object Kind(string name) => Native.Enum("PCL.InstanceResourceKind", name)!;
    internal static IList Scan(string root, string kind) => (IList)Store("Scan", root, root, Kind(kind), None)!;
    internal static List<JObject> Points(string game) => (List<JObject>)World("Points", game)!;
    internal static string Capture(string game, string world, string reason = "manual", int retain = 50, CancellationToken token = default) => (string)World("CaptureWorld", game, world, reason, retain, token, null)!;
    internal static string Restore(string game, string point, int retain = 50, CancellationToken token = default, Action<int>? commit = null) => (string)World("Restore", game, point, retain, token, commit)!;
    internal static void Put(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
    internal static void Text(string file, string expected) => Native.Check(File.ReadAllText(file) == expected, "Actual backup/resource bytes changed: " + file);
    internal static Exception Reject(Action action, string label)
    {
        try { action(); } catch (Exception failure) { return failure; }
        throw new InvalidOperationException("Actual Main unexpectedly accepted " + label);
    }
    internal static ModMinecraft.McInstance Instance(string folder)
    {
        string name = Path.GetFileName(folder);
        Put(Path.Combine(folder, name + ".json"), "{\"id\":\"" + name + "\",\"type\":\"release\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[],\"downloads\":{},\"arguments\":{\"game\":[],\"jvm\":[]}}");
        Put(Path.Combine(folder, "PCL/Setup.ini"), "VersionArgumentIndieV2:True");
        ModMinecraft.McInstance owner = new(folder);
        foreach (DictionaryEntry item in Native.Entries) {
            object entry = item.Value!;
            if (Native.Get(entry, "Source")!.ToString() == "Instance") ((IDictionary)Native.Get(entry, "ValueCache")!)[owner.PathVersion] = Native.Get(entry, "DefaultValue");
        }
        Cache(owner, "VersionArgumentIndieV2", true);
        return owner;
    }
    internal static object? Cache(ModMinecraft.McInstance owner, string key) => ((IDictionary)Native.Get(Native.Entries[key]!, "ValueCache")!)[owner.PathVersion];
    internal static void Cache(ModMinecraft.McInstance owner, string key, object value) => ((IDictionary)Native.Get(Native.Entries[key]!, "ValueCache")!)[owner.PathVersion] = value;
    internal static T Part<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new MissingMemberException(element.GetType().FullName, name);
    internal static object ReadProperty(object value, string name) => Native.Property(value.GetType(), name).GetValue(value)!;
    // Constructors are instance members; Static also admits .cctor and makes
    // a genuine zero-argument WPF type with static initialization ambiguous.
    internal static object New(string type, params object?[] values) => Activator.CreateInstance(Native.Type(type), Native.Flags & ~BindingFlags.Static, null, values, null)!;
    internal static JObject Point(string world, int age = 0) => new() { ["id"] = Guid.NewGuid().ToString("N"), ["world"] = world, ["label"] = "Same world display name", ["created"] = DateTime.UtcNow.AddDays(-age), ["reason"] = "manual", ["description"] = world + " - manual - 19 B", ["healthy"] = false, ["bytes"] = 19L, ["archiveBytes"] = 120L, ["files"] = new JObject() };
    internal static Delegate Subscribe(object target, string name, Action action)
    {
        EventInfo evt = target.GetType().GetEvent(name, Native.Flags)!;
        ParameterExpression[] parameters = evt.EventHandlerType!.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        Delegate handler = Expression.Lambda(evt.EventHandlerType, Expression.Invoke(Expression.Constant(action)), parameters).Compile();
        evt.GetAddMethod(true)!.Invoke(target, [handler]);
        return handler;
    }
    internal static void Unsubscribe(object target, string name, Delegate handler) => target.GetType().GetEvent(name, Native.Flags)!.GetRemoveMethod(true)!.Invoke(target, [handler]);
}
