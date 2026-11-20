using System.Collections.ObjectModel;
using MeloongCore;
using PCLCS.Loaders;
using PCLCS.Validation;

// Assertions come from LF ModValidate.vb / ModLoader.vb / LoaderFileOperation.vb
// at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983, including Test-ReviewFixes Lock.
// This runs the real production engines and pinned Shared worker. The small typed
// adapters below only supply fields/events and synchronous non-WPF host primitives.
// Windows facade/Dispatcher/Err and instance-lock integration remain separate gates.
internal static class LegacyValidationLoaderTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("legacy validation keeps null acceptance error short circuit and deferred exceptions", ValidationOrdering);
        yield return ("legacy filename validation retains Object parent requirement and real duplicate policy", FileNameParentOption);
        yield return ("legacy folder validation defers real discovery and preserves live ignore and error ordering", FolderDiscovery);
        yield return ("legacy loader chain forwards null restarts changed input and preserves VB conversion failures", LoaderChain);
        yield return ("legacy loader discards abandoned thread completion without completing its replacement twice", AbandonedThread);
        yield return ("legacy file loader cancellation retains real worker until flat payload cleanup and rejects late work", () => FileCancellation(false));
        yield return ("legacy file loader cancellation retains mixed generic nested payload through cleanup", () => FileCancellation(true));
        yield return ("legacy worker adapter reports cancellation before the real worker finishes cleanup", WorkerAdapterCancellation);
        yield return ("legacy loader workload observation retains canceled and superseded thread cleanup", WorkloadObservation);
    }

    private static void WorkloadObservation() {
        using ManualResetEventSlim enteredFirst = new(), enteredSecond = new(), releaseFirst = new(), releaseSecond = new();
        int starts = 0;
        var task = new TestTask<int, int>("owned workload lifetime", _ => {
            bool first = Interlocked.Increment(ref starts) == 1;
            (first ? enteredFirst : enteredSecond).Set();
            if (!(first ? releaseFirst : releaseSecond).Wait(10000)) throw new TimeoutException("Owned workload was not released.");
        }) { CoreInterruptOnCancel = false };
        Thread? firstThread = null, secondThread = null;
        try {
            task.Start(1); firstThread = task.CoreLastRunningThread;
            if (!enteredFirst.Wait(5000)) throw new TimeoutException("First real loader thread did not enter.");
            task.Cancel();
            Equal(State.Canceled, task.State); Equal(true, task.HasActiveWorkload);
            task.Start(2, true); secondThread = task.CoreLastRunningThread;
            if (!enteredSecond.Wait(5000)) throw new TimeoutException("Replacement real loader thread did not enter.");
            releaseSecond.Set();
            if (!secondThread!.Join(5000)) throw new TimeoutException("Replacement loader did not finish.");
            Equal(State.Finished, task.State); Equal(true, task.HasActiveWorkload);
            releaseFirst.Set();
            if (!firstThread!.Join(5000)) throw new TimeoutException("Superseded cleanup did not finish.");
            Equal(false, task.HasActiveWorkload); Equal(1, task.PreviewFinishes);
        } finally {
            releaseFirst.Set(); releaseSecond.Set();
            if (firstThread is not null && !firstThread.Join(5000)) throw new TimeoutException("Owned first loader cleanup failed.");
            if (secondThread is not null && !secondThread.Join(5000)) throw new TimeoutException("Owned replacement loader cleanup failed.");
        }
    }

    private static void ValidationOrdering() {
        int laterCalls = 0;
        LegacyValidationRule later = new LegacyValidateFunc(_ => { laterCalls++; throw new IOException("Unexpected later rule."); });
        Equal("", LegacyValidation.Validate(null, new[] { new LegacyValidateNullable(), later }));
        Equal("输入内容不能为空！", LegacyValidation.Validate(" ", new[] { new LegacyValidateNullOrWhiteSpace(), later }));
        Equal(0, laterCalls);
        bool readTail = false;
        IEnumerable<LegacyValidationRule> StopBeforeTail() {
            yield return new LegacyValidateFunc(_ => "first error");
            readTail = true;
            throw new IOException("Unexpected enumeration tail.");
        }
        Equal("first error", LegacyValidation.Validate("value", StopBeforeTail()));
        Equal(false, readTail);
        IOException expected = new("fixture deferred exception");
        IEnumerable<LegacyValidationRule> FailAfterPass() {
            yield return new LegacyValidateFunc(_ => "");
            throw expected;
        }
        Same(expected, Throws<IOException>(() => LegacyValidation.Validate("value", FailAfterPass())));
        Same(expected, Throws<IOException>(() => LegacyValidation.Validate("value", new[] { new LegacyValidateFunc(_ => throw expected) })));
        Collection<string?> words = ["forbidden"];
        LegacyValidateExcept mutable = new(words, null);
        Equal("", mutable.Validate("FORBIDDEN"));
        Equal("", mutable.ErrorMessage); // LF mutates a null message when a fragment matches.
        words.Add("live");
        mutable.ErrorMessage = "cannot contain %";
        Equal("cannot contain live", mutable.Validate("LIVE"));
    }

    private static void FileNameParentOption() {
        using Fixture fixture = new();
        string missing = Path.Combine(fixture.Root, "missing");
        LegacyValidateFileName rule = new() { ParentFolder = missing };
        Equal(typeof(object), typeof(LegacyValidateFileName).GetProperty(nameof(rule.RequireParentFolderExists))!.PropertyType);
        foreach (object? optional in new object?[] { null, false, 0, "False" }) {
            rule.RequireParentFolderExists = optional;
            Equal("", rule.Validate("save.bin"));
        }
        foreach (object required in new object[] { true, -1, "True" }) {
            rule.RequireParentFolderExists = required;
            Equal("父文件夹不存在：" + missing, rule.Validate("save.bin"));
        }
        rule.RequireParentFolderExists = "not a Boolean";
        True(rule.Validate("save.bin").StartsWith("错误：", StringComparison.Ordinal), "Invalid Object conversion did not remain a validation error.");
        rule.ParentFolder = fixture.Root;
        Equal("", rule.Validate("save.bin")); // The option is converted only when the parent is missing.
        File.WriteAllText(Path.Combine(fixture.Root, "slot.bin"), "fixture");
        Equal("不可与现有文件重名！", rule.Validate("SLOT.bin"));
        rule.IgnoreCase = false;
        Equal("", rule.Validate("SLOT.bin"));
    }

    private static void FolderDiscovery() {
        using Fixture fixture = new();
        List<string> ignored = new();
        LegacyValidateFolderName rule = new(fixture.Root, true, ignored);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Taken")); // Created after the validator, before enumeration.
        Equal("不可与现有文件夹重名！", rule.Validate("taken"));
        ignored.Add("TAKEN");
        Equal("", rule.Validate("taken"));
        ignored.Clear();
        Equal("不可与现有文件夹重名！", rule.Validate("taken"));
        int enumerations = 0;
        IEnumerable<string> BrokenPaths() {
            enumerations++;
            throw new IOException("fixture folder enumeration");
#pragma warning disable CS0162 // A deferred iterator must have a yield without executing the throw eagerly.
            yield break;
#pragma warning restore CS0162
        }
        LegacyValidateFolderName broken = new(fixture.Root, true, null, BrokenPaths());
        Equal("文件夹名不能以空格开头！", broken.Validate(" bad"));
        Equal(0, enumerations);
        Equal("错误：fixture folder enumeration", broken.Validate("candidate"));
        Equal(1, enumerations);
    }

    private static void LoaderChain() {
        int firstRuns = 0, secondRuns = 0;
        TestTask<string?, string?> first = new("first", task => { Interlocked.Increment(ref firstRuns); task.CoreOutput = task.CoreInput is null ? null : task.CoreInput + "-a"; });
        TestTask<string?, string?> second = new("second", task => { Interlocked.Increment(ref secondRuns); task.CoreOutput = task.CoreInput is null ? "null payload" : task.CoreInput + "-b"; });
        TestCombo<string?> combo = new("chain", first, second);
        combo.Start();
        Finish(combo, State.Finished); Join(first.CoreLastRunningThread); Join(second.CoreLastRunningThread);
        Equal("null payload", second.CoreOutput);
        Same(combo, first.RealParent); Equal<Root?>(null, combo.RealParent);
        combo.Start("changed");
        Finish(combo, State.Finished); Join(first.CoreLastRunningThread); Join(second.CoreLastRunningThread);
        Equal("changed-a-b", second.CoreOutput);
        combo.Start("changed");
        Finish(combo, State.Finished);
        Equal(2, firstRuns); Equal(2, secondRuns);
        TestTask<int, int> number = new("numeric payload", task => task.CoreOutput = task.CoreInput + 1);
        number.Start("41"); Finish(number, State.Finished); Join(number.CoreLastRunningThread);
        Equal(42, number.CoreOutput); // Production VB generic conversion, not a fixture cast.
        int delegateCalls = 0;
        TestTask<int, int> incompatible = new("stored delegate", _ => { });
        incompatible.CoreInputDelegate = () => { delegateCalls++; return 7; };
        Throws<InvalidCastException>(() => incompatible.Engine.StartGetInput());
        int input = 0;
        Equal(true, incompatible.Engine.ShouldStart(ref input));
        Equal(0, input); Equal(0, delegateCalls); Equal(State.Failed, incompatible.State);
        True(incompatible.Error is InvalidCastException, "The stored Func<object> was adapted instead of preserving LF's delegate cast.");
    }

    private static void AbandonedThread() {
        using ManualResetEventSlim entered = new(), release = new();
        int oldWasCanceled = 0;
        TestTask<int, int> task = new("replacement identity", current => {
            if (current.CoreInput != 1) return;
            entered.Set();
            // A native copy can keep running after Thread.Interrupt; no managed wait here.
            while (!release.IsSet) Thread.SpinWait(10000);
            if (current.Engine.IsCanceled) Interlocked.Exchange(ref oldWasCanceled, 1);
        });
        task.Start(1);
        Thread? abandoned = task.CoreLastRunningThread;
        try {
            True(entered.Wait(5000), "Original payload never entered.");
            task.Start(2);
            Finish(task, State.Finished); Join(task.CoreLastRunningThread);
            Equal(1, task.PreviewFinishes);
        } finally {
            release.Set(); Join(abandoned);
        }
        Equal(1, oldWasCanceled); Equal(1, task.PreviewFinishes); Equal(State.Finished, task.State);
    }

    private static void FileCancellation(bool nested) {
        using Fixture fixture = new();
        using ManualResetEventSlim entered = new(), release = new(), cleaned = new(), stopped = new();
        int executions = 0;
        Action<CancellationToken> payload = token => {
            Interlocked.Increment(ref executions); entered.Set();
            try { release.Wait(); token.ThrowIfCancellationRequested(); }
            finally { File.WriteAllText(Path.Combine(fixture.Root, "cleanup.txt"), "complete"); cleaned.Set(); }
        };
        Root leaf;
        TestCombo<string> combo;
        if (nested) {
            TestTask<int, int> typed = new("mixed leaf", task => payload(task.CreateCancellationToken()));
            leaf = typed; combo = new("outer", new TestCombo<int>("inner", typed));
        } else {
            TestTask<string, string> typed = new("flat leaf", task => payload(task.CreateCancellationToken()));
            leaf = typed; combo = new("outer", typed);
        }
        Action workload = LegacyLoaderFileOperation.CreateWorkload<Root, State, UiState>(combo, "input");
        IWorker worker = new RedoableWorker(workload);
        worker.Stopped += () => stopped.Set();
        try {
            worker.Start(); True(entered.Wait(5000), "Real file payload never entered.");
            combo.Cancel();
            Equal(State.Canceled, combo.State);
            True(!stopped.Wait(150), "Displayed cancellation released the real worker before cleanup.");
            True(worker.Running && !cleaned.IsSet, "The workload lifetime ended at the loader's canceled state.");
        } finally {
            release.Set(); True(stopped.Wait(5000), "Canceled file worker never stopped.");
            True(worker.WaitIfRunning(5000), "File worker thread remained active after Stopped.");
        }
        True(cleaned.IsSet && File.Exists(Path.Combine(fixture.Root, "cleanup.txt")), "Worker stopped before its actual payload finally.");
        leaf.Start(IsForceRestart: true); Finish(leaf, State.Canceled);
        Equal(1, executions); // A late queued child must fail at the closed production gate.
        TestTask<string, string> preLeaf = new("pre-canceled", _ => Interlocked.Increment(ref executions));
        TestCombo<string> preCombo = new("pre-canceled combo", preLeaf);
        Action preWorkload = LegacyLoaderFileOperation.CreateWorkload<Root, State, UiState>(preCombo, "input");
        preCombo.Cancel();
        Throws<OperationCanceledException>(preWorkload);
        Equal(1, executions);
    }

    private static void WorkerAdapterCancellation() {
        using ManualResetEventSlim entered = new(), release = new(), cleaned = new(), stopped = new();
        IWorker worker = new RedoableWorker(token => {
            entered.Set();
            try { release.Wait(); token!.Value.ThrowIfCancellationRequested(); }
            finally { cleaned.Set(); }
        });
        TestWorker loader = new(worker);
        worker.Stopped += () => stopped.Set();
        try {
            loader.Start(); True(entered.Wait(5000), "Shared worker payload never entered.");
            loader.Cancel(); Equal(State.Canceled, loader.State);
            True(worker.Running && !cleaned.IsSet && !stopped.Wait(100), "Worker adapter confused cancellation request with payload completion.");
        } finally {
            release.Set(); True(stopped.Wait(5000), "Actual worker did not complete cancellation.");
            True(worker.WaitIfRunning(5000), "Actual worker did not leave its run.");
        }
        True(cleaned.IsSet, "Worker adapter lost actual payload cleanup.");
        Equal(State.Canceled, loader.State); Equal(0, loader.PreviewFinishes);
    }

    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Same(object expected, object? actual) => True(ReferenceEquals(expected, actual), "Object identity changed.");
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Throws<T>(Action run) where T : Exception { try { run(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Finish(Root loader, State state) => True(SpinWait.SpinUntil(() => loader.State == state, 5000), $"Expected {state}: {loader.Name}, got {loader.State}, {loader.Error}");
    private static void Join(Thread? thread) { if (thread is not null) True(thread.Join(5000), "Payload thread never exited."); }

    // These are closed type values and host wiring, never a replacement loader algorithm.
    private enum State { Waiting, Loading, Finished, Failed, Canceled }
    private enum UiState { Run, Stop, Error }
    private static int nextUuid;
    private static readonly LegacyLoaderHost<Root, State, UiState> Host = new() {
        Waiting = State.Waiting, Loading = State.Loading, Finished = State.Finished, Failed = State.Failed, Canceled = State.Canceled,
        UiRun = UiState.Run, UiStop = UiState.Stop, UiError = UiState.Error,
        NewUuid = () => Interlocked.Increment(ref nextUuid), Clock = () => Environment.TickCount64,
        DebugFinishDelay = () => false, RandomInteger = (minimum, _) => minimum,
        RunInUi = work => work(), RunInUiWait = work => work(), RunInThread = work => work(), RefreshExtraDownload = () => { },
        Kind = value => GeneralUtils.IsGenericInstanceOf(value, typeof(TestTask<,>)) ? LegacyLoaderKind.Task :
            GeneralUtils.IsGenericInstanceOf(value, typeof(TestCombo<>)) ? LegacyLoaderKind.Combo : value is TestWorker ? LegacyLoaderKind.Worker : LegacyLoaderKind.Other,
        IsDownloadInput = _ => false
    };
    private abstract class Root : LegacyLoaderBase<Root, State, UiState> {
        private event Action<Root, State, State>? UiChanged, ThreadChanged;
        internal int PreviewFinishes;
        protected Root() : base(LegacyValidationLoaderTests.Host) { }
        protected override void ConnectUiHandler(Action<Root, State, State> handler) => UiChanged += handler;
        protected override void ConnectThreadHandler(Action<Root, State, State> handler) => ThreadChanged += handler;
        protected override void RaiseStateUi(Root loader, State next, State before) => UiChanged?.Invoke(loader, next, before);
        protected override void RaiseStateThread(Root loader, State next, State before) => ThreadChanged?.Invoke(loader, next, before);
        protected override void RaiseLoadingState(UiState next, UiState before) { }
        protected override void RaiseProgress(double next, double before) { }
        protected override void RaisePreviewFinishCore(Root loader) => Interlocked.Increment(ref PreviewFinishes);
        protected override void ApplyFileWorkloadGuard(Func<Action, Action> wrap) { }
    }
    private sealed class TestTask<TIn, TOut> : Root, ILegacyLoaderTaskData<TestTask<TIn, TOut>, TIn, TOut> {
        public override bool? HasActiveWorkload => Engine.HasActiveWorkload;
        internal readonly LegacyLoaderTaskEngine<TestTask<TIn, TOut>, TIn, TOut, Root, State, UiState> Engine;
        internal TestTask(string name, Action<TestTask<TIn, TOut>> work) { Name = name; CoreLoadDelegate = work; Engine = new(this); }
        public ThreadPriority CoreThreadPriority { get; set; } = ThreadPriority.Normal;
        public Action<TestTask<TIn, TOut>>? CoreLoadDelegate { get; set; }
        public Func<object?>? CoreInputDelegate { get; set; }
        public int CoreReloadTimeout { get; set; } = -1;
        public long CoreLastFinishedTime { get; set; }
        public Thread? CoreLastRunningThread { get; set; }
        public bool CoreInterruptOnCancel { get; set; } = true;
        public TIn CoreInput { get; set; } = default!;
        public TOut CoreOutput { get; set; } = default!;
        public override void Start(object? Input = null, bool IsForceRestart = false) => Engine.Start(Input, IsForceRestart);
        public override void Cancel() => Engine.Cancel();
        public override void Failed(Exception Ex) => Engine.Failed(Ex);
        protected override bool ProbeTaskStart(object? input, bool ignoreReloadTimeout) => Engine.ShouldStartForCombo(input, ignoreReloadTimeout);
        protected override object? ReadTaskOutput() => CoreOutput;
        protected override void ApplyFileWorkloadGuard(Func<Action, Action> wrap) => Engine.GuardFileWorkload(wrap);
    }
    private sealed class TestCombo<TIn> : Root, ILegacyLoaderComboData<Root, TIn> {
        private readonly LegacyLoaderComboEngine<TestCombo<TIn>, TIn, Root, State, UiState> engine;
        internal TestCombo(string name, params Root[] children) { engine = new(this); engine.Initialize(name, children); }
        public List<Root> CoreLoaders { get; set; } = new();
        public TIn CoreInput { get; set; } = default!;
        public override double Progress { get => engine.Progress; set => engine.Progress = value; }
        public override void InitParent(Root? Parent) => engine.InitParent(Parent);
        public override void Start(object? Input = null, bool IsForceRestart = false) => engine.Start(Input, IsForceRestart);
        public override void Cancel() => engine.Cancel();
        public override void Failed(Exception Ex) => engine.Failed(Ex);
        protected override void ApplyFileWorkloadGuard(Func<Action, Action> wrap) => engine.GuardFileWorkload(wrap);
    }
    private sealed class TestWorker : Root {
        private readonly LegacyLoaderWorkerEngine<Root, State, UiState> engine;
        internal TestWorker(IWorker worker) {
            engine = new(this, () => worker); engine.BindInitialProgress(worker);
            worker.Succeeded += engine.Succeeded; worker.Canceled += Cancel; worker.Failed += Failed;
        }
        public override void Start(object? Input = null, bool IsForceRestart = false) => engine.Start(Input, IsForceRestart);
        public override void Cancel() => engine.Cancel();
        public override void Failed(Exception Ex) => engine.Failed(Ex);
    }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclLegacyValidationLoader-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() {
            string full = Path.GetFullPath(Root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal) || !Path.GetFileName(full).StartsWith("PclLegacyValidationLoader-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            Directory.Delete(full, true);
        }
    }
}
