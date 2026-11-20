using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCLCS;

public sealed class ModpackVersionSelectHost<TVersion, TItem> where TVersion : ResourceVersionModelData where TItem : Grid {
    public Func<TItem> CreateItem = null!;
    public Action<TItem, TVersion, string, string> Configure = null!;
    public Func<TItem, bool> Checked = null!;
    public Action<TItem, Action> OnCheck = null!, OnChanged = null!;
    public Action<TVersion?> Select = null!;
    public Action<int> SetChannel = null!;
    public Action SelectionChanged = null!;
}
public sealed class ModpackVersionSelectController<TVersion, TItem, TButton, TScroll>
    where TVersion : ResourceVersionModelData where TItem : Grid where TButton : FrameworkElement where TScroll : ScrollViewer {
    public readonly List<TVersion> Versions;
    private readonly List<TItem> selections;
    private readonly ModpackDialogController<TButton, TScroll> view;
    private readonly ModpackVersionSelectHost<TVersion, TItem> host;
    public ModpackVersionSelectController(ModpackDialogController<TButton, TScroll> view, IEnumerable<TVersion> items, List<TItem> selections, ModpackVersionSelectHost<TVersion, TItem> host) {
        this.view = view; this.host = host; this.selections = selections; Versions = items.OrderByDescending(item => item.ReleaseDate).ToList();
        view.Height = Math.Min(420, Math.Max(200, 100 + 72 * Versions.GroupBy(ChannelOf).Select(group => group.Count()).DefaultIfEmpty().Max()));
        foreach (int kind in new[] { 1, 2, 3 }) { int count = Versions.Count(item => ChannelOf(item) == kind); view.AddFilter(ChannelName(kind) + "（" + count + "）", kind, count > 0, ShowChannel); }
        ShowChannel(new[] { 1, 2, 3 }.FirstOrDefault(kind => Versions.Any(item => ChannelOf(item) == kind)));
    }
    public static int ChannelOf(TVersion version) => version.ReleaseTypeCode is 1 or 2 ? version.ReleaseTypeCode : 3;
    public static string ChannelName(int kind) => kind == 1 ? "正式版" : kind == 2 ? "测试版" : "早期测试版";
    public void ShowChannel(int kind) {
        host.SetChannel(kind); host.Select(null); selections.Clear(); host.SelectionChanged(); view.BeginFilter(kind);
        foreach (TVersion version in Versions.Where(item => ChannelOf(item) == kind)) {
            List<string> games = version.GameVersions ?? new List<string>();
            string gameText = games.Count == 0 ? "游戏版本未知" : string.Join("、", games.Take(4)) + (games.Count > 4 ? " 等" : "");
            string loader = version.ModLoaders == ModLoaders.None ? "" : " · " + version.ModLoaders;
            TItem item = host.CreateItem();
            host.Configure(item, version, "Minecraft " + gameText + loader + " · " + version.ReleaseDate.ToString("yyyy-MM-dd"), version.Display + "\r\nMinecraft " + string.Join("、", games) + loader);
            Border outline = new() { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            outline.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5"); Grid.SetColumnSpan(outline, 999); Grid.SetRowSpan(outline, 999); item.Children.Insert(0, outline);
            host.OnCheck(item, () => { host.Select(version); host.SelectionChanged(); });
            host.OnChanged(item, () => outline.SetResourceReference(Border.BorderBrushProperty, host.Checked(item) ? "ColorBrush3" : "ColorBrushGray5"));
            selections.Add(item); view.Rows.Children.Add(item);
        }
        if (selections.Count == 0) view.Rows.Children.Add(ModpackDialogController<TButton, TScroll>.Label("此分类没有其他可下载的版本。", 13, margin: new Thickness(8)));
    }
}
