using System.Windows;
using PCLCS;

namespace PCL;

// Original LF host integration; repository LICENCE applies.
public partial class PageInstanceOverall {
    private void RefreshMmcActions() {
        // ContextStamp is already attached by the normal instance reader. Avoid
        // re-reading settings with side effects merely to decide button visibility.
        BtnMmcLegacyAdoption.Visibility = LauncherInstanceContext.Instance?.Config.ContextStamp.Kind == MmcConfigurationContextKind.Unmanaged ? Visibility.Visible : Visibility.Collapsed;
    }
    private void BtnMmcLegacyAdoption_Click(object sender, RoutedEventArgs e) {
        try { MmcLegacyAdoptionUi.PromptCreateCopy(LauncherInstanceContext.Instance); }
        catch (System.Exception error) { MeloongCore.Logger.Error(error, "无法开始创建 MMC 管理副本", MeloongCore.LogBehavior.Alert); }
    }
}
