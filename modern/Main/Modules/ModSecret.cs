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
using PCLCS;
using ThemeSlot = global::PCLLF.Presentation.Foundation.LegacyThemeColorSlot<global::PCL.ModBase.MyColor>;
using ThemeHost = global::PCLLF.Presentation.Foundation.LegacyThemeHost<global::PCL.ModBase.MyColor>;
using ThemeService = global::PCLLF.Presentation.Foundation.LegacyThemeService<global::PCL.ModBase.MyColor>;
using ThrottleDebounce;

namespace PCL
{

    // 由于包含加解密等安全信息，本文件中的部分代码已被删除

    internal static class ModSecret
    {

        // 标注 PCL 的不同分支，仅用于替换标记
        public const global::System.String VersionBranchMain = "OpenSource";
        // 在开源版的注册表与常规版的注册表隔离，以防数据冲突
        public const global::System.String RegFolder = "PCLDebug";
        // 用于微软登录的 ClientId
        public static string OAuthClientId
        {
            get
            {
                return global::PCL.BuildCredentials.ReadMicrosoftClientId();
            }
        }
        // CurseForge API Key
        public static string CurseForgeAPIKey
        {
            get
            {
                return global::PCL.BuildCredentials.ReadCurseForgeKey();
            }
        }

        internal static global::System.String SecretCdnSign(global::System.String UrlWithMark)
        {
            return global::PCLCS.LegacySecretNetwork.CdnSign(UrlWithMark);
        }
        internal static void SecretHeadersSign(global::System.String Url, ref global::System.Net.Http.HttpRequestMessage Req, global::System.Boolean SimulateBrowserHeaders = false, global::System.Boolean IncludeCredentials = true)
        {
            global::PCL.ModSecret.NetworkSigner.SignHeaders(Url, ref Req, SimulateBrowserHeaders, IncludeCredentials);
        }
        // 固定默认外观；旧自定义主题配置保留存储，不在本版本启用调色。
        public const global::System.Int32 DefaultThemeHue = 225;
        public const global::System.Int32 DefaultThemeSat = 100;
        public const global::System.Int32 DefaultThemeDelta = 115;
        public const global::System.Int32 DefaultThemeLight = 40;
        public const global::System.Int32 DefaultBackgroundOpacity = 748;

        public static global::PCL.ModBase.MyColor Color1 = new global::PCL.ModBase.MyColor(52d, 61d, 74d);
        public static global::PCL.ModBase.MyColor Color2 = new global::PCL.ModBase.MyColor(11d, 91d, 203d);
        public static global::PCL.ModBase.MyColor Color3 = new global::PCL.ModBase.MyColor(19d, 112d, 243d);
        public static global::PCL.ModBase.MyColor Color4 = new global::PCL.ModBase.MyColor(72d, 144d, 245d);
        public static global::PCL.ModBase.MyColor Color5 = new global::PCL.ModBase.MyColor(150d, 192d, 249d);
        public static global::PCL.ModBase.MyColor Color6 = new global::PCL.ModBase.MyColor(213d, 230d, 253d);
        public static global::PCL.ModBase.MyColor Color7 = new global::PCL.ModBase.MyColor(222d, 236d, 253d);
        public static global::PCL.ModBase.MyColor Color8 = new global::PCL.ModBase.MyColor(234d, 242d, 254d);
        public static global::PCL.ModBase.MyColor ColorBg0 = new global::PCL.ModBase.MyColor(150d, 192d, 249d);
        public static global::PCL.ModBase.MyColor ColorBg1 = new global::PCL.ModBase.MyColor(190d, global::PCL.ModSecret.Color7);
        public static global::PCL.ModBase.MyColor ColorGray1 = new global::PCL.ModBase.MyColor(64d, 64d, 64d);
        public static global::PCL.ModBase.MyColor ColorGray2 = new global::PCL.ModBase.MyColor(115d, 115d, 115d);
        public static global::PCL.ModBase.MyColor ColorGray3 = new global::PCL.ModBase.MyColor(140d, 140d, 140d);
        public static global::PCL.ModBase.MyColor ColorGray4 = new global::PCL.ModBase.MyColor(166d, 166d, 166d);
        public static global::PCL.ModBase.MyColor ColorGray5 = new global::PCL.ModBase.MyColor(204d, 204d, 204d);
        public static global::PCL.ModBase.MyColor ColorGray6 = new global::PCL.ModBase.MyColor(235d, 235d, 235d);
        public static global::PCL.ModBase.MyColor ColorGray7 = new global::PCL.ModBase.MyColor(240d, 240d, 240d);
        public static global::PCL.ModBase.MyColor ColorGray8 = new global::PCL.ModBase.MyColor(245d, 245d, 245d);
        public static global::PCL.ModBase.MyColor ColorSemiTransparent = new global::PCL.ModBase.MyColor(1d, global::PCL.ModSecret.Color8);

        public static global::System.Int32 ColorHue = global::PCL.ModSecret.DefaultThemeHue;
        public static global::System.Int32 ColorSat = global::PCL.ModSecret.DefaultThemeSat;
        public static global::System.Int32 ColorLightAdjust = ((global::PCL.ModSecret.DefaultThemeLight) - (20));
        public static global::MeloongCore.OneOf<global::System.Int32, global::System.Int32[]> ColorHueTopbarDelta = ((global::PCL.ModSecret.DefaultThemeDelta) - (90));


        private readonly static global::PCLCS.LegacySecretNetwork NetworkSigner = new global::PCLCS.LegacySecretNetwork(new global::PCLCS.LegacySecretNetworkHost()
        {
            VersionName = new Func<global::System.String>(() => global::PCL.ModBase.VersionBaseName),
            BuildType = new Func<global::System.Int32>(() => (global::System.Int32)global::PCL.ModBase.BuildType),
            VersionCode = new Func<global::System.Int32>(() => global::PCL.ModBase.VersionCode),
            CurseForgeKey = new Func<global::System.String>(() => global::PCL.ModSecret.CurseForgeAPIKey),
            IsCurseForgeRequest = global::PCL.BuildCredentials.IsCurseForgeRequest
        });
        private readonly static global::PCLCS.LegacyDeviceIdentify IdentityService = new global::PCLCS.LegacyDeviceIdentify(new global::PCLCS.LegacyDeviceIdentifyHost()
        {
            ApplicationStartTick = new Func<global::System.Int64>(() => global::PCL.ModBase.ApplicationStartTick),
            Milliseconds = global::PCL.ModBase.GetTimeMs,
            AvailablePhysicalMemory = new Func<global::System.UInt64>(() => global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory),
            ReadHardware = new Func<global::System.Object>(() => global::MeloongCore.RegistryUtils.TryRead(@"HKEY_LOCAL_MACHINE\SYSTEM\HardwareConfig", "LastConfig", "Unknown")),
            ReadSeed = new Func<global::System.String>(() => global::PCL.Settings.Get<string>("Identify")),
            WriteSeed = new Action<global::System.String>(Value => global::PCL.Settings.Set("Identify", Value)),
            SourceIdentify = global::PCL.Settings.GetPclSourceIdentify,
            Warn = new Action<global::System.String>(Text => global::MeloongCore.Logger.Warn(Text))
        });
        internal readonly static global::PCLLF.Presentation.Foundation.LegacyThemeService<global::PCL.ModBase.MyColor> PaletteService = new global::PCLLF.Presentation.Foundation.LegacyThemeService<global::PCL.ModBase.MyColor>(global::PCL.ModSecret.CreateThemeHost());
        private static global::PCLLF.Presentation.Foundation.LegacyThemeColorSlot<global::PCL.ModBase.MyColor> ThemeField(global::System.Func<global::PCL.ModBase.MyColor> Read, global::System.Action<global::PCL.ModBase.MyColor> Write)
        {
            return new global::PCLLF.Presentation.Foundation.LegacyThemeColorSlot<global::PCL.ModBase.MyColor>() { Read = Read, Write = Write };
        }
        private static global::PCLLF.Presentation.Foundation.LegacyThemeHost<global::PCL.ModBase.MyColor> CreateThemeHost()
        {
            return new global::PCLLF.Presentation.Foundation.LegacyThemeHost<global::PCL.ModBase.MyColor>()
            {
                Color1 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color1, Value => global::PCL.ModSecret.Color1 = Value),
                Color2 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color2, Value => global::PCL.ModSecret.Color2 = Value),
                Color3 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color3, Value => global::PCL.ModSecret.Color3 = Value),
                Color4 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color4, Value => global::PCL.ModSecret.Color4 = Value),
                Color5 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color5, Value => global::PCL.ModSecret.Color5 = Value),
                Color6 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color6, Value => global::PCL.ModSecret.Color6 = Value),
                Color7 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color7, Value => global::PCL.ModSecret.Color7 = Value),
                Color8 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.Color8, Value => global::PCL.ModSecret.Color8 = Value),
                Bg0 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorBg0, Value => global::PCL.ModSecret.ColorBg0 = Value),
                Bg1 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorBg1, Value => global::PCL.ModSecret.ColorBg1 = Value),
                Gray1 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray1, value => global::PCL.ModSecret.ColorGray1 = value),
                Gray2 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray2, value => global::PCL.ModSecret.ColorGray2 = value),
                Gray3 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray3, value => global::PCL.ModSecret.ColorGray3 = value),
                Gray5 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray5, value => global::PCL.ModSecret.ColorGray5 = value),
                Gray6 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray6, value => global::PCL.ModSecret.ColorGray6 = value),
                Gray7 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray7, value => global::PCL.ModSecret.ColorGray7 = value),
                Gray8 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray8, value => global::PCL.ModSecret.ColorGray8 = value),
                Gray4 = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorGray4, Value => global::PCL.ModSecret.ColorGray4 = Value),
                SemiTransparent = global::PCL.ModSecret.ThemeField(() => global::PCL.ModSecret.ColorSemiTransparent, Value => global::PCL.ModSecret.ColorSemiTransparent = Value),
                DefaultHue = DefaultThemeHue,
                DefaultSaturation = DefaultThemeSat,
                DefaultLight = DefaultThemeLight,
                DefaultDelta = DefaultThemeDelta,
                Hue = new Func<global::System.Int32>(() => global::PCL.ModSecret.ColorHue),
                Saturation = new Func<global::System.Int32>(() => global::PCL.ModSecret.ColorSat),
                Light = new Func<global::System.Int32>(() => global::PCL.ModSecret.ColorLightAdjust),
                SetHue = new Action<global::System.Int32>(Value => global::PCL.ModSecret.ColorHue = Value),
                SetSaturation = new Action<global::System.Int32>(Value => global::PCL.ModSecret.ColorSat = Value),
                SetLight = new Action<global::System.Int32>(Value => global::PCL.ModSecret.ColorLightAdjust = Value),
                Delta = new Func<global::MeloongCore.OneOf<global::System.Int32, global::System.Int32[]>>(() => global::PCL.ModSecret.ColorHueTopbarDelta),
                SetDelta = new Action<global::MeloongCore.OneOf<global::System.Int32, global::System.Int32[]>>(Value => global::PCL.ModSecret.ColorHueTopbarDelta = Value),
                Hsl = new Func<global::System.Double, global::System.Double, global::System.Double, global::PCL.ModBase.MyColor>((H, S, L) => new global::PCL.ModBase.MyColor().FromHSL2(H, S, L)),
                Rgb = new Func<global::System.Double, global::System.Double, global::System.Double, global::PCL.ModBase.MyColor>((R, G, B) => new global::PCL.ModBase.MyColor(R, G, B)),
                Alpha = new Func<global::System.Double, global::PCL.ModBase.MyColor, global::PCL.ModBase.MyColor>((A, Color) => new global::PCL.ModBase.MyColor(A, Color)),
                Channels = new Func<global::PCLLF.Presentation.Controls.ControlColor, global::PCL.ModBase.MyColor>(Color => new global::PCL.ModBase.MyColor(Color.A, Color.R, Color.G, Color.B)),
                Resources = new Func<global::System.Windows.ResourceDictionary>(() => global::System.Windows.Application.Current.Resources),
                RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                RunInUiWait = work => global::PCL.ModBase.RunInUiWait(work),
                ColorMode = () => global::PCL.Settings.Get<int>("UiLauncherColorMode"),
                PanelOpacity = () => global::PCL.Settings.Get<int>("UiPanelOpacity"),
                BackgroundOpacity = () => global::PCL.Settings.Get<int>("UiBackgroundOpacity"),
                AddAnimationSuppression = delta => global::PCL.ModAnimation.AniControlEnabled += delta,
                LoadBackgroundResource = resource => (global::System.Windows.Media.ImageBrush)new global::PCL.MyBitmap(global::PCL.ModBase.PathImage + resource),
                InstallTooltipTheme = global::PCL.ModernTooltipTheme.Install,
                MainLoaded = () => false,
                Colorful = new Func<global::System.Boolean>(() => global::PCL.Settings.Get<bool>("UiBackgroundColorful")),
                BackgroundMode = new Func<global::System.Int32>(() => global::PCL.Settings.Get<int>("UiBackgroundMode")),
                                                                                ErrorToast = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Error(Err, Text, global::MeloongCore.LogBehavior.Toast))
            };
        }
        public static void ThemeRefresh(global::System.Int32 NewTheme = -(1))
        {
            var dispatcher = global::System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess()) {
                dispatcher.Invoke(() => ThemeRefresh(NewTheme));
                return;
            }
            var main = ModMain.FrmMain;
            bool dark = Settings.Get<int>("UiLauncherColorMode") == 1;
            void ApplyPalette() => PaletteService.Refresh(NewTheme);
            if (main?.TryRefreshGlassTheme(dark, ApplyPalette) == true) return;
            ApplyPalette();
        }
        internal static global::System.Windows.Media.Brush CreateTitleBackground() => PaletteService.CreateTitleBackground();
        public static void ThemeRefreshMain()
        {
            global::PCL.ModSecret.PaletteService.RefreshMain();
        }
        internal static void ApplyMainBackground(global::System.Windows.Controls.Panel Panel, global::System.Windows.UIElement Image)
        {
            global::PCL.ModSecret.PaletteService.ApplyBackground(Panel, Image);
        }
        internal static void UpdateCheckByButton()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            void Open()
            {
                var main = ModMain.FrmMain;
                if (main is null) return;
                bool alreadyOpen = main.PageCurrent == FormMain.PageType.Setup && main.PageCurrentSub == FormMain.PageSubType.SetupUpdate;
                main.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
                if (alreadyOpen || !ModLauncherUpdate.AutomaticCheckEnabled) main.CheckGlassLauncherUpdates();
            }
            if (dispatcher.CheckAccess()) Open(); else dispatcher.InvokeAsync(Open);
        }
        #if LEGACY_LINK
        // LF's original open-source legacy link configuration hook.
        public static JObject ServerConfig;
        public static ModLoader.LoaderTask<int, int> ServerLoader = new("PCL 配置更新", _ => Logger.Info("该版本中不包含更新通知功能……"), Priority: ThreadPriority.BelowNormal) { ReloadTimeout = 1000 * 60 * 60 };
#endif
        internal static global::System.String GetIdentify()
        {
            return global::PCL.ModSecret.IdentityService.Read();
        }
    }
}
