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
        var map = new Dictionary<string, ModMinecraft.McInstance>();
        foreach (var instance in new[]
        {
            sourceInstance,
            cloneInstance,
            siblingInstance,
            grandInstance
        }

        )
            map.Add(instance.PathVersion.TrimEnd('\\'), instance);
        FrameworkElement choiceValue = (FrameworkElement)Native.Static("PCL.PageSelectRight", "BuildCloneChoice", model.ByPath[source], map, null)!;
        C(111, choiceValue is StackPanel panel && panel.Children.Count == 2, "Source row is not separate from its collapsible clone group.");
        StackPanel choice = (StackPanel)choiceValue;
        MyListItem sourceRow = (MyListItem)MyVirtualizingElement.TryInit((FrameworkElement)choice.Children[0]);
        MyCard card = (MyCard)choice.Children[1];
        C(113, ReferenceEquals(sourceRow.Tag, sourceInstance) && card.IsSwapped && ((Panel)card.SwapControl!).Children.Count == 0 && card.Title.Contains("source", StringComparison.Ordinal), "Source selection or default lazy collapse contract failed.");
        PageSelectRight.McInstanceListContent(sourceRow, EventArgs.Empty);
        C(115, sourceRow.Buttons.Count() == 3, "Source row lost native favorite/delete/settings controls.");
        card.IsSwapped = false;
        Panel children = (Panel)card.SwapControl!;
        C(117, children.Children.Count == 3, "Expanded clone group omitted a child or native spacer.");
        C(119, children.Children[0] is StackPanel, "Clone-of-clone source row was lost.");
        MyListItem cloneRow = (MyListItem)MyVirtualizingElement.TryInit((FrameworkElement)((StackPanel)children.Children[0]).Children[0]);
        C(121, ReferenceEquals(cloneRow.Tag, cloneInstance), "Nested row is bound to its source instead of its own clone.");
        PageSelectRight.McInstanceListContent(cloneRow, EventArgs.Empty);
        C(123, cloneRow.Buttons.Count() == 3, "Nested clone lost native favorite/delete/settings controls.");
        StackPanel selectedChoice = (StackPanel)Native.Static("PCL.PageSelectRight", "BuildCloneChoice", model.ByPath[source], map, grandchild)!;
        MyCard selectedCard = (MyCard)selectedChoice.Children[1];
        MyCard selectedChild = (MyCard)((StackPanel)((Panel)selectedCard.SwapControl!).Children[0]).Children[1];
        C(125, !selectedCard.IsSwapped && !selectedChild.IsSwapped, "Selected descendant did not expand its ancestor groups.");
        var cards = new Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>>
        {
            [ModMinecraft.McInstanceCardType.OriginalLike] = new()
            {
                sourceInstance
            },
            [ModMinecraft.McInstanceCardType.API] = new()
            {
                cloneInstance,
                siblingInstance,
                grandInstance
            },
            [ModMinecraft.McInstanceCardType.Star] = new()
            {
                siblingInstance
            }
        };
        Native.State(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, grandInstance);
        PageSelectRight Render(Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>> values, bool hidden)
        {
            ModMinecraft.McInstanceList = values;
            PageSelectRight page = new()
            {
                ShowHidden = hidden
            };
            Native.Call(typeof(PageSelectRight), "McInstanceListUI", page, new object? [] { null });
            return page;
        }

        Panel Main(PageSelectRight page) => (Panel)page.FindName("PanMain");
        PageSelectRight page = Render(cards, false);
        C(129, Main(page).Children.Count == 2 && GroupCards(Main(page)).Count(item => Equals(item.Tag, source)) == 1, "Cross-category grouping or separate favorite entry failed.");
        var hiddenCards = new Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>>
        {
            [ModMinecraft.McInstanceCardType.OriginalLike] = new()
            {
                sourceInstance
            },
            [ModMinecraft.McInstanceCardType.Hidden] = new()
            {
                cloneInstance
            }
        };
        PageSelectRight hiddenPage = Render(hiddenCards, true);
        C(132, Main(hiddenPage).Children.Count == 1 && !GroupCards(Main(hiddenPage)).Any(), "Hidden clone vanished or grouped under a source outside the current view.");
        RedoableWorker worker = new((Action)(() =>
        {
        }));
        C(135, ModMinecraft.McInstanceLock.TryAcquire(worker, sourceInstance.PathVersion), "Could not acquire fixture-only source lock.");
        try
        {
            PageSelectRight lockedPage = Render(cards, false);
            C(138, !GroupCards(Main(lockedPage)).Any(item => Equals(item.Tag, source)) && Main(lockedPage).Children.Count == 2, "Locked source was still a group parent or its clones disappeared.");
        }
        finally
        {
            Release(worker, sourceInstance);
        }

        // LF50 split write exclusion from navigation: a snapshot must allow the
        // real grouping/selection UI while preserving the source reservation.
        worker = new((Action)(() =>
        {
        }));
        f.Check("LF50 snapshot contract", 1, (bool)Native.Call(typeof(ModMinecraft.McInstanceLock), "TryAcquireSnapshot", null, worker, sourceInstance.PathVersion)!, "Could not reserve snapshot source.");
        try
        {
            bool blocks = (bool)Native.Call(typeof(ModMinecraft.McInstanceLock), "BlocksNavigation", null, sourceInstance)!;
            PageSelectRight snapshotPage = Render(cards, false);
            f.Check("LF50 snapshot contract", 2, ModMinecraft.McInstanceLock.IsLocked(sourceInstance) && !blocks && GroupCards(Main(snapshotPage)).Count(item => Equals(item.Tag, source)) == 1, "Snapshot lost write exclusion or hid its browseable source group.");
        }
        finally
        {
            Release(worker, sourceInstance);
        }

        object target = Native.Static("PCL.PageSelectRight", "ResolveDeleteTarget", cloneRow, cloneInstance)!;
        C(146, ReferenceEquals(target, cloneInstance), "Nested delete target did not come from its own row Tag.");
        Reject(147, () => Native.Static("PCL.PageSelectRight", "ResolveDeleteTarget", cloneRow, sourceInstance), "Mismatched delete target was accepted.");
        var pruned = (Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>>)Native.Static("PCL.PageSelectRight", "WithoutDeletedInstance", cards, clone)!;
        var all = pruned.Values.SelectMany(value => value).ToArray();
        C(150, !all.Any(item => CloneFixture.SamePath(item.PathVersion, clone)) && new[] { source, sibling, grandchild }.All(path => all.Any(item => CloneFixture.SamePath(item.PathVersion, path))), "Deleting one clone pruned a source, sibling or descendant.");
        C(151, Native.Static("PCL.PageSelectRight", "SelectionAfterDelete", cloneInstance, clone)is null, "Deleted selected clone was retained.");
        C(152, ReferenceEquals(Native.Static("PCL.PageSelectRight", "SelectionAfterDelete", sourceInstance, clone), sourceInstance), "Deleting a clone changed an unrelated selected source.");
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

    private static IEnumerable<MyCard> GroupCards(UIElement element)
    {
        if (element is MyCard card && card.Tag is not null)
            yield return card;
        if (element is Panel panel)
            foreach (UIElement child in panel.Children)
                foreach (MyCard value in GroupCards(child))
                    yield return value;
    }

    private static void Release(RedoableWorker worker, ModMinecraft.McInstance instance)
    {
        using ManualResetEventSlim stopped = new(false);
        // Attach after the real lease handler; Running=false/WaitIfRunning
        // are signaled before the worker invokes its Stopped event handlers.
        worker.Stopped += stopped.Set;
        worker.Start();
        if (!stopped.Wait(10000))
            CloneFixture.FailClosed("Actual fixture lease worker did not reach its real Stopped handler tail.");
        if (worker.Running || ModMinecraft.McInstanceLock.IsLocked(instance))
            CloneFixture.FailClosed("Actual fixture lease outlived its completed Stopped handler tail.");
    }

    private static void ClonePage(CloneFixture f, string versions, ModMinecraft.McInstance source, ModMinecraft.McInstance clone, ModMinecraft.McInstance orphan, ModMinecraft.McInstance bad)
    {
        const string script = "Test-InstanceCloneGroups.ps1";
        void C(int line, bool value, string message) => f.Check(script, line, value, message);
        ModMinecraft.McInstance? old = PageInstanceLeft.Instance;
        PageInstanceClone? page = null;
        bool subscribed = false;
        bool Busy() => (bool)Native.Property(Native.Type("PCL.InstanceClonerUi"), "IsBusy").GetValue(null)!;
        try
        {
            PageInstanceLeft.Instance = source;
            page = new();
            page.Refresh();
            foreach (string part in new[]
            {
                "TextName",
                "BtnClone",
                "BtnMigrate",
                "LabSource",
                "LabOrigin",
                "LabValidation",
                "CardOrigin",
                "CheckSaves",
                "CheckScreenshots",
                "CheckServers"
            }

            )
                C(72, page.FindName(part)is not null, "Clone page lost real named part: " + part);
            T Part<T>(string name)
                where T : FrameworkElement => (T)page.FindName(name);
            MyTextBox nameBox = Part<MyTextBox>("TextName");
            MyButton cloneButton = Part<MyButton>("BtnClone"), migrateButton = Part<MyButton>("BtnMigrate");
            MyCard originCard = Part<MyCard>("CardOrigin");
            TextBlock origin = Part<TextBlock>("LabOrigin"), validation = Part<TextBlock>("LabValidation"), label = Part<TextBlock>("LabSource");
            C(74, nameBox.Text == source.Name + " - 测试" && label.Text == "当前版本：" + source.Name && originCard.Visibility == Visibility.Collapsed && cloneButton.IsEnabled, "Clone page did not bind source/default name or hide missing origin.");
            C(75, nameBox.ValidateRules.Count == 1 && nameBox.ValidateRules[0].GetType().FullName == "PCL.ValidateFolderName" && CloneFixture.SamePath(((ValidateFolderName)nameBox.ValidateRules[0]).Folder, versions), "Clone page lost original fixture-scoped folder validator.");
            foreach (string part in new[]
            {
                "CheckSaves",
                "CheckScreenshots",
                "CheckServers"
            }

            )
                C(76, Part<MyCheckBox>(part).GetType().FullName == "PCL.MyCheckBox" && !Part<MyCheckBox>(part).Checked, "Clone options lost native two-state type/false default.");
            Part<MyCheckBox>("CheckSaves").Checked = true;
            Part<MyCheckBox>("CheckServers").Checked = true;
            nameBox.Text = source.Name;
            Native.Call(typeof(PageInstanceClone), "ValidateName", page);
            C(79, !cloneButton.IsEnabled && validation.Visibility == Visibility.Visible && validation.Text == "同名版本已存在，请使用新的测试版本名称。", "Clone page accepted an existing version name.");
            nameBox.Text = "invalid:name";
            Native.Call(typeof(PageInstanceClone), "ValidateName", page);
            C(81, !cloneButton.IsEnabled && !string.IsNullOrWhiteSpace(validation.Text), "Clone page accepted an invalid native folder name.");
            string[] before = Directory.GetDirectories(versions).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            Native.Call(typeof(PageInstanceClone), "CloneVersion", page);
            C(84, !Busy() && before.SequenceEqual(Directory.GetDirectories(versions).Order(StringComparer.OrdinalIgnoreCase)), "Invalid-name disabled action dispatched work or created a directory.");
            nameBox.Text = "ui-clone-valid";
            page.Refresh();
            C(86, cloneButton.IsEnabled && validation.Visibility == Visibility.Collapsed && nameBox.Text == "ui-clone-valid" && Part<MyCheckBox>("CheckSaves").Checked && !Part<MyCheckBox>("CheckScreenshots").Checked && Part<MyCheckBox>("CheckServers").Checked, "Same-owner refresh discarded user name/options.");
            PageInstanceLeft.Instance = clone;
            Native.Call(typeof(PageInstanceClone), "CloneVersion", page);
            C(89, !Busy() && !Directory.Exists(Path.Combine(versions, "ui-clone-valid")) && before.SequenceEqual(Directory.GetDirectories(versions).Order(StringComparer.OrdinalIgnoreCase)), "Stale-owner page dispatched work or created its former target.");
            page.Refresh();
            C(91, nameBox.Text == clone.Name + " - 测试" && originCard.Visibility == Visibility.Visible && origin.Text.StartsWith("来源版本：" + source.Name, StringComparison.Ordinal) && migrateButton.IsEnabled, "Changing owner lost actual clone origin card/default name.");
            PageInstanceLeft.Instance = orphan;
            page.Refresh();
            C(93, originCard.Visibility == Visibility.Collapsed, "Missing clone source remained actionable.");
            PageInstanceLeft.Instance = bad;
            page.Refresh();
            C(95, originCard.Visibility == Visibility.Visible && origin.Text == "副本来源记录不可用，无法自动迁移。请在资源管理器中检查版本目录。" && !migrateButton.IsEnabled, "Malformed origin lost original unavailable-source state.");
            PageInstanceLeft.Instance = source;
            subscribed = true;
            Native.Call(typeof(PageInstanceClone), "PageLoaded", page);
            nameBox.Text = "ui-retained-on-changed";
            Native.Static("PCL.InstanceClonerUi", "PublishChanged");
            C(99, nameBox.Text == "ui-retained-on-changed", "Changed subscription discarded same-owner user name.");
            PageInstanceLeft.Instance = clone;
            Native.Static("PCL.InstanceClonerUi", "PublishChanged");
            C(101, nameBox.Text == clone.Name + " - 测试" && label.Text == "当前版本：" + clone.Name, "Loaded handler failed to refresh actual Changed owner.");
            Native.Call(typeof(PageInstanceClone), "PageUnloaded", page);
            subscribed = false;
            PageInstanceLeft.Instance = source;
            Native.Static("PCL.InstanceClonerUi", "PublishChanged");
            C(104, label.Text == "当前版本：" + clone.Name, "Unloaded handler retained Changed subscription.");
        }
        finally
        {
            try
            {
                if (subscribed && page is not null)
                    Native.Call(typeof(PageInstanceClone), "PageUnloaded", page);
            }
            finally
            {
                PageInstanceLeft.Instance = old!;
            }
        }
    }
}
