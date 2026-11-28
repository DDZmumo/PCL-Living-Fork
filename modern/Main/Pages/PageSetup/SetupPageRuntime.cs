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
            if (global::PCL.GlassVideoWallpaper.IsVideo(path)) return global::PCL.GlassVideoWallpaper.Create(path);
            global::PCL.GlassVideoWallpaper.Stop();
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
