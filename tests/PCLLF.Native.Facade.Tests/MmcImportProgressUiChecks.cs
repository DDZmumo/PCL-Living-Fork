using System.Collections;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static partial class MmcImportRecoveryUiChecks {
    // Actual Main producer and offscreen native task controls; no desktop interaction.
    internal static void RunProgress(FixtureEnvironment environment) {
        using var scope = new Scope(environment, "mmc-import-progress-ui");
        scope.ImportStageProgress(); scope.BackendFailureStageCard(); scope.FailedArchiveStageCard(); scope.FreshImportAfterFailure();
        scope.VerifyLastProgressFinished();
        scope.Finish();
        Console.WriteLine("PASS mmc-import-progress-ui: real Main tiny cache-only import advances measured archive/extraction/payload phases; seven stage rows share one real producer/card; duplicate metadata does not rewind Java; decided cancellation keeps publishing; failed archive/runtime stages remain readable while automatic rollback leaves no erroneous version; fresh import succeeds. No game launch or desktop interaction.");
    }
    internal static void RunProgressCancellation(FixtureEnvironment environment) {
        using var scope = new Scope(environment, "mmc-import-progress-cancel");
        scope.ImportCancellationCard(); scope.PublicationCancellationCard(); scope.OrdinaryCancellationCard(); scope.SameTitleProjectedCancellation(); scope.Finish();
        Console.WriteLine("PASS mmc-import-progress-cancel: actual native cancel button retains extraction/publication cards as Loading with Cancelling stage until real producers drain and automatic rollback finishes; unstarted cancellation creates no producer; ordinary tasks retain their close behavior. No game launch or desktop interaction.");
    }
    internal static void RunCompletionBoundary(FixtureEnvironment environment) {
        using var scope = new Scope(environment, "mmc-import-completion-boundary");
        scope.PublishedCompletionBoundary(); scope.UndecidedCompletionBoundary(); scope.Finish();
        Console.WriteLine("PASS mmc-import-completion-boundary: real Main tiny import is already published at the actual worker PreviewFinish boundary; late cancellation keeps root Loading and then Finished. A genuine undecided worker at the same boundary also rejects cancellation without a cancelled token. No synthetic completed flag, game launch, desktop interaction or timing delay.");
    }

    private sealed record StageView(string Stage, string State, double? Progress, long CompletedBytes, long TotalBytes) {
        public string? Detail { get; init; }
        public int? CompletedFiles { get; init; }
        public int? TotalFiles { get; init; }
    }
    private static StageView[] Stages(ModLoader.LoaderCombo<string> task) {
        object tracker = Native.Property(task.GetType(), "ImportProgress").GetValue(task)!;
        var snapshots = (IEnumerable)Native.Call(tracker.GetType(), "Snapshot", tracker, task.State)!;
        object? Property(object value, string name) => Native.Property(value.GetType(), name).GetValue(value);
        return snapshots.Cast<object>().Select(value => new StageView(Property(value, "Stage")!.ToString()!, Property(value, "State")!.ToString()!,
            (double?)Property(value, "Progress"), (long)Property(value, "CompletedBytes")!, (long)Property(value, "TotalBytes")!) {
                Detail = (string?)Property(value, "Detail"), CompletedFiles = (int?)Property(value, "CompletedFiles"), TotalFiles = (int?)Property(value, "TotalFiles")
            }).ToArray();
    }
    private static StageView Stage(ModLoader.LoaderCombo<string> task, string name) => Stages(task).Single(value => value.Stage == name);
    private static bool ProducerDone(ModLoader.LoaderCombo<string> task) => (bool)Native.Property(task.GetType(), "ProducerCompleted").GetValue(task)!;
    private static MyCard[] Cards(PageSpeedRight right) => ((StackPanel)right.FindName("PanMain")).Children.OfType<MyCard>().ToArray();
    private static Grid Rows(MyCard card) => (Grid)card.Children[3];
    private static void CheckRows(MyCard card) => Native.Check(Rows(card).Children.OfType<StackPanel>().Count(value => value.Tag?.GetType().Name == "MmcImportStage") == 7,
        "Actual MMC native task card does not contain all seven measured/boundary stage rows.");

    private sealed partial class Scope {
        internal void VerifyLastProgressFinished() => Native.Check(Stages(tasks.Last()).All(value => value.State == "Finished"), "Successful fresh import did not complete its real phase observations.");
        private static (PageSpeedLeft Left, PageSpeedRight Right) TaskPages() {
            _ = FileTaskShell.Install();
            var left = new PageSpeedLeft(); var right = new PageSpeedRight(); ModMain.FrmSpeedLeft = left; ModMain.FrmSpeedRight = right;
            return (left, right);
        }
        private ModLoader.LoaderCombo<string> StartProgressImport(string name, PageSpeedLeft left, Action<MmcInstanceBackendProgress>? observed = null,
            Action<ModLoader.LoaderCombo<string>>? onRegister = null, string? sourceArchive = null, Action<MmcTransactionCheckpoint>? transactionObserved = null) {
            var hook = Native.Property(typeof(MmcInstances), "ServicesFactory"); object? old = hook.GetValue(null);
            hook.SetValue(null, new Func<ModpackMmcInstallRequest, MmcImportServices>(_ => new() {
                Metadata = metadata, CacheRoots = cache, SelectName = candidate => {
                    if (name == "name-from-package") Native.Check(candidate == name, "Main did not parse the actual first-line instance.cfg name.");
                    return name;
                },
                RegisterTask = task => { current = task; tasks.Add(task); ModLoader.LoaderTaskbarAdd(task); left.TaskRefresh(task); onRegister?.Invoke(task); },
                Published = _ => { }, DownloadCreated = _ => throw new IOException("Tiny progress fixture unexpectedly requested an actual network transfer."),
                ProgressObserved = observed, TransactionObserved = (phase, _) => transactionObserved?.Invoke(phase)
            }));
            try { return MmcInstances.StartImport(new(sourceArchive ?? archive, game)); }
            finally { hook.SetValue(null, old); }
        }
        private void Returned(ModLoader.LoaderCombo<string> task, ModBase.LoadState expected) {
            Until(() => ProducerDone(task) && task.State is not (ModBase.LoadState.Waiting or ModBase.LoadState.Loading) &&
                Native.Get(task.Loaders.Single(), "LastRunningThread") is not Thread { IsAlive: true }, "Actual progress producer did not drain.");
            Native.Check(task.State == expected, "Actual progress root differs: " + task.State + " expected=" + expected + " error=" + task.Error);
        }

        internal void ImportStageProgress() {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) {
                using var writer = new StreamWriter(zip.CreateEntry("instance.cfg").Open(), new UTF8Encoding(false)); writer.Write("name=name-from-package\n");
            }
            var (left, right) = TaskPages();
            using ManualResetEventSlim staging = new(), stageRelease = new(), publishing = new(), publishRelease = new();
            var samples = new ConcurrentQueue<(string Phase, double Fraction, StageView[] Stages)>();
            MyCard? original = null;
            long readBefore = FileTaskProgress.ReadBytes, writtenBefore = FileTaskProgress.WrittenBytes;
            var task = StartProgressImport("name-from-package", left, value => {
                samples.Enqueue((value.Phase, current!.Progress, Stages(current!)));
                if (value.Phase == "package-staging") { staging.Set(); if (!stageRelease.Wait(10000)) throw new TimeoutException("Stage identity barrier not released."); }
                if (value.Phase == "private-payload-verify") { publishing.Set(); if (!publishRelease.Wait(10000)) throw new TimeoutException("Publication barrier not released."); }
            }, _ => { original = Cards(right).Single(); Native.Check(original.Title == "MMC 整合包安装：original", "Initial task card did not use the real original ZIP name."); });
            try {
                Until(() => staging.IsSet, "Real Main did not reach its package-staging boundary.");
                left.TaskRefresh(task); CheckRows(original!);
                Native.Check(ReferenceEquals(Cards(right).Single(), original) && original!.Title == "MMC 整合包安装：name-from-package" && task.Loaders.Count == 1,
                    "Changing from archive title to instance title created a second/ghost card or extra scheduled producer.");
                Native.Check(Stages(task).Take(3).All(value => value.State == "Finished") && Stage(task, "ExtractPackage").State == "Running" &&
                    Stages(task).Skip(4).All(value => value.State == "Waiting"), "Stage boundary completed future work before it actually ran.");
                task.Name = "MMC 整合包安装：renamed display"; left.TaskRefresh(task); left.TaskRefresh(task);
                Native.Check(Cards(right).Length == 1 && ReferenceEquals(Cards(right).Single(), original) && original.Title == task.Name, "Repeated dynamic names lost stable task identity.");
                stageRelease.Set(); Until(() => publishing.IsSet, "Real Main did not reach private payload verification.");
                left.TaskRefresh(task);
                Native.Check(task.State == ModBase.LoadState.Loading && task.Progress < 1 && Stage(task, "PublishInstance").State == "Running" &&
                    Stages(task).Take(6).All(value => value.State == "Finished"), "Publication was marked finished before its real producer returned.");
                task.Cancel(); left.TaskRefresh(task);
                Native.Check(task.State == ModBase.LoadState.Loading && Stages(task).All(value => value.State != "Cancelling") &&
                    !original!.Children.OfType<MyIconButton>().Single().IsEnabled, "Decided publication accepted cancellation or showed an unaccepted cancelling state.");
            } finally { stageRelease.Set(); publishRelease.Set(); }
            Returned(task, ModBase.LoadState.Finished);
            Native.Check(FileTaskProgress.ReadBytes > readBefore && FileTaskProgress.WrittenBytes > writtenBefore,
                "Real Main import did not contribute its successful disk reads and writes to the existing task I/O counters.");
            var copied = samples.Where(value => value.Phase == "package-extract").ToArray();
            Native.Check(copied.Length >= 2 && copied.All(value => value.Stages.Single(stage => stage.Stage == "ExtractPackage") is { State: "Running", CompletedBytes: > 0, TotalBytes: > 0, Progress: not null }) &&
                copied.Select(value => value.Fraction).SequenceEqual(copied.Select(value => value.Fraction).Order()) && copied.Last().Fraction > .20,
                "Actual ZIP extraction stayed at 15%, lost byte totals or fabricated/regressed progress.");
            Native.Check(samples.Any(value => value.Phase == "metadata-resolution" && value.Stages.Single(stage => stage.Stage == "SelectJava").State == "Running") &&
                samples.Any(value => value.Phase == "private-payload-install") && Stages(task).All(value => value.State == "Finished"),
                "Repeated prepared metadata rewound stage state, payload events were lost or successful producer did not finish its rows.");
            left.TaskRefresh(task); left.TaskRemove(task); ModLoader.LoaderTaskbar.Remove(task);
        }

        internal void BackendFailureStageCard() {
            var (left, right) = TaskPages();
            var task = StartProgressImport("progress-backend-failure", left, value => {
                if (value.Phase == "materializing-runtime") throw new IOException("owned runtime preparation failure");
            });
            Returned(task, ModBase.LoadState.Failed); left.TaskRefresh(task);
            var card = Cards(right).Single(); CheckRows(card);
            Native.Check(Stage(task, "PrepareRuntime").State == "Failed" && Stages(task).Skip(5).All(value => value.State == "NotRun") &&
                Rows(card).Children.OfType<TextBlock>().Any(value => Equals(value.Tag, "mmc-import-error") && value.Text.Contains("owned runtime preparation failure", StringComparison.Ordinal) &&
                    !value.Text.Contains("MMC 导入恢复", StringComparison.Ordinal)) &&
                !Directory.EnumerateDirectories(Path.Combine(game, "versions"), ".pcllf-mmc-import-*").Any(),
                "Backend failure lost its active stage/error or left an unowned-looking version requiring manual recovery.");
            ModLoader.LoaderTaskbar.Remove(task);
            using var presentation = GlassPresentation.Create(right);
            Native.Check(ProjectedTree(presentation.View).OfType<TextBlock>().Any(item => item.Text.Contains("owned runtime preparation failure", StringComparison.Ordinal)),
                "Removing the last failed task from the live pool removed its projected diagnostic.");
            left.TaskRemove(task);
        }

        internal void PublishedCompletionBoundary() {
            var (left, right) = TaskPages(); using ManualResetEventSlim entered = new(), release = new();
            var task = StartProgressImport("completion-boundary-published", left, onRegister: rootTask => {
                rootTask.Loaders.Single().PreviewFinish += _ => {
                    entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("Published PreviewFinish boundary was not released.");
                };
            });
            string finalRoot = Path.Combine(game, "versions", "completion-boundary-published");
            try {
                Until(() => entered.IsSet, "Actual published Main worker did not reach PreviewFinish.");
                var final = MmcInstanceInspector.Read(finalRoot);
                Native.Check(ProducerDone(task) && task.State == ModBase.LoadState.Loading && task.Loaders.Single().State == ModBase.LoadState.Loading &&
                    Native.Get(task.Loaders.Single(), "LastRunningThread") is Thread { IsAlive: true } && final.IsConsistent && final.Definition!.Generation == 1 &&
                    File.ReadAllText(Path.Combine(final.Paths!.GameRoot, "saves", "author-world", "level.dat")) == "original author world",
                    "Completion boundary did not hold the actual native notification after a real successful MMC publication.");
                task.Cancel(); task.Cancel(); left.TaskRefresh(task);
                Native.Check(task.State == ModBase.LoadState.Loading && task.Loaders.Single().State == ModBase.LoadState.Loading &&
                    Stages(task).All(value => value.State != "Cancelling") && Cards(right).Length == 1 && ModLoader.LoaderTaskbar.Contains(task),
                    "Completed decided import was falsely cancelled between its genuine producer return and native Finished notification.");
            } finally { release.Set(); }
            Returned(task, ModBase.LoadState.Finished);
            Native.Check(MmcInstanceInspector.Read(finalRoot).IsConsistent && Stages(task).All(value => value.State == "Finished"),
                "Late completion-boundary cancellation changed the successful published instance or final task result.");
            left.TaskRefresh(task); ModLoader.LoaderTaskbar.Remove(task);
        }

        internal void UndecidedCompletionBoundary() {
            // Exercise the completed-without-decision callback separately from the
            // real published case above. Native Start/ExecuteWorker/PreviewFinish
            // are all genuine; no fence completion/finishing state is assigned.
            using ManualResetEventSlim entered = new(), release = new();
            object fence = Activator.CreateInstance(Native.Type("PCL.MmcOperationCancellationFence"), new object?[] { null })!;
            object progress = Activator.CreateInstance(Native.Type("PCL.MmcImportProgress"), nonPublic: true)!;
            Type workerType = Native.Type("PCL.MmcOperationWorker`2").MakeGenericType(typeof(string), typeof(string));
            var worker = (ModLoader.LoaderTask<string, string>)Activator.CreateInstance(workerType,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                [fence, "owned undecided producer", new Action<ModLoader.LoaderTask<string, string>>(value => value.Output = "genuine producer returned")], null)!;
            Native.Property(worker.GetType(), "InterruptOnCancel").SetValue(worker, false);
            worker.PreviewFinish += _ => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("Undecided PreviewFinish boundary was not released."); };
            Type comboType = Native.Type("PCL.MmcImportOperationCombo`1").MakeGenericType(typeof(string));
            var task = (ModLoader.LoaderCombo<string>)Activator.CreateInstance(comboType,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                [fence, progress, "owned undecided completion boundary", new ModLoader.LoaderBase[] { worker }], null)!;
            tasks.Add(task); task.Start("owned-input");
            try {
                Until(() => entered.IsSet, "Actual undecided worker did not reach PreviewFinish.");
                Native.Check(ProducerDone(task) && task.State == ModBase.LoadState.Loading && worker.State == ModBase.LoadState.Loading &&
                    worker.LastRunningThread is { IsAlive: true } && worker.Output == "genuine producer returned",
                    "Undecided test did not reach the genuine completed-producer / Loading-loader boundary.");
                task.Cancel(); task.Cancel();
                Native.Check(task.State == ModBase.LoadState.Loading && worker.State == ModBase.LoadState.Loading,
                    "Completed undecided callback cancelled a real producer whose token had never been cancelled.");
            } finally { release.Set(); }
            Returned(task, ModBase.LoadState.Finished);
        }

        internal void FailedArchiveStageCard() {
            var (left, right) = TaskPages();
            string wrong = Path.Combine(root, "progress-invalid.zip"); File.WriteAllText(wrong, "owned invalid ZIP input");
            StartProgressImport("progress-invalid", left, sourceArchive: wrong); Returned(current!, ModBase.LoadState.Failed); left.TaskRefresh(current!);
            var card = Cards(right).Single(); CheckRows(card);
            Native.Check(Stage(current!, "ReadArchive").State == "Failed" && Stages(current!).Skip(1).All(value => value.State == "NotRun") &&
                Rows(card).Children.OfType<TextBlock>().Any(value => Equals(value.Tag, "mmc-import-error") && !string.IsNullOrWhiteSpace(value.Text) && Equals(value.ToolTip, "单击复制错误详情")),
                "Invalid archive hid its failed phase, falsely completed later phases or omitted the copyable diagnostic.");
            left.TaskRemove(current!); ModLoader.LoaderTaskbar.Remove(current!);
        }
        internal void FreshImportAfterFailure() {
            var (left, _) = TaskPages(); var task = StartProgressImport("progress-fresh-after-failure", left); Returned(task, ModBase.LoadState.Finished);
            Native.Check(MmcInstanceInspector.Read(Path.Combine(game, "versions", "progress-fresh-after-failure")).IsConsistent, "A fresh import after automatic failure cleanup required a manual retry entry.");
            left.TaskRefresh(task); ModLoader.LoaderTaskbar.Remove(task);
        }

        private void CancelFromCard(PageSpeedLeft left, PageSpeedRight right, ManualResetEventSlim held, ModLoader.LoaderCombo<string> task) {
            left.TaskRefresh(task); var card = Cards(right).Single(); var button = card.Children.OfType<MyIconButton>().Single();
            Native.Call(typeof(MyIconButton), "OnClick", button, button, EventArgs.Empty);
            Until(() => Stages(task).Any(value => value.State == "Cancelling"), "Actual cancel button did not send the operation cancellation.");
            left.TaskRefresh(task); ModLoader.LoaderTaskbarProgressRefresh();
            Native.Check(task.State == ModBase.LoadState.Loading && !ProducerDone(task) && Native.Get(task.Loaders.Single(), "LastRunningThread") is Thread { IsAlive: true } &&
                ModLoader.LoaderTaskbar.Contains(task) && ReferenceEquals(Cards(right).Single(), card) && Rows(card).Children.OfType<TextBlock>().Any(value => value.Text == "取消中") &&
                Rows(card).Children.OfType<StackPanel>().SelectMany(value => value.Children.OfType<TextBlock>()).Any(value => value.Text.StartsWith("正在取消，等待文件操作结束", StringComparison.Ordinal)),
                "Cancel hid a still-writing import/retry or reported it terminal before the held actual producer drained.");
            left.TaskRemove(task); Native.Check(Cards(right).Length == 1, "TaskRemove hid a MMC operation with a live producer.");
            held.Set(); Returned(task, ModBase.LoadState.Canceled); left.TaskRefresh(task); ModLoader.LoaderTaskbarProgressRefresh();
            Native.Check(!ModLoader.LoaderTaskbar.Contains(task), "Drained cancelled MMC import remained an active taskbar operation.");
        }
        internal void ImportCancellationCard() {
            var (left, right) = TaskPages(); using ManualResetEventSlim entered = new(), release = new();
            long stagingRead = FileTaskProgress.ReadBytes, stagingWritten = FileTaskProgress.WrittenBytes;
            var task = StartProgressImport("progress-early-cancel", left, value => {
                if (value.Phase == "package-staging") { stagingRead = FileTaskProgress.ReadBytes; stagingWritten = FileTaskProgress.WrittenBytes; }
                if (value.Phase != "package-extract" || entered.IsSet) return;
                entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("Extract cancellation barrier not released.");
            });
            try {
                Until(() => entered.IsSet, "Real Main did not enter package extraction.");
                Native.Check(FileTaskProgress.ReadBytes > stagingRead && FileTaskProgress.WrittenBytes > stagingWritten,
                    "Held real ZIP extraction did not update the task I/O counters after successful entry reads and writes.");
                CancelFromCard(left, right, release, task);
            }
            finally { release.Set(); }
            Native.Check(!Directory.Exists(Path.Combine(game, "versions", "progress-early-cancel")), "Cancelled extraction published a complete destination.");
            // Genuine scheduler pause: cancel before a producer has been scheduled.
            using var pause = new MmcRootUpdatePause(() => current);
            long unstartedRead = FileTaskProgress.ReadBytes, unstartedWritten = FileTaskProgress.WrittenBytes;
            var unstarted = StartProgressImport("progress-unstarted", left);
            Native.Check(pause.Captured, "Unstarted MMC task did not reach the actual native scheduler pause.");
            unstarted.Cancel(); pause.Release(); pause.AwaitReturn(); Returned(unstarted, ModBase.LoadState.Canceled);
            Native.Check(Native.Get(unstarted.Loaders.Single(), "LastRunningThread") is null, "Unstarted MMC cancellation scheduled a producer.");
            Native.Check(FileTaskProgress.ReadBytes == unstartedRead && FileTaskProgress.WrittenBytes == unstartedWritten,
                "Cancelling the genuinely unstarted import recorded disk I/O without a producer.");
            ModLoader.LoaderTaskbar.Remove(unstarted);
        }
        internal void PublicationCancellationCard() {
            var (left, right) = TaskPages(); using ManualResetEventSlim entered = new(), release = new();
            StartProgressImport("progress-publication-cancel", left, transactionObserved: phase => {
                if (phase == MmcTransactionCheckpoint.Prepared) { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("Publication cancellation barrier not released."); }
            });
            try {
                Until(() => entered.IsSet, "Real import did not reach its Prepared transaction.");
                Native.Check(Stages(current!).Take(6).All(value => value.State == "Finished") && Stage(current!, "PublishInstance").State == "Running",
                    "The real import rewound completed work or prematurely finished publication.");
                CancelFromCard(left, right, release, current!);
            }
            finally { release.Set(); }
            Native.Check(!Directory.Exists(Path.Combine(game, "versions", "progress-publication-cancel")) &&
                !Directory.EnumerateDirectories(Path.Combine(game, "versions"), ".pcllf-mmc-import-*").Any(), "Cancelled publication left a partial version requiring manual recovery.");
        }
        private static IEnumerable<DependencyObject> ProjectedTree(DependencyObject root)
        {
            yield return root;
            foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (DependencyObject node in ProjectedTree(child)) yield return node;
        }

        internal void SameTitleProjectedCancellation()
        {
            var (left, right) = TaskPages();
            var first = Waiting("first distinct step");
            var second = Waiting("second distinct step");
            using var session = GlassPresentation.Create(right);
            var root = ProjectedTree(session.View).OfType<ScrollViewer>().Single();
            var body = (StackPanel)root.Content;
            var cards = body.Children.OfType<StackPanel>().Single();
            Native.Check(cards.Children.Count == 2, "The new task page merged distinct tasks with one title.");
            var cancel = ProjectedTree(cards.Children[1]).OfType<Button>().Single(item => Equals(item.Content, "取消"));
            cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Until(() => first.State == ModBase.LoadState.Canceled, "Cancelling the older of two same-name tasks did not reach its own loader.");
            Native.Check(ModLoader.LoaderTaskbar.Contains(second) && second.State == ModBase.LoadState.Waiting, "Cancelling one task affected the other task.");
            left.TaskRemove(second);
            ModLoader.LoaderTaskbar.Remove(second);

            ModLoader.LoaderCombo<string> Waiting(string step)
            {
                var task = new ModLoader.LoaderCombo<string>("same title", [new ModLoader.LoaderTask<string, int>(step, _ =>
                    throw new IOException("An unstarted test producer must not run."))]);
                tasks.Add(task);
                ModLoader.LoaderTaskbarAdd(task);
                left.TaskRefresh(task);
                return task;
            }
        }

        internal void OrdinaryCancellationCard() {
            var (left, right) = TaskPages();
            var ordinary = new ModLoader.LoaderCombo<string>("ordinary pack task fixture", [new ModLoader.LoaderTask<string, int>("ordinary extraction", _ => throw new IOException("Unstarted ordinary fixture must not execute."))]);
            tasks.Add(ordinary); ModLoader.LoaderTaskbarAdd(ordinary); left.TaskRefresh(ordinary);
            var button = Cards(right).Single().Children.OfType<MyIconButton>().Single();
            Native.Call(typeof(MyIconButton), "OnClick", button, button, EventArgs.Empty);
            Native.Check(!ModLoader.LoaderTaskbar.Contains(ordinary), "MMC task adapter changed the ordinary task's immediate close behavior.");
            Until(() => ordinary.State == ModBase.LoadState.Canceled, "Ordinary unstarted task did not receive its original cancellation.");
        }
    }
}
