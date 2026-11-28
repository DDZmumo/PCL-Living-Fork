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

    // Actual LF model/platform types and live original field access only.
    internal sealed class AuxiliaryPageRuntime
    {
        internal static readonly global::PCLCS.LegacyToolboxPageRuntime<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>> Tools = new global::PCLCS.LegacyToolboxPageRuntime<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>(new global::PCLCS.LegacyToolboxPageHost<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>()
        {
            Common = global::PCL.SetupPageRuntime.Common,
            LuckIdentify = () => ModBase.Identify,
            LuckTimeChanged = () => ModMain.FrmMain?.IsSystemTimeChanged ?? false,
            CurrentPage = new Func<global::PCLCS.LegacyToolboxPageController<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>>(() => global::PCL.ModMain.FrmOtherTest is null ? (global::PCLCS.LegacyToolboxPageController<global::PCL.ModNet.NetFile, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>)(null) : (global::PCL.ModMain.FrmOtherTest.Controller)),
            HasMain = new Func<global::System.Boolean>(() => global::PCL.ModMain.FrmMain is not null),
            RefreshTaskButton = pulse => ModMain.FrmMain?.RefreshTaskButton(pulse),
            HasRunningGame = new Func<global::System.Boolean>(() => global::PCL.ModWatcher.HasRunningMinecraft),
            LaunchLoading = new Func<global::System.Boolean>(() => (global::PCL.ModLaunch.McLaunchLoader.State == global::PCL.ModBase.LoadState.Loading)),
            HasDownloadingTask = () => global::PCL.ModNet.HasDownloadingTask(),
            HasGameFolders = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McFolderList.Any()),
            HasAdminRole = global::MeloongCore.WindowsUtils.HasAdminRole,
            StartFolderLoader = new Action(() => global::PCL.ModMinecraft.McFolderListLoader.Start()),
            WaitFolderLoader = new Action(() => global::PCL.ModMinecraft.McFolderListLoader.WaitForExit()),
            OptimizeNative = global::PCL.NativeMemoryOptimizer.Run,
            GameFolders = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>>(() => global::PCL.ModMinecraft.McFolderList.Select(Item => Item.Location)),
            AvailableMemory = new Func<global::System.Int64>(() => (global::System.Int64)global::PCL.My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory),
            RunElevated = global::PCL.ModBase.RunAsAdmin,
            AcquireCleanup = global::PCL.PageOtherTest.AcquireCleanup,
            AcquireMemory = global::PCL.PageOtherTest.AcquireMemory,
            ReadCleanup = global::PCL.PageOtherTest.ReadCleanup,
            ReleaseCleanup = global::PCL.PageOtherTest.ReleaseCleanup,
            ReleaseMemory = global::PCL.PageOtherTest.ReleaseMemory,
            NewThread = new Action<global::System.Action, global::System.String>((Work, Name) => global::PCL.ModBase.RunInNewThread(Work, Name)),
            RandomOne = new Func<global::System.Collections.Generic.ICollection<global::System.String>, global::System.String>(Values => global::PCL.ModBase.RandomOne(Values)),
            SaveFile = new Func<global::System.String, global::System.String, global::System.String>((Title, Name) => global::PCL.ModBase.RunInUiWait(() => global::MeloongCore.Wpf.Dialogs.SaveFile(Title, Name))),
            SelectFolder = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFolder("选择目标文件夹", false).FirstOrDefault()),
            StartProcess = new Action<global::System.String>(Path => global::PCL.ModBase.StartProcess(Path)),
            ValidateFolder = new Func<global::System.String, global::System.String>(Path => (new global::PCL.ValidateFolderPath(false)).Validate(Path)),
            AddValidation = new Action<global::PCLLF.Wpf.Glass.GlassValidatedTextBox, global::System.Func<global::System.String, global::System.String>>((TextBox, Rule) => TextBox.ValidateRules.Add(new global::PCL.ValidateFunc(Rule))),
            AddFolderValidation = new Action<global::PCLLF.Wpf.Glass.GlassValidatedTextBox>(TextBox => TextBox.ValidateRules.Add(new global::PCL.ValidateFolderPath(false))),
            CreateSession = new Func<global::System.String, global::System.String, global::PCLCS.LegacyToolboxDownloadSession>((Folder, Name) => global::PCL.ToolboxDownloadSession.Create(Folder, Name)),
            NewFile = new Func<global::System.String, global::System.String, global::System.Int64, global::System.Boolean, global::PCL.ModNet.NetFile>((Url, Path, Minimum, Browser) => new global::PCL.ModNet.NetFile(new[] { Url }, Path, new global::MeloongCore.FileChecker() { MinSize = Minimum }, SimulateBrowserHeaders: Browser)),
            SetCredentialHeaders = new Action<global::PCL.ModNet.NetFile, global::System.Boolean>((File, Allow) => File.AllowCredentialHeaders = Allow),
            SetMergeFinished = new Action<global::PCL.ModNet.NetFile, global::System.Action>((File, Callback) => File.CanceledMergeFinished = Callback),
            NewDownload = new Func<global::System.String, global::System.Collections.Generic.List<global::PCL.ModNet.NetFile>, global::PCL.ModNet.LoaderDownload>((Name, Files) => new global::PCL.ModNet.LoaderDownload(Name, (global::System.Collections.Generic.IEnumerable<global::PCL.ModNet.NetFile>)Files)),
            NewSave = new Func<global::System.String, global::System.Action<global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>>, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>>((Name, Work) => new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>(Name, Task => Work(Task))),
            SaveCanceled = new Func<global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::System.Boolean>(Task => Task.IsCanceled),
            SaveWeight = new Action<global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::System.Double>((Task, Weight) => Task.ProgressWeight = Weight),
            NewCombo = new Func<global::System.String, global::PCL.ModNet.LoaderDownload, global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>, global::PCL.ModLoader.LoaderCombo<global::System.Int32>>((Name, Download, Save) => new global::PCL.ModLoader.LoaderCombo<global::System.Int32>(Name, new global::PCL.ModLoader.LoaderBase[] { Download, Save })),
            ComboState = new Func<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.Int32>(Loader => (global::System.Int32)Loader.State),
            ComboName = new Func<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.String>(Loader => Loader.Name),
            ComboError = new Func<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.Exception>(Loader => Loader.Error),
            SetStateCallback = new Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>, global::System.Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>>>((Loader, Callback) => Loader.OnStateChanged = new Action<global::PCL.ModLoader.LoaderBase>(Changed => Callback((global::PCL.ModLoader.LoaderCombo<global::System.Int32>)Changed))),
            StartCombo = new Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>>(Loader => Loader.Start()),
            AddTaskbar = new Action<global::PCL.ModLoader.LoaderCombo<global::System.Int32>>(Loader => global::PCL.ModLoader.LoaderTaskbarAdd(Loader)),
            NewUuid = global::PCL.ModBase.GetUuid
        });
    }
}
