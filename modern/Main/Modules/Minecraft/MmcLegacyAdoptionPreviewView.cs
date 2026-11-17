using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PCLCS;

namespace PCL;

// Original LF host presentation; repository LICENCE applies.
internal sealed class MmcLegacyAdoptionPreviewView : StackPanel {
    internal MmcLegacyAdoptionPreviewView(MmcLegacyAdoptionPreview preview, string targetName) {
        Width = 720;
        Children.Add(new TextBlock { Text = "创建 MMC 管理副本：" + targetName + "\n旧实例、存档和设置会保留；转换前另建私有恢复备份。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        Children.Add(new TextBlock { Text = preview.SourceCheck.Explanation, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        Children.Add(new TextBlock { Text = "旧组件：" + preview.SourceCheck.OldComponents + "\n新组件：" + preview.SourceCheck.NewComponents,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 100, Margin = new(0, 0, 0, 10) });
        var list = new DataGrid { Height = 300, IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, ItemsSource = preview.SourceCheck.Differences };
        list.Columns.Add(new DataGridTextColumn { Header = "游戏文件", Binding = new Binding(nameof(MmcLegacyAdoptionDifference.RelativePath)), Width = new(1, DataGridLengthUnitType.Star) });
        list.Columns.Add(new DataGridTextColumn { Header = "差异", Binding = new Binding(nameof(MmcLegacyAdoptionDifference.Kind)), Width = 125 });
        list.Columns.Add(new DataGridTextColumn { Header = "当前字节", Binding = new Binding(nameof(MmcLegacyAdoptionDifference.CurrentBytes)), Width = 100 });
        list.Columns.Add(new DataGridTextColumn { Header = "原包字节", Binding = new Binding(nameof(MmcLegacyAdoptionDifference.AuthorBytes)), Width = 100 });
        Children.Add(list);
        Children.Add(new TextBlock { Text = "当前游戏文件按个人快照保留，包括已删除状态。旧启动 JSON、PCL 私有记录和日志仅保留在私有备份；新启动定义来自本次选择的原 MMC ZIP。运行兼容性仍需实际启动验证。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) });
    }
}
