using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCL;

// Original LF view; repository LICENCE applies.
internal sealed class MmcProjectionReviewView : Grid {
    private const int MaximumDisplayedCharacters = 131072;
    internal double PreferredWidth => 760;
    internal MmcProjectionReviewView(MmcProjectionDivergence inspected) {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var note = new TextBlock { Text = "重建依据是保存的组件与补丁；当前 JSON 不会变成作者来源。\n当前文件副本：" + inspected.BackupRelativePath,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        Children.Add(note); SetColumnSpan(note, 2);
        string expected = new JObject { ["mainClass"] = inspected.ExpectedMainClass, ["jvmArguments"] = new JArray(inspected.ExpectedJvmArguments),
            ["gameArguments"] = new JArray(inspected.ExpectedGameArguments), ["classpath"] = new JArray(inspected.ExpectedClasspath),
            ["publishedJsonSha256"] = inspected.ExpectedProjectionSha256 }.ToString();
        AddColumn("保存的定义", expected, 0);
        AddColumn("当前外部修改的 JSON", inspected.ObservedJson, 1);
    }
    private void AddColumn(string caption, string value, int column) {
        var panel = new DockPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 0) };
        var title = new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Bold };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        if (value.Length > MaximumDisplayedCharacters) {
            value = value[..MaximumDisplayedCharacters] + "\n…显示内容已截断；完整当前文件会按摘要保存在上述私有副本中。";
        }
        panel.Children.Add(new MyTextBox { Text = value, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220, MaxHeight = 420 });
        Children.Add(panel); SetRow(panel, 1); SetColumn(panel, column);
    }
}
