using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
/// <summary>
/// Owned Windows process group backed by an explicit Job Object without <c>KILL_ON_JOB_CLOSE</c>.
/// Closing the host retains running game/wrapper processes for conservative crash recovery, while explicit
/// launch/cancellation/teardown observes and terminates the entire process tree atomically.
/// </summary>
internal sealed class MmcSessionProcessTree : IDisposable {
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const int JobObjectInfoClassBasicProcessIdList = 3;
    private const int JobObjectInfoClassExtendedLimitInformation = 9;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private static readonly nint ProcThreadAttributeHandleList = 0x00020002;
    private static readonly nint ProcThreadAttributeJobList = 0x0002000D;

    private readonly SafeWaitHandle jobHandle;
    private StreamReader? stdoutReader;
    private StreamReader? stderrReader;
    private Task pipeDrainTask = Task.CompletedTask;
    private int pipesStarted;
    private int disposed;

    private MmcSessionProcessTree(SafeWaitHandle jobHandle, Process entryProcess, long entryStartTimeUtcTicks,
        StreamReader? stdoutReader, StreamReader? stderrReader) {
        this.jobHandle = jobHandle;
        EntryProcess = entryProcess;
        EntryStartTimeUtcTicks = entryStartTimeUtcTicks;
        this.stdoutReader = stdoutReader;
        this.stderrReader = stderrReader;
    }

    public Process EntryProcess { get; }
    public long EntryStartTimeUtcTicks { get; }
    public Task PipeDrainTask => Volatile.Read(ref pipeDrainTask);

    /// <summary>
    /// Creates the entry process suspended inside a freshly configured Job Object, validates its kernel image
    /// before a single user instruction executes, and resumes it.
    /// </summary>
    public static MmcSessionProcessTree Start(string executablePath, ProcessStartInfo startInfo, bool redirectPipes,
        Action<Process>? validateSuspendedProcess = null) {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MmcSessionProcessTree requires Windows.");

        SafeWaitHandle? jobHandle = null;
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, nulRead = null;
        nint attrList = 0, jobBuf = 0, handleListBuf = 0, envBuf = 0;
        nint hProcess = 0, hThread = 0;
        Process? entryProcess = null;
        bool ownershipTransferred = false;

        try {
            jobHandle = CreateConfiguredJob();
            if (redirectPipes) {
                CreateInheritablePipe(out outRead, out outWrite);
                CreateInheritablePipe(out errRead, out errWrite);
                nulRead = OpenInheritableNulRead();
            }

            int attrCount = redirectPipes ? 2 : 1;
            nint attrSize = 0;
            InitializeProcThreadAttributeList(0, attrCount, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrList, attrCount, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化受管进程属性列表。");

            jobBuf = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(jobBuf, jobHandle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeJobList, jobBuf, (nint)nint.Size, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将受管入口绑定到作业对象。");

            if (redirectPipes) {
                handleListBuf = Marshal.AllocHGlobal(nint.Size * 3);
                Marshal.WriteIntPtr(handleListBuf, 0, nulRead!.DangerousGetHandle());
                Marshal.WriteIntPtr(handleListBuf, nint.Size, outWrite!.DangerousGetHandle());
                Marshal.WriteIntPtr(handleListBuf, nint.Size * 2, errWrite!.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handleListBuf, (nint)(nint.Size * 3), 0, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置受管进程继承句柄列表。");
            }

            string commandLine = BuildCommandLine(executablePath, startInfo.ArgumentList);
            envBuf = AllocateEnvironmentBlock(startInfo.Environment);

            var si = new STARTUPINFOEXW {
                StartupInfo = new STARTUPINFOW {
                    cb = Marshal.SizeOf<STARTUPINFOEXW>(),
                    dwFlags = redirectPipes ? StartfUseStdHandles : 0,
                    hStdInput = redirectPipes ? nulRead!.DangerousGetHandle() : 0,
                    hStdOutput = redirectPipes ? outWrite!.DangerousGetHandle() : 0,
                    hStdError = redirectPipes ? errWrite!.DangerousGetHandle() : 0
                },
                lpAttributeList = attrList
            };

            uint flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (startInfo.CreateNoWindow) flags |= CreateNoWindow;

            if (!CreateProcessW(executablePath, commandLine, 0, 0, redirectPipes, flags, envBuf,
                    startInfo.WorkingDirectory, ref si, out PROCESS_INFORMATION pi)) {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建受管进程树入口。");
            }
            hProcess = pi.hProcess;
            hThread = pi.hThread;

            // Close child pipe write ends in parent immediately so pipe EOF coincides with child tree exit.
            outWrite?.Dispose(); outWrite = null;
            errWrite?.Dispose(); errWrite = null;
            nulRead?.Dispose(); nulRead = null;

            entryProcess = Process.GetProcessById(pi.dwProcessId);
            validateSuspendedProcess?.Invoke(entryProcess);
            long entryStartTicks = entryProcess.StartTime.ToUniversalTime().Ticks;

            if (ResumeThread(hThread) == unchecked((uint)-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复已验证的受管进程入口线程。");

            StreamReader? stdoutReader = null, stderrReader = null;
            if (redirectPipes) {
                stdoutReader = new StreamReader(
                    new FileStream(outRead!, FileAccess.Read, 4096, isAsync: false),
                    startInfo.StandardOutputEncoding ?? Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false);
                stderrReader = new StreamReader(
                    new FileStream(errRead!, FileAccess.Read, 4096, isAsync: false),
                    startInfo.StandardErrorEncoding ?? Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false);
                outRead = null; errRead = null;
            }

            var tree = new MmcSessionProcessTree(jobHandle, entryProcess, entryStartTicks, stdoutReader, stderrReader);
            jobHandle = null; entryProcess = null;
            ownershipTransferred = true;
            return tree;
        } finally {
            if (!ownershipTransferred) {
                if (jobHandle is not null && !jobHandle.IsInvalid) {
                    try { TerminateJobObject(jobHandle, 1); } catch { }
                }
                entryProcess?.Dispose();
                jobHandle?.Dispose();
            }
            if (hThread != 0) CloseHandle(hThread);
            if (hProcess != 0) CloseHandle(hProcess);
            outWrite?.Dispose(); errWrite?.Dispose(); nulRead?.Dispose();
            outRead?.Dispose(); errRead?.Dispose();
            if (attrList != 0) {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (jobBuf != 0) Marshal.FreeHGlobal(jobBuf);
            if (handleListBuf != 0) Marshal.FreeHGlobal(handleListBuf);
            if (envBuf != 0) Marshal.FreeHGlobal(envBuf);
        }
    }

    /// <summary>Returns the PIDs of processes currently active inside this owned Job Object.</summary>
    public int[] ObserveProcessIds() {
        if (Volatile.Read(ref disposed) != 0 || jobHandle.IsInvalid || jobHandle.IsClosed) return [];
        for (int capacity = 64; capacity <= 4096; capacity *= 2) {
            int size = 8 + capacity * nint.Size;
            nint buffer = Marshal.AllocHGlobal(size);
            try {
                if (QueryInformationJobObject(jobHandle, JobObjectInfoClassBasicProcessIdList, buffer, (uint)size, out _)) {
                    int count = Marshal.ReadInt32(buffer, 4);
                    int[] result = new int[count];
                    for (int i = 0; i < count; i++) {
                        nint rawPid = Marshal.ReadIntPtr(buffer, 8 + i * nint.Size);
                        result[i] = unchecked((int)rawPid.ToInt64());
                    }
                    return result;
                }
                int error = Marshal.GetLastWin32Error();
                if (error is not (122 or 234)) return [];
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    /// <summary>Starts background readers forwarding stdout and stderr lines from the owned pipe handles.</summary>
    public void BeginReadPipes(Action<string?> onOutputLine, Action<string?> onErrorLine) {
        ArgumentNullException.ThrowIfNull(onOutputLine);
        ArgumentNullException.ThrowIfNull(onErrorLine);
        if (Interlocked.CompareExchange(ref pipesStarted, 1, 0) != 0) return;

        Task ReadLoop(StreamReader? reader, Action<string?> callback) {
            if (reader is null) {
                callback(null);
                return Task.CompletedTask;
            }
            return Task.Run(() => {
                try {
                    while (true) {
                        string? line = reader.ReadLine();
                        if (line is null) break;
                        callback(line);
                    }
                } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
                } finally {
                    try { reader.Dispose(); } catch { }
                    try { callback(null); } catch { }
                }
            });
        }

        Task outTask = ReadLoop(stdoutReader, onOutputLine);
        Task errTask = ReadLoop(stderrReader, onErrorLine);
        Volatile.Write(ref pipeDrainTask, Task.WhenAll(outTask, errTask));
    }

    /// <summary>Waits asynchronously until no active process remains inside this owned Job Object.</summary>
    public async Task WaitForGroupEmptyAsync(CancellationToken cancellationToken = default) {
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            int[] pids = ObserveProcessIds();
            if (pids.Length == 0) return;
            var waits = new List<Task>(pids.Length);
            foreach (int pid in pids) {
                if (pid <= 0) continue;
                try {
                    Process p = Process.GetProcessById(pid);
                    waits.Add(WaitAndDisposeAsync(p, cancellationToken));
                } catch (ArgumentException) {
                    // Process already gone before handle could be opened.
                } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) {
                    // Transiently unopenable process in the job; fall back to bounded wait below.
                }
            }
            if (waits.Count > 0) await Task.WhenAny(waits).ConfigureAwait(false);
            else await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Requests immediate termination of the entire Job Object.</summary>
    public void Terminate() {
        if (jobHandle.IsInvalid || jobHandle.IsClosed) return;
        try { TerminateJobObject(jobHandle, 1); } catch (ObjectDisposedException) { }
    }

    /// <summary>Terminates every process in the Job Object and proves the group empty within the grace window.</summary>
    public bool TerminateAndWait(int graceMilliseconds = MmcProcessTreeTermination.GraceMilliseconds) {
        Terminate();
        long until = Environment.TickCount64 + Math.Max(0, graceMilliseconds);
        while (ObserveProcessIds().Length > 0 && Environment.TickCount64 < until) Thread.Sleep(25);
        return ObserveProcessIds().Length == 0 && EntryProcess.HasExited;
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Interlocked.CompareExchange(ref pipesStarted, -1, 0) == 0) {
            try { stdoutReader?.Dispose(); } catch { }
            try { stderrReader?.Dispose(); } catch { }
        }
        stdoutReader = null; stderrReader = null;
        // Intentionally do NOT TerminateJobObject on Dispose: host closing keeps managed game alive for recovery.
        jobHandle.Dispose();
    }

    private static async Task WaitAndDisposeAsync(Process process, CancellationToken cancellationToken) {
        using (process) {
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    private static SafeWaitHandle CreateConfiguredJob() {
        SafeWaitHandle handle = CreateJobObjectW(0, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建受管进程作业对象。");
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        nint infoPtr = Marshal.AllocHGlobal(size);
        try {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitActiveProcess;
            info.BasicLimitInformation.ActiveProcessLimit = 1024;
            Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectInfoClassExtendedLimitInformation, infoPtr, (uint)size)) {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(err, "无法配置受管进程作业限制。");
            }
            return handle;
        } finally {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    private static void CreateInheritablePipe(out SafeFileHandle readEnd, out SafeFileHandle writeEnd) {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out readEnd, out writeEnd, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建受管进程标准流管道。");
        if (!SetHandleInformation(readEnd, HandleFlagInherit, 0)) {
            int err = Marshal.GetLastWin32Error();
            readEnd.Dispose(); writeEnd.Dispose();
            throw new Win32Exception(err, "无法禁止管道读端句柄继承。");
        }
    }

    private static SafeFileHandle OpenInheritableNulRead() {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        SafeFileHandle nul = CreateFileW("NUL", GenericRead, FileShareReadWrite, ref sa, OpenExisting, FileAttributeNormal, 0);
        if (nul.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 NUL 标准输入句柄。");
        return nul;
    }

    private static string BuildCommandLine(string executablePath, IList<string> arguments) {
        StringBuilder sb = new();
        sb.Append('"').Append(executablePath.Trim().Trim('"')).Append('"');
        foreach (string arg in arguments) {
            sb.Append(' ');
            AppendQuotedArgument(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuotedArgument(StringBuilder sb, string argument) {
        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) {
            sb.Append(argument);
            return;
        }
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in argument) {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(c);
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static nint AllocateEnvironmentBlock(IDictionary<string, string?> environment) {
        StringBuilder block = new();
        foreach (var pair in environment
            .Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value is not null && !kv.Key.Contains('=') && !kv.Key.Contains('\0') && !kv.Value.Contains('\0'))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        block.Append('\0');
        byte[] bytes = Encoding.Unicode.GetBytes(block.ToString());
        nint ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES {
        public int nLength;
        public nint lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeWaitHandle hJob, int jobObjectInfoClass, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeWaitHandle hJob, int jobObjectInfoClass, nint lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeWaitHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount, uint dwFlags, ref nint lpSize);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nint attribute, nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? lpApplicationName, string? lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint ResumeThread(nint hThread);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
