using System.Diagnostics;
using PCLCS;

internal static class LegacyGameSessionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("legacy server arguments split only an explicit port", ServerAddress);
        if (OperatingSystem.IsWindows())
            yield return ("legacy Java journals and backup guards follow the owned game through a relay and launcher exit", RelaunchJournals);
    }

    private static void ServerAddress() {
        foreach (var (address, expectedHost, expectedPort) in new[] {
            ("mc.example", "mc.example", 25565),
            ("localhost:25570", "localhost", 25570),
            (" 192.0.2.5:1 ", "192.0.2.5", 1),
            ("192.0.2.5", "192.0.2.5", 25565),
            ("2001:db8::1:1234", "2001:db8::1:1234", 25565),
            ("[2001:db8::1]", "2001:db8::1", 25565),
            ("[2001:db8::1]:65535", "2001:db8::1", 65535),
            ("[fe80::1%12]:35565", "fe80::1%12", 35565)
        }) {
            Check(LegacyServerAddress.TryParse(address, out string host, out int port) &&
                host == expectedHost && port == expectedPort, "Wrong legacy server host or port for " + address);
        }
        foreach (string address in new[] {
            "", ":25565", "mc.example:", "mc.example:65536", "mc.example:0", "mc.example:+25565",
            "https://mc.example", "mc.example:25565:25566", "--demo", "[mc.example]:25565",
            "[::1", "[::1]1234", "[::1]:-2", "2001:db8::1:25565", "fe80::1%eth0:25565"
        }) {
            Check(!LegacyServerAddress.TryParse(address, out _, out _), "Invalid legacy server address was accepted: " + address);
        }
    }

    private static void RelaunchJournals() {
        string root = Path.Combine(Path.GetTempPath(), "PCLLF-JavaSession-" + Guid.NewGuid().ToString("N"));
        string unrelatedFolder = Path.Combine(root, "unrelated");
        Directory.CreateDirectory(unrelatedFolder);
        string java = Path.Combine(root, "java.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), java);
        // Named like a JVM so the real Windows process classifier and job
        // membership probe run, without a dependency on an installed JDK.
        const string waitScript = "@echo off\r\n:wait\r\nif exist quit exit /b 0\r\nping -n 2 127.0.0.1 >nul\r\ngoto wait\r\n";
        File.WriteAllText(Path.Combine(root, "game.cmd"), waitScript);
        File.WriteAllText(Path.Combine(unrelatedFolder, "game.cmd"), waitScript);
        File.WriteAllText(Path.Combine(root, "entry.cmd"),
            "@echo off\r\nset /p run=\r\nstart \"\" /b \"" + java + "\" /d /q /c game.cmd\r\n");

        var instance = new FixtureInstance(root);
        var data = new LocalGameDataStore(() => Path.Combine(root, "profile"), EnsureWithin);
        var personal = new PersonalOptionsFlow(data, new object(), () => false, _ => instance, key => key == "lang",
            _ => { }, string.Equals, (_, message) => throw new Exception(message), () => new PersonalOptionsSessionData());
        int quickWarnings = 0;
        var history = new QuickPlayHistory(data, new object(), () => true, EnsureWithin, string.Equals, _ => true,
            (_, message) => throw new Exception(message), () => new RecentWorldData());
        var quick = new QuickPlayFlow(history, data, new object(), () => true, EnsureWithin,
            (_, _) => quickWarnings++, () => new QuickPlaySessionData(), () => new RecentWorldData());
        var backup = new WorldBackupEngine<FixtureInstance>(new WorldBackupHost<FixtureInstance> {
            ProfileAddress = data.Address, RemoveExtendedPrefix = path => path,
            GameFolder = owner => owner.GameFolder, VersionPath = owner => owner.VersionPath,
            Retention = _ => 1, AutoEnabled = _ => true, Watchers = () => [],
            WorldLabel = _ => null, FormatSize = bytes => bytes + " bytes", NotifyChanged = () => { },
            UiBusy = () => false, Dispatch = action => action(), Hint = (_, _) => { },
            Warn = (error, message) => throw new Exception(message, error)
        });
        const string profile = "personal-options.json";
        data.WriteJson(profile, new JObject { ["values"] = new JObject { ["lang"] = "en_us" } });
        string options = Path.Combine(root, "options.txt");
        File.WriteAllText(options, "lang:en_us\r\n");
        PersonalOptionsSessionData settings = personal.Prepare(instance) ?? throw new Exception("No settings session prepared.");
        QuickPlaySessionData recent = quick.Prepare(instance, null, [], false) ?? throw new Exception("No Quick Play session prepared.");
        string settingsJournal = "options-sessions/" + settings.Id + ".json";
        string recentJournal = "quickplay-sessions/" + recent.Id + ".json";
        Directory.CreateDirectory(Path.Combine(root, "saves", "World"));
        File.WriteAllText(Path.Combine(root, "saves", "World", "level.dat"), "");

        var entryStart = StartInfo(root, "entry.cmd");
        entryStart.RedirectStandardInput = true;
        LegacyJavaLaunchIdentity identity = LegacyJavaLaunchIdentity.Prepare(entryStart) ?? throw new Exception("No Windows session identity.");
        using Process entry = Process.Start(entryStart)!;
        identity.Bind(entry);
        using LegacyJavaProcessGroup group = LegacyJavaProcessGroup.TryAttach(entry) ?? throw new Exception("No observation job attached.");
        using Process unrelated = Process.Start(StartInfo(unrelatedFolder, "game.cmd"))!;
        Process? game = null;
        try {
            backup.Track(instance, entry);
            personal.Track(settings, entry);
            quick.Track(recent, entry);
            entry.StandardInput.WriteLine("run");
            entry.StandardInput.Flush();
            Check(entry.WaitForExit(5000) && entry.ExitCode == 0, "Entry JVM failed to relay the launch.");
            game = MmcSessionProcessTree.ObserveNamedJob(identity.JobName).ProcessIds
                .Select(group.OpenJava).Single(process => process is not null);
            Check(!game!.HasExited && group.OpenJava(unrelated.Id) is null, "Job evidence crossed the launch boundary.");

            File.WriteAllText(options, "lang:zh_cn\r\n");
            File.WriteAllText(recent.LogFile!, "[{\"type\":\"singleplayer\",\"id\":\"World\"}]");
            CheckRunning(); // Same launcher, original PID is gone.

            group.Dispose(); // The launcher releases its last job handle.
            Check(!game.HasExited && MmcSessionProcessTree.ObserveNamedJob(identity.JobName).State == MmcNamedJobState.Missing,
                "Closing the observation job did not reproduce a live successor with no job name.");
            Check(LegacyJavaLaunchIdentity.ReadJournal(data.ReadJson(recentJournal)) == GameSessionProcessState.Active,
                "The surviving owned JVM was not identified after the launcher released its job.");
            CheckRunning();

            JObject original = data.ReadJson(recentJournal);
            var ambiguous = (JObject)original.DeepClone();
            ambiguous["legacyJavaLaunch"]!["jobAssigned"] = "invalid";
            data.WriteJson(recentJournal, ambiguous);
            File.WriteAllText(recent.LogFile!, "{");
            quick.RecoverExitedSessions();
            Check(quick.HasRunningSessions() && File.Exists(recent.LogFile) && quickWarnings == 0,
                "Unknown ownership did not defer access to the in-flight Quick Play log.");
            data.WriteJson(recentJournal, original);
            File.WriteAllText(recent.LogFile!, "[{\"type\":\"singleplayer\",\"id\":\"World\"}]");

            File.WriteAllText(Path.Combine(root, "quit"), "");
            Check(game.WaitForExit(5000) && !unrelated.HasExited, "Controlled game did not exit independently of the unrelated process.");
            identity.WatcherCompleted = true;
            backup.OnProcessStopped(instance, entry, normalExit: true);
            Check(!backup.HasActiveGame(root) &&
                Directory.GetFiles(backup.Store(root), "world.zip", SearchOption.AllDirectories).Length == 1 &&
                Directory.GetFiles(data.Address("world-backup-sessions"), "*.json").Length == 0,
                "The normal game exit did not back up the world and close the original journal.");
            personal.RecoverExitedSessions();
            quick.RecoverExitedSessions();
            Check(!personal.HasActiveSession(root) && !quick.HasRunningSessions() && !File.Exists(data.Address(settingsJournal)) &&
                !File.Exists(data.Address(recentJournal)) && !File.Exists(recent.LogFile), "Exited session was not cleaned while unrelated Java stayed live.");
            Check((string?)data.ReadJson(profile)["values"]?["lang"] == "zh_cn" &&
                history.ReadEntries().Single().Identifier == "World", "Recovery did not import the final game data.");
        } finally {
            File.WriteAllText(Path.Combine(root, "quit"), "");
            File.WriteAllText(Path.Combine(unrelatedFolder, "quit"), "");
            entry.StandardInput.Close();
            entry.WaitForExit(5000);
            game?.WaitForExit(5000);
            game?.Dispose();
            unrelated.WaitForExit(5000);
        }
        EnsureWithin(Path.GetTempPath(), root);
        Directory.Delete(Path.GetFullPath(root), recursive: true);

        ProcessStartInfo StartInfo(string workingDirectory, string script) => new(java, "/d /q /c " + script) {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        void CheckRunning() {
            personal.RecoverExitedSessions();
            quick.RecoverExitedSessions();
            Check(backup.HasActiveGame(root, entry) && personal.HasActiveSession(root) && personal.Prepare(instance) is null && quick.HasRunningSessions() &&
                File.Exists(data.Address(settingsJournal)) && File.Exists(data.Address(recentJournal)) && File.Exists(recent.LogFile),
                "Recovery finalized an active successor session or allowed personal settings to be prepared again.");
            Check((string?)data.ReadJson(profile)["values"]?["lang"] == "en_us", "Recovery imported settings while Java was still running.");
        }
    }

    private static void EnsureWithin(string root, string path) {
        string relative = Path.GetRelativePath(root, path);
        Check(!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "Test accessed a path outside its fixture.");
    }

    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    private sealed class FixtureInstance(string root) : IPersonalOptionsInstance, IQuickPlayInstance {
        public string VersionPath => root;
        public string GameFolder => root;
        public bool SyncEnabled => true;
        public bool VersionValid => true;
        public int VanillaMajor => 20;
        public bool IsSnapshot => false;
        public DateTime ReleaseTime => new(2023, 6, 7);
        public bool HasSavedIsolation => true;
        public bool IsolationEnabled => true;
    }
}
