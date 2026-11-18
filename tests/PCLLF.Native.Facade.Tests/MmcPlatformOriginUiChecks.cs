using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using PCL;
using PCLCS;
using PCLCS.Loaders;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main views/Loader and owned fixtures. No installed Java, game process, external metadata or substitute backend.
internal static class MmcPlatformOriginUiChecks {
    internal static void RunViews(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        var first = new MmcPlatformReleaseDescriptor(ResourcePlatforms.Modrinth, "2:ProjectA", "ProjectA", "VersionA",
            new("sha512:" + new string('a', 128), "first.zip", "https://cdn.modrinth.com/first.zip", 12, new string('b', 40), new string('a', 128)), new string('c', 64));
        var second = first with { VersionId = "VersionB", File = first.File with { FileName = "second.zip", DownloadUrl = "https://cdn.modrinth.com/second.zip" } };
        object selector = New("PCL.MmcPlatformReleaseSelectView", ImmutableArray.Create(first, second), "选择原包对应的发布文件", "");
        var controls = (IList)Native.Get(selector, "Selections")!;
        Native.Check(controls.Count == 2 && controls.Cast<MyListItem>().All(item => !item.Checked) && Property(selector, "Selected") is null && !(bool)Property(selector, "CanConfirm")!,
            "The real platform selector preselected an ambiguous/equal-digest release file.");
        var releasePort = Native.Static("PCL.MessageDialogPlatform", "VersionSelection", selector) ?? throw new IOException("Missing real release-dialog selection port.");
        Native.Check(!((Func<bool>)Native.Get(releasePort, "HasSelection")!)(), "The actual modal accepted an unselected release.");
        ((MyListItem)controls[1]!).Checked = true;
        Native.Check(Equals(Property(selector, "Selected"), second) && (bool)Property(selector, "CanConfirm")!, "An explicit complete release file did not enable confirmation.");
        Native.Check(((Func<bool>)Native.Get(releasePort, "HasSelection")!)(), "The actual modal ignored the explicit release selection.");
        object single = New("PCL.MmcPlatformReleaseSelectView", ImmutableArray.Create(first), "选择文件", "");
        Native.Check(Property(single, "Selected") is null && !(bool)Property(single, "CanConfirm")!, "A single hash candidate bypassed explicit selection.");
        string original = Path.Combine(files.Root, "original.zip"); File.WriteAllText(original, "Input form only; backend never consumes this inert file.");
        object input = New("PCL.MmcPlatformOriginInputView", new MmcInstanceOrigin(new string('d', 64), "old-name.zip"));
        Native.Check(!(bool)Property(input, "CanConfirm")!, "The real origin form began with implicit platform/project/version authority.");
        var inputPort = Native.Static("PCL.MessageDialogPlatform", "VersionSelection", input) ?? throw new IOException("Missing real origin-form selection port.");
        Native.Check(!((Func<bool>)Native.Get(inputPort, "HasSelection")!)(), "The actual modal accepted an incomplete origin form.");
        ((MyTextBox)Native.Get(input, "OriginalZip")!).Text = original;
        ((MyComboBox)Native.Get(input, "Platform")!).SelectedIndex = 0;
        ((MyTextBox)Native.Get(input, "ProjectId")!).Text = "ProjectA";
        ((MyTextBox)Native.Get(input, "VersionId")!).Text = "VersionA";
        Native.Check((bool)Property(input, "CanConfirm")!, "Complete explicit original/project/version fields did not enable reading release files.");
        Native.Check(((Func<bool>)Native.Get(inputPort, "HasSelection")!)(), "The actual modal ignored the complete origin form.");
        ((MyTextBox)Native.Get(input, "ProjectId")!).Text = "net.minecraftforge";
        Native.Check(!(bool)Property(input, "CanConfirm")!, "An MMC UID was accepted as a stable platform project ID.");
        ((MyTextBox)Native.Get(input, "ProjectId")!).Text = "123"; ((MyComboBox)Native.Get(input, "Platform")!).SelectedIndex = 1;
        ((MyTextBox)Native.Get(input, "VersionId")!).Text = "000456";
        Native.Check(!(bool)Property(input, "CanConfirm")!, "A noncanonical CurseForge file ID was accepted.");
        ((MyTextBox)Native.Get(input, "VersionId")!).Text = "456";
        Native.Check((bool)Property(input, "CanConfirm")!, "The explicit CurseForge project/file fields did not enable selection.");
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-platform-origin-views: genuine LF form and release controls require explicit project/version/file, including one or equal-digest candidates; no network or JVM.");
    }
    internal static void RunCancellationUnstarted(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment); string root = Path.Combine(files.Root, "platform-binding-cancel"), game = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(root); Directory.CreateDirectory(game); files.SelectMinecraft(game);
        string version = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
        var owner = new ModMinecraft.McInstance(version).Load(); var before = MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(version));
        int requests = 0;
        var catalog = MmcPlatformOriginCatalog.ForLoopbackFixtures((_, _) => {
            Interlocked.Increment(ref requests); throw new IOException("An unstarted canceled origin worker cannot request metadata.");
        }, MmcPlatformApiPolicy.ForLoopbackFixtures(new("http://127.0.0.1:1/v2/"), new("http://127.0.0.1:1/v1/")));
        Type ui = Native.Type("PCL.MmcPlatformOriginUi"), updates = Native.Type("PCL.MmcPackageUpdateUi");
        var register = Native.Property(ui, "RegisterTask"); object? oldRegister = register.GetValue(null);
        var scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action oldRefresh = scheduler.RefreshExtraDownload; ModLoader.LoaderCombo<string>? actual = null;
        try {
            scheduler.RefreshExtraDownload = () => { };
            register.SetValue(null, new Action<ModLoader.LoaderCombo<string>>(value => { actual = value; ModLoader.LoaderTaskbar.Add(value); }));
            using (var pause = new MmcRootUpdatePause(() => actual)) {
                Native.Static("PCL.MmcPlatformOriginUi", "StartBinding", owner, Path.Combine(root, "owned.zip"), ResourcePlatforms.Modrinth, "ProjectA", "VersionA", catalog);
                Native.Check(actual is not null && pause.Captured, "The real origin Loader root was not captured before worker scheduling.");
                actual!.Cancel(); pause.Release(); pause.AwaitReturn();
                Native.Check(!(bool)Native.Property(updates, "HasActiveOperation").GetValue(null)!, "Unstarted origin cancellation retained the shared MMC update busy fence.");
                Native.Check(Native.Get(actual.Loaders.Single(), "LastRunningThread") is null && requests == 0, "Unstarted origin cancellation left a real producer or metadata request.");
                actual.Loaders.Single().Start(version);
                Native.Check(Native.Get(actual.Loaders.Single(), "LastRunningThread") is null && requests == 0, "Late worker Start escaped the canceled origin fence.");
            }
            Native.Check(MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(version)).Sha256 == before.Sha256, "Unstarted origin cancellation changed the primary definition.");
            Native.Call(typeof(FileTaskScope), "FinishActualTasks", files);
        } finally { register.SetValue(null, oldRegister); scheduler.RefreshExtraDownload = oldRefresh; if (actual is not null) ModLoader.LoaderTaskbar.Remove(actual); }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-platform-origin-cancel-unstarted: actual Main Loader cancellation and late Start keep zero producers/requests and preserve source authority; no JVM or game.");
    }
    private static object New(string name, params object?[] arguments) => Activator.CreateInstance(Native.Type(name), Native.Flags & ~BindingFlags.Static, null, arguments, null)!;
    private static object? Property(object target, string name) => Native.Property(target.GetType(), name).GetValue(target);
}
