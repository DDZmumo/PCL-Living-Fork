param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Only synthetic temporary files; no network or user settings.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run with powershell -NoProfile (.NET Framework), not pwsh.' }
$assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
public static class ToolboxDownloadChecks {
    static Type session;
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object Call(string name, object instance, params object[] args) {
        try { return session.GetMethod(name, Flags).Invoke(instance, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static string Field(object instance, string name) { return (string)session.GetField(name, Flags).GetValue(instance); }
    static void ExpectFailure(Action action, Type kind) {
        try { action(); } catch (Exception e) { if (kind.IsInstanceOfType(e)) return; throw; }
        throw new Exception("Expected rejection: " + kind.Name);
    }
    static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
    static void CleanFixture(string root) {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
        Guid id;
        const string marker = "LivingPCL-ToolboxDownload-";
        root = Path.GetFullPath(root);
        string leaf = Path.GetFileName(root);
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !leaf.StartsWith(marker) ||
            !Guid.TryParseExact(leaf.Substring(marker.Length), "N", out id)) throw new Exception("Unsafe fixture root");
        if (!Directory.Exists(root)) return;
        foreach (string path in Directory.GetFileSystemEntries(root)) {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new Exception("Unexpected fixture link");
        }
        Directory.Delete(root, true);
    }
    public static void Run(Assembly assembly) {
        session = assembly.GetType("PCL.ToolboxDownloadSession", true);
        Check((string)Call("ValidateName", null, "normal.zip") == "", "Normal filename rejected");
        foreach (string name in new [] { "", "..", "../a", "a\\b", "a:b", "NUL.txt", "COM1", "LPT\u00b2.zip", "a.", " a", "a " })
            Check((string)Call("ValidateName", null, name) != "", "Invalid filename accepted: " + name);
        foreach (string url in new [] { "file:///C:/a", "ftp://example.com/a", "https://user:pass@example.com/a", "not-a-url" })
            ExpectFailure(() => Call("ParseUrl", null, url), typeof(ArgumentException));
        Call("ParseUrl", null, "http://127.0.0.1/a");
        Check((string)Call("SuggestName", null, "https://example.com/a+b.zip?file=bad#fragment") == "a+b.zip", "Query/plus filename changed");
        Check((string)Call("SuggestName", null, "https://example.com/%E6%B5%8B%E8%AF%95.zip") == "\u6d4b\u8bd5.zip", "Unicode filename changed");
        Check((string)Call("SuggestName", null, "https://example.com/a%2Fb.zip") == "", "Decoded traversal accepted");
        Type credentials = assembly.GetType("PCL.BuildCredentials", true);
        MethodInfo policy = credentials.GetMethod("IsCurseForgeRequest", Flags);
        foreach (string uri in new [] { "https://api.curseforge.com/v1/mods", "https://API.CURSEFORGE.COM:443/v1/mods" })
            Check((bool)policy.Invoke(null, new object[] { new Uri(uri) }), "Official API rejected");
        foreach (string uri in new [] { "https://evil.example/?api.curseforge.com", "https://api.curseforge.com.evil.example/", "http://api.curseforge.com/", "https://api.curseforge.com:444/", "https://user@api.curseforge.com/", "https://media.forgecdn.net/file" })
            Check(!(bool)policy.Invoke(null, new object[] { new Uri(uri) }), "API header policy accepts untrusted destination");

        string root = Path.Combine(Path.GetTempPath(), "LivingPCL-ToolboxDownload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            ExpectFailure(() => Call("ResolveTarget", null, "relative", "file.zip"), typeof(ArgumentException));
            ExpectFailure(() => Call("ResolveTarget", null, root, "..\\escape.zip"), typeof(ArgumentException));
            string target = Path.Combine(root, "saved.zip");
            File.WriteAllText(target, "original-content");
            object active = Call("Create", null, root, "saved.zip");
            ExpectFailure(() => Call("Create", null, root.ToUpperInvariant(), "SAVED.ZIP"), typeof(IOException));
            string stage = Field(active, "StagePath");
            File.WriteAllText(stage, "new-content");
            Check(File.ReadAllText(target) == "original-content", "Original changed before commit");
            ExpectFailure(() => Call("Commit", active, false, new Func<bool>(() => false)), typeof(IOException));
            Check(File.ReadAllText(target) == "original-content", "Overwrite rejection changed original");
            Call("Complete", active, false);
            Check(!Directory.Exists(Field(active, "StageFolder")), "Canceled staging retained");
            // A late merge can recreate the canceled staging file; the callback removes only that exact file.
            Directory.CreateDirectory(Field(active, "StageFolder"));
            File.WriteAllText(stage, "late-merge");
            Call("OnCanceledMergeFinished", active);
            Check(!Directory.Exists(Field(active, "StageFolder")), "Late merge was retained");
            Call("Complete", active, false);
            foreach (bool existed in new [] { false, true }) {
                string name = existed ? "saved.zip" : "new.zip";
                string destination = Path.Combine(root, name);
                active = Call("Create", null, root, name);
                File.WriteAllText(Field(active, "StagePath"), "cancel-before");
                ExpectFailure(() => Call("Commit", active, true, new Func<bool>(() => true)), typeof(OperationCanceledException));
                Check(existed ? File.ReadAllText(destination) == "original-content" : !File.Exists(destination), "Pre-commit cancellation changed destination");
                Call("Complete", active, false);

                active = Call("Create", null, root, name);
                File.WriteAllText(Field(active, "StagePath"), "cancel-before-combo-finish");
                Call("Commit", active, true, new Func<bool>(() => false));
                Call("Complete", active, false);
                Check(existed ? File.ReadAllText(destination) == "original-content" : !File.Exists(destination), "Cancel after save did not restore destination");

                active = Call("Create", null, root, name);
                File.WriteAllText(Field(active, "StagePath"), "cancel-after");
                int checks = 0;
                ExpectFailure(() => Call("Commit", active, true, new Func<bool>(() => ++checks > 1)), typeof(OperationCanceledException));
                Check(existed ? File.ReadAllText(destination) == "original-content" : !File.Exists(destination), "Post-commit cancellation did not roll back");
                Call("Complete", active, false);

                active = Call("Create", null, root, name);
                File.WriteAllText(Field(active, "StagePath"), "completed-content");
                Call("Commit", active, true, new Func<bool>(() => false));
                Call("Complete", active, true);
                Check(File.ReadAllText(destination) == "completed-content", "Successful save failed");
                Check(!Directory.Exists(Field(active, "StageFolder")), "Successful staging retained");
            }
            Check(Directory.GetDirectories(root).Length == 0, "Unexpected remaining staging directory");
            active = Call("Create", null, root, "locked.zip");
            File.WriteAllText(Field(active, "StagePath"), "locked-new-file");
            Call("Commit", active, true, new Func<bool>(() => false));
            using (FileStream locked = File.Open(Path.Combine(root, "locked.zip"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                Call("Complete", active, false);
                string pending = (string)session.GetProperty("UnrevertedPath", Flags).GetValue(active, null);
                Check(pending == Path.Combine(root, "locked.zip"), "Unreverted new file was not reported");
            }
        } finally { CleanFixture(root); }
    }
}
'@
try { [ToolboxDownloadChecks]::Run($assembly) } catch { throw }
Write-Output 'Toolbox download checks passed: URL/name validation, API header destinations, duplicate targets, overwrite preservation, cancel rollback, successful save and late merge cleanup.'
