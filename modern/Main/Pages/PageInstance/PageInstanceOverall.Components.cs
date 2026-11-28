using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
using ThrottleDebounce;

namespace PCL
{
    public partial class PageInstanceOverall
    {
        private global::System.Int32 ComponentRefreshId;
        private readonly global::PCL.ModLoader.LoaderTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult> ComponentsLoader;
        private class ComponentRequest : global::PCLCS.InstanceComponentRequestData<global::PCL.ModMinecraft.McInstance>
        {
        }

        private class ComponentResult : global::PCLCS.InstanceComponentResultData<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.InstalledComponentSummary.Item>
        {
        }

        private global::PCLCS.InstanceComponentsController<global::PCL.ModMinecraft.McInstance, global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult, global::PCL.InstalledComponentSummary.Item, global::System.Windows.Controls.Grid, global::System.Windows.Controls.Image> ComponentController;
        private global::PCLCS.InstanceComponentsController<global::PCL.ModMinecraft.McInstance, global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult, global::PCL.InstalledComponentSummary.Item, global::System.Windows.Controls.Grid, global::System.Windows.Controls.Image> ComponentView
        {
            get
            {
                if (this.ComponentController is null)
                {
                    global::PCLCS.InstanceComponentsHost<global::PCL.ModMinecraft.McInstance, global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult, global::PCL.InstalledComponentSummary.Item, global::System.Windows.Controls.Grid, global::System.Windows.Controls.Image> @init = new global::PCLCS.InstanceComponentsHost<global::PCL.ModMinecraft.McInstance, global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult, global::PCL.InstalledComponentSummary.Item, global::System.Windows.Controls.Grid, global::System.Windows.Controls.Image>();
                    this.ComponentController = new global::PCLCS.InstanceComponentsController<global::PCL.ModMinecraft.McInstance, global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult, global::PCL.InstalledComponentSummary.Item, global::System.Windows.Controls.Grid, global::System.Windows.Controls.Image>((@init.Instance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.LauncherInstanceContext.Instance), @init.Modpack = new Func<global::System.Boolean>(() => global::PCL.ModModpackMode.Enabled), @init.NextRevision = new Func<global::System.Int32>(() => global::PCLCS.InstanceComponentRevision.Next(ref this.ComponentRefreshId)), @init.Revision = new Func<global::System.Int32>(() => this.ComponentRefreshId), @init.CreateRequest = new Func<global::PCL.PageInstanceOverall.ComponentRequest>(() => new global::PCL.PageInstanceOverall.ComponentRequest()), @init.CreateResult = new Func<global::PCL.PageInstanceOverall.ComponentResult>(() => new global::PCL.PageInstanceOverall.ComponentResult()), @init.HasSavedIsolation = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Target => global::PCL.Settings.HasSaved("VersionArgumentIndieV2", Target)), @init.Isolation = new Func<global::PCL.ModMinecraft.McInstance, global::System.Boolean>(Target => global::PCL.Settings.Get<bool>("VersionArgumentIndieV2", Target)), @init.VersionFolder = new Func<global::PCL.ModMinecraft.McInstance, global::System.String>(Target => Target.PathVersion), @init.MinecraftFolder = new Func<global::System.String>(() => global::PCL.ModMinecraft.McFolderSelected), @init.ImageRoot = new Func<global::System.String>(() => global::PCL.ModBase.PathImage), @init.Version = new Func<global::PCL.ModMinecraft.McInstance, global::PCLCS.McVersion>(Target => Target.Version), @init.Components = new Func<global::PCL.ModMinecraft.McInstance, global::PCLCS.McComponents>(Target => global::PCLCS.Configs.McInstallManifest.Get(Target.Config)?.Components), @init.Start = new Action<global::PCL.PageInstanceOverall.ComponentRequest, global::System.Boolean>((Request, Force) => this.ComponentsLoader.Start(Request, IsForceRestart: Force)), @init.Cancel = new Action(() => this.ComponentsLoader.Cancel()), @init.Warn = new Action<global::System.Exception, global::System.String>((Err, Message) => global::MeloongCore.Logger.Warn(Err, Message)), @init.Cards = PanInstalledComponents, @init.CreateImage = new Func<global::System.String, global::System.Windows.Controls.Image>(Source => new Image { Source = GlassShellRuntime.LoadInstanceIcon(Source, null) }), @init.SetStretch = new Action<global::System.Windows.Controls.Image, global::System.Windows.Media.Stretch>((Image, Stretch) => Image.Stretch = Stretch), @init.CreateCard = new Func<global::System.String, global::System.Windows.Controls.Grid>(Title => new Grid { Tag = Title }), @init.TouchTitle = new Action<global::System.Windows.Controls.Grid>(Card =>
                    {
                        var title = Theme.Text((string)Card.Tag);
                        title.FontWeight = FontWeights.SemiBold;
                        title.VerticalAlignment = VerticalAlignment.Center;
                        title.Margin = new Thickness(12, 0, 0, 0);
                        var data = (FrameworkElement)Card.Children[0];
                        Card.Children.Clear();
                        var contents = new Grid { Children = { title, data } };
                        var frame = GlassPageResources.Plate(contents, new Thickness(0));
                        frame.Padding = new Thickness(0);
                        Card.Children.Add(frame);
                    }), @init).@init, global::PCL.InstalledComponentSummary.Engine);
                }

                return this.ComponentController;
            }
        }

        public PageInstanceOverall()
        {
            ComponentsLoader = new global::PCL.ModLoader.LoaderTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult>("读取已安装组件", this.ReadInstalledComponents);
            this.IsLoad = false;
            this.OriginalHeaderInfo = "";
            this.OverallView = new global::PCLCS.InstanceOverallPageView<GlassInstanceHeader>();
            InitializeComponent();
            AttachScroll(PanBack);
            Loaded += PageSetupLaunch_Loaded;
            Unloaded += (_, _) => { ComponentsLoader.Cancel(); LoadComponents.IsActive = false; };
            ComboDisplayType.SelectionChanged += ComboDisplayType_SelectionChanged;
            ComboDisplayLogo.SelectionChanged += (_, _) => ComboDisplayLogo_SelectionChanged();
            ItemDisplayLogoCustom.PreviewMouseLeftButtonDown += (_, _) => {
                if (ReferenceEquals(ComboDisplayLogo.SelectedItem, ItemDisplayLogoCustom)) ComboDisplayLogo_SelectionChanged();
            };
            BtnDisplayDesc.Click += BtnDisplayDesc_Click;
            BtnDisplayRename.Click += BtnDisplayRename_Click;
            BtnDisplayStar.Click += BtnDisplayStar_Click;
            BtnFolderVersion.Click += (_, _) => BtnFolderVersion_Click();
            BtnFolderSaves.Click += (_, _) => BtnFolderSaves_Click();
            BtnFolderMods.Click += (_, _) => BtnFolderMods_Click();
            BtnFolderScreen.Click += BtnFolderScreen_Click;
            BtnManageScript.Click += (_, _) => BtnManageScript_Click();
            BtnManageCheck.Click += BtnManageCheck_Click;
            BtnManageDelete.Click += BtnManageDelete_Click;
            BtnComponentsRetry.Click += (_, _) => RefreshInstalledComponents();
            ComponentsLoaderInit();
            BuildOverviewTiles();
        }

        // Quick folders as icon tiles, and the instance's contents as counts that open their pages.
        private readonly global::System.Collections.Generic.List<(string Folder, string[] Patterns, bool Directories, global::System.Windows.Controls.TextBlock Value)> _stats = new();

        private void BuildOverviewTiles()
        {
            string Icon(FormMain.PageSubType route) => global::PCL.ResourceBrowserCatalog.Find(route)!.Icon;
            GlassTiles.Action(BtnFolderVersion, global::PCL.ModBase.Logo.IconButtonOpen, "版本文件夹");
            GlassTiles.Action(BtnFolderSaves, Icon(FormMain.PageSubType.DownloadMap), "存档文件夹");
            GlassTiles.Action(BtnFolderMods, Icon(FormMain.PageSubType.DownloadMod), "Mod 文件夹");
            GlassTiles.Action(BtnFolderScreen, Icon(FormMain.PageSubType.DownloadResourcePack), "截图文件夹");
            void Stat(string title, FormMain.PageSubType icon, FormMain.PageSubType page, string folder, bool directories, params string[] patterns)
            {
                var (tile, value) = GlassTiles.Stat(Icon(icon), title, () => global::PCL.ModMain.FrmMain?.PageChange(FormMain.PageType.InstanceSetup, page));
                PanStats.Children.Add(tile);
                _stats.Add((folder, patterns, directories, value));
            }
            Stat("Mod", FormMain.PageSubType.DownloadMod, FormMain.PageSubType.InstanceMod, "mods", false, "*.jar", "*.jar.disabled", "*.zip");
            Stat("资源包", FormMain.PageSubType.DownloadResourcePack, FormMain.PageSubType.InstanceResourcePacks, "resourcepacks", true, "*.zip");
            Stat("光影包", FormMain.PageSubType.DownloadShader, FormMain.PageSubType.InstanceShaderPacks, "shaderpacks", true, "*.zip");
            Stat("存档", FormMain.PageSubType.DownloadMap, FormMain.PageSubType.InstanceSaves, "saves", true);
            Stat("截图", FormMain.PageSubType.DownloadResourcePack, FormMain.PageSubType.InstanceScreenshots, "screenshots", false, "*.png", "*.jpg");
            Loaded += (_, _) => RefreshStats();
        }

        private async void RefreshStats()
        {
            if (global::PCL.LauncherInstanceContext.Instance is not { } instance) return;
            string root;
            try { root = instance.PathIndie; } catch (global::System.Exception) { return; }
            var counts = await global::System.Threading.Tasks.Task.Run(() => _stats.Select(stat => {
                try {
                    var folder = new global::System.IO.DirectoryInfo(global::System.IO.Path.Combine(root, stat.Folder));
                    if (!folder.Exists) return 0;
                    int files = stat.Patterns.Sum(pattern => folder.EnumerateFiles(pattern).Count());
                    // Folder-form entries (unpacked resource packs, shader folders, worlds) count as one each.
                    int folders = stat.Directories ? folder.EnumerateDirectories().Count() : 0;
                    return files + folders;
                }
                catch (global::System.Exception) { return -1; }
            }).ToArray());
            if (!ReferenceEquals(instance, global::PCL.LauncherInstanceContext.Instance)) return;
            for (int index = 0; index < counts.Length && index < _stats.Count; index++)
                _stats[index].Value.Text = counts[index] < 0 ? "—" : counts[index].ToString();
        }

        private static global::PCLCS.InstanceComponentsTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult> ComponentTask(global::PCL.ModLoader.LoaderTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult> Loader)
        {
            return new global::PCLCS.InstanceComponentsTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult>()
            {
                Input = new Func<global::PCL.PageInstanceOverall.ComponentRequest>(() => Loader.Input),
                Output = new Func<global::PCL.PageInstanceOverall.ComponentResult>(() => Loader.Output),
                SetOutput = new Action<global::PCL.PageInstanceOverall.ComponentResult>(Value => Loader.Output = Value),
                IsCanceled = new Func<global::System.Boolean>(() => Loader.IsCanceled)
            };
        }

        private void ComponentsLoaderInit()
        {
            ComponentsLoader.OnStateChangedUi += (_, state, _) => {
                if (state != ComponentsLoader.State) return;
                if (state == ModBase.LoadState.Finished) InstalledComponentsLoaded(ComponentsLoader);
                UpdateComponentsLoading();
            };
        }

        private void RefreshInstalledComponents()
        {
            this.ComponentView.Refresh();
            UpdateComponentsLoading();
        }

        private void UpdateComponentsLoading()
        {
            bool loading = ModModpackMode.Enabled && ComponentsLoader.State == ModBase.LoadState.Loading;
            bool failed = ModModpackMode.Enabled && ComponentsLoader.State == ModBase.LoadState.Failed;
            PanBack.Visibility = loading || failed ? Visibility.Collapsed : Visibility.Visible;
            PanComponentsLoad.Visibility = loading || failed ? Visibility.Visible : Visibility.Collapsed;
            LoadComponents.IsActive = loading && IsLoaded;
            LoadComponents.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            LabComponentsLoad.Text = failed ? "读取已安装组件失败。" : "正在读取已安装组件";
            BtnComponentsRetry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ReadInstalledComponents(global::PCL.ModLoader.LoaderTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult> Loader)
        {
            this.ComponentView.Read(global::PCL.PageInstanceOverall.ComponentTask(Loader));
        }

        private void InstalledComponentsLoaded(global::PCL.ModLoader.LoaderTask<global::PCL.PageInstanceOverall.ComponentRequest, global::PCL.PageInstanceOverall.ComponentResult> Loader)
        {
            this.ComponentView.Loaded(global::PCL.PageInstanceOverall.ComponentTask(Loader));
        }

        internal void ShowInstalledComponents(global::System.Collections.Generic.IEnumerable<global::PCL.InstalledComponentSummary.Item> Items)
        {
            this.ComponentView.Show(Items);
        }
    }
}
