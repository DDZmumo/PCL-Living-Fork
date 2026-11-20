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
using PCLCS;
using global::PCLLF.Presentation.Accounts;
using ThrottleDebounce;

namespace PCL
{
    public partial class MyMsgLogin
    {
        private readonly global::PCL.ModMain.MyMsgBoxConverter _modalConverter;
        private readonly global::PCLLF.Presentation.Accounts.LegacyDeviceLoginDialog<global::PCL.ModMain.MyMsgBoxConverter> Native;
        static MyMsgLogin()
        {
            global::PCL.PresentationRuntime.EnsureConfigured();
        }

        public MyMsgLogin(global::PCL.ModMain.MyMsgBoxConverter Converter)
        {
            _modalConverter = Converter;
            __LfAttachOwnHandles();
            this.Native = new global::PCLLF.Presentation.Accounts.LegacyDeviceLoginDialog<global::PCL.ModMain.MyMsgBoxConverter>(new global::PCLLF.Presentation.Accounts.LegacyDeviceLoginHost<global::PCL.ModMain.MyMsgBoxConverter>() { NewUuid = new Func<global::System.Int32>(() => global::PCL.ModBase.GetUuid()), Content = new Func<global::PCL.ModMain.MyMsgBoxConverter, global::Newtonsoft.Json.Linq.JObject>(Value => ((global::Newtonsoft.Json.Linq.JObject)(Value.Content))), IsExited = new Func<global::PCL.ModMain.MyMsgBoxConverter, global::System.Boolean>(Value => Value.IsExited), IsWarn = new Func<global::PCL.ModMain.MyMsgBoxConverter, global::System.Boolean>(Value => Value.IsWarn), SetExited = new Action<global::PCL.ModMain.MyMsgBoxConverter, global::System.Boolean>((Value, State) => Value.IsExited = State), SetResult = new Action<global::PCL.ModMain.MyMsgBoxConverter, global::System.Object>((Value, Result) => Value.Result = Result), RestartException = new Func<global::System.Exception>(() => new global::PCL.ModBase.RestartException()), OAuthClientId = new Func<global::System.String>(() => global::PCL.ModSecret.OAuthClientId), HasWaitingMessages = new Func<global::System.Boolean>(() => global::PCL.ModMain.WaitingMyMsgBox.Any()), Control = new Func<global::System.Windows.FrameworkElement>(() => this), MessagePanel = new Func<global::System.Windows.FrameworkElement>(() => global::PCL.ModMain.FrmMain.PanMsg), Position = new Func<global::System.Windows.Media.TranslateTransform>(() => this.TransformPos), Rotation = new Func<global::System.Windows.Media.RotateTransform>(() => this.TransformRotate), Title = new Func<global::System.String>(() => this.LabTitle.Text), Caption = new Func<global::System.String>(() => this.LabCaption.Text), SetTitle = new Action<global::System.String>(Value => this.LabTitle.Text = Value), SetCaption = new Action<global::System.String>(Value => this.LabCaption.Text = Value), SetWebEvent = new Action<global::System.String>(Value => global::PCL.CustomEventService.SetEventData(this.Btn1, Value)), SetCopyEvent = new Action<global::System.String>(Value => global::PCL.CustomEventService.SetEventData(this.Btn2, Value)), ButtonName = this.ReadButtonName, SetButtonName = this.WriteButtonName, SetLineThickness = new Action(() => this.ShapeLine.StrokeThickness = global::PCL.ModBase.GetWPFSize(1d)), RemoveControl = new Action(() => ((global::System.Windows.Controls.Grid)(this.Parent)).Children.Remove(this)), ShowWindowToTop = new Action(() => global::PCL.ModMain.FrmMain.ShowWindowToTop()), RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)), RunInNewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)), OpenWebsite = new Action<global::System.String>(Value => global::PCL.ModBase.OpenWebsite(Value)), ClipboardSet = new Action<global::System.String>(Value => global::PCL.ModBase.ClipboardSet(Value)), LaunchLog = new Action<global::System.String>(Value => global::PCL.ModLaunch.McLaunchLog(Value)), Hint = global::PCL.LoginPageRuntime.PublishHint, Request = new Func<global::System.String, global::System.Net.Http.HttpMethod, global::System.String, global::System.String, global::System.Int32, global::System.Boolean, global::System.Boolean, global::System.String>((Url, Method, Content, ContentType, Timeout, MakeLog, RequireJson) => global::PCL.ModNet.NetRequestByClient(Url, Method, Content: Content, ContentType: ContentType, Timeout: Timeout, MakeLog: MakeLog, RequireJson: RequireJson)) });
            try
            {
                this.InitializeComponent();
                this.Native.Initialize(Converter);
            }
            catch (global::System.Exception ex)
            {
                global::MeloongCore.Logger.Error(ex, "登录弹窗初始化失败", global::MeloongCore.LogBehavior.Toast);
            }
        }

        private global::System.String ReadButtonName(global::System.Int32 Index)
        {
            switch (Index)
            {
                case 1:
                {
                    return this.Btn1.Name;
                }

                case 2:
                {
                    return this.Btn2.Name;
                }

                default:
                {
                    return this.Btn3.Name;
                }
            }
        }

        private void WriteButtonName(global::System.Int32 Index, global::System.String Value)
        {
            switch (Index)
            {
                case 1:
                {
                    this.Btn1.Name = Value;
                    break;
                }

                case 2:
                {
                    this.Btn2.Name = Value;
                    break;
                }

                default:
                {
                    this.Btn3.Name = Value;
                    break;
                }
            }
        }

        private void Load(global::System.Object sender, global::System.EventArgs e)
        {
            this.Native.Loaded();
        }

        // 原按钮由 XAML CustomEventService 打开网页；该兼容入口保持原空处理。
        public void Btn1_Click()
        {
        }

        public void Btn3_Click()
        {
            this.Native.CancelClick();
        }

        private void Drag(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            Microsoft.VisualBasic.CompilerServices.ProjectData.ClearProjectError();
            // A failed VB If condition resumes at the Then statement, even for
            // the single-line original. The actual drag request is still made.
            bool drag = true;
            try
            {
                drag = global::PCLLF.Presentation.Accounts.LegacyDeviceLoginDialog<global::PCL.ModMain.MyMsgBoxConverter>.ShouldDrag(e.GetPosition(this.ShapeLine).Y);
            }
            catch (Exception error)
            {
                Microsoft.VisualBasic.CompilerServices.ProjectData.SetProjectError(error);
            }

            if (drag)
                try
                {
                    global::PCL.ModMain.FrmMain.DragMove();
                }
                catch (Exception error)
                {
                    Microsoft.VisualBasic.CompilerServices.ProjectData.SetProjectError(error);
                }
        }
    }
}
