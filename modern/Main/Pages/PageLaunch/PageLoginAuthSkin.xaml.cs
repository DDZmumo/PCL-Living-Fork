using System;
using System.Windows;
using System.Windows.Input;
using PCLLF.Presentation.Accounts;

namespace PCL
{
    public partial class PageLoginAuthSkin
    {
        private readonly LegacyLoggedInPage<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer> Native;
        public PageLoginAuthSkin()
        {
            __LfAttachOwnHandles();
            Native = CreateController(this);
            this.InitializeComponent();
            this.Skin.Loader = PageLaunchLeft.SkinAuth;
        }

        private static LegacyLoggedInPage<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer> CreateController(PageLoginAuthSkin Page)
        {
            return new LegacyLoggedInPage<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer>(LoginPageRuntime.Accounts, new LegacyLoggedInView() { SetName = new Action<string>(Value => Page.TextName.Text = Value), StartSkin = new Action(() => Page.Skin.Loader.Start()), EditButton = new Func<FrameworkElement>(() => Page.BtnEdit), ExitButton = new Func<FrameworkElement>(() => Page.BtnExit), SetEmail = new Action<string>(Value => Page.TextEmail.Text = Value), ShowEmail = new Action<bool>(Value => Page.TextEmail.Visibility = Value ? Visibility.Visible : Visibility.Collapsed), SaveSkin = new Action(() => Page.Skin.BtnSkinSave_Click()), RefreshSkin = new Action(() => Page.Skin.RefreshClick()), ChangeCape = new Action(() => Page.Skin.BtnSkinCape_Click()) }, "Auth");
        }

        private void PageLoginLegacy_Loaded()
        {
            Native.Loaded();
        }

        public void Reload(bool KeepInput)
        {
            if (GlassSkinSites.CurrentForLaunch(ModMinecraft.McInstanceSelected) is { } character) {
                TextName.Text = character.Profile.Name;
                TextEmail.Text = character.Account.Username;
                TextEmail.Visibility = Settings.Get<bool>("UiLauncherEmail") ? Visibility.Collapsed : Visibility.Visible;
                Skin.Loader.Start();
                return;
            }
            Native.Reload(KeepInput);
        }

        public static ModLaunch.McLoginServer GetLoginData()
        {
            return CreateController(ModMain.FrmLoginAuthSkin).GetServerLoginData();
        }

        private void PageLoginAuthSkin_MouseEnter(object sender, MouseEventArgs e)
        {
            Native.ShowPanel();
        }

        private void PageLoginAuthSkin_MouseLeave(object sender, MouseEventArgs e)
        {
            Native.HidePanel();
        }

        private void BtnEdit_Click(object sender, EventArgs e)
        {
            if (GlassSkinSites.CurrentForLaunch(ModMinecraft.McInstanceSelected) is not null) return;
            Native.EditClick(GetLoginData, () => Reload(true));
        }

        public static void ExitLogin()
        {
            if (GlassSkinSites.CurrentForLaunch(ModMinecraft.McInstanceSelected) is not null) {
                ModLaunch.McLoginLoader.Cancel();
                ModLaunch.McLoginAuthLoader.Cancel();
                GlassSkinSites.Deactivate();
                ModMain.FrmLaunchLeft?.RefreshPage(false, true);
                return;
            }
            CreateController(ModMain.FrmLoginAuthSkin).ExitLogin();
        }

        private void Skin_Click(object sender, MouseButtonEventArgs e)
        {
            if (GlassSkinSites.CurrentForLaunch(ModMinecraft.McInstanceSelected) is { } character) {
                if (Uri.TryCreate(character.Site.Register, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) ModBase.OpenWebsite(uri.AbsoluteUri);
                return;
            }
            Native.SkinClick();
        }
    }
}
