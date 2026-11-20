using System;
using System.Windows;
namespace PCLCS;
public sealed class InstanceModDisabledController {
    public FrameworkElement Download = null!, Version = null!;
    public Func<bool> Modpack = null!, DownloadHidden = null!, HiddenForceShow = null!, SelectShowHidden = null!;
    public Action DownloadPage = null!, LaunchPage = null!, SelectPage = null!, Resize = null!;
    public void OpenDownload() => DownloadPage();
    public void OpenVersions() { LaunchPage(); SelectPage(); }
    public void Loaded() {
        Visibility next = Modpack() || DownloadHidden() && !HiddenForceShow() || SelectShowHidden() ? Visibility.Collapsed : Visibility.Visible;
        Version.Visibility = Modpack() ? Visibility.Collapsed : Visibility.Visible;
        if (Download.Visibility != next) { Download.Visibility = next; Resize(); }
    }
}
