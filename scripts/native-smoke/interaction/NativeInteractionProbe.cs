// External UIAutomation/message observer for the genuine published LF50 Main.
// No product assembly is loaded, reflected, injected, or replaced. See root LICENCE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;

namespace PclLfNativeInteraction;

internal sealed class Probe
{
    private readonly string Notice;
    private readonly ReviewedNotice noticePolicy;
    private readonly Dictionary<string, string> args;
    private readonly Dictionary<string, object?> evidence = new();
    private readonly List<object> steps = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly int pid;
    private readonly IntPtr hwnd;
    private readonly string fixture, owner, exe, output;
    private readonly Process process;
    private bool themeChanged;
    private bool identityProved;
    private bool cursorSafe = true;
    private ObservedShell shell = ObservedShell.Unverified;
    private AutomationElement Root => AutomationElement.FromHandle(hwnd);

    private Probe(Dictionary<string, string> arguments)
    {
        args = arguments;
        Notice = args["notice"];
        noticePolicy = ReviewedInformationNotice.ForTitle(Notice, args.GetValueOrDefault("notice-style", "plain"), args.GetValueOrDefault("notice-source", "local"));
        pid = int.Parse(args["pid"], CultureInfo.InvariantCulture);
        hwnd = new IntPtr(long.Parse(args["hwnd"], CultureInfo.InvariantCulture));
        fixture = Path.GetFullPath(args["fixture"]); owner = args["owner"];
        exe = Path.GetFullPath(args["exe"]);
        if (!Guid.TryParseExact(owner, "N", out _) || args["pass"] is not ("1" or "2")) throw new InvalidOperationException("Invalid fixture identity/pass.");
        output = Path.Combine(fixture, "interaction-" + args["pass"] + ".json");
        process = Process.GetProcessById(pid);
        evidence["Result"] = "pending"; evidence["FixtureId"] = owner; evidence["Pass"] = args["pass"];
        evidence["ProcessId"] = pid; evidence["MainHandle"] = hwnd.ToInt64(); evidence["MainSha256"] = args["sha256"];
        evidence["Steps"] = steps;
        evidence["Limit"] = "Only the owned published Main in the CI windowstation. Layout/screenshot and observable UIA state are evidence; human mouse/keyboard/font feel, drag/drop, real accounts/services/game/hardware remain unverified.";
    }

    [STAThread]
    private static int Main(string[] values)
    {
        Probe? probe = null;
        try
        {
            if (values.Length == 1 && values[0] == "--self-test-notice-policy")
            {
                ReviewedInformationNotice.VerifyPolicy();
                ShellUiPolicy.VerifyPolicy();
                return 0;
            }
            if (values.Length == 1 && values[0] == "--self-test-shell-policy")
            {
                ShellUiPolicy.VerifyPolicy();
                return 0;
            }
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Environment.GetEnvironmentVariable("CI") != "true" || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted") throw new InvalidOperationException("PENDING: isolated GitHub-hosted x64 Windows only.");
            Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (values.Length is not (18 or 22)) throw new InvalidOperationException("Expected nine fixture arguments and the optional bound notice style/source pair.");
            Dictionary<string, string> arguments = new(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i += 2) arguments.Add(values[i].TrimStart('-'), values[i + 1]);
            probe = new Probe(arguments); probe.Guard(true); probe.identityProved = true; probe.Run();
            if (!probe.cursorSafe) throw new InvalidOperationException("PENDING: original CI cursor restoration was not proved.");
            probe.evidence["Result"] = "passed";
            return 0;
        }
        catch (Exception error)
        {
            if (probe != null) { probe.evidence["Result"] = error.Message.StartsWith("PENDING:", StringComparison.Ordinal) ? "pending" : "failed"; probe.evidence["Error"] = error.GetType().Name + ": " + error.Message; }
            if (probe?.identityProved == true && probe.cursorSafe)
            {
                // Failure evidence only, after the same live owner/image/profile
                // guard; never act on an unknown window or mask the primary error.
                try { probe.evidence["FailureUi"] = probe.Nodes().Where(probe.Visible).Take(200).Select(node => {
                    var current = node.Current;
                    return new { Name = current.Name.Length <= 256 ? current.Name : current.Name[..256],
                        current.AutomationId, current.ClassName, Type = current.ControlType.ProgrammaticName,
                        Bounds = current.BoundingRectangle.ToString(CultureInfo.InvariantCulture) };
                }).ToArray(); }
                catch (Exception diagnostic) { probe.evidence["FailureUiError"] = diagnostic.GetType().Name; }
            }
            else if (probe != null && !probe.cursorSafe) { probe.evidence["Result"] = "failed"; probe.evidence["FailureUiSkipped"] = "Cursor restoration is unproved; no subsequent UIA, Guard/file read or real UI recovery is authorized."; }
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1;
        }
        finally
        {
            if (probe != null)
            {
                if (!probe.cursorSafe)
                {
                    // A displaced/unknown cursor forbids further UIA, real UI
                    // rollback and file reads/writes. Emit only captured facts;
                    // no receipt is manufactured for the outer Smoke to pass.
                    probe.evidence["Result"] = "failed";
                    string summary = JsonSerializer.Serialize(new { Result = "failed", probe.owner, Pass = probe.args["pass"], ProcessId = probe.pid,
                        MainHandle = probe.hwnd.ToInt64(), PrimaryError = probe.evidence.GetValueOrDefault("Error"),
                        CursorRestoration = probe.evidence.GetValueOrDefault("CursorRestoration"),
                        Skipped = "UIA snapshot, Guard, theme rollback and evidence-file IO: cursor restoration unproved." });
                    Console.Error.WriteLine("CURSOR_RESTORE_UNPROVED " + (summary.Length <= 12000 ? summary : summary[..12000]));
                }
                else
                {
                    if (probe.themeChanged)
                    {
                        try { probe.SetTheme("浅色", 0x24); probe.evidence["ThemeRollback"] = "restored-through-owned-real-UI"; }
                        catch (Exception error) { probe.evidence["ThemeRollback"] = error.Message; probe.evidence["Result"] = "failed"; }
                    }
                    // Guard the destination again. A changed marker preserves evidence instead of writing elsewhere.
                    if (File.Exists(Path.Combine(probe.fixture, "owner.txt")) && File.ReadAllText(Path.Combine(probe.fixture, "owner.txt")) == probe.owner)
                    {
                        probe.evidence["ElapsedMilliseconds"] = probe.clock.ElapsedMilliseconds;
                        File.WriteAllText(probe.output, JsonSerializer.Serialize(probe.evidence, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                    }
                }
                probe.process.Dispose();
            }
        }
    }

    private void Guard(bool hash = false)
    {
        if (clock.Elapsed.TotalSeconds > 80) throw new InvalidOperationException("PENDING: bounded interaction budget exhausted.");
        string workspace = Path.GetFullPath(args["workspace"]).TrimEnd(Path.DirectorySeparatorChar);
        if (!fixture.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !exe.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.ReadAllText(Path.Combine(fixture, "owner.txt")) != owner) throw new InvalidOperationException("Fixture path/owner differs.");
        foreach (string name in new[] { "Software\\PCL", "Software\\PCLDebug", "Software\\PCL Living Fork" })
        {
            using RegistryKey? store = Registry.CurrentUser.OpenSubKey(name, false);
            if (store?.GetValue("__PclLfNativeSmokeOwner") as string != owner) throw new InvalidOperationException("Synthetic profile ownership differs.");
        }
        process.Refresh();
        if (process.HasExited || Native.Owner(hwnd) != pid || !Native.IsWindowVisible(hwnd) || Native.Title(hwnd) != "LivingPCL" || !Native.Class(hwnd).StartsWith("HwndWrapper", StringComparison.Ordinal)) throw new InvalidOperationException("Actual Main PID/HWND/title/class is no longer owned and visible.");
        StringBuilder path = new(32768); uint count = (uint)path.Capacity;
        if (!Native.QueryFullProcessImageName(process.Handle, 0, path, ref count) || !string.Equals(path.ToString(), exe, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Actual process handle image differs from owned EXE.");
        if (hash && !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))), args["sha256"], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned installed EXE SHA differs.");
        Native.CheckNamedWindows(pid, hwnd);
        using (StreamReader reader = new(new FileStream(Path.Combine(Path.GetDirectoryName(exe)!, "PCL", "Log1.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            string log = reader.ReadToEnd();
            if (Regex.IsMatch(log, @"(?m)^\d{2}:\d{2}:\d{2}\.\d{3} E ")) throw new InvalidOperationException("Actual Main emitted an Error log during interaction.");
            noticePolicy.VerifyLogCaption(log);
        }
        if (Root.Current.ProcessId != pid || Root.Current.NativeWindowHandle != hwnd.ToInt64()) throw new InvalidOperationException("UIA root does not represent actual Main HWND.");
    }

    private List<AutomationElement> Nodes()
    {
        Guard(); List<AutomationElement> result = new();
        Walk(Root, 0, result); return result;
    }
    private static void Walk(AutomationElement node, int depth, List<AutomationElement> result)
    {
        if (depth > 40 || result.Count > 2500) throw new InvalidOperationException("PENDING: bounded owned UIA tree exceeded.");
        result.Add(node);
        for (AutomationElement? child = TreeWalker.RawViewWalker.GetFirstChild(node); child != null; child = TreeWalker.RawViewWalker.GetNextSibling(child)) Walk(child, depth + 1, result);
    }
    private bool Visible(AutomationElement node)
    {
        var current = node.Current; var r = current.BoundingRectangle;
        // WPF can report unclipped scroll descendants as IsOffscreen=false.
        // Real settings RAM labels below this HWND still animate every second;
        // only geometry intersecting the actual Main client is visible evidence.
        return current.ProcessId == pid && !current.IsOffscreen && !r.IsEmpty && r.Width > 0 && r.Height > 0 && Native.Client(hwnd).IntersectsWith(r);
    }
    private AutomationElement Unique(Func<AutomationElement, bool> test, string description)
        => Unique(Nodes(), test, description);
    private AutomationElement Unique(IEnumerable<AutomationElement> nodes, Func<AutomationElement, bool> test, string description)
    {
        var found = nodes.Where(n => Visible(n) && test(n)).ToList();
        if (found.Count != 1) throw new InvalidOperationException("PENDING: expected one owned visible " + description + "; found " + found.Count + ".");
        AssertAncestor(found[0]); return found[0];
    }
    private AutomationElement Text(string text, string id, bool top = false, bool left = false) => Unique(n => n.Current.ControlType == ControlType.Text && n.Current.Name == text && n.Current.AutomationId == id && (!top || n.Current.BoundingRectangle.Bottom <= Native.Client(hwnd).Top + 57) && (!left || n.Current.BoundingRectangle.Right < Native.Client(hwnd).Left + 220), "Text(" + id + "," + text + ")");
    private static ShellNode ShellMetadata(AutomationElement node)
    {
        var current = node.Current;
        return new(current.AutomationId, current.Name, current.ClassName, current.ControlType, current.BoundingRectangle);
    }
    private (List<AutomationElement> Nodes, ObservedShell Mode, System.Windows.Rect Client, double Scale) ObserveShell()
    {
        var nodes = Nodes().Where(Visible).ToList();
        var metadata = nodes.Select(ShellMetadata).ToArray();
        var client = Native.Client(hwnd);
        double scale = metadata.Any(ShellUiPolicy.IsGlassMarker) ? Native.DpiScale(hwnd) : 1;
        return (nodes, ShellUiPolicy.Detect(metadata, client, scale), client, scale);
    }
    private void DetectShell()
    {
        var observed = ObserveShell();
        if (shell != ObservedShell.Unverified) throw new InvalidOperationException("Shell identity was already admitted.");
        foreach (var node in observed.Nodes.Where(node => ShellUiPolicy.IsGlassMarker(ShellMetadata(node)))) AssertAncestor(node);
        shell = observed.Mode;
        evidence["Shell"] = new { Mode = shell.ToString(), DpiScale = observed.Scale,
            Proof = shell == ObservedShell.Glass ? "Five exact ordered native header Button IDs/names plus AccountButton on one centreline; complete owned header geometry within 104 DIPs" : "Original unique LabText launch/settings peers within the unchanged 57-pixel classic title strip" };
    }
    private AutomationElement HeaderNavigation(string text)
    {
        var observed = ObserveShell();
        if (shell == ObservedShell.Unverified || observed.Mode != shell) throw new InvalidOperationException("PENDING: admitted Main shell identity changed.");
        if (shell == ObservedShell.Classic) return Text(text, "LabText", top: true);
        var target = ShellUiPolicy.HeaderTarget(text);
        return Unique(observed.Nodes, node => ShellUiPolicy.IsGlassHeaderTarget(ShellMetadata(node), target.Id, target.Name, observed.Client, observed.Scale),
            "glass header Button(" + target.Id + "," + target.Name + ")");
    }
    private AutomationElement ContextNavigation(string text)
    {
        var observed = ObserveShell();
        if (shell == ObservedShell.Unverified || observed.Mode != shell) throw new InvalidOperationException("PENDING: admitted Main shell identity changed.");
        if (shell == ObservedShell.Classic) return Text(text, "LabTitle", left: true);
        var account = Unique(observed.Nodes, node => node.Current.AutomationId == "AccountButton", "admitted glass account capsule");
        ShellNode accountBounds = ShellMetadata(account);
        var rail = Unique(observed.Nodes, node => ShellUiPolicy.IsGlassRailAnchor(ShellMetadata(node), accountBounds, observed.Client, observed.Scale),
            "glass rail NavigationMenuButton footer");
        ShellNode railBounds = ShellMetadata(rail);
        return Unique(observed.Nodes, node => ShellUiPolicy.IsGlassContextTarget(ShellMetadata(node), text, railBounds, accountBounds, observed.Client, observed.Scale),
            "glass contextual rail Button(" + text + ")");
    }
    private AutomationElement Combo() => Unique(n => n.Current.ControlType == ControlType.ComboBox && n.Current.AutomationId == "ComboLauncherColorMode", "ComboLauncherColorMode");
    private void AssertAncestor(AutomationElement? node)
    {
        for (int i = 0; i < 45 && node != null; i++)
        {
            if (node.Current.ProcessId != pid) throw new InvalidOperationException("UIA ancestor belongs to another process.");
            int handle = node.Current.NativeWindowHandle;
            if (handle != 0) { if (new IntPtr(handle) != hwnd) throw new InvalidOperationException("UIA ancestor is outside the owned Main HWND."); return; }
            node = TreeWalker.RawViewWalker.GetParent(node);
        }
        throw new InvalidOperationException("PENDING: UIA target has no provable Main HWND ancestor.");
    }
    private void AssertScrollContainsTheme(AutomationElement theme, AutomationElement scroll)
    {
        // A shared rectangle alone cannot prove that this is the current page's
        // scroller. Follow only the already-owned theme peer's raw ancestors.
        AutomationElement? ancestor = TreeWalker.RawViewWalker.GetParent(theme);
        for (int depth = 0; depth < 45 && ancestor != null; depth++)
        {
            if (ancestor.Current.ProcessId != pid) throw new InvalidOperationException("Theme scroll ancestor belongs to another process.");
            if (ancestor.Equals(scroll)) return;
            if (ancestor.Current.NativeWindowHandle != 0) break;
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
        }
        throw new InvalidOperationException("PENDING: actual theme ComboBox has no provable selected PanBack ancestor.");
    }
    private bool IsDescendant(AutomationElement node, AutomationElement ancestor)
    {
        AutomationElement? current = TreeWalker.RawViewWalker.GetParent(node);
        for (int depth = 0; depth < 45 && current != null; depth++, current = TreeWalker.RawViewWalker.GetParent(current))
        {
            if (current.Current.ProcessId != pid) throw new InvalidOperationException("Notice ancestor belongs to another process.");
            if (current.Equals(ancestor)) return true;
            if (current.Current.NativeWindowHandle != 0) break;
        }
        return false;
    }
    private AutomationElement VerifyInformationNotice()
    {
        var title = Text(Notice, "LabTitle"); var button = Text("确定", "LabText");
        if (!noticePolicy.IsMarkdown)
        {
            var caption = Text(noticePolicy.LogCaption, "LabCaption");
            if (caption.Current.BoundingRectangle.Top <= title.Current.BoundingRectangle.Top || button.Current.BoundingRectangle.Top <= caption.Current.BoundingRectangle.Top)
                throw new InvalidOperationException("Information notice geometry/unique button differs.");
            return button;
        }
        // The caption TextBlock is intentionally replaced by the native content
        // factory. Inspect the actual UIA Document/TextPattern, not its old peer.
        var content = Unique(node => node.Current.AutomationId == "PanCaption", "native notice PanCaption");
        // WPF also exposes the FlowDocument's own nested Document peer. Bind
        // the fixed viewer viewport, whose TextPattern delegates the full text.
        var document = Unique(node => node.Current.ControlType == ControlType.Document && node.Current.ClassName == "FlowDocumentScrollViewer" && IsDescendant(node, content) && node.TryGetCurrentPattern(TextPattern.Pattern, out _), "native Markdown viewer Document/TextPattern");
        if (!document.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) throw new InvalidOperationException("PENDING: native Markdown TextPattern disappeared.");
        string text = ((TextPattern)pattern).DocumentRange.GetText(16001);
        noticePolicy.VerifyDocument(text);
        var published = Text("查看发布页", "LabText");
        AutomationElement? suppression = noticePolicy.HasSuppression ? Text(ReviewedInformationNotice.SuppressionLabel, "LabText") : null;
        if (suppression != null && (!IsDescendant(suppression, content) || IsDescendant(suppression, document)))
            throw new InvalidOperationException("PENDING: native suppression label is not a separate footer in the owned notice content.");
        ReviewedInformationNotice.VerifyRichGeometry(Native.Client(hwnd), title.Current.BoundingRectangle, content.Current.BoundingRectangle, document.Current.BoundingRectangle,
            suppression?.Current.BoundingRectangle, button.Current.BoundingRectangle, published.Current.BoundingRectangle);
        evidence["ReviewedStartupNotice"] = new { noticePolicy.Title, noticePolicy.Style, noticePolicy.Source, CompleteLocalDocument = true,
            DocumentCharacters = text.Length, DocumentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReviewedInformationNotice.NormalizeDocument(text)))),
            SuppressionLabel = suppression?.Current.Name, SeparateFooter = suppression != null, CompleteOwnedGeometry = true, LocalLogCaption = true,
            InputTarget = "确定 only; suppression and release-page controls are observed without input" };
        return button;
    }
    private void Click(AutomationElement node, string step)
    {
        // A posted coordinate alone does not establish WPF Mouse.DirectlyOver.
        // Preserve the actual LF mouse event path, including down/leave/up state.
        string text = node.Current.Name;
        WithOwnedHover(node, step, (screen, local, bounds) =>
        {
            IntPtr location = Native.Pack(local.X, local.Y);
            Native.Send(hwnd, 0x0200, IntPtr.Zero, location); Thread.Sleep(80);
            CheckDesktopTarget(screen, bounds, cursorAtTarget: true);
            Native.Send(hwnd, 0x0201, new IntPtr(1), location);
            CheckDesktopTarget(screen, bounds, cursorAtTarget: true);
            Native.Send(hwnd, 0x0202, IntPtr.Zero, location);
            steps.Add(new { Action = step, Input = "Verified system hover, then WM_MOUSEMOVE/WM_LBUTTONDOWN/WM_LBUTTONUP to owned Main client only", Text = text, X = local.X, Y = local.Y });
        });
    }
    private void WithOwnedHover(AutomationElement node, string step, Action<Native.Point, Native.Point, System.Windows.Rect> input)
    {
        Guard(true); AssertAncestor(node);
        var target = node.Current.BoundingRectangle; var window = Native.Window(hwnd);
        for (int sample = 0; sample < 3; sample++)
        {
            if (!Visible(node) || !node.Current.IsEnabled || !Native.Client(hwnd).Contains(target) || node.Current.BoundingRectangle != target || Native.Window(hwnd) != window)
                throw new InvalidOperationException("PENDING: owned hover target/client bounds did not remain stable.");
            Thread.Sleep(80);
        }
        Native.Point screen = new() { X = (int)(target.Left + target.Width / 2), Y = (int)(target.Top + target.Height / 2) };
        Native.Point local = screen;
        if (!Native.ScreenToClient(hwnd, ref local)) throw new InvalidOperationException("Client conversion failed.");
        Guard(true);
        // No cursor movement while a UIA/provider call may block. After this
        // point use only bounded Win32 messages and immediate native queries;
        // reserve twenty seconds before the outer observer's ninety-second cap.
        if (clock.Elapsed.TotalSeconds > 70) throw new InvalidOperationException("PENDING: insufficient bounded budget to move and restore the CI cursor.");
        if (Native.GetForegroundWindow() != hwnd)
        {
            if (Native.Owner(hwnd) != pid || !Native.SetForegroundWindow(hwnd)) throw new InvalidOperationException("PENDING: owned Main could not become the foreground window; observed foreground handle=" + Native.GetForegroundWindow().ToInt64() + ".");
            Stopwatch foreground = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != hwnd && foreground.ElapsedMilliseconds < 1000) Thread.Sleep(20);
        }
        CheckDesktopTarget(screen, window, cursorAtTarget: false);
        if (!Native.GetCursorPos(out Native.Point saved)) throw CursorError("GetCursorPos before owned hover", Marshal.GetLastWin32Error());
        steps.Add(new { Action = "cursor-saved", Target = step, X = saved.X, Y = saved.Y, TargetX = screen.X, TargetY = screen.Y, ForegroundHandle = hwnd.ToInt64(), HitHandle = Native.WindowFromPoint(screen).ToInt64(), StableTargetSamples = 3 });
        Exception? primary = null;
        cursorSafe = false;
        try
        {
            bool moved = Native.SetCursorPos(screen.X, screen.Y); int moveError = moved ? 0 : Marshal.GetLastWin32Error();
            steps.Add(new { Action = "cursor-moved", Target = step, RequestedX = screen.X, RequestedY = screen.Y, SetSucceeded = moved, SetWin32Error = moveError });
            if (!moved) throw CursorError("SetCursorPos for owned hover", moveError);
            CheckDesktopTarget(screen, window, cursorAtTarget: true);
            input(screen, local, window);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // Restoration is independent of product/UIA availability. A failed
            // Set/Get or mismatched position is pending, never a passed hover.
            bool set = false, get = false; int setError = 0, getError = 0; Native.Point restored = default; Exception? restoreError = null;
            try
            {
                set = Native.SetCursorPos(saved.X, saved.Y); setError = set ? 0 : Marshal.GetLastWin32Error();
                get = Native.GetCursorPos(out restored); getError = get ? 0 : Marshal.GetLastWin32Error();
            }
            catch (Exception error) { restoreError = error; }
            bool same = set && get && restored.X == saved.X && restored.Y == saved.Y;
            cursorSafe = same;
            var receipt = new { Action = "cursor-restored", Target = step, Restored = same, RequestedX = saved.X, RequestedY = saved.Y, ObservedX = restored.X, ObservedY = restored.Y, SetSucceeded = set, SetWin32Error = setError, GetSucceeded = get, GetWin32Error = getError, RestoreError = restoreError?.GetType().Name + (restoreError is null ? "" : ": " + restoreError.Message) };
            steps.Add(receipt); evidence["CursorRestoration"] = receipt;
            if (!same && primary is null) throw new InvalidOperationException("PENDING: original CI cursor restoration was not proved; SetCursorPos Win32=" + setError + ", GetCursorPos Win32=" + getError + ".", restoreError);
        }
    }
    private static InvalidOperationException CursorError(string action, int error) => new("PENDING: " + action + " failed in this CI windowstation; Win32=" + error + " (" + new System.ComponentModel.Win32Exception(error).Message + ").");
    private void CheckDesktopTarget(Native.Point screen, System.Windows.Rect window, bool cursorAtTarget)
    {
        // No UIA/file/dispatcher calls while the system cursor is displaced.
        process.Refresh(); StringBuilder image = new(32768); uint count = (uint)image.Capacity;
        IntPtr foreground = Native.GetForegroundWindow(), hit = Native.WindowFromPoint(screen);
        if (process.HasExited || Native.Owner(hwnd) != pid || !Native.QueryFullProcessImageName(process.Handle, 0, image, ref count) || !string.Equals(image.ToString(), exe, StringComparison.OrdinalIgnoreCase) ||
            foreground != hwnd || hit != hwnd || Native.Window(hwnd) != window || !Native.Client(hwnd).Contains(new System.Windows.Point(screen.X, screen.Y)))
        {
            steps.Add(new { Action = "owned-hover-proof-failed", ExpectedMainHandle = hwnd.ToInt64(), ForegroundHandle = foreground.ToInt64(), HitHandle = hit.ToInt64(), TargetX = screen.X, TargetY = screen.Y });
            throw new InvalidOperationException("PENDING: foreground/hit-test/image/owner/client proof changed before owned mouse input.");
        }
        if (cursorAtTarget)
        {
            if (!Native.GetCursorPos(out Native.Point current)) throw CursorError("GetCursorPos during owned hover", Marshal.GetLastWin32Error());
            if (current.X != screen.X || current.Y != screen.Y) throw new InvalidOperationException("PENDING: CI cursor did not reach the uniquely verified owned target.");
        }
    }
    private void Wait(Func<bool> check, string description, int timeout = 7000)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeout) { Guard(); if (check()) return; Thread.Sleep(100); }
        throw new InvalidOperationException("PENDING: actual Main did not demonstrate " + description + " within bound.");
    }
    private void Stable(string step)
    {
        Stopwatch timer = Stopwatch.StartNew(); string? previous = null; int same = 0, samples = 0;
        while (timer.ElapsedMilliseconds < 7000)
        {
            var nodes = Nodes().Where(Visible).Select(n => { var c = n.Current; var r = c.BoundingRectangle; return c.ClassName + ":" + c.AutomationId + ":" + Math.Round(r.X, 1) + "," + Math.Round(r.Y, 1) + "," + Math.Round(r.Width, 1) + "," + Math.Round(r.Height, 1); }).OrderBy(s => s, StringComparer.Ordinal);
            string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", nodes)))); samples++;
            same = signature == previous ? same + 1 : 0; previous = signature;
            if (same >= 3 && timer.ElapsedMilliseconds >= 900) { steps.Add(new { Action = step, LayoutStableSamples = same + 1, Samples = samples, LayoutSha256 = signature }); return; }
            Thread.Sleep(180);
        }
        throw new InvalidOperationException("PENDING: bounded owned UIA layout did not stabilize: " + step);
    }
    private string Selection(AutomationElement combo)
    {
        if (!combo.TryGetCurrentPattern(SelectionPattern.Pattern, out object pattern)) throw new InvalidOperationException("PENDING: actual theme ComboBox lacks SelectionPattern.");
        var selected = ((SelectionPattern)pattern).Current.GetSelection();
        if (selected.Length != 1 || selected[0].Current.ProcessId != pid) throw new InvalidOperationException("Theme selection is not uniquely owned.");
        return selected[0].Current.Name;
    }
    private void Focus()
    {
        Guard(); var combo = Combo(); combo.SetFocus();
        Wait(() => Combo().Current.HasKeyboardFocus && AutomationElement.FocusedElement.Current.ProcessId == pid, "owned theme ComboBox keyboard focus");
        steps.Add(new { Action = "focus", Target = "ComboLauncherColorMode", ObservedKeyboardFocus = true });
    }
    private void SetTheme(string selected, int key)
    {
        Focus(); Guard(); Native.Send(hwnd, 0x0100, new IntPtr(key), new IntPtr(1)); Native.Send(hwnd, 0x0101, new IntPtr(key), new IntPtr(unchecked((int)0xC0000001)));
        Wait(() => Selection(Combo()) == selected, "real theme selection " + selected); Stable("theme-" + selected);
        string expected = selected == "深色" ? "1" : "0";
        Wait(() => ReadThemeSetting() == expected, "owned Setup.ini UiLauncherColorMode=" + expected);
        steps.Add(new { Action = "theme", Selection = selected, OwnedIniValue = expected, Input = key == 0x23 ? "VK_END" : "VK_HOME" });
    }
    private string? ReadThemeSetting()
    {
        Guard(); string file = Path.Combine(Path.GetDirectoryName(exe)!, "PCL", "Setup.ini");
        using StreamReader reader = new(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        List<string> values = new(); string? line;
        while ((line = reader.ReadLine()) != null) if (line.StartsWith("UiLauncherColorMode:", StringComparison.Ordinal)) values.Add(line[20..]);
        if (values.Count > 1) throw new InvalidOperationException("Ambiguous owned theme setting.");
        return values.SingleOrDefault();
    }
    private double Capture(string step)
    {
        Guard(); var r = Native.Window(hwnd); int width = (int)r.Width, height = (int)r.Height;
        if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new InvalidOperationException("Unsupported own window screenshot bounds.");
        using Bitmap bitmap = new(width, height); using Graphics graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc(); bool printed;
        try { printed = Native.PrintWindow(hwnd, hdc, 2); } finally { graphics.ReleaseHdc(hdc); }
        if (!printed) throw new InvalidOperationException("PENDING: PrintWindow failed for actual owned Main.");
        List<double> luma = new(); HashSet<int> colors = new();
        for (int y = height / 4; y < height - 30; y += 13) for (int x = width / 3; x < width - 30; x += 13) { Color color = bitmap.GetPixel(x, y); colors.Add(color.ToArgb()); luma.Add(.2126 * color.R + .7152 * color.G + .0722 * color.B); }
        if (colors.Count < 3) throw new InvalidOperationException("PENDING: own Main screenshot is uniform/blank.");
        string file = Path.Combine(fixture, "interaction-" + args["pass"] + "-" + step + ".png"); bitmap.Save(file, ImageFormat.Png);
        double mean = luma.Average(); steps.Add(new { Action = "screenshot", Checkpoint = step, File = Path.GetFileName(file), Width = width, Height = height, SampleColors = colors.Count, MeanLuma = mean }); return mean;
    }
    private void Run()
    {
        if (args["notice"] != Notice) throw new InvalidOperationException("PENDING: expected information notice changed after fixture admission.");
        Stable("known-notice"); VerifyInformationNotice(); Capture("notice");
        var button = VerifyInformationNotice(); Click(button, "dismiss-exact-information-notice");
        Wait(() => !Nodes().Any(n => Visible(n) && n.Current.Name == Notice), "only reviewed notice dismissed");
        Stable("launch"); DetectShell(); Capture("launch");
        Click(HeaderNavigation("设置"), "main-settings"); Stable("settings");
        Click(ContextNavigation("个性化"), "personalization"); Stable("personalization");
        Text("外观主题", "");
        if (Selection(Combo()) != "浅色") throw new InvalidOperationException("PENDING: initial synthetic theme differs from default light.");
        double light = Capture("light");
        // Mark before input so a partial dark transition also attempts a real UI rollback.
        themeChanged = true; SetTheme("深色", 0x23); double dark = Capture("dark");
        if (light - dark < 15) throw new InvalidOperationException("PENDING: actual Main did not render an observable light/dark change.");
        SetTheme("浅色", 0x24); themeChanged = false; double restored = Capture("light-restored");
        if (Math.Abs(light - restored) > 10) throw new InvalidOperationException("PENDING: restored theme screenshot differs materially.");
        Focus(); Guard(); Native.Send(hwnd, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
        Wait(() => Native.IsIconic(hwnd), "same owned Main minimized");
        Native.Send(hwnd, 0x0112, new IntPtr(0xF120), IntPtr.Zero);
        Wait(() => !Native.IsIconic(hwnd), "same owned Main restored"); Stable("restored-window"); Focus(); Capture("restored-window");
        AutomationElement theme = Combo(); var themeBounds = theme.Current.BoundingRectangle;
        var scroll = Unique(n => n.Current.AutomationId == "PanBack" && n.Current.ClassName == "ScrollViewer" && n.Current.ControlType == ControlType.Pane &&
            Native.Client(hwnd).Contains(n.Current.BoundingRectangle) && n.Current.BoundingRectangle.Contains(themeBounds), "personalization PanBack containing actual theme ComboBox");
        AssertScrollContainsTheme(theme, scroll);
        bool hasScrollPattern = scroll.TryGetCurrentPattern(ScrollPattern.Pattern, out object rawPattern);
        steps.Add(new { Action = "scroll-provider", AutomationId = scroll.Current.AutomationId, ClassName = scroll.Current.ClassName,
            Bounds = scroll.Current.BoundingRectangle.ToString(CultureInfo.InvariantCulture), ThemeBounds = themeBounds.ToString(CultureInfo.InvariantCulture),
            ThemeAncestorProved = true, ScrollPatternAvailable = hasScrollPattern });
        if (!hasScrollPattern) throw new InvalidOperationException("PENDING: actual owned personalization PanBack ancestor has no ScrollPattern provider.");
        ScrollPattern pattern = (ScrollPattern)rawPattern;
        if (!pattern.Current.VerticallyScrollable || pattern.Current.VerticalScrollPercent > 1) throw new InvalidOperationException("PENDING: personalization has no provable top/scrollable content.");
        double before = pattern.Current.VerticalScrollPercent;
        WithOwnedHover(scroll, "wheel-down", (screen, local, bounds) => {
            Native.Send(hwnd, 0x0200, IntPtr.Zero, Native.Pack(local.X, local.Y)); Thread.Sleep(80); CheckDesktopTarget(screen, bounds, cursorAtTarget: true);
            Native.Send(hwnd, 0x020A, new IntPtr(unchecked((int)0xFE980000)), Native.Pack(screen.X, screen.Y));
        });
        Wait(() => pattern.Current.VerticalScrollPercent > before + .1, "real wheel message changed ScrollPattern offset"); Stable("wheel-down");
        double after = pattern.Current.VerticalScrollPercent; Capture("wheel-down");
        WithOwnedHover(scroll, "wheel-up", (screen, local, bounds) => {
            Native.Send(hwnd, 0x0200, IntPtr.Zero, Native.Pack(local.X, local.Y)); Thread.Sleep(80); CheckDesktopTarget(screen, bounds, cursorAtTarget: true);
            Native.Send(hwnd, 0x020A, new IntPtr(0x01680000), Native.Pack(screen.X, screen.Y));
        });
        Wait(() => pattern.Current.VerticalScrollPercent < after - .1, "real reverse wheel changed ScrollPattern offset"); Stable("wheel-up"); Capture("wheel-up");
        steps.Add(new { Action = "wheel", BeforePercent = before, AfterPercent = after, ReturnedPercent = pattern.Current.VerticalScrollPercent, Input = "owned WM_MOUSEWHEEL +/-360" });
        // Return to the safe launch page; no download/login/update/link/UAC/clear action exists here.
        Click(HeaderNavigation("启动"), "return-launch"); Stable("launch-returned"); Capture("launch-returned");
        Guard(true); if (ReadThemeSetting() != "0") throw new InvalidOperationException("Owned theme setting changed after real UI rollback."); evidence["ThemeRestored"] = true;
        evidence["ObservedScope"] = "Exact local notice, Main settings/personalization/launch, real theme and INI restore, focus, same-HWND minimize/restore, measured wheel offsets and stable owned-window captures.";
    }
}

internal enum ObservedShell { Unverified, Classic, Glass }
internal readonly record struct ShellNode(string AutomationId, string Name, string ClassName, ControlType Type, System.Windows.Rect Bounds);

/// <summary>Exact observed shell identities and geometry, independent of any product assembly or UIA injection.</summary>
internal static class ShellUiPolicy
{
    // Actual native x:Name and AutomationProperties.Name pairs in GlassLauncherView.xaml.
    private static readonly (string Id, string Name)[] Header = [
        ("NavLaunch", "启动"), ("NavDownload", "下载"), ("NavFavorites", "收藏"), ("NavSettings", "设置"), ("NavOther", "更多")
    ];

    internal static (string Id, string Name) HeaderTarget(string name) => Header.Single(target => target.Name == name);
    internal static bool IsGlassMarker(ShellNode node) => node.ClassName == "GlassLauncherView" ||
        node.AutomationId is "AccountButton" or "NavigationMenuButton" || Header.Any(target => target.Id == node.AutomationId);
    internal static ObservedShell Detect(IReadOnlyList<ShellNode> nodes, System.Windows.Rect client, double scale)
    {
        bool classicLaunch = nodes.Any(node => IsClassicHeader(node, "启动", client));
        bool classicSettings = nodes.Any(node => IsClassicHeader(node, "设置", client));
        if (nodes.Any(IsGlassMarker))
        {
            // A partial/new shell must not fall through to an old hidden or stale TextBlock peer.
            if (classicLaunch || classicSettings) throw Pending("mixed classic/glass navigation peers are visible");
            ShellNode[] header = Header.Select(target => ExactlyOne(nodes, node => node.AutomationId == target.Id, target.Id)).ToArray();
            for (int index = 0; index < header.Length; index++)
            {
                var target = Header[index];
                if (!IsGlassHeaderTarget(header[index], target.Id, target.Name, client, scale)) throw Pending("glass header Button identity/geometry differs: " + target.Id);
                if (index > 0 && (Math.Abs(header[index - 1].Bounds.Right - header[index].Bounds.Left) > scale ||
                    Math.Abs(header[index - 1].Bounds.Top - header[index].Bounds.Top) > scale ||
                    Math.Abs(header[index - 1].Bounds.Bottom - header[index].Bounds.Bottom) > scale ||
                    Math.Abs(header[index - 1].Bounds.Width - header[index].Bounds.Width) > scale))
                    throw Pending("glass navigation buttons are not one ordered non-overlapping capsule");
            }
            ShellNode account = ExactlyOne(nodes, node => node.AutomationId == "AccountButton", "AccountButton");
            if (account.Type != ControlType.Button || account.ClassName != "Button" ||
                !account.Name.StartsWith("当前账户：", StringComparison.Ordinal) || account.Name.Length == "当前账户：".Length ||
                !IsGlassHeaderBounds(account.Bounds, client, scale) || account.Bounds.Right > header[0].Bounds.Left + scale ||
                Math.Abs(account.Bounds.Top + account.Bounds.Height / 2 - header[0].Bounds.Top - header[0].Bounds.Height / 2) > scale)
                throw Pending("glass account capsule identity/geometry differs");
            return ObservedShell.Glass;
        }
        // Preserve the original classic matching rules, including its physical-pixel geometry.
        ExactlyOne(nodes, node => IsClassicHeader(node, "启动", client), "classic launch LabText");
        ExactlyOne(nodes, node => IsClassicHeader(node, "设置", client), "classic settings LabText");
        return ObservedShell.Classic;
    }

    internal static bool IsGlassHeaderTarget(ShellNode node, string id, string name, System.Windows.Rect client, double scale) =>
        node.Type == ControlType.Button && node.ClassName == "Button" && node.AutomationId == id && node.Name == name &&
        IsGlassHeaderBounds(node.Bounds, client, scale);
    internal static bool IsGlassRailAnchor(ShellNode node, ShellNode account, System.Windows.Rect client, double scale)
    {
        var bounds = node.Bounds;
        return ValidScale(scale) && ValidBounds(bounds, client) && node.Type == ControlType.Button && node.ClassName == "Button" &&
            node.AutomationId == "NavigationMenuButton" && node.Name == "展开全部页面选项" &&
            bounds.Top >= account.Bounds.Bottom + 4 * scale && bounds.Right <= account.Bounds.Left - 8 * scale &&
            bounds.Width >= 40 * scale && bounds.Width <= 46 * scale && bounds.Height >= 40 * scale && bounds.Height <= 46 * scale;
    }
    internal static bool IsGlassContextTarget(ShellNode node, string name, ShellNode rail, ShellNode account, System.Windows.Rect client, double scale)
    {
        var bounds = node.Bounds;
        if (!IsGlassRailAnchor(rail, account, client, scale) || !ValidBounds(bounds, client)) return false;
        // The real named footer supplies the rail's current X and width, including responsive placement.
        return node.Type == ControlType.Button && node.ClassName == "Button" && node.AutomationId.Length == 0 && node.Name == name &&
            Math.Abs(bounds.Left - rail.Bounds.Left) <= scale && Math.Abs(bounds.Right - rail.Bounds.Right) <= scale &&
            bounds.Top >= account.Bounds.Bottom + 4 * scale && bounds.Bottom <= rail.Bounds.Top + scale &&
            bounds.Width >= 40 * scale && bounds.Width <= 46 * scale &&
            bounds.Height >= 40 * scale && bounds.Height <= 46 * scale;
    }
    private static bool IsClassicHeader(ShellNode node, string name, System.Windows.Rect client) =>
        node.Type == ControlType.Text && node.AutomationId == "LabText" && node.Name == name && node.Bounds.Bottom <= client.Top + 57;
    private static bool IsGlassHeaderBounds(System.Windows.Rect bounds, System.Windows.Rect client, double scale)
    {
        if (!ValidScale(scale) || !ValidBounds(bounds, client)) return false;
        // Exact button identities and their observed row establish the capsules; do not guess responsive insets.
        return bounds.Top >= client.Top + 8 * scale && bounds.Bottom <= client.Top + 104 * scale &&
            bounds.Width >= 36 * scale && bounds.Height >= 36 * scale && bounds.Height <= 56 * scale;
    }
    private static bool ValidScale(double scale) => double.IsFinite(scale) && scale > 0;
    private static bool ValidBounds(System.Windows.Rect bounds, System.Windows.Rect client) =>
        !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 && client.Contains(bounds);
    private static ShellNode ExactlyOne(IReadOnlyList<ShellNode> nodes, Func<ShellNode, bool> predicate, string target)
    {
        ShellNode[] matches = nodes.Where(predicate).ToArray();
        if (matches.Length != 1) throw Pending("expected one visible " + target + "; found " + matches.Length);
        return matches[0];
    }
    private static InvalidOperationException Pending(string reason) => new("PENDING: " + reason + ".");

    internal static void VerifyPolicy()
    {
        var client = new System.Windows.Rect(100, 200, 850, 500);
        ShellNode Classic(string name, double bottom = 45) => new("LabText", name, "TextBlock", ControlType.Text,
            new System.Windows.Rect(client.Left + 320, client.Top + bottom - 20, 30, 20));
        ShellNode[] classic = [Classic("启动"), Classic("设置")];
        Require(Detect(classic, client, 1) == ObservedShell.Classic, "Original classic title identities were rejected.");
        Require(Detect([Classic("启动", 57), Classic("设置", 57)], client, 1) == ObservedShell.Classic, "Classic 57-pixel boundary changed.");
        Reject(() => Detect([Classic("启动", 58), Classic("设置")], client, 1));
        Reject(() => Detect([classic[0]], client, 1));
        Reject(() => Detect([classic[0], classic[1], classic[1]], client, 1));

        foreach (double scale in new[] { 1.0, 1.5 })
        foreach (double logicalWidth in new[] { 850.0, 930.0, 1120.0 })
        {
            var scaledClient = new System.Windows.Rect(client.Left, client.Top, logicalWidth * scale, 500 * scale);
            bool compact = logicalWidth < 930;
            double headerTop = compact ? 18 : 24, headerLeft = compact ? 76 : 82, accountWidth = compact ? 148 : 168;
            System.Windows.Rect Bounds(double x, double y, double width, double height) =>
                new(scaledClient.Left + x * scale, scaledClient.Top + y * scale, width * scale, height * scale);
            ShellNode[] glass = Header.Select((target, index) => new ShellNode(target.Id, target.Name, "Button", ControlType.Button,
                Bounds(headerLeft + accountWidth + 18 + 90 * index, headerTop + 6, 90, 42)))
                .Append(new ShellNode("AccountButton", "当前账户：CI", "Button", ControlType.Button, Bounds(headerLeft, headerTop, accountWidth, 54))).ToArray();
            Require(Detect(glass, scaledClient, scale) == ObservedShell.Glass, "Exact native glass header was rejected.");
            Require(IsGlassHeaderTarget(glass[3], "NavSettings", "设置", scaledClient, scale), "Exact glass settings target was rejected.");
            Reject(() => Detect(glass.Concat(classic).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Where(node => node.AutomationId != "NavSettings").Concat(classic).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Where(node => node.AutomationId != "NavSettings").ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Append(glass[3]).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Select(node => node.AutomationId == "NavSettings" ? node with { Name = "启动" } : node).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Select(node => node.AutomationId == "NavSettings" ? node with { Type = ControlType.Text } : node).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Select(node => node.AutomationId == "NavSettings" ? node with { Bounds = Bounds(518, 70, 90, 42) } : node).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Select(node => node.AutomationId == "NavSettings" ? node with { Bounds = Bounds(518, 30, 1, 42) } : node).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Select(node => node.AutomationId == "NavSettings" ? node with { Bounds = glass[0].Bounds } : node).ToArray(), scaledClient, scale));
            Reject(() => Detect(glass.Select(node => node.AutomationId == "AccountButton" ? node with { Name = "账户" } : node).ToArray(), scaledClient, scale));
            var inset = glass.Select(node => node with { Bounds = new System.Windows.Rect(node.Bounds.X + 30 * scale, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height) }).ToArray();
            Require(Detect(inset, scaledClient, scale) == ObservedShell.Glass, "A detached narrower header was rejected because of a guessed inset.");
            ShellNode account = glass[^1];
            var footer = new ShellNode("NavigationMenuButton", "展开全部页面选项", "Button", ControlType.Button, Bounds(22, 400, 44, 44));
            var rail = new ShellNode("", "个性化", "Button", ControlType.Button, Bounds(22, 210, 44, 44));
            Reject(() => Detect(classic.Append(footer).ToArray(), scaledClient, scale));
            Require(IsGlassRailAnchor(footer, account, scaledClient, scale), "Exact native rail footer was rejected.");
            Require(IsGlassContextTarget(rail, "个性化", footer, account, scaledClient, scale), "Exact contextual icon button was rejected.");
            Require(!IsGlassContextTarget(rail with { Name = "初始化本页设置" }, "个性化", footer, account, scaledClient, scale), "A reset action matched contextual navigation.");
            Require(!IsGlassContextTarget(rail with { Type = ControlType.Text }, "个性化", footer, account, scaledClient, scale), "A text peer matched a glass native Button.");
            Require(!IsGlassContextTarget(rail with { Bounds = Bounds(120, 210, 44, 44) }, "个性化", footer, account, scaledClient, scale), "A portal/page peer outside the observed rail matched contextual navigation.");
            Require(!IsGlassContextTarget(rail with { Bounds = Bounds(22, 30, 44, 44) }, "个性化", footer, account, scaledClient, scale), "A header control matched contextual navigation.");
            Require(!IsGlassContextTarget(rail with { Bounds = Bounds(22, 470, 44, 44) }, "个性化", footer, account, scaledClient, scale), "A clipped contextual control was accepted.");
            Require(!IsGlassContextTarget(rail, "个性化", footer with { Name = "收起全部页面选项" }, account, scaledClient, scale), "An unreviewed open navigation portal was accepted.");
            Require(!IsGlassContextTarget(rail, "个性化", footer with { AutomationId = "OtherButton" }, account, scaledClient, scale), "A missing real rail footer was accepted.");
        }
        Console.WriteLine("PASS shell UI policy: unchanged classic selectors; exact native glass header/rail identities and DPI geometry; partial, mixed, duplicate, renamed, misplaced and clipped peers remain pending.");
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("PENDING:", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A missing, ambiguous or different shell identity was accepted.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr first, IntPtr second, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    internal static int Owner(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return (int)pid; }
    internal static double DpiScale(IntPtr hwnd) { uint dpi = GetDpiForWindow(hwnd); if (dpi == 0) throw new InvalidOperationException("PENDING: owned glass Main DPI is unavailable."); return dpi / 96d; }
    internal static string Title(IntPtr hwnd) { StringBuilder text = new(1024); GetWindowText(hwnd, text, text.Capacity); return text.ToString().Trim(); }
    internal static string Class(IntPtr hwnd) { StringBuilder text = new(256); GetClassName(hwnd, text, text.Capacity); return text.ToString(); }
    internal static System.Windows.Rect Window(IntPtr hwnd) { if (!GetWindowRect(hwnd, out Rect r)) throw new InvalidOperationException("Window bounds unavailable."); return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); }
    internal static System.Windows.Rect Client(IntPtr hwnd) { if (!GetClientRect(hwnd, out Rect r)) throw new InvalidOperationException("Client bounds unavailable."); Point p = new(); if (!ClientToScreen(hwnd, ref p)) throw new InvalidOperationException("Client origin unavailable."); return new(p.X, p.Y, r.Right, r.Bottom); }
    internal static IntPtr Pack(int x, int y) { if (x < short.MinValue || x > short.MaxValue || y < short.MinValue || y > short.MaxValue) throw new InvalidOperationException("Message coordinates exceed signed client contract."); return new IntPtr(unchecked((int)(((uint)(ushort)y << 16) | (ushort)x))); }
    internal static void Send(IntPtr hwnd, uint message, IntPtr first, IntPtr second) { if (SendMessageTimeout(hwnd, message, first, second, 2, 1200, out _) == IntPtr.Zero) throw new InvalidOperationException("PENDING: bounded owned HWND message was not processed: " + message); }
    internal static void CheckNamedWindows(int pid, IntPtr main)
    {
        bool unexpected = false;
        EnumWindows((window, _) => { if (Owner(window) == pid && IsWindowVisible(window) && window != main && Title(window).Length != 0) unexpected = true; return true; }, IntPtr.Zero);
        if (unexpected) throw new InvalidOperationException("PENDING: unexpected named actual product window; never auto-clicked.");
    }
}
