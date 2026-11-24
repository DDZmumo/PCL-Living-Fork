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
using global::System.Windows.Markup;
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
using SearchCore = global::PCLCS.ResourceSearchPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ResourceProject, global::PCL.ResourceSearcher.SearchResult, global::PCL.ResourceSearcher.SearchRequest>;
using ThrottleDebounce;

namespace PCL
{
    [global::System.Windows.Markup.ContentPropertyAttribute("SearchTags")]
    public partial class PageResource
    {
        public static global::PCL.ModMinecraft.McInstance TargetInstance = (global::PCL.ModMinecraft.McInstance)null;
        public static global::System.String TargetName = (global::System.String)null;
        public global::PCL.ModLoader.LoaderTask<global::PCL.ResourceSearcher.SearchRequest, global::System.Int32> Loader;
        public global::PCL.ResourceSearcher.SearchResult Storage;
        public const global::System.Int32 PAGE_SIZE = 40;
        public global::System.Int32 Page;
        private const string ResultPageSizeSetting = "ToolDownloadResourcePageSize";
        private static readonly int[] ResultPageSizes = [15, 30, 40, 60, 80];
        private int resultPageSize = 15;
        private bool syncingResultPageSize;
        private ResourceSearcher.SearchRequest lastCompletedSearch;
        internal ModMinecraft.McInstance DownloadTargetInstance { get => Service.DownloadTargetInstance; set => Service.DownloadTargetInstance = value; }
        private global::PCLCS.ResourceSearchPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ResourceProject, global::PCL.ResourceSearcher.SearchResult, global::PCL.ResourceSearcher.SearchRequest> Controller;
        private global::PCLCS.ResourceSearchPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ResourceProject, global::PCL.ResourceSearcher.SearchResult, global::PCL.ResourceSearcher.SearchRequest> Service
        {
            get
            {
                if (this.Controller is null)
                    this.Controller = new global::PCLCS.ResourceSearchPageController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McInstance.McInstanceState, global::PCL.ModMinecraft.McInstanceCardType, global::PCL.ResourceProject, global::PCL.ResourceSearcher.SearchResult, global::PCL.ResourceSearcher.SearchRequest>(this.CreateHost());
                return this.Controller;
            }
        }

        public PageResource()
        {
            __LfAttachOwnHandles();
            Loader = global::PCL.ResourceSearcher.GetLoader(this.LoaderInput, QueueSearchPreview);
            this.Storage = new global::PCL.ResourceSearcher.SearchResult();
            this.Page = 0;
            this.InitializeComponent();
            foreach (int size in ResultPageSizes)
                ComboResultPageSize.Items.Add(new MyComboBoxItem { Content = $"{size} 条/页", Tag = size });
            SyncResultPageSize();
            ComboResultPageSize.SelectionChanged += (_, _) => {
                if (syncingResultPageSize || FavoriteStore is not null || ComboResultPageSize.SelectedItem is not MyComboBoxItem { Tag: int size } ||
                    size == resultPageSize) return;
                resultPageSize = size;
                Settings.Set(ResultPageSizeSetting, size);
                RestartForPageSize();
            };
        }

        private bool SyncResultPageSize()
        {
            int saved = Settings.Get<int>(ResultPageSizeSetting);
            int index = Array.IndexOf(ResultPageSizes, saved);
            if (index < 0) index = 0;
            int size = ResultPageSizes[index];
            bool changed = resultPageSize != size;
            resultPageSize = size;
            syncingResultPageSize = true;
            try { ComboResultPageSize.SelectedIndex = index; }
            finally { syncingResultPageSize = false; }
            return changed;
        }

        private void RestartForPageSize()
        {
            Service.ResetPage();
            StartResultPage();
        }

        private void StartResultPage()
        {
            if (Loader.Input is not { } previous) return;
            if (Loader.State == ModBase.LoadState.Finished && ReferenceEquals(previous.Storage, Storage))
                lastCompletedSearch = previous;

            // Each worker owns its cursors. Reusing an interrupted worker's storage can skip rows.
            var completed = lastCompletedSearch is { } finished && finished.SameQuery(previous) ? finished.Storage : null;
            Storage = completed is null ? new ResourceSearcher.SearchResult() : new ResourceSearcher.SearchResult {
                Results = completed.Results.ToList(), ErrorMessage = completed.ErrorMessage,
                CurseForgeOffset = completed.CurseForgeOffset, CurseForgeTotal = completed.CurseForgeTotal,
                ModrinthOffset = completed.ModrinthOffset, ModrinthTotal = completed.ModrinthTotal,
                ModrinthDirectOffset = completed.ModrinthDirectOffset, ModrinthDirectTotal = completed.ModrinthDirectTotal
            };
            int start = checked(Page * resultPageSize);
            var cached = Storage.Results.Skip(start).Take(resultPageSize).ToArray();
            var request = new ResourceSearcher.SearchRequest(previous.Type, Storage, checked(start + resultPageSize)) {
                PresentationPageSize = resultPageSize,
                SearchText = previous.SearchText, GameVersion = previous.GameVersion, Tag = previous.Tag,
                ModLoaders = previous.ModLoaders, Sources = previous.Sources
            };
            Service.ShowPendingPage(cached.Length >= resultPageSize);
            Loader.Start(request, IsForceRestart: true);
            if (cached.Length > 0) QueueSearchPreview(request, cached);
            NotifyResultsChanged();
        }

        private global::PCLCS.ResourceSearchPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.ResourceProject, global::PCL.ResourceSearcher.SearchResult, global::PCL.ResourceSearcher.SearchRequest> CreateHost()
        {
            return new global::PCLCS.ResourceSearchPageHost<global::PCL.ModMinecraft.McInstance, global::PCL.ResourceProject, global::PCL.ResourceSearcher.SearchResult, global::PCL.ResourceSearcher.SearchRequest>()
            {
                View = this.CreateView,
                TargetInstance = new Func<global::PCL.ModMinecraft.McInstance>(() => global::PCL.PageResource.TargetInstance),
                SetTargetInstance = new Action<global::PCL.ModMinecraft.McInstance>(Value => global::PCL.PageResource.TargetInstance = Value),
                TargetName = new Func<global::System.String>(() => global::PCL.PageResource.TargetName),
                SetTargetName = new Action<global::System.String>(Value => global::PCL.PageResource.TargetName = Value),
                Storage = new Func<global::PCL.ResourceSearcher.SearchResult>(() => this.Storage),
                SetStorage = new Action<global::PCL.ResourceSearcher.SearchResult>(Value => this.Storage = Value),
                Page = new Func<global::System.Int32>(() => this.Page),
                ItemsPerPage = () => resultPageSize,
                SetPage = new Action<global::System.Int32>(Value => this.Page = Value),
                Request = new Func<global::PCLCS.ResourceTypes, global::PCL.ResourceSearcher.SearchResult, global::System.Int32, global::PCL.ResourceSearcher.SearchRequest>((Type, Store, Count) => new global::PCL.ResourceSearcher.SearchRequest(Type, Store, Count)),
                Input = new Func<global::PCL.ResourceSearcher.SearchRequest>(() => this.Loader.Input),
                ShouldStart = new Func<global::PCL.ResourceSearcher.SearchRequest, global::System.Boolean>(Input => this.Loader.ShouldStart(ref Input)),
                Start = () => {
                    Loader.Start();
                    if (Loader.State == ModBase.LoadState.Loading) Service.ShowPendingPage();
                    NotifyResultsChanged();
                },
                StartPage = StartResultPage,
                ResetFinishedTime = new Action(() => this.Loader.LastFinishedTime = 0L),
                InitializeLoader = new Action(() => ((global::PCL.MyPageRight)this.Parent).PageLoaderInit(this.Load, this.PanLoad, this.PanContent, this.PanAlways, this.Loader, (_) => this.Load_OnFinish(), this.LoaderInput, ProjectContent: true)),
                Restart = new Action(() => ((global::PCL.MyPageRight)this.Parent).PageLoaderRestart()),
                BackToTop = new Action(() => global::PCL.ModMain.FrmMain?.BackToTop()),
                InstallPack = new Action(() => global::PCL.ModModpack.ModpackInstall()),
                LoaderName = new Action<global::System.String>(Value => this.Loader.Name = Value),
                Failed = new Func<global::System.Boolean>(() => (this.Loader.State == global::PCL.ModBase.LoadState.Failed)),
                Error = new Func<global::System.Exception>(() => this.Loader.Error),
                AllDrops = new Func<global::System.Collections.Generic.List<global::System.Int32>>(() => global::PCL.ModDownload.AllDrops),
                Item = new Func<global::System.Object, global::System.Object, global::System.Boolean, global::System.Windows.Controls.ComboBoxItem>((Content, Tag, Selected) => new global::PCL.MyComboBoxItem() { Content = Content, Tag = Tag, IsSelected = Selected }),
                ProjectItem = new Func<global::PCL.ResourceProject, global::System.Boolean, global::System.Boolean, global::System.Windows.FrameworkElement>((Project, ShowVersion, ShowLoader) => Project.ToResourceItem(ShowVersion, ShowLoader))
            };
        }

        private global::PCLCS.ResourceSearchPageView CreateView()
        {
            return new global::PCLCS.ResourceSearchPageView()
            {
                Name = TextSearchName,
                Version = TextSearchVersion,
                Source = ComboSearchSource,
                Tag = ComboSearchTag,
                Loader = ComboSearchLoader,
                InstallPack = BtnSearchInstallModPack,
                Pages = CardPages,
                First = BtnPageFirst,
                Left = BtnPageLeft,
                Right = BtnPageRight,
                Error = HintError,
                PageNumber = LabPage,
                Projects = PanProjects,
                SearchTitle = new Action<global::System.String>(Value => this.PanAlways.Title = Value),
                LoadingText = new Action<global::System.String>(Value => this.Load.Text = Value),
                ErrorText = new Action<global::System.String>(Value => this.HintError.Text = Value),
                ScrollHome = new Action(() => this.ScrollToHome()),
                ScrollTop = new Action(() => this.ScrollToTop())
            };
        }

        public global::System.Windows.Controls.ItemCollection SearchTags
        {
            get
            {
                return this.ComboSearchTag.Items;
            }
        }

        public string TypeName
        {
            get
            {
                return this.Service.TypeName;
            }

            set
            {
                this.Service.TypeName = value;
            }
        }

        public string TypeNameSpaced
        {
            get
            {
                return this.Service.TypeNameSpaced;
            }

            set
            {
                this.Service.TypeNameSpaced = value;
            }
        }

        public global::PCLCS.ResourceTypes PageType
        {
            get
            {
                if (FavoriteStore is not null) return FavoriteCategory ?? ResourceTypes.Any;
                return this.Service.PageType;
            }

            set
            {
                if (FavoriteStore is not null) { ChooseFavoriteCategory(value == ResourceTypes.Any ? null : value); return; }
                this.Service.PageType = value;
            }
        }

        private void PageResource_Loaded(global::System.Object sender, global::System.EventArgs e)
        {
            if (FavoriteStore is not null) { FavoritesChanged(); return; }
            if (SyncResultPageSize() && TargetInstance is null) RestartForPageSize();
            this.Service.Loaded();
            if (Loader.State == ModBase.LoadState.Loading)
                Service.ShowPendingPage(SearchPreview?.Children.Count >= resultPageSize);
        }

        private global::PCL.ResourceSearcher.SearchRequest LoaderInput()
        {
            if (FavoriteStore is not null) throw new InvalidOperationException("本地收藏不使用在线搜索加载器。");
            return this.Service.LoaderInput();
        }

        private void Load_OnFinish()
        {
            if (FavoriteStore is not null) { FavoritesChanged(); return; }
            // PageLoaderInit calls this from PreviewFinish, before the loader transitions to Finished.
            if (Loader.State is not (ModBase.LoadState.Loading or ModBase.LoadState.Finished) ||
                Loader.Input is not { } input || !ReferenceEquals(input.Storage, Storage)) return;
            lastCompletedSearch = input;
            this.Service.LoadOnFinish();
            NotifyResultsChanged();
        }

        private ResourceSearcher.SearchRequest previewInput;
        private StackPanel previewRows;
        internal StackPanel SearchPreview
        {
            get
            {
                if (previewInput is null) return null;
                if (MatchesSearch(previewInput) && (Loader.State == ModBase.LoadState.Loading ||
                    Loader.State == ModBase.LoadState.Finished && PanContent.Visibility != Visibility.Visible)) return previewRows;
                previewInput = null;
                previewRows = null;
                return null;
            }
        }
        internal int ResultsRevision { get; private set; }
        internal event Action ResultsChanged;
        internal void NotifyResultsChanged()
        {
            ResultsRevision++;
            ResultsChanged?.Invoke();
        }
        private bool MatchesSearch(ResourceSearcher.SearchRequest input) =>
            Page == (input.PresentationPageSize > 0 ? (input.TargetResultCount - 1) / input.PresentationPageSize : 0) &&
            ReferenceEquals(input, Loader.Input) && ReferenceEquals(input.Storage, Storage);
        internal bool IsOnlineSearch => FavoriteStore is null;
        internal bool IsPreviewPageFull => SearchPreview?.Children.Count >= resultPageSize;
        private bool CanNavigateOnlinePage => Loader.State != ModBase.LoadState.Loading || IsPreviewPageFull;
        private void QueueSearchPreview(ResourceSearcher.SearchRequest input, IReadOnlyList<ResourceProject> projects)
        {
            _ = Dispatcher.InvokeAsync(() => {
                if (Loader.State != ModBase.LoadState.Loading || ReferenceEquals(lastCompletedSearch, input) || !MatchesSearch(input)) return;
                var rows = new StackPanel();
                bool showVersion = input.GameVersion is null;
                bool showLoader = input.ModLoaders == ModLoaders.None && input.Type is ResourceTypes.Mod or ResourceTypes.ModPack;
                foreach (ResourceProject project in projects.Take(resultPageSize))
                    rows.Children.Add(project.ToResourceItem(showVersion, showLoader, input.Type));
                previewInput = input;
                previewRows = rows;
                Service.ShowPendingPage(rows.Children.Count >= resultPageSize);
                NotifyResultsChanged();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Load_State(global::System.Object sender, global::PCL.MyLoading.MyLoadingState state, global::PCL.MyLoading.MyLoadingState oldState)
        {
            if (FavoriteStore is not null) return;
            this.Service.LoadState();
        }

        private void BtnPageFirst_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            if (FavoriteStore is not null) { ChangeFavoritePage(0); return; }
            if (!CanNavigateOnlinePage) return;
            this.Service.FirstPage();
        }

        private void BtnPageLeft_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            if (FavoriteStore is not null) { ChangeFavoritePage(Page - 1); return; }
            if (!CanNavigateOnlinePage) return;
            this.Service.PreviousPage();
        }

        private void BtnPageRight_Click(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            if (FavoriteStore is not null) { ChangeFavoritePage(Page + 1); return; }
            if (!CanNavigateOnlinePage) return;
            this.Service.NextPage();
        }

        private void ChangePage(global::System.Int32 NewPage)
        {
            if (FavoriteStore is not null) { ChangeFavoritePage(NewPage); return; }
            this.Service.ChangePage(NewPage);
        }

        private void StartNewSearch()
        {
            if (FavoriteStore is not null) {
                ApplyFavoriteSearch();
                if (FavoriteStore.LoadError is not null || FavoriteStore.LastOperationError is not null) FavoriteReload();
                return;
            }
            this.Service.StartNewSearch();
        }

        private void EnterTrigger(global::System.Object sender, global::System.Windows.Input.KeyEventArgs e)
        {
            if (FavoriteStore is not null) { if (e.Key == Key.Enter) { StartNewSearch(); e.Handled = true; } return; }
            this.Service.KeyDown(e.Key);
        }

        private void ResetFilter()
        {
            if (FavoriteStore is not null) { ResetFavoriteFilter(); return; }
            this.Service.ResetFilter();
        }

        private void TextSearchVersion_TextChanged()
        {
            if (FavoriteStore is not null) { if (!TextSearchVersion.IsDropDownOpen) FavoriteLoaderVisibility(); return; }
            this.Service.VersionTextChanged();
        }

        private void UpdateSearchLoaderVisibility()
        {
            if (FavoriteStore is not null) { FavoriteLoaderVisibility(); return; }
            this.Service.UpdateSearchLoaderVisibility();
        }

        private void BtnSearchInstallModPack_Click(global::System.Object sender, global::System.EventArgs e)
        {
            if (FavoriteStore is not null) return;
            this.Service.InstallExistingPack();
        }
    }
}
