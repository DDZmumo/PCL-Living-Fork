using System;
using System.Collections;
using System.Collections.Concurrent;
using global::System.Collections.Generic;
using System.Collections.Generic;
using global::System.Diagnostics;
using System.Diagnostics;
using global::System.IO;
using System.IO;
using System.IO.Compression;
using global::System.Linq;
using System.Linq;
using System.Net;
using global::System.Net.Http;
using System.Net.Http;
using System.Runtime.CompilerServices;
using global::System.Text;
using System.Text;
using global::System.Threading;
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
using global::Newtonsoft.Json.Linq;
using Newtonsoft.Json.Linq;
using PCLCS;
using LaunchCore = global::PCLCS.LegacyLaunchEngine<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher>;
using LaunchHost = global::PCLCS.LegacyLaunchHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher>;
using ThrottleDebounce;

namespace PCL
{

    // LF 原字段、嵌套类型及平台调用身份；完整启动／登录／参数／进程业务由 C# 实现维护。
    public static class ModLaunch
    {
        public static global::PCL.ModLaunch.McLaunchOptions CurrentLaunchOptions = (global::PCL.ModLaunch.McLaunchOptions)null;
        public static global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLaunchOptions, global::System.Object> McLaunchLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLaunchOptions, global::System.Object>("Loader Launch", global::PCL.ModLaunch.McLaunchStart) { OnStateChanged = Loader => global::PCL.ModLaunch.McLaunchState((global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLaunchOptions, global::System.Object>)Loader) };
        public static global::PCL.ModLoader.LoaderCombo<global::System.Object> McLaunchLoaderReal;
        public static global::System.Diagnostics.Process McLaunchProcess;
        public static global::PCL.ModWatcher.Watcher McLaunchWatcher;
        public static global::PCLCS.Java McLaunchJavaSelected = (global::PCLCS.Java)null;
        private static global::System.String CanceledHint = (global::System.String)null;
        private static global::System.String McLaunchArgument;
        private static global::System.Text.Encoding McLaunchEncoding;
        public static global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginResult> McLoginLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginResult>("登录", global::PCL.ModLaunch.McLoginStart, global::PCL.ModLaunch.McLoginInput, global::System.Threading.ThreadPriority.BelowNormal) { ReloadTimeout = 1, ProgressWeight = 15d, Block = false };
        public static global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginResult> McLoginMsLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginResult>("Loader Login Ms", global::PCL.ModLaunch.McLoginMsStart) { ReloadTimeout = 1 };
        public static global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModLaunch.McLoginResult> McLoginLegacyLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModLaunch.McLoginResult>("Loader Login Legacy", global::PCL.ModLaunch.McLoginLegacyStart);
        public static global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult> McLoginNideLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult>("Loader Login Nide", global::PCL.ModLaunch.McLoginServerStart) { ReloadTimeout = (((1000) * (60)) * (10)) };
        public static global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult> McLoginAuthLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult>("Loader Login Auth", global::PCL.ModLaunch.McLoginServerStart) { ReloadTimeout = (((1000) * (60)) * (10)) };
        private readonly static global::PCLCS.LegacyLaunchEngine<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher> Service = new global::PCLCS.LegacyLaunchEngine<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher>(global::PCL.ModLaunch.CreateLaunchHost());

        public enum McLoginType
        {
            Legacy = 0,
            Nide = 2,
            Auth = 3,
            Ms = 5
        }
        public struct McLoginResult
        {
            public global::System.String Name;
            public global::System.String Uuid;
            public global::System.String AccessToken;
            public global::System.String Type;
            public global::System.String ClientToken;
            public global::System.String ProfileJson;
        }
        public class McLaunchOptions : global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>
        {
            /// <summary>
        /// 强制指定在启动后进入的服务器 IP。
        /// 默认值：Nothing。使用版本设置的值。
        /// </summary>
            public global::System.String ServerIp = (global::System.String)null;
            /// <summary>
        /// 将启动脚本保存到该地址，然后取消启动。这同时会改变启动时的提示等。
        /// 默认值：Nothing。不保存。
        /// </summary>
            public global::System.String SaveBatch = (global::System.String)null;
            /// <summary>
        /// 强行指定启动的 MC 版本。
        /// 默认值：Nothing。使用 McInstanceCurrent。
        /// </summary>
            public global::PCL.ModMinecraft.McInstance Instance = (global::PCL.ModMinecraft.McInstance)null;
            /// <summary>
        /// 额外的启动参数。
        /// </summary>
            public global::System.Collections.Generic.List<global::System.String> ExtraGameArgs = new global::System.Collections.Generic.List<global::System.String>();
            public global::System.String QuickPlayWorld = (global::System.String)null;
            internal global::PCL.PersonalOptionsSession PersonalOptions;
            internal global::PCL.QuickPlaySession QuickPlay;
            internal global::System.String EnvironmentPoint;

            private string BridgeServerIp
            {
                get
                {
                    return this.ServerIp;
                }
                set
                {
                    this.ServerIp = value;
                }
            }

            string global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.ServerIp { get => this.BridgeServerIp; set => this.BridgeServerIp = value; }
            private string BridgeSaveBatch
            {
                get
                {
                    return this.SaveBatch;
                }
                set
                {
                    this.SaveBatch = value;
                }
            }

            string global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.SaveBatch { get => this.BridgeSaveBatch; set => this.BridgeSaveBatch = value; }
            private global::PCL.ModMinecraft.McInstance BridgeInstance
            {
                get
                {
                    return this.Instance;
                }
                set
                {
                    this.Instance = value;
                }
            }

            global::PCL.ModMinecraft.McInstance global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.Instance { get => this.BridgeInstance; set => this.BridgeInstance = value; }
            private global::System.Collections.Generic.List<global::System.String> BridgeExtraGameArgs
            {
                get
                {
                    return this.ExtraGameArgs;
                }
            }

            global::System.Collections.Generic.List<global::System.String> global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.ExtraGameArgs { get => this.BridgeExtraGameArgs; }
            private string BridgeQuickPlayWorld
            {
                get
                {
                    return this.QuickPlayWorld;
                }
                set
                {
                    this.QuickPlayWorld = value;
                }
            }

            string global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.QuickPlayWorld { get => this.BridgeQuickPlayWorld; set => this.BridgeQuickPlayWorld = value; }
            private string BridgeEnvironmentPoint
            {
                get
                {
                    return this.EnvironmentPoint;
                }
                set
                {
                    this.EnvironmentPoint = value;
                }
            }

            string global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.EnvironmentPoint { get => this.BridgeEnvironmentPoint; set => this.BridgeEnvironmentPoint = value; }
            private global::PCLCS.PersonalOptionsSessionData BridgePersonalOptions
            {
                get
                {
                    return this.PersonalOptions;
                }
                set
                {
                    this.PersonalOptions = (global::PCL.PersonalOptionsSession)value;
                }
            }

            global::PCLCS.PersonalOptionsSessionData global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.PersonalOptions { get => this.BridgePersonalOptions; set => this.BridgePersonalOptions = value; }
            private global::PCLCS.QuickPlaySessionData BridgeQuickPlay
            {
                get
                {
                    return this.QuickPlay;
                }
                set
                {
                    this.QuickPlay = (global::PCL.QuickPlaySession)value;
                }
            }

            global::PCLCS.QuickPlaySessionData global::PCLCS.ILegacyLaunchOptions<global::PCL.ModMinecraft.McInstance>.QuickPlay { get => this.BridgeQuickPlay; set => this.BridgeQuickPlay = value; }
        }

        public abstract class McLoginData : global::PCLCS.ILegacyLaunchLoginData
        {
            /// <summary>
        /// 登录方式。
        /// </summary>
            public global::PCL.ModLaunch.McLoginType Type;
            public override global::System.Boolean Equals(global::System.Object obj)
            {
                return global::PCLCS.LegacyLaunchIdentity.LoginEquals(this, obj);
            }
            private int BridgeTypeCode
            {
                get
                {
                    return (global::System.Int32)this.Type;
                }
            }

            int global::PCLCS.ILegacyLaunchLoginData.TypeCode { get => this.BridgeTypeCode; }
        }

        public class McLoginServer : global::PCL.ModLaunch.McLoginData, global::PCLCS.ILegacyLaunchServerData
        {

            /// <summary>
        /// 登录用户名。
        /// </summary>
            public global::System.String UserName;
            /// <summary>
        /// 登录密码。
        /// </summary>
            public global::System.String Password;
            /// <summary>
        /// 登录服务器基础地址。
        /// </summary>
            public global::System.String BaseUrl;
            /// <summary>
        /// 登录所使用的标识符，目前只可能为 “Auth” 或 “Nide”，用于存储缓存等。
        /// </summary>
            public global::System.String Token;
            /// <summary>
        /// 登录方式的描述字符串，如 “正版”、“统一通行证”。
        /// </summary>
            public global::System.String Description;
            /// <summary>
        /// 是否在本次登录中强制要求玩家重新选择角色，目前仅对 Authlib-Injector 生效。
        /// </summary>
            public global::System.Boolean ForceReselectProfile = false;

            public McLoginServer(global::PCL.ModLaunch.McLoginType Type)
            {
                this.Type = Type;
            }
            public override global::System.Int32 GetHashCode()
            {
                return global::PCLCS.LegacyLaunchIdentity.ServerHash(this.UserName, this.Password, this.BaseUrl, this.Token, (global::System.Int32)this.Type);
            }


            private string BridgeUserName
            {
                get
                {
                    return this.UserName;
                }
            }

            string global::PCLCS.ILegacyLaunchServerData.UserName { get => this.BridgeUserName; }
            private string BridgePassword
            {
                get
                {
                    return this.Password;
                }
            }

            string global::PCLCS.ILegacyLaunchServerData.Password { get => this.BridgePassword; }
            private string BridgeBaseUrl
            {
                get
                {
                    return this.BaseUrl;
                }
            }

            string global::PCLCS.ILegacyLaunchServerData.BaseUrl { get => this.BridgeBaseUrl; }
            private string BridgeToken
            {
                get
                {
                    return this.Token;
                }
            }

            string global::PCLCS.ILegacyLaunchServerData.Token { get => this.BridgeToken; }
            private string BridgeDescription
            {
                get
                {
                    return this.Description;
                }
            }

            string global::PCLCS.ILegacyLaunchServerData.Description { get => this.BridgeDescription; }
            private bool BridgeForceReselectProfile
            {
                get
                {
                    return this.ForceReselectProfile;
                }
            }

            bool global::PCLCS.ILegacyLaunchServerData.ForceReselectProfile { get => this.BridgeForceReselectProfile; }
        }

        public class McLoginMs : global::PCL.ModLaunch.McLoginData, global::PCLCS.ILegacyLaunchMicrosoftData
        {

            /// <summary>
        /// 缓存的 OAuth Refresh Token。若没有则为空字符串。
        /// </summary>
            public global::System.String OAuthRefreshToken = "";
            public global::System.String AccessToken = "";
            public global::System.String Uuid = "";
            public global::System.String UserName = "";
            public global::System.String ProfileJson = "";

            public McLoginMs()
            {
                this.Type = global::PCL.ModLaunch.McLoginType.Ms;
            }
            public override global::System.Int32 GetHashCode()
            {
                return global::PCLCS.LegacyLaunchIdentity.MicrosoftHash(this.OAuthRefreshToken, this.UserName);
            }

            private string BridgeOAuthRefreshToken
            {
                get
                {
                    return this.OAuthRefreshToken;
                }
            }

            string global::PCLCS.ILegacyLaunchMicrosoftData.OAuthRefreshToken { get => this.BridgeOAuthRefreshToken; }
            private string BridgeAccessToken
            {
                get
                {
                    return this.AccessToken;
                }
            }

            string global::PCLCS.ILegacyLaunchMicrosoftData.AccessToken { get => this.BridgeAccessToken; }
            private string BridgeUuid
            {
                get
                {
                    return this.Uuid;
                }
            }

            string global::PCLCS.ILegacyLaunchMicrosoftData.Uuid { get => this.BridgeUuid; }
            private string BridgeUserName
            {
                get
                {
                    return this.UserName;
                }
            }

            string global::PCLCS.ILegacyLaunchMicrosoftData.UserName { get => this.BridgeUserName; }
            private string BridgeProfileJson
            {
                get
                {
                    return this.ProfileJson;
                }
            }

            string global::PCLCS.ILegacyLaunchMicrosoftData.ProfileJson { get => this.BridgeProfileJson; }
        }

        public class McLoginLegacy : global::PCL.ModLaunch.McLoginData, global::PCLCS.ILegacyLaunchOfflineData
        {
            /// <summary>
        /// 登录用户名。
        /// </summary>
            public global::System.String UserName;
            /// <summary>
        /// 皮肤种类。
        /// </summary>
            public global::System.Int32 SkinType;
            /// <summary>
        /// 若采用正版皮肤，则为该皮肤名。
        /// </summary>
            public global::System.String SkinName;

            public McLoginLegacy()
            {
                this.Type = global::PCL.ModLaunch.McLoginType.Legacy;
            }
            public override global::System.Int32 GetHashCode()
            {
                return global::PCLCS.LegacyLaunchIdentity.OfflineHash(this.UserName, this.SkinType, this.SkinName, (global::System.Int32)this.Type);
            }

            private string BridgeUserName
            {
                get
                {
                    return this.UserName;
                }
            }

            string global::PCLCS.ILegacyLaunchOfflineData.UserName { get => this.BridgeUserName; }
            private int BridgeSkinType
            {
                get
                {
                    return this.SkinType;
                }
            }

            int global::PCLCS.ILegacyLaunchOfflineData.SkinType { get => this.BridgeSkinType; }
            private string BridgeSkinName
            {
                get
                {
                    return this.SkinName;
                }
            }

            string global::PCLCS.ILegacyLaunchOfflineData.SkinName { get => this.BridgeSkinName; }
        }

        private static global::PCLCS.LegacyLaunchHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher> CreateLaunchHost()
        {
            global::PCLCS.LegacyLaunchHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher> @init = new global::PCLCS.LegacyLaunchHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModLaunch.McLaunchOptions, global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModWatcher.Watcher>();
            @init.ManagedMmcSteps = (instance, options) => MmcLaunch.CreateSteps(instance, options, java => McLaunchJavaSelected = java);
            return (@init.Options = new Func<global::PCL.ModLaunch.McLaunchOptions>(() => global::PCL.ModLaunch.CurrentLaunchOptions), @init.StoreOptions = new Action<global::PCL.ModLaunch.McLaunchOptions>(Value => global::PCL.ModLaunch.CurrentLaunchOptions = Value), @init.SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected), @init.StoreSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.ModMinecraft.McInstanceSelected = Value), @init.CreateInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Name => new global::PCL.ModMinecraft.McInstance(Name)), @init.SelectedFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected), @init.SelectedJava = new Func<global::PCLCS.Java>(() => global::PCL.ModLaunch.McLaunchJavaSelected), @init.StoreSelectedJava = new Action<global::PCLCS.Java>(Value => global::PCL.ModLaunch.McLaunchJavaSelected = Value), @init.Arguments = new Func<global::System.String>(() => global::PCL.ModLaunch.McLaunchArgument), @init.StoreArguments = new Action<global::System.String>(Value => global::PCL.ModLaunch.McLaunchArgument = Value), @init.CanceledHint = new Func<global::System.String>(() => global::PCL.ModLaunch.CanceledHint), @init.StoreCanceledHint = new Action<global::System.String>(Value => global::PCL.ModLaunch.CanceledHint = Value), @init.Encoding = new Func<global::System.Text.Encoding>(() => global::PCL.ModLaunch.McLaunchEncoding), @init.StoreEncoding = new Action<global::System.Text.Encoding>(Value => global::PCL.ModLaunch.McLaunchEncoding = Value), @init.StoreProcess = new Action<global::System.Diagnostics.Process>(Value => global::PCL.ModLaunch.McLaunchProcess = Value), @init.StoreWatcher = new Action<global::PCL.ModWatcher.Watcher>(Value => global::PCL.ModLaunch.McLaunchWatcher = Value), @init.ReadString = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.String>((Key, Instance) => global::PCL.Settings.Get<string>(Key, Instance: Instance)), @init.ReadInteger = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Int32>((Key, Instance) => (Key == "LoginType") ? (((global::System.Int32)global::PCL.Settings.Get<McLoginType>(Key))) : (global::PCL.Settings.Get<int>(Key, Instance: Instance))), @init.ReadBoolean = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Boolean>((Key, Instance) => global::PCL.Settings.Get<bool>(Key, Instance: Instance)), @init.WriteSetting = new Action<global::System.String, global::System.Object>((Key, Value) => global::PCL.Settings.Set(Key, Value)), @init.ReadIni = new Func<global::System.String, global::System.String, global::System.String, global::System.String>((File, Key, DefaultValue) => global::PCL.ModBase.ReadIni(File, Key, DefaultValue)), @init.WriteIni = new Action<global::System.String, global::System.String, global::System.String>((File, Key, Value) => global::PCL.ModBase.WriteIni(File, Key, Value)), @init.ClearIniCache = global::PCL.ModBase.IniClearCache, @init.AccountSnapshot = new Func<global::System.String, global::System.Collections.Generic.Dictionary<global::System.String, global::System.Object>>(Prefix => global::PCL.Settings.AccountSnapshot(Prefix)), @init.SaveMicrosoftSession = new Action<global::System.Collections.Generic.IDictionary<global::System.String, global::System.Object>, global::System.String, global::System.Func<global::System.Boolean>>((Values, PreviousName, Canceled) => global::PCL.Settings.SaveMicrosoftSession(Values, PreviousName, Canceled)), @init.SaveAccountGroup = new Action<global::System.Collections.Generic.IDictionary<global::System.String, global::System.Object>, global::System.Func<global::System.Boolean>>((Values, Canceled) => global::PCL.Settings.SaveAccountGroup(Values, Canceled)), @init.UpdateServerAccounts = new Action<global::System.String, global::System.String, global::System.String, global::System.Boolean>((Token, Name, Password, Remove) => global::PCL.Settings.UpdateServerAccounts(Token, Name, Password, Remove)), @init.UpdateLegacyNames = new Action<global::System.Action<global::System.Collections.Generic.List<global::System.String>>>(Change => global::PCL.Settings.UpdateLegacyNames(Change)), @init.OnUiThread = new Func<global::System.Boolean>(() => global::PCL.ModBase.RunInUi()), @init.Debug = new Func<global::System.Boolean>(() => global::PCL.ModBase.ModeDebug), @init.ReleaseBuild = new Func<global::System.Boolean>(() => (global::PCL.ModBase.BuildType == global::PCL.ModBase.BuildTypes.Release)), @init.IsGbkEncoding = new Func<global::System.Boolean>(() => global::PCL.ModBase.IsGBKEncoding), @init.PackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled), @init.CleaningFiles = new Func<global::System.Boolean>(() => global::PCL.PageOtherTest.IsCleaningGameFiles), @init.HasRunningMinecraft = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft), @init.PackAllowsInstance = new Func<global::System.String, global::System.Boolean>(Folder => global::PCL.ModModpackMode.AllowsInstance(Folder)), @init.WorldBackupsBusy = new Func<global::System.Boolean>(() => global::PCL.WorldBackups.IsBusy), @init.WorldBackupsUiBusy = new Func<global::System.Boolean>(() => global::PCL.WorldBackupsUi.IsBusy), @init.ClonerUiBusy = new Func<global::System.Boolean>(() => global::PCL.InstanceClonerUi.IsBusy), @init.HistoryUiBusy = new Func<global::System.Boolean>(() => global::PCL.EnvironmentHistoryUi.IsBusy), @init.ModpackUpdating = new Func<global::System.Boolean>(() => global::PCL.ModpackUpdateFlow.IsUpdating), @init.InstanceLocked = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Instance => global::PCL.ModMinecraft.McInstanceLock.IsLocked(Instance)), @init.PendingChanges = new Func<global::System.String, global::System.Int32>(Folder => global::PCL.ModpackUpdate.Pending(Folder).Count), @init.AcquireInstance = new Func<global::MeloongCore.IWorker, global::System.String, global::System.Boolean>((Worker, Folder) => global::PCL.ModMinecraft.McInstanceLock.TryAcquire(Worker, Folder)), @init.Dispatch = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)), @init.DispatchWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(Work)), @init.NewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)), @init.RefreshButtons = new Action(() => global::PCL.ModMain.FrmLaunchLeft.RefreshButtonsUI()), @init.RefreshPageAfterSwitch = new Action(() => global::PCL.ModMain.FrmLaunchLeft.RefreshPage(false, false)), @init.AprilGiveup = new Action(() => global::PCL.ModMain.FrmMain.AprilGiveup()), @init.RemoveInstanceSelectionPage = global::PCL.ModLaunch.RemoveInstanceSelectionPage, @init.LoginPage = new Action(() => global::PCL.ModMain.FrmLaunchLeft.PageChangeToLogin()), @init.ClearLaunchLog = new Action(() => global::PCL.ModMain.FrmLaunchRight.LabLog.Text = ""), @init.LaunchingPage = new Action(() => global::PCL.ModMain.FrmLaunchLeft.PageChangeToLaunching()), @init.RefreshLaunching = new Action(() => global::PCL.ModMain.FrmLaunchLeft.Dispatcher.Invoke(global::PCL.ModMain.FrmLaunchLeft.LaunchingRefresh)), @init.RefreshRecentVisibility = new Action(() => global::PCL.ModMain.FrmLaunchRight?.UpdateRecentWorldVisibility()), @init.RefreshLoginPage = new Action(() => global::PCL.ModMain.FrmLaunchLeft.RefreshPage(true, false)), @init.RefreshRecentWorlds = new Action(() => global::PCL.ModMain.FrmLaunchRight?.RefreshRecentWorlds()), @init.RefreshKeybindings = global::PCL.ModLaunch.RefreshVisibleKeybindings, @init.AppendLog = new Action<global::System.String>(Text => global::PCL.ModMain.FrmLaunchRight.LabLog.AppendText(Text)), @init.Hint = global::PCL.ModLaunch.PublishLaunchHint, @init.Message = global::PCL.ModLaunch.ShowLaunchMessage, @init.OpenWebsite = global::PCL.ModBase.OpenWebsite, @init.OpenExplorer = new Action<global::System.String>(Address => global::PCL.ModBase.OpenExplorer(Address)), @init.CurrentLoginValidation = global::PCL.ModLaunch.CurrentLoginValidation, @init.LoginValidation = global::PCL.ModLaunch.ValidateLoginData, @init.LoginInput = global::PCL.ModLaunch.ReadLoginInput, @init.ExitServerLogin = global::PCL.ModLaunch.ExitServerLogin, @init.SelectProfile = global::PCL.ModLaunch.SelectLoginProfile, @init.DeviceLoginPrompt = global::PCL.ModLaunch.CreateDevicePrompt, @init.Request = global::PCL.ModLaunch.SendLaunchRequest, @init.OAuthClientId = new Func<global::System.String>(() => global::PCL.ModSecret.OAuthClientId), @init.TempFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp), @init.PureFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathPure.Value), @init.ImagePath = new Func<global::System.String>(() => global::PCL.ModBase.PathImage), @init.ExecutablePath = new Func<global::System.String>(() => global::PCL.ModBase.PathExe), @init.OsDrive = new Func<global::System.String>(() => global::PCL.ModBase.OsDrive), @init.VersionDisplay = new Func<global::System.String>(() => global::PCL.ModBase.VersionDisplay), @init.VersionCode = new Func<global::System.String>(() => (global::PCL.ModBase.VersionCode).ToString()), @init.Dpi = new Func<global::System.Double>(() => global::PCL.ModBase.DPI), @init.LauncherSize = global::PCL.ModLaunch.LauncherPixelSize, @init.AvailablePhysicalMemory = new Func<global::System.UInt64>(() => global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory), @init.RamGb = new Func<global::PCL.ModMinecraft.McInstance, global::System.Double>(Instance => global::PCL.PageInstanceSetup.GetRam(Instance)), @init.IsAdmin = new Func<global::System.Boolean>(() => global::MeloongCore.WindowsUtils.HasAdminRole()), @init.SetGpuPreference = global::PCL.ModMain.SetGPUPreference, @init.RunAdminTaskDone = new Func<global::System.String, global::System.Boolean>(Arguments => ((global::PCL.ModBase.RunAsAdmin(Arguments)) == (global::System.Int32)(global::PCL.ModBase.ProcessReturnValues.TaskDone))), @init.StartProcess = new Func<global::System.Diagnostics.ProcessStartInfo, global::System.Diagnostics.Process>(Info => global::PCL.ModBase.StartProcess(Info)), @init.LoadImage = global::PCL.ModLaunch.LoadLaunchImage, @init.ArgumentReplace = new Func<global::System.String, global::System.Boolean, global::System.String>((Text, ReplaceTime) => global::PCL.ModMain.ArgumentReplace(Text, ReplaceTime: ReplaceTime)), @init.FilterAccessToken = global::PCL.ModMinecraft.FilterAccessToken, @init.FilterUserName = global::PCL.ModMinecraft.FilterUserName, @init.Libraries = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean, global::System.Collections.Generic.IEnumerable<global::PCLCS.LegacyMinecraftLibrary>>((Instance, MainJar) => global::PCL.ModMinecraft.McLibListGet(Instance, MainJar)), @init.JsonRuleCheck = global::PCL.ModMinecraft.McJsonRuleCheck, @init.AssetsIndexName = global::PCL.ModMinecraft.McAssetsGetIndexName, @init.CreateLauncherProfiles = global::PCL.ModMinecraft.McFolderLauncherProfilesJsonCreate, @init.SelectJava = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean, global::System.Threading.CancellationToken, global::MeloongCore.ProgressProvider, global::PCLCS.Java>((Instance, Fix, Token, Progress) => global::PCL.ModJava.SelectOrDownloadJava(Instance, Fix, Token, Progress)), @init.JavaRequirement = new Func<global::PCL.ModMinecraft.McInstance, global::MeloongCore.ValueRange<global::System.Version>>(Instance => global::PCL.ModJava.GetJavaRequirement(Instance).Range), @init.LaunchLoader = new Func<global::PCLCS.LegacyLaunchTask<global::PCL.ModLaunch.McLaunchOptions, global::System.Object>>(() => TaskPort(global::PCL.ModLaunch.McLaunchLoader)), @init.LoginLoader = new Func<global::PCLCS.LegacyLaunchTask<global::PCL.ModLaunch.McLoginData, global::PCLCS.ILegacyLaunchLoginResult>>(() => LoginPort(global::PCL.ModLaunch.McLoginLoader)), @init.MicrosoftLoader = new Func<global::PCLCS.LegacyLaunchTask<global::PCL.ModLaunch.McLoginMs, global::PCLCS.ILegacyLaunchLoginResult>>(() => LoginPort(global::PCL.ModLaunch.McLoginMsLoader)), @init.OfflineLoader = new Func<global::PCLCS.LegacyLaunchTask<global::PCL.ModLaunch.McLoginLegacy, global::PCLCS.ILegacyLaunchLoginResult>>(() => LoginPort(global::PCL.ModLaunch.McLoginLegacyLoader)), @init.ServerLoader = new Func<global::System.Int32, global::PCLCS.LegacyLaunchTask<global::PCL.ModLaunch.McLoginServer, global::PCLCS.ILegacyLaunchLoginResult>>(Kind => LoginPort(((Kind) == (((global::System.Int32)global::PCL.ModLaunch.McLoginType.Nide))) ? (global::PCL.ModLaunch.McLoginNideLoader) : (global::PCL.ModLaunch.McLoginAuthLoader))), @init.ClientFix = global::PCL.ModLaunch.ClientFixStep, @init.CreateCombo = global::PCL.ModLaunch.CreateLaunchCombo, @init.StoreLaunchOperation = new Action<global::PCLCS.LegacyLaunchOperation>(Operation => global::PCL.ModLaunch.McLaunchLoaderReal = (global::PCL.ModLoader.LoaderCombo<global::System.Object>)Operation.Source), @init.AddTaskbar = new Action<global::PCLCS.LegacyLaunchOperation>(Operation => global::PCL.ModLoader.LoaderTaskbarAdd((global::PCL.ModLoader.LoaderCombo<global::System.Object>)Operation.Source)), @init.DependencyContext = global::PCL.ModLaunch.DependencyContext, @init.ConfirmDependencies = global::PCL.ModLaunch.ConfirmDependencies, @init.OpenMods = global::PCL.ModLaunch.OpenDependencyMods, @init.MemoryOptimize = new Action(() => global::PCL.PageOtherTest.MemoryOptimize(false)), @init.RandomInteger = global::PCL.ModBase.RandomInteger, @init.RecoverWorlds = new Action<global::System.String, global::System.Threading.CancellationToken>((Folder, Token) => global::PCL.WorldBackups.RecoverBeforeLaunch(Folder, Token)), @init.PreparePersonal = new Func<global::PCL.ModMinecraft.McInstance, global::PCLCS.PersonalOptionsSessionData>(Instance => global::PCL.PersonalGameSettings.Prepare(Instance)), @init.TrackPersonal = new Action<global::PCLCS.PersonalOptionsSessionData, global::System.Diagnostics.Process>((Session, Game) => global::PCL.PersonalGameSettings.Track((global::PCL.PersonalOptionsSession)Session, Game)), @init.FinishPersonal = new Action<global::PCLCS.PersonalOptionsSessionData>(Session => global::PCL.PersonalGameSettings.Finish((global::PCL.PersonalOptionsSession)Session)), @init.PrepareQuickPlay = new Func<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Collections.Generic.List<global::System.String>, global::System.Boolean, global::PCLCS.QuickPlaySessionData>((Instance, Identifier, Arguments, SaveBatch) => global::PCL.QuickPlayWorlds.Prepare(Instance, Identifier, Arguments, SaveBatch)), @init.TrackQuickPlay = new Action<global::PCLCS.QuickPlaySessionData, global::System.Diagnostics.Process>((Session, Game) => global::PCL.QuickPlayWorlds.Track((global::PCL.QuickPlaySession)Session, Game)), @init.FinishQuickPlay = new Action<global::PCLCS.QuickPlaySessionData>(Session => global::PCL.QuickPlayWorlds.Finish((global::PCL.QuickPlaySession)Session)), @init.TrackWorlds = new Action<global::PCL.ModMinecraft.McInstance, global::System.Diagnostics.Process>((Instance, Game) => global::PCL.WorldBackups.Track(Instance, Game)), @init.CaptureEnvironment = new Func<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Threading.CancellationToken, global::System.String>((Instance, Reason, Token) => global::PCL.EnvironmentHistory.CaptureInstance(Instance, Reason, Token)), @init.CreateWatcher = new Func<global::PCLCS.LegacyLaunchTask<global::System.Diagnostics.Process, global::System.Int32>, global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Action<global::System.Diagnostics.Process>, global::System.Action, global::System.String, global::PCL.ModWatcher.Watcher>((Loader, Instance, Title, BeforeMonitor, Stopped, Point) => new global::PCL.ModWatcher.Watcher((global::PCL.ModLoader.LoaderTask<global::System.Diagnostics.Process, global::System.Int32>)Loader.Source, Instance, Title, BeforeMonitor, Stopped, Point)), @init.WatcherState = new Func<global::PCL.ModWatcher.Watcher, global::System.Int32>(Value => (global::System.Int32)Value.State), @init.MusicPause = new Func<global::System.Boolean>(() => global::PCL.ModMusic.MusicPause()), @init.MusicResume = new Func<global::System.Boolean>(() => global::PCL.ModMusic.MusicResume()), @init.EndProgram = new Action(() => global::PCL.ModMain.FrmMain.EndProgram(false)), @init.HideLauncher = new Action(() => global::PCL.ModMain.FrmMain.Hidden = true), @init.MinimizeLauncher = new Action(() => global::PCL.ModMain.FrmMain.WindowState = global::System.Windows.WindowState.Minimized), @init).@init;
        }

        private static void FillPort(global::PCLCS.LegacyLaunchTaskPort Port, global::PCL.ModLoader.LoaderBase Raw, global::System.Func<global::System.Boolean> ForceRestarting = null, global::System.Func<global::System.Boolean> Canceled = null, global::System.Func<global::System.Threading.Thread, global::System.Boolean> CanceledWithThread = null)
        {
            Port.Source = Raw;
            Port.ReadState = new Func<global::PCLCS.LegacyLaunchState>(() => ((global::PCLCS.LegacyLaunchState)(((global::System.Int32)Raw.State))));
            Port.WriteState = new Action<global::PCLCS.LegacyLaunchState>(Value => Raw.State = ((global::PCL.ModBase.LoadState)(((global::System.Int32)Value))));
            Port.ReadError = new Func<global::System.Exception>(() => Raw.Error);
            Port.ReadCanceled = (Canceled) ?? (() => (Raw.State == global::PCL.ModBase.LoadState.Canceled));
            Port.ReadForceRestarting = (ForceRestarting) ?? (() => false);
            Port.CanceledWithThread = (CanceledWithThread) ?? (Worker => (Raw.State == global::PCL.ModBase.LoadState.Canceled));
            Port.ReadProgress = new Func<global::System.Double>(() => Raw.Progress);
            Port.WriteProgress = new Action<global::System.Double>(Value => Raw.Progress = Value);
            Port.CreateCancellationToken = new Func<global::System.Threading.CancellationToken>(() => Raw.CreateCancellationToken());
            Port.CreateSyncProgressProvider = new Func<global::MeloongCore.ProgressProvider>(() => Raw.CreateSyncProgressProvider());
            Port.Cancel = new Action(() => Raw.Cancel());
            Port.CancelParent = new Action(() => Raw.Parent.Cancel());
        }
        private static global::PCLCS.LegacyLaunchTask<TInput, TOutput> TaskPort<TInput, TOutput>(global::PCL.ModLoader.LoaderTask<TInput, TOutput> Raw)
        {
            var Port = new global::PCLCS.LegacyLaunchTask<TInput, TOutput>()
            {
                ReadInput = new Func<TInput>(() => Raw.Input),
                ReadOutput = new Func<TOutput>(() => Raw.Output),
                WriteOutput = new Action<TOutput>(Value => Raw.Output = Value),
                StartWithInput = new Action<TInput, global::System.Boolean>((Input, Force) => Raw.Start(Input, Force)),
                Wait = new Action<TInput, global::PCLCS.LegacyLaunchTaskPort, global::System.Boolean, global::System.Threading.CancellationToken, global::MeloongCore.ProgressProvider>((Input, ProgressSource, Force, Token, Progress) => Raw.WaitForExit(Input, ProgressSource is null ? (global::PCL.ModLoader.LoaderBase)(null) : (((global::PCL.ModLoader.LoaderBase)ProgressSource.Source)), Force, Token, Progress))
            };
            global::PCL.ModLaunch.FillPort(Port, Raw, () => Raw.IsForceRestarting, () => Raw.IsCanceled, Worker => Raw.IsCanceledWithThread(Worker));
            return Port;
        }
        private static global::PCLCS.LegacyLaunchTask<TInput, global::PCLCS.ILegacyLaunchLoginResult> LoginPort<TInput>(global::PCL.ModLoader.LoaderTask<TInput, global::PCL.ModLaunch.McLoginResult> Raw)
        {
            var Result = new global::PCLCS.LegacyLaunchLoginResultPort()
            {
                ReadName = new Func<global::System.String>(() => Raw.Output.Name),
                WriteName = new Action<global::System.String>(Value => Raw.Output.Name = Value),
                ReadUuid = new Func<global::System.String>(() => Raw.Output.Uuid),
                WriteUuid = new Action<global::System.String>(Value => Raw.Output.Uuid = Value),
                ReadAccessToken = new Func<global::System.String>(() => Raw.Output.AccessToken),
                WriteAccessToken = new Action<global::System.String>(Value => Raw.Output.AccessToken = Value),
                ReadType = new Func<global::System.String>(() => Raw.Output.Type),
                WriteType = new Action<global::System.String>(Value => Raw.Output.Type = Value),
                ReadClientToken = new Func<global::System.String>(() => Raw.Output.ClientToken),
                WriteClientToken = new Action<global::System.String>(Value => Raw.Output.ClientToken = Value),
                ReadProfileJson = new Func<global::System.String>(() => Raw.Output.ProfileJson),
                WriteProfileJson = new Action<global::System.String>(Value => Raw.Output.ProfileJson = Value)
            };
            var Port = new global::PCLCS.LegacyLaunchTask<TInput, global::PCLCS.ILegacyLaunchLoginResult>()
            {
                ReadInput = new Func<TInput>(() => Raw.Input),
                ReadOutput = new Func<global::PCLCS.ILegacyLaunchLoginResult>(() => Result),
                WriteOutput = new Action<global::PCLCS.ILegacyLaunchLoginResult>(Value => Raw.Output = new global::PCL.ModLaunch.McLoginResult()
                {
                    Name = Value.Name,
                    Uuid = Value.Uuid,
                    AccessToken = Value.AccessToken,
                    Type = Value.Type,
                    ClientToken = Value.ClientToken,
                    ProfileJson = Value.ProfileJson
                }),
                StartWithInput = new Action<TInput, global::System.Boolean>((Input, Force) => Raw.Start(Input, Force)),
                Wait = new Action<TInput, global::PCLCS.LegacyLaunchTaskPort, global::System.Boolean, global::System.Threading.CancellationToken, global::MeloongCore.ProgressProvider>((Input, ProgressSource, Force, Token, Progress) => Raw.WaitForExit(Input, ProgressSource is null ? (global::PCL.ModLoader.LoaderBase)(null) : (((global::PCL.ModLoader.LoaderBase)ProgressSource.Source)), Force, Token, Progress))
            };
            global::PCL.ModLaunch.FillPort(Port, Raw, () => Raw.IsForceRestarting, () => Raw.IsCanceled, Worker => Raw.IsCanceledWithThread(Worker));
            return Port;
        }
        private static global::PCLCS.LegacyLaunchOperation OperationPort<TInput>(global::PCL.ModLoader.LoaderCombo<TInput> Raw)
        {
            var Port = new global::PCLCS.LegacyLaunchOperation() { Start = new Action(() => Raw.Start()) };
            global::PCL.ModLaunch.FillPort(Port, Raw);
            return Port;
        }
        private static global::PCLCS.LegacyLaunchStep ClientFixStep(global::PCL.ModMinecraft.McInstance Instance)
        {
            return new global::PCLCS.LegacyLaunchExistingStep() { Name = "补全文件", Task = OperationPort(new global::PCL.ModLoader.LoaderCombo<global::System.String>("补全文件", (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)global::PCL.ModDownload.DlClientFix(Instance, false, true))), PreserveMetadata = false };
        }
        private static global::PCLCS.LegacyLaunchOperation CreateLaunchCombo(global::System.String Name, global::System.Collections.Generic.IList<global::PCLCS.LegacyLaunchStep> Steps)
        {
            var Children = new global::System.Collections.Generic.List<global::PCL.ModLoader.LoaderBase>();
            foreach (var Step in Steps)
            {
                global::PCL.ModLoader.LoaderBase Raw;
                if (Step is global::PCLCS.LegacyLaunchExistingStep)
                {
                    Raw = (global::PCL.ModLoader.LoaderBase)((global::PCLCS.LegacyLaunchExistingStep)Step).Task.Source;
                    if (((global::PCLCS.LegacyLaunchExistingStep)Step).PreserveMetadata)
                    {
                        Children.Add(Raw);
                        continue;
                    }
                }
                else if (Step is global::PCLCS.LegacyLaunchIntStep)
                {
                    var Work = ((global::PCLCS.LegacyLaunchIntStep)Step).Work;
                    Raw = new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>(Step.Name, Task => Work(TaskPort(Task))) { InterruptOnCancel = Step.InterruptOnCancel };
                }
                else if (Step is global::PCLCS.LegacyLaunchStringStep)
                {
                    var Work = ((global::PCLCS.LegacyLaunchStringStep)Step).Work;
                    Raw = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Int32>(Step.Name, Task => Work(TaskPort(Task))) { InterruptOnCancel = Step.InterruptOnCancel };
                }
                else if (Step is global::PCLCS.LegacyLaunchProcessStep)
                {
                    var Work = ((global::PCLCS.LegacyLaunchProcessStep)Step).Work;
                    Raw = new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Diagnostics.Process>(Step.Name, Task => Work(TaskPort(Task))) { InterruptOnCancel = Step.InterruptOnCancel };
                }
                else if (Step is global::PCLCS.LegacyLaunchWaitStep)
                {
                    var Work = ((global::PCLCS.LegacyLaunchWaitStep)Step).Work;
                    Raw = new global::PCL.ModLoader.LoaderTask<global::System.Diagnostics.Process, global::System.Int32>(Step.Name, Task => Work(TaskPort(Task))) { InterruptOnCancel = Step.InterruptOnCancel };
                }
                else
                {
                    throw new global::System.InvalidOperationException("未知启动步骤类型：" + Step.GetType().FullName);
                }
                Raw.ProgressWeight = Step.ProgressWeight;
                Raw.Block = Step.Block;
                Raw.Show = Step.Show;
                Children.Add(Raw);
            }
            return OperationPort(new global::PCL.ModLoader.LoaderCombo<global::System.Object>(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModLoader.LoaderBase>)Children) { Show = false });
        }

        private static void RemoveInstanceSelectionPage()
        {
            global::PCL.ModMain.FrmMain.PageStack = global::PCL.ModMain.FrmMain.PageStack.Where(Page => (Page.Page != global::PCL.FormMain.PageType.InstanceSelect)).ToList();
        }
        private static void RefreshVisibleKeybindings()
        {
            if (global::PCL.ModMain.FrmMain?.PageRight is global::PCL.PageInstanceKeybindings)
                ((global::PCL.PageInstanceKeybindings)global::PCL.ModMain.FrmMain.PageRight).RefreshBindings();
        }
        private static void PublishLaunchHint(global::System.String Text, global::PCLCS.LegacyLaunchHint Kind)
        {
            switch (Kind)
            {
                case global::PCLCS.LegacyLaunchHint.Red:
                    {
                        global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red);
                        break;
                    }
                case global::PCLCS.LegacyLaunchHint.Green:
                    {
                        global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green);
                        break;
                    }
                case global::PCLCS.LegacyLaunchHint.Blue:
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
        private static global::System.Int32 ShowLaunchMessage(global::PCLCS.LegacyLaunchMessage Message)
        {
            return global::PCL.ModMain.MyMsgBox(Message.Text, Message.Title, Message.Button1, Message.Button2, Message.Button3, IsWarn: Message.IsWarn, HighLight: Message.HighLight, ForceWait: Message.ForceWait, Button1Action: Message.Button1Action, Button2Action: Message.Button2Action, Button3Action: Message.Button3Action);
        }
        private static global::System.String CurrentLoginValidation(global::System.Int32 Kind)
        {
            switch (((global::PCL.ModLaunch.McLoginType)(Kind)))
            {
                case global::PCL.ModLaunch.McLoginType.Ms:
                    {
                        return global::PCL.ModMain.FrmLoginMs.IsVaild();
                    }
                case global::PCL.ModLaunch.McLoginType.Legacy:
                    {
                        return global::PCL.ModMain.FrmLoginLegacy.IsVaild();
                    }
                case global::PCL.ModLaunch.McLoginType.Nide:
                    {
                        return global::PCL.ModMain.FrmLoginNide.IsVaild();
                    }
                case global::PCL.ModLaunch.McLoginType.Auth:
                    {
                        return global::PCL.ModMain.FrmLoginAuth.IsVaild();
                    }

                default:
                    {
                        return "未知的登录方式";
                    }
            }
        }
        private static global::System.String ValidateLoginData(global::System.Int32 Kind, global::PCL.ModLaunch.McLoginData Data)
        {
            switch (((global::PCL.ModLaunch.McLoginType)(Kind)))
            {
                case global::PCL.ModLaunch.McLoginType.Ms:
                    {
                        return global::PCL.PageLoginMs.IsVaild((global::PCL.ModLaunch.McLoginMs)Data);
                    }
                case global::PCL.ModLaunch.McLoginType.Legacy:
                    {
                        return global::PCL.PageLoginLegacy.IsVaild((global::PCL.ModLaunch.McLoginLegacy)Data);
                    }
                case global::PCL.ModLaunch.McLoginType.Nide:
                    {
                        return global::PCL.PageLoginNide.IsVaild((global::PCL.ModLaunch.McLoginServer)Data);
                    }
                case global::PCL.ModLaunch.McLoginType.Auth:
                    {
                        return global::PCL.PageLoginAuth.IsVaild((global::PCL.ModLaunch.McLoginServer)Data);
                    }

                default:
                    {
                        return "未知的登录方式";
                    }
            }
        }
        private static global::PCL.ModLaunch.McLoginData ReadLoginInput(global::System.Int32 Kind, global::System.Boolean Skin)
        {
            switch (((global::PCL.ModLaunch.McLoginType)(Kind)))
            {
                case global::PCL.ModLaunch.McLoginType.Legacy:
                    {
                        return global::PCL.PageLoginLegacy.GetLoginData();
                    }
                case global::PCL.ModLaunch.McLoginType.Ms:
                    {
                        return (Skin) ? (global::PCL.PageLoginMsSkin.GetLoginData()) : (global::PCL.PageLoginMs.GetLoginData());
                    }
                case global::PCL.ModLaunch.McLoginType.Nide:
                    {
                        return (Skin) ? (global::PCL.PageLoginNideSkin.GetLoginData()) : (global::PCL.PageLoginNide.GetLoginData());
                    }
                case global::PCL.ModLaunch.McLoginType.Auth:
                    {
                        return (Skin) ? (global::PCL.PageLoginAuthSkin.GetLoginData()) : (global::PCL.PageLoginAuth.GetLoginData());
                    }

                default:
                    {
                        return (global::PCL.ModLaunch.McLoginData)null;
                    }
            }
        }
        private static void ExitServerLogin(global::System.Int32 Kind)
        {
            switch (((global::PCL.ModLaunch.McLoginType)(Kind)))
            {
                case global::PCL.ModLaunch.McLoginType.Auth:
                    {
                        global::PCL.PageLoginAuthSkin.ExitLogin();
                        break;
                    }
                case global::PCL.ModLaunch.McLoginType.Nide:
                    {
                        global::PCL.PageLoginNideSkin.ExitLogin();
                        break;
                    }
            }
        }
        private static global::System.Int32 SelectLoginProfile(global::Newtonsoft.Json.Linq.JArray Profiles)
        {
            var Controls = new global::System.Collections.Generic.List<global::PCL.IMyRadio>();
            foreach (var Profile in Profiles)
                Controls.Add(new global::PCL.MyRadioBox() { Text = Profile["name"].ToString() });
            global::System.Int32 Selected = (global::System.Int32)global::PCL.ModMain.MyMsgBoxSelect((global::System.Collections.Generic.IEnumerable<global::PCL.IMyRadio>)Controls, "选择使用的角色");
            return Selected;
        }
        private static global::System.Func<global::PCLCS.LegacyLaunchDeviceResult> CreateDevicePrompt(global::Newtonsoft.Json.Linq.JObject Prepared)
        {
            var Converter = new global::PCL.ModMain.MyMsgBoxConverter() { Content = Prepared, ForceWait = true, Type = global::PCL.ModMain.MyMsgBoxType.Login };
            global::PCL.ModMain.WaitingMyMsgBox.Enqueue(Converter);
            return new Func<global::PCLCS.LegacyLaunchDeviceResult>(() =>
                {
                    var Result = Converter.Result;
                    if (Result is null)
                        return (global::PCLCS.LegacyLaunchDeviceResult)null;
                    if (Result is global::PCL.ModBase.RestartException)
                        return new global::PCLCS.LegacyLaunchDeviceResult() { Kind = global::PCLCS.LegacyLaunchDeviceResultKind.Restart };
                    if (Result is global::System.Exception)
                        return new global::PCLCS.LegacyLaunchDeviceResult() { Kind = global::PCLCS.LegacyLaunchDeviceResultKind.Error, Error = (global::System.Exception)Result };
                    global::System.String[] Tokens = (string[])Result;
                    return new global::PCLCS.LegacyLaunchDeviceResult() { Kind = global::PCLCS.LegacyLaunchDeviceResultKind.Tokens, AccessToken = Tokens[0], RefreshToken = Tokens[1] };
                });
        }
        private static global::System.String SendLaunchRequest(global::PCLCS.LegacyLaunchRequest Request)
        {
            if ((Request.Kind == global::PCLCS.LegacyLaunchRequestKind.Multiple))
            {
                return global::PCL.ModNet.NetRequestByClientMultiple(Request.Url, Request.Method, ThreadCount: Request.ThreadCount.GetValueOrDefault(3), Content: Request.Content, ContentType: Request.ContentType, Headers: Request.Headers, RequireJson: Request.RequireJson, Encoding: Request.Encoding);
            }
            return global::PCL.ModNet.NetRequestByClientRetry(Request.Url, Request.Method, Content: Request.Content, ContentType: Request.ContentType, Headers: Request.Headers, RequireJson: Request.RequireJson, Encoding: Request.Encoding);
        }
        private static global::PCLCS.LegacyLaunchSize LauncherPixelSize()
        {
            return new global::PCLCS.LegacyLaunchSize(global::PCL.ModBase.GetPixelSize(global::PCL.ModMain.FrmMain.PanForm.ActualWidth), global::PCL.ModBase.GetPixelSize(global::PCL.ModMain.FrmMain.PanForm.ActualHeight));
        }
        private static global::PCLCS.LegacyLaunchImage LoadLaunchImage(global::System.String Address)
        {
            return global::PCL.ModLaunch.ImagePort(new global::PCL.MyBitmap(Address));
        }
        private static global::PCLCS.LegacyLaunchImage ImagePort(global::PCL.MyBitmap Image)
        {
            return new global::PCLCS.LegacyLaunchImage()
            {
                ReadHeight = new Func<global::System.Double>(() => Image.Pic.Height),
                Save = new Action<global::System.String>(Address => Image.Save(Address)),
                Clip = new Func<global::System.Int32, global::System.Int32, global::System.Int32, global::System.Int32, global::PCLCS.LegacyLaunchImage>((X, Y, Width, Height) => global::PCL.ModLaunch.ImagePort(Image.Clip(X, Y, Width, Height)))
            };
        }
        private static global::PCLCS.LegacyLaunchDependencyContext DependencyContext(global::PCL.ModMinecraft.McInstance Instance, global::System.String JavaVersion, global::System.String ModsFolder)
        {
            var Context = global::PCL.ModDependencyInspector.ContextForInstance(Instance, JavaVersion);
            return new global::PCLCS.LegacyLaunchDependencyContext() { Inspect = Token =>
            {
                var origin = PackDependencyOrigin.Read(Instance.PathVersion, Instance.PathIndie, ModModpackMode.Enabled,
                    ModpackUpdate.ReadManifest, ModpackUpdate.DigestForInspection, Token);
                // A pack's content hash can change without a size/mtime change. Its graph
                // must describe the same actual files as the fresh provenance snapshot.
                var report = origin.IsPack ? ModDependencyInspector.ScanRaw(ModsFolder, Context, Token, true) :
                    ModDependencyInspector.Inspect(ModsFolder, Context, Token);
                report.PackOrigin = origin;
                ModDependencyOnline.Enrich(report, ModsFolder, Token, new ModDependencyOnline.LookupOptions {
                    ComparePlatformDependencies = true, MaxSearchFiles = 0, MaxFiles = 512
                });
                Token.ThrowIfCancellationRequested();
                return DependencyReport(ModsFolder, report);
            } };
        }
        private static global::PCLCS.LegacyLaunchDependencyReport DependencyReport(global::System.String Folder, global::PCL.ModDependencyInspector.InspectionReport Report)
        {
            global::PCL.ModBase.RunInUi(() => global::PCL.ModMain.FrmInstanceMod?.ApplyLoaderWarnings());
            return new global::PCLCS.LegacyLaunchDependencyReport()
            {
                Source = Report,
                ReadHasErrors = new Func<global::System.Boolean>(() => Report.HasErrors),
                ReadNeedsAttention = () => Report.HasErrors || Report.PlatformReviewNotes().Count > 0 || Report.PackOrigin?.NeedsAttention == true ||
                    Report.PackOrigin?.IsPack == true && Report.MetadataIncomplete,
                WasAccepted = new Func<global::System.Boolean>(() => global::PCL.LaunchDependencyCheck.WasAccepted(Folder, Report)),
                Accept = new Action(() => global::PCL.LaunchDependencyCheck.Accept(Folder, Report)),
                BuildAutoFill = new Func<global::System.Threading.CancellationToken, global::PCLCS.LegacyLaunchDependencyPlan>(Token => global::PCL.ModLaunch.DependencyPlan(global::PCL.ModDependencyAutoFill.BuildPlan(Folder, Report, Token))),
                BuildActivation = new Func<global::System.Threading.CancellationToken, global::PCLCS.LegacyLaunchDependencyPlan>(Token => global::PCL.ModLaunch.DependencyPlan(global::PCL.ModDependencyActivation.Prepare(Folder, Report, Token)))
            };
        }
        private static global::PCLCS.LegacyLaunchDependencyPlan DependencyPlan(global::PCL.ModDependencyAutoFill.Plan Plan)
        {
            return new global::PCLCS.LegacyLaunchDependencyPlan()
            {
                Source = Plan,
                ReadRoot = new Func<global::System.String>(() => Plan.Root),
                ReadActionText = new Func<global::System.String>(() => Plan.ActionText),
                WriteCaption = new Action<global::System.String>(Value => Plan.Caption = Value),
                ReadItemsCount = new Func<global::System.Int32>(() => Plan.Items.Count),
                ReadEnablesCount = new Func<global::System.Int32>(() => Plan.Enables.Count),
                ConfirmAuthorReplacement = () => Plan.AuthorReplacementConfirmed = true,
                Install = new Func<global::System.Threading.CancellationToken, global::System.Int32>(Token => global::PCL.ModDependencyAutoFill.Install(Plan, Token))
            };
        }
        private static global::System.Int32 ConfirmDependencies(global::PCLCS.LegacyLaunchDependencyReport Report, global::PCLCS.LegacyLaunchDependencyPlan Plan, global::System.Boolean AfterRepair)
        {
            global::PCL.ModDependencyInspector.InspectionReport Original = (global::PCL.ModDependencyInspector.InspectionReport)Report.Source;
            if (AfterRepair)
                return global::PCL.ModMain.MyMsgBoxConfirmContent("前置已处理，仍有模组问题或整合包来源需要核对。", "启动前模组检查", "返回 Mod 管理", () => new global::PCL.ModDependencyCheckView(Original), "取消启动", "仍然启动", WarningButton: 3);
            global::PCL.ModDependencyAutoFill.Plan OriginalPlan = (global::PCL.ModDependencyAutoFill.Plan)Plan.Source;
            return global::PCL.ModMain.MyMsgBoxConfirmContent("请检查以下模组问题。", "启动前模组检查", OriginalPlan.ActionText, () => new global::PCL.ModDependencyCheckView(Original, OriginalPlan), "取消启动", "仍然启动", WarningButton: 3);
        }
        private static void OpenDependencyMods(global::PCL.ModMinecraft.McInstance Instance)
        {
            global::PCL.PageInstanceLeft.Instance = Instance;
            global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup, global::PCL.FormMain.PageSubType.InstanceMod);
        }

        public static global::System.Boolean McLaunchStart(global::PCL.ModLaunch.McLaunchOptions Options = null)
        {
            return global::PCL.ModLaunch.Service.McLaunchStart(Options);
        }
        public static void McLaunchLog(global::System.String Text, global::MeloongCore.LogLevel Level = global::MeloongCore.LogLevel.Info)
        {
            global::PCL.ModLaunch.Service.McLaunchLog(Text, Level);
        }
        private static void McLaunchState(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLaunchOptions, global::System.Object> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchState(TaskPort(Loader));
        }
        private static void McLaunchStart(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLaunchOptions, global::System.Object> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchStart(TaskPort(Loader));
        }
        private static void McLaunchDependencyCheck(global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchDependencyCheck(TaskPort(Loader));
        }
        private static void InstallLaunchDependencies(global::PCL.ModMinecraft.McInstance Instance, global::PCL.ModDependencyAutoFill.Plan Plan, global::System.Threading.CancellationToken Token)
        {
            global::PCL.ModLaunch.Service.InstallLaunchDependencies(Instance, global::PCL.ModLaunch.DependencyPlan(Plan), Token);
        }
        private static void McLaunchMemoryOptimize(global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchMemoryOptimize(TaskPort(Loader));
        }
        private static void McLaunchPrecheck()
        {
            global::PCL.ModLaunch.Service.McLaunchPrecheck();
        }
        public static global::System.String McLoginName()
        {
            return global::PCL.ModLaunch.Service.McLoginName();
        }
        public static global::System.String McLoginAble()
        {
            return global::PCL.ModLaunch.Service.McLoginAble();
        }
        public static global::System.String McLoginAble(global::PCL.ModLaunch.McLoginData LoginData)
        {
            return global::PCL.ModLaunch.Service.McLoginAble(LoginData);
        }
        public static global::PCL.ModLaunch.McLoginData McLoginInput()
        {
            return global::PCL.ModLaunch.Service.McLoginInput();
        }
        private static void McLoginStart(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginData, global::PCL.ModLaunch.McLoginResult> Data)
        {
            global::PCL.ModLaunch.Service.McLoginStart(LoginPort(Data));
        }
        private static void McLoginMsStart(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginResult> Data)
        {
            global::PCL.ModLaunch.Service.McLoginMsStart(LoginPort(Data));
        }
        private static void McLoginServerStart(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult> Data)
        {
            global::PCL.ModLaunch.Service.McLoginServerStart(LoginPort(Data));
        }
        private static void McLoginLegacyStart(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginLegacy, global::PCL.ModLaunch.McLoginResult> Data)
        {
            global::PCL.ModLaunch.Service.McLoginLegacyStart(LoginPort(Data));
        }
        private static void McLoginRequestValidate(ref global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult> Data)
        {
            global::PCL.ModLaunch.Service.McLoginRequestValidate(LoginPort(Data));
        }
        private static void McLoginRequestRefresh(ref global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult> Data, global::System.Boolean RequestUser)
        {
            global::PCL.ModLaunch.Service.McLoginRequestRefresh(LoginPort(Data), RequestUser);
        }
        private static global::System.Boolean McLoginRequestLogin(ref global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginServer, global::PCL.ModLaunch.McLoginResult> Data)
        {
            return global::PCL.ModLaunch.Service.McLoginRequestLogin(LoginPort(Data));
        }
        private static (global::System.String OAuthAccessToken, global::System.String OAuthRefreshToken) MsLoginStep1New(global::PCL.ModLoader.LoaderTask<global::PCL.ModLaunch.McLoginMs, global::PCL.ModLaunch.McLoginResult> Data)
        {
            return global::PCL.ModLaunch.Service.MsLoginStep1New(LoginPort(Data));
        }
        private static (global::System.String OAuthAccessToken, global::System.String OAuthRefreshToken) MsLoginStep1Refresh(global::System.String Code)
        {
            return global::PCL.ModLaunch.Service.MsLoginStep1Refresh(Code);
        }
        private static global::System.String MsLoginStep2(global::System.String AccessToken)
        {
            return global::PCL.ModLaunch.Service.MsLoginStep2(AccessToken);
        }
        private static (global::System.String XSTSToken, global::System.String UHS) MsLoginStep3(global::System.String XBLToken)
        {
            return global::PCL.ModLaunch.Service.MsLoginStep3(XBLToken);
        }
        private static (global::System.String AccessToken, global::System.Int32 ExpiresAt) MsLoginStep4((global::System.String XSTSToken, global::System.String UHS) Tokens)
        {
            return global::PCL.ModLaunch.Service.MsLoginStep4(Tokens);
        }
        private static void MsLoginStep5(global::System.String AccessToken)
        {
            global::PCL.ModLaunch.Service.MsLoginStep5(AccessToken);
        }
        private static (global::System.String UUID, global::System.String UserName, global::System.String ProfileJson) MsLoginStep6(global::System.String AccessToken)
        {
            return global::PCL.ModLaunch.Service.MsLoginStep6(AccessToken);
        }
        private static global::System.String McLoginLegacyUuidWithCustomSkin(global::System.String UserName, global::System.Int32 SkinType, global::System.String SkinName)
        {
            return global::PCL.ModLaunch.Service.McLoginLegacyUuidWithCustomSkin(UserName, SkinType, SkinName);
        }
        public static global::System.Object McLoginMojangUuid(global::System.String Name, global::System.Boolean ThrowOnNotFound)
        {
            return global::PCL.ModLaunch.Service.McLoginMojangUuid(Name, ThrowOnNotFound);
        }
        public static global::System.Object McLoginLegacyUuid(global::System.String Name)
        {
            return global::PCL.ModLaunch.Service.McLoginLegacyUuid(Name);
        }
        private static void McLaunchJava(global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32> Task)
        {
            global::PCL.ModLaunch.Service.McLaunchJava(TaskPort(Task));
        }
        private static void McLaunchArgumentMain(global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Int32> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchArgumentMain(TaskPort(Loader));
        }
        private static global::System.Collections.Generic.List<global::System.String> McLaunchArgumentsJVM()
        {
            return global::PCL.ModLaunch.Service.McLaunchArgumentsJVM();
        }
        private static global::System.Collections.Generic.List<global::System.String> McLaunchArgumentsGame()
        {
            return global::PCL.ModLaunch.Service.McLaunchArgumentsGame();
        }
        private static global::System.Collections.Generic.IEnumerable<(global::System.String Key, global::System.String Value)> McLaunchArgumentsReplace(global::System.Collections.Generic.List<global::System.String> Arguments)
        {
            return global::PCL.ModLaunch.Service.McLaunchArgumentsReplace(Arguments);
        }
        private static global::System.Collections.Generic.IEnumerable<global::System.String> SplitJavaArguments(global::System.String Arg)
        {
            return global::PCL.ModLaunch.Service.SplitJavaArguments(Arg);
        }
        private static global::System.Collections.Generic.List<global::System.String> DeduplicateJavaArguments(global::System.Collections.Generic.List<global::System.String> Args, global::System.Boolean IsJVMArgs)
        {
            return global::PCL.ModLaunch.Service.DeduplicateJavaArguments(Args, IsJVMArgs);
        }
        private static void McLaunchNatives(global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchNatives(TaskPort(Loader));
        }
        public static global::System.String GetNativesFolder(global::PCL.ModMinecraft.McInstance Instance = null)
        {
            return global::PCL.ModLaunch.Service.GetNativesFolder(Instance);
        }
        private static void McLaunchPrerun()
        {
            global::PCL.ModLaunch.Service.McLaunchPrerun();
        }
        private static void McLaunchCustom(global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchCustom(TaskPort(Loader));
        }
        private static void McLaunchRun(global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Diagnostics.Process> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchRun(TaskPort(Loader));
        }
        private static void McLaunchWait(global::PCL.ModLoader.LoaderTask<global::System.Diagnostics.Process, global::System.Int32> Loader)
        {
            global::PCL.ModLaunch.Service.McLaunchWait(TaskPort(Loader));
        }
        private static void McLaunchEnd()
        {
            global::PCL.ModLaunch.Service.McLaunchEnd();
        }

    }
}
