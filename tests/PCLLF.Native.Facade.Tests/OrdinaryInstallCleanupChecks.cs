using System.Reflection;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class OrdinaryInstallCleanupChecks {
    internal static void Run(string parent) {
        string root = Path.Combine(parent, "ordinary-proof", "versions"); Directory.CreateDirectory(root);
        Type actual = typeof(MmcAdmissionGate).Assembly.GetType("PCLCS.OrdinaryInstallCleanupProof", true)!;
        foreach (string mode in new[] { "empty", "old", "unmarked", "namebinding", "nonce", "identity", "mmc", "config", "saves", "versions", "partial", "other-owner" }) {
            string folder = Path.Combine(root, mode); object task = new();
            if (mode == "old") Directory.CreateDirectory(folder);
            if (mode == "namebinding") {
                // Mismatching names retain the ordinary marker behavior but never seal
                // a special cleanup proof for an unrelated JSON.
                Native.Call(actual, "Register", null, task, folder, "other");
            } else Native.Call(actual, "Register", null, task, folder, mode);
            if (mode == "unmarked") { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, ".pclignore"), "existing foreign marker"); }
            Native.Call(actual, "WriteMarker", null, task, folder);
            string json = Path.Combine(folder, mode + ".json"); File.WriteAllBytes(json, []);
            if (mode == "nonce") File.AppendAllText(Path.Combine(folder, ".pclignore"), "foreign");
            if (mode == "identity") {
                string retained = folder + "-retained"; Directory.Move(folder, retained); Directory.CreateDirectory(folder);
                File.Copy(Path.Combine(retained, ".pclignore"), Path.Combine(folder, ".pclignore")); File.WriteAllBytes(json, []);
            }
            if (mode is "mmc" or "config") { Directory.CreateDirectory(Path.Combine(folder, "PCL")); File.WriteAllText(Path.Combine(folder, "PCL", mode == "mmc" ? "InstanceDefinition.json" : "config.json"), mode == "mmc" ? "{}" : "{"); }
            if (mode is "saves" or "versions") { Directory.CreateDirectory(Path.Combine(folder, mode)); File.WriteAllText(Path.Combine(folder, mode, "keep.txt"), "player data"); }
            if (mode == "partial") File.WriteAllText(json, "{");
            if (mode == "namebinding") { File.WriteAllText(json, "{"); File.WriteAllBytes(Path.Combine(folder, "other.json"), []); }
            object[] arguments = [new object[] { mode == "other-owner" ? new object() : task }, folder, false, null!];
            bool allowed = (bool)actual.GetMethod("TryAuthorize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
            Native.Check(allowed == (mode == "empty"), "Ordinary cleanup source boundary changed: " + mode + " / " + arguments[3]);
            Native.Check((bool)arguments[2] == (mode is not ("old" or "other-owner")), "A registered task silently fell back to ordinary cleanup: " + mode);
            if (mode == "unmarked") Native.Check(File.ReadAllText(Path.Combine(folder, ".pclignore")) == "existing foreign marker", "The current task overwrote an unproven preexisting marker.");
            Native.Check(Directory.Exists(folder) && File.Exists(Path.Combine(folder, ".pclignore")), "Read-only cleanup admission changed its fixture bytes.");
            if (mode == "empty") {
                Native.Check(!MmcInstanceInspector.Read(folder).Management.CanUseLegacyBackend, "Scoped empty-JSON admission relaxed the global management probe.");
                string other = Path.Combine(root, "wrong-target"); Directory.CreateDirectory(other);
                object[] wrong = [new object[] { task }, other, false, null!];
                bool wrongAllowed = (bool)actual.GetMethod("TryAuthorize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, wrong)!;
                Native.Check(!wrongAllowed && (bool)wrong[2], "An existing proof fell through to ordinary deletion for another CleanupInput target.");
            }
        }
        Console.WriteLine("PASS actual ordinary-install cleanup proof: only the live new-target nonce/identity may acknowledge its empty JSON; old targets, other owners, changed identity/nonce, MMC evidence, corrupt config, partial JSON and player folders remain denied.");
    }
}
