using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>
/// Builds the environment block of a managed MMC process. This is the single production boundary for
/// package environment mapping, so the overlay order is testable without constructing a launch plan.
/// </summary>
public static class MmcRuntimeEnvironmentOverlay {
    /// <summary>
    /// Applies the ordered package mapping onto the inherited base environment.
    /// Order: the launcher removes the execution-input channels it never observes, asserts its own
    /// per-session temporary directory, overlays the package entries in source order, and finally
    /// re-asserts that temporary directory so no package entry can displace the launcher's boundary.
    /// An empty mapping therefore leaves the launcher-owned behavior byte-for-byte unchanged.
    /// Values are literal; launch-variable macro expansion is a different channel and is never applied.
    /// </summary>
    public static void Apply(IDictionary<string, string?> environment, ImmutableArray<MmcEnvironmentVariable> mapping, string temporaryDirectory) {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(temporaryDirectory);
        if (mapping.IsDefault) throw new MmcRuntimeLaunchException("MMC_RUNTIME_ENV_SETTINGS", "A package environment mapping must be an explicit list.");
        // These would inject unobserved JVM/agent options into the managed process.
        foreach (string key in ExecutionInputChannels) environment.Remove(key);
        SetLauncherTemporaryDirectory(environment, temporaryDirectory);
        foreach (var entry in mapping) {
            if (entry.Name is null || entry.Value is null || entry.Name.Length == 0 || entry.Name.Contains('=') || entry.Name.Any(char.IsControl) || entry.Value.Contains('\0'))
                throw new MmcRuntimeLaunchException("MMC_RUNTIME_ENV", "A package environment entry is not a valid literal name/value pair.");
            if (MmcEnvironmentPolicy.IsReserved(entry.Name)) throw new MmcRuntimeLaunchException("MMC_RUNTIME_ENV_RESERVED",
                "A package environment entry redeclares a launcher-managed variable: " + entry.Name);
            environment[entry.Name] = entry.Value;
        }
        // The launcher-owned values win over any case-variant collision a package entry attempted.
        SetLauncherTemporaryDirectory(environment, temporaryDirectory);
    }

    private static readonly string[] ExecutionInputChannels = ["JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH"];

    private static void SetLauncherTemporaryDirectory(IDictionary<string, string?> environment, string temporaryDirectory) {
        environment["TEMP"] = temporaryDirectory; environment["TMP"] = temporaryDirectory;
    }
}
