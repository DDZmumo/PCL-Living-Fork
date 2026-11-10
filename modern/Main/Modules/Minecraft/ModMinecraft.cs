using System;
using System.Collections;
using global::System.Collections.Concurrent;
using System.Collections.Concurrent;
using global::System.Collections.Generic;
using System.Collections.Generic;
using System.Diagnostics;
using global::System.IO;
using System.IO;
using System.IO.Compression;
using global::System.Linq;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using MinecraftCore = global::PCLCS.LegacyMinecraftService<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McLibEntry, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModMinecraft.McFolder.Types>;
using ThrottleDebounce;

namespace PCL
{

    // 原 LF 公共模块、嵌套模型身份和平台入口；实例／目录／库／资源业务由 C# 单实现维护。
    public static class ModMinecraft
    {
        private static global::System.String _McFolderSelected;
        private static global::PCL.ModMinecraft.McInstance _McInstanceSelected;
        internal static event Action InstanceSelectionChanged;
        public static global::System.Collections.Generic.List<global::PCL.ModMinecraft.McFolder> McFolderList = new global::System.Collections.Generic.List<global::PCL.ModMinecraft.McFolder>();
        public static global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>> McInstanceList = new global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>>();
        public static global::System.Boolean McInstanceListForceRefresh = false;
        private static global::System.Boolean IsFirstMcInstanceListLoad = true;
        private static global::System.String OSVersion = System.Environment.OSVersion.Version.ToString();
        public static global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32> McFolderListLoader = new global::PCL.ModLoader.LoaderTask<global::System.Int32, global::System.Int32>("Minecraft Folder List", (_) => global::PCL.ModMinecraft.McFolderListLoadSub(), Priority: global::System.Threading.ThreadPriority.AboveNormal);
        public static global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Int32> McInstanceListLoader = new global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Int32>("Minecraft Instance List", global::PCL.ModMinecraft.InitMcInstanceList) { ReloadTimeout = 1 };
        private readonly static global::PCLCS.LegacyMinecraftInstanceHost<global::PCL.ModMinecraft.McInstance> InstanceHost = global::PCL.ModMinecraft.CreateInstanceHost();
        private readonly static global::PCLCS.LegacyMinecraftService<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McLibEntry, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModMinecraft.McFolder.Types> Service = new global::PCLCS.LegacyMinecraftService<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McLibEntry, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ModMinecraft.McFolder.Types>(global::PCL.ModMinecraft.CreateMinecraftHost());

        private static global::PCLCS.LegacyMinecraftInstanceHost<global::PCL.ModMinecraft.McInstance> CreateInstanceHost()
        {
            return new global::PCLCS.LegacyMinecraftInstanceHost<global::PCL.ModMinecraft.McInstance>()
            {
                SelectedFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected),
                CreateInstance = new Func<global::System.String, global::PCL.ModMinecraft.McInstance>(Name => new global::PCL.ModMinecraft.McInstance(Name)),
                ReadString = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.String>((Key, Instance) => global::PCL.Settings.Get<string>(Key, Instance: Instance)),
                ReadInteger = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Int32>((Key, Instance) => global::PCL.Settings.Get<int>(Key, Instance: Instance)),
                ReadBoolean = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Boolean>((Key, Instance) => global::PCL.Settings.Get<bool>(Key, Instance: Instance)),
                HasSaved = new Func<global::System.String, global::PCL.ModMinecraft.McInstance, global::System.Boolean>((Key, Instance) => global::PCL.Settings.HasSaved(Key, Instance: Instance)),
                WriteString = new Action<global::System.String, global::System.String, global::PCL.ModMinecraft.McInstance>((Key, Value, Instance) => global::PCL.Settings.Set(Key, Value, Instance: Instance)),
                WriteInteger = new Action<global::System.String, global::System.Int32, global::PCL.ModMinecraft.McInstance>((Key, Value, Instance) => global::PCL.Settings.Set(Key, Value, Instance: Instance)),
                WriteBoolean = new Action<global::System.String, global::System.Boolean, global::PCL.ModMinecraft.McInstance>((Key, Value, Instance) => global::PCL.Settings.Set(Key, Value, Instance: Instance)),
                ReadIni = new Func<global::System.String, global::System.String, global::System.String, global::System.String>((File, Key, DefaultValue) => global::PCL.ModBase.ReadIni(File, Key, DefaultValue)),
                WriteIni = new Action<global::System.String, global::System.String, global::System.String>((File, Key, Value) => global::PCL.ModBase.WriteIni(File, Key, Value)),
                ClearIniCache = new Action<global::System.String>(File => global::PCL.ModBase.IniClearCache(File)),
                ClearInstanceSettingsCache = global::PCL.ModMinecraft.ClearInstanceSettingsCache,
                OnUiThread = new Func<global::System.Boolean>(() => global::PCL.ModBase.RunInUi()),
                ImagePath = new Func<global::System.String>(() => global::PCL.ModBase.PathImage),
                NormalizeResourceUri = new Func<global::System.String, global::System.String>(Address => global::PCL.MyBitmap.NormalizeResourceUri(Address)),
                LogoForStorage = new Func<global::System.String, global::System.String>(Address => global::PCL.MinecraftLogoCompatibility.ToStorageUri(Address)),
                ReadVersionLogo = new Func<global::System.String, global::System.String, global::System.String>((File, DefaultValue) => global::PCL.MinecraftLogoCompatibility.ReadVersionLogo(File, DefaultValue)),
                WriteVersionLogo = new Action<global::System.String, global::System.String>((File, Address) => global::PCL.MinecraftLogoCompatibility.WriteVersionLogo(File, Address))
            };
        }
        private static global::PCLCS.LegacyMinecraftHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McLibEntry, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstanceCardType> CreateMinecraftHost()
        {
            global::PCLCS.LegacyMinecraftHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McLibEntry, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstanceCardType> @init = new global::PCLCS.LegacyMinecraftHost<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McFolder, global::PCL.ModMinecraft.McLibEntry, global::PCL.ModNet.NetFile, global::PCL.ModMinecraft.McInstanceCardType>();
            return (@init.Instance = InstanceHost, @init.ModpackEnabled = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled), @init.PackFolder = new Func<global::System.String>(() => global::PCL.MinecraftFolderPolicy.PackFolder(global::MeloongCore.Paths.Base)), @init.PackFolders = new Func<global::System.Collections.Generic.List<global::PCL.ModMinecraft.McFolder>>(() => global::PCL.ModModpackMode.CreatePackFolderList()), @init.FolderCache = new Func<global::System.String>(() => global::PCL.ModMinecraft._McFolderSelected), @init.StoreFolderCache = new Action<global::System.String>(Value => global::PCL.ModMinecraft._McFolderSelected = Value), @init.Folders = new Func<global::System.Collections.Generic.List<global::PCL.ModMinecraft.McFolder>>(() => global::PCL.ModMinecraft.McFolderList), @init.StoreFolders = new Action<global::System.Collections.Generic.List<global::PCL.ModMinecraft.McFolder>>(Value => global::PCL.ModMinecraft.McFolderList = Value), @init.SelectedInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.ModMinecraft._McInstanceSelected), @init.StoreSelectedInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.ModMinecraft._McInstanceSelected = Value), @init.Instances = new Func<global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>>>(() => global::PCL.ModMinecraft.McInstanceList), @init.StoreInstances = new Action<global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>>>(Value => global::PCL.ModMinecraft.McInstanceList = Value), @init.ForceRefresh = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.McInstanceListForceRefresh), @init.StoreForceRefresh = new Action<global::System.Boolean>(Value => global::PCL.ModMinecraft.McInstanceListForceRefresh = Value), @init.FirstLoad = new Func<global::System.Boolean>(() => global::PCL.ModMinecraft.IsFirstMcInstanceListLoad), @init.StoreFirstLoad = new Action<global::System.Boolean>(Value => global::PCL.ModMinecraft.IsFirstMcInstanceListLoad = Value), @init.AnimationControlEnabled = new Func<global::System.Int32>(() => global::PCL.ModAnimation.AniControlEnabled), @init.RefreshRecentWorlds = new Action(() => global::PCL.ModBase.RunInUi(() => global::PCL.ModMain.FrmLaunchRight?.RefreshRecentWorlds())), @init.ClearDownloadFolderCache = new Action(() => global::PCL.PageDownloadResourceDetail.CachedFolder.Clear()), @init.CheckPermission = global::PCL.ModBase.CheckPermissionWithException, @init.RandomDelay = new Action<global::System.Int32, global::System.Int32>((Minimum, Maximum) => global::System.Threading.Thread.Sleep(global::PCL.ModBase.RandomInteger(Minimum, Maximum))), @init.RepairFolderIcons = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.Int32>(Folders => global::PCL.MinecraftLogoCompatibility.RepairFolders(Folders)), @init.Hint = global::PCL.ModMinecraft.PublishMinecraftHint, @init.Message = new Action<global::System.String, global::System.String, global::System.Boolean>((Text, Title, Warn) => global::PCL.ModMain.MyMsgBox(Text, Title, IsWarn: Warn)), @init.SelectSkin = new Func<global::System.String>(() => global::MeloongCore.Wpf.Dialogs.SelectFile("选择皮肤文件", false, filter: new[] { (new[] { "png", "jpg", "jpeg", "webp" }, "皮肤文件") }).FirstOrDefault()), @init.ImageSize = global::PCL.ModMinecraft.ReadSkinImageSize, @init.SelectSkinModel = new Func<global::System.Int32>(() => global::PCL.ModMain.MyMsgBox("此皮肤为 Steve 模型（粗手臂）还是 Alex 模型（细手臂）？", "选择皮肤种类", "Steve 模型", "Alex 模型", "我不知道", HighLight: false)), @init.RequestJson = new Func<global::System.String, global::System.String, global::System.String>((Address, Backup) => global::PCL.ModNet.NetRequestByClientRetry(Address, BackupUrl: Backup, RequireJson: true)), @init.DownloadFile = new Action<global::System.String, global::System.String>((Address, Destination) => global::PCL.ModNet.NetDownloadByClient(Address, Destination)), @init.TempFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathTemp), @init.PureFolder = new Func<global::System.String>(() => global::PCL.ModBase.PathPure.Value), @init.OsVersion = new Func<global::System.String>(() => global::PCL.ModMinecraft.OSVersion), @init.NextUuid = new Func<global::System.Int32>(() => global::PCL.ModBase.GetUuid()), @init.ClientJar = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean, global::PCL.ModNet.NetFile>((Instance, CheckClient) => global::PCL.ModDownload.DlClientJarGet(Instance, CheckClient)), @init.CreateNetFile = new Func<global::System.Collections.Generic.IEnumerable<global::System.String>, global::System.String, global::MeloongCore.FileChecker, global::PCL.ModNet.NetFile>((Urls, Destination, Checker) => new global::PCL.ModNet.NetFile(Urls, Destination, Checker)), @init.DownloadPath = new Func<global::PCL.ModNet.NetFile, global::System.String>(File => File.LocalPath), @init.PreferMojang = new Func<global::System.Boolean>(() => global::PCL.ModDownload.DlSourcePreferMojang), @init.LauncherSources = new Func<global::System.String, global::System.Collections.Generic.IEnumerable<global::System.String>>(Address => global::PCL.ModDownload.DlSourceLauncherOrMetaGet(Address)), @init.LibrarySources = new Func<global::System.String, global::System.Collections.Generic.IEnumerable<global::System.String>>(Address => global::PCL.ModDownload.DlSourceLibraryGet(Address)), @init.AssetSources = new Func<global::System.String, global::System.Collections.Generic.IEnumerable<global::System.String>>(Address => global::PCL.ModDownload.DlSourceAssetsGet(Address)), @init.ExtractResource = new Action<global::System.String, global::System.String>((Destination, Name) => global::PCL.ModBase.ExtractResources(Destination, Name)), @init.AccessToken = new Func<global::System.String>(() => global::PCL.ModLaunch.McLoginLoader.Output.AccessToken), @init.LoginUuid = new Func<global::System.String>(() => global::PCL.ModLaunch.McLoginLoader.Output.Uuid), @init.ShowUpdateNotice = new Func<global::PCLCS.LegacyMinecraftUpdateNotice, global::System.Int32>(Notice => global::PCL.ModMain.MyMsgBox(Notice.Text, "Minecraft 更新提示", "确定", "下载", Notice.ThirdButton, Button3Action: Notice.ThirdAction)), @init.ShowUpdateLog = new Action<global::Newtonsoft.Json.Linq.JToken>(Version => global::PCL.ModDownloadLib.McUpdateLogShow(Version)), @init.OpenDownload = global::PCL.ModMinecraft.OpenMinecraftDownload, @init).@init;
        }
        private static void ClearInstanceSettingsCache(global::PCL.ModMinecraft.McInstance Instance)
        {
            foreach (var Entry in global::PCL.Settings.Entries.Values)
            {
                if ((Entry.Source == global::PCL.Settings.Sources.Instance))
                    Entry.ValueCache.Remove(Instance.PathVersion);
            }
        }
        private static void PublishMinecraftHint(global::System.String Text, global::System.Boolean IsError)
        {
            if (IsError)
            {
                global::PCL.ModMain.Hint(Text, global::PCL.ModMain.HintType.Red);
            }
            else
            {
                global::PCL.ModMain.Hint(Text);
            }
        }
        private static (global::System.Double Width, global::System.Double Height) ReadSkinImageSize(global::System.String File)
        {
            var Image = new global::PCL.MyBitmap(File);
            return (Image.Pic.Width, Image.Pic.Height);
        }
        private static void OpenMinecraftDownload(global::System.String VersionName)
        {
            global::PCL.ModBase.RunInUi(() =>
        {
            global::PCL.PageDownloadInstall.VersionWaitingSelect = VersionName;
            global::PCL.ModMain.FrmMain.PageChange((global::PCL.FormMain.PageStackData)global::PCL.FormMain.PageType.Download, global::PCL.FormMain.PageSubType.DownloadInstall);
        });
        }

        public static string McFolderSelected
        {
            get
            {
                return global::PCL.ModMinecraft.Service.McFolderSelected;
            }
            set
            {
                global::PCL.ModMinecraft.Service.McFolderSelected = value;
            }
        }
        private static void SetMcFolder(global::System.String Value)
        {
            global::PCL.ModMinecraft.Service.McFolderSelected = Value;
        }
        private static void McFolderListLoadSub()
        {
            global::PCL.ModMinecraft.Service.McFolderListLoad();
            LegacyResourceRecycleCleanup.Schedule(McFolderList.Select(folder => folder.Location),
                new[] { ModBase.PathTemp, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCL") });
            LegacyResourceRecycleCleanup.ScheduleBackupCleanup();
        }
        internal static void RepairSharedMinecraftIcons()
        {
            global::PCL.ModMinecraft.Service.RepairSharedMinecraftIcons();
        }
        public static void McFolderLauncherProfilesJsonCreate(global::System.String Folder)
        {
            global::PCL.ModMinecraft.Service.McFolderLauncherProfilesJsonCreate(Folder);
        }
        public static global::PCL.ModMinecraft.McInstance McInstanceSelected
        {
            get
            {
                return global::PCL.ModMinecraft._McInstanceSelected;
            }
            set
            {
                var previous = _McInstanceSelected;
                global::PCL.ModMinecraft.Service.SelectInstance(value);
                if (!ReferenceEquals(previous, _McInstanceSelected)) InstanceSelectionChanged?.Invoke();
            }
        }

        public class McFolder : global::PCLCS.LegacyMinecraftFolder<global::PCL.ModMinecraft.McFolder.Types>
        {
            public enum Types
            {
                Vanilla,
                RenamedVanilla,
                Custom
            }
            public override global::System.Boolean Equals(global::System.Object Obj)
            {
                return ((Obj is global::PCL.ModMinecraft.McFolder) && (base.Equals(Obj)));
            }
        }

        public class McInstance : global::PCLCS.LegacyMcInstance<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType>
        {
            public enum McInstanceState
            {
                Error,
                Original,
                Snapshot,
                Fool,
                OptiFine,
                Old,
                Forge,
                NeoForge,
                LiteLoader,
                Fabric
            }
            public McInstance(global::System.String NameOrFullPath) : base(NameOrFullPath, () => global::PCL.ModMinecraft.InstanceHost)
            {
            }
            internal new string LogoForStorage
            {
                get
                {
                    return base.LogoForStorage;
                }
            }

            public global::PCL.MyVirtualizingElement<global::PCL.MyListItem> ToListItem(global::System.Action<global::PCL.MyListItem, global::System.EventArgs> ContentHandler = null)
            {
                return new global::PCL.MyVirtualizingElement<global::PCL.MyListItem>(() =>
        {
            var NewItem = new global::PCL.MyListItem()
            {
                Info = Info,
                Height = 42d,
                Tag = this,
                SnapsToDevicePixels = true,
                Type = global::PCL.MyListItem.CheckType.Clickable,
                ContentHandler = ContentHandler
            };
            NewItem.Inlines.Clear();
            NewItem.Inlines.Add(new global::System.Windows.Documents.Run(this.Name));
            if (!string.IsNullOrEmpty(global::PCL.ModBase.ReadIni(this.PathVersion + @"PCL\Setup.ini", "CustomInfo")))
            {
                NewItem.Inlines.Add(new global::System.Windows.Documents.Run("  |  " + this.VersionDisplayName()) { Foreground = new global::PCL.ModBase.MyColor(215d, 215d, 215d), FontSize = 12d });
            }
            try
            {
                if (base.Logo.EndsWithF(@"PCL\Logo.png"))
                {
                    NewItem.Logo = (this.PathVersion + @"PCL\Logo.png");
                }
                else
                {
                    NewItem.Logo = this.Logo;
                }
            }
            catch (global::System.Exception ex)
            {
                global::MeloongCore.Logger.Error(ex, "加载版本图标失败", global::MeloongCore.LogBehavior.Toast);
                NewItem.Logo = "pack://application:,,,/images/Blocks/RedstoneBlock.png";
            }
            return NewItem;
        })
                {
                    Height = 42d
                };
            }
            public static bool operator ==(global::PCL.ModMinecraft.McInstance a, global::PCL.ModMinecraft.McInstance b)
            {
                if ((a is null && b is null))
                    return true;
                if ((a is null || b is null))
                    return false;
                return global::MeloongCore.PathUtils.IsSame(a.PathVersion, b.PathVersion);
            }
            public static bool operator !=(global::PCL.ModMinecraft.McInstance a, global::PCL.ModMinecraft.McInstance b)
            {
                return !(((a) == (b)));
            }
        }

        public sealed class McInstanceLock
        {
            internal static void Release(IWorker Worker, string InstancePath) => Native.Release(Worker, InstancePath);
            private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, global::PCLCS.LegacyMinecraftInstanceLease> LockedPaths = new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.String, global::PCLCS.LegacyMinecraftInstanceLease>(global::System.StringComparer.OrdinalIgnoreCase);
            private static readonly global::PCLCS.LegacyMinecraftInstanceLocks Native = new global::PCLCS.LegacyMinecraftInstanceLocks(global::PCL.ModMinecraft.McInstanceLock.LockedPaths);
            internal static bool IsAnyLocked
            {
                get
                {
                    return global::PCL.ModMinecraft.McInstanceLock.Native.IsAnyLocked;
                }
            }
            public static global::System.Boolean TryAcquire(global::MeloongCore.IWorker Worker, global::System.String InstancePath)
            {
                return global::PCL.ModMinecraft.McInstanceLock.Native.TryAcquire(Worker, InstancePath);
            }
            internal static bool TryAcquireSnapshot(IWorker Worker, string InstancePath)
            {
                return Native.TryAcquireSnapshot(Worker, InstancePath);
            }
            internal static bool BlocksNavigation(McInstance Instance)
            {
                return Native.BlocksNavigation(Instance?.PathVersion);
            }
            public static global::System.Boolean IsLocked(global::PCL.ModMinecraft.McInstance Instance)
            {
                return global::PCL.ModMinecraft.McInstanceLock.Native.IsLocked(Instance?.PathVersion);
            }
        }
        public static global::System.String GetMcFoolName(global::System.String Name)
        {
            return global::PCLCS.LegacyMinecraftText.GetMcFoolName(Name);
        }
        private static void InitMcInstanceList(global::PCL.ModLoader.LoaderTask<global::System.String, global::System.Int32> Loader)
        {
            global::PCL.ModMinecraft.Service.InitMcInstanceList(Loader.Input, () => Loader.IsCanceled);
        }
        private static global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>> InitMcInstanceListWithCache(global::System.String Folder)
        {
            return global::PCL.ModMinecraft.Service.InitMcInstanceListWithCache(Folder);
        }
        private static global::System.Collections.Generic.Dictionary<global::PCL.ModMinecraft.McInstanceCardType, global::System.Collections.Generic.List<global::PCL.ModMinecraft.McInstance>> InitMcInstanceListWithoutCache(global::System.String Folder)
        {
            return global::PCL.ModMinecraft.Service.InitMcInstanceListWithoutCache(Folder);
        }
        public enum McInstanceCardType
        {
            Star = -(1),
            Auto = 0,
            Hidden = 1,
            API = 2,
            OriginalLike = 3,
            Rubbish = 4,
            Fool = 5,
            Error = 6
        }

        public struct McSkinInfo
        {
            public global::System.Boolean IsSlim;
            public global::System.String LocalFile;
            public global::System.Boolean IsVaild;
        }
        public static global::PCL.ModMinecraft.McSkinInfo McSkinSelect()
        {
            var Result = global::PCL.ModMinecraft.Service.McSkinSelect();
            return new global::PCL.ModMinecraft.McSkinInfo() { IsSlim = Result.IsSlim, LocalFile = Result.LocalFile, IsVaild = Result.IsVaild };
        }
        public static global::System.String McSkinGetAddress(global::System.String UUID, global::System.String Type)
        {
            return global::PCL.ModMinecraft.Service.McSkinGetAddress(UUID, Type);
        }
        public static global::System.String McSkinDownload(global::System.String Address)
        {
            return global::PCL.ModMinecraft.Service.McSkinDownload(Address);
        }
        public static global::System.String McSkinSex(global::System.String Uuid)
        {
            return global::PCLCS.LegacyMinecraftText.McSkinSex(Uuid);
        }
        public class McLibEntry : global::PCLCS.LegacyMinecraftLibrary
        {
        }
        public static global::System.Boolean McJsonRuleCheck(global::Newtonsoft.Json.Linq.JToken RuleToken)
        {
            return global::PCL.ModMinecraft.Service.McJsonRuleCheck(RuleToken);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModMinecraft.McLibEntry> McLibListGet(global::PCL.ModMinecraft.McInstance Instance, global::System.Boolean IncludeMainJar)
        {
            return global::PCL.ModMinecraft.Service.McLibListGet(Instance, IncludeMainJar);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModMinecraft.McLibEntry> McLibListGetWithJson(global::Newtonsoft.Json.Linq.JObject JsonObject, global::System.Boolean KeepSameNameDifferentVersionResult = false, global::System.String CustomMcFolder = null)
        {
            return global::PCL.ModMinecraft.Service.McLibListGetWithJson(JsonObject, KeepSameNameDifferentVersionResult, CustomMcFolder);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> McLibNetFilesFromInstance(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.ModMinecraft.Service.McLibNetFilesFromInstance(Instance);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> McLibNetFilesFromTokens(global::System.Collections.Generic.List<global::PCL.ModMinecraft.McLibEntry> Libs, global::System.String CustomMcFolder = null)
        {
            return global::PCL.ModMinecraft.Service.McLibNetFilesFromTokens(Libs, CustomMcFolder);
        }
        public static global::System.String McLibGet(global::System.String Original, global::System.Boolean WithHead = true, global::System.Boolean IgnoreLiteLoader = false, global::System.String CustomMcFolder = null)
        {
            return global::PCL.ModMinecraft.Service.McLibGet(Original, WithHead, IgnoreLiteLoader, CustomMcFolder);
        }
        public static global::System.Object ShouldIgnoreFileCheck(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.ModMinecraft.Service.ShouldIgnoreFileCheck(Instance);
        }
        public static global::Newtonsoft.Json.Linq.JToken McAssetsGetIndex(global::PCL.ModMinecraft.McInstance Instance, global::System.Boolean ReturnLegacyOnError = false, global::System.Boolean CheckURLEmpty = false)
        {
            return global::PCL.ModMinecraft.Service.McAssetsGetIndex(Instance, ReturnLegacyOnError, CheckURLEmpty);
        }
        public static global::System.String McAssetsGetIndexName(global::PCL.ModMinecraft.McInstance Instance)
        {
            return global::PCL.ModMinecraft.Service.McAssetsGetIndexName(Instance);
        }
        public static global::System.Collections.Generic.List<global::PCL.ModNet.NetFile> McAssetsFixList(global::PCL.ModMinecraft.McInstance Instance, global::System.Boolean CheckHash, [Optional] ref global::PCL.ModLoader.LoaderBase ProgressFeed)
        {
            var CurrentFeed = ProgressFeed;
            global::System.Action<global::System.Double> Update = (global::System.Action<global::System.Double>)null;
            if (CurrentFeed is not null)
                Update = new Action<global::System.Double>(Value => CurrentFeed.Progress = Value);
            return global::PCL.ModMinecraft.Service.McAssetsFixList(Instance, CheckHash, Update);
        }
        public static void McDownloadClientUpdateHint(global::System.String VersionName, global::Newtonsoft.Json.Linq.JObject Json)
        {
            global::PCL.ModMinecraft.Service.McDownloadClientUpdateHint(VersionName, Json);
        }
        public static global::System.String FilterAccessToken(global::System.String Raw, global::System.Char FilterChar)
        {
            return global::PCL.ModMinecraft.Service.FilterAccessToken(Raw, FilterChar);
        }
        public static global::System.String FilterUserName(global::System.String Raw, global::System.Char FilterChar)
        {
            return global::PCLCS.LegacyMinecraftText.FilterUserName(Raw, FilterChar);
        }
    }

    internal sealed class McInstanceDirectoryScanner
    {
        private McInstanceDirectoryScanner()
        {
        }
        internal static global::System.IO.DirectoryInfo[] GetDirectories(global::System.String Folder)
        {
            return global::PCLCS.LegacyMinecraftDirectoryScanner.GetDirectories(Folder);
        }
        internal static global::System.Boolean HasFiles(global::System.IO.DirectoryInfo Folder)
        {
            return global::PCLCS.LegacyMinecraftDirectoryScanner.HasFiles(Folder);
        }
    }
}
