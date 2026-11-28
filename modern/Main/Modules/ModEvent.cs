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
using static global::PCL.FormMain;
using PCLCS;
using EventController = global::PCLCS.CustomEventController<global::PCL.CustomEvent.EventType, global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType, global::PCL.ModMinecraft.McInstance, global::PCL.ModMain.HintType>;
using EventHost = global::PCLCS.CustomEventHost<global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType, global::PCL.ModMinecraft.McInstance, global::PCL.ModMain.HintType>;
using ThrottleDebounce;

namespace PCL
{

    [global::System.Windows.Markup.ContentPropertyAttribute("Events")]
    public class CustomEventCollection : global::PCLCS.CustomEventCollectionData<global::PCL.CustomEvent>, global::System.Collections.Generic.IEnumerable<global::PCL.CustomEvent>
    {
        private global::System.Collections.Generic.List<global::PCL.CustomEvent> _Events = new global::System.Collections.Generic.List<global::PCL.CustomEvent>();
        protected override global::System.Collections.Generic.List<global::PCL.CustomEvent> EventStorage
        {
            get
            {
                return this._Events;
            }
        }
        public new global::System.Collections.Generic.List<global::PCL.CustomEvent> Events
        {
            get
            {
                return base.Events;
            }
        }
        public new global::System.Collections.Generic.IEnumerator<global::PCL.CustomEvent> GetEnumerator()
        {
            return base.GetEnumerator();
        }
        private global::System.Collections.IEnumerator IEnumerable_GetEnumerator()
        {
            return base.GetEnumerator();
        }

        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => this.IEnumerable_GetEnumerator();
    }

    public class CustomEventService
    {
        public static readonly global::System.Windows.DependencyProperty EventsProperty = global::PCLCS.CustomEventProperties.Collection(typeof(global::PCL.CustomEventService), typeof(global::PCL.CustomEventCollection));
        [global::System.Windows.AttachedPropertyBrowsableForTypeAttribute(typeof(global::System.Windows.DependencyObject))]
        public static void SetEvents(global::System.Windows.DependencyObject d, global::PCL.CustomEventCollection value)
        {
            global::PCLCS.CustomEventProperties.Set(d, global::PCL.CustomEventService.EventsProperty, value);
        }
        [global::System.Windows.AttachedPropertyBrowsableForTypeAttribute(typeof(global::System.Windows.DependencyObject))]
        public static global::PCL.CustomEventCollection GetEvents(global::System.Windows.DependencyObject d)
        {
            return global::PCLCS.CustomEventProperties.Collection(d, global::PCL.CustomEventService.EventsProperty, () => new global::PCL.CustomEventCollection());
        }
        public static readonly global::System.Windows.DependencyProperty EventTypeProperty = global::PCLCS.CustomEventProperties.AttachedType(typeof(global::PCL.CustomEventService), typeof(global::PCL.CustomEvent.EventType));
        [global::System.Windows.AttachedPropertyBrowsableForTypeAttribute(typeof(global::System.Windows.DependencyObject))]
        public static void SetEventType(global::System.Windows.DependencyObject d, global::PCL.CustomEvent.EventType value)
        {
            global::PCLCS.CustomEventProperties.Set(d, global::PCL.CustomEventService.EventTypeProperty, value);
        }
        [global::System.Windows.AttachedPropertyBrowsableForTypeAttribute(typeof(global::System.Windows.DependencyObject))]
        public static global::PCL.CustomEvent.EventType GetEventType(global::System.Windows.DependencyObject d)
        {
            return global::PCLCS.CustomEventProperties.Get<global::PCL.CustomEvent.EventType>(d, global::PCL.CustomEventService.EventTypeProperty);
        }
        public static readonly global::System.Windows.DependencyProperty EventDataProperty = global::PCLCS.CustomEventProperties.AttachedData(typeof(global::PCL.CustomEventService));
        [global::System.Windows.AttachedPropertyBrowsableForTypeAttribute(typeof(global::System.Windows.DependencyObject))]
        public static void SetEventData(global::System.Windows.DependencyObject d, global::System.String value)
        {
            global::PCLCS.CustomEventProperties.Set(d, global::PCL.CustomEventService.EventDataProperty, value);
        }
        [global::System.Windows.AttachedPropertyBrowsableForTypeAttribute(typeof(global::System.Windows.DependencyObject))]
        public static global::System.String GetEventData(global::System.Windows.DependencyObject d)
        {
            return global::PCLCS.CustomEventProperties.Get<string>(d, global::PCL.CustomEventService.EventDataProperty);
        }
    }

    public static partial class ModMain
    {
        public static void RaiseCustomEvent(this global::System.Windows.DependencyObject Control)
        {
            global::PCLCS.CustomEventBatch.Raise(() => global::PCL.CustomEventService.GetEvents(Control), () => global::PCL.CustomEventService.GetEventType(Control), () => global::PCL.CustomEventService.GetEventData(Control), (Type, Data) => new global::PCL.CustomEvent(Type, Data), EventValue => EventValue.Raise(), (Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name), global::PCL.ModBase.GetUuid);
        }
    }

    public class CustomEvent : global::PCLCS.CustomEventObject<global::PCL.CustomEvent.EventType>
    {
        public new global::PCL.CustomEvent.EventType Type
        {
            get
            {
                return base.Type;
            }
            set
            {
                base.Type = value;
            }
        }
        public new string Data
        {
            get
            {
                return base.Data;
            }
            set
            {
                base.Data = value;
            }
        }
        public static readonly global::System.Windows.DependencyProperty TypeProperty = global::PCLCS.CustomEventProperties.ObjectType(typeof(global::PCL.CustomEvent), global::PCL.CustomEvent.EventType.None);
        public static readonly global::System.Windows.DependencyProperty DataProperty = global::PCLCS.CustomEventProperties.ObjectData(typeof(global::PCL.CustomEvent));
        protected override global::System.Windows.DependencyProperty TypeKey
        {
            get
            {
                return global::PCL.CustomEvent.TypeProperty;
            }
        }
        protected override global::System.Windows.DependencyProperty DataKey
        {
            get
            {
                return global::PCL.CustomEvent.DataProperty;
            }
        }
        protected override void ReadInUi(global::System.Action Work)
        {
            global::PCL.ModBase.RunInUiWait(Work);
        }
        public CustomEvent()
        {
        }
        public CustomEvent(global::PCL.CustomEvent.EventType Type, global::System.String Data)
        {
            this.Type = Type;
            this.Data = Data;
        }
        public void Raise()
        {
            global::PCL.CustomEvent.Raise(this.Type, this.Data);
        }
        public enum EventType
        {
            None = 0,
            打开网页,
            打开文件,
            打开帮助,
            执行命令,
            启动游戏,
            复制文本,
            刷新主页,
            刷新页面,
            刷新帮助,
            今日人品,
            内存优化,
            清理垃圾,
            弹出窗口,
            弹出提示,
            切换页面,
            导入整合包,
            安装整合包,
            下载文件,
            修改设置,
            写入设置,
            修改变量,
            写入变量,
            加入房间,
            检查更新
        }
        private static readonly global::PCLCS.CustomEventController<global::PCL.CustomEvent.EventType, global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType, global::PCL.ModMinecraft.McInstance, global::PCL.ModMain.HintType> Controller = new global::PCLCS.CustomEventController<global::PCL.CustomEvent.EventType, global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType, global::PCL.ModMinecraft.McInstance, global::PCL.ModMain.HintType>(global::PCL.CustomEvent.CreateHost());
        private static global::PCLCS.CustomEventHost<global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType, global::PCL.ModMinecraft.McInstance, global::PCL.ModMain.HintType> CreateHost()
        {
            var Host = new global::PCLCS.CustomEventHost<global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType, global::PCL.ModMinecraft.McInstance, global::PCL.ModMain.HintType>()
            {
                RunInThread = global::PCL.ModBase.RunInThread,
                RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
                RunInUiWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(Work)),
                IsUiThread = new Func<global::System.Boolean>(() => global::PCL.ModBase.RunInUi()),
                Message = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.String, global::System.Int32>((Text, Title, First, Second, Third) => global::PCL.ModMain.MyMsgBox(Text, Title, First, Second, Third)),
                Hint = new Action<global::System.String, global::PCL.ModMain.HintType>((Text, Kind) => global::PCL.ModMain.Hint(Text, Kind)),
                Blue = global::PCL.ModMain.HintType.Blue,
                Green = global::PCL.ModMain.HintType.Green,
                Red = global::PCL.ModMain.HintType.Red,
                Info = new Action<global::System.String>(Text => global::MeloongCore.Logger.Info(Text)),
                OpenWebsite = new Action<global::System.String>(Url => global::PCL.ModBase.OpenWebsite(Url)),
                Clipboard = new Action<global::System.String>(Text => global::PCL.ModBase.ClipboardSet(Text)),
                EnterHelp = new Action<global::System.String>(Path => global::PCL.LauncherHelpCatalog.Open(Path)),
                ErrorAlert = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Error(Err, Text, global::MeloongCore.LogBehavior.Alert)),
                WarnToast = new Action<global::System.Exception, global::System.String>((Err, Text) => global::MeloongCore.Logger.Warn(Err, Text, global::MeloongCore.LogBehavior.Toast)),
                StartProcess = new Action<global::System.Diagnostics.ProcessStartInfo>(Info => global::PCL.ModBase.StartProcess(Info)),
                SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft.McInstanceSelected),
                InstanceName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Target => Target.Name),
                CreateInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Name => new global::PCL.ModMinecraft.McInstance(Name)),
                Launch = new Func<global::PCL.ModMinecraft.McInstance, global::System.String, global::System.Boolean>((Target, Server) => global::PCL.ModLaunch.McLaunchStart(new global::PCL.ModLaunch.McLaunchOptions() { ServerIp = Server, Instance = Target })),
                RefreshHome = FormMain.RefreshHomepage,
                RefreshCurrent = () =>
                {
                    if (!ModMain.FrmMain.TryRefreshDirectPage()) (ModMain.FrmMain.PageRight as IRefreshable)?.Refresh();
                },
                CanRefreshCurrent = () => ModMain.FrmMain.CanRefreshDirectPage || ModMain.FrmMain.PageRight is IRefreshable,
                RefreshHelp = new Action(() => ModMain.FrmMain?.ReloadGlassHelp()),
                Jrrp = global::PCL.PageOtherTest.Jrrp,
                MemoryOptimize = new Action(() => global::PCL.PageOtherTest.MemoryOptimize(true)),
                RubbishClear = new Action(() => global::PCL.PageOtherTest.RubbishClear()),
                InstallModpack = new Action(() => global::PCL.ModModpack.ModpackInstall()),
                CheckUpdates = global::PCL.ModSecret.UpdateCheckByButton,
                HintCustomCommand = new Func<global::System.Boolean>(() => global::PCL.Settings.Get<bool>("HintCustomCommand")),
                RememberCustomCommand = new Action(() => global::PCL.Settings.Set("HintCustomCommand", true)),
                Locked = global::PCL.ModMinecraft.McInstanceLock.BlocksNavigation,
                SetEditedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Target => global::PCL.LauncherInstanceContext.Instance = Target),
                InstanceSetup = global::PCL.FormMain.PageType.InstanceSetup,
                DefaultSubPage = global::PCL.FormMain.PageSubType.Default,
                ChangePage = new Action<global::PCL.FormMain.PageType, global::PCL.FormMain.PageSubType>((Page, SubPage) => global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)Page, SubPage)),
                Download = new Action<global::System.String, global::System.String, global::System.String>((Url, Name, Folder) => global::PCL.PageOtherTest.StartCustomDownload(Url, Name, Folder)),
                SetSetting = new Action<global::System.String, global::System.String, global::PCL.ModMinecraft.McInstance>((Key, Value, Target) => global::PCL.Settings.SetSafe(Key, Value, Target)),
                RegistryFolder = new Func<global::System.String>(() => global::PCL.ModSecret.RegFolder),
                WriteRegistry = new Action<global::System.String, global::System.String, global::System.String>((Path, Name, Value) => global::MeloongCore.RegistryUtils.Write(Path, Name, Value)),
                BaseFolder = new Func<global::System.String>(() => global::MeloongCore.Paths.Base),
                CacheFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp),
                RequestTempFolder = new Func<global::System.String>(() => global::PCL.ModMain.RequestTaskTempFolder()),
                ExtractHelp = global::PCL.ModMain.HelpTryExtract,
                FileExists = new Func<global::System.String, global::System.Boolean>(Path => global::MeloongCore.FileUtils.Exists(Path)),
                DownloadFile = new Action<global::System.String, global::System.String>((Url, Path) => global::PCL.ModNet.NetDownloadByClient(Url, Path))
            };
            #if LEGACY_LINK
            Host.LegacyLink = true;
            Host.JoinDefault = () => global::PCL.PageLinkMain.Join();
            Host.Join = arg => global::PCL.PageLinkMain.Join(arg);
#endif
            return Host;
        }
        public static void Raise(global::PCL.CustomEvent.EventType Type, global::System.String Arg)
        {
            if (Type == EventType.None) return;
            if (!LauncherContentActions.Allows(Type, Arg ?? "")) {
                ModMain.Hint("自定义页面的操作已被安全限制拦截。", ModMain.HintType.Red);
                return;
            }
            if (Type == EventType.打开帮助) {
                LauncherHelpCatalog.Open(Arg);
                return;
            }
            if (Type == EventType.打开网页) Arg = new Uri(Arg, UriKind.Absolute).AbsoluteUri;
            global::PCL.CustomEvent.Controller.Raise(Type, Arg);
        }
        public static global::System.String[] GetAbsoluteUrls(global::System.String RelativeUrl, global::PCL.CustomEvent.EventType Type)
        {
            return global::PCL.CustomEvent.Controller.GetAbsoluteUrls(RelativeUrl, Type);
        }
        private static global::System.Boolean EventSafetyConfirm(global::System.String Message)
        {
            return global::PCL.CustomEvent.Controller.SafetyConfirm(Message);
        }
    }
}
