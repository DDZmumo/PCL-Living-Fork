using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using ThrottleDebounce;

namespace PCL
{
    // Original LF XAML fields/Handles and public entry points remain typed; business lives in C#.
    public partial class PageSetupLaunch
    {
        private const string JavaDownloadSearchPrompt = "选择下载条件后点击搜索。";
        private CancellationTokenSource _javaDownloadSearch;

        private global::PCLCS.LegacyPageSetupLaunchController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo> _controller;
        internal global::PCLCS.LegacyPageSetupLaunchController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo> Controller
        {
            get
            {
                if (this._controller is null)
                {
                    this._controller = new global::PCLCS.LegacyPageSetupLaunchController<global::PCL.ModMinecraft.McInstance, global::PCL.ModMinecraft.McSkinInfo>(global::PCL.SetupPageRuntime.Launch, new global::PCLCS.LegacyPageSetupLaunchView() { Page = new Func<global::System.Windows.DependencyObject>(() => this), PanBack = new Func<global::System.Windows.Controls.ScrollViewer>(() => this.PanBack), PanMain = new Func<global::System.Windows.Controls.StackPanel>(() => this.PanMain), CheckPersonalOptionsSync = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckPersonalOptionsSync), BtnImportPersonalOptions = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnImportPersonalOptions), CardArgument = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardArgument), LabArgumentIndie = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabArgumentIndie), ComboArgumentIndieV2 = new Func<global::PCLLF.Presentation.Controls.MyComboBoxView>(() => this.ComboArgumentIndieV2), ComboArgumentVisibie = new Func<global::PCLLF.Presentation.Controls.MyComboBoxView>(() => this.ComboArgumentVisibie), PanArgumentWindow = new Func<global::System.Windows.Controls.Grid>(() => this.PanArgumentWindow), ComboArgumentWindowType = new Func<global::PCLLF.Presentation.Controls.MyComboBoxView>(() => this.ComboArgumentWindowType), TextArgumentWindowWidth = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextArgumentWindowWidth), LabArgumentWindowMiddle = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabArgumentWindowMiddle), TextArgumentWindowHeight = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextArgumentWindowHeight), RadioRamType0 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioRamType0), RadioRamType1 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioRamType1), SliderRamCustom = new Func<global::PCLLF.Presentation.Controls.MySliderView>(() => this.SliderRamCustom), CheckArgumentRam = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckArgumentRam), PanRamDisplay = new Func<global::System.Windows.Controls.Grid>(() => this.PanRamDisplay), ColumnRamUsed = new Func<global::System.Windows.Controls.ColumnDefinition>(() => this.ColumnRamUsed), ColumnRamGame = new Func<global::System.Windows.Controls.ColumnDefinition>(() => this.ColumnRamGame), ColumnRamEmpty = new Func<global::System.Windows.Controls.ColumnDefinition>(() => this.ColumnRamEmpty), RectRamUsed = new Func<global::System.Windows.Shapes.Rectangle>(() => this.RectRamUsed), RectRamGame = new Func<global::System.Windows.Shapes.Rectangle>(() => this.RectRamGame), RectRamEmpty = new Func<global::System.Windows.Shapes.Rectangle>(() => this.RectRamEmpty), LabRamUsedTitle = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabRamUsedTitle), LabRamGameTitle = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabRamGameTitle), LabRamUsed = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabRamUsed), LabRamTotal = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabRamTotal), LabRamGame = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabRamGame), CardSkin = new Func<global::PCLLF.Presentation.Controls.MyCardView>(() => this.CardSkin), RadioSkinType0 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioSkinType0), RadioSkinType1 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioSkinType1), RadioSkinType2 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioSkinType2), RadioSkinType3 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioSkinType3), RadioSkinType4 = new Func<global::PCLLF.Presentation.Controls.MyRadioBoxView>(() => this.RadioSkinType4), PanSkinID = new Func<global::System.Windows.Controls.Grid>(() => this.PanSkinID), BtnSkinSave = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnSkinSave), BtnSkinCache = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnSkinCache), PanSkinChange = new Func<global::System.Windows.Controls.Grid>(() => this.PanSkinChange), BtnSkinChange = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnSkinChange), BtnSkinDelete = new Func<global::PCLLF.Presentation.Controls.MyButtonView>(() => this.BtnSkinDelete), BtnAdvanceJvmReset = new Func<global::PCLLF.Presentation.Controls.MyIconButtonView>(() => this.BtnAdvanceJvmReset), TextAdvanceJvm = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextAdvanceJvm), TextAdvanceGame = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextAdvanceGame), TextAdvanceRun = new Func<global::PCLLF.Presentation.Controls.MyTextBoxView>(() => this.TextAdvanceRun), CheckAdvanceRunWait = new Func<global::PCLLF.Presentation.Controls.MyCheckBoxView>(() => this.CheckAdvanceRunWait), PanAdvanceJava = new Func<global::System.Windows.Controls.Grid>(() => this.PanAdvanceJava), ComboAdvanceJava = new Func<global::PCLLF.Presentation.Controls.MyComboBoxView>(() => this.ComboAdvanceJava), LabAdvanceJava = new Func<global::System.Windows.Controls.TextBlock>(() => this.LabAdvanceJava), BtnAdvanceJavaSearch = new Func<global::PCLLF.Presentation.Controls.MyIconButtonView>(() => this.BtnAdvanceJavaSearch), BtnSwitch = new Func<global::System.Windows.FrameworkElement>(() => this.BtnSwitch) });
                }

                return this._controller;
            }
        }

        public PageSetupLaunch()
        {
            __LfAttachOwnHandles();
            this.InitializeComponent();
            TextJavaDownloadMajor.TextChanged += (_, _) => JavaDownloadFiltersChanged();
            foreach (var filter in new[] { ComboJavaDownloadDistribution, ComboJavaDownloadArchitecture, ComboJavaDownloadType })
                filter.SelectionChanged += (_, _) => JavaDownloadFiltersChanged();
            // The download sub-page owns the panel while it is shown, so leaving the Java page for it keeps the panel open.
            JavaPage = new PageSetupJava(this, CardJavaManagement, UpdateJavaList, () => { if (!JavaDownloadPageShown) CloseJavaDownload(); });
        }

        internal PageSetupJava JavaPage { get; }

        private PageSetupJavaDownload _javaDownloadPage;
        /// <summary>The download catalogue as its own detail page (modern shell); the classic card keeps the inline panel.</summary>
        internal PageSetupJavaDownload JavaDownloadPage => _javaDownloadPage ??= new PageSetupJavaDownload(this);
        private bool JavaDownloadPageShown => _javaDownloadPage is not null && ReferenceEquals(ModMain.FrmMain?.PageRight, _javaDownloadPage);

        internal void OpenJavaDownloadPanel()
        {
            if (PanJavaDownload.Visibility != Visibility.Visible) BtnToggleJavaDownload_Click(null, null);
        }

        internal void CloseJavaDownloadPanel() => CloseJavaDownload();

        internal void ShowJavaDownloadPage()
        {
            if (ModMain.FrmMain is not FormMain window) return;
            OpenJavaDownloadPanel();
            window.PageChange(new FormMain.PageStackData { Page = FormMain.PageType.HelpDetail, Additional = new object[] { "下载 Java", JavaDownloadPage } });
        }

        private void PageSetupLaunch_Loaded(global::System.Object sender, global::System.Windows.RoutedEventArgs e)
        {
            this.Controller.Loaded();
        }

        public void Refresh()
        {
            this.Controller.Refresh();
        }

        public void Reset()
        {
            this.Controller.Reset();
        }

        private void PersonalSyncChanged(global::System.Object Sender, global::System.Boolean User)
        {
            this.Controller.PersonalSyncChanged(Sender, User);
        }

        private global::System.Boolean ImportPersonalOptions()
        {
            return this.Controller.ImportPersonalOptions();
        }

        private void ImportPersonalOptionsClick()
        {
            this.Controller.ImportPersonalOptionsClick();
        }

        private void BtnSkinChange_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.ChangeSkinClick();
        }

        private void RadioSkinType3_Check(global::System.Object sender, global::PCL.ModBase.RouteEventArgs e)
        {
            this.Controller.PreviewCustomSkin(e.RaiseByMouse, ref e.Handled);
        }

        private global::System.Boolean ChangeSkin(global::PCL.ModMinecraft.McSkinInfo SkinInfo)
        {
            return this.Controller.ChangeSkin(SkinInfo);
        }

        private void BtnSkinDelete_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.DeleteSkin();
        }

        private void BtnSkinSave_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.SaveSkin();
        }

        private void BtnSkinCache_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.RefreshSkinCache();
        }

        public static void UpdateSkinType()
        {
            global::PCL.SetupPageRuntime.Launch.UpdateSkinType();
        }

        public static void UpdateRamType()
        {
            global::PCL.SetupPageRuntime.Launch.UpdateRamType();
        }

        public void RefreshRam(global::System.Boolean ShowAnim)
        {
            this.Controller.RefreshRam(ShowAnim);
        }

        private void RefreshRam()
        {
            this.Controller.RefreshRam();
        }

        private void RefreshRamText()
        {
            this.Controller.RefreshRamText();
        }

        public static global::System.Double GetRam(global::PCL.ModMinecraft.McInstance Instance, global::System.Boolean UseVersionJavaSetup)
        {
            return global::PCL.SetupPageRuntime.Launch.GetRam(Instance, UseVersionJavaSetup);
        }

        public void UpdateJavaList()
        {
            this.Controller.UpdateJavaList();
            if (JavaPage.IsLoaded) RefreshJavaManagement();
        }

        private void BtnRefreshJavaManagement_Click(object sender, MouseButtonEventArgs e) => ModJava.ManuallySearchJava();

        private void BtnImportJavaManagement_Click(object sender, MouseButtonEventArgs e) => ModJava.ManuallyImportJava();

        private void RefreshJavaManagement()
        {
            if (PanJavaManagement?.Visibility != Visibility.Visible) return;
            bool searching = ModJava.JavaListRefreshWorker.Running;
            BtnRefreshJavaManagement.IsEnabled = BtnImportJavaManagement.IsEnabled = !searching;

            PanJavaRows.Children.Clear();
            foreach (Java entry in Configs.JavaList.Get()!)
            {
                string version = entry.Version is { } detected ? $"Java {detected}" : "Java（待检测）";
                string title = string.Join(" · ", new[] { version, entry.Distribution, entry.Architecture, entry.VirtualMachine }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                var open = new MyIconButton { Logo = ModBase.Logo.IconButtonOpen, ToolTip = "打开 Java 所在文件夹", Width = 26, Height = 26 };
                open.Click += (_, _) => ModBase.OpenExplorer(entry.Folder);
                List<MyIconButton> actions = [open];
                if (JavaRuntimeDownloads.IsManaged(entry))
                {
                    var uninstall = new MyIconButton { Logo = ModBase.Logo.IconButtonDelete, ToolTip = "卸载此 Java", Width = 26, Height = 26 };
                    uninstall.Click += (_, _) => JavaRuntimeDownloads.StartUninstall(entry);
                    actions.Add(uninstall);
                }
                var row = new MyListItem
                {
                    Title = title,
                    Info = entry.Folder,
                    ToolTip = entry.JavaExePath,
                    Height = 48,
                    Type = MyListItem.CheckType.Clickable,
                    IsScaleAnimationEnabled = false,
                    Tag = entry,
                    Buttons = actions,
                };
                row.Click += (_, _) => ModMain.MyMsgBox($"{title}\n\n{entry.JavaExePath}", "Java 详情");
                PanJavaRows.Children.Add(row);
            }
            FilterJavaManagement(searching);
        }

        private void TextJavaFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PanJavaRows != null) FilterJavaManagement(ModJava.JavaListRefreshWorker.Running);
        }

        private void FilterJavaManagement(bool searching)
        {
            string[] words = TextJavaFilter.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            bool hasMatch = false;
            foreach (MyListItem row in PanJavaRows.Children)
            {
                bool matches = words.All(word => row.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                    row.Info.Contains(word, StringComparison.OrdinalIgnoreCase));
                row.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                hasMatch |= matches;
            }
            LabJavaManagementStatus.Text = searching ? "正在搜索 Java…" :
                PanJavaRows.Children.Count == 0 ? "尚未找到 Java，可以刷新搜索或手动导入。" : "没有符合筛选条件的 Java。";
            LabJavaManagementStatus.Visibility = searching || !hasMatch ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnToggleJavaDownload_Click(object sender, MouseButtonEventArgs e)
        {
            if (PanJavaDownload.Visibility == Visibility.Visible)
            {
                CloseJavaDownload();
                return;
            }

            if (ComboJavaDownloadDistribution.Items.Count == 0)
            {
                ComboJavaDownloadDistribution.Items.Add(new MyComboBoxItem { Content = "全部" });
                foreach (var distribution in JavaRuntimeCatalog.Distributions)
                    ComboJavaDownloadDistribution.Items.Add(new MyComboBoxItem { Content = distribution.Value, Tag = distribution.Key });
                ComboJavaDownloadDistribution.SelectedIndex = 0;
            }
            if (ComboJavaDownloadArchitecture.Items.Count == 0)
            {
                string architecture = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.Arm64 => "arm64",
                    _ => null,
                };
                if (architecture is not null)
                {
                    ComboJavaDownloadArchitecture.Items.Add(new MyComboBoxItem { Content = architecture, Tag = architecture });
                    ComboJavaDownloadArchitecture.Items.Add(new MyComboBoxItem { Content = "x86", Tag = "x86" });
                    ComboJavaDownloadArchitecture.SelectedIndex = 0;
                }
            }
            BtnSearchJavaDownload.IsEnabled = ComboJavaDownloadArchitecture.SelectedIndex >= 0;
            LabJavaDownloadStatus.Text = BtnSearchJavaDownload.IsEnabled ? JavaDownloadSearchPrompt : "当前系统架构暂无 Java 下载选项。";
            PanJavaDownload.Visibility = Visibility.Visible;
        }

        private void JavaDownloadFiltersChanged()
        {
            if (PanJavaDownload?.Visibility == Visibility.Visible && BtnSearchJavaDownload?.IsEnabled == true)
                CancelJavaDownloadSearch(JavaDownloadSearchPrompt);
        }

        private void CloseJavaDownload()
        {
            if (PanJavaDownload.Visibility != Visibility.Visible) return;
            PanJavaDownload.Visibility = Visibility.Collapsed;
            CancelJavaDownloadSearch(JavaDownloadSearchPrompt);
        }

        private void BtnCancelJavaDownloadSearch_Click(object sender, MouseButtonEventArgs e) =>
            CancelJavaDownloadSearch("已取消搜索。");

        private void CancelJavaDownloadSearch(string status)
        {
            var request = _javaDownloadSearch;
            _javaDownloadSearch = null;
            request?.Cancel();
            foreach (UIElement row in PanJavaDownloadResults.Children)
                row.Visibility = Visibility.Collapsed;
            PanJavaDownloadResults.Children.Clear();
            BtnCancelJavaDownloadSearch.Visibility = Visibility.Collapsed;
            LabJavaDownloadStatus.Text = status;
        }

        private async void BtnSearchJavaDownload_Click(object sender, MouseButtonEventArgs e) =>
            await SearchJavaDownloadCatalogAsync(JavaRuntimeDownloads.SearchAsync);

        internal async Task SearchJavaDownloadCatalogAsync(
            Func<int, string, string, string, CancellationToken, Task<IReadOnlyList<JavaRuntimePackage>>> search)
        {
            if (PanJavaDownload.Visibility != Visibility.Visible ||
                (ComboJavaDownloadArchitecture.SelectedItem as ComboBoxItem)?.Tag is not string architecture)
                return;
            if (!int.TryParse(TextJavaDownloadMajor.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
                major is < JavaRuntimeCatalog.MinimumMajor or > JavaRuntimeCatalog.MaximumMajor)
            {
                CancelJavaDownloadSearch($"请输入 {JavaRuntimeCatalog.MinimumMajor} 至 {JavaRuntimeCatalog.MaximumMajor} 的 Java 主版本。");
                return;
            }

            var distribution = (ComboJavaDownloadDistribution.SelectedItem as ComboBoxItem)?.Tag as string;
            var packageType = (ComboJavaDownloadType.SelectedItem as ComboBoxItem)?.Tag as string;
            CancelJavaDownloadSearch("正在搜索可下载的 Java…");
            using var request = new CancellationTokenSource();
            _javaDownloadSearch = request;
            BtnCancelJavaDownloadSearch.Visibility = Visibility.Visible;
            try
            {
                var packages = await search(major, architecture, distribution, packageType, request.Token);
                if (!ReferenceEquals(_javaDownloadSearch, request)) return;
                foreach (var package in packages)
                    PanJavaDownloadResults.Children.Add(CreateJavaDownloadRow(package));
                LabJavaDownloadStatus.Text = packages.Count == 0 ? "没有符合条件的 Java 安装包。" : $"找到 {packages.Count} 个安装包。";
                if (ReferenceEquals(ModMain.FrmSetupLaunch, this)) ModMain.FrmMain?.RefreshGlassShell();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (ReferenceEquals(_javaDownloadSearch, request))
                {
                    Logger.Warn(error, "搜索 Java 下载目录失败");
                    CancelJavaDownloadSearch("无法获取 Java 下载列表，请检查网络后重试。");
                }
            }
            finally
            {
                if (ReferenceEquals(_javaDownloadSearch, request))
                {
                    _javaDownloadSearch = null;
                    BtnCancelJavaDownloadSearch.Visibility = Visibility.Collapsed;
                }
            }
        }

        private MyListItem CreateJavaDownloadRow(JavaRuntimePackage package)
        {
            string distribution = JavaRuntimeCatalog.Distributions.TryGetValue(package.Distribution, out string name)
                ? name : package.Distribution;
            string title = $"{distribution} · Java {package.Version}";
            string size = package.Size > 0 ? StringUtils.FormatByteSize(package.Size) : "大小未知";
            string details = $"{package.PackageType.ToUpperInvariant()} · {package.Architecture} · {size}";
            var download = new MyIconButton { Logo = ModBase.Logo.IconButtonSave, ToolTip = "下载此 Java", Width = 26, Height = 26 };
            var row = new MyListItem
            {
                Title = title,
                Info = details,
                ToolTip = package.FileName,
                Height = 48,
                Type = MyListItem.CheckType.Clickable,
                IsScaleAnimationEnabled = false,
                Tag = package,
                Buttons = [download],
            };
            download.Click += (_, _) =>
            {
                if (row.Parent == PanJavaDownloadResults && PanJavaDownload.Visibility == Visibility.Visible)
                    JavaRuntimeDownloads.StartInstall(package);
            };
            row.Click += (_, _) => ModMain.MyMsgBox($"{title}\n{details}\n\n{package.FileName}", "Java 下载详情");
            return row;
        }

        private void MoveJavaInList(global::PCLCS.Java JavaEntry, global::System.Int32 Offset)
        {
            this.Controller.MoveJavaInList(JavaEntry, Offset);
        }

        private void BtnAdvanceJavaSearch_Click()
        {
            this.Controller.SearchJava();
        }

        private void WindowTypeUIRefresh()
        {
            this.Controller.WindowTypeUIRefresh();
        }

        private void ComboArgumentVisibie_SizeChanged(global::System.Object sender, global::System.Windows.Controls.SelectionChangedEventArgs e)
        {
            this.Controller.VisibilitySelectionChanged(e);
        }

        private void CheckArgumentRam_Change()
        {
            this.Controller.MemoryOptimizationChanged();
        }

        private void ComboArgumentIndie_SelectionChanged(global::System.Object sender, global::System.Windows.Controls.SelectionChangedEventArgs e)
        {
            this.Controller.IsolationSelectionChanged();
        }

        private void TextAdvanceRun_TextChanged(global::System.Object sender, global::System.Windows.Controls.TextChangedEventArgs e)
        {
            this.Controller.AdvanceRunChanged();
        }

        private void TextAdvanceJvm_TextChanged()
        {
            this.Controller.AdvanceJvmChanged();
        }

        private void BtnAdvanceJvmReset_Click(global::System.Object sender, global::System.EventArgs e)
        {
            this.Controller.ResetAdvanceJvm();
        }

        private void ReplaceEnter(global::PCL.MyTextBox sender, global::System.Windows.Controls.TextChangedEventArgs e)
        {
            this.Controller.ReplaceEnter(sender, e);
        }

        private void BtnSwitch_Click(global::System.Object sender, global::System.Windows.Input.MouseButtonEventArgs e)
        {
            this.Controller.SwitchToInstance();
        }
    }
}
