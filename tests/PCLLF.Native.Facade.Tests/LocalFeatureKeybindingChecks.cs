using System.Collections;
using System.Windows;
using System.Windows.Controls;
using PCLLF.Wpf.Glass;
using PCL;
using static PCLLF.Native.Facade.Tests.LocalFeatureChecks;

namespace PCLLF.Native.Facade.Tests;
internal static class LocalFeatureKeybindingChecks
{
    private static void K(int line, bool value, string? detail = null) => Check("groups", line, value, detail);
    internal static void Run(Scope scope)
    {
        PageInstanceKeybindings page = new(); Type binding = Native.Type("PCL.InstanceKeybinding");
        var entries = Native.Property(page.GetType(), "Entries");
        Type expected = typeof(List<>).MakeGenericType(binding); K(83, entries.PropertyType == expected);
        IList items = (IList)Activator.CreateInstance(expected)!;
        foreach (var (name, key, count) in new[] { ("保存快捷栏", "c", 3), ("复制位置", "c", 3), ("调试崩溃", "c", 3), ("投影菜单", "v", 2), ("地图菜单", "v", 2), ("打开物品栏", "e", 1) }) {
            object item = Activator.CreateInstance(binding, true)!;
            Native.Set(item, "Key", "key_fixture." + items.Count); Native.Set(item, "Name", name); Native.Set(item, "Owners", "Minecraft"); Native.Set(item, "Value", "key.keyboard." + key); Native.Set(item, "Normalized", "key.keyboard." + key); Native.Set(item, "SharedCount", count); items.Add(item);
        }
        entries.SetValue(page, items); Native.Call(page.GetType(), "RenderList", page);
        StackPanel all = Part<StackPanel>(page, "PanBindings");
        K(93, all.Children.Count == 6 && !all.Children.OfType<GlassSurface>().Any());
        static Grid Columns(Panel row) => (Grid)((Border)row.Children[0]).Child;
        for (int i = 0; i < items.Count; i++) K(94, ((TextBlock)Columns((Panel)all.Children[i]).Children.OfType<StackPanel>().Single().Children[0]).Text == (string)Native.Get(items[i]!, "Name")!, "index=" + i);
        Part<RadioButton>(page, "BtnFilterConflicts").IsChecked = true;
        StackPanel[] groups = all.Children.OfType<StackPanel>().ToArray(); K(97, groups.Length == 2 && all.Children.Count == 2);
        static string Title(StackPanel group) => ((TextBlock)group.Children[0]).Text;
        StackPanel c = groups.Single(group => Title(group).StartsWith('C')); K(99, Title(c).Contains("与 2 项共用", StringComparison.Ordinal));
        var rows = (StackPanel)c.Children[1];
        K(101, rows.Children.Count == 3 && ((IDictionary)Native.Property(page.GetType(), "KeyButtons").GetValue(page)!).Count == 5);
        foreach (Panel row in rows.Children) K(102, Columns(row).Children.OfType<Button>().Count(button => Grid.GetColumn(button) == 3) == 1);
        Part<TextBox>(page, "SearchBox").Text = "复制位置"; groups = all.Children.OfType<StackPanel>().ToArray();
        K(105, groups.Length == 1 && Title(groups[0]).Contains("与 2 项共用", StringComparison.Ordinal));
        Part<TextBox>(page, "SearchBox").Text = ""; Part<RadioButton>(page, "BtnFilterConflicts").IsChecked = true;
        K(107, all.Children.Count == 2);
        try { LocalFeatureControllerChecks.Run(Path.Combine(scope.Root, "controller")); K(114, true); }
        finally { Native.Call(page.GetType(), "PageUnloaded", page); }
        Console.WriteLine("PASS local-features keybinding groups: actual Main native row/card/button identities, complete original ordering/search/group counts plus every original controller Require assertion.");
    }
}
