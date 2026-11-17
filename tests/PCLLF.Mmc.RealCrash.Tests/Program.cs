using System.IO;

// Original LF host acceptance fixture; repository LICENCE applies.
if (!OperatingSystem.IsWindows()) {
    Console.Error.WriteLine("This acceptance requires actual Windows file sharing and process termination.");
    return 2;
}
try {
    if (args is ["--child", var root, var rootNonce, var scenario, var scenarioNonce])
        return MmcRealCrashChecks.RunChild(root, Guid.ParseExact(rootNonce, "N"), scenario, Guid.ParseExact(scenarioNonce, "N"));
    if (args is ["--run"])
        return MmcRealCrashChecks.Run(null);
    if (args is ["--run", "--case", var selected] && selected is "prepared" or "committed" or "foreign")
        return MmcRealCrashChecks.Run(selected);
    Console.Error.WriteLine("Use the built Windows apphost: PCLLF.Mmc.RealCrash.Tests.exe --run [--case prepared|committed|foreign]. Evidence and all GUID fixtures are retained.");
    return 2;
} catch (Exception error) {
    Console.Error.WriteLine("FAIL real process-crash acceptance: " + error);
    return 1;
}
