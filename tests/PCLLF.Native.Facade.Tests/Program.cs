using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace PCLLF.Native.Facade.Tests;
internal static class Program
{
    private static readonly Dictionary<string, Action<FixtureEnvironment>> Cases = new(StringComparer.Ordinal)
    {
        ["map-details"] = MapResourceChecks.MapDetails,
        ["instance-delete"] = InstanceDeletionChecks.Run,
        ["launcher-update-page"] = LauncherUpdatePageChecks.Run,
        ["title-theme-toggle"] = TitleThemeToggleChecks.Run,
        ["theme-transition"] = ThemeTransitionChecks.Run,
        ["deferred-update"] = DeferredUpdateButtonChecks.Run,
        ["window-chrome"] = WindowChromeChecks.Run,
        ["resource-entry"] = MapResourceChecks.ResourceEntries,
        ["world-details"] = WorldChecks.Run,
        ["registry-names"] = RegistryChecks.Run,
        ["registry-data"] = RegistryDataChecks.Run,
        ["theme"] = environment => { ThemeColdSettingsChecks.Run(environment); TooltipThemeChecks.Run(environment); ThemeScenarios.Run(Application.Current, Path.Combine(AppContext.BaseDirectory, "assets")); },
        ["file-tasks"] = FileTaskScenarios.Run,
        ["mod-dependencies"] = NativeDependencyChecks.Local,
        ["mod-dependency-online"] = NativeDependencyChecks.Online,
        ["updater"] = NativeUpdateChecks.Run,
        ["backup-resources"] = BackupResourceChecks.Run,
        ["instance-clone"] = CloneNativeChecks.Run,
        ["modpack-update"] = ModpackUpdateChecks.Run,
        ["environment-history"] = EnvironmentHistoryChecks.Run,
        ["local-features"] = LocalFeatureChecks.Run,
        ["launch-dependencies"] = LaunchDependencyChecks.Run,
        ["dependency-graph"] = DependencyGraphChecks.Run,
        ["toolbox"] = ToolboxChecks.Run,
        ["visual-assets"] = VisualAssetChecks.Run,
        ["launcher-lifecycle"] = LauncherLifecycleChecks.Run,
        ["modpack-mode"] = ModpackModeChecks.Run,
        ["review-fixes"] = ReviewFixesChecks.Run,
        ["migrated-controls"] = ControlOriginalChecks.Run,
        ["map-install-recycle"] = MapInstallChecks.Run,
        ["resource-updates"] = ResourceUpdateChecks.Run,
        ["mmc-import-contract"] = MmcImportChecks.Run,
        ["launch-process"] = LaunchProcessChecks.Run,
        ["pack-root-install"] = PackRootInstallChecks.Run,
        ["account-ui"] = AccountUiChecks.Run,
        ["mod-favorites"] = ModFavoriteChecks.Run,
        ["update-log-prompt"] = UpdateLogPromptChecks.Run,
        ["instance-sidebar"] = InstanceSidebarChecks.Run
    };
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This host requires a real Windows .NET10/WPF runtime.");
            if (args.SequenceEqual(new[] { "--list-groups" }, StringComparer.Ordinal))
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Cases.Keys.ToArray()));
                return 0;
            }
            if (args.Contains("--modpack-download-child", StringComparer.Ordinal))
                return ModpackDownloadProcess.RunChild(args);
            if (BackupResourceChecks.TryRunIdleChild(args, out int backupIdleExit))
                return backupIdleExit;
            string[] selected = ReadGroups(args);
            bool isolated = args.Contains("--isolated", StringComparer.Ordinal);
            if (!isolated)
                return RunChild(args);
            string root = ReadOption(args, "--fixture") ?? throw new ArgumentException("Missing isolated fixture root.");
            ValidateRoot(root);
            Native.Check(Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(root, "output")), "The child is not running from the complete GUID output directory.");
            using FixtureEnvironment environment = new(root);
            if (args.Contains("--dependency-index-child", StringComparer.Ordinal))
                return NativeDependencyChecks.IndexChild(environment, args);
            foreach (string group in selected)
            {
                Cases[group](environment);
                environment.AssertNoForbiddenIo();
                Console.WriteLine("PASS native facade " + group);
            }

            environment.AssertNoForbiddenIo();
            Native.Check(Application.Current.Windows.Count == 0, "A selected facade group left a real window open.");
            Console.WriteLine("PASS " + selected.Length + " selected groups; genuine Main data/adapters. Selected UI/task families drain real Dispatcher callbacks after LF Startup hooks are detached. EntryAssembly remains the test host; actual product cold entry is checked separately.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL native facade: " + error);
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + key + " value.");
    }

    private static string[] ReadGroups(string[] args)
    {
        string? value = ReadOption(args, "--groups");
        string[] selected = value is null ? Cases.Keys.Where(name => name is not ("registry-data" or "account-ui")).ToArray() : value.Split(',', StringSplitOptions.TrimEntries);
        if (selected.Length == 0 || selected.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Empty facade selection.");
        foreach (string item in selected)
            if (!Cases.ContainsKey(item))
                throw new ArgumentException("Unknown facade group: " + item);
        if (selected.Contains("registry-data", StringComparer.Ordinal) && selected.Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("Select registry-data alone in its fresh hosted-profile process.");
        if (selected.Contains("account-ui", StringComparer.Ordinal) && selected.Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("Select account-ui alone in its fresh guarded Shared configuration process.");
        return selected.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static int RunDownloadChild(string nonce) => RunChild(["--modpack-download-child", nonce], true);

    private static int RunChild(string[] args, bool dedicatedDownload = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "output");
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        Exception? primaryFailure = null;
        bool outputPrinted = false;
        try
        {
            CopyOutput(AppContext.BaseDirectory, output);
            string executable = Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("Build the actual Windows apphost before running this fixture.", executable);
            ProcessStartInfo start = new(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = root,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in args) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--isolated"); start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(root);
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the actual facade child.");
            Stopwatch run = Stopwatch.StartNew();
            int Remaining() => Math.Max(0, 60000 - checked((int)run.ElapsedMilliseconds));
            stdout = process.StandardOutput.ReadToEndAsync(); ObserveFault(stdout);
            stderr = process.StandardError.ReadToEndAsync(); ObserveFault(stderr);
            Task[] drains = [stdout, stderr];
            if (dedicatedDownload)
            {
                string receipt = Path.Combine(root, ModpackDownloadProcess.ReceiptName);
                while (!File.Exists(receipt))
                {
                    if (Remaining() == 0) throw new TimeoutException("Dedicated download did not prove completion within the same60-second deadline.");
                    if (process.WaitForExit(Math.Min(25, Remaining())))
                        throw new InvalidOperationException("Dedicated download exited before its complete owned-manager receipt: " + process.ExitCode);
                }
                ModpackDownloadProcess.RequireReceipt(receipt, args[1], process.Id, root);
                if (process.HasExited) throw new InvalidOperationException("Dedicated download exited before the parent took ownership of its retained manager threads.");
                // LF's three NetManager loops intentionally live for the entire
                // product process. The verified dedicated child retains its
                // settings/platform guards until this explicit process boundary.
                process.Kill(true);
                if (!process.WaitForExit(Remaining()) || !Task.WaitAll(drains, Remaining()) || run.ElapsedMilliseconds >= 60000)
                    throw new TimeoutException("Owned download termination/output exceeded the same60-second deadline.");
                if (process.ExitCode != -1) throw new InvalidOperationException("Owned Windows download termination did not return the expected -1: " + process.ExitCode);
                string errors = stderr.GetAwaiter().GetResult();
                if (errors.Length != 0) throw new InvalidOperationException("Dedicated download reported stderr despite its receipt: " + errors);
                Console.Write(stdout.GetAwaiter().GetResult());
                outputPrinted = true;
                Console.WriteLine("PASS native modpack download lifetime: complete UX/file/network/listener evidence; exactly three original foreground NetManager loops retained until expected parent-owned termination (-1), not a natural exit0.");
                return 0;
            }
            if (!process.WaitForExit(Remaining()) || !Task.WaitAll(drains, Remaining()) || run.ElapsedMilliseconds >= 60000)
                throw new TimeoutException("Native facade host/output exceeded the same60-second deadline.");
            Console.Write(stdout.GetAwaiter().GetResult()); Console.Error.Write(stderr.GetAwaiter().GetResult());
            outputPrinted = true;
            return process.ExitCode;
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            List<Exception> cleanupErrors = new();
            void Cleanup(string phase, Action action)
            {
                try { action(); }
                catch (Exception failure) { cleanupErrors.Add(new InvalidOperationException("Native child cleanup: " + phase, failure)); }
            }
            bool canDelete = process is null;
            if (process is not null)
            {
                Stopwatch cleanup = Stopwatch.StartNew();
                int Remaining() => Math.Max(0, 5000 - checked((int)cleanup.ElapsedMilliseconds));
                bool stopped = false, settled = false;
                Task[] drains = new Task?[] { stdout, stderr }.OfType<Task>().ToArray();
                void ObserveDrains(int milliseconds)
                {
                    try { Task.WaitAll(drains, milliseconds); }
                    catch (AggregateException) { /* Record each actual read fault below after both readers close. */ }
                    catch (Exception failure) { cleanupErrors.Add(new InvalidOperationException("Native child output observation failed.", failure)); }
                }
                try
                {
                    Cleanup("terminate only owned process", () => { if (!process.HasExited) process.Kill(true); });
                    // A kill/HasExited race must not skip a real exit observation.
                    Cleanup("observe owned process exit", () => stopped = process.WaitForExit(Remaining()));
                    // Give normal EOF a small part of the SAME cleanup budget;
                    // descendants holding a pipe must not consume all five seconds.
                    if (stopped) ObserveDrains(Math.Min(250, Remaining()));
                    try { Cleanup("close stdout reader", () => process.StandardOutput.Dispose()); }
                    finally { Cleanup("close stderr reader", () => process.StandardError.Dispose()); }
                    ObserveDrains(Remaining());
                    settled = stdout is not null && stderr is not null && drains.All(drain => drain.IsCompleted);
                    foreach (Task drain in drains)
                    {
                        if (drain.IsFaulted) cleanupErrors.Add(new InvalidOperationException("Native child output read failed.", drain.Exception));
                        else if (drain.IsCanceled) cleanupErrors.Add(new TaskCanceledException("Native child output read was canceled.", null, default));
                    }
                    if (!stopped) cleanupErrors.Add(new InvalidOperationException("Owned Native child exit was not established; retain its GUID fixture and live guards."));
                    if (!settled) cleanupErrors.Add(new TimeoutException("Owned Native child stdout/stderr did not both settle; retain its GUID fixture."));
                    if (cleanup.ElapsedMilliseconds >= 5000) cleanupErrors.Add(new TimeoutException("Native child cleanup exceeded the same five-second deadline."));
                    if (!outputPrinted)
                    {
                        Cleanup("write failure metadata", () => Console.Error.WriteLine("Native child failure evidence: groups=" + (dedicatedDownload ? "modpack-download-child" : string.Join(',', ReadGroups(args))) +
                            "; PID=" + process.Id + "; stopped=" + stopped + "; settled=" + settled + "; stdout=" + stdout?.Status + "; stderr=" + stderr?.Status));
                        Cleanup("write complete stdout tail", () => PrintTail(stdout, "stdout"));
                        Cleanup("write complete stderr tail", () => PrintTail(stderr, "stderr"));
                    }
                }
                finally { Cleanup("dispose owned process handle", process.Dispose); }
                // Failed/canceled reads and cleanup faults are not acceptance.
                // Unresolved ownership must never authorize deleting the context.
                canDelete = stopped && settled && cleanupErrors.Count == 0;
            }
            if (canDelete)
                Cleanup("validate and remove stopped fixture", () => { ValidateRoot(root); if (Directory.Exists(root)) Directory.Delete(root, true); });
            if (!canDelete || cleanupErrors.Count > 0)
                Cleanup("report retained fixture", () => Console.Error.WriteLine("Native child fixture retained after incomplete/failed cleanup: " + root));
            if (cleanupErrors.Count > 0)
            {
                if (primaryFailure is null) throw new AggregateException("Native child cleanup failed; no successful acceptance is reported.", cleanupErrors);
                // The original exception is already rethrowing with its stack.
                // Preserve it even when cleanup or these diagnostics fail.
                try { Console.Error.WriteLine("Native cleanup faults after primary " + primaryFailure.GetType().Name + ": " +
                    string.Join(" | ", cleanupErrors.Select(failure => failure.ToString()))); }
                catch (Exception diagnosticFailure) { cleanupErrors.Add(diagnosticFailure); }
            }
        }
        static void ObserveFault(Task task) => _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        static void PrintTail(Task<string>? task, string channel)
        {
            if (task?.IsCompletedSuccessfully != true) return;
            string text = task.GetAwaiter().GetResult();
            bool truncated = text.Length > 65536;
            int first = Math.Max(0, text.Length - 65536);
            if (first > 0 && char.IsLowSurrogate(text[first])) first++;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text.AsSpan(first).ToString());
            int start = Math.Max(0, bytes.Length - 65536);
            if (start > 0)
            {
                truncated = true;
                while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
            }
            string tail = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(start));
            Console.Error.WriteLine("Native child " + channel + " tail:\n" + (truncated ? "<truncated to final65536 UTF-8 bytes>\n" : "") + tail);
        }
    }

    private static void CopyOutput(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected linked output file.");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected linked output directory.");
            CopyOutput(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    internal static void ValidateRoot(string root)
    {
        string absolute = Native.Full(root), prefix = Native.Full(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        Native.Check(absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(absolute), "^PclNativeFacade-[a-f0-9]{32}$"), "Unsafe fixture root.");
    }
}
