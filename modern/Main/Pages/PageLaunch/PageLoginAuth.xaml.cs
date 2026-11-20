using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.CompilerServices;
using PCLLF.Presentation.Accounts;

namespace PCL
{
    public partial class PageLoginAuth
    {
        private readonly LegacyServerLoginPage<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer> Native;
        public PageLoginAuth()
        {
            __LfAttachOwnHandles();
            Native = CreateController(this);
            this.InitializeComponent();
        }

        private static LegacyServerLoginPage<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer> CreateController(PageLoginAuth Page)
        {
            return new LegacyServerLoginPage<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer>(LoginPageRuntime.Accounts, new LegacyServerLoginView() { Name = new Func<string>(() => Page.ComboName.Text), Password = new Func<string>(() => Page.TextPass.Password), SetName = new Action<string>(Value => Page.ComboName.Text = Value), SetPassword = new Action<string>(Value => Page.TextPass.Password = Value), SetNames = new Action<IEnumerable<string>>(Value => Page.ComboName.ItemsSource = Value), SetRemember = new Action<bool>(Value => Page.CheckRemember.Checked = Value), LinkContent = new Func<object>(() => Page.BtnLink.Content), SetLinkContent = new Action<string>(Value => Page.BtnLink.Content = Value), ShowRegister = new Action<bool>(Value => Page.BtnLink.Visibility = Value ? Visibility.Visible : Visibility.Collapsed) }, true);
        }

        public void Reload(bool KeepInput)
        {
            Native.Reload(KeepInput);
        }

        public static ModLaunch.McLoginServer GetLoginData()
        {
            var page = ModMain.FrmLoginAuth;
            return (page?.Native ?? CreateController(null)).GetLoginData(() => page is not null, () => page.ComboName.Text, () => page.TextPass.Password);
        }

        public static string IsVaild(ModLaunch.McLoginServer LoginData)
        {
            return CreateController(ModMain.FrmLoginAuth).IsVaild(LoginData);
        }

        public string IsVaild()
        {
            return IsVaild(GetLoginData());
        }

        private void ComboName_TextChanged(object sender, TextChangedEventArgs e)
        {
            Native.NameChanged(Conversions.ToString(((dynamic)sender).Text));
        }

        private void TextPass_PasswordChanged(object sender, RoutedEventArgs e)
        {
            Native.PasswordChanged();
        }

        private void ComboName_SelectionChanged(MyComboBox sender, SelectionChangedEventArgs e)
        {
            Native.SelectionChanged(sender.SelectedIndex);
        }

        private void CheckBoxChange(MyCheckBox sender, object e)
        {
            Native.RememberChanged(Conversions.ToString(sender.Tag), sender.Checked);
        }

        private void ComboName_TextChanged()
        {
            Native.UpdateLinkText();
        }

        private void Btn_Click(object sender, EventArgs e)
        {
            Native.LinkClick();
        }

        private void ReloadRegisterButton()
        {
            Native.ReloadRegisterButton();
        }
    }
}
