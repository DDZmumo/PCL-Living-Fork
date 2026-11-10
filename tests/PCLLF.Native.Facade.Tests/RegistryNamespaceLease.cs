using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.CompilerServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using MeloongCore;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Explicitly opt-in, fresh hosted Windows profile only. Never adopts a user's key.
internal sealed class RegistryNamespaceLease : IDisposable
{
    internal const string Marker = "LivingPclNativeRegistryFixtureOwner";
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, RegistryKey> owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> folders = ["PCL", "PCLDebug", "LivingPCL", LfRegistryStorage.Folder];
    private bool disposed;
    internal RegistryKey Official => owned["PCL"];
    internal RegistryKey Legacy => owned["PCLDebug"];
    internal RegistryKey OlderPrivate => owned["LivingPCL"];
    internal RegistryKey LfOnly => owned[LfRegistryStorage.Folder];
    internal RegistryNamespaceLease()
    {
        Native.Check(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" && Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows",
            "registry-data requires an explicit fresh GitHub-hosted Windows profile; refusal is a failure, not a skipped pass.");
        Native.Check((string)Native.Field(Native.Type("PCL.ModSecret"), "RegFolder").GetRawConstantValue()! == "PCLDebug", "Unreviewed actual private namespace.");
        // Check every root before creating any. Existence is sufficient to refuse;
        // no value or account in an existing namespace is read.
        foreach (string folder in folders)
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey("Software\\" + folder, false);
            Native.Check(existing is null, "Refusing an existing unowned Registry namespace: " + folder);
        }
        try
        {
            foreach (string folder in folders)
            {
                int result = RegCreateKeyEx(new IntPtr(unchecked((int)0x80000001)), "Software\\" + folder, 0, null, 0, 0x2001F, IntPtr.Zero, out SafeRegistryHandle handle, out int disposition);
                if (result != 0) { handle.Dispose(); throw new Win32Exception(result); }
                if (disposition != 1) { handle.Dispose(); throw new InvalidOperationException("Registry namespace appeared during atomic acquisition: " + folder); }
                RegistryKey key = RegistryKey.FromHandle(handle);
                owned.Add(folder, key);
                key.SetValue(Marker, token, RegistryValueKind.String);
            }
        }
        catch { Dispose(); throw; }
    }
    internal void ValidateFolder(string folder)
    {
        Native.Check(!disposed && owned.ContainsKey(folder) && CurrentOwnerMatches(folder),
            "Registry access is outside the owned GUID namespace or its ownership marker changed.");
    }
    private bool CurrentOwnerMatches(string folder) {
        using RegistryKey? current = Registry.CurrentUser.OpenSubKey("Software\\" + folder, false);
        return current is not null && string.Equals(current.GetValue(Marker, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, token, StringComparison.Ordinal);
    }
    internal void ValidatePath(string path, string value)
    {
        const string prefix = "HKEY_CURRENT_USER\\Software\\";
        Native.Check(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Registry primitive requested an unreviewed hive/path.");
        string folder = path.Substring(prefix.Length);
        ValidateFolder(folder); // Exact dictionary membership rejects nested/traversal paths.
        Native.Check(!IsUnsuffixedMicrosoft(value), "Production primitive attempted a shared unsuffixed Microsoft value: " + value);
    }
    internal static bool IsUnsuffixedMicrosoft(string name) => (name == "LoginMsJson" || name.StartsWith("CacheMs", StringComparison.Ordinal)) && !name.EndsWith("_LF", StringComparison.Ordinal);
    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(IntPtr root, string subkey, int reserved, string? keyClass, int options, int access, IntPtr security, out SafeRegistryHandle handle, out int disposition);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var errors = new List<Exception>();
        foreach (var item in owned.Reverse())
        {
            try
            {
                Native.Check(CurrentOwnerMatches(item.Key),
                    "Registry cleanup refused a missing/changed GUID owner; unknown data is not deleted.");
                item.Value.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree("Software\\" + item.Key, false);
            }
            catch (Exception error) { errors.Add(error); item.Value.Dispose(); }
        }
        owned.Clear();
        if (errors.Count != 0) throw new AggregateException("Owned Registry fixture cleanup did not fully complete.", errors);
    }
}

// Only platform primitives are scoped. Every opened account store is the actual
// production sealed Windows adapter; the real Main backend and records stay put.
internal sealed class RegistryDataPlatformScope : IDisposable
{
    private readonly Stack<Action> restore = new();
    private readonly object backend;
    private readonly RegistryNamespaceLease lease;
    private int violations;
    internal int Opens, Reads, Writes, Has;
    internal const string Hardware = "native-facade-synthetic-hardware", Seed = "native-facade-synthetic-seed";
    internal RegistryDataPlatformScope(RegistryNamespaceLease lease)
    {
        this.lease = lease;
        backend = Native.Field(Native.Type("PCL.Settings"), "Backend").GetValue(null)!;
        object host = Native.Get(backend, "host")!;
        var router = new LfRegistryStorage((folder, writable, create) => {
            Guard(() => lease.ValidateFolder(folder)); Opens++;
            ILegacySettingsStore? store = LegacySettingsRegistryStore.Open(folder, writable, create);
            Native.Check(store is not null && store.GetType() == typeof(LegacySettingsRegistryStore), "Expected the genuine Windows Registry adapter.");
            return store;
        });
        try
        {
            Replace(host, "ReadRegistry", new Func<string, string, string?, string?>((path, key, fallback) =>
            {
                Guard(() => lease.ValidatePath(path, key)); Reads++;
                return router.ReadRegistry(path, key, fallback);
            }));
            Replace(host, "WriteRegistry", new Action<string, string, string?>((path, key, value) =>
            {
                Guard(() => lease.ValidatePath(path, key)); Writes++;
                router.WriteRegistry(path, key, value);
            }));
            Replace(host, "HasRegistry", new Func<string, string, bool>((path, key) =>
            {
                Guard(() => lease.ValidatePath(path, key)); Has++;
                return router.HasRegistry(path, key);
            }));
            Replace(host, "OpenRegistry", new Func<string, bool, bool, ILegacySettingsStore?>((folder, writable, create) =>
            {
                return router.OpenRegistry(folder, writable, create);
            }));
            // The same actual SourceIdentify primitive with synthetic input only;
            // never invoke the hardware-enumerating Windows IdentifyCandidates.
            Replace(host, "IdentifyCandidates", new Func<string, List<string>>(seed =>
                new List<string> { LegacySettingsCrypto.SourceIdentify(Hardware, seed)! }));
        }
        catch { Dispose(); throw; }
    }
    private void Guard(Action action)
    {
        try { action(); } catch { violations++; throw; }
    }
    private void Replace(object host, string name, object replacement)
    {
        FieldInfo field = Native.Field(host.GetType(), name);
        object? original = field.GetValue(host);
        restore.Push(() => field.SetValue(host, original));
        field.SetValue(host, replacement);
    }
    internal void AssertScope() => Native.Check(violations == 0 && ReferenceEquals(backend, Native.Field(Native.Type("PCL.Settings"), "Backend").GetValue(null)), "A forbidden Registry operation occurred, including caught errors, or the real backend changed.");
    public void Dispose()
    {
        while (restore.TryPop(out Action? action)) action();
        AssertScope();
    }
}
