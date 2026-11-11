using System.Collections;
using System.Globalization;
using System.Net.Security;
using System.Windows.Threading;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static partial class LauncherLifecycleChecks
{
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static int assertions;
    private static void Check(string id, bool condition, string? detail = null)
    {
        assertions++; Seen.Add(id);
        Native.Check(condition, "Lifecycle " + id + " " + LauncherLifecycleOriginalChecks.Messages[id] + " " + detail);
    }
    private static object? SettingsCall(string name, params object?[] args) => Native.Static("PCL.Settings", name, args);
    private static string Encrypt(string value, string? key = "PCLsynthetic-device") => (string)SettingsCall("DesEncrypt", value, key)!;
    private static string Decrypt(string value, string? key = "PCLsynthetic-device") => (string)SettingsCall("DesDecrypt", value, key)!;
    private static IDisposable? Acquire(params string[] names) => (IDisposable?)Native.Static("PCL.LauncherSessionLock", "TryAcquire", names, 0);
    internal static void Run(FixtureEnvironment environment)
    {
        VerifyLifecycleChild();
        Seen.Clear(); assertions = 0;
        string root = Path.Combine(environment.Data, "launcher-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            WindowActivation();
            MutexLifetime();
            AccountPlannerAndCancellation();
            SharedPreferenceRegistry();
            CertificateAndUpdateChoices();
            CustomEventAndSecret();
            VersionCancellation(root);
            FileOperationLifetime(Path.Combine(root, "flat"), false);
            FileOperationLifetime(Path.Combine(root, "nested"), true);
            FileOperationPreCanceled(Path.Combine(root, "pre-cancel"));
            SourceOracles();
            DrainUi(); environment.AssertNoForbiddenIo();
            string[] missing = LauncherLifecycleOriginalChecks.Messages.Keys.Except(Seen, StringComparer.Ordinal).ToArray();
            Native.Check(missing.Length == 0, "Lifecycle observable IDs not reached: " + string.Join(", ", missing));
            Console.WriteLine("PASS native launcher-lifecycle: " + assertions + " runtime/oracle assertions over " + Seen.Count + " mapped original IDs. Historical source witnesses are reported separately; no product startup/authentication/external window/release was run.");
        }
        finally
        {
            DrainUi();
            Native.Check(Path.GetDirectoryName(root) == environment.Data && Path.GetFileName(root).StartsWith("launcher-lifecycle-", StringComparison.Ordinal), "Unsafe lifecycle fixture cleanup.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void VerifyLifecycleChild()
    {
        string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, "--fixture");
        Native.Check(args.Contains("--isolated", StringComparer.Ordinal) && i >= 0 && i + 1 < args.Length &&
            System.Reflection.Assembly.GetEntryAssembly() == typeof(FixtureEnvironment).Assembly,
            "Lifecycle cleanup termination requires its own isolated test child.");
        string root = Native.Full(args[i + 1]);
        Native.Check(root.StartsWith(Native.Full(Path.GetTempPath()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^PclNativeFacade-[a-f0-9]{32}$") &&
            Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(root, "output")), "Invalid lifecycle GUID/output root.");
    }
    private static void DrainUi()
    {
        VerifyLifecycleChild();
        var elapsed = System.Diagnostics.Stopwatch.StartNew(); DispatcherFrame frame = new(); bool drained = false; int completed = 0;
        void FailClosed()
        {
            Console.Error.WriteLine("FAIL isolated lifecycle UI cleanup; guards retained until child exit."); Console.Error.Flush();
            Environment.Exit(1); throw new InvalidOperationException("Isolated lifecycle exit returned.");
        }
        using var watchdog = new System.Threading.Timer(_ => { if (Volatile.Read(ref completed) == 0) FailClosed(); }, null, 5000, Timeout.Infinite);
        DispatcherTimer timer = new(DispatcherPriority.Send, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(5000) };
        timer.Tick += (_, _) => frame.Continue = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { drained = true; frame.Continue = false; }));
        timer.Start(); try { Dispatcher.PushFrame(frame); } catch { FailClosed(); } finally { timer.Stop(); }
        if (!drained || elapsed.ElapsedMilliseconds >= 5000) FailClosed();
        Volatile.Write(ref completed, 1); watchdog.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static void AccountPlannerAndCancellation()
    {
        // Fixed LF DES wire-format vectors also used by the repository's pure
        // settings family: independently computed OpenSSL3 DES-CBC/PKCS7, not
        // values produced by Main then used as their own expected result.
        (string Plain, string? Key, string Cipher)[] vectors =
        [
            ("", null, "Z2rkugz11VI="), ("synthetic-user", null, "easpyrFDg8NjmwbSatOV+g=="),
            ("synthetic-user", "", "yVKVR2ckWDXyfsNYInb5MQ=="),
            ("{\"fixture\":\"非真实账号\",\"empty\":\"\"}", "PCLsynthetic-device", "U3h0tfLTk5jVI51OcO6CLWlEmQLUs/4kX0EOL0l6KuBKJ0jt5HEudtHi/lecGoib")
        ];
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var vector in vectors)
                    Check("audit:DES", Encrypt(vector.Plain, vector.Key) == vector.Cipher && Decrypt(vector.Cipher, vector.Key) == vector.Plain);
            }
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Dictionary<string, string> raw = new(StringComparer.Ordinal)
        {
            ["CacheMsOAuthRefresh"] = Encrypt("old-refresh"), ["CacheMsV2OAuthRefresh"] = Encrypt("new-refresh"), ["CacheMsV2Expires"] = Encrypt("9999999999")
        };
        Dictionary<string, string> Plan() => (Dictionary<string, string>)SettingsCall("PlanLegacyMicrosoftUpgrade", raw, "synthetic-device")!;
        var result = Plan(); Check("audit:22", result.Count == 1 && result.ContainsKey("CacheMsV2Migrated"));
        raw["CacheMsV2OAuthRefresh"] = Encrypt(""); raw["CacheMsV2Expires"] = Encrypt("0"); Check("audit:26", Plan().Count == 1);
        raw["CacheMsV2OAuthRefresh"] = "invalid-cipher"; Check("audit:29", Plan().Count == 1);
        raw.Remove("CacheMsV2OAuthRefresh"); raw.Remove("CacheMsV2Expires"); result = Plan(); Check("audit:33", result.Count == 7 && Decrypt(result["CacheMsV2Expires"]) == "0");
        raw["CacheMsOAuthRefresh"] = "invalid-cipher"; Check("audit:35", Plan().Count == 0);
        Check("audit:37", (string)SettingsCall("RegistryFolderFor", Settings.Entries["CacheMsV2Migrated"])! == "PCL Living Fork");
        var entry = Settings.Entries["LoginMsJson"]; bool had = entry.ValueCache.TryGetValue("", out object? old);
        entry.ValueCache[""] = "{\"stale\":\"cached-token\"}";
        string fakeRaw = Encrypt("{\"first\":\"token-1\"}");
        Func<Settings.Setting, string> reader = _ => fakeRaw;
        try
        {
            string first = (string)SettingsCall("GetCore", "LoginMsJson", null, reader, "synthetic-device")!;
            fakeRaw = Encrypt("{\"second\":\"token-2\"}"); string second = (string)SettingsCall("GetCore", "LoginMsJson", null, reader, "synthetic-device")!;
            Check("audit:50", first == "{\"first\":\"token-1\"}" && second == "{\"second\":\"token-2\"}");
        }
        finally { if (had) entry.ValueCache[""] = old!; else entry.ValueCache.TryRemove("", out _); }
        foreach (string name in new[] { "SaveMicrosoftSession", "SaveAccountGroup" })
        {
            bool canceled = false;
            try
            {
                Dictionary<string, object> values = new(); Func<bool> stop = () => true;
                if (name == "SaveMicrosoftSession") SettingsCall(name, values, "synthetic-user", stop); else SettingsCall(name, values, stop);
            }
            catch (OperationCanceledException) { canceled = true; }
            Check("audit:157", canceled, name);
        }
        QueuedAccountCancellation();
    }

    private static void QueuedAccountCancellation()
    {
        object sync = Native.Field(Native.Type("PCL.Settings"), "SharedAccountSync").GetValue(null)!;
        foreach (string name in new[] { "SaveMicrosoftSession", "SaveAccountGroup" })
        {
            bool canceled = false, observed = false; Exception? failure = null;
            using ManualResetEventSlim started = new(false), finished = new(false);
            Func<bool> stop = () => Volatile.Read(ref canceled);
            Thread worker = new(() =>
            {
                started.Set();
                try
                {
                    Dictionary<string, object> values = new();
                    if (name == "SaveMicrosoftSession") SettingsCall(name, values, "synthetic-user", stop); else SettingsCall(name, values, stop);
                }
                catch (OperationCanceledException) { observed = true; }
                catch (Exception error) { failure = error; }
                finally { finished.Set(); }
            }) { IsBackground = true, Name = "Lifecycle queued-account cancellation fixture" };
            Monitor.Enter(sync);
            try { worker.Start(); Check("audit:184", started.Wait(3000)); Volatile.Write(ref canceled, true); }
            finally { Monitor.Exit(sync); }
            Check("audit:187", finished.Wait(3000)); Native.Check(worker.Join(3000), "Queued synthetic account worker did not terminate");
            Check("audit:189", failure is null && observed, failure?.GetType().Name);
        }
    }

    private static void CertificateAndUpdateChoices()
    {
        foreach (int code in new[] { 1, 2, 4, 7 })
        {
            foreach (string host in new[] { "xboxlive.com", "user.auth.xboxlive.com", "api.minecraftservices.com", "API.MINECRAFTSERVICES.COM." })
            {
                Check("audit:134", (bool)Native.Static("PCL.LoginCertificatePolicy", "Accept", host, (SslPolicyErrors)code, false)!);
                Check("audit:135", !(bool)Native.Static("PCL.LoginCertificatePolicy", "Accept", host, (SslPolicyErrors)code, true)!);
            }
            foreach (object? sender in new object?[] { null, 123, "github.com", "api.github.com", "xboxlive.attacker.test", "notminecraftservices.com", "api.minecraftservices.com.attacker.test" })
                Check("audit:138", !(bool)Native.Static("PCL.LoginCertificatePolicy", "Accept", sender, (SslPolicyErrors)code, false)!);
        }
        Check("audit:141", (bool)Native.Static("PCL.LoginCertificatePolicy", "Accept", null, SslPolicyErrors.None, true)!);
        int previous = (int)Settings.Entries["ToolLauncherUpdateChannel"].ValueCache[""]; Native.Cache("ToolLauncherUpdateChannel", 0);
        try
        {
            bool Prompt(string tag, string current, string ignored) => (bool)Native.Static("PCL.ModLauncherUpdate", "ShouldPromptForUpdate", tag, current, ignored)!;
            Check("audit:143", Prompt("v1.0.2", "1.0.1", "")); Check("audit:144", !Prompt("v1.0.2", "1.0.1", "v1.0.2")); Check("audit:145", Prompt("v1.0.3", "1.0.1", "v1.0.2"));
            Check("audit:147", (string)Native.Static("PCL.ModLauncherUpdate", "IgnoreTagAfterChoice", "v1.0.2", "v1.0.2", 1)! == "");
            Check("audit:148", (string)Native.Static("PCL.ModLauncherUpdate", "IgnoreTagAfterChoice", "", "v1.0.2", 2)! == "v1.0.2");
        }
        finally { Native.Cache("ToolLauncherUpdateChannel", previous); }
    }
}
