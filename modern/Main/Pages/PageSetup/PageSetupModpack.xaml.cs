// SPDX-License-Identifier: MPL-2.0
using System.Windows;

namespace PCL;

public partial class PageSetupModpack : GlassWorkspacePage
{
    private bool _refreshing;

    public PageSetupModpack()
    {
        InitializeComponent();
        AttachScroll(PanBack);
        AttachPlaceholder(TextSystemCache, "默认");
        SettingService.BindNativeControls(this);
        Loaded += (_, _) => PageLoaded();
        CheckModpackMode.Checked += ModeChanged;
        CheckModpackMode.Unchecked += ModeChanged;
        TextSystemCache.TextChanged += (_, _) => {
            if (IsLoaded && !_refreshing && ModAnimation.AniControlEnabled == 0 && TextSystemCache.IsValidated)
                ModMain.Hint("缓存文件夹将在重启启动器后生效。", Log: false);
        };
    }

    private void PageLoaded()
    {
        _refreshing = true;
        try
        {
            LabVersion.Text = ModBase.LauncherDisplayName + " " + ModBase.VersionBaseName;
            SettingService.RefreshSettings(this);
            CheckModpackMode.IsChecked = ModModpackMode.Enabled;
            PanBack.ScrollToTop();
        }
        finally { _refreshing = false; }
    }

    private void ModeChanged(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded || _refreshing) return;
        _refreshing = true;
        try { ModModpackMode.Toggle(CheckModpackMode); }
        finally { _refreshing = false; }
    }
}
