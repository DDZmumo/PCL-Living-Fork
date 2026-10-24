param(
    [string]$ExecutablePath,
    [string]$CompilerPath
)
# 只打开当前令牌作角色检查，并注入虚构清理/提权委托；不调整权限、不清理内存、不触发 UAC。
# 不传 ExecutablePath 时只编译独立帮助类，不构建或启动启动器，不读配置及私有构建输入。
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run with powershell -NoProfile (.NET Framework), not pwsh.' }
$fixture = $null
try {
    if ($ExecutablePath) {
        $assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
    } else {
        $fixture = Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-ToolboxMemory-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($fixture) | Out-Null
        $output = Join-Path $fixture 'MemoryChecks.dll'
        $source = Join-Path (Split-Path $PSScriptRoot -Parent) 'Plain Craft Launcher 2/Modules/Base/NativeMemoryOptimizer.vb'
        if (-not $CompilerPath) {
            $sdk = & dotnet --list-sdks | Select-Object -Last 1
            if ($sdk -notmatch '^(\S+)\s+\[(.+)\]$') { throw 'A .NET SDK is required for standalone memory helper checks.' }
            $CompilerPath = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn/bincore/vbc.dll'
        }
        $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
        & dotnet $CompilerPath /nologo /target:library /rootnamespace:PCL /optioninfer+ /optionstrict+ /optionexplicit+ "/sdkpath:$framework" "/out:$output" $source
        if ($LASTEXITCODE -ne 0) { throw "Standalone memory helper compile failed: $LASTEXITCODE" }
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($output))
    }
    Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Security.Principal;

public static class LivingPclMemoryChecks {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object Call(Type type, string method, params object[] args) {
        MethodInfo target = type.GetMethod(method, Flags);
        Check(target != null, "Missing memory test seam: " + method);
        try { return target.Invoke(null, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static Exception Failure(Action operation) {
        try { operation(); }
        catch (Exception error) { return error; }
        throw new Exception("Expected a synthetic failure");
    }
    static long Execute(Type type, bool elevated, Func<long> available, Action optimize, Func<int> runElevated) {
        return (long)Call(type, "Execute", elevated, available, optimize, runElevated);
    }
    static int Helper(Type type, Func<long> available, Action optimize) {
        return (int)Call(type, "ExecuteHelper", available, optimize);
    }
    static Func<long> Samples(params long[] values) {
        Queue<long> samples = new Queue<long>(values);
        return () => samples.Dequeue();
    }
    public static void Run(Assembly assembly) {
        Type native = assembly.GetType("PCL.NativeMemoryOptimizer", true);
        // This is the same identity opening used by AcquireProfilePrivilege, but never enable it.
        // WindowsPrincipal duplicates a primary token; the pre-fix Query|AdjustPrivileges mask fails here.
        using (WindowsIdentity expectedIdentity = WindowsIdentity.GetCurrent())
        using (WindowsIdentity identity = (WindowsIdentity)Call(native, "OpenPrivilegeIdentity")) {
            bool expected = new WindowsPrincipal(expectedIdentity).IsInRole(WindowsBuiltInRole.Administrator);
            Check(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) == expected,
                "The native memory token must allow the real administrator role check");
            Check(identity.User.Equals(expectedIdentity.User), "The memory token identity changed");
        }

        Type operation = assembly.GetType("PCL.MemoryOptimizationOperation", true);
        int optimizations = 0, reads = 0, elevations = 0;
        Func<long> available = () => { reads++; return reads == 1 ? 4096L : 7168L; };
        Action optimize = () => { optimizations++; };
        Func<int> forbiddenElevation = () => { elevations++; throw new Exception("Unexpected elevation"); };
        Check(Execute(operation, true, available, optimize, forbiddenElevation) == 3072L &&
            reads == 2 && optimizations == 1 && elevations == 0, "Elevated execution did not measure one optimization");

        Check(Execute(operation, true, Samples(8192, 4096), () => {}, null) == 0,
            "A concurrent memory decrease must not produce a negative release result");
        Func<long> forbiddenRead = () => { throw new Exception("Unexpected parent memory measurement"); };
        Action forbiddenOptimize = () => { throw new Exception("Unexpected parent optimization"); };
        Check(Execute(operation, false, forbiddenRead, forbiddenOptimize, () => 7) == 7168L,
            "The parent did not convert the helper result from KiB");
        Check(Execute(operation, false, forbiddenRead, forbiddenOptimize, () => 0) == 0,
            "A successful zero-release helper result was rejected");
        Check(Execute(operation, false, forbiddenRead, forbiddenOptimize, () => Int32.MaxValue) == (long)Int32.MaxValue * 1024L,
            "A large helper result overflowed");
        foreach (int exitCode in new[] { -1, unchecked((int)0xC0000005) }) {
            Exception error = Failure(() => Execute(operation, false, forbiddenRead, forbiddenOptimize, () => exitCode));
            Check(error is InvalidOperationException && error.Message.Contains(exitCode.ToString()),
                "A failed or crashed helper was silently treated as completion");
        }

        Win32Exception cancelled = new Win32Exception(1223, "synthetic UAC cancellation");
        Exception cancellation = Failure(() => Execute(operation, false, forbiddenRead, forbiddenOptimize, () => { throw cancelled; }));
        Check(cancellation is OperationCanceledException && Object.ReferenceEquals(cancellation.InnerException, cancelled),
            "UAC cancellation must have a distinct cancellation result");
        Win32Exception denied = new Win32Exception(5, "synthetic elevation denial");
        Check(Object.ReferenceEquals(Failure(() => Execute(operation, false, forbiddenRead, forbiddenOptimize, () => { throw denied; })), denied),
            "A genuine elevation failure was swallowed or treated as cancellation");

        Exception commandError = new InvalidOperationException("synthetic optimization denied");
        reads = 0;
        available = () => { reads++; return 4096; };
        Check(Object.ReferenceEquals(Failure(() => Execute(operation, true, available, () => { throw commandError; }, forbiddenElevation)), commandError) &&
            reads == 1 && elevations == 0, "A local optimization failure was swallowed, retried, or measured as success");
        Check(Object.ReferenceEquals(Failure(() => Helper(operation, Samples(1), () => { throw commandError; })), commandError),
            "The helper swallowed an optimization failure");
        Check(Helper(operation, Samples(0, 5 * 1024 + 1023), () => {}) == 5,
            "The helper result must use whole KiB");
        Check(Helper(operation, Samples(8192, 4096), () => {}) == 0,
            "The helper must clamp a memory decrease");
        Check(Helper(operation, Samples(0, Int64.MaxValue), () => {}) == Int32.MaxValue,
            "The helper must clamp large successful results before returning an exit code");

        Check(Failure(() => Execute(operation, false, forbiddenRead, forbiddenOptimize, null)) is ArgumentNullException,
            "A missing elevation delegate was accepted");
        Check(Failure(() => Execute(operation, true, null, forbiddenOptimize, null)) is ArgumentNullException,
            "A missing memory reader was accepted");
        Check(Failure(() => Execute(operation, true, forbiddenRead, null, null)) is ArgumentNullException,
            "A missing optimization delegate was accepted");
    }
}
'@
    [LivingPclMemoryChecks]::Run($assembly)
    Write-Output 'Memory regressions passed: read-only real token role check; simulated elevated/helper success, cancellation, denial, failure propagation, decreasing memory and exit-code bounds. No memory cleanup, token adjustment or UAC was executed.'
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($fixture -and [IO.Directory]::Exists($fixture)) {
        $resolved = [IO.Path]::GetFullPath($fixture)
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($resolved) -ne $temp -or
            [IO.Path]::GetFileName($resolved) -notmatch '\ALivingPCL-ToolboxMemory-[0-9a-f]{32}\z' -or
            (([IO.File]::GetAttributes($resolved) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Unsafe memory fixture cleanup path'
        }
        [IO.Directory]::Delete($resolved, $true)
    }
}
