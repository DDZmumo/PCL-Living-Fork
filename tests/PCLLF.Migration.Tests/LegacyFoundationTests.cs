using System.Collections;
using System.Collections.Concurrent;
using MeloongCore;
using PCLCS;

// Actual production C024c engines and unchanged pinned Shared. Original LF behavior,
// including partial writes and VB conversions, remains the compatibility contract.
internal static class LegacyFoundationTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("main termination preserves original statement continuation and thread Err state", LegacyMainTerminationTests.Run);
        yield return ("startup singleton retains real mutex exclusion partial rollback and Windows abandonment", StartupMutex);
        yield return ("startup activation retains original captions message order failure and bounded retry", StartupActivation);
        yield return ("foundation preserves actual UUID Task timeout exceptions and process parameters", () => InFixture(Tasks));
        yield return ("foundation search retains in-place partial failure tie order and lazy random enumeration", () => InFixture(Search));
        yield return ("foundation reads real legacy INI encodings and preserves resource bytes without global providers", () => InFixture(Files));
        yield return ("INI preserves Shared file addresses for native paths and Unix literal Windows names", () => InFixture(IniFileAddresses));
        yield return ("INI verified writes preserve bytes and cache on failure and merge concurrent ordinary writes", () => InFixture(IniTransactions));
    }
    private static void StartupActivation() {
        List<string> order = new(); int pauses = 0, searches = 0;
        bool Request(Func<string, IntPtr> lookup, Func<IntPtr, uint, bool>? queue = null) =>
            PCLCS.Startup.LauncherWindowActivation.RequestActivation(lookup,
                queue ?? ((window, message) => { Assert(window == new IntPtr(17) && message == 6402U, "actual original activation protocol"); order.Add("queue"); return true; }),
                _ => order.Add("foreground"), ms => { Assert(ms == 200, "bounded pause interval"); pauses++; });
        Assert(Request(caption => caption == "Plain Craft Launcher　" ? new IntPtr(17) : IntPtr.Zero), "official caption activation");
        Assert(order.SequenceEqual(new[] { "queue", "foreground" }) && pauses == 0, "queue before foreground");
        order.Clear();
        Assert(Request(caption => caption == "PCL Living Fork" ? new IntPtr(17) : IntPtr.Zero), "modern display caption activation");
        order.Clear();
        Assert(Request(caption => pauses == 2 && caption == "LivingPCL　" ? new IntPtr(17) : IntPtr.Zero) && pauses == 2, "delayed LF caption retry");
        order.Clear(); pauses = 0;
        Assert(!Request(_ => { searches++; return IntPtr.Zero; }) && searches == 40 && pauses == 9 && order.Count == 0, "missing window bounds");
        Assert(!Request(_ => new IntPtr(17), (_, _) => false) && order.SequenceEqual(new[] { "foreground" }), "failed post still requests foreground and reports false");
        var expected = new InvalidOperationException("lookup failure");
        try { Request(_ => throw expected); throw new Exception("Expected lookup failure"); }
        catch (InvalidOperationException ex) { Assert(ReferenceEquals(ex, expected), "activation exception identity"); }
    }
    private static void StartupMutex() {
        string primary = "LivingPCL-Mutex-" + Guid.NewGuid().ToString("N"), secondary = primary + "-secondary";
        WithOwnedMutex(primary, () => Assert(PCLCS.Startup.LauncherSessionLease.TryAcquire(new[] { primary }, 0) is null, "existing official owner blocks"));
        using (var lease = PCLCS.Startup.LauncherSessionLease.TryAcquire(new[] { primary, secondary }, 0)) {
            Assert(lease is not null, "free ordered pair acquired");
            bool available = true;
            Thread probe = new(() => { using Mutex gate = new(false, primary); available = gate.WaitOne(0); if (available) gate.ReleaseMutex(); });
            probe.Start(); Assert(probe.Join(3000) && !available, "held lease excludes actual other thread");
        }
        WithOwnedMutex(secondary, () => {
            Assert(PCLCS.Startup.LauncherSessionLease.TryAcquire(new[] { primary, secondary }, 0) is null, "secondary owner rejects whole lease");
            bool available = false;
            Thread probe = new(() => { using Mutex gate = new(false, primary); available = gate.WaitOne(0); if (available) gate.ReleaseMutex(); });
            probe.Start(); Assert(probe.Join(3000) && available, "partial primary acquisition actually rolled back for other thread");
        });
        // Windows thread-abandonment semantics are also exercised by the original
        // product audit; keep the platform case explicit instead of claiming it on Linux.
        if (OperatingSystem.IsWindows()) {
            Mutex? abandoned = null;
            Thread owner = new(() => { abandoned = new Mutex(false, primary); abandoned.WaitOne(); });
            owner.Start(); Assert(owner.Join(3000), "abandoned owner completed");
            try { using var lease = PCLCS.Startup.LauncherSessionLease.TryAcquire(new[] { primary }, 0); Assert(lease is not null, "actual abandoned lease recovered"); }
            finally { abandoned?.Dispose(); }
        }
    }
    private static void WithOwnedMutex(string name, Action sample) {
        using ManualResetEventSlim ready = new(), release = new(); Exception? failure = null;
        Thread owner = new(() => {
            try { using Mutex gate = new(false, name); gate.WaitOne(); try { ready.Set(); release.Wait(); } finally { gate.ReleaseMutex(); } }
            catch (Exception error) { failure = error; ready.Set(); }
        }) { IsBackground = true };
        owner.Start();
        try { Assert(ready.Wait(3000), "mutex owner ready"); if (failure is not null) throw failure; sample(); }
        finally { release.Set(); Assert(owner.Join(3000), "mutex fixture owner exits"); }
        if (failure is not null) throw failure;
    }
    private static void InFixture(Action<string> sample) {
        string root = Path.Combine(Path.GetTempPath(), "LivingPCL-Foundation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string previous = Directory.GetCurrentDirectory();
        try { Directory.SetCurrentDirectory(root); sample(root); }
        finally { Directory.SetCurrentDirectory(previous); Directory.Delete(root, true); }
    }
    private static void IniFileAddresses(string root) {
        List<string> addresses = new() { Path.Combine(root, "native", "address.ini") };
        // No real Windows drive is involved: on Unix these are relative filenames
        // inside the GUID fixture, transformed only by the unchanged Shared helper.
        if (!OperatingSystem.IsWindows()) {
            addresses.Add(@"C:\literal-fixture\address.ini");
            addresses.Add(@"\\?\C:\literal-fixture\extended.ini");
        }
        foreach (string address in addresses) {
            string physical = Path.GetFullPath(PathUtils.ForApi(address));
            if (OperatingSystem.IsWindows()) physical = PathUtils.RemoveExtendedPrefix(physical);
            Assert(physical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "INI address stays inside GUID fixture");
            FileUtils.Write(address, "A:first\r\nA:second\r\nB:one:two\r\n");
            Assert(File.Exists(physical), "Shared wrote the expected physical file");
            var cache = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
            Assert(LegacyBaseOperations.ReadIni(address, "A", "missing", cache) == "second" &&
                LegacyBaseOperations.ReadIni(address, "B", "missing", cache) == "one:two", "INI reads the same Shared address without changing duplicate/first-colon rules");
            LegacyBaseOperations.UpdateIniValue(address, "Saved", _ => "verified", cache);
            Assert(FileUtils.ReadAsString(address).Contains("Saved:verified", StringComparison.Ordinal) &&
                LegacyBaseOperations.ReadIni(address, "A", "missing", cache) == "second", "verified write uses the same physical address and preserves old keys");
            FileUtils.Write(address, "A:external\r\n");
            Assert(LegacyBaseOperations.ReadIni(address, "A", "missing", cache) == "second", "INI cache remains stable until clear");
            LegacyBaseOperations.IniClearCache(address, cache);
            Assert(LegacyBaseOperations.ReadIni(address, "A", "missing", cache) == "external", "cache clear reloads the same Shared address");
        }
    }
    private static void IniTransactions(string root) {
        string ini = Path.Combine(root, "transaction", "Setup.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
        var cache = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows()) {
            const string unc = @"\\fixture.invalid\share\Setup.ini";
            cache[unc] = new();
            LegacyBaseOperations.IniClearCache(@"\\?\UNC\fixture.invalid\share\Setup.ini", cache);
            Assert(!cache.ContainsKey(unc), "canonical UNC cache identity (no network IO)");
        }
        foreach (string key in new[] { "UiModFavorite", LegacyUpdateLogPrompt.Key }) {
            File.WriteAllText(ini, "Keep:原设置\r\n\r\n;保留注释\r\n" + key + ":old\r\n");
            cache.Clear();
            byte[] original = File.ReadAllBytes(ini);
            Assert(LegacyBaseOperations.ReadIni(ini, key, "missing", cache) == "old", "prime old cache");
            if (OperatingSystem.IsWindows()) {
                using (FileStream held = new(ini, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Throws<IOException>(() => LegacyBaseOperations.UpdateIniValue(ini, key, _ => "rejected", cache));
                Assert(File.ReadAllBytes(ini).SequenceEqual(original) && LegacyBaseOperations.ReadIni(ini, key, "missing", cache) == "old", "failed save preserves exact file bytes and cache");
            }
            using ManualResetEventSlim entered = new(), release = new(), otherStarted = new();
            Task transaction = Task.Run(() => LegacyBaseOperations.UpdateIniValue(ini, key, old => {
                Assert(old == "old", "transaction reads actual old value");
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException("INI fixture lock timeout");
                return "saved";
            }, cache));
            Assert(entered.Wait(5000), "transaction reached its read/modify/write boundary");
            string alias = OperatingSystem.IsWindows() ? ini.ToUpperInvariant() : Path.Combine(root, "transaction", "..", "transaction", "Setup.ini");
            Task ordinary = Task.Run(() => { otherStarted.Set(); LegacyBaseOperations.WriteIni(alias, "Other", "parallel", cache); });
            try {
                Assert(otherStarted.Wait(5000), "ordinary writer started");
                Assert(!ordinary.Wait(50), "ordinary writer shares the canonical path lock");
            } finally { release.Set(); }
            Assert(Task.WaitAll(new[] { transaction, ordinary }, 5000), "both INI writers completed");
            LegacyBaseOperations.IniClearCache(ini, cache);
            Assert(LegacyBaseOperations.ReadIni(ini, key, "", cache) == "saved" && LegacyBaseOperations.ReadIni(ini, "Other", "", cache) == "parallel", "transaction and ordinary keys both survived");
            string actual = File.ReadAllText(ini);
            Assert(actual.Contains("Keep:原设置\r\n\r\n;保留注释\r\n", StringComparison.Ordinal), "unrelated keys blank lines and comments survived");
            Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(ini)!, "*.tmp").Any(), "atomic writer cleaned owned temporary files");
        }
    }
    private static void Tasks(string root) {
    int uuid = 1; object? gate = null;
    Assert(LegacyBaseOperations.GetUuid(ref uuid, ref gate) == 2, "first uuid"); object initialGate = gate!;
    Assert(LegacyBaseOperations.GetUuid(ref uuid, ref gate) == 3 && ReferenceEquals(gate, initialGate), "same uuid lock/store");
    uuid = int.MaxValue; Throws<OverflowException>(() => LegacyBaseOperations.GetUuid(ref uuid, ref gate)); Assert(uuid == int.MaxValue, "overflow leaves uuid");
    DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    Assert(LegacyBaseOperations.ToUnixTimestampUtc(epoch.AddTicks(5000000)) == 0 && LegacyBaseOperations.ToUnixTimestampUtc(epoch.AddTicks(15000000)) == 2 && LegacyBaseOperations.ToUnixTimestampUtc(epoch.AddTicks(25000000)) == 2, "original VB banker rounding");
    Assert(LegacyBaseOperations.GetDate(0).ToUniversalTime() == epoch, "timestamp date");
    Assert(LegacyBaseOperations.Val(null) == 0 && LegacyBaseOperations.Val("&") == 0 && LegacyBaseOperations.Val("12.5junk") == 12.5 && LegacyBaseOperations.Val(new object()) == 0, "Val fallback");
    Assert(LegacyBaseOperations.GetResultWithTimeout(Task.FromResult(42), new CancellationTokenSource(), 1000) == 42, "typed task success");
    using (CancellationTokenSource timeout = new()) {
        int canceled = 0; timeout.Token.Register(() => canceled++);
        Throws<TimeoutException>(() => LegacyBaseOperations.GetResultWithTimeout(new TaskCompletionSource<int>().Task, timeout, 10));
        Assert(timeout.IsCancellationRequested && canceled == 1, "timeout cancels source before throw");
    }
    var expected = new InvalidOperationException("original-task-failure");
    try { LegacyBaseOperations.GetResultWithTimeout(Task.FromException<int>(expected), new CancellationTokenSource(), 1000); throw new Exception("expected task failure"); }
    catch (InvalidOperationException ex) { Assert(ReferenceEquals(ex, expected), "unwrapped task failure identity"); }
    if (OperatingSystem.IsLinux()) {
        using (var shellDefault = LegacyBaseOperations.StartProcess("/bin/true")) {
            Assert(shellDefault.WaitForExit(3000) && shellDefault.ExitCode == 0 && shellDefault.StartInfo.UseShellExecute, "actual string StartProcess keeps Framework shell default");
        }
        var supplied = new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"printf fixture\"") { UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = root };
        using var redirected = LegacyBaseOperations.StartProcess(supplied);
        string stdout = redirected.StandardOutput.ReadToEnd();
        Assert(redirected.WaitForExit(3000) && redirected.ExitCode == 0 && stdout == "fixture" && !supplied.UseShellExecute && ReferenceEquals(redirected.StartInfo, supplied), "actual caller PSI/redirect identity preserved");
    }

    }
    private static void Search(string root) {
    var first = new Entry("first", new Source(["world loader"]));
    var second = new Entry("second", new Source(["world loader"]));
    var fuzzy = new Entry("fuzzy", new Source(["word load"]));
    var entries = new List<Entry> { first, second, fuzzy };
    var matches = LegacyBaseSearch.Search<Entry, Source>(entries, "world loader", 1, 0);
    Assert(matches.Count == 3 && ReferenceEquals(matches[0], first) && ReferenceEquals(matches[1], second) && ReferenceEquals(matches[2], fuzzy), "same live entries/exact tie/fuzzy cap");
    Assert(first.AbsoluteRight && second.AbsoluteRight && !fuzzy.AbsoluteRight && first.Similarity > 0, "search live mutation");
    var acrossAliases = new Entry("across", new Source(["world"]), new Source(["loader"]));
    Assert(LegacyBaseSearch.Search<Entry, Source>([acrossAliases], "world loader")[0].AbsoluteRight, "query parts can match separate sources");
    var partial = new Entry("partial", new Source(["world"])) { Similarity = -1 };
    var broken = new Entry("broken", new Source([null!])) { Similarity = -1, AbsoluteRight = true };
    Throws<NullReferenceException>(() => LegacyBaseSearch.Search<Entry, Source>([partial, broken], "world", 5, 0));
    Assert(partial.Similarity > 0 && partial.AbsoluteRight && broken.Similarity == 0 && broken.AbsoluteRight, "partial search mutation before failure unchanged");
    IList<int> numbers = new List<int> { 3, 1, 2 }; var sorted = LegacyBaseOperations.SortByComparison(numbers, (a, b) => a > b);
    Assert(numbers.Count == 0 && sorted.SequenceEqual([3, 2, 1]), "original destructive sort");
    var random = new FixedRandom(); Assert(LegacyBaseOperations.RandomInteger(1, 2, random) == 2, "inclusive random");
    int before = random.Calls; Throws<OverflowException>(() => LegacyBaseOperations.RandomInteger(int.MinValue, int.MaxValue, random)); Assert(random.Calls == before, "range overflow before random draw");
    Assert(LegacyBaseOperations.RandomOne(Array.Empty<string>(), (min, max) => { Assert(min == 0 && max == -1, "empty random bounds"); return 0; }) == null, "empty random returns default");
    int reads = 0; IEnumerable<int> Values() { reads++; yield return 3; yield return 2; yield return 1; }
    var shuffled = LegacyBaseOperations.Shuffle(Values(), (min, max) => min); Assert(reads == 0, "shuffle lazy source");
    Assert(shuffled.SequenceEqual([3, 2, 1]) && reads == 1, "actual lazy shuffle");

    }
    private static void Files(string root) {
    string native = OperatingSystem.IsWindows() ? Path.Combine(root, "fixture") + Path.DirectorySeparatorChar : @"C:\fixture\";
    string ini = native + "data.ini";
    FileUtils.Write(ini, "A:first\r\nA:second\r\n:skip\r\nNoSeparator\r\nB:one:two\r\n");
    var cache = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
    Assert(LegacyBaseOperations.ReadIni(ini, "A", "missing", cache) == "second" && LegacyBaseOperations.ReadIni(ini, "B", "missing", cache) == "one:two", "duplicate ini/first colon");
    Assert(!LegacyBaseOperations.HasIniKey(ini, "", cache), "ini empty key ignored");
    LegacyBaseOperations.WriteIni(ini, "A", "line\r\nnext", cache); Assert(LegacyBaseOperations.ReadIni(ini, "A", "missing", cache) == "linenext", "ini newline and same live cache");
    LegacyBaseOperations.WriteIni(ini, "A", null, cache); Assert(!LegacyBaseOperations.HasIniKey(ini, "A", cache), "ini null deletes");
    FileUtils.Write(ini, "A:external"); Assert(!LegacyBaseOperations.HasIniKey(ini, "A", cache), "ini cache stays until clear");
    LegacyBaseOperations.IniClearCache(ini, cache); Assert(LegacyBaseOperations.ReadIni(ini, "A", "missing", cache) == "external", "ini actual clear/reload");
    int providerBefore = LookupCodePage();
    foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(false), new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode, System.Text.Encoding.BigEndianUnicode }) {
        string bomIni = native + "bom-" + encoding.CodePage + "-" + encoding.GetPreamble().Length + ".ini";
        byte[] bomBytes = encoding.GetPreamble().Concat(encoding.GetBytes("Name:旧数据")).ToArray(); FileUtils.Write(bomIni, bomBytes);
        Assert(LegacyBaseOperations.ReadIni(bomIni, "Name", "missing", cache) == "旧数据", "UTF8/UTF16 BOM/noBOM INI");
    }
    string ansiIni = native + "legacy.ini";
    FileUtils.Write(ansiIni, System.Text.CodePagesEncodingProvider.Instance.GetEncoding("GB18030")!.GetBytes("Name:原设置"));
    Assert(LegacyBaseOperations.ReadIni(ansiIni, "Name", "missing", cache) == "原设置", "legacy GB18030 from actual Shared decoder/provider");
    byte[] oldBytes = FileUtils.ReadAsBytes(ini); LegacyBaseOperations.WriteIni(ini, "bad:key", "ignored", cache);
    Assert(FileUtils.ReadAsBytes(ini).SequenceEqual(oldBytes), "ini invalid key handled without disk write");
    string extracted = native + "resource.bin"; byte[] payload = [0, 1, 255, 10];
    LegacyBaseOperations.ExtractResources(extracted, "fixture", _ => payload); Assert(FileUtils.ReadAsBytes(extracted).SequenceEqual(payload), "real resource bytes");
    LegacyBaseOperations.ExtractResources(extracted, "fixture", _ => "文字"); Assert(FileUtils.ReadAsString(extracted) == "文字", "resource text overwrite");
    byte[] resourceWithBom = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("文字")).ToArray();
    FileUtils.Write(extracted, resourceWithBom); LegacyBaseOperations.ExtractResources(extracted, "fixture", _ => "文字");
    Assert(FileUtils.ReadAsBytes(extracted).SequenceEqual(resourceWithBom), "equal decoded resource preserves original BOM bytes");
    Assert(LookupCodePage() == providerBefore, "local provider query does not change global registrations");
    Throws<NullReferenceException>(() => LegacyBaseOperations.ExtractResources(extracted, "missing", _ => null));
    }
static int LookupCodePage() { try { return System.Text.Encoding.GetEncoding("GB18030").CodePage; } catch (ArgumentException ex) when (ex.ParamName == "name") { return -1; } }
static void Assert(bool value, string label) { if (!value) throw new Exception(label); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
private sealed class Source(string[] aliases, double weight = 1) : ILegacyBaseSearchSource { public string[] Aliases => aliases; public double Weight => weight; }
private sealed class Entry(string item, params Source[] sources) : ILegacyBaseSearchEntry<Source> { public string Item = item; public List<Source> Sources { get; } = [.. sources]; public double Similarity { get; set; } public bool AbsoluteRight { get; set; } }
private sealed class FixedRandom : Random { public int Calls; public override double NextDouble() { Calls++; return 0.5; } }
}
