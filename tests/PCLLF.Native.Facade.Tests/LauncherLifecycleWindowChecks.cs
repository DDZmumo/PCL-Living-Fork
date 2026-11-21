using System.Runtime.InteropServices;
using System.Text;

namespace PCLLF.Native.Facade.Tests;
internal static partial class LauncherLifecycleChecks
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr FindWindowA(IntPtr cls, IntPtr title);
    private static void WindowActivation()
    {
        string title = "LivingPCL-Test-" + Guid.NewGuid().ToString("N") + "\u3000";
        IntPtr window = CreateWindowExW(0, "STATIC", title, 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Check("activation:25", window != IntPtr.Zero);
        try
        {
            Check("activation:27", (IntPtr)Native.Static("PCL.LauncherWindowActivation", "FindWindowW", null, title)! == window);
            byte[] ansi = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(title + "\0"); IntPtr buffer = Marshal.AllocHGlobal(ansi.Length);
            try { Marshal.Copy(ansi, 0, buffer, ansi.Length); Check("activation:32", FindWindowA(IntPtr.Zero, buffer) == IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Check("activation:34", DestroyWindow(window)); }
        List<string> order = new(); int pauses = 0;
        Func<string, IntPtr> find = caption => caption == "Plain Craft Launcher\u3000" ? new IntPtr(17) : IntPtr.Zero;
        Func<IntPtr, uint, bool> queue = (hwnd, message) => { Check("activation:41", hwnd == new IntPtr(17) && message == 6402); order.Add("queue"); return true; };
        Action<IntPtr> foreground = _ => order.Add("foreground");
        Action<int> pause = ms => { Check("activation:45", ms == 200); pauses++; };
        bool Request() => (bool)Native.Static("PCL.LauncherWindowActivation", "RequestActivation", find, queue, foreground, pause)!;
        Check("activation:46", Request() && string.Join(",", order) == "queue,foreground" && pauses == 0);
        order.Clear(); find = caption => pauses == 2 && caption == "LivingPCL\u3000" ? new IntPtr(17) : IntPtr.Zero;
        Check("activation:49", Request() && pauses == 2);
        pauses = 0; order.Clear();
        string[] captions = ["PCL Living Fork", "LivingPCL\u3000", "Plain Craft Launcher\u3000", "Plain Craft Launcher 2\u3000"];
        List<string> searched = new();
        find = caption => { searched.Add(caption); return IntPtr.Zero; };
        Check("activation:52", !Request() && searched.SequenceEqual(Enumerable.Range(0, 10).SelectMany(_ => captions)) &&
            pauses == 9 && order.Count == 0, "Modern title and three legacy aliases must be tried in order on each bounded attempt.");
        find = _ => new IntPtr(17); queue = (_, _) => false; Check("activation:55", !Request());
        Console.WriteLine("PASS native lifecycle activation: genuine Main Unicode FindWindowW only on its own GUID hidden HWND; original6402/order/delayed/bounded/failure logic with controlled callbacks, no user window searched or foregrounded.");
    }

    private static Thread HoldingMutex(string name, ManualResetEventSlim ready, ManualResetEventSlim release, Action<Exception> failed)
    {
        Thread thread = new(() =>
        {
            try
            {
                using Mutex gate = new(true, name, out bool created);
                if (!created) throw new InvalidOperationException("GUID fixture mutex unexpectedly already exists.");
                ready.Set();
                try { if (!release.Wait(5000)) throw new TimeoutException("GUID mutex fixture release timed out."); }
                finally { gate.ReleaseMutex(); }
            }
            catch (Exception error) { failed(error); ready.Set(); }
        }) { IsBackground = true, Name = "Lifecycle owned mutex fixture" };
        thread.Start(); return thread;
    }
    private static void MutexLifetime()
    {
        string prefix = "LivingPCL-Audit-" + Guid.NewGuid().ToString("N"), primary = prefix + "-primary", secondary = prefix + "-secondary";
        using ManualResetEventSlim ready = new(false), release = new(false);
        Exception? failure = null; Thread owner = HoldingMutex(primary, ready, release, error => failure = error);
        try
        {
            Check("audit:199", ready.Wait(3000) && failure is null); using IDisposable? lease = Acquire(primary); Check("audit:200", lease is null);
        }
        finally { release.Set(); Native.Check(owner.Join(5000), "GUID mutex owner did not end."); }
        using (IDisposable? lease = Acquire(primary, secondary))
        {
            Check("audit:203", lease is not null);
            using Mutex official = new(true, primary, out bool created); Check("audit:204", !created);
        }
        ready.Reset(); release.Reset(); failure = null; owner = HoldingMutex(secondary, ready, release, error => failure = error);
        try
        {
            Check("audit:208", ready.Wait(3000) && failure is null);
            using IDisposable? lease = Acquire(primary, secondary); Check("audit:210", lease is null);
            using Mutex check = new(true, primary, out bool created); Check("audit:211", created); if (created) check.ReleaseMutex();
        }
        finally { release.Set(); Native.Check(owner.Join(5000), "Second GUID mutex owner did not end."); }
        Mutex? abandoned = null; Thread abandonedOwner = new(() => abandoned = new Mutex(true, primary, out _)) { IsBackground = true };
        abandonedOwner.Start(); Native.Check(abandonedOwner.Join(3000), "Abandoned GUID mutex owner did not end.");
        try { using IDisposable? lease = Acquire(primary); Check("audit:217", lease is not null); }
        finally { abandoned?.Dispose(); }
        Console.WriteLine("PASS native lifecycle singleton: actual Main.TryAcquire official-style/legacy-style GUID contention, full/partial release, abandoned owner recovery and bounded fixture threads.");
    }
}
