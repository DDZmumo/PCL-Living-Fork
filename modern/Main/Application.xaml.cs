using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace PCL
{

    public partial class Application
    {

        // 开始
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                if (ModLauncherUpdate.HandleUpdateCommand(e.Args))
                    return;
                bool UtilityCommand = e.Args.Length > 0 && new[] { "--update", "--gpu", "--memory" }.Contains(e.Args[0]);
                if (!UtilityCommand)
                    WaitingMutex(e);
                // 先迁移旧分支的兼容偏好，再初始化配置缓存；之后与官方 PCL 直接共用存储。
                if (!UtilityCommand)
                    Settings.ImportLegacyPclPreferences();
                // 核心初始化
                MeloongCore.Main.Init("PCL");
                MeloongCore.Wpf.Main.Init();
                if (!(e.Args.Contains("--update") || e.Args.Contains("--gpu") || e.Args.Contains("--memory"))) // 若是通过命令行调用，则不检查单例、不轮换日志（#9137）
                {
                    Logger.Instance = new PclLogger() { logFolder = Paths.BaseThenName, MinLevel = ModBase.ModeDebug ? LogLevel.Trace : LogLevel.Info };
                    Settings.ImportCompatiblePclAccounts();
                    try
                    {
                        Settings.EnsureMicrosoftCacheUpgrade();
                    }
                    catch
                    {
                        Logger.Warn("旧版微软缓存升级未完成，原有数据已保留。");
                    }
                    var IconRepair = new Thread(ModMinecraft.RepairSharedMinecraftIcons) { IsBackground = true, Name = "Repair Shared Minecraft Icons", Priority = ThreadPriority.BelowNormal };
                    IconRepair.Start();
                    ModBase.RunInNewThread(() =>
                        {
                            try
                            {
                                PersonalGameSettings.RecoverExitedSessions();
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "恢复个人游戏设置失败");
                            }
                            try
                            {
                                QuickPlayWorlds.RecoverExitedSessions();
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "恢复最近存档记录失败");
                            }
                            ModBase.RunInUi(() => ModMain.FrmLaunchRight?.RefreshRecentWorlds());
                        }, "Recover Local Game Data", ThreadPriority.BelowNormal);
                }
                // 提升主线程优先级
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                // 执行开发版测试
                if (ModBase.BuildType == ModBase.BuildTypes.Debug)
                {
                    try
                    {
                        ModDevelop.Start();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "开发者模式测试出错", LogBehavior.Alert);
                    }
                }
                // 当前 C# 产品由 .NET10 Desktop 宿主运行；不再要求额外安装 CLR4。
                try
                {
                    if (Environment.Version.Major < 10)
                        OldEnvironmentAssert();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "检查 .NET 运行时版本失败");
                }
                // 修复 WPF 字体加载（#3467）
                try
                {
                    var _unused = new FormattedText("", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Fonts.SystemTypefaces.First(), 96d, new ModBase.MyColor(), ModBase.DPI);
                }
                catch (UriFormatException ex)
                {
                    Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"), EnvironmentVariableTarget.User);
                    var _unused = new FormattedText("", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Fonts.SystemTypefaces.First(), 96d, new ModBase.MyColor(), ModBase.DPI);
                }
                // 检查参数调用
                if (e.Args.Length > 0)
                {
                    if (e.Args[0] == "--update")
                    {
                        // 固定版本不接受官方更新，也不处理更新文件替换参数。
                        Logger.Info("已拒绝启动器更新：本版本已禁用官方更新。");
                        Environment.Exit((int)ModBase.ProcessReturnValues.Fail);
                    }
                    else if (e.Args[0] == "--gpu")
                    {
                        // 调整显卡设置
                        try
                        {
                            ModMain.SetGPUPreference(e.Args[1].Trim('"'));
                            Environment.Exit((int)ModBase.ProcessReturnValues.TaskDone);
                        }
                        catch (Exception ex)
                        {
                            Environment.Exit((int)ModBase.ProcessReturnValues.Fail);
                        }
                    }
                    else if (e.Args[0] == "--memory")
                    {
                        // 内存优化
                        try
                        {
                            Environment.Exit(MemoryOptimizationOperation.ExecuteHelper(() => (long)My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory, () => PageOtherTest.MemoryOptimizeInternal(false)));
                        }
                        catch (Exception ex)
                        {
                            Interaction.MsgBox(ex.Message, MsgBoxStyle.Critical, "内存优化失败");
                            Environment.Exit(MemoryOptimizationOperation.FailedExitCode);
                        }
                    }
                }
                // 初始化文件结构
                try
                {
                    DirectoryUtils.Create(Paths.Base + @"PCL\Pictures\");
                    DirectoryUtils.Create(Paths.Base + @"PCL\Musics\");
                    ModBase.CheckPermissionWithException(Paths.Base + @"PCL\");
                }
                catch (Exception ex)
                {
                    Interaction.MsgBox($@"PCL 没有对当前文件夹的权限（{Paths.Base}PCL\），请尝试：" + Constants.vbCrLf + "1. 将 PCL 移动到其他文件夹" + (PathUtils.GetDiskName(Paths.Base) is { } arg1 && arg1 == 'C' ? "，例如 C 盘和桌面以外的其他位置。" : "。") + Constants.vbCrLf + "2. 删除当前目录中的 PCL 文件夹，然后再试。" + Constants.vbCrLf + "3. 右键 PCL 选择属性，打开 兼容性 中的 以管理员身份运行此程序。", MsgBoxStyle.Critical, "运行环境错误");
                    Environment.Exit((int)ModBase.ProcessReturnValues.Cancel);
                }

            RetryCacheCheck:
                ;

                try
                {
                    DirectoryUtils.Create(ModBase.PathTemp);
                    ModBase.CheckPermissionWithException(ModBase.PathTemp);
                }
                catch (Exception ex)
                {
                    if (PathUtils.IsSame(ModBase.PathTemp, Path.GetTempPath() + @"PCL\"))
                    {
                        ModMain.MyMsgBox("PCL 无法访问缓存文件夹，可能导致程序出错或无法正常使用！" + Constants.vbCrLf + Constants.vbCrLf + "错误原因：" + ex.GetDisplay(true), "缓存文件夹不可用");
                    }
                    else
                    {
                        ModMain.MyMsgBox("手动设置的缓存文件夹不可用，PCL 将使用默认缓存文件夹。" + Constants.vbCrLf + Constants.vbCrLf + "错误原因：" + ex.GetDisplay(true), "缓存文件夹不可用");
                        Settings.Set("SystemSystemCache", "");
                        ModBase.PathTemp = Path.GetTempPath() + @"PCL\";
                        goto RetryCacheCheck;
                    }
                }
                DirectoryUtils.Create(ModBase.PathTemp + @"Cache\");
                DirectoryUtils.Create(Paths.AppDataThenName);
                // 设置 ToolTipService 默认值
                ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(100));
                // 设置网络配置默认值
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 10000;
                ServicePointManager.UseNagleAlgorithm = false;
                ServicePointManager.EnableDnsRoundRobin = true;
                ServicePointManager.ReusePort = true;
                if (Environment.OSVersion.Version < new Version(6, 2)) // Windows 7 回退（#8519）
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                }
                // 设置初始窗口
                if (Settings.Get<bool>("UiLauncherLogo"))
                {
                    ModMain.FrmStart = new SplashScreen(@"Images\Branding\LivingPCL-256.png");
                    ModMain.FrmStart.Show(false, true);
                }
                // 基础信息
                Logger.Info($"程序版本：{ModBase.VersionDisplay} ({ModBase.VersionCode}{(string.IsNullOrEmpty(ModBase.CommitHash) ? "" : $"，#{ModBase.CommitHash}")})");
                Logger.Info($"识别码：{ModBase.Identify}");
                Logger.Info($"程序路径：{ModBase.PathExe}");
                Logger.Info($"系统编码：{(global::PCL.Compatibility.WindowsRuntimeDefaults.WindowsDefaultEncoding.HeaderName)} ({(global::PCL.Compatibility.WindowsRuntimeDefaults.WindowsDefaultEncoding.CodePage)}, GBK={ModBase.IsGBKEncoding})");
                Logger.Info($"管理员权限：{WindowsUtils.HasAdminRole()}");
                // 检测异常环境
                if (Paths.Base.Contains(Path.GetTempPath()) || Paths.Base.Contains(@"AppData\Local\Temp\"))
                {
                    ModMain.MyMsgBox("请将 PCL 从压缩包中解压后再使用！" + Constants.vbCrLf + "如果不会解压，可以在网上寻找教程。", "需要解压！", "我知道了", IsWarn: true, ForceWait: true);
                    FormMain.EndProgramForce(ModBase.ProcessReturnValues.Cancel);
                }
                if (!Environment.Is64BitOperatingSystem)
                {
                    ModMain.MyMsgBox("PCL 和新版 Minecraft 均不再支持 32 位系统，请重装为 64 位系统后再进行游戏！", "环境警告", "我知道了", IsWarn: true, ForceWait: true);
                    FormMain.EndProgramForce(ModBase.ProcessReturnValues.Cancel);
                }
                // 计时
                Logger.Info($"第一阶段加载用时：{ModBase.GetTimeMs() - ModBase.ApplicationStartTick} ms");
                ModBase.ApplicationStartTick = ModBase.GetTimeMs();
                ModAnimation.AniControlEnabled += 1;
            }
            catch (Exception ex)
            {
                string FilePath = null;
                try
                {
                    FilePath = ModBase.PathExe;
                }
                catch
                {
                }
                Interaction.MsgBox(ex.GetDisplay(true) + Constants.vbCrLf + "PCL 所在路径：" + (string.IsNullOrEmpty(FilePath) ? "获取失败" : FilePath), MsgBoxStyle.Critical, "PCL 初始化错误");
                FormMain.EndProgramForce(ModBase.ProcessReturnValues.Exception);
            }
        }
        /// <summary>
    /// 要求程序以单例运行。
    /// 如果已在运行，则将其窗口拖出来并播放提示音，然后结束当前进程。
    /// </summary>
        private static void WaitingMutex(StartupEventArgs e)
        {
            try
            {
                int Timeout = e.Args.Length > 0 && e.Args[0] == "--wait" ? 10000 : 0;
                SessionLease = LauncherSessionLock.TryAcquire(new[] { "PCL_SingletonMutex", "LivingPCL_SingletonMutex" }, Timeout);
                if (SessionLease is not null)
                    return;
                // 与原版 PCL 一致：已有实例时优先唤醒窗口，不创建第二个实例。
                // 启动过程可能尚未创建主窗口，短暂重试以避免并发启动时找不到窗口。
                if (LauncherWindowActivation.TryActivateExistingWindow())
                {
                    Interaction.Beep();
                }
                else
                {
                    MessageBox.Show("PCL 或 PCL Living Fork 已在运行，但未能唤醒其窗口。请切换到正在运行的启动器，或退出后重试。", "启动器正在运行", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch
            {
                MessageBox.Show("无法确认启动器的运行状态。为保护共享账号，本次启动已停止。", "无法启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Environment.Exit(2);
        }
        private static LauncherSessionLock SessionLease;

        // 结束
        private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            ModMain.FrmMain?.EndProgram(false);
        }

        // 异常
        private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Direct statement-resume translation of LF's On Error Resume Next.
            // In VB a failing If resumes at its Then statement, and a new Err is
            // retained after the method; the isolated real-VB oracle verifies it.
            ProjectData.ClearProjectError();
            string Detail = null;
            int next = 0;
            while (true)
            {
                try
                {
                    switch (next)
                    {
                        case 0:
                            next = 1;
                            if (!(ModMain.FrmMain?.PageLeft is not null && ModMain.FrmMain.PageLeft is IDispatcherUnhandledException)) next = 3;
                            break;
                        case 1:
                            next = 2;
                            ((IDispatcherUnhandledException)ModMain.FrmMain.PageLeft).DispatcherUnhandledException(sender, e);
                            break;
                        case 2:
                            next = 13;
                            if (e.Handled) return;
                            next = 3;
                            break;
                        case 3:
                            next = 4;
                            if (!(ModMain.FrmMain?.PageRight is not null && ModMain.FrmMain.PageRight is IDispatcherUnhandledException)) next = 6;
                            break;
                        case 4:
                            next = 5;
                            ((IDispatcherUnhandledException)ModMain.FrmMain.PageRight).DispatcherUnhandledException(sender, e);
                            break;
                        case 5:
                            next = 13;
                            if (e.Handled) return;
                            next = 6;
                            break;
                        case 6:
                            next = 7;
                            e.Handled = true;
                            break;
                        case 7:
                            next = 13;
                            if (ModBase.IsProgramEnding) return;
                            next = 8;
                            break;
                        case 8:
                            next = 9;
                            ModMain.FeedbackInfo();
                            break;
                        case 9:
                            next = 10;
                            Detail = e.Exception.GetDisplay(true);
                            break;
                        case 10:
                            next = 11;
                            if (!(Detail.Contains("System.Windows.Threading.Dispatcher.Invoke") || Detail.Contains("MS.Internal.AppModel.ITaskbarList.HrInit") || Detail.Contains("未能加载文件或程序集") || Detail.Contains(".NET Framework"))) next = 12;
                            break;
                        case 11:
                            next = 13;
                            OldEnvironmentAssert(e.Exception);
                            break;
                        case 12:
                            next = 13;
                            Logger.Error(e.Exception, "程序出现未知错误", LogBehavior.AlertThenCrash);
                            break;
                        default:
                            return;
                    }
                }
                catch (Exception error) { ProjectData.SetProjectError(error); }
            }
        }
        private static void OldEnvironmentAssert(Exception ex = null)
        {
            ModBase.OpenWebsite("https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0");
            if (ex is null)
            {
                Logger.Error($"当前 .NET 运行环境异常，请安装或修复 .NET 10 Desktop Runtime！{Constants.vbCrLf}请使用与启动器架构一致的 Windows Desktop Runtime 安装程序。", LogBehavior.AlertThenCrash);
            }
            else
            {
                Logger.Error(ex, $"当前 .NET 运行环境异常，请安装或修复 .NET 10 Desktop Runtime！{Constants.vbCrLf}请使用与启动器架构一致的 Windows Desktop Runtime 安装程序。", LogBehavior.AlertThenCrash);
            }
        }

        // 动态 DLL 加载
        private Application()
        {
            // LF's legacy text/ZIP readers require the Windows code pages before
            // any instance, pack or server-script parsing can start.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // VB Me Handles subscriptions precede the original constructor statements.
            Startup += Application_Startup;
            SessionEnding += Application_SessionEnding;
            DispatcherUnhandledException += Application_DispatcherUnhandledException;
            // Managed dependencies are resolved once by the .NET host from the
            // dependency graph/bundle. Only the native WebP payload is embedded.
        }
        private static readonly Lazy<bool> WebPReady = new Lazy<bool>(() =>
        {
            // .NET's normal resolver can load Imazen without AssemblyResolve.
            // Bind its native dependency explicitly at the real WebP boundary.
            string nativePath = Path.Combine(ModBase.PathPure.Value, "libwebp.dll");
            ModBase.ExtractResources(nativePath, "libwebp64");
            IntPtr module = NativeLibrary.Load(nativePath);
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Imazen.WebP.SimpleDecoder).Assembly,
                    (name, assembly, searchPath) =>
                        name.Equals("libwebp", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("libwebp.dll", StringComparison.OrdinalIgnoreCase) ? module : IntPtr.Zero);
            }
            catch
            {
                NativeLibrary.Free(module);
                throw;
            }
            // Cached P/Invoke entry points require this handle until process exit.
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        internal static void PrepareWebP() => _ = WebPReady.Value;

        // 控件模板事件
        private void MyIconButton_Click(object sender, EventArgs e)
        {
            try
            {
                switch (Settings.Get<ModLaunch.McLoginType>("LoginType"))
                {
                    case ModLaunch.McLoginType.Ms:
                        {
                            // 微软
                            Settings.UpdateMicrosoftAccounts(Accounts => Accounts.Property(Conversions.ToString(((dynamic)sender).Tag), StringComparison.OrdinalIgnoreCase)?.Remove());
                            if (object.ReferenceEquals(ModMain.FrmLoginMs.ComboAccounts.SelectedItem, ((dynamic)sender).Parent))
                                ModMain.FrmLoginMs.ComboAccounts.SelectedIndex = 0;
                            ModMain.FrmLoginMs.ComboAccounts.Items.Remove(((dynamic)sender).Parent);
                            break;
                        }
                    case ModLaunch.McLoginType.Legacy:
                        {
                            // 离线
                            var Names = Settings.UpdateLegacyNames(Items => Items.RemoveAll(Name => Name.Equals(Conversions.ToString(((dynamic)sender).Tag), StringComparison.OrdinalIgnoreCase)));
                            ModMain.FrmLoginLegacy.ComboName.ItemsSource = Names;
                            ModMain.FrmLoginLegacy.ComboName.Text = Names.Any() ? Names[0] : "";
                            break;
                        }

                    default:
                        {
                            // 第三方
                            string Token = Settings.Get<ModLaunch.McLoginType>("LoginType").ToString();
                            var Dict = Settings.UpdateServerAccounts(Token, Conversions.ToString(((dynamic)sender).Tag), "", true);
                            switch (Token ?? "")
                            {
                                case "Nide":
                                    {
                                        ModMain.FrmLoginNide.ComboName.ItemsSource = Dict.Keys;
                                        ModMain.FrmLoginNide.ComboName.Text = Dict.Keys.Any() ? Dict.Keys.ElementAtOrDefault(0) : "";
                                        ModMain.FrmLoginNide.TextPass.Password = Dict.Values.Any() ? Dict.Values.ElementAtOrDefault(0) : "";
                                        break;
                                    }
                                case "Auth":
                                    {
                                        ModMain.FrmLoginAuth.ComboName.ItemsSource = Dict.Keys;
                                        ModMain.FrmLoginAuth.ComboName.Text = Dict.Keys.Any() ? Dict.Keys.ElementAtOrDefault(0) : "";
                                        ModMain.FrmLoginAuth.TextPass.Password = Dict.Values.Any() ? Dict.Values.ElementAtOrDefault(0) : "";
                                        break;
                                    }
                            }

                            break;
                        }
                }
            }
            catch
            {
                Logger.Error("账号记录未能保存，原有记录已保留。请稍后重试。", LogBehavior.Toast);
            }
        }

    }
}
