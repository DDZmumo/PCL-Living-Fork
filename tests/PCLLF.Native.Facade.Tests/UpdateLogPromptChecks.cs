using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Presentation.Shell;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main message factory, MyMsgText, Markdown, native MyCheckBox, settings
// cache and physical GUID-fixture Setup.ini. No launcher startup, HTTP or windows.
internal static class UpdateLogPromptChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        UpdateLogPromptLayoutChecks.Run(environment);
        string file = Path.Combine(Paths.Base, "PCL", "Setup.ini");
        Native.Check(Native.Full(file).StartsWith(Native.Full(environment.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Notice setting escaped its GUID fixture.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "SystemUpdateLogPrompt:False\r\nKeep:原设置\r\n");
        ModBase.IniClearCache("Setup");
        Native.Cache(LegacyUpdateLogPrompt.Key, "");
        Native.Cache("ToolLauncherUpdateIgnoredTag", "v9.9.9");
        string ignored = Settings.Get<string>("ToolLauncherUpdateIgnoredTag");
        string identity = (string)Native.Property(typeof(FormMain), "CurrentUpdateLogIdentity").GetValue(null)!;
        Native.Check(identity == LegacyUpdateLogPrompt.BuildIdentity(ModBase.VersionBaseName, ModBase.ReleaseBaselineVersion,
            ModBase.ReleaseBaselineIsPrerelease, ModBase.VersionCode, (int)ModBase.BuildType), "The dismissal did not use the actual Main build identity.");

        object runtime = Native.Field(typeof(ModMain), "NativeMessages").GetValue(null)!;
        var host = (MainMessageHost<ModMain.MyMsgBoxConverter, ModMain.MyMsgBoxType>)Native.Get(runtime, "host")!;
        var beforePanel = host.Panel;
        var beforeQueue = host.Queue;
        var beforeUi = host.IsUiThread;
        var beforeDrag = host.DragStop;
        ConcurrentQueue<ModMain.MyMsgBoxConverter> queue = new();
        Grid fakePanel = new();
        host.Panel = () => fakePanel;
        host.Queue = () => queue;
        host.IsUiThread = () => false;
        host.DragStop = () => { };
        object transport = Activator.CreateInstance(Native.Type("PCL.ModLauncherUpdate+UpdateTransport"), true)!;
        int requests = 0;
        Native.Set(transport, "ReadJson", new Func<string, JObject>(_ => { requests++; throw new IOException("Fixture offline"); }));
        const string summary = "# 本地更新说明\n\n- 离线和开发版本同样使用原生样式\n- 个人数据保留\n";
        try
        {
            byte[] original = File.ReadAllBytes(file);
            using (FileStream held = new(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Dialog(true, (dialog, body, box) =>
                {
                    Native.Check(!box.Checked, "The legacy permanent false value suppressed the new notice.");
                    UserCheck(box, true);
                    Native.Check(!box.Checked && Settings.Get<string>(LegacyUpdateLogPrompt.Key) == "", "A failed notice write falsely changed its native checkbox/cache.");
                });
            Native.Check(File.ReadAllBytes(file).SequenceEqual(original), "A rejected dismissal changed the original Setup.ini bytes.");

            Dialog(true, (dialog, body, box) =>
            {
                Native.Check(!box.Checked, "A failed dismissal leaked into the next dialog.");
                UserCheck(box, true);
                Native.Check(box.Checked && Settings.Get<string>(LegacyUpdateLogPrompt.Key) == identity, "Checking did not persist the actual build identity.");
                Preview(dialog);
            });
            Native.Check(ModBase.ReadIni("Setup", LegacyUpdateLogPrompt.Key, "") == identity && ModBase.ReadIni("Setup", "Keep", "") == "原设置", "A notice save lost its dismissal or another Setup.ini key.");
            int requestsBeforeSuppressed = requests;
            Native.Check((bool)Native.Static("PCL.ModLauncherUpdate", "TryShowCurrentReleaseNotes", true, summary, transport)! && queue.IsEmpty && requests == requestsBeforeSuppressed,
                "Suppressed automatic notes still queued a modal or requested release metadata.");

            Dialog(false, (_, _, box) =>
            {
                Native.Check(box.Checked, "Manual viewing must reflect the current build's saved dismissal.");
                UserCheck(box, false);
                Native.Check(!box.Checked && Settings.Get<string>(LegacyUpdateLogPrompt.Key) == "", "Manual unchecking did not restore this version's automatic notes.");
            });
            Dialog(true, (_, _, box) => Native.Check(!box.Checked, "Automatic viewing did not resume after manual unchecking."));
            Native.Check(Settings.Get<string>("ToolLauncherUpdateIgnoredTag") == ignored, "Notice preferences changed the independent newer-version reminder.");
            bool published = (bool)Native.Static("PCL.ModLauncherUpdate", "UsesPublishedReleaseNotes", ModBase.VersionBaseName, ModBase.ReleaseBaselineIsPrerelease, ModBase.BuildType)!;
            Native.Check(published ? requests > 0 : requests == 0, "Development builds requested release metadata or official offline notices bypassed their lookup.");
            if (published)
            {
                Native.Set(transport, "ReadJson", new Func<string, JObject>(_ => new JObject
                {
                    ["tag_name"] = "v" + ModBase.VersionBaseName, ["draft"] = false, ["prerelease"] = false,
                    ["body"] = "# 正式版完整发布说明\n\n**完整 Markdown**\n\n- 正式版功能\n"
                }));
                Dialog(false, (_, body, box) =>
                {
                    var notes = (FlowDocumentScrollViewer)Native.Get(body, "Notes")!;
                    string rendered = new TextRange(notes.Document.ContentStart, notes.Document.ContentEnd).Text;
                    Native.Check(rendered.Contains("正式版完整发布说明", StringComparison.Ordinal) && !rendered.Contains("**", StringComparison.Ordinal),
                        "The official Main route did not use the returned current-release Markdown in its native prompt.");
                });
            }

            // The formal, exact-tag Markdown and both fallbacks share this same
            // native content view; lookup validity is exercised by startup notes.
            foreach (string markdown in new[] { "# 正式发布说明\n\n**完整 Markdown**\n\n- 正式版功能\n", summary, "- 开发版基线说明" })
            {
                FrameworkElement body = NewBody(markdown, false, _ => true);
                MyMsgText modal = new(new ModMain.MyMsgBoxConverter { Title = "版本说明", Button1 = "确定", Button2 = "查看发布页", ContentFactory = () => body });
                CheckLayout(modal, body, (MyCheckBox)Native.Get(body, "Suppress")!);
            }
            CheckStartupPromptOrder(queue);
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS native update notes: genuine Main modal factory/Markdown/MyCheckBox with fixed footer, legacy false ignored, failed-save bytes/cache/UI rollback, current-build dismissal, manual restore, automatic resume and independent updater preference; GUID fixture only, no HTTP/browser/startup.");
        }
        finally
        {
            host.Panel = beforePanel;
            host.Queue = beforeQueue;
            host.IsUiThread = beforeUi;
            host.DragStop = beforeDrag;
        }

        void Dialog(bool automatic, Action<MyMsgText, FrameworkElement, MyCheckBox> inspect)
        {
            Exception? callbackFailure = null;
            bool observed = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ModMain.MyMsgBoxConverter? converter = null;
                try
                {
                    Native.Check(queue.TryDequeue(out converter), "The actual Main route did not enqueue its native content dialog.");
                    MyMsgText dialog = new(converter!);
                    FrameworkElement body = (FrameworkElement)((ContentControl)dialog.FindName("PanCaption")).Content;
                    Native.Check(body.GetType() == Native.Type("PCL.ReleaseNotesPromptView"), "The actual notice route used a different content body.");
                    MyCheckBox box = (MyCheckBox)Native.Get(body, "Suppress")!;
                    Native.Check(converter!.Button3 == "" && converter.Button1 == "确定" && converter.Button2 == "查看发布页", "The notice still uses a third permanent opt-out button.");
                    CheckLayout(dialog, body, box);
                    inspect(dialog, body, box);
                    observed = true;
                }
                catch (Exception error) { callbackFailure = error; }
                finally { if (converter is not null) { converter.Result = 1; converter.WaitFrame.Continue = false; } }
            }));
            Native.Check((bool)Native.Static("PCL.ModLauncherUpdate", "TryShowCurrentReleaseNotes", automatic, summary, transport)!, "The native notice route declined a supported build.");
            if (callbackFailure is not null) throw callbackFailure;
            Native.Check(observed && queue.IsEmpty, "The actual modal was not fully observed/consumed.");
        }
    }

    private static void CheckStartupPromptOrder(ConcurrentQueue<ModMain.MyMsgBoxConverter> queue)
    {
        const string autoCheckKey = "ToolLauncherUpdateAutoCheck";
        bool saved = Settings.Get<bool>(autoCheckKey);
        Type preload = Native.Type("PCL.GlassStartupPreload");
        var interfaceSlot = Native.Field(preload, "_interfaceShown");
        var releaseSlot = Native.Field(preload, "_release");
        var sourceSlot = Native.Field(preload, "_releaseSource");
        var running = Native.Field(typeof(ModLauncherUpdate), "CheckRunning");
        var interfaceReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var noticeClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object? oldInterface = interfaceSlot.GetValue(null), oldRelease = releaseSlot.GetValue(null), oldSource = sourceSlot.GetValue(null);
        int major = Version.Parse(ModBase.ReleaseBaselineVersion.Split('-')[0]).Major + 1;
        try
        {
            Native.Cache(autoCheckKey, true);
            interfaceSlot.SetValue(null, interfaceReady);
            sourceSlot.SetValue(null, Native.Static("PCL.ModLauncherUpdate", "PreferredUpdateSource"));
            releaseSlot.SetValue(null, Task.FromResult(new JObject { ["tag_name"] = $"v{major}.0.0" }));
            Native.Static("PCL.ModLauncherUpdate", "CheckForUpdates", true, noticeClosed.Task);
            VisualAssetChecks.Pump(() => releaseSlot.GetValue(null) is null, "read the release checked during the splash");
            Native.Check(queue.IsEmpty, "The automatic update prompt appeared during the splash.");
            interfaceReady.SetResult(true);
            VisualAssetChecks.Observe(120);
            Native.Check(queue.IsEmpty && (int)running.GetValue(null)! == 1, "An available update appeared before the announcement closed.");
            noticeClosed.SetResult(true);
            VisualAssetChecks.Pump(() => !queue.IsEmpty, "show the available update after the announcement closes");
            Native.Check(queue.TryPeek(out var prompt) && prompt.Title == "发现 PCL Living Fork 新版本", "The automatic updater lost its confirmation prompt.");
        }
        finally
        {
            interfaceReady.TrySetResult(false);
            noticeClosed.TrySetResult(true);
            VisualAssetChecks.Pump(() => {
                while (queue.TryDequeue(out var prompt)) { prompt.Result = 0; prompt.WaitFrame.Continue = false; }
                return (int)running.GetValue(null)! == 0;
            }, "close the fixture update confirmation");
            interfaceSlot.SetValue(null, oldInterface);
            releaseSlot.SetValue(null, oldRelease);
            sourceSlot.SetValue(null, oldSource);
            Native.Cache(autoCheckKey, saved);
        }
    }

    private static FrameworkElement NewBody(string markdown, bool suppressed, Func<bool, bool> persist) =>
        (FrameworkElement)Activator.CreateInstance(Native.Type("PCL.ReleaseNotesPromptView"), Native.Flags, null,
            new object?[] { markdown, suppressed, persist, (Action<string>)(_ => throw new InvalidOperationException("Browser forbidden")) }, null)!;

    private static void UserCheck(MyCheckBox box, bool value)
    {
        // Offscreen controls do not raise OnChange from the IsLoaded-only setter.
        // Invoke that genuine event boundary after changing the actual native DP.
        box.Checked = value;
        Native.Call(typeof(MyCheckBox), "OnChange", box, true);
    }

    private static void CheckLayout(MyMsgText dialog, FrameworkElement body, MyCheckBox box)
    {
        Native.Check(box.GetType() == typeof(MyCheckBox) && box.Text == "下次更新前不再弹出" &&
            ReferenceEquals(box.Parent, body) && Grid.GetRow(box) == 1, "The notice footer is not the original native checkbox/text outside the Markdown viewer.");
        var notes = Native.Get(body, "Notes") as FlowDocumentScrollViewer ?? throw new InvalidOperationException("Missing native Markdown viewer");
        Native.Check(ReferenceEquals(notes.Parent, body) && Grid.GetRow(notes) == 0 &&
            notes.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "The Markdown body lost its independent scrolling area.");
        Native.Check(((MyButton)dialog.FindName("Btn3")).Visibility == Visibility.Collapsed, "The obsolete third opt-out button is still visible.");
        dialog.Measure(new Size(760, 650)); dialog.Arrange(new Rect(0, 0, 760, 650)); dialog.UpdateLayout();
        var caption = (ScrollViewer)dialog.FindName("PanCaption");
        caption.ApplyTemplate();
        var viewport = (FrameworkElement)caption.Template.FindName("PART_ScrollContentPresenter", caption);
        Rect bounds = box.TransformToAncestor(viewport).TransformBounds(new Rect(box.RenderSize));
        Native.Check(box.ActualHeight > 0 && bounds.Top >= -0.5 && bounds.Bottom <= viewport.ActualHeight + 0.5,
            "The native checkbox's complete bounds do not fit the real content viewport.");
    }

    private static void Preview(MyMsgText dialog)
    {
        string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--preview-path");
        if (index < 0 || index + 1 >= args.Length) return;
        dialog.Opacity = 1;
        ((RotateTransform)dialog.FindName("TransformRotate")).Angle = 0;
        ((TranslateTransform)dialog.FindName("TransformPos")).Y = 0;
        dialog.Measure(new Size(760, 650)); dialog.Arrange(new Rect(0, 0, 760, 650)); dialog.UpdateLayout();
        RenderTargetBitmap bitmap = new(760, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
        PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
        string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1]))!, "update-log-prompt-preview.png");
        using FileStream stream = File.Create(file); png.Save(stream);
    }
}
