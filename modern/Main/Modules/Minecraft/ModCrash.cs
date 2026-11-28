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
using ThrottleDebounce;

namespace PCL
{
    // LF 公开入口与原界面/隐私过滤服务；完整日志导入、检测与报告流程由 C# 承载。
    public class CrashAnalyzer : global::PCLCS.ILegacyCrashAnalyzer
    {
        private readonly global::PCLCS.LegacyCrashAnalyzer<global::PCL.ModMinecraft.McInstance, global::PCL.RecentEnvironmentChangesReport> Native;
        private static readonly global::PCLCS.LegacyCrashHost<global::PCL.ModMinecraft.McInstance, global::PCL.RecentEnvironmentChangesReport> Host = new global::PCLCS.LegacyCrashHost<global::PCL.ModMinecraft.McInstance, global::PCL.RecentEnvironmentChangesReport>()
        {
            RequestTaskTempFolder = new Func<global::System.String>(() => global::PCL.ModMain.RequestTaskTempFolder()),
            TempPath = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp),
            LauncherIssuesUrl = new Func<global::System.String>(() => global::PCL.ModBase.LauncherIssuesUrl),
            NewUuid = new Func<global::System.Int32>(() => global::PCL.ModBase.GetUuid()),
            ShowWindowToTop = new Action(() => global::PCL.ModMain.FrmMain.ShowWindowToTop()),
            FeedbackInfo = new Action(() => global::PCL.ModMain.FeedbackInfo()),
            BuildRecentChanges = new Func<global::PCL.ModMinecraft.McInstance, global::PCL.RecentEnvironmentChangesReport>(Instance => global::PCL.RecentEnvironmentChanges.BuildInstance(Instance, global::System.Threading.CancellationToken.None)),
            AppendRecentAnalysis = global::PCL.RecentEnvironmentChanges.AppendAnalysis,
            HasBaseline = new Func<global::PCL.RecentEnvironmentChangesReport, global::System.Boolean>(Report => Report.HasBaseline),
            ChangesText = new Func<global::PCL.RecentEnvironmentChangesReport, global::System.Int32, global::System.String>((Report, Maximum) => Report.ToAnalysisText(Maximum)),
            OpenRestorePoint = global::PCL.CrashAnalyzer.OpenRestorePoint,
            MessageBox = new Func<global::System.String, global::System.String, global::System.String, global::System.String, global::System.String, global::System.Action, global::System.Int32>((Detail, Title, Button1, Button2, Button3, Button2Action) => global::PCL.ModMain.MyMsgBox(Detail, Title, Button1, Button2, Button3, Button2Action: Button2Action)),
            SaveReportPath = new Func<global::System.String, global::System.String, global::System.String>((Title, Name) => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, Name, filter: new[] { ("zip", "Minecraft 错误报告") })),
            RunInUi = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUi(Work)),
            RunInUiWait = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInUiWait(Work)),
            StartProcess = new Action<global::System.String>(Path => global::PCL.ModBase.StartProcess(Path)),
            OpenExplorer = new Action<global::System.String>(Path => global::PCL.ModBase.OpenExplorer(Path)),
            ExportedHint = new Action<global::System.String>(Text => global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Green)),
            FilterUserName = new Func<global::System.String, global::System.Char, global::System.String>((Text, Mask) => global::PCL.ModMinecraft.FilterUserName(Text, Mask)),
            FilterAccessToken = new Func<global::System.String, global::System.Char, global::System.String>((Text, Mask) => global::PCL.ModMinecraft.FilterAccessToken(Text, Mask))
        };
        public CrashAnalyzer(global::PCL.ModMinecraft.McInstance TargetInstance = null)
        {
            this.Native = new global::PCLCS.LegacyCrashAnalyzer<global::PCL.ModMinecraft.McInstance, global::PCL.RecentEnvironmentChangesReport>(global::PCL.CrashAnalyzer.Host, TargetInstance);
        }
        public void Collect(global::System.String VersionPathIndie, global::System.Collections.Generic.IList<global::System.String> LatestLog = null)
        {
            this.Native.Collect(VersionPathIndie, LatestLog);
        }
        public void Import(global::System.String FilePath)
        {
            this.Native.Import(FilePath);
        }
        public global::System.Boolean Prepare()
        {
            return this.Native.Prepare();
        }
        public void Analyze()
        {
            this.Native.Analyze();
        }
        public void Output(global::System.Boolean IsManualAnalyze, global::System.Collections.Generic.List<global::System.String> ExtraFiles = null)
        {
            this.Native.Output(IsManualAnalyze, ExtraFiles);
        }
        private static void OpenRestorePoint(global::PCL.ModMinecraft.McInstance TargetInstance, global::PCL.RecentEnvironmentChangesReport Report)
        {
            global::PCL.LauncherInstanceContext.Instance = TargetInstance;
            var window = ModMain.FrmMain;
            var historyRoute = FormMain.PageSubType.InstanceRestorePoints;
            var page = window.GlassInstanceNavigation.GetPage(historyRoute);
            ((PageInstanceRestorePoints)page).FocusPoint(Report.BaselineId);
            window.PageChange(FormMain.PageType.InstanceSetup, historyRoute);
        }
    }
}
