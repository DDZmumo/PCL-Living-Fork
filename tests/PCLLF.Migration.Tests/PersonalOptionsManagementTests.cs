using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;

internal static class PersonalOptionsManagementTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("personal settings preview applies selected changes with an exact backup", PreviewApply);
        yield return ("personal settings stale options/profile/selection and running game reject writes", StaleInputs);
        yield return ("personal settings conflict resolution rejects stale decisions and preserves other values", Conflicts);
        yield return ("saving conflict drafts validates their records before committing the batch", ConflictDraftSave);
        yield return ("personal settings pending conflicts stay out of automatic application", PendingConflicts);
        yield return ("personal settings catalog covers only known personal fields", Catalog);
        yield return ("personal settings basic sync never injects fields missing in the target", ExistingFieldsOnly);
        yield return ("personal scheme editors validate values and preserve game files", EditScheme);
        yield return ("personal scheme editors reject stale and partially invalid batches", EditGuards);
        yield return ("personal sliders preserve Minecraft ranges and encodings", SliderRanges);
        yield return ("version editors update only local options with backup and conversion", EditLocal);
        yield return ("version editors reject stale and absent keys without global writes", LocalEditGuards);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Expected guarded operation rejection."); }
    private static void PreviewApply() {
        using var f = new Fixture();
        byte[] before = File.ReadAllBytes(f.File);
        var preview = f.Manager.Preview(f.Instance);
        Require(preview.Changed.SetEquals(["mouseSensitivity", "fov"]) && File.ReadAllBytes(f.File).SequenceEqual(before), "Preview changed files or included author configuration.");
        Require(f.Manager.Apply(f.Instance, preview) == 2, "Wrong applied count.");
        Require(File.ReadAllText(f.File).Contains("fov:90") && File.ReadAllText(f.File).Contains("renderDistance:16"), "Selected values or author configuration changed incorrectly.");
        Require(File.ReadAllBytes(f.File + ".pcl-backup").SequenceEqual(before), "Original bytes were not backed up.");
    }
    private static void StaleInputs() {
        using var f = new Fixture();
        var preview = f.Manager.Preview(f.Instance);
        File.AppendAllText(f.File, "showSubtitles:true\n");
        byte[] current = File.ReadAllBytes(f.File);
        Reject(() => f.Manager.Apply(f.Instance, preview));
        Require(File.ReadAllBytes(f.File).SequenceEqual(current), "Stale preview overwrote newer options.");
        preview = f.Manager.Preview(f.Instance);
        f.Profile["values"]!["fov"] = "100"; f.Save();
        Reject(() => f.Manager.Apply(f.Instance, preview));
        preview = f.Manager.Preview(f.Instance); f.Selected.Remove("fov");
        Reject(() => f.Manager.Apply(f.Instance, preview));
        preview = f.Manager.Preview(f.Instance); f.Running = true;
        Reject(() => f.Manager.Apply(f.Instance, preview)); f.Running = false;
        f.Instance.Enabled = false; Reject(() => f.Manager.Apply(f.Instance, preview));
        Require(File.ReadAllBytes(f.File).SequenceEqual(current), "A rejected write changed target bytes.");
    }
    private static void Conflicts() {
        using var f = new Fixture();
        var record = JObject.Parse("{\"baseline\":\"0.2\",\"profile\":\"0.8\",\"game\":\"0.6\"}");
        f.Profile["conflicts"] = new JObject(new JProperty("mouseSensitivity", record)); f.Save();
        f.Manager.Resolve("mouseSensitivity", true, (JObject)record.DeepClone());
        JObject after = f.Manager.Profile();
        Require((string?)after["values"]!["mouseSensitivity"] == "0.6" && (string?)after["values"]!["fov"] == "90" && ((JObject)after["conflicts"]!).Count == 0, "Conflict choice changed unrelated values.");
        f.Save(); f.Profile["values"]!["mouseSensitivity"] = "0.9"; f.Save();
        Reject(() => f.Manager.Resolve("mouseSensitivity", true, record));
        Require((string?)f.Manager.Profile()["values"]!["mouseSensitivity"] == "0.9", "Stale resolution replaced a newer profile value.");
    }
    private static void PendingConflicts() {
        using var f = new Fixture();
        f.Profile["conflicts"] = JObject.Parse("{\"fov\":{\"profile\":\"90\",\"game\":\"100\"}}"); f.Save();
        var preview = f.Manager.Preview(f.Instance);
        Require(!preview.Changed.Contains("fov") && preview.Merged.Contains("fov:70"), "Preview applied an unresolved conflict.");
        var flow = new PersonalOptionsFlow(f.Data, f.Gate, () => false, _ => f.Instance, f.Selected.Contains, _ => { }, (a,b) => a == b, (_,_) => { }, () => new PersonalOptionsSessionData());
        var session = flow.Prepare(f.Instance)!;
        Require(!session.Keys.Contains("fov") && File.ReadAllText(f.File).Contains("fov:70"), "Launch preparation applied an unresolved conflict.");
    }
    private static void ConflictDraftSave() {
        using var f = new Fixture();
        var record = JObject.Parse("{\"baseline\":\"80\",\"profile\":\"90\",\"game\":\"100\"}");
        f.Profile["conflicts"] = new JObject(new JProperty("fov", record.DeepClone())); f.Save();
        var expected = new Dictionary<string,JObject> { ["fov"] = record };
        f.Profile["conflicts"]!["fov"]!["game"] = "95"; f.Save();
        string before = f.Manager.Profile().ToString();
        Reject(() => f.Manager.EditValues([new("fov","100","90"),new("mouseSensitivity","0.6","0.8")], expected));
        Require(f.Manager.Profile().ToString()==before, "A stale conflict decision committed part of the draft.");
        expected["fov"] = (JObject)f.Profile["conflicts"]!["fov"]!.DeepClone();
        f.Manager.EditValues([new("fov","95","90")], expected);
        Require((string?)f.Manager.Profile()["values"]!["fov"]=="95" && f.Manager.Profile()["conflicts"]!["fov"] is null &&
            (string?)f.Manager.Profile()["sourceKind"]=="custom", "Saving the conflict draft lost its value, source kind or resolution.");
    }
    private static void Catalog() {
        Require(PersonalOptionsCatalog.Items.Select(i => i.Key).Distinct().Count() == PersonalOptionsCatalog.Items.Count, "Duplicate personal option identities.");
        foreach (var item in PersonalOptionsCatalog.Items)
            Require(PersonalOptionsRules.VanillaBinding(item.Key) || PersonalOptionsRules.IsMouseKey(item.Key) || PersonalOptionsRules.IsViewKey(item.Key) || PersonalOptionsRules.IsAccessibilityKey(item.Key) || item.Key.StartsWith("soundCategory_"), "Catalog includes a non-personal setting.");
        foreach (string key in new[] { "showSubtitles", "darknessEffectScale", "fovEffectScale", "rawMouseInput", "key_key.advancements", "key_key.swapOffhand", "soundCategory_voice" })
            Require(!PersonalOptionsCatalog.Contains(key), "Version-specific setting leaked into basic sync: " + key);
    }
    private static void ExistingFieldsOnly() {
        using var f = new Fixture();
        f.Profile["values"]!["key_key.forward"] = "key.keyboard.r"; f.Selected.Add("key_key.forward"); f.Save();
        var preview = f.Manager.Preview(f.Instance);
        Require(!preview.Merged.Contains("key_key.forward"), "Manual apply injected a missing key.");
        var flow = new PersonalOptionsFlow(f.Data, f.Gate, () => false, _ => f.Instance, f.Selected.Contains, _ => { }, (a,b) => a == b, (_,_) => { }, () => new PersonalOptionsSessionData(), existingOnly: true);
        var session = flow.Prepare(f.Instance)!;
        Require(!File.ReadAllText(f.File).Contains("key_key.forward") && !session.Keys.Contains("key_key.forward"), "Launch sync injected or tracked a missing key.");
    }
    private static void EditScheme() {
        using var f = new Fixture();
        byte[] original = File.ReadAllBytes(f.File);
        f.Manager.EditValues([new("fov", "100", "90"), new("invertYMouse", "True", null), new("key_key.forward", "19:alt", null)]);
        JObject saved = (JObject)f.Manager.Profile()["values"]!;
        Require((string?)saved["fov"] == "100" && (string?)saved["invertYMouse"] == "true" && (string?)saved["key_key.forward"] == "key.keyboard.r:ALT", "Scheme edit was not canonicalized.");
        Require(File.ReadAllBytes(f.File).SequenceEqual(original) && (string?)saved["renderDistance"] == "32", "Scheme editor changed the game file or unrelated scheme values.");
    }
    private static void EditGuards() {
        using var f = new Fixture();
        string before = f.Manager.Profile().ToString();
        Reject(() => f.Manager.EditValues([new("fov", "80", "70")]));
        Reject(() => f.Manager.EditValues([new("fov", "80", "90"), new("mouseSensitivity", "2", "0.8")]));
        foreach (var edit in new[] { new PersonalOptionEdit("renderDistance", "4", "32"), new("fov", "NaN", "90"), new("fov", "111", "90"), new("invertYMouse", "invalid", null) }) Reject(() => f.Manager.EditValues([edit]));
        f.Running = true; Reject(() => f.Manager.EditValues([new("fov", "80", "90")]));
        Require(f.Manager.Profile().ToString() == before, "Rejected batch partially changed the stored scheme.");
    }
    private static void SliderRanges() {
        Require(PersonalOptionsCatalog.SliderValue("fov", "90") == 60 && PersonalOptionsCatalog.FromSlider("fov", 60) == "90", "FOV slider range is wrong.");
        Require(PersonalOptionsCatalog.SliderValue("mouseSensitivity", "0.5") == 100 && PersonalOptionsCatalog.FromSlider("mouseSensitivity", 100) == "0.5", "Mouse sensitivity does not match the game scale.");
        Require(PersonalOptionsCatalog.FromSlider("soundCategory_master", 75) == "0.75", "Volume slider lost its game encoding.");
        Reject(() => PersonalOptionsCatalog.FromSlider("fov", 81));
        Reject(() => PersonalOptionsCatalog.FromSlider("soundCategory_master", -1));
    }
    private static void EditLocal() {
        using var f = new Fixture();
        byte[] original = File.ReadAllBytes(f.File);
        string profile = f.Manager.Profile().ToString();
        f.Manager.EditInstanceValues(f.Instance, [new("fov", "100", "70"), new("mouseSensitivity", "0.6", "0.2")]);
        Require(File.ReadAllText(f.File).Contains("fov:100") && File.ReadAllText(f.File).Contains("renderDistance:16"), "Local edit lost values or author settings.");
        Require(File.ReadAllBytes(f.File + ".pcl-backup").SequenceEqual(original) && f.Manager.Profile().ToString() == profile, "Local edit changed the global scheme or lost its backup.");
    }
    private static void LocalEditGuards() {
        using var f = new Fixture();
        byte[] original = File.ReadAllBytes(f.File);
        string profile = f.Manager.Profile().ToString();
        Reject(() => f.Manager.EditInstanceValues(f.Instance, [new("fov", "100", "90")]));
        Reject(() => f.Manager.EditInstanceValues(f.Instance, [new("fov", "100", "70"), new("bobView", "true", null)]));
        Reject(() => f.Manager.EditInstanceValues(f.Instance, [new("renderDistance", "4", "16")]));
        f.Running = true; Reject(() => f.Manager.EditInstanceValues(f.Instance, [new("fov", "100", "70")]));
        Require(File.ReadAllBytes(f.File).SequenceEqual(original) && f.Manager.Profile().ToString() == profile, "Rejected local edits changed a file or the global scheme.");
    }
    private sealed class Instance : IPersonalOptionsInstance {
        public string VersionPath { get; init; } = "";
        public string GameFolder { get; init; } = "";
        internal bool Enabled = true;
        public bool SyncEnabled => Enabled;
        public bool VersionValid => true;
        public int VanillaMajor => 20;
    }
    private sealed class Fixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PclPersonalManagement-" + Guid.NewGuid().ToString("N"));
        internal readonly object Gate = new();
        internal readonly HashSet<string> Selected = ["mouseSensitivity", "fov"];
        internal readonly LocalGameDataStore Data;
        internal readonly PersonalOptionsManagement Manager;
        internal readonly Instance Instance;
        internal readonly JObject Profile = JObject.Parse("{\"version\":1,\"values\":{\"mouseSensitivity\":\"0.8\",\"fov\":\"90\",\"renderDistance\":\"32\"}}");
        internal bool Running;
        internal string File => Path.Combine(root, "game", "options.txt");
        internal Fixture() {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, "mouseSensitivity:0.2\nfov:70\nrenderDistance:16\n", new UTF8Encoding(false));
            Data = new(() => Path.Combine(root, "profile"), (_,_) => { });
            Manager = new(Data, Gate, Selected.Contains, () => Running, _ => { });
            Instance = new() { VersionPath = root, GameFolder = Path.Combine(root, "game") }; Save();
        }
        internal void Save() => Data.WriteJson("personal-options.json", Profile);
        public void Dispose() => Directory.Delete(root, true);
    }
}
