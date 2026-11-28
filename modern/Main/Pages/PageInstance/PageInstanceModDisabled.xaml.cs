using System;
using PCLCS;

namespace PCL
{
    public partial class PageInstanceModDisabled
    {
        private InstanceModDisabledController Controller
        {
            get
            {
                return new InstanceModDisabledController()
                {
                    Download = BtnDownload,
                    Version = BtnVersion,
                    Modpack = new Func<bool>(() => ModModpackMode.Enabled),
                    DownloadHidden = new Func<bool>(() => Settings.Get<bool>("UiHiddenPageDownload")),
                    HiddenForceShow = new Func<bool>(() => LauncherAppearanceSettings.HiddenForceShow),
                    SelectShowHidden = () => ModMain.FrmMain?.ShowHiddenInstances == true,
                    DownloadPage = new Action(() => ModMain.FrmMain.PageChange((FormMain.PageStackData)FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall)),
                    LaunchPage = new Action(() => ModMain.FrmMain.PageChange((FormMain.PageStackData)FormMain.PageType.Launch)),
                    SelectPage = new Action(() => ModMain.FrmMain.PageChange((FormMain.PageStackData)FormMain.PageType.InstanceSelect)),
                    Resize = PanMain.InvalidateMeasure
                };
            }
        }

        private void BtnDownload_Click(object sender, EventArgs e)
        {
            Controller.OpenDownload();
        }

        private void BtnVersion_Click(object sender, EventArgs e)
        {
            Controller.OpenVersions();
        }

        public void BtnDownload_Loaded()
        {
            Controller.Loaded();
        }

        public PageInstanceModDisabled()
        {
            this.InitializeComponent();
            BtnDownload.Click += BtnDownload_Click;
            BtnVersion.Click += BtnVersion_Click;
            Loaded += (_, _) => BtnDownload_Loaded();
        }
    }
}
