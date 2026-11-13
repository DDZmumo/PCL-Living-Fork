using System;
using System.Collections.Generic;
using System.Linq;

namespace PCLCS;

// Original LF length calculation for ProcessStartInfo.ArgumentList. Counts
// UTF-16 units emitted by .NET 10 Windows Process and CRT argument quoting.
// References: dotnet/runtime v10.0.0 Process.Windows.cs / PasteArguments.cs;
// CreateProcessW accepts at most 32767 units including the terminating NUL.
public static class MmcRuntimeWindowsCommandLine {
    public const int MaximumLengthIncludingTerminator = 32767;
    private static long ExecutableHeaderLengthIncludingTerminator(string executable) {
        ArgumentNullException.ThrowIfNull(executable);
        string fileName = executable.Trim();
        return fileName.Length + (fileName.StartsWith('"') && fileName.EndsWith('"') ? 0L : 2L) + 1L;
    }
    public static long LengthIncludingTerminator(string executable, IEnumerable<string> arguments) {
        ArgumentNullException.ThrowIfNull(arguments);
        long length = ExecutableHeaderLengthIncludingTerminator(executable);
        foreach (string argument in arguments) {
            ArgumentNullException.ThrowIfNull(argument);
            length += 1L + argument.Length;
            if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) continue;
            length += 2;
            long slashes = 0;
            foreach (char value in argument) {
                if (value == '\\') { slashes++; continue; }
                if (value == '"') length += slashes + 1;
                slashes = 0;
            }
            length += slashes;
        }
        return length;
    }
    public static long LengthIncludingTerminator(string executable, string rawArguments) {
        ArgumentNullException.ThrowIfNull(rawArguments);
        long length = ExecutableHeaderLengthIncludingTerminator(executable);
        return string.IsNullOrEmpty(rawArguments) ? length : length + 1L + rawArguments.Length;
    }
    public static void Validate(string executable, IEnumerable<string> arguments) =>
        EnsureWithinLimit(LengthIncludingTerminator(executable, arguments));
    public static void ValidateVerbatim(string executable, string rawArguments) =>
        EnsureWithinLimit(LengthIncludingTerminator(executable, rawArguments));
    private static void EnsureWithinLimit(long length) {
        if (length > MaximumLengthIncludingTerminator)
            throw new MmcRuntimeLaunchException("MMC_RUNTIME_COMMAND_LENGTH", "The exact Windows command line exceeds 32767 UTF-16 units including its terminator (actual: " + length + "). Reduce approved launch arguments or classpath inputs.");
    }
}
