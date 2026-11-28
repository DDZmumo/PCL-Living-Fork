using System.Windows;
using System.Windows.Controls;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;
// The two generic arguments are closed over the real internal Main DTO types.
// No grouping algorithm, selection model or controls are reimplemented here.
internal static class CloneGroupChecks<TNode, TSet>
    where TNode : InstanceCloneGroupNodeData<TNode> where TSet : InstanceCloneGroupSetData<TNode>
{
    internal static void Run(CloneFixture f)
    {
        const string script = "Test-InstanceCloneGroups.ps1";
        void C(int line, bool value, string message) => f.Check(script, line, value, message);
        void Reject(int line, Action action, string message) => f.Reject(script, line, action, message);
        Type groups = Native.Type("PCL.InstanceCloneGroups");
        TSet Build(params string[] folders) => (TSet)Native.Call(groups, "Build", null, folders, CancellationToken.None)!;
        void All(TSet model, int count, int line)
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (TNode root in model.Roots)
                foreach (TNode node in (IEnumerable<TNode>)Native.Call(groups, "Flatten", null, root)!)
                    C(line, seen.Add(node.VersionFolder!), "Grouped version appears more than once.");
            C(line, seen.Count == count && model.ByPath.Count == count, "A visible version disappeared from grouping.");
        }

        string versions = f.Folder("groups/minecraft/versions");
        f.SelectMinecraft(Path.GetDirectoryName(versions)!);
        string Version(string name)
        {
            string folder = Path.Combine(versions, name);
            CloneFixture.Put(Path.Combine(folder, name + ".json"), new JObject { ["id"] = name, ["clientVersion"] = "1.20.1", ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main", ["libraries"] = new JArray() }.ToString());
            CloneFixture.Put(Path.Combine(folder, "PCL/Setup.ini"), "VersionArgumentIndieV2:True");
            return folder;
        }

        void Origin(string clone, string source) => CloneFixture.Put(Path.Combine(clone, "PCL/CloneOrigin.json"), new JObject { ["schema"] = 1, ["sourceVersionFolder"] = source, ["sourceGameRoot"] = source, ["sourceName"] = Path.GetFileName(source), ["cloneName"] = Path.GetFileName(clone) }.ToString());
        string source = Version("source"), clone = Version("clone"), sibling = Version("sibling"), grandchild = Version("grandchild");
        Origin(clone, source);
        Origin(sibling, source);
        Origin(grandchild, clone);
        TSet model = Build(grandchild, clone, sibling, source, source.ToUpperInvariant());
        All(model, 4, 38);
        C(39, model.Roots.Count == 1 && model.Roots[0].VersionFolder == source && model.ByPath[clone].ParentFolder == source && model.ByPath[grandchild].ParentFolder == clone, "Clone-of-clone or cross-order grouping failed.");
        TSet visible = Build(clone, sibling, grandchild);
        All(visible, 3, 40);
        C(41, string.IsNullOrEmpty(visible.ByPath[clone].ParentFolder) && string.IsNullOrEmpty(visible.ByPath[sibling].ParentFolder) && visible.Roots.Count == 2, "Missing/hidden/locked source was used as a group parent.");
        string orphan = Version("orphan");
        Origin(orphan, Path.Combine(versions, "missing"));
        TSet orphanModel = Build(orphan);
        C(43, orphanModel.Roots.Count == 1 && !string.IsNullOrEmpty(orphanModel.Roots[0].DeclaredSourceFolder), "Orphan clone disappeared or lost minimal source information.");
        string bad = Version("bad");
        CloneFixture.Put(Path.Combine(bad, "PCL/CloneOrigin.json"), """{"schema":"not-an-integer","sourceVersionFolder":"C:\\outside"}""");
        string badJson = Version("bad-json");
        CloneFixture.Put(Path.Combine(badJson, "PCL/CloneOrigin.json"), "[]");
        string large = Version("large");
        CloneFixture.Put(Path.Combine(large, "PCL/CloneOrigin.json"), new string ('x', 65537));
        string external = Version("external"), outsideSource = Path.Combine(f.Root, "groups/other-minecraft/versions/source");
        CloneFixture.Put(Path.Combine(outsideSource, "keep.txt"), "keep-outside");
        Origin(external, outsideSource);
        string renamed = Version("renamed");
        Origin(renamed, source);
        JObject renamedData = JObject.Parse(File.ReadAllText(Path.Combine(renamed, "PCL/CloneOrigin.json")));
        renamedData["cloneName"] = "previous-name";
        CloneFixture.Put(Path.Combine(renamed, "PCL/CloneOrigin.json"), renamedData.ToString());
        TSet invalid = Build(bad, badJson, large, external, renamed, source);
        All(invalid, 6, 49);
        C(50, invalid.Roots.Count == 6, "Invalid relation did not fall back to a standalone version.");
        string cycleA = Version("cycle-a"), cycleB = Version("cycle-b"), cycleChild = Version("cycle-child");
        Origin(cycleA, cycleB);
        Origin(cycleB, cycleA);
        Origin(cycleChild, cycleA);
        TSet cycle = Build(cycleA, cycleB, cycleChild);
        All(cycle, 3, 52);
        C(53, cycle.Roots.Count == 3, "Cyclic source chain was grouped.");
        List<string> chain = new()
        {
            source
        };
        string previous = source;
        for (int i = 1; i <= 36; i++)
        {
            string next = Version("depth-" + i);
            Origin(next, previous);
            chain.Add(next);
            previous = next;
        }

        All(Build(chain.ToArray()), chain.Count, 54);
        string linkedClone = Version("linked-clone"), junction = Path.Combine(versions, "linked-source");
        f.Junction(junction, outsideSource);
        try
        {
            Origin(linkedClone, junction);
            TSet linked = Build(linkedClone, junction);
            All(linked, 2, 56);
            C(56, linked.Roots.Count == 2, "Reparse source was followed for grouping.");
        }
        finally
        {
            f.Unlink(junction);
        }

        C(58, File.ReadAllText(Path.Combine(outsideSource, "keep.txt")) == "keep-outside", "Relation parser modified linked data.");
        using (CancellationTokenSource cancel = new())
        {
            cancel.Cancel();
            Reject(59, () => Native.Call(groups, "Build", null, new[] { source, clone }, cancel.Token), "Canceled relation scan completed.");
        }

        ModMinecraft.McInstance Instance(string path)
        {
            ModMinecraft.McInstance instance = new(path)
            {
                Version = new McVersion("1.20.1", true),
                State = ModMinecraft.McInstance.McInstanceState.Original,
                IsLoaded = true,
                Info = "Fixture instance",
                Logo = "pack://application:,,,/images/Blocks/Grass.png"
            };
            foreach (Settings.Setting setting in Settings.Entries.Values)
                if (setting.Source == Settings.Sources.Instance)
                    setting.ValueCache[instance.PathVersion] = setting.DefaultValue!;
            Settings.Entries["VersionArgumentIndieV2"].ValueCache[instance.PathVersion] = true;
            return instance;
        }

        ModMinecraft.McInstance sourceInstance = Instance(source), cloneInstance = Instance(clone), siblingInstance = Instance(sibling), grandInstance = Instance(grandchild);
        siblingInstance.IsStar = true;
        ClonePage(f, versions, sourceInstance, cloneInstance, Instance(orphan), Instance(bad));
        var cards = new Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>> {
            [ModMinecraft.McInstanceCardType.OriginalLike] = [sourceInstance, cloneInstance, siblingInstance, grandInstance]
        };
        var row = new Button { Tag = cloneInstance };
        using var lease = new System.Threading.ManualResetEventSlim();
        var target = PCLCS.InstanceSelectionPageController<ModMinecraft.McInstance, ModMinecraft.McInstance.McInstanceState, ModMinecraft.McInstanceCardType, InstanceCloneGroupNode, InstanceCloneGroupSet>.ResolveDeleteTarget(row, cloneInstance);
        C(146, ReferenceEquals(target, cloneInstance), "Delete target did not come from its own row Tag.");
        Reject(147, () => PCLCS.InstanceSelectionPageController<ModMinecraft.McInstance, ModMinecraft.McInstance.McInstanceState, ModMinecraft.McInstanceCardType, InstanceCloneGroupNode, InstanceCloneGroupSet>.ResolveDeleteTarget(row, sourceInstance), "Mismatched delete target was accepted.");
        var pruned = PCLCS.InstanceSelectionPageController<ModMinecraft.McInstance, ModMinecraft.McInstance.McInstanceState, ModMinecraft.McInstanceCardType, InstanceCloneGroupNode, InstanceCloneGroupSet>.WithoutDeletedInstance(cards, clone);
        var all = pruned.Values.SelectMany(value => value).ToArray();
        C(150, !all.Any(item => CloneFixture.SamePath(item.PathVersion, clone)) && new[] { source, sibling, grandchild }.All(path => all.Any(item => CloneFixture.SamePath(item.PathVersion, path))), "Deleting one clone pruned a source, sibling or descendant.");
        C(151, PCLCS.InstanceSelectionPageController<ModMinecraft.McInstance, ModMinecraft.McInstance.McInstanceState, ModMinecraft.McInstanceCardType, InstanceCloneGroupNode, InstanceCloneGroupSet>.SelectionAfterDelete(cloneInstance, clone)is null, "Deleted selected clone was retained.");
        C(152, ReferenceEquals(PCLCS.InstanceSelectionPageController<ModMinecraft.McInstance, ModMinecraft.McInstance.McInstanceState, ModMinecraft.McInstanceCardType, InstanceCloneGroupNode, InstanceCloneGroupSet>.SelectionAfterDelete(sourceInstance, clone), sourceInstance), "Deleting a clone changed an unrelated selected source.");
        TSet afterDelete = Build(source, sibling, grandchild);
        All(afterDelete, 3, 153);
        C(154, string.IsNullOrEmpty(afterDelete.ByPath[grandchild].ParentFolder), "Descendant disappeared into a deleted parent group.");
        foreach (string path in new[]
        {
            source,
            clone,
            sibling,
            grandchild
        }

        )
            C(155, File.Exists(Path.Combine(path, Path.GetFileName(path) + ".json")), "UI/deletion contract touched a version directory.");
    }

    private static void ClonePage(CloneFixture f, string versions, ModMinecraft.McInstance source,
        ModMinecraft.McInstance clone, ModMinecraft.McInstance orphan, ModMinecraft.McInstance bad)
    {
        var previous = LauncherInstanceContext.Instance;
        try {
            LauncherInstanceContext.Instance = source;
            var page = new PageInstanceClone();
            Native.Check(page.FindName("TextName") is not null, "The native clone workspace has no name editor.");
        } finally { LauncherInstanceContext.Instance = previous; }
    }
}
