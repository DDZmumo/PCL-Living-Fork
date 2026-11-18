using System;
using System.Collections;
using System.Collections.Concurrent;
using global::System.Collections.Generic;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using global::System.Linq;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
using Newtonsoft.Json.Linq;
using PCLCS;
using ThrottleDebounce;

namespace PCL
{

    // 保留 LF Java 公共入口及原 WithEvents 工作器；规则、设置迁移、选择和下载计划由 C# 单实现维护。
    public static class ModJava
    {
        /// <summary>搜索、检查并缓存 Java，完成后由原事件刷新设置页。</summary>
        private static global::MeloongCore.RedoableWorker _JavaListRefreshWorker;

        public static global::MeloongCore.RedoableWorker JavaListRefreshWorker
        {
            [MethodImpl(MethodImplOptions.Synchronized)]
            get
            {
                return _JavaListRefreshWorker;
            }

            [MethodImpl(MethodImplOptions.Synchronized)]
            set
            {
                if (_JavaListRefreshWorker != null)
                {
                    _JavaListRefreshWorker.Started -= UpdateJavaLists;
                    _JavaListRefreshWorker.Stopped -= UpdateJavaLists;
                }

                _JavaListRefreshWorker = value;
                if (_JavaListRefreshWorker != null)
                {
                    _JavaListRefreshWorker.Started += UpdateJavaLists;
                    _JavaListRefreshWorker.Stopped += UpdateJavaLists;
                }
            }
        }
        private readonly static global::PCLCS.LegacyJavaService<global::PCL.ModMinecraft.McInstance> Service = new global::PCLCS.LegacyJavaService<global::PCL.ModMinecraft.McInstance>(global::PCL.ModJava.CreateJavaHost());

        static ModJava()
        {
            JavaListRefreshWorker = new global::MeloongCore.RedoableWorker((c, p) => global::PCLCS.JavaUtils.RefreshListAsync((global::System.Threading.CancellationToken)((c)), p).Run());
        }

        private static global::PCLCS.LegacyJavaHost<global::PCL.ModMinecraft.McInstance> CreateJavaHost()
        {
            return new global::PCLCS.LegacyJavaHost<global::PCL.ModMinecraft.McInstance>()
            {
                ReadString = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.String>((Key, Instance) => global::PCL.Settings.Get<string>(Key, Instance: Instance)),
                ReadInteger = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Int32>((Key, Instance) => global::PCL.Settings.Get<int>(Key, Instance: Instance)),
                WriteInteger = new Action<global::System.String, global::System.Int32, global::PCL.ModMinecraft.McInstance>((Key, Value, Instance) => global::PCL.Settings.Set(Key, Value, Instance: Instance)),
                Config = new Func<global::PCL.ModMinecraft.McInstance, global::MeloongCore.IConfigProvider>(Instance => Instance.Config),
                Version = new Func<global::PCL.ModMinecraft.McInstance, global::PCLCS.McVersion>(Instance => Instance.Version),
                VersionPath = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.PathVersion),
                InstanceName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.Name),
                VersionDisplayName = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Instance => Instance.VersionDisplayName()),
                ReleaseTime = new Func<global::PCL.ModMinecraft.McInstance, global::System.DateTime>(Instance => Instance.ReleaseTime),
                JsonObject = new Func<global::PCL.ModMinecraft.McInstance, global::Newtonsoft.Json.Linq.JObject>(Instance => Instance.JsonObject),
                JsonVersion = new Func<global::PCL.ModMinecraft.McInstance, global::Newtonsoft.Json.Linq.JObject>(Instance => Instance.JsonVersion),
                IsNideLogin = new Func<global::System.Boolean>(() => (global::PCL.Settings.Get<global::PCL.ModLaunch.McLoginType>("LoginType") == global::PCL.ModLaunch.McLoginType.Nide)),
                RefreshWorker = new Func<global::MeloongCore.RedoableWorker>(() => global::PCL.ModJava.JavaListRefreshWorker),
                UpdateJavaLists = global::PCL.ModJava.UpdateJavaLists,
                SelectJavaExecutable = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFile("选择 java.exe（可能在 bin 文件夹中）", false, filter: "java.exe|java.exe").FirstOrDefault()),
                Hint = global::PCL.ModJava.PublishJavaHint,
                ReloadCurrentJava = new Action(() => global::PCL.PageInstanceLeft.ReloadCurrentJava()),
                RunInThread = new Action<global::System.Action>(Work => global::PCL.ModBase.RunInThread(Work)),
                SwitchToInstanceSetup = global::PCL.ModJava.SwitchToInstanceSetup,
                Message = new Action<global::System.String, global::System.String, global::System.Boolean, global::System.Boolean>((Caption, Title, IsWarn, ForceWait) => global::PCL.ModMain.MyMsgBox(Caption, Title, IsWarn: IsWarn, ForceWait: ForceWait)),
                DisableJavaSettings = global::PCL.ModJava.DisableJavaSettings,
                RestoreJavaSettings = global::PCL.ModJava.RestoreJavaSettings,
                VersionListOrder = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Collections.Generic.IEnumerable<global::System.String>>((Official, Mirror) => global::PCL.ModDownload.DlVersionListOrder(Official, Mirror)),
                SourceOrder = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Collections.Generic.IEnumerable<global::System.String>>((Official, Mirror) => global::PCL.ModDownload.DlSourceOrder(Official, Mirror)),
                RequestJson = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.String>(Urls => global::PCL.ModNet.NetRequestByLoader(Urls, IsJson: true)),
                CreateDownload = global::PCL.ModJava.CreateJavaDownload
            };
        }

        /// <summary>初始化原 Java 缓存并迁移旧版设置；无可用项目时触发搜索。</summary>
        public static void JavaInit()
        {
            global::PCL.ModJava.Service.JavaInit();
        }

        public static void UpdateJavaLists()
        {
            global::PCL.ModBase.RunInUi(() =>
        {
            global::PCL.ModMain.FrmSetupLaunch?.UpdateJavaList();
            global::PCL.ModMain.FrmInstanceSetup?.UpdateJavaList();
        });
        }

        public static void ManuallyImportJava()
        {
            global::PCL.ModJava.Service.ManuallyImportJava();
        }
        public static void ManuallyRemoveJava(global::PCLCS.Java JavaEntry)
        {
            global::PCL.ModJava.Service.ManuallyRemoveJava(JavaEntry);
        }
        public static void ManuallySearchJava(global::System.Threading.CancellationToken CancellationToken = default(global::System.Threading.CancellationToken))
        {
            global::PCL.ModJava.Service.ManuallySearchJava(CancellationToken);
        }

        public static (global::MeloongCore.ValueRange<global::System.Version> Range, global::System.String RecommendedComponent) GetJavaRequirement(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.ModJava.Service.GetJavaRequirement(Instance);
        }

        public static (global::MeloongCore.ValueRange<global::System.Version> Range, global::System.String RecommendedComponent) GetManagedJavaRequirement(
            global::PCL.ModMinecraft.McInstance Instance, global::PCLLF.MMCC.Core.MmcLaunchAuthorityResult Authority, bool NideLogin)
        {
            return global::PCL.ModJava.Service.GetManagedJavaRequirement(Instance,
                Authority?.Retires("net.minecraft", global::PCLLF.MMCC.Core.MmcRetiredHeuristicKind.BuiltinMinecraftJavaInference) == true,
                Authority?.Retires("net.minecraftforge", global::PCLLF.MMCC.Core.MmcRetiredHeuristicKind.BuiltinForgeJavaInference) == true, NideLogin);
        }

        public static global::PCLCS.Java SelectOrDownloadJava(global::MeloongCore.OneOf<global::MeloongCore.ValueRange<global::System.Version>, global::PCL.ModMinecraft.McInstance> Target, global::System.Boolean TryFixOrHint, global::System.Threading.CancellationToken c = default(global::System.Threading.CancellationToken), global::MeloongCore.ProgressProvider p = null)
        {
            if (Target.Is<global::PCL.ModMinecraft.McInstance>() &&
                MmcJavaDiscovery.TrySelect(Target.As<global::PCL.ModMinecraft.McInstance>(), c, p, out var managedJava))
                return managedJava;
            return global::PCL.ModJava.Service.SelectOrDownloadJava(Target, TryFixOrHint, c, p);
        }

        private static void PublishJavaHint(global::System.String Text, global::PCLCS.LegacyJavaHintKind Kind)
        {
            global::PCL.ModMain.Hint(Text, (Kind == global::PCLCS.LegacyJavaHintKind.Red) ? (global::PCL.ModMain.HintType.Red) : (global::PCL.ModMain.HintType.Green));
        }
        private static void SwitchToInstanceSetup(global::PCL.ModMinecraft.McInstance Instance)
        {
            global::PCL.ModBase.RunInUi(() =>
        {
            global::PCL.PageInstanceLeft.Instance = Instance;
            global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.InstanceSetup, global::PCL.FormMain.PageSubType.InstanceSetup);
        });
        }
        private static void DisableJavaSettings()
        {
            if (global::PCL.ModMain.FrmInstanceSetup is not null)
                global::PCL.ModBase.RunInUiWait(() => global::PCL.ModMain.FrmInstanceSetup.ComboArgumentJava.IsEnabled = false);
        }
        private static void RestoreJavaSettings()
        {
            if (global::PCL.ModMain.FrmInstanceSetup is not null)
            {
                global::PCL.ModBase.RunInUiWait(() =>
                        {
                            global::PCL.SettingService.RefreshSettings(global::PCL.ModMain.FrmInstanceSetup.ComboArgumentJava);
                            global::PCL.SettingService.RefreshSettings(global::PCL.ModMain.FrmInstanceSetup.ComboArgumentJavaSelect);
                            global::PCL.ModMain.FrmInstanceSetup.ComboArgumentJava.IsEnabled = true;
                        });
            }
        }

        // All LF processes use the same durable pin store, outside individual JDK homes.
        public static MmcJavaPinStore MmcPins => new MmcJavaPinStore(Path.Combine(Paths.AppData, ".minecraft", "runtime", ".pcllf-mmc-pins"));

        private static global::PCLCS.LegacyJavaDownloadOperation CreateJavaDownload(global::PCLCS.LegacyJavaDownloadPlan Plan)
        {
            return CreateProtectedJavaDownload(Plan, MmcPins, Path.Combine(Paths.AppData, ".minecraft", "runtime"));
        }

        // Maintenance is acquired before constructing download files and held until every writer
        // has drained. A terminal loader state alone must never authorize recursive cleanup.
        private static LegacyJavaDownloadOperation CreateProtectedJavaDownload(LegacyJavaDownloadPlan plan,
            MmcJavaPinStore pins, string runtimeRoot)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.BaseDirectory));
            if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(target).StartsWith(".", StringComparison.Ordinal))
                throw new IOException("Java 下载目标不在运行时组件目录内。");
            foreach (var file in plan.Files)
                if (!Path.GetFullPath(file.Destination).StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Java 下载文件超出了组件目录。");
            var stop = new CancellationTokenSource();
            int started = 0;
            return new LegacyJavaDownloadOperation
            {
                Wait = (c, p) =>
                {
                    if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Java 下载操作不能重复使用。");
                    Task.Run(async () =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(c, stop.Token);
                        using var maintenance = pins.EnterMaintenance(target, TimeSpan.FromSeconds(10), linked.Token);
                        linked.Token.ThrowIfCancellationRequested();
                        MmcJavaPinStore.ValidateMaintenanceTarget(target);
                        bool complete = false;
                        bool writesStarted = false;
                        try
                        {
                            var files = plan.Files.Select(file => new ModNet.NetFile(file.Sources, file.Destination,
                                new FileChecker { ActualSize = file.ActualSize, Hash = file.Hash })).ToList();
                            var loader = new ModNet.LoaderDownload("下载 Java 文件", files);
                            object cancelGate = new();
                            Task cancelWork = Task.CompletedTask;
                            using var cancellation = linked.Token.Register(() =>
                            {
                                lock (cancelGate) cancelWork = Task.Run(loader.Cancel);
                            });
                            try
                            {
                                lock (loader.LockState) { linked.Token.ThrowIfCancellationRequested(); writesStarted = true; loader.Start(); }
                                while (loader.State == ModBase.LoadState.Loading)
                                {
                                    p?.Set(loader.Progress * 0.95);
                                    await Task.Delay(10).ConfigureAwait(false);
                                }
                                loader.WaitForExit(StartIfNeeded: false);
                                linked.Token.ThrowIfCancellationRequested();
                                complete = true;
                            }
                            finally
                            {
                                await cancellation.DisposeAsync().ConfigureAwait(false);
                                if (loader.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading)
                                    await Task.Run(loader.Cancel).ConfigureAwait(false);
                                Task pending; lock (cancelGate) pending = cancelWork;
                                try { await pending.ConfigureAwait(false); }
                                finally { await MmcDownloads.DrainAsync(loader).ConfigureAwait(false); }
                            }
                        }
                        finally { if (writesStarted && !complete) { MmcJavaPinStore.ValidateMaintenanceTarget(target); plan.Incomplete(); } }
                        linked.Token.ThrowIfCancellationRequested();
                        var refresh = JavaListRefreshWorker;
                        refresh.Start(linked.Token, p);
                        await refresh.WaitIfRunningAsync(cancellationToken: linked.Token).ConfigureAwait(false);
                        linked.Token.ThrowIfCancellationRequested();
                        if (!refresh.LastSucceeded) throw new IOException("Java 文件已下载，但刷新 Java 列表失败。");
                    }, CancellationToken.None).GetAwaiter().GetResult();
                },
                // Mark cancellation immediately; its callbacks run on the pool, never a UI click.
                Cancel = () => { _ = stop.CancelAsync(); }
            };
        }
    }
}
