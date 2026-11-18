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
using global::PCLCS;
using PCLCS;
using global::PCLLF.Presentation.Controls;
using ThrottleDebounce;

namespace PCL
{

    // LF settings pages keep their XAML/DP owners and real platform/model identities.
    // All page policy, workflows and mutable page state are implemented in C#.
    internal sealed class SetupPageRuntime
    {
        internal static readonly global::PCLCS.LegacySetupPageHost Common = new global::PCLCS.LegacySetupPageHost()
        {
            Read = new Func<global::System.String, global::System.Object>(Key => global::PCL.Settings.Get<object>(Key)),
            Write = new Action<global::System.String, global::System.Object>((Key, Value) => global::PCL.Settings.Set(Key, Value)),
            Reset = new Action<global::System.String>(Key => global::PCL.Settings.Reset(Key)),
            Default = global::PCL.Settings.GetDefault,
            RefreshSettings = new Action<global::System.Windows.DependencyObject>(Page => global::PCL.SettingService.RefreshSettings(Page)),
            ResetSettings = new Action<global::System.Windows.DependencyObject>(Page => global::PCL.SettingService.ResetSettings(Page)),
            AnimationSuppression = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
            AddAnimationSuppression = new Action<global::System.Int32>(Delta => global::PCL.ModAnimation.AniControlEnabled += Delta),
            Message = new global::PCLCS.LegacySetupMessage((Text, Title, Button1, Button2, Button3, Warning) => global::PCL.ModMain.MyMsgBox(Text, Title, Button1, Button2, Button3, IsWarn: Warning)),
            Hint = new Action<global::System.String, global::PCLCS.LegacySetupHint, global::System.Boolean>((Text, Kind, Log) => global::PCL.ModMain.Hint(Text, (global::PCL.ModMain.HintType)Kind, Log)),
            BaseFolder = new Func<global::System.String>(() => global::MeloongCore.Paths.Base),
            ImageRoot = new Func<global::System.String>(() => global::PCL.ModBase.PathImage),
            AppData = new Func<global::System.String>(() => global::MeloongCore.Paths.AppData),
            UserDataFolder = new Func<global::System.String>(() => global::MeloongCore.Paths.AppDataThenName),
            OpenExplorer = global::PCL.ModBase.OpenExplorer,
            OpenWebsite = global::PCL.ModBase.OpenWebsite,
            DeleteDirectory = new Action<global::System.String>(Path => global::MeloongCore.DirectoryUtils.Delete(Path)),
            InThread = global::PCL.ModBase.RunInThread,
            InUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
            Sleep = new Action<global::System.Int32>(Delay => global::System.Threading.Thread.Sleep(Delay)),
            ExtractResource = global::PCL.ModBase.ExtractResources,
            LoadBitmap = new Func<global::System.String, global::PCLCS.LegacyBitmap>(Path => new global::PCL.MyBitmap(Path)),
            LoadBackgroundBrush = LoadBoundedBackgroundBrush,
            SelectImage = new Func<global::System.String, global::System.String[], global::System.String, global::System.Collections.Generic.IEnumerable<global::System.String>>((Title, Extensions, Label) => global::MeloongCore.Wpf.Dialogs.SelectFile(Title, false, filter: new[] { (Extensions, Label) })),
            StartAnimations = global::PCL.SetupPageRuntime.StartAnimations,
            StopAnimation = new Action<global::System.String>(Name => global::PCL.ModAnimation.AniStop(Name)),
            AnimationRunning = global::PCL.ModAnimation.AniIsRun
        };
        internal static readonly global::PCLCS.LegacyPageSetupUIRuntime UI = new global::PCLCS.LegacyPageSetupUIRuntime(new global::PCLCS.LegacySetupUiHost()
        {
            Common = Common,
            CurrentUI = new Func<global::PCLCS.LegacyPageSetupUIController>(() => global::PCL.ModMain.FrmSetupUI is null ? (global::PCLCS.LegacyPageSetupUIController)(null) : (global::PCL.ModMain.FrmSetupUI.Controller)),
            ReadHiddenForceShow = global::PCL.PageSetupUI.ReadHiddenForceShow,
            WriteHiddenForceShow = global::PCL.PageSetupUI.WriteHiddenForceShow,
            HasMain = new Func<global::System.Boolean>(() => global::PCL.ModMain.FrmMain is not null),
            MainPanel = new Func<global::System.Windows.Controls.Panel>(() => global::PCL.ModMain.FrmMain.PanForm),
            MainBackground = new Func<global::System.Windows.Controls.Panel>(() => global::PCL.ModMain.FrmMain.ImgBack),
            TitleLogo = new Func<global::PCLLF.Presentation.Controls.MyImageView>(() => global::PCL.ModMain.FrmMain.ImageTitleLogo),
            ApplyBackground = new Action<global::System.Windows.Controls.Panel, global::System.Windows.Controls.Panel>((Panel, Image) => global::PCL.ModSecret.ApplyMainBackground(Panel, Image)),
            UseDefaultBackground = ModTheme.UseDefaultBackground, MarkCustomBackground = ModTheme.MarkCustomBackground,
            UpdateBackgroundAndTitle = new Action(() => global::PCL.ModMain.FrmMain.UpdateBackgroundAndTitleBar()),
            RandomPicture = new Func<global::System.Collections.Generic.IList<global::System.String>, global::System.String>(Pictures => global::PCL.ModBase.RandomOne(Pictures)),
            MusicAll = new Func<global::System.Collections.Generic.List<global::System.String>>(() => global::PCL.ModMusic.MusicAllList),
            ClearMusicPlayer = new Action(() => global::PCL.ModMusic.MusicNAudio = (global::System.Object)null),
            AssignWaitingMusic = new Action<global::System.Collections.Generic.List<global::System.String>>(Values => global::PCL.ModMusic.MusicWaitingList = Values),
            AssignAllMusic = new Action<global::System.Collections.Generic.List<global::System.String>>(Values => global::PCL.ModMusic.MusicAllList = Values),
            RefreshMusic = new Action<global::System.Boolean>(ShowHint => global::PCL.ModMusic.MusicRefreshPlay(ShowHint)),
            RefreshHome = new Action(() => global::PCL.ModMain.FrmLaunchRight.ForceRefresh()),
            SetHomeHintEvent = new Action<global::System.Windows.DependencyObject, global::System.Boolean>((Control, OpenWeb) => global::PCL.CustomEventService.SetEventType(Control, (OpenWeb) ? (global::PCL.CustomEvent.EventType.打开网页) : (global::PCL.CustomEvent.EventType.None))),
            SetHomeHintEventData = global::PCL.CustomEventService.SetEventData,
            Shell = global::PCL.SetupPageRuntime.Shell,
            HasShell = global::PCL.SetupPageRuntime.HasShell,
            IsPage = global::PCL.SetupPageRuntime.IsPage,
            RefreshLaunchButtons = new Action(() => global::PCL.ModMain.FrmLaunchLeft.RefreshButtonsUI()),
            RefreshEmptyDownload = new Action(() => global::PCL.ModMain.FrmSelectRight.BtnEmptyDownload_Loaded()),
            RefreshDisabledModsDownload = new Action(() => global::PCL.ModMain.FrmInstanceModDisabled.BtnDownload_Loaded()),
            ApplyModpackVisibility = () => global::PCL.ModModpackMode.ApplyVisibility(),
            HasDisabledModsPage = new Func<global::System.Boolean>(() => global::PCL.ModMain.FrmInstanceModDisabled is not null)
        });
        internal static readonly global::PCLCS.LegacyPageSetupLaunchRuntime<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo> Launch = new global::PCLCS.LegacyPageSetupLaunchRuntime<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo>(new global::PCLCS.LegacySetupLaunchHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo>()
        {
            Common = Common,
            CurrentLaunch = new Func<global::PCLCS.LegacyPageSetupLaunchController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo>>(() => global::PCL.ModMain.FrmSetupLaunch is null ? (global::PCLCS.LegacyPageSetupLaunchController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo>)(null) : (global::PCL.ModMain.FrmSetupLaunch.Controller)),
            SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
            InstanceName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name),
            InstanceIndiePath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathIndie),
            InstanceLoaded = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.IsLoaded),
            InstanceModable = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.Modable),
            InstanceOptiFine = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => Instance.Version.HasOptiFine),
            LoadInstance = new Action<global::PCL.ModMinecraft.McInstance>(Instance => Instance.Load()),
            AssignSetupInstance = new Action<global::PCL.ModMinecraft.McInstance>(Instance => global::PCL.PageInstanceLeft.Instance = Instance),
            ImportPersonalOptions = global::PCL.PersonalGameSettings.ImportFrom,
            ShowInstanceSetup = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup, global::PCL.FormMain.PageSubType.InstanceSetup)),
            PersonalProfileExists = global::PCL.PersonalGameSettings.HasProfile,
            IsCurrentRamPage = new Func<global::System.Boolean>(() => (((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Setup) && (global::PCL.ModMain.FrmSetupLeft.PageID == global::PCL.FormMain.PageSubType.SetupLaunch))),
            ApplyInstanceVisibility = global::PCL.ModModpackMode.ApplyInstanceVisibility,
            AvailableMemory = new Func<global::System.UInt64>(() => global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory),
            TotalMemory = new Func<global::System.UInt64>(() => global::PCL.My.MyWpfExtension.Computer.Info.TotalPhysicalMemory),
            SelectSkin = global::PCL.ModMinecraft.McSkinSelect,
            SkinValid = new Func<global::PCL.ModMinecraft.McSkinInfo, global::System.Boolean>(Skin => Skin.IsVaild),
            SkinSlim = new Func<global::PCL.ModMinecraft.McSkinInfo, global::System.Boolean>(Skin => Skin.IsSlim),
            SkinPath = new Func<global::PCL.ModMinecraft.McSkinInfo, global::System.String>(Skin => Skin.LocalFile),
            StartLegacySkin = new Action<global::System.Boolean>(Force => global::PCL.PageLaunchLeft.SkinLegacy.Start(IsForceRestart: Force)),
            SaveSkin = new Action(() => global::PCL.MySkin.Save(global::PCL.PageLaunchLeft.SkinLegacy)),
            RefreshSkinCache = new Action(() => global::PCL.MySkin.RefreshCache((global::PCL.ModLoader.LoaderTask<(global::System.String, global::System.String), global::System.String>)null)),
            StartJavaRefresh = new Action(() => global::PCL.ModJava.JavaListRefreshWorker.Start()),
            SearchJava = () => global::PCL.ModJava.ManuallySearchJava(),
            ImportJava = global::PCL.ModJava.ManuallyImportJava,
            UpdateJavaLists = global::PCL.ModJava.UpdateJavaLists,
            RemoveJava = global::PCL.ModJava.ManuallyRemoveJava,
            JavaRefreshRunning = new Func<global::System.Boolean>(() => global::PCL.ModJava.JavaListRefreshWorker.Running),
            NewJavaItem = new Func<global::PCLLF.Presentation.Controls.MyListItemView>(() => new global::PCL.MyListItem()),
            NewJavaButton = new Func<global::PCLLF.Presentation.Controls.MyIconButtonView>(() => new global::PCL.MyIconButton()),
            ConnectItemClick = global::PCL.SetupPageRuntime.ConnectItemClick,
            ConnectIconClick = global::PCL.SetupPageRuntime.ConnectIconClick,
            SetJavaButtons = new Action<global::PCLLF.Presentation.Controls.MyListItemView, global::System.Collections.Generic.IEnumerable<global::PCLLF.Presentation.Controls.MyIconButtonView>>((Item, Buttons) => ((global::PCL.MyListItem)Item).Buttons = (global::System.Collections.Generic.IEnumerable<global::PCL.MyIconButton>)Buttons.Cast<MyIconButton>().ToList()),
            JavaUpLogo = global::PCL.ModBase.Logo.IconButtonArrowUp,
            JavaDownLogo = global::PCL.ModBase.Logo.IconButtonArrowDown,
            JavaDeleteLogo = global::PCL.ModBase.Logo.IconButtonStop,
            JavaOpenLogo = global::PCL.ModBase.Logo.IconButtonOpen,
            HasAdminRole = global::MeloongCore.WindowsUtils.HasAdminRole
        });
        internal static readonly global::PCLCS.LegacySetupSystemHost SystemHost = new global::PCLCS.LegacySetupSystemHost()
        {
            Common = Common,
            ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled),
            CanEnableModpack = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.CanEnable),
            ToggleModpack = new Action<global::PCLLF.Presentation.Controls.MyCheckBoxView, global::System.Boolean>((Control, User) => global::PCL.ModModpackMode.Toggle((global::PCL.MyCheckBox)Control, User)),
            LauncherDisplayName = new Func<global::System.String>(() => global::PCL.ModBase.LauncherDisplayName),
            LauncherVersion = new Func<global::System.String>(() => global::PCL.ModBase.VersionBaseName),
            CheckLauncherUpdate = global::PCL.ModSecret.UpdateCheckByButton,
            PreferredUpdateChannel = () => (int)ModLauncherUpdate.PreferredUpdateChannel(),
            ChangeUpdateChannel = ModLauncherUpdate.ChangeUpdateChannel
        };
        private static void ConnectItemClick(global::PCLLF.Presentation.Controls.MyListItemView Item, global::System.Action Work)
        {
            ((global::PCL.MyListItem)Item).Click += (_, _) => Work();
        }
        private static void ConnectIconClick(global::PCLLF.Presentation.Controls.MyIconButtonView Button, global::System.Action Work)
        {
            ((global::PCL.MyIconButton)Button).Click += (_, _) => Work();
        }
        private static void StartAnimations(global::System.Collections.Generic.IEnumerable<global::PCLCS.LegacySetupAnimation> Plans, global::System.String Name)
        {
            global::PCL.ModAnimation.AniStart(Plans.Select(CreateAnimation).ToArray(), Name);
        }
        private static global::PCL.ModAnimation.AniData CreateAnimation(global::PCLCS.LegacySetupAnimation Plan)
        {
            global::PCL.ModAnimation.AniEase Ease = (global::PCL.ModAnimation.AniEase)null;
            switch (Plan.Ease)
            {
                case global::PCLCS.LegacySetupEase.OutFluentWeak:
                    {
                        Ease = new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Weak);
                        break;
                    }
                case global::PCLCS.LegacySetupEase.OutFluentStrong:
                    {
                        Ease = new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Strong);
                        break;
                    }
            }
            switch (Plan.Kind)
            {
                case global::PCLCS.LegacySetupAnimationKind.GridWidth:
                    {
                        return global::PCL.ModAnimation.AaGridLengthWidth((global::System.Windows.Controls.ColumnDefinition)Plan.Target, Plan.Delta, Plan.Duration, Ease: Ease);
                    }
                case global::PCLCS.LegacySetupAnimationKind.Opacity:
                    {
                        return global::PCL.ModAnimation.AaOpacity(Plan.Target, Plan.Delta, Plan.Duration, Ease: Ease);
                    }
                case global::PCLCS.LegacySetupAnimationKind.X:
                    {
                        return global::PCL.ModAnimation.AaX(Plan.Target, Plan.Delta, Plan.Duration, Ease: Ease);
                    }

                default:
                    {
                        throw new global::System.ArgumentOutOfRangeException(nameof(Plan));
                    }
            }
        }
        private static global::System.Boolean HasShell(global::PCLCS.LegacySetupShellPart Part)
        {
            switch (Part)
            {
                case global::PCLCS.LegacySetupShellPart.InstanceLeft:
                    {
                        return global::PCL.ModMain.FrmInstanceLeft is not null;
                    }
                case global::PCLCS.LegacySetupShellPart.SetupLeft:
                    {
                        return global::PCL.ModMain.FrmSetupLeft is not null;
                    }
                case global::PCLCS.LegacySetupShellPart.OtherLeft:
                    {
                        return global::PCL.ModMain.FrmOtherLeft is not null;
                    }

                default:
                    {
                        throw new global::System.ArgumentOutOfRangeException(nameof(Part));
                    }
            }
        }
        private static global::System.Boolean IsPage(global::PCLCS.LegacySetupShellPage Page)
        {
            switch (Page)
            {
                case global::PCLCS.LegacySetupShellPage.Launch:
                    {
                        return ((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Launch);
                    }
                case global::PCLCS.LegacySetupShellPage.InstanceSelect:
                    {
                        return ((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSelect);
                    }
                case global::PCLCS.LegacySetupShellPage.InstanceSetup:
                    {
                        return ((global::PCL.ModMain.FrmMain.PageCurrent) == (global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup);
                    }

                default:
                    {
                        throw new global::System.ArgumentOutOfRangeException(nameof(Page));
                    }
            }
        }
        private static global::System.Windows.FrameworkElement Shell(global::PCLCS.LegacySetupShellElement Element)
        {
            switch (Element)
            {
                case global::PCLCS.LegacySetupShellElement.TitlePanel:
                    {
                        return global::PCL.ModMain.FrmMain.PanTitleSelect;
                    }
                case global::PCLCS.LegacySetupShellElement.TitleDownload:
                    {
                        return global::PCL.ModMain.FrmMain.BtnTitleSelect1;
                    }
                case global::PCLCS.LegacySetupShellElement.TitleLink:
                    {
                        return global::PCL.ModMain.FrmMain.BtnTitleSelect2;
                    }
                case global::PCLCS.LegacySetupShellElement.TitleSetup:
                    {
                        return global::PCL.ModMain.FrmMain.BtnTitleSelect3;
                    }
                case global::PCLCS.LegacySetupShellElement.TitleOther:
                    {
                        return global::PCL.ModMain.FrmMain.BtnTitleSelect4;
                    }
                case global::PCLCS.LegacySetupShellElement.TitleFavorites:
                    {
                        return global::PCL.ModMain.FrmMain.BtnTitleSelect5;
                    }
                case global::PCLCS.LegacySetupShellElement.ModifyInstance:
                    {
                        return global::PCL.ModMain.FrmInstanceLeft.ItemModify;
                    }
                case global::PCLCS.LegacySetupShellElement.SetupLaunch:
                    {
                        return global::PCL.ModMain.FrmSetupLeft.ItemLaunch;
                    }
                case global::PCLCS.LegacySetupShellElement.SetupUI:
                    {
                        return global::PCL.ModMain.FrmSetupLeft.ItemUI;
                    }
                case global::PCLCS.LegacySetupShellElement.SetupLink:
                    {
                        return global::PCL.ModMain.FrmSetupLeft.ItemLink;
                    }
                case global::PCLCS.LegacySetupShellElement.SetupSystem:
                    {
                        return global::PCL.ModMain.FrmSetupLeft.ItemSystem;
                    }
                case global::PCLCS.LegacySetupShellElement.SetupNavigation:
                    {
                        return global::PCL.ModMain.FrmSetupLeft.PanItem;
                    }
                case global::PCLCS.LegacySetupShellElement.OtherHelp:
                    {
                        return global::PCL.ModMain.FrmOtherLeft.ItemHelp;
                    }
                case global::PCLCS.LegacySetupShellElement.OtherAbout:
                    {
                        return global::PCL.ModMain.FrmOtherLeft.ItemAbout;
                    }
                case global::PCLCS.LegacySetupShellElement.OtherTest:
                    {
                        return global::PCL.ModMain.FrmOtherLeft.ItemTest;
                    }
                case global::PCLCS.LegacySetupShellElement.OtherNavigation:
                    {
                        return global::PCL.ModMain.FrmOtherLeft.PanItem;
                    }

                default:
                    {
                        throw new global::System.ArgumentOutOfRangeException(nameof(Element));
                    }
            }
        }

        internal static ImageSource TryLoadBoundedBitmap(string path, int maxPixelWidth)
        {
            try
            {
                using Stream probe = global::MeloongCore.FileUtils.ReadAsStream(path);
                byte[] header = new byte[2];
                int read = probe.Read(header, 0, 2);
                if (read == 2 && header[0] == 82 && header[1] == 73) return null; // RIFF/WebP uses Imazen fallback
                probe.Seek(0, SeekOrigin.Begin);
                var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return null;
                int nativeWidth = decoder.Frames[0].PixelWidth;
                if (nativeWidth <= 0) return null;

                probe.Seek(0, SeekOrigin.Begin);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (nativeWidth > maxPixelWidth) bitmap.DecodePixelWidth = maxPixelWidth;
                bitmap.StreamSource = probe;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static ImageBrush LoadBoundedBackgroundBrush(string path)
        {
            if (TryLoadBoundedBitmap(path, 1920) is { } fastImage)
            {
                // Keep ImageBrush unfrozen while its BitmapSource is frozen: LegacyMainWindowAppearance
                // mutates Stretch/TileMode/Viewport/Alignment directly on ImgBack.Background.
                return new ImageBrush(fastImage);
            }
            var legacy = new global::PCL.MyBitmap(path);
            try { return legacy.ToImageBrush(); }
            finally { legacy.Pic?.Dispose(); legacy.Pic = null; }
        }
    }
}
