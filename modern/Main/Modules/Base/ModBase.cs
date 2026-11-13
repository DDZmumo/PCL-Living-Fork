using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;
using PCLCS;
using BaseOps = PCLCS.LegacyBaseOperations;
using BaseSearch = PCLCS.LegacyBaseSearch;
using BaseUi = PCLLF.Presentation.Foundation.LegacyBaseUi;
using BaseXaml = PCLLF.Presentation.Foundation.LegacyBaseXaml;

namespace PCL
{

    public static class ModBase
    {

        #region 声明

        // 下列版本信息由更新器自动修改
        public const string ReleaseBaselineVersion = "1.4.2";
        public const bool ReleaseBaselineIsPrerelease = false;
#if OFFICIAL_RELEASE
        public const string VersionBaseName = ReleaseBaselineVersion;
#else
        public const string VersionBaseName = "dev_edition";
#endif
        public const string LauncherDisplayName = "PCL Living Fork";
        public const string LauncherExecutableName = "PCL-Living-Fork-" + VersionBaseName + ".exe";
        public const string LauncherRepository = "DDZmumo/PCL-Living-Fork";
        public const string LauncherRepositoryUrl = "https://github.com/" + LauncherRepository;
        public const string LauncherIssuesUrl = LauncherRepositoryUrl + "/issues";
        public const string LauncherReleasesUrl = LauncherRepositoryUrl + "/releases";
        public const string LauncherCnbRepository = "ddzmumo/PCL-Living-Fork";
        public const string LauncherCnbUrl = "https://cnb.cool/" + LauncherCnbRepository;
        public const string CommitHash = ""; // Commit Hash，由 GitHub Workflow 自动替换
#if RELEASE
        public const int VersionCode = 422; // 发布版（含预发布）
#else
        public const int VersionCode = 423; // 快照版
#endif
        public const string VersionDisplay = BuildTypeDisplay + " " + VersionBaseName;
#if DEBUG
        public const string BuildTypeDisplay = "开发版";
        public const BuildTypes BuildType = BuildTypes.Debug;
#elif RELEASE
        public const string BuildTypeDisplay = VersionBaseName == "dev_edition" ? "开发版" : ReleaseBaselineIsPrerelease ? "预发布版" : "正式版";
        public const BuildTypes BuildType = BuildTypes.Release;
#else
        public const string BuildTypeDisplay = "快照版";
        public const BuildTypes BuildType = BuildTypes.Snapshot;
#endif
        public enum BuildTypes
        {
            Debug = 100,
            Release = 50,
            Snapshot = 0
        }

        /// <summary>
    /// 主窗口句柄。
    /// </summary>
        public static IntPtr Handle;
        /// <summary>
    /// 包含程序名的完整路径。
    /// </summary>
        // The native apphost can be renamed; retain the actual running executable path.
        public static string PathExe = Environment.ProcessPath;
        /// <summary>
    /// 程序内嵌图片文件夹路径，以 / 结尾。
    /// </summary>
        public static string PathImage = "pack://application:,,,/" + typeof(MyBitmap).Assembly.GetName().Name + ";component/Images/";
        /// <summary>
    /// 程序的缓存文件夹路径，以 \ 结尾。
    /// </summary>
        public static string PathTemp = BaseOps.CachePath(() => Settings.Get<string>("SystemSystemCache"));
        /// <summary>
    /// 当前程序的语言。
    /// </summary>
        public static string Lang = "zh_CN";
        /// <summary>
    /// 程序的打开计时。
    /// </summary>
        public static long ApplicationStartTick = GetTimeMs();
        /// <summary>
    /// 程序打开时的时间。
    /// </summary>
        public static DateTime ApplicationOpenTime = DateTime.Now;
        /// <summary>
    /// 识别码。
    /// </summary>
        public static string Identify = ModSecret.GetIdentify();
        /// <summary>
    /// 程序是否正在结束。
    /// </summary>
        public static bool IsProgramEnding = false;
        /// <summary>
    /// 是否使用 GBK 编码。
    /// </summary>
        public static bool IsGBKEncoding = global::PCL.Compatibility.WindowsRuntimeDefaults.WindowsDefaultEncoding.CodePage == 936;
        /// <summary>
    /// 系统盘盘符，以 \ 结尾。例如 “C:\”。
    /// </summary>
        public static string OsDrive = Environment.GetLogicalDrives().First(p => Directory.Exists(p)); // #3799

        public static bool ModeDebug
        {
            get
            {
                return Settings.Get<bool>("SystemDebugMode");
            }
        }

        #endregion

        #region 矢量图标

        public class Logo
        {
            /// <summary>
        /// 图标按钮，心（空心），1.1x
        /// </summary>
            public const string IconButtonLikeLine = LegacyBaseIcons.IconButtonLikeLine;
            public const string IconButtonTheme = LegacyBaseIcons.IconButtonTheme;
            /// <summary>
        /// 图标按钮，心（实心），1.1x
        /// </summary>
            public const string IconButtonLikeFill = LegacyBaseIcons.IconButtonLikeFill;
            /// <summary>
        /// 图标按钮，垃圾桶，1.1x
        /// </summary>
            public const string IconButtonDelete = LegacyBaseIcons.IconButtonDelete;
            /// <summary>
        /// 图标按钮，禁止，1x
        /// </summary>
            public const string IconButtonStop = LegacyBaseIcons.IconButtonStop;
            /// <summary>
        /// 图标按钮，勾选，1x
        /// </summary>
            public const string IconButtonCheck = LegacyBaseIcons.IconButtonCheck;
            /// <summary>
        /// 图标按钮，笔，1x
        /// </summary>
            public const string IconButtonEdit = LegacyBaseIcons.IconButtonEdit;
            /// <summary>
        /// 图标按钮，齿轮，1.1x
        /// </summary>
            public const string IconButtonSetup = LegacyBaseIcons.IconButtonSetup;
            /// <summary>
        /// 图标按钮，重置，0.9x
        /// </summary>
            public const string IconButtonReset = LegacyBaseIcons.IconButtonReset;
            /// <summary>
        /// 图标按钮，刷新，0.85x
        /// </summary>
            public const string IconButtonRefresh = LegacyBaseIcons.IconButtonRefresh;
            public const string IconButtonPersonalSettings = "M2,5 H6 V3 H8 V9 H6 V7 H2 Z M10,5 H22 V7 H10 Z M2,11 H14 V9 H16 V15 H14 V13 H2 Z M18,11 H22 V13 H18 Z M2,17 H4 V15 H6 V21 H4 V19 H2 Z M8,17 H22 V19 H8 Z";
            /// <summary>
        /// 图标按钮，软盘，1x
        /// </summary>
            public const string IconButtonSave = LegacyBaseIcons.IconButtonSave;
            /// <summary>
        /// 图标按钮，信息，1.05x
        /// </summary>
            public const string IconButtonInfo = LegacyBaseIcons.IconButtonInfo;
            /// <summary>
        /// 图标按钮，列表，1x
        /// </summary>
            public const string IconButtonList = LegacyBaseIcons.IconButtonList;
            /// <summary>
        /// 图标按钮，文件夹，1.1x
        /// </summary>
            public const string IconButtonOpen = LegacyBaseIcons.IconButtonOpen;
            /// <summary>
        /// 图标按钮，上箭头，0.95x
        /// </summary>
            public const string IconButtonArrowUp = LegacyBaseIcons.IconButtonArrowUp;
            /// <summary>
        /// 图标按钮，下箭头，0.95x
        /// </summary>
            public const string IconButtonArrowDown = LegacyBaseIcons.IconButtonArrowDown;
            /// <summary>
        /// 图标按钮，名片，1.1x
        /// </summary>
            public const string IconButtonCard = LegacyBaseIcons.IconButtonCard;
            /// <summary>
        /// 图标按钮，×，0.85x
        /// </summary>
            public const string IconButtonCross = LegacyBaseIcons.IconButtonCross;
            /// <summary>
        /// 图标按钮，Mojang，1.1x
        /// </summary>
            public const string IconButtonMojang = LegacyBaseIcons.IconButtonMojang;
            /// <summary>
        /// 图标按钮，用户，0.95x
        /// </summary>
            public const string IconButtonUser = LegacyBaseIcons.IconButtonUser;
            /// <summary>
        /// 图标按钮，盾牌，1x
        /// </summary>
            public const string IconButtonShield = LegacyBaseIcons.IconButtonShield;
            /// <summary>
        /// 图标按钮，离线，0.85x
        /// </summary>
            public const string IconButtonOffline = LegacyBaseIcons.IconButtonOffline;
            /// <summary>
        /// 图标，服务端，1x
        /// </summary>
            public const string IconButtonServer = LegacyBaseIcons.IconButtonServer;
            /// <summary>
        /// 图标，音符，1x
        /// </summary>
            public const string IconMusic = LegacyBaseIcons.IconMusic;
            /// <summary>
        /// 图标，播放，0.8x
        /// </summary>
            public const string IconPlay = LegacyBaseIcons.IconPlay;
        }

        #endregion

        #region 自定义类

        /// <summary>
    /// 支持小数与常见类型隐式转换的颜色；数值规则由 C# 呈现基础实现。
    /// </summary>
        public class MyColor : PCLLF.Presentation.Foundation.LegacyColor
        {

            public MyColor() : base()
            {
            }
            public MyColor(Color col) : base(col)
            {
            }
            public MyColor(string HexString) : base(HexString)
            {
            }
            public MyColor(double newA, MyColor col) : base(newA, col)
            {
            }
            public MyColor(double newR, double newG, double newB) : base(newR, newG, newB)
            {
            }
            public MyColor(double newA, double newR, double newG, double newB) : base(newA, newR, newG, newB)
            {
            }
            public MyColor(Brush brush) : base(brush)
            {
            }
            public MyColor(SolidColorBrush brush) : base(brush)
            {
            }
            public MyColor(object obj) : base(obj)
            {
            }

            public static implicit operator MyColor(string str)
            {
                return new MyColor(str);
            }
            public static implicit operator MyColor(Color col)
            {
                return new MyColor(col);
            }
            public static implicit operator Color(MyColor conv)
            {
                return conv.ToMediaColor();
            }
            public static implicit operator System.Drawing.Color(MyColor conv)
            {
                return conv.ToDrawingColor();
            }
            public static implicit operator MyColor(SolidColorBrush bru)
            {
                return new MyColor(bru.Color);
            }
            public static implicit operator SolidColorBrush(MyColor conv)
            {
                return conv.ToBrush();
            }
            public static implicit operator MyColor(Brush bru)
            {
                return new MyColor(bru);
            }
            public static implicit operator Brush(MyColor conv)
            {
                return conv.ToBrush();
            }
            private static MyColor FromChannels(PCLLF.Presentation.Controls.ControlColor Value)
            {
                return new MyColor(Value.A, Value.R, Value.G, Value.B);
            }
            public static MyColor operator +(MyColor a, MyColor b)
            {
                return FromChannels(Add(a, b));
            }
            public static MyColor operator -(MyColor a, MyColor b)
            {
                return FromChannels(Subtract(a, b));
            }
            public static MyColor operator *(MyColor a, double b)
            {
                return FromChannels(Multiply(a, b));
            }
            public static MyColor operator /(MyColor a, double b)
            {
                return FromChannels(Divide(a, b));
            }
            public static bool operator ==(MyColor a, MyColor b)
            {
                return AreEqual(a, b);
            }
            public static bool operator !=(MyColor a, MyColor b)
            {
                return !AreEqual(a, b);
            }
            public static new MyColor Lerp(MyColor ValueA, MyColor ValueB, double Percent)
            {
                return FromChannels(PCLLF.Presentation.Foundation.LegacyColor.Lerp(ValueA, ValueB, Percent));
            }
            public static new MyColor Round(MyColor col, int w = 0)
            {
                return FromChannels(PCLLF.Presentation.Foundation.LegacyColor.Round(col, w));
            }
            public static new byte ClampToByte(double d)
            {
                return PCLLF.Presentation.Foundation.LegacyColor.ClampToByte(d);
            }
            public new double Hue(double v1, double v2, double vH)
            {
                return base.Hue(v1, v2, vH);
            }
            public override string ToString()
            {
                return base.ToString();
            }
            public new MyColor FromHSL(double sH, double sS, double sL)
            {
                base.FromHSL(sH, sS, sL);
                return this;
            }
            public new MyColor FromHSL2(double sH, double sS, double sL)
            {
                base.FromHSL2(sH, sS, sL);
                return this;
            }
            public override bool Equals(object obj)
            {
                // Preserve VB's original object/operator binding at the legacy type boundary.
                return Operators.ConditionalCompareObjectEqual(this, obj, false);
            }
        }

        /// <summary>
    /// 支持负数与浮点数的矩形；旧类型仅保留构造入口。
    /// </summary>
        public class MyRect : PCLLF.Presentation.Foundation.LegacyRect
        {

            public MyRect() : base()
            {
            }
            public MyRect(double left, double top, double width, double height) : base(left, top, width, height)
            {
            }
        }

        /// <summary>
    /// 模块加载状态枚举。
    /// </summary>
        public enum LoadState
        {
            Waiting,
            Loading,
            Finished,
            Failed,
            Canceled
        }

        /// <summary>
    /// 执行返回值。
    /// </summary>
        public enum ProcessReturnValues
        {
            /// <summary>
        /// 执行成功。
        /// </summary>
            Success = 0,
            /// <summary>
        /// 执行失败。
        /// </summary>
            Fail = 1,
            /// <summary>
        /// 执行时出现未经处理的异常。
        /// </summary>
            Exception = 2,
            /// <summary>
        /// 执行超时。
        /// </summary>
            Timeout = 3,
            /// <summary>
        /// 取消执行。可能是由于不满足执行的前置条件。
        /// </summary>
            Cancel = 4,
            /// <summary>
        /// 任务成功完成。
        /// </summary>
            TaskDone = 5
        }

        #endregion

        #region 文件

        // ini

        private readonly static ConcurrentDictionary<string, ConcurrentDictionary<string, string>> IniCache = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
    /// 清除某 ini 文件的运行时缓存。
    /// </summary>
    /// <param name="FileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
        public static void IniClearCache(string FileName)
        {
            BaseOps.IniClearCache(FileName, IniCache);
        }
        /// <summary>
    /// 获取 ini 文件缓存。如果没有，则新读取 ini 文件内容。
    /// 在文件不存在或读取失败时返回 Nothing。
    /// </summary>
    /// <param name="FileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
        private static ConcurrentDictionary<string, string> IniGetContent(string FileName)
        {
            return BaseOps.IniGetContent(FileName, IniCache);
        }
        /// <summary>
    /// 读取 ini 文件。这可能会使用到缓存。
    /// </summary>
    /// <param name="FileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
    /// <param name="Key">键。</param>
    /// <param name="DefaultValue">没有找到键时返回的默认值。</param>
        public static string ReadIni(string FileName, string Key, string DefaultValue = "")
        {
            return BaseOps.ReadIni(FileName, Key, DefaultValue, IniCache);
        }
        /// <summary>
    /// 判断 ini 文件中是否包含某个键。这可能会使用到缓存。
    /// </summary>
        public static bool HasIniKey(string FileName, string Key)
        {
            return BaseOps.HasIniKey(FileName, Key, IniCache);
        }
        /// <summary>
    /// 从 ini 文件中移除某个键。这会更新缓存。
    /// </summary>
        public static void DeleteIniKey(string FileName, string Key)
        {
            BaseOps.WriteIni(FileName, Key, null, IniCache);
        }
        /// <summary>
    /// 写入 ini 文件，这会更新缓存。
    /// 若 Value 为 Nothing，则删除该键。
    /// </summary>
    /// <param name="FileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
    /// <param name="Key">键。</param>
    /// <param name="Value">值。</param>
        public static void WriteIni(string FileName, string Key, string Value)
        {
            BaseOps.WriteIni(FileName, Key, Value, IniCache);
        }

        // A checked transaction uses the same lock/cache as ordinary INI settings.
        // Its updater only computes a value; any UI/logging belongs outside it.
        internal static string UpdateIniValue(string file, string key, Func<string, string> update)
            => BaseOps.UpdateIniValue(file, key, value => update(value ?? ""), IniCache) ?? "";

        // 权限检查

        /// <summary>
    /// 检查是否拥有某一文件夹的 I/O 权限。如果文件夹不存在，会返回 False。
    /// </summary>
        public static bool CheckPermission(string Folder)
        {
            return BaseOps.CheckPermission(Folder, GetUuid);
        }
        /// <summary>
    /// 检查是否拥有某一文件夹的 I/O 权限。如果出错，则抛出异常。
    /// </summary>
        public static void CheckPermissionWithException(string Folder)
        {
            BaseOps.CheckPermissionWithException(Folder, GetUuid);
        }

        #endregion

        #region 文本
        public static char vbLQ = Convert.ToChar(8220);
        public static char vbRQ = Convert.ToChar(8221);

        /// <summary>
    /// 不会报错的 Val。
    /// 如果输入有误，返回 0。
    /// </summary>
        public static double Val(object Str)
        {
            return BaseOps.Val(Str);
        }

        #endregion

        #region 搜索

        /// <summary>
    /// 获取搜索文本的相似度。
    /// </summary>
    /// <param name="Source">被搜索的长内容。</param>
    /// <param name="Query">用户输入的搜索文本。</param>
        private static double SearchSimilarity(string Source, string Query)
        {
            return BaseSearch.Similarity(Source, Query);
        }
        /// <summary>
    /// 获取多段文本加权后的相似度。
    /// </summary>
        private static double SearchSimilarityWeighted(List<SearchSource> Source, string Query)
        {
            return BaseSearch.Weighted(Source, Query);
        }
        /// <summary>
    /// 用于搜索的项目。
    /// </summary>
        public class SearchEntry<T> : ILegacyBaseSearchEntry<SearchSource>
        {
            /// <summary>
        /// 该项目对应的源数据。
        /// </summary>
            public T Item;
            /// <summary>
        /// 该项目用于搜索的文本源。
        /// 在搜索时，会对每个文本源单独加权，但单个文本源内的多个别名只取最高的一个的相似度。
        /// </summary>
            public List<SearchSource> SearchSource;
            /// <summary>
        /// 相似度。
        /// </summary>
            public double Similarity;
            /// <summary>
        /// 是否完全匹配。
        /// </summary>
            public bool AbsoluteRight;
            public override string ToString()
            {
                return BaseSearch.FormatEntry(Similarity, Item);
            }
            private List<SearchSource> BridgeSources
            {
                get
                {
                    return SearchSource;
                }
            }

            List<SearchSource> ILegacyBaseSearchEntry<SearchSource>.Sources { get => BridgeSources; }
            private double BridgeSimilarity
            {
                get
                {
                    return Similarity;
                }
                set
                {
                    Similarity = value;
                }
            }

            double ILegacyBaseSearchEntry<SearchSource>.Similarity { get => BridgeSimilarity; set => BridgeSimilarity = value; }
            private bool BridgeAbsoluteRight
            {
                get
                {
                    return AbsoluteRight;
                }
                set
                {
                    AbsoluteRight = value;
                }
            }

            bool ILegacyBaseSearchEntry<SearchSource>.AbsoluteRight { get => BridgeAbsoluteRight; set => BridgeAbsoluteRight = value; }
        }
        /// <summary>
    /// 单个用于搜索的文本源。
    /// </summary>
        public class SearchSource : ILegacyBaseSearchSource
        {
            public string[] Aliases;
            public double Weight;
            public SearchSource(string[] Aliases, double Weight = 1d)
            {
                this.Aliases = Aliases;
                this.Weight = Weight;
            }
            public SearchSource(string Text, double Weight = 1d)
            {
                Aliases = new[] { Text };
                this.Weight = Weight;
            }
            private string[] BridgeAliases
            {
                get
                {
                    return Aliases;
                }
            }

            string[] ILegacyBaseSearchSource.Aliases { get => BridgeAliases; }
            private double BridgeWeight
            {
                get
                {
                    return Weight;
                }
            }

            double ILegacyBaseSearchSource.Weight { get => BridgeWeight; }
        }
        /// <summary>
    /// 进行多段文本加权搜索，获取相似度较高的数项结果。
    /// 在搜索时，会对每个文本源单独加权，但单个文本源内的多个别名只取最高的一个的相似度。
    /// 这会修改 Entries 中每项的 Similarity 与 AbsoluteRight 字段。
    /// </summary>
    /// <param name="MaxBlurCount">返回的最大模糊结果数。</param>
    /// <param name="MinBlurSimilarity">返回结果要求的最低相似度。</param>
        public static List<SearchEntry<T>> Search<T>(List<SearchEntry<T>> Entries, string Query, int MaxBlurCount = 5, double MinBlurSimilarity = 0.1d)
        {
            return BaseSearch.Search<SearchEntry<T>, SearchSource>(Entries, Query, MaxBlurCount, MinBlurSimilarity);
        }

        #endregion

        #region 系统

        /// <summary>
    /// 为 Task 设置超时，在超时时抛出 TimeoutException。
    /// </summary>
        public static T GetResultWithTimeout<T>(this Task<T> TargetTask, CancellationTokenSource TokenSource, int TimeoutMs)
        {
            return BaseOps.GetResultWithTimeout(TargetTask, TokenSource, TimeoutMs);
        }
        /// <summary>
    /// 为 Task 设置超时，在超时时抛出 TimeoutException。
    /// </summary>
        public static void GetResultWithTimeout(this Task TargetTask, CancellationTokenSource TokenSource, int TimeoutMs)
        {
            BaseOps.GetResultWithTimeout(TargetTask, TokenSource, TimeoutMs);
        }

        /// <summary>
    /// 可用于临时存放文件的，不含任何特殊字符的文件夹路径，以“\”结尾。
    /// </summary>
        public static Lazy<string> PathPure = new Lazy<string>(() => BaseOps.PurePath(() => PathTemp, () => OsDrive));

        /// <summary>
    /// 指示接取到这个异常的函数进行重试。
    /// </summary>
        public class RestartException : Exception
        {
        }

        /// <summary>
    /// 以管理员权限运行当前程序，并等待程序运行结束。
    /// 返回程序的返回代码，如果运行失败将抛出异常。
    /// </summary>
        public static int RunAsAdmin(string Argument)
        {
            return BaseOps.RunAsAdmin(Argument, PathExe);
        }

        private static int Uuid = 1;
        private static object UuidLock;
        /// <summary>
    /// 获取一个全程序内不会重复的数字（伪 Uuid）。
    /// </summary>
        public static int GetUuid()
        {
            return BaseOps.GetUuid(ref Uuid, ref UuidLock);
        }

        // 时间相关
        /// <summary>
    /// 获取一个单调递增时间值（毫秒）。
    /// </summary>
        public static long GetTimeMs()
        {
            return BaseOps.GetTimeMs();
        }
        /// <summary>
    /// 获取十进制 Unix 时间戳。
    /// </summary>
        public static long GetUnixTimestampUtc()
        {
            return BaseOps.GetUnixTimestampUtc();
        }
        /// <summary>
    /// 获取十进制 Unix 时间戳。
    /// </summary>
        public static long ToUnixTimestampUtc(DateTime Time)
        {
            return BaseOps.ToUnixTimestampUtc(Time);
        }
        /// <summary>
    /// 时间戳转化为日期。
    /// </summary>
        public static DateTime GetDate(int timeStamp)
        {
            return BaseOps.GetDate(timeStamp);
        }
        /// <summary>
    /// 将 UTC 时间转化为当前时区的时间。
    /// </summary>
        public static DateTime GetLocalTime(DateTime UtcDate)
        {
            return BaseOps.GetLocalTime(UtcDate);
        }

        /// <summary>
    /// 用于储存 RaiseByMouse 的 EventArgs。
    /// </summary>
        public sealed class RouteEventArgs : EventArgs
        {
            public bool RaiseByMouse;
            public bool Handled = false;
            public RouteEventArgs(bool RaiseByMouse = false)
            {
                this.RaiseByMouse = RaiseByMouse;
            }
        }

        /// <summary>
    /// 启动进程并返回进程实例，若失败则抛出异常。
    /// 会自动进行长路径处理。
    /// </summary>
        public static Process StartProcess(string FileName, string Arguments = "")
        {
            return BaseOps.StartProcess(FileName, Arguments);
        }
        /// <summary>
    /// 启动进程并返回进程实例，若失败则抛出异常。
    /// 会自动进行长路径处理。
    /// </summary>
        public static Process StartProcess(ProcessStartInfo StartInfo)
        {
            return BaseOps.StartProcess(StartInfo);
        }

        /// <summary>
    /// 在新的工作线程中执行代码。
    /// </summary>
        public static Thread RunInNewThread(Action Action, string Name = null, ThreadPriority Priority = ThreadPriority.Normal)
        {
            return BaseOps.RunInNewThread(Action, Name, Priority, GetUuid);
        }
        /// <summary>
    /// 确保在 UI 线程中执行代码。
    /// 如果当前并非 UI 线程，则会阻断当前线程，直至 UI 线程执行完毕。
    /// 为防止线程互锁，请仅在开始加载动画、从 UI 获取输入时使用！
    /// </summary>
        public static Output RunInUiWait<Output>(Func<Output> Action)
        {
            return BaseUi.RunInUiWait(Action, UiThreadId);
        }
        /// <summary>
    /// 确保在 UI 线程中执行代码。
    /// 如果当前并非 UI 线程，则会阻断当前线程，直至 UI 线程执行完毕。
    /// 为防止线程互锁，请仅在开始加载动画、从 UI 获取输入时使用！
    /// </summary>
        public static void RunInUiWait(Action Action)
        {
            BaseUi.RunInUiWait(Action, UiThreadId);
        }
        /// <summary>
    /// 确保在 UI 线程中执行代码，代码按触发顺序执行。
    /// 如果当前并非 UI 线程，也不阻断当前线程的执行。
    /// </summary>
        public static void RunInUi(Action Action, bool ForceWaitUntilLoaded = false)
        {
            BaseUi.RunInUi(Action, ForceWaitUntilLoaded, UiThreadId);
        }
        /// <summary>
    /// 确保在工作线程中执行代码。
    /// </summary>
        public static void RunInThread(Action Action)
        {
            BaseOps.RunInThread(Action, () => RunInUi(), GetUuid);
        }

        /// <summary>
    /// 按照既定的函数进行选择排序。
    /// 返回第一个对象是否应该排在前面（a > b）。
    /// </summary>
        public static List<T> SortByComparison<T>(this IList<T> List, Func<T, T, bool> SortRule)
        {
            return BaseOps.SortByComparison(List, SortRule);
        }

        /// <summary>
    /// 获取程序启动参数。
    /// </summary>
    /// <param name="Name">参数名。</param>
    /// <param name="DefaultValue">默认值。</param>
        public static object GetProgramArgument(string Name, [System.Runtime.InteropServices.Optional, System.Runtime.InteropServices.DefaultParameterValue("")] object DefaultValue)
        {
            return BaseOps.GetProgramArgument(Name, DefaultValue);
        }

        /// <summary>
    /// 打开网页。
    /// </summary>
        public static void OpenWebsite(string Url)
        {
            BaseOps.OpenWebsite(Url, CreateBaseHost(), GetUuid);
        }
        /// <summary>
    /// 打开 explorer。
    /// 若不以 \ 结尾，则将视作文件路径，打开并选中此文件。
    /// </summary>
        public static void OpenExplorer(string Location)
        {
            BaseOps.OpenExplorer(Location);
        }

        /// <summary>
    /// 设置剪贴板。将在另一线程运行，且不会抛出异常。
    /// </summary>
        public static void ClipboardSet(string Text, bool ShowSuccessHint = true)
        {
            BaseOps.ClipboardSet(Text, ShowSuccessHint, CreateBaseHost(), GetUuid);
        }
        /// <summary>
    /// 获取剪贴板文本。将在 UI 线程运行，且不会抛出异常。
    /// </summary>
        public static string ClipboardGetText()
        {
            return BaseOps.ClipboardGetText(CreateBaseHost());
        }

        /// <summary>
    /// 将程序中的资源释放到文件。
    /// </summary>
        public static void ExtractResources(string FilePath, string ResourceName)
        {
            BaseOps.ExtractResources(FilePath, ResourceName, Name => My.Resources.Resources.ResourceManager.GetObject(Name));
        }

        #endregion

        #region UI

        // DPI 转换
        public readonly static int DPI = (int)Math.Round(System.Drawing.Graphics.FromHwnd(IntPtr.Zero).DpiX);
        /// <summary>
    /// 将经过 DPI 缩放的 WPF 尺寸转化为实际的像素尺寸。
    /// </summary>
        public static double GetPixelSize(double WPFSize)
        {
            return BaseUi.GetPixelSize(WPFSize, DPI);
        }
        /// <summary>
    /// 将实际的像素尺寸转化为经过 DPI 缩放的 WPF 尺寸。
    /// </summary>
        public static double GetWPFSize(double PixelSize)
        {
            return BaseUi.GetWpfSize(PixelSize, DPI);
        }

        /// <summary>
    /// 将 XML 转换为对应 UI 对象。
    /// 注意：性能较差，不应大量使用。
    /// </summary>
        public static object GetObjectFromXML(XElement Str)
        {
            return BaseXaml.Load(Str, typeof(MyBitmap).Assembly.GetName().Name, MyBitmap.NormalizeResourceUri, typeof(SettingService));
        }
        /// <summary>
    /// 将 XML 转换为对应 UI 对象。
    /// 注意：性能较差，不应大量使用。
    /// </summary>
        public static object GetObjectFromXML(string Str)
        {
            return BaseXaml.Load(Str, typeof(MyBitmap).Assembly.GetName().Name, MyBitmap.NormalizeResourceUri, typeof(SettingService));
        }

        private readonly static int UiThreadId = Thread.CurrentThread.ManagedThreadId;
        /// <summary>
    /// 当前线程是否为主线程。
    /// </summary>
        public static bool RunInUi()
        {
            return BaseUi.RunInUi(UiThreadId);
        }

        /// <summary>
    /// 检查某个控件是否位于主窗口可视区域内，且控件本身可见。
    /// </summary>
        public static bool IsVisibleInForm(this FrameworkElement element)
        {
            return BaseUi.IsVisibleInForm(element, ModMain.FrmMain);
        }

        /// <summary>
    /// 控件是否受到 TextTrimming 属性影响，导致内容被截取。
    /// </summary>
        public static bool IsTextTrimmed(this TextBlock TextBlock)
        {
            return BaseUi.IsTextTrimmed(TextBlock, DPI);
        }

        /// <summary>
    /// 将布尔值转换为 Visibility。True 转换为 Visible，False 转换为 Collapsed。
    /// </summary>
        public static Visibility ToVisibility(this bool IsVisible)
        {
            return BaseUi.ToVisibility(IsVisible);
        }

        #endregion

        #region 随机

        private readonly static Random Random = new Random();

        /// <summary>
    /// 随机选择其一。
    /// </summary>
        public static T RandomOne<T>(ICollection<T> objects)
        {
            return BaseOps.RandomOne(objects, RandomInteger);
        }

        /// <summary>
    /// 取随机整数（包含）。
    /// </summary>
        public static int RandomInteger(int min, int max)
        {
            return BaseOps.RandomInteger(min, max, Random);
        }

        /// <summary>
    /// 将数组随机打乱。
    /// </summary>
        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> Raw)
        {
            return BaseOps.Shuffle(Raw, RandomInteger);
        }

        #endregion

        // Only the real platform primitives. Algorithms, retries and threading stay in C#.
        private static LegacyBaseHost CreateBaseHost()
        {
            return new LegacyBaseHost()
            {
                OnUiThread = new Func<bool>(() => RunInUi()),
                DispatchWait = new Action<Action>(Work => RunInUiWait(Work)),
                ClearClipboard = new Action(() => My.MyWpfExtension.Computer.Clipboard.Clear()),
                WriteClipboardText = new Action<string>(Text => My.MyWpfExtension.Computer.Clipboard.SetText(Text)),
                ClipboardContainsText = new Func<bool>(() => My.MyWpfExtension.Computer.Clipboard.ContainsText()),
                ReadClipboardText = new Func<string>(() => My.MyWpfExtension.Computer.Clipboard.GetText()),
                SuccessHint = new Action<string>(Text => ModMain.Hint(Text, ModMain.HintType.Green)),
                Message = new Action<string, string>((Text, Title) => ModMain.MyMsgBox(Text, Title))
            };
        }

    }
}
