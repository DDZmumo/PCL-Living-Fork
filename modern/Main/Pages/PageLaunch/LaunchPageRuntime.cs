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
using global::PCLLF.Presentation.Launch;
using ThrottleDebounce;

using PCLLF.Presentation.Launch;
using SkinLoader = PCL.ModLoader.LoaderTask<PCLLF.Presentation.Launch.LegacySkinRequest, string>;

namespace PCL
{

    // 原 LF 数据、窗口和 Aa/Ani 原语接线；两页完整流程及四类皮肤决策由 C# 承载。
    internal sealed class LaunchPageRuntime
    {
        static LaunchPageRuntime()
        {
            global::PCL.PresentationRuntime.EnsureConfigured();
        }
        internal static global::PCLLF.Presentation.Launch.LegacyLaunchAnimations<global::PCL.ModAnimation.AniData> Animations()
        {
            return new global::PCLLF.Presentation.Launch.LegacyLaunchAnimations<global::PCL.ModAnimation.AniData>()
            {
                Opacity = new Func<global::System.Object, global::System.Double, global::System.Int32, global::System.Int32, global::PCLLF.Presentation.Launch.LegacyLaunchEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Value, Time, Delay, Ease, After) => global::PCL.ModAnimation.AaOpacity(Target, Value, Time, Delay, global::PCL.LaunchPageRuntime.ToEase(Ease), After)),
                ScaleTransform = new Func<global::System.Object, global::System.Double, global::System.Int32, global::System.Int32, global::PCLLF.Presentation.Launch.LegacyLaunchEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Value, Time, Delay, Ease, After) => global::PCL.ModAnimation.AaScaleTransform(Target, Value, Time, Delay, global::PCL.LaunchPageRuntime.ToEase(Ease), After)),
                Width = new Func<global::System.Object, global::System.Double, global::System.Int32, global::System.Int32, global::PCLLF.Presentation.Launch.LegacyLaunchEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Value, Time, Delay, Ease, After) => global::PCL.ModAnimation.AaWidth(Target, Value, Time, Delay, global::PCL.LaunchPageRuntime.ToEase(Ease), After)),
                Height = new Func<global::System.Object, global::System.Double, global::System.Int32, global::System.Int32, global::PCLLF.Presentation.Launch.LegacyLaunchEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Value, Time, Delay, Ease, After) => global::PCL.ModAnimation.AaHeight(Target, Value, Time, Delay, global::PCL.LaunchPageRuntime.ToEase(Ease), After)),
                GridWidth = new Func<global::System.Object, global::System.Double, global::System.Int32, global::System.Int32, global::PCLLF.Presentation.Launch.LegacyLaunchEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Value, Time, Delay, Ease, After) => global::PCL.ModAnimation.AaGridLengthWidth(Target, Value, Time, Delay, global::PCL.LaunchPageRuntime.ToEase(Ease), After)),
                TranslateX = new Func<global::System.Object, global::System.Double, global::System.Int32, global::System.Int32, global::PCLLF.Presentation.Launch.LegacyLaunchEase, global::System.Boolean, global::PCL.ModAnimation.AniData>((Target, Value, Time, Delay, Ease, After) => global::PCL.ModAnimation.AaTranslateX(Target, Value, Time, Delay, global::PCL.LaunchPageRuntime.ToEase(Ease), After)),
                Code = new Func<global::System.Action, global::System.Int32, global::System.Boolean, global::PCL.ModAnimation.AniData>((Work, Delay, After) => global::PCL.ModAnimation.AaCode(() => Work(), Delay, After)),
                Start = new Action<global::System.Collections.Generic.IEnumerable<global::PCL.ModAnimation.AniData>, global::System.String, global::System.Boolean>((Frames, Name, RefreshTime) => global::PCL.ModAnimation.AniStart((global::System.Collections.IList)Frames, Name, RefreshTime)),
                Stop = new Action<global::System.String>(Name => global::PCL.ModAnimation.AniStop(Name)),
                IsRunning = global::PCL.ModAnimation.AniIsRun
            };
        }
        private static global::PCL.ModAnimation.AniEase ToEase(global::PCLLF.Presentation.Launch.LegacyLaunchEase Ease)
        {
            switch (Ease)
            {
                case global::PCLLF.Presentation.Launch.LegacyLaunchEase.OutFluent:
                    {
                        return new global::PCL.ModAnimation.AniEaseOutFluent();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLaunchEase.InFluent:
                    {
                        return new global::PCL.ModAnimation.AniEaseInFluent();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLaunchEase.OutBackWeak:
                    {
                        return new global::PCL.ModAnimation.AniEaseOutBack(global::PCL.ModAnimation.AniEasePower.Weak);
                    }
                case global::PCLLF.Presentation.Launch.LegacyLaunchEase.OutFluentWeak:
                    {
                        return new global::PCL.ModAnimation.AniEaseOutFluent(global::PCL.ModAnimation.AniEasePower.Weak);
                    }
                case global::PCLLF.Presentation.Launch.LegacyLaunchEase.InFluentWeak:
                    {
                        return new global::PCL.ModAnimation.AniEaseInFluent(global::PCL.ModAnimation.AniEasePower.Weak);
                    }

                default:
                    {
                        return (global::PCL.ModAnimation.AniEase)null;
                    }
            }
        }
        internal static readonly LegacyLaunchSkins<SkinLoader> Skins = new(new() {
            ReadString = key => Settings.Get<string>(key),
            ReadInteger = key => Settings.Get<int>(key),
            WriteSetting = (key, value) => Settings.Set(key, value),
            OfflinePageReloaded = () => ModMain.FrmLoginLegacy?.IsReloaded == true,
            OfflineName = () => ModMain.FrmLoginLegacy.ComboName.Text,
            OfflineLibrarySkin = SkinLibraryApplicationService.FindOfflineSkinFile,
            AccountInput = ReadAccountSkin,
            Input = task => task.Input,
            ResetInput = task => task.Input = default,
            Output = (task, path) => task.Output = path,
            Canceled = (task, thread) => task.IsCanceledWithThread(thread),
            Dispatch = work => ModBase.RunInUi(work),
            ClearSkin = kind => Skin(kind)?.Clear(),
            PageExists = kind => Skin(kind) is not null,
            RenderSkin = (kind, request, path, fetched) => Skin(kind)?.Load(kind, request, path, fetched),
            ImagePath = () => ModBase.PathImage,
            AppDataPath = () => Paths.AppDataThenName,
            OfflineUuid = name => global::Microsoft.VisualBasic.CompilerServices.Conversions.ToString(ModLaunch.McLoginLegacyUuid(name)),
            SkinSex = ModMinecraft.McSkinSex,
            DownloadSkin = ModMinecraft.McSkinDownload,
            SkinAddress = (kind, request) => request.UsesSkinSite
                ? GlassSkinSites.SkinAddress(request.Server, request.Id).GetAwaiter().GetResult() ?? throw new InvalidOperationException("未设置自定义皮肤")
                : ModMinecraft.McSkinGetAddress(request.Id, kind, request.Server),
            MojangUuid = (name, throwOnNotFound) => global::Microsoft.VisualBasic.CompilerServices.Conversions.ToString(ModLaunch.McLoginMojangUuid(name, throwOnNotFound)),
            Hint = text => ModMain.Hint(text)
        });

        internal static LegacySkinRequest SkinSiteRequest(SkinSiteCharacter character) =>
            new(character.Profile.Name, character.Profile.Id, character.Site.Server, UsesSkinSite: true);

        internal static ImageSource Avatar(string kind, LegacySkinRequest request) =>
            Skin(kind)?.AvatarFor(request) ?? GlassSkinHead.LastGood(kind, request);

        private static LegacySkinRequest ReadAccountSkin(string kind)
        {
            var instance = ModMinecraft.McInstanceSelected;
            if (kind == "Auth" && GlassSkinSites.CurrentForLaunch() is { } character)
                return SkinSiteRequest(character);
            string prefix = kind == "Ms" ? "CacheMsV2" : "Cache" + kind;
            var cache = Settings.AccountSnapshot(prefix);
            string server = kind switch {
                "Auth" => instance is null ? cache.GetValueOrDefault("CacheAuthServerServer") as string ?? "" : Settings.Get<string>("VersionServerAuthServer", instance),
                "Nide" => instance is null ? cache.GetValueOrDefault("CacheNideServer") as string ?? "" : Settings.Get<string>("VersionServerNide", instance),
                _ => ""
            };
            if (kind == "Auth" && !AuthlibEndpoint.Same(AuthlibEndpoint.ForServer(server), cache.GetValueOrDefault(AuthlibEndpoint.CredentialServerSetting) as string))
                return new("", "", server);
            return new(cache.GetValueOrDefault(prefix + "Name") as string ?? "", cache.GetValueOrDefault(prefix + "Uuid") as string ?? "", server);
        }

        internal static MySkin Skin(string kind) => kind switch {
            "Legacy" => ModMain.FrmLoginLegacy?.Skin,
            "Ms" => ModMain.FrmLoginMsSkin?.Skin,
            "Nide" => ModMain.FrmLoginNideSkin?.Skin,
            "Auth" => ModMain.FrmLoginAuthSkin?.Skin,
            _ => null
        };

        internal static global::System.Windows.FrameworkElement ReadPage(global::PCLLF.Presentation.Launch.LegacyLoginPageKind Kind)
        {
            switch (Kind)
            {
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Legacy:
                    {
                        return global::PCL.ModMain.FrmLoginLegacy;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Ms:
                    {
                        return global::PCL.ModMain.FrmLoginMs;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.MsSkin:
                    {
                        return global::PCL.ModMain.FrmLoginMsSkin;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Nide:
                    {
                        return global::PCL.ModMain.FrmLoginNide;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.NideSkin:
                    {
                        return global::PCL.ModMain.FrmLoginNideSkin;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Auth:
                    {
                        return global::PCL.ModMain.FrmLoginAuth;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.AuthSkin:
                    {
                        return global::PCL.ModMain.FrmLoginAuthSkin;
                    }

                default:
                    {
                        throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                    }
            }
        }
        internal static global::System.Windows.FrameworkElement CreatePage(global::PCLLF.Presentation.Launch.LegacyLoginPageKind Kind)
        {
            switch (Kind)
            {
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Legacy:
                    {
                        return new global::PCL.PageLoginLegacy();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Ms:
                    {
                        return new global::PCL.PageLoginMs();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.MsSkin:
                    {
                        return new global::PCL.PageLoginMsSkin();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Nide:
                    {
                        return new global::PCL.PageLoginNide();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.NideSkin:
                    {
                        return new global::PCL.PageLoginNideSkin();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Auth:
                    {
                        return new global::PCL.PageLoginAuth();
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.AuthSkin:
                    {
                        return new global::PCL.PageLoginAuthSkin();
                    }

                default:
                    {
                        throw new global::System.ArgumentOutOfRangeException(nameof(Kind));
                    }
            }
        }
        internal static void StorePage(global::PCLLF.Presentation.Launch.LegacyLoginPageKind Kind, global::System.Windows.FrameworkElement Page)
        {
            switch (Kind)
            {
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Legacy:
                    {
                        global::PCL.ModMain.FrmLoginLegacy = ((global::PCL.PageLoginLegacy)(Page));
                        break;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Ms:
                    {
                        global::PCL.ModMain.FrmLoginMs = ((global::PCL.PageLoginMs)(Page));
                        break;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.MsSkin:
                    {
                        global::PCL.ModMain.FrmLoginMsSkin = ((global::PCL.PageLoginMsSkin)(Page));
                        break;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Nide:
                    {
                        global::PCL.ModMain.FrmLoginNide = ((global::PCL.PageLoginNide)(Page));
                        break;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.NideSkin:
                    {
                        global::PCL.ModMain.FrmLoginNideSkin = ((global::PCL.PageLoginNideSkin)(Page));
                        break;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.Auth:
                    {
                        global::PCL.ModMain.FrmLoginAuth = ((global::PCL.PageLoginAuth)(Page));
                        break;
                    }
                case global::PCLLF.Presentation.Launch.LegacyLoginPageKind.AuthSkin:
                    {
                        global::PCL.ModMain.FrmLoginAuthSkin = ((global::PCL.PageLoginAuthSkin)(Page));
                        break;
                    }
            }
        }
        internal static global::PCLLF.Presentation.Launch.LegacyLaunchJobPort Job(global::PCL.ModLoader.LoaderBase Loader, global::System.Func<global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>> Items = null)
        {
            return new global::PCLLF.Presentation.Launch.LegacyLaunchJobPort()
            {
                State = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchPanelState>(() => ((global::PCLLF.Presentation.Launch.LegacyLaunchPanelState)(((global::System.Int32)Loader.State)))),
                Progress = new Func<global::System.Double>(() => Loader.Progress),
                Wait = new Action<global::System.Boolean>(Force => Loader.WaitForExit(IsForceRestart: Force)),
                Cancel = new Action(() => Loader.Cancel()),
                Items = Items is null ? null : new Func<global::System.Collections.Generic.IEnumerable<global::PCLLF.Presentation.Launch.LegacyLaunchItemPort>>(() => Items().Select(Value => global::PCL.LaunchPageRuntime.Item(Value)))
            };
        }
        internal static global::PCLLF.Presentation.Launch.LegacyLaunchItemPort Item(global::PCL.ModLoader.LoaderBase Loader)
        {
            if (Loader is null)
                return (global::PCLLF.Presentation.Launch.LegacyLaunchItemPort)null;
            return new global::PCLLF.Presentation.Launch.LegacyLaunchItemPort()
            {
                State = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchPanelState>(() => ((global::PCLLF.Presentation.Launch.LegacyLaunchPanelState)(((global::System.Int32)Loader.State)))),
                Name = new Func<global::System.String>(() => Loader.Name),
                Parent = new Func<global::PCLLF.Presentation.Launch.LegacyLaunchItemPort>(() => global::PCL.LaunchPageRuntime.Item(Loader.RealParent))
            };
        }
    }
}
