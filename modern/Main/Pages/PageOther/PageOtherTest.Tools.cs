using System;
using System.Threading;
using System.Windows.Input;
using PCLCS;
using PCLLF.Wpf.Glass;
using System.Windows.Controls;

namespace PCL
{
    // Only LF original XAML/type/Handles/model identity adapters; all workflows are C#.
    public partial class PageOtherTest
    {
        private bool PageLoaded;
        private static int MemoryRunning;
        private static int CleanupRunning;
        private LegacyToolboxPageController<PCL.ModNet.NetFile, PCL.ModNet.LoaderDownload, PCL.ModLoader.LoaderTask<int, int>, PCL.ModLoader.LoaderCombo<int>> _controller;
        internal LegacyToolboxPageController<PCL.ModNet.NetFile, PCL.ModNet.LoaderDownload, PCL.ModLoader.LoaderTask<int, int>, PCL.ModLoader.LoaderCombo<int>> Controller
        {
            get
            {
                if (_controller is null)
                    _controller = new LegacyToolboxPageController<PCL.ModNet.NetFile, PCL.ModNet.LoaderDownload, PCL.ModLoader.LoaderTask<int, int>, PCL.ModLoader.LoaderCombo<int>>(AuxiliaryPageRuntime.Tools, new LegacyToolboxPageView() { Url = new Func<GlassValidatedTextBox>(() => this.TextDownloadUrl), Folder = new Func<GlassValidatedTextBox>(() => this.TextDownloadFolder), Name = new Func<GlassValidatedTextBox>(() => this.TextDownloadName), Start = new Func<Button>(() => this.BtnDownloadStart), Open = new Func<Button>(() => this.BtnDownloadOpen), Clear = new Func<Button>(() => this.BtnClear), Loaded = new Func<bool>(() => PageLoaded), SetLoaded = new Action<bool>(Value => PageLoaded = Value) });
                return _controller;
            }
        }

        internal static int AcquireCleanup()
        {
            return Interlocked.CompareExchange(ref CleanupRunning, 1, 0);
        }

        internal static int AcquireMemory()
        {
            return Interlocked.CompareExchange(ref MemoryRunning, 1, 0);
        }

        internal static void ReleaseCleanup()
        {
            Interlocked.Exchange(ref CleanupRunning, 0);
        }

        internal static void ReleaseMemory()
        {
            Interlocked.Exchange(ref MemoryRunning, 0);
        }

        internal static int ReadCleanup()
        {
            return Volatile.Read(ref CleanupRunning);
        }

        internal static bool IsCleaningGameFiles
        {
            get
            {
                return AuxiliaryPageRuntime.Tools.IsCleaningGameFiles;
            }
        }

        public PageOtherTest()
        {
            InitializeComponent();
            AttachScroll(PanBack);
            Controller.InitializeValidation();
            Loaded += (_, _) => MeLoaded();
            TextDownloadUrl.TextChanged += (_, _) => { TextDownloadUrl_TextChanged(); StartButtonRefresh(); };
            TextDownloadFolder.TextChanged += (_, _) => { SaveCacheDownloadFolder(); StartButtonRefresh(); };
            TextDownloadName.TextChanged += (_, _) => StartButtonRefresh();
            TextDownloadUrl.KeyDown += TextDownloadUrl_KeyDown;
            TextDownloadName.KeyDown += TextDownloadUrl_KeyDown;
            BtnDownloadStart.Click += (_, _) => BtnDownloadStart_Click();
            BtnDownloadSelect.Click += (_, _) => BtnDownloadSelect_Click();
            BtnDownloadOpen.Click += (_, _) => BtnDownloadOpen_Click();
            BtnClear.Click += (_, _) => BtnClear_Click();
            BtnMemory.Click += (_, _) => BtnMemory_Click();
        }

        private void MeLoaded()
        {
            Controller.Loaded();
        }

        private void StartButtonRefresh()
        {
            Controller.StartButtonRefresh();
        }

        private void TextDownloadUrl_TextChanged()
        {
            Controller.UrlChanged();
        }

        private void SaveCacheDownloadFolder()
        {
            Controller.SaveFolder();
        }

        private void TextDownloadUrl_KeyDown(object sender, KeyEventArgs e)
        {
            Controller.UrlKeyDown(e);
        }

        private void BtnDownloadStart_Click()
        {
            Controller.StartDownload();
        }

        public static void StartCustomDownload(string Url, string FileName, string Folder = null)
        {
            AuxiliaryPageRuntime.Tools.StartCustomDownload(Url, FileName, Folder);
        }

        private static bool TryStartCustomDownload(string Url, string FileName, string Folder)
        {
            return AuxiliaryPageRuntime.Tools.TryStartCustomDownload(Url, FileName, Folder);
        }

        private static void DownloadState(PCL.ModLoader.LoaderCombo<int> Loader, ToolboxDownloadSession Session)
        {
            AuxiliaryPageRuntime.Tools.DownloadState(Loader, Session);
        }

        private void BtnDownloadSelect_Click()
        {
            Controller.SelectFolder();
        }

        private void BtnDownloadOpen_Click()
        {
            Controller.OpenFolder();
        }

        private void BtnClear_Click()
        {
            Controller.Clear();
        }

        public static void RubbishClear()
        {
            AuxiliaryPageRuntime.Tools.RubbishClear();
        }

        private static bool GameFilesBusy()
        {
            return AuxiliaryPageRuntime.Tools.GameFilesBusy();
        }

        private void BtnMemory_Click()
        {
            Controller.Memory();
        }

        public static void MemoryOptimize(bool ShowHint)
        {
            AuxiliaryPageRuntime.Tools.MemoryOptimize(ShowHint);
        }

        public static void MemoryOptimizeInternal(bool ShowHint)
        {
            AuxiliaryPageRuntime.Tools.MemoryOptimizeInternal(ShowHint);
        }
    }
}
