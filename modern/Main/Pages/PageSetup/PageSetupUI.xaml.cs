using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using global::PCLCS;
using PCLCS;
using ThrottleDebounce;

namespace PCL
{
    // Original LF XAML fields/Handles and public entry points remain typed; business lives in C#.
    public partial class PageSetupUI
    {
        private static global::System.Boolean _HiddenForceShow = false;
        public PageSetupUI()
        {
            Initialized += (_, _) =>
            {
                ModAnimation.AniControlEnabled += 1;
                try { SettingService.RefreshSettings(ComboLauncherColorMode); }
                finally { ModAnimation.AniControlEnabled -= 1; }
            };
            __LfAttachOwnHandles();
            this.InitializeComponent();
            if (FormMain.GlassShellPreferred) {
                ((MyComboBoxItem)ComboBackgroundMode.Items[0]).Content = "自定义壁纸";
                ((MyComboBoxItem)ComboBackgroundMode.Items[1]).Content = "默认背景";
            }
            CheckHiddenPageFavorites.Change += (_, user) => {
                if (user) ModBase.RunInUi(() => HiddenRefresh(), true);
            };
        }

        internal static global::System.Boolean ReadHiddenForceShow()
        {
            return global::PCL.PageSetupUI._HiddenForceShow;
        }

        internal static void WriteHiddenForceShow(global::System.Boolean Value)
        {
            global::PCL.PageSetupUI._HiddenForceShow = Value;
        }

        private global::PCLCS.LegacyPageSetupUIController _controller;
        internal global::PCLCS.LegacyPageSetupUIController Controller
        {
            get
            {
                if (this._controller is null)
                {
                    this._controller = new global::PCLCS.LegacyPageSetupUIController(global::PCL.SetupPageRuntime.UI, new global::PCLCS.LegacyPageSetupUIView() { Page = new Func<global::System.Windows.DependencyObject>(() => this), PanBack = new Func<global::System.Windows.Controls.ScrollViewer>(() => this.PanBack), PanMain = new Func<global::System.Windows.Controls.StackPanel>(() => this.PanMain), CardLauncher = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardLauncher), SliderLauncherTransparent = new Func<global::PCLLF.Presentation.Controls.MySliderView>(() => this.SliderLauncherTransparent), SliderPanelOpacity = () => SliderPanelOpacity, PanOfficialTheme = new Func<global::System.Windows.Controls.Border>(() => this.PanOfficialTheme), BtnOfficialTheme = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnOfficialTheme), CardBackground = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardBackground), ComboBackgroundMode = new Func<global::PCLLF.Presentation.Controls.MyComboBoxView>(() => this.ComboBackgroundMode), PanBackgroundSuit = new Func<global::System.Windows.Controls.Grid>(() => this.PanBackgroundSuit), PanBackgroundOpacity = new Func<global::System.Windows.Controls.Grid>(() => this.PanBackgroundOpacity), SliderBackgroundOpacity = new Func<global::PCLLF.Presentation.Controls.MySliderView>(() => this.SliderBackgroundOpacity), PanBackgroundBlur = new Func<global::System.Windows.Controls.Grid>(() => this.PanBackgroundBlur), SliderBackgroundBlur = new Func<global::PCLLF.Presentation.Controls.MySliderView>(() => this.SliderBackgroundBlur), CheckBackgroundColorful = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckBackgroundColorful), BtnBackgroundOpen = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnBackgroundOpen), BtnBackgroundRefresh = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnBackgroundRefresh), BtnBackgroundClear = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnBackgroundClear), CardMusic = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardMusic), PanMusicVolume = new Func<global::System.Windows.Controls.Grid>(() => this.PanMusicVolume), SliderMusicVolume = new Func<global::PCLLF.Presentation.Controls.MySliderView>(() => this.SliderMusicVolume), PanMusicDetail = new Func<global::System.Windows.Controls.StackPanel>(() => this.PanMusicDetail), CheckMusicRandom = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckMusicRandom), CheckMusicAuto = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckMusicAuto), CheckMusicStart = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckMusicStart), CheckMusicStop = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckMusicStop), BtnMusicOpen = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnMusicOpen), BtnMusicRefresh = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnMusicRefresh), BtnMusicClear = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnMusicClear), CardLogo = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardLogo), RadioLogoType0 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioLogoType0), RadioLogoType1 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioLogoType1), RadioLogoType2 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioLogoType2), RadioLogoType3 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioLogoType3), CheckLogoLeft = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckLogoLeft), PanLogoText = new Func<global::System.Windows.Controls.Grid>(() => this.PanLogoText), TextLogoText = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextLogoText), PanLogoChange = new Func<global::System.Windows.Controls.Grid>(() => this.PanLogoChange), BtnLogoChange = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnLogoChange), BtnLogoDelete = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnLogoDelete), CardCustom = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardCustom), RadioCustomType0 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioCustomType0), RadioCustomType3 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioCustomType3), RadioCustomType1 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioCustomType1), RadioCustomType2 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioCustomType2), HintCustom = new Func<global::PCLLF.Presentation.Controls.MyHintView>(() => this.HintCustom), HintCustomWarn = new Func<global::PCLLF.Presentation.Controls.MyHintView>(() => this.HintCustomWarn), PanCustomLocal = new Func<global::System.Windows.Controls.WrapPanel>(() => this.PanCustomLocal), BtnCustomRefresh = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnCustomRefresh), BtnCustomFile = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnCustomFile), BtnCustomTutorial = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnCustomTutorial), BtnCustomFolder = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnCustomFolder), PanCustomNet = new Func<global::System.Windows.Controls.Grid>(() => this.PanCustomNet), TextCustomNet = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextCustomNet), PanCustomPreset = new Func<global::System.Windows.Controls.Grid>(() => this.PanCustomPreset), CheckRecentWorlds = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckRecentWorlds), CardSwitch = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardSwitch), CheckHiddenPageDownload = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenPageDownload), CheckHiddenPageLink = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenPageLink), CheckHiddenPageSetup = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenPageSetup), CheckHiddenPageOther = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenPageOther), CheckHiddenSetupLaunch = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenSetupLaunch), CheckHiddenSetupLink = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenSetupLink), CheckHiddenSetupUI = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenSetupUI), CheckHiddenSetupSystem = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenSetupSystem), CheckHiddenOtherHelp = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenOtherHelp), CheckHiddenOtherAbout = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenOtherAbout), CheckHiddenOtherTest = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenOtherTest), CheckLauncherEmail = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckLauncherEmail), CheckHiddenFunctionSelect = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenFunctionSelect), CheckHiddenFunctionModUpdate = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenFunctionModUpdate), CheckHiddenFunctionHidden = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenFunctionHidden), CheckHiddenFunctionModify = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckHiddenFunctionModify) });
                }

                return this._controller;
            }
        }

        private void PageSetupUI_Loaded(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            this.Controller.Loaded();
        }

        public void Refresh()
        {
            this.Controller.Refresh();
        }

        public void Reset()
        {
            this.Controller.Reset();
        }

        private void BtnUIBgOpen_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.OpenBackground();
        }

        private void BtnBackgroundRefresh_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.RefreshBackground();
        }

        public void BackgroundRefreshUI(global::System.Boolean Show, global::System.Int32 Count)
        {
            this.Controller.BackgroundRefreshUI(Show, Count);
        }

        private void BtnBackgroundClear_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.ClearBackground();
        }

        public static void BackgroundRefresh(global::System.Boolean IsHint, global::System.Boolean Refresh)
        {
            global::PCL.SetupPageRuntime.UI.BackgroundRefresh(IsHint, Refresh);
        }

        private void BtnLogoChange_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.ChangeLogo();
        }

        private void RadioLogoType3_Check(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Controller.PreviewCustomLogo(e.RaiseByMouse, ref e.Handled);
        }

        private void BtnLogoDelete_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.DeleteLogo();
        }

        private void BtnMusicOpen_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.OpenMusic();
        }

        private void BtnMusicRefresh_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.RefreshMusic();
        }

        public void MusicRefreshUI()
        {
            this.Controller.MusicRefreshUI();
        }

        private void BtnMusicClear_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.ClearMusic();
        }

        private void CheckMusicStart_Change()
        {
            this.Controller.MusicStartChanged();
        }

        private void CheckMusicStop_Change()
        {
            this.Controller.MusicStopChanged();
        }

        private void BtnCustomFile_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.CreateCustomFile();
        }

        private void BtnCustomRefresh_Click()
        {
            this.Controller.RefreshCustom();
        }

        private void BtnCustomTutorial_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.CustomTutorial();
        }

        public static void OnMainPageTypeChanged()
        {
            global::PCL.SetupPageRuntime.UI.OnMainPageTypeChanged();
        }

        private void BtnCustomFolder_Click(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Controller.OpenCustomFolder();
        }

        public static void HiddenRefresh()
        {
            global::PCL.SetupPageRuntime.UI.HiddenRefresh();
        }

        private void HiddenSetupMain()
        {
            this.Controller.HiddenSetupMain();
        }

        private void HiddenSetupSub()
        {
            this.Controller.HiddenSetupSub();
        }

        private void HiddenOtherMain()
        {
            this.Controller.HiddenOtherMain();
        }

        private void HiddenOtherSub(global::System.Object sender, global::System.Boolean user)
        {
            this.Controller.HiddenOtherSub(sender, user);
        }

        private void HiddenHint(global::System.Object sender, global::System.Boolean user)
        {
            this.Controller.HiddenHint(sender, user);
        }

        private void SliderLoad()
        {
            this.Controller.SliderLoad();
        }

        public static bool HiddenForceShow
        {
            get
            {
                return global::PCL.SetupPageRuntime.UI.HiddenForceShow;
            }

            set
            {
                global::PCL.SetupPageRuntime.UI.HiddenForceShow = value;
            }
        }
    }
}
