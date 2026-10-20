param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# 使用临时目录重放取消下载后的目录快照，不下载游戏，不操作用户实例。
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[Reflection.Assembly]::LoadFrom((Join-Path $root 'MeloongCore/Shared/bin/MeloongCore.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $root 'Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')) | Out-Null
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
public static class LivingPclCancellationCheck {
    public static void Cleanup(string root) {
        root = Path.GetFullPath(root);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
        string leaf = Path.GetFileName(root);
        Guid id;
        const string marker = "LivingPCL-CancelScan-";
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !leaf.StartsWith(marker) ||
            !Guid.TryParseExact(leaf.Substring(marker.Length), "N", out id)) throw new Exception("Unsafe fixture cleanup path");
        if (!Directory.Exists(root)) return;
        string prefix = root.TrimEnd('\\') + "\\";
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture file");
            File.Delete(file);
        }
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) {
            if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture directory");
            Directory.Delete(dir, false);
        }
        Directory.Delete(root, false);
    }
    public static int Run(Assembly assembly) {
        Type scanner = assembly.GetType("PCL.McInstanceDirectoryScanner");
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Func<DirectoryInfo, bool> hasFiles = d => scanner == null ? d.Exists && d.EnumerateFiles().Any() :
            (bool)scanner.GetMethod("HasFiles", flags).Invoke(null, new object[] { d });
        Func<string, DirectoryInfo[]> getDirectories = p => scanner == null ? new DirectoryInfo(p).GetDirectories() :
            (DirectoryInfo[])scanner.GetMethod("GetDirectories", flags).Invoke(null, new object[] { p });
        string root = Path.Combine(Path.GetTempPath(), "LivingPCL-CancelScan-" + Guid.NewGuid().ToString("N"));
        string versions = Path.Combine(root, "versions");
        string kept = Path.Combine(versions, "kept"), cancelled = Path.Combine(versions, "26.3"), empty = Path.Combine(versions, "empty");
        int failures = 0;
        try {
            Directory.CreateDirectory(kept); Directory.CreateDirectory(cancelled); Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(kept, "kept.json"), "{}");
            File.WriteAllText(Path.Combine(cancelled, "26.3.json"), "{}");
            DirectoryInfo[] snapshot = getDirectories(versions);
            DirectoryInfo removed = snapshot.Single(d => d.Name == "26.3");
            if (!removed.Exists) throw new Exception("Invalid fixture");
            File.Delete(Path.Combine(cancelled, "26.3.json")); Directory.Delete(cancelled, false);
            Action<DirectoryInfo, string> checkRemoved = (d, name) => {
                try { if (hasFiles(d)) throw new Exception("Deleted version retained"); }
                catch (Exception e) { failures++; Console.WriteLine("FAIL: " + name + "; " + e.GetBaseException().GetType().Name); }
            };
            checkRemoved(removed, "cached directory removed before enumeration");
            if (!hasFiles(snapshot.Single(d => d.Name == "kept"))) throw new Exception("Valid version dropped");
            if (hasFiles(snapshot.Single(d => d.Name == "empty"))) throw new Exception("Empty version retained");
            for (int i = 0; i < 10; i++) {
                Directory.CreateDirectory(cancelled); File.WriteAllText(Path.Combine(cancelled, "26.3.json"), "{}");
                DirectoryInfo stale = new DirectoryInfo(cancelled);
                if (!stale.Exists) throw new Exception("Invalid cached Exists fixture");
                File.Delete(Path.Combine(cancelled, "26.3.json")); Directory.Delete(cancelled, false);
                checkRemoved(stale, "cancellation replay " + i);
            }
            string missing = Path.Combine(root, "removed-versions");
            Directory.CreateDirectory(missing); Directory.Delete(missing, false);
            try { if (getDirectories(missing).Length != 0) throw new Exception("Removed root returned entries"); }
            catch (Exception e) { failures++; Console.WriteLine("FAIL: removed versions root; " + e.GetBaseException().GetType().Name); }
        } finally { Cleanup(root); }
        return failures;
    }
}
'@
$failures=[LivingPclCancellationCheck]::Run($assembly)
if($failures -ne 0) { throw "Version list cancellation checks failed: $failures" }
Write-Output 'Version-list cancellation checks passed: stale directory snapshots, ten cancellation replays, valid/empty siblings, and removed versions root.'
