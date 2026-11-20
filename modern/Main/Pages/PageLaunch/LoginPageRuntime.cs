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
using global::PCLLF.Presentation.Accounts;
using ThrottleDebounce;

using SkinLoader = PCL.ModLoader.LoaderTask<PCLLF.Presentation.Launch.LegacySkinRequest, string>;

namespace PCL
{

    // 真实 LF UI、设置、登录加载器及网络服务适配；页面流程只在 C# 控制器中维护。
    internal sealed class LoginPageRuntime
    {
        static LoginPageRuntime()
        {
            global::PCL.PresentationRuntime.EnsureConfigured();
        }
        internal static readonly global::PCLLF.Presentation.Accounts.LegacyLoginHost<global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginServer> Accounts = new global::PCLLF.Presentation.Accounts.LegacyLoginHost<global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginServer>()
        {
            ReadString = new Func<global::System.String, global::System.String>(Key => global::PCL.Settings.Get<string>(Key)),
            ReadInstanceString = new Func<global::System.String, global::System.String>(Key => global::PCL.Settings.Get<string>(Key, Instance: global::PCL.ModMinecraft.McInstanceSelected)),
            ReadBoolean = new Func<global::System.String, global::System.Boolean>(Key => global::PCL.Settings.Get<bool>(Key)),
            ReadInteger = new Func<global::System.String, global::System.Int32>(Key => global::PCL.Settings.Get<int>(Key)),
            WriteSetting = new Action<global::System.String, global::System.Object>((Key, Value) => global::PCL.Settings.Set(Key, Value)),
            AccountSnapshot = new Func<global::System.String, global::System.Collections.Generic.Dictionary<global::System.String, global::System.Object>>(Prefix => global::PCL.Settings.AccountSnapshot(Prefix)),
            SaveAccountGroup = new Action<global::System.Collections.Generic.Dictionary<global::System.String, global::System.Object>>(Values => global::PCL.Settings.SaveAccountGroup(Values)),
            SignOut = provider => LauncherApplicationServices.Accounts.SignOut(provider),
            CreateLegacy = new Func<global::PCLLF.Presentation.Accounts.LegacyLoginInput, global::PCL.ModLaunch.McLoginLegacy>(Data => new global::PCL.ModLaunch.McLoginLegacy() { UserName = Data.UserName, SkinType = Data.SkinType, SkinName = Data.SkinName }),
            CreateMs = global::PCL.LoginPageRuntime.CreateMicrosoftData,
            CreateServer = global::PCL.LoginPageRuntime.CreateServerData,
            LegacyName = new Func<global::PCL.ModLaunch.McLoginLegacy, global::System.String>(Data => Data.UserName),
            MsRefreshToken = new Func<global::PCL.ModLaunch.McLoginMs, global::System.String>(Data => Data.OAuthRefreshToken),
            ServerName = new Func<global::PCL.ModLaunch.McLoginServer, global::System.String>(Data => Data.UserName),
            ServerPassword = new Func<global::PCL.ModLaunch.McLoginServer, global::System.String>(Data => Data.Password),
            ForceReselectProfile = new Action<global::PCL.ModLaunch.McLoginServer, global::System.Boolean>((Data, Value) => Data.ForceReselectProfile = Value),
            IsReselectingServer = new Func<global::System.Object, global::System.Boolean>(Data => ((Data is global::PCL.ModLaunch.McLoginServer) && (((global::PCL.ModLaunch.McLoginServer)Data).ForceReselectProfile))),
            HasSelectedInstance = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McInstanceSelected is not null),
            SelectedVersionValid = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McInstanceSelected.Version.Valid),
            SelectedVanillaVersion = new Func<global::System.Version>(() => global::PCL.ModMinecraft.McInstanceSelected.Version.Vanilla),
            AnimationDisabled = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled),
            HiddenForceShow = new Func<global::System.Boolean>(() => global::PCL.PageSetupUI.HiddenForceShow),
            HasLaunchPage = new Func<global::System.Boolean>(() => global::PCL.ModMain.FrmLaunchLeft is not null),
            OpenLaunchSettings = new Action(() => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Setup, global::PCL.FormMain.PageSubType.SetupLaunch)),
            RefreshLaunchPage = new Action<global::System.Boolean, global::System.Boolean>((KeepInput, Anim) => global::PCL.ModMain.FrmLaunchLeft.RefreshPage(KeepInput, Anim)),
            OpenWebsite = new Action<global::System.String>(Address => global::PCL.ModBase.OpenWebsite(Address)),
            Hint = global::PCL.LoginPageRuntime.PublishHint,
            RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
            RunInUiWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(Work)),
            RunInThread = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInThread(Work)),
            RunInNewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)),
            MsLogin = TaskPort(() => global::PCL.ModLaunch.McLoginMsLoader),
            NideLogin = TaskPort(() => global::PCL.ModLaunch.McLoginNideLoader),
            AuthLogin = TaskPort(() => global::PCL.ModLaunch.McLoginAuthLoader),
            MainLogin = TaskPort(() => global::PCL.ModLaunch.McLoginLoader),
            Launch = TaskPort(() => global::PCL.ModLaunch.McLaunchLoader),
            MsAccessToken = new Func<global::System.String>(() => global::PCL.ModLaunch.McLoginMsLoader.Output.AccessToken),
            MsUuid = new Func<global::System.String>(() => global::PCL.ModLaunch.McLoginMsLoader.Output.Uuid),
            MsProfileJson = new Func<global::System.String>(() => global::PCL.ModLaunch.McLoginMsLoader.Output.ProfileJson),
            SetMsProfileJson = new Action<global::System.String>(Value => global::PCL.ModLaunch.McLoginMsLoader.Output.ProfileJson = Value),
            RequestRetry = global::PCL.LoginPageRuntime.RequestRetry
        };
        internal static readonly global::PCLLF.Presentation.Accounts.LegacySkinServices<ModLaunch.McLoginLegacy, ModLaunch.McLoginMs, ModLaunch.McLoginServer, SkinLoader> Skins = new(new()
        {
            Accounts = Accounts,
            Task = loader => TaskPort(() => loader),
            Output = loader => loader.Output,
            Name = loader => loader.Name,
            SkinLoaders = () => PageLaunchLeft.SkinLoaders,
            LegacyLoader = () => PageLaunchLeft.SkinLegacy,
            MicrosoftLoader = () => PageLaunchLeft.SkinMs,
            ImagePath = new Func<global::System.String>(() => global::PCL.ModBase.PathImage),
            TempPath = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp),
            CreateBitmap = new Func<global::System.String, global::PCLCS.LegacyBitmap>(Address => new global::PCL.MyBitmap(Address)),
            SaveSkinPath = new Func<global::System.String, global::System.String, global::System.String>((Title, Name) => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, Name, filter: new[] { ("png", "皮肤图片文件") })),
            ClearIniCache = new Action<global::System.String>(Address => global::PCL.ModBase.IniClearCache(Address)),
            WriteIni = new Action<global::System.String, global::System.String, global::System.String>((Address, Key, Value) => global::PCL.ModBase.WriteIni(Address, Key, Value)),
            MsLoggedInData = global::PCL.PageLoginMsSkin.GetLoginData,
            SelectCape = global::PCL.LoginPageRuntime.SelectCape
        });
        private static global::PCL.ModLaunch.McLoginMs CreateMicrosoftData(global::PCLLF.Presentation.Accounts.LegacyLoginInput Data)
        {
            var Result = new global::PCL.ModLaunch.McLoginMs();
            if (Data.PopulateMsBasic)
            {
                Result.OAuthRefreshToken = Data.RefreshToken;
                Result.UserName = Data.UserName;
            }
            if (Data.PopulateMsFull)
            {
                Result.AccessToken = Data.AccessToken;
                Result.Uuid = Data.Uuid;
                Result.ProfileJson = Data.ProfileJson;
            }
            return Result;
        }
        private static global::PCL.ModLaunch.McLoginServer CreateServerData(global::PCLLF.Presentation.Accounts.LegacyLoginInput Data)
        {
            return new global::PCL.ModLaunch.McLoginServer((Data.Token == "Auth") ? (global::PCL.ModLaunch.McLoginType.Auth) : (global::PCL.ModLaunch.McLoginType.Nide))
            {
                Token = Data.Token,
                BaseUrl = Data.BaseUrl,
                UserName = Data.UserName,
                Password = Data.Password,
                Description = Data.Description,
                Type = (Data.Token == "Auth") ? (global::PCL.ModLaunch.McLoginType.Auth) : (global::PCL.ModLaunch.McLoginType.Nide)
            };
        }
        internal static global::PCLLF.Presentation.Accounts.LegacyLoginTaskPort TaskPort<TIn, TOut>(global::System.Func<global::PCL.ModLoader.LoaderTask<TIn, TOut>> Read)
        {
            return new global::PCLLF.Presentation.Accounts.LegacyLoginTaskPort()
            {
                State = new Func<global::PCLLF.Presentation.Accounts.LegacyLoginTaskState>(() => ((global::PCLLF.Presentation.Accounts.LegacyLoginTaskState)(((global::System.Int32)Read().State)))),
                Progress = new Func<global::System.Double>(() => Read().Progress),
                Error = new Func<global::System.Exception>(() => Read().Error),
                Input = new Func<global::System.Object>(() => Read().Input),
                Start = new Action<global::System.Object, global::System.Boolean>((Input, Force) => Read().Start(Input, Force)),
                Wait = new Action<global::System.Object, global::System.Boolean>((Input, Force) => Read().WaitForExit(Input, IsForceRestart: Force)),
                Cancel = new Action(() => Read().Cancel()),
                ResetInput = new Action(() => Read().Input = (TIn)(default(TIn)))
            };
        }
        internal static void PublishHint(global::System.String Text, global::PCLLF.Presentation.Accounts.LegacyLoginHint Kind)
        {
            switch (Kind)
            {
                case global::PCLLF.Presentation.Accounts.LegacyLoginHint.Red:
                    {
                        global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red);
                        break;
                    }
                case global::PCLLF.Presentation.Accounts.LegacyLoginHint.Green:
                    {
                        global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green);
                        break;
                    }
                case global::PCLLF.Presentation.Accounts.LegacyLoginHint.Blue:
                    {
                        global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Blue);
                        break;
                    }

                default:
                    {
                        global::PCL.ModMain.Hint(Text);
                        break;
                    }
            }
        }
        private static global::System.String RequestRetry(global::System.String Url, global::System.Net.Http.HttpMethod Method, global::System.Object Content, global::System.String ContentType, global::System.Collections.Generic.Dictionary<global::System.String, global::System.String> Headers)
        {
            var Values = new global::System.String[(Headers.Count), 2];
            var Index = default(global::System.Int32);
            foreach (var Pair in Headers)
            {
                Values[Index, 0] = Pair.Key;
                Values[Index, 1] = Pair.Value;
                Index += 1;
            }
            return global::PCL.ModNet.NetRequestByClientRetry(Url, Method, Content: Content, ContentType: ContentType, Headers: Values);
        }
        private static global::System.Int32? SelectCape(global::System.Collections.Generic.IReadOnlyList<global::PCLLF.Presentation.Accounts.LegacyCapeChoice> Choices)
        {
            var Controls = new global::System.Collections.Generic.List<global::PCL.IMyRadio>();
            foreach (var Choice in Choices)
                Controls.Add(new global::PCL.MyRadioBox() { Text = Choice.Text, Checked = Choice.Checked });
            return global::PCL.ModMain.MyMsgBoxSelect((global::System.Collections.Generic.IEnumerable<global::PCL.IMyRadio>)Controls, "选择披风", "确定", "取消");
        }
        internal static global::PCLLF.Presentation.Accounts.LegacySelectedSkin SelectSkin()
        {
            var Data = global::PCL.ModMinecraft.McSkinSelect();
            return new global::PCLLF.Presentation.Accounts.LegacySelectedSkin() { IsVaild = Data.IsVaild, IsSlim = Data.IsSlim, LocalFile = Data.LocalFile };
        }
    }
}
