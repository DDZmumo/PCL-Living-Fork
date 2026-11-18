using System.Security.Cryptography;
using PCL;
using PCLCS;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcRenameAdmissionChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope tasks = new(environment);
        string root = Path.Combine(tasks.Root, "rename-admission"), game = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(game); tasks.SelectMinecraft(game);
        string source = Import(root, game); var original = MmcInstanceInspector.Read(source);
        var owner = new ModMinecraft.McInstance(source).Load();
        var ui = Native.Type("PCL.MmcLifecycleUi");
        var select = Native.Property(ui, "SelectRenameName"); var register = Native.Property(ui, "RegisterTask"); var published = Native.Property(ui, "Published");
        object? previousSelect = select.GetValue(null), previousRegister = register.GetValue(null), previousPublished = published.GetValue(null);
        var scheduler = (LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action oldRefresh = scheduler.RefreshExtraDownload; using ManualResetEventSlim refreshed = new(false);
        ModLoader.LoaderCombo<string>? actual = null; Guid replacementId = Guid.Empty; string replacementHash = ""; bool wasPublished = false;
        try {
            scheduler.RefreshExtraDownload = refreshed.Set;
            register.SetValue(null, new Action<ModLoader.LoaderCombo<string>>(loader => { actual = loader; ModLoader.LoaderTaskbar.Add(loader); }));
            published.SetValue(null, new Action<MmcInstanceLifecycleResult>(_ => wasPublished = true));
            select.SetValue(null, new Func<string, string, string>((_, parent) => {
                var relocated = new MmcInstanceLifecycleBackend().RelocateAsync(new(source, Path.Combine(parent, "original-retained"),
                    original.Definition!.InstanceId, original.Definition.Generation, original.PrimarySha256!, MmcInstanceRelocationKind.Rename)).GetAwaiter().GetResult();
                Native.Check(relocated.Status == MmcInstanceBackendStatus.Succeeded, "Controlled concurrent relocation failed: " + relocated.Failure);
                string replacementRoot = Path.Combine(root, "replacement-input"); Directory.CreateDirectory(replacementRoot);
                Native.Check(Import(replacementRoot, game) == source, "Replacement used another path.");
                replacementId = MmcInstanceInspector.Read(source).Definition!.InstanceId; replacementHash = TreeHash(source);
                Native.Check(replacementId != original.Definition.InstanceId, "Replacement retained the first identity.");
                return "must-not-rename-replacement";
            }));
            Native.Static("PCL.MmcLifecycleUi", "PromptRename", owner, null);
            Native.Check(actual is not null, "Real rename prompt did not dispatch its task.");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (actual!.State == ModBase.LoadState.Loading || !refreshed.IsSet) {
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, deadline);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Real stale rename task did not finish.");
                Thread.Sleep(5);
            }
            Native.Check(!wasPublished && actual.State == ModBase.LoadState.Failed && MmcInstanceInspector.Read(source).Definition?.InstanceId == replacementId &&
                TreeHash(source) == replacementHash && !Directory.Exists(Path.Combine(Path.GetDirectoryName(source)!, "must-not-rename-replacement")),
                "Rename accepted a different identity installed while its name dialog was open.");
            Native.Check(!(bool)Native.Property(ui, "HasActiveOperation").GetValue(null)!, "Rejected stale rename retained its busy state.");
        } finally { scheduler.RefreshExtraDownload = oldRefresh; select.SetValue(null, previousSelect); register.SetValue(null, previousRegister); published.SetValue(null, previousPublished); }
        InstanceConfigProviders.FlushAndDisposeWithin(root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-rename-admission: actual prompt/task reject a different identity installed at the same path while the name chooser is open; both instances and replacement bytes remain preserved.");
    }
    private static string Import(string root, string game) => (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
    private static string TreeHash(string root) => string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))));
}
